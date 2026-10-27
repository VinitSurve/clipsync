package com.clipsync.android

import com.clipsync.android.crypto.ClipSyncCrypto
import com.clipsync.android.models.EncryptedPayloadEnvelope
import com.clipsync.android.models.SyncItem
import com.clipsync.android.models.SyncItemMetadata
import com.clipsync.android.service.ClipboardStateManager
import com.google.gson.Gson
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import java.security.GeneralSecurityException
import javax.crypto.AEADBadTagException

class ClipSyncUnitTest {

    private val gson = Gson()
    private val testPassphrase = "clipsync-test-passphrase"
    private val testSalt = "a1b2c3d4e5f67890"

    @Before
    fun setUp() {
        ClipboardStateManager.reset()
    }

    @Test
    fun testSha256Checksum() {
        val input = "Hello, ClipSync Universal Clipboard!"
        val hash = ClipSyncCrypto.sha256(input)
        assertNotNull(hash)
        assertEquals(64, hash.length) // 256 bits = 64 hex chars
        // Deterministic
        assertEquals(hash, ClipSyncCrypto.sha256(input))
        // Case-sensitive distinct
        assertNotEquals(hash, ClipSyncCrypto.sha256("hello, clipsync universal clipboard!"))
    }

    @Test
    fun testAesGcmEncryptDecryptRoundtrip() {
        val crypto = ClipSyncCrypto(testPassphrase, testSalt)
        val originalText = "Cross-platform clipboard sync: Mac ↔ Windows ↔ Android"

        val envelope = crypto.encrypt(originalText)
        assertNotNull(envelope)
        assertEquals(1, envelope.version)
        assertTrue(envelope.iv.isNotEmpty())
        assertTrue(envelope.ciphertext.isNotEmpty())
        assertTrue(envelope.tag.isNotEmpty())

        val decrypted = crypto.decrypt(envelope)
        assertEquals(originalText, decrypted)
    }

    @Test
    fun testAesGcmTamperDetection() {
        val crypto = ClipSyncCrypto(testPassphrase, testSalt)
        val originalText = "Secret authentication data"

        val envelope = crypto.encrypt(originalText)

        // Tamper with tag
        val tamperedTag = if (envelope.tag.startsWith("A")) "B" + envelope.tag.substring(1) else "A" + envelope.tag.substring(1)
        val tamperedEnvelope = envelope.copy(tag = tamperedTag)

        try {
            crypto.decrypt(tamperedEnvelope)
            fail("Decryption should have thrown AEADBadTagException on tampered tag")
        } catch (e: GeneralSecurityException) {
            // Expected
            assertTrue(e is AEADBadTagException || e.cause is AEADBadTagException)
        }

        // Tamper with ciphertext
        val tamperedCiphertext = if (envelope.ciphertext.startsWith("X")) "Y" + envelope.ciphertext.substring(1) else "X" + envelope.ciphertext.substring(1)
        val tamperedCiphertextEnvelope = envelope.copy(ciphertext = tamperedCiphertext)

        try {
            crypto.decrypt(tamperedCiphertextEnvelope)
            fail("Decryption should have thrown AEADBadTagException on tampered ciphertext")
        } catch (e: GeneralSecurityException) {
            // Expected
        }
    }

    @Test
    fun testWrongPassphraseRejection() {
        val crypto1 = ClipSyncCrypto("correct-passphrase", testSalt)
        val crypto2 = ClipSyncCrypto("wrong-passphrase", testSalt)

        val envelope = crypto1.encrypt("Confidential data")
        try {
            crypto2.decrypt(envelope)
            fail("Decryption should have failed when using incorrect passphrase")
        } catch (e: GeneralSecurityException) {
            // Expected authentication tag verification failure
        }
    }

