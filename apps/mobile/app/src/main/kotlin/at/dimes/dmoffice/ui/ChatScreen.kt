package at.dimes.dmoffice.ui

import androidx.compose.ui.draw.alpha
import androidx.compose.foundation.layout.offset
import androidx.compose.animation.core.animateFloat
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.horizontalScroll
import androidx.compose.material3.ListItem
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.filled.ThumbUp
import androidx.compose.material.icons.filled.ThumbDown
import androidx.compose.material.icons.outlined.ThumbUp as ThumbUpOutline
import androidx.compose.material.icons.outlined.ThumbDown as ThumbDownOutline
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Reply
import androidx.compose.material.icons.automirrored.filled.Send
import androidx.compose.material.icons.automirrored.filled.StickyNote2
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.Place
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.filled.PhotoLibrary
import androidx.compose.material.icons.filled.Videocam
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.Cloud
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Event
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.automirrored.filled.Forward
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Done
import androidx.compose.material.icons.filled.DoneAll
import androidx.compose.material.icons.filled.Group
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Save
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.Slider
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.chat.ChatMember
import at.dimes.dmoffice.chat.ChatMessage
import at.dimes.dmoffice.chat.ChatPerson
import at.dimes.dmoffice.chat.ChatRef
import at.dimes.dmoffice.chat.ChatRoom
import at.dimes.dmoffice.chat.LiveLocation
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.notify.ChatNotify
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle

private val QUICK = listOf("👍", "❤️", "😂", "😮", "😢", "🙏")

private fun localTime(ms: Long): String =
    Instant.ofEpochMilli(ms).atZone(ZoneId.systemDefault()).toLocalTime().format(DateTimeFormatter.ofLocalizedTime(FormatStyle.SHORT))

private fun dayOf(ms: Long): LocalDate = Instant.ofEpochMilli(ms).atZone(ZoneId.systemDefault()).toLocalDate()

/** Art eines Gruppenraums in Worten (Stufe 4). */
@Composable
private fun spaceLabel(kind: String): String = stringResource(when (kind) {
    "calendar" -> R.string.space_calendar; "list" -> R.string.space_list; "folder" -> R.string.space_folder; else -> R.string.space_notes
})

private fun spaceIcon(kind: String): androidx.compose.ui.graphics.vector.ImageVector = when (kind) {
    "calendar" -> Icons.Default.CalendarMonth; "list" -> Icons.Default.Checklist; "folder" -> Icons.Default.Folder
    else -> Icons.AutoMirrored.Filled.StickyNote2
}

/** Hinweis im Verlauf (JSON vom Server) in Worten. */
@Composable
private fun systemText(m: ChatMessage, names: (String) -> String): String {
    val o = try { kotlinx.serialization.json.Json.parseToJsonElement(m.text ?: "{}") as? kotlinx.serialization.json.JsonObject } catch (_: Exception) { null }
    val ev = o?.get("ev")?.let { (it as? kotlinx.serialization.json.JsonPrimitive)?.content }
    val title = o?.get("title")?.let { (it as? kotlinx.serialization.json.JsonPrimitive)?.content } ?: ""
    val who = (o?.get("users") as? kotlinx.serialization.json.JsonArray)?.joinToString(", ") { names((it as kotlinx.serialization.json.JsonPrimitive).content) } ?: ""
    val by = names(m.sender)
    return when (ev) {
        "created" -> stringResource(R.string.chat_sys_created, by, title)
        "renamed" -> stringResource(R.string.chat_sys_renamed, by, title)
        "added" -> stringResource(R.string.chat_sys_added, by, who)
        "removed" -> stringResource(R.string.chat_sys_removed, by, who)
        "left" -> stringResource(R.string.chat_sys_left, who)
        "space" -> stringResource(R.string.chat_sys_space, by, spaceLabel(o?.get("kind")?.let { (it as? kotlinx.serialization.json.JsonPrimitive)?.content } ?: ""), title)
        else -> m.text ?: ""
    }
}

@Composable
private fun Initials(name: String, group: Boolean, size: Int = 40, pukki: Boolean = false, look: ChatMember? = null) {
    // Version 2: eine Person - ihr Profilbild bzw. Anfangsbuchstabe in ihrer Akzentfarbe
    if (pukki) { PukkiAvatar(size); return }
    if (look != null && !group) { PersonAvatar(look.id, look.name.ifBlank { name }, look.login, look.avatar, look.accent, size); return }
    Box(Modifier.size(size.dp).clip(CircleShape).background(MaterialTheme.colorScheme.secondaryContainer), contentAlignment = Alignment.Center) {
        if (group) Icon(Icons.Default.Group, contentDescription = null, tint = MaterialTheme.colorScheme.onSecondaryContainer)
        else Text(name.split(' ').filter { it.isNotBlank() }.take(2).joinToString("") { it.take(1).uppercase() },
            color = MaterialTheme.colorScheme.onSecondaryContainer, fontWeight = FontWeight.SemiBold)
    }
}

private const val PukkiId = "pukki"

