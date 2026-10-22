package at.dimes.dmoffice.ui

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
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.foundation.layout.Box
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
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
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
import java.io.File

/**
 * Betrachter wie im Web: Bilder (Wischen, Zoomen; Bild in 1600 px vom Server), PDF (Seiten), Text und Markdown,
 * Audio und Video (gestreamt mit Bereichsanfragen). Wischen blaettert durch die Dateien der aktuellen Liste.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun FileViewer(items: List<FileItem>, start: Int, onClose: () -> Unit, onShare: (FileItem) -> Unit, onOpenExternal: (FileItem) -> Unit) {
    BackHandler(onBack = onClose)
    val pager = rememberPagerState(initialPage = start.coerceIn(0, (items.size - 1).coerceAtLeast(0))) { items.size }
    var zoomed by remember { mutableStateOf(false) }
    val current = items.getOrNull(pager.currentPage)
    Scaffold(containerColor = Color.Black, topBar = {
        TopAppBar(colors = TopAppBarDefaults.topAppBarColors(containerColor = Color.Black.copy(alpha = 0.6f), titleContentColor = Color.White,
            navigationIconContentColor = Color.White, actionIconContentColor = Color.White),
            title = { Column { Text(current?.name ?: "", maxLines = 1, style = MaterialTheme.typography.titleMedium)
                if (items.size > 1) Text("${pager.currentPage + 1} / ${items.size}", style = MaterialTheme.typography.labelSmall) } },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                if (current != null) {
                    IconButton(onClick = { onShare(current) }) { Icon(Icons.Default.Share, contentDescription = stringResource(R.string.files_share)) }
                    IconButton(onClick = { onOpenExternal(current) }) { Icon(Icons.AutoMirrored.Filled.OpenInNew, contentDescription = stringResource(R.string.files_open_with)) }
                }
            })
    }) { pad ->
        // Gezoomt: Wischen verschiebt das Bild statt zu blaettern
        HorizontalPager(pager, modifier = Modifier.fillMaxSize().padding(pad), userScrollEnabled = !zoomed, key = { items[it].path }) { page ->
            val item = items[page]
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                when (FileViews.kindOf(item)) {
                    FileViews.IMAGE -> ZoomableImage(item, onZoom = { if (page == pager.currentPage) zoomed = it })
                    FileViews.PDF -> PdfPages(item)
                    FileViews.TEXT -> TextView(item)
                    FileViews.AUDIO, FileViews.VIDEO -> if (page == pager.currentPage) MediaPlayerView(item) else Text(item.name, color = Color.White)
                    else -> Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Text(item.name, color = Color.White)
                        Spacer(Modifier.height(12.dp))
                        androidx.compose.material3.Button(onClick = { onOpenExternal(item) }) { Text(stringResource(R.string.files_open_with)) }
                    }
                }
            }
        }
    }
}

/** Bild vom Server (erst klein, dann 1600 px); Zoomen mit zwei Fingern, Doppeltippen wechselt 1x/2.5x. */
@Composable
private fun ZoomableImage(item: FileItem, onZoom: (Boolean) -> Unit) {
    val ctx = LocalContext.current
    val small by produceState(Thumbs.cached(item, Thumbs.SMALL), item) { if (value == null) value = try { Thumbs.load(ctx, item, Thumbs.SMALL) } catch (_: Exception) { null } }
    val large by produceState<Bitmap?>(Thumbs.cached(item, Thumbs.LARGE), item) { if (value == null) value = try { Thumbs.load(ctx, item, Thumbs.LARGE) } catch (_: Exception) { null } }
    var scale by remember { mutableFloatStateOf(1f) }
    var offset by remember { mutableStateOf(Offset.Zero) }
    LaunchedEffect(scale) { onZoom(scale > 1.01f) }
    val bmp = large ?: small
    if (bmp == null) { CircularProgressIndicator(color = Color.White); return }
    Image(bmp.asImageBitmap(), contentDescription = item.name, contentScale = ContentScale.Fit,
        modifier = Modifier.fillMaxSize()
            .pointerInput(item.path) {
                detectTapGestures(onDoubleTap = { p ->
                    if (scale > 1.01f) { scale = 1f; offset = Offset.Zero } else { scale = 2.5f; offset = (Offset(size.width / 2f, size.height / 2f) - p) * 1.5f }
                })
            }
            .pointerInput(item.path) {
                // Nur mit zwei Fingern oder gezoomt: sonst bleibt das Wischen beim Blaettern (Pager)
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    do {
                        val event = awaitPointerEvent()
                        if (event.changes.size > 1 || scale > 1.01f) {
                            val s = (scale * event.calculateZoom()).coerceIn(1f, 6f)
                            val pan = event.calculatePan()
                            scale = s
                            // Beim Verschieben nicht ueber den Rand hinaus
                            val maxX = (size.width * (s - 1)) / 2; val maxY = (size.height * (s - 1)) / 2
                            offset = if (s <= 1.01f) Offset.Zero else Offset((offset.x + pan.x).coerceIn(-maxX, maxX), (offset.y + pan.y).coerceIn(-maxY, maxY))
                            event.changes.forEach { if (it.positionChanged()) it.consume() }
                        }
                    } while (event.changes.any { it.pressed })
                }
            }
            .graphicsLayer { scaleX = scale; scaleY = scale; translationX = offset.x; translationY = offset.y })
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