    @Test
    fun testProtocolEnvelopeSerialization() {
        val crypto = ClipSyncCrypto(testPassphrase, testSalt)
        val text = "Synchronized text snippet #42"
        val envelope = crypto.encrypt(text)
        val envelopeJson = gson.toJson(envelope)

        val metadata = SyncItemMetadata(
            size = text.toByteArray().size,
            mimeType = "text/plain",
            sha256 = ClipSyncCrypto.sha256(text),
            previewSnippet = text.take(100),
            sourceApp = "Redmi 10 Prime"
        )

        val syncItem = SyncItem(
            version = 1,
            itemId = "item-android-12345",
            type = "text",
            sourceDeviceId = "device-android-xyz",
            createdAt = "2026-10-27T10:00:00.000Z",
            payload = envelopeJson,
            metadata = metadata
        )

        // Serialize to JSON
        val json = gson.toJson(syncItem)
        assertTrue(json.contains("item-android-12345"))
        assertTrue(json.contains("Redmi 10 Prime"))
        assertTrue(json.contains("device-android-xyz"))

        // Deserialize from JSON
        val deserialized = gson.fromJson(json, SyncItem::class.java)
        assertEquals(syncItem.itemId, deserialized.itemId)
        assertEquals(syncItem.sourceDeviceId, deserialized.sourceDeviceId)
        assertEquals(syncItem.type, deserialized.type)
        assertEquals(syncItem.metadata?.sha256, deserialized.metadata?.sha256)

        // Decrypt payload from deserialized item
        val innerEnvelope = gson.fromJson(deserialized.payload, EncryptedPayloadEnvelope::class.java)
        val decryptedText = crypto.decrypt(innerEnvelope)
        assertEquals(text, decryptedText)
    }

    @Test
    fun testDuplicateAndLoopSuppression() {
        val seenItemIds = LinkedHashSet<String>()
        var lastSentHash = ""
        var lastReceivedHash = ""

        val testText = "Quick brown fox jumps over the lazy dog"
        val testHash = ClipSyncCrypto.sha256(testText)

        // 1. Simulating local copy -> sent
        lastSentHash = testHash
        val localItemId = "local-uuid-1"
        seenItemIds.add(localItemId)

        // 2. Incoming echo of same item (same itemId)
        val shouldProcess = !seenItemIds.contains(localItemId)
        assertFalse("Duplicate itemId should be suppressed", shouldProcess)

        // 3. Incoming echo with different itemId but same hash as lastSentHash
        val incomingEchoHash = testHash
        val isEcho = (incomingEchoHash == lastSentHash)
        assertTrue("Incoming item matching lastSentHash must be suppressed as echo loop", isEcho)

        // 4. Remote new item
        val remoteText = "New remote clipboard from MacBook Air"
        val remoteHash = ClipSyncCrypto.sha256(remoteText)
        val remoteItemId = "remote-uuid-99"

        val isRemoteDuplicate = seenItemIds.contains(remoteItemId)
        val isRemoteEcho = (remoteHash == lastSentHash)
        assertFalse(isRemoteDuplicate)
        assertFalse(isRemoteEcho)

        // Process remote item
        seenItemIds.add(remoteItemId)
        lastReceivedHash = remoteHash

        // 5. Subsequent local clipboard change event triggered by system applying the remote text
        val triggerHash = remoteHash
        val isSelfLoopFromReceive = (triggerHash == lastReceivedHash)
        assertTrue("Local trigger matching lastReceivedHash must be ignored to prevent feedback loops", isSelfLoopFromReceive)
    }

    @Test
    fun testClipboardStateManagerEchoDetection() {
        val localText = "Local copied text"
        val localHash = ClipboardStateManager.markSent(localText)

        assertTrue("Hash of text sent must be identified as echo", ClipboardStateManager.isEcho(localHash))
        assertTrue("Hash of text sent must be identified as already synced", ClipboardStateManager.isAlreadySynced(localHash))

        val remoteText = "Text received from MacBook Air"
        val remoteHash = ClipboardStateManager.markReceived(remoteText, "MacBook Air", System.currentTimeMillis())

        assertTrue("Hash of text received must be identified as echo", ClipboardStateManager.isEcho(remoteHash))

        val brandNewText = "Completely fresh text"
        val brandNewHash = ClipSyncCrypto.sha256(brandNewText)

        assertFalse("Unsynced text must not be flagged as echo", ClipboardStateManager.isEcho(brandNewHash))
        assertFalse("Unsynced text must not be flagged as already synced", ClipboardStateManager.isAlreadySynced(brandNewHash))
    }

