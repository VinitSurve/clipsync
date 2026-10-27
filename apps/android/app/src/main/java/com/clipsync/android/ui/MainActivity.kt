package com.clipsync.android.ui

import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.view.LayoutInflater
import android.view.View
import android.view.ViewGroup
import android.widget.EditText
import android.widget.TextView
import android.widget.Toast
import androidx.appcompat.app.AlertDialog
import androidx.appcompat.app.AppCompatActivity
import androidx.core.content.ContextCompat
import androidx.recyclerview.widget.LinearLayoutManager
import androidx.recyclerview.widget.RecyclerView
import com.clipsync.android.R
import com.clipsync.android.crypto.ClipSyncCrypto
import com.clipsync.android.models.ClaimPairingResponse
import com.clipsync.android.models.DeviceInfo
import com.clipsync.android.models.DeviceListResponse
import com.clipsync.android.models.PairingSessionResponse
import com.clipsync.android.service.ClipSyncForegroundService
import com.clipsync.android.service.ClipboardStateManager
import com.clipsync.android.storage.SecureStorage
import com.google.android.material.appbar.MaterialToolbar
import com.google.android.material.button.MaterialButton
import com.google.gson.Gson
import okhttp3.Call
import okhttp3.Callback
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import java.io.IOException
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.TimeUnit

data class HistoryEntry(
    val id: String,
    val text: String,
    val source: String,
    val timestamp: Long
)

class MainActivity : AppCompatActivity() {

    private lateinit var secureStorage: SecureStorage
    private lateinit var clipboardManager: ClipboardManager

    private val httpClient = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(10, TimeUnit.SECONDS)
        .build()
    private val gson = Gson()
    private val mainHandler = Handler(Looper.getMainLooper())

    private val historyList = mutableListOf<HistoryEntry>()
    private val deviceList = mutableListOf<DeviceInfo>()

    private lateinit var historyAdapter: HistoryAdapter
    private lateinit var deviceAdapter: DeviceAdapter

    // Views
    private lateinit var viewStatusDot: View
    private lateinit var txtStatusTitle: TextView
    private lateinit var txtDeviceLabel: TextView
    private lateinit var txtServerUrl: TextView
    private lateinit var txtSyncMode: TextView
    private lateinit var btnToggleConnect: MaterialButton
    private lateinit var btnSyncNow: MaterialButton
    private lateinit var btnPair: MaterialButton
    private lateinit var btnRefreshDevices: MaterialButton
    private lateinit var txtEmptyDevices: TextView
    private lateinit var recyclerDevices: RecyclerView
    private lateinit var btnClearHistory: MaterialButton
    private lateinit var txtEmptyHistory: TextView
    private lateinit var recyclerHistory: RecyclerView

    private var currentConnectionState: String = ClipSyncForegroundService.STATE_DISCONNECTED

    // Foreground clipboard listener
    private val foregroundClipListener = ClipboardManager.OnPrimaryClipChangedListener {
        onForegroundClipboardChanged()
    }

    // Broadcast receiver for service events
    private val serviceReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            when (intent?.action) {
                ClipSyncForegroundService.ACTION_ITEM_RECEIVED -> {
                    val text = intent.getStringExtra("text") ?: return
                    val source = intent.getStringExtra("sourceApp") ?: "Remote Device"
                    val timestamp = intent.getLongExtra("timestamp", System.currentTimeMillis())
                    val id = intent.getStringExtra("itemId") ?: System.currentTimeMillis().toString()

                    runOnUiThread {
                        addHistoryEntry(HistoryEntry(id, text, source, timestamp))

                        // When MainActivity is foregrounded with window focus, auto-apply immediately
                        if (hasWindowFocus() || ClipboardStateManager.isAppInForeground) {
                            try {
                                ClipboardStateManager.markAppliedLocally(text)
                                val clip = ClipData.newPlainText("ClipSync", text)
                                clipboardManager.setPrimaryClip(clip)
                                val preview = if (text.length > 25) text.take(25) + "..." else text
                                Toast.makeText(
                                    this@MainActivity,
                                    "ClipSync: Auto-applied from $source (\"$preview\")",
                                    Toast.LENGTH_SHORT
                                ).show()
                            } catch (e: Exception) {
                                Log.d("MainActivity", "Foreground auto-apply failed: ${e.message}")
                            }
                        }
                    }
                }

                ClipSyncForegroundService.ACTION_CONNECTION_STATE -> {
                    val state = intent.getStringExtra("state") ?: ClipSyncForegroundService.STATE_DISCONNECTED
                    val error = intent.getStringExtra("error")
                    runOnUiThread {
                        updateConnectionUI(state, error)
                    }
                }
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        secureStorage = SecureStorage(this)
        clipboardManager = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

        initViews()
        setupRecyclerViews()
        setupToolbar()

        // Register broadcast receivers
        val filter = IntentFilter().apply {
            addAction(ClipSyncForegroundService.ACTION_ITEM_RECEIVED)
            addAction(ClipSyncForegroundService.ACTION_CONNECTION_STATE)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            registerReceiver(serviceReceiver, filter, RECEIVER_NOT_EXPORTED)
        } else {
            registerReceiver(serviceReceiver, filter)
        }

        // Handle deep-link pairing clipsync://pair?code=123456&server=...
        handleDeepLink(intent)

        // Refresh UI state and start service if paired
        refreshUIFromStorage()
        if (secureStorage.hasCredentials()) {
            startSyncService()
            fetchDevices()
        }
    }

