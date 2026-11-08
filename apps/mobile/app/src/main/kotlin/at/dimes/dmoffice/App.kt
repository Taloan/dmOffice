package at.dimes.dmoffice

import android.app.Application
import android.content.Context
import androidx.work.Configuration
import at.dimes.dmoffice.data.Account
import at.dimes.dmoffice.data.AppDb
import at.dimes.dmoffice.data.Prefs
import at.dimes.dmoffice.data.Repo
import at.dimes.dmoffice.notify.Reminders
import at.dimes.dmoffice.sync.SyncWorker

class App : Application(), Configuration.Provider {
    val prefs by lazy { Prefs(this) }
    val db by lazy { AppDb.get(this) }
    val repo by lazy { Repo(this, db) }
    val mail by lazy { at.dimes.dmoffice.mail.MailRepo(this, db) }
    val contacts by lazy { at.dimes.dmoffice.data.ContactsRepo(this, db) }
    val notes by lazy { at.dimes.dmoffice.data.NotesRepo(this, db) }
    /** Notizen einer Datenbank im Tresor, solange sie gezeigt wird (Notizenliste und Editor); sonst null. */
    @Volatile var vaultNotes: at.dimes.dmoffice.data.NotesRepo? = null
    /** Notizen, die Liste und Editor gerade zeigen: die im Tresor oder die gewählte Datenbank. */
    val shownNotes: at.dimes.dmoffice.data.NotesRepo get() = vaultNotes ?: notes
    /** Weg zu einer Notiz der gerade gezeigten Datenbank - im Tresor mit ihrem Namen, sonst die gewöhnliche Adresse. */
    fun noteRoute(id: Long): String = "note/$id" + (vaultNotes?.vault?.let { "?vault=" + android.net.Uri.encode(it) } ?: "")
    /** Notizen für den Editor: im Tresor nur, wenn der Weg die Datenbank ausdrücklich nennt (Notizen aus Chat oder Übersicht sind nie im Tresor). */
    fun notesFor(vault: String?): at.dimes.dmoffice.data.NotesRepo =
        if (vault == null) notes else vaultNotes?.takeIf { it.vault == vault } ?: at.dimes.dmoffice.data.NotesRepo(this, db, vault)
    val files by lazy { at.dimes.dmoffice.files.FilesRepo(this) }
    val chat by lazy { at.dimes.dmoffice.chat.ChatRepo(this) }
    val journal by lazy { at.dimes.dmoffice.journal.JournalRepo(this) }
    val coach by lazy { at.dimes.dmoffice.coach.CoachRepo(this) }
    val dashboard by lazy { at.dimes.dmoffice.dashboard.DashboardRepo(this) }
    val audio by lazy { at.dimes.dmoffice.audio.AudioRepo(this) }

    /** API-Client fuer das aktuelle Konto (null ohne Konto); wird bei Kontowechsel neu gebaut. */
    @Volatile private var apiFor: Pair<Account, at.dimes.dmoffice.api.ApiClient>? = null
    val api: at.dimes.dmoffice.api.ApiClient? get() {
        val a = prefs.account.value ?: return null
        apiFor?.let { if (it.first == a) return it.second }
        return at.dimes.dmoffice.api.ApiClient(a, prefs).also { apiFor = a to it }
    }

    override val workManagerConfiguration: Configuration
        get() = Configuration.Builder().setMinimumLoggingLevel(android.util.Log.INFO).build()

    override fun onCreate() {
        super.onCreate()
        Reminders.ensureChannel(this)
        at.dimes.dmoffice.notify.MailNotify.ensureChannels(this)
        at.dimes.dmoffice.notify.ChatNotify.ensureChannel(this)
        at.dimes.dmoffice.update.UpdateWorker.ensureChannel(this)
        at.dimes.dmoffice.update.UpdateWorker.schedule(this)
        if (prefs.account.value != null) SyncWorker.schedule(this)
        upgradeToEnvelope()
        at.dimes.dmoffice.sync.Push.init(this)
        at.dimes.dmoffice.sync.Push.register(this)
        at.dimes.dmoffice.files.FilesProvider.watchAccount(this)
        at.dimes.dmoffice.upload.AutoUpload.init(this)
        at.dimes.dmoffice.ui.TresorScreenLock.register(this)
    }

    /**
     * Konto von vor dem Umschlag: registrieren, sobald der Server /api/e2e/key anbietet - still nur mit dem Schluessel aus
     * dem QR-Code. Sonst wartet der angebotene Schluessel in [pendingServerKey], bis der Benutzer in den Einstellungen den
     * Fingerabdruck verglichen hat (ein Proxy koennte sonst seinen eigenen Schluessel unterschieben).
     */
    private fun upgradeToEnvelope() {
        val a = prefs.account.value ?: return
        if (a.encrypted) return
        Thread {
            try {
                val b = at.dimes.dmoffice.api.E2e.pair(a)
                if (b.encrypted) { prefs.setAccount(b); apiFor = null; at.dimes.dmoffice.sync.Push.register(this, force = true) }
            } catch (e: at.dimes.dmoffice.api.E2e.KeyUnconfirmed) { pendingServerKey.value = e.key }
            catch (_: Exception) { }
        }.start()
    }

    /** Vom Server angebotener, noch nicht bestaetigter Schluessel fuer den Umschlag (siehe [upgradeToEnvelope]). */
    val pendingServerKey = kotlinx.coroutines.flow.MutableStateFlow<String?>(null)

    /** Fingerabdruck verglichen: mit genau diesem Schluessel auf den Umschlag umstellen. Netzwerk, nicht im Hauptthread. */
    fun confirmServerKey(key: String) {
        val a = prefs.account.value ?: return
        if (a.encrypted) return
        val b = at.dimes.dmoffice.api.E2e.pair(a.copy(serverKey = key))
        if (!b.encrypted) return
        prefs.setAccount(b); apiFor = null; pendingServerKey.value = null
        at.dimes.dmoffice.sync.Push.register(this, force = true)
    }

    companion object {
        fun get(context: Context): App = context.applicationContext as App
        /** Für Stellen ohne Context (Tresor: „Handy gesperrt“). */
        @Volatile var lastContext: Context? = null
    }
}
