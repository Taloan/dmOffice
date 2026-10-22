package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.CalendarMonth
import androidx.compose.material.icons.filled.Checklist
import androidx.compose.material.icons.filled.Event
import androidx.compose.material.icons.filled.Email
import androidx.compose.material.icons.filled.MoreHoriz
import androidx.compose.material.icons.automirrored.filled.StickyNote2
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.ExtendedFloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material.icons.filled.Contacts
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
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

enum class Tab(val route: String, val label: Int) { Mail("mail", R.string.mail), Calendar("calendar", R.string.calendar), Tasks("tasks", R.string.tasks), Notes("notes", R.string.notes_tab), Contacts("contacts", R.string.contacts), Files("files", R.string.files), Settings("settings", R.string.settings), More("more", R.string.more) }

/** Reiter zu einer Adresse; "agenda" (alte Widgets, Benachrichtigungen) ist jetzt eine Ansicht im Kalender. */
private fun tabOf(route: String?): Tab? = if (route == "agenda" || route == "more") Tab.Calendar else Tab.entries.firstOrNull { it.route == route }

/** Hauptbildschirm mit fuenf Reitern; der Plus-Knopf legt je nach Reiter Termin oder Aufgabe an. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(initialTab: String?, onOpen: (String) -> Unit, onNewEvent: (Long) -> Unit, onNewTask: () -> Unit, onNavigate: (String) -> Unit,
               openList: Pair<String, Long>? = null, onSignedOut: () -> Unit = {}) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    var tab by rememberSaveable { mutableStateOf(tabOf(initialTab) ?: Tab.Calendar) }
    LaunchedEffect(initialTab) { tabOf(initialTab)?.let { tab = it } }
    // Aus dem Agenda-Widget: Kalender in der Ansicht Agenda
    LaunchedEffect(initialTab) { if (initialTab == "agenda") CalendarPrefs.setMode(ctx, CalMode.Agenda) }
    LaunchedEffect(openList) { if (openList != null) tab = Tab.Tasks }
    val snack = remember { SnackbarHostState() }
    // Ohne Mailkonto kein Mail-Reiter (Konten richtet man im Web ein)
    val hasMail by app.mail.hasAccounts.collectAsState()
    LaunchedEffect(Unit) { try { app.mail.accounts() } catch (_: Exception) { } }
    LaunchedEffect(hasMail) { if (!hasMail && tab == Tab.Mail) tab = Tab.Calendar }
    val settings by app.prefs.settings.collectAsState()
    var selectedDay by remember { mutableStateOf(java.time.LocalDate.now()) }

    LaunchedEffect(settings.conflictNotice) {
        if (settings.conflictNotice) {
            app.prefs.setConflictNotice(false)
            snack.showSnackbar(ctx.getString(R.string.conflict_notice))
        }
    }

    Scaffold(
        snackbarHost = { SnackbarHost(snack) },
        bottomBar = {
            // Alle Bereiche als Reiter, nur Symbole (Name als Beschreibung fuer Bedienungshilfen und langes Druecken)
            NavigationBar {
                val iconColor = androidx.compose.material3.MaterialTheme.colorScheme.primary
                val colors = androidx.compose.material3.NavigationBarItemDefaults.colors(
                    selectedIconColor = iconColor, unselectedIconColor = iconColor,
                    indicatorColor = iconColor.copy(alpha = 0.18f),
                )
                for (t in Tab.entries.filter { it != Tab.More && (it != Tab.Mail || hasMail) }) {
                    NavigationBarItem(
                        selected = tab == t, onClick = { tab = t }, label = null, alwaysShowLabel = false,
                        icon = { Icon(when (t) {
                            Tab.Mail -> Icons.Default.Email; Tab.Calendar -> Icons.Default.CalendarMonth; Tab.Tasks -> Icons.Default.Checklist
                            Tab.Notes -> Icons.AutoMirrored.Filled.StickyNote2; Tab.Contacts -> Icons.Default.Contacts; Tab.Files -> Icons.Default.Folder
                            Tab.Settings -> Icons.Default.Settings; Tab.More -> Icons.Default.MoreHoriz
                        }, contentDescription = stringResource(t.label)) },
                        colors = colors,
                    )
                }
            }
        },
        floatingActionButton = {
            when (tab) {
                // Im Essensplan traegt man ueber das Plus je Mahlzeit ein
                Tab.Calendar -> if (CalendarPrefs.mode.value != CalMode.MealPlan) ExtendedFloatingActionButton(
                    onClick = { onNewEvent(selectedDay.epochMillis()) },
                    icon = { Icon(Icons.Default.Event, contentDescription = null) }, text = { Text(stringResource(R.string.new_event)) })
                Tab.Tasks -> ExtendedFloatingActionButton(onClick = onNewTask, icon = { Icon(Icons.Default.Add, contentDescription = null) }, text = { Text(stringResource(R.string.new_task)) })
                else -> {}
            }
        }
    ) { pad ->
        Box(Modifier.padding(pad).fillMaxSize()) {
            when (tab) {
                Tab.Mail -> MailScreen(onOpen = { acc, folder, uid -> onNavigate("message/" + at.dimes.dmoffice.MainActivity.enc(acc) + "/" + at.dimes.dmoffice.MainActivity.enc(folder) + "/" + uid) },
                    onCompose = { acc -> onNavigate("compose/" + ComposeInit(acc).encode()) })
                Tab.Calendar -> CalendarScreen(selectedDay, { selectedDay = it }, onOpen, onNewEventAt = onNewEvent, onOpenNote = { id -> onNavigate("note/$id") }) { d -> onNewEvent(d.epochMillis()) }
                Tab.Tasks -> TasksScreen(onOpen, openList, onOpenMail = { acc, folder, uid ->
                    onNavigate("message/" + at.dimes.dmoffice.MainActivity.enc(acc) + "/" + at.dimes.dmoffice.MainActivity.enc(folder) + "/" + uid) })
                Tab.Notes -> NotesScreen(onOpen = { id -> onNavigate("note/$id") }, onClose = null)
                Tab.Contacts -> ContactsScreen(onOpen = { id -> onNavigate("contact/" + at.dimes.dmoffice.MainActivity.enc(id)) }, onClose = null)
                Tab.Files -> FilesScreen(onClose = null, onAutoUpload = { onNavigate("autoupload") })
                Tab.Settings -> SettingsScreen(onSignedOut = onSignedOut, onNavigate = onNavigate)
                Tab.More -> MoreScreen(onContacts = { onNavigate("contacts") }, onFiles = { onNavigate("files") }, onSettings = { onNavigate("settings") })
            }
        }
    }
}
