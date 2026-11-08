package at.dimes.dmoffice.ui

import android.graphics.Bitmap
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MyLocation
import androidx.compose.material.icons.automirrored.filled.RotateLeft
import androidx.compose.material.icons.automirrored.filled.RotateRight
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.Thumbs
import kotlin.math.roundToInt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * Foto zuschneiden und drehen (wie im Web): große Vorschau gedreht, darüber der Ausschnitt - Ecken ziehen, Fläche verschieben,
 * Seitenverhältnisse. Gespeichert rechnet der Server aus dem Original (Metadaten bleiben), als Kopie oder überschreibend.
 */
@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class, androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
internal fun PhotoEditScreen(item: FileItem, canOverwrite: Boolean, onDone: (String?) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var bmp by remember { mutableStateOf<Bitmap?>(null) }
    var rotate by remember { mutableStateOf(0) }
    // Ausschnitt in Anteilen des gedrehten Bildes
    var crop by remember { mutableStateOf(floatArrayOf(0f, 0f, 1f, 1f)) }
    var ratio by remember { mutableStateOf<Float?>(null) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var area by remember { mutableStateOf(IntSize.Zero) }
    // Regler (0 = unverändert); die Vorschau rechnet der Server wie beim Speichern
    var adjust by remember { mutableStateOf(emptyMap<String, Float>()) }
    var tuned by remember { mutableStateOf<Bitmap?>(null) }
    var tab by remember { mutableStateOf(0) }
    var ai by remember { mutableStateOf(false) }
    var engine by remember { mutableStateOf(false) }
    var asking by remember { mutableStateOf(false) }
    var analysis by remember { mutableStateOf<String?>(null) }
    // Letzter Vorschlag und wie stark er gilt (Prozent, alle vorgeschlagenen Regler gleichmäßig)
    var suggested by remember { mutableStateOf<Map<String, Float>?>(null) }
    var strength by remember { mutableStateOf(100f) }
    val files = App.get(ctx).files
    val neutral = adjust.values.all { kotlin.math.abs(it) < 0.005f }
    LaunchedEffect(item.path) { bmp = try { Thumbs.load(ctx, item, Thumbs.LARGE) } catch (_: Exception) { null } }
    // Im Tresor („tresor:…“) kein Vorschlag der KI - sie hat dort grundsätzlich keinen Zugriff
    LaunchedEffect(Unit) { val r = withContext(Dispatchers.IO) { files.photoAiAvailable() }; ai = r.available && !item.path.startsWith("tresor:"); engine = r.engine }
    LaunchedEffect(adjust) {
        if (neutral) { tuned = null; return@LaunchedEffect }
        kotlinx.coroutines.delay(300)
        tuned = try { withContext(Dispatchers.IO) { files.photoPreview(item.path, adjust).let { android.graphics.BitmapFactory.decodeByteArray(it, 0, it.size) } } } catch (_: Exception) { tuned }
    }
    val base = if (neutral) bmp else tuned ?: bmp
    val shown = remember(base, rotate) {
        base?.let { b -> if (rotate == 0) b else Bitmap.createBitmap(b, 0, 0, b.width, b.height, android.graphics.Matrix().apply { postRotate(rotate.toFloat()) }, true) }
    }
    fun turn(d: Int) { rotate = (rotate + d + 360) % 360; crop = floatArrayOf(0f, 0f, 1f, 1f); ratio = null }
    fun applyRatio(r: Float?) {
        ratio = r
        val s = shown ?: return
        if (r == null) return
        val box = s.width.toFloat() / s.height
        crop = if (box > r) floatArrayOf((1 - r / box) / 2, 0f, r / box, 1f) else floatArrayOf(0f, (1 - box / r) / 2, 1f, box / r)
    }
    val full = crop[0] < 0.001f && crop[1] < 0.001f && crop[2] > 0.999f && crop[3] > 0.999f
    fun save(overwrite: Boolean) = scope.launch {
        busy = true; error = null
        try {
            val path = withContext(Dispatchers.IO) { files.photoEdit(item.path, rotate, if (full) null else crop.toList(), overwrite, if (neutral) null else adjust) }
            onDone(path)
        } catch (e: Exception) { error = e.message ?: e.toString() }
        busy = false
    }
    // Regler wie in Lightroom: nach Gruppen, Name links, Wert rechts, darunter eine schmale Linie
    val sliderGroups = listOf(
        R.string.photo_group_light to listOf(
            Triple("exposure", R.string.photo_exposure, -1.5f..1.5f), Triple("contrast", R.string.photo_contrast, -60f..60f),
            Triple("highlights", R.string.photo_highlights, -60f..60f), Triple("shadows", R.string.photo_shadows, -60f..60f),
            Triple("whites", R.string.photo_whites, -50f..50f), Triple("blacks", R.string.photo_blacks, -50f..50f)),
        R.string.photo_group_color to listOf(
            Triple("temperature", R.string.photo_temperature, -50f..50f), Triple("tint", R.string.photo_tint, -50f..50f),
            Triple("vibrance", R.string.photo_vibrance, -50f..50f), Triple("saturation", R.string.photo_saturation, -50f..50f)),
        R.string.photo_group_effects to listOf(
            Triple("clarity", R.string.photo_clarity, -100f..100f), Triple("dehaze", R.string.photo_dehaze, -100f..100f)))
    // -1: „Auto“ (Vorschlag der KI bzw. der Photo-Engine), -2: Farbmischer, sonst Index in sliderGroups
    var group by remember { mutableStateOf(0) }
    // Farbmischer: Reiter (h, s, l) und Zielwerkzeug (im Bild auf eine Farbe drücken und hoch oder runter ziehen)
    var mixPart by remember { mutableStateOf('h') }
    var target by remember { mutableStateOf(false) }

    androidx.compose.ui.window.Dialog(onDismissRequest = { onDone(null) }, properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
    Surface(Modifier.fillMaxSize(), color = Color.Black, contentColor = Color.White) {
    // Querformat (Tablet, aufgeklapptes Handy): Bild links, Bedienfeld rechts; Hochformat: Bild oben, Bedienfeld unten
    androidx.compose.foundation.layout.BoxWithConstraints(Modifier.fillMaxSize().systemBarsPadding().padding(8.dp)) {
        val wide = maxWidth > maxHeight && maxWidth >= 600.dp

        val topBar: @Composable () -> Unit = {
            Row(verticalAlignment = Alignment.CenterVertically) {
                IconButton(onClick = { onDone(null) }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) }
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    FilterChip(selected = tab == 0, onClick = { tab = 0 }, label = { Text(stringResource(R.string.photo_tab_crop)) })
                    FilterChip(selected = tab == 1, onClick = { tab = 1 }, label = { Text(stringResource(R.string.photo_tab_adjust)) })
                }
                Spacer(Modifier.weight(1f))
                if (tab == 0) {
                    IconButton(onClick = { turn(-90) }) { Icon(Icons.AutoMirrored.Filled.RotateLeft, contentDescription = stringResource(R.string.photo_rotate_left)) }
                    IconButton(onClick = { turn(90) }) { Icon(Icons.AutoMirrored.Filled.RotateRight, contentDescription = stringResource(R.string.photo_rotate_right)) }
                }
            }
        }

        val imageArea: @Composable (Modifier) -> Unit = { mod ->
            Box(mod.onSizeChanged { area = it }, contentAlignment = Alignment.Center) {
                val s = shown
                if (s == null) CircularProgressIndicator()
                else {
                    // Bild eingepasst; Ausschnitt in Bildkoordinaten
                    val f = minOf(area.width.toFloat() / s.width, area.height.toFloat() / s.height)
                    val w = s.width * f; val h = s.height * f
                    val left = (area.width - w) / 2; val top = (area.height - h) / 2
                    Canvas(Modifier.fillMaxSize().pointerInput(s, ratio, tab, target, group, mixPart) {
                        // Zielwerkzeug des Farbmischers: Farbe unter dem Finger, dann hoch = mehr, runter = weniger, anteilig auf die Bänder
                        if (tab == 1 && group == -2 && target) {
                            var weights = emptyMap<String, Float>(); var start = emptyMap<String, Float>(); var dy = 0f
                            detectDragGestures(onDragStart = { pt ->
                                val x = ((pt.x - left) / f).toInt(); val y = ((pt.y - top) / f).toInt()
                                weights = if (x in 0 until s.width && y in 0 until s.height) ColorMix.weightsAt(s, x, y) else emptyMap()
                                start = weights.keys.associateWith { adjust[ColorMix.key(mixPart, it)] ?: 0f }; dy = 0f
                            }) { change, drag ->
                                change.consume()
                                if (weights.isEmpty()) return@detectDragGestures
                                dy += drag.y
                                val delta = -dy / density * 0.6f
                                adjust = adjust + weights.map { (b, w) -> ColorMix.key(mixPart, b) to Math.round(((start[b] ?: 0f) + delta * w).coerceIn(-100f, 100f)).toFloat() }
                            }
                            return@pointerInput
                        }
                        // Ziehen nur beim Zuschneiden
                        if (tab != 0) return@pointerInput
                        var mode = ""
                        detectDragGestures(onDragStart = { p ->
                            val cx = left + crop[0] * w; val cy = top + crop[1] * h; val cw = crop[2] * w; val ch = crop[3] * h
                            val near = 56f
                            fun at(x: Float, y: Float) = kotlin.math.abs(p.x - x) < near && kotlin.math.abs(p.y - y) < near
                            mode = when {
                                at(cx, cy) -> "nw"; at(cx + cw, cy) -> "ne"; at(cx, cy + ch) -> "sw"; at(cx + cw, cy + ch) -> "se"
                                p.x in cx..cx + cw && p.y in cy..cy + ch -> "move"; else -> ""
                            }
                        }) { change, drag ->
                            change.consume()
                            if (mode.isEmpty()) return@detectDragGestures
                            val dx = drag.x / w; val dy = drag.y / h
                            var (x, y, cw, ch) = crop.toList()
                            if (mode == "move") { x = (x + dx).coerceIn(0f, 1f - cw); y = (y + dy).coerceIn(0f, 1f - ch) }
                            else {
                                if ('w' in mode) { val nx = (x + dx).coerceIn(0f, x + cw - 0.05f); cw += x - nx; x = nx }
                                if ('e' in mode) cw = (cw + dx).coerceIn(0.05f, 1f - x)
                                val r = ratio
                                if (r != null) { val nh = cw * w / r / h; if ('n' in mode) y += ch - nh; ch = nh }
                                else {
                                    if ('n' in mode) { val ny = (y + dy).coerceIn(0f, y + ch - 0.05f); ch += y - ny; y = ny }
                                    if ('s' in mode) ch = (ch + dy).coerceIn(0.05f, 1f - y)
                                }
                                if (x < 0 || y < 0 || x + cw > 1.0001f || y + ch > 1.0001f) return@detectDragGestures
                            }
                            crop = floatArrayOf(x, y, cw, ch)
                        }
                    }) {
                        drawImage(s.asImageBitmap(), dstOffset = IntOffset(left.toInt(), top.toInt()), dstSize = IntSize(w.toInt(), h.toInt()))
                        // Rahmen, Abdunkeln und Griffe nur beim Zuschneiden
                        if (tab != 0) return@Canvas
                        val cx = left + crop[0] * w; val cy = top + crop[1] * h; val cw = crop[2] * w; val ch = crop[3] * h
                        val shade = Color.Black.copy(alpha = 0.55f)
                        drawRect(shade, Offset(left, top), Size(w, cy - top))
                        drawRect(shade, Offset(left, cy + ch), Size(w, top + h - cy - ch))
                        drawRect(shade, Offset(left, cy), Size(cx - left, ch))
                        drawRect(shade, Offset(cx + cw, cy), Size(left + w - cx - cw, ch))
                        drawRect(Color.White, Offset(cx, cy), Size(cw, ch), style = Stroke(2.dp.toPx()))
                        for (i in 1..2) {
                            drawLine(Color.White.copy(alpha = 0.4f), Offset(cx + cw * i / 3, cy), Offset(cx + cw * i / 3, cy + ch))
                            drawLine(Color.White.copy(alpha = 0.4f), Offset(cx, cy + ch * i / 3), Offset(cx + cw, cy + ch * i / 3))
                        }
                        for ((px, py) in listOf(cx to cy, cx + cw to cy, cx to cy + ch, cx + cw to cy + ch)) drawCircle(Color.White, 9.dp.toPx(), Offset(px, py))
                    }
                }
            }
        }

        // Inhalt des Bedienfelds: Seitenverhältnisse (Zuschnitt) bzw. die Regler der gewählten Gruppe oder „Auto“
        val panel: @Composable (Modifier) -> Unit = { mod ->
            Column(mod.verticalScroll(rememberScrollState())) {
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                if (tab == 0) Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for ((label, r) in listOf(stringResource(R.string.photo_ratio_free) to null, "1:1" to 1f, "4:3" to 4f / 3, "3:2" to 1.5f, "16:9" to 16f / 9, stringResource(R.string.photo_ratio_portrait) to 0.75f))
                        FilterChip(selected = ratio == r, onClick = { applyRatio(r) }, label = { Text(label) })
                }
                else if (group == -1) {
                    // Vorschlag von der KI oder der Photo-Engine - beide belegen nur die Regler vor, entschieden wird hier
                    fun ask(from: String) = scope.launch {
                        asking = true
                        try { val r = withContext(Dispatchers.IO) { files.photoSuggest(item.path, from) }
                            suggested = r.values; adjust = r.values.mapValues { it.value * strength / 100f }; analysis = r.analysis }
                        catch (e: Exception) { analysis = e.message ?: e.toString() }
                        asking = false
                    }
                    androidx.compose.foundation.layout.FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                        if (ai) OutlinedButton(onClick = { ask("suggest") }, enabled = !asking) { Icon(Icons.Default.AutoAwesome, contentDescription = null); Spacer(Modifier.width(4.dp)); Text(stringResource(R.string.photo_ai)) }
                        if (engine) OutlinedButton(onClick = { ask("engine") }, enabled = !asking) { Text(stringResource(R.string.photo_engine)) }
                        androidx.compose.material3.TextButton(onClick = { adjust = emptyMap(); analysis = null; suggested = null }, enabled = !neutral) { Text(stringResource(R.string.photo_reset)) }
                        if (asking) CircularProgressIndicator(Modifier.size(20.dp))
                    }
                    suggested?.let { sug ->
                        Row(Modifier.fillMaxWidth().padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                            Text(stringResource(R.string.photo_strength), style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
                            Text("${strength.toInt()} %", style = MaterialTheme.typography.bodyLarge)
                        }
                        androidx.compose.material3.Slider(strength, { p -> strength = (p / 5f).roundToInt() * 5f; adjust = adjust + sug.mapValues { it.value * strength / 100f } },
                            valueRange = 0f..150f, modifier = Modifier.fillMaxWidth().height(32.dp),
                            thumb = {
                                Box(Modifier.size(22.dp).background(Color.Black, androidx.compose.foundation.shape.CircleShape)
                                    .border(2.dp, Color.White, androidx.compose.foundation.shape.CircleShape))
                            },
                            track = { Box(Modifier.fillMaxWidth().height(2.dp).background(Color.White.copy(alpha = 0.45f))) })
                    }
                    analysis?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = Color.White.copy(alpha = 0.8f)) }
                }
                else if (group == -2) {
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        for ((p, l) in listOf('h' to R.string.photo_mix_hue, 's' to R.string.photo_mix_saturation, 'l' to R.string.photo_mix_luminance))
                            FilterChip(selected = mixPart == p, onClick = { mixPart = p }, label = { Text(stringResource(l)) })
                        Spacer(Modifier.weight(1f))
                        IconButton(onClick = { target = !target }, modifier = Modifier.background(if (target) Color.White.copy(alpha = 0.25f) else Color.Transparent, androidx.compose.foundation.shape.CircleShape)) {
                            Icon(Icons.Default.MyLocation, contentDescription = stringResource(R.string.photo_mix_target))
                        }
                    }
                    if (target) Text(stringResource(R.string.photo_mix_target_hint), style = MaterialTheme.typography.bodySmall, color = Color.White.copy(alpha = 0.7f))
                    for ((band, label, center) in ColorMix.BANDS) {
                        val key = ColorMix.key(mixPart, band); val v = adjust[key] ?: 0f
                        Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                            Box(Modifier.size(12.dp).background(ColorMix.color(center), androidx.compose.foundation.shape.CircleShape))
                            Text(stringResource(label), style = MaterialTheme.typography.bodyLarge, modifier = Modifier.padding(start = 8.dp).weight(1f))
                            Text("%+d".format(v.toInt()), style = MaterialTheme.typography.bodyLarge)
                        }
                        androidx.compose.material3.Slider(v, { adjust = adjust + (key to Math.round(it).toFloat()) }, valueRange = -100f..100f, modifier = Modifier.fillMaxWidth().height(28.dp),
                            thumb = {
                                Box(Modifier.size(20.dp).background(Color.Black, androidx.compose.foundation.shape.CircleShape)
                                    .border(2.dp, Color.White, androidx.compose.foundation.shape.CircleShape))
                            },
                            track = { Box(Modifier.fillMaxWidth().height(6.dp).background(ColorMix.track(mixPart, center), androidx.compose.foundation.shape.RoundedCornerShape(3.dp))) })
                    }
                }
                else for ((key, label, range) in sliderGroups[group].second) {
                    val v = adjust[key] ?: 0f
                    Row(Modifier.fillMaxWidth().padding(top = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(stringResource(label), style = MaterialTheme.typography.bodyLarge, modifier = Modifier.weight(1f))
                        Text(if (key == "exposure") "%+.2f".format(v) else "%+d".format(v.toInt()), style = MaterialTheme.typography.bodyLarge)
                    }
                    androidx.compose.material3.Slider(v, { adjust = adjust + (key to it) }, valueRange = range, modifier = Modifier.fillMaxWidth().height(32.dp),
                        thumb = {
                            Box(Modifier.size(22.dp).background(Color.Black, androidx.compose.foundation.shape.CircleShape)
                                .border(2.dp, Color.White, androidx.compose.foundation.shape.CircleShape))
                        },
                        track = { Box(Modifier.fillMaxWidth().height(2.dp).background(Color.White.copy(alpha = 0.45f))) })
                }
            }
        }

        // Gruppenleiste unten (nur bei den Reglern): Auto, Licht, Farbe, Effekte
        val groupBar: @Composable () -> Unit = {
            if (tab == 1) Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(vertical = 4.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                if (ai || engine) FilterChip(selected = group == -1, onClick = { group = -1 }, label = { Text(stringResource(R.string.photo_group_auto)) },
                    leadingIcon = { Icon(Icons.Default.AutoAwesome, contentDescription = null, modifier = Modifier.size(18.dp)) })
                sliderGroups.forEachIndexed { i, (label, _) -> FilterChip(selected = group == i, onClick = { group = i }, label = { Text(stringResource(label)) }) }
                FilterChip(selected = group == -2, onClick = { group = -2 }, label = { Text(stringResource(R.string.photo_group_mixer)) })
            }
        }

        val footer: @Composable () -> Unit = {
            Row(Modifier.padding(top = 6.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { save(false) }, enabled = !busy && !(full && rotate == 0 && neutral), modifier = Modifier.weight(1f)) { Text(stringResource(R.string.photo_save_copy)) }
                if (canOverwrite) Button(onClick = { save(true) }, enabled = !busy && !(full && rotate == 0 && neutral), modifier = Modifier.weight(1f)) { Text(stringResource(R.string.photo_overwrite)) }
            }
            if (busy) androidx.compose.material3.LinearProgressIndicator(Modifier.fillMaxWidth().padding(top = 6.dp))
        }

        if (wide) Column(Modifier.fillMaxSize()) {
            topBar()
            Row(Modifier.weight(1f).fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                imageArea(Modifier.weight(1f).fillMaxSize())
                Column(Modifier.width(360.dp)) {
                    panel(Modifier.fillMaxWidth().weight(1f))
                    groupBar()
                    footer()
                }
            }
        }
        else Column(Modifier.fillMaxSize()) {
            topBar()
            imageArea(Modifier.weight(1f).fillMaxWidth())
            panel(Modifier.fillMaxWidth().heightIn(max = if (tab == 0) 64.dp else 300.dp))
            groupBar()
            footer()
        }
    }
    }
    }
}
