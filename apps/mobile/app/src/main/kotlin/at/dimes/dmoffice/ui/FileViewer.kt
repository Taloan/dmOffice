package at.dimes.dmoffice.ui

import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.selected
import androidx.compose.ui.semantics.semantics
import android.graphics.Bitmap
import android.graphics.pdf.PdfRenderer
import android.net.Uri
import android.os.ParcelFileDescriptor
import android.widget.MediaController
import android.widget.VideoView
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.calculateCentroid
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.foundation.Canvas
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.foundation.layout.Box
import androidx.compose.material.icons.filled.StarBorder
import androidx.compose.material.icons.filled.Star
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.OpenInNew
import androidx.compose.material.icons.filled.Share
import androidx.compose.material.icons.filled.HighQuality
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.filled.Crop
import androidx.compose.material.icons.filled.Place
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.TextButton
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.size
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.FileViews
import at.dimes.dmoffice.files.Thumbs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.coroutines.launch
import java.io.File

/**
 * Betrachter wie im Web: Bilder (Wischen, Zoomen; Bild in 1600 px vom Server), PDF (Seiten), Text und Markdown,
 * Audio und Video (gestreamt mit Bereichsanfragen). Wischen blaettert durch die Dateien der aktuellen Liste.
 */
@OptIn(ExperimentalMaterial3Api::class, androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
// onShare/onOpenExternal null und secure: nur gezeigt (Version 2) - nicht weitergeben, kein Bildschirmfoto
// onShare: (Datei, verkleinert?) - null = wie eingestellt; lange drücken fragt jedes Mal
fun FileViewer(items: List<FileItem>, start: Int, onClose: () -> Unit, onShare: ((FileItem, Boolean?) -> Unit)?, onOpenExternal: ((FileItem) -> Unit)?, secure: Boolean = false,
               locationOf: ((FileItem) -> Pair<Double, Double>?)? = null, infoOf: ((FileItem) -> at.dimes.dmoffice.files.PhotoMeta?)? = null,
               onInfoSaved: () -> Unit = {}) {
    var askShare by remember { mutableStateOf<FileItem?>(null) }
    askShare?.let { item ->
        AlertDialog(onDismissRequest = { askShare = null }, title = { Text(stringResource(R.string.share_quality)) },
            text = { Column {
                TextButton(onClick = { askShare = null; onShare?.invoke(item, false) }) { Text(stringResource(R.string.share_original)) }
                TextButton(onClick = { askShare = null; onShare?.invoke(item, true) }) { Text(stringResource(R.string.share_small)) }
            } }, confirmButton = {}, dismissButton = { TextButton(onClick = { askShare = null }) { Text(stringResource(R.string.cancel)) } })
    }
    BackHandler(onBack = onClose)
    // secure: nur gezeigt - Keine Bildschirmfoto-Sperre mehr (Entscheidung): abfotografieren laesst sich nie verhindern (Web, zweites Handy) - Zeigen heisst nur, nicht speichern, herunterladen oder weiterleiten
    val pager = rememberPagerState(initialPage = start.coerceIn(0, (items.size - 1).coerceAtLeast(0))) { items.size }
    var zoomed by remember { mutableStateOf(false) }
    // Bilder, bei denen „Original anzeigen“ gedrückt wurde (sonst die große Vorschau vom Server)
    var originals by remember { mutableStateOf(emptySet<String>()) }
    var editing by remember { mutableStateOf<FileItem?>(null) }
    val savedCopy = stringResource(R.string.photo_saved_copy)
    val savedOver = stringResource(R.string.photo_saved_over)
    editing?.let { e ->
        val ctxE = LocalContext.current
        PhotoEditScreen(e, canOverwrite = true) { path ->
            editing = null
            if (path != null) {
                android.widget.Toast.makeText(ctxE, if (path.trimStart('/') == e.path.trimStart('/')) savedOver else savedCopy.format(path.substringAfterLast('/')), android.widget.Toast.LENGTH_LONG).show()
            }
        }
    }
    val current = items.getOrNull(pager.currentPage)
    // Sterne, Markierung, Farbe: gleich sichtbar; die Galerie lädt erst beim Schließen neu, damit Wischen flüssig bleibt
    var marks by remember { mutableStateOf(emptyMap<String, PhotoMark>()) }
    val markScope = rememberCoroutineScope()
    val markCtx = LocalContext.current
    val markedAny = remember { mutableStateOf(false) }
    DisposableEffect(Unit) { onDispose { if (markedAny.value) onInfoSaved() } }
    // Bildinfos (Galerie): Aufnahme, Kamera, Objektiv, Belichtung, Abmessungen, Größe
    var showInfo by remember { mutableStateOf(false) }
    if (showInfo && current != null) PhotoInfoSheet(current, infoOf?.invoke(current), onSaved = { marks = marks - current.path; onInfoSaved() }) { showInfo = false }
    // Aufnahmeort (Galerie): kleine Karte, Antippen öffnet die Karten-App
    var showPlace by remember { mutableStateOf<Pair<Double, Double>?>(null) }
    showPlace?.let { (lat, lon) ->
        val ctxP = LocalContext.current
        AlertDialog(onDismissRequest = { showPlace = null }, title = { Text(stringResource(R.string.photo_place)) },
            text = { MiniMap(lat, lon, onClick = { openMap(ctxP, lat, lon) }, modifier = Modifier.fillMaxWidth().height(260.dp)) },
            confirmButton = { TextButton(onClick = { openMap(ctxP, lat, lon) }) { Text(stringResource(R.string.photo_place_open)) } },
            dismissButton = { TextButton(onClick = { showPlace = null }) { Text(stringResource(R.string.close)) } })
    }
    Scaffold(containerColor = Color.Black, bottomBar = {
        if (infoOf != null && current != null && !zoomed && FileViews.kindOf(current) == FileViews.IMAGE) {
            val m = infoOf(current)
            val mark = marks[current.path] ?: PhotoMark(m?.rating ?: 0, m?.pick ?: 0, m?.label)
            PhotoMarkBar(mark) { next, body ->
                val before = marks[current.path]
                marks = marks + (current.path to next)
                markScope.launch {
                    val failed = try { withContext(Dispatchers.IO) { App.get(markCtx).files.photoInfo(body(listOf(current.path))) }.firstOrNull()?.error }
                        catch (e: Exception) { e.message ?: e.toString() }
                    if (failed != null) {
                        marks = if (before != null) marks + (current.path to before) else marks - current.path
                        android.widget.Toast.makeText(markCtx, failed, android.widget.Toast.LENGTH_SHORT).show()
                    } else markedAny.value = true
                }
            }
        }
    }, topBar = {
        TopAppBar(colors = TopAppBarDefaults.topAppBarColors(containerColor = Color.Black.copy(alpha = 0.6f), titleContentColor = Color.White,
            navigationIconContentColor = Color.White, actionIconContentColor = Color.White),
            title = { Column { Text(current?.name ?: "", maxLines = 1, style = MaterialTheme.typography.titleMedium)
                if (items.size > 1) Text("${pager.currentPage + 1} / ${items.size}", style = MaterialTheme.typography.labelSmall) } },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                if (current != null) {
                    if (infoOf != null) IconButton(onClick = { showInfo = true }) { Icon(Icons.Outlined.Info, contentDescription = stringResource(R.string.photo_info_show)) }
                    current.let { c -> locationOf?.invoke(c) }?.let { place ->
                        IconButton(onClick = { showPlace = place }) { Icon(Icons.Default.Place, contentDescription = stringResource(R.string.photo_place)) }
                    }
                    // Original statt Vorschau: nur bei Bildern, die Android selbst lesen kann (kein RAW), und nicht bei nur gezeigten
                    if (onShare != null && FileViews.kindOf(current) == FileViews.IMAGE && current.path !in originals
                        && current.name.substringAfterLast('.', "").lowercase() !in at.dimes.dmoffice.files.FilesRepo.RAW_EXT)
                        IconButton(onClick = { originals = originals + current.path }) { Icon(Icons.Default.HighQuality, contentDescription = stringResource(R.string.viewer_original)) }
                    // JPEG zuschneiden und drehen (nicht bei nur gezeigten und Album-Fotos)
                    if (onShare != null && current.name.lowercase().let { it.endsWith(".jpg") || it.endsWith(".jpeg") } && !current.path.startsWith("alb:"))
                        IconButton(onClick = { editing = current }) { Icon(Icons.Default.Crop, contentDescription = stringResource(R.string.photo_crop)) }
                    if (onShare != null) Box(Modifier.size(48.dp).combinedClickable(onClick = { onShare(current, null) }, onLongClick = { askShare = current }), contentAlignment = Alignment.Center) {
                        Icon(Icons.Default.Share, contentDescription = stringResource(R.string.files_share))
                    }
                    if (onOpenExternal != null) IconButton(onClick = { onOpenExternal(current) }) { Icon(Icons.AutoMirrored.Filled.OpenInNew, contentDescription = stringResource(R.string.files_open_with)) }
                }
            })
    }) { pad ->
        // Gezoomt: Wischen verschiebt das Bild statt zu blaettern
        HorizontalPager(pager, modifier = Modifier.fillMaxSize().padding(pad), userScrollEnabled = !zoomed, key = { items[it].path }) { page ->
            val item = items[page]
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                when (FileViews.kindOf(item)) {
                    FileViews.IMAGE -> ZoomableImage(item, original = item.path in originals, onZoom = { if (page == pager.currentPage) zoomed = it },
                        // Tief hineingezoomt: Original von selbst (wie „HQ“), außer bei RAW und nur gezeigten Bildern
                        onNeedOriginal = if (onShare != null && item.name.substringAfterLast('.', "").lowercase() !in at.dimes.dmoffice.files.FilesRepo.RAW_EXT)
                            { { originals = originals + item.path } } else null)
                    FileViews.PDF -> PdfPages(item)
                    FileViews.TEXT -> TextView(item)
                    FileViews.AUDIO, FileViews.VIDEO -> if (page == pager.currentPage) MediaPlayerView(item) else Text(item.name, color = Color.White)
                    else -> Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(item.name, color = Color.White)
                        Spacer(Modifier.height(12.dp))
                        if (onOpenExternal != null) androidx.compose.material3.Button(onClick = { onOpenExternal(item) }) { Text(stringResource(R.string.files_open_with)) }
                        else Text(stringResource(R.string.files_shown_only), color = Color.White)
                    }
                }
            }
        }
    }
}

