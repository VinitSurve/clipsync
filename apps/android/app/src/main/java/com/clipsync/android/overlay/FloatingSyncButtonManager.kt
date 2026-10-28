package com.clipsync.android.overlay

import android.animation.Animator
import android.animation.AnimatorListenerAdapter
import android.animation.ValueAnimator
import android.content.Context
import android.content.Intent
import android.content.res.Configuration
import android.graphics.Color
import android.graphics.PixelFormat
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.Settings
import android.util.Log
import android.view.Gravity
import android.view.LayoutInflater
import android.view.MotionEvent
import android.view.View
import android.view.ViewConfiguration
import android.view.WindowManager
import android.view.animation.LinearInterpolator
import android.view.animation.OvershootInterpolator
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.graphics.drawable.DrawableCompat
import com.clipsync.android.R
import com.clipsync.android.storage.SecureStorage
import com.clipsync.android.ui.ClipSyncCaptureActivity
import kotlin.math.hypot

/**
 * Manages the Sync Core floating button overlay.
 *
 * Requirements & Architecture:
 * 1. Compact 56dp circular orb with dark charcoal surface, subtle teal rim, and mint sync symbol.
 * 2. Uses WindowManager with TYPE_APPLICATION_OVERLAY and FLAG_NOT_FOCUSABLE.
 * 3. Draggable and snaps to the nearest screen edge.
 * 4. Distinguishes drag from tap using ViewConfiguration touch slop.
 * 5. Distinct visual states:
 *    - IDLE: Static mint sync arrows, no battery-wasting animation.
 *    - PRESSED: Subtle scale-down feedback.
 *    - SYNCING: Smooth rotation while transmitting clipboard to Mac.
 *    - SUCCESS: Glowing green rim with checkmark and "Synced to Mac" badge.
 *    - ERROR: Glowing red rim with exclamation mark and concise error badge.
 * 6. Tap launches ClipSyncCaptureActivity (ACTION_SYNC_TO_MAC) to gain window focus
 *    and read primaryClip without opening MainActivity.
 * 7. Handles configuration changes, screen bounds, edge insets, and safe lifecycle attachment.
 */
