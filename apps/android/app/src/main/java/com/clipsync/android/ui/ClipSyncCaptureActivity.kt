package com.clipsync.android.ui

import android.app.Activity
import android.app.NotificationManager
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.Toast
import androidx.core.content.ContextCompat
import com.clipsync.android.service.ClipSyncForegroundService
import com.clipsync.android.service.ClipboardStateManager
import com.clipsync.android.storage.SecureStorage
import java.util.UUID

/**
 * Translucent, 0ms trampoline activity.
 * Needed on Android 10+ (API 29+) to access ClipboardManager with window focus
 * when triggered from Copy Alert Notifications, Quick Settings Tile, or the
 * system text-selection menu (ACTION_PROCESS_TEXT).
 */
class ClipSyncCaptureActivity : Activity() {

    private val mainHandler = Handler(Looper.getMainLooper())
    private var hasExecuted = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val action = intent?.action
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager

        // Dismiss copy alert notification upon user interaction
        nm.cancel(ClipSyncForegroundService.NOTIFICATION_ALERT_ID)

        when (action) {
            Intent.ACTION_PROCESS_TEXT -> {
                handleProcessText()
                closeActivity()
            }
            ACTION_APPLY_CLIPBOARD -> {
                handleApplyClipboard()
                closeActivity()
            }
            ACTION_CAPTURE_AND_SYNC, ACTION_SYNC_TO_MAC -> {
                // Must wait for window focus on Android 10+ (API 29+) before reading primaryClip
                // Safety timeout in case window focus is delayed or never granted
                mainHandler.postDelayed({
                    if (!hasExecuted && !isFinishing) {
                        Log.w(TAG, "Window focus timeout reached; attempting clipboard capture fallback")
                        executeClipboardCapture()
                    }
                }, 800)
            }
            else -> {
                closeActivity()
            }
        }
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        val action = intent?.action
        if (hasFocus && !hasExecuted && (action == ACTION_CAPTURE_AND_SYNC || action == ACTION_SYNC_TO_MAC)) {
            Log.i(TAG, "Window focused; executing clipboard capture")
            executeClipboardCapture()
        }
    }

    private fun handleProcessText() {
        val syncId = "PROCTEXT-" + UUID.randomUUID().toString().take(6)
        val selectedText = intent?.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString()
        val secureStorage = SecureStorage(this)

        if (selectedText.isNullOrBlank()) {
            Log.w(TAG, "[$syncId] ACTION_PROCESS_TEXT received empty or null selection")
            Toast.makeText(this, "ClipSync: No text selected", Toast.LENGTH_SHORT).show()
        } else if (!secureStorage.hasCredentials()) {
            Log.w(TAG, "[$syncId] Device not paired; cannot sync text")
            Toast.makeText(this, "ClipSync: Device not paired", Toast.LENGTH_SHORT).show()
        } else {
            Log.i(TAG, "[$syncId] ACTION_PROCESS_TEXT dispatching selected text (len=${selectedText.length})")
            val syncIntent = Intent(this, ClipSyncForegroundService::class.java).apply {
                putExtra(ClipSyncForegroundService.EXTRA_OUTBOUND_TEXT, selectedText)
                putExtra(ClipSyncForegroundService.EXTRA_IS_MANUAL, true)
                putExtra(ClipSyncForegroundService.EXTRA_SYNC_ID, syncId)
            }
            ContextCompat.startForegroundService(this, syncIntent)
            val preview = if (selectedText.length > 25) selectedText.take(25) + "..." else selectedText
            Toast.makeText(this, "ClipSync: Synced selection to Mac (\"$preview\")", Toast.LENGTH_SHORT).show()
        }
    }

    private fun handleApplyClipboard() {
        val textToApply = intent?.getStringExtra(EXTRA_TEXT_TO_APPLY)
        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        if (!textToApply.isNullOrBlank()) {
            ClipboardStateManager.markAppliedLocally(textToApply)
            val clip = ClipData.newPlainText("ClipSync", textToApply)
            cm.setPrimaryClip(clip)
            Toast.makeText(this, "ClipSync: Copied to clipboard", Toast.LENGTH_SHORT).show()
        }
    }

    private fun executeClipboardCapture() {
        if (hasExecuted || isFinishing) return
        hasExecuted = true

        val syncId = "NOTIFSYNC-" + UUID.randomUUID().toString().take(6)
        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val secureStorage = SecureStorage(this)

        if (!secureStorage.hasCredentials()) {
            Log.w(TAG, "[$syncId] Device not paired; cannot sync clipboard")
            Toast.makeText(this, "ClipSync: Device not paired", Toast.LENGTH_SHORT).show()
        } else {
            try {
                val clip = cm.primaryClip
                val hasClip = clip != null
                val itemCount = clip?.itemCount ?: 0
                val text = if (hasClip && itemCount > 0) clip?.getItemAt(0)?.text?.toString() else null

                Log.i(TAG, "[$syncId] Capture and sync executed with focus: hasClip=$hasClip, itemCount=$itemCount, textLen=${text?.length ?: 0}")

                if (!text.isNullOrBlank()) {
                    val syncIntent = Intent(this, ClipSyncForegroundService::class.java).apply {
                        putExtra(ClipSyncForegroundService.EXTRA_OUTBOUND_TEXT, text)
                        putExtra(ClipSyncForegroundService.EXTRA_IS_MANUAL, true)
                        putExtra(ClipSyncForegroundService.EXTRA_SYNC_ID, syncId)
                    }
                    ContextCompat.startForegroundService(this, syncIntent)
                    val preview = if (text.length > 25) text.take(25) + "..." else text
                    Toast.makeText(this, "ClipSync: Synced to Mac (\"$preview\")", Toast.LENGTH_SHORT).show()
                } else {
                    Toast.makeText(this, "ClipSync: Clipboard is empty", Toast.LENGTH_SHORT).show()
                }
            } catch (e: Exception) {
                Log.e(TAG, "[$syncId] Clipboard capture error: ${e.message}")
                Toast.makeText(this, "ClipSync capture error: ${e.message}", Toast.LENGTH_SHORT).show()
            }
        }

        closeActivity()
    }

    private fun closeActivity() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            overrideActivityTransition(OVERRIDE_TRANSITION_CLOSE, 0, 0)
        } else {
            @Suppress("DEPRECATION")
            overridePendingTransition(0, 0)
        }
        finish()
    }

    companion object {
        const val TAG = "ClipSyncCaptureActivity"
        const val ACTION_CAPTURE_AND_SYNC = "com.clipsync.action.CAPTURE_AND_SYNC"
        const val ACTION_SYNC_TO_MAC = "com.clipsync.action.SYNC_TO_MAC"
        const val ACTION_APPLY_CLIPBOARD = "com.clipsync.action.APPLY_CLIPBOARD"
        const val EXTRA_TEXT_TO_APPLY = "extra_text_to_apply"
        const val EXTRA_SOURCE = "extra_source"
    }
}
