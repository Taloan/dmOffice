package at.dimes.dmoffice.ui

import android.graphics.drawable.BitmapDrawable
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.coach.ActivityDetail
import at.dimes.dmoffice.coach.ActivityPhoto
import at.dimes.dmoffice.coach.GeoItem
import at.dimes.dmoffice.coach.GeoPreview
import at.dimes.dmoffice.coach.GeoState
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.TextButton
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.text.input.KeyboardType
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.math.roundToInt
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.Thumbs
import org.osmdroid.config.Configuration
import org.osmdroid.tileprovider.tilesource.TileSourceFactory
import org.osmdroid.util.BoundingBox
import org.osmdroid.util.GeoPoint
import org.osmdroid.views.MapView
import org.osmdroid.views.overlay.Marker
import org.osmdroid.views.overlay.Polyline
import java.text.NumberFormat

private val ActivityColor = Color(0xFFF2B53C)

private fun ActivityPhoto.item() = FileItem(path = path, name = name, isDir = false, mime = mime, etag = etag)

/**
 * Eine Aktivität von der Uhr (Vollbild): Start, Dauer, Strecke, kcal; die Strecke von Garmin auf der Karte (OpenStreetMap), Fotos
 * aus der Galerie, die währenddessen aufgenommen wurden - mit Ort als Punkte an der Strecke.
 */
