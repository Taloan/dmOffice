package at.dimes.dmoffice.ui

import androidx.compose.material.icons.filled.PhotoLibrary
import androidx.compose.material.icons.filled.LibraryMusic
import androidx.compose.material.icons.filled.Inventory2
import androidx.compose.material.icons.filled.AutoStories
import androidx.compose.material.icons.filled.MonitorHeart
import androidx.compose.material.icons.filled.Dashboard
import android.content.Context
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.Chat
import androidx.compose.material.icons.automirrored.filled.StickyNote2
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.Contacts
import androidx.compose.material.icons.filled.Email
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R

/**
 * Leiste unten (Version 2): Reihenfolge der Bereiche und welche davon im Menü „Mehr“ liegen. Gilt nur auf diesem Gerät.
 * Ohne Einstellung: alle Bereiche in der bisherigen Reihenfolge in der Leiste.
 */
object TabBar {
    private fun sp(ctx: Context) = ctx.getSharedPreferences("ui", Context.MODE_PRIVATE)

    /** Bereiche, die man anordnen kann (Mehr ist der Knopf selbst). */
    val all: List<Tab> get() = Tab.entries.filter { it != Tab.More }

    /** Stand fuer die Oberflaeche: Reihenfolge und die Bereiche im Menü „Mehr“. */
    val state = mutableStateOf<Pair<List<Tab>, Set<Tab>>?>(null)

    /** Symbolgröße der Leiste (dp); die Höhe der Leiste richtet sich danach. */
    enum class Size(val icon: Int, val height: Int, val label: Int) {
        Small(20, 44, R.string.tabbar_size_small), Normal(24, 52, R.string.tabbar_size_normal), Large(30, 64, R.string.tabbar_size_large)
    }

    val size = mutableStateOf<Size?>(null)

    fun size(ctx: Context): Size = size.value
        ?: (Size.entries.firstOrNull { it.name == sp(ctx).getString("tabSize", null) } ?: Size.Normal).also { size.value = it }

    fun setSize(ctx: Context, s: Size) {
        size.value = s
        sp(ctx).edit().putString("tabSize", s.name).apply()
    }

    fun get(ctx: Context): Pair<List<Tab>, Set<Tab>> = state.value ?: run {
        val p = sp(ctx)
        val F = at.dimes.dmoffice.data.Features.also { it.load(ctx) }
        val all = all.filter {
            // Release-Liste, Hauptschalter der Module und abgeschaltete Dateiablage (Verwaltung)
            when (it) {
                Tab.Journal -> F.on(F.JOURNAL)
                Tab.Coach -> F.on(F.COACH)
                Tab.Tasks -> F.on(F.TASKS)
                Tab.Notes -> F.on(F.NOTES)
                Tab.Audio -> F.on(F.AUDIO)
                Tab.Dms -> !F.filesOff.value && F.on(F.DMS)
                Tab.Gallery -> !F.filesOff.value && F.on(F.GALLERY)
                Tab.Files -> !F.filesOff.value
                else -> true
            }
        }
        val saved = p.getString("tabOrder", null)?.split(',')?.mapNotNull { n -> all.firstOrNull { it.name == n } } ?: emptyList()
        // Neue Bereiche (spaetere Versionen) haengen hinten an - die Übersicht kommt nach vorn
        val added = all.filter { it !in saved }
        val order = added.filter { it == Tab.Dashboard } + saved + added.filter { it != Tab.Dashboard }
        val more = p.getString("tabMore", null)?.split(',')?.mapNotNull { n -> all.firstOrNull { it.name == n } }?.toSet() ?: emptySet()
        (order to more).also { state.value = it }
    }

    fun set(ctx: Context, order: List<Tab>, more: Set<Tab>) {
        state.value = order to more
        sp(ctx).edit().putString("tabOrder", order.joinToString(",") { it.name }).putString("tabMore", more.joinToString(",") { it.name }).apply()
    }

    fun reset(ctx: Context) {
        sp(ctx).edit().remove("tabOrder").remove("tabMore").remove("tabSize").apply()
        state.value = null
        size.value = null
        get(ctx)
    }

