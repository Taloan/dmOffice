package at.dimes.dmoffice.widget

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Checkbox
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.ColorMark
import at.dimes.dmoffice.data.RowLayout
import at.dimes.dmoffice.data.TimePos
import at.dimes.dmoffice.data.WidgetConfig

/**
 * Beispielzeilen oben in den Widget-Einstellungen: zeigt sofort, wie Zeilenlayout, Farbmarke, Zeitposition,
 * Textgroesse und Deckkraft aussehen. Nachgebaut wie in Widgets.kt (AgendaLine, TaskLine) - nur zur Vorschau.
 */
@Composable
internal fun WidgetPreview(c: WidgetConfig, tasksOnly: Boolean) {
    val bg = MaterialTheme.colorScheme.surface.copy(alpha = c.opacity / 100f)
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp)
            .background(MaterialTheme.colorScheme.surfaceVariant, RoundedCornerShape(16.dp)).padding(8.dp)
    ) {
        Text(stringResource(R.string.preview), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(start = 4.dp, bottom = 4.dp))
        Column(Modifier.fillMaxWidth().background(bg, RoundedCornerShape(12.dp)).padding(horizontal = 10.dp, vertical = 6.dp)) {
            if (!tasksOnly) {
                SampleEvent(c, Color(0xFF0F6CBD), "09:30", "09:30 – 10:30", stringResource(R.string.preview_event), stringResource(R.string.preview_location))
                SampleEvent(c, Color(0xFF13A10E), stringResource(R.string.all_day), stringResource(R.string.all_day), stringResource(R.string.preview_event2), null)
            } else {
                SampleTask(c, Color(0xFF038387), stringResource(R.string.preview_task), stringResource(R.string.due_today))
                SampleTask(c, Color(0xFFE3008C), stringResource(R.string.preview_task2), null)
            }
        }
    }
}

@Composable
private fun SampleEvent(c: WidgetConfig, color: Color, time: String, range: String, title: String, location: String?) {
    val s = c.textSize.scale
    val fg = MaterialTheme.colorScheme.onSurface
    val sub = MaterialTheme.colorScheme.onSurfaceVariant
    val badge = c.colorMark == ColorMark.Badge
    val loc = if (c.showLocation) location else null
    val lines = when (c.rowLayout) { RowLayout.Compact -> 1; RowLayout.Normal -> 2; RowLayout.Detailed -> 2 + (if (loc != null) 1 else 0) }
    val timeText = if (badge) range else time
    @Composable fun T(t: String, max: Int = 1) = Text(t, maxLines = max, overflow = TextOverflow.Ellipsis, fontSize = (14 * s).sp, fontWeight = FontWeight.Bold, color = fg)
    @Composable fun S(t: String, m: Modifier = Modifier) = Text(t, maxLines = 1, overflow = TextOverflow.Ellipsis, fontSize = (11.5f * s).sp, color = sub, modifier = m)
    Row(Modifier.fillMaxWidth().padding(vertical = if (c.rowLayout == RowLayout.Compact) 1.dp else 3.dp), verticalAlignment = Alignment.CenterVertically) {
        if (badge) {
            Box(Modifier.size((36 * s).dp).background(color, RoundedCornerShape(8.dp)), contentAlignment = Alignment.Center) {
                Text(time.take(5), fontSize = (11 * s).sp, color = Color.White, fontWeight = FontWeight.Medium, maxLines = 1)
            }
            Spacer(Modifier.width(10.dp))
        } else Mark(c, color, 14f * lines * s)
        when (c.rowLayout) {
            RowLayout.Compact -> { if (!badge) S(time, Modifier.width((52 * s).dp)); Column(Modifier.weight(1f)) { T(title) } }
            else -> if (c.timePos == TimePos.Left && !badge) {
                S(timeText, Modifier.width((56 * s).dp))
                Column(Modifier.weight(1f)) {
                    T(title, if (c.rowLayout == RowLayout.Detailed) 2 else 1)
                    if (loc != null) S(loc)
                }
            } else Column(Modifier.weight(1f)) {
                T(title, if (c.rowLayout == RowLayout.Detailed) 2 else 1)
                S(if (c.rowLayout == RowLayout.Normal) timeText + (loc?.let { " · $it" } ?: "") else timeText)
                if (c.rowLayout == RowLayout.Detailed && loc != null) S(loc)
            }
        }
    }
}

@Composable
private fun SampleTask(c: WidgetConfig, color: Color, title: String, due: String?) {
    val s = c.textSize.scale
    val sub = MaterialTheme.colorScheme.onSurfaceVariant
    if (c.tapToComplete) {
        Column(Modifier.fillMaxWidth()) {
            Text(title, maxLines = 2, fontSize = (15 * s).sp, modifier = Modifier.padding(start = 4.dp, top = (8 * s).dp, bottom = (8 * s).dp))
            Box(Modifier.fillMaxWidth().height(1.dp).background(MaterialTheme.colorScheme.outline))
        }
        return
    }
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Checkbox(checked = false, onCheckedChange = null, modifier = Modifier.padding(end = 6.dp))
        Column(Modifier.weight(1f)) {
            when (c.rowLayout) {
                RowLayout.Compact -> Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(title, maxLines = 1, fontSize = (13 * s).sp, modifier = Modifier.weight(1f))
                    if (due != null) Text(due, maxLines = 1, fontSize = (11 * s).sp, color = sub, modifier = Modifier.padding(start = 6.dp))
                }
                else -> {
                    Text(title, maxLines = if (c.rowLayout == RowLayout.Detailed) 2 else 1, fontSize = (13 * s).sp)
                    if (due != null) Text(due, maxLines = 1, fontSize = (11 * s).sp, color = sub)
                }
            }
        }
        when (c.colorMark) {
            ColorMark.Badge, ColorMark.Bar -> Box(Modifier.width(4.dp).height((24 * s).dp).background(color, RoundedCornerShape(2.dp)))
            ColorMark.Dot -> Box(Modifier.size(9.dp).background(color, CircleShape))
            ColorMark.None -> {}
        }
    }
}

@Composable
private fun Mark(c: WidgetConfig, color: Color, height: Float) {
    when (c.colorMark) {
        ColorMark.Bar -> { Box(Modifier.width(4.dp).height(height.dp).background(color, RoundedCornerShape(2.dp))); Spacer(Modifier.width(8.dp)) }
        ColorMark.Dot -> { Box(Modifier.size(9.dp).background(color, CircleShape)); Spacer(Modifier.width(8.dp)) }
        else -> {}
    }
}
