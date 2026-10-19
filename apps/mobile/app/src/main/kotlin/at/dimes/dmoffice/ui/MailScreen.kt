package at.dimes.dmoffice.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.AttachFile
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.Badge
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.pulltorefresh.PullToRefreshBox
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
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
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.Folder
import at.dimes.dmoffice.api.MailAccount
import at.dimes.dmoffice.api.MailItem
import kotlinx.coroutines.launch
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId

/** Anzeigename eines Ordners: Spezialordner uebersetzt, sonst der Name vom Server. */
@Composable
fun folderLabel(f: Folder): String = when (f.special) {
    "inbox" -> stringResource(R.string.inbox); "sent" -> stringResource(R.string.folder_sent); "drafts" -> stringResource(R.string.folder_drafts)
    "trash" -> stringResource(R.string.folder_trash); "junk" -> stringResource(R.string.folder_junk); "archive" -> stringResource(R.string.folder_archive)
    "flagged" -> stringResource(R.string.folder_flagged); else -> f.name
}

/** Mailliste: Konto und Ordner oben waehlbar, Liste aus dem Cache, Ziehen zum Aktualisieren. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun MailScreen(onOpen: (account: String, folder: String, uid: Long) -> Unit, onCompose: (String) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val settings by app.prefs.settings.collectAsState()
    var accounts by remember { mutableStateOf<List<MailAccount>>(emptyList()) }
    var account by rememberSaveable { mutableStateOf(settings.mailAccount) }
    var folder by rememberSaveable { mutableStateOf("INBOX") }
    var refreshing by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }
    var hasMore by remember { mutableStateOf(false) }
    var page by remember { mutableStateOf(0) }
    var folderMenu by remember { mutableStateOf(false) }
    var accountMenu by remember { mutableStateOf(false) }

    suspend fun refresh(acc: String) {
        refreshing = true; error = null
        try {
            app.mail.refreshFolders(acc)
            hasMore = app.mail.refresh(acc, folder, 0); page = 0
        } catch (e: Exception) { error = e.message } finally { refreshing = false }
    }

    LaunchedEffect(Unit) {
        app.mail.loadCategories()
        try {
            accounts = app.mail.accounts()
            if (account == null || accounts.none { it.id == account }) { account = accounts.firstOrNull()?.id; app.prefs.setMailAccount(account) }
            account?.let { refresh(it) }
        } catch (e: Exception) { error = e.message }
    }

    val acc = account
    if (acc == null) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(error ?: stringResource(R.string.no_mail_account), color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(24.dp))
        }
        return
    }
    val folders by remember(acc) { app.mail.folders(acc) }.collectAsState(initial = emptyList())
    val items by remember(acc, folder) { app.mail.items(acc, folder) }.collectAsState(initial = emptyList())
    val current = folders.firstOrNull { it.path == folder }
    // Prio-Posteingang wie im Web: Reiter Relevant/Sonstige im Posteingang
    var focusInfo by remember(acc, folder) { mutableStateOf<at.dimes.dmoffice.api.FocusInfo?>(null) }
    var focusTab by rememberSaveable { mutableStateOf(true) }
    val inbox = current?.special == "inbox"
    var focusRefreshed by remember(acc, folder) { mutableStateOf(false) }
    LaunchedEffect(acc, folder, inbox, items.size) {
        focusInfo = if (inbox) try { app.mail.focusInfo(acc, folder) } catch (_: Exception) { null } else null
        // Der Server hat gerade eingeordnet: einmal neu laden, damit die Liste die Einordnung kennt
        if (focusInfo?.enabled == true && !focusRefreshed && items.any { it.focus == null }) {
            focusRefreshed = true
            try { app.mail.refresh(acc, folder, 0) } catch (_: Exception) { }
        }
    }
    val focusOn = inbox && focusInfo?.enabled == true
    val shownItems = if (!focusOn) items else items.filter { (it.focus ?: true) == focusTab }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(start = 8.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            Box {
                Row(Modifier.clip(MaterialTheme.shapes.small).clickable { folderMenu = true }.padding(horizontal = 8.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(current?.let { folderLabel(it) } ?: folder, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
                    if ((current?.unread ?: 0) > 0) { Spacer(Modifier.width(8.dp)); Badge { Text(current!!.unread.toString()) } }
                    Icon(Icons.Default.ArrowDropDown, contentDescription = null)
                }
                DropdownMenu(expanded = folderMenu, onDismissRequest = { folderMenu = false }) {
                    for (f in folders) DropdownMenuItem(
                        text = { Text(folderLabel(f) + if (f.unread > 0) "  (${f.unread})" else "", fontWeight = if (f.unread > 0) FontWeight.SemiBold else FontWeight.Normal) },
                        onClick = { folderMenu = false; folder = f.path; scope.launch { refresh(acc) } }
                    )
                }
            }
            Spacer(Modifier.weight(1f))
            if (accounts.size > 1) Box {
                TextButton(onClick = { accountMenu = true }) { Text(accounts.firstOrNull { it.id == acc }?.name ?: "", maxLines = 1, overflow = TextOverflow.Ellipsis) }
                DropdownMenu(expanded = accountMenu, onDismissRequest = { accountMenu = false }) {
                    for (a in accounts) DropdownMenuItem(text = { Text(a.name + " · " + a.email) }, onClick = { accountMenu = false; account = a.id; app.prefs.setMailAccount(a.id); folder = "INBOX"; scope.launch { refresh(a.id) } })
                }
            }
            IconButton(onClick = { scope.launch { app.mail.sync(); refresh(acc) } }) { Icon(Icons.Default.Refresh, contentDescription = stringResource(R.string.refresh)) }
        }
        error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp)) }
        if (focusOn) androidx.compose.material3.TabRow(selectedTabIndex = if (focusTab) 0 else 1) {
            val fi = focusInfo!!
            androidx.compose.material3.Tab(selected = focusTab, onClick = { focusTab = true },
                text = { Text(stringResource(R.string.focused) + if (fi.focused > 0) " (${fi.focused})" else "") })
            androidx.compose.material3.Tab(selected = !focusTab, onClick = { focusTab = false },
                text = { Text(stringResource(R.string.other) + if (fi.other > 0) " (${fi.other})" else "") })
        }
        PullToRefreshBox(isRefreshing = refreshing, onRefresh = { scope.launch { refresh(acc) } }, modifier = Modifier.fillMaxSize()) {
            if (shownItems.isEmpty() && !refreshing) Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { Text(stringResource(R.string.no_messages), color = MaterialTheme.colorScheme.onSurfaceVariant) }
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
                items(shownItems.size, key = { i -> "${shownItems[i].account}/${shownItems[i].folder}/${shownItems[i].uid}" }) { i ->
                    val m = shownItems[i]
                    MailRow(m, folder) { onOpen(m.account.ifEmpty { acc }, m.folder.ifEmpty { folder }, m.uid) }
                }
                if (hasMore) item { TextButton(onClick = { scope.launch { try { hasMore = app.mail.refresh(acc, folder, page + 1); page++ } catch (e: Exception) { error = e.message } } }, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.load_more)) } }
            }
        }
    }
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.BottomEnd) {
        ExtendedFloatingActionButton(onClick = { onCompose(acc) }, icon = { Icon(Icons.Default.Edit, contentDescription = null) }, text = { Text(stringResource(R.string.compose)) }, modifier = Modifier.padding(16.dp))
    }
}

@Composable
fun MailRow(m: MailItem, currentFolder: String, onClick: () -> Unit) {
    val ctx = LocalContext.current
    val categories by App.get(ctx).mail.categories.collectAsState()
    val zone = ZoneId.systemDefault()
    val date = try { Instant.parse(m.date) } catch (_: Exception) { try { java.time.OffsetDateTime.parse(m.date).toInstant() } catch (_: Exception) { null } }
    val dateText = date?.let { d -> if (d.atZone(zone).toLocalDate() == LocalDate.now(zone)) Fmt.time(ctx, d.toEpochMilli()) else Fmt.shortDate(d.atZone(zone).toLocalDate()) } ?: ""
    val sentFolder = currentFolder.equals("Sent", true) || currentFolder.contains("Gesendet", true)
    val who = if (sentFolder) m.to.joinToString(", ") { it.text } else (m.from?.text ?: "")
    val weight = if (m.seen) FontWeight.Normal else FontWeight.Bold
    Row(Modifier.fillMaxWidth().clickable(onClick = onClick).padding(horizontal = 12.dp, vertical = 8.dp), verticalAlignment = Alignment.Top) {
        Box(Modifier.padding(top = 6.dp).size(10.dp).clip(CircleShape).background(if (m.seen) MaterialTheme.colorScheme.surface else MaterialTheme.colorScheme.primary))
        Spacer(Modifier.width(10.dp))
        Column(Modifier.weight(1f)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(who.ifBlank { "–" }, style = MaterialTheme.typography.titleSmall, fontWeight = weight, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
                if (m.hasAttachments) Icon(Icons.Default.AttachFile, contentDescription = null, modifier = Modifier.size(14.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                if (m.flagged) Icon(Icons.Default.Flag, contentDescription = null, modifier = Modifier.size(14.dp), tint = MaterialTheme.colorScheme.error)
                Spacer(Modifier.width(6.dp))
                Text(dateText, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            Text(m.subject.ifBlank { "(…)" }, style = MaterialTheme.typography.bodyMedium, fontWeight = weight, maxLines = 1, overflow = TextOverflow.Ellipsis)
            if (m.preview.isNotBlank()) Text(m.preview, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 2, overflow = TextOverflow.Ellipsis)
            CategoryChips(categories.filter { it.key in m.keywords })
        }
    }
}
