package com.clipsync.android.tile

import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import androidx.annotation.RequiresApi
import com.clipsync.android.service.ClipSyncForegroundService

@RequiresApi(Build.VERSION_CODES.N)
class ClipSyncTileService : TileService() {

    override fun onStartListening() {
        super.onStartListening()
        val tile = qsTile ?: return
        tile.state = Tile.STATE_ACTIVE
        tile.label = "Sync Clipboard"
        tile.updateTile()
    }

    override fun onClick() {
        super.onClick()
        // Trigger immediate clipboard capture and sync
        val serviceIntent = Intent(this, ClipSyncForegroundService::class.java).apply {
            putExtra("action_sync_now", true)
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            startForegroundService(serviceIntent)
        } else {
            startService(serviceIntent)
        }
    }
}
