package com.clipsync.android.crypto

import android.util.Base64
import com.clipsync.android.models.EncryptedPayloadEnvelope
import java.nio.charset.StandardCharsets
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.Cipher
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.PBEKeySpec
import javax.crypto.spec.SecretKeySpec

class ClipSyncCrypto(passphrase: String, salt: String) {

    private val secretKey: SecretKeySpec

    init {
        val factory = SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256")
        val spec = PBEKeySpec(passphrase.toCharArray(), salt.toByteArray(StandardCharsets.UTF_8), 100000, 256)
        val tmp = factory.generateSecret(spec)
        secretKey = SecretKeySpec(tmp.encoded, "AES")
    }

    fun encrypt(plainText: String): EncryptedPayloadEnvelope {
        val iv = ByteArray(12)
        SecureRandom().nextBytes(iv)

        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        val spec = GCMParameterSpec(128, iv)
        cipher.init(Cipher.ENCRYPT_MODE, secretKey, spec)

        val plainBytes = plainText.toByteArray(StandardCharsets.UTF_8)
        val encryptedWithTag = cipher.doFinal(plainBytes)

        // Split ciphertext and 16-byte tag
        val ciphertextLength = encryptedWithTag.size - 16
        val ciphertext = ByteArray(ciphertextLength)
        val tag = ByteArray(16)

        System.arraycopy(encryptedWithTag, 0, ciphertext, 0, ciphertextLength)
        System.arraycopy(encryptedWithTag, ciphertextLength, tag, 0, 16)

        return EncryptedPayloadEnvelope(
            version = 1,
            iv = toBase64Url(iv),
            ciphertext = toBase64Url(ciphertext),
            tag = toBase64Url(tag)
        )
    }

    fun decrypt(envelope: EncryptedPayloadEnvelope): String {
        val iv = fromBase64Url(envelope.iv)
        val ciphertext = fromBase64Url(envelope.ciphertext)
        val tag = fromBase64Url(envelope.tag)

        // Recombine ciphertext and tag for Java GCM
        val combined = ByteArray(ciphertext.size + tag.size)
        System.arraycopy(ciphertext, 0, combined, 0, ciphertext.size)
        System.arraycopy(tag, 0, combined, ciphertext.size, tag.size)

        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        val spec = GCMParameterSpec(128, iv)
        cipher.init(Cipher.DECRYPT_MODE, secretKey, spec)

        val decrypted = cipher.doFinal(combined)
        return String(decrypted, StandardCharsets.UTF_8)
    }

    companion object {
        fun sha256(input: String): String {
            val md = MessageDigest.getInstance("SHA-256")
            val digest = md.digest(input.toByteArray(StandardCharsets.UTF_8))
            return digest.joinToString("") { "%02x".format(it) }
        }

        fun toBase64Url(bytes: ByteArray): String {
            return Base64.encodeToString(bytes, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
        }

        fun fromBase64Url(input: String): ByteArray {
            return Base64.decode(input, Base64.URL_SAFE or Base64.NO_PADDING or Base64.NO_WRAP)
        }
    }
}