private data class PhotoMark(val rating: Int, val pick: Int, val label: String?)

private val MarkColors = listOf("red" to Color(0xFFE5484D), "yellow" to Color(0xFFF5C518), "green" to Color(0xFF30A46C), "blue" to Color(0xFF0091FF), "purple" to Color(0xFF8E4EC6))

/** P/X/U, Sterne und Farbe wie in der Web-Galerie: nochmal tippen nimmt Stern oder Farbe wieder weg. */
@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
private fun PhotoMarkBar(m: PhotoMark, onChange: (PhotoMark, (List<String>) -> at.dimes.dmoffice.files.PhotoInfoBody) -> Unit) {
    androidx.compose.foundation.layout.FlowRow(
        Modifier.fillMaxWidth().background(Color.Black.copy(alpha = 0.6f)).navigationBarsPadding().padding(horizontal = 8.dp, vertical = 4.dp),
        horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(10.dp, Alignment.CenterHorizontally),
        verticalArrangement = androidx.compose.foundation.layout.Arrangement.Center) {
        androidx.compose.foundation.layout.Row(verticalAlignment = Alignment.CenterVertically) {
            for ((text, value, name) in listOf(Triple("P", 1, R.string.photo_pick_pick), Triple("X", -1, R.string.photo_pick_reject), Triple("U", 0, R.string.photo_pick_none))) {
                val on = m.pick == value
                val desc = stringResource(name)
                Box(Modifier.size(44.dp).clickable { if (!on) onChange(m.copy(pick = value)) { at.dimes.dmoffice.files.PhotoInfoBody(it, pick = value) } }
                    .semantics { contentDescription = desc; selected = on }, contentAlignment = Alignment.Center) {
                    Box(Modifier.size(32.dp).background(if (on) MaterialTheme.colorScheme.primary else Color.Transparent, androidx.compose.foundation.shape.RoundedCornerShape(6.dp)), contentAlignment = Alignment.Center) {
                        Text(text, color = Color.White, fontWeight = if (on) androidx.compose.ui.text.font.FontWeight.Bold else androidx.compose.ui.text.font.FontWeight.Normal)
                    }
                }
            }
        }
        androidx.compose.foundation.layout.Row(verticalAlignment = Alignment.CenterVertically) {
            val desc = stringResource(R.string.photo_info_rating)
            for (n in 1..5) Box(Modifier.size(width = 36.dp, height = 44.dp).clickable {
                    val v = if (m.rating == n) 0 else n
                    onChange(m.copy(rating = v)) { at.dimes.dmoffice.files.PhotoInfoBody(it, rating = v) }
                }.semantics { contentDescription = "$desc $n" }, contentAlignment = Alignment.Center) {
                Icon(if (n <= m.rating) Icons.Default.Star else Icons.Default.StarBorder, contentDescription = null, tint = if (n <= m.rating) Color(0xFFE8A33D) else Color.White)
            }
        }
        androidx.compose.foundation.layout.Row(verticalAlignment = Alignment.CenterVertically) {
            val desc = stringResource(R.string.photo_label)
            for ((name, color) in MarkColors) {
                val on = m.label == name
                Box(Modifier.size(width = 36.dp, height = 44.dp).clickable {
                        val v = if (on) "" else name
                        onChange(m.copy(label = v.ifEmpty { null })) { at.dimes.dmoffice.files.PhotoInfoBody(it, label = v) }
                    }.semantics { contentDescription = "$desc $name"; selected = on }, contentAlignment = Alignment.Center) {
                    Box(Modifier.size(22.dp).background(color, androidx.compose.foundation.shape.CircleShape)
                        .border(if (on) 2.dp else 0.dp, if (on) Color.White else Color.Transparent, androidx.compose.foundation.shape.CircleShape))
                }
            }
        }
    }
}

