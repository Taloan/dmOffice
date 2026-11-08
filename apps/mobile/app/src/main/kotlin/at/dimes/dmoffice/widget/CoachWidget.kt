package at.dimes.dmoffice.widget

import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import androidx.compose.runtime.Composable
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
import androidx.glance.LocalSize
import androidx.glance.action.clickable
import androidx.glance.appwidget.GlanceAppWidget
import androidx.glance.appwidget.GlanceAppWidgetReceiver
import androidx.glance.appwidget.LinearProgressIndicator
import androidx.glance.appwidget.SizeMode
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
import java.text.NumberFormat
import java.time.LocalDate
import kotlin.math.abs
import kotlin.math.roundToInt

/**
 * Coach auf dem Startbildschirm, im Stil des Coach-Tagebuchs: links der Kalorienring (übrig), daneben Gegessen,
 * Bewegung und Wasser, rechts zwei runde Knöpfe – „+“ (Eintragen, Mahlzeit nach Uhrzeit) und Barcode (gleich der
 * Scanner). Der Tag kommt vom Server und wird für unterwegs zwischengespeichert. Nur eine Zeile hoch: kleinerer Ring,
 * die Knöpfe nebeneinander.
 */
class CoachWidget : GlanceAppWidget() {
    override val sizeMode = SizeMode.Exact

    companion object {
        private val json = kotlinx.serialization.json.Json { ignoreUnknownKeys = true }
        private fun prefs(context: Context) = context.getSharedPreferences("widget_coach", Context.MODE_PRIVATE)

        /** Nach einer Änderung im Coach-Tagebuch neu zeichnen. */
        suspend fun refresh(context: Context) { try { CoachWidget().updateAll(context) } catch (_: Exception) { }; refreshLarge(context) }

        /** Zuletzt geladener Stand von heute - die Widgets beobachten ihn (ein Tap zählt so sofort mit). */
        internal val current = kotlinx.coroutines.flow.MutableStateFlow<Day?>(null)

        internal suspend fun load(context: Context): Day? = loadFresh(context).also { current.value = it }

        private suspend fun loadFresh(context: Context): Day? {
            val today = LocalDate.now().toString()
            val fresh = try { kotlinx.coroutines.withTimeoutOrNull(8000) { App.get(context).coach.day(today) } }
                // Bereichssperre: gesperrt - auch den gemerkten Stand nicht zeigen
                catch (e: at.dimes.dmoffice.api.ApiException) { if (e.code == 423) { prefs(context).edit().remove("day").apply(); return null }; null }
                catch (_: Exception) { null }
            if (fresh != null) { prefs(context).edit().putString("day", json.encodeToString(Day.serializer(), fresh)).apply(); return fresh }
            // Ohne Netz: den letzten Stand, aber nur von heute
            return try { prefs(context).getString("day", null)?.let { json.decodeFromString(Day.serializer(), it) }?.takeIf { it.date == today } } catch (_: Exception) { null }
        }

        /**
         * Drei Ringe, von außen nach innen zu lesen: gegessen (pink) minus Bewegung (grün, vom Ende des Gegessenen zurück)
         * ergibt netto (orange, dick, direkt um die Zahl). Der volle Kreis ist der Grundbedarf ohne Bewegung; netto über 100 % wird rot.
         */
        internal fun ring(context: Context, eaten: Float, move: Float, net: Float, dp: Int = 64): Bitmap {
            val px = (dp * context.resources.displayMetrics.density).roundToInt()
            val bmp = Bitmap.createBitmap(px, px, Bitmap.Config.ARGB_8888)
            val thin = px * 0.05f; val thick = px * 0.09f; val gap = px * 0.02f
            val c = px / 2f
            fun oval(radius: Float) = RectF(c - radius, c - radius, c + radius, c + radius)
            val r0 = c - thin / 2; val r1 = r0 - thin - gap; val r2 = r1 - thin / 2 - gap - thick / 2
            val paint = Paint(Paint.ANTI_ALIAS_FLAG).apply { style = Paint.Style.STROKE; strokeCap = Paint.Cap.ROUND }
            Canvas(bmp).apply {
                fun track(radius: Float, w: Float) = drawArc(oval(radius), 0f, 360f, false, paint.apply { strokeWidth = w; color = 0x38888888 })
                fun arc(radius: Float, w: Float, from: Float, to: Float, col: Int) {
                    val a = from.coerceIn(0f, 1f); val b = to.coerceIn(0f, 1f)
                    if (b > a) drawArc(oval(radius), -90f + 360f * a, 360f * (b - a), false, paint.apply { strokeWidth = w; color = col })
                }
                track(r0, thin); track(r1, thin); track(r2, thick)
                arc(r0, thin, 0f, eaten, EatenColor.toArgb())
                arc(r1, thin, eaten - move, eaten, StepsColor.toArgb())
                arc(r2, thick, 0f, net, (if (net > 1f) OverColor else NetColor).toArgb())
            }
            return bmp
        }
    }