    override fun onNewIntent(intent: Intent?) {
        super.onNewIntent(intent)
        handleDeepLink(intent)
    }

    override fun onResume() {
        super.onResume()
        ClipboardStateManager.isAppInForeground = true
        try {
            clipboardManager.addPrimaryClipChangedListener(foregroundClipListener)
        } catch (_: Exception) {}

        checkAndProcessClipboardOnForeground()
        refreshUIFromStorage()
        if (secureStorage.hasCredentials()) {
            fetchDevices()
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) {
            ClipboardStateManager.isAppInForeground = true
            checkAndProcessClipboardOnForeground()
        } else {
            ClipboardStateManager.isAppInForeground = false
        }
    }

    override fun onPause() {
        super.onPause()
        ClipboardStateManager.isAppInForeground = false
        try {
            clipboardManager.removePrimaryClipChangedListener(foregroundClipListener)
        } catch (_: Exception) {}
    }

    override fun onDestroy() {
        try {
            unregisterReceiver(serviceReceiver)
        } catch (_: Exception) {}
        super.onDestroy()
    }

    private fun checkAndProcessClipboardOnForeground() {
        // 1. If there is a pending remote text waiting to be applied, auto-apply it immediately
        val pending = ClipboardStateManager.pendingRemoteText
        val pendingSource = ClipboardStateManager.pendingRemoteSource ?: "Desktop"
        if (!pending.isNullOrBlank()) {
            try {
                ClipboardStateManager.markAppliedLocally(pending)
                val clip = ClipData.newPlainText("ClipSync", pending)
                clipboardManager.setPrimaryClip(clip)
                val preview = if (pending.length > 25) pending.take(25) + "..." else pending
                Toast.makeText(this, "ClipSync: Auto-applied from $pendingSource (\"$preview\")", Toast.LENGTH_SHORT).show()
            } catch (e: Exception) {
                Log.d("MainActivity", "Auto-apply pending failed: ${e.message}")
            }
            return
        }

        // 2. Otherwise check if local clipboard has new text copied from another app
        try {
            val clip = clipboardManager.primaryClip
            if (clip != null && clip.itemCount > 0) {
                val text = clip.getItemAt(0).text?.toString()
                if (!text.isNullOrBlank()) {
                    val hash = ClipSyncCrypto.sha256(text)
                    if (!ClipboardStateManager.isEcho(hash) && !ClipboardStateManager.isAlreadySynced(hash)) {
                        // NEW text copied in external app! Auto-sync to desktop
                        ClipboardStateManager.markSent(text)
                        val intent = Intent(this, ClipSyncForegroundService::class.java).apply {
                            putExtra(ClipSyncForegroundService.EXTRA_OUTBOUND_TEXT, text)
                        }
                        startService(intent)
                        val preview = if (text.length > 25) text.take(25) + "..." else text
                        Toast.makeText(this, "ClipSync: Auto-synced clipboard (\"$preview\")", Toast.LENGTH_SHORT).show()
                    }
                }
            }
        } catch (e: Exception) {
            Log.d("MainActivity", "Check primaryClip failed: ${e.message}")
        }
    }