/**
 * Bild vom Server (erst klein, dann die große Vorschau); [original]: das Original laden. Zoomen mit zwei Fingern, Doppeltippen
 * wechselt 1x/2.5x. Mit dem Original geht es bis zu den einzelnen Pixeln: der sichtbare Ausschnitt wird dann in voller Auflösung
 * nachgeladen (wie Immich) - das ganze Bild in voller Größe passte bei 40 MP nicht in den Speicher. Wird tiefer gezoomt, als die
 * Vorschau hergibt, holt [onNeedOriginal] das Original von selbst.
 */
@Composable
private fun ZoomableImage(item: FileItem, original: Boolean = false, onZoom: (Boolean) -> Unit, onNeedOriginal: (() -> Unit)? = null) {
    val ctx = LocalContext.current
    val small by produceState(Thumbs.cached(item, Thumbs.SMALL), item) { if (value == null) value = try { Thumbs.load(ctx, item, Thumbs.SMALL) } catch (_: Exception) { null } }
    val large by produceState<Bitmap?>(Thumbs.cached(item, Thumbs.LARGE), item) { if (value == null) value = try { Thumbs.load(ctx, item, Thumbs.LARGE) } catch (_: Exception) { null } }
    var failed by remember(item, original) { mutableStateOf(false) }
    val source by produceState<Original?>(null, item, original) {
        if (original) value = try { withContext(Dispatchers.IO) { Original.open(App.get(ctx).files.cacheFile(item)) } } catch (_: Exception) { null }
        failed = original && value == null
    }
    DisposableEffect(source) { val s = source; onDispose { s?.close() } }
    if (original && source == null && !failed) Box(Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        androidx.compose.material3.LinearProgressIndicator(Modifier.fillMaxWidth())
    }
    var scale by remember { mutableFloatStateOf(1f) }
    var offset by remember { mutableStateOf(Offset.Zero) }
    var box by remember { mutableStateOf(androidx.compose.ui.unit.IntSize.Zero) }
    // Ausschnitt in voller Auflösung: Bild und seine Lage im ganzen Bild (gedreht wie aufgenommen)
    var tile by remember(source) { mutableStateOf<Pair<Bitmap, android.graphics.RectF>?>(null) }
    LaunchedEffect(scale) { onZoom(scale > 1.01f) }
    val bmp = source?.preview ?: large ?: small
    if (bmp == null) { CircularProgressIndicator(color = Color.White); return }
    // Maße des ganzen Bilds und wie es in die Fläche passt
    val w = source?.width ?: bmp.width
    val h = source?.height ?: bmp.height
    val fit = if (box.width > 0) minOf(box.width.toFloat() / w, box.height.toFloat() / h) else 1f
    // Höchstens zwei Bildschirmpunkte je Pixel des Originals (ohne Original wie bisher 6x)
    val maxScale = if (source != null) maxOf(6f, 2f / fit) else 6f
    // Tiefer gezoomt, als die Vorschau scharf zeigt: Original holen
    val blurry = box.width > 0 && fit * scale > 1.5f * bmp.width / w
    LaunchedEffect(blurry) { if (blurry && !original) onNeedOriginal?.invoke() }
    LaunchedEffect(source, scale, offset, box) {
        val src = source ?: return@LaunchedEffect
        if (box.width == 0 || fit * scale <= src.preview.width.toFloat() / w) { tile = null; return@LaunchedEffect }
        kotlinx.coroutines.delay(150)   // erst wenn die Finger ruhen
        val cx = box.width / 2f; val cy = box.height / 2f
        val left = (box.width - w * fit) / 2; val top = (box.height - h * fit) / 2
        // Sichtbarer Teil der Fläche, zurückgerechnet auf Pixel des Bilds
        fun img(x: Float, y: Float) = Offset((cx + (x - cx - offset.x) / scale - left) / fit, (cy + (y - cy - offset.y) / scale - top) / fit)
        val a = img(0f, 0f); val b = img(box.width.toFloat(), box.height.toFloat())
        val r = android.graphics.RectF(a.x.coerceIn(0f, w.toFloat()), a.y.coerceIn(0f, h.toFloat()), b.x.coerceIn(0f, w.toFloat()), b.y.coerceIn(0f, h.toFloat()))
        if (r.width() < 1 || r.height() < 1) return@LaunchedEffect
        var sample = 1
        while (sample * 2 * fit * scale <= 1f) sample *= 2
        withContext(Dispatchers.IO) { src.region(r, sample) }?.let { tile = it to r }
    }
    Canvas(Modifier.fillMaxSize()
        .onSizeChanged { box = it }
        .pointerInput(item.path) {
            detectTapGestures(onDoubleTap = { p ->
                if (scale > 1.01f) { scale = 1f; offset = Offset.Zero } else { scale = 2.5f; offset = (Offset(size.width / 2f, size.height / 2f) - p) * 1.5f }
            })
        }
        .pointerInput(item.path, maxScale) {
            // Nur mit zwei Fingern oder gezoomt: sonst bleibt das Wischen beim Blaettern (Pager)
            awaitEachGesture {
                awaitFirstDown(requireUnconsumed = false)
                do {
                    val event = awaitPointerEvent()
                    if (event.changes.size > 1 || scale > 1.01f) {
                        val s = (scale * event.calculateZoom()).coerceIn(1f, maxScale)
                        val pan = event.calculatePan()
                        // Um die Stelle zwischen den Fingern zoomen
                        val c = event.calculateCentroid()
                        val mid = Offset(size.width / 2f, size.height / 2f)
                        val o = if (c == Offset.Unspecified) offset else (offset - (c - mid)) * (s / scale) + (c - mid)
                        scale = s
                        // Beim Verschieben nicht ueber den Rand hinaus
                        val maxX = (size.width * (s - 1)) / 2; val maxY = (size.height * (s - 1)) / 2
                        offset = if (s <= 1.01f) Offset.Zero else Offset((o.x + pan.x).coerceIn(-maxX, maxX), (o.y + pan.y).coerceIn(-maxY, maxY))
                        event.changes.forEach { if (it.positionChanged()) it.consume() }
                    }
                } while (event.changes.any { it.pressed })
            }
        }
        .graphicsLayer { scaleX = scale; scaleY = scale; translationX = offset.x; translationY = offset.y }) {
        val f = minOf(size.width / w, size.height / h)
        val left = (size.width - w * f) / 2; val top = (size.height - h * f) / 2
        drawImage(bmp.asImageBitmap(), dstOffset = androidx.compose.ui.unit.IntOffset(left.toInt(), top.toInt()),
            dstSize = androidx.compose.ui.unit.IntSize((w * f).toInt(), (h * f).toInt()), filterQuality = androidx.compose.ui.graphics.FilterQuality.High)
        tile?.let { (t, r) ->
            drawImage(t.asImageBitmap(), dstOffset = androidx.compose.ui.unit.IntOffset((left + r.left * f).toInt(), (top + r.top * f).toInt()),
                dstSize = androidx.compose.ui.unit.IntSize((r.width() * f).toInt().coerceAtLeast(1), (r.height() * f).toInt().coerceAtLeast(1)),
                filterQuality = androidx.compose.ui.graphics.FilterQuality.High)
        }
    }
}

