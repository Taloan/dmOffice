package at.dimes.dmoffice.ui.dms

import android.app.Activity
import android.content.Context
import android.content.ContextWrapper
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.graphics.Paint
import android.graphics.pdf.PdfDocument
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.IntentSenderRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.systemGestureExclusion
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import com.google.android.gms.common.ConnectionResult
import com.google.android.gms.common.GoogleApiAvailability
import com.google.mlkit.vision.documentscanner.GmsDocumentScannerOptions
import com.google.mlkit.vision.documentscanner.GmsDocumentScanning
import com.google.mlkit.vision.documentscanner.GmsDocumentScanningResult
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File
import kotlin.math.hypot

/*
 * Dokumente abfotografieren (docs/dms-konzept.md, Stufe 4). Mit Google-Play-Diensten der ML Kit Document Scanner
 * (Rahmen erkennen, entzerren, mehrere Seiten, liefert eine PDF; die App braucht keine Kamera-Berechtigung). Ohne sie oder
 * wenn der Scanner nicht startet: Foto mit der Kamera-App, Ecken von Hand setzen, entzerren (Matrix.setPolyToPoly), Seiten
 * sammeln, PDF mit PdfDocument. Texterkennung und PDF/A macht danach der Server.
 * Der Rezept-Scan der Notizen (docs/rezept-scan-konzept.md) nutzt denselben Scanner, bekommt aber die Seiten als JPEG.
 */

/** Abfotografieren: [start] beginnt; der eigene Weg zeigt sich über [Screen] an Stelle des Bereichs. */
class DmsScanner internal constructor(val start: () -> Unit, internal val manual: androidx.compose.runtime.MutableState<Boolean>, internal val onPdf: (File) -> Unit,
    internal val onImages: ((List<File>) -> Unit)? = null)

/** Eigener Weg offen: zeigt ihn und liefert true (der Aufrufer zeigt dann nichts anderes). */
@Composable
fun DmsScanner.Screen(): Boolean {
    if (!manual.value) return false
    ManualScan(images = onImages != null, onDone = { files ->
        manual.value = false
        if (!files.isNullOrEmpty()) onImages?.invoke(files) ?: onPdf(files.first())
    })
    return true
}

/** [onPdf] bekommt die fertige PDF im Zwischenspeicher. */
@Composable
fun rememberDmsScanner(onPdf: (File) -> Unit): DmsScanner = rememberScanner(onPdf, null)

/** Wie [rememberDmsScanner], aber [onPages] bekommt die Seiten als JPEG im Zwischenspeicher (höchstens [limit]). */
@Composable
fun rememberPageScanner(limit: Int = 10, onPages: (List<File>) -> Unit): DmsScanner = rememberScanner({}, onPages, limit)

@Composable
private fun rememberScanner(onPdf: (File) -> Unit, onImages: ((List<File>) -> Unit)?, limit: Int = 50): DmsScanner {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val manualState = remember { mutableStateOf(false) }
    var manual by manualState
    val mlkit = rememberLauncherForActivityResult(ActivityResultContracts.StartIntentSenderForResult()) { r ->
        if (r.resultCode != Activity.RESULT_OK) return@rememberLauncherForActivityResult
        val result = GmsDocumentScanningResult.fromActivityResultIntent(r.data) ?: return@rememberLauncherForActivityResult
        if (onImages != null) {
            val uris = result.pages?.map { it.imageUri }.orEmpty()
            scope.launch {
                val files = withContext(Dispatchers.IO) {
                    uris.mapIndexedNotNull { i, u ->
                        val out = pageFile(ctx, i)
                        try { ctx.contentResolver.openInputStream(u)?.use { s -> out.outputStream().use { o -> s.copyTo(o) } }; out } catch (_: Exception) { null }
                    }
                }
                if (files.isNotEmpty()) onImages(files)
            }
            return@rememberLauncherForActivityResult
        }
        val uri = result.pdf?.uri ?: return@rememberLauncherForActivityResult
        scope.launch {
            val f = withContext(Dispatchers.IO) {
                val out = scanFile(ctx)
                try { ctx.contentResolver.openInputStream(uri)?.use { i -> out.outputStream().use { o -> i.copyTo(o) } }; out } catch (_: Exception) { null }
            }
            if (f != null) onPdf(f)
        }
    }
    return remember {
        DmsScanner(start = {
            val activity = ctx.activity()
            val play = try { GoogleApiAvailability.getInstance().isGooglePlayServicesAvailable(ctx) == ConnectionResult.SUCCESS } catch (_: Throwable) { false }
            if (!play || activity == null) manual = true
            else {
                val options = GmsDocumentScannerOptions.Builder()
                    .setGalleryImportAllowed(true)
                    .setPageLimit(limit)
                    .setResultFormats(if (onImages != null) GmsDocumentScannerOptions.RESULT_FORMAT_JPEG else GmsDocumentScannerOptions.RESULT_FORMAT_PDF)
                    .setScannerMode(GmsDocumentScannerOptions.SCANNER_MODE_FULL)
                    .build()
                // Startet der Scanner nicht (Modul fehlt, altes Gerät): eigener Weg
                GmsDocumentScanning.getClient(options).getStartScanIntent(activity)
                    .addOnSuccessListener { mlkit.launch(IntentSenderRequest.Builder(it).build()) }
                    .addOnFailureListener { manual = true }
            }
        }, manual = manualState, onPdf = onPdf, onImages = onImages)
    }
}

