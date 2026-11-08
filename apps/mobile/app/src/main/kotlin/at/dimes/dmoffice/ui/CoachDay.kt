package at.dimes.dmoffice.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.DirectionsWalk
import androidx.compose.material.icons.automirrored.filled.DirectionsRun
import androidx.compose.material.icons.automirrored.filled.DirectionsBike
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.material.icons.filled.Bedtime
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.DinnerDining
import androidx.compose.material.icons.filled.FreeBreakfast
import androidx.compose.material.icons.filled.LunchDining
import androidx.compose.material.icons.filled.MonitorWeight
import androidx.compose.material.icons.filled.Restaurant
import androidx.compose.material.icons.filled.Cookie
import androidx.compose.material.icons.filled.WaterDrop
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import kotlinx.coroutines.launch
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.coach.Day
import at.dimes.dmoffice.coach.Entry
import at.dimes.dmoffice.coach.FastingView
import java.text.NumberFormat
import java.time.Instant
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * Tagesansicht des Coach (Entwurf vom 27.10.): oben die drei Ringe mit „+gegessen“ und „−Bewegung“, Nährstoffe und eine
 * Zeile zum Fasten bzw. zur Mahlzeit; darunter die Mahlzeiten nur als Summe (Zutaten erst beim Antippen), dann Wasser und
 * Gewicht nebeneinander (zum Erfassen), Schritte über die volle Breite. Jede Sache hat ihre Farbe - wie im Widget.
 */

internal val RingEaten = Color(0xFFC239B3)
internal val RingMove = Color(0xFF5DA84E)
internal val RingNet = Color(0xFFE8833A)
internal val RingOver = Color(0xFFD13438)
private val WeightColor = Color(0xFFB57BD0)
private val TileWater = Color(0xFF4F8FE0)

/**
 * Für Arbeiten, die einen anderen Bildschirm überdauern müssen (Kamera, Galerie, Spracheingabe): ein Bereich, der nicht mit der
 * Ansicht endet - sonst bricht das Hochladen mit „The coroutine scope left the composition“ ab.
 */
internal val CoachWork = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + kotlinx.coroutines.Dispatchers.Main)

/** Je Mahlzeit Farbe, Symbol und Anteil am Tag (wie im Web; Snacks ohne eigenes Ziel). */
internal fun mealColor(m: String) = when (m) { "breakfast" -> Color(0xFFF2B53C); "lunch" -> Color(0xFF3CC4A0); "dinner" -> Color(0xFFF06B5B); else -> Color(0xFFC239B3) }
private fun mealIcon(m: String): ImageVector = when (m) { "breakfast" -> Icons.Default.FreeBreakfast; "lunch" -> Icons.Default.LunchDining; "dinner" -> Icons.Default.DinnerDining; else -> Icons.Default.Cookie }
private fun mealShare(m: String) = when (m) { "breakfast" -> 0.35; "lunch" -> 0.35; "dinner" -> 0.3; else -> 0.0 }

private fun n0(v: Double) = NumberFormat.getIntegerInstance().format(v.roundToInt())

/** Ab 1000 kurz mit einer Nachkommastelle (2,4k). */
internal fun kShort(v: Double) = if (abs(v) < 1000) n0(v)
    else NumberFormat.getNumberInstance().apply { minimumFractionDigits = 1; maximumFractionDigits = 1 }.format(v / 1000) + "k"

/** Getönte Kachel: Fläche und Rand in der Farbe ihrer Sache. */
@Composable
internal fun Tile(color: Color, modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Card(modifier, shape = RoundedCornerShape(16.dp), colors = CardDefaults.cardColors(containerColor = color.copy(alpha = 0.10f)),
        border = BorderStroke(1.dp, color.copy(alpha = 0.35f))) { Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) { content() } }
}

