package com.clipsync.android

import com.clipsync.android.crypto.ClipSyncCrypto
import com.clipsync.android.models.EncryptedPayloadEnvelope
import com.clipsync.android.models.SyncItem
import com.clipsync.android.models.SyncItemMetadata
import com.clipsync.android.service.ClipboardStateManager
import com.clipsync.android.service.ClipSyncForegroundService
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

    @Test
    fun testShouldApplyRemoteWhenRemoteIsNewer() {
        val remoteText = "Mac clipboard snippet"
        ClipboardStateManager.markReceived(remoteText, "MacBook Air", 5000L)

        // Local clip timestamp is older (e.g. 3000L)
        val shouldApply = ClipboardStateManager.shouldApplyRemote(
            localClipTimestamp = 3000L,
            localText = "Older local text"
        )
        assertTrue("Remote clip must be applied when it is newer than local clipboard", shouldApply)
    }

    @Test
    fun testShouldNotApplyRemoteWhenLocalClipIsNewer() {
        val remoteText = "Older remote snippet"
        ClipboardStateManager.markReceived(remoteText, "MacBook Air", 5000L)

        val newerLocalText = "Fresh user copy in Chrome"
        // Local clip timestamp is newer (e.g. 7000L)
        val shouldApply = ClipboardStateManager.shouldApplyRemote(
            localClipTimestamp = 7000L,
            localText = newerLocalText
        )
        assertFalse("Stale remote clip must NOT overwrite newer local clipboard", shouldApply)
        assertNull("Stale pending remote text should be discarded", ClipboardStateManager.pendingRemoteText)

        // And newer local text must be allowed to auto-sync to Mac
        val localHash = ClipSyncCrypto.sha256(newerLocalText)
        assertFalse("Newer local text must not be considered already synced", ClipboardStateManager.isAlreadySynced(localHash))
        assertFalse("Newer local text must not be considered echo", ClipboardStateManager.isEcho(localHash))
    }

    @Test
    fun testRedundantRemoteReapplySuppression() {
        val remoteText = "Exact text already on clipboard"
        ClipboardStateManager.markReceived(remoteText, "MacBook Air", 5000L)

        // Local clipboard already matches remote text exactly
        val shouldApply = ClipboardStateManager.shouldApplyRemote(
            localClipTimestamp = 5000L,
            localText = remoteText
        )
        assertFalse("Redundant re-application must be suppressed if clipboard already contains text", shouldApply)
        assertNull("Pending remote text should be cleared once matched", ClipboardStateManager.pendingRemoteText)

        val hash = ClipSyncCrypto.sha256(remoteText)
        assertTrue("Matched text must be marked as already synced", ClipboardStateManager.isAlreadySynced(hash))
    }

    @Test
    fun testUnsentOutboundNotPrematurelyMarkedSynced() {
        val text = "Text waiting to be sent"
        val hash = ClipSyncCrypto.sha256(text)

        // Before successful transmission:
        assertFalse("Unsent text must not be considered already synced before transmission succeeds", ClipboardStateManager.isAlreadySynced(hash))

        // When transmission succeeds:
        ClipboardStateManager.markSent(text)
        assertTrue("Text must be marked as synced only after transmission succeeds", ClipboardStateManager.isAlreadySynced(hash))
    }

    @Test
    fun testShouldNotApplyRemoteWhenTimestampZeroAndLocalIsFreshCopy() {
        val initialText = "Initial local copy"
        ClipboardStateManager.markSent(initialText)

        val remoteText = "Mac clipboard item"
        ClipboardStateManager.markReceived(remoteText, "MacBook Air", 5000L)

        // Local timestamp is 0 (unavailable), but user copied fresh text locally
        val freshLocalText = "Fresh copy in Android Chrome"
        val shouldApply = ClipboardStateManager.shouldApplyRemote(
            localClipTimestamp = 0L,
            localText = freshLocalText
        )

        assertFalse("Stale remote text must NOT overwrite fresh local copy even if timestamp is 0", shouldApply)
        assertNull("Stale pending remote text should be discarded", ClipboardStateManager.pendingRemoteText)
    }

    @Test
    fun testOptionAAlertAndCaptureConstants() {
        assertEquals("clipsync_copy_alerts_channel", ClipSyncForegroundService.CHANNEL_ALERT_ID)
        assertEquals(1002, ClipSyncForegroundService.NOTIFICATION_ALERT_ID)
        assertEquals("com.clipsync.action.CAPTURE_AND_SYNC", com.clipsync.android.ui.ClipSyncCaptureActivity.ACTION_CAPTURE_AND_SYNC)
        assertEquals("com.clipsync.action.SYNC_TO_MAC", com.clipsync.android.ui.ClipSyncCaptureActivity.ACTION_SYNC_TO_MAC)
        assertEquals("com.clipsync.action.APPLY_CLIPBOARD", com.clipsync.android.ui.ClipSyncCaptureActivity.ACTION_APPLY_CLIPBOARD)
        assertEquals("extra_source", com.clipsync.android.ui.ClipSyncCaptureActivity.EXTRA_SOURCE)
    }

    @Test
    fun testOptionDProcessTextPipelinePreservesClipboardIntegrity() {
        // Simulating text selected in Chrome and dispatched via ACTION_PROCESS_TEXT
        val selectedText = "Highlighted research article excerpt"
        val selectedHash = ClipSyncCrypto.sha256(selectedText)

        assertFalse("Unsynced selected text must not be considered already synced", ClipboardStateManager.isAlreadySynced(selectedHash))

        // When transmitted to broker:
        ClipboardStateManager.markSent(selectedText)
        assertTrue("Selected text must be marked as sent after transmission", ClipboardStateManager.isAlreadySynced(selectedHash))
        assertTrue("Selected text must be marked as echo to prevent loopback", ClipboardStateManager.isEcho(selectedHash))

        // Different clipboard copy afterwards is allowed:
        val laterCopy = "Different URL copied later"
        val laterHash = ClipSyncCrypto.sha256(laterCopy)
        assertFalse("Different local copy must not be blocked", ClipboardStateManager.isAlreadySynced(laterHash))
    }

    @Test
    fun testDragGestureThresholdDetection() {
        val touchSlop = 16

        // 1. Tiny jitter (dx=4, dy=3, hypot=5 <= 16): Must be treated as TAP, NOT DRAG
        val isDragSmall = com.clipsync.android.overlay.FloatingOverlayCalculator.isDragGesture(4f, 3f, touchSlop)
        assertFalse("Movements within touch slop must NOT be detected as drag", isDragSmall)

        // 2. Exact threshold (dx=0, dy=16, hypot=16 <= 16): Still TAP
        val isDragAtLimit = com.clipsync.android.overlay.FloatingOverlayCalculator.isDragGesture(0f, 16f, touchSlop)
        assertFalse("Movement equal to touch slop is not a drag", isDragAtLimit)

        // 3. Deliberate move (dx=15, dy=15, hypot=21.2 > 16): DRAG
        val isDragLarge = com.clipsync.android.overlay.FloatingOverlayCalculator.isDragGesture(15f, 15f, touchSlop)
        assertTrue("Movements exceeding touch slop must be detected as drag", isDragLarge)

        // 4. Horizontal swipe (dx=40, dy=0): DRAG
        assertTrue(com.clipsync.android.overlay.FloatingOverlayCalculator.isDragGesture(40f, 0f, touchSlop))

        // 5. Vertical swipe (dx=0, dy=40): DRAG
        assertTrue(com.clipsync.android.overlay.FloatingOverlayCalculator.isDragGesture(0f, 40f, touchSlop))
    }

    @Test
    fun testComputeSnapTargetX() {
        val screenWidth = 1080
        val buttonWidth = 60
        val margin = 8

        // 1. Ball on left side of screen (center = 100 + 30 = 130 < 540)
        val leftSnap = com.clipsync.android.overlay.FloatingOverlayCalculator.computeSnapTargetX(
            currentX = 100,
            buttonWidth = buttonWidth,
            screenWidth = screenWidth,
            margin = margin
        )
        assertEquals(margin, leftSnap)

        // 2. Ball on right side of screen (center = 800 + 30 = 830 >= 540)
        val rightSnap = com.clipsync.android.overlay.FloatingOverlayCalculator.computeSnapTargetX(
            currentX = 800,
            buttonWidth = buttonWidth,
            screenWidth = screenWidth,
            margin = margin
        )
        assertEquals(screenWidth - buttonWidth - margin, rightSnap) // 1080 - 60 - 8 = 1012

        // 3. Ball exactly at screen center (center = 510 + 30 = 540 >= 540) -> Snaps right
        val centerSnap = com.clipsync.android.overlay.FloatingOverlayCalculator.computeSnapTargetX(
            currentX = 510,
            buttonWidth = buttonWidth,
            screenWidth = screenWidth,
            margin = margin
        )
        assertEquals(1012, centerSnap)
    }

    @Test
    fun testClampCoordinates() {
        val buttonWidth = 60
        val buttonHeight = 60
        val screenWidth = 1080
        val screenHeight = 2400
        val topInset = 36
        val bottomInset = 48

        // Normal within-bounds point
        val (normX, normY) = com.clipsync.android.overlay.FloatingOverlayCalculator.clampCoordinates(
            x = 200,
            y = 500,
            buttonWidth = buttonWidth,
            buttonHeight = buttonHeight,
            screenWidth = screenWidth,
            screenHeight = screenHeight,
            topInset = topInset,
            bottomInset = bottomInset
        )
        assertEquals(200, normX)
        assertEquals(500, normY)

        // Negative coordinates out-of-bounds
        val (negX, negY) = com.clipsync.android.overlay.FloatingOverlayCalculator.clampCoordinates(
            x = -150,
            y = 10,
            buttonWidth = buttonWidth,
            buttonHeight = buttonHeight,
            screenWidth = screenWidth,
            screenHeight = screenHeight,
            topInset = topInset,
            bottomInset = bottomInset
        )
        assertEquals(0, negX)
        assertEquals(topInset, negY)

        // Exceeding screen bounds
        val (exceedX, exceedY) = com.clipsync.android.overlay.FloatingOverlayCalculator.clampCoordinates(
            x = 1500,
            y = 3000,
            buttonWidth = buttonWidth,
            buttonHeight = buttonHeight,
            screenWidth = screenWidth,
            screenHeight = screenHeight,
            topInset = topInset,
            bottomInset = bottomInset
        )
        assertEquals(screenWidth - buttonWidth, exceedX) // 1020
        assertEquals(screenHeight - buttonHeight - bottomInset, exceedY) // 2400 - 60 - 48 = 2292
    }

    @Test
    fun testFloatingOverlayActionConstants() {
        assertEquals("com.clipsync.action.SHOW_FLOATING_BUTTON", ClipSyncForegroundService.ACTION_SHOW_FLOATING_BUTTON)
        assertEquals("com.clipsync.action.HIDE_FLOATING_BUTTON", ClipSyncForegroundService.ACTION_HIDE_FLOATING_BUTTON)
        assertEquals("com.clipsync.action.TOGGLE_FLOATING_BUTTON", ClipSyncForegroundService.ACTION_TOGGLE_FLOATING_BUTTON)
    }
}
