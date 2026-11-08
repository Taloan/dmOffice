package at.dimes.dmoffice.ui

import android.Manifest
import android.annotation.SuppressLint
import android.app.DatePickerDialog
import android.app.TimePickerDialog
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.OpenableColumns
import android.webkit.WebView
import android.webkit.WebViewClient
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.systemBars
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items as gridItems
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.FormatListBulleted
import androidx.compose.material.icons.automirrored.filled.StickyNote2
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.CloudDownload
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.FormatBold
import androidx.compose.material.icons.filled.FormatItalic
import androidx.compose.material.icons.filled.FormatListNumbered
import androidx.compose.material.icons.filled.FormatStrikethrough
import androidx.compose.material.icons.filled.FormatUnderlined
import androidx.compose.material.icons.filled.Lightbulb
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.MyLocation
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.filled.PhotoLibrary
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material.icons.filled.Smartphone
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.material.icons.filled.Title
import androidx.compose.material.icons.filled.WbSunny
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SmallFloatingActionButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.NoteItem
import at.dimes.dmoffice.journal.DayEvent
import at.dimes.dmoffice.journal.Entry
import at.dimes.dmoffice.journal.EntryInput
import at.dimes.dmoffice.journal.Found
import at.dimes.dmoffice.journal.Media
import at.dimes.dmoffice.journal.Moods
import at.dimes.dmoffice.journal.Place
import at.dimes.dmoffice.journal.Ref
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.coroutines.flow.first
import kotlinx.serialization.json.JsonPrimitive
import java.io.File
import java.time.Instant
import java.time.ZoneId
import java.time.ZonedDateTime
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

/**
 * Tagebucheintrag schreiben bzw. ansehen (Version 2): Datum und Uhrzeit, Stimmung, Titel, Text mit Formatierung und Checklisten
 * (dasselbe HTML wie im Web), Vorschlag aus Terminen und erledigten Aufgaben, Fotos und Videos (kleingerechnet, Datum und Ort aus
 * dem Bild), Sprachaufnahme, Dateien, Notizen verknüpfen, Ort mit Standort, Wetter und Ortsname (eingeschaltet), Schlagwörter,
 * privat, Termine des Tages. Aus einem nur lesbaren geteilten Tagebuch: nur ansehen.
 */