@Composable
internal fun ActivityDetailScreen(id: Long, onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    var d by remember { mutableStateOf<ActivityDetail?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var big by remember { mutableStateOf<ActivityPhoto?>(null) }
    // Verorten: Vorschau mit Versatz der Kamera-Uhr (Minuten), dann im Hintergrund schreiben und den Stand abfragen
    var geo by remember { mutableStateOf<GeoPreview?>(null) }
    var offset by remember { mutableStateOf("0") }
    var geoBusy by remember { mutableStateOf<GeoState?>(null) }
    // Die Vorschau liest alle Fotos und holt die Strecke mit Zeiten - das dauert, also sichtbar warten
    var geoLoading by remember { mutableStateOf(false) }
    var geoMsg by remember { mutableStateOf<String?>(null) }
    val scope = rememberCoroutineScope()
    val seconds = ((offset.replace(',', '.').toDoubleOrNull() ?: 0.0) * 60).roundToInt()
    LaunchedEffect(id) { try { d = App.get(ctx).coach.activity(id) } catch (e: Exception) { error = e.message ?: e.toString() } }
    LaunchedEffect(seconds) {
        if (geo == null) return@LaunchedEffect
        delay(400)
        try { geo = App.get(ctx).coach.geotagPreview(id, seconds) } catch (e: Exception) { error = e.message ?: e.toString() }
    }
    val ready = geo?.items?.filter { it.state == "ok" } ?: emptyList()
    // Fotos wie in der Liste: JPG und RAW gleichen Namens im selben Ordner sind ein Foto, verortet werden beide Dateien
    val readyPhotos = ready.map { it.path.replace(Regex("\\.[^./]+$"), "").lowercase() }.toSet().size
    fun startGeo() = scope.launch {
        val repo = App.get(ctx).coach
        try {
            repo.geotagStart(id, seconds, ready.map { it.path })
            geo = null
            var s: GeoState
            do { delay(1200); s = repo.geotagState(id); geoBusy = s } while (s.running)
            geoBusy = null
            geoMsg = if (s.failed.isEmpty()) ctx.getString(R.string.coach_geo_done, s.total)
                else ctx.getString(R.string.coach_geo_partly, s.total - s.failed.size, s.total, s.failed.joinToString(", ") { "${it.name} (${it.error})" })
            d = repo.activity(id)
        } catch (e: Exception) { geoBusy = null; error = e.message ?: e.toString() }
    }
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss, properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
    Column(Modifier.fillMaxSize().systemBarsPadding().padding(horizontal = 12.dp, vertical = 8.dp)) {
        val a = d
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onDismiss) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) }
            Text(a?.let { it.name.ifBlank { it.type } } ?: "", color = ActivityColor, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
        }
        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        if (a == null) { if (error == null) Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }; return@Column }
        val l = NumberFormat.getNumberInstance().apply { maximumFractionDigits = 2 }
        val min = ((a.durationS ?: 0.0) / 60).toInt()
        LazyVerticalGrid(GridCells.Adaptive(104.dp), Modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            val full: androidx.compose.foundation.lazy.grid.LazyGridItemSpanScope.() -> GridItemSpan = { GridItemSpan(maxLineSpan) }
            item(span = full) {
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for ((label, value) in listOf(
                        stringResource(R.string.coach_act_start) to (a.start.takeIf { it.length >= 16 }?.substring(11, 16) ?: "–"),
                        stringResource(R.string.coach_act_duration) to (if (min > 0) "%d:%02d h".format(min / 60, min % 60) else "–"),
                        stringResource(R.string.coach_act_distance) to (a.distanceM?.takeIf { it > 0 }?.let { l.format(it / 1000) + " km" } ?: "–"),
                        "kcal" to (a.kcal?.let { NumberFormat.getIntegerInstance().format(it) } ?: "–"),
                    )) Tile(ActivityColor, Modifier.weight(1f)) {
                        Text(label, color = ActivityColor, style = MaterialTheme.typography.labelSmall, maxLines = 1)
                        Text(value, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, maxLines = 1)
                    }
                }
            }
            val located = a.photos.filter { it.lat != null && it.lon != null }
            if (a.unlocated > 0 && a.track.size > 1 && geo == null && geoBusy == null) item(span = full) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(enabled = !geoLoading, onClick = {
                        geoMsg = null
                        geoLoading = true
                        scope.launch {
                            try { geo = App.get(ctx).coach.geotagPreview(id, seconds) } catch (e: Exception) { error = e.message ?: e.toString() }
                            geoLoading = false
                        }
                    }) { Text(stringResource(R.string.coach_geo_button, a.unlocated)) }
                    if (geoLoading) CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                }
                if (geoLoading) Text(stringResource(R.string.coach_geo_checking), style = MaterialTheme.typography.bodySmall)
            }
            geoBusy?.let { s -> item(span = full) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                    Text(stringResource(R.string.coach_geo_busy, s.done, s.total), style = MaterialTheme.typography.bodySmall)
                }
            } }
            geoMsg?.let { m -> item(span = full) { Text(m, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary) } }
            geo?.let { g -> item(span = full) {
                Tile(ActivityColor, Modifier.fillMaxWidth()) {
                    if (!g.timed) Text(g.trackError ?: stringResource(R.string.coach_geo_untimed), style = MaterialTheme.typography.bodyMedium)
                    else {
                        Text(when {
                            ready.isEmpty() -> stringResource(R.string.coach_geo_none)
                            readyPhotos < ready.size -> stringResource(R.string.coach_geo_ready_files, readyPhotos, ready.size)
                            else -> stringResource(R.string.coach_geo_ready, ready.size)
                        }, style = MaterialTheme.typography.bodyMedium)
                        for ((state, res) in listOf("outside" to R.string.coach_geo_outside, "readonly" to R.string.coach_geo_readonly, "unsupported" to R.string.coach_geo_unsupported)) {
                            val n = g.items.count { it.state == state }
                            if (n > 0) Text(stringResource(res, n), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        val rough = ready.count { it.rough }
                        if (rough > 0) Text(stringResource(R.string.coach_geo_rough, rough), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        OutlinedTextField(offset, { offset = it }, Modifier.padding(top = 6.dp), label = { Text(stringResource(R.string.coach_geo_offset)) }, singleLine = true,
                            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Text))
                    }
                    Row(Modifier.padding(top = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Button(onClick = { startGeo() }, enabled = g.timed && ready.isNotEmpty()) { Text(if (readyPhotos < ready.size) stringResource(R.string.coach_geo_start_files, readyPhotos, ready.size) else stringResource(R.string.coach_geo_start, ready.size)) }
                        TextButton(onClick = { geo = null }) { Text(stringResource(R.string.cancel)) }
                    }
                }
            } }
            item(span = full) {
                if (a.track.size > 1 || located.isNotEmpty()) Card(Modifier.fillMaxWidth().height(320.dp), shape = RoundedCornerShape(16.dp)) {
                    ActivityMap(a, located, ready.filter { it.lat != null && it.lon != null }) { big = it }
                }
                else Text(a.trackError ?: stringResource(R.string.coach_act_no_track), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (a.photos.isNotEmpty()) {
                item(span = full) { Text(stringResource(R.string.coach_act_photos, a.photos.size), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold) }
                items(a.photos, key = { it.path }) { p ->
                    Box(Modifier.aspectRatio(1f).clip(RoundedCornerShape(8.dp)).clickable { big = p }) { ThumbOrIcon(p.item(), Modifier.fillMaxSize(), 32.dp) }
                }
            }
        }
    }
    }
    }
    // Foto groß (wie im Betrachter, mit Zoom)
    big?.let { p ->
        val list = d?.photos?.map { it.item() } ?: listOf(p.item())
        FileViewer(list, list.indexOfFirst { it.path == p.path }.coerceAtLeast(0), onClose = { big = null }, onShare = null, onOpenExternal = null)
    }
}

