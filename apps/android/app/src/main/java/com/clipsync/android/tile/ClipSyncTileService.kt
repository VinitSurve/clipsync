package com.clipsync.android.tile

import android.app.PendingIntent
import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import androidx.annotation.RequiresApi
import com.clipsync.android.service.ClipboardStateManager
import com.clipsync.android.ui.ClipSyncCaptureActivity

@RequiresApi(Build.VERSION_CODES.N)
class ClipSyncTileService : TileService() {

    override fun onStartListening() {
        super.onStartListening()
        val tile = qsTile ?: return
        tile.state = Tile.STATE_ACTIVE

        val pending = ClipboardStateManager.pendingRemoteText
        if (!pending.isNullOrBlank()) {
            val src = ClipboardStateManager.pendingRemoteSource ?: "Desktop"
            tile.label = "Apply $src Clip"
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                tile.subtitle = "${pending.take(15)}..."
            }
        } else {
            tile.label = "Sync Clipboard"
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                tile.subtitle = "Capture & Sync"
            }
        }
        tile.updateTile()
    }

    override fun onClick() {
        super.onClick()
        val intent = Intent(this, ClipSyncCaptureActivity::class.java).apply {
            action = ClipSyncCaptureActivity.ACTION_CAPTURE_AND_SYNC
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            val pendingIntent = PendingIntent.getActivity(
                this, 0, intent, PendingIntent.FLAG_IMMUTABLE
            )
            startActivityAndCollapse(pendingIntent)
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }
}