class FloatingSyncButtonManager(
    private val context: Context,
    private val secureStorage: SecureStorage
) {

    enum class SyncState {
        IDLE,
        SYNCING,
        SUCCESS,
        ERROR
    }

    private val windowManager = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
    private val mainHandler = Handler(Looper.getMainLooper())

    private var overlayView: View? = null
    private var containerFloating: LinearLayout? = null
    private var orbSyncCore: FrameLayout? = null
    private var imgSyncCoreIcon: ImageView? = null
    private var txtFeedbackBadge: TextView? = null

    private var layoutParams: WindowManager.LayoutParams? = null
    var isAttached: Boolean = false
        private set

    var currentState: SyncState = SyncState.IDLE
        private set

    // Saved position of the orb itself (x, y)
    private var currentOrbX: Int = 0
    private var currentOrbY: Int = 0
    private var isSnappedToRight: Boolean = true

    private var lastSyncTime: Long = 0L

    private var snapAnimator: ValueAnimator? = null
    private var syncRotateAnimator: ValueAnimator? = null
    private var badgeFadeRunnable: Runnable? = null
    private var stateResetRunnable: Runnable? = null

    init {
        activeInstance = this
    }

    /**
     * Shows the Sync Core floating button overlay if permission is granted and not already attached.
     */
    fun show() {
        if (isAttached) {
            Log.d(TAG, "Sync Core already attached")
            return
        }

        if (!Settings.canDrawOverlays(context)) {
            Log.w(TAG, "Cannot show Sync Core: SYSTEM_ALERT_WINDOW permission not granted")
            return
        }

        mainHandler.post {
            try {
                createAndAttachView()
            } catch (e: Exception) {
                Log.e(TAG, "Error attaching Sync Core overlay: ${e.message}", e)
                isAttached = false
            }
        }
    }

    /**
     * Hides and removes the Sync Core overlay from the WindowManager.
     */
    fun hide() {
        mainHandler.post {
            cancelAnimators()
            if (isAttached && overlayView != null) {
                try {
                    windowManager.removeView(overlayView)
                    Log.i(TAG, "Sync Core overlay removed successfully")
                } catch (e: Exception) {
                    Log.w(TAG, "Error removing overlay view: ${e.message}")
                }
            }
            overlayView = null
            containerFloating = null
            orbSyncCore = null
            imgSyncCoreIcon = null
            txtFeedbackBadge = null
            layoutParams = null
            isAttached = false
            currentState = SyncState.IDLE
        }
    }

    /**
     * Clean up resources, animators, and static references.
     */
    fun destroy() {
        if (activeInstance === this) {
            activeInstance = null
        }
        hide()
    }

    /**
     * Handles device rotation and display-size changes.
     */
    fun onConfigurationChanged(@Suppress("UNUSED_PARAMETER") newConfig: Configuration) {
        if (!isAttached || overlayView == null || layoutParams == null) return

        mainHandler.post {
            val params = layoutParams ?: return@post
            val metrics = context.resources.displayMetrics
            val screenWidth = metrics.widthPixels
            val screenHeight = metrics.heightPixels

            val buttonSize = dpToPx(56)
            val clamped = FloatingOverlayCalculator.clampCoordinates(
                x = currentOrbX,
                y = currentOrbY,
                buttonWidth = buttonSize,
                buttonHeight = buttonSize,
                screenWidth = screenWidth,
                screenHeight = screenHeight,
                topInset = dpToPx(36),
                bottomInset = dpToPx(48)
            )

            val snappedX = FloatingOverlayCalculator.computeSnapTargetX(
                currentX = clamped.first,
                buttonWidth = buttonSize,
                screenWidth = screenWidth,
                margin = dpToPx(8)
            )

            currentOrbX = snappedX
            currentOrbY = clamped.second
            isSnappedToRight = (currentOrbX + buttonSize / 2 >= screenWidth / 2)

            updateContainerChildOrder()

            params.x = currentOrbX
            params.y = currentOrbY

            try {
                windowManager.updateViewLayout(overlayView, params)
                secureStorage.floatingButtonX = currentOrbX
                secureStorage.floatingButtonY = currentOrbY
            } catch (e: Exception) {
                Log.w(TAG, "Could not update overlay layout on config change: ${e.message}")
            }
        }
    }

    private fun createAndAttachView() {
        if (isAttached) return

        val inflater = LayoutInflater.from(context)
        val view = inflater.inflate(R.layout.layout_floating_sync_core, null)
        overlayView = view

        containerFloating = view.findViewById(R.id.containerFloating)
        orbSyncCore = view.findViewById(R.id.orbSyncCore)
        imgSyncCoreIcon = view.findViewById(R.id.imgSyncCoreIcon)
        txtFeedbackBadge = view.findViewById(R.id.txtFeedbackBadge)

        val metrics = context.resources.displayMetrics
        val screenWidth = metrics.widthPixels
        val screenHeight = metrics.heightPixels
        val buttonSize = dpToPx(56)

        // Load saved coordinates or initialize near right edge
        val savedX = secureStorage.floatingButtonX
        val savedY = secureStorage.floatingButtonY

        val initialX = if (savedX >= 0) {
            savedX.coerceIn(0, (screenWidth - buttonSize).coerceAtLeast(0))
        } else {
            (screenWidth - buttonSize - dpToPx(8)).coerceAtLeast(0)
        }

        val initialY = if (savedY >= 0) {
            savedY.coerceIn(dpToPx(36), (screenHeight - buttonSize - dpToPx(48)).coerceAtLeast(dpToPx(36)))
        } else {
            screenHeight / 3
        }

        currentOrbX = initialX
        currentOrbY = initialY
        isSnappedToRight = (currentOrbX + buttonSize / 2 >= screenWidth / 2)

        updateContainerChildOrder()

        val windowType = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY
        } else {
            @Suppress("DEPRECATION")
            WindowManager.LayoutParams.TYPE_PHONE
        }

        val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            windowType,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT
        ).apply {
            gravity = Gravity.TOP or Gravity.START
            x = currentOrbX
            y = currentOrbY
        }
        layoutParams = params

        setupTouchListener(orbSyncCore!!, params)

        windowManager.addView(view, params)
        isAttached = true
        applyState(SyncState.IDLE)
        Log.i(TAG, "Sync Core attached at x=$currentOrbX, y=$currentOrbY (isRight=$isSnappedToRight)")
    }

    private fun updateContainerChildOrder() {
        val container = containerFloating ?: return
        val orb = orbSyncCore ?: return
        val badge = txtFeedbackBadge ?: return

        container.removeAllViews()
        if (isSnappedToRight) {
            // Orb on right edge: [Badge] [Orb]
            container.addView(badge)
            container.addView(orb)
        } else {
            // Orb on left edge: [Orb] [Badge]
            container.addView(orb)
            container.addView(badge)
        }
    }

    private fun setupTouchListener(orbView: View, params: WindowManager.LayoutParams) {
        val touchSlop = ViewConfiguration.get(context).scaledTouchSlop
        var initialX = 0
        var initialY = 0
        var initialTouchX = 0f
        var initialTouchY = 0f
        var isDragging = false

        orbView.setOnTouchListener { _, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> {
                    cancelAnimators()
                    txtFeedbackBadge?.visibility = View.GONE

                    initialX = currentOrbX
                    initialY = currentOrbY
                    initialTouchX = event.rawX
                    initialTouchY = event.rawY
                    isDragging = false

                    if (currentState != SyncState.SYNCING && areAnimationsEnabled()) {
                        orbView.animate()
                            .scaleX(0.90f)
                            .scaleY(0.90f)
                            .setDuration(75)
                            .start()
                    }
                    true
                }

                MotionEvent.ACTION_MOVE -> {
                    val dx = event.rawX - initialTouchX
                    val dy = event.rawY - initialTouchY

                    if (!isDragging && FloatingOverlayCalculator.isDragGesture(dx, dy, touchSlop)) {
                        isDragging = true
                        orbView.animate().scaleX(1.0f).scaleY(1.0f).setDuration(75).start()
                    }

                    if (isDragging) {
                        val metrics = context.resources.displayMetrics
                        val buttonSize = dpToPx(56)
                        val clamped = FloatingOverlayCalculator.clampCoordinates(
                            x = initialX + dx.toInt(),
                            y = initialY + dy.toInt(),
                            buttonWidth = buttonSize,
                            buttonHeight = buttonSize,
                            screenWidth = metrics.widthPixels,
                            screenHeight = metrics.heightPixels,
                            topInset = dpToPx(36),
                            bottomInset = dpToPx(48)
                        )

                        currentOrbX = clamped.first
                        currentOrbY = clamped.second
                        params.x = currentOrbX
                        params.y = currentOrbY

                        try {
                            windowManager.updateViewLayout(overlayView, params)
                        } catch (_: Exception) {}
                    }
                    true
                }

                MotionEvent.ACTION_UP -> {
                    if (areAnimationsEnabled()) {
                        orbView.animate()
                            .scaleX(1.0f)
                            .scaleY(1.0f)
                            .setInterpolator(OvershootInterpolator(1.4f))
                            .setDuration(160)
                            .start()
                    } else {
                        orbView.scaleX = 1.0f
                        orbView.scaleY = 1.0f
                    }

                    if (!isDragging) {
                        // Deliberate TAP: Initiate sync
                        handleOrbTap()
                    } else {
                        // Drag completed: Snap to screen edge smoothly
                        val metrics = context.resources.displayMetrics
                        val screenWidth = metrics.widthPixels
                        val buttonSize = dpToPx(56)

                        val targetX = FloatingOverlayCalculator.computeSnapTargetX(
                            currentX = currentOrbX,
                            buttonWidth = buttonSize,
                            screenWidth = screenWidth,
                            margin = dpToPx(8)
                        )
                        animateSnap(currentOrbX, targetX, params)
                    }
                    isDragging = false
                    true
                }

                MotionEvent.ACTION_CANCEL -> {
                    orbView.animate().scaleX(1.0f).scaleY(1.0f).setDuration(100).start()
                    isDragging = false
                    true
                }

                else -> false
            }
        }
    }

    private fun handleOrbTap() {
        val now = SystemClock.elapsedRealtime()
        if (currentState == SyncState.SYNCING || now - lastSyncTime < 600L) {
            Log.d(TAG, "Ignoring tap: sync already in progress or debounced")
            return
        }
        lastSyncTime = now

        Log.i(TAG, "Sync Core tapped — initiating 1-tap clipboard sync")

        // 1. Enter SYNCING state with subtle rotation animation
        applyState(SyncState.SYNCING)

        // 2. Launch translucent ClipSyncCaptureActivity to obtain window focus and capture primaryClip
        try {
            val syncIntent = Intent(context, ClipSyncCaptureActivity::class.java).apply {
                action = ClipSyncCaptureActivity.ACTION_SYNC_TO_MAC
                putExtra(ClipSyncCaptureActivity.EXTRA_SOURCE, "floating_button")
                addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_NO_ANIMATION)
            }
            context.startActivity(syncIntent)
        } catch (e: Exception) {
            Log.e(TAG, "Could not start capture activity from overlay: ${e.message}")
            showFeedbackBadge("Sync error", isSuccess = false)
            applyState(SyncState.ERROR)
            return
        }

        // Safety timeout in case no result notification arrives
        mainHandler.postDelayed({
            if (currentState == SyncState.SYNCING) {
                applyState(SyncState.IDLE)
            }
        }, 3000L)
    }

    /**
     * Applies a specific visual state to the Sync Core orb.
     */
    fun applyState(state: SyncState) {
        mainHandler.post {
            currentState = state
            val orb = orbSyncCore ?: return@post
            val icon = imgSyncCoreIcon ?: return@post

            stateResetRunnable?.let { mainHandler.removeCallbacks(it) }

            when (state) {
                SyncState.IDLE -> {
                    stopRotationAnimation()
                    orb.setBackgroundResource(R.drawable.bg_sync_core_orb)
                    icon.setImageResource(R.drawable.ic_sync_core_arrows)
                    icon.rotation = 0f
                    icon.scaleX = 1f
                    icon.scaleY = 1f
                }

                SyncState.SYNCING -> {
                    orb.setBackgroundResource(R.drawable.bg_sync_core_orb)
                    icon.setImageResource(R.drawable.ic_sync_core_arrows)
                    startRotationAnimation()
                }

                SyncState.SUCCESS -> {
                    stopRotationAnimation()
                    orb.setBackgroundResource(R.drawable.bg_sync_core_orb_success)
                    icon.setImageResource(R.drawable.ic_sync_core_check)
                    icon.rotation = 0f

                    if (areAnimationsEnabled()) {
                        orb.scaleX = 0.92f
                        orb.scaleY = 0.92f
                        orb.animate()
                            .scaleX(1.0f)
                            .scaleY(1.0f)
                            .setInterpolator(OvershootInterpolator(2.0f))
                            .setDuration(220)
                            .start()
                    }

                    // Schedule return to idle after 2 seconds
                    stateResetRunnable = Runnable {
                        applyState(SyncState.IDLE)
                    }
                    mainHandler.postDelayed(stateResetRunnable!!, 2000L)
                }

                SyncState.ERROR -> {
                    stopRotationAnimation()
                    orb.setBackgroundResource(R.drawable.bg_sync_core_orb_error)
                    icon.setImageResource(R.drawable.ic_sync_core_error)
                    icon.rotation = 0f

                    if (areAnimationsEnabled()) {
                        playWobbleAnimation(orb)
                    }

                    // Schedule return to idle after 2 seconds
                    stateResetRunnable = Runnable {
                        applyState(SyncState.IDLE)
                    }
                    mainHandler.postDelayed(stateResetRunnable!!, 2000L)
                }
            }
        }
    }

    private fun startRotationAnimation() {
        stopRotationAnimation()
        val icon = imgSyncCoreIcon ?: return
        if (!areAnimationsEnabled()) return

        syncRotateAnimator = ValueAnimator.ofFloat(0f, 360f).apply {
            duration = 850
            repeatCount = ValueAnimator.INFINITE
            interpolator = LinearInterpolator()
            addUpdateListener { animator ->
                icon.rotation = animator.animatedValue as Float
            }
            start()
        }
    }

    private fun stopRotationAnimation() {
        syncRotateAnimator?.cancel()
        syncRotateAnimator = null
        imgSyncCoreIcon?.rotation = 0f
    }

    private fun playWobbleAnimation(view: View) {
        view.translationX = 0f
        view.animate()
            .translationX(-dpToPx(4).toFloat())
            .setDuration(50)
            .withEndAction {
                view.animate()
                    .translationX(dpToPx(4).toFloat())
                    .setDuration(60)
                    .withEndAction {
                        view.animate()
                            .translationX(-dpToPx(2).toFloat())
                            .setDuration(50)
                            .withEndAction {
                                view.animate()
                                    .translationX(0f)
                                    .setDuration(50)
                                    .start()
                            }
                            .start()
                    }
                    .start()
            }
            .start()
    }

    private fun animateSnap(startX: Int, endX: Int, params: WindowManager.LayoutParams) {
        val metrics = context.resources.displayMetrics
        val screenWidth = metrics.widthPixels
        val buttonSize = dpToPx(56)

        isSnappedToRight = (endX + buttonSize / 2 >= screenWidth / 2)
        updateContainerChildOrder()

        if (!areAnimationsEnabled() || startX == endX) {
            currentOrbX = endX
            params.x = endX
            try {
                windowManager.updateViewLayout(overlayView, params)
                secureStorage.floatingButtonX = currentOrbX
                secureStorage.floatingButtonY = currentOrbY
            } catch (_: Exception) {}
            return
        }

        snapAnimator?.cancel()
        snapAnimator = ValueAnimator.ofInt(startX, endX).apply {
            duration = 180
            interpolator = OvershootInterpolator(1.1f)
            addUpdateListener { animator ->
                currentOrbX = animator.animatedValue as Int
                params.x = currentOrbX
                try {
                    windowManager.updateViewLayout(overlayView, params)
                } catch (_: Exception) {}
            }
            addListener(object : AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: Animator) {
                    currentOrbX = endX
                    params.x = endX
                    secureStorage.floatingButtonX = currentOrbX
                    secureStorage.floatingButtonY = currentOrbY
                }
            })
            start()
        }
    }

    /**
     * Displays an ephemeral feedback badge next to the Sync Core orb.
     */
    fun showFeedbackBadge(text: String, isSuccess: Boolean) {
        mainHandler.post {
            val badge = txtFeedbackBadge ?: return@post
            val params = layoutParams ?: return@post
            val view = overlayView ?: return@post

            badgeFadeRunnable?.let { mainHandler.removeCallbacks(it) }

            badge.text = text
            if (isSuccess) {
                badge.setTextColor(Color.parseColor("#00F5A0")) // Mint Green
                val bg = badge.background
                if (bg != null) {
                    DrawableCompat.setTint(bg.mutate(), Color.parseColor("#0F2820"))
                }
            } else {
                badge.setTextColor(Color.parseColor("#FCA5A5")) // Soft Red
                val bg = badge.background
                if (bg != null) {
                    DrawableCompat.setTint(bg.mutate(), Color.parseColor("#2E1317"))
                }
            }

            badge.visibility = View.VISIBLE
            badge.alpha = 0f

            // Measure badge width to adjust layoutParams.x if snapped to right edge
            // so the Sync Core orb remains perfectly stationary on screen
            badge.measure(
                View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED),
                View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED)
            )
            val badgeTotalWidth = badge.measuredWidth + dpToPx(16) // including horizontal margins

            if (isSnappedToRight) {
                params.x = (currentOrbX - badgeTotalWidth).coerceAtLeast(0)
            } else {
                params.x = currentOrbX
            }

            try {
                windowManager.updateViewLayout(view, params)
            } catch (_: Exception) {}

            if (areAnimationsEnabled()) {
                badge.animate()
                    .alpha(1f)
                    .setDuration(150)
                    .start()
            } else {
                badge.alpha = 1f
            }

            badgeFadeRunnable = Runnable {
                if (areAnimationsEnabled()) {
                    badge.animate()
                        .alpha(0f)
                        .setDuration(200)
                        .withEndAction {
                            badge.visibility = View.GONE
                            params.x = currentOrbX
                            try {
                                windowManager.updateViewLayout(view, params)
                            } catch (_: Exception) {}
                        }
                        .start()
                } else {
                    badge.visibility = View.GONE
                    params.x = currentOrbX
                    try {
                        windowManager.updateViewLayout(view, params)
                    } catch (_: Exception) {}
                }
            }
            mainHandler.postDelayed(badgeFadeRunnable!!, 2000L)
        }
    }

    private fun areAnimationsEnabled(): Boolean {
        return try {
            val scale = Settings.Global.getFloat(
                context.contentResolver,
                Settings.Global.ANIMATOR_DURATION_SCALE,
                1.0f
            )
            scale > 0f
        } catch (_: Exception) {
            true
        }
    }

    private fun cancelAnimators() {
        snapAnimator?.cancel()
        snapAnimator = null
        stopRotationAnimation()
        badgeFadeRunnable?.let { mainHandler.removeCallbacks(it) }
        badgeFadeRunnable = null
        stateResetRunnable?.let { mainHandler.removeCallbacks(it) }
        stateResetRunnable = null
    }

    private fun dpToPx(dp: Int): Int {
        return (dp * context.resources.displayMetrics.density).toInt()
    }

    companion object {
        const val TAG = "FloatingSyncButton"

        @Volatile
        var activeInstance: FloatingSyncButtonManager? = null
            private set

        /**
         * Global notification entry point for sync results.
         */
        fun notifySyncResult(isSuccess: Boolean, message: String) {
            activeInstance?.let { manager ->
                manager.showFeedbackBadge(message, isSuccess)
                manager.applyState(if (isSuccess) SyncState.SUCCESS else SyncState.ERROR)
            }
        }
    }
}