@SuppressLint("SetJavaScriptEnabled")
@OptIn(ExperimentalMaterial3Api::class, androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
fun JournalEditorScreen(journal: Long?, uri: String?, onClose: () -> Unit, shareBatch: String? = null, start: String? = null, day: String? = null) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var entry by remember { mutableStateOf<Entry?>(null) }
    var loaded by remember { mutableStateOf(uri == null) }
    var target by remember { mutableStateOf(journal) }
    var own by remember { mutableStateOf(true) }
    var lookupOn by remember { mutableStateOf(false) }
    // Aus dem Kalender (Tag ohne Eintrag): dieser Tag, Uhrzeit von jetzt
    val forDay = remember(day) { day?.let { try { java.time.LocalDate.parse(it) } catch (_: Exception) { null } } }
    var date by remember { mutableStateOf(ZonedDateTime.now().let { n -> forDay?.let { n.with(it) } ?: n }) }
    var dateTouched by remember { mutableStateOf(uri != null || forDay != null) }
    var title by remember { mutableStateOf("") }
    var mood by remember { mutableStateOf<Int?>(null) }
    var tags by remember { mutableStateOf("") }
    var place by remember { mutableStateOf(Place()) }
    var weather by remember { mutableStateOf("") }
    var private by remember { mutableStateOf(false) }
    var showEvents by remember { mutableStateOf(false) }
    var events by remember { mutableStateOf<List<DayEvent>>(emptyList()) }
    var media by remember { mutableStateOf(listOf<Media>()) }
    var refs by remember { mutableStateOf(listOf<Ref>()) }
    var uploading by remember { mutableStateOf(0) }
    var busy by remember { mutableStateOf(false) }
    var looking by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    var pickNote by remember { mutableStateOf(false) }
    var pickServer by remember { mutableStateOf(false) }
    var suggesting by remember { mutableStateOf(false) }
    var pickGallery by remember { mutableStateOf(false) }
    // Mit KI schreiben: nur mit eingerichteter und freigegebener KI; aiNotes = bisheriger Text beim Öffnen
    var ai by remember { mutableStateOf<at.dimes.dmoffice.journal.AiState?>(null) }
    var aiNotes by remember { mutableStateOf<String?>(null) }
    var recorder by remember { mutableStateOf<VoiceRecorder?>(null) }
    val web = remember { mutableStateOf<WebView?>(null) }
    var html by remember { mutableStateOf("") }
    val readOnly = entry?.canWrite == false
    val placeholder = stringResource(R.string.journal_text_hint)
    fun toast(msg: String?) = Toast.makeText(ctx, msg ?: "?", Toast.LENGTH_LONG).show()
    fun applyHtml() = web.value?.evaluateJavascript("setHtml(${JsonPrimitive(html)}, ${JsonPrimitive(if (readOnly) "" else placeholder)}); e.contentEditable = ${!readOnly}", null)

    LaunchedEffect(journal, uri) {
        try {
            lookupOn = try { app.journal.settings().lookup } catch (_: Exception) { false }
            ai = try { app.journal.ai() } catch (_: Exception) { null }
            if (target == null) target = app.journal.journals().firstOrNull { it.owner == null }?.id
            if (journal != null && uri != null) {
                val e = app.journal.entry(journal, uri)
                entry = e
                own = e.owner == null
                date = Instant.parse(e.date).atZone(ZoneId.systemDefault())
                title = e.title ?: ""; mood = e.mood; tags = e.tags.joinToString(", "); place = e.place ?: Place()
                weather = e.weather ?: ""; private = e.private; showEvents = e.showEvents; media = e.media; refs = e.refs; html = e.html
                applyHtml()
            }
        } catch (e: Exception) { toast(e.message) }
        loaded = true
    }
    // Termine des Tages, wenn gewünscht
    val day = date.toLocalDate().toString()
    LaunchedEffect(showEvents, day) { events = if (showEvents) try { app.journal.events(day) } catch (_: Exception) { emptyList() } else emptyList() }

    /** Wetter und Ortsname holen (nur eingeschaltet) - leere Felder werden gefüllt. */
    fun lookup(p: Place, quiet: Boolean) {
        val lat = p.lat ?: return
        val lon = p.lon ?: return
        if (!lookupOn) return
        looking = true
        scope.launch {
            try {
                val r = app.journal.lookup(lat, lon, date.toInstant().toString())
                r.weather?.let { if (weather.isBlank()) weather = it }
                r.place?.let { if (place.name.isNullOrBlank()) place = place.copy(name = it) }
                if (!quiet && r.weather == null && r.place == null) toast(ctx.getString(R.string.journal_lookup_none))
            } catch (e: Exception) { if (!quiet) toast(e.message) }
            looking = false
        }
    }

    /** Neues Foto: Datum und Ort aus dem Bild, solange der Eintrag noch keine hat. */
    fun adopt(found: List<Found>) {
        media = media + found.map { it.media }.filter { m -> media.none { it.path == m.path } }
        found.mapNotNull { it.media.taken }.minOrNull()?.let { t -> if (!dateTouched) { date = Instant.parse(t).atZone(ZoneId.systemDefault()); dateTouched = true } }
        found.firstOrNull { it.lat != null && it.lon != null }?.let { f -> if (place.lat == null) { place = place.copy(lat = f.lat, lon = f.lon); lookup(place, true) } }
    }

    fun uploadFiles(files: List<Triple<File, String, String?>>, done: () -> Unit = {}) {
        val j = entry?.journal ?: target ?: return done()
        uploading += files.size
        scope.launch {
            val found = mutableListOf<Found>()
            for ((file, name, mime) in files) {
                try { found += app.journal.upload(j, file, name, mime) } catch (e: Exception) { toast(e.message) } finally { file.delete() }
                uploading--
            }
            adopt(found)
            done()
        }
    }

    // Aus dem Teilen-Menü: die Fotos hochladen, sobald das Tagebuch feststeht - Datum und Ort kommen aus dem ersten (adopt)
    var shareStarted by remember { mutableStateOf(false) }
    // Gibt es am Tag des ersten Fotos schon einen Eintrag, fragt die App vorher: dort anhängen oder neuer Eintrag
    var sameDay by remember { mutableStateOf<at.dimes.dmoffice.journal.Preview?>(null) }
    var shareFiles by remember { mutableStateOf<List<File>>(emptyList()) }
    fun uploadShared() {
        val dir = shareBatch?.let { at.dimes.dmoffice.upload.ShareUploadWorker.batchDir(ctx, it) } ?: return
        uploadFiles(shareFiles.map { Triple(it, it.name, at.dimes.dmoffice.files.FilesRepo.guessMime(it.name)) }) { dir.deleteRecursively() }
    }
    /** Fotos an den vorhandenen Eintrag: der Editor zeigt ihn, die Fotos kommen dazu, Speichern übernimmt sie. */
    fun attachTo(p: at.dimes.dmoffice.journal.Preview) {
        scope.launch {
            try {
                val e = app.journal.entry(p.journal, p.uri)
                entry = e; own = e.owner == null; target = e.journal
                date = Instant.parse(e.date).atZone(ZoneId.systemDefault()); dateTouched = true
                title = e.title ?: ""; mood = e.mood; tags = e.tags.joinToString(", "); place = e.place ?: Place()
                weather = e.weather ?: ""; private = e.private; showEvents = e.showEvents; refs = e.refs; html = e.html; media = e.media
                applyHtml()
            } catch (ex: Exception) { toast(ex.message) }
            uploadShared()
        }
    }
    LaunchedEffect(target) {
        val j = target
        if (shareBatch == null || uri != null || j == null || shareStarted) return@LaunchedEffect
        shareStarted = true
        val dir = at.dimes.dmoffice.upload.ShareUploadWorker.batchDir(ctx, shareBatch)
        shareFiles = dir.listFiles()?.filter { it.isFile }?.sortedBy { it.name } ?: emptyList()
        // Datum, Uhrzeit und Ort aus dem ersten Foto (dem frühesten) - vor dem Hochladen, damit gleich gefragt werden kann
        val first = withContext(Dispatchers.IO) { shareFiles.mapNotNull { f -> photoMeta(f)?.let { f to it } }.minByOrNull { it.second.first } }
        first?.second?.let { (taken, geo) ->
            date = taken; dateTouched = true
            if (geo != null && place.lat == null) { place = place.copy(lat = geo.first, lon = geo.second); lookup(place, true) }
        }
        val day = date.toLocalDate()
        val before = day.plusDays(1).atStartOfDay(ZoneId.systemDefault()).toInstant().toString()
        sameDay = try {
            app.journal.entries(journal = j, before = before, limit = 10)
                .firstOrNull { it.canWrite && Instant.parse(it.date).atZone(ZoneId.systemDefault()).toLocalDate() == day }
        } catch (_: Exception) { null }
        // Noch kein Eintrag an dem Tag: ohne Frage hochladen
        if (sameDay == null) uploadShared()
    }

    fun uploadUris(uris: List<Uri>) {
        if (uris.isEmpty()) return
        scope.launch {
            val files = withContext(Dispatchers.IO) {
                uris.mapNotNull { u ->
                    try {
                        var name = "Datei"
                        ctx.contentResolver.query(u, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c -> if (c.moveToFirst() && !c.isNull(0)) name = c.getString(0) }
                        val f = File.createTempFile("journal", ".tmp", File(ctx.cacheDir, "journal").apply { mkdirs() })
                        ctx.contentResolver.openInputStream(u)?.use { i -> f.outputStream().use { o -> i.copyTo(o) } }
                        Triple(f, name, ctx.contentResolver.getType(u))
                    } catch (e: Exception) { null }
                }
            }
            uploadFiles(files)
        }
    }
    val pickMedia = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia()) { uploadUris(it) }
    val pickFiles = rememberLauncherForActivityResult(ActivityResultContracts.GetMultipleContents()) { uploadUris(it) }
    // Standort der App: aus dem Knopf (immer übernehmen) oder automatisch für Kamera und Widget (nur, wenn noch kein Ort da ist; still)
    var hereQuiet by remember { mutableStateOf(false) }
    fun takeHere(quiet: Boolean) = scope.launch {
        if (quiet && place.lat != null) return@launch
        currentLocation(ctx)?.let { l -> if (!quiet || place.lat == null) { place = place.copy(lat = Math.round(l.latitude * 1e6) / 1e6, lon = Math.round(l.longitude * 1e6) / 1e6); lookup(place, true) } }
            ?: run { if (!quiet) toast(ctx.getString(R.string.journal_no_location)) }
    }
    val locationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { granted ->
        if (granted.values.any { it }) takeHere(hereQuiet)
        hereQuiet = false
    }
    fun hereIfNoPlace() {
        if (place.lat != null) return
        val ok = listOf(Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION)
            .any { ContextCompat.checkSelfPermission(ctx, it) == PackageManager.PERMISSION_GRANTED }
        if (ok) takeHere(true) else { hereQuiet = true; locationPermission.launch(arrayOf(Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION)) }
    }

    // Foto mit der Kamera: in den Zwischenspeicher (camera/), dann hochladen und anhängen. Datum und Ort aus dem Foto (adopt);
    // hat es keinen Ort, der Standort der App. Der Pfad überlebt, falls Android die App während der Aufnahme beendet.
    var captureFile by rememberSaveable { mutableStateOf<String?>(null) }
    val takePhoto = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { ok ->
        val f = captureFile?.let { File(it) }
        captureFile = null
        if (f == null) return@rememberLauncherForActivityResult
        if (!ok || f.length() == 0L) { f.delete(); return@rememberLauncherForActivityResult }
        scope.launch {
            // Aus dem Widget: das Tagebuch steht evtl. noch nicht fest - kurz warten
            var waited = 0
            while ((entry?.journal ?: target) == null && waited < 50) { kotlinx.coroutines.delay(200); waited++ }
            withContext(Dispatchers.IO) { photoMeta(f) }?.let { (taken, geo) ->
                if (!dateTouched) { date = taken; dateTouched = true }
                if (geo != null && place.lat == null) { place = place.copy(lat = geo.first, lon = geo.second); lookup(place, true) }
            }
            uploadFiles(listOf(Triple(f, f.name, "image/jpeg"))) { hereIfNoPlace() }
        }
    }
    fun capture() {
        val dir = File(ctx.cacheDir, "camera").apply { mkdirs() }
        val f = File(dir, "Foto " + DateTimeFormatter.ofPattern("yyyy-MM-dd HH.mm.ss").format(ZonedDateTime.now()) + ".jpg")
        val u = androidx.core.content.FileProvider.getUriForFile(ctx, ctx.packageName + ".files", f)
        captureFile = f.absolutePath
        try { takePhoto.launch(u) } catch (e: Exception) { captureFile = null; toast(e.message) }
    }
    val cameraPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok -> if (ok) capture() }
    fun startCamera() {
        if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) capture()
        else cameraPermission.launch(Manifest.permission.CAMERA)
    }
    // Aus dem Widget: „+ Foto-Eintrag“ öffnet gleich die Kamera, „+ Eintrag“ übernimmt gleich den Standort - je nur einmal
    var started by rememberSaveable { mutableStateOf(false) }
    val windowInfo = androidx.compose.ui.platform.LocalWindowInfo.current
    LaunchedEffect(start) {
        if (started || uri != null || start == null) return@LaunchedEffect
        // Erst, wenn kein anderer Dialog davor steht (etwa die Frage nach Benachrichtigungen beim ersten Start) - Android
        // verwirft sonst die zweite Erlaubnis-Anfrage stillschweigend, und die Kamera ginge nie auf
        androidx.compose.runtime.snapshotFlow { windowInfo.isWindowFocused }.first { it }
        kotlinx.coroutines.delay(400)
        androidx.compose.runtime.snapshotFlow { windowInfo.isWindowFocused }.first { it }
        started = true
        when (start) { "camera" -> startCamera(); "new" -> hereIfNoPlace() }
    }
    fun startRecording() {
        try {
            val f = File(File(ctx.cacheDir, "journal").apply { mkdirs() }, "Sprachaufnahme " + DateTimeFormatter.ofPattern("yyyy-MM-dd HH-mm-ss").format(ZonedDateTime.now()) + ".m4a")
            recorder = VoiceRecorder(ctx, f)
        } catch (e: Exception) { toast(ctx.getString(R.string.journal_mic_failed)) }
    }
    val micPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok -> if (ok) startRecording() }
    fun toggleRecording() {
        val r = recorder
        if (r != null) { recorder = null; r.stop()?.let { f -> uploadFiles(listOf(Triple(f, f.name, "audio/mp4"))) }; return }
        if (ContextCompat.checkSelfPermission(ctx, Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) startRecording()
        else micPermission.launch(Manifest.permission.RECORD_AUDIO)
    }

    /** Vom Server anhängen (Dateiablage, Galerie-Ordner): der Server kopiert - nichts geht über das Telefon. */
    fun attachFromServer(files: List<String>, external: List<String>) {
        val j = entry?.journal ?: target ?: return
        if (files.isEmpty() && external.isEmpty()) return
        uploading += files.size + external.size
        scope.launch {
            try { adopt(app.journal.attachFiles(j, files) + app.journal.attachExternal(j, external)) }
            catch (e: Exception) { toast(e.message) }
            finally { uploading -= files.size + external.size }
        }
    }

    // Vorschlag: zum Auswählen wie im Web - Termine, Erledigtes und die Fotos des Tages aus der Galerie
    fun suggest() { suggesting = true }

    fun save() {
        val w = web.value ?: return
        val j = entry?.journal ?: target ?: return
        busy = true
        w.evaluateJavascript("getHtml()") { raw ->
            val text = try { Json.decodeFromString<String>(raw) } catch (_: Exception) { "" }
            scope.launch {
                try {
                    app.journal.save(EntryInput(
                        journal = j, uri = entry?.uri, etag = entry?.etag, date = date.toInstant().toString(), title = title.trim().ifEmpty { null }, html = text,
                        mood = mood, tags = tags.split(',', ';').map { it.trim() }.filter { it.isNotEmpty() },
                        place = place.takeIf { !it.name.isNullOrBlank() || it.lat != null }, private = private && own, showEvents = showEvents,
                        media = media, refs = refs, weather = weather.trim().ifEmpty { null }))
                    onClose()
                } catch (e: Exception) { busy = false; toast(e.message) }
            }
        }
    }

    Scaffold(topBar = {
        TopAppBar(title = { Text(if (readOnly) (entry?.title ?: stringResource(R.string.journal)) else stringResource(if (uri == null) R.string.journal_new else R.string.journal_edit)) },
            navigationIcon = { IconButton(onClick = { recorder?.cancel(); onClose() }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } },
            actions = {
                // Vorschlag (Termine, Erledigtes, Fotos des Tages) - sichtbar wie im Web, nicht versteckt in der Format-Leiste
                // Aus dem Tresor geht nichts an eine KI (Tagebücher im Tresor: Kennung ab 2^40, wie auf dem Server)
                if (!readOnly && ai?.allowed == true && (target ?: 0L) < (1L shl 40)) IconButton(onClick = {
                    web.value?.evaluateJavascript("getText()") { raw -> aiNotes = try { Json.decodeFromString<String>(raw) } catch (_: Exception) { "" } }
                }) { Icon(Icons.Default.AutoAwesome, contentDescription = stringResource(R.string.journal_ai)) }
                if (!readOnly) IconButton(onClick = { suggest() }) { Icon(Icons.Default.Lightbulb, contentDescription = stringResource(R.string.journal_suggest)) }
                if (entry != null && !readOnly) IconButton(onClick = { confirmDelete = true }) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.delete)) }
                if (!readOnly) IconButton(onClick = { save() }, enabled = loaded && !busy && uploading == 0 && recorder == null && (entry != null || target != null)) {
                    if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Icon(Icons.Default.Check, contentDescription = stringResource(R.string.save))
                }
            })
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            entry?.owner?.let { Text(stringResource(R.string.journal_from, it), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
            // Datum und Uhrzeit
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(enabled = !readOnly, onClick = {
                    DatePickerDialog(ctx, { _, y, m, d -> date = date.withYear(y).withMonth(m + 1).withDayOfMonth(d); dateTouched = true }, date.year, date.monthValue - 1, date.dayOfMonth).show()
                }) { Text(date.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.FULL))) }
                OutlinedButton(enabled = !readOnly, onClick = {
                    TimePickerDialog(ctx, { _, h, min -> date = date.withHour(h).withMinute(min); dateTouched = true }, date.hour, date.minute, android.text.format.DateFormat.is24HourFormat(ctx)).show()
                }) { Text(date.format(DateTimeFormatter.ofPattern("HH:mm"))) }
            }
            // Stimmung
            Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(stringResource(R.string.journal_mood), style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(end = 8.dp))
                Moods.forEach { (value, emoji) ->
                    TextButton(onClick = { if (!readOnly) mood = if (mood == value) null else value }, modifier = Modifier.size(44.dp),
                        contentPadding = androidx.compose.foundation.layout.PaddingValues(0.dp)) {
                        Text(emoji, fontSize = 24.sp, color = MaterialTheme.colorScheme.onSurface.copy(alpha = if (mood == value || mood == null) 1f else 0.35f))
                    }
                }
            }
            if (!readOnly || title.isNotBlank()) OutlinedTextField(title, { title = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.journal_title)) }, singleLine = true, readOnly = readOnly)

            // Text mit Formatierung (WebView, dasselbe HTML wie im Web)
            Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(8.dp)).border(1.dp, MaterialTheme.colorScheme.outline, RoundedCornerShape(8.dp))) {
                if (!readOnly) Row(Modifier.horizontalScroll(rememberScrollState()), verticalAlignment = Alignment.CenterVertically) {
                    @Composable fun tool(icon: androidx.compose.ui.graphics.vector.ImageVector, label: Int, js: String) =
                        IconButton(onClick = { web.value?.evaluateJavascript(js, null) }) { Icon(icon, contentDescription = stringResource(label)) }
                    tool(Icons.Default.FormatBold, R.string.journal_bold, "exec('bold')")
                    tool(Icons.Default.FormatItalic, R.string.journal_italic, "exec('italic')")
                    tool(Icons.Default.FormatUnderlined, R.string.journal_underline, "exec('underline')")
                    tool(Icons.Default.FormatStrikethrough, R.string.journal_strike, "exec('strikeThrough')")
                    tool(Icons.Default.Title, R.string.journal_heading, "exec('formatBlock','h2')")
                    tool(Icons.AutoMirrored.Filled.FormatListBulleted, R.string.journal_bullets, "exec('insertUnorderedList')")
                    tool(Icons.Default.FormatListNumbered, R.string.journal_numbers, "exec('insertOrderedList')")
                    tool(Icons.Default.Checklist, R.string.journal_checklist, "checklist()")
                }
                AndroidView(factory = { c ->
                    WebView(c).apply {
                        settings.javaScriptEnabled = true
                        setBackgroundColor(0)
                        webViewClient = object : WebViewClient() { override fun onPageFinished(view: WebView, url: String?) { applyHtml() } }
                        loadUrl("file:///android_asset/journal-editor.html")
                        web.value = this
                    }
                }, modifier = Modifier.fillMaxWidth().height(280.dp))
            }
            if (events.isNotEmpty()) Surface(shape = RoundedCornerShape(8.dp), color = MaterialTheme.colorScheme.surfaceVariant, modifier = Modifier.fillMaxWidth()) {
                Column(Modifier.padding(10.dp)) {
                    Text(stringResource(R.string.journal_events_title), style = MaterialTheme.typography.labelLarge)
                    events.forEach { e -> Text((if (e.allDay) stringResource(R.string.journal_all_day) else e.time ?: "") + " · " + e.title, style = MaterialTheme.typography.bodySmall) }
                }
            }

            // Fotos, Videos, Sprache, Dateien
            Text(stringResource(R.string.journal_media_title), style = MaterialTheme.typography.labelLarge)
            val j = entry?.journal ?: target ?: 0L
            Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                media.forEach { m ->
                    Box(Modifier.size(88.dp).clip(RoundedCornerShape(8.dp))) {
                        if (m.isImage || m.isVideo) Thumb(j, m, Modifier.fillMaxSize())
                        else Box(Modifier.fillMaxSize().padding(6.dp), contentAlignment = Alignment.Center) {
                            Text((if (m.isAudio) "🎙 " else "") + m.name, style = MaterialTheme.typography.labelSmall, maxLines = 3)
                        }
                        if (!readOnly) SmallFloatingActionButton(onClick = { media = media.filter { it.path != m.path } }, modifier = Modifier.align(Alignment.TopEnd).padding(2.dp).size(26.dp)) {
                            Icon(Icons.Default.Close, contentDescription = stringResource(R.string.remove), Modifier.size(14.dp))
                        }
                    }
                }
                if (uploading > 0) Box(Modifier.size(88.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            }
            // Gleich große Knöpfe in zwei Reihen: Symbol über der Beschriftung
            if (!readOnly) {
                @Composable fun RowScope.Way(icon: androidx.compose.ui.graphics.vector.ImageVector, label: Int, onClick: () -> Unit) =
                    OutlinedButton(onClick = onClick, modifier = Modifier.weight(1f).height(68.dp), shape = RoundedCornerShape(14.dp),
                        contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 2.dp, vertical = 4.dp)) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Icon(icon, contentDescription = null)
                            Text(stringResource(label), style = MaterialTheme.typography.labelSmall, maxLines = 2, textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                        }
                    }
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    Way(Icons.Default.PhotoCamera, R.string.journal_camera) { startCamera() }
                    Way(Icons.Default.PhotoLibrary, R.string.journal_from_gallery) { pickGallery = true }
                    Way(Icons.Default.Smartphone, R.string.journal_add_photos) { pickMedia.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) }
                    Way(Icons.Default.AttachFile, R.string.journal_add_files) { pickFiles.launch("*/*") }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    Way(if (recorder != null) Icons.Default.Stop else Icons.Default.Mic, if (recorder != null) R.string.journal_record_stop else R.string.journal_record) { toggleRecording() }
                    Way(Icons.Default.CloudDownload, R.string.journal_add_server) { pickServer = true }
                    if (own) Way(Icons.AutoMirrored.Filled.StickyNote2, R.string.journal_link_note) { pickNote = true } else Spacer(Modifier.weight(1f))
                    Spacer(Modifier.weight(1f))
                }
            }
            if (refs.isNotEmpty()) FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                refs.forEach { r ->
                    AssistChip(onClick = { if (!readOnly) refs = refs - r }, label = { Text(r.name) },
                        leadingIcon = { Icon(Icons.AutoMirrored.Filled.StickyNote2, contentDescription = null, Modifier.size(16.dp)) },
                        trailingIcon = if (readOnly) null else { { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.remove), Modifier.size(14.dp)) } })
                }
            }

            // Ort
            Row(verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(place.name ?: "", { place = place.copy(name = it) }, Modifier.weight(1f), label = { Text(stringResource(R.string.journal_place)) }, singleLine = true, readOnly = readOnly)
                if (!readOnly) IconButton(onClick = { locationPermission.launch(arrayOf(Manifest.permission.ACCESS_FINE_LOCATION, Manifest.permission.ACCESS_COARSE_LOCATION)) }) {
                    Icon(Icons.Default.MyLocation, contentDescription = stringResource(R.string.journal_here))
                }
            }
            if (place.lat != null && place.lon != null) Row(verticalAlignment = Alignment.CenterVertically) {
                Text("%.4f, %.4f".format(place.lat, place.lon), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (!readOnly) TextButton(onClick = { place = place.copy(lat = null, lon = null) }) { Text(stringResource(R.string.journal_remove_coords)) }
            }
            Row(verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(weather, { weather = it }, Modifier.weight(1f), label = { Text(stringResource(R.string.journal_weather)) }, singleLine = true, readOnly = readOnly)
                if (!readOnly && lookupOn && place.lat != null) IconButton(onClick = { weather = ""; lookup(place, false) }, enabled = !looking) {
                    if (looking) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Icon(Icons.Default.WbSunny, contentDescription = stringResource(R.string.journal_lookup))
                }
            }
            OutlinedTextField(tags, { tags = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.journal_tags)) }, singleLine = true, readOnly = readOnly,
                supportingText = if (readOnly) null else { { Text(stringResource(R.string.journal_tags_hint)) } })
            if (!readOnly) {
                if (own) Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.journal_private), Modifier.weight(1f))
                    Switch(private, { private = it })
                }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(stringResource(R.string.journal_show_events), Modifier.weight(1f))
                    Switch(showEvents, { showEvents = it })
                }
            }
            Box(Modifier.height(48.dp))
        }
    }

    sameDay?.let { p ->
        val label = p.title?.takeIf { it.isNotBlank() } ?: p.snippet.take(60).ifBlank { stringResource(R.string.journal_share_untitled) }
        AlertDialog(onDismissRequest = { sameDay = null; uploadShared() },
            title = { Text(stringResource(R.string.journal_share_existing_title)) },
            text = { Text(stringResource(R.string.journal_share_existing_text, date.format(java.time.format.DateTimeFormatter.ofLocalizedDate(java.time.format.FormatStyle.LONG)), label)) },
            confirmButton = { TextButton(onClick = { sameDay = null; attachTo(p) }) { Text(stringResource(R.string.journal_share_attach), fontWeight = FontWeight.SemiBold) } },
            dismissButton = { TextButton(onClick = { sameDay = null; uploadShared() }) { Text(stringResource(R.string.journal_share_new)) } })
    }

    if (confirmDelete) AlertDialog(onDismissRequest = { confirmDelete = false },
        title = { Text(stringResource(R.string.journal_delete_title)) }, text = { Text(stringResource(R.string.journal_delete_text)) },
        confirmButton = {
            TextButton(onClick = {
                confirmDelete = false
                val e = entry ?: return@TextButton
                scope.launch { try { app.journal.remove(e.journal, e.uri); onClose() } catch (ex: Exception) { toast(ex.message) } }
            }) { Text(stringResource(R.string.delete), fontWeight = FontWeight.SemiBold) }
        },
        dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text(stringResource(R.string.cancel)) } })

    if (pickNote) NotePickDialog(onDismiss = { pickNote = false }) { n ->
        pickNote = false
        if (refs.none { it.type == "note" && it.id == n.id.toString() }) refs = refs + Ref("note", n.id.toString(), n.title)
    }

    // Schon Angehängtes ausgegraut: gleicher Pfad oder - die Kopie im Tagebuch ist kleingerechnet/umbenannt - gleicher Grundname
    if (pickServer) StoredFilePicker(onDismiss = { pickServer = false }, attached = { f -> media.any { it.path == f.path || baseName(it.path) == baseName(f.name) } }) { picked ->
        pickServer = false
        attachFromServer(picked.map { it.path }, emptyList())
    }

    aiNotes?.let { notes ->
        val state = ai ?: return@let
        AiWriteDialog(state, target, day, mood, title, notes, place.name?.trim()?.ifEmpty { null }, weather.trim().ifEmpty { null }) { html ->
            aiNotes = null
            // Die Wahl ist jetzt die Vorgabe
            scope.launch { ai = try { app.journal.ai() } catch (_: Exception) { ai } }
            if (html != null) web.value?.evaluateJavascript("aiInsert(${JsonPrimitive(html)})", null)
        }
    }

    if (suggesting) SuggestDialog(day, media, onDismiss = { suggesting = false }) { html, files, external ->
        suggesting = false
        if (html.isNotEmpty()) web.value?.evaluateJavascript("exec('insertHTML', ${JsonPrimitive(html)})", null)
        attachFromServer(files, external)
    }
    // Aus Galerie: alle Fotos der dmOffice-Galerie (die des Tages bietet „Vorschlag“); Galerie-Ordner gehen als „external“ ohne „ext:“
    if (pickGallery) GalleryPhotoPicker(onDismiss = { pickGallery = false }) { picked ->
        pickGallery = false
        val (ext, own) = picked.partition { it.path.startsWith("ext:") }
        attachFromServer(own.map { it.path }, ext.map { it.path.removePrefix("ext:") })
    }
}

