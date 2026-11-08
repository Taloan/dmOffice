package at.dimes.dmoffice

import android.Manifest
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.launch
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.runtime.setValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.isImeVisible
import androidx.compose.foundation.layout.navigationBars
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.compose.rememberNavController
import androidx.navigation.navArgument
import at.dimes.dmoffice.data.Account
import at.dimes.dmoffice.sync.SyncWorker
import at.dimes.dmoffice.ui.AppTheme
import at.dimes.dmoffice.ui.EventEditor
import at.dimes.dmoffice.ui.HomeScreen
import at.dimes.dmoffice.ui.ItemRouter
import at.dimes.dmoffice.ui.SetupScreen
import at.dimes.dmoffice.ui.TaskEditor
import java.net.URLDecoder
import java.net.URLEncoder

class MainActivity : FragmentActivity() {
    /** Von Widget oder Benachrichtigung geoeffnetes Objekt bzw. gewuenschter Reiter. */
    private val pendingHref = mutableStateOf<String?>(null)
    private val pendingTab = mutableStateOf<String?>(null)
    private val pendingNew = mutableStateOf<String?>(null)
    private val pendingList = mutableStateOf<String?>(null)
    /** Mail aus einer Benachrichtigung oeffnen: Konto, Ordner, UID. */
    private val pendingMail = mutableStateOf<Triple<String, String, Long>?>(null)
    /** Aufgabenliste oeffnen (Titelzeile des Aufgaben-Widgets); die Zeit macht jeden Tipp neu. */
    private val pendingOpenList = mutableStateOf<Pair<String, Long>?>(null)
    private val pendingPair = mutableStateOf<Account?>(null)
    /** Aus der Update-Benachrichtigung: Hinweis zeigen bzw. gleich aktualisieren. */
    private val pendingUpdate = mutableStateOf<String?>(null)
    /** Aus einer Benachrichtigung: diese Seite oeffnen (z. B. die Upload-Warteschlange). */
    private val pendingRoute = mutableStateOf<String?>(null)

