package at.dimes.dmoffice.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Category
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material.icons.filled.KeyboardArrowUp
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.widthIn
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CheckboxDefaults
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextDecoration
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.ItemEntity
import at.dimes.dmoffice.data.ListTypes
import at.dimes.dmoffice.data.NaturalDate
import at.dimes.dmoffice.ical.IcalTime
import kotlinx.coroutines.launch
import java.time.LocalDate
import java.time.ZoneId

/** Aufgaben in Gruppen: ueberfaellig, heute, spaeter, ohne Datum, erledigt (optional). Schnell-Eingabe oben. */
@Composable
fun TasksScreen(onOpen: (String) -> Unit, openList: Pair<String, Long>? = null, onOpenMail: (String, String, Long) -> Unit = { _, _, _ -> }) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val tasks by app.repo.tasks.collectAsState(initial = emptyList())
    val cals by app.repo.calendars.collectAsState(initial = emptyList())
    val settings by app.prefs.settings.collectAsState()
    var quick by remember { mutableStateOf("") }
    val zone = ZoneId.systemDefault()
    val todayStart = LocalDate.now(zone).epochMillis(zone)
    val tomorrowStart = todayStart + 86400_000
    val colors = remember(cals) { cals.associate { it.href to it.color } }
    val visible = remember(cals) { cals.filter { it.visible }.map { it.href }.toSet() }

    // Liste waehlen: "Alle" oder eine einzelne Liste; Einkaufs- und Checklisten nach Abteilung/Kategorie
    val types by ListTypes.byUri.collectAsState()
    LaunchedEffect(Unit) { ListTypes.refresh(ctx) }
    val taskLists = cals.filter { it.supportsTasks && it.visible }
    var listHref by rememberSaveable { mutableStateOf(lastTaskList) }
    // Aus dem Widget: gleich diese Liste
    LaunchedEffect(openList) { openList?.let { listHref = it.first } }
    if (listHref != null && taskLists.isNotEmpty() && taskLists.none { it.href == listHref }) listHref = null
    lastTaskList = listHref
    val info = listHref?.let { types[ListTypes.uriOf(it)] }

    val shown = tasks.filter { it.calendarHref in visible && (listHref == null || it.calendarHref == listHref) }
    val open = shown.filter { !it.completed }
    val groups = listOf(
        R.string.overdue to open.filter { it.due != null && it.due < todayStart },
        R.string.due_today to open.filter { it.due != null && it.due >= todayStart && it.due < tomorrowStart },
        R.string.later to open.filter { it.due != null && it.due >= tomorrowStart },
        R.string.no_date to open.filter { it.due == null },
        R.string.completed to if (settings.showCompleted) shown.filter { it.completed } else emptyList(),
    )

    // Einkaufs-/Checkliste wie im Web: "2 kg Kartoffeln" zerlegen, Vorschlaege vom Server, Abteilung waehlen oder raten.
    // Keine Datumserkennung ("Ingwer Orange Tee" ist kein Termin).
    val isList = info != null && info.type != ListTypes.TASKS
    var addSection by remember(listHref) { mutableStateOf("") }   // leer: automatisch
    val onListNames = remember(shown) { shown.filter { !it.completed }.map { ListTypes.itemOf(it.ics, it.summary).item.lowercase() }.toSet() }
    val typedItem = remember(quick) { ListTypes.parseItem(quick).item }
    val suggestions = remember(typedItem, info) { if (isList) ListTypes.suggest(info!!.history, typedItem) else emptyList() }
    fun addItem(name: String? = null) {
        val i = info ?: return
        val p = ListTypes.parseItem(quick)
        val item = (name ?: p.item).trim()
        if (item.isEmpty()) return
        val known = i.history.firstOrNull { it.name.equals(item, true) }
        val unit = p.unit.ifEmpty { if (p.qty.isNotEmpty()) known?.unit ?: "" else "" }
        val sec = addSection.ifEmpty { known?.section ?: ListTypes.guessSection(item, i.sections) ?: "" }
        val x = ListTypes.ShopItem(p.qty, unit, known?.name ?: item)
        quick = ""
        scope.launch {
            app.repo.quickTask(x.title(), null, listHref, item = x.item, section = sec.ifEmpty { null }, qty = x.qty.ifEmpty { null }, unit = x.unit.ifEmpty { null })
        }
    }
    val parsed = remember(quick) { if (quick.isBlank() || isList) null else NaturalDate.parse(quick) }
    fun add() {
        val s = quick.trim()
        if (s.isEmpty()) return
        if (isList) { addItem(); return }
        val r = NaturalDate.parse(s)
        quick = ""
        val due = when {
            r.date == null -> null
            r.time != null -> IcalTime(null, r.date.atTime(r.time).atZone(zone).toInstant(), zone)
            else -> IcalTime(r.date, null, null)
        }
        scope.launch { app.repo.quickTask(r.title, due, listHref) }
    }
    val curCal = cals.firstOrNull { it.href == listHref }
    // E-Mail-Nachverfolgung wie im Web: gekennzeichnete Mails mit Faelligkeit
    var mailView by rememberSaveable { mutableStateOf(false) }
    var newList by remember { mutableStateOf(false) }
    var editSections by remember { mutableStateOf(false) }
    var importOpen by remember { mutableStateOf(false) }
    fun failed() = android.widget.Toast.makeText(ctx, ctx.getString(R.string.save_failed), android.widget.Toast.LENGTH_LONG).show()

    Column(Modifier.fillMaxSize()) {
        Row(
            Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            if (taskLists.size > 1) FilterChip(selected = !mailView && listHref == null, onClick = { mailView = false; listHref = null }, label = { Text(stringResource(R.string.all_lists)) })
            FilterChip(selected = mailView, onClick = { mailView = true }, label = { Text(stringResource(R.string.mail_followup)) },
                leadingIcon = { Icon(Icons.Default.Flag, contentDescription = null, modifier = Modifier.size(16.dp), tint = Color(0xFFD13438)) })
            for (c in taskLists) FilterChip(
                selected = !mailView && listHref == c.href, onClick = { mailView = false; listHref = c.href }, label = { Text(c.name) },
                // In der Farbe der Liste wie im Web
                colors = FilterChipDefaults.filterChipColors(selectedContainerColor = colorOf(c.color).copy(alpha = 0.22f)),
                leadingIcon = { androidx.compose.foundation.layout.Box(Modifier.padding(start = 2.dp).size(10.dp).clip(androidx.compose.foundation.shape.CircleShape).background(colorOf(c.color))) }
            )
            // Neue Liste wie im Web (Menueband "Neue Liste")
            androidx.compose.material3.AssistChip(onClick = { newList = true }, label = { Text(stringResource(R.string.new_list)) },
                leadingIcon = { Icon(Icons.Default.Add, contentDescription = null, modifier = Modifier.size(18.dp)) })
        }
        if (mailView) { MailFollowUps(onOpenMail); return@Column }
        // Werkzeuge der gewaehlten Liste: Abteilungen, Menue mit Abgehakten, Import, Listentyp
        if (info != null) Row(Modifier.fillMaxWidth().padding(start = 8.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            if (isList) androidx.compose.material3.TextButton(onClick = { editSections = true }) {
                Icon(Icons.Default.Edit, contentDescription = null, modifier = Modifier.size(18.dp))
                Text(stringResource(if (info.type == ListTypes.CHECK) R.string.edit_categories else R.string.edit_departments), modifier = Modifier.padding(start = 6.dp))
            }
            androidx.compose.foundation.layout.Spacer(Modifier.weight(1f))
            ListMenu(info,
                onAction = { what -> scope.launch { if (!ListTypes.action(ctx, info.id, what)) failed() } },
                onMeta = { type, done -> scope.launch { if (!ListTypes.setMeta(ctx, info.id, type, done)) failed() } },
                onImport = { importOpen = true })
        }
        // Eine Zeile: Eingabe, Abteilung (klein, im Feld) und Hinzufuegen
        OutlinedTextField(
            value = quick, onValueChange = { quick = it }, singleLine = true,
            placeholder = { Text(stringResource(when (info?.type) { ListTypes.SHOP -> R.string.add_item_shop; ListTypes.CHECK -> R.string.add_item_check; else -> R.string.quick_add_task }),
                maxLines = 1, overflow = TextOverflow.Ellipsis) },
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done), keyboardActions = KeyboardActions(onDone = { add() }),
            trailingIcon = {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    // Abteilung fuer den neuen Eintrag: automatisch (bekannt oder geraten) oder fest
                    if (isList && info!!.sections.isNotEmpty()) {
                        var open by remember { mutableStateOf(false) }
                        androidx.compose.foundation.layout.Box {
                            if (addSection.isEmpty())
                                IconButton(onClick = { open = true }) {
                                    Icon(Icons.Default.Category, contentDescription = stringResource(R.string.automatic), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                                }
                            else
                                androidx.compose.material3.TextButton(onClick = { open = true }, contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 6.dp)) {
                                    Text(addSection, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.labelMedium, modifier = Modifier.widthIn(max = 90.dp))
                                }
                            androidx.compose.material3.DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                                androidx.compose.material3.DropdownMenuItem(text = { Text(stringResource(R.string.automatic)) }, onClick = { addSection = ""; open = false })
                                for (sec in info.sections) androidx.compose.material3.DropdownMenuItem(text = { Text(sec) }, onClick = { addSection = sec; open = false })
                            }
                        }
                    }
                    IconButton(onClick = { add() }, enabled = quick.isNotBlank()) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.add)) }
                }
            }
        )
        if (suggestions.isNotEmpty()) Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp)) {
            for (k in suggestions) {
                Row(Modifier.fillMaxWidth().clickable { addItem(k.name) }.padding(start = 12.dp, top = 2.dp, bottom = 2.dp),
                    verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Default.History, contentDescription = null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(18.dp))
                    Text(k.name, modifier = Modifier.weight(1f).padding(start = 10.dp), style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    if (k.name.lowercase() in onListNames) Text(stringResource(R.string.already_on_list), style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.primary, modifier = Modifier.padding(end = 8.dp))
                    k.section?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1) }
                    // Vorschlag vergessen (Tippfehler)
                    IconButton(onClick = { scope.launch { if (!ListTypes.forget(ctx, info!!.id, k.name)) failed() } }) {
                        Icon(Icons.Default.Close, contentDescription = stringResource(R.string.forget_suggestion), modifier = Modifier.size(16.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f))
            }
        }
        if (newList) NewListDialog { name, type ->
            newList = false
            if (name != null) scope.launch { if (!ListTypes.createList(ctx, name, type)) failed() }
        }
        if (editSections && info != null) SectionsDialog(info.sections, info.type == ListTypes.CHECK) { v ->
            editSections = false
            if (v != null) scope.launch { if (!ListTypes.saveSections(ctx, info.id, v)) failed() }
        }
        if (importOpen && info != null) ImportDialog(info.id) { importOpen = false }
        if (parsed != null && parsed.found) Text(
            stringResource(R.string.detected_due, Fmt.detected(ctx, parsed)) + "  " + stringResource(R.string.detected_as, parsed.title),
            modifier = Modifier.padding(horizontal = 20.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary
        )
        if (info != null && info.type != ListTypes.TASKS) {
            CheckList(shown, info, colorOf(curCal?.color), settings.showCompleted, onToggle = { t -> scope.launch { app.repo.setCompleted(t.href, !t.completed) } }, onEdit = { t -> onOpen(t.href) })
            return@Column
        }
        if (open.isEmpty() && groups.last().second.isEmpty()) {
            Text(stringResource(R.string.no_tasks), modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
            for ((label, list) in groups) {
                if (list.isEmpty()) continue
                item(key = "h$label") { SectionHeader(stringResource(label), highlight = label == R.string.overdue) }
                items(list.size, key = { i -> list[i].href }) { i ->
                    val t: ItemEntity = list[i]
                    TaskRow(t, colors[t.calendarHref], onClick = { onOpen(t.href) }, onToggle = { done -> scope.launch { app.repo.setCompleted(t.href, done) } })
                }
            }
        }
    }
}

