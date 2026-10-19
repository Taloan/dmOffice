package at.dimes.dmoffice.ui

import android.content.Intent
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Reply
import androidx.compose.material.icons.automirrored.filled.ReplyAll
import androidx.compose.material.icons.filled.Archive
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.runtime.collectAsState
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material.icons.filled.Forward
import androidx.compose.material.icons.filled.MarkEmailUnread
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.outlined.Flag
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.FileProvider
import androidx.webkit.WebSettingsCompat
import androidx.webkit.WebViewFeature
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.Message
import kotlinx.coroutines.launch
import java.io.File

/** Eine Mail lesen: Kopf, Anhaenge, Inhalt im WebView (serverseitig bereinigtes HTML), Aktionen oben. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MessageScreen(account: String, folder: String, uid: Long, onClose: () -> Unit, onCompose: (ComposeInit) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var msg by remember { mutableStateOf<Message?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var menu by remember { mutableStateOf(false) }
    var confirmDelete by remember { mutableStateOf(false) }
    var flagged by remember { mutableStateOf(false) }
    val dark = androidx.compose.foundation.isSystemInDarkTheme()
    val item by remember(uid) { app.mail.observeItem(account, folder, uid) }.collectAsState(initial = null)
    val keywords = item?.keywords?.toSet() ?: emptySet()
    val categories by app.mail.categories.collectAsState()
    val folders by remember(account) { app.mail.folders(account) }.collectAsState(initial = emptyList())
    val special = folders.firstOrNull { it.path == folder }?.special
    LaunchedEffect(Unit) { if (categories.isEmpty()) app.mail.loadCategories() }
    fun toast(res: Int) = android.widget.Toast.makeText(ctx, ctx.getString(res), android.widget.Toast.LENGTH_SHORT).show()
    fun act(block: suspend () -> Unit) { scope.launch { try { block() } catch (e: Exception) { error = e.message } } }

    LaunchedEffect(uid) {
        try {
            val m = app.mail.message(account, folder, uid)
            msg = m; flagged = m.flagged
            if (!m.seen) try { app.mail.setSeen(account, folder, listOf(uid), true) } catch (_: Exception) { }
        } catch (e: Exception) { error = e.message }
    }

    Scaffold(topBar = {
        TopAppBar(
            title = { Text(msg?.subject ?: "", maxLines = 1, style = MaterialTheme.typography.titleMedium) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                val m = msg
                if (m != null) {
                    IconButton(onClick = { onCompose(ComposeInit.reply(ctx, account, m, all = false)) }) { Icon(Icons.AutoMirrored.Filled.Reply, contentDescription = stringResource(R.string.reply), tint = ActionColors.Reply) }
                    // Archivieren und Loeschen direkt, alles Weitere im Menue (wie das Menueband im Web)
                    IconButton(onClick = { act { if (app.mail.archive(account, folder, listOf(uid))) onClose() else error = ctx.getString(R.string.no_archive) } }) {
                        Icon(Icons.Default.Archive, contentDescription = stringResource(R.string.archive), tint = ActionColors.Archive)
                    }
                    IconButton(onClick = { confirmDelete = true }) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.delete)) }
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = null) }
                    MailMenu(menu, { menu = false }, flagged, inJunk = special == "junk", inSnoozed = special == "snoozed",
                        categories = categories, keywords = keywords, a = MailMenuActions(
                            replyAll = { onCompose(ComposeInit.reply(ctx, account, m, all = true)) },
                            forward = { onCompose(ComposeInit.forward(ctx, account, m)) },
                            archive = { act { if (app.mail.archive(account, folder, listOf(uid))) onClose() else error = ctx.getString(R.string.no_archive) } },
                            junk = { act { app.mail.junk(account, folder, listOf(uid), true); toast(R.string.learned_junk); onClose() } },
                            notJunk = { act { app.mail.junk(account, folder, listOf(uid), false); toast(R.string.learned_not_junk); if (special == "junk") onClose() } },
                            markUnread = { act { app.mail.setSeen(account, folder, listOf(uid), false); onClose() } },
                            followUp = { due -> act { app.mail.followUp(account, folder, listOf(uid), due); flagged = true } },
                            followUpDone = { act { app.mail.followUp(account, folder, listOf(uid), null, done = true); flagged = false } },
                            category = { key, on -> act { app.mail.setCategory(account, folder, listOf(uid), key, on) } },
                            snooze = { at -> act { app.mail.snooze(account, folder, listOf(uid), at.atZone(java.time.ZoneId.systemDefault()).toOffsetDateTime().toString()); onClose() } },
                            unsnooze = { act { app.mail.unsnooze(account, folder, listOf(uid)); onClose() } },
                            moveFocus = { focused, always -> act { app.mail.moveFocus(account, folder, listOf(uid), focused, always)
                                toast(if (focused) R.string.moved_focused else R.string.moved_other) } },
                        ), focus = if (special == "inbox") (item?.focus ?: true) else null)
                }
            }
        )
    }) { pad ->
        val m = msg
        Column(Modifier.padding(pad).fillMaxSize()) {
            error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
            if (m == null) { if (error == null) CircularProgressIndicator(Modifier.padding(24.dp)); return@Column }
            Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                Text(m.subject.ifBlank { "(…)" }, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                Text((m.from?.full ?: ""), style = MaterialTheme.typography.bodyMedium)
                Text(stringResource(R.string.to) + ": " + m.to.joinToString(", ") { it.text } + (if (m.cc.isNotEmpty()) "  ·  Cc: " + m.cc.joinToString(", ") { it.text } else ""),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(formatDate(ctx, m.date), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                CategoryChips(categories.filter { it.key in keywords })
                // Newsletter: abmelden wie im Web (One-Click/Mail ueber den Server, sonst Link im Browser)
                if (m.unsubscribe != null) {
                    var unsubDone by remember(uid) { mutableStateOf(false) }
                    TextButton(enabled = !unsubDone, onClick = { act {
                        val (done, url) = app.mail.unsubscribe(account, folder, uid)
                        if (done) { unsubDone = true; toast(R.string.unsubscribed) }
                        else if (url != null) try { ctx.startActivity(Intent(Intent.ACTION_VIEW, android.net.Uri.parse(url))) } catch (_: Exception) { }
                    } }) { Text(stringResource(if (unsubDone) R.string.unsubscribed else R.string.unsubscribe)) }
                }
                if (m.attachments.any { !it.inline }) Row(Modifier.fillMaxWidth().padding(top = 6.dp).horizontalScroll(rememberScrollState())) {
                    for (a in m.attachments.filter { !it.inline }) AssistChip(
                        onClick = { scope.launch { openAttachment(ctx, account, folder, uid, a.index, a.name, a.mime) } },
                        label = { Text(a.name.ifBlank { "#" + a.index } + " · " + sizeText(a.size)) },
                        leadingIcon = { Icon(Icons.Default.AttachFile, contentDescription = null, modifier = Modifier.padding(0.dp)) },
                        modifier = Modifier.padding(end = 6.dp)
                    )
                }
                if (m.hasRemoteImages && !m.imagesAllowed) TextButton(onClick = { scope.launch { try { msg = app.mail.message(account, folder, uid, images = true, force = true) } catch (e: Exception) { error = e.message } } }) { Text(stringResource(R.string.load_images)) }
            }
            val bg = MaterialTheme.colorScheme.background.toArgb()
            val fg = MaterialTheme.colorScheme.onBackground.toArgb()
            AndroidView(
                factory = { c ->
                    WebView(c).apply {
                        settings.javaScriptEnabled = false
                        settings.loadWithOverviewMode = true
                        settings.useWideViewPort = true
                        settings.builtInZoomControls = true
                        settings.displayZoomControls = false
                        settings.blockNetworkImage = false
                        setBackgroundColor(bg)
                        if (WebViewFeature.isFeatureSupported(WebViewFeature.ALGORITHMIC_DARKENING)) WebSettingsCompat.setAlgorithmicDarkeningAllowed(settings, dark)
                        webViewClient = object : WebViewClient() {
                            override fun shouldOverrideUrlLoading(view: WebView, request: android.webkit.WebResourceRequest): Boolean {
                                try { c.startActivity(Intent(Intent.ACTION_VIEW, request.url)) } catch (_: Exception) { }
                                return true
                            }
                        }
                    }
                },
                update = { wv ->
                    val css = "body{font-family:sans-serif;font-size:16px;line-height:1.45;margin:12px;word-wrap:break-word;color:${hex(fg)};background:${hex(bg)}} img{max-width:100%;height:auto} table{max-width:100%} pre{white-space:pre-wrap} a{color:#4A9EE8}"
                    val html = if (m.html.isNotBlank()) m.html else "<pre style=\"white-space:pre-wrap;font-family:sans-serif\">" + android.text.Html.escapeHtml(m.text) + "</pre>"
                    wv.loadDataWithBaseURL(app.prefs.account.value?.baseUrl, "<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><style>$css</style></head><body>$html</body></html>", "text/html", "utf-8", null)
                },
                modifier = Modifier.fillMaxSize()
            )
        }
    }

    if (confirmDelete) AlertDialog(
        onDismissRequest = { confirmDelete = false },
        title = { Text(stringResource(R.string.delete)) }, text = { Text(stringResource(R.string.delete_mail_confirm)) },
        confirmButton = { TextButton(onClick = { confirmDelete = false; scope.launch { try { app.mail.delete(account, folder, listOf(uid)); onClose() } catch (e: Exception) { error = e.message } } }) { Text(stringResource(R.string.delete)) } },
        dismissButton = { TextButton(onClick = { confirmDelete = false }) { Text(stringResource(R.string.cancel)) } }
    )
}

private fun hex(argb: Int) = String.format("#%06X", argb and 0xFFFFFF)

fun sizeText(n: Long): String = when {
    n < 1024 -> "$n B"
    n < 1024 * 1024 -> "${n / 1024} KB"
    else -> String.format("%.1f MB", n / 1024.0 / 1024.0)
}

fun formatDate(ctx: android.content.Context, iso: String): String {
    val i = try { java.time.OffsetDateTime.parse(iso).toInstant() } catch (_: Exception) { try { java.time.Instant.parse(iso) } catch (_: Exception) { return iso } }
    return Fmt.dateTime(ctx, i.toEpochMilli(), false)
}

/** Anhang in den Cache laden und mit der passenden App oeffnen (FileProvider). */
suspend fun openAttachment(ctx: android.content.Context, account: String, folder: String, uid: Long, index: Int, name: String, mime: String) {
    try {
        val (bytes, type) = App.get(ctx).mail.attachment(account, folder, uid, index)
        val dir = File(ctx.cacheDir, "attachments").apply { mkdirs() }
        val safe = name.ifBlank { "anhang-$index" }.replace(Regex("[^A-Za-z0-9._ -]"), "_")
        val f = File(dir, safe).apply { writeBytes(bytes) }
        val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".files", f)
        val i = Intent(Intent.ACTION_VIEW).setDataAndType(uri, (type ?: mime).ifBlank { "*/*" }.substringBefore(';')).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        ctx.startActivity(Intent.createChooser(i, name).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    } catch (e: Exception) {
        android.widget.Toast.makeText(ctx, e.message ?: "Fehler", android.widget.Toast.LENGTH_LONG).show()
    }
}

@Suppress("unused")
private fun keepScroll() = Modifier.verticalScroll(androidx.compose.runtime.mutableStateOf(0).let { androidx.compose.foundation.ScrollState(0) })
