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

    /**
     * Determines whether pending remote text should be auto-applied to the local clipboard.
     * Prevents overwriting newer local clipboard content with stale remote updates.
     */
    @Synchronized
    fun shouldApplyRemote(localClipTimestamp: Long, localText: String?): Boolean {
        val remote = pendingRemoteText ?: return false
        if (remote.isBlank()) return false

        // If local clipboard already contains the remote text, mark applied and don't re-apply
        if (localText == remote) {
            markAppliedLocally(remote)
            return false
        }

        // If local clip was copied AFTER remote text was received, local is newer!
        if (localClipTimestamp > pendingRemoteTimestamp && !localText.isNullOrBlank()) {
            val localHash = ClipSyncCrypto.sha256(localText)
            // If local text is not an echo of remote, user copied something new locally
            if (localHash != lastReceivedHash) {
                clearPending()
                return false
            }
        }

        return true
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
