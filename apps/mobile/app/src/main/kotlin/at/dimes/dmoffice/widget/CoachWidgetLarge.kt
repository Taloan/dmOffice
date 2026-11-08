package at.dimes.dmoffice.widget

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Path
import android.graphics.RectF
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.glance.ColorFilter
import androidx.glance.GlanceId
import androidx.glance.GlanceModifier
import androidx.glance.GlanceTheme
import androidx.glance.Image
import androidx.glance.ImageProvider
import androidx.compose.ui.unit.DpSize
import androidx.glance.LocalSize
import androidx.glance.Visibility
import androidx.glance.visibility
import androidx.glance.action.Action
import androidx.glance.action.ActionParameters
import androidx.glance.action.actionParametersOf
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.LinearProgressIndicator
import androidx.glance.appwidget.SizeMode
import androidx.glance.appwidget.action.ActionCallback
import androidx.glance.appwidget.action.actionRunCallback
import androidx.glance.appwidget.action.actionStartActivity
import androidx.glance.appwidget.appWidgetBackground
import androidx.glance.appwidget.cornerRadius
import androidx.glance.appwidget.provideContent
import androidx.glance.appwidget.updateAll
import androidx.glance.background
import androidx.glance.layout.Alignment
import androidx.glance.layout.Box
import androidx.glance.layout.Column
import androidx.glance.layout.Row
import androidx.glance.layout.Spacer
import androidx.glance.layout.fillMaxHeight
import androidx.glance.layout.fillMaxSize
import androidx.glance.layout.fillMaxWidth
import androidx.glance.layout.height
import androidx.glance.layout.padding
import androidx.glance.layout.size
import androidx.glance.layout.width
import androidx.glance.text.FontWeight
import androidx.glance.text.Text
import androidx.glance.text.TextAlign
import androidx.glance.text.TextStyle
import androidx.glance.unit.ColorProvider
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import at.dimes.dmoffice.coach.Day
import at.dimes.dmoffice.coach.mealNow
import java.text.NumberFormat
import java.time.LocalDate
import kotlin.math.roundToInt

/**
 * Coach groß (ab 4 × 3 Feldern), aufgebaut wie der Kopf der Coach-Übersicht: links der Dreifach-Ring (kcal übrig), rechts
 * aufgenommen, Bewegung, Eiweiß, Fett, Kohlenhydrate. Darunter Kacheln in fester Reihenfolge - Wasser (antippen: das erste
 * eigene Gefäß eintragen) und Gewicht (antippen: eintragen) stehen immer an derselben Stelle, dahinter kommen mit der Größe
 * Schritte, Ruhepuls (Verlauf), Body Battery und Schlaf dazu. Unten die Leiste der Mahlzeit, die gerade dran ist (Farbe und
 * Symbol wie in der Übersicht) mit „+“, Foto und Barcode. Die Kacheln teilen sich die übrige Höhe - kein Leerraum.
 * Gerechnet wird mit der Schriftgröße des Handys, damit nichts abgeschnitten wird.
 */