/** Grundname ohne Endung und ohne „ (2)“ - angehängte Bilder werden kleingerechnet (.jpg) und bei Gleichnamigen nummeriert. */
private fun baseName(p: String) = p.substringAfterLast('/').substringBeforeLast('.').replace(Regex(" \\(\\d+\\)$"), "").lowercase()

/** Schon am Eintrag? Gleicher Pfad, gleiche Aufnahmezeit (die Kopie behält sie) oder - ohne Aufnahmezeit - gleicher Name. Wie im Web. */
private fun attached(p: at.dimes.dmoffice.journal.DayPhoto, media: List<Media>) = media.any { m ->
    m.path == p.path || run {
        val mt = m.taken?.let { t -> try { Instant.parse(t).toEpochMilli() } catch (_: Exception) { null } }
        if (mt != null && p.taken != null) kotlin.math.abs(mt - p.taken) < 1000 else baseName(m.path) == baseName(p.name)
    }
}

/**
 * Vorschlag für einen Tag zum Auswählen (wie im Web): Termine und erledigte Aufgaben als Text (vorab angehakt), dazu die Fotos
 * und Videos des Tages aus der Galerie - ohne die, die schon am Eintrag hängen. Übernommen wird, was angehakt ist.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun SuggestDialog(day: String, media: List<Media>, photosOnly: Boolean = false, onDismiss: () -> Unit, onTake: (html: String, files: List<String>, external: List<String>) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    var data by remember { mutableStateOf<at.dimes.dmoffice.journal.DaySuggestion?>(null) }
    var events by remember { mutableStateOf(setOf<Int>()) }
    var tasks by remember { mutableStateOf(setOf<Int>()) }
    var photos by remember { mutableStateOf(setOf<String>()) }
    LaunchedEffect(day) {
        try {
            val r = app.journal.suggestDay(day)
            data = if (photosOnly) r.copy(events = emptyList(), tasks = emptyList()) else r
            if (!photosOnly) { events = r.events.indices.toSet(); tasks = r.tasks.indices.toSet() }
        } catch (e: Exception) { Toast.makeText(ctx, e.message ?: "?", Toast.LENGTH_LONG).show(); onDismiss() }
    }
    val shown = remember(data, media) { data?.photos?.filter { !attached(it, media) } ?: emptyList() }
    val labelEvents = stringResource(R.string.journal_html_events)
    val labelDone = stringResource(R.string.journal_html_done)
    val labelMeals = stringResource(R.string.journal_html_meals)
    fun take() {
        val d = data ?: return
        fun enc(x: String) = x.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;").replace("\"", "&quot;")
        val ev = d.events.filterIndexed { i, _ -> i in events }
        val done = d.tasks.filterIndexed { i, _ -> i in tasks }
        // Essensplan-Einträge als Mahlzeiten („Abendessen: Kichererbsen-Salat“), die übrigen als Termine
        val appts = ev.filter { it.meal == null }
        val meals = ev.filter { it.meal != null }
        val html = (if (appts.isNotEmpty()) "<p><b>${enc(labelEvents)}</b> " + appts.joinToString(", ") { enc((if (it.allDay || it.time == null) "" else it.time + " ") + it.title) } + "</p>" else "") +
            (if (meals.isNotEmpty()) "<p><b>${enc(labelMeals)}</b> " + meals.joinToString(", ") { enc(it.meal + ": " + it.title) } + "</p>" else "") +
            (if (done.isNotEmpty()) "<p><b>${enc(labelDone)}</b> " + done.joinToString(", ") { enc(it) } + "</p>" else "")
        val picked = shown.filter { it.path in photos }
        onTake(html, picked.filter { it.kind == "file" }.map { it.path }, picked.filter { it.kind == "ext" }.map { it.path.removePrefix(at.dimes.dmoffice.files.FilesRepo.EXT) })
    }
    // Ganzer Bildschirm, Ränder selbst (Statusleiste oben, Navigationsleiste unten) - sonst lag die unterste Reihe unter der Navigationsleiste
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss,
        properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        Scaffold(Modifier.fillMaxSize(), contentWindowInsets = androidx.compose.foundation.layout.WindowInsets.systemBars, topBar = {
            TopAppBar(
                title = { Text(if (photosOnly) stringResource(R.string.journal_from_gallery) else stringResource(R.string.journal_suggest_title, java.time.LocalDate.parse(day).format(DateTimeFormatter.ofLocalizedDate(java.time.format.FormatStyle.MEDIUM))), maxLines = 1) },
                navigationIcon = { IconButton(onClick = onDismiss) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.close)) } },
                actions = {
                    TextButton(enabled = data != null && (events.isNotEmpty() || tasks.isNotEmpty() || photos.isNotEmpty()), onClick = { take() }) {
                        Text(stringResource(R.string.journal_suggest_take), fontWeight = FontWeight.SemiBold)
                    }
                })
        }) { pad ->
            val d = data
            if (d == null) Box(Modifier.padding(pad).fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            else LazyVerticalGrid(GridCells.Adaptive(104.dp), Modifier.padding(pad).fillMaxSize(), contentPadding = androidx.compose.foundation.layout.PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                val full: androidx.compose.foundation.lazy.grid.LazyGridItemSpanScope.() -> androidx.compose.foundation.lazy.grid.GridItemSpan = { androidx.compose.foundation.lazy.grid.GridItemSpan(maxLineSpan) }
                if (d.events.isEmpty() && d.tasks.isEmpty() && shown.isEmpty()) item(span = full) {
                    Text(stringResource(R.string.journal_suggest_nothing), color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                for ((meal, title) in listOf(false to R.string.journal_suggest_events, true to R.string.journal_suggest_meals)) if (d.events.any { (it.meal != null) == meal }) {
                    item(span = full) { Text(stringResource(title), fontWeight = FontWeight.SemiBold) }
                    d.events.forEachIndexed { i, e ->
                        if ((e.meal != null) == meal) item(span = full) {
                            Row(Modifier.fillMaxWidth().clickable { events = if (i in events) events - i else events + i }, verticalAlignment = Alignment.CenterVertically) {
                                Checkbox(i in events, { events = if (it) events + i else events - i })
                                Text(if (e.meal != null) e.meal + ": " + e.title else (if (e.allDay || e.time == null) "" else e.time + " ") + e.title + (e.location?.let { " · $it" } ?: ""))
                            }
                        }
                    }
                }
                if (d.tasks.isNotEmpty()) {
                    item(span = full) { Text(stringResource(R.string.journal_suggest_tasks), fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 8.dp)) }
                    d.tasks.forEachIndexed { i, x ->
                        item(span = full) {
                            Row(Modifier.fillMaxWidth().clickable { tasks = if (i in tasks) tasks - i else tasks + i }, verticalAlignment = Alignment.CenterVertically) {
                                Checkbox(i in tasks, { tasks = if (it) tasks + i else tasks - i })
                                Text(x)
                            }
                        }
                    }
                }
                if (shown.isNotEmpty()) {
                    item(span = full) {
                        Row(Modifier.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                            Text(stringResource(R.string.journal_suggest_photos, shown.size), fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f))
                            TextButton(onClick = { photos = if (photos.size == shown.size) emptySet() else shown.map { it.path }.toSet() }) {
                                Text(stringResource(if (photos.size == shown.size) R.string.journal_suggest_none_photos else R.string.journal_suggest_all_photos))
                            }
                        }
                    }
                    gridItems(shown, key = { it.path }) { p ->
                        val on = p.path in photos
                        val item = at.dimes.dmoffice.files.FileItem(path = p.path, name = p.name, isDir = false, mime = p.mime, etag = p.etag)
                        Box(Modifier.aspectRatio(1f).clip(RoundedCornerShape(8.dp))
                            .then(if (on) Modifier.border(3.dp, MaterialTheme.colorScheme.primary, RoundedCornerShape(8.dp)) else Modifier)
                            .clickable { photos = if (on) photos - p.path else photos + p.path }) {
                            ThumbOrIcon(item, Modifier.fillMaxSize(), 32.dp)
                            if (on) Icon(Icons.Default.CheckCircle, contentDescription = null, tint = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(22.dp))
                        }
                    }
                    val already = d.photos.size - shown.size
                    if (already > 0) item(span = full) {
                        Text(stringResource(R.string.journal_suggest_attached, already), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
        }
    }
}

/** Notiz zum Verknüpfen suchen (eigene Notizen). */
@Composable
private fun NotePickDialog(onDismiss: () -> Unit, onPick: (NoteItem) -> Unit) {
    val app = App.get(LocalContext.current)
    var query by remember { mutableStateOf("") }
    var list by remember { mutableStateOf<List<NoteItem>?>(null) }
    LaunchedEffect(query) {
        if (query.isNotBlank()) kotlinx.coroutines.delay(250)
        list = try { if (query.isBlank()) app.notes.view("all") else app.notes.searchServer(query) } catch (_: Exception) { emptyList() }
    }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.journal_link_note)) },
        text = {
            Column {
                OutlinedTextField(query, { query = it }, Modifier.fillMaxWidth(), singleLine = true, placeholder = { Text(stringResource(R.string.search)) })
                val l = list
                if (l == null) CircularProgressIndicator(Modifier.padding(16.dp))
                else LazyColumn(Modifier.heightIn(max = 360.dp)) {
                    items(l.filter { it.id > 0 }, key = { it.id }) { n ->
                        ListItem(headlineContent = { Text(n.title.ifBlank { "–" }, maxLines = 1) }, supportingContent = { Text(n.snippet, maxLines = 1) },
                            modifier = Modifier.clickable { onPick(n) })
                    }
                }
            }
        },
        confirmButton = {}, dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Aufnahmezeit (mit Zeitzone aus dem Bild, sonst die eigene) und GPS aus dem EXIF eines Fotos; null bei Videos und Bildern ohne Datum. */
