package at.dimes.dmoffice.ui

import android.content.Context
import android.content.Intent
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.net.Uri
import android.os.Build
import android.os.Looper
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Place
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clipToBounds
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.App
import at.dimes.dmoffice.chat.ChatMessage
import at.dimes.dmoffice.chat.ChatRef
import at.dimes.dmoffice.chat.LiveLocation
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.material3.TextButton
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.viewinterop.AndroidView
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.osmdroid.config.Configuration
import org.osmdroid.tileprovider.tilesource.TileSourceFactory
import org.osmdroid.util.GeoPoint
import org.osmdroid.views.MapView
import org.osmdroid.views.overlay.Marker
import java.text.DateFormat
import java.time.Instant
import java.util.Date
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import java.util.Locale
import kotlin.coroutines.resume

/**
 * Standort-Anfrage (Stufe 7): die Position einmal bestimmen, wenn der Gefragte "Standort schicken" tippt - kein
 * laufendes Verfolgen. Erst der beste Anbieter (Fused, sonst GPS, dann Netz) mit Wartezeit, sonst die letzte bekannte
 * Position der letzten zehn Minuten. null: geht gerade nicht (aus, keine Berechtigung, kein Empfang).
 */
suspend fun currentLocation(ctx: Context): Location? {
    val lm = ctx.getSystemService(LocationManager::class.java) ?: return null
    val providers = buildList {
        if (Build.VERSION.SDK_INT >= 31) add(LocationManager.FUSED_PROVIDER)
        add(LocationManager.GPS_PROVIDER); add(LocationManager.NETWORK_PROVIDER)
    }.filter { try { lm.isProviderEnabled(it) } catch (_: Exception) { false } }
    try {
        for (p in providers) withTimeoutOrNull(15_000) { once(ctx, lm, p) }?.let { return it }
        return providers.mapNotNull { lm.getLastKnownLocation(it) }
            .filter { System.currentTimeMillis() - it.time < 10 * 60_000 }.minByOrNull { it.accuracy }
    } catch (_: SecurityException) { return null }
}

@Suppress("DEPRECATION")
private suspend fun once(ctx: Context, lm: LocationManager, provider: String): Location? = suspendCancellableCoroutine { cont ->
    try {
        if (Build.VERSION.SDK_INT >= 30) {
            val cancel = android.os.CancellationSignal()
            cont.invokeOnCancellation { cancel.cancel() }
            lm.getCurrentLocation(provider, cancel, ctx.mainExecutor) { loc -> if (cont.isActive) cont.resume(loc) }
        } else {
            val listener = LocationListener { loc -> if (cont.isActive) cont.resume(loc) }
            cont.invokeOnCancellation { lm.removeUpdates(listener) }
            lm.requestSingleUpdate(provider, listener, Looper.getMainLooper())
        }
    } catch (e: SecurityException) { if (cont.isActive) cont.resume(null) }
    catch (e: IllegalArgumentException) { if (cont.isActive) cont.resume(null) }
}

/**
 * Standort in einer Nachricht: Kartenausschnitt (OpenStreetMap wie im Atlas), Genauigkeit, Antippen oeffnet die
 * Karten-App (geo:). Live: "live noch n Min" und fuer den Absender "Beenden", danach "Letzter Standort".
 */