    private fun onForegroundClipboardChanged() {
        try {
            val clip = clipboardManager.primaryClip
            if (clip != null && clip.itemCount > 0) {
                val text = clip.getItemAt(0).text?.toString()
                if (!text.isNullOrBlank()) {
                    val hash = ClipSyncCrypto.sha256(text)
                    if (ClipboardStateManager.isEcho(hash) || ClipboardStateManager.isAlreadySynced(hash)) {
                        return
                    }
                    ClipboardStateManager.markSent(text)
                    val intent = Intent(this, ClipSyncForegroundService::class.java).apply {
                        putExtra(ClipSyncForegroundService.EXTRA_OUTBOUND_TEXT, text)
                    }
                    startService(intent)
                    val preview = if (text.length > 25) text.take(25) + "..." else text
                    Toast.makeText(this, "ClipSync: Auto-synced (\"$preview\")", Toast.LENGTH_SHORT).show()
                }
            }
        } catch (e: Exception) {
            // Foreground clip error
        }
    }

    private fun initViews() {
        viewStatusDot = findViewById(R.id.viewStatusDot)
        txtStatusTitle = findViewById(R.id.txtStatusTitle)
        txtDeviceLabel = findViewById(R.id.txtDeviceLabel)
        txtServerUrl = findViewById(R.id.txtServerUrl)
        txtSyncMode = findViewById(R.id.txtSyncMode)
        btnToggleConnect = findViewById(R.id.btnToggleConnect)
        btnSyncNow = findViewById(R.id.btnSyncNow)
        btnPair = findViewById(R.id.btnPair)
        btnRefreshDevices = findViewById(R.id.btnRefreshDevices)
        txtEmptyDevices = findViewById(R.id.txtEmptyDevices)
        recyclerDevices = findViewById(R.id.recyclerDevices)
        btnClearHistory = findViewById(R.id.btnClearHistory)
        txtEmptyHistory = findViewById(R.id.txtEmptyHistory)
        recyclerHistory = findViewById(R.id.recyclerHistory)

        btnToggleConnect.setOnClickListener {
            if (!secureStorage.hasCredentials()) {
                showPairDialog()
            } else {
                startSyncService(reconnect = true)
                Toast.makeText(this, "Reconnecting to ClipSync...", Toast.LENGTH_SHORT).show()
            }
        }

        btnSyncNow.setOnClickListener {
            syncCurrentClipboard()
        }

        btnPair.setOnClickListener {
            showPairDialog()
        }

        btnRefreshDevices.setOnClickListener {
            if (secureStorage.hasCredentials()) {
                fetchDevices()
            } else {
                Toast.makeText(this, "Pair device first", Toast.LENGTH_SHORT).show()
            }
        }

        btnClearHistory.setOnClickListener {
            historyList.clear()
            historyAdapter.notifyDataSetChanged()
            updateHistoryVisibility()
            Toast.makeText(this, "History cleared", Toast.LENGTH_SHORT).show()
        }
    }

    private fun setupToolbar() {
        val toolbar = findViewById<MaterialToolbar>(R.id.topAppBar)
        toolbar.inflateMenu(R.menu.menu_main)
        toolbar.setOnMenuItemClickListener { menuItem ->
            when (menuItem.itemId) {
                R.id.action_settings -> {
                    showSettingsDialog()
                    true
                }
                else -> false
            }
        }
    }

    private fun setupRecyclerViews() {
        // History
        historyAdapter = HistoryAdapter(historyList) { entry ->
            applyToClipboard(entry.text)
        }
        recyclerHistory.layoutManager = LinearLayoutManager(this)
        recyclerHistory.adapter = historyAdapter
        updateHistoryVisibility()

        // Devices
        deviceAdapter = DeviceAdapter(deviceList) { device ->
            confirmRevokeDevice(device)
        }
        recyclerDevices.layoutManager = LinearLayoutManager(this)
        recyclerDevices.adapter = deviceAdapter
        updateDeviceVisibility()
    }

    private fun refreshUIFromStorage() {
        txtDeviceLabel.text = "Device: ${secureStorage.deviceLabel}"
        txtServerUrl.text = "Server: ${secureStorage.serverUrl}"
        txtSyncMode.text = "Sync: Auto (Foreground) • 1-Tap QS / Notification"

        if (!secureStorage.hasCredentials()) {
            updateConnectionUI(ClipSyncForegroundService.STATE_NOT_PAIRED, null)
        } else if (currentConnectionState == ClipSyncForegroundService.STATE_NOT_PAIRED) {
            updateConnectionUI(ClipSyncForegroundService.STATE_DISCONNECTED, null)
        }
    }

