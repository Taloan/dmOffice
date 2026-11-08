package at.dimes.dmoffice.ui

import android.graphics.Bitmap
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import at.dimes.dmoffice.R

/**
 * Farbmischer des Foto-Editors (wie im Web und in CameraImporter): je Farbband Farbton, Sättigung und Luminanz als Regler „hsl_h_red“ …
 * Gerechnet wird auf dem Server (PhotoAdjust.Mixer); hier nur Bänder, Farben der Regler und die Gewichte des Zielwerkzeugs.
 */
object ColorMix {
    val BANDS = listOf(
        Triple("red", R.string.photo_mix_red, 0f), Triple("orange", R.string.photo_mix_orange, 30f), Triple("yellow", R.string.photo_mix_yellow, 60f),
        Triple("green", R.string.photo_mix_green, 120f), Triple("aqua", R.string.photo_mix_aqua, 180f), Triple("blue", R.string.photo_mix_blue, 240f),
        Triple("purple", R.string.photo_mix_purple, 285f), Triple("magenta", R.string.photo_mix_magenta, 320f))

    fun key(part: Char, band: String) = "hsl_${part}_$band"

    fun color(deg: Float, light: Float = 0.52f) = Color.hsl(((deg % 360f) + 360f) % 360f, 0.75f, light)

    /** Spur: Farbton zeigt die Richtung, Sättigung von Grau zur Farbe, Luminanz von Dunkel über die Farbe zu Hell. */
    fun track(part: Char, center: Float): Brush = when (part) {
        'h' -> Brush.horizontalGradient(listOf(-50f, -25f, 0f, 25f, 50f).map { color(center + it) })
        's' -> Brush.horizontalGradient(listOf(Color(0xFF808080), color(center)))
        else -> Brush.horizontalGradient(listOf(Color(0xFF1A1A1A), color(center), Color(0xFFF0F0F0)))
    }

    /** Anteil jedes Bandes an der Farbe um (x, y) (Mittel über 5 × 5 Pixel); leer bei Grau. */
    fun weightsAt(bmp: Bitmap, x: Int, y: Int): Map<String, Float> {
        var r = 0f; var g = 0f; var b = 0f; var n = 0
        for (yy in (y - 2).coerceAtLeast(0)..(y + 2).coerceAtMost(bmp.height - 1))
            for (xx in (x - 2).coerceAtLeast(0)..(x + 2).coerceAtMost(bmp.width - 1)) {
                val c = bmp.getPixel(xx, yy); r += (c shr 16 and 0xFF); g += (c shr 8 and 0xFF); b += (c and 0xFF); n++
            }
        if (n == 0) return emptyMap()
        val hsv = FloatArray(3)
        android.graphics.Color.RGBToHSV((r / n).toInt(), (g / n).toInt(), (b / n).toInt(), hsv)
        if (hsv[1] < 0.05f) return emptyMap()
        return BANDS.associate { (band, _, c) ->
            val d = kotlin.math.abs(((hsv[0] - c + 180f) % 360f + 360f) % 360f - 180f)
            val w = (1f - d / 40f).coerceIn(0f, 1f)
            band to w * w * (3f - 2f * w)
        }.filterValues { it > 0f }
    }
}
