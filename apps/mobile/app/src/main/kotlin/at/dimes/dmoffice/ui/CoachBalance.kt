package at.dimes.dmoffice.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.coach.Day
import at.dimes.dmoffice.coach.MEALS
import java.text.NumberFormat
import java.time.LocalDate
import java.time.ZoneId
import kotlin.math.cos
import kotlin.math.sin

private val nf0 = NumberFormat.getIntegerInstance()
private fun n0(v: Double) = nf0.format(Math.round(v))

private val EatenColor = Color(0xFFC239B3)
private val BurnedColor = Color(0xFFEAA300)

/** Übliche Uhrzeit einer Mahlzeit - für Einträge, die an einem anderen Tag nachgetragen wurden (wie im Web). */
private val MealHour = mapOf("breakfast" to 8.0, "lunch" to 12.5, "dinner" to 19.0, "snack" to 15.5)

/** Waage (gegessen gegen verbraucht) und Kalorien im Tagesverlauf nebeneinander, wie die Tagesübersicht im Web. */
@Composable
internal fun BalanceCard(d: Day) {
    // Bedarf: Gesamtumsatz des Tages plus Bewegung; die Farbe richtet sich nach dem Tagesziel (beim Abnehmen mit Defizit)
    val burned = d.targets.tdee?.let { it + d.activityBonus }
    val past = d.date < LocalDate.now(ZoneId.systemDefault()).toString()
    Card(Modifier.fillMaxWidth()) {
        Row(Modifier.fillMaxWidth().padding(12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f), horizontalAlignment = Alignment.CenterHorizontally) {
                Scale(d.eaten.kcal, burned, d.budget, past, Modifier.fillMaxWidth().aspectRatio(1.6f))
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceAround) {
                    Value(if (d.hideNumbers) "–" else n0(d.eaten.kcal), stringResource(R.string.coach_scale_eaten), EatenColor)
                    Value(burned?.let { n0(it) } ?: "–", stringResource(R.string.coach_scale_burned), BurnedColor)
                }
            }
            if (!d.hideNumbers) Column(Modifier.weight(1f)) {
                Text(stringResource(R.string.coach_curve), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Curve(d, d.budget, Modifier.fillMaxWidth().aspectRatio(1.5f))
            }
        }
    }
}