    private fun updateConnectionUI(state: String, error: String?) {
        currentConnectionState = state
        when (state) {
            ClipSyncForegroundService.STATE_CONNECTED -> {
                viewStatusDot.setBackgroundResource(R.drawable.shape_dot_connected)
                txtStatusTitle.text = "Status: Connected"
                txtStatusTitle.setTextColor(ContextCompat.getColor(this, R.color.status_connected))
                btnToggleConnect.text = "Reconnect"
            }
            ClipSyncForegroundService.STATE_CONNECTING -> {
                viewStatusDot.setBackgroundResource(R.drawable.shape_dot_connecting)
                txtStatusTitle.text = "Status: Connecting..."
                txtStatusTitle.setTextColor(ContextCompat.getColor(this, R.color.status_connecting))
                btnToggleConnect.text = "Connecting"
            }
            ClipSyncForegroundService.STATE_NOT_PAIRED -> {
                viewStatusDot.setBackgroundResource(R.drawable.shape_dot_disconnected)
                txtStatusTitle.text = "Status: Not Paired"
                txtStatusTitle.setTextColor(ContextCompat.getColor(this, R.color.status_disconnected))
                btnToggleConnect.text = "Pair Now"
            }
            ClipSyncForegroundService.STATE_ERROR -> {
                viewStatusDot.setBackgroundResource(R.drawable.shape_dot_disconnected)
                txtStatusTitle.text = if (!error.isNullOrBlank()) "Error: $error" else "Status: Error"
                txtStatusTitle.setTextColor(ContextCompat.getColor(this, R.color.status_disconnected))
                btnToggleConnect.text = "Retry"
            }
            else -> {
                viewStatusDot.setBackgroundResource(R.drawable.shape_dot_disconnected)
                txtStatusTitle.text = "Status: Disconnected"
                txtStatusTitle.setTextColor(ContextCompat.getColor(this, R.color.status_disconnected))
                btnToggleConnect.text = "Connect"
            }
        }
    }

    private fun syncCurrentClipboard() {
        try {
            val clip = clipboardManager.primaryClip
            if (clip != null && clip.itemCount > 0) {
                val text = clip.getItemAt(0).text?.toString()
                if (!text.isNullOrBlank()) {
                    ClipboardStateManager.markSent(text)
                    val intent = Intent(this, ClipSyncForegroundService::class.java).apply {
                        putExtra(ClipSyncForegroundService.EXTRA_OUTBOUND_TEXT, text)
                    }
                    startService(intent)
                    val preview = if (text.length > 30) text.take(30) + "..." else text
                    Toast.makeText(this, "Synced clipboard: \"$preview\"", Toast.LENGTH_SHORT).show()
                } else {
                    Toast.makeText(this, "Clipboard is empty", Toast.LENGTH_SHORT).show()
                }
            } else {
                Toast.makeText(this, "Clipboard is empty", Toast.LENGTH_SHORT).show()
            }
        } catch (e: Exception) {
            Toast.makeText(this, "Clipboard error: ${e.message}", Toast.LENGTH_SHORT).show()
        }
    }

    private fun applyToClipboard(text: String) {
        try {
            ClipboardStateManager.markAppliedLocally(text)
            val clip = ClipData.newPlainText("ClipSync", text)
            clipboardManager.setPrimaryClip(clip)
            Toast.makeText(this, "Copied to clipboard", Toast.LENGTH_SHORT).show()
        } catch (e: Exception) {
            Toast.makeText(this, "Copy failed: ${e.message}", Toast.LENGTH_SHORT).show()
        }
    }

    private fun addHistoryEntry(entry: HistoryEntry) {
        // Prevent immediate visual duplicates
        if (historyList.isNotEmpty() && historyList[0].text == entry.text) {
            return
        }
        historyList.add(0, entry)
        if (historyList.size > 100) {
            historyList.removeAt(historyList.size - 1)
        }
        historyAdapter.notifyItemInserted(0)
        recyclerHistory.scrollToPosition(0)
        updateHistoryVisibility()
    }

    private fun updateHistoryVisibility() {
        if (historyList.isEmpty()) {
            txtEmptyHistory.visibility = View.VISIBLE
            recyclerHistory.visibility = View.GONE
        } else {
            txtEmptyHistory.visibility = View.GONE
            recyclerHistory.visibility = View.VISIBLE
        }
    }

