package at.dimes.dmoffice.ui

import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.Point
import android.graphics.PorterDuff
import android.graphics.PorterDuffXfermode
import android.graphics.Rect
import android.graphics.RectF
import android.graphics.drawable.BitmapDrawable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.FileViews
import at.dimes.dmoffice.files.PhotoMeta
import at.dimes.dmoffice.files.Thumbs
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.osmdroid.config.Configuration
import org.osmdroid.events.MapListener
import org.osmdroid.events.ScrollEvent
import org.osmdroid.events.ZoomEvent
import org.osmdroid.tileprovider.tilesource.TileSourceFactory
import org.osmdroid.util.BoundingBox
import org.osmdroid.util.GeoPoint
import org.osmdroid.views.MapView
import org.osmdroid.views.overlay.Marker

/**
 * Atlas der Galerie (wie im Web): die gezeigten Fotos mit Aufnahmeort auf einer Karte von OpenStreetMap. Nahe beieinander liegende
 * fasst er zu einem Kreis mit Anzahl zusammen (je Zoomstufe neu). Antippen eines Kreises zeigt die Galerie, gefiltert auf seine
 * Fotos (onPick); ein einzelnes Foto öffnet sich gleich.
 * Filter und Album gelten wie in der Kachelansicht.
 */
@Composable
fun PhotoAtlas(items: List<FileItem>, meta: Map<String, PhotoMeta>, onOpen: (FileItem, List<FileItem>) -> Unit, onPick: (List<FileItem>) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val placed = remember(items, meta) {
        items.mapNotNull { i -> meta[i.path]?.let { m -> if (m.lat != null && m.lon != null) i to GeoPoint(m.lat, m.lon) else null } }
    }
    if (placed.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(stringResource(R.string.photos_atlas_none), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        return
    }
    val brand = MaterialTheme.colorScheme.primary.toArgb()
    // OpenStreetMap verlangt eine eigene Kennung der App; Kacheln landen im Cache der App
    remember { Configuration.getInstance().apply { userAgentValue = ctx.packageName; osmdroidBasePath = ctx.cacheDir; osmdroidTileCache = java.io.File(ctx.cacheDir, "osm") } }
    val map = remember {
        MapView(ctx).apply {
            setTileSource(TileSourceFactory.MAPNIK)
            setMultiTouchControls(true)
            zoomController.setVisibility(org.osmdroid.views.CustomZoomButtonsController.Visibility.NEVER)
            minZoomLevel = 2.0
        }
    }
    var drawJob: Job? = remember { null }
    fun draw() {
        drawJob?.cancel()
        drawJob = scope.launch {
            delay(120)
            val proj = map.projection
            val cells = LinkedHashMap<Pair<Int, Int>, MutableList<Pair<FileItem, GeoPoint>>>()
            val pt = Point()
            for (p in placed) {
                proj.toPixels(p.second, pt)
                cells.getOrPut(Math.floorDiv(pt.x, 140) to Math.floorDiv(pt.y, 140)) { mutableListOf() }.add(p)
            }
            map.overlays.removeAll { it is Marker }
            for (c in cells.values) {
                val center = GeoPoint(c.sumOf { it.second.latitude } / c.size, c.sumOf { it.second.longitude } / c.size)
                val first = c[0].first
                val marker = Marker(map).apply {
                    position = center
                    setAnchor(Marker.ANCHOR_CENTER, Marker.ANCHOR_CENTER)
                    icon = BitmapDrawable(ctx.resources, bubble(Thumbs.cached(first, Thumbs.SMALL), c.size, brand))
                    setOnMarkerClickListener { _, _ ->
                        val list = c.map { it.first }
                        if (c.size == 1) onOpen(first, list) else onPick(list)
                        true
                    }
                }
                map.overlays.add(marker)
                // Vorschau nachladen, falls noch nicht im Speicher
                if (Thumbs.cached(first, Thumbs.SMALL) == null && FileViews.hasThumb(first)) launch {
                    val b = try { Thumbs.load(ctx, first, Thumbs.SMALL) } catch (_: Exception) { null }
                    if (b != null) { marker.icon = BitmapDrawable(ctx.resources, bubble(b, c.size, brand)); map.invalidate() }
                }
            }
            map.invalidate()
        }
    }
    DisposableEffect(map) {
        map.onResume()
        val listener = object : MapListener {
            override fun onScroll(event: ScrollEvent?) = false
            override fun onZoom(event: ZoomEvent?): Boolean { draw(); return false }
        }
        map.addMapListener(listener)
        onDispose { map.removeMapListener(listener); map.onPause(); map.onDetach() }
    }
    LaunchedEffect(placed) {
        // Erst nach dem Auslegen kennt die Karte ihre Größe
        map.post {
            val pts = placed.map { it.second }
            if (pts.size == 1) { map.controller.setZoom(14.0); map.controller.setCenter(pts[0]) }
            else map.zoomToBoundingBox(BoundingBox.fromGeoPoints(pts).increaseByScale(1.2f), false)
            draw()
        }
    }
    Column(Modifier.fillMaxSize()) {
        AndroidView({ map }, Modifier.weight(1f).fillMaxWidth())
        Text(stringResource(R.string.photos_atlas_info, placed.size, items.size), style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp))
    }
}

/** Runde Vorschau mit weißem Rand, bei mehreren mit Anzahl oben rechts. */
private fun bubble(thumb: Bitmap?, count: Int, brand: Int): Bitmap {
    val d = if (count == 1) 120 else (110 + (Math.log10(count.toDouble()) * 30).toInt()).coerceAtMost(170)
    val pad = 24
    val out = Bitmap.createBitmap(d + pad, d + pad, Bitmap.Config.ARGB_8888)
    val c = Canvas(out)
    val p = Paint(Paint.ANTI_ALIAS_FLAG)
    val cx = (d / 2f) + pad / 2f; val cy = cx
    p.color = android.graphics.Color.WHITE
    c.drawCircle(cx, cy, d / 2f, p)
    val inner = d / 2f - 5f
    if (thumb != null) {
        val layer = Bitmap.createBitmap(d, d, Bitmap.Config.ARGB_8888)
        val lc = Canvas(layer)
        val lp = Paint(Paint.ANTI_ALIAS_FLAG)
        lc.drawCircle(d / 2f, d / 2f, inner, lp)
        lp.xfermode = PorterDuffXfermode(PorterDuff.Mode.SRC_IN)
        val side = minOf(thumb.width, thumb.height)
        val src = Rect((thumb.width - side) / 2, (thumb.height - side) / 2, (thumb.width + side) / 2, (thumb.height + side) / 2)
        lc.drawBitmap(thumb, src, RectF(0f, 0f, d.toFloat(), d.toFloat()), lp)
        c.drawBitmap(layer, pad / 2f, pad / 2f, null)
    } else {
        p.color = brand
        c.drawCircle(cx, cy, inner, p)
    }
    if (count > 1) {
        val text = if (count > 999) "999+" else count.toString()
        val tp = Paint(Paint.ANTI_ALIAS_FLAG).apply { color = android.graphics.Color.WHITE; textSize = 30f; isFakeBoldText = true }
        val w = tp.measureText(text) + 22
        val r = RectF(out.width - w - 2, 2f, out.width - 2f, 42f)
        p.color = brand
        c.drawRoundRect(r, 20f, 20f, p)
        p.color = android.graphics.Color.WHITE; p.style = Paint.Style.STROKE; p.strokeWidth = 3f
        c.drawRoundRect(r, 20f, 20f, p)
        c.drawText(text, r.left + 11, r.bottom - 11, tp)
    }
    return out
}