/**
 * Original zum Hineinzoomen: kleine Fassung für das ganze Bild (höchstens 2048 px) und Ausschnitte in voller Auflösung über
 * BitmapRegionDecoder. Maße und Ausschnitte gelten im gedrehten Bild (wie aufgenommen); der Decoder kennt nur das ungedrehte.
 */
private class Original(private val decoder: android.graphics.BitmapRegionDecoder, val preview: Bitmap, private val degrees: Int) {
    private val rawW = decoder.width
    private val rawH = decoder.height
    val width get() = if (degrees % 180 == 0) rawW else rawH
    val height get() = if (degrees % 180 == 0) rawH else rawW

    /** Ausschnitt [r] (im gedrehten Bild) mit inSampleSize [sample], gedreht. */
    fun region(r: android.graphics.RectF, sample: Int): Bitmap? = synchronized(this) {
        if (decoder.isRecycled) return null
        val raw = when (degrees) {
            90 -> android.graphics.RectF(r.top, rawH - r.right, r.bottom, rawH - r.left)
            180 -> android.graphics.RectF(rawW - r.right, rawH - r.bottom, rawW - r.left, rawH - r.top)
            270 -> android.graphics.RectF(rawW - r.bottom, r.left, rawW - r.top, r.right)
            else -> r
        }
        val rect = android.graphics.Rect(raw.left.toInt().coerceAtLeast(0), raw.top.toInt().coerceAtLeast(0),
            kotlin.math.ceil(raw.right).toInt().coerceAtMost(rawW), kotlin.math.ceil(raw.bottom).toInt().coerceAtMost(rawH))
        if (rect.isEmpty) return null
        val bmp = try { decoder.decodeRegion(rect, android.graphics.BitmapFactory.Options().apply { inSampleSize = sample }) } catch (_: Exception) { null } ?: return null
        if (degrees == 0) bmp else Bitmap.createBitmap(bmp, 0, 0, bmp.width, bmp.height, android.graphics.Matrix().apply { postRotate(degrees.toFloat()) }, true)
    }