private fun Context.activity(): Activity? {
    var c: Context? = this
    while (c is ContextWrapper) { if (c is Activity) return c; c = c.baseContext }
    return null
}

/** Ziel der fertigen PDF: „Scan 2026-10-30 14.05.12.pdf“ (der Server nimmt bei solchen Namen die erste Zeile als Titel). */
private fun scanFile(ctx: Context): File {
    val dir = File(ctx.cacheDir, "files/dms").apply { mkdirs() }
    val stamp = java.time.LocalDateTime.now().format(java.time.format.DateTimeFormatter.ofPattern("yyyy-MM-dd HH.mm.ss"))
    return File(dir, "Scan $stamp.pdf")
}

/** Seite [i] des Rezept-Scans als JPEG im Zwischenspeicher. */
private fun pageFile(ctx: Context, i: Int): File = File(File(ctx.cacheDir, "files/recipe-scan").apply { mkdirs() }, "seite-${System.currentTimeMillis()}-${i + 1}.jpg")

/** Eigener Weg ohne Play-Dienste: aufnehmen, Ecken setzen, nächste Seite oder fertig; [images]: Seiten als JPEG statt einer PDF. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ManualScan(images: Boolean, onDone: (List<File>?) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val pages = remember { mutableStateListOf<Bitmap>() }
    var photo by remember { mutableStateOf<Bitmap?>(null) }
    var corners by remember { mutableStateOf<List<Offset>>(emptyList()) }
    var busy by remember { mutableStateOf(false) }
    val shot = remember { File(File(ctx.cacheDir, "camera").apply { mkdirs() }, "dms-scan.jpg") }
    val uri = remember { androidx.core.content.FileProvider.getUriForFile(ctx, ctx.packageName + ".files", shot) }
    fun cancel() { shot.delete(); onDone(null) }
    val camera = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { ok ->
        if (!ok) { if (pages.isEmpty()) cancel(); return@rememberLauncherForActivityResult }
        scope.launch {
            busy = true
            val b = withContext(Dispatchers.IO) { loadUpright(shot, 2400) }
            shot.delete()
            busy = false
            if (b == null) return@launch
            photo = b
            // Etwas vom Rand weg: am Rand greift sonst die Zurück-Geste des Systems
            val dx = b.width * 0.12f; val dy = b.height * 0.08f
            corners = listOf(Offset(dx, dy), Offset(b.width - dx, dy), Offset(b.width - dx, b.height - dy), Offset(dx, b.height - dy))
        }
    }
    // Die App führt die Kamera-Berechtigung (QR-Codes); dann verlangt Android sie auch für die Kamera-App
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok ->
        if (ok) camera.launch(uri) else if (pages.isEmpty()) cancel()
    }
    fun shoot() {
        if (androidx.core.content.ContextCompat.checkSelfPermission(ctx, android.Manifest.permission.CAMERA) == android.content.pm.PackageManager.PERMISSION_GRANTED) camera.launch(uri)
        else permission.launch(android.Manifest.permission.CAMERA)
    }
    LaunchedEffect(Unit) { shoot() }

    fun take(next: Boolean) {
        val p = photo ?: return
        busy = true
        scope.launch {
            pages.add(withContext(Dispatchers.Default) { warp(p, corners) })
            photo = null; busy = false
            if (next) shoot()
            else {
                busy = true
                val files = withContext(Dispatchers.IO) {
                    if (images) pages.mapIndexedNotNull { i, b -> toJpeg(b, pageFile(ctx, i)) } else listOfNotNull(toPdf(ctx, pages))
                }
                onDone(files)
            }
        }
    }

    BackHandler { cancel() }
    run {
        Scaffold(contentWindowInsets = androidx.compose.foundation.layout.WindowInsets(0), topBar = {
            TopAppBar(windowInsets = androidx.compose.foundation.layout.WindowInsets(0), title = { Text(stringResource(R.string.dms_scan_page, pages.size + 1)) },
                navigationIcon = { IconButton(onClick = { cancel() }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } })
        }) { pad ->
            Column(Modifier.padding(pad).fillMaxSize()) {
                val p = photo
                Box(Modifier.weight(1f).fillMaxWidth().background(Color.Black), contentAlignment = Alignment.Center) {
                    if (busy || p == null) CircularProgressIndicator()
                    else CornerEditor(p, corners) { corners = it }
                }
                Text(stringResource(R.string.dms_scan_corners), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
                Row(Modifier.fillMaxWidth().padding(16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { photo = null; shoot() }, enabled = !busy) { Text(stringResource(R.string.dms_scan_retake)) }
                    OutlinedButton(onClick = { take(true) }, enabled = !busy && p != null) { Text(stringResource(R.string.dms_scan_next)) }
                    Button(onClick = { take(false) }, enabled = !busy && p != null, modifier = Modifier.weight(1f)) { Text(stringResource(R.string.dms_scan_done)) }
                }
            }
        }
    }
}

/** Bild mit vier verschiebbaren Ecken (Bildkoordinaten); die nächste Ecke folgt dem Finger. */
@Composable
internal fun CornerEditor(bmp: Bitmap, corners: List<Offset>, onChange: (List<Offset>) -> Unit) {
    val image = remember(bmp) { bmp.asImageBitmap() }
    val grab = with(LocalDensity.current) { 48.dp.toPx() }
    val accent = MaterialTheme.colorScheme.primary
    BoxWithConstraints(Modifier.fillMaxSize()) {
        val w = constraints.maxWidth.toFloat(); val h = constraints.maxHeight.toFloat()
        val scale = minOf(w / bmp.width, h / bmp.height)
        val left = (w - bmp.width * scale) / 2; val top = (h - bmp.height * scale) / 2
        fun toView(o: Offset) = Offset(left + o.x * scale, top + o.y * scale)
        Image(image, contentDescription = null, modifier = Modifier.fillMaxSize())
        var current by remember { mutableStateOf(corners) }
        current = corners
        var active by remember { mutableStateOf(-1) }
        // Ecken liegen nah am Rand: dort nicht die Zurück-Geste des Systems
        Canvas(Modifier.fillMaxSize().systemGestureExclusion().pointerInput(bmp) {
            detectDragGestures(
                onDragStart = { at ->
                    val i = current.indices.minByOrNull { (toView(current[it]) - at).getDistance() } ?: -1
                    active = if (i >= 0 && (toView(current[i]) - at).getDistance() < grab * 1.5f) i else -1
                },
                onDragEnd = { active = -1 },
                onDrag = { change, drag ->
                    if (active < 0) return@detectDragGestures
                    change.consume()
                    val o = current[active] + drag / scale
                    val list = current.toMutableList()
                    list[active] = Offset(o.x.coerceIn(0f, bmp.width.toFloat()), o.y.coerceIn(0f, bmp.height.toFloat()))
                    onChange(list)
                })
        }) {
            if (current.size != 4) return@Canvas
            val pts = current.map { toView(it) }
            val path = Path().apply { moveTo(pts[0].x, pts[0].y); for (q in pts.drop(1)) lineTo(q.x, q.y); close() }
            drawPath(path, accent.copy(alpha = 0.18f))
            drawPath(path, accent, style = Stroke(width = 2.dp.toPx()))
            pts.forEachIndexed { i, q ->
                drawCircle(Color.White, radius = 14.dp.toPx(), center = q)
                drawCircle(accent, radius = (if (i == active) 12 else 10).dp.toPx(), center = q)
            }
        }
    }
}