class CoachWidgetLarge : GlanceAppWidget() {
    // Nicht SizeMode.Exact: Das rechnet mit der Groesse, die der Launcher meldet. Ist sie falsch, wird das Widget zu breit und zu niedrig aufgebaut.
    // Ab Android 12 waehlt das System hier nach dem tatsaechlichen Platz. Die Hoehen sind genau die Stufen, ab denen eine weitere Kachelreihe passt.
    // Wichtig: Alle Varianten muessen denselben Aufbau haben, Unterschiede nur per Sichtbarkeit. Beim Umschalten baut Android die vorhandene Ansicht um,
    // statt sie neu zu erzeugen; bei unterschiedlichem Aufbau stimmen danach die Hoehen nicht mehr (Kacheln und Mahlzeitleiste verschwinden).
    override val sizeMode: SizeMode
        get() {
            val m = CoachLargeMetrics(android.content.res.Resources.getSystem().configuration.fontScale)
            // Höchstens 16 Varianten (Grenze von Android): 2 Breiten (Ring kleiner oder voll) x 6 Höhen
            return SizeMode.Responsive(listOf(200f, 300f).flatMap { w -> m.heights().map { h -> DpSize(w.dp, h.dp) } }.toSet())
        }

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val first = if (App.get(context).prefs.account.value == null) null else CoachWidget.load(context)
        val open = MainActivity.openIntent(context, null, tab = "coach")
        val meal = mealNow()
        val m = CoachLargeMetrics(context.resources.configuration.fontScale)
        val pad = m.pad; val gap = m.gap; val barH = m.barH; val headFull = m.headFull; val headCompact = m.headCompact; val tileH = m.tileH
        provideContent {
            // Der Stand als beobachteter Zustand: ein Tap auf Wasser zählt sofort mit, danach kommt der Stand vom Server -
            // ohne dass das Widget neu aufgebaut werden muss (das täte es sonst erst, wenn seine Sitzung endet)
            val day by CoachWidget.current.collectAsState(first)
            val budget = day?.budget?.takeIf { it > 0 }
            val eaten = day?.eaten?.kcal ?: 0.0
            val base = budget?.let { it - (day?.movement ?: 0.0) }?.takeIf { it > 0 }
            val move = minOf(day?.movement ?: 0.0, eaten)
            fun ring(dp: Int) = if (base == null) CoachWidget.ring(context, 0f, 0f, 0f, dp)
                else CoachWidget.ring(context, (eaten / base).toFloat(), (move / base).toFloat(), ((eaten - move) / base).toFloat(), dp)
            val glass = day?.water?.sizes?.firstOrNull() ?: 250
            val size = LocalSize.current
            val w = size.width.value; val h = size.height.value
            val free = h - 2 * pad - barH - 2 * gap
            val compact = free - headFull < tileH
            val headH = if (compact) headCompact else headFull
            val ringDp = minOf(headH, w * 0.38f, 112f).roundToInt().coerceAtLeast(56)
            // Sehr niedrig (der Launcher lässt das zu): erst die Kacheln, dann auch die Mahlzeitleiste ausblenden
            val rows = ((free - headH + 6f) / (tileH + 6f)).toInt().coerceIn(0, 3)
            val bar = if (h >= m.withBar()) Visibility.Visible else Visibility.Gone
            val tiles = tilesFor(context, day, glass)
            GlanceTheme(colors = widgetColors(context)) {
                Column(GlanceModifier.fillMaxSize().appWidgetBackground().cornerRadius(24.dp).background(GlanceTheme.colors.widgetBackground).padding(pad.dp)) {
                    Row(GlanceModifier.fillMaxWidth().clickable(actionStartActivity(open)), verticalAlignment = Alignment.CenterVertically) {
                        Box(GlanceModifier.size(ringDp.dp), contentAlignment = Alignment.Center) {
                            Image(ImageProvider(ring(ringDp)), contentDescription = null, modifier = GlanceModifier.size(ringDp.dp))
                            RingCenter(context, day, budget, ringDp)
                        }
                        Spacer(GlanceModifier.width(10.dp))
                        Box(GlanceModifier.defaultWeight()) { HeadText(context, day, compact) }
                    }
                    Spacer(GlanceModifier.height(gap.dp))
                    Column(GlanceModifier.fillMaxWidth().defaultWeight()) {
                        tiles.chunked(2).forEachIndexed { i, rowTiles ->
                            val shown = if (i < rows) Visibility.Visible else Visibility.Gone
                            if (i > 0) Spacer(GlanceModifier.height(6.dp).visibility(shown))
                            Row(GlanceModifier.fillMaxWidth().defaultWeight().visibility(shown)) {
                                rowTiles.forEachIndexed { j, t ->
                                    if (j > 0) Spacer(GlanceModifier.width(6.dp))
                                    Box(GlanceModifier.defaultWeight().fillMaxHeight()) { t() }
                                }
                                // Unvollständige Reihe: Platz frei lassen, damit die Kachel ihre Breite behält
                                repeat(2 - rowTiles.size) { Spacer(GlanceModifier.width(6.dp)); Box(GlanceModifier.defaultWeight()) { } }
                            }
                        }
                    }
                    Spacer(GlanceModifier.height(gap.dp).visibility(bar))
                    MealBar(context, meal, barH, bar)
                }
            }
        }
    }
}

