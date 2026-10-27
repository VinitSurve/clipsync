package com.clipsync.android.storage

import android.content.Context
import android.content.SharedPreferences
import android.os.Build
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

class SecureStorage(context: Context) {

    private val prefs: SharedPreferences = try {
        val masterKey = MasterKey.Builder(context)
            .setKeyScheme(MasterKey.KeyScheme.AES256_GCM)
            .build()

        EncryptedSharedPreferences.create(
            context,
            PREFS_FILE_NAME,
            masterKey,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM
        )
    } catch (e: Exception) {
        // Fallback to standard private preferences if Keystore initialization fails in testing
        context.getSharedPreferences("clipsync_fallback_prefs", Context.MODE_PRIVATE)
    }

    var serverUrl: String
        get() = prefs.getString(KEY_SERVER_URL, DEFAULT_SERVER_URL) ?: DEFAULT_SERVER_URL
        set(value) = prefs.edit().putString(KEY_SERVER_URL, value.trim()).apply()

    var deviceId: String
        get() = prefs.getString(KEY_DEVICE_ID, "") ?: ""
        set(value) = prefs.edit().putString(KEY_DEVICE_ID, value.trim()).apply()

    var token: String
        get() = prefs.getString(KEY_TOKEN, "") ?: ""
        set(value) = prefs.edit().putString(KEY_TOKEN, value.trim()).apply()

    var syncSalt: String
        get() = prefs.getString(KEY_SYNC_SALT, "") ?: ""
        set(value) = prefs.edit().putString(KEY_SYNC_SALT, value.trim()).apply()

    var syncPassphrase: String
        get() = prefs.getString(KEY_PASSPHRASE, DEFAULT_PASSPHRASE) ?: DEFAULT_PASSPHRASE
        set(value) = prefs.edit().putString(KEY_PASSPHRASE, value).apply()

    var deviceLabel: String
        get() = prefs.getString(KEY_DEVICE_LABEL, defaultDeviceLabel()) ?: defaultDeviceLabel()
        set(value) = prefs.edit().putString(KEY_DEVICE_LABEL, value.trim()).apply()

    var isPaused: Boolean
        get() = prefs.getBoolean(KEY_IS_PAUSED, false)
        set(value) = prefs.edit().putBoolean(KEY_IS_PAUSED, value).apply()

    fun hasCredentials(): Boolean {
        return deviceId.isNotEmpty() && token.isNotEmpty() && syncSalt.isNotEmpty()
    }

    fun clearCredentials() {
        prefs.edit()
            .remove(KEY_DEVICE_ID)
            .remove(KEY_TOKEN)
            .remove(KEY_SYNC_SALT)
            .apply()
    }

    private fun defaultDeviceLabel(): String {
        val model = Build.MODEL
        return if (!model.isNullOrBlank()) model else "Android Phone"
    }

    companion object {
        private const val PREFS_FILE_NAME = "clipsync_secure_keystore_prefs"
        const val DEFAULT_SERVER_URL = "https://clipsync-server-cloudflare.survevinit56.workers.dev"
        const val DEFAULT_PASSPHRASE = "clipsync-master-sync-passphrase"

        private const val KEY_SERVER_URL = "clipsync_server_url"
        private const val KEY_DEVICE_ID = "clipsync_device_id"
        private const val KEY_TOKEN = "clipsync_token"
        private const val KEY_SYNC_SALT = "clipsync_sync_salt"
        private const val KEY_PASSPHRASE = "clipsync_passphrase"
        private const val KEY_DEVICE_LABEL = "clipsync_device_label"
        private const val KEY_IS_PAUSED = "clipsync_is_paused"
    }
}
