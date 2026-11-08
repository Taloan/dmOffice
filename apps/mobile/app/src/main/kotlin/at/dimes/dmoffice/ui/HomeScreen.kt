package at.dimes.dmoffice.ui

import kotlinx.coroutines.launch
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.ui.Alignment
import androidx.compose.ui.unit.dp
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.Event
import androidx.compose.material.icons.filled.Email
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.automirrored.filled.StickyNote2
import androidx.compose.material.icons.automirrored.filled.Chat
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material.icons.filled.Contacts
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R

enum class Tab(val route: String, val label: Int) { Dashboard("dashboard", R.string.dashboard), Mail("mail", R.string.mail), Chat("chat", R.string.chat), Calendar("calendar", R.string.calendar), Tasks("tasks", R.string.tasks), Notes("notes", R.string.notes_tab), Journal("journal", R.string.journal), Coach("coach", R.string.coach), Contacts("contacts", R.string.contacts), Files("files", R.string.files), Gallery("gallery", R.string.gallery), Audio("audio", R.string.audio), Dms("dms", R.string.dms_tab), Settings("settings", R.string.settings), More("more", R.string.more) }

/** Reiter zu einer Adresse; "agenda" (alte Widgets, Benachrichtigungen) ist jetzt eine Ansicht im Kalender. */
/** Ein angehaengtes "#..." (Version 2, Gruppenraum aus dem Chat) macht denselben Reiter erneut zu einer Aenderung. */
private fun tabOf(route: String?): Tab? = route?.substringBefore('#').let { r -> if (r == "agenda" || r == "more") Tab.Calendar else Tab.entries.firstOrNull { it.route == r } }