@Composable
fun LocationCard(ref: ChatRef, message: ChatMessage? = null, own: Boolean = false) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val lat = ref.lat
    val lon = ref.lon
    if (lat == null || lon == null) {
        Text(stringResource(R.string.chat_location) + " – " + stringResource(if (ref.state == "revoked") R.string.chat_revoked else R.string.chat_expired),
            fontStyle = FontStyle.Italic, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
        return
    }
    val until = remember(ref.liveUntil) { ref.liveUntil?.let { try { Instant.parse(it).toEpochMilli() } catch (_: Exception) { null } } }
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(until) { while (until != null && now < until) { delay(15_000); now = System.currentTimeMillis() } }
    val left = if (until != null && until > now) maxOf(1L, (until - now + 59_999) / 60_000).toInt() else null
    val active by LiveLocation.active.collectAsState()
    val at = ref.at?.let { try { DateFormat.getTimeInstance(DateFormat.SHORT).format(Date(Instant.parse(it).toEpochMilli())) } catch (_: Exception) { null } }
    val detail = listOfNotNull(
        left?.let { stringResource(R.string.chat_location_live_left, it) },
        if (until != null) at?.let { stringResource(R.string.chat_location_at, it) } else null,
        ref.accuracy?.let { stringResource(R.string.chat_location_accuracy, Math.round(it).toInt()) },
    ).joinToString(" · ")
    Column(Modifier.padding(vertical = 2.dp).width(260.dp).clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.surfaceVariant)
        .clickable { openMap(ctx, lat, lon) }) {
        Row(Modifier.padding(start = 8.dp, top = 6.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Default.Place, null)
            Column(Modifier.padding(start = 8.dp)) {
                Text(stringResource(when { left != null -> R.string.chat_location_live_title; until != null -> R.string.chat_location_last; else -> R.string.chat_location }),
                    fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.bodyMedium)
                if (detail.isNotEmpty()) Text(detail, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        MiniMap(lat, lon, { openMap(ctx, lat, lon) }, Modifier.padding(horizontal = 8.dp, vertical = 6.dp).fillMaxWidth().height(140.dp).clip(RoundedCornerShape(6.dp)))
        Row(Modifier.padding(start = 8.dp, end = 4.dp, bottom = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.chat_location_open), Modifier.weight(1f), style = MaterialTheme.typography.labelMedium,
                color = MaterialTheme.colorScheme.primary, textDecoration = TextDecoration.Underline)
            if (own && !ref.forwarded && left != null && message != null) TextButton(onClick = {
                if (active?.message == message.id) LiveLocation.stop(ctx)
                else scope.launch { try { App.get(ctx).chat.stopLocation(message.room, message.id) } catch (_: Exception) { } }
            }) { Text(stringResource(R.string.chat_location_stop)) }
        }
    }
}

/** Kleine Karte ohne Bedienung, folgt Live-Positionen. */
@Composable
internal fun MiniMap(lat: Double, lon: Double, onClick: () -> Unit, modifier: Modifier) {
    val ctx = LocalContext.current
    val brand = MaterialTheme.colorScheme.primary.toArgb()
    // OpenStreetMap verlangt eine eigene Kennung der App; Kacheln landen im Cache der App
    remember { Configuration.getInstance().apply { userAgentValue = ctx.packageName; osmdroidBasePath = ctx.cacheDir; osmdroidTileCache = java.io.File(ctx.cacheDir, "osm") } }
    val map = remember {
        MapView(ctx).apply {
            setTileSource(TileSourceFactory.MAPNIK)
            setMultiTouchControls(false)
            zoomController.setVisibility(org.osmdroid.views.CustomZoomButtonsController.Visibility.NEVER)
            controller.setZoom(16.0)
            // Nur innerhalb des eigenen Rahmens zeichnen - in Dialogen und Listen ragten die Kacheln sonst über Text und Titel
            outlineProvider = android.view.ViewOutlineProvider.BOUNDS
            clipToOutline = true
        }
    }
    val marker = remember { Marker(map).apply { setAnchor(Marker.ANCHOR_CENTER, Marker.ANCHOR_CENTER); icon = dot(brand, ctx.resources.displayMetrics.density); setInfoWindow(null) }.also { map.overlays.add(it) } }
    LaunchedEffect(lat, lon) { val p = GeoPoint(lat, lon); marker.position = p; map.controller.setCenter(p); map.invalidate() }
    DisposableEffect(map) { map.onResume(); onDispose { map.onPause(); map.onDetach() } }
    // Eine Schicht darueber nimmt alle Beruehrungen: kein Verschieben im Verlauf, Antippen oeffnet die Karten-App
    Box(modifier.clipToBounds()) {
        AndroidView({ map }, Modifier.matchParentSize().clipToBounds())
        Box(Modifier.matchParentSize().clickable(onClick = onClick))
    }
}

private fun dot(color: Int, density: Float): android.graphics.drawable.Drawable {
    // Gut sichtbar: 26 dp mit weißem Rand und leichtem Schatten - in festen Pixeln war der Punkt auf dichten Bildschirmen winzig
    val size = (26 * density).toInt()
    val b = android.graphics.Bitmap.createBitmap(size, size, android.graphics.Bitmap.Config.ARGB_8888)
    val c = android.graphics.Canvas(b)
    val paint = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG)
    paint.color = 0x55000000; c.drawCircle(size / 2f, size / 2f, size / 2f, paint)
    paint.color = android.graphics.Color.WHITE; c.drawCircle(size / 2f, size / 2f, size / 2f - density, paint)
    paint.color = color; c.drawCircle(size / 2f, size / 2f, size / 2f - 5 * density, paint)
    return android.graphics.drawable.BitmapDrawable(null as android.content.res.Resources?, b)
}

internal fun openMap(ctx: Context, lat: Double, lon: Double) {
    val geo = String.format(Locale.ROOT, "geo:%.6f,%.6f?q=%.6f,%.6f", lat, lon, lat, lon)
    try { ctx.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(geo))) }
    catch (_: android.content.ActivityNotFoundException) {
        // Keine Karten-App: im Browser (OpenStreetMap)
        ctx.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(String.format(Locale.ROOT,
            "https://www.openstreetmap.org/?mlat=%.6f&mlon=%.6f#map=17/%.6f/%.6f", lat, lon, lat, lon))))
    }
}
