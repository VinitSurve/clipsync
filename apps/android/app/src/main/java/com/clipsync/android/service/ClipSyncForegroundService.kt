package com.clipsync.android.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.clipsync.android.crypto.ClipSyncCrypto
import com.clipsync.android.models.EncryptedPayloadEnvelope
import com.clipsync.android.models.SyncItem
import com.clipsync.android.models.SyncItemMetadata
import com.clipsync.android.ui.MainActivity
import com.google.gson.Gson
import com.google.gson.JsonObject
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.UUID
import java.util.concurrent.TimeUnit

class ClipSyncForegroundService : Service() {

    private lateinit var clipboardManager: ClipboardManager
    private lateinit var okHttpClient: OkHttpClient
    private var webSocket: WebSocket? = null
    private var crypto: ClipSyncCrypto? = null

    private var serverUrl: String = "http://10.0.2.2:8787"
    private var deviceId: String = ""
    private var token: String = ""
    private var syncSalt: String = ""
    private var syncPassphrase: String = "clipsync-master-sync-passphrase"

    private var lastReceivedHash: String = ""
    private var lastSentHash: String = ""
    private var isPaused: Boolean = false
    private val seenItemIds = HashSet<String>()
    private val gson = Gson()

    private val clipListener = ClipboardManager.OnPrimaryClipChangedListener {
        onClipboardChanged()
    }

    override fun onCreate() {
        super.onCreate()
        clipboardManager = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        clipboardManager.addPrimaryClipChangedListener(clipListener)

        okHttpClient = OkHttpClient.Builder()
            .pingInterval(20, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .build()

        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        intent?.let {
            if (it.hasExtra("serverUrl")) serverUrl = it.getStringExtra("serverUrl") ?: serverUrl
            if (it.hasExtra("deviceId")) deviceId = it.getStringExtra("deviceId") ?: deviceId
            if (it.hasExtra("token")) token = it.getStringExtra("token") ?: token
            if (it.hasExtra("syncSalt")) syncSalt = it.getStringExtra("syncSalt") ?: syncSalt
            if (it.hasExtra("isPaused")) isPaused = it.getBooleanExtra("isPaused", false)

            if (it.getBooleanExtra("action_sync_now", false)) {
                onClipboardChanged()
            }
        }

        startForeground(NOTIFICATION_ID, buildNotification("ClipSync Active — Ready to sync"))

        if (deviceId.isNotEmpty() && token.isNotEmpty() && syncSalt.isNotEmpty()) {
            if (crypto == null) {
                crypto = ClipSyncCrypto(syncPassphrase, syncSalt)
            }
            connectWebSocket()
        }

        return START_STICKY
    }

    private fun connectWebSocket() {
        webSocket?.close(1000, "Reconnecting")

        val wsScheme = if (serverUrl.startsWith("https")) "wss" else "ws"
        val cleanHost = serverUrl.replace("http://", "").replace("https://", "").trimEnd('/')
        val url = "$wsScheme://$cleanHost/v1/events?token=$token"

        val request = Request.Builder().url(url).build()
        webSocket = okHttpClient.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                updateNotification("Connected to ClipSync")
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                handleIncomingMessage(text)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                updateNotification("Disconnected — Reconnecting...")
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                updateNotification("Connection error: ${t.localizedMessage}")
            }
        })
    }

    private fun handleIncomingMessage(rawJson: String) {
        try {
            val json = gson.fromJson(rawJson, JsonObject::class.java)
            val type = json.get("type")?.asString ?: return

            if (type == "sync:item") {
                val itemObj = json.getAsJsonObject("item")
                val seq = json.get("sequenceNumber")?.asInt ?: 0
                val itemId = itemObj.get("itemId").asString
                val sourceDeviceId = itemObj.get("sourceDeviceId").asString

                // Acknowledge
                val ack = JsonObject()
                ack.addProperty("type", "ack")
                ack.addProperty("itemId", itemId)
                ack.addProperty("sequenceNumber", seq)
                webSocket?.send(ack.toString())

                // Loop prevention
                if (sourceDeviceId == deviceId) return
                if (seenItemIds.contains(itemId)) return
                seenItemIds.add(itemId)

                if (isPaused) return

                val payload = itemObj.get("payload").asString
                val envelope = gson.fromJson(payload, EncryptedPayloadEnvelope::class.java)
                val decrypted = crypto?.decrypt(envelope) ?: return

                // Set clipboard
                lastReceivedHash = ClipSyncCrypto.sha256(decrypted)
                val clip = ClipData.newPlainText("ClipSync", decrypted)
                clipboardManager.setPrimaryClip(clip)

                // Send broadcast intent to MainActivity
                val broadcast = Intent("com.clipsync.ITEM_RECEIVED").apply {
                    putExtra("text", decrypted)
                    putExtra("sourceApp", itemObj.getAsJsonObject("metadata")?.get("sourceApp")?.asString ?: "Remote")
                }
                sendBroadcast(broadcast)
                updateNotification("Synced from other device: ${decrypted.take(30)}...")
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private fun onClipboardChanged() {
        if (isPaused) return

        try {
            val primaryClip = clipboardManager.primaryClip
            if (primaryClip != null && primaryClip.itemCount > 0) {
                val text = primaryClip.getItemAt(0).text?.toString() ?: return
                if (text.isBlank()) return

                val hash = ClipSyncCrypto.sha256(text)
                if (hash == lastReceivedHash) return // Ignore echo from remote
                if (hash == lastSentHash) return     // Ignore duplicate

                lastSentHash = hash
                broadcastText(text)
            }
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private fun broadcastText(text: String) {
        val safeCrypto = crypto ?: return
        val ws = webSocket ?: return

        try {
            val envelope = safeCrypto.encrypt(text)
            val envelopeJson = gson.toJson(envelope)
            val itemId = "android-${UUID.randomUUID()}"
            seenItemIds.add(itemId)

            val syncItem = SyncItem(
                version = 1,
                itemId = itemId,
                type = "text",
                sourceDeviceId = deviceId,
                createdAt = java.time.Instant.now().toString(),
                payload = envelopeJson,
                metadata = SyncItemMetadata(
                    size = text.toByteArray().size,
                    sha256 = ClipSyncCrypto.sha256(text),
                    previewSnippet = text.take(100),
                    sourceApp = "Android Phone"
                )
            )

            val msg = JsonObject()
            msg.addProperty("type", "sync:item")
            msg.add("item", gson.toJsonTree(syncItem))

            ws.send(msg.toString())
            updateNotification("Copied & Synced: ${text.take(30)}...")
        } catch (e: Exception) {
            e.printStackTrace()
        }
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "ClipSync Clipboard Service",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Keeps universal clipboard synchronized across your devices"
            }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(contentText: String): Notification {
        val pendingIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("ClipSync Universal Clipboard")
            .setContentText(contentText)
            .setSmallIcon(android.R.drawable.ic_menu_agenda)
            .setContentIntent(pendingIntent)
            .setOngoing(true)
            .build()
    }

    private fun updateNotification(text: String) {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(NOTIFICATION_ID, buildNotification(text))
    }

    override fun onDestroy() {
        clipboardManager.removePrimaryClipChangedListener(clipListener)
        webSocket?.close(1000, "Service destroyed")
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val CHANNEL_ID = "clipsync_foreground_channel"
        const val NOTIFICATION_ID = 1001
    }
}
