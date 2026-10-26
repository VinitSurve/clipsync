package com.clipsync.android.ui

import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.SharedPreferences
import android.os.Build
import android.os.Bundle
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.Switch
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.clipsync.android.R
import com.clipsync.android.models.ClaimPairingResponse
import com.clipsync.android.models.RegisterDeviceResponse
import com.clipsync.android.service.ClipSyncForegroundService
import com.google.gson.Gson
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException

data class DisplayItem(val text: String, val sourceApp: String, val timestamp: Long)

class MainActivity : AppCompatActivity() {

    private lateinit var prefs: SharedPreferences
    private val httpClient = OkHttpClient()
    private val gson = Gson()
    private val historyItems = ArrayList<DisplayItem>()
    private lateinit var historyAdapter: HistoryAdapter

    private var serverUrl = "http://10.0.2.2:8787"
    private var deviceId = ""
    private var token = ""
    private var syncSalt = ""

    private val itemReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            val text = intent?.getStringExtra("text") ?: return
            val source = intent.getStringExtra("sourceApp") ?: "Remote"
            runOnUiThread {
                historyItems.add(0, DisplayItem(text, source, System.currentTimeMillis()))
                historyAdapter.notifyItemInserted(0)
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        prefs = getSharedPreferences("clipsync_prefs", Context.MODE_PRIVATE)

        deviceId = prefs.getString("deviceId", "") ?: ""
        token = prefs.getString("token", "") ?: ""
        syncSalt = prefs.getString("syncSalt", "") ?: ""
        serverUrl = prefs.getString("serverUrl", "http://10.0.2.2:8787") ?: "http://10.0.2.2:8787"

        setupSimpleView()

        // Register broadcast receiver
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(itemReceiver, IntentFilter("com.clipsync.ITEM_RECEIVED"), RECEIVER_NOT_EXPORTED)
        } else {
            registerReceiver(itemReceiver, IntentFilter("com.clipsync.ITEM_RECEIVED"))
        }

        // Handle deep-link pairing if launched via clipsync://pair?code=...
        intent?.data?.let { uri ->
            if (uri.scheme == "clipsync" && uri.host == "pair") {
                val code = uri.getQueryParameter("code")
                val srv = uri.getQueryParameter("server")
                if (!srv.isNullOrBlank()) serverUrl = srv
                if (!code.isNullOrBlank()) {
                    pairWithCode(code)
                }
            }
        }