    fun close() = synchronized(this) { decoder.recycle() }

    companion object {
        fun open(file: File): Original? {
            val degrees = when (android.media.ExifInterface(file.path).getAttributeInt(android.media.ExifInterface.TAG_ORIENTATION, android.media.ExifInterface.ORIENTATION_NORMAL)) {
                android.media.ExifInterface.ORIENTATION_ROTATE_90 -> 90
                android.media.ExifInterface.ORIENTATION_ROTATE_180 -> 180
                android.media.ExifInterface.ORIENTATION_ROTATE_270 -> 270
                else -> 0
            }
            val decoder = try {
                if (android.os.Build.VERSION.SDK_INT >= 31) android.graphics.BitmapRegionDecoder.newInstance(file.path)
                else @Suppress("DEPRECATION") android.graphics.BitmapRegionDecoder.newInstance(file.path, false)
            } catch (_: Exception) { null } ?: return null
            // Ganzes Bild verkleinert (die Ausschnitte bringen die Schärfe)
            var sample = 1
            while (maxOf(decoder.width, decoder.height) / sample > 2048) sample *= 2
            val small = android.graphics.BitmapFactory.decodeFile(file.path, android.graphics.BitmapFactory.Options().apply { inSampleSize = sample })
                ?: run { decoder.recycle(); return null }
            val preview = if (degrees == 0) small else Bitmap.createBitmap(small, 0, 0, small.width, small.height, android.graphics.Matrix().apply { postRotate(degrees.toFloat()) }, true)
            return Original(decoder, preview, degrees)
        }
    }
}