    private fun updateDeviceVisibility() {
        if (deviceList.isEmpty()) {
            txtEmptyDevices.visibility = View.VISIBLE
            recyclerDevices.visibility = View.GONE
        } else {
            txtEmptyDevices.visibility = View.GONE
            recyclerDevices.visibility = View.VISIBLE
        }
    }

    private fun startSyncService(reconnect: Boolean = false) {
        val intent = Intent(this, ClipSyncForegroundService::class.java).apply {
            if (reconnect) putExtra(ClipSyncForegroundService.EXTRA_RECONNECT, true)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(intent)
        } else {
            startService(intent)
        }
    }

    // -------------------------------------------------------------------------
    // Pairing & Device Management
    // -------------------------------------------------------------------------

    private fun handleDeepLink(intent: Intent?) {
        val uri = intent?.data ?: return
        if (uri.scheme == "clipsync" && uri.host == "pair") {
            val code = uri.getQueryParameter("code")
            val server = uri.getQueryParameter("server")
            val salt = uri.getQueryParameter("salt")

            if (!server.isNullOrBlank()) {
                secureStorage.serverUrl = server
            }
            if (!salt.isNullOrBlank()) {
                secureStorage.syncSalt = salt
            }
            if (!code.isNullOrBlank()) {
                pairWithCode(code)
            }
        }
    }

    private fun showPairDialog() {
        val dialogView = LayoutInflater.from(this).inflate(R.layout.dialog_pair, null)
        val editCode = dialogView.findViewById<EditText>(R.id.editPairCode)
        val btnGenerateSession = dialogView.findViewById<MaterialButton>(R.id.btnGenerateSession)
        val txtGeneratedCode = dialogView.findViewById<TextView>(R.id.txtGeneratedCode)

        val dialog = AlertDialog.Builder(this)
            .setTitle("Device Pairing")
            .setView(dialogView)
            .setPositiveButton("Pair", null) // Overridden below to prevent auto-close on validation failure
            .setNegativeButton("Cancel", null)
            .create()

        btnGenerateSession.setOnClickListener {
            if (!secureStorage.hasCredentials()) {
                Toast.makeText(this, "This phone must already be paired to generate a code for another device.", Toast.LENGTH_LONG).show()
                return@setOnClickListener
            }
            generatePairingSession(btnGenerateSession, txtGeneratedCode)
        }

        dialog.setOnShowListener {
            val positiveBtn = dialog.getButton(AlertDialog.BUTTON_POSITIVE)
            positiveBtn.setOnClickListener {
                val code = editCode.text.toString().trim()
                if (code.length == 6 && code.all { it.isDigit() }) {
                    dialog.dismiss()
                    pairWithCode(code)
                } else {
                    editCode.error = "Enter a valid 6-digit code"
                }
            }
        }

        dialog.show()
    }

    private fun generatePairingSession(btnGenerate: MaterialButton, txtCode: TextView) {
        btnGenerate.isEnabled = false
        btnGenerate.text = "Generating..."

        val req = Request.Builder()
            .url("${secureStorage.serverUrl}/v1/auth/pairing/session")
            .header("Authorization", "Bearer ${secureStorage.token}")
            .post("{}".toRequestBody("application/json".toMediaType()))
            .build()

        httpClient.newCall(req).enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                runOnUiThread {
                    btnGenerate.isEnabled = true
                    btnGenerate.text = "Generate Code on This Phone"
                    Toast.makeText(this@MainActivity, "Failed: ${e.message}", Toast.LENGTH_SHORT).show()
                }
            }

