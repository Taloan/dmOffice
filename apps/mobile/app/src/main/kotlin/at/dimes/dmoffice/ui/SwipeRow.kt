package at.dimes.dmoffice.ui

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Archive
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material.icons.filled.MarkEmailRead
import androidx.compose.material.icons.filled.Report
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.SwapHoriz
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.remember
import androidx.compose.runtime.getValue
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import kotlinx.coroutines.launch
import kotlin.math.abs
import kotlin.math.roundToInt

/** Was eine Wischgeste in der Mailliste tut. "none" beim weiten Wischen: wie kurzes Wischen. */
enum class SwipeAction(val key: String, val label: Int, val icon: ImageVector?, val color: Color) {
    None("none", R.string.swipe_none, null, Color(0xFF6B6B6B)),
    Delete("delete", R.string.delete, Icons.Default.Delete, Color(0xFFD13438)),
    Archive("archive", R.string.archive, Icons.Default.Archive, ActionColors.Archive),
    Read("read", R.string.swipe_read, Icons.Default.MarkEmailRead, ActionColors.Forward),
    Flag("flag", R.string.flag_menu, Icons.Default.Flag, Color(0xFFE8A317)),
    Snooze("snooze", R.string.swipe_snooze, Icons.Default.Schedule, ActionColors.Schedule),
    Junk("junk", R.string.report_junk, Icons.Default.Report, Color(0xFF8A2BE2)),
    Focus("focus", R.string.swipe_focus, Icons.Default.SwapHoriz, Color(0xFF0B8A96));

    companion object { fun of(key: String?) = entries.firstOrNull { it.key == key } ?: None }
}

/**
 * Zeile mit Wischgesten in zwei Stufen wie in Outlook/Gmail: ein Stueck wischen und loslassen = erste Aktion,
 * weit wischen (ueber 55 % der Breite) = zweite Aktion. Der Hintergrund zeigt, was beim Loslassen passiert;
 * beim Wechsel der Stufe gibt es einen kurzen Ruck.
 */
@Composable
fun SwipeRow(
    left: SwipeAction, leftFar: SwipeAction, right: SwipeAction, rightFar: SwipeAction,
    onAction: (SwipeAction) -> Unit, content: @Composable () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val offset = remember { Animatable(0f) }
    // Beim Loslassen gewaehlte Aktion: bleibt sichtbar, waehrend die Zeile hinausgleitet
    // (sonst zeigte der Hintergrund dabei die Aktion fuer weites Wischen)
    var chosen by remember { androidx.compose.runtime.mutableStateOf<SwipeAction?>(null) }
    val haptic = LocalHapticFeedback.current
    val density = LocalDensity.current
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        val width = with(density) { maxWidth.toPx() }
        val near = width * 0.18f
        val far = width * 0.55f
        // Was beim Loslassen passiert (nach links: Finger nach links, offset negativ)
        fun actionAt(x: Float): SwipeAction? {
            val leftward = x < 0
            val first = if (leftward) left else right
            val second = (if (leftward) leftFar else rightFar).takeIf { it != SwipeAction.None } ?: first
            return when {
                abs(x) >= far -> second.takeIf { it != SwipeAction.None }
                abs(x) >= near -> first.takeIf { it != SwipeAction.None }
                else -> null
            }
        }
        val current = chosen ?: actionAt(offset.value)
        // Hintergrund mit Symbol und Text der Aktion
        Box(Modifier.matchParentSize().background(current?.color ?: MaterialTheme.colorScheme.surfaceVariant)) {
            if (current != null) Row(
                Modifier.matchParentSize().padding(horizontal = 20.dp),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = if (offset.value < 0) androidx.compose.foundation.layout.Arrangement.End else androidx.compose.foundation.layout.Arrangement.Start,
            ) {
                current.icon?.let { Icon(it, contentDescription = null, tint = Color.White) }
                Spacer(Modifier.width(8.dp))
                Text(stringResource(current.label), color = Color.White, style = MaterialTheme.typography.labelLarge)
            }
        }
        Box(
            Modifier.fillMaxWidth().offset { IntOffset(offset.value.roundToInt(), 0) }
                .background(MaterialTheme.colorScheme.background)
                .pointerInput(left, leftFar, right, rightFar) {
                    var last: SwipeAction? = null
                    detectHorizontalDragGestures(
                        onDragEnd = {
                            val a = actionAt(offset.value)
                            chosen = a
                            scope.launch {
                                if (a != null && (a == SwipeAction.Delete || a == SwipeAction.Archive || a == SwipeAction.Junk || a == SwipeAction.Snooze || a == SwipeAction.Focus)) {
                                    // Verschwindet aus der Liste: ganz hinausschieben, dann ausfuehren
                                    offset.animateTo(if (offset.value < 0) -width else width, tween(160))
                                    onAction(a)
                                    offset.snapTo(0f)
                                } else {
                                    if (a != null) onAction(a)
                                    offset.animateTo(0f, tween(180))
                                }
                                chosen = null
                            }
                            last = null
                        },
                        onDragCancel = { scope.launch { offset.animateTo(0f) }; last = null },
                    ) { change, dx ->
                        change.consume()
                        chosen = null
                        val next = (offset.value + dx).coerceIn(-width, width)
                        // Nur in Richtungen mit Aktion wischen
                        val allowed = if (next < 0) left != SwipeAction.None || leftFar != SwipeAction.None else right != SwipeAction.None || rightFar != SwipeAction.None
                        scope.launch { offset.snapTo(if (allowed) next else 0f) }
                        val a = actionAt(next)
                        if (a != last) { if (a != null) haptic.performHapticFeedback(HapticFeedbackType.LongPress); last = a }
                    }
                }
        ) { content() }
    }
}