/** Datei in den Cache laden (wie "Oeffnen"); null mit Fehlertext. */
@Composable
private fun rememberCached(item: FileItem): Pair<File?, String?> {
    val ctx = LocalContext.current
    val r by produceState<Pair<File?, String?>>(null to null, item) {
        value = try { withContext(Dispatchers.IO) { App.get(ctx).files.cacheFile(item) } to null } catch (e: Exception) { null to (e.message ?: e.toString()) }
    }
    return r
}

/** PDF: Seiten mit dem PdfRenderer von Android (aus der zwischengespeicherten Datei). */
@Composable
private fun PdfPages(item: FileItem) {
    val (file, error) = rememberCached(item)
    if (error != null) { Text(error, color = Color.White); return }
    if (file == null) { CircularProgressIndicator(color = Color.White); return }
    val doc = remember(file) { try { ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY).let { it to PdfRenderer(it) } } catch (_: Exception) { null } }
    DisposableEffect(doc) { onDispose { try { doc?.second?.close(); doc?.first?.close() } catch (_: Exception) { } } }
    if (doc == null) { Text(stringResource(R.string.files_viewer_failed), color = Color.White); return }
    val renderer = doc.second
    val lock = remember { Any() }
    LazyColumn(Modifier.fillMaxSize().background(Color.DarkGray), contentPadding = PaddingValues(8.dp)) {
        items(renderer.pageCount) { n ->
            val bmp by produceState<Bitmap?>(null, n) {
                value = withContext(Dispatchers.IO) {
                    synchronized(lock) {
                        renderer.openPage(n).use { p ->
                            val w = 1240; val h = (w.toFloat() * p.height / p.width).toInt()
                            Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888).also { b -> b.eraseColor(android.graphics.Color.WHITE); p.render(b, null, null, PdfRenderer.Page.RENDER_MODE_FOR_DISPLAY) }
                        }
                    }
                }
            }
            val b = bmp
            if (b == null) Box(Modifier.fillMaxWidth().height(400.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            else Image(b.asImageBitmap(), contentDescription = null, modifier = Modifier.fillMaxWidth().padding(vertical = 4.dp), contentScale = ContentScale.FillWidth)
        }
    }
}

/** Text und Markdown (bis 2 MB), Text markierbar. */
@Composable
private fun TextView(item: FileItem) {
    val (file, error) = rememberCached(item)
    if (error != null) { Text(error, color = Color.White); return }
    if (file == null) { CircularProgressIndicator(color = Color.White); return }
    val text = remember(file) { try { file.inputStream().use { it.readNBytesCompat(2 * 1024 * 1024) }.toString(Charsets.UTF_8) } catch (e: Exception) { e.message ?: "" } }
    Box(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surface)) {
        SelectionContainer(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp)) {
            if (FileViews.isMarkdown(item.name)) Text(markdown(text, MaterialTheme.colorScheme.primary), style = MaterialTheme.typography.bodyMedium)
            else Text(text, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall)
        }
    }
}

private fun java.io.InputStream.readNBytesCompat(max: Int): ByteArray {
    val out = java.io.ByteArrayOutputStream(); val buf = ByteArray(16 * 1024); var total = 0
    while (total < max) { val n = read(buf, 0, minOf(buf.size, max - total)); if (n < 0) break; out.write(buf, 0, n); total += n }
    return out.toByteArray()
}