@Composable
private fun TileHead(icon: ImageVector, label: String, color: Color, trailing: String? = null) = Row(verticalAlignment = Alignment.CenterVertically) {
    Icon(icon, contentDescription = null, tint = color, modifier = Modifier.size(18.dp))
    Spacer(Modifier.width(6.dp))
    Text(label, color = color, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
    trailing?.let { Text(it, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
}

/**
 * Drei Ringe, von außen nach innen zu lesen: gegessen minus Bewegung (vom Ende des Gegessenen zurück) ergibt netto (dick,
 * direkt um die Zahl). Voller Kreis = Grundbedarf ohne Bewegung; netto darüber wird rot.
 */
@Composable
internal fun TripleRing(d: Day, size: Dp, modifier: Modifier = Modifier) {
    val budget = d.budget
    // Grundbedarf ohne Bewegung = Budget − ganze aktive Energie der Uhr; die Bewegung steht im mittleren Ring am Ende des Gegessenen
    val base = budget?.let { it - d.movement }?.takeIf { it > 0 }
    val eaten = d.eaten.kcal
    val move = minOf(d.movement, eaten)
    val track = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.10f)
    Box(modifier.size(size), contentAlignment = Alignment.Center) {
        Canvas(Modifier.fillMaxSize()) {
            val thin = this.size.minDimension * 0.05f; val thick = this.size.minDimension * 0.09f; val gap = this.size.minDimension * 0.02f
            val c = this.size.minDimension / 2
            val r0 = c - thin / 2; val r1 = r0 - thin - gap; val r2 = r1 - thin / 2 - gap - thick / 2
            fun DrawScope.ring(r: Float, w: Float, from: Float, to: Float, col: Color) {
                val a = from.coerceIn(0f, 1f); val b = to.coerceIn(0f, 1f)
                if (b > a) drawArc(col, -90f + 360f * a, 360f * (b - a), false, Offset(c - r, c - r), Size(2 * r, 2 * r), style = Stroke(w, cap = StrokeCap.Round))
            }
            for ((r, w) in listOf(r0 to thin, r1 to thin, r2 to thick)) drawCircle(track, r, style = Stroke(w))
            if (base != null) {
                val fe = (eaten / base).toFloat(); val fm = (move / base).toFloat(); val fn = ((eaten - move) / base).toFloat()
                ring(r0, thin, 0f, fe, RingEaten)
                ring(r1, thin, fe - fm, fe, RingMove)
                ring(r2, thick, 0f, fn, if (fn > 1f) RingOver else RingNet)
            }
        }
        Column(horizontalAlignment = Alignment.CenterHorizontally) {
            val left = budget?.let { it - eaten }
            val over = left != null && left < 0
            Text(when { budget == null -> kShort(eaten); d.hideNumbers -> "${(eaten / budget * 100).roundToInt()} %"; over -> "+" + kShort(-left!!); else -> kShort(left!!) },
                style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold, color = if (over && !d.hideNumbers) RingOver else Color.Unspecified)
            if (!d.hideNumbers) Text("kcal", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

/** Kopf: „+gegessen“, Ringe, „−Bewegung“; Nährstoffe; Zeile zum Fasten bzw. zur Mahlzeit (antippen: Fasten starten/beenden). */
@Composable
internal fun DayHeadCard(d: Day, fasting: FastingView?, isToday: Boolean, macros: @Composable () -> Unit, onFastingTap: () -> Unit, onProfile: () -> Unit) {
    val accent = MaterialTheme.colorScheme.primary
    Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(20.dp), colors = CardDefaults.cardColors(containerColor = accent.copy(alpha = 0.08f)),
        border = BorderStroke(1.dp, accent.copy(alpha = 0.30f))) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Side(if (d.hideNumbers) "" else "+" + n0(d.eaten.kcal), stringResource(R.string.coach_head_eaten), RingEaten, Modifier.weight(1f))
                TripleRing(d, 132.dp)
                Side(if (d.hideNumbers || d.movement <= 0) "" else "−" + n0(d.movement), stringResource(R.string.coach_head_move), RingMove, Modifier.weight(1f))
            }
            if (!d.targets.complete) {
                Text(stringResource(R.string.coach_profile_missing), style = MaterialTheme.typography.bodySmall, textAlign = TextAlign.Center, modifier = Modifier.fillMaxWidth())
                TextButton(onClick = onProfile, modifier = Modifier.align(Alignment.CenterHorizontally)) { Text(stringResource(R.string.coach_fill_profile)) }
            }
            macros()
            if (isToday) StatusLine(fasting, onFastingTap)
        }
    }
}

@Composable
private fun Side(value: String, label: String, color: Color, modifier: Modifier) = Column(modifier, horizontalAlignment = Alignment.CenterHorizontally) {
    Text(value.ifEmpty { "–" }, color = color, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
    Text(label, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

/**
 * Fasten an: gerade Fastenzeit (bis wann, wie lange noch) oder Essenszeit (bis wann). Sonst die Mahlzeit, die gerade dran ist.
 * Das Essensfenster ergibt sich aus „Essen ab“ und dem Plan (16:8 → 8 Stunden).
 */
@Composable
private fun StatusLine(f: FastingView?, onTap: () -> Unit) {
    val hm = DateTimeFormatter.ofPattern("HH:mm")
    fun dur(min: Long) = "%d:%02d h".format(min / 60, min % 60)
    val now = LocalDateTime.now()
    var color = Color(0xFF2E9E5B)
    var icon: ImageVector = Icons.Default.Restaurant
    val text: String = if (f != null && f.settings.enabled && f.notAllowed == null) {
        val cur = f.current
        if (cur != null) {
            color = Color(0xFF7C5CD6); icon = Icons.Default.Bedtime
            val ends = Instant.parse(cur.ends.let { if (it.endsWith("Z")) it else it + "Z" }).atZone(ZoneId.systemDefault()).toLocalDateTime()
            if (!ends.isAfter(now)) stringResource(R.string.coach_line_fast_done)
            else stringResource(R.string.coach_line_fast, dur(java.time.Duration.between(now, ends).toMinutes()), ends.format(hm))
        } else {
            val from = try { LocalTime.parse(f.settings.eatFrom) } catch (_: Exception) { LocalTime.NOON }
            val start = now.toLocalDate().atTime(from)
            val end = start.plusMinutes(((24 - f.goalHours) * 60).roundToInt().toLong())
            when {
                now.isBefore(start) -> { color = Color(0xFF7C5CD6); icon = Icons.Default.Bedtime
                    stringResource(R.string.coach_line_fast, dur(java.time.Duration.between(now, start).toMinutes()), start.format(hm)) }
                now.isBefore(end) -> stringResource(R.string.coach_line_eat, dur(java.time.Duration.between(now, end).toMinutes()), end.format(hm))
                else -> { color = Color(0xFF7C5CD6); icon = Icons.Default.Bedtime; stringResource(R.string.coach_line_fast_from, end.format(hm)) }
            }
        }
    } else stringResource(R.string.coach_line_meal, mealNameOf(at.dimes.dmoffice.coach.mealNow()))
    Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(color.copy(alpha = 0.22f)).clickable(onClick = onTap).padding(horizontal = 10.dp, vertical = 7.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, contentDescription = null, tint = color, modifier = Modifier.size(16.dp))
        Spacer(Modifier.width(6.dp))
        Text(text, style = MaterialTheme.typography.bodySmall)
    }
}

@Composable
private fun mealNameOf(m: String) = stringResource(when (m) { "breakfast" -> R.string.coach_breakfast; "lunch" -> R.string.coach_lunch; "dinner" -> R.string.coach_dinner; else -> R.string.coach_snack })

/** Eine Mahlzeit als Summe: farbiges Symbol mit Ring (Anteil am Mahlzeitenziel), Summe, Zahl der Einträge, „+“ in ihrer Farbe. */
@Composable
internal fun MealSummary(meal: String, entries: List<Entry>, budget: Double?, hide: Boolean, onOpen: () -> Unit, onAdd: () -> Unit) {
    val color = mealColor(meal)
    val kcal = entries.sumOf { it.kcal }
    val goal = budget?.let { it * mealShare(meal) }?.takeIf { it > 0 }
    Row(Modifier.fillMaxWidth().height(IntrinsicSize.Min).clip(RoundedCornerShape(14.dp)).background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.55f))
        .clickable(onClick = onOpen), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.width(4.dp).fillMaxHeight().background(color))
        Row(Modifier.weight(1f).padding(horizontal = 12.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(44.dp), contentAlignment = Alignment.Center) {
                Canvas(Modifier.fillMaxSize()) {
                    val w = 3.dp.toPx()
                    drawCircle(color.copy(alpha = 0.16f), size.minDimension / 2 - w / 2)
                    drawCircle(color.copy(alpha = 0.25f), size.minDimension / 2 - w / 2, style = Stroke(w))
                    if (goal != null && kcal > 0) drawArc(color, -90f, 360f * (kcal / goal).toFloat().coerceIn(0f, 1f), false,
                        Offset(w / 2, w / 2), Size(size.width - w, size.height - w), style = Stroke(w, cap = StrokeCap.Round))
                }
                Icon(mealIcon(meal), contentDescription = null, tint = color, modifier = Modifier.size(22.dp))
            }
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(mealNameOf(meal), style = MaterialTheme.typography.titleSmall)
                    if (entries.isNotEmpty()) Icon(Icons.Default.ChevronRight, contentDescription = null, modifier = Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                val sub = listOfNotNull(
                    if (hide) null else n0(kcal) + (goal?.let { " / " + n0(it) } ?: "") + " kcal",
                    entries.size.takeIf { it > 0 }?.let { androidx.compose.ui.res.pluralStringResource(R.plurals.coach_entries, it, it) }).joinToString(" · ")
                if (sub.isNotEmpty()) Text(sub, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Box(Modifier.size(38.dp).clip(CircleShape).background(color).clickable(onClick = onAdd), contentAlignment = Alignment.Center) {
                Icon(Icons.Default.Add, contentDescription = stringResource(R.string.coach_add_to, mealNameOf(meal)), tint = Color.White)
            }
        }
    }
}

/** Wasser zum Erfassen: Menge zum Ziel, 1-l-Flaschen mit Füllstand, Knöpfe für die eigenen Gefäße, das letzte zurücknehmen. */
@Composable
internal fun WaterTile(d: Day, modifier: Modifier, onWater: (Int) -> Unit, onUndo: (Long) -> Unit) {
    val l = NumberFormat.getNumberInstance().apply { maximumFractionDigits = 2 }
    Tile(TileWater, modifier) {
        TileHead(Icons.Default.WaterDrop, stringResource(R.string.coach_water), TileWater)
        Text(l.format(d.water.ml / 1000.0) + " / " + l.format(d.water.goal / 1000.0) + " l", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        WaterBottles(d.water.ml, d.water.goal)
        Row(Modifier.padding(top = 4.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            for (ml in d.water.sizes.ifEmpty { listOf(250) }.take(4))
                Box(Modifier.weight(1f).border(1.dp, TileWater, RoundedCornerShape(8.dp)).clip(RoundedCornerShape(8.dp)).clickable { onWater(ml) }.padding(vertical = 4.dp),
                    contentAlignment = Alignment.Center) { Text("+$ml", color = TileWater, style = MaterialTheme.typography.labelMedium) }
        }
        d.water.entries.lastOrNull()?.let { last ->
            Text(stringResource(R.string.coach_undo_last), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.clickable { onUndo(last.id) }.padding(top = 2.dp))
        }
    }
}

/** Gewicht zum Erfassen: heutiger (bzw. letzter) Wert, „Eintragen“ öffnet die Eingabe. */
@Composable
internal fun WeightTile(d: Day, modifier: Modifier, onWeight: (Double) -> Unit) {
    var ask by remember { mutableStateOf(false) }
    val l = NumberFormat.getNumberInstance().apply { maximumFractionDigits = 1 }
    Tile(WeightColor, modifier) {
        TileHead(Icons.Default.MonitorWeight, stringResource(R.string.coach_weight), WeightColor)
        Text((d.weight?.let { l.format(it) } ?: "–") + " kg", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
        Spacer(Modifier.height(6.dp))
        Box(Modifier.fillMaxWidth().border(1.dp, WeightColor, RoundedCornerShape(8.dp)).clip(RoundedCornerShape(8.dp)).clickable { ask = true }.padding(vertical = 4.dp),
            contentAlignment = Alignment.Center) { Text(stringResource(R.string.coach_enter), color = WeightColor, style = MaterialTheme.typography.labelMedium) }
    }
    if (ask) WeightDialog(onDismiss = { ask = false }, onWeight = { ask = false; onWeight(it) })
}

/** Gewicht eintragen (aus der Kachel und vom Widget auf dem Startbildschirm). */
@Composable
internal fun WeightDialog(onDismiss: () -> Unit, onWeight: (Double) -> Unit) {
    var v by remember { mutableStateOf("") }
    val n = v.replace(',', '.').trim().toDoubleOrNull()
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.coach_weight)) },
        text = { NumberField(v, { v = it }, label = { Text("kg") }) },
        confirmButton = { TextButton(onClick = { onWeight(n!!) }, enabled = n != null && n in 20.0..400.0) { Text(stringResource(R.string.coach_enter)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Schritte über die volle Breite: Zahl, Überschuss, Balken (Ziel dunkel-, Überschuss hellgrün), Strecke und Ziel; [extra]: Abgleich-Knöpfe. */
@Composable
internal fun StepsTile(d: Day, extra: @Composable () -> Unit) {
    val steps = d.activity?.steps ?: d.steps.steps ?: 0
    val goal = d.steps.goal
    val l = NumberFormat.getNumberInstance().apply { maximumFractionDigits = 1 }
    Tile(RingMove, Modifier.fillMaxWidth()) {
        TileHead(Icons.AutoMirrored.Filled.DirectionsWalk, stringResource(R.string.coach_steps), RingMove,
            listOfNotNull(d.activity?.distance?.takeIf { it > 0 }?.let { l.format(it / 1000) + " km" }, stringResource(R.string.coach_steps_goal_n, n0(goal.toDouble()))).joinToString(" · "))
        Row(verticalAlignment = Alignment.Bottom) {
            Text(n0(steps.toDouble()), style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            if (goal in 1..steps) Text("  ✓ +" + n0((steps - goal).toDouble()), color = StepsExtraColor, style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(bottom = 3.dp))
        }
        StepsBar(steps, goal, Modifier.fillMaxWidth().height(7.dp))
        extra()
    }
}

/** Aktivitäten von der Uhr: Art, Dauer, Strecke, kcal - wächst mit dem Tag, darum zuletzt. */
@Composable
internal fun ActivitiesTile(d: Day, onOpen: (Long) -> Unit) {
    val color = Color(0xFFF2B53C)
    val l = NumberFormat.getNumberInstance().apply { maximumFractionDigits = 2 }
    Tile(color, Modifier.fillMaxWidth()) {
        TileHead(Icons.AutoMirrored.Filled.DirectionsRun, stringResource(R.string.coach_activities), color, stringResource(R.string.coach_from_watch))
        for (a in d.activities) Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).clickable { onOpen(a.id) }.padding(top = 4.dp, bottom = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(if (a.type.contains("cycl") || a.type.contains("bik")) Icons.AutoMirrored.Filled.DirectionsBike else if (a.type.contains("run")) Icons.AutoMirrored.Filled.DirectionsRun
                else Icons.AutoMirrored.Filled.DirectionsWalk, contentDescription = null, tint = color, modifier = Modifier.size(18.dp))
            Spacer(Modifier.width(8.dp))
            Column(Modifier.weight(1f)) {
                Text(a.name.ifBlank { a.type }, style = MaterialTheme.typography.bodyMedium, maxLines = 1)
                val min = ((a.durationS ?: 0.0) / 60).roundToInt()
                Text(listOfNotNull(a.start.takeIf { it.length >= 16 }?.substring(11, 16), if (min > 0) "%d:%02d h".format(min / 60, min % 60) else null,
                    a.distanceM?.takeIf { it > 0 }?.let { l.format(it / 1000) + " km" }).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            a.kcal?.let { Text(n0(it) + " kcal", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold) }
        }
    }
}

/**
 * Eine Mahlzeit im Einzelnen (Vollbild, in ihrer Farbe): Ring mit Summe und Rest zum Mahlzeitenziel, Nährstoffe zum Anteil am
 * Tagesziel, die Einträge (antippen: Menge ändern, ⋮: verschieben, löschen), darunter „Wie gestern“ und „Mehr hinzufügen“.
 * „Wie gestern“ zeigt erst die Einträge dieser Mahlzeit von gestern, alle ausgewählt; übernommen werden nur die ausgewählten.
 */
@Composable
internal fun MealDetail(meal: String, d: Day, onDismiss: () -> Unit, onAdd: () -> Unit, onRemove: (Long) -> Unit, onMove: (Long, String) -> Unit,
                        onAmount: (Long, Double) -> Unit, loadYesterday: suspend () -> List<Entry>, onYesterday: (List<Long>) -> Unit,
                        onTemplate: () -> Unit) {
    val color = mealColor(meal)
    val entries = d.meals[meal] ?: emptyList()
    val kcal = entries.sumOf { it.kcal }
    val share = mealShare(meal)
    val goal = d.budget?.let { it * share }?.takeIf { it > 0 }
    var editing by remember { mutableStateOf<Entry?>(null) }
    val ctx = androidx.compose.ui.platform.LocalContext.current
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    var yesterday by remember { mutableStateOf<List<Entry>?>(null) }
    var picked by remember { mutableStateOf(emptySet<Long>()) }
    val showYesterday: () -> Unit = { scope.launch {
        try { val list = loadYesterday(); picked = list.map { it.id }.toSet(); yesterday = list }
        catch (e: Exception) { android.widget.Toast.makeText(ctx, e.message ?: e.toString(), android.widget.Toast.LENGTH_SHORT).show() }
    } }
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss, properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
    androidx.compose.material3.Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
    Column(Modifier.fillMaxSize().systemBarsPadding().padding(horizontal = 14.dp, vertical = 8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            androidx.compose.material3.IconButton(onClick = onDismiss) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) }
            Text(mealNameOf(meal), color = color, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
            var menu by remember { mutableStateOf(false) }
            Box {
                androidx.compose.material3.IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
                androidx.compose.material3.DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    if (entries.isNotEmpty()) androidx.compose.material3.DropdownMenuItem(text = { Text(stringResource(R.string.coach_save_template)) }, onClick = { menu = false; onTemplate() })
                    androidx.compose.material3.DropdownMenuItem(text = { Text(stringResource(R.string.coach_like_yesterday)) }, onClick = { menu = false; showYesterday() })
                }
            }
        }
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            // Kopf: Ring, Summe zum Mahlzeitenziel, was noch geht
            Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(20.dp), colors = CardDefaults.cardColors(containerColor = color.copy(alpha = 0.14f)),
                border = BorderStroke(1.dp, color.copy(alpha = 0.4f))) {
                Row(Modifier.padding(14.dp), verticalAlignment = Alignment.CenterVertically) {
                    Box(Modifier.size(84.dp), contentAlignment = Alignment.Center) {
                        Canvas(Modifier.fillMaxSize()) {
                            val w = 7.dp.toPx()
                            drawCircle(color.copy(alpha = 0.18f), size.minDimension / 2 - w / 2)
                            drawCircle(color.copy(alpha = 0.3f), size.minDimension / 2 - w / 2, style = Stroke(w))
                            if (goal != null && kcal > 0) drawArc(color, -90f, 360f * (kcal / goal).toFloat().coerceIn(0f, 1f), false,
                                Offset(w / 2, w / 2), Size(size.width - w, size.height - w), style = Stroke(w, cap = StrokeCap.Round))
                        }
                        Icon(mealIcon(meal), contentDescription = null, tint = color, modifier = Modifier.size(30.dp))
                    }
                    Spacer(Modifier.width(14.dp))
                    Column {
                        Row(verticalAlignment = Alignment.Bottom) {
                            Text(if (d.hideNumbers) "–" else n0(kcal), style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
                            if (goal != null && !d.hideNumbers) Text("  / " + n0(goal) + " kcal", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 3.dp))
                        }
                        if (goal != null && !d.hideNumbers) Text(stringResource(if (kcal <= goal) R.string.coach_meal_left else R.string.coach_meal_over, n0(abs(goal - kcal))),
                            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        if (entries.isNotEmpty()) Box(Modifier.padding(top = 6.dp).clip(RoundedCornerShape(50)).background(color).padding(horizontal = 10.dp, vertical = 3.dp)) {
                            Text(androidx.compose.ui.res.pluralStringResource(R.plurals.coach_entries, entries.size, entries.size), color = Color.White, style = MaterialTheme.typography.labelMedium)
                        }
                    }
                }
            }
            // Nährstoffe dieser Mahlzeit zum Anteil am Tagesziel
            if (d.targets.complete && !d.hideNumbers) Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                @Composable fun M(label: Int, v: Double, g: Double?, c: Color) = Tile(c, Modifier.weight(1f)) {
                    Text(stringResource(label), color = c, style = MaterialTheme.typography.labelSmall, maxLines = 1)
                    Text(n0(v) + (g?.let { " / " + n0(it * share) } ?: "") + " g", style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold, maxLines = 1)
                    LinearProgressIndicator(progress = { if (g != null && g * share > 0) (v / (g * share)).toFloat().coerceIn(0f, 1f) else 0f }, color = c, modifier = Modifier.fillMaxWidth())
                }
                M(R.string.coach_protein, entries.sumOf { it.protein }, d.targets.protein, Color(0xFFE0675B))
                M(R.string.coach_fat, entries.sumOf { it.fat }, d.targets.fat, Color(0xFF4FB3A0))
                M(R.string.coach_carbs, entries.sumOf { it.carbs }, d.targets.carbs, Color(0xFFE0B14E))
            }
            // Die Einträge
            if (entries.isNotEmpty()) Card(Modifier.fillMaxWidth(), shape = RoundedCornerShape(16.dp)) {
                Column(Modifier.padding(horizontal = 12.dp)) {
                    entries.forEachIndexed { i, e ->
                        if (i > 0) androidx.compose.material3.HorizontalDivider()
                        Row(Modifier.fillMaxWidth().clickable { editing = e }.padding(vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                            Box(Modifier.size(38.dp).clip(RoundedCornerShape(10.dp)).background(color.copy(alpha = 0.18f)), contentAlignment = Alignment.Center) {
                                Icon(mealIcon(meal), contentDescription = null, tint = color, modifier = Modifier.size(20.dp))
                            }
                            Spacer(Modifier.width(10.dp))
                            Column(Modifier.weight(1f)) {
                                Text(e.name, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                                val sub = listOfNotNull(e.brand, e.portion, e.amount?.takeIf { e.portion?.contains(Regex("\\d\\s*(g|ml)\\b")) != true }?.let { n0(it) + " g" }).joinToString(" · ")
                                if (sub.isNotEmpty()) Text(sub, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1,
                                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                            }
                            if (!d.hideNumbers) Text(n0(e.kcal), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
                            if (!d.hideNumbers) Text(" kcal", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            var menu by remember { mutableStateOf(false) }
                            Box {
                                androidx.compose.material3.IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
                                androidx.compose.material3.DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                                    for (m in at.dimes.dmoffice.coach.MEALS.filter { it != meal })
                                        androidx.compose.material3.DropdownMenuItem(text = { Text(stringResource(R.string.coach_move_to, mealNameOf(m)), color = mealColor(m)) }, onClick = { menu = false; onMove(e.id, m) })
                                    androidx.compose.material3.DropdownMenuItem(text = { Text(stringResource(R.string.delete), color = MaterialTheme.colorScheme.error) }, onClick = { menu = false; onRemove(e.id) })
                                }
                            }
                        }
                    }
                }
            }
            if (entries.isNotEmpty()) Text(stringResource(R.string.coach_meal_hint), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            // Gleich unter den Einträgen (unten angeheftet rutschten sie auf manchen Geräten unter den Bildschirmrand)
            Row(Modifier.fillMaxWidth().padding(top = 4.dp, bottom = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = showYesterday, modifier = Modifier.weight(1f).height(46.dp), shape = CircleShape, border = BorderStroke(1.5.dp, color)) {
                    Text(stringResource(R.string.coach_like_yesterday), color = color)
                }
                androidx.compose.material3.Button(onClick = onAdd, modifier = Modifier.weight(1.4f).height(46.dp), shape = CircleShape,
                    colors = androidx.compose.material3.ButtonDefaults.buttonColors(containerColor = color, contentColor = Color.White)) {
                    Icon(Icons.Default.Add, contentDescription = null); Spacer(Modifier.width(6.dp)); Text(stringResource(R.string.coach_more_add))
                }
            }
        }
    }
    }
    }
    // „Wie gestern“: die Einträge von gestern, alle ausgewählt, abwählbar
    yesterday?.let { list ->
        AlertDialog(onDismissRequest = { yesterday = null },
            title = { Text(if (list.isEmpty()) stringResource(R.string.coach_like_yesterday) else stringResource(R.string.coach_yesterday_at, mealNameOf(meal))) },
            text = {
                if (list.isEmpty()) Text(stringResource(R.string.coach_nothing_yesterday))
                else Column(Modifier.verticalScroll(rememberScrollState())) {
                    list.forEach { e ->
                        val on = e.id in picked
                        Row(Modifier.fillMaxWidth().clickable { picked = if (on) picked - e.id else picked + e.id }.padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                            androidx.compose.material3.Checkbox(checked = on, onCheckedChange = { picked = if (it) picked + e.id else picked - e.id })
                            Column(Modifier.weight(1f)) {
                                Text(e.name, style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                                val sub = listOfNotNull(e.brand, e.portion, e.amount?.takeIf { e.portion?.contains(Regex("\\d\\s*(g|ml)\\b")) != true }?.let { n0(it) + " g" }).joinToString(" · ")
                                if (sub.isNotEmpty()) Text(sub, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1,
                                    overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis)
                            }
                            if (!d.hideNumbers) Text(n0(e.kcal) + " kcal", style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(start = 8.dp))
                        }
                    }
                }
            },
            confirmButton = {
                if (list.isEmpty()) TextButton(onClick = { yesterday = null }) { Text(stringResource(R.string.close)) }
                else TextButton(enabled = picked.isNotEmpty(), onClick = { val ids = list.map { it.id }.filter { it in picked }; yesterday = null; onYesterday(ids) }) {
                    Text(stringResource(R.string.apply))
                }
            },
            dismissButton = { if (list.isNotEmpty()) TextButton(onClick = { yesterday = null }) { Text(stringResource(R.string.cancel)) } })
    }
    // Menge eines Eintrags ändern
    editing?.let { e ->
        var v by remember(e.id) { mutableStateOf(e.amount?.let { n0(it) } ?: "") }
        val n = v.replace(',', '.').trim().toDoubleOrNull()
        AlertDialog(onDismissRequest = { editing = null }, title = { Text(e.name, maxLines = 2) },
            text = { NumberField(v, { v = it }, label = { Text("g") }) },
            confirmButton = { TextButton(onClick = { editing = null; onAmount(e.id, n!!) }, enabled = n != null && n > 0 && n <= 5000) { Text(stringResource(R.string.coach_enter)) } },
            dismissButton = { TextButton(onClick = { editing = null }) { Text(stringResource(R.string.cancel)) } })
    }
}

/** Zutaten einer Mahlzeit (beim Antippen der Summe). */
@Composable
internal fun MealDialog(meal: String, onDismiss: () -> Unit, content: @Composable () -> Unit) {
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss) {
        Box(Modifier.fillMaxWidth()) { content() }
    }
}

/**
 * Zahlenfeld, das beim Hineintippen alles markiert - gleich lostippen ersetzt den alten Wert. Der Fokus setzt die Markierung
 * einen Augenblick später, sonst setzt der Tipp selbst den Cursor wieder an seine Stelle.
 */
@Composable
internal fun NumberField(value: String, onValueChange: (String) -> Unit, modifier: Modifier = Modifier, label: (@Composable () -> Unit)? = null,
                         suffix: (@Composable () -> Unit)? = null, decimal: Boolean = true) {
    var tf by remember { mutableStateOf(androidx.compose.ui.text.input.TextFieldValue(value)) }
    val shown = if (tf.text == value) tf else androidx.compose.ui.text.input.TextFieldValue(value, androidx.compose.ui.text.TextRange(value.length))
    var focused by remember { mutableStateOf(false) }
    androidx.compose.runtime.LaunchedEffect(focused) {
        if (focused) { kotlinx.coroutines.delay(60); tf = androidx.compose.ui.text.input.TextFieldValue(value, androidx.compose.ui.text.TextRange(0, value.length)) }
    }
    OutlinedTextField(shown, { tf = it; if (it.text != value) onValueChange(it.text) }, singleLine = true, label = label, suffix = suffix,
        keyboardOptions = KeyboardOptions(keyboardType = if (decimal) KeyboardType.Decimal else KeyboardType.Number),
        modifier = modifier.onFocusChanged { focused = it.isFocused })
}