        if (deviceId.isNotEmpty()) {
            startSyncService()
        }
    }

    private fun setupSimpleView() {
        val root = android.widget.LinearLayout(this).apply {
            orientation = android.widget.LinearLayout.VERTICAL
            setPadding(32, 48, 32, 32)
        }

        val title = TextView(this).apply {
            text = "ClipSync Universal Clipboard"
            textSize = 22f
            setTypeface(null, android.graphics.Typeface.BOLD)
        }
        root.addView(title)

        val status = TextView(this).apply {
            text = if (deviceId.isNotEmpty()) "Status: Connected" else "Status: Not Paired"
            textSize = 14f
            setPadding(0, 8, 0, 16)
        }
        root.addView(status)

        val pairBtn = Button(this).apply {
            text = "Pair with 6-Digit Code (from Mac/Windows)"
            setOnClickListener { showPairDialog() }
        }
        root.addView(pairBtn)

        val syncNowBtn = Button(this).apply {
            text = "Sync Current Clipboard Now"
            setOnClickListener {
                val serviceIntent = Intent(this@MainActivity, ClipSyncForegroundService::class.java).apply {
                    putExtra("action_sync_now", true)
                }
                startService(serviceIntent)
                Toast.makeText(this@MainActivity, "Clipboard sync requested", Toast.LENGTH_SHORT).show()
            }
        }
        root.addView(syncNowBtn)

        val historyTitle = TextView(this).apply {
            text = "Recent Clipboard Items"
            textSize = 16f
            setTypeface(null, android.graphics.Typeface.BOLD)
            setPadding(0, 24, 0, 8)
        }
        root.addView(historyTitle)

        val recyclerView = RecyclerView(this)
        recyclerView.layoutManager = LinearLayoutManager(this)
        historyAdapter = HistoryAdapter(historyItems) { item ->
            val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
            cm.setPrimaryClip(ClipData.newPlainText("ClipSync", item.text))
            Toast.makeText(this, "Copied to clipboard", Toast.LENGTH_SHORT).show()
        }
        recyclerView.adapter = historyAdapter
        root.addView(recyclerView)

        setContentView(root)
    }

    private fun showPairDialog() {
        val input = EditText(this).apply {
            hint = "6-digit code"
            textSize = 20f
            textAlignment = View.TEXT_ALIGNMENT_CENTER
        }

        AlertDialog.Builder(this)
            .setTitle("Enter Pairing Code")
            .setMessage("Enter the 6-digit code shown on your MacBook or PC:")
            .setView(input)
            .setPositiveButton("Pair") { _, _ ->
                val code = input.text.toString().trim()
                if (code.length == 6) {
                    pairWithCode(code)
                }
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun pairWithCode(code: String) {
        val json = gson.toJson(mapOf(
            "code" to code,
            "label" to "Android Phone",
            "platform" to "android"
        ))

        val req = Request.Builder()
            .url("$serverUrl/v1/auth/pair")
            .post(json.toRequestBody("application/json".toMediaType()))
            .build()

        httpClient.newCall(req).enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                runOnUiThread { Toast.makeText(this@MainActivity, "Pairing failed: ${e.message}", Toast.LENGTH_LONG).show() }
            }

            override fun onResponse(call: Call, response: Response) {
                val body = response.body?.string() ?: ""
                if (!response.isSuccessful) {
                    runOnUiThread { Toast.makeText(this@MainActivity, "Pairing rejected by server", Toast.LENGTH_LONG).show() }
                    return
                }

                val claim = gson.fromJson(body, ClaimPairingResponse::class.java)
                deviceId = claim.deviceId
                token = claim.token
                syncSalt = claim.syncSalt

                prefs.edit()
                    .putString("deviceId", deviceId)
                    .putString("token", token)
                    .putString("syncSalt", syncSalt)
                    .putString("serverUrl", serverUrl)
                    .apply()

                runOnUiThread {
                    Toast.makeText(this@MainActivity, "Paired successfully with ${claim.groupId}!", Toast.LENGTH_LONG).show()
                    startSyncService()
                }
            }
        })
    }

    private fun startSyncService() {
        val intent = Intent(this, ClipSyncForegroundService::class.java).apply {
            putExtra("serverUrl", serverUrl)
            putExtra("deviceId", deviceId)
            putExtra("token", token)
            putExtra("syncSalt", syncSalt)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent)
        } else {
            startService(intent)
        }
    }

    override fun onDestroy() {
        unregisterReceiver(itemReceiver)
        super.onDestroy()
    }

    class HistoryAdapter(
        private val items: List<DisplayItem>,
        private val onCopyClick: (DisplayItem) -> Unit
    ) : RecyclerView.Adapter<HistoryAdapter.ViewHolder>() {

        class ViewHolder(v: View) : RecyclerView.ViewHolder(v) {
            val textView: TextView = v.findViewById(android.R.id.text1)
            val subText: TextView = v.findViewById(android.R.id.text2)
        }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
            val v = LayoutInflater.from(parent.context).inflate(android.R.layout.simple_list_item_2, parent, false)
            return ViewHolder(v)
        }

        override fun onBindViewHolder(holder: ViewHolder, position: Int) {
            val item = items[position]
            holder.textView.text = item.text
            holder.subText.text = "[${item.sourceApp}] Tap to copy"
            holder.itemView.setOnClickListener { onCopyClick(item) }
        }

        override fun getItemCount(): Int = items.size
    }
}
