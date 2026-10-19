package at.dimes.dmoffice

import android.Manifest
import android.content.Context
import android.content.Intent
import android.os.Build
import android.os.Bundle
import androidx.fragment.app.FragmentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
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

    private val notifPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    /** App-Sperre: nach dem Start und nach mehr als einer Minute im Hintergrund wieder sperren. */
    private val locked = mutableStateOf(true)
    private var hiddenAt = 0L
    override fun onStop() { super.onStop(); hiddenAt = System.currentTimeMillis() }
    override fun onStart() { super.onStart(); if (hiddenAt > 0 && System.currentTimeMillis() - hiddenAt > 60_000) locked.value = true }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        consume(intent)
        setContent {
            val app = App.get(this)
            val account by app.prefs.account.collectAsState()
            AppTheme {
                val settings by app.prefs.settings.collectAsState()
                if (settings.appLock && account != null && locked.value) { at.dimes.dmoffice.ui.LockScreen(this) { locked.value = false }; return@AppTheme }
                val nav = rememberNavController()
                NavHost(navController = nav, startDestination = if (account == null) "setup" else "home") {
                    composable("setup") {
                        SetupScreen(initial = pendingPair.value, onDone = {
                            pendingPair.value = null
                            SyncWorker.schedule(this@MainActivity)
                            SyncWorker.now(this@MainActivity)
                            nav.navigate("home") { popUpTo("setup") { inclusive = true } }
                        })
                    }
                    composable("home") {
                        LaunchedEffect(Unit) {
                            if (Build.VERSION.SDK_INT >= 33) notifPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                        }
                        HomeScreen(
                            initialTab = pendingTab.value,
                            openList = pendingOpenList.value,
                            onOpen = { href -> nav.navigate("item/${enc(href)}") },
                            onNewEvent = { startMs -> nav.navigate("event?start=$startMs") },
                            onNewTask = { nav.navigate("task") },
                            onNavigate = { route -> nav.navigate(route) },
                        )
                        val mail = pendingMail.value
                        LaunchedEffect(mail) { if (mail != null) { pendingMail.value = null; nav.navigate("message/" + enc(mail.first) + "/" + enc(mail.second) + "/" + mail.third) } }
                        val href = pendingHref.value
                        LaunchedEffect(href) { if (href != null) { pendingHref.value = null; nav.navigate("item/${enc(href)}") } }
                        val newKind = pendingNew.value
                        LaunchedEffect(newKind) { if (newKind != null) { pendingNew.value = null
                            val list = pendingList.value; pendingList.value = null
                            nav.navigate(if (newKind == "task") "task?list=" + enc(list ?: "") else "event?start=0") } }
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
                    composable("settings") { at.dimes.dmoffice.ui.SettingsScreen(onSignedOut = { nav.navigate("setup") { popUpTo("home") { inclusive = true } } }, onClose = { nav.popBackStack() }) }
                    composable("contacts") { at.dimes.dmoffice.ui.ContactsScreen(onOpen = { id -> nav.navigate("contact/${enc(id)}") }, onClose = { nav.popBackStack() }) }
                    composable("contact/{id}", arguments = listOf(navArgument("id") { type = NavType.StringType })) { back ->
                        at.dimes.dmoffice.ui.ContactScreen(id = dec(back.arguments?.getString("id") ?: ""), onClose = { nav.popBackStack() },
                            onMail = { addr -> val acc = app.prefs.settings.value.mailAccount; if (acc != null) nav.navigate("compose/" + at.dimes.dmoffice.ui.ComposeInit(acc, to = addr).encode()) })
                    }
                    composable("notes") { at.dimes.dmoffice.ui.NotesScreen(onOpen = { id -> nav.navigate("note/$id") }, onClose = { nav.popBackStack() }) }
                    composable("note/{id}", arguments = listOf(navArgument("id") { type = NavType.LongType })) { back ->
                        at.dimes.dmoffice.ui.NoteScreen(id = back.arguments?.getLong("id") ?: 0L, onClose = { nav.popBackStack() })
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
        if (intent.action == Intent.ACTION_VIEW) intent.data?.let { Account.fromPairUri(it)?.let { a -> pendingPair.value = a } }
    }

    companion object {
        const val EXTRA_HREF = "href"
        const val EXTRA_TAB = "tab"
        const val EXTRA_NEW = "new"
        const val EXTRA_LIST = "list"
        const val EXTRA_MAIL = "mail"

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