    override suspend fun provideGlance(context: Context, id: GlanceId) {
        val day = if (App.get(context).prefs.account.value == null) null else load(context)
        val open = MainActivity.openIntent(context, null, tab = "coach")
        val add = MainActivity.coachIntent(context, "add")
        val scan = MainActivity.coachIntent(context, "scan")
        val budget = day?.budget?.takeIf { it > 0 }
        val eaten = day?.eaten?.kcal ?: 0.0
        // Grundbedarf ohne Bewegung = voller Kreis; Bewegung zählt höchstens so weit zurück, wie gegessen wurde
        val base = budget?.let { it - (day?.movement ?: 0.0) }?.takeIf { it > 0 }
        val move = minOf(day?.movement ?: 0.0, eaten)
        val ring = if (base == null) ring(context, 0f, 0f, 0f)
            else ring(context, (eaten / base).toFloat(), (move / base).toFloat(), ((eaten - move) / base).toFloat())
        provideContent {
            // Eine Zeile hoch: die beiden Knöpfe übereinander passen nicht (2 × 40 dp + Abstand + Rand)
            val flat = LocalSize.current.height < 110.dp
            val ringSize = if (flat) 56.dp else 64.dp
            GlanceTheme(colors = widgetColors(context)) {
                Row(GlanceModifier.fillMaxSize().appWidgetBackground().cornerRadius(24.dp).background(GlanceTheme.colors.widgetBackground).padding(if (flat) 8.dp else 10.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    Row(GlanceModifier.defaultWeight().fillMaxHeight().clickable(actionStartActivity(open)), verticalAlignment = Alignment.CenterVertically) {
                        Box(GlanceModifier.size(ringSize), contentAlignment = Alignment.Center) {
                            Image(ImageProvider(ring), contentDescription = null, modifier = GlanceModifier.size(ringSize))
                            CoachRingText(day, budget)
                        }
                        Spacer(GlanceModifier.width(12.dp))
                        Box(GlanceModifier.defaultWeight()) { Lines(context, day) }
                        // Abstand zu den Knöpfen - sonst stoßen Zahlen und „+“ aneinander
                        Spacer(GlanceModifier.width(12.dp))
                    }
                    val plus = @Composable { RoundButton(R.drawable.ic_restaurant, context.getString(R.string.coach_add_food), GlanceTheme.colors.primary, GlanceTheme.colors.onPrimary, add) }
                    val barcode = @Composable { RoundButton(R.drawable.ic_barcode, context.getString(R.string.coach_barcode), GlanceTheme.colors.secondaryContainer, GlanceTheme.colors.onSecondaryContainer, scan) }
                    if (flat) Row(verticalAlignment = Alignment.CenterVertically) { plus(); Spacer(GlanceModifier.width(8.dp)); barcode() }
                    else Column(horizontalAlignment = Alignment.CenterHorizontally) { plus(); Spacer(GlanceModifier.height(6.dp)); barcode() }
                }
            }
        }
    }
}

internal val CoachEatenColor = Color(0xFFC239B3)
internal val CoachStepsColor = Color(0xFF5DA84E)
internal val CoachWaterColor = Color(0xFF4F8FE0)
private val EatenColor = CoachEatenColor
private val StepsColor = CoachStepsColor
private val StepsExtraColor = Color(0xFF9BE07A)
private val NetColor = Color(0xFFE8833A)
private val WaterColor = Color(0xFF4F8FE0)
private val OverColor = Color(0xFFD13438)

private fun n(v: Double) = NumberFormat.getIntegerInstance().format(v.roundToInt())

/** Ab 1000 kurz mit einer Nachkommastelle: 1961 → „2,0k“, 10487 → „10,5k“. */
private fun k(v: Double) = if (abs(v) < 1000) n(v)
    else NumberFormat.getNumberInstance().apply { minimumFractionDigits = 1; maximumFractionDigits = 1 }.format(v / 1000) + "k"

/** Mitte des Rings: was heute noch geht (rot mit „+“, wenn drüber), ohne Grundbedarf das Gegessene, bei verborgenen Zahlen der Anteil. */
@Composable
internal fun CoachRingText(d: Day?, budget: Double?, size: Int = 13) {
    val eaten = d?.eaten?.kcal ?: 0.0
    val (value, over) = when {
        d == null -> "–" to false
        budget == null -> k(eaten) to false
        d.hideNumbers -> "${(eaten / budget * 100).roundToInt()} %" to (eaten > budget)
        eaten <= budget -> k(budget - eaten) to false
        else -> "+" + k(eaten - budget) to true
    }
    Text(value, maxLines = 1, style = TextStyle(color = if (over) ColorProvider(OverColor) else GlanceTheme.colors.onSurface, fontSize = size.sp,
        fontWeight = FontWeight.Bold, textAlign = TextAlign.Center))
}

/** Rechts drei Zeilen aus Symbol, Balken und Wert: gegessen (zum Grundbedarf), Schritte (zum Ziel, erreicht dunkelgrün), Wasser. */
@Composable
private fun Lines(context: Context, d: Day?) {
    val muted = TextStyle(color = GlanceTheme.colors.onSurfaceVariant, fontSize = 12.sp)
    if (d == null) { Text(context.getString(R.string.coach_widget_nodata), maxLines = 3, style = muted); return }
    // Über 100 % (nur Schritte): der Balken steht dann für alle Schritte, das Ziel dunkel, was darüber ist, hell
    @Composable fun Line(icon: Int, description: String, color: Color, part: Float, barColor: Color, value: String, extra: Color? = null) =
        Row(GlanceModifier.fillMaxWidth().padding(vertical = 1.dp), verticalAlignment = Alignment.CenterVertically) {
            Image(ImageProvider(icon), contentDescription = description, modifier = GlanceModifier.size(13.dp), colorFilter = ColorFilter.tint(ColorProvider(color)))
            Spacer(GlanceModifier.width(5.dp))
            // Balken über die freie Breite; über dem Ziel steht er für alle Schritte: Ziel-Anteil dunkel, der Rest (Überschuss) hell
            val over = extra != null && part > 1f
            LinearProgressIndicator(progress = if (over) 1f / part else part.coerceIn(0f, 1f), modifier = GlanceModifier.defaultWeight().height(6.dp),
                color = ColorProvider(barColor), backgroundColor = ColorProvider(if (over) extra!! else Color(0x40888888)))
            Spacer(GlanceModifier.width(6.dp))
            Text(value, maxLines = 1, modifier = GlanceModifier.width(46.dp),
                style = TextStyle(color = GlanceTheme.colors.onSurface, fontSize = 12.sp, fontWeight = FontWeight.Medium, textAlign = TextAlign.End))
        }
    Column(GlanceModifier.fillMaxWidth()) {
        val base = d.budget?.let { it - d.movement }?.takeIf { it > 0 }
        val eatenPart = if (base != null) (d.eaten.kcal / base).toFloat() else 0f
        Line(R.drawable.ic_restaurant, context.getString(R.string.coach_scale_eaten), EatenColor, eatenPart, if (eatenPart > 1f) OverColor else EatenColor,
            if (d.hideNumbers) "${(eatenPart * 100).roundToInt()} %" else k(d.eaten.kcal))
        val steps = d.activity?.steps ?: d.steps.steps
        if (steps != null) {
            val goal = d.steps.goal
            Line(R.drawable.ic_walk, context.getString(R.string.coach_steps), StepsColor, if (goal > 0) steps.toFloat() / goal else 0f,
                if (goal in 1..steps) at.dimes.dmoffice.ui.StepsDoneColor else StepsColor, k(steps.toDouble()), StepsExtraColor)
        }
        val l = NumberFormat.getNumberInstance().apply { maximumFractionDigits = 1 }
        Line(R.drawable.ic_water_drop, context.getString(R.string.coach_water), WaterColor, if (d.water.goal > 0) d.water.ml.toFloat() / d.water.goal else 0f,
            WaterColor, l.format(d.water.ml / 1000.0) + " l")
    }
}

@Composable
private fun RoundButton(icon: Int, description: String, bg: ColorProvider, fg: ColorProvider, intent: android.content.Intent) {
    Box(GlanceModifier.size(40.dp).cornerRadius(20.dp).background(bg).clickable(actionStartActivity(intent)), contentAlignment = Alignment.Center) {
        Image(ImageProvider(icon), contentDescription = description, modifier = GlanceModifier.size(22.dp), colorFilter = ColorFilter.tint(fg))
    }
}

class CoachWidgetReceiver : GlanceAppWidgetReceiver() { override val glanceAppWidget: GlanceAppWidget = CoachWidget() }