    private val notifPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    /** App-Sperre: nach dem Start und nach mehr als einer Minute im Hintergrund wieder sperren. */
    private val locked = mutableStateOf(true)
    override fun onStop() {
        super.onStop()
        AppForeground.stopped()
    }
    override fun onStart() {
        val back = AppForeground.hidden()
        super.onStart()
        if (AppForeground.started() || AppForeground.lockPending) { AppForeground.lockPending = false; locked.value = true }
        // Coach: ungefährer Ort fürs Wetter bei Pukkis Bewegungs-Erinnerung (nur wenn eingeschaltet, höchstens alle zwei Stunden)
        lifecycleScope.launch { at.dimes.dmoffice.coach.CoachLocation.sendIfWanted(this@MainActivity) }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        // Nach Drehen o. Ä. nicht noch einmal den Eintragen-Dialog aus dem Coach-Widget öffnen
        if (savedInstanceState != null) intent?.removeExtra(EXTRA_COACH)
        consume(intent)
        setContent {
            val app = App.get(this)
            val account by app.prefs.account.collectAsState()
            AppTheme {
                val settings by app.prefs.settings.collectAsState()
                // App-Sperre: Nach dem ersten Entsperren bleibt alles darunter bestehen (offener Bereich, Chat, getippter Text),
                // die Sperre liegt nur darüber. Vor dem ersten Entsperren wird nichts aufgebaut und nichts geladen.
                val mustUnlock = settings.appLock && account != null && locked.value
                var everUnlocked by androidx.compose.runtime.saveable.rememberSaveable { androidx.compose.runtime.mutableStateOf(false) }
                if (!mustUnlock) everUnlocked = true
                if (mustUnlock && !everUnlocked) { at.dimes.dmoffice.ui.LockScreen(this) { locked.value = false }; return@AppTheme }
                androidx.compose.foundation.layout.Box(androidx.compose.ui.Modifier.fillMaxSize()) {
                androidx.compose.foundation.layout.Box(androidx.compose.ui.Modifier.fillMaxSize().then(if (mustUnlock) androidx.compose.ui.Modifier.clearAndSetSemantics {} else androidx.compose.ui.Modifier)) {
                val nav = rememberNavController()
                at.dimes.dmoffice.ui.UpdateDialog()
                // Bereichssperre: derselbe Entsperr-Dialog für jede Antwort 423
                if (account != null) at.dimes.dmoffice.ui.TresorHost()
                val upd = pendingUpdate.value
                LaunchedEffect(upd) { if (upd != null) { pendingUpdate.value = null; at.dimes.dmoffice.update.Updater.fromNotification(this@MainActivity, upd == UPDATE_INSTALL) } }
                // Seite aus einer Benachrichtigung: auch wenn gerade eine andere Seite offen ist
                val route = pendingRoute.value
                LaunchedEffect(route, account) { if (route != null && account != null) { pendingRoute.value = null; nav.navigate(route) { launchSingleTop = true } } }
                // Seiten aus einem Reiter (Chat, Tagebucheintrag, Notiz) behalten die Leiste unten; ein Reiter führt zurück zum Hauptbildschirm.
                // Mit offener Tastatur weg (sie stünde sonst über dem Eingabefeld)
                @androidx.compose.runtime.Composable
                fun WithTabs(selected: at.dimes.dmoffice.ui.Tab, content: @androidx.compose.runtime.Composable () -> Unit) {
                    @OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
                    val ime = androidx.compose.foundation.layout.WindowInsets.isImeVisible
                    androidx.compose.foundation.layout.Column(androidx.compose.ui.Modifier.fillMaxSize()) {
                        androidx.compose.foundation.layout.Box(androidx.compose.ui.Modifier.weight(1f)
                            .then(if (ime) androidx.compose.ui.Modifier else androidx.compose.ui.Modifier.consumeWindowInsets(androidx.compose.foundation.layout.WindowInsets.navigationBars))) { content() }
                        if (!ime) at.dimes.dmoffice.ui.AppTabBar(selected) { t -> pendingTab.value = t.route + "#" + System.nanoTime(); nav.popBackStack("home", false) }
                    }
                }
                NavHost(navController = nav, startDestination = if (account == null) "setup" else "home") {
                    composable("setup") {
                        val done = {
                            pendingPair.value = null
                            at.dimes.dmoffice.ui.TabBar.state.value = null
                            SyncWorker.schedule(this@MainActivity)
                            SyncWorker.now(this@MainActivity)
                            nav.navigate("home") { popUpTo("setup") { inclusive = true } }
                        }
                        SetupScreen(initial = pendingPair.value, onDone = done)
                    }
                    composable("home") {
                        LaunchedEffect(Unit) {
                            if (Build.VERSION.SDK_INT >= 33) notifPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                            at.dimes.dmoffice.update.Updater.checkOnStart(this@MainActivity)
                        }
                        val rejected by at.dimes.dmoffice.api.AuthLost.rejected.collectAsState()
                        androidx.compose.foundation.layout.Box(androidx.compose.ui.Modifier.fillMaxSize()) {
                        HomeScreen(
                            initialTab = pendingTab.value,
                            openList = pendingOpenList.value,
                            onOpen = { href -> nav.navigate("item/${enc(href)}") },
                            onNewEvent = { startMs -> nav.navigate("event?start=$startMs") },
                            onNewTask = { nav.navigate("task") },
                            onNavigate = { route -> nav.navigate(route) },
                            onSignedOut = { nav.navigate("setup") { popUpTo("home") { inclusive = true } } },
                        )
                        // App-Passwort abgelehnt: Hinweis mit „Neu verbinden“
                        if (rejected) at.dimes.dmoffice.ui.AuthLostBanner(
                            androidx.compose.ui.Modifier.align(androidx.compose.ui.Alignment.BottomCenter)) { nav.navigate("reconnect") }
                        }
                        val mail = pendingMail.value
                        LaunchedEffect(mail) { if (mail != null) { pendingMail.value = null; nav.navigate("message/" + enc(mail.first) + "/" + enc(mail.second) + "/" + mail.third) } }
                        val href = pendingHref.value
                        LaunchedEffect(href) { if (href != null) { pendingHref.value = null; nav.navigate("item/${enc(href)}") } }
                        val newKind = pendingNew.value
                        LaunchedEffect(newKind) { if (newKind != null) { pendingNew.value = null
                            val list = pendingList.value; pendingList.value = null
                            nav.navigate(if (newKind == "task") "task?list=" + enc(list ?: "") else "event?start=0") } }
                    }
                    // Neu verbinden (App-Passwort abgelehnt, etwa gelöscht): wie die Einrichtung, aber ohne Abmelden - Einstellungen,
                    // Auto-Upload und Zwischenspeicher bleiben; Adresse und Anmeldename stehen schon drin
                    composable("reconnect") {
                        val acc = app.prefs.account.value
                        at.dimes.dmoffice.ui.SetupScreen(
                            initial = acc?.let { at.dimes.dmoffice.data.Account(baseUrl = it.baseUrl, login = it.login, token = "", displayName = it.displayName) },
                            onDone = { SyncWorker.now(this@MainActivity); nav.popBackStack() })
                    }
                    composable("item/{href}", arguments = listOf(navArgument("href") { type = NavType.StringType })) { back ->
                        ItemRouter(href = dec(back.arguments?.getString("href") ?: ""), onClose = { nav.popBackStack() })
                    }
                    composable("event?start={start}", arguments = listOf(navArgument("start") { type = NavType.LongType; defaultValue = 0L })) { back ->
                        EventEditor(href = null, startMs = back.arguments?.getLong("start") ?: 0L, onClose = { nav.popBackStack() })
                    }
                    composable("task?list={list}", arguments = listOf(navArgument("list") { type = NavType.StringType; defaultValue = "" })) { back ->
                        TaskEditor(href = null, list = dec(back.arguments?.getString("list") ?: "").ifEmpty { null }, onClose = { nav.popBackStack() })
                    }
                    composable("settings") { at.dimes.dmoffice.ui.SettingsScreen(onSignedOut = { nav.navigate("setup") { popUpTo("home") { inclusive = true } } }, onClose = { nav.popBackStack() }, onNavigate = { nav.navigate(it) }) }
                    composable("contacts") { at.dimes.dmoffice.ui.ContactsScreen(onOpen = { id -> nav.navigate("contact/${enc(id)}") }, onClose = { nav.popBackStack() }, onNew = { nav.navigate("contactedit") }) }
                    composable("contact/{id}", arguments = listOf(navArgument("id") { type = NavType.StringType })) { back ->
                        at.dimes.dmoffice.ui.ContactScreen(id = dec(back.arguments?.getString("id") ?: ""), onClose = { nav.popBackStack() },
                            onMail = { addr -> val acc = app.prefs.settings.value.mailAccount; if (acc != null) nav.navigate("compose/" + at.dimes.dmoffice.ui.ComposeInit(acc, to = addr).encode()) },
                            onEdit = { id -> nav.navigate("contactedit?id=${enc(id)}") })
                    }
                    // Kontakt anlegen bzw. bearbeiten (Version 2); nach dem Anlegen gleich die Karte
                    composable("contactedit?id={id}", arguments = listOf(navArgument("id") { type = NavType.StringType; nullable = true; defaultValue = null })) { back ->
                        val editId = back.arguments?.getString("id")?.let { dec(it) }
                        at.dimes.dmoffice.ui.ContactEditScreen(editId, onClose = { nav.popBackStack() }, onSaved = { saved ->
                            nav.popBackStack()
                            if (editId == null) nav.navigate("contact/${enc(saved)}")
                        })
                    }
                    composable("files") { at.dimes.dmoffice.ui.FilesScreen(onClose = { nav.popBackStack() }, onAutoUpload = { nav.navigate("autoupload") }) }
                    composable("autoupload") { at.dimes.dmoffice.ui.AutoUploadScreen(onRule = { id -> nav.navigate("autoupload/rule/$id") }, onQueue = { nav.navigate("autoupload/queue") }, onFreeUp = { nav.navigate("autoupload/free") }, onClose = { nav.popBackStack() }) }
                    composable("autoupload/rule/{id}", arguments = listOf(navArgument("id") { type = NavType.LongType })) { back ->
                        at.dimes.dmoffice.ui.UploadRuleScreen(id = back.arguments?.getLong("id") ?: 0L, onClose = { nav.popBackStack() })
                    }
                    composable("autoupload/queue") { at.dimes.dmoffice.ui.UploadQueueScreen(onClose = { nav.popBackStack() }) }
                    composable("autoupload/free") { at.dimes.dmoffice.ui.FreeUpScreen(onClose = { nav.popBackStack() }) }
                    // Chat (Version 2): eine Unterhaltung (auch aus der Benachrichtigung, routeIntent "chat/<id>")
                    composable("chatsettings") { at.dimes.dmoffice.ui.ChatSettingsScreen(onClose = { nav.popBackStack() }) }
                    composable("chat/{id}?focus={focus}", arguments = listOf(navArgument("id") { type = NavType.LongType }, navArgument("focus") { type = NavType.LongType; defaultValue = -1L })) { back ->
                        // focus: aus der Suche zur Nachricht springen
                        WithTabs(at.dimes.dmoffice.ui.Tab.Chat) { at.dimes.dmoffice.ui.ChatRoomScreen(roomId = back.arguments?.getLong("id") ?: 0L, focus = back.arguments?.getLong("focus")?.takeIf { it > 0 }, onClose = { nav.popBackStack() },
                            // Mail-Adresse im Chat angetippt: neue Mail an sie
                            onMail = { addr -> val acc = app.prefs.settings.value.mailAccount; if (acc != null) nav.navigate("compose/" + at.dimes.dmoffice.ui.ComposeInit(acc, to = addr).encode()) },
                            // Gruppenraum (Stufe 4): Kalender und Liste als Reiter, Ordner und Notizen als eigene Seiten
                            onOpenSpace = { kind, _ ->
                                when (kind) {
                                    "folder" -> nav.navigate("files")
                                    "notes" -> nav.navigate("notes")
                                    else -> { pendingTab.value = (if (kind == "calendar") "calendar" else "tasks") + "#" + System.nanoTime(); nav.popBackStack("home", false) }
                                }
                            }) }
                    }
                    composable("notes") { at.dimes.dmoffice.ui.NotesScreen(onOpen = { id -> nav.navigate(App.get(this@MainActivity).noteRoute(id)) }, onClose = { nav.popBackStack() }) }
                    // Tagebuch (Version 2): neuer Eintrag bzw. Eintrag bearbeiten
                    // Neu - auch aus dem Teilen-Menü (share: Fotos im Cache, journal: Ziel)
                    // start: aus den Widgets - „camera“ öffnet gleich die Kamera, „new“ übernimmt gleich den Standort
                    composable("journal/new?share={share}&journal={journal}&start={start}&day={day}", arguments = listOf(
                        navArgument("share") { type = NavType.StringType; nullable = true; defaultValue = null },
                        navArgument("journal") { type = NavType.LongType; defaultValue = -1L },
                        navArgument("start") { type = NavType.StringType; nullable = true; defaultValue = null },
                        navArgument("day") { type = NavType.StringType; nullable = true; defaultValue = null })) { back ->
                        at.dimes.dmoffice.ui.JournalEditorScreen(journal = back.arguments?.getLong("journal")?.takeIf { it >= 0 }, uri = null,
                            shareBatch = back.arguments?.getString("share"), start = back.arguments?.getString("start"), day = back.arguments?.getString("day"),
                            onClose = { if (!nav.popBackStack()) nav.navigate("home") })
                    }
                    // Lesen (Tipp in der Zeitleiste), daraus Bearbeiten
                    composable("journal-view/{journal}/{uri}", arguments = listOf(navArgument("journal") { type = NavType.LongType }, navArgument("uri") { type = NavType.StringType })) { back ->
                        WithTabs(at.dimes.dmoffice.ui.Tab.Journal) { at.dimes.dmoffice.ui.JournalEntryScreen(journal = back.arguments?.getLong("journal") ?: 0L, uri = back.arguments?.getString("uri") ?: "",
                            onEdit = { j, u -> nav.navigate("journal/$j/" + android.net.Uri.encode(u)) }, onClose = { nav.popBackStack() }) }
                    }
                    composable("journal/{journal}/{uri}", arguments = listOf(navArgument("journal") { type = NavType.LongType }, navArgument("uri") { type = NavType.StringType })) { back ->
                        at.dimes.dmoffice.ui.JournalEditorScreen(journal = back.arguments?.getLong("journal"), uri = back.arguments?.getString("uri"), onClose = { nav.popBackStack() })
                    }
                    composable("note/{id}?vault={vault}", arguments = listOf(navArgument("id") { type = NavType.LongType },
                        navArgument("vault") { type = NavType.StringType; nullable = true; defaultValue = null })) { back ->
                        val vault = back.arguments?.getString("vault")
                        WithTabs(at.dimes.dmoffice.ui.Tab.Notes) { at.dimes.dmoffice.ui.NoteScreen(id = back.arguments?.getLong("id") ?: 0L, vault = vault, onClose = { nav.popBackStack() },
                            onNext = { next -> nav.navigate("note/$next" + (vault?.let { "?vault=" + android.net.Uri.encode(it) } ?: "")) { popUpTo("note/{id}?vault={vault}") { inclusive = true } } }) }
                    }
                    composable("message/{acc}/{folder}/{uid}", arguments = listOf(navArgument("acc") { type = NavType.StringType }, navArgument("folder") { type = NavType.StringType }, navArgument("uid") { type = NavType.LongType })) { back ->
                        at.dimes.dmoffice.ui.MessageScreen(account = dec(back.arguments?.getString("acc") ?: ""), folder = dec(back.arguments?.getString("folder") ?: ""), uid = back.arguments?.getLong("uid") ?: 0L,
                            onClose = { nav.popBackStack() }, onCompose = { init -> nav.navigate("compose/" + init.encode()) })
                    }
                    composable("compose/{init}", arguments = listOf(navArgument("init") { type = NavType.StringType })) { back ->
                        at.dimes.dmoffice.ui.ComposeScreen(init = at.dimes.dmoffice.ui.ComposeInit.decode(back.arguments?.getString("init") ?: ""), onClose = { nav.popBackStack() })
                    }
                }
                }
                if (mustUnlock) {
                    // Die Tastatur bleibt nicht über der Sperre, Zurück führt aus der App und nicht in die Seite darunter
                    val keyboard = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
                    LaunchedEffect(Unit) { keyboard?.hide() }
                    androidx.activity.compose.BackHandler { moveTaskToBack(true) }
                    at.dimes.dmoffice.ui.LockScreen(this@MainActivity) { locked.value = false }
                }
                }
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        consume(intent)
    }

    private fun consume(intent: Intent?) {
        intent ?: return
        intent.getStringExtra(EXTRA_HREF)?.let { pendingHref.value = it }
        intent.getStringExtra(EXTRA_TAB)?.let { pendingTab.value = it }
        pendingList.value = intent.getStringExtra(EXTRA_LIST)
        intent.getStringExtra(EXTRA_MAIL)?.split('\n')?.takeIf { it.size == 3 }?.let { p -> p[2].toLongOrNull()?.let { pendingMail.value = Triple(p[0], p[1], it) } }
        if (intent.getStringExtra(EXTRA_NEW) == null) pendingList.value?.let { pendingOpenList.value = it to System.nanoTime() }
        intent.getStringExtra(EXTRA_NEW)?.let { pendingNew.value = it }
        intent.getStringExtra(EXTRA_UPDATE)?.let { pendingUpdate.value = it }
        // Coach-Widget: „+“ oder Barcode - in den Coach-Reiter (jedes Mal, daher eindeutig) und dort eintragen
        intent.getStringExtra(EXTRA_COACH)?.takeIf { it == "add" || it == "scan" || it == "photo" || it == "weight" }?.let {
            at.dimes.dmoffice.coach.CoachLaunch.request.value = it
            pendingTab.value = "coach#" + System.nanoTime()
        }
        // EXTRA_ROUTE kommt aus eigenen Benachrichtigungen (z. B. "autoupload/queue"). Die Activity ist exportiert,
        // also koennte eine fremde App hier eine Route unterschieben. compose/message wuerden einen vorbefuellten
        // (Weiterleitungs-)Entwurf oder eine bestimmte Mail oeffnen - das von aussen nie zulassen.
        intent.getStringExtra(EXTRA_ROUTE)?.let { r -> if (!r.startsWith("compose") && !r.startsWith("message")) pendingRoute.value = r }
        if (intent.action == Intent.ACTION_VIEW) intent.data?.let { Account.fromPairUri(it)?.let { a -> pendingPair.value = a } }
    }

    companion object {
        const val EXTRA_HREF = "href"
        const val EXTRA_TAB = "tab"
        const val EXTRA_NEW = "new"
        const val EXTRA_LIST = "list"
        const val EXTRA_MAIL = "mail"
        const val EXTRA_UPDATE = "update"
        const val EXTRA_ROUTE = "route"
        const val EXTRA_COACH = "coach"
        const val UPDATE_SHOW = "show"
        const val UPDATE_INSTALL = "install"

        /** Intent der Update-Benachrichtigung: Hinweis zeigen oder ([install]) gleich laden und installieren. */
        fun updateIntent(context: Context, install: Boolean): Intent =
            Intent(context, MainActivity::class.java).setAction(Intent.ACTION_MAIN)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(EXTRA_UPDATE, if (install) UPDATE_INSTALL else UPDATE_SHOW)
                .setData(android.net.Uri.parse("dmoffice://update/" + if (install) UPDATE_INSTALL else UPDATE_SHOW))

        /** Intent, der eine Seite der App oeffnet (Benachrichtigungen des automatischen Hochladens, Tagebuch-Widgets). */
        fun routeIntent(context: Context, route: String): Intent =
            Intent(context, MainActivity::class.java).setAction(Intent.ACTION_MAIN)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(EXTRA_ROUTE, route)
                .setData(android.net.Uri.parse("dmoffice://route/" + enc(route)))

        /** Intent des Coach-Widgets: [action] „add“ (Eintragen) oder „scan“ (gleich den Barcode-Scanner). */
        fun coachIntent(context: Context, action: String): Intent =
            Intent(context, MainActivity::class.java).setAction(Intent.ACTION_MAIN)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(EXTRA_COACH, action)
                .setData(android.net.Uri.parse("dmoffice://coach/$action"))

        /** Intent fuer Mail-Benachrichtigungen: oeffnet die Mail direkt. */
        fun mailIntent(context: Context, account: String, folder: String, uid: Long): Intent =
            Intent(context, MainActivity::class.java).setAction(Intent.ACTION_MAIN)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(EXTRA_MAIL, account + "\n" + folder + "\n" + uid)
                .setData(android.net.Uri.parse("dmoffice://mail/" + enc(account) + "/" + uid))

        /** Intent fuer Widgets und Benachrichtigungen; die Daten-URI macht jeden PendingIntent eindeutig.
         *  [list]: neue Aufgabe gleich in dieser Aufgabenliste (Aufgaben-Widget). */
        fun openIntent(context: Context, href: String?, tab: String? = null, create: String? = null, list: String? = null): Intent =
            Intent(context, MainActivity::class.java).setAction(Intent.ACTION_MAIN)
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                .putExtra(EXTRA_HREF, href).putExtra(EXTRA_TAB, tab).putExtra(EXTRA_NEW, create).putExtra(EXTRA_LIST, list)
                .setData(android.net.Uri.parse("dmoffice://open/" + enc(href ?: tab ?: "") + (create?.let { "/new/$it" } ?: "") + (list?.let { "/list/" + enc(it) } ?: "")))

        fun enc(s: String): String = URLEncoder.encode(s, "UTF-8")
        fun dec(s: String): String = URLDecoder.decode(s, "UTF-8")
    }
}

/**
 * Fuer die App-Sperre: ist die App (irgendeine eigene Activity, z. B. der Office-Editor) sichtbar? Ein Wechsel
 * zwischen eigenen Activities zaehlt nicht als "im Hintergrund".
 */
internal object AppForeground {
    private var visible = 0
    private var hiddenAt = 0L
    /** Der Office-Editor wurde nach langer Pause geschlossen: MainActivity soll sperren. */
    @Volatile var lockPending = false

    /** Aus onStart; true, wenn die App vorher laenger als eine Minute ganz im Hintergrund war. */
    @Synchronized fun started(): Boolean {
        val away = visible == 0 && hiddenAt > 0 && System.currentTimeMillis() - hiddenAt > 60_000
        visible++
        return away
    }

    /** Keine eigene Activity sichtbar. */
    @Synchronized fun hidden(): Boolean = visible == 0

    @Synchronized fun stopped() {
        visible = (visible - 1).coerceAtLeast(0)
        if (visible == 0) hiddenAt = System.currentTimeMillis()
    }
}