/** Karte: Strecke als Linie (Start grün, Ende rot), Fotos mit Ort als kleine Bilder; zeigt alles auf einmal. */
@Composable
private fun ActivityMap(a: ActivityDetail, located: List<ActivityPhoto>, pending: List<GeoItem>, onPhoto: (ActivityPhoto) -> Unit) {
    val ctx = LocalContext.current
    remember { Configuration.getInstance().apply { userAgentValue = ctx.packageName; osmdroidBasePath = ctx.cacheDir; osmdroidTileCache = java.io.File(ctx.cacheDir, "osm") } }
    val track = remember(a) { a.track.map { GeoPoint(it[0], it[1]) } }
    val map = remember {
        MapView(ctx).apply {
            setTileSource(TileSourceFactory.MAPNIK)
            setMultiTouchControls(true)
            zoomController.setVisibility(org.osmdroid.views.CustomZoomButtonsController.Visibility.NEVER)
        }
    }
    LaunchedEffect(a, pending) {
        map.overlays.clear()
        if (track.size > 1) {
            map.overlays.add(Polyline(map).apply { setPoints(track); outlinePaint.color = ActivityColor.toArgb(); outlinePaint.strokeWidth = 12f })
            for ((pt, c) in listOf(track.first() to Color(0xFF2E9E5B), track.last() to Color(0xFFD13438)))
                map.overlays.add(Marker(map).apply { position = pt; setAnchor(Marker.ANCHOR_CENTER, Marker.ANCHOR_CENTER); icon = dot(ctx, c.toArgb()); setOnMarkerClickListener { _, _ -> true } })
        }
        for (p in located) {
            val m = Marker(map).apply {
                position = GeoPoint(p.lat!!, p.lon!!); setAnchor(Marker.ANCHOR_CENTER, Marker.ANCHOR_CENTER)
                icon = dot(ctx, Color(0xFFE07AD6).toArgb())
                setOnMarkerClickListener { _, _ -> onPhoto(p); true }
            }
            map.overlays.add(m)
            // Kleines Bild statt Punkt, sobald die Vorschau da ist
            val b = try { Thumbs.load(ctx, p.item(), Thumbs.SMALL) } catch (_: Exception) { null }
            if (b != null) { m.icon = BitmapDrawable(ctx.resources, android.graphics.Bitmap.createScaledBitmap(b, 96, 96 * b.height / b.width.coerceAtLeast(1), true)); map.invalidate() }
        }
        // Vorschau des Verortens: Punkte in der Farbe der Strecke, noch nicht geschrieben
        for (g in pending)
            map.overlays.add(Marker(map).apply {
                position = GeoPoint(g.lat!!, g.lon!!); setAnchor(Marker.ANCHOR_CENTER, Marker.ANCHOR_CENTER)
                icon = dot(ctx, ActivityColor.copy(alpha = 0.6f).toArgb()); title = g.name
            })
        val all = track + located.map { GeoPoint(it.lat!!, it.lon!!) }
        map.post {
            if (all.size == 1) { map.controller.setZoom(16.0); map.controller.setCenter(all[0]) }
            else map.zoomToBoundingBox(BoundingBox.fromGeoPoints(all).increaseByScale(1.2f), false, 48)
        }
        map.invalidate()
    }
    AndroidView({ map }, Modifier.fillMaxSize())
}

private fun dot(ctx: android.content.Context, color: Int): BitmapDrawable {
    val s = 36
    val bmp = android.graphics.Bitmap.createBitmap(s, s, android.graphics.Bitmap.Config.ARGB_8888)
    val c = android.graphics.Canvas(bmp)
    val p = android.graphics.Paint(android.graphics.Paint.ANTI_ALIAS_FLAG)
    p.color = android.graphics.Color.WHITE; c.drawCircle(s / 2f, s / 2f, s / 2f, p)
    p.color = color; c.drawCircle(s / 2f, s / 2f, s / 2f - 5, p)
    return BitmapDrawable(ctx.resources, bmp)
}
