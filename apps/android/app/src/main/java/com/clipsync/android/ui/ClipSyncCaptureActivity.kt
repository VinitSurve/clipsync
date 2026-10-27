package com.clipsync.android.ui

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.widget.Toast
import com.clipsync.android.service.ClipSyncForegroundService

/**
 * Translucent, 0ms trampoline activity.
 * Needed on Android 10+ (API 29+) to access ClipboardManager with window focus
 * when triggered from Quick Settings Tile or Notification actions.
 */
class ClipSyncCaptureActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val action = intent?.action
        val cm = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager

        when (action) {
            ACTION_CAPTURE_AND_SYNC -> {
                try {
                    val clip = cm.primaryClip
                    if (clip != null && clip.itemCount > 0) {
                        val text = clip.getItemAt(0).text?.toString()
                        if (!text.isNullOrBlank()) {
                            // Forward text to service for encryption and sync
                            val syncIntent = Intent(this, ClipSyncForegroundService::class.java).apply {
                                putExtra(ClipSyncForegroundService.EXTRA_OUTBOUND_TEXT, text)
                            }
                            startService(syncIntent)
                            Toast.makeText(this, "ClipSync: Synced clipboard (${text.take(25)}...)", Toast.LENGTH_SHORT).show()
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

            ACTION_APPLY_CLIPBOARD -> {
                val textToApply = intent?.getStringExtra(EXTRA_TEXT_TO_APPLY)
                if (!textToApply.isNullOrBlank()) {
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