/** Foto laden, verkleinert auf höchstens [max] px, nach der EXIF-Angabe aufgerichtet. */
internal fun loadUpright(f: File, max: Int): Bitmap? {
    val o = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeFile(f.path, o)
    var sample = 1
    while (maxOf(o.outWidth, o.outHeight) / sample > max * 2) sample *= 2
    var b = BitmapFactory.decodeFile(f.path, BitmapFactory.Options().apply { inSampleSize = sample }) ?: return null
    val big = maxOf(b.width, b.height)
    if (big > max) b = Bitmap.createScaledBitmap(b, b.width * max / big, b.height * max / big, true)
    val rotation = try {
        when (android.media.ExifInterface(f.path).getAttributeInt(android.media.ExifInterface.TAG_ORIENTATION, 1)) {
            android.media.ExifInterface.ORIENTATION_ROTATE_90 -> 90f
            android.media.ExifInterface.ORIENTATION_ROTATE_180 -> 180f
            android.media.ExifInterface.ORIENTATION_ROTATE_270 -> 270f
            else -> 0f
        }
    } catch (_: Exception) { 0f }
    return if (rotation == 0f) b else Bitmap.createBitmap(b, 0, 0, b.width, b.height, Matrix().apply { postRotate(rotation) }, true)
}

/** Viereck (oben links, oben rechts, unten rechts, unten links) auf ein Rechteck entzerren. */
internal fun warp(src: Bitmap, c: List<Offset>): Bitmap {
    fun d(a: Offset, b: Offset) = hypot(a.x - b.x, a.y - b.y)
    val w = maxOf(d(c[0], c[1]), d(c[3], c[2])).toInt().coerceAtLeast(1)
    val h = maxOf(d(c[0], c[3]), d(c[1], c[2])).toInt().coerceAtLeast(1)
    val m = Matrix()
    m.setPolyToPoly(floatArrayOf(c[0].x, c[0].y, c[1].x, c[1].y, c[2].x, c[2].y, c[3].x, c[3].y), 0,
        floatArrayOf(0f, 0f, w.toFloat(), 0f, w.toFloat(), h.toFloat(), 0f, h.toFloat()), 0, 4)
    val out = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
    android.graphics.Canvas(out).apply { drawColor(android.graphics.Color.WHITE); drawBitmap(src, m, Paint(Paint.FILTER_BITMAP_FLAG or Paint.ANTI_ALIAS_FLAG)) }
    return out
}

/** Bild als JPEG in [f]; null, wenn das Schreiben scheitert. */
internal fun toJpeg(b: Bitmap, f: File, quality: Int = 88): File? = try {
    f.outputStream().use { b.compress(Bitmap.CompressFormat.JPEG, quality, it) }; f
} catch (_: Exception) { null }

/** Seiten zu einer PDF: Seitenbreite wie A4 (595 pt), Höhe nach dem Seitenverhältnis. */
private fun toPdf(ctx: Context, pages: List<Bitmap>): File? = try {
    val doc = PdfDocument()
    pages.forEachIndexed { i, b ->
        val pw = 595
        val ph = (pw.toFloat() * b.height / b.width).toInt().coerceAtLeast(1)
        val page = doc.startPage(PdfDocument.PageInfo.Builder(pw, ph, i + 1).create())
        page.canvas.drawBitmap(b, null, android.graphics.Rect(0, 0, pw, ph), Paint(Paint.FILTER_BITMAP_FLAG))
        doc.finishPage(page)
    }
    val f = scanFile(ctx)
    f.outputStream().use { doc.writeTo(it) }
    doc.close()
    f
} catch (_: Exception) { null }