@Composable
private fun Value(value: String, label: String, color: Color) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(value, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, color = color)
        Text(label, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/**
 * Apothekerwaage: der Balken neigt sich zur schwereren Seite (höchstens 14°). Farbe nach dem Tagesziel: nah dran grün,
 * darüber orange/rot, deutlich darunter erst bei abgeschlossenen Tagen gelb/orange (wie im Web).
 */
@Composable
internal fun Scale(eaten: Double, burned: Double?, goal: Double?, past: Boolean, modifier: Modifier) {
    val diff = if (burned != null) (eaten - burned) / maxOf(burned, 1.0) else 0.0
    val angle = (diff * 40).coerceIn(-14.0, 14.0)
    val neutral = MaterialTheme.colorScheme.onSurfaceVariant
    val target = goal ?: burned
    val off = target?.let { (eaten - it) / maxOf(it, 1.0) }
    val ink = when {
        off == null -> neutral
        off > 0.25 -> Color(0xFFD13438)
        off > 0.08 -> Color(0xFFE07B00)
        off >= -0.08 -> Color(0xFF2E9E5B)
        !past -> neutral
        off >= -0.25 -> Color(0xFFC9A400)
        else -> Color(0xFFE07B00)
    }
    val bg = MaterialTheme.colorScheme.surface
    Canvas(modifier) {
        val u = size.width / 220f
        val cx = size.width / 2
        val pivotY = 22 * u
        val arm = 78 * u
        val chain = 44 * u
        val rad = Math.toRadians(-angle)
        val l = Offset(cx - arm * cos(rad).toFloat(), pivotY - arm * sin(rad).toFloat())
        val r = Offset(cx + arm * cos(rad).toFloat(), pivotY + arm * sin(rad).toFloat())
        val bottom = size.height - 2 * u
        // Ständer und Fuß
        drawLine(ink, Offset(cx, pivotY), Offset(cx, bottom - 8 * u), 3 * u, StrokeCap.Round)
        drawPath(Path().apply {
            moveTo(cx - 26 * u, bottom); lineTo(cx + 26 * u, bottom); lineTo(cx + 14 * u, bottom - 10 * u); lineTo(cx - 14 * u, bottom - 10 * u); close()
        }, ink)
        drawCircle(ink, 4 * u, Offset(cx, pivotY - 8 * u))
        // Balken
        drawLine(ink, l, r, 3 * u, StrokeCap.Round)
        drawCircle(bg, 4 * u, Offset(cx, pivotY))
        drawCircle(ink, 4 * u, Offset(cx, pivotY), style = Stroke(2 * u))
        // Schalen hängen immer waagerecht
        for ((p, c) in listOf(l to EatenColor, r to BurnedColor)) {
            drawLine(ink, p, Offset(p.x - 22 * u, p.y + chain), 1 * u)
            drawLine(ink, p, Offset(p.x + 22 * u, p.y + chain), 1 * u)
            drawPath(Path().apply {
                moveTo(p.x - 30 * u, p.y + chain); quadraticTo(p.x, p.y + chain + 16 * u, p.x + 30 * u, p.y + chain); close()
            }, c.copy(alpha = 0.85f))
        }
    }
}

/** Treppe der aufsummierten Kalorien über den Tag (x: Uhrzeit), gestrichelt das Tagesziel. */
@Composable
private fun Curve(d: Day, goal: Double?, modifier: Modifier) {
    val zone = ZoneId.systemDefault()
    val dayStart = LocalDate.parse(d.date).atStartOfDay(zone).toEpochSecond()
    val points = MEALS.flatMap { m -> d.meals[m].orEmpty() }.map { e ->
        val h = (e.at - dayStart) / 3600.0
        (if (e.at > 0 && h >= 0 && h < 24) h else MealHour[e.meal] ?: 12.0) to e.kcal
    }.sortedBy { it.first }
    val today = LocalDate.now(zone).toString() == d.date
    val end = if (today) minOf(24.0, (System.currentTimeMillis() / 1000 - dayStart) / 3600.0) else 24.0
    val total = points.sumOf { it.second }
    val max = maxOf(goal ?: 0.0, total, 100.0) * 1.08
    val grid = MaterialTheme.colorScheme.outlineVariant
    val label = MaterialTheme.colorScheme.onSurfaceVariant
    Canvas(modifier) {
        val pad = 4.dp.toPx()
        fun x(h: Double) = (pad + h / 24 * (size.width - 2 * pad)).toFloat()
        fun y(k: Double) = (pad + (1 - k / max) * (size.height - 2 * pad)).toFloat()
        for (h in listOf(0.0, 6.0, 12.0, 18.0, 24.0)) drawLine(grid, Offset(x(h), pad), Offset(x(h), size.height - pad), 0.5.dp.toPx())
        drawLine(grid, Offset(x(0.0), y(0.0)), Offset(x(24.0), y(0.0)), 1.dp.toPx())
        if (goal != null) drawLine(label, Offset(x(0.0), y(goal)), Offset(x(24.0), y(goal)), 1.dp.toPx(),
            pathEffect = PathEffect.dashPathEffect(floatArrayOf(4.dp.toPx(), 3.dp.toPx())))
        var sum = 0.0
        val path = Path().apply {
            moveTo(x(0.0), y(0.0))
            for ((h, k) in points) { lineTo(x(h), y(sum)); sum += k; lineTo(x(h), y(sum)) }
            lineTo(x(maxOf(end, points.lastOrNull()?.first ?: 0.0)), y(sum))
        }
        drawPath(path, EatenColor, style = Stroke(2.dp.toPx()))
    }
}
