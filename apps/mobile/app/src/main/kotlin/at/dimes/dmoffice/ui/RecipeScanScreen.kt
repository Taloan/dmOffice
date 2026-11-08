package at.dimes.dmoffice.ui

import android.graphics.Bitmap
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.ScaffoldDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
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
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.ui.dms.CornerEditor
import at.dimes.dmoffice.ui.dms.loadUpright
import at.dimes.dmoffice.ui.dms.rememberPageScanner
import at.dimes.dmoffice.ui.dms.Screen
import at.dimes.dmoffice.ui.dms.toJpeg
import at.dimes.dmoffice.ui.dms.warp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.RequestBody.Companion.asRequestBody
import java.io.File

/*
 * Rezept-Scan (docs/rezept-scan-konzept.md): Kochbuchseiten mit dem Scanner aus dem DMS abfotografieren, der Server erkennt
 * Titel, Portionen, Zutaten und Schritte (mehrere Rezepte auf einer Seite getrennt), hier prüfen, Titelbild wählen und je
 * Rezept eine Rezept-Notiz anlegen - in derselben Gestalt wie ein Rezept per Link.
 */

@Serializable data class ScanDraft(
    val title: String = "", val yield: String = "", val prepTime: String = "", val cookTime: String = "", val totalTime: String = "",
    val description: String = "", val ingredients: String = "", val steps: String = "", val tip: String = "", val source: String = "",
    /** Kalorien je Portion (Zahl); ohne Angabe schlägt der Essensplan das Rezept nicht vor */
    val calories: String = "",
)
@Serializable private data class ScanState(val ocr: Boolean = false, val reason: String? = null, val aiAllowed: Boolean = false, val ai: Boolean = false)
@Serializable private data class ScanResult(val drafts: List<ScanDraft> = emptyList(), val text: String = "", val hints: List<String> = emptyList(), val ai: Boolean = false)
@Serializable private data class AiResult(val drafts: List<ScanDraft> = emptyList())

private class RecipeScanApi(private val ctx: android.content.Context) {
    private val jpeg = "image/jpeg".toMediaType()
    private fun api() = App.get(ctx).api ?: throw IllegalStateException("not signed in")

    suspend fun state(): ScanState = withContext(Dispatchers.IO) { api().let { it.get<ScanState>(it.url("/api/notes/recipes/scan/state")) } }

    suspend fun scan(pages: List<File>): ScanResult = withContext(Dispatchers.IO) {
        val a = api()
        val b = MultipartBody.Builder().setType(MultipartBody.FORM)
        pages.forEach { b.addFormDataPart("page", it.name, it.asRequestBody(jpeg)) }
        a.json.decodeFromString(a.postForm(a.url("/api/notes/recipes/scan"), b.build()))
    }

    suspend fun ai(text: String, pages: List<File>): List<ScanDraft> = withContext(Dispatchers.IO) {
        val a = api()
        val b = MultipartBody.Builder().setType(MultipartBody.FORM).addFormDataPart("text", text)
        pages.forEach { b.addFormDataPart("page", it.name, it.asRequestBody(jpeg)) }
        a.json.decodeFromString<AiResult>(a.postForm(a.url("/api/notes/recipes/scan/ai"), b.build())).drafts
    }

    suspend fun save(d: ScanDraft, folder: String?, image: File?): Long = withContext(Dispatchers.IO) {
        val a = api()
        val b = MultipartBody.Builder().setType(MultipartBody.FORM).addFormDataPart("draft", a.json.encodeToString(d))
        if (folder != null) b.addFormDataPart("folder", folder)
        if (image != null) b.addFormDataPart("image", "titelbild.jpg", image.asRequestBody(jpeg))
        a.json.parseToJsonElement(a.postForm(a.url("/api/notes/recipes/scan/save"), b.build())).jsonObject["id"]!!.jsonPrimitive.content.toLong()
    }
}