/** Hauptbildschirm mit fuenf Reitern; der Plus-Knopf legt je nach Reiter Termin oder Aufgabe an. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(initialTab: String?, onOpen: (String) -> Unit, onNewEvent: (Long) -> Unit, onNewTask: () -> Unit, onNavigate: (String) -> Unit,
               openList: Pair<String, Long>? = null, onSignedOut: () -> Unit = {}) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    var tab by rememberSaveable { mutableStateOf(tabOf(initialTab) ?: TabBar.get(ctx).first.firstOrNull() ?: Tab.Dashboard) }
    // Vom Server nachziehen: Release-Liste und Dateiablage
    LaunchedEffect(Unit) {
        try {
            val st = app.chat.authState()
            if (st.user != null && at.dimes.dmoffice.data.Features.update(ctx, st.featuresOff.toSet(), st.user.files)) TabBar.state.value = null
        } catch (_: Exception) { }
    }
    // Galerie und Tagebuch still vorladen: nach dem Start und nach längerer Pause, wenn die App wieder vorne ist
    LaunchedEffect(Unit) { Preload.run(ctx) }
    val owner = androidx.lifecycle.compose.LocalLifecycleOwner.current
    val preloadScope = androidx.compose.runtime.rememberCoroutineScope()
    androidx.compose.runtime.DisposableEffect(owner) {
        val o = androidx.lifecycle.LifecycleEventObserver { _, e -> if (e == androidx.lifecycle.Lifecycle.Event.ON_RESUME) preloadScope.launch { Preload.run(ctx) } }
        owner.lifecycle.addObserver(o)
        onDispose { owner.lifecycle.removeObserver(o) }
    }
    // Audiothek (Version 2): nur, wenn der Server sie für dieses Konto anbietet (Release-Liste, Verwaltung, Dateiablage)
    LaunchedEffect(Unit) { app.audio.checkEnabled() }
    LaunchedEffect(Unit) {
        val (order, more) = TabBar.get(ctx)
        if (tab !in order && tab !in more) tab = order.firstOrNull() ?: Tab.Chat
    }
    // Sprungziel aus Widget oder Benachrichtigung nur einmal anwenden - sonst setzt jede Rückkehr
    // zur Startseite (z. B. aus dem Tagebuch-Editor) den Reiter wieder darauf
    var handledTab by rememberSaveable { mutableStateOf<String?>(null) }
    var handledList by rememberSaveable { mutableStateOf<Long?>(null) }
    LaunchedEffect(initialTab) {
        if (initialTab == null || initialTab == handledTab) return@LaunchedEffect
        handledTab = initialTab
        tabOf(initialTab)?.let { tab = it }
        // Aus dem Agenda-Widget: Kalender in der Ansicht Agenda
        if (initialTab == "agenda") CalendarPrefs.setMode(ctx, CalMode.Agenda)
    }
    LaunchedEffect(openList) { if (openList != null && openList.second != handledList) { handledList = openList.second; tab = Tab.Tasks } }
    val snack = remember { SnackbarHostState() }
    // Ohne Mailkonto kein Mail-Reiter (Konten richtet man im Web ein)
    val hasMail by app.mail.hasAccounts.collectAsState()
    LaunchedEffect(Unit) { try { app.mail.accounts() } catch (_: Exception) { } }
    LaunchedEffect(hasMail) { if (!hasMail && tab == Tab.Mail) tab = Tab.Calendar }
    val settings by app.prefs.settings.collectAsState()
    // Chat (Version 2): Zahl der ungelesenen Nachrichten am Reiter
    val chatUnread by app.chat.unread.collectAsState()
    LaunchedEffect(Unit) { while (true) { try { app.chat.status() } catch (_: Exception) { }; kotlinx.coroutines.delay(60_000) } }
    var selectedDay by remember { mutableStateOf(java.time.LocalDate.now()) }

    LaunchedEffect(settings.conflictNotice) {
        if (settings.conflictNotice) {
            app.prefs.setConflictNotice(false)
            snack.showSnackbar(ctx.getString(R.string.conflict_notice))
        }
    }

    Scaffold(
        snackbarHost = { SnackbarHost(snack) },
        // Audiothek: laufender Titel über der Leiste
        bottomBar = { Column { AudioMiniBar(); AppTabBar(tab) { tab = it } } },
        floatingActionButton = {
            when (tab) {
                // Version 2: überall derselbe runde „+“-Knopf (Aufgaben zeigen ihn selbst)
                // Im Essensplan traegt man ueber das Plus je Mahlzeit ein
                Tab.Calendar -> if (CalendarPrefs.mode.value != CalMode.MealPlan) AddButton(stringResource(R.string.new_event)) { onNewEvent(selectedDay.epochMillis()) }
                else -> {}
            }
        }
    ) { pad ->
        Box(Modifier.padding(pad).fillMaxSize()) {
            when (tab) {
                // Übersicht (Version 2): Widgets führen in die Bereiche - Kalendertag, Reiter oder eine Seite
                Tab.Dashboard -> DashboardScreen(onTab = { tab = it }, onCalendarDay = { selectedDay = it; tab = Tab.Calendar }, onNavigate = onNavigate)
                Tab.Mail -> MailScreen(onOpen = { acc, folder, uid -> onNavigate("message/" + at.dimes.dmoffice.MainActivity.enc(acc) + "/" + at.dimes.dmoffice.MainActivity.enc(folder) + "/" + uid) },
                    onCompose = { acc -> onNavigate("compose/" + ComposeInit(acc).encode()) })
                Tab.Chat -> ChatListScreen(onOpen = { id, msg -> onNavigate(if (msg != null) "chat/$id?focus=$msg" else "chat/$id") })
                Tab.Calendar -> CalendarScreen(selectedDay, { selectedDay = it }, onOpen, onNewEventAt = onNewEvent, onOpenNote = { id -> onNavigate("note/$id") }) { d -> onNewEvent(d.epochMillis()) }
                Tab.Tasks -> TasksScreen(onOpen, openList, onNew = onNewTask, onOpenMail = { acc, folder, uid ->
                    onNavigate("message/" + at.dimes.dmoffice.MainActivity.enc(acc) + "/" + at.dimes.dmoffice.MainActivity.enc(folder) + "/" + uid) })
                Tab.Notes -> NotesScreen(onOpen = { id -> onNavigate(at.dimes.dmoffice.App.get(ctx).noteRoute(id)) }, onClose = null)
                Tab.Journal -> JournalScreen(onOpen = { j, uri -> onNavigate("journal-view/$j/" + android.net.Uri.encode(uri)) }, onNew = { onNavigate("journal/new") }, onNewDay = { d -> onNavigate("journal/new?day=$d") })
                Tab.Coach -> CoachScreen()
                Tab.Contacts -> ContactsScreen(onOpen = { id -> onNavigate("contact/" + at.dimes.dmoffice.MainActivity.enc(id)) }, onClose = null, onNew = { onNavigate("contactedit") })
                Tab.Files -> FilesScreen(onClose = null, onAutoUpload = { onNavigate("autoupload") })
                // Galerie (Version 2): alle Bilder der Dateiablage nach Datum - dieselbe Ansicht, eigener Reiter
                Tab.Gallery -> FilesScreen(onClose = null, onAutoUpload = { onNavigate("autoupload") }, gallery = true)
                // Audiothek (Version 2): Musik aus den Audiothek-Ordnern, Wiedergabe im Hintergrund
                Tab.Audio -> AudioScreen()
                Tab.Dms -> at.dimes.dmoffice.ui.dms.DmsScreen()
                Tab.Settings -> SettingsScreen(onSignedOut = onSignedOut, onNavigate = onNavigate)
                Tab.More -> MoreScreen(onContacts = { onNavigate("contacts") }, onFiles = { onNavigate("files") }, onSettings = { onNavigate("settings") })
            }
            // Version 2: Postausgang (ohne Netz Geschriebenes) beim Start anstoßen
            LaunchedEffect(Unit) {
                at.dimes.dmoffice.sync.Outbox.load(ctx)
                if (at.dimes.dmoffice.sync.Outbox.chat.value.isNotEmpty() || at.dimes.dmoffice.sync.Outbox.mail.value.isNotEmpty()) at.dimes.dmoffice.sync.Outbox.schedule(ctx)
            }
            // Version 2: oben rechts, ob der Server erreichbar ist (statt Meldungen in jedem Bereich)
            if (tab == Tab.Files || tab == Tab.Settings || tab == Tab.More) ServerBadge(Modifier.align(androidx.compose.ui.Alignment.TopEnd))
        }
    }
}

/**
 * Die Leiste unten: alle Bereiche als Reiter, nur Symbole (Name als Beschreibung für Bedienungshilfen und langes Drücken).
 * Auch auf Seiten, die aus einem Reiter geöffnet werden (Chat, Tagebucheintrag, Notiz) - dort führt ein Reiter zurück zum Hauptbildschirm.
 */
