package at.dimes.dmoffice.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.outlined.Send
import androidx.compose.material.icons.outlined.Archive
import androidx.compose.material.icons.outlined.AllInbox
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Check
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.material.icons.outlined.Delete
import androidx.compose.material.icons.outlined.Drafts
import androidx.compose.material.icons.outlined.Folder
import androidx.compose.material.icons.outlined.Inbox
import androidx.compose.material.icons.outlined.LocalFireDepartment
import androidx.compose.material.icons.outlined.Schedule
import androidx.compose.material.icons.outlined.StarOutline
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.api.Folder
import at.dimes.dmoffice.api.MailAccount
import at.dimes.dmoffice.api.MailCategory

/** Alle Posteingaenge zusammen - kein Ordner auf dem Server (MailStore.UnifiedFolder). */
const val UNIFIED = "__unified"

/** Symbol eines Ordners wie bei Proton: Systemordner eigene Symbole, sonst ein Ordner in der Akzentfarbe. */
private fun iconOf(f: Folder): ImageVector = when (f.special) {
    "inbox" -> Icons.Outlined.Inbox; "drafts" -> Icons.Outlined.Drafts; "sent" -> Icons.AutoMirrored.Outlined.Send
    "flagged" -> Icons.Outlined.StarOutline; "junk" -> Icons.Outlined.LocalFireDepartment; "archive" -> Icons.Outlined.Archive
    "trash" -> Icons.Outlined.Delete; "snoozed" -> Icons.Outlined.Schedule; else -> Icons.Outlined.Folder
}

/**
 * Ordnerleiste der Mail wie bei Proton: Konto oben, Systemordner in fester Reihenfolge mit Symbolen,
 * darunter eigene Ordner (eingerueckt nach Ebene), dann die Kategorien mit Farbpunkt. Ungelesene rechts.
 */
@Composable
fun FolderDrawer(
    accounts: List<MailAccount>, account: String, folders: List<Folder>, current: String, categories: List<MailCategory>,
    onAccount: (String) -> Unit, onFolder: (String) -> Unit,
) {
    val order = listOf("inbox", "drafts", "sent", "flagged", "snoozed", "junk", "archive", "trash")
    val system = folders.filter { it.special in order }.sortedBy { order.indexOf(it.special) }
    val own = folders.filter { it.special !in order }.sortedBy { it.path.lowercase() }
    Column(Modifier.fillMaxHeight().verticalScroll(rememberScrollState()).padding(vertical = 12.dp)) {
        val acc = accounts.firstOrNull { it.id == account }
        Text("dmOffice Mail", style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.Bold, modifier = Modifier.padding(horizontal = 24.dp, vertical = 8.dp))
        // Aktuelles Konto als Auswahl: Tippen oeffnet die Liste, das aktive mit Haken
        if (acc != null) Box(Modifier.padding(horizontal = 12.dp)) {
            var open by remember { mutableStateOf(false) }
            Row(Modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).clickable(enabled = accounts.size > 1) { open = true }.padding(horizontal = 12.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text(acc.name.ifBlank { acc.email }, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(acc.email, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
                if (accounts.size > 1) Icon(Icons.Filled.ArrowDropDown, contentDescription = null)
            }
            androidx.compose.material3.DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                for (a in accounts) androidx.compose.material3.DropdownMenuItem(
                    text = { Column { Text(a.name.ifBlank { a.email }); Text(a.email, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) } },
                    trailingIcon = { if (a.id == account) Icon(Icons.Filled.Check, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                    onClick = { open = false; if (a.id != account) onAccount(a.id) })
            }
        }
        HorizontalDivider(Modifier.padding(vertical = 8.dp))
        // Alle Posteingaenge zusammen (ohne Relevant/Sonstige - die Konten koennen das verschieden eingestellt haben)
        if (accounts.size > 1) Entry(Icons.Outlined.AllInbox, androidx.compose.ui.res.stringResource(at.dimes.dmoffice.R.string.all_inboxes), 0,
            current == UNIFIED, 0) { onFolder(UNIFIED) }
        for (f in system) Entry(iconOf(f), folderLabel(f), f.unread, f.path == current, 0) { onFolder(f.path) }
        if (own.isNotEmpty()) HorizontalDivider(Modifier.padding(vertical = 8.dp))
        for (f in own) {
            // Ebene aus dem Pfad ("Kunden/2026" -> eine Stufe eingerueckt), angezeigt wird nur der letzte Teil
            val depth = f.path.count { it == '/' || it == '.' }.coerceAtMost(4)
            Entry(iconOf(f), f.name.substringAfterLast('/'), f.unread, f.path == current, depth, iconTint = MaterialTheme.colorScheme.primary) { onFolder(f.path) }
        }
        if (categories.isNotEmpty()) HorizontalDivider(Modifier.padding(vertical = 8.dp))
        for (c in categories) {
            val path = "cat:" + c.key
            Row(Modifier.fillMaxWidth().padding(horizontal = 12.dp).clip(RoundedCornerShape(12.dp))
                .background(if (path == current) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface.copy(alpha = 0f))
                .clickable { onFolder(path) }.padding(horizontal = 14.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(14.dp).clip(CircleShape).background(parseColor(c.color)))
                Spacer(Modifier.width(20.dp))
                Text(c.name, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

@Composable
private fun Entry(icon: ImageVector, label: String, unread: Int, selected: Boolean, depth: Int,
                  iconTint: androidx.compose.ui.graphics.Color = MaterialTheme.colorScheme.onSurfaceVariant, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 12.dp).clip(RoundedCornerShape(12.dp))
            .background(if (selected) MaterialTheme.colorScheme.secondaryContainer else MaterialTheme.colorScheme.surface.copy(alpha = 0f))
            .clickable(onClick = onClick).padding(start = (14 + depth * 16).dp, end = 14.dp, top = 12.dp, bottom = 12.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Icon(icon, contentDescription = null, tint = if (selected) MaterialTheme.colorScheme.primary else iconTint)
        Spacer(Modifier.width(18.dp))
        Text(label, style = MaterialTheme.typography.bodyLarge, fontWeight = if (selected || unread > 0) FontWeight.SemiBold else FontWeight.Normal,
            maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        if (unread > 0) Text(unread.toString(), style = MaterialTheme.typography.labelLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
}
