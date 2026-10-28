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
import android.view.animation.OvershootInterpolator
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.TextView
import androidx.core.graphics.drawable.DrawableCompat
import com.clipsync.android.R
import com.clipsync.android.storage.SecureStorage
import com.clipsync.android.ui.ClipSyncCaptureActivity
import kotlin.math.hypot

/**
 * Manages the floating Tennis Ball overlay button.
 *
 * Requirements:
 * 1. Uses WindowManager with TYPE_APPLICATION_OVERLAY and FLAG_NOT_FOCUSABLE.
 * 2. Draggable and snaps to the nearest screen edge.
 * 3. Distinguishes drag from tap using ViewConfiguration touch slop.
 * 4. Tap launches ClipSyncCaptureActivity (ACTION_SYNC_TO_MAC) to gain window focus
 *    and read primaryClip without opening MainActivity.
 * 5. Provides visual feedback (scale bounce + ephemeral status badge).
 * 6. Handles configuration changes, screen bounds, and safe lifecycle attachment.
 */
class FloatingSyncButtonManager(
    private val context: Context,
    private val secureStorage: SecureStorage
) {

    private val windowManager = context.getSystemService(Context.WINDOW_SERVICE) as WindowManager
    private val mainHandler = Handler(Looper.getMainLooper())

    private var overlayView: View? = null
    private var containerFloating: LinearLayout? = null
    private var imgTennisBall: ImageView? = null
    private var txtFeedbackBadge: TextView? = null

    private var layoutParams: WindowManager.LayoutParams? = null
    var isAttached: Boolean = false
        private set

    // Saved position of the tennis ball itself (x, y)
    private var currentBallX: Int = 0
    private var currentBallY: Int = 0
    private var isSnappedToRight: Boolean = true

    private var isSyncing: Boolean = false
    private var lastSyncTime: Long = 0L

    private var snapAnimator: ValueAnimator? = null
    private var badgeFadeRunnable: Runnable? = null

    init {
        activeInstance = this
    }

    /**
     * Shows the floating tennis ball overlay if permission is granted and not already attached.
     */
    fun show() {
        if (isAttached) {
            Log.d(TAG, "Floating button already attached")
            return
        }

        if (!Settings.canDrawOverlays(context)) {
            Log.w(TAG, "Cannot show floating button: SYSTEM_ALERT_WINDOW permission not granted")
            return
        }

        mainHandler.post {
            try {
                createAndAttachView()
            } catch (e: Exception) {
                Log.e(TAG, "Error attaching floating button overlay: ${e.message}", e)
                isAttached = false
            }
        }
    }

    /**
     * Hides and removes the floating button overlay from the WindowManager.
     */
    fun hide() {
        mainHandler.post {
            cancelAnimators()
            if (isAttached && overlayView != null) {
                try {
                    windowManager.removeView(overlayView)
                    Log.i(TAG, "Floating button overlay removed successfully")
                } catch (e: Exception) {
                    Log.w(TAG, "Error removing overlay view: ${e.message}")
                }
            }
            overlayView = null
            containerFloating = null
            imgTennisBall = null
            txtFeedbackBadge = null
            layoutParams = null
            isAttached = false
        }
    }

    /**
     * Clean up resources and animators.
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

            val buttonSize = dpToPx(60)
            val clamped = FloatingOverlayCalculator.clampCoordinates(
                x = currentBallX,
                y = currentBallY,
                buttonWidth = buttonSize,
                buttonHeight = buttonSize,
                screenWidth = screenWidth,
                screenHeight = screenHeight,
                topInset = dpToPx(36),
                bottomInset = dpToPx(48)
            )

            // Re-snap to closest edge in the new orientation
            val snappedX = FloatingOverlayCalculator.computeSnapTargetX(
                currentX = clamped.first,
                buttonWidth = buttonSize,
                screenWidth = screenWidth,
                margin = dpToPx(8)
            )

            currentBallX = snappedX
            currentBallY = clamped.second
            isSnappedToRight = (currentBallX + buttonSize / 2 >= screenWidth / 2)

            updateContainerChildOrder()

            params.x = currentBallX
            params.y = currentBallY

            try {
                windowManager.updateViewLayout(overlayView, params)
                secureStorage.floatingButtonX = currentBallX
                secureStorage.floatingButtonY = currentBallY
            } catch (e: Exception) {
                Log.w(TAG, "Could not update overlay layout on config change: ${e.message}")
            }
        }
    }

    private fun createAndAttachView() {
        if (isAttached) return

        val inflater = LayoutInflater.from(context)
        val view = inflater.inflate(R.layout.layout_floating_tennis_ball, null)
        overlayView = view

        containerFloating = view.findViewById(R.id.containerFloating)
        imgTennisBall = view.findViewById(R.id.imgTennisBall)
        txtFeedbackBadge = view.findViewById(R.id.txtFeedbackBadge)

        val metrics = context.resources.displayMetrics
        val screenWidth = metrics.widthPixels
        val screenHeight = metrics.heightPixels
        val buttonSize = dpToPx(60)

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

        currentBallX = initialX
        currentBallY = initialY
        isSnappedToRight = (currentBallX + buttonSize / 2 >= screenWidth / 2)

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
            x = currentBallX
            y = currentBallY
        }
        layoutParams = params

        setupTouchListener(imgTennisBall!!, params)

        windowManager.addView(view, params)
        isAttached = true
        Log.i(TAG, "Floating button attached at x=$currentBallX, y=$currentBallY (isRight=$isSnappedToRight)")
    }

    private fun updateContainerChildOrder() {
        val container = containerFloating ?: return
        val ball = imgTennisBall ?: return
        val badge = txtFeedbackBadge ?: return

        container.removeAllViews()
        if (isSnappedToRight) {
            // Ball on right edge: [Badge] [Ball]
            container.addView(badge)
            container.addView(ball)
        } else {
            // Ball on left edge: [Ball] [Badge]
            container.addView(ball)
            container.addView(badge)
        }
    }

    private fun setupTouchListener(ballView: View, params: WindowManager.LayoutParams) {
        val touchSlop = ViewConfiguration.get(context).scaledTouchSlop
        var initialX = 0
        var initialY = 0
        var initialTouchX = 0f
        var initialTouchY = 0f
        var isDragging = false

        ballView.setOnTouchListener { _, event ->
            when (event.action) {
                MotionEvent.ACTION_DOWN -> {
                    cancelAnimators()
                    // Hide any active badge immediately when touched
                    txtFeedbackBadge?.visibility = View.GONE

                    initialX = currentBallX
                    initialY = currentBallY
                    initialTouchX = event.rawX
                    initialTouchY = event.rawY
                    isDragging = false
                    true
                }

                MotionEvent.ACTION_MOVE -> {
                    val dx = event.rawX - initialTouchX
                    val dy = event.rawY - initialTouchY

                    if (!isDragging && FloatingOverlayCalculator.isDragGesture(dx, dy, touchSlop)) {
                        isDragging = true
                    }

                    if (isDragging) {
                        val metrics = context.resources.displayMetrics
                        val buttonSize = dpToPx(60)
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

                        currentBallX = clamped.first
                        currentBallY = clamped.second
                        params.x = currentBallX
                        params.y = currentBallY

                        try {
                            windowManager.updateViewLayout(overlayView, params)
                        } catch (_: Exception) {}
                    }
                    true
                }

                MotionEvent.ACTION_UP -> {
                    if (!isDragging) {
                        // Deliberate TAP: Initiate sync
                        handleBallTap()
                    } else {
                        // Drag completed: Snap to screen edge smoothly
                        val metrics = context.resources.displayMetrics
                        val screenWidth = metrics.widthPixels
                        val buttonSize = dpToPx(60)

                        val targetX = FloatingOverlayCalculator.computeSnapTargetX(
                            currentX = currentBallX,
                            buttonWidth = buttonSize,
                            screenWidth = screenWidth,
                            margin = dpToPx(8)
                        )
                        animateSnap(currentBallX, targetX, params)
                    }
                    isDragging = false
                    true
                }

                else -> false
            }
        }
    }

    private fun handleBallTap() {
        val now = SystemClock.elapsedRealtime()
        if (isSyncing || now - lastSyncTime < 600L) {
            Log.d(TAG, "Ignoring tap: sync already in progress or debounced")
            return
        }
        lastSyncTime = now
        isSyncing = true

        Log.i(TAG, "Floating tennis ball tapped — triggering 1-tap clipboard sync")

        // 1. Play tactile squash & bounce animation on the tennis ball
        playBallBounceAnimation()

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
            isSyncing = false
            return
        }

        // Release mutex after a safety timeout
        mainHandler.postDelayed({
            isSyncing = false
        }, 1200L)
    }

    private fun playBallBounceAnimation() {
        val ball = imgTennisBall ?: return
        if (!areAnimationsEnabled()) return

        ball.animate().cancel()
        ball.scaleX = 1f
        ball.scaleY = 1f

        // Squash to 0.82 and bounce back with overshoot
        ball.animate()
            .scaleX(0.82f)
            .scaleY(0.82f)
            .setDuration(90)
            .withEndAction {
                ball.animate()
                    .scaleX(1.0f)
                    .scaleY(1.0f)
                    .setInterpolator(OvershootInterpolator(2.2f))
                    .setDuration(220)
                    .start()
            }
            .start()
    }

    private fun animateSnap(startX: Int, endX: Int, params: WindowManager.LayoutParams) {
        val metrics = context.resources.displayMetrics
        val screenWidth = metrics.widthPixels
        val buttonSize = dpToPx(60)

        isSnappedToRight = (endX + buttonSize / 2 >= screenWidth / 2)
        updateContainerChildOrder()

        if (!areAnimationsEnabled() || startX == endX) {
            currentBallX = endX
            params.x = endX
            try {
                windowManager.updateViewLayout(overlayView, params)
                secureStorage.floatingButtonX = currentBallX
                secureStorage.floatingButtonY = currentBallY
            } catch (_: Exception) {}
            return
        }

        snapAnimator?.cancel()
        snapAnimator = ValueAnimator.ofInt(startX, endX).apply {
            duration = 180
            interpolator = OvershootInterpolator(1.1f)
            addUpdateListener { animator ->
                currentBallX = animator.animatedValue as Int
                params.x = currentBallX
                try {
                    windowManager.updateViewLayout(overlayView, params)
                } catch (_: Exception) {}
            }
            addListener(object : AnimatorListenerAdapter() {
                override fun onAnimationEnd(animation: Animator) {
                    currentBallX = endX
                    params.x = endX
                    secureStorage.floatingButtonX = currentBallX
                    secureStorage.floatingButtonY = currentBallY
                }
            })
            start()
        }
    }

    /**
     * Displays a small, non-intrusive feedback badge next to the tennis ball.
     */
    fun showFeedbackBadge(text: String, isSuccess: Boolean) {
        mainHandler.post {
            val badge = txtFeedbackBadge ?: return@post
            val params = layoutParams ?: return@post
            val view = overlayView ?: return@post

            badgeFadeRunnable?.let { mainHandler.removeCallbacks(it) }

            badge.text = text
            if (isSuccess) {
                badge.setTextColor(Color.WHITE)
                val bg = badge.background
                if (bg != null) {
                    DrawableCompat.setTint(bg.mutate(), Color.parseColor("#10B981")) // Emerald Green
                }
            } else {
                badge.setTextColor(Color.WHITE)
                val bg = badge.background
                if (bg != null) {
                    DrawableCompat.setTint(bg.mutate(), Color.parseColor("#EF4444")) // Crimson Red
                }
            }

            badge.visibility = View.VISIBLE
            badge.alpha = 0f

            // Measure badge width to adjust layoutParams.x if snapped to right edge
            // so the tennis ball remains perfectly stationary on screen
            badge.measure(
                View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED),
                View.MeasureSpec.makeMeasureSpec(0, View.MeasureSpec.UNSPECIFIED)
            )
            val badgeTotalWidth = badge.measuredWidth + dpToPx(12) // including horizontal margins

            if (isSnappedToRight) {
                params.x = (currentBallX - badgeTotalWidth).coerceAtLeast(0)
            } else {
                params.x = currentBallX
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
                            params.x = currentBallX
                            try {
                                windowManager.updateViewLayout(view, params)
                            } catch (_: Exception) {}
                        }
                        .start()
                } else {
                    badge.visibility = View.GONE
                    params.x = currentBallX
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
        badgeFadeRunnable?.let { mainHandler.removeCallbacks(it) }
        badgeFadeRunnable = null
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
            activeInstance?.showFeedbackBadge(message, isSuccess)
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
