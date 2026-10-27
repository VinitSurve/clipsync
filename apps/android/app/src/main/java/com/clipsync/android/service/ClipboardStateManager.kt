package com.clipsync.android.service

import com.clipsync.android.crypto.ClipSyncCrypto

/**
 * Thread-safe singleton managing clipboard synchronization state,
 * deduplication hashes, and pending remote updates across MainActivity,
 * ClipSyncForegroundService, and ClipSyncCaptureActivity.
 */
object ClipboardStateManager {

    @Volatile
    var lastSentHash: String = ""
        private set

    @Volatile
    var lastReceivedHash: String = ""
        private set

    @Volatile
    var lastLocalHash: String = ""
        private set

    @Volatile
    var pendingRemoteText: String? = null
        private set

    @Volatile
    var pendingRemoteSource: String? = null
        private set

    @Volatile
    var pendingRemoteTimestamp: Long = 0L
        private set

    @Volatile
    var isAppInForeground: Boolean = false

    @Synchronized
    fun isEcho(hash: String): Boolean {
        if (hash.isEmpty()) return true
        return hash == lastSentHash || hash == lastReceivedHash
    }

    @Synchronized
    fun isAlreadySynced(hash: String): Boolean {
        if (hash.isEmpty()) return true
        return hash == lastSentHash || hash == lastLocalHash
    }

    @Synchronized
    fun markSent(text: String): String {
        val hash = ClipSyncCrypto.sha256(text)
        lastSentHash = hash
        lastLocalHash = hash
        // If the text being sent matches the pending text, clear pending
        if (pendingRemoteText == text) {
            clearPending()
        }
        return hash
    }

    @Synchronized
    fun markReceived(text: String, source: String, timestamp: Long): String {
        val hash = ClipSyncCrypto.sha256(text)
        lastReceivedHash = hash
        pendingRemoteText = text
        pendingRemoteSource = source
        pendingRemoteTimestamp = timestamp
        return hash
    }

    @Synchronized
    fun markAppliedLocally(text: String) {
        val hash = ClipSyncCrypto.sha256(text)
        lastLocalHash = hash
        lastReceivedHash = hash
        clearPending()
    }

    @Synchronized
    fun clearPending() {
        pendingRemoteText = null
        pendingRemoteSource = null
        pendingRemoteTimestamp = 0L
    }

    @Synchronized
    fun reset() {
        lastSentHash = ""
        lastReceivedHash = ""
        lastLocalHash = ""
        clearPending()
        isAppInForeground = false
    }

    /**
     * Calculates exponential backoff delay in seconds.
     * Capped between 2s and 30s.
     */
    fun calculateBackoffSeconds(attempt: Int): Long {
        val shift = attempt.coerceIn(0, 5)
        return (1L shl shift).coerceIn(2L, 30L)
    }
}