/**
 * Ganzer Bildschirm an Stelle der Notizliste; [onDone] mit der Nummer der (letzten) neuen Notiz, null bei Abbruch.
 * [inTab]: als Reiter im Hauptbildschirm, der die Systemleisten schon berücksichtigt.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RecipeScanScreen(folder: String?, inTab: Boolean, onDone: (Long?) -> Unit) {
    val insets = if (inTab) WindowInsets(0) else null
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    val api = remember { RecipeScanApi(ctx.applicationContext) }
    val pages = remember { mutableStateListOf<File>() }
    var state by remember { mutableStateOf<ScanState?>(null) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    val drafts = remember { mutableStateListOf<ScanDraft>() }
    val images = remember { mutableStateListOf<File?>() }
    var reviewing by remember { mutableStateOf(false) }
    var cur by remember { mutableStateOf(0) }
    var raw by remember { mutableStateOf("") }
    var hints by remember { mutableStateOf<List<String>>(emptyList()) }
    var usedAi by remember { mutableStateOf(false) }
    var cropping by remember { mutableStateOf(false) }

    val scanner = rememberPageScanner(limit = 10) { files -> pages.addAll(files.take(10 - pages.size)) }
    // Schon fotografierte Seiten aus der Galerie (aufrecht und verkleinert übernommen)
    val pickPages = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(10)) { uris ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        busy = true
        scope.launch {
            val files = withContext(Dispatchers.IO) {
                uris.take(10 - pages.size).mapIndexedNotNull { i, u ->
                    val tmp = File(File(ctx.cacheDir, "camera").apply { mkdirs() }, "recipe-page.jpg")
                    try {
                        ctx.contentResolver.openInputStream(u)?.use { s -> tmp.outputStream().use { o -> s.copyTo(o) } }
                        loadUpright(tmp, 2400)?.let { toJpeg(it, File(File(ctx.cacheDir, "files/recipe-scan").apply { mkdirs() }, "seite-${System.currentTimeMillis()}-$i.jpg")) }
                    } catch (_: Exception) { null } finally { tmp.delete() }
                }
            }
            pages.addAll(files); busy = false
        }
    }
    LaunchedEffect(Unit) {
        state = try { api.state() } catch (e: Exception) { error = e.message; null }
        if (state?.ocr == true) scanner.start()
    }
    fun close(id: Long?) {
        pages.forEach { it.delete() }; images.forEach { it?.delete() }
        onDone(id)
    }
    fun found(list: List<ScanDraft>) {
        drafts.clear(); drafts.addAll(list.ifEmpty { listOf(ScanDraft()) })
        images.clear(); repeat(drafts.size) { images.add(null) }
        cur = 0; reviewing = true
    }
    fun recognize() {
        busy = true; error = null
        scope.launch {
            try { val r = api.scan(pages); raw = r.text; hints = r.hints; usedAi = r.ai; found(r.drafts) }
            catch (e: Exception) { error = e.message ?: e.toString() }
            finally { busy = false }
        }
    }
    fun withAi() {
        busy = true; error = null
        scope.launch {
            try {
                val source = drafts.getOrNull(cur)?.source ?: ""
                found(api.ai(raw, pages).map { it.copy(source = source) })
                usedAi = true; hints = hints.filter { it.startsWith("Seite") }
            } catch (e: Exception) { error = e.message ?: e.toString() } finally { busy = false }
        }
    }
    // Je Rezept eine Notiz; schlägt eins fehl, bleiben die übrigen zum erneuten Versuch
    fun save() {
        busy = true; error = null
        scope.launch {
            var last: Long? = null
            try {
                while (drafts.isNotEmpty()) {
                    last = api.save(drafts[0], folder, images[0])
                    drafts.removeAt(0); images.removeAt(0)?.delete()
                }
                close(last)
            } catch (e: Exception) { error = e.message ?: e.toString(); cur = 0 } finally { busy = false }
        }
    }

    if (scanner.Screen()) return
    if (cropping && pages.isNotEmpty()) {
        CropPage(pages, insets, onDone = { f -> cropping = false; if (f != null) { images[cur]?.delete(); images[cur] = f } })
        return
    }
    BackHandler { if (reviewing) reviewing = false else close(null) }

    Scaffold(contentWindowInsets = insets ?: ScaffoldDefaults.contentWindowInsets, topBar = {
        TopAppBar(windowInsets = insets ?: TopAppBarDefaults.windowInsets, title = { Text(stringResource(if (reviewing) R.string.recipe_scan_check else R.string.recipe_scan)) },
            navigationIcon = {
                if (reviewing) IconButton(onClick = { reviewing = false }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) }
                else IconButton(onClick = { close(null) }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) }
            })
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().imePadding()) {
            Column(Modifier.weight(1f).fillMaxWidth().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
                state?.let { if (!it.ocr) Text(it.reason ?: stringResource(R.string.recipe_scan_no_ocr), color = MaterialTheme.colorScheme.error) }
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                if (!reviewing) {
                    Text(stringResource(R.string.recipe_scan_pages_hint), style = MaterialTheme.typography.bodyMedium)
                    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        pages.forEachIndexed { i, f ->
                            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                                Thumb(f, Modifier.width(96.dp).height(128.dp))
                                Text(stringResource(R.string.recipe_scan_page, i + 1), style = MaterialTheme.typography.bodySmall)
                                IconButton(onClick = { pages.removeAt(i).delete() }) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.recipe_scan_remove_page)) }
                            }
                        }
                    }
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedButton(onClick = { scanner.start() }, enabled = !busy && pages.size < 10 && state?.ocr == true) { Text(stringResource(R.string.recipe_scan_add_pages)) }
                        OutlinedButton(onClick = { pickPages.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) },
                            enabled = !busy && pages.size < 10 && state?.ocr == true) { Text(stringResource(R.string.recipe_scan_pick_pages)) }
                    }
                } else {
                    val d = drafts.getOrNull(cur)
                    if (drafts.size > 1) Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically) {
                        drafts.forEachIndexed { i, x ->
                            FilterChip(selected = i == cur, onClick = { cur = i }, label = { Text(x.title.ifBlank { stringResource(R.string.recipe_scan_recipe_n, i + 1) }) })
                        }
                        TextButton(onClick = { drafts.removeAt(cur); images.removeAt(cur)?.delete(); cur = 0 }) { Text(stringResource(R.string.recipe_scan_remove_recipe)) }
                    }
                    hints.forEach { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
                    if (usedAi) Text(stringResource(R.string.recipe_scan_by_ai), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    if (state?.aiAllowed == true && !usedAi && raw.isNotBlank())
                        OutlinedButton(onClick = { withAi() }, enabled = !busy) { Text(stringResource(R.string.recipe_scan_ai)) }
                    if (d != null) {
                        fun upd(f: (ScanDraft) -> ScanDraft) { drafts[cur] = f(drafts[cur]) }
                        Field(R.string.recipe_scan_title, d.title) { v -> upd { it.copy(title = v) } }
                        Field(R.string.recipe_scan_yield, d.yield) { v -> upd { it.copy(yield = v) } }
                        Field(R.string.recipe_scan_calories, d.calories, hint = R.string.recipe_scan_calories_hint, number = true) { v -> upd { it.copy(calories = v.filter(Char::isDigit).take(4)) } }
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Field(R.string.recipe_scan_prep, d.prepTime, Modifier.weight(1f)) { v -> upd { it.copy(prepTime = v) } }
                            Field(R.string.recipe_scan_cook, d.cookTime, Modifier.weight(1f)) { v -> upd { it.copy(cookTime = v) } }
                        }
                        Field(R.string.recipe_scan_total, d.totalTime) { v -> upd { it.copy(totalTime = v) } }
                        Field(R.string.recipe_scan_description, d.description, lines = 2) { v -> upd { it.copy(description = v) } }
                        Field(R.string.recipe_scan_ingredients, d.ingredients, lines = 6, hint = R.string.recipe_scan_ingredients_hint) { v -> upd { it.copy(ingredients = v) } }
                        Field(R.string.recipe_scan_steps, d.steps, lines = 6, hint = R.string.recipe_scan_steps_hint) { v -> upd { it.copy(steps = v) } }
                        Field(R.string.recipe_scan_tip, d.tip, lines = 2) { v -> upd { it.copy(tip = v) } }
                        Field(R.string.recipe_scan_source, d.source, hint = R.string.recipe_scan_source_hint) { v -> upd { it.copy(source = v) } }
                        TitleImage(images.getOrNull(cur), onPick = { f -> images[cur]?.delete(); images[cur] = f }, onCrop = { cropping = true },
                            onClear = { images[cur]?.delete(); images[cur] = null })
                    }
                    if (raw.isNotBlank()) {
                        var open by remember { mutableStateOf(false) }
                        TextButton(onClick = { open = !open }) { Text(stringResource(R.string.recipe_scan_raw)) }
                        if (open) Text(raw, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
            }
            Row(Modifier.fillMaxWidth().padding(16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                if (busy) CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 2.dp)
                if (!reviewing) Button(onClick = { recognize() }, enabled = !busy && pages.isNotEmpty() && state?.ocr == true, modifier = Modifier.weight(1f)) {
                    Text(stringResource(if (busy) R.string.recipe_scan_recognizing else R.string.recipe_scan_recognize))
                }
                else Button(onClick = { save() }, enabled = !busy && drafts.isNotEmpty(), modifier = Modifier.weight(1f)) {
                    Text(if (drafts.size > 1) stringResource(R.string.recipe_scan_create_n, drafts.size) else stringResource(R.string.recipe_scan_create))
                }
            }
        }
    }
}

@Composable
private fun Field(label: Int, value: String, modifier: Modifier = Modifier.fillMaxWidth(), lines: Int = 1, hint: Int? = null, number: Boolean = false, onChange: (String) -> Unit) {
    OutlinedTextField(value = value, onValueChange = onChange, label = { Text(stringResource(label)) }, modifier = modifier,
        singleLine = lines == 1, minLines = lines, supportingText = hint?.let { { Text(stringResource(it)) } },
        suffix = if (number) { { Text("kcal") } } else null,
        keyboardOptions = if (number) androidx.compose.foundation.text.KeyboardOptions(keyboardType = androidx.compose.ui.text.input.KeyboardType.Number)
            else androidx.compose.foundation.text.KeyboardOptions.Default)
}

/** Vorschaubild einer Datei (verkleinert geladen). */
@Composable
private fun Thumb(f: File, modifier: Modifier) {
    var bmp by remember(f) { mutableStateOf<Bitmap?>(null) }
    LaunchedEffect(f) { bmp = withContext(Dispatchers.IO) { loadUpright(f, 400) } }
    Box(modifier.background(MaterialTheme.colorScheme.surfaceVariant), contentAlignment = Alignment.Center) {
        bmp?.let { Image(it.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize()) }
    }
}