/** Platzbedarf in dp: Schrift wächst mit der Schriftgröße des Handys (Zeile ≈ 1,4 × Schriftgröße, eher zu viel als zu wenig). */
private class CoachLargeMetrics(fs: Float) {
    private fun line(sp: Float, fs: Float) = sp * 1.4f * fs
    val pad = 12f
    val gap = 8f
    val barH = maxOf(44f, line(12f, fs) + 18f)
    // Kopf: zwei Zeilen und drei Nährstoffe mit Balken; zu niedrig: die Nährstoffe in einer Zeile
    val headFull = 2 * line(12f, fs) + 3 * (line(11f, fs) + 7f) + 4 * 4f
    val headCompact = 3 * line(12f, fs) + 2 * 4f + 7f
    val tileH = 12f + line(11f, fs) + line(15f, fs) + 4f

    /** Ab dieser Höhe passt unter den kurzen Kopf die Mahlzeitleiste. */
    fun withBar() = 2 * pad + headCompact + gap + barH

    /**
     * Höhen, ab denen sich der Aufbau ändert: nur kurzer Kopf, dazu die Mahlzeitleiste, dazu eine Reihe, voller Kopf mit einer bis drei Reihen.
     * 1 dp Reserve gegen Rundung.
     */
    fun heights(): List<Float> {
        val base = 2 * pad + barH + 2 * gap
        return listOf(2 * pad + headCompact, withBar(), base + headCompact + tileH, base + headFull + tileH, base + headFull + 2 * tileH + 6f,
            base + headFull + 3 * tileH + 12f).map { it + 1f }
    }
}

/** Mitte des Rings: was heute noch geht (rot mit „+“, wenn drüber), darunter „kcal übrig“, wenn Platz ist. */
@Composable
private fun RingCenter(context: Context, d: Day?, budget: Double?, ringDp: Int) {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        CoachRingText(d, budget, (ringDp / 6).coerceIn(13, 18))
        if (d != null && !d.hideNumbers && budget != null && d.eaten.kcal <= budget)
            Text(context.getString(R.string.widget_coach_left), maxLines = 1, style = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 10.sp),
                modifier = GlanceModifier.visibility(if (ringDp >= 80) Visibility.Visible else Visibility.Gone))
    }
}