private fun photoMeta(file: File): Pair<ZonedDateTime, Pair<Double, Double>?>? = try {
    val exif = android.media.ExifInterface(file.absolutePath)
    val raw = exif.getAttribute(android.media.ExifInterface.TAG_DATETIME_ORIGINAL) ?: exif.getAttribute(android.media.ExifInterface.TAG_DATETIME)
    raw?.let {
        val local = java.time.LocalDateTime.parse(it.trim(), java.time.format.DateTimeFormatter.ofPattern("yyyy:MM:dd HH:mm:ss"))
        val offset = exif.getAttribute(android.media.ExifInterface.TAG_OFFSET_TIME_ORIGINAL)?.let { o -> try { java.time.ZoneOffset.of(o.trim()) } catch (_: Exception) { null } }
        val taken = if (offset != null) local.atOffset(offset).atZoneSameInstant(ZoneId.systemDefault()) else local.atZone(ZoneId.systemDefault())
        val ll = FloatArray(2)
        @Suppress("DEPRECATION")
        val geo = if (exif.getLatLong(ll) && (ll[0] != 0f || ll[1] != 0f)) ll[0].toDouble() to ll[1].toDouble() else null
        taken to geo
    }
} catch (_: Exception) { null }

/**
 * Mit KI schreiben (wie im Web): Länge und was mitgeht wählen (beim nächsten Mal vorgegeben), Ergebnis ansehen,
 * dann übernehmen bzw. anhängen oder neu schreiben lassen.
 */