/**
 * Abteilungen bzw. Kategorien: umbenennen, mit den Pfeilen in die Reihenfolge des Laufwegs bringen, entfernen, neue anlegen.
 * Eintraege einer entfernten Abteilung landen unter "Ohne Zuordnung".
 */
@Composable
private fun SectionsDialog(sections: List<String>, check: Boolean, onClose: (List<String>?) -> Unit) {
    val rows = remember { androidx.compose.runtime.mutableStateListOf<String>().apply { addAll(sections) } }
    var added by remember { mutableStateOf("") }
    androidx.compose.material3.AlertDialog(
        onDismissRequest = { onClose(null) },
        title = { Text(stringResource(if (check) R.string.categories else R.string.departments)) },
        text = {
            LazyColumn(Modifier.fillMaxWidth().heightIn(max = 460.dp)) {
                item { Text(stringResource(if (check) R.string.categories_intro else R.string.departments_intro), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 8.dp)) }
                items(rows.size) { i ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        OutlinedTextField(value = rows[i], onValueChange = { rows[i] = it }, singleLine = true, modifier = Modifier.weight(1f))
                        IconButton(onClick = { if (i > 0) { val x = rows.removeAt(i); rows.add(i - 1, x) } }, enabled = i > 0) {
                            Icon(Icons.Default.KeyboardArrowUp, contentDescription = stringResource(R.string.move_up))
                        }
                        IconButton(onClick = { if (i < rows.size - 1) { val x = rows.removeAt(i); rows.add(i + 1, x) } }, enabled = i < rows.size - 1) {
                            Icon(Icons.Default.KeyboardArrowDown, contentDescription = stringResource(R.string.move_down))
                        }
                        IconButton(onClick = { rows.removeAt(i) }) { Icon(Icons.Default.Delete, contentDescription = stringResource(R.string.delete)) }
                    }
                }
                item {
                    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 8.dp)) {
                        OutlinedTextField(value = added, onValueChange = { added = it }, singleLine = true, modifier = Modifier.weight(1f),
                            placeholder = { Text(stringResource(if (check) R.string.new_category else R.string.new_department)) })
                        IconButton(onClick = { added.trim().takeIf { it.isNotEmpty() }?.let { rows.add(it) }; added = "" }, enabled = added.isNotBlank()) {
                            Icon(Icons.Default.Add, contentDescription = stringResource(R.string.add))
                        }
                    }
                }
            }
        },
        confirmButton = {
            androidx.compose.material3.TextButton(onClick = {
                val extra = added.trim()
                onClose((rows.toList() + listOfNotNull(extra.ifEmpty { null })).map { it.trim() }.filter { it.isNotEmpty() }.distinctBy { it.lowercase() })
            }) { Text(stringResource(R.string.save)) }
        },
        dismissButton = { androidx.compose.material3.TextButton(onClick = { onClose(null) }) { Text(stringResource(R.string.cancel)) } }
    )
}

