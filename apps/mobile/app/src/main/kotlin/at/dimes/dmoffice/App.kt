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
        at.dimes.dmoffice.notify.MailNotify.ensureChannel(this)
        if (prefs.account.value != null) SyncWorker.schedule(this)
        upgradeToEnvelope()
        at.dimes.dmoffice.sync.Push.init(this)
        at.dimes.dmoffice.sync.Push.register(this)
    }

    /** Konto von vor dem Umschlag: still registrieren, sobald der Server /api/e2e/key anbietet. */
    private fun upgradeToEnvelope() {
        val a = prefs.account.value ?: return
        if (a.encrypted) return
        Thread {
            try {
                val b = at.dimes.dmoffice.api.E2e.pair(a)
                if (b.encrypted) { prefs.setAccount(b); apiFor = null; at.dimes.dmoffice.sync.Push.register(this, force = true) }
            } catch (_: Exception) { }
        }.start()
    }

    companion object {
        fun get(context: Context): App = context.applicationContext as App
    }
}