@Composable
private fun AiWriteDialog(state: at.dimes.dmoffice.journal.AiState, journal: Long?, day: String, mood: Int?, title: String, notes: String, place: String?, weather: String?,
                          onClose: (html: String?) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var o by remember { mutableStateOf(state.settings ?: at.dimes.dmoffice.journal.AiSettings()) }
    var busy by remember { mutableStateOf(false) }
    var html by remember { mutableStateOf<String?>(null) }
    fun write() {
        busy = true
        scope.launch {
            try {
                val lang = if (ctx.resources.configuration.locales[0].language == "en") "en" else "de"
                html = app.journal.aiWrite(at.dimes.dmoffice.journal.AiWrite(day, o.length, o.fitness, o.events, o.place, o.weather, o.news,
                    place, weather, mood, title.trim().ifEmpty { null }, notes.trim().ifEmpty { null }, lang, journal)).html
            } catch (e: Exception) { Toast.makeText(ctx, e.message ?: "?", Toast.LENGTH_LONG).show() }
            busy = false
        }
    }
    @Composable fun check(checked: Boolean, enabled: Boolean, label: String, onChange: (Boolean) -> Unit) =
        Row(verticalAlignment = Alignment.CenterVertically) {
            androidx.compose.material3.Checkbox(checked && enabled, onChange, enabled = enabled)
            Text(label, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurface.copy(alpha = if (enabled) 1f else 0.4f))
        }
    AlertDialog(onDismissRequest = { if (!busy) onClose(null) },
        title = { Text(stringResource(R.string.journal_ai)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for ((v, l) in listOf("short" to R.string.journal_ai_short, "medium" to R.string.journal_ai_medium, "long" to R.string.journal_ai_long))
                        androidx.compose.material3.FilterChip(o.length == v, { o = o.copy(length = v) }, label = { Text(stringResource(l)) })
                }
                Text(stringResource(R.string.journal_ai_send), style = MaterialTheme.typography.labelLarge, modifier = Modifier.padding(top = 6.dp))
                check(o.fitness, true, stringResource(R.string.journal_ai_fitness)) { o = o.copy(fitness = it) }
                check(o.events, true, stringResource(R.string.journal_ai_events)) { o = o.copy(events = it) }
                check(o.place, place != null, if (place != null) stringResource(R.string.journal_ai_place, place) else stringResource(R.string.journal_ai_no_place)) { o = o.copy(place = it) }
                check(o.weather, weather != null, if (weather != null) stringResource(R.string.journal_ai_weather, weather) else stringResource(R.string.journal_ai_no_weather)) { o = o.copy(weather = it) }
                check(o.news, state.feeds || state.search, stringResource(if (state.search && place != null) R.string.journal_ai_news_near else R.string.journal_ai_news)) { o = o.copy(news = it) }
                Text(stringResource(R.string.journal_ai_hint) + if (o.news && !state.feeds) " " + stringResource(R.string.journal_ai_news_search) else "",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (busy) Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(top = 8.dp)) {
                    CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp)
                    Text(stringResource(R.string.journal_ai_busy))
                }
                html?.takeIf { !busy }?.let { h ->
                    Surface(shape = RoundedCornerShape(8.dp), color = MaterialTheme.colorScheme.surfaceVariant, modifier = Modifier.fillMaxWidth().padding(top = 8.dp)) {
                        Text(androidx.core.text.HtmlCompat.fromHtml(h, androidx.core.text.HtmlCompat.FROM_HTML_MODE_COMPACT).toString().trim(), Modifier.padding(10.dp),
                            style = MaterialTheme.typography.bodyMedium)
                    }
                }
            }
        },
        confirmButton = {
            val h = html
            if (h == null) TextButton(onClick = { write() }, enabled = !busy) { Text(stringResource(R.string.journal_ai_write), fontWeight = FontWeight.SemiBold) }
            else Row {
                TextButton(onClick = { write() }, enabled = !busy) { Text(stringResource(R.string.journal_ai_again)) }
                TextButton(onClick = { onClose(h) }, enabled = !busy) {
                    Text(stringResource(if (notes.isNotBlank()) R.string.journal_ai_append else R.string.journal_ai_take), fontWeight = FontWeight.SemiBold)
                }
            }
        },
        dismissButton = { TextButton(onClick = { onClose(null) }, enabled = !busy) { Text(stringResource(R.string.cancel)) } })
}