/** Wie in der Übersicht: aufgenommen (pink), Bewegung (grün, „–“ ohne), dann Eiweiß, Fett, Kohlenhydrate zum Ziel. */
@Composable
private fun HeadText(context: Context, d: Day?, compact: Boolean) {
    val muted = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp)
    if (d == null) { Text(context.getString(R.string.coach_widget_nodata), maxLines = 3, style = muted); return }
    val value = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 12.sp, fontWeight = FontWeight.Medium, textAlign = TextAlign.End)
    @Composable fun Line(label: String, v: String, color: ColorProvider) = Row(GlanceModifier.fillMaxWidth()) {
        Text(label, maxLines = 1, style = muted, modifier = GlanceModifier.defaultWeight())
        Text(v, maxLines = 1, style = value.copy(color = color))
    }
    // Je Nährstoff eine eigene Spalte: Glance nimmt höchstens 10 Elemente je Column, der Rest fiele still weg
    @Composable fun Macro(label: Int, v: Double, goal: Double?, color: Color) = Column(GlanceModifier.fillMaxWidth().padding(top = 4.dp).visibility(if (compact) Visibility.Gone else Visibility.Visible)) {
        Row(GlanceModifier.fillMaxWidth()) {
            Text(context.getString(label), maxLines = 1, style = muted.copy(fontSize = 11.sp), modifier = GlanceModifier.defaultWeight())
            if (!d.hideNumbers) Text(whole(v) + (goal?.let { " / " + whole(it) } ?: "") + " g", maxLines = 1, style = muted.copy(fontSize = 11.sp))
        }
        Spacer(GlanceModifier.height(2.dp))
        LinearProgressIndicator(progress = if (goal != null && goal > 0) (v / goal).toFloat().coerceIn(0f, 1f) else 0f,
            modifier = GlanceModifier.fillMaxWidth().height(4.dp), color = ColorProvider(color), backgroundColor = ColorProvider(Color(0x40888888)))
    }
    Column(GlanceModifier.fillMaxWidth()) {
        Line(context.getString(R.string.coach_head_eaten), if (d.hideNumbers) "" else "+" + whole(d.eaten.kcal), ColorProvider(CoachEatenColor))
        Spacer(GlanceModifier.height(4.dp))
        Line(context.getString(R.string.coach_head_move), if (d.hideNumbers || d.movement <= 0) "–" else "−" + whole(d.movement), ColorProvider(CoachStepsColor))
        // Zu niedrig: die drei Nährstoffe in einer Zeile, darunter ein gemeinsamer Balken (Mittel der drei)
        Column(GlanceModifier.fillMaxWidth().visibility(if (compact) Visibility.Visible else Visibility.Gone)) {
            Spacer(GlanceModifier.height(4.dp))
            Text(if (d.hideNumbers) "" else context.getString(R.string.widget_coach_macros_short, whole(d.eaten.protein), whole(d.eaten.fat), whole(d.eaten.carbs)),
                maxLines = 1, style = muted.copy(fontSize = 11.sp))
            val parts = listOf(d.eaten.protein to d.targets.protein, d.eaten.fat to d.targets.fat, d.eaten.carbs to d.targets.carbs)
            val avg = parts.mapNotNull { (v, g) -> g?.takeIf { it > 0 }?.let { (v / it).coerceIn(0.0, 1.0) } }.takeIf { it.isNotEmpty() }?.average()?.toFloat() ?: 0f
            Spacer(GlanceModifier.height(3.dp))
            LinearProgressIndicator(progress = avg, modifier = GlanceModifier.fillMaxWidth().height(4.dp), color = ColorProvider(Color(0xFFE0B14E)),
                backgroundColor = ColorProvider(Color(0x40888888)))
        }
        Macro(R.string.coach_protein, d.eaten.protein, d.targets.protein, Color(0xFFE0675B))
        Macro(R.string.coach_fat, d.eaten.fat, d.targets.fat, Color(0xFF4FB3A0))
        Macro(R.string.coach_carbs, d.eaten.carbs, d.targets.carbs, Color(0xFFE0B14E))
    }
}

/**
 * Die Kacheln in fester Reihenfolge: Wasser, Gewicht (beide zum Eintragen, immer vorn), Schritte, Ruhepuls, Body Battery,
 * Schlaf - Werte von der Uhr nur, wenn es sie gibt. Welche davon passen, entscheidet die Sichtbarkeit der Reihe.
 */