/** Titelbild: Foto aufnehmen, aus der Galerie, Ausschnitt einer Seite oder keins. */
@Composable
private fun TitleImage(image: File?, onPick: (File) -> Unit, onCrop: () -> Unit, onClear: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    fun target() = File(File(ctx.cacheDir, "files/recipe-scan").apply { mkdirs() }, "titel-${System.currentTimeMillis()}.jpg")
    val shot = remember { File(File(ctx.cacheDir, "camera").apply { mkdirs() }, "recipe-title.jpg") }
    val uri = remember { androidx.core.content.FileProvider.getUriForFile(ctx, ctx.packageName + ".files", shot) }
    // Foto oder Galeriebild aufrecht und verkleinert übernehmen
    fun take(src: File) = scope.launch {
        val f = withContext(Dispatchers.IO) { loadUpright(src, 1600)?.let { toJpeg(it, target()) } }
        if (f != null) onPick(f)
    }
    val camera = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { ok -> if (ok) take(shot) }
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok -> if (ok) camera.launch(uri) }
    val gallery = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { u ->
        if (u == null) return@rememberLauncherForActivityResult
        scope.launch {
            val tmp = File(File(ctx.cacheDir, "camera").apply { mkdirs() }, "recipe-gallery.jpg")
            val ok = withContext(Dispatchers.IO) { try { ctx.contentResolver.openInputStream(u)?.use { i -> tmp.outputStream().use { o -> i.copyTo(o) } } != null } catch (_: Exception) { false } }
            if (ok) take(tmp)
        }
    }
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text(stringResource(R.string.recipe_scan_image), style = MaterialTheme.typography.titleSmall)
        if (image != null) Thumb(image, Modifier.width(160.dp).height(110.dp))
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            OutlinedButton(onClick = {
                if (androidx.core.content.ContextCompat.checkSelfPermission(ctx, android.Manifest.permission.CAMERA) == android.content.pm.PackageManager.PERMISSION_GRANTED) camera.launch(uri)
                else permission.launch(android.Manifest.permission.CAMERA)
            }) { Text(stringResource(R.string.recipe_scan_take_photo)) }
            OutlinedButton(onClick = { gallery.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)) }) { Text(stringResource(R.string.recipe_scan_gallery)) }
            OutlinedButton(onClick = onCrop) { Text(stringResource(R.string.recipe_scan_crop)) }
            if (image != null) TextButton(onClick = onClear) { Text(stringResource(R.string.recipe_scan_no_image)) }
        }
    }
}