/** Einfaches Markdown: Ueberschriften, Listen, Zitate, Codebloecke, **fett**, *kursiv*, `Code`. */
internal fun markdown(src: String, accent: Color): AnnotatedString = buildAnnotatedString {
    var code = false
    for (raw in src.lines()) {
        if (raw.trimStart().startsWith("```")) { code = !code; continue }
        if (code) { withStyle(SpanStyle(fontFamily = FontFamily.Monospace, background = Color(0x22888888))) { append(raw) }; append('\n'); continue }
        val h = Regex("^(#{1,6})\\s+(.*)").find(raw)
        when {
            h != null -> withStyle(SpanStyle(fontWeight = FontWeight.Bold, color = accent, fontSize = androidx.compose.ui.unit.TextUnit(22f - h.groupValues[1].length * 2, androidx.compose.ui.unit.TextUnitType.Sp))) { inline(h.groupValues[2]) }
            Regex("^\\s*[-*+]\\s+").containsMatchIn(raw) -> { append("  • "); inline(raw.replaceFirst(Regex("^\\s*[-*+]\\s+"), "")) }
            raw.startsWith(">") -> withStyle(SpanStyle(color = Color.Gray)) { inline(raw.removePrefix(">").trim()) }
            else -> inline(raw)
        }
        append('\n')
    }
}

private fun AnnotatedString.Builder.inline(s: String) {
    var i = 0
    val r = Regex("\\*\\*(.+?)\\*\\*|\\*(.+?)\\*|`(.+?)`")
    for (m in r.findAll(s)) {
        append(s.substring(i, m.range.first))
        when {
            m.groupValues[1].isNotEmpty() -> withStyle(SpanStyle(fontWeight = FontWeight.Bold)) { append(m.groupValues[1]) }
            m.groupValues[2].isNotEmpty() -> withStyle(SpanStyle(fontStyle = androidx.compose.ui.text.font.FontStyle.Italic)) { append(m.groupValues[2]) }
            else -> withStyle(SpanStyle(fontFamily = FontFamily.Monospace, background = Color(0x22888888))) { append(m.groupValues[3]) }
        }
        i = m.range.last + 1
    }
    append(s.substring(i))
}

/** Audio und Video direkt vom Server gestreamt (Ticket bzw. Anmeldung im Kopf, Spulen ueber Bereichsanfragen). */
@Composable
private fun MediaPlayerView(item: FileItem) {
    val ctx = LocalContext.current
    var error by remember { mutableStateOf<String?>(null) }
    // Das Ticket kommt im Umschlag vom Server - also nicht im Hauptthread
    val stream by produceState<Pair<String, Map<String, String>>?>(null, item.path) {
        value = try { kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { App.get(ctx).files.streamUrl(item) } }
            catch (e: Exception) { error = e.message ?: e.toString(); null }
    }
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        val (url, headers) = stream ?: run {
            if (error == null) CircularProgressIndicator()
            else Text(stringResource(R.string.files_viewer_failed) + " (" + error + ")", color = Color.White, modifier = Modifier.padding(24.dp))
            return@Box
        }
        AndroidView(modifier = Modifier.fillMaxWidth(), factory = { c ->
            VideoView(c).apply {
                val mc = MediaController(c); mc.setAnchorView(this); setMediaController(mc)
                setOnPreparedListener { start(); mc.show(0) }
                setOnErrorListener { _, what, extra -> error = "$what/$extra"; true }
                // 805: Bild laesst sich auf diesem Geraet nicht dekodieren (Ton laeuft ggf. weiter)
                setOnInfoListener { _, what, _ -> if (what == android.media.MediaPlayer.MEDIA_INFO_VIDEO_NOT_PLAYING) error = "805"; false }
                setVideoURI(Uri.parse(url), headers)
            }
        }, onRelease = { it.stopPlayback() })
        if (FileViews.kindOf(item) == FileViews.AUDIO) Text(item.name, color = Color.White, modifier = Modifier.align(Alignment.TopCenter).padding(24.dp))
        error?.let { Text(stringResource(R.string.files_viewer_failed) + " ($it)", color = Color.White, modifier = Modifier.align(Alignment.BottomCenter).padding(24.dp)) }
    }
}

