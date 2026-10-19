package at.dimes.dmoffice.ui

import android.content.Context
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material3.AssistChip
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.Draft
import at.dimes.dmoffice.api.Message
import kotlinx.coroutines.launch
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

/** Vorbelegung des Editors (neu, Antwort, Weiterleitung); wird als JSON in der Route uebergeben. */
@Serializable
data class ComposeInit(
    val account: String, val to: String = "", val cc: String = "", val subject: String = "", val body: String = "",
    val inReplyTo: String? = null, val references: List<String> = emptyList(),
    val replyFolder: String? = null, val replyUid: Long? = null,
    val forwardFolder: String? = null, val forwardUid: Long? = null, val forwardAttachments: List<Int> = emptyList(),
) {
    fun encode(): String = java.net.URLEncoder.encode(Json.encodeToString(this), "UTF-8")

    companion object {
        fun decode(s: String): ComposeInit = Json.decodeFromString(java.net.URLDecoder.decode(s, "UTF-8"))

        private fun quote(ctx: Context, m: Message): String =
            "\n\n" + ctx.getString(R.string.wrote_on, formatDate(ctx, m.date), m.from?.full ?: "") + "\n" + m.text.lines().joinToString("\n") { "> $it" }

        fun reply(ctx: Context, account: String, m: Message, all: Boolean): ComposeInit {
            val me = App.get(ctx).prefs.account.value?.login ?: ""
            val replyTo = (m.replyTo.ifEmpty { listOfNotNull(m.from) }).map { it.full }
            val others = if (all) (m.to + m.cc).filter { !it.address.equals(me, true) && replyTo.none { r -> r.contains(it.address, true) } }.map { it.full } else emptyList()
            val refs = (m.references + listOfNotNull(m.messageId)).distinct()
            return ComposeInit(account, to = replyTo.joinToString(", "), cc = others.joinToString(", "),
                subject = if (m.subject.startsWith("Re:", true)) m.subject else "Re: " + m.subject,
                body = quote(ctx, m), inReplyTo = m.messageId, references = refs, replyFolder = m.folder, replyUid = m.uid)
        }

        fun forward(ctx: Context, account: String, m: Message): ComposeInit = ComposeInit(
            account, subject = if (m.subject.startsWith("Fwd:", true) || m.subject.startsWith("WG:", true)) m.subject else "Fwd: " + m.subject,
            body = "\n\n---------- " + ctx.getString(R.string.forwarded_message) + " ----------\n" +
                ctx.getString(R.string.from_label) + ": " + (m.from?.full ?: "") + "\n" + ctx.getString(R.string.date_label) + ": " + formatDate(ctx, m.date) + "\n" +
                ctx.getString(R.string.subject) + ": " + m.subject + "\n" + ctx.getString(R.string.to) + ": " + m.to.joinToString(", ") { it.full } + "\n\n" + m.text,
            forwardFolder = m.folder, forwardUid = m.uid, forwardAttachments = m.attachments.filter { !it.inline }.map { it.index },
        )
    }
}

/** Neue Mail schreiben; gesendet wird ueber den Server (SMTP des Kontos, Kopie in Gesendet). */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ComposeScreen(init: ComposeInit, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var to by remember { mutableStateOf(init.to) }
    var cc by remember { mutableStateOf(init.cc) }
    var bcc by remember { mutableStateOf("") }
    var showCc by remember { mutableStateOf(init.cc.isNotBlank()) }
    var subject by remember { mutableStateOf(init.subject) }
    var body by remember { mutableStateOf(init.body) }
    var files by remember { mutableStateOf<List<Triple<String, String, ByteArray>>>(emptyList()) }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        for (u in uris) try {
            val name = ctx.contentResolver.query(u, null, null, null, null)?.use { c -> val i = c.getColumnIndex(android.provider.OpenableColumns.DISPLAY_NAME); if (c.moveToFirst() && i >= 0) c.getString(i) else null } ?: "datei"
            val bytes = ctx.contentResolver.openInputStream(u)?.use { it.readBytes() } ?: continue
            files = files + Triple(name, ctx.contentResolver.getType(u) ?: "application/octet-stream", bytes)
        } catch (e: Exception) { error = e.message }
    }

    fun send() {
        if (to.isBlank() && cc.isBlank() && bcc.isBlank()) { error = ctx.getString(R.string.no_recipient); return }
        busy = true; error = null
        scope.launch {
            try {
                val html = "<div style=\"font-family:sans-serif;font-size:14px\">" + android.text.Html.escapeHtml(body).replace("\n", "<br>") + "</div>"
                val warning = app.mail.send(init.account, Draft(to, cc, bcc, subject, html, init.inReplyTo, init.references, init.replyFolder, init.replyUid,
                    init.forwardFolder, init.forwardUid, init.forwardAttachments), files)
                android.widget.Toast.makeText(ctx, if (warning == null) ctx.getString(R.string.sent_ok) else ctx.getString(R.string.sent_warning, warning), android.widget.Toast.LENGTH_LONG).show()
                onClose()
            } catch (e: Exception) { error = e.message } finally { busy = false }
        }
    }

    Scaffold(topBar = {
        TopAppBar(
            title = { Text(stringResource(R.string.compose)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                IconButton(onClick = { picker.launch(arrayOf("*/*")) }) { Icon(Icons.Default.AttachFile, contentDescription = stringResource(R.string.attach_file)) }
                IconButton(onClick = { send() }, enabled = !busy) { Icon(Icons.AutoMirrored.Filled.Send, contentDescription = stringResource(R.string.send)) }
            }
        )
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedTextField(value = to, onValueChange = { to = it }, label = { Text(stringResource(R.string.to)) }, modifier = Modifier.fillMaxWidth(), singleLine = true,
                trailingIcon = { if (!showCc) TextButton(onClick = { showCc = true }) { Text("Cc") } })
            if (showCc) {
                OutlinedTextField(value = cc, onValueChange = { cc = it }, label = { Text(stringResource(R.string.cc)) }, modifier = Modifier.fillMaxWidth(), singleLine = true)
                OutlinedTextField(value = bcc, onValueChange = { bcc = it }, label = { Text(stringResource(R.string.bcc)) }, modifier = Modifier.fillMaxWidth(), singleLine = true)
            }
            OutlinedTextField(value = subject, onValueChange = { subject = it }, label = { Text(stringResource(R.string.subject)) }, modifier = Modifier.fillMaxWidth(), singleLine = true)
            if (files.isNotEmpty() || init.forwardAttachments.isNotEmpty()) Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState())) {
                if (init.forwardAttachments.isNotEmpty()) AssistChip(onClick = {}, label = { Text(stringResource(R.string.attachments) + ": " + init.forwardAttachments.size) }, modifier = Modifier.padding(end = 6.dp))
                for ((i, f) in files.withIndex()) AssistChip(onClick = { files = files.filterIndexed { j, _ -> j != i } }, label = { Text(f.first + " ✕") }, modifier = Modifier.padding(end = 6.dp))
            }
            OutlinedTextField(value = body, onValueChange = { body = it }, label = { Text(stringResource(R.string.message_body)) }, modifier = Modifier.fillMaxWidth(), minLines = 10)
            error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            if (busy) Text(stringResource(R.string.sending), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
