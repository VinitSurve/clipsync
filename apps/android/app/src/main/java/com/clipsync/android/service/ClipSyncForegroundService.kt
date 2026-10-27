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
import android.net.ConnectivityManager
import android.net.Network
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.util.Log
import androidx.core.app.NotificationCompat
import com.clipsync.android.crypto.ClipSyncCrypto
import com.clipsync.android.models.EncryptedPayloadEnvelope
import com.clipsync.android.models.SyncItem
import com.clipsync.android.models.SyncItemMetadata
import com.clipsync.android.storage.SecureStorage
import com.clipsync.android.ui.ClipSyncCaptureActivity
import com.clipsync.android.ui.MainActivity
import com.google.gson.Gson
import com.google.gson.JsonObject
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.time.Instant
import java.util.UUID
import java.util.concurrent.TimeUnit

class ClipSyncForegroundService : Service() {

    private lateinit var clipboardManager: ClipboardManager
    private lateinit var connectivityManager: ConnectivityManager
    private lateinit var secureStorage: SecureStorage
    private lateinit var okHttpClient: OkHttpClient

    private var webSocket: WebSocket? = null
    private var crypto: ClipSyncCrypto? = null
    private val gson = Gson()
    private val mainHandler = Handler(Looper.getMainLooper())

    private val seenItemIds = LinkedHashSet<String>()
    private var reconnectAttempt = 0
    private var isExplicitlyStopped = false
    private var currentConnectionState = STATE_DISCONNECTED