    fun icon(t: Tab): ImageVector = when (t) {
        Tab.Dashboard -> Icons.Default.Dashboard; Tab.Mail -> Icons.Default.Email; Tab.Chat -> Icons.AutoMirrored.Filled.Chat; Tab.Calendar -> Icons.Default.CalendarMonth; Tab.Tasks -> Icons.Default.Checklist
        Tab.Notes -> Icons.AutoMirrored.Filled.StickyNote2; Tab.Journal -> Icons.Default.AutoStories; Tab.Coach -> Icons.Default.MonitorHeart; Tab.Contacts -> Icons.Default.Contacts; Tab.Files -> Icons.Default.Folder; Tab.Gallery -> Icons.Default.PhotoLibrary; Tab.Audio -> Icons.Default.LibraryMusic; Tab.Dms -> Icons.Default.Inventory2
        Tab.Settings -> Icons.Default.Settings; Tab.More -> Icons.Default.MoreHoriz
    }
}

/** Symbolgröße der Leiste unten (Einstellungen → Darstellung). */
@Composable
fun TabBarSizePicker(modifier: Modifier = Modifier) {
    val ctx = LocalContext.current
    val cur = TabBar.size.value ?: TabBar.size(ctx)
    androidx.compose.foundation.layout.Column(modifier) {
        Text(stringResource(R.string.tabbar_size), style = MaterialTheme.typography.labelLarge)
        androidx.compose.material3.SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth().padding(top = 6.dp)) {
            TabBar.Size.entries.forEachIndexed { i, s ->
                SegmentedButton(cur == s, { TabBar.setSize(ctx, s) },
                    androidx.compose.material3.SegmentedButtonDefaults.itemShape(i, TabBar.Size.entries.size)) { Text(stringResource(s.label), maxLines = 1) }
            }
        }
    }
}

/**
 * Leiste unten anordnen (ganzer Bildschirm): Pfeile verschieben, der Schalter legt einen Bereich in die Leiste oder ins
 * Menü „Mehr“. Der Name bleibt TabBarDialog - aufgerufen wird er aus den Einstellungen.
 */
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class)
@Composable
fun TabBarDialog(onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    val (order, more) = TabBar.state.value ?: TabBar.get(ctx)
    fun move(i: Int, by: Int) {
        val j = i + by
        if (j !in order.indices) return
        val l = order.toMutableList(); val x = l.removeAt(i); l.add(j, x)
        TabBar.set(ctx, l, more)
    }
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss,
        // Innerhalb der Systemleisten: sonst ragt die Liste unter die Navigationsleiste und lässt sich nicht bis zum Ende rollen
        properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        androidx.compose.material3.Scaffold(Modifier.fillMaxSize(),
            topBar = {
                androidx.compose.material3.TopAppBar(title = { Text(stringResource(R.string.tabbar_title)) },
                    navigationIcon = { IconButton(onClick = onDismiss) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
                    actions = { TextButton(onClick = { TabBar.reset(ctx) }) { Text(stringResource(R.string.tabbar_reset)) } })
            }) { pad ->
            LazyColumn(Modifier.padding(pad).fillMaxSize(), contentPadding = androidx.compose.foundation.layout.PaddingValues(bottom = 24.dp)) {
                item { Text(stringResource(R.string.tabbar_hint), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) }
                itemsIndexed(order, key = { _, t -> t.name }) { i, t ->
                    val inBar = t !in more
                    ListItem(modifier = Modifier.fillMaxWidth(),
                        leadingContent = { Icon(TabBar.icon(t), contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
                        headlineContent = { Text(stringResource(t.label)) },
                        supportingContent = { Text(stringResource(if (inBar) R.string.tabbar_in_bar else R.string.tabbar_in_more)) },
                        trailingContent = {
                            Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                                IconButton(onClick = { move(i, -1) }, enabled = i > 0) { Icon(Icons.Default.KeyboardArrowUp, contentDescription = stringResource(R.string.tabbar_up)) }
                                IconButton(onClick = { move(i, 1) }, enabled = i < order.size - 1) { Icon(Icons.Default.KeyboardArrowDown, contentDescription = stringResource(R.string.tabbar_down)) }
                                // Mindestens ein Bereich bleibt in der Leiste
                                Switch(checked = inBar, enabled = !inBar || order.count { it !in more } > 1,
                                    onCheckedChange = { on -> TabBar.set(ctx, order, if (on) more - t else more + t) })
                            }
                        })
                }
            }
        }
    }
}