private fun tilesFor(context: Context, d: Day?, glass: Int): List<@Composable () -> Unit> {
    if (d == null) return emptyList()
    val l = NumberFormat.getNumberInstance().apply { maximumFractionDigits = 1 }
    val list = mutableListOf<@Composable () -> Unit>()
    list += {
        Tile(R.drawable.ic_water_drop, CoachWaterColor, context.getString(R.string.coach_water) + " · +$glass",
            l.format(d.water.ml / 1000.0) + " / " + l.format(d.water.goal / 1000.0) + " l", R.drawable.ic_add,
            actionRunCallback<CoachWaterAction>(actionParametersOf(CoachWaterAction.ml to glass))) {
            Bar(if (d.water.goal > 0) d.water.ml.toFloat() / d.water.goal else 0f, CoachWaterColor)
        }
    }
    list += {
        Tile(R.drawable.ic_coach_weight, WeightColor, context.getString(R.string.coach_weight), d.weight?.let { l.format(it) + " kg" } ?: "–",
            R.drawable.ic_coach_edit, actionStartActivity(MainActivity.coachIntent(context, "weight"))) { }
    }
    val steps = d.activity?.steps ?: d.steps.steps
    list += {
        val done = steps != null && d.steps.goal in 1..steps
        Tile(R.drawable.ic_walk, CoachStepsColor, context.getString(R.string.coach_steps),
            (steps?.let { whole(it.toDouble()) } ?: "–") + (if (d.steps.goal > 0) " / " + whole(d.steps.goal.toDouble()) else ""), null, null) {
            Bar(if (steps != null && d.steps.goal > 0) steps.toFloat() / d.steps.goal else 0f, if (done) at.dimes.dmoffice.ui.StepsDoneColor else CoachStepsColor)
        }
    }
    d.activity?.restingHr?.let { hr ->
        val spark = sparkline(context, d.trend.map { it.restingHr?.toDouble() }, HeartColor)
        list += {
            Tile(R.drawable.ic_coach_heart, HeartColor, context.getString(R.string.coach_resting_hr) + " $hr" + (d.avg7.restingHr?.let { " · Ø " + whole(it) } ?: ""), null, null, null) {
                Image(ImageProvider(spark), contentDescription = null, modifier = GlanceModifier.fillMaxWidth().height(18.dp))
            }
        }
    }
    if (d.extra.bodyBatteryLow != null || d.extra.bodyBatteryHigh != null) {
        val lo = d.extra.bodyBatteryLow; val hi = d.extra.bodyBatteryHigh
        val bar = range(context, (lo ?: 0.0) / 100, (hi ?: 100.0) / 100, BatteryColor)
        list += {
            Tile(R.drawable.ic_coach_battery, BatteryColor, "Body Battery ${lo?.let { whole(it) } ?: "–"} → ${hi?.let { whole(it) } ?: "–"}", null, null, null) {
                Image(ImageProvider(bar), contentDescription = null, modifier = GlanceModifier.fillMaxWidth().height(14.dp))
            }
        }
    }
    d.activity?.sleepMin?.let { min ->
        list += {
            Tile(R.drawable.ic_coach_sleep, SleepColor, context.getString(R.string.widget_coach_sleep) + (d.extra.hrv?.let { " · HRV " + whole(it) } ?: ""),
                "%d:%02d h".format(min / 60, min % 60), null, null) { }
        }
    }
    return list
}