/** Bildinfos eines Fotos: nur Angaben, die es gibt. */
@Composable
private fun PhotoInfoSheet(item: FileItem, m: at.dimes.dmoffice.files.PhotoMeta?, onSaved: () -> Unit, onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    // Titel und Beschreibung wie in Lightroom (dc:title, dc:description) - gleich hier bearbeiten
    var title by remember(item.path) { mutableStateOf(m?.title ?: "") }
    var desc by remember(item.path) { mutableStateOf(m?.description ?: "") }
    var busy by remember { mutableStateOf(false) }
    // Sterne: gleich gespeichert; derselbe Stern noch einmal entfernt die Bewertung
    var rating by remember(item.path) { mutableStateOf(m?.rating ?: 0) }
    val changed = title != (m?.title ?: "") || desc != (m?.description ?: "")
    val saved = stringResource(R.string.photo_info_saved)
    val fmt = java.text.DateFormat.getDateTimeInstance(java.text.DateFormat.MEDIUM, java.text.DateFormat.MEDIUM)
    fun num(v: Double) = if (v % 1.0 == 0.0) v.toInt().toString() else "%.1f".format(v)
    // Wie die Details im Web: Datei, dann Aufnahme - je Angabe eine Zeile, Bezeichnung links
    val rows = buildList {
        add(stringResource(R.string.photo_info_name) to item.name)
        item.mime?.let { add(stringResource(R.string.photo_info_kind) to it) }
        if (item.size > 0) add(stringResource(R.string.photo_info_size) to android.text.format.Formatter.formatShortFileSize(ctx, item.size))
        if (item.modifiedMs > 0) add(stringResource(R.string.photo_info_modified) to fmt.format(java.util.Date(item.modifiedMs)))
        item.path.substringBeforeLast('/', "").let { dir ->
            val shown = if (dir.startsWith("ext:")) dir.substringAfter('/', "") else dir
            if (shown.isNotEmpty()) add(stringResource(R.string.photo_info_folder) to "/" + shown.trimStart('/'))
        }
        m?.taken?.let { add(stringResource(R.string.photo_info_taken) to fmt.format(java.util.Date(it))) }
        m?.camera?.let { add(stringResource(R.string.photo_info_camera) to it) }
        m?.lens?.let { add(stringResource(R.string.photo_info_lens) to it) }
        m?.focal?.let { add(stringResource(R.string.photo_info_focal) to "${Math.round(it)} mm") }
        m?.aperture?.let { add(stringResource(R.string.photo_info_aperture) to "f/" + num(it)) }
        m?.exposure?.let { add(stringResource(R.string.photo_info_shutter) to if (it < 1) "1/" + Math.round(1 / it) + " s" else num(it) + " s") }
        m?.iso?.let { add(stringResource(R.string.photo_info_iso) to it.toString()) }
        if (m?.width != null && m.height != null) add(stringResource(R.string.photo_info_dimensions) to "${m.width} × ${m.height} · ${"%.1f".format(m.width * m.height / 1e6)} MP")
        if (m?.lat != null && m.lon != null) add(stringResource(R.string.photo_place) to "%.5f, %.5f".format(java.util.Locale.ROOT, m.lat, m.lon))
        m?.keywords?.takeIf { it.isNotBlank() }?.let { add(stringResource(R.string.photo_info_keywords) to it) }
    }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.photo_info_show)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(6.dp)) {
                androidx.compose.foundation.layout.Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Text(stringResource(R.string.photo_info_rating), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.width(118.dp))
                    for (n in 1..5) IconButton(enabled = !busy, modifier = Modifier.size(40.dp), onClick = {
                        val v = if (rating == n) 0 else n
                        rating = v; busy = true
                        scope.launch {
                            val failed = try { withContext(Dispatchers.IO) { at.dimes.dmoffice.App.get(ctx).files.photoInfo(at.dimes.dmoffice.files.PhotoInfoBody(listOf(item.path), rating = v)) }.firstOrNull()?.error }
                                catch (e: Exception) { e.message ?: e.toString() }
                            busy = false
                            if (failed != null) android.widget.Toast.makeText(ctx, failed, android.widget.Toast.LENGTH_SHORT).show() else onSaved()
                        }
                    }) {
                        Icon(if (n <= rating) Icons.Default.Star else Icons.Default.StarBorder, contentDescription = null,
                            tint = if (n <= rating) androidx.compose.ui.graphics.Color(0xFFE8A33D) else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                androidx.compose.material3.OutlinedTextField(title, { title = it }, label = { Text(stringResource(R.string.photo_info_title)) }, singleLine = true, modifier = Modifier.fillMaxWidth())
                androidx.compose.material3.OutlinedTextField(desc, { desc = it }, label = { Text(stringResource(R.string.photo_info_caption)) }, minLines = 2, modifier = Modifier.fillMaxWidth())
                for ((label, value) in rows) androidx.compose.foundation.layout.Row {
                    Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.width(118.dp))
                    Text(value, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f))
                }
            }
        },
        confirmButton = {
            if (changed) TextButton(enabled = !busy, onClick = {
                busy = true
                scope.launch {
                    val failed = try { withContext(Dispatchers.IO) { at.dimes.dmoffice.App.get(ctx).files.photoInfo(at.dimes.dmoffice.files.PhotoInfoBody(listOf(item.path), title = title, description = desc)) }.firstOrNull()?.error }
                        catch (e: Exception) { e.message ?: e.toString() }
                    busy = false
                    android.widget.Toast.makeText(ctx, failed ?: saved, android.widget.Toast.LENGTH_SHORT).show()
                    if (failed == null) { onSaved(); onDismiss() }
                }
            }) { Text(stringResource(R.string.save)) }
            else TextButton(onClick = onDismiss) { Text(stringResource(R.string.close)) }
        },
        dismissButton = { if (changed) TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}