/** Lange nichts gehört: nach sechs Stunden schläft Pukki (Zzz), nach drei Tagen träumt er. Die nächste Nachricht weckt ihn. */
@Composable
private fun PukkiNap(lastMs: Long) {
    val age = System.currentTimeMillis() - lastMs
    if (age < 6 * 3600_000L) return
    val dreams = age >= 3 * 24 * 3600_000L
    val float = androidx.compose.animation.core.rememberInfiniteTransition(label = "zzz")
    val lift by float.animateFloat(0f, 1f, androidx.compose.animation.core.infiniteRepeatable(androidx.compose.animation.core.tween(2400)), label = "steigt")
    Row(Modifier.fillMaxWidth().padding(top = 16.dp, bottom = 4.dp), verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Box(Modifier.size(64.dp)) {
            PukkiPose("bitte", 64)
            if (dreams) Text("💭", Modifier.align(Alignment.TopEnd).offset(x = 14.dp, y = (-14).dp), fontSize = 20.sp)
            else Text("Zzz", Modifier.align(Alignment.TopEnd).offset(x = (6 + lift * 8).dp, y = (-lift * 14).dp).alpha(1f - lift * 0.8f),
                color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold)
        }
        Text(stringResource(if (dreams) R.string.pukki_dreams else R.string.pukki_sleeps), style = MaterialTheme.typography.bodyMedium,
            fontStyle = androidx.compose.ui.text.font.FontStyle.Italic, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}

/** „Pukki denkt nach“ mit drei pulsierenden Punkten - solange er die KI fragt (wie im Web). */
@Composable
private fun ThinkingBubble() {
    val pulse = androidx.compose.animation.core.rememberInfiniteTransition(label = "denkt")
    Row(Modifier.fillMaxWidth().padding(vertical = 2.dp), verticalAlignment = Alignment.Bottom, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        PukkiPose("arbeitet", 48)
        Surface(shape = RoundedCornerShape(16.dp), color = MaterialTheme.colorScheme.surfaceContainerHigh) {
            Row(Modifier.padding(horizontal = 10.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.chat_thinking), style = MaterialTheme.typography.bodyMedium, fontStyle = androidx.compose.ui.text.font.FontStyle.Italic,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                Row(horizontalArrangement = Arrangement.spacedBy(3.dp)) {
                    repeat(3) { i ->
                        val a by pulse.animateFloat(0.25f, 1f, androidx.compose.animation.core.infiniteRepeatable(
                            androidx.compose.animation.core.tween(600, delayMillis = i * 200), androidx.compose.animation.core.RepeatMode.Reverse), label = "punkt$i")
                        Box(Modifier.size(6.dp).clip(CircleShape).background(MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = a)))
                    }
                }
            }
        }
    }
}

/** Ohne Netz geschrieben, wartet auf Versand: eigene Blase mit Uhr; lange drücken bietet „Nicht senden“ an. */
@OptIn(androidx.compose.foundation.ExperimentalFoundationApi::class)
@Composable
private fun PendingBubble(o: at.dimes.dmoffice.sync.OutChat, onCancel: () -> Unit) {
    var menu by remember { mutableStateOf(false) }
    Box(Modifier.fillMaxWidth(), contentAlignment = Alignment.CenterEnd) {
        Surface(shape = RoundedCornerShape(topStart = 16.dp, topEnd = 16.dp, bottomStart = 16.dp, bottomEnd = 4.dp),
            color = MaterialTheme.colorScheme.primaryContainer.copy(alpha = 0.6f),
            modifier = Modifier.padding(end = 38.dp).widthIn(max = 300.dp).padding(top = 6.dp).combinedClickable(onClick = {}, onLongClick = { menu = true })) {
            Column(Modifier.padding(horizontal = 10.dp, vertical = 6.dp)) {
                Text(o.text?.takeIf { it.isNotBlank() } ?: if (o.mime?.startsWith("audio/") == true) "🎤 " + stringResource(R.string.chat_voice_name) else "📎 " + (o.name ?: ""))
                Row(Modifier.align(Alignment.End), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Default.Schedule, contentDescription = null, Modifier.size(12.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text(" " + stringResource(R.string.chat_waiting), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
            DropdownMenuItem(text = { Text(stringResource(R.string.chat_waiting_cancel)) }, onClick = { menu = false; onCancel() })
        }
    }
}

/** @Erwaehnungen: Mitglieder, in Gruppen auch Pukki (antwortet auf „@Pukki …“, ist aber kein Mitglied). */
private fun mentionPeople(r: ChatRoom): List<ChatMember> =
    if (r.isGroup && r.members.none { it.id == PukkiId }) r.members + ChatMember(PukkiId, "Pukki") else r.members

/** Namen, die als „@…“ gelten (Anzeigename, Vorname, Login, in Gruppen „alle“), und welche davon mich meinen. */
private class MentionNames(val all: List<String>, val mine: Set<String>)

private fun mentionNames(r: ChatRoom, me: String?): MentionNames {
    fun of(p: ChatMember) = listOfNotNull(p.name, p.name.split(' ').firstOrNull(), p.login).filter { it.isNotBlank() }
    val everyone = listOf("alle", "all", "everyone")
    val people = mentionPeople(r)
    return MentionNames((people.flatMap(::of) + if (r.isGroup) everyone else emptyList()).distinct(),
        (people.filter { it.id == me }.flatMap(::of) + everyone).map { it.lowercase() }.toSet())
}

/** „@Name“ als eigenes Wort hervorheben - laengste Namen zuerst, damit „@Ben Test“ vor „@Ben“ greift; die eigene Erwaehnung unterlegt. */
@Composable
private fun highlightMentions(raw: String, names: MentionNames?, onLink: (String) -> Unit = {}): androidx.compose.ui.text.AnnotatedString {
    val color = MaterialTheme.colorScheme.primary
    val mineBg = MaterialTheme.colorScheme.primaryContainer
    val re = remember(names) {
        if (names == null || names.all.isEmpty()) null
        else Regex("""(?<![\p{L}\p{N}])@(?:""" + names.all.sortedByDescending { it.length }.joinToString("|") { Regex.escape(it) } + """)(?![\p{L}\p{N}])""",
            setOf(RegexOption.IGNORE_CASE))
    }
    // [Titel](Adresse) - etwa Pukkis Schlagzeilen: nur der Titel wird gezeigt und ist der Link
    val mdLinks = mutableListOf<Triple<Int, Int, String>>()
    val text = if (!raw.contains("](")) raw else buildString {
        var from = 0
        for (m in MdLinks.findAll(raw)) {
            append(raw, from, m.range.first)
            val start = length
            append(m.groupValues[1])
            mdLinks += Triple(start, length, m.groupValues[2])
            // Fremdes Ziel hinter einem Titel: die Adresse steht dabei (wie im Web) - ein untergeschobener Titel verrät so sein Ziel
            runCatching { android.net.Uri.parse(m.groupValues[2]).host }.getOrNull()?.takeIf { it.isNotEmpty() }?.let { append(" ($it)") }
            from = m.range.last + 1
        }
        append(raw, from, raw.length)
    }
    return androidx.compose.ui.text.buildAnnotatedString {
        var at = 0
        if (re != null && names != null && text.contains('@')) for (m in re.findAll(text)) {
            append(text.substring(at, m.range.first))
            val mine = m.value.substring(1).lowercase() in names.mine
            withStyle(androidx.compose.ui.text.SpanStyle(color = color, fontWeight = FontWeight.SemiBold, background = if (mine) mineBg else androidx.compose.ui.graphics.Color.Unspecified)) { append(m.value) }
            at = m.range.last + 1
        }
        append(text.substring(at))
        // Klickbar (Version 2): Web-Adressen, Mail-Adressen (neue Mail), Telefonnummern (Telefon)
        val linkStyle = androidx.compose.ui.text.TextLinkStyles(androidx.compose.ui.text.SpanStyle(color = color, textDecoration = androidx.compose.ui.text.style.TextDecoration.Underline))
        for (m in ChatLinks.findAll(text)) {
            val value = m.value.trimEnd('.', ',', ')', ';', ':')
            val target = when {
                value.startsWith("http", ignoreCase = true) -> value
                value.contains('@') -> "mailto:$value"
                else -> "tel:" + value.filter { it.isDigit() || it == '+' }
            }
            addLink(androidx.compose.ui.text.LinkAnnotation.Clickable(target, linkStyle) { onLink(target) }, m.range.first, m.range.first + value.length)
        }
        // Interne Links (#/…) kennt nur das Web - in der App bleibt der Titel als Text
        for ((start, end, url) in mdLinks.filter { !it.third.startsWith("#") }) addLink(androidx.compose.ui.text.LinkAnnotation.Clickable(url, linkStyle) { onLink(url) }, start, end)
    }
}

/** Web-Adressen, Mail-Adressen und Telefonnummern (mit + oder 0 vorn, mindestens 7 Ziffern) in Chat-Texten. */
/** [Titel](https://…) in Chat-Texten. */
private val MdLinks = Regex("""\[([^\]\n]{1,300})\]\(((?:https?://|#/)[^\s)]+)\)""")

private val ChatLinks = Regex("""https?://[^\s<>"]+|[\w.+-]+@[\w-]+(\.[\w-]+)+|(?<![\w/.])(\+\d|0\d)[\d \-/()]{5,}\d""")

private fun snippet(m: ChatMessage?, deleted: String): String =
    when { m == null -> ""; m.deleted -> deleted; m.text != null -> m.text; else -> m.refs.joinToString(", ") { it.preview } }

/** Reiter Chat: alle Unterhaltungen, neueste oben; wartet, solange er sichtbar ist, auf Neues. */
@Composable
fun ChatListScreen(onOpen: (Long, Long?) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val owner = LocalLifecycleOwner.current
    var rooms by remember { mutableStateOf<List<ChatRoom>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var creating by remember { mutableStateOf(false) }
    var me by remember { mutableStateOf<String?>(null) }
    // Suche im Chat (Chat-Komfort): filtert die Unterhaltungen, ab drei Zeichen auch die Nachrichten
    // Version 2 wie alle Reiter: Lupe in der Kopfzeile
    val search = rememberTabSearch()
    val query = if (search.open) search.query else ""
    var hits by remember { mutableStateOf<List<at.dimes.dmoffice.chat.ChatSearchHit>?>(null) }
    LaunchedEffect(query) {
        val q = query.trim()
        if (q.length < 3) { hits = null; return@LaunchedEffect }
        delay(300)
        hits = try { app.chat.search(q) } catch (e: CancellationException) { throw e } catch (_: Exception) { emptyList() }
    }

    // Offline: was zuletzt da war, bis der Server antwortet
    LaunchedEffect(Unit) { if (rooms == null) app.chat.cache.rooms()?.let { if (rooms == null) rooms = it } }
    LaunchedEffect(owner) {
        owner.lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            me = app.chat.whoAmI()
            var seq = -1L
            while (isActive) {
                try {
                    // Noch unbekannt (Server ohne me, Netz weg): jede Runde neu fragen, sonst bliebe alles linksbuendig
                    if (me == null) me = app.chat.whoAmI()
                    if (seq < 0) { seq = app.chat.status().seq; rooms = app.chat.rooms(); error = null }
                    val next = app.chat.wait(seq)
                    if (next > seq) { seq = next; rooms = app.chat.rooms() }
                } catch (e: CancellationException) { throw e }
                catch (e: Exception) {
                    // Server ohne Chat (vor Version 2): 403 aus dem Umschlag bzw. 404 - verstaendlich sagen und selten nachfragen
                    val old = e is at.dimes.dmoffice.api.ApiException && (e.code == 403 || e.code == 404)
                    error = if (old) ctx.getString(R.string.chat_server_old) else e.message
                    seq = -1; delay(if (old) 300_000 else 10_000)
                }
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
    TabTopBar(stringResource(R.string.chat), search, searchHint = stringResource(R.string.chat_search), insets = androidx.compose.foundation.layout.WindowInsets(0))
    Box(Modifier.fillMaxSize()) {
        val all = rooms
        val list = if (query.isBlank()) all else all?.filter { it.title.contains(query.trim(), ignoreCase = true) }
        when {
            all == null && error == null -> CircularProgressIndicator(Modifier.align(Alignment.Center))
            all.isNullOrEmpty() -> Text(error ?: stringResource(R.string.chat_empty), Modifier.align(Alignment.Center).padding(32.dp),
                color = MaterialTheme.colorScheme.onSurfaceVariant)
            else -> LazyColumn(Modifier.fillMaxSize()) {
                items(list.orEmpty(), key = { it.id }) { r ->
                    Row(Modifier.fillMaxWidth().clickable { onOpen(r.id, null) }.padding(horizontal = 16.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                        // Farbe der Unterhaltung (persoenlich), ohne eigene die Akzentfarbe
                        Box(Modifier.padding(end = 8.dp).size(3.dp, 36.dp).clip(RoundedCornerShape(2.dp)).background(chatColorOf(r.color) ?: MaterialTheme.colorScheme.primary))
                        Initials(r.title, r.isGroup, pukki = r.isPukki, look = if (r.isGroup) null else r.members.firstOrNull { it.id != me })
                        Column(Modifier.weight(1f).padding(start = 12.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(r.title, Modifier.weight(1f), fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                r.last?.let { Text(localTime(it.createdMs), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                            }
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                val last = r.last
                                val text = when {
                                    last == null -> ""
                                    last.isSystem -> systemText(last) { id -> if (id == me) ctx.getString(R.string.chat_you) else r.members.firstOrNull { it.id == id }?.name ?: "?" }
                                    r.isGroup && last.sender != me -> last.senderName + ": " + snippet(last, stringResource(R.string.chat_deleted))
                                    else -> snippet(last, stringResource(R.string.chat_deleted))
                                }
                                Text(text, Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant)
                                if (r.mentioned > 0) Box(Modifier.padding(start = 6.dp).size(20.dp).clip(CircleShape).background(MaterialTheme.colorScheme.primary),
                                    contentAlignment = Alignment.Center) { Text("@", color = MaterialTheme.colorScheme.onPrimary, fontSize = 12.sp, fontWeight = FontWeight.Bold) }
                                if (r.unread > 0) Box(Modifier.padding(start = 6.dp).clip(CircleShape)
                                    .background(if (r.muted) MaterialTheme.colorScheme.outline else MaterialTheme.colorScheme.primary).padding(horizontal = 7.dp, vertical = 1.dp)) {
                                    Text(r.unread.toString(), color = MaterialTheme.colorScheme.onPrimary, fontSize = 12.sp)
                                }
                            }
                        }
                    }
                }
                hits?.let { found ->
                    item(key = "hits") {
                        Text(stringResource(if (found.isEmpty()) R.string.chat_search_none else R.string.chat_search_messages),
                            Modifier.padding(start = 16.dp, end = 16.dp, top = 14.dp, bottom = 4.dp),
                            style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    items(found, key = { "m" + it.message.id }) { h ->
                        Column(Modifier.fillMaxWidth().clickable { onOpen(h.room, h.message.id) }.padding(horizontal = 16.dp, vertical = 8.dp)) {
                            Row(verticalAlignment = Alignment.CenterVertically) {
                                Text(h.roomTitle, Modifier.weight(1f), fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                                Text(dayOf(h.message.createdMs).format(DateTimeFormatter.ofLocalizedDate(FormatStyle.SHORT)),
                                    style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                            Text(h.message.senderName + ": " + snippet(h.message, stringResource(R.string.chat_deleted)), maxLines = 2, overflow = TextOverflow.Ellipsis,
                                style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
                item(key = "end") { Spacer(Modifier.height(80.dp)) }
            }
        }
        AddButton(stringResource(R.string.chat_new), Modifier.align(Alignment.BottomEnd).padding(16.dp)) { creating = true }
    }
    }
    if (creating) NewChatDialog(onDismiss = { creating = false }, onCreated = { creating = false; onOpen(it.id, null) })
}

/** Neue Unterhaltung: eine Person = zu zweit, mehrere = Gruppe mit Namen. */
@Composable
private fun NewChatDialog(onDismiss: () -> Unit, onCreated: (ChatRoom) -> Unit) {
    val app = App.get(LocalContext.current)
    val scope = rememberCoroutineScope()
    var people by remember { mutableStateOf<List<ChatPerson>?>(null) }
    var chosen by remember { mutableStateOf(setOf<String>()) }
    var title by remember { mutableStateOf("") }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(Unit) { people = try { app.chat.people() } catch (e: Exception) { error = e.message; emptyList() } }
    val group = chosen.size > 1
    AlertDialog(onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.chat_new)) },
        text = {
            Column {
                val list = people
                if (list == null) CircularProgressIndicator()
                else if (list.isEmpty()) Text(error ?: stringResource(R.string.chat_no_people))
                else LazyColumn(Modifier.heightIn(max = 320.dp)) {
                    items(list, key = { it.id }) { p ->
                        Row(Modifier.fillMaxWidth().clickable { chosen = if (p.id in chosen) chosen - p.id else chosen + p.id }, verticalAlignment = Alignment.CenterVertically) {
                            Checkbox(checked = p.id in chosen, onCheckedChange = { chosen = if (it) chosen + p.id else chosen - p.id })
                            Text(p.displayName, Modifier.weight(1f))
                            Text(p.login, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }
                if (group) OutlinedTextField(title, { title = it }, Modifier.fillMaxWidth().padding(top = 8.dp), label = { Text(stringResource(R.string.chat_group_name)) }, singleLine = true)
            }
        },
        confirmButton = {
            TextButton(enabled = chosen.isNotEmpty() && (!group || title.isNotBlank()), onClick = {
                scope.launch {
                    try { onCreated(app.chat.open(chosen.toList(), if (group) title.trim() else null)) }
                    catch (e: Exception) { error = e.message }
                }
            }) { Text(stringResource(if (group) R.string.chat_create_group else R.string.chat_write)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(android.R.string.cancel)) } })
}

/** Eine Unterhaltung: Verlauf (neueste unten), Antworten, Reaktionen, Fotos senden, Verweise speichern. */
@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
/** onOpenSpace: Gruppenraum oeffnen (Art, Sprungziel) - Kalender, Liste, Ordner, Notizordner (Stufe 4). */
fun ChatRoomScreen(roomId: Long, focus: Long? = null, onClose: () -> Unit, onOpenSpace: (String, String?) -> Unit = { _, _ -> }, onMail: (String) -> Unit = {}) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val owner = LocalLifecycleOwner.current
    var room by remember { mutableStateOf<ChatRoom?>(null) }
    var messages by remember { mutableStateOf<List<ChatMessage>>(emptyList()) }
    var complete by remember { mutableStateOf(false) }
    var me by remember { mutableStateOf<String?>(null) }
    // Eingabe samt Cursor - @Erwaehnungen setzen den Namen ein und den Cursor dahinter
    var field by remember { mutableStateOf(androidx.compose.ui.text.input.TextFieldValue("")) }
    // Knopf unter Pukkis Antwort, der gerade ausgefuehrt wird
    // Postausgang: ohne Netz geschriebene Nachrichten (alle Unterhaltungen)
    LaunchedEffect(Unit) { at.dimes.dmoffice.sync.Outbox.load(ctx) }
    val outbox by at.dimes.dmoffice.sync.Outbox.chat.collectAsState()
    var acting by remember { mutableStateOf<Long?>(null) }
    var reply by remember { mutableStateOf<ChatMessage?>(null) }
    var editing by remember { mutableStateOf<ChatMessage?>(null) }
    var menuFor by remember { mutableStateOf<Long?>(null) }
    var roomMenu by remember { mutableStateOf(false) }
    var busy by remember { mutableStateOf(false) }
    // Pukki fragt gerade die KI („denkt nach“)
    var thinking by remember { mutableStateOf(false) }
    var note by remember { mutableStateOf<Pair<String, String>?>(null) }
    // Fotos im Bildbetrachter der Galerie: alle Fotos der Nachricht zum Durchblättern
    var bigImage by remember { mutableStateOf<Pair<List<FileItem>, Int>?>(null) }
    var saveFor by remember { mutableStateOf<Triple<Long, Int, ChatRef>?>(null) }
    // Stufe 3: Ordner, Termin/Aufgabe/Kontakt ansehen, Ziel-Sammlung fuer "Bei mir speichern"
    var folderView by remember { mutableStateOf<Triple<Long, Int, ChatRef>?>(null) }
    var albumView by remember { mutableStateOf<Triple<Long, Int, ChatRef>?>(null) }
    albumView?.let { (msg, idx, ref) -> AlbumViewDialog(msg, idx, ref.name) { albumView = null } }
    var itemView by remember { mutableStateOf<Triple<Long, Int, ChatRef>?>(null) }
    var collectionFor by remember { mutableStateOf<Triple<Long, Int, ChatRef>?>(null) }
    var folderImage by remember { mutableStateOf<Triple<Long, Int, at.dimes.dmoffice.chat.ChatFolderEntry>?>(null) }
    var forwardFor by remember { mutableStateOf<ChatMessage?>(null) }
    var pickFromFiles by remember { mutableStateOf(false) }
    var pickNote by remember { mutableStateOf(false) }
    var colorDialog by remember { mutableStateOf(false) }
    var otherRooms by remember { mutableStateOf<List<ChatRoom>>(emptyList()) }
    // Stufe 4: Gruppenraeume
    var spaces by remember { mutableStateOf<List<at.dimes.dmoffice.chat.ChatSpace>>(emptyList()) }
    var spaceDialog by remember { mutableStateOf<String?>(null) }   // add, remove
    LaunchedEffect(roomId, room?.seq, room?.isGroup) {
        if (room?.isGroup == true) spaces = try { app.chat.spaces(roomId) } catch (_: Exception) { spaces }
    }
    val listState = rememberLazyListState()

    fun toast(msg: String?) = android.widget.Toast.makeText(ctx, msg ?: "?", android.widget.Toast.LENGTH_SHORT).show()
    // Abgebrochen (Bildschirm verlassen, Animation abgeloest) ist kein Fehler
    fun fail(e: Exception) { if (e !is CancellationException) toast(e.message) }
    fun put(list: List<ChatMessage>) {
        if (list.isEmpty()) return
        val byId = LinkedHashMap<Long, ChatMessage>()
        for (m in messages) byId[m.id] = m
        for (m in list) { val old = byId[m.id]; if (old == null || old.seq <= m.seq) byId[m.id] = m }
        messages = byId.values.sortedBy { it.id }
    }

    // Standort-Anfrage (Stufe 7): fragen darf, wem es die Verwaltung erlaubt - nur zu zweit, nicht Pukki
    var locationRight by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { locationRight = try { app.chat.settings().rights.locationRequests } catch (_: Exception) { false } }
    // Beantworten: Position einmal bestimmen (vorher die Berechtigung) und als Antwort schicken
    var locationFor by remember { mutableStateOf<ChatMessage?>(null) }
    var locating by remember { mutableStateOf<Long?>(null) }
    fun answerLocation(m: ChatMessage) {
        scope.launch {
            locating = m.id
            try {
                val loc = currentLocation(ctx)
                if (loc == null) toast(ctx.getString(R.string.chat_location_none))
                else put(listOf(app.chat.sendLocation(roomId, loc.latitude, loc.longitude, if (loc.hasAccuracy()) loc.accuracy.toDouble() else null, m.id)))
            } catch (e: Exception) { fail(e) } finally { locating = null }
        }
    }
    val locationPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { granted ->
        val m = locationFor
        locationFor = null
        if (m != null && granted.values.any { it }) answerLocation(m) else if (m != null) toast(ctx.getString(R.string.chat_location_denied))
    }
    fun sendLocationFor(m: ChatMessage) {
        val fine = android.Manifest.permission.ACCESS_FINE_LOCATION
        val coarse = android.Manifest.permission.ACCESS_COARSE_LOCATION
        if (ctx.checkSelfPermission(fine) == android.content.pm.PackageManager.PERMISSION_GRANTED
            || ctx.checkSelfPermission(coarse) == android.content.pm.PackageManager.PERMISSION_GRANTED) answerLocation(m)
        else { locationFor = m; locationPermission.launch(arrayOf(fine, coarse)) }
    }

    // Eigenen Standort teilen: einmalig (minutes 0) oder live fuer 1-30 Minuten (Vordergrunddienst mit Benachrichtigung)
    var shareDialog by remember { mutableStateOf(false) }
    var shareMinutes by remember { mutableStateOf<Int?>(null) }
    fun shareLocation(minutes: Int) {
        scope.launch {
            busy = true
            try {
                val loc = currentLocation(ctx)
                if (loc == null) { toast(ctx.getString(R.string.chat_location_none)); return@launch }
                val m = app.chat.sendLocation(roomId, loc.latitude, loc.longitude, if (loc.hasAccuracy()) loc.accuracy.toDouble() else null, null, minutes.takeIf { it > 0 })
                put(listOf(m))
                if (minutes > 0) {
                    val until = m.refs.firstOrNull()?.liveUntil?.let { try { java.time.Instant.parse(it).toEpochMilli() } catch (_: Exception) { null } }
                        ?: (System.currentTimeMillis() + minutes * 60_000L)
                    LiveLocation.start(ctx, LiveLocation.Share(roomId, m.id, until, room?.title ?: ""))
                }
            } catch (e: Exception) { fail(e) } finally { busy = false }
        }
    }
    val sharePermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { granted ->
        val n = shareMinutes
        shareMinutes = null
        val loc = granted[android.Manifest.permission.ACCESS_FINE_LOCATION] == true || granted[android.Manifest.permission.ACCESS_COARSE_LOCATION] == true
            || ctx.checkSelfPermission(android.Manifest.permission.ACCESS_COARSE_LOCATION) == android.content.pm.PackageManager.PERMISSION_GRANTED
        if (n != null && loc) shareLocation(n) else if (n != null) toast(ctx.getString(R.string.chat_location_denied))
    }
    fun startShare(minutes: Int) {
        val granted = { p: String -> ctx.checkSelfPermission(p) == android.content.pm.PackageManager.PERMISSION_GRANTED }
        val fine = android.Manifest.permission.ACCESS_FINE_LOCATION
        val coarse = android.Manifest.permission.ACCESS_COARSE_LOCATION
        // Live: auch die Benachrichtigung (Android 13+), sonst sieht man das laufende Teilen nicht
        val notify = minutes > 0 && android.os.Build.VERSION.SDK_INT >= 33 && !granted(android.Manifest.permission.POST_NOTIFICATIONS)
        if ((granted(fine) || granted(coarse)) && !notify) { shareLocation(minutes); return }
        shareMinutes = minutes
        sharePermission.launch(buildList { add(fine); add(coarse); if (notify) add(android.Manifest.permission.POST_NOTIFICATIONS) }.toTypedArray())
    }

    // Offline: zuerst, was zuletzt da war; jeder neue Stand kommt (kurz verzoegert) in den Zwischenspeicher
    LaunchedEffect(roomId) {
        if (messages.isEmpty()) app.chat.cache.messages(roomId)?.let { if (messages.isEmpty()) messages = it }
        if (room == null) app.chat.cache.rooms()?.firstOrNull { it.id == roomId }?.let { if (room == null) room = it }
    }
    LaunchedEffect(messages) {
        if (messages.isEmpty()) return@LaunchedEffect
        delay(1000)
        app.chat.cache.saveMessages(roomId, messages)
    }

    // Nur solange die Unterhaltung wirklich zu sehen ist (App im Vordergrund): keine Benachrichtigung dafür.
    // Mit offener Unterhaltung zum Startbildschirm oder Handy gesperrt: wieder melden.
    LaunchedEffect(owner, roomId) {
        owner.lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
            ChatNotify.openRoom = roomId
            ChatNotify.cancel(ctx, roomId)
            try { kotlinx.coroutines.awaitCancellation() } finally { if (ChatNotify.openRoom == roomId) ChatNotify.openRoom = null }
        }
    }

    LaunchedEffect(owner, roomId) {
        owner.lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            me = app.chat.whoAmI()
            var seq = -1L
            // Unbekannte Version: das erste Warten kommt gleich mit dem aktuellen Stand von „denkt nach“ zurück
            var busyVersion = Long.MAX_VALUE
            while (isActive) {
                try {
                    // Noch unbekannt (Server ohne me, Netz weg): jede Runde neu fragen, sonst bliebe alles linksbuendig
                    if (me == null) me = app.chat.whoAmI()
                    if (seq < 0) {
                        busyVersion = Long.MAX_VALUE
                        seq = app.chat.status().seq
                        room = app.chat.room(roomId)
                        val page = app.chat.messages(roomId)
                        put(page); complete = page.size < 50
                        // Verbindung (wieder) da: Vorgemerktes gleich senden
                        if (at.dimes.dmoffice.sync.Outbox.chat.value.isNotEmpty()) scope.launch { at.dimes.dmoffice.sync.Outbox.flush(ctx) }
                    }
                    // Wacht auch auf, wenn Pukki anfängt oder aufhört nachzudenken
                    val w = app.chat.waitBusy(seq, busyVersion)
                    busyVersion = w.busy
                    thinking = roomId in w.busyRooms
                    val next = w.seq
                    if (next > seq) {
                        val c = app.chat.changes(seq)
                        seq = c.seq
                        c.rooms.firstOrNull { it.id == roomId }?.let { room = it } ?: run { onClose(); return@repeatOnLifecycle }
                        put(c.messages.filter { it.room == roomId })
                    }
                } catch (e: CancellationException) { throw e }
                catch (_: Exception) { seq = -1; thinking = false; delay(10_000) }
            }
        }
    }

    // Gelesen, sobald die neueste Nachricht da ist (Bildschirm offen)
    val lastId = messages.lastOrNull()?.id ?: 0
    LaunchedEffect(lastId, room?.lastRead) {
        val r = room ?: return@LaunchedEffect
        if (lastId > r.lastRead) {
            try { app.chat.markRead(roomId, lastId); room = r.copy(lastRead = lastId, unread = 0) } catch (_: Exception) { }
        }
    }
    // Aus der Suche: zur Nachricht springen - aeltere Seiten nachladen, bis sie da ist; kurz hervorheben
    var flash by remember { mutableStateOf<Long?>(null) }
    var focused by remember { mutableStateOf(false) }
    LaunchedEffect(focus, messages.size, complete) {
        val target = focus ?: return@LaunchedEffect
        if (focused || messages.isEmpty()) return@LaunchedEffect
        val i = messages.indexOfFirst { it.id == target }
        if (i >= 0) {
            listState.scrollToItem(outbox.count { it.room == roomId } + messages.size - 1 - i)
            focused = true; flash = target
        } else if (!complete) {
            try { val older = app.chat.messages(roomId, before = messages.first().id); put(older); complete = older.size < 50 }
            catch (e: CancellationException) { throw e } catch (_: Exception) { focused = true }
        } else focused = true
    }
    LaunchedEffect(thinking) { if (thinking && listState.firstVisibleItemIndex <= 2) listState.animateScrollToItem(0) }
    LaunchedEffect(flash) { if (flash != null) { delay(2500); flash = null } }
    // Neue Nachricht: nach unten (reverseLayout: Index 0 ist die neueste)
    LaunchedEffect(lastId) { if (focus != null && !focused) return@LaunchedEffect; if (listState.firstVisibleItemIndex <= 2) listState.animateScrollToItem(0) }
    // Oben angekommen: Aelteres nachladen
    val nearTop = listState.layoutInfo.visibleItemsInfo.lastOrNull()?.index?.let { it >= messages.size - 3 } ?: false
    LaunchedEffect(nearTop, messages.firstOrNull()?.id) {
        val first = messages.firstOrNull() ?: return@LaunchedEffect
        if (nearTop && !complete) try { val older = app.chat.messages(roomId, before = first.id); put(older); complete = older.size < 50 } catch (_: Exception) { }
    }

    // ---------------- Anhaengen: Foto/Video aufnehmen, Galerie, Datei ----------------
    // Alles geht ueber eine Datei im Zwischenspeicher (camera/) und wird gestreamt hochgeladen
    var attachMenu by remember { mutableStateOf(false) }
    // Gesammelte Anhänge: gehen erst mit „Senden“ raus, zusammen mit dem Text in einer Nachricht
    var pending by remember { mutableStateOf(listOf<at.dimes.dmoffice.chat.ChatPending>()) }
    var pickGallery by remember { mutableStateOf(false) }
    fun addLocal(f: java.io.File, name: String, mime: String?) { pending = pending + at.dimes.dmoffice.chat.ChatPending.Local(f, name, mime) }
    var captureUri by androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf<String?>(null) }
    var captureFile by androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf<String?>(null) }
    fun cacheFile(prefix: String, ext: String) =
        java.io.File(java.io.File(ctx.cacheDir, "camera").apply { mkdirs() }, "$prefix-${System.currentTimeMillis()}.$ext")
    fun sendFile(f: java.io.File, name: String, mime: String?, keep: Boolean = false) {
        scope.launch {
            busy = true
            try { put(listOf(app.chat.upload(roomId, name, mime, f))) }
            catch (e: Exception) {
                // Ohne Netz: in den Postausgang - geht raus, sobald der Server erreichbar ist
                if (at.dimes.dmoffice.sync.Outbox.offline(e)) at.dimes.dmoffice.sync.Outbox.addChat(ctx, roomId, file = at.dimes.dmoffice.sync.Outbox.keep(ctx, f), name = name, mime = mime)
                else fail(e)
            }
            finally { if (!keep) f.delete(); busy = false }
        }
    }
    // Vom Handy gewählt: gleich in den Zwischenspeicher kopieren (die Freigabe der Galerie gilt nicht ewig) und anhängen
    fun sendUris(uris: List<android.net.Uri>) {
        if (uris.isEmpty()) return
        scope.launch {
            busy = true
            for (uri in uris) try {
                val cr = ctx.contentResolver
                val mime = cr.getType(uri)
                val name = cr.query(uri, arrayOf(android.provider.OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c -> if (c.moveToFirst()) c.getString(0) else null }
                    ?: ("Datei-" + System.currentTimeMillis())
                val f = cacheFile("pick", "bin")
                kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { cr.openInputStream(uri)!!.use { i -> f.outputStream().use { o -> i.copyTo(o) } } }
                addLocal(f, name, mime)
            } catch (e: Exception) { fail(e) }
            busy = false
        }
    }
    val pickMedia = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(10)) { sendUris(it) }
    val pickFiles = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { sendUris(it) }
    val stamp = { java.time.LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH.mm.ss")) }
    val takePhoto = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { ok ->
        val f = captureFile?.let { java.io.File(it) }
        captureUri = null; captureFile = null
        if (f != null) { if (ok && f.length() > 0) addLocal(f, "Foto ${stamp()}.jpg", "image/jpeg") else f.delete() }
    }
    val takeVideo = rememberLauncherForActivityResult(ActivityResultContracts.CaptureVideo()) { ok ->
        val f = captureFile?.let { java.io.File(it) }
        captureUri = null; captureFile = null
        if (f != null) { if (ok && f.length() > 0) addLocal(f, "Video ${stamp()}.mp4", "video/mp4") else f.delete() }
    }
    var pendingVideo by remember { mutableStateOf(false) }
    fun capture(video: Boolean) {
        val f = cacheFile(if (video) "video" else "foto", if (video) "mp4" else "jpg")
        val uri = androidx.core.content.FileProvider.getUriForFile(ctx, ctx.packageName + ".files", f)
        captureFile = f.absolutePath; captureUri = uri.toString()
        try { if (video) takeVideo.launch(uri) else takePhoto.launch(uri) } catch (e: Exception) { fail(e) }
    }
    val cameraPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted -> if (granted) capture(pendingVideo) }
    fun startCapture(video: Boolean) {
        pendingVideo = video
        if (androidx.core.content.ContextCompat.checkSelfPermission(ctx, android.Manifest.permission.CAMERA) == android.content.pm.PackageManager.PERMISSION_GRANTED) capture(video)
        else cameraPermission.launch(android.Manifest.permission.CAMERA)
    }

    // Senden mit Anhängen: Handy-Dateien und Galerie-Ordner-Fotos vorab hochladen, dann eine Nachricht mit Text und allen Verweisen
    fun sendComposed(text: String) {
        val items = pending
        val replyTo = reply?.id
        scope.launch {
            busy = true
            try {
                val refs = mutableListOf<Pair<at.dimes.dmoffice.chat.ChatOutRef, String>>()
                for (p in items) when (p) {
                    is at.dimes.dmoffice.chat.ChatPending.Local -> { val s = app.chat.stage(roomId, p.name, p.mime, p.file); refs += at.dimes.dmoffice.chat.ChatOutRef("file", fileId = s.fileId) to s.mode }
                    is at.dimes.dmoffice.chat.ChatPending.Gallery -> {
                        val f = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { app.files.cacheFile(p.item) }
                        val s = app.chat.stage(roomId, p.item.name, p.item.mime, f)
                        refs += at.dimes.dmoffice.chat.ChatOutRef("file", fileId = s.fileId) to p.mode
                    }
                    is at.dimes.dmoffice.chat.ChatPending.Ref -> refs += p.ref to p.mode
                }
                put(listOf(app.chat.sendComposed(roomId, text.ifEmpty { null }, replyTo, refs)))
                items.filterIsInstance<at.dimes.dmoffice.chat.ChatPending.Local>().forEach { it.file.delete() }
                pending = emptyList(); field = androidx.compose.ui.text.input.TextFieldValue(""); reply = null
            } catch (e: Exception) {
                // Ohne Netz: Handy-Dateien und Text in den Postausgang (je einzeln, wie bisher); Verweise auf dmOffice brauchen den Server
                if (at.dimes.dmoffice.sync.Outbox.offline(e) && items.all { it is at.dimes.dmoffice.chat.ChatPending.Local }) {
                    for (p in items.filterIsInstance<at.dimes.dmoffice.chat.ChatPending.Local>())
                        at.dimes.dmoffice.sync.Outbox.addChat(ctx, roomId, file = at.dimes.dmoffice.sync.Outbox.keep(ctx, p.file), name = p.name, mime = p.mime)
                    if (text.isNotEmpty()) at.dimes.dmoffice.sync.Outbox.addChat(ctx, roomId, text, replyTo)
                    pending = emptyList(); field = androidx.compose.ui.text.input.TextFieldValue(""); reply = null
                } else if (at.dimes.dmoffice.sync.Outbox.offline(e)) toast(ctx.getString(R.string.chat_pending_offline))
                else fail(e)
            } finally { busy = false }
        }
    }

    // ---------------- Sprachnachricht (Chat-Komfort): Mikrofon antippen, aufnehmen, senden oder verwerfen ----------------
    var recorder by remember { mutableStateOf<VoiceRecorder?>(null) }
    var recordingSince by remember { mutableStateOf<Long?>(null) }
    var recordingNow by remember { mutableStateOf(0L) }
    fun finishRecording() {
        val f = recorder?.stop()
        recorder = null; recordingSince = null
        if (f != null) sendFile(f, ctx.getString(R.string.chat_voice_name) + " " + stamp() + ".m4a", "audio/mp4")
    }
    LaunchedEffect(recordingSince) {
        val since = recordingSince ?: return@LaunchedEffect
        while (recordingSince == since) {
            recordingNow = System.currentTimeMillis()
            if (recordingNow - since > MaxVoiceMs) { finishRecording(); break }
            delay(500)
        }
    }
    DisposableEffect(Unit) { onDispose { recorder?.cancel() } }
    fun record() {
        try {
            recorder = VoiceRecorder(ctx, cacheFile("voice", "m4a"))
            recordingSince = System.currentTimeMillis(); recordingNow = recordingSince!!
        } catch (e: Exception) { recorder = null; recordingSince = null; fail(e) }
    }
    // Unterhaltung exportieren: Speicherort waehlen, dann das ZIP vom Server dorthin
    val exportTo = rememberLauncherForActivityResult(ActivityResultContracts.CreateDocument("application/zip")) { uri ->
        if (uri != null) scope.launch {
            busy = true
            try {
                val f = app.chat.export(roomId)
                kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { ctx.contentResolver.openOutputStream(uri)!!.use { o -> f.inputStream().use { it.copyTo(o) } } }
                f.delete()
                toast(ctx.getString(R.string.chat_exported))
            } catch (e: Exception) { fail(e) } finally { busy = false }
        }
    }
    val micPermission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted -> if (granted) record() }
    fun startRecording() {
        if (androidx.core.content.ContextCompat.checkSelfPermission(ctx, android.Manifest.permission.RECORD_AUDIO) == android.content.pm.PackageManager.PERMISSION_GRANTED) record()
        else micPermission.launch(android.Manifest.permission.RECORD_AUDIO)
    }

    val r = room
    val others = r?.members?.filter { it.id != me } ?: emptyList()
    val self = r?.members?.firstOrNull { it.id == me }
    fun nameOf(id: String) = if (id == me) ctx.getString(R.string.chat_you) else r?.members?.firstOrNull { it.id == id }?.name ?: "?"
    /** Link im Text: Mail-Adresse = neue Mail in dmOffice, sonst die passende App (Telefon, Browser). */
    fun openLink(target: String) {
        if (target.startsWith("mailto:")) { onMail(target.removePrefix("mailto:")); return }
        val action = if (target.startsWith("tel:")) android.content.Intent.ACTION_DIAL else android.content.Intent.ACTION_VIEW
        try { ctx.startActivity(android.content.Intent(action, android.net.Uri.parse(target))) } catch (_: Exception) { }
    }
    /** Eigene Nachricht ändern/löschen: in Gruppen immer, zu zweit nur ungelesen (ohne Lesebestätigung: nicht zugestellt). */
    fun changeable(m: ChatMessage) = r?.isGroup == true || others.filter { it.id != PukkiId }.all { o -> if (o.receipts) o.lastRead < m.id else o.delivered < m.id }
    // @Erwaehnungen: Hervorheben und Vorschlaege, solange vor dem Cursor „@Anfang“ steht
    val everyone = stringResource(R.string.chat_mention_all)
    val mention = remember(r, me) { r?.let { mentionNames(it, me) } }
    val suggestQuery = Regex("""(^|\s)@([\p{L}\p{N}._-]*)$""").find(field.text.substring(0, field.selection.end.coerceIn(0, field.text.length)))
    val suggestions = if (suggestQuery == null || r == null) emptyList<ChatMember>() else {
        val q = suggestQuery.groupValues[2]
        (mentionPeople(r).filter { it.id != me } + if (r.isGroup) listOf(ChatMember("*", everyone)) else emptyList())
            .filter { p -> (listOf(p.name, p.login ?: "") + p.name.split(' ')).any { it.startsWith(q, ignoreCase = true) } }.take(5)
    }
    fun pickMention(p: ChatMember) {
        val hit = suggestQuery ?: return
        val start = hit.range.first + hit.groupValues[1].length
        val end = hit.range.last + 1
        val value = field.text.substring(0, start) + "@" + p.name + " " + field.text.substring(end)
        field = androidx.compose.ui.text.input.TextFieldValue(value, androidx.compose.ui.text.TextRange(start + p.name.length + 2))
    }

    Scaffold(
        topBar = {
            TopAppBar(
                navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.chat_back)) } },
                title = {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Initials(r?.title ?: "", r?.isGroup == true, 36, pukki = r?.isPukki == true, look = if (r?.isGroup == true) null else others.firstOrNull())
                        Column(Modifier.padding(start = 10.dp)) {
                            Text(r?.title ?: "", maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium)
                            val sub = if (r?.isGroup == true) r.members.joinToString(", ") { nameOf(it.id) } else others.firstOrNull()?.login ?: ""
                            if (sub.isNotEmpty()) Text(sub, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                },
                actions = {
                    IconButton(onClick = { roomMenu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
                    DropdownMenu(expanded = roomMenu, onDismissRequest = { roomMenu = false }) {
                        if (r?.isGroup == true && r.admin && spaces.size < 4) DropdownMenuItem(text = { Text(stringResource(R.string.chat_space_add)) }, onClick = { roomMenu = false; spaceDialog = "add" })
                        if (spaces.any { it.mine }) DropdownMenuItem(text = { Text(stringResource(R.string.chat_space_remove)) }, onClick = { roomMenu = false; spaceDialog = "remove" })
                        if (r != null && locationRight && !r.isGroup && !r.isPukki) DropdownMenuItem(text = { Text(stringResource(R.string.chat_location_ask)) }, onClick = {
                            roomMenu = false
                            if (!r.askLocation) toast(ctx.getString(R.string.chat_location_not_allowed, r.title))
                            else scope.launch { try { put(listOf(app.chat.requestLocation(roomId))) } catch (e: Exception) { fail(e) } }
                        })
                        if (r != null) DropdownMenuItem(text = { Text(stringResource(R.string.chat_color)) }, onClick = { roomMenu = false; colorDialog = true })
                        if (r != null) DropdownMenuItem(text = { Text(stringResource(if (r.muted) R.string.chat_unmute else R.string.chat_mute)) }, onClick = {
                            roomMenu = false
                            scope.launch { try { app.chat.mute(roomId, !r.muted); room = r.copy(muted = !r.muted) } catch (e: Exception) { fail(e) } }
                        })
                        if (r != null) DropdownMenuItem(text = { Text(stringResource(R.string.chat_export)) }, onClick = {
                            roomMenu = false
                            exportTo.launch("Chat " + r.title.replace(Regex("""[\\/:*?"<>|]"""), "_") + " " + java.time.LocalDate.now() + ".zip")
                        })
                        if (r != null) DropdownMenuItem(text = { Text(stringResource(if (r.isGroup) R.string.chat_leave_group else R.string.chat_hide)) }, onClick = {
                            roomMenu = false
                            scope.launch { try { app.chat.leave(roomId); onClose() } catch (e: Exception) { fail(e) } }
                        })
                    }
                },
            )
        },
    ) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().imePadding()) {
            // Gruppenraeume: zum Hinspringen
            if (spaces.isNotEmpty()) Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 8.dp, vertical = 2.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                for (s in spaces) androidx.compose.material3.AssistChip(onClick = { onOpenSpace(s.kind, s.target) },
                    label = { Text(spaceLabel(s.kind)) }, leadingIcon = { Icon(spaceIcon(s.kind), contentDescription = null, Modifier.size(18.dp)) })
            }
            val byId = messages.associateBy { it.id }
            val shown = messages.asReversed()
            // Pukki: Daumen unter seinen Antworten statt Emoji-Reaktionen (wie im Web)
            val pukkiRoom = r?.isPukki == true
            var ratings by remember(roomId) { mutableStateOf<Map<Long, Int>>(emptyMap()) }
            LaunchedEffect(roomId, pukkiRoom) { if (pukkiRoom) ratings = try { app.chat.pukkiRatings() } catch (_: Exception) { emptyMap() } }
            val waiting = outbox.filter { it.room == roomId }.asReversed()
            LazyColumn(Modifier.weight(1f).fillMaxWidth().background(MaterialTheme.colorScheme.surface)
                .background((chatColorOf(r?.color) ?: MaterialTheme.colorScheme.primary).copy(alpha = 0.07f)), state = listState, reverseLayout = true,
                contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 10.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                // Pukki fragt gerade die KI: ganz unten
                if (thinking) item(key = "thinking") { ThinkingBubble() }
                else if (r?.isPukki == true && shown.isNotEmpty()) item(key = "nap") { PukkiNap(shown.first().createdMs) }
                // Postausgang (ohne Netz geschrieben): ganz unten, mit Uhr; lange drücken = nicht senden
                items(waiting.size, key = { "out-" + waiting[it].id }) { i -> PendingBubble(waiting[i]) { scope.launch { at.dimes.dmoffice.sync.Outbox.cancelChat(ctx, waiting[i].id) } } }
                items(shown.size, key = { shown[it].id }) { i ->
                    val m = shown[i]
                    val older = shown.getOrNull(i + 1)
                    val newDay = older == null || dayOf(older.createdMs) != dayOf(m.createdMs)
                    // Am selben Tag nach mehr als 4 Stunden Pause (morgens nach einer spaeten Nachricht): die Uhrzeit als Trenner
                    val pause = !newDay && older != null && m.createdMs - older.createdMs >= 4 * 3600_000L
                    Column(Modifier.fillMaxWidth()) {
                        if (newDay || pause) Text(if (newDay) dayOf(m.createdMs).format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM))
                            else Instant.ofEpochMilli(m.createdMs).atZone(ZoneId.systemDefault()).format(DateTimeFormatter.ofPattern("HH:mm")),
                            Modifier.align(Alignment.CenterHorizontally).padding(vertical = 8.dp).clip(RoundedCornerShape(10.dp))
                                .background(MaterialTheme.colorScheme.surface).padding(horizontal = 10.dp, vertical = 2.dp),
                            style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        if (m.isSystem) {
                            Text(systemText(m, ::nameOf), Modifier.align(Alignment.CenterHorizontally).padding(vertical = 4.dp),
                                style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        } else {
                            val own = m.sender == me
                            val firstOfRun = newDay || pause || older?.sender != m.sender || older.isSystem
                            // Pukki: Pose zur Stimmung an der letzten Nachricht einer Folge, davor Platz (Blasen bleiben bündig)
                            val fromPukki = m.sender == PukkiId && !own && !m.deleted
                            val newer = shown.getOrNull(i - 1)
                            Box(Modifier.fillMaxWidth(), contentAlignment = if (own) Alignment.CenterEnd else Alignment.CenterStart) {
                                if (fromPukki && (newer == null || newer.sender != m.sender || newer.isSystem))
                                    PukkiPose(m.mood ?: "zeigen", 48, Modifier.align(Alignment.BottomStart))
                                // Eigenes Profilbild an der letzten eigenen Nachricht einer Folge, davor Platz
                                if (own && self != null && (newer == null || newer.sender != m.sender || newer.isSystem))
                                    Box(Modifier.align(Alignment.BottomEnd)) { PersonAvatar(self.id, self.name, self.login, self.avatar, self.accent, 32) }
                                Surface(
                                    // Ecke zur eigenen Seite spitzer - wie eine Sprechblase
                                    shape = RoundedCornerShape(topStart = 16.dp, topEnd = 16.dp, bottomStart = if (own) 16.dp else 4.dp, bottomEnd = if (own) 4.dp else 16.dp),
                                    color = if (own) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerHighest,
                                    tonalElevation = 1.dp,
                                    border = if (flash == m.id) androidx.compose.foundation.BorderStroke(2.dp, MaterialTheme.colorScheme.primary) else null,
                                    modifier = Modifier.padding(start = if (fromPukki) 54.dp else 0.dp, end = if (own) 38.dp else 0.dp).widthIn(max = 300.dp).padding(top = if (firstOfRun) 6.dp else 0.dp)
                                        .combinedClickable(onClick = {}, onLongClick = { if (!m.deleted) menuFor = m.id }),
                                ) {
                                    Column(Modifier.padding(horizontal = 10.dp, vertical = 6.dp)) {
                                        if (firstOfRun && !own && r?.isGroup == true) Text(m.senderName, style = MaterialTheme.typography.labelMedium,
                                            color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.SemiBold)
                                        m.replyTo?.let { q ->
                                            val quoted = byId[q]
                                            Text((quoted?.let { nameOf(it.sender) + ": " + snippet(it, stringResource(R.string.chat_deleted)) }) ?: stringResource(R.string.chat_earlier),
                                                Modifier.padding(bottom = 4.dp).clip(RoundedCornerShape(6.dp)).background(MaterialTheme.colorScheme.surfaceVariant).padding(6.dp),
                                                maxLines = 2, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                                        }
                                        if (m.deleted) Text(stringResource(R.string.chat_deleted), fontStyle = FontStyle.Italic, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                        else {
                                            m.refs.forEachIndexed { idx, ref ->
                                                val mine = own && ref.owner == me
                                                RefItem(m, ref, idx, mine,
                                                    onImage = {
                                                        val imgs = m.refs.withIndex().filter { it.value.isImage && it.value.usable }
                                                        bigImage = imgs.map { at.dimes.dmoffice.files.FilesRepo.chatItem(m.id, it.index, it.value.name, it.value.mime, it.value.size) } to
                                                            imgs.indexOfFirst { it.index == idx }.coerceAtLeast(0)
                                                    },
                                                    onOpen = {
                                                        scope.launch {
                                                            try {
                                                                if (ref.type == "note") { val n = app.chat.note(m.id, idx); note = n.title to androidx.core.text.HtmlCompat.fromHtml(n.html, androidx.core.text.HtmlCompat.FROM_HTML_MODE_COMPACT).toString().trim() }
                                                                // Stufe 3: Ordner blaettern, Termin/Aufgabe/Kontakt ansehen
                                                                else if (ref.type == "folder") folderView = Triple(m.id, idx, ref)
                                                                else if (ref.type == "album") albumView = Triple(m.id, idx, ref)
                                                                else if (ref.type == "event" || ref.type == "task" || ref.type == "contact") itemView = Triple(m.id, idx, ref)
                                                                // Gezeigt: keine Kopie an andere Apps - nur Bilder und Notizen lassen sich hier ansehen
                                                                else if (ref.shown && !mine) toast(ctx.getString(R.string.chat_shown_web_only))
                                                                else { val f = app.chat.download(m.id, idx, ref.name); openLocalFile(ctx, f, FileItem(path = ref.name, name = ref.name, isDir = false, size = ref.size ?: 0, mime = ref.mime), share = false) }
                                                            } catch (e: Exception) { toast(ctx.getString(R.string.chat_gone)) }
                                                        }
                                                    },
                                                    onSave = { if (ref.type == "event" || ref.type == "task" || ref.type == "contact") collectionFor = Triple(m.id, idx, ref) else saveFor = Triple(m.id, idx, ref) },
                                                    onMode = { mode -> scope.launch { try { put(listOf(app.chat.changeRef(m.id, idx, mode = mode))) } catch (e: Exception) { fail(e) } } },
                                                    onRevoke = { revoke -> scope.launch { try { put(listOf(app.chat.changeRef(m.id, idx, revoke = revoke))) } catch (e: Exception) { fail(e) } } })
                                            }
                                            m.text?.let { Text(highlightMentions(it, mention) { target -> openLink(target) }) }
                                            if (m.actions.isNotEmpty() && !own) Row(Modifier.padding(top = 6.dp).horizontalScroll(rememberScrollState()),
                                                horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                                                for (a in m.actions) {
                                                    val click: () -> Unit = if (a.key == "loc-yes") ({ sendLocationFor(m) }) else ({
                                                        scope.launch { acting = m.id; try { app.chat.act(m.id, a.key) } catch (e: Exception) { fail(e) } finally { acting = null } }
                                                    })
                                                    val pad = androidx.compose.foundation.layout.PaddingValues(horizontal = 12.dp, vertical = 2.dp)
                                                    if (a.key == "undo" || a.key == "loc-no") androidx.compose.material3.OutlinedButton(onClick = click, enabled = acting != m.id && locating != m.id, contentPadding = pad) { Text(a.label) }
                                                    else androidx.compose.material3.Button(onClick = click, enabled = acting != m.id && locating != m.id, contentPadding = pad) { Text(a.label) }
                                                }
                                            }
                                        }
                                        // Antwort von Pukki auf eine eigene Nachricht: richtig oder falsch verstanden?
                                        if (pukkiRoom && m.sender == PukkiId && older?.sender == me && m.kind == "text" && !m.deleted && !m.text.isNullOrBlank()) Row(Modifier.padding(top = 2.dp)) {
                                            for ((v, filled, outline) in listOf(Triple(1, Icons.Filled.ThumbUp, Icons.Outlined.ThumbUpOutline), Triple(-1, Icons.Filled.ThumbDown, Icons.Outlined.ThumbDownOutline))) {
                                                val on = ratings[m.id] == v
                                                IconButton(onClick = {
                                                    val value = if (on) 0 else v
                                                    ratings = ratings + (m.id to value)
                                                    scope.launch { try { app.chat.ratePukki(m.id, value) } catch (e: Exception) { fail(e) } }
                                                }, modifier = Modifier.size(32.dp)) {
                                                    Icon(if (on) filled else outline, contentDescription = stringResource(if (v == 1) R.string.pukki_rate_good else R.string.pukki_rate_bad),
                                                        Modifier.size(18.dp), tint = if (on) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                                                }
                                            }
                                        }
                                        if (m.reactions.isNotEmpty()) Row(Modifier.padding(top = 4.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                                            for (x in m.reactions) Text(x.emoji + if (x.users.size > 1) " ${x.users.size}" else "",
                                                Modifier.clip(RoundedCornerShape(10.dp))
                                                    .background(if (me in x.users) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surfaceVariant)
                                                    .clickable { scope.launch { try { put(listOf(app.chat.react(m.id, x.emoji))) } catch (e: Exception) { fail(e) } } }
                                                    .padding(horizontal = 6.dp, vertical = 1.dp))
                                        }
                                        Row(Modifier.align(Alignment.End), verticalAlignment = Alignment.CenterVertically) {
                                            if (m.edited != null && !m.deleted) Text(stringResource(R.string.chat_edited) + " ", style = MaterialTheme.typography.labelSmall,
                                                color = MaterialTheme.colorScheme.onSurfaceVariant)
                                            Text(localTime(m.createdMs), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                                            if (own && !m.deleted) {
                                                // ✓ gesendet, ✓✓ zugestellt, ✓✓ farbig gelesen (Pukki zählt nicht)
                                                val readers = others.filter { it.id != PukkiId }
                                                val read = readers.isNotEmpty() && readers.all { it.lastRead >= m.id }
                                                val delivered = readers.isNotEmpty() && readers.all { it.delivered >= m.id }
                                                Icon(if (read || delivered) Icons.Default.DoneAll else Icons.Default.Done,
                                                    contentDescription = stringResource(when { read -> R.string.chat_read; delivered -> R.string.chat_delivered; else -> R.string.chat_sent }),
                                                    Modifier.padding(start = 3.dp).size(14.dp), tint = if (read) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                                            }
                                        }
                                    }
                                }
                                DropdownMenu(expanded = menuFor == m.id, onDismissRequest = { menuFor = null }) {
                                    if (!pukkiRoom) Row(Modifier.padding(horizontal = 8.dp)) {
                                        for (e in QUICK) Text(e, Modifier.clickable {
                                            menuFor = null
                                            scope.launch { try { put(listOf(app.chat.react(m.id, e))) } catch (x: Exception) { fail(x) } }
                                        }.padding(6.dp), fontSize = 22.sp)
                                    }
                                    HorizontalDivider()
                                    DropdownMenuItem(text = { Text(stringResource(R.string.chat_reply)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.Reply, null) },
                                        onClick = { menuFor = null; reply = m; editing = null })
                                    DropdownMenuItem(text = { Text(stringResource(R.string.chat_forward)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.Forward, null) }, onClick = {
                                        menuFor = null
                                        scope.launch { otherRooms = try { app.chat.rooms().filter { it.id != roomId } } catch (_: Exception) { emptyList() }; forwardFor = m }
                                    })
                                    if (m.text != null) DropdownMenuItem(text = { Text(stringResource(R.string.chat_copy)) }, onClick = { menuFor = null; copyText(ctx, m.text) })
                                    if (own && m.text != null && changeable(m)) DropdownMenuItem(text = { Text(stringResource(R.string.chat_edit)) },
                                        onClick = { menuFor = null; editing = m; reply = null; field = androidx.compose.ui.text.input.TextFieldValue(m.text, androidx.compose.ui.text.TextRange(m.text.length)) })
                                    if ((own && changeable(m)) || (r?.isGroup == true && r.admin)) DropdownMenuItem(text = { Text(stringResource(R.string.chat_delete_all)) }, onClick = {
                                        menuFor = null
                                        scope.launch { try { app.chat.delete(m.id); put(listOf(m.copy(deleted = true, text = null, refs = emptyList(), seq = m.seq + 1))) } catch (e: Exception) { fail(e) } }
                                    })
                                }
                            }
                        }
                    }
                }
            }
            if (reply != null || editing != null) {
                Row(Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surfaceVariant).padding(horizontal = 12.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(if (reply != null) Icons.AutoMirrored.Filled.Reply else Icons.Default.Done, null, Modifier.size(18.dp))
                    Text(reply?.let { nameOf(it.sender) + ": " + snippet(it, "") } ?: stringResource(R.string.chat_editing),
                        Modifier.weight(1f).padding(start = 8.dp), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
                    IconButton(onClick = { reply = null; if (editing != null) { editing = null; field = androidx.compose.ui.text.input.TextFieldValue("") } }) { Icon(Icons.Default.Close, stringResource(android.R.string.cancel)) }
                }
            }
            if (suggestions.isNotEmpty()) Column(Modifier.fillMaxWidth().background(MaterialTheme.colorScheme.surfaceContainer).padding(vertical = 2.dp)) {
                for (p in suggestions) Row(Modifier.fillMaxWidth().clickable { pickMention(p) }.padding(horizontal = 12.dp, vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    Initials(p.name, p.id == "*", 24, pukki = p.id == PukkiId, look = p.takeIf { it.id != "*" })
                    Text(p.name, Modifier.padding(start = 10.dp), fontWeight = FontWeight.SemiBold)
                    if (p.login != null && p.id != PukkiId) Text(p.login, Modifier.padding(start = 8.dp), style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            // Gesammelte Anhänge: entfernen mit ×, weitere über die Büroklammer
            if (pending.isNotEmpty()) androidx.compose.foundation.lazy.LazyRow(Modifier.fillMaxWidth().padding(start = 8.dp, end = 8.dp, top = 6.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                items(pending.size) { i -> PendingChip(pending[i]) { val p = pending[i]; pending = pending - p; if (p is at.dimes.dmoffice.chat.ChatPending.Local) p.file.delete() } }
            }
            Row(Modifier.fillMaxWidth().padding(6.dp), verticalAlignment = Alignment.Bottom) {
                Box {
                    IconButton(enabled = !busy && editing == null, onClick = { attachMenu = true }) {
                        if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp) else Icon(Icons.Default.AttachFile, stringResource(R.string.chat_attach))
                    }
                    DropdownMenu(expanded = attachMenu, onDismissRequest = { attachMenu = false }) {
                        // Vom Handy: wird hochgeladen und liegt danach in "Chat" in der eigenen Ablage
                        AttachHeader(Icons.Default.PhoneAndroid, stringResource(R.string.chat_attach_device), stringResource(R.string.chat_attach_device_hint))
                        DropdownMenuItem(text = { Text(stringResource(R.string.chat_take_photo)) }, leadingIcon = { Icon(Icons.Default.PhotoCamera, null) },
                            onClick = { attachMenu = false; startCapture(video = false) })
                        DropdownMenuItem(text = { Text(stringResource(R.string.chat_take_video)) }, leadingIcon = { Icon(Icons.Default.Videocam, null) },
                            onClick = { attachMenu = false; startCapture(video = true) })
                        DropdownMenuItem(text = { Text(stringResource(R.string.chat_gallery)) }, leadingIcon = { Icon(Icons.Default.PhotoLibrary, null) },
                            onClick = { attachMenu = false; pickMedia.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo)) })
                        DropdownMenuItem(text = { Text(stringResource(R.string.chat_file_device)) }, leadingIcon = { Icon(Icons.Default.Description, null) },
                            onClick = { attachMenu = false; pickFiles.launch(arrayOf("*/*")) })
                        if (room?.isPukki != true) DropdownMenuItem(text = { Text(stringResource(R.string.chat_location_share)) }, leadingIcon = { Icon(Icons.Default.Place, null) },
                            onClick = { attachMenu = false; shareDialog = true })
                        HorizontalDivider()
                        // Aus dmOffice: liegt schon auf dem Server - die Nachricht zeigt nur darauf
                        AttachHeader(Icons.Default.Cloud, stringResource(R.string.chat_attach_server), stringResource(R.string.chat_attach_server_hint))
                        DropdownMenuItem(text = { Text(stringResource(R.string.chat_gallery_dm)) }, leadingIcon = { Icon(Icons.Default.PhotoLibrary, null) },
                            onClick = { attachMenu = false; pickGallery = true })
                        DropdownMenuItem(text = { Text(stringResource(R.string.chat_from_files)) }, leadingIcon = { Icon(Icons.Default.Folder, null) },
                            onClick = { attachMenu = false; pickFromFiles = true })
                        DropdownMenuItem(text = { Text(stringResource(R.string.chat_note)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.StickyNote2, null) },
                            onClick = { attachMenu = false; pickNote = true })
                    }
                }
                val since = recordingSince
                if (since != null) {
                    // Laufende Aufnahme: Zeit, Verwerfen, Senden
                    Row(Modifier.weight(1f).height(56.dp).padding(start = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                        RecordingDot()
                        Text(stringResource(R.string.chat_voice_recording) + " " + voiceTime(recordingNow - since), Modifier.weight(1f).padding(start = 8.dp))
                        IconButton(onClick = { recorder?.cancel(); recorder = null; recordingSince = null }) {
                            Icon(Icons.Default.Delete, stringResource(R.string.chat_voice_discard))
                        }
                    }
                    IconButton(onClick = { finishRecording() }) { Icon(Icons.AutoMirrored.Filled.Send, stringResource(R.string.chat_send), tint = MaterialTheme.colorScheme.primary) }
                } else {
                OutlinedTextField(field, { field = it }, Modifier.weight(1f), placeholder = { Text(stringResource(R.string.chat_write_hint)) }, maxLines = 5,
                    shape = RoundedCornerShape(20.dp))
                // Leeres Feld: Mikrofon fuer eine Sprachnachricht - nicht bei Pukki (versteht keine Sprache; diktieren geht ueber die Tastatur)
                if (field.text.isBlank() && pending.isEmpty() && editing == null && r?.isPukki != true) IconButton(enabled = !busy, onClick = { startRecording() }) {
                    Icon(Icons.Default.Mic, stringResource(R.string.chat_voice_record), tint = MaterialTheme.colorScheme.primary)
                } else IconButton(enabled = !busy && (field.text.isNotBlank() || pending.isNotEmpty()), onClick = {
                    val value = field.text.trim()
                    if (pending.isNotEmpty() && editing == null) { sendComposed(value); return@IconButton }
                    scope.launch {
                        try {
                            val sent = editing?.let { app.chat.edit(it.id, value) } ?: app.chat.send(roomId, value, reply?.id)
                            // Nach unten scrollt LaunchedEffect(lastId) - ein zweites animateScrollToItem hier braeche das erste ab ("Mutation interrupted")
                            put(listOf(sent)); field = androidx.compose.ui.text.input.TextFieldValue(""); reply = null; editing = null
                        } catch (e: Exception) {
                            // Ohne Netz: neue Nachricht in den Postausgang (Bearbeiten braucht den Server)
                            if (editing == null && at.dimes.dmoffice.sync.Outbox.offline(e)) {
                                at.dimes.dmoffice.sync.Outbox.addChat(ctx, roomId, value, reply?.id)
                                field = androidx.compose.ui.text.input.TextFieldValue(""); reply = null
                            } else fail(e)
                        }
                    }
                }) { Icon(Icons.AutoMirrored.Filled.Send, stringResource(R.string.chat_send), tint = MaterialTheme.colorScheme.primary) }
                }
            }
        }
    }

    if (shareDialog) LocationShareDialog(onDismiss = { shareDialog = false }, onShare = { minutes -> shareDialog = false; startShare(minutes) })
    note?.let { (title, body) ->
        AlertDialog(onDismissRequest = { note = null }, title = { Text(title) },
            text = { LazyColumn(Modifier.heightIn(max = 420.dp)) { item { Text(body) } } },
            confirmButton = { TextButton(onClick = { note = null }) { Text(stringResource(R.string.close)) } })
    }
    bigImage?.let { (items, start) ->
        androidx.compose.ui.window.Dialog(onDismissRequest = { bigImage = null },
            properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
            FileViewer(items, start, onClose = { bigImage = null }, onShare = null, onOpenExternal = null)
        }
    }
    folderView?.let { (msg, idx, ref) ->
        val mine = ref.owner == me
        FolderViewDialog(msg, idx, ref.name, canSave = !mine && !ref.shown, canOpen = mine || !ref.shown, onDismiss = { folderView = null },
            onOpenFile = { v ->
                val e = v.entry
                if ((e.mime ?: "").startsWith("image/")) folderImage = Triple(msg, idx, e)
                else if (v.canOpen) scope.launch {
                    try { val f = app.chat.folderDownload(msg, idx, e); openLocalFile(ctx, f, FileItem(path = e.name, name = e.name, isDir = false, size = e.size, mime = e.mime), share = false) }
                    catch (x: Exception) { fail(x) }
                }
                else toast(ctx.getString(R.string.chat_shown_web_only))
            },
            onMessage = { toast(it) })
    }
    folderImage?.let { (msg, idx, e) ->
        var bmp by remember(msg, idx, e.path) { mutableStateOf<android.graphics.Bitmap?>(null) }
        LaunchedEffect(msg, idx, e.path) { bmp = app.chat.folderThumb(msg, idx, e, 1600) }
        AlertDialog(onDismissRequest = { folderImage = null }, title = { Text(e.name, maxLines = 1, overflow = TextOverflow.Ellipsis) }, text = {
            bmp?.let { Image(it.asImageBitmap(), null, Modifier.fillMaxWidth(), contentScale = ContentScale.Fit) } ?: CircularProgressIndicator()
        }, confirmButton = { TextButton(onClick = { folderImage = null }) { Text(stringResource(R.string.close)) } })
    }
    itemView?.let { (msg, idx, ref) ->
        ItemViewDialog(msg, idx, canSave = ref.owner != me && !ref.shown, onDismiss = { itemView = null },
            onSave = { itemView = null; collectionFor = Triple(msg, idx, ref) })
    }
    collectionFor?.let { (msg, idx, ref) ->
        CollectionPickDialog(ref.type, ref.name, onDismiss = { collectionFor = null }) { id ->
            collectionFor = null
            scope.launch { try { app.chat.saveTo(msg, idx, id); toast(ctx.getString(R.string.chat_saved_item)) } catch (e: Exception) { fail(e) } }
        }
    }
    saveFor?.let { (msg, idx, ref) ->
        SaveTargetDialog(isNote = ref.type == "note", name = ref.name, onDismiss = { saveFor = null }) { folder ->
            saveFor = null
            scope.launch {
                try { val s = app.chat.saveTo(msg, idx, folder); toast(ctx.getString(if (s.type == "note") R.string.chat_saved_note else R.string.chat_saved_file)) }
                catch (e: Exception) { fail(e) }
            }
        }
    }
    forwardFor?.let { m ->
        ForwardDialog(m, otherRooms, onDismiss = { forwardFor = null }, onDone = { forwardFor = null; toast(ctx.getString(R.string.chat_forwarded_done)) },
            onError = { toast(it) })
    }
    if (pickFromFiles) FilePickDialog(onDismiss = { pickFromFiles = false }) { files, mode ->
        pickFromFiles = false
        pending = pending + files.map { f -> at.dimes.dmoffice.chat.ChatPending.Ref(at.dimes.dmoffice.chat.ChatOutRef("file", fileId = f.fileId), mode, f.name,
            FileItem(f.path, f.name, false, f.size, f.mime, fileId = f.fileId)) }
    }
    if (pickNote) NotePickDialog(onDismiss = { pickNote = false }) { n, mode ->
        pickNote = false
        pending = pending + at.dimes.dmoffice.chat.ChatPending.Ref(at.dimes.dmoffice.chat.ChatOutRef("note", noteId = n.id), mode, n.title)
    }
    // Galerie von dmOffice: eigene Fotos als Verweis, Fotos aus Galerie-Ordnern werden beim Senden kopiert
    if (pickGallery) GalleryPickDialog(onDismiss = { pickGallery = false }) { photos, mode ->
        pickGallery = false
        pending = pending + photos.map { p ->
            if (p.fileId > 0 && !at.dimes.dmoffice.files.FilesRepo.isExt(p.path)) at.dimes.dmoffice.chat.ChatPending.Ref(at.dimes.dmoffice.chat.ChatOutRef("file", fileId = p.fileId), mode, p.name, p)
            else at.dimes.dmoffice.chat.ChatPending.Gallery(p, mode)
        }
    }
    when (spaceDialog) {
        "add" -> AlertDialog(onDismissRequest = { spaceDialog = null }, title = { Text(stringResource(R.string.chat_space_add)) },
            text = {
                Column {
                    Text(stringResource(R.string.chat_space_add_hint), style = MaterialTheme.typography.bodySmall)
                    for (k in listOf("calendar", "list", "folder", "notes").filter { k -> spaces.none { it.kind == k } })
                        ListItem(headlineContent = { Text(spaceLabel(k)) }, leadingContent = { Icon(spaceIcon(k), contentDescription = null) },
                            modifier = Modifier.clickable { spaceDialog = null; scope.launch { try { spaces = app.chat.createSpace(roomId, k) } catch (e: Exception) { fail(e) } } })
                }
            },
            confirmButton = { TextButton(onClick = { spaceDialog = null }) { Text(stringResource(android.R.string.cancel)) } })
        "remove" -> AlertDialog(onDismissRequest = { spaceDialog = null }, title = { Text(stringResource(R.string.chat_space_remove)) },
            text = {
                Column {
                    Text(stringResource(R.string.chat_space_remove_hint), style = MaterialTheme.typography.bodySmall)
                    for (s in spaces.filter { it.mine })
                        ListItem(headlineContent = { Text(spaceLabel(s.kind)) }, supportingContent = { Text(s.name) }, leadingContent = { Icon(spaceIcon(s.kind), contentDescription = null) },
                            modifier = Modifier.clickable { spaceDialog = null; scope.launch { try { app.chat.removeSpace(roomId, s.kind); spaces = spaces - s } catch (e: Exception) { fail(e) } } })
                }
            },
            confirmButton = { TextButton(onClick = { spaceDialog = null }) { Text(stringResource(android.R.string.cancel)) } })
    }
    if (colorDialog) ColorDialog(room?.color, onDismiss = { colorDialog = false }) { c ->
        colorDialog = false
        scope.launch { try { room = app.chat.setColor(roomId, c) } catch (e: Exception) { fail(e) } }
    }

    // Keine Bildschirmfoto-Sperre mehr (Entscheidung): abfotografieren laesst sich nie verhindern (Web, zweites Handy) - Zeigen heisst nur, nicht speichern, herunterladen oder weiterleiten
}

/** Kennzeichen der Art: Auge = gezeigt (nur ansehen), Pfeil = gesendet (darf gespeichert werden), mit Ablauf und Zustand. */
@Composable
private fun ModeLine(ref: ChatRef, own: Boolean, onMode: (String) -> Unit, onRevoke: (Boolean) -> Unit) {
    var menu by remember { mutableStateOf(false) }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(if (ref.shown) Icons.Default.Visibility else Icons.AutoMirrored.Filled.Send, null, Modifier.size(12.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        // Kein stringResource im try - Compose erlaubt keine Composable-Aufrufe in try/catch
        val untilDate = ref.expires?.let { e -> try { dayOf(Instant.parse(e).toEpochMilli()).format(DateTimeFormatter.ofLocalizedDate(FormatStyle.SHORT)) } catch (_: Exception) { null } }
        val until = if (untilDate != null) " · " + stringResource(R.string.chat_until, untilDate) else ""
        val state = when (ref.state) { "revoked" -> " · " + stringResource(R.string.chat_revoked); "expired" -> " · " + stringResource(R.string.chat_expired); else -> "" }
        Text(stringResource(if (ref.shown) R.string.chat_shown else R.string.chat_sent_badge) + until + (if (ref.forwarded) " · " + stringResource(R.string.chat_forwarded) else "") + state,
            Modifier.padding(start = 3.dp), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        // Eigenes Original: Art umstellen, zurueckziehen, wieder freigeben
        if (own && !ref.forwarded) Box {
            Icon(Icons.Default.MoreVert, stringResource(R.string.more), Modifier.padding(start = 4.dp).size(18.dp).clickable { menu = true }, tint = MaterialTheme.colorScheme.onSurfaceVariant)
            DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                // Einen Standort gibt es nur gezeigt
                if (ref.type == "location") Unit
                else if (ref.shown) DropdownMenuItem(text = { Text(stringResource(R.string.chat_allow_save)) }, onClick = { menu = false; onMode("send") })
                else DropdownMenuItem(text = { Text(stringResource(R.string.chat_show_only)) }, onClick = { menu = false; onMode("show") })
                if (ref.state == "revoked") DropdownMenuItem(text = { Text(stringResource(R.string.chat_unrevoke)) }, onClick = { menu = false; onRevoke(false) })
                else DropdownMenuItem(text = { Text(stringResource(R.string.chat_revoke)) }, onClick = { menu = false; onRevoke(true) })
            }
        }
    }
}

/** Anhang einer Nachricht: Bild als Vorschau, sonst Karte mit Oeffnen und (bei Senden) "Bei mir speichern". */
@Composable
private fun RefItem(m: ChatMessage, ref: ChatRef, index: Int, own: Boolean, onImage: () -> Unit, onOpen: () -> Unit, onSave: () -> Unit,
                    onMode: (String) -> Unit, onRevoke: (Boolean) -> Unit) {
    val app = App.get(LocalContext.current)
    // Abgelaufen oder zurueckgezogen: Empfaenger sehen nur den Hinweis
    if (!ref.usable && !own) {
        Text((if (ref.type == "location") stringResource(R.string.chat_location) else ref.name) + " – " + stringResource(if (ref.state == "expired") R.string.chat_expired else R.string.chat_revoked),
            fontStyle = FontStyle.Italic, color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
        return
    }
    // Standort: Karte mit Link in die Karten-App
    if (ref.type == "location") { Column { LocationCard(ref, m, own); ModeLine(ref, own, onMode, onRevoke) }; return }
    // Ordner: einzelne Dateien speichert man in der Ordner-Ansicht
    val canSave = !own && !ref.shown && ref.type != "folder"
    Column {
        var shownAsImage = false
        if (ref.isImage) {
            var bmp by remember(m.id, index, ref.state) { mutableStateOf<android.graphics.Bitmap?>(null) }
            var gone by remember(m.id, index, ref.state) { mutableStateOf(false) }
            LaunchedEffect(m.id, index, ref.state) { bmp = app.chat.thumb(m.id, index); gone = bmp == null }
            val b = bmp
            if (b != null) {
                shownAsImage = true
                Box {
                    Image(b.asImageBitmap(), ref.name, Modifier.padding(vertical = 2.dp).widthIn(max = 260.dp).heightIn(max = 300.dp).clip(RoundedCornerShape(10.dp)).clickable(onClick = onImage),
                        contentScale = ContentScale.Fit)
                    if (canSave) IconButton(onClick = onSave, Modifier.align(Alignment.TopEnd)) {
                        Icon(Icons.Default.Save, stringResource(R.string.chat_save_mine), tint = Color.White,
                            modifier = Modifier.clip(CircleShape).background(Color.Black.copy(alpha = 0.35f)).padding(4.dp))
                    }
                }
            } else if (!gone) { Box(Modifier.size(160.dp, 120.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(24.dp)) }; shownAsImage = true }
        }
        // Ton (Sprachnachricht): gleich abspielen
        if (!shownAsImage && ref.type == "file" && (ref.mime ?: "").startsWith("audio/") && ref.usable) { VoicePlayer(m, index, ref); shownAsImage = true }
        if (!shownAsImage) Row(Modifier.padding(vertical = 2.dp).clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.surfaceVariant).clickable(onClick = onOpen)
            .padding(start = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(when (ref.type) {
                "note" -> Icons.AutoMirrored.Filled.StickyNote2; "folder" -> Icons.Default.Folder; "album" -> Icons.Default.PhotoLibrary; "event" -> Icons.Default.Event
                "task" -> Icons.Default.Checklist; "contact" -> Icons.Default.Person; else -> Icons.Default.Description
            }, null)
            Column(Modifier.width(170.dp).padding(start = 8.dp, top = 6.dp, bottom = 6.dp)) {
                Text(ref.name, maxLines = 2, overflow = TextOverflow.Ellipsis, fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.bodyMedium)
                Text(when (ref.type) {
                    "note" -> stringResource(R.string.chat_note); "folder" -> stringResource(R.string.chat_kind_folder); "album" -> stringResource(R.string.chat_kind_album); "event" -> stringResource(R.string.chat_kind_event)
                    "task" -> stringResource(R.string.chat_kind_task); "contact" -> stringResource(R.string.chat_kind_contact)
                    else -> android.text.format.Formatter.formatShortFileSize(LocalContext.current, ref.size ?: 0)
                },
                    style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            if (canSave) IconButton(onClick = onSave) { Icon(Icons.Default.Save, stringResource(R.string.chat_save_mine)) } else Spacer(Modifier.width(8.dp))
        }
        ModeLine(ref, own, onMode, onRevoke)
    }
}

/** Abschnittskopf im Anhaengen-Menue: woher es kommt (Handy oder dmOffice) und was dabei passiert. */
@Composable
private fun AttachHeader(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, hint: String) {
    Row(Modifier.padding(start = 12.dp, end = 16.dp, top = 8.dp, bottom = 4.dp).widthIn(max = 280.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, null, Modifier.size(16.dp), tint = MaterialTheme.colorScheme.primary)
        Column(Modifier.padding(start = 8.dp)) {
            Text(title, style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.primary)
            Text(hint, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

/** Gezeigtes Album: Fotos als Raster, antippen zeigt das Bild gross (nur ansehen). */
@Composable
private fun AlbumViewDialog(message: Long, index: Int, name: String, onDismiss: () -> Unit) {
    val app = App.get(LocalContext.current)
    var photos by remember { mutableStateOf<List<at.dimes.dmoffice.chat.ChatAlbumPhoto>?>(null) }
    var big by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(message, index) { photos = try { app.chat.album(message, index) } catch (_: Exception) { emptyList() } }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(name) }, confirmButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.close)) } },
        text = {
            val list = photos
            if (list == null) CircularProgressIndicator()
            else if (list.isEmpty()) Text(stringResource(R.string.chat_gone))
            else androidx.compose.foundation.lazy.grid.LazyVerticalGrid(androidx.compose.foundation.lazy.grid.GridCells.Fixed(3), Modifier.heightIn(max = 480.dp),
                horizontalArrangement = Arrangement.spacedBy(4.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                items(list.size) { n ->
                    val p = list[n]
                    val bmp by androidx.compose.runtime.produceState<android.graphics.Bitmap?>(null, p.key) { value = app.chat.albumThumb(message, index, p.key) }
                    Box(Modifier.height(100.dp).fillMaxWidth().clip(RoundedCornerShape(6.dp)).background(MaterialTheme.colorScheme.surfaceVariant).clickable { big = p.key }) {
                        bmp?.let { androidx.compose.foundation.Image(it.asImageBitmap(), p.name, Modifier.fillMaxSize(), contentScale = androidx.compose.ui.layout.ContentScale.Crop) }
                    }
                }
            }
        })
    big?.let { key ->
        val bmp by androidx.compose.runtime.produceState<android.graphics.Bitmap?>(null, key) { value = app.chat.albumThumb(message, index, key, 1600) }
        androidx.compose.ui.window.Dialog(onDismissRequest = { big = null }, properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false)) {
            Box(Modifier.fillMaxSize().background(Color.Black).clickable { big = null }, contentAlignment = Alignment.Center) {
                bmp?.let { androidx.compose.foundation.Image(it.asImageBitmap(), null, Modifier.fillMaxSize(), contentScale = androidx.compose.ui.layout.ContentScale.Fit) } ?: CircularProgressIndicator()
            }
        }
    }
}

/** Standort teilen: einmalig jetzt oder live fuer 1-30 Minuten. */
@Composable
private fun LocationShareDialog(onDismiss: () -> Unit, onShare: (Int) -> Unit) {
    var minutes by remember { mutableStateOf(15f) }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.chat_location_share)) }, text = {
        Column {
            Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).clickable { onShare(0) }.padding(vertical = 10.dp, horizontal = 4.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Place, null)
                Column(Modifier.padding(start = 12.dp)) {
                    Text(stringResource(R.string.chat_location_now), fontWeight = FontWeight.SemiBold)
                    Text(stringResource(R.string.chat_location_now_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            HorizontalDivider(Modifier.padding(vertical = 8.dp))
            Text(stringResource(R.string.chat_location_live), fontWeight = FontWeight.SemiBold)
            Text(stringResource(R.string.chat_location_live_minutes, minutes.toInt()), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Slider(value = minutes, onValueChange = { minutes = it }, valueRange = 1f..30f, steps = 28)
        }
    }, confirmButton = { TextButton(onClick = { onShare(minutes.toInt()) }) { Text(stringResource(R.string.chat_location_live_start)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

/** Ein gesammelter Anhang über dem Eingabefeld: Vorschau bzw. Symbol, Name, × zum Entfernen. */
@Composable
private fun PendingChip(p: at.dimes.dmoffice.chat.ChatPending, onRemove: () -> Unit) {
    val ctx = LocalContext.current
    val bmp by androidx.compose.runtime.produceState<android.graphics.Bitmap?>(null, p) {
        value = try {
            when {
                !p.image -> null
                p is at.dimes.dmoffice.chat.ChatPending.Local -> kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
                    val o = android.graphics.BitmapFactory.Options().apply { inSampleSize = 8 }
                    android.graphics.BitmapFactory.decodeFile(p.file.absolutePath, o)
                }
                p is at.dimes.dmoffice.chat.ChatPending.Ref && p.item != null -> at.dimes.dmoffice.files.Thumbs.load(ctx, p.item, at.dimes.dmoffice.files.Thumbs.SMALL)
                p is at.dimes.dmoffice.chat.ChatPending.Gallery -> at.dimes.dmoffice.files.Thumbs.load(ctx, p.item, at.dimes.dmoffice.files.Thumbs.SMALL)
                else -> null
            }
        } catch (_: Exception) { null }
    }
    Row(Modifier.clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.surfaceVariant).padding(start = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        val b = bmp
        if (b != null) Image(b.asImageBitmap(), null, Modifier.size(40.dp).clip(RoundedCornerShape(8.dp)), contentScale = ContentScale.Crop)
        else Icon(if (p is at.dimes.dmoffice.chat.ChatPending.Ref && p.ref.type == "note") Icons.AutoMirrored.Filled.StickyNote2 else Icons.Default.Description, null,
            Modifier.padding(8.dp).size(24.dp))
        Text(p.name, Modifier.widthIn(max = 120.dp).padding(start = 6.dp), maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall)
        IconButton(onClick = onRemove, Modifier.size(32.dp)) { Icon(Icons.Default.Close, stringResource(R.string.chat_pending_remove), Modifier.size(18.dp)) }
    }
}