/** Ausschnitt einer Seite als Titelbild: Seite wählen, Ecken um das Bild des Gerichts legen (Ecken-Editor aus dem DMS). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun CropPage(pages: List<File>, insets: WindowInsets?, onDone: (File?) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var index by remember { mutableStateOf(0) }
    var bmp by remember { mutableStateOf<Bitmap?>(null) }
    var corners by remember { mutableStateOf<List<Offset>>(emptyList()) }
    var busy by remember { mutableStateOf(false) }
    LaunchedEffect(index) {
        bmp = null
        val b = withContext(Dispatchers.IO) { loadUpright(pages[index], 2000) } ?: return@LaunchedEffect
        val dx = b.width * 0.2f; val dy = b.height * 0.2f
        corners = listOf(Offset(dx, dy), Offset(b.width - dx, dy), Offset(b.width - dx, b.height - dy), Offset(dx, b.height - dy))
        bmp = b
    }
    BackHandler { onDone(null) }
    Scaffold(contentWindowInsets = insets ?: ScaffoldDefaults.contentWindowInsets, topBar = {
        TopAppBar(windowInsets = insets ?: TopAppBarDefaults.windowInsets, title = { Text(stringResource(R.string.recipe_scan_crop)) },
            navigationIcon = { IconButton(onClick = { onDone(null) }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } })
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            if (pages.size > 1) Row(Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                pages.indices.forEach { i -> FilterChip(selected = i == index, onClick = { index = i }, label = { Text(stringResource(R.string.recipe_scan_page, i + 1)) }) }
            }
            Box(Modifier.weight(1f).fillMaxWidth().background(Color.Black), contentAlignment = Alignment.Center) {
                val b = bmp
                if (b == null || busy) CircularProgressIndicator() else CornerEditor(b, corners) { corners = it }
            }
            Text(stringResource(R.string.recipe_scan_crop_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
            Button(onClick = {
                val b = bmp ?: return@Button
                busy = true
                scope.launch {
                    val f = withContext(Dispatchers.Default) {
                        toJpeg(warp(b, corners), File(File(ctx.cacheDir, "files/recipe-scan").apply { mkdirs() }, "titel-${System.currentTimeMillis()}.jpg"))
                    }
                    busy = false
                    onDone(f)
                }
            }, enabled = bmp != null && !busy, modifier = Modifier.fillMaxWidth().padding(16.dp)) { Text(stringResource(R.string.recipe_scan_crop_apply)) }
        }
    }
}