    @Test
    fun testClipboardStateManagerPendingLifecycle() {
        val remoteText = "Code snippet: https://github.com/VinitSurve/clipsync"
        ClipboardStateManager.markReceived(remoteText, "MacBook Air", 5000L)

        assertEquals(remoteText, ClipboardStateManager.pendingRemoteText)
        assertEquals("MacBook Air", ClipboardStateManager.pendingRemoteSource)
        assertEquals(5000L, ClipboardStateManager.pendingRemoteTimestamp)

        // Apply locally
        ClipboardStateManager.markAppliedLocally(remoteText)

        assertNull("Pending text must be cleared after application", ClipboardStateManager.pendingRemoteText)
        assertNull("Pending source must be cleared after application", ClipboardStateManager.pendingRemoteSource)

        val hash = ClipSyncCrypto.sha256(remoteText)
        assertEquals(hash, ClipboardStateManager.lastReceivedHash)
        assertEquals(hash, ClipboardStateManager.lastLocalHash)
        assertTrue(ClipboardStateManager.isAlreadySynced(hash))
    }

    @Test
    fun testStaleOrIdenticalClipboardSuppression() {
        val text = "Repeating same text"
        ClipboardStateManager.markSent(text)
        val hash = ClipSyncCrypto.sha256(text)

        assertTrue(ClipboardStateManager.isAlreadySynced(hash))

        // Simulating second copy of identical text
        val shouldSyncAgain = !ClipboardStateManager.isAlreadySynced(hash)
        assertFalse("Identical text copied repeatedly must be suppressed", shouldSyncAgain)
    }

    @Test
    fun testReconnectExponentialBackoff() {
        // Capped between 2s and 30s
        assertEquals(2L, ClipboardStateManager.calculateBackoffSeconds(0))
        assertEquals(2L, ClipboardStateManager.calculateBackoffSeconds(1))
        assertEquals(4L, ClipboardStateManager.calculateBackoffSeconds(2))
        assertEquals(8L, ClipboardStateManager.calculateBackoffSeconds(3))
        assertEquals(16L, ClipboardStateManager.calculateBackoffSeconds(4))
        assertEquals(30L, ClipboardStateManager.calculateBackoffSeconds(5))
        assertEquals(30L, ClipboardStateManager.calculateBackoffSeconds(6))
        assertEquals(30L, ClipboardStateManager.calculateBackoffSeconds(100))
    }

    @Test
    fun testMultiHopLoopSuppression() {
        // Step 1: Remote Mac sends item
        val macText = "Architecture Document Revision 3"
        val macHash = ClipboardStateManager.markReceived(macText, "MacBook Air", System.currentTimeMillis())

        // Step 2: Android applies to local clipboard
        ClipboardStateManager.markAppliedLocally(macText)

        // Step 3: Android OS triggers onPrimaryClipChanged with macText
        val osTriggeredHash = ClipSyncCrypto.sha256(macText)
        val wouldLoop = !ClipboardStateManager.isEcho(osTriggeredHash) && !ClipboardStateManager.isAlreadySynced(osTriggeredHash)
        assertFalse("OS clip trigger matching remote received text must be suppressed", wouldLoop)

        // Step 4: User subsequently copies different text on Android
        val userCopiedText = "User edited note on Redmi"
        val userHash = ClipSyncCrypto.sha256(userCopiedText)
        val wouldSync = !ClipboardStateManager.isEcho(userHash) && !ClipboardStateManager.isAlreadySynced(userHash)
        assertTrue("User copy of different text must be allowed to sync", wouldSync)
    }
}
