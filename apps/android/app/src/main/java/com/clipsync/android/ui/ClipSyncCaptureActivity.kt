package com.clipsync.android.ui

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import com.clipsync.android.service.ClipSyncForegroundService
import com.clipsync.android.service.ClipboardStateManager

/**
 * Translucent, 0ms trampoline activity.
 * Needed on Android 10+ (API 29+) to access ClipboardManager with window focus
 * when triggered from Quick Settings Tile, Notification actions, or the
 * system text selection menu (ACTION_PROCESS_TEXT).
 */
class ClipSyncCaptureActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val action = intent?.action
        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

        when (action) {
            Intent.ACTION_PROCESS_TEXT -> {
                // Triggered when user selects text in any app and picks "ClipSync"
                val selectedText = intent?.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString()
                if (!selectedText.isNullOrBlank()) {
                    val syncIntent = Intent(this, ClipSyncForegroundService::class.java).apply {
                        putExtra(ClipSyncForegroundService.EXTRA_OUTBOUND_TEXT, selectedText)
                        putExtra(ClipSyncForegroundService.EXTRA_IS_MANUAL, true)
                    }
                    androidx.core.content.ContextCompat.startForegroundService(this, syncIntent)
                    val preview = if (selectedText.length > 25) selectedText.take(25) + "..." else selectedText
                    Toast.makeText(this, "ClipSync: Synced selection \"$preview\"", Toast.LENGTH_SHORT).show()
                }
            }

            ACTION_CAPTURE_AND_SYNC -> {
                // If there is pending remote text waiting to be applied, prioritize applying it
                val pending = ClipboardStateManager.pendingRemoteText
                val pendingSource = ClipboardStateManager.pendingRemoteSource ?: "desktop"
                if (!pending.isNullOrBlank()) {
                    ClipboardStateManager.markAppliedLocally(pending)
                    val clip = ClipData.newPlainText("ClipSync", pending)
                    cm.setPrimaryClip(clip)
                    val preview = if (pending.length > 25) pending.take(25) + "..." else pending
                    Toast.makeText(this, "ClipSync: Applied from $pendingSource \"$preview\"", Toast.LENGTH_SHORT).show()
                } else {
                    // Otherwise capture current clipboard and sync to desktop
                    try {
                        val clip = cm.primaryClip
                        if (clip != null && clip.itemCount > 0) {
                            val text = clip.getItemAt(0).text?.toString()
                            if (!text.isNullOrBlank()) {
                                val syncIntent = Intent(this, ClipSyncForegroundService::class.java).apply {
                                    putExtra(ClipSyncForegroundService.EXTRA_OUTBOUND_TEXT, text)
                                    putExtra(ClipSyncForegroundService.EXTRA_IS_MANUAL, true)
                                }
                                androidx.core.content.ContextCompat.startForegroundService(this, syncIntent)
                                val preview = if (text.length > 25) text.take(25) + "..." else text
                                Toast.makeText(this, "ClipSync: Synced \"$preview\"", Toast.LENGTH_SHORT).show()
                            } else {
                                Toast.makeText(this, "ClipSync: Clipboard is empty", Toast.LENGTH_SHORT).show()
                            }
                        } else {
                            Toast.makeText(this, "ClipSync: Clipboard is empty", Toast.LENGTH_SHORT).show()
                        }
                    } catch (e: Exception) {
                        Toast.makeText(this, "ClipSync capture error: ${e.message}", Toast.LENGTH_SHORT).show()
                    }
                }
            }

            ACTION_APPLY_CLIPBOARD -> {
                val textToApply = intent?.getStringExtra(EXTRA_TEXT_TO_APPLY)
                if (!textToApply.isNullOrBlank()) {
                    ClipboardStateManager.markAppliedLocally(textToApply)
                    val clip = ClipData.newPlainText("ClipSync", textToApply)
                    cm.setPrimaryClip(clip)
                    Toast.makeText(this, "ClipSync: Copied to clipboard", Toast.LENGTH_SHORT).show()
                }
            }
        }

        // Close immediately
        finish()
    }

    companion object {
        const val ACTION_CAPTURE_AND_SYNC = "com.clipsync.action.CAPTURE_AND_SYNC"
        const val ACTION_APPLY_CLIPBOARD = "com.clipsync.action.APPLY_CLIPBOARD"
        const val EXTRA_TEXT_TO_APPLY = "extra_text_to_apply"
    }
}