@Composable
fun AppTabBar(selected: Tab?, onTab: (Tab) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val hasMail by app.mail.hasAccounts.collectAsState()
    val audioOn by app.audio.enabled.collectAsState()
    val chatUnread by app.chat.unread.collectAsState()
    // Eigene Leiste statt NavigationBar (feste 80 dp): Höhe und Symbole nach der eingestellten Größe
    val size = TabBar.size.value ?: TabBar.size(ctx)
    androidx.compose.material3.Surface(color = androidx.compose.material3.NavigationBarDefaults.containerColor,
        tonalElevation = androidx.compose.material3.NavigationBarDefaults.Elevation) {
    androidx.compose.foundation.layout.Row(Modifier.fillMaxWidth().windowInsetsPadding(androidx.compose.material3.NavigationBarDefaults.windowInsets)
        .height(size.height.dp).selectableGroup(), verticalAlignment = Alignment.CenterVertically) {
        val iconColor = androidx.compose.material3.MaterialTheme.colorScheme.primary
        @Composable fun Item(selected: Boolean, onClick: () -> Unit, icon: @Composable () -> Unit) {
            androidx.compose.foundation.layout.Box(Modifier.weight(1f).fillMaxHeight()
                .selectable(selected, onClick = onClick, role = androidx.compose.ui.semantics.Role.Tab,
                    interactionSource = null, indication = androidx.compose.material3.ripple(bounded = false, radius = (size.icon * 1.4f).dp)),
                contentAlignment = Alignment.Center) {
                // Markierung wie bei Material: Pille hinter dem Symbol
                androidx.compose.foundation.layout.Box(Modifier.widthIn(max = (size.icon * 2.5f).dp).fillMaxWidth().padding(horizontal = 3.dp).height((size.icon * 1.3f).dp)
                    .background(if (selected) iconColor.copy(alpha = 0.18f) else androidx.compose.ui.graphics.Color.Transparent, androidx.compose.foundation.shape.CircleShape),
                    contentAlignment = Alignment.Center) {
                    androidx.compose.runtime.CompositionLocalProvider(androidx.compose.material3.LocalContentColor provides iconColor) { icon() }
                }
            }
        }
        // Version 2: Reihenfolge und Menü „Mehr“ stellt man unter Einstellungen → Leiste unten ein
        val (order, more) = TabBar.state.value ?: TabBar.get(ctx)
        val usable = order.filter { (it != Tab.Mail || hasMail) && (it != Tab.Audio || audioOn) }
        @Composable fun chatBadge(t: Tab) { if (t == Tab.Chat && chatUnread > 0) androidx.compose.material3.Badge { Text(chatUnread.toString()) } }
        for (t in usable.filter { it !in more }) {
            Item(selected == t, { onTab(t) }) {
                androidx.compose.material3.BadgedBox(badge = { chatBadge(t) }) { Icon(TabBar.icon(t), contentDescription = stringResource(t.label), modifier = Modifier.size(size.icon.dp)) }
            }
        }
        val overflow = usable.filter { it in more }
        if (overflow.isNotEmpty()) {
            var moreOpen by remember { mutableStateOf(false) }
            val current = selected?.takeIf { it in overflow }
            Item(current != null, { moreOpen = true }) {
                    androidx.compose.foundation.layout.Box {
                        // Ein Bereich aus „Mehr“ ist offen: sein Symbol statt der Punkte
                        androidx.compose.material3.BadgedBox(badge = { if (Tab.Chat in overflow) chatBadge(Tab.Chat) }) {
                            Icon(current?.let { TabBar.icon(it) } ?: Icons.Default.MoreHoriz, contentDescription = stringResource(R.string.more), modifier = Modifier.size(size.icon.dp))
                        }
                        androidx.compose.material3.DropdownMenu(expanded = moreOpen, onDismissRequest = { moreOpen = false }) {
                            for (t in overflow) androidx.compose.material3.DropdownMenuItem(
                                text = { Text(stringResource(t.label)) },
                                leadingIcon = { Icon(TabBar.icon(t), contentDescription = null, tint = iconColor) },
                                trailingIcon = { chatBadge(t) },
                                onClick = { moreOpen = false; onTab(t) })
                        }
                    }
            }
        }
    }
    }
}