/** Kachel: Symbol und Bezeichnung, Wert, darunter [extra] (Balken, Verlauf); mit [action] zum Antippen (rechts oben das Zeichen). */
@Composable
private fun Tile(icon: Int, color: Color, label: String, value: String?, actionIcon: Int?, action: Action?, extra: @Composable () -> Unit) {
    val bg = if (action != null) GlanceTheme.colors.primaryContainer else GlanceTheme.colors.secondaryContainer
    val fg = if (action != null) GlanceTheme.colors.onPrimaryContainer else GlanceTheme.colors.onSecondaryContainer
    var m = GlanceModifier.fillMaxSize().cornerRadius(12.dp).background(bg)
    if (action != null) m = m.clickable(action)
    Column(m.padding(horizontal = 9.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Row(GlanceModifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Image(ImageProvider(icon), contentDescription = null, modifier = GlanceModifier.size(12.dp), colorFilter = ColorFilter.tint(ColorProvider(color)))
            Spacer(GlanceModifier.width(4.dp))
            Text(label, maxLines = 1, style = TextStyle(color = fg, fontSize = 11.sp), modifier = GlanceModifier.defaultWeight())
            if (actionIcon != null) Box(GlanceModifier.size(22.dp).cornerRadius(11.dp).background(GlanceTheme.colors.primary), contentAlignment = Alignment.Center) {
                Image(ImageProvider(actionIcon), contentDescription = label, modifier = GlanceModifier.size(14.dp), colorFilter = ColorFilter.tint(GlanceTheme.colors.onPrimary))
            }
        }
        if (value != null) Text(value, maxLines = 1, style = TextStyle(color = fg, fontSize = 15.sp, fontWeight = FontWeight.Medium))
        Spacer(GlanceModifier.height(2.dp))
        extra()
    }
}

@Composable
private fun Bar(part: Float, color: Color) =
    LinearProgressIndicator(progress = part.coerceIn(0f, 1f), modifier = GlanceModifier.fillMaxWidth().height(4.dp), color = ColorProvider(color),
        backgroundColor = ColorProvider(Color(0x40888888)))

/**
 * Unten: Leiste der Mahlzeit, die nach der Uhrzeit dran ist - Rahmen, Symbol und Name in ihrer Farbe wie in der Übersicht,
 * darin „+“ (Eintragen für diese Mahlzeit), Foto und Barcode.
 */
@Composable
private fun MealBar(context: Context, meal: String, h: Float, shown: Visibility) {
    val color = at.dimes.dmoffice.ui.mealColor(meal)
    val icon = when (meal) { "breakfast" -> R.drawable.ic_meal_breakfast; "snack" -> R.drawable.ic_meal_snack; else -> R.drawable.ic_restaurant }
    val name = context.getString(when (meal) { "breakfast" -> R.string.coach_breakfast; "lunch" -> R.string.coach_lunch; "dinner" -> R.string.coach_dinner; else -> R.string.coach_snack })
    val btnH = (h - 12f).dp
    @Composable fun B(ic: Int, desc: String, bg: ColorProvider, fg: ColorProvider, action: String) {
        Spacer(GlanceModifier.width(6.dp))
        Box(GlanceModifier.width(48.dp).height(btnH).cornerRadius(10.dp).background(bg).clickable(actionStartActivity(MainActivity.coachIntent(context, action))),
            contentAlignment = Alignment.Center) {
            Image(ImageProvider(ic), contentDescription = desc, modifier = GlanceModifier.size(20.dp), colorFilter = ColorFilter.tint(fg))
        }
    }
    // Rahmen: außen die Farbe der Mahlzeit, innen der Hintergrund mit einem Hauch davon (Glance kennt keinen Rand)
    Box(GlanceModifier.fillMaxWidth().height(h.dp).cornerRadius(14.dp).background(ColorProvider(color)).padding(2.dp).visibility(shown)) {
        Box(GlanceModifier.fillMaxSize().cornerRadius(12.dp).background(GlanceTheme.colors.widgetBackground)) {
            Row(GlanceModifier.fillMaxSize().cornerRadius(12.dp).background(ColorProvider(color.copy(alpha = 0.16f))).padding(start = 10.dp, end = 4.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Image(ImageProvider(icon), contentDescription = null, modifier = GlanceModifier.size(20.dp), colorFilter = ColorFilter.tint(ColorProvider(color)))
                Spacer(GlanceModifier.width(6.dp))
                Text(name, maxLines = 1, style = TextStyle(color = ColorProvider(color), fontSize = 12.sp, fontWeight = FontWeight.Medium), modifier = GlanceModifier.defaultWeight())
                B(R.drawable.ic_add, context.getString(R.string.coach_add_food), ColorProvider(color), ColorProvider(Color(0xFF1E1530)), "add")
                B(R.drawable.ic_journal_photo, context.getString(R.string.widget_coach_photo), GlanceTheme.colors.secondaryContainer, GlanceTheme.colors.onSecondaryContainer, "photo")
                B(R.drawable.ic_barcode, context.getString(R.string.coach_barcode), GlanceTheme.colors.secondaryContainer, GlanceTheme.colors.onSecondaryContainer, "scan")
            }
        }
    }
}

private val WeightColor = Color(0xFFB57BD0)
private val HeartColor = Color(0xFFE0675B)
private val BatteryColor = Color(0xFF378ADD)
private val SleepColor = Color(0xFFA98BE8)

/** Verlauf (14 Tage) als Linie; fehlende Tage überspringen, der letzte Wert als Punkt. */
private fun sparkline(context: Context, values: List<Double?>, color: Color, wDp: Int = 140, hDp: Int = 18): Bitmap {
    val d = context.resources.displayMetrics.density
    val w = (wDp * d).roundToInt(); val h = (hDp * d).roundToInt()
    val bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
    val pts = values.withIndex().filter { it.value != null }
    if (pts.size < 2) return bmp
    val lo = pts.minOf { it.value!! }; val hi = pts.maxOf { it.value!! }
    val span = (hi - lo).takeIf { it > 0 } ?: 1.0
    val stroke = 2 * d
    fun x(i: Int) = stroke + (w - 2 * stroke) * i / (values.size - 1).coerceAtLeast(1)
    fun y(v: Double) = (h - stroke) - ((v - lo) / span * (h - 2 * stroke)).toFloat()
    val path = Path()
    pts.forEachIndexed { n, p -> if (n == 0) path.moveTo(x(p.index), y(p.value!!)) else path.lineTo(x(p.index), y(p.value!!)) }
    val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeWidth = stroke; this.color = color.toArgb(); strokeJoin = Paint.Join.ROUND; strokeCap = Paint.Cap.ROUND }
    Canvas(bmp).apply {
        drawPath(path, paint)
        val last = pts.last()
        drawCircle(x(last.index), y(last.value!!), stroke * 1.4f, paint.apply { style = Paint.Style.FILL })
    }
    return bmp
}

/** Spanne (Body Battery: tiefster bis höchster Stand) auf einer Leiste von 0 bis 100. */
private fun range(context: Context, from: Double, to: Double, color: Color, wDp: Int = 140, hDp: Int = 14): Bitmap {
    val d = context.resources.displayMetrics.density
    val w = (wDp * d).roundToInt(); val h = (hDp * d).roundToInt()
    val bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
    val bh = 6 * d; val top = (h - bh) / 2
    val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    Canvas(bmp).apply {
        drawRoundRect(RectF(0f, top, w.toFloat(), top + bh), bh / 2, bh / 2, paint.apply { this.color = 0x40888888 })
        val a = (from.coerceIn(0.0, 1.0) * w).toFloat(); val b = (to.coerceIn(0.0, 1.0) * w).toFloat().coerceAtLeast(a + bh)
        drawRoundRect(RectF(a, top, b, top + bh), bh / 2, bh / 2, paint.apply { this.color = color.toArgb() })
    }
    return bmp
}

private fun whole(v: Double) = NumberFormat.getIntegerInstance().format(v.roundToInt())

/** „+ Wasser“ im Widget: gleich eintragen (das erste eigene Gefäß), dann beide Coach-Widgets neu zeichnen. */
class CoachWaterAction : ActionCallback {
    companion object { val ml = ActionParameters.Key<Int>("ml") }
    override suspend fun onAction(context: Context, glanceId: GlanceId, parameters: ActionParameters) {
        val amount = parameters[ml] ?: 250
        // Gleich mitzählen (der Server bestätigt danach mit dem echten Stand)
        CoachWidget.current.value?.let { d -> CoachWidget.current.value = d.copy(water = d.water.copy(ml = d.water.ml + amount)) }
        // Scheitert es, sagen (sonst sieht es aus wie angenommen) - und ins Log, damit sich die Ursache finden lässt
        try { App.get(context).coach.water(LocalDate.now().toString(), amount) }
        catch (e: Exception) {
            android.util.Log.w("dmOffice", "Wasser aus dem Widget nicht eingetragen", e)
            val text = context.getString(R.string.widget_coach_water_failed, e.message ?: e.javaClass.simpleName)
            android.os.Handler(android.os.Looper.getMainLooper()).post { android.widget.Toast.makeText(context, text, android.widget.Toast.LENGTH_LONG).show() }
        }
        // Echten Stand holen (macht auch ein fehlgeschlagenes Mitzählen rückgängig); laufende Widgets zeichnen sich damit selbst neu
        CoachWidget.load(context)
        CoachWidget.refresh(context)
    }
}

class CoachWidgetLargeReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget: GlanceAppWidget = CoachWidgetLarge() }

/** Für CoachWidget.refresh: auch das große Widget neu zeichnen. */
internal suspend fun refreshLarge(context: Context) { try { CoachWidgetLarge().updateAll(context) } catch (_: Exception) { } }