/** Gekennzeichnete Mails nach Faelligkeit: Tippen oeffnet die Mail, Haken = erledigt, Faelligkeit aendern ueber das Menue. */
@Composable
private fun MailFollowUps(onOpenMail: (String, String, Long) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var list by remember { mutableStateOf<List<at.dimes.dmoffice.api.FollowUp>?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    suspend fun load() { try { list = app.mail.followUps(); error = null } catch (e: Exception) { error = e.message } }
    LaunchedEffect(Unit) { load() }
    // Bei mehreren Konten: aus welchem Postfach die Mail stammt
    var mailboxes by remember { mutableStateOf<Map<String, String>>(emptyMap()) }
    LaunchedEffect(Unit) { try { mailboxes = app.mail.accounts().let { a -> if (a.size > 1) a.associate { it.id to it.email } else emptyMap() } } catch (_: Exception) { } }
    val today = LocalDate.now().toString()
    val sorted = (list ?: emptyList()).sortedWith(compareBy<at.dimes.dmoffice.api.FollowUp>({ it.due ?: "9999" }).thenByDescending { it.mail.date })
    error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
    if (list != null && sorted.isEmpty()) Text(stringResource(R.string.no_followups), modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
        items(sorted.size, key = { i -> sorted[i].mail.account + "/" + sorted[i].mail.folder + "/" + sorted[i].mail.uid }) { i ->
            val f = sorted[i]
            val m = f.mail
            var menu by remember { mutableStateOf(false) }
            Row(Modifier.fillMaxWidth().clickable { onOpenMail(m.account, m.folder, m.uid) }.padding(start = 4.dp, end = 4.dp, top = 2.dp, bottom = 2.dp),
                verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = false, onCheckedChange = {
                    scope.launch { try { app.mail.followUp(m.account, m.folder, listOf(m.uid), null, done = true); load() } catch (e: Exception) { error = e.message } }
                })
                Column(Modifier.weight(1f)) {
                    Text(m.subject.ifBlank { "(…)" }, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(listOfNotNull(m.from?.text, mailboxes[m.account], f.due?.let { d -> java.time.LocalDate.parse(d).let { Fmt.shortDate(it) } }).joinToString(" · "),
                        style = MaterialTheme.typography.bodySmall, maxLines = 1,
                        color = if (f.due != null && f.due < today) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant)
                }
                androidx.compose.foundation.layout.Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.Flag, contentDescription = stringResource(R.string.flag_menu), tint = Color(0xFFD13438)) }
                    androidx.compose.material3.DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        for ((label, due, hint) in followUpOptions(ctx)) androidx.compose.material3.DropdownMenuItem(
                            text = { Text(label) }, trailingIcon = { if (hint.isNotEmpty()) Text(hint, style = MaterialTheme.typography.bodySmall) },
                            onClick = { menu = false; scope.launch { try { app.mail.followUp(m.account, m.folder, listOf(m.uid), due); load() } catch (e: Exception) { error = e.message } } })
                    }
                }
            }
            HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.4f))
        }
    }
}