    private val clipListener = ClipboardManager.OnPrimaryClipChangedListener {
        onClipboardChanged()
    }

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            Log.i(TAG, "Network interface active")
            if (!isExplicitlyStopped && secureStorage.hasCredentials() && (webSocket == null || currentConnectionState != STATE_CONNECTED)) {
                mainHandler.post {
                    reconnectAttempt = 0
                    connectWebSocket()
                }
            }
        }

        override fun onLost(network: Network) {
            Log.w(TAG, "Network interface lost")
            broadcastConnectionState(STATE_DISCONNECTED, "Network offline")
        }
    }

    override fun onCreate() {
        super.onCreate()
        secureStorage = SecureStorage(this)
        clipboardManager = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        connectivityManager = getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager

        try {
            clipboardManager.addPrimaryClipChangedListener(clipListener)
        } catch (e: Exception) {
            Log.w(TAG, "Could not add background clip listener: ${e.message}")
        }

        try {
            connectivityManager.registerDefaultNetworkCallback(networkCallback)
        } catch (e: Exception) {
            Log.w(TAG, "Could not register default network callback: ${e.message}")
        }

        okHttpClient = OkHttpClient.Builder()
            .pingInterval(20, TimeUnit.SECONDS)
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(0, TimeUnit.MILLISECONDS)
            .build()

        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        intent?.let {
            if (it.hasExtra(EXTRA_OUTBOUND_TEXT)) {
                val outbound = it.getStringExtra(EXTRA_OUTBOUND_TEXT)
                if (!outbound.isNullOrBlank()) {
                    broadcastText(outbound, isManual = true)
                }
            }

            if (it.getBooleanExtra(EXTRA_RECONNECT, false)) {
                reconnectAttempt = 0
                connectWebSocket()
            }
        }

        startForeground(NOTIFICATION_ID, buildNotification("ClipSync Active — Ready to sync"))

        if (secureStorage.hasCredentials()) {
            initCrypto()
            connectWebSocket()
        } else {
            updateNotification("ClipSync — Waiting for device pairing")
            broadcastConnectionState(STATE_NOT_PAIRED)
        }

        return START_STICKY
    }

    private fun initCrypto() {
        val salt = secureStorage.syncSalt
        val pass = secureStorage.syncPassphrase
        if (salt.isNotEmpty()) {
            crypto = ClipSyncCrypto(pass, salt)
        }
    }

    @Synchronized
    private fun connectWebSocket() {
        if (!secureStorage.hasCredentials()) return
        isExplicitlyStopped = false

        webSocket?.close(1000, "Reconnecting")
        broadcastConnectionState(STATE_CONNECTING)

        val serverUrl = secureStorage.serverUrl
        val token = secureStorage.token
        val wsScheme = if (serverUrl.startsWith("https", ignoreCase = true)) "wss" else "ws"
        val cleanHost = serverUrl
            .replace("http://", "", ignoreCase = true)
            .replace("https://", "", ignoreCase = true)
            .trimEnd('/')

        val wsUrl = "$wsScheme://$cleanHost/v1/events?token=$token"
        Log.i(TAG, "Connecting to WebSocket: $wsScheme://$cleanHost/v1/events")

        val request = Request.Builder().url(wsUrl).build()
        webSocket = okHttpClient.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                Log.i(TAG, "WebSocket connected successfully.")
                reconnectAttempt = 0
                broadcastConnectionState(STATE_CONNECTED)
                updateNotification("ClipSync Connected — Clipboard live")
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                handleIncomingMessage(text)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                Log.w(TAG, "WebSocket closed ($code): $reason")
                if (!isExplicitlyStopped) {
                    broadcastConnectionState(STATE_CONNECTING)
                    scheduleReconnect()
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                Log.e(TAG, "WebSocket failure: ${t.localizedMessage}")
                if (!isExplicitlyStopped) {
                    broadcastConnectionState(STATE_ERROR, t.localizedMessage ?: "Connection error")
                    updateNotification("ClipSync Disconnected — Reconnecting...")
                    scheduleReconnect()
                }
            }
        })
    }

    private fun scheduleReconnect() {
        if (isExplicitlyStopped) return
        val delaySec = ClipboardStateManager.calculateBackoffSeconds(reconnectAttempt)
        reconnectAttempt++
        Log.i(TAG, "Scheduling reconnect in ${delaySec}s (attempt #$reconnectAttempt)")
        mainHandler.postDelayed({
            if (!isExplicitlyStopped && secureStorage.hasCredentials()) {
                connectWebSocket()
            }
        }, delaySec * 1000)
    }

    private fun handleIncomingMessage(rawJson: String) {
        try {
            val json = gson.fromJson(rawJson, JsonObject::class.java) ?: return
            val type = json.get("type")?.asString ?: return

            if (type == "sync:item") {
                val itemObj = json.getAsJsonObject("item") ?: return
                val seq = json.get("sequenceNumber")?.asInt ?: 0
                val itemId = itemObj.get("itemId")?.asString ?: ""
                val sourceDeviceId = itemObj.get("sourceDeviceId")?.asString ?: ""

                // 1. Send immediate ACK
                val ack = JsonObject().apply {
                    addProperty("type", "ack")
                    addProperty("itemId", itemId)
                    addProperty("sequenceNumber", seq)
                }
                webSocket?.send(ack.toString())

                // 2. Loop & duplicate suppression
                if (sourceDeviceId.isNotEmpty() && sourceDeviceId == secureStorage.deviceId) {
                    return // Echo of item sent by this device
                }
                if (itemId.isNotEmpty()) {
                    synchronized(seenItemIds) {
                        if (seenItemIds.contains(itemId)) return
                        seenItemIds.add(itemId)
                        if (seenItemIds.size > 200) {
                            seenItemIds.iterator().apply { next(); remove() }
                        }
                    }
                }

                if (secureStorage.isPaused) return

                // 3. Decrypt payload
                val payloadStr = itemObj.get("payload")?.asString ?: return
                val envelope = gson.fromJson(payloadStr, EncryptedPayloadEnvelope::class.java)
                if (crypto == null) initCrypto()
                val decrypted = crypto?.decrypt(envelope) ?: return

                // 4. Integrity check via SHA-256
                val computedHash = ClipSyncCrypto.sha256(decrypted)
                val expectedHash = itemObj.getAsJsonObject("metadata")?.get("sha256")?.asString
                if (!expectedHash.isNullOrEmpty() && !computedHash.equals(expectedHash, ignoreCase = true)) {
                    Log.w(TAG, "Integrity check failed for incoming item: hash mismatch.")
                    return
                }

                // Check echo against what we just sent
                if (computedHash == ClipboardStateManager.lastSentHash) {
                    return
                }

                val sourceApp = itemObj.getAsJsonObject("metadata")?.get("sourceApp")?.asString ?: "Remote Device"

                // 5. Mark received in shared state
                ClipboardStateManager.markReceived(decrypted, sourceApp, System.currentTimeMillis())

                // 6. Try applying directly if OS permits (in foreground or accessible)
                try {
                    val clip = ClipData.newPlainText("ClipSync", decrypted)
                    clipboardManager.setPrimaryClip(clip)
                    ClipboardStateManager.markAppliedLocally(decrypted)
                } catch (e: Exception) {
                    Log.d(TAG, "Direct background clipboard write restricted by OS: ${e.message}")
                }

                // 7. Broadcast to UI so MainActivity can add to history AND auto-apply if foregrounded
                val broadcast = Intent(ACTION_ITEM_RECEIVED).apply {
                    putExtra("text", decrypted)
                    putExtra("sourceApp", sourceApp)
                    putExtra("itemId", itemId)
                    putExtra("timestamp", System.currentTimeMillis())
                }
                sendBroadcast(broadcast)

                // 8. Update notification with 1-tap Copy action
                val preview = if (decrypted.length > 35) decrypted.take(35) + "..." else decrypted
                updateNotificationWithAction(
                    title = "Received from $sourceApp",
                    text = preview,
                    fullTextToCopy = decrypted
                )
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error handling incoming message: ${e.message}")
        }
    }

    private fun onClipboardChanged() {
        if (secureStorage.isPaused) return

        try {
            val clip = clipboardManager.primaryClip
            if (clip != null && clip.itemCount > 0) {
                val text = clip.getItemAt(0).text?.toString()
                if (!text.isNullOrBlank()) {
                    val hash = ClipSyncCrypto.sha256(text)
                    if (ClipboardStateManager.isEcho(hash)) return
                    if (ClipboardStateManager.isAlreadySynced(hash)) return

                    ClipboardStateManager.markSent(text)
                    broadcastText(text, isManual = false)
                }
            }
        } catch (e: Exception) {
            Log.d(TAG, "onClipboardChanged check skipped: ${e.message}")
        }
    }

    fun broadcastText(text: String, isManual: Boolean = false) {
        if (crypto == null) initCrypto()
        val safeCrypto = crypto ?: return
        val ws = webSocket ?: return

        try {
            val hash = ClipboardStateManager.markSent(text)

            val envelope = safeCrypto.encrypt(text)
            val envelopeJson = gson.toJson(envelope)
            val itemId = "android-${UUID.randomUUID()}"

            synchronized(seenItemIds) {
                seenItemIds.add(itemId)
            }

            val deviceLabel = secureStorage.deviceLabel
            val syncItem = SyncItem(
                version = 1,
                itemId = itemId,
                type = "text",
                sourceDeviceId = secureStorage.deviceId,
                createdAt = Instant.now().toString(),
                payload = envelopeJson,
                metadata = SyncItemMetadata(
                    size = text.toByteArray().size,
                    mimeType = "text/plain",
                    sha256 = hash,
                    previewSnippet = text.take(100),
                    sourceApp = deviceLabel
                )
            )

            val msg = JsonObject().apply {
                addProperty("type", "sync:item")
                add("item", gson.toJsonTree(syncItem))
            }

            ws.send(msg.toString())
            Log.i(TAG, "Successfully synced text to broker ($itemId, ${text.length} chars)")

            val preview = if (text.length > 30) text.take(30) + "..." else text
            updateNotification("Synced: $preview")

            // Also add locally to history
            val broadcast = Intent(ACTION_ITEM_RECEIVED).apply {
                putExtra("text", text)
                putExtra("sourceApp", "$deviceLabel (You)")
                putExtra("itemId", itemId)
                putExtra("timestamp", System.currentTimeMillis())
            }
            sendBroadcast(broadcast)

        } catch (e: Exception) {
            Log.e(TAG, "Failed to broadcast text: ${e.message}")
        }
    }

    private fun broadcastConnectionState(state: String, error: String? = null) {
        currentConnectionState = state
        val intent = Intent(ACTION_CONNECTION_STATE).apply {
            putExtra("state", state)
            if (error != null) putExtra("error", error)
        }
        sendBroadcast(intent)
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                "ClipSync Live Clipboard",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Keeps universal clipboard synchronized across your devices"
            }
            val manager = getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(contentText: String): Notification {
        val openAppIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val syncIntent = Intent(this, ClipSyncCaptureActivity::class.java).apply {
            action = ClipSyncCaptureActivity.ACTION_CAPTURE_AND_SYNC
        }
        val syncPendingIntent = PendingIntent.getActivity(
            this,
            1,
            syncIntent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("ClipSync Universal Clipboard")
            .setContentText(contentText)
            .setSmallIcon(android.R.drawable.ic_menu_agenda)
            .setContentIntent(openAppIntent)
            .addAction(android.R.drawable.ic_menu_share, "Sync Clipboard", syncPendingIntent)
            .setOngoing(true)
            .build()
    }

    private fun updateNotification(text: String) {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(NOTIFICATION_ID, buildNotification(text))
    }

    private fun updateNotificationWithAction(title: String, text: String, fullTextToCopy: String) {
        val openAppIntent = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val copyIntent = Intent(this, ClipSyncCaptureActivity::class.java).apply {
            action = ClipSyncCaptureActivity.ACTION_APPLY_CLIPBOARD
            putExtra(ClipSyncCaptureActivity.EXTRA_TEXT_TO_APPLY, fullTextToCopy)
        }
        val copyPendingIntent = PendingIntent.getActivity(
            this, fullTextToCopy.hashCode(), copyIntent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val notif = NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(title)
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_agenda)
            .setContentIntent(openAppIntent)
            .addAction(android.R.drawable.ic_menu_set_as, "Copy to Clipboard", copyPendingIntent)
            .setOngoing(true)
            .build()

        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(NOTIFICATION_ID, notif)
    }

    override fun onDestroy() {
        isExplicitlyStopped = true
        try {
            clipboardManager.removePrimaryClipChangedListener(clipListener)
        } catch (_: Exception) {}
        try {
            connectivityManager.unregisterNetworkCallback(networkCallback)
        } catch (_: Exception) {}
        webSocket?.close(1000, "Service stopped")
        broadcastConnectionState(STATE_DISCONNECTED)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val TAG = "ClipSyncService"
        const val CHANNEL_ID = "clipsync_foreground_channel"
        const val NOTIFICATION_ID = 1001

        const val EXTRA_OUTBOUND_TEXT = "extra_outbound_text"
        const val EXTRA_RECONNECT = "extra_reconnect"

        const val ACTION_ITEM_RECEIVED = "com.clipsync.ITEM_RECEIVED"
        const val ACTION_CONNECTION_STATE = "com.clipsync.CONNECTION_STATE"

        const val STATE_CONNECTED = "CONNECTED"
        const val STATE_CONNECTING = "CONNECTING"
        const val STATE_DISCONNECTED = "DISCONNECTED"
        const val STATE_NOT_PAIRED = "NOT_PAIRED"
        const val STATE_ERROR = "ERROR"
    }
}
