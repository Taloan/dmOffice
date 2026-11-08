package at.dimes.dmoffice.ui

import android.content.Intent
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowLeft
import androidx.compose.material.icons.automirrored.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Share
import androidx.compose.material3.AssistChip
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.foundation.clickable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.fromHtml
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.journal.Entry
import at.dimes.dmoffice.journal.moodEmoji
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

/**
 * Tagebucheintrag lesen (Version 2): Fotos groß zum Wischen, Datum, Ort, Wetter, Stimmung, Text; oben Teilen und Bearbeiten,
 * unten zum älteren bzw. neueren Eintrag (Reihenfolge der Zeitleiste, aus der man kam).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun JournalEntryScreen(journal: Long, uri: String, onEdit: (journal: Long, uri: String) -> Unit, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    var current by remember { mutableStateOf(journal to uri) }
    var entry by remember { mutableStateOf<Entry?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var reload by remember { mutableIntStateOf(0) }

    // Zurück vom Bearbeiten: neu laden (gelöscht → zurück zur Liste)
    val owner = LocalLifecycleOwner.current
    DisposableEffect(owner) {
        var first = true
        val o = LifecycleEventObserver { _, e -> if (e == Lifecycle.Event.ON_RESUME) { if (first) first = false else reload++ } }
        owner.lifecycle.addObserver(o)
        onDispose { owner.lifecycle.removeObserver(o) }
    }
    LaunchedEffect(current, reload) {
        try { entry = app.journal.entry(current.first, current.second); error = null }
        catch (e: Exception) { if (reload > 0) onClose() else error = e.message ?: e.toString() }
    }
    BackHandler { onClose() }

    // Nachbarn in der Zeitleiste: links älter, rechts neuer
    val order = app.journal.order
    val at = order.indexOf(current)
    val older = order.getOrNull(at + 1).takeIf { at >= 0 }
    val newer = order.getOrNull(at - 1).takeIf { at > 0 }

    Scaffold(
        topBar = {
            TopAppBar(title = {}, navigationIcon = {
                IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) }
            }, actions = {
                entry?.let { e ->
                    IconButton(onClick = {
                        val text = listOfNotNull(e.title?.takeIf { it.isNotBlank() }, e.text.trim().takeIf { it.isNotEmpty() }).joinToString("\n\n")
                        ctx.startActivity(Intent.createChooser(Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, text), null))
                    }) { Icon(Icons.Default.Share, contentDescription = stringResource(R.string.journal_share_text)) }
                    if (e.canWrite) IconButton(onClick = { onEdit(e.journal, e.uri) }) { Icon(Icons.Default.Edit, contentDescription = stringResource(R.string.journal_edit)) }
                }
            })
        },
        bottomBar = {
            if (older != null || newer != null) Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp)) {
                if (older != null) IconButton(onClick = { current = older; entry = null }) {
                    Icon(Icons.AutoMirrored.Filled.KeyboardArrowLeft, contentDescription = stringResource(R.string.journal_older_entry))
                }
                Spacer(Modifier.weight(1f))
                if (newer != null) IconButton(onClick = { current = newer; entry = null }) {
                    Icon(Icons.AutoMirrored.Filled.KeyboardArrowRight, contentDescription = stringResource(R.string.journal_newer_entry))
                }
            }
        },
    ) { pad ->
        val e = entry
        var bigAt by remember { mutableStateOf<Int?>(null) }
        if (e == null) {
            Box(Modifier.padding(pad).fillMaxSize(), contentAlignment = Alignment.Center) {
                if (error != null) Text(error!!, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(24.dp)) else CircularProgressIndicator()
            }
            return@Scaffold
        }
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState())) {
            val pics = e.media.filter { it.isImage || it.isVideo }
            bigAt?.let { at -> JournalPhotoViewer(e.journal, pics.filter { it.isImage }, at) { bigAt = null } }
            if (pics.isNotEmpty()) {
                val pager = rememberPagerState { pics.size }
                Box(Modifier.fillMaxWidth().height(420.dp).background(Color.Black)) {
                    // Tippen: Bilder groß, mit Zoom und Blättern
                    HorizontalPager(pager, Modifier.fillMaxSize(), key = { pics[it].path }) { i ->
                        Thumb(e.journal, pics[i], Modifier.fillMaxSize().clickable { if (pics[i].isImage) bigAt = pics.filter { it.isImage }.indexOf(pics[i]) }, size = 1024, scale = ContentScale.Fit)
                    }
                    if (pics.size > 1) Text("${pager.currentPage + 1} / ${pics.size}", Modifier.align(Alignment.BottomEnd).padding(12.dp)
                        .clip(RoundedCornerShape(8.dp)).background(Color.Black.copy(alpha = 0.55f)).padding(horizontal = 8.dp, vertical = 2.dp), color = Color.White)
                }
            }
            val d = try { Instant.parse(e.date).atZone(ZoneId.systemDefault()) } catch (_: Exception) { null }
            Column(Modifier.padding(horizontal = 20.dp, vertical = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (d != null) Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    Text("%02d".format(d.dayOfMonth), fontSize = 44.sp, fontWeight = FontWeight.Bold, color = MaterialTheme.colorScheme.primary)
                    Column {
                        Text(d.format(DateTimeFormatter.ofPattern("d. MMMM yyyy", Locale.getDefault())), fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.primary)
                        Text(d.format(DateTimeFormatter.ofPattern("EEEE HH:mm", Locale.getDefault())), fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.primary)
                    }
                    Spacer(Modifier.weight(1f))
                    if (e.private) Icon(Icons.Default.Lock, contentDescription = stringResource(R.string.journal_private_short), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    e.mood?.let { Text(moodEmoji(it), fontSize = 28.sp) }
                }
                val meta = listOfNotNull(
                    e.place?.let { p -> p.name?.takeIf { it.isNotBlank() } ?: p.lat?.let { "%.4f, %.4f".format(Locale.ROOT, it, p.lon ?: 0.0) } }?.let { "📍 $it" },
                    e.weather?.takeIf { it.isNotBlank() }?.let { "🌤 $it" },
                    e.owner?.let { "👥 $it" },
                )
                if (meta.isNotEmpty()) Text(meta.joinToString("  ·  "), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                if (e.tags.isNotEmpty()) Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    e.tags.take(6).forEach { AssistChip(onClick = {}, label = { Text("#$it") }) }
                }
                e.title?.takeIf { it.isNotBlank() }?.let { Text(it, style = MaterialTheme.typography.titleLarge, modifier = Modifier.padding(top = 8.dp)) }
                val body = remember(e.html) { AnnotatedString.fromHtml(e.html) }
                if (body.isNotBlank()) Text(body, style = MaterialTheme.typography.bodyLarge, modifier = Modifier.padding(top = 4.dp))
            }
        }
    }
}
