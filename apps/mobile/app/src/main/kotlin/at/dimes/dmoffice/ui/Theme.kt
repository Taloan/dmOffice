package at.dimes.dmoffice.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.ColorScheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.platform.LocalContext
import at.dimes.dmoffice.App
import kotlin.math.roundToInt

const val LAVA = 0xFFE2412BL
const val VIOLETT = 0xFF7B1DD3L

/** Vorgabe-Akzent von dmOffice: Violett (Lava war als Vorgabe zu grell), wie in der Weboberflaeche. */
const val DEFAULT_ACCENT = VIOLETT

/** Akzentfarben zur Wahl, gleiche Namen wie im Web; die Vorgabe steht vorne. */
val ACCENTS: List<Pair<String, Long>> = listOf(
    "Violett" to VIOLETT, "Lava" to LAVA, "Petrol" to 0xFF0B8A96L, "Safran" to 0xFFE08A00L, "Jade" to 0xFF0F9D6EL,
    "Himbeere" to 0xFFD1245EL, "Mint" to 0xFF13B8A2L, "Blau" to 0xFF0F6CBDL, "Grau" to 0xFF5B5B5BL,
)

/**
 * Leichte Farbakzente fuer haeufige Aktionen, wie in der Weboberflaeche (Menueband). Bereichssymbole
 * (Leiste unten, "Mehr") bleiben einheitlich in der Akzentfarbe.
 */
object ActionColors {
    val Archive = Color(0xFF1F9E77)
    val Junk = Color(0xFFD13438)       // auch Nachverfolgen/Markieren
    val Move = Color(0xFFD69A1E)
    val Reply = Color(0xFF8B5CF6)
    val Forward = Color(0xFF2F7FD9)    // auch Gelesen/Ungelesen
    val Schedule = Color(0xFF0E9AA7)   // Wiedervorlage, Termin
    val Categorize = Color(0xFFE2782E)
}

/** Mischt c mit w (Weiss oder Schwarz) im Anteil t. */
private fun mix(c: Color, w: Color, t: Float) = Color(c.red + (w.red - c.red) * t, c.green + (w.green - c.green) * t, c.blue + (w.blue - c.blue) * t, 1f)

/** Schrift auf einer Flaeche dieser Farbe: Weiss, ausser die Farbe ist sehr hell. */
private fun on(c: Color) = if (c.luminance() > 0.45f) Color(0xFF1A1A1A) else Color.White

/**
 * Grau im Dunkelmodus mit leichtem Blaustich (Anthrazit), dieselbe Formel wie anthracite() im Web:
 * R = g * 0.93, G = g * 0.95, B = g * 1.03 + 5.
 */
fun anthracite(g: Int): Color {
    fun t(x: Double) = x.roundToInt().coerceIn(0, 255)
    return Color(t(g * 0.93), t(g * 0.95), t(g * 1.03 + 5))
}

fun lightScheme(accent: Color): ColorScheme {
    val container = mix(accent, Color.White, 0.85f)
    return lightColorScheme(
        primary = accent, onPrimary = on(accent),
        primaryContainer = container, onPrimaryContainer = mix(accent, Color.Black, 0.55f),
        secondary = mix(accent, Color(0xFF5B5B5B), 0.55f), onSecondary = Color.White,
        secondaryContainer = mix(accent, Color.White, 0.9f), onSecondaryContainer = mix(accent, Color.Black, 0.6f),
        tertiary = mix(accent, Color(0xFF0B8A96), 0.5f),
        background = Color(0xFFFAFAFA), surface = Color(0xFFFAFAFA),
        surfaceContainerLowest = Color.White, surfaceContainerLow = Color(0xFFF5F5F5), surfaceContainer = Color(0xFFF0F0F0),
        surfaceContainerHigh = Color(0xFFEAEAEA), surfaceContainerHighest = Color(0xFFE3E3E3), surfaceVariant = Color(0xFFE6E6E6),
        outline = Color(0xFF8A8A8A), outlineVariant = Color(0xFFD1D1D1),
    )
}

fun darkScheme(accent: Color): ColorScheme {
    // Auf Anthrazit leuchtet ein etwas hellerer Ton besser und bleibt lesbar
    val primary = mix(accent, Color.White, 0.18f)
    return darkColorScheme(
        primary = primary, onPrimary = on(primary),
        primaryContainer = mix(accent, anthracite(0x1F), 0.6f), onPrimaryContainer = mix(accent, Color.White, 0.7f),
        secondary = mix(accent, Color(0xFFB0B4C0), 0.6f), onSecondary = anthracite(0x1A),
        secondaryContainer = mix(accent, anthracite(0x2C), 0.78f), onSecondaryContainer = mix(accent, Color.White, 0.8f),
        tertiary = mix(accent, Color(0xFF4FD1DC), 0.5f),
        background = anthracite(0x1C), onBackground = Color(0xFFE6E7EC),
        surface = anthracite(0x1C), onSurface = Color(0xFFE6E7EC), onSurfaceVariant = Color(0xFFB4B7C2),
        surfaceContainerLowest = anthracite(0x14), surfaceContainerLow = anthracite(0x20), surfaceContainer = anthracite(0x25),
        surfaceContainerHigh = anthracite(0x2D), surfaceContainerHighest = anthracite(0x37), surfaceVariant = anthracite(0x3A),
        outline = anthracite(0x8A), outlineVariant = anthracite(0x44),
        inverseSurface = Color(0xFFE6E7EC), inverseOnSurface = anthracite(0x1C),
    )
}

/** Hell/Dunkel nach System, Akzentfarbe aus den Optionen (Vorgabe Violett). */
@Composable
fun AppTheme(content: @Composable () -> Unit) {
    val dark = isSystemInDarkTheme()
    val settings by App.get(LocalContext.current).prefs.settings.collectAsState()
    val accent = Color(settings.accent.toInt())
    MaterialTheme(colorScheme = if (dark) darkScheme(accent) else lightScheme(accent), content = content)
}