/** Zuletzt gewaehlte Liste - bleibt beim Wechsel zwischen den Reitern erhalten. */
private var lastTaskList: String? = null

/**
 * Einkaufs- oder Checkliste wie im Web: nach Abteilung/Kategorie in der Reihenfolge der Liste, Menge und Einheit rechts,
 * Notiz hinter dem Artikel. Abgehakte je nach Einstellung der Liste an ihrem Platz, unten ("Im Wagen") oder ausgeblendet.
 * Tippen auf die Zeile hakt ab bzw. wieder an (ohne Kaestchen), der Stift oder lange druecken oeffnet den Eintrag.
 */
@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun CheckList(items: List<ItemEntity>, info: ListTypes.Info, color: Color, showHidden: Boolean, onToggle: (ItemEntity) -> Unit, onEdit: (ItemEntity) -> Unit) {
    val check = info.type == ListTypes.CHECK
    val mode = ListTypes.doneMode(info)
    val noSection = stringResource(if (check) R.string.no_category else R.string.no_department)
    val parts = remember(items) { items.associate { it.href to ListTypes.itemOf(it.ics, it.summary) } }
    fun nameOf(t: ItemEntity) = parts[t.href]?.item?.lowercase() ?: t.summary.lowercase()
    fun secOf(t: ItemEntity): String? = ListTypes.sectionOf(t.ics)?.let { s -> info.sections.firstOrNull { it.equals(s, true) } }
    val done = items.filter { it.completed }.sortedBy { nameOf(it) }
    val inGroups = if (mode == "place") items else items.filter { !it.completed }
    val groups = (info.sections.map<String, String?> { it } + listOf(null))
        .map { sec -> (sec ?: noSection) to inGroups.filter { secOf(it) == sec }.sortedBy { nameOf(it) } }
        .filter { it.second.isNotEmpty() }
    val tail = if (mode == "bottom" || (mode == "hide" && showHidden)) done else emptyList()

    @Composable
    fun Header(label: String) {
        // Abteilung/Kategorie als Balken, getoent in der Listenfarbe wie der gewaehlte Listen-Chip oben (wie im Web)
        Text(label.uppercase(), style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.onSurface,
            modifier = Modifier.fillMaxWidth().padding(top = 12.dp).background(color.copy(alpha = 0.22f)).padding(horizontal = 16.dp, vertical = 6.dp))
    }

    @Composable
    fun Line(t: ItemEntity) {
        val p = parts[t.href] ?: ListTypes.ShopItem("", "", t.summary)
        val faded = if (t.completed) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.onSurface
        val strike = if (t.completed) TextDecoration.LineThrough else null
        // Ohne Kaestchen wie AnyList: die ganze Zeile hakt ab; Stift oder lange druecken bearbeitet
        Row(
            Modifier.fillMaxWidth().combinedClickable(onClick = { onToggle(t) }, onLongClick = { onEdit(t) })
                .padding(start = 16.dp, end = 4.dp, top = 2.dp, bottom = 2.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Column(Modifier.weight(1f)) {
                Text(p.item.ifBlank { t.summary.ifBlank { "–" } }, style = MaterialTheme.typography.bodyLarge, maxLines = 2, overflow = TextOverflow.Ellipsis,
                    textDecoration = strike, color = faded)
                t.description?.takeIf { it.isNotBlank() }?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            val amount = listOf(p.qty, p.unit).filter { it.isNotBlank() }.joinToString(" ")
            if (amount.isNotEmpty()) Text(amount, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant,
                textDecoration = strike, modifier = Modifier.padding(start = 8.dp))
            IconButton(onClick = { onEdit(t) }) {
                Icon(Icons.Default.Edit, contentDescription = stringResource(R.string.edit), tint = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        HorizontalDivider(color = MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.5f))
    }

    LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 88.dp)) {
        if (items.isEmpty()) item {
            Text(stringResource(if (check) R.string.no_entries else R.string.list_empty), modifier = Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        for ((label, list) in groups) {
            if (info.sections.isNotEmpty()) item(key = "s$label") { Header(label) }
            items(list.size, key = { i -> list[i].href }) { i -> Line(list[i]) }
        }
        if (tail.isNotEmpty()) {
            item(key = "tail") { Header(stringResource(if (check) R.string.done_n else R.string.in_cart_n, tail.size)) }
            items(tail.size, key = { i -> "t" + tail[i].href }) { i -> Line(tail[i]) }
        }
        if (mode == "hide" && !showHidden && done.isNotEmpty()) item(key = "hidden") {
            Text(ctx().resources.getQuantityString(R.plurals.n_hidden_done, done.size, done.size), modifier = Modifier.padding(16.dp),
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun ctx() = LocalContext.current

/** Menue der gewaehlten Liste wie das Menueband im Web: Abgehakte loeschen/zuruecksetzen, Abgehakte-Modus, Import, Listentyp. */
@Composable
private fun ListMenu(info: ListTypes.Info, onAction: (String) -> Unit, onMeta: (String?, String?) -> Unit, onImport: () -> Unit) {
    var open by remember { mutableStateOf(false) }
    val mode = ListTypes.doneMode(info)
    androidx.compose.foundation.layout.Box {
        IconButton(onClick = { open = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
        androidx.compose.material3.DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            @Composable
            fun Item(label: Int, selected: Boolean? = null, onClick: () -> Unit) = androidx.compose.material3.DropdownMenuItem(
                text = { Text(stringResource(label)) }, onClick = { open = false; onClick() },
                trailingIcon = if (selected == true) ({ Icon(Icons.Default.Check, contentDescription = null) }) else null)
            @Composable
            fun Group(label: Int) = Text(stringResource(label), style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.primary,
                modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp))
            if (info.type == ListTypes.SHOP) Item(R.string.clear_done) { onAction("clear") }
            if (info.type == ListTypes.CHECK) Item(R.string.reset_list) { onAction("reset") }
            if (info.type != ListTypes.TASKS) {
                Item(R.string.import_suggestions) { onImport() }
                HorizontalDivider()
                Group(R.string.done_mode)
                Item(R.string.done_place, mode == "place") { onMeta(null, "place") }
                Item(R.string.done_bottom, mode == "bottom") { onMeta(null, "bottom") }
                Item(R.string.done_hide, mode == "hide") { onMeta(null, "hide") }
                HorizontalDivider()
            }
            Group(R.string.list_type)
            Item(R.string.type_tasks, info.type == ListTypes.TASKS) { onMeta(ListTypes.TASKS, null) }
            Item(R.string.type_shop, info.type == ListTypes.SHOP) { onMeta(ListTypes.SHOP, null) }
            Item(R.string.type_check, info.type == ListTypes.CHECK) { onMeta(ListTypes.CHECK, null) }
        }
    }
}

/** Neue Liste: Name und Typ (Aufgaben, Einkaufsliste, Checkliste). */
@Composable
private fun NewListDialog(onClose: (String?, String) -> Unit) {
    var name by remember { mutableStateOf("") }
    var type by remember { mutableStateOf(ListTypes.TASKS) }
    androidx.compose.material3.AlertDialog(
        onDismissRequest = { onClose(null, type) },
        title = { Text(stringResource(R.string.new_list)) },
        text = {
            Column {
                OutlinedTextField(value = name, onValueChange = { name = it }, singleLine = true, label = { Text(stringResource(R.string.list_name)) }, modifier = Modifier.fillMaxWidth())
                for ((t, label) in listOf(ListTypes.TASKS to R.string.type_tasks, ListTypes.SHOP to R.string.type_shop, ListTypes.CHECK to R.string.type_check)) {
                    Row(Modifier.fillMaxWidth().clickable { type = t }.padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
                        androidx.compose.material3.RadioButton(selected = type == t, onClick = { type = t })
                        Text(stringResource(label))
                    }
                }
            }
        },
        confirmButton = { androidx.compose.material3.TextButton(onClick = { onClose(name.trim(), type) }, enabled = name.isNotBlank()) { Text(stringResource(R.string.save)) } },
        dismissButton = { androidx.compose.material3.TextButton(onClick = { onClose(null, type) }) { Text(stringResource(R.string.cancel)) } }
    )
}

/** Vorschlaege importieren wie im Web: AnyList-HTML oder ein Eintrag je Zeile; erst pruefen, dann uebernehmen. */
@Composable
private fun ImportDialog(listId: Long, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var text by remember { mutableStateOf("") }
    var result by remember { mutableStateOf<ListTypes.ImportResult?>(null) }
    var busy by remember { mutableStateOf(false) }
    androidx.compose.material3.AlertDialog(
        onDismissRequest = onClose,
        title = { Text(stringResource(R.string.import_suggestions)) },
        text = {
            Column {
                Text(stringResource(R.string.import_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                OutlinedTextField(value = text, onValueChange = { text = it; result = null }, minLines = 5, maxLines = 10, modifier = Modifier.fillMaxWidth().padding(top = 8.dp))
                result?.let { Text(stringResource(R.string.import_result, it.found, it.added), modifier = Modifier.padding(top = 8.dp), color = MaterialTheme.colorScheme.primary) }
            }
        },
        confirmButton = {
            androidx.compose.material3.TextButton(enabled = text.isNotBlank() && !busy, onClick = {
                busy = true
                scope.launch {
                    val apply = result != null
                    val r = ListTypes.importText(ctx, listId, text, apply)
                    busy = false
                    if (r == null) android.widget.Toast.makeText(ctx, ctx.getString(R.string.save_failed), android.widget.Toast.LENGTH_LONG).show()
                    else if (apply) onClose() else result = r
                }
            }) { Text(stringResource(if (result == null) R.string.check_import else R.string.apply)) }
        },
        dismissButton = { androidx.compose.material3.TextButton(onClick = onClose) { Text(stringResource(R.string.cancel)) } }
    )
}