/**
 * Pure calculation helpers for testability without WindowManager mocks.
 */
object FloatingOverlayCalculator {

    fun isDragGesture(dx: Float, dy: Float, touchSlop: Int): Boolean {
        return hypot(dx.toDouble(), dy.toDouble()) > touchSlop
    }

    fun computeSnapTargetX(currentX: Int, buttonWidth: Int, screenWidth: Int, margin: Int): Int {
        val centerX = currentX + buttonWidth / 2
        return if (centerX < screenWidth / 2) {
            margin
        } else {
            (screenWidth - buttonWidth - margin).coerceAtLeast(0)
        }
    }

    fun clampCoordinates(
        x: Int,
        y: Int,
        buttonWidth: Int,
        buttonHeight: Int,
        screenWidth: Int,
        screenHeight: Int,
        topInset: Int,
        bottomInset: Int
    ): Pair<Int, Int> {
        val minX = 0
        val maxX = (screenWidth - buttonWidth).coerceAtLeast(0)
        val minY = topInset
        val maxY = (screenHeight - buttonHeight - bottomInset).coerceAtLeast(minY)

        val clampedX = x.coerceIn(minX, maxX)
        val clampedY = y.coerceIn(minY, maxY)
        return Pair(clampedX, clampedY)
    }
}