            override fun onResponse(call: Call, response: Response) {
                val body = response.body?.string().orEmpty()
                runOnUiThread {
                    btnGenerate.isEnabled = true
                    btnGenerate.text = "Generate Code on This Phone"
                    if (response.isSuccessful) {
                        try {
                            val session = gson.fromJson(body, PairingSessionResponse::class.java)
                            txtCode.text = "Code: ${session.code}"
                            txtCode.visibility = View.VISIBLE
                            Toast.makeText(this@MainActivity, "Enter code ${session.code} on Mac/Windows", Toast.LENGTH_LONG).show()
                        } catch (e: Exception) {
                            Toast.makeText(this@MainActivity, "Response parse error: ${e.message}", Toast.LENGTH_SHORT).show()
                        }
                    } else {
                        Toast.makeText(this@MainActivity, "Failed to create session (${response.code})", Toast.LENGTH_SHORT).show()
                    }
                }
            }
        })
    }

    private fun pairWithCode(code: String) {
        Toast.makeText(this, "Pairing with code $code...", Toast.LENGTH_SHORT).show()

        val json = gson.toJson(
            mapOf(
                "code" to code,
                "label" to secureStorage.deviceLabel,
                "platform" to "android"
            )
        )

        val req = Request.Builder()
            .url("${secureStorage.serverUrl}/v1/auth/pair")
            .post(json.toRequestBody("application/json".toMediaType()))
            .build()

        httpClient.newCall(req).enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                runOnUiThread {
                    Toast.makeText(this@MainActivity, "Pairing failed: ${e.message}", Toast.LENGTH_SHORT).show()
                }
            }

            override fun onResponse(call: Call, response: Response) {
                val body = response.body?.string().orEmpty()
                if (!response.isSuccessful) {
                    runOnUiThread {
                        Toast.makeText(this@MainActivity, "Pairing rejected: Invalid or expired code ($body)", Toast.LENGTH_LONG).show()
                    }
                    return
                }

                try {
                    val claim = gson.fromJson(body, ClaimPairingResponse::class.java)
                    secureStorage.deviceId = claim.deviceId
                    secureStorage.token = claim.token
                    secureStorage.syncSalt = claim.syncSalt

                    runOnUiThread {
                        Toast.makeText(this@MainActivity, "Paired successfully with ${claim.groupId}!", Toast.LENGTH_LONG).show()
                        refreshUIFromStorage()
                        startSyncService(reconnect = true)
                        fetchDevices()
                    }
                } catch (e: Exception) {
                    runOnUiThread {
                        Toast.makeText(this@MainActivity, "Pairing parse error: ${e.message}", Toast.LENGTH_SHORT).show()
                    }
                }
            }
        })
    }

    private fun fetchDevices() {
        if (!secureStorage.hasCredentials()) return

        val req = Request.Builder()
            .url("${secureStorage.serverUrl}/v1/devices")
            .header("Authorization", "Bearer ${secureStorage.token}")
            .get()
            .build()

        httpClient.newCall(req).enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                // Ignore device list failure
            }

            override fun onResponse(call: Call, response: Response) {
                val body = response.body?.string().orEmpty()
                if (response.isSuccessful) {
                    try {
                        val resp = gson.fromJson(body, DeviceListResponse::class.java)
                        runOnUiThread {
                            deviceList.clear()
                            deviceList.addAll(resp.devices)
                            deviceAdapter.notifyDataSetChanged()
                            updateDeviceVisibility()
                        }
                    } catch (_: Exception) {}
                }
            }
        })
    }

    private fun confirmRevokeDevice(device: DeviceInfo) {
        AlertDialog.Builder(this)
            .setTitle("Revoke Device")
            .setMessage("Are you sure you want to revoke '${device.label}'? It will be disconnected from ClipSync.")
            .setPositiveButton("Revoke") { _, _ ->
                revokeDevice(device)
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun revokeDevice(device: DeviceInfo) {
        val req = Request.Builder()
            .url("${secureStorage.serverUrl}/v1/devices/${device.deviceId}")
            .header("Authorization", "Bearer ${secureStorage.token}")
            .delete()
            .build()

        httpClient.newCall(req).enqueue(object : Callback {
            override fun onFailure(call: Call, e: IOException) {
                runOnUiThread {
                    Toast.makeText(this@MainActivity, "Revoke failed: ${e.message}", Toast.LENGTH_SHORT).show()
                }
            }

            override fun onResponse(call: Call, response: Response) {
                runOnUiThread {
                    if (response.isSuccessful) {
                        Toast.makeText(this@MainActivity, "Device revoked", Toast.LENGTH_SHORT).show()
                        fetchDevices()
                    } else {
                        Toast.makeText(this@MainActivity, "Revoke error: ${response.code}", Toast.LENGTH_SHORT).show()
                    }
                }
            }
        })
    }

    // -------------------------------------------------------------------------
    // Settings Dialog
    // -------------------------------------------------------------------------

    private fun showSettingsDialog() {
        val dialogView = LayoutInflater.from(this).inflate(R.layout.dialog_settings, null)
        val editServerUrl = dialogView.findViewById<EditText>(R.id.editServerUrl)
        val editPassphrase = dialogView.findViewById<EditText>(R.id.editPassphrase)
        val editDeviceLabel = dialogView.findViewById<EditText>(R.id.editDeviceLabel)

        editServerUrl.setText(secureStorage.serverUrl)
        editPassphrase.setText(secureStorage.syncPassphrase)
        editDeviceLabel.setText(secureStorage.deviceLabel)

        AlertDialog.Builder(this)
            .setTitle("ClipSync Settings")
            .setView(dialogView)
            .setPositiveButton("Save") { _, _ ->
                val newUrl = editServerUrl.text.toString().trim()
                val newPass = editPassphrase.text.toString()
                val newLabel = editDeviceLabel.text.toString().trim()

                if (newUrl.isNotEmpty()) secureStorage.serverUrl = newUrl
                secureStorage.syncPassphrase = newPass
                if (newLabel.isNotEmpty()) secureStorage.deviceLabel = newLabel

                refreshUIFromStorage()
                if (secureStorage.hasCredentials()) {
                    startSyncService(reconnect = true)
                }
                Toast.makeText(this, "Settings saved", Toast.LENGTH_SHORT).show()
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    // -------------------------------------------------------------------------
    // Adapters
    // -------------------------------------------------------------------------

    class HistoryAdapter(
        private val items: List<HistoryEntry>,
        private val onCopyClick: (HistoryEntry) -> Unit
    ) : RecyclerView.Adapter<HistoryAdapter.ViewHolder>() {

        private val timeFormat = SimpleDateFormat("HH:mm:ss", Locale.getDefault())

        class ViewHolder(v: View) : RecyclerView.ViewHolder(v) {
            val txtSourceBadge: TextView = v.findViewById(R.id.txtSourceBadge)
            val txtTimestamp: TextView = v.findViewById(R.id.txtTimestamp)
            val btnCopyItem: MaterialButton = v.findViewById(R.id.btnCopyItem)
            val txtContent: TextView = v.findViewById(R.id.txtContent)
        }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
            val v = LayoutInflater.from(parent.context).inflate(R.layout.item_history, parent, false)
            return ViewHolder(v)
        }

        override fun onBindViewHolder(holder: ViewHolder, position: Int) {
            val item = items[position]
            holder.txtSourceBadge.text = item.source
            holder.txtTimestamp.text = timeFormat.format(Date(item.timestamp))
            holder.txtContent.text = item.text

            holder.btnCopyItem.setOnClickListener { onCopyClick(item) }
            holder.itemView.setOnClickListener { onCopyClick(item) }
        }

        override fun getItemCount(): Int = items.size
    }

    class DeviceAdapter(
        private val devices: List<DeviceInfo>,
        private val onRevokeClick: (DeviceInfo) -> Unit
    ) : RecyclerView.Adapter<DeviceAdapter.ViewHolder>() {

        class ViewHolder(v: View) : RecyclerView.ViewHolder(v) {
            val txtDeviceName: TextView = v.findViewById(R.id.txtDeviceName)
            val txtPlatformBadge: TextView = v.findViewById(R.id.txtPlatformBadge)
            val txtDeviceDetail: TextView = v.findViewById(R.id.txtDeviceDetail)
            val btnRevokeDevice: MaterialButton = v.findViewById(R.id.btnRevokeDevice)
        }

        override fun onCreateViewHolder(parent: ViewGroup, viewType: Int): ViewHolder {
            val v = LayoutInflater.from(parent.context).inflate(R.layout.item_device, parent, false)
            return ViewHolder(v)
        }

        override fun onBindViewHolder(holder: ViewHolder, position: Int) {
            val dev = devices[position]
            holder.txtDeviceName.text = if (dev.isCurrent == true) "${dev.label} (This device)" else dev.label
            holder.txtPlatformBadge.text = dev.platform
            holder.txtDeviceDetail.text = if (!dev.lastSeen.isNullOrBlank()) "Last active: ${dev.lastSeen.take(19).replace('T', ' ')}" else "Active"

            if (dev.isCurrent == true) {
                holder.btnRevokeDevice.visibility = View.GONE
            } else {
                holder.btnRevokeDevice.visibility = View.VISIBLE
                holder.btnRevokeDevice.setOnClickListener { onRevokeClick(dev) }
            }
        }

        override fun getItemCount(): Int = devices.size
    }
}
