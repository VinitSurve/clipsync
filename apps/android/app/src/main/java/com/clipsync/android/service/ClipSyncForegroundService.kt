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
import android.content.res.Configuration
import android.net.ConnectivityManager
import android.net.Network
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.provider.Settings
import android.util.Log
import androidx.core.app.NotificationCompat
import com.clipsync.android.crypto.ClipSyncCrypto
import com.clipsync.android.models.EncryptedPayloadEnvelope
import com.clipsync.android.models.SyncItem
import com.clipsync.android.models.SyncItemMetadata
import com.clipsync.android.overlay.FloatingSyncButtonManager
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
    private lateinit var floatingButtonManager: FloatingSyncButtonManager

    private var activeWebSocket: WebSocket? = null
    private var crypto: ClipSyncCrypto? = null
    private val gson = Gson()
    private val mainHandler = Handler(Looper.getMainLooper())

    private val seenItemIds = LinkedHashSet<String>()
    private var reconnectAttempt = 0
    private var isExplicitlyStopped = false
    private var currentConnectionState = STATE_DISCONNECTED

    @Volatile
    var pendingOutboundText: String? = null
        private set

    @Volatile
    private var lastAlertTime = 0L

    private val reconnectRunnable = Runnable {
        if (!isExplicitlyStopped && secureStorage.hasCredentials()) {
            connectWebSocket()
        }
    }

    private fun cancelScheduledReconnect() {
        mainHandler.removeCallbacks(reconnectRunnable)
    }

    private val clipListener = ClipboardManager.OnPrimaryClipChangedListener {
        onClipboardChanged()
    }

    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            Log.i(TAG, "Network interface active")
            if (!isExplicitlyStopped && secureStorage.hasCredentials() && (activeWebSocket == null || currentConnectionState != STATE_CONNECTED)) {
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

        floatingButtonManager = FloatingSyncButtonManager(this, secureStorage)
        if (secureStorage.isFloatingButtonEnabled && Settings.canDrawOverlays(this)) {
            floatingButtonManager.show()
        }
    }

    override fun onConfigurationChanged(newConfig: Configuration) {
        super.onConfigurationChanged(newConfig)
        try {
            floatingButtonManager.onConfigurationChanged(newConfig)
        } catch (e: Exception) {
            Log.w(TAG, "Error updating floating overlay on config change: ${e.message}")
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        intent?.let {
            val syncId = it.getStringExtra(EXTRA_SYNC_ID) ?: ("SVC-" + UUID.randomUUID().toString().take(6))
            when (it.action) {
                ACTION_SHOW_FLOATING_BUTTON -> {
                    if (Settings.canDrawOverlays(this)) {
                        secureStorage.isFloatingButtonEnabled = true
                        floatingButtonManager.show()
                        updateNotification("ClipSync Active — Ready to sync")
                    }
                }
                ACTION_HIDE_FLOATING_BUTTON -> {
                    secureStorage.isFloatingButtonEnabled = false
                    floatingButtonManager.hide()
                    updateNotification("ClipSync Active — Ready to sync")
                }
                ACTION_TOGGLE_FLOATING_BUTTON -> {
                    if (floatingButtonManager.isAttached) {
                        secureStorage.isFloatingButtonEnabled = false
                        floatingButtonManager.hide()
                    } else if (Settings.canDrawOverlays(this)) {
                        secureStorage.isFloatingButtonEnabled = true
                        floatingButtonManager.show()
                    }
                    updateNotification("ClipSync Active — Ready to sync")
                }
                ACTION_UPDATE_FLOATING_CONFIG -> {
                    if (it.hasExtra(EXTRA_FLOATING_SIZE_DP)) {
                        val sizeDp = it.getIntExtra(EXTRA_FLOATING_SIZE_DP, secureStorage.floatingButtonSizeDp)
                        floatingButtonManager.updateSize(sizeDp)
                    }
                    if (it.hasExtra(EXTRA_FLOATING_TRANSPARENCY)) {
                        val transparency = it.getBooleanExtra(EXTRA_FLOATING_TRANSPARENCY, secureStorage.isFloatingButtonTransparencyEnabled)
                        floatingButtonManager.updateTransparency(transparency)
                    }
                }
            }

            if (it.hasExtra(EXTRA_OUTBOUND_TEXT)) {
                val outbound = it.getStringExtra(EXTRA_OUTBOUND_TEXT)
                if (!outbound.isNullOrBlank()) {
                    val isManual = it.getBooleanExtra(EXTRA_IS_MANUAL, false)
                    broadcastText(outbound, isManual = isManual, syncId = syncId)
                }
            }

            if (it.getBooleanExtra(EXTRA_RECONNECT, false)) {
                reconnectAttempt = 0
                connectWebSocket(force = true)
            }
        }

        startForeground(NOTIFICATION_ID, buildNotification("ClipSync Active — Ready to sync"))

        if (secureStorage.hasCredentials()) {
            initCrypto()
            // Do NOT reconnect if already connected or actively connecting!
            if (activeWebSocket == null && currentConnectionState != STATE_CONNECTED && currentConnectionState != STATE_CONNECTING) {
                connectWebSocket()
            }
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
    private fun connectWebSocket(force: Boolean = false) {
        if (!secureStorage.hasCredentials() || isExplicitlyStopped) return

        cancelScheduledReconnect()

        // If already connected and not forced, keep existing connection
        if (!force && activeWebSocket != null && currentConnectionState == STATE_CONNECTED) {
            Log.d(TAG, "WebSocket already connected, skipping redundant connection")
            return
        }

        val oldSocket = activeWebSocket
        activeWebSocket = null
        try {
            oldSocket?.close(1000, "Reconnecting")
        } catch (_: Exception) {}

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
        activeWebSocket = okHttpClient.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                if (webSocket !== activeWebSocket) {
                    Log.w(TAG, "Ignoring onOpen for superseded WebSocket")
                    try { webSocket.close(1000, "Stale") } catch (_: Exception) {}
                    return
                }
                Log.i(TAG, "WebSocket connected successfully.")
                reconnectAttempt = 0
                cancelScheduledReconnect()
                broadcastConnectionState(STATE_CONNECTED)
                updateNotification("ClipSync Connected — Clipboard live")

                // Flush pending outbound text if queued while disconnected
                val pending = pendingOutboundText
                if (!pending.isNullOrBlank()) {
                    val flushSyncId = "FLUSH-" + UUID.randomUUID().toString().take(6)
                    Log.i(TAG, "[$flushSyncId] Flushing pending outbound text on connect: len=${pending.length}")
                    broadcastText(pending, isManual = false, syncId = flushSyncId)
                }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                if (webSocket !== activeWebSocket) return
                handleIncomingMessage(text)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                if (webSocket !== activeWebSocket) {
                    Log.d(TAG, "Ignoring onClosed for superseded WebSocket ($code: $reason)")
                    return
                }
                Log.w(TAG, "WebSocket closed ($code): $reason")
                activeWebSocket = null
                if (!isExplicitlyStopped) {
                    broadcastConnectionState(STATE_CONNECTING)
                    scheduleReconnect()
                }
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                if (webSocket !== activeWebSocket) {
                    Log.d(TAG, "Ignoring onFailure for superseded WebSocket: ${t.localizedMessage}")
                    return
                }
                Log.e(TAG, "WebSocket failure: ${t.localizedMessage}")
                activeWebSocket = null
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
        cancelScheduledReconnect()
        val delaySec = ClipboardStateManager.calculateBackoffSeconds(reconnectAttempt)
        reconnectAttempt++
        Log.i(TAG, "Scheduling reconnect in ${delaySec}s (attempt #$reconnectAttempt)")
        mainHandler.postDelayed(reconnectRunnable, delaySec * 1000)
    }

    private fun handleIncomingMessage(rawJson: String) {
        try {
            val json = gson.fromJson(rawJson, JsonObject::class.java) ?: return
            val type = json.get("type")?.asString ?: return

            if (type == "sync:ack" || type == "ack") {
                val ackItemId = json.get("itemId")?.asString ?: ""
                val seq = json.get("sequenceNumber")?.asInt ?: 0
                Log.i(TAG, "[ACK] Server acknowledged item: itemId=$ackItemId, seq=$seq")
                return
            }

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
                activeWebSocket?.send(ack.toString())

                // 2. Loop & duplicate suppression
                if (sourceDeviceId.isNotEmpty() && sourceDeviceId == secureStorage.deviceId) {
                    Log.d(TAG, "[ACK] Server forwarded our own item echo: itemId=$itemId (sourceDeviceId=$sourceDeviceId)")
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
        if (secureStorage.isPaused || !secureStorage.hasCredentials()) return

        val syncId = "BGCLIP-" + UUID.randomUUID().toString().take(6)
        val isForeground = ClipboardStateManager.isAppInForeground
        Log.i(TAG, "[$syncId] Background onClipboardChanged listener invoked. isForeground=$isForeground")

        if (isForeground) {
            // MainActivity handles foreground clipboard inspection
            return
        }

        // 1. Attempt direct read in case OS permits on this platform/ROM
        val clip = try {
            clipboardManager.primaryClip
        } catch (e: Exception) {
            Log.d(TAG, "[$syncId] Background primaryClip read threw: ${e.message}")
            null
        }

        val hasClip = clip != null && clip.itemCount > 0
        Log.i(TAG, "[$syncId] Background primaryClip read state: hasClip=$hasClip")

        if (hasClip) {
            val text = clip?.getItemAt(0)?.text?.toString()
            if (!text.isNullOrBlank()) {
                val hash = ClipSyncCrypto.sha256(text)
                if (!ClipboardStateManager.isEcho(hash) && !ClipboardStateManager.isAlreadySynced(hash)) {
                    Log.i(TAG, "[$syncId] Background clipboard read succeeded directly (len=${text.length}, hash=${hash.take(8)})")
                    broadcastText(text, isManual = false, syncId = syncId)
                    return
                } else {
                    Log.d(TAG, "[$syncId] Background clip is echo or already synced")
                    return
                }
            }
        }

        // 2. OS blocked background clipboard reading (standard Android 10+ privacy rule).
        // Post a copy-detected alert notification with 1-tap "Sync to Mac" action.
        val now = System.currentTimeMillis()
        if (now - lastAlertTime < 1500L) {
            Log.d(TAG, "[$syncId] Suppressing duplicate copy alert notification (debounced)")
            return
        }
        lastAlertTime = now

        Log.i(TAG, "[$syncId] Displaying copy-detected alert notification with 1-tap Sync to Mac action")
        showCopyAlertNotification(syncId)
    }

    fun broadcastText(text: String, isManual: Boolean = false, syncId: String = "") {
        val corrId = if (syncId.isNotEmpty()) syncId else ("SVC-" + UUID.randomUUID().toString().take(6))
        if (crypto == null) initCrypto()
        val safeCrypto = crypto ?: return
        val hash = ClipSyncCrypto.sha256(text)

        Log.i(TAG, "[$corrId] Foreground service received broadcast request: isManual=$isManual, len=${text.length}, hash=${hash.take(8)}")

        if (!isManual && (ClipboardStateManager.isEcho(hash) || ClipboardStateManager.isAlreadySynced(hash))) {
            Log.d(TAG, "[$corrId] broadcastText skipped: already synced or echo (hash=${hash.take(8)}, isEcho=${ClipboardStateManager.isEcho(hash)}, isAlreadySynced=${ClipboardStateManager.isAlreadySynced(hash)})")
            return
        }

        val ws = activeWebSocket
        val isConnected = ws != null && currentConnectionState == STATE_CONNECTED
        Log.i(TAG, "[$corrId] WebSocket state: wsConnected=$isConnected, state=$currentConnectionState")

        if (!isConnected || ws == null) {
            Log.i(TAG, "[$corrId] WebSocket not connected ($currentConnectionState). Buffered text for transmission upon connect.")
            pendingOutboundText = text
            FloatingSyncButtonManager.notifySyncResult(false, "Offline — Queued")
            if (!isExplicitlyStopped && secureStorage.hasCredentials() && currentConnectionState != STATE_CONNECTING) {
                connectWebSocket()
            }
            return
        }

        try {
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

            Log.i(TAG, "[$corrId] Outbound message queued: itemId=$itemId, hash=${hash.take(8)}")
            val sent = ws.send(msg.toString())
            Log.i(TAG, "[$corrId] WebSocket send result: sent=$sent")
            if (sent) {
                ClipboardStateManager.markSent(text)
                pendingOutboundText = null
                FloatingSyncButtonManager.notifySyncResult(true, "Synced to Mac")
                Log.i(TAG, "[$corrId] Successfully synced text to broker ($itemId, ${text.length} chars, hash=${hash.take(8)})")

                val preview = if (text.length > 30) text.take(30) + "..." else text
                updateNotification("Synced: $preview")

                try {
                    val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
                    nm.cancel(NOTIFICATION_ALERT_ID)
                } catch (_: Exception) {}

                // Also add locally to history
                val broadcast = Intent(ACTION_ITEM_RECEIVED).apply {
                    putExtra("text", text)
                    putExtra("sourceApp", "$deviceLabel (You)")
                    putExtra("itemId", itemId)
                    putExtra("timestamp", System.currentTimeMillis())
                }
                sendBroadcast(broadcast)
            } else {
                Log.w(TAG, "[$corrId] ws.send returned false. Buffered text for reconnect.")
                pendingOutboundText = text
                FloatingSyncButtonManager.notifySyncResult(false, "Sync failed")
            }

        } catch (e: Exception) {
            Log.e(TAG, "[$corrId] Failed to broadcast text: ${e.message}")
            FloatingSyncButtonManager.notifySyncResult(false, "Send error")
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
            val manager = getSystemService(NotificationManager::class.java)

            // 1. Ongoing persistent foreground service channel
            val serviceChannel = NotificationChannel(
                CHANNEL_ID,
                "ClipSync Live Clipboard",
                NotificationManager.IMPORTANCE_LOW
            ).apply {
                description = "Keeps universal clipboard synchronized across your devices"
            }
            manager.createNotificationChannel(serviceChannel)

            // 2. High-importance channel for copy alerts with 1-tap Sync to Mac action
            val alertChannel = NotificationChannel(
                CHANNEL_ALERT_ID,
                "ClipSync Copy Alerts",
                NotificationManager.IMPORTANCE_HIGH
            ).apply {
                description = "Alerts when new text is copied with 1-tap sync to Mac"
                enableVibration(false)
                setShowBadge(true)
            }
            manager.createNotificationChannel(alertChannel)
        }
    }

    private fun showCopyAlertNotification(syncId: String) {
        val syncIntent = Intent(this, ClipSyncCaptureActivity::class.java).apply {
            action = ClipSyncCaptureActivity.ACTION_CAPTURE_AND_SYNC
            putExtra(ClipSyncCaptureActivity.EXTRA_SOURCE, "copy_alert")
            putExtra(EXTRA_SYNC_ID, syncId)
        }
        val syncPendingIntent = PendingIntent.getActivity(
            this,
            NOTIFICATION_ALERT_ID,
            syncIntent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val notif = NotificationCompat.Builder(this, CHANNEL_ALERT_ID)
            .setContentTitle("New text copied")
            .setContentText("Tap to sync clipboard to Mac")
            .setSmallIcon(android.R.drawable.ic_menu_agenda)
            .setContentIntent(syncPendingIntent)
            .addAction(android.R.drawable.ic_menu_send, "Sync to Mac", syncPendingIntent)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setAutoCancel(true)
            .setTimeoutAfter(30000)
            .build()

        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        manager.notify(NOTIFICATION_ALERT_ID, notif)
    }

    private fun buildNotification(contentText: String): Notification {
        val syncIntent = Intent(this, ClipSyncCaptureActivity::class.java).apply {
            action = ClipSyncCaptureActivity.ACTION_SYNC_TO_MAC
            putExtra(ClipSyncCaptureActivity.EXTRA_SOURCE, "foreground_notification")
        }
        val syncPendingIntent = PendingIntent.getActivity(
            this,
            1,
            syncIntent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        val toggleBallIntent = Intent(this, ClipSyncForegroundService::class.java).apply {
            action = ACTION_TOGGLE_FLOATING_BUTTON
        }
        val toggleBallPendingIntent = PendingIntent.getService(
            this,
            2,
            toggleBallIntent,
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )
        val syncCoreActionTitle = if (::floatingButtonManager.isInitialized && floatingButtonManager.isAttached) "Hide Sync Core" else "Show Sync Core"

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("ClipSync — Tap to Sync to Mac")
            .setContentText(contentText)
            .setSmallIcon(android.R.drawable.ic_menu_agenda)
            .setContentIntent(syncPendingIntent)
            .addAction(android.R.drawable.ic_menu_send, "Sync to Mac", syncPendingIntent)
            .addAction(android.R.drawable.ic_menu_manage, syncCoreActionTitle, toggleBallPendingIntent)
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
        cancelScheduledReconnect()
        try {
            floatingButtonManager.destroy()
        } catch (_: Exception) {}
        try {
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
            nm.cancel(NOTIFICATION_ALERT_ID)
        } catch (_: Exception) {}
        try {
            clipboardManager.removePrimaryClipChangedListener(clipListener)
        } catch (_: Exception) {}
        try {
            connectivityManager.unregisterNetworkCallback(networkCallback)
        } catch (_: Exception) {}
        val ws = activeWebSocket
        activeWebSocket = null
        try {
            ws?.close(1000, "Service stopped")
        } catch (_: Exception) {}
        broadcastConnectionState(STATE_DISCONNECTED)
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    companion object {
        const val TAG = "ClipSyncService"
        const val CHANNEL_ID = "clipsync_foreground_channel"
        const val CHANNEL_ALERT_ID = "clipsync_copy_alerts_channel"
        const val NOTIFICATION_ID = 1001
        const val NOTIFICATION_ALERT_ID = 1002

        const val EXTRA_OUTBOUND_TEXT = "extra_outbound_text"
        const val EXTRA_IS_MANUAL = "extra_is_manual"
        const val EXTRA_RECONNECT = "extra_reconnect"
        const val EXTRA_SYNC_ID = "extra_sync_id"

        const val ACTION_SHOW_FLOATING_BUTTON = "com.clipsync.action.SHOW_FLOATING_BUTTON"
        const val ACTION_HIDE_FLOATING_BUTTON = "com.clipsync.action.HIDE_FLOATING_BUTTON"
        const val ACTION_TOGGLE_FLOATING_BUTTON = "com.clipsync.action.TOGGLE_FLOATING_BUTTON"
        const val ACTION_UPDATE_FLOATING_CONFIG = "com.clipsync.action.UPDATE_FLOATING_CONFIG"
        const val EXTRA_FLOATING_SIZE_DP = "extra_floating_size_dp"
        const val EXTRA_FLOATING_TRANSPARENCY = "extra_floating_transparency"

        const val ACTION_ITEM_RECEIVED = "com.clipsync.ITEM_RECEIVED"
        const val ACTION_CONNECTION_STATE = "com.clipsync.CONNECTION_STATE"

        const val STATE_CONNECTED = "CONNECTED"
        const val STATE_CONNECTING = "CONNECTING"
        const val STATE_DISCONNECTED = "DISCONNECTED"
        const val STATE_NOT_PAIRED = "NOT_PAIRED"
        const val STATE_ERROR = "ERROR"
    }
}
