package at.dimes.dmoffice.sync

import android.accounts.AbstractAccountAuthenticator
import android.accounts.AccountAuthenticatorResponse
import android.accounts.AccountManager
import android.app.Service
import android.content.AbstractThreadedSyncAdapter
import android.content.ContentProviderClient
import android.content.ContentResolver
import android.content.Context
import android.content.Intent
import android.content.SyncResult
import android.os.Bundle
import android.os.IBinder
import android.provider.CalendarContract
import android.provider.ContactsContract
import android.util.Log
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R

/**
 * Android-Konto fuer dmOffice (eigener Kontotyp, in der Debug-Variante mit Suffix). Daran haengen die
 * Kalender und Kontakte in den Android-Datenbanken; mit den Sync-Adaptern meldet Android Aenderungen aus
 * Samsung-/Google-Kalender und der Kontakte-App, die dann per [SyncWorker] zum Server gehen.
 * Je dmOffice-Konto ein Android-Konto; die App kennt nur eines.
 */
object AndroidAccount {
    private const val TAG = "dmOffice.Account"

    fun type(ctx: Context): String = ctx.getString(R.string.account_type)

    /** Kontoname wie bei DAVx5: Benutzer@Server (oder die Anmeldung, wenn sie schon eine Adresse ist). */
    fun nameFor(a: at.dimes.dmoffice.data.Account): String {
        if (a.login.contains('@')) return a.login
        val host = try { java.net.URI(a.baseUrl).host } catch (_: Exception) { null }
        return if (host.isNullOrBlank()) a.login else "${a.login}@$host"
    }

    /** Das Android-Konto zum aktuellen dmOffice-Konto, falls schon angelegt. */
    fun current(ctx: Context): android.accounts.Account? {
        val a = App.get(ctx).prefs.account.value ?: return null
        val name = nameFor(a)
        return AccountManager.get(ctx).getAccountsByType(type(ctx)).firstOrNull { it.name == name }
    }

    /**
     * Konto anlegen (oder das passende behalten) und Sync-Einstellungen nach den Schaltern setzen.
     * Konten anderer dmOffice-Anmeldungen fallen weg - Android loescht deren Kalender und Kontakte mit.
     */
    @Synchronized
    fun ensure(ctx: Context): android.accounts.Account? {
        val app = App.get(ctx)
        val a = app.prefs.account.value ?: return null
        val am = AccountManager.get(ctx)
        val name = nameFor(a)
        var acc: android.accounts.Account? = null
        for (x in am.getAccountsByType(type(ctx))) {
            if (x.name == name) acc = x else am.removeAccountExplicitly(x)
        }
        if (acc == null) {
            acc = android.accounts.Account(name, type(ctx))
            if (!am.addAccountExplicitly(acc, null, null)) { Log.w(TAG, "addAccountExplicitly failed"); return null }
        }
        val s = app.prefs.settings.value
        configure(acc, CalendarContract.AUTHORITY, s.androidCalendar)
        configure(acc, ContactsContract.AUTHORITY, s.androidContacts)
        return acc
    }

    private fun configure(acc: android.accounts.Account, authority: String, on: Boolean) {
        ContentResolver.setIsSyncable(acc, authority, if (on) 1 else 0)
        if (on) ContentResolver.setSyncAutomatically(acc, authority, true)
    }

    /** Beide Schalter aus oder abgemeldet: Konto weg (Android entfernt Kalender und Kontakte des Kontos). */
    fun removeAll(ctx: Context) {
        val am = AccountManager.get(ctx)
        for (x in am.getAccountsByType(type(ctx))) am.removeAccountExplicitly(x)
    }

    /** Nach Schalterwechsel: Konto anlegen bzw. entfernen, Sync-Einstellungen anpassen. */
    fun update(ctx: Context) {
        val s = App.get(ctx).prefs.settings.value
        if (!s.androidCalendar && !s.androidContacts) removeAll(ctx) else ensure(ctx)
    }
}

/** Kein Anmelden ueber die Android-Einstellungen: "Konto hinzufuegen" oeffnet die App. */
class Authenticator(private val ctx: Context) : AbstractAccountAuthenticator(ctx) {
    override fun addAccount(response: AccountAuthenticatorResponse?, accountType: String?, authTokenType: String?, requiredFeatures: Array<out String>?, options: Bundle?): Bundle =
        Bundle().apply { putParcelable(AccountManager.KEY_INTENT, Intent(ctx, MainActivity::class.java)) }
    override fun editProperties(response: AccountAuthenticatorResponse?, accountType: String?): Bundle? = null
    override fun confirmCredentials(response: AccountAuthenticatorResponse?, account: android.accounts.Account?, options: Bundle?): Bundle? = null
    override fun getAuthToken(response: AccountAuthenticatorResponse?, account: android.accounts.Account?, authTokenType: String?, options: Bundle?): Bundle? = null
    override fun getAuthTokenLabel(authTokenType: String?): String? = null
    override fun updateCredentials(response: AccountAuthenticatorResponse?, account: android.accounts.Account?, authTokenType: String?, options: Bundle?): Bundle? = null
    override fun hasFeatures(response: AccountAuthenticatorResponse?, account: android.accounts.Account?, features: Array<out String>?): Bundle =
        Bundle().apply { putBoolean(AccountManager.KEY_BOOLEAN_RESULT, false) }
}

class AuthenticatorService : Service() {
    private val authenticator by lazy { Authenticator(this) }
    override fun onBind(intent: Intent?): IBinder? = authenticator.iBinder
}

/**
 * Android meldet eine Aenderung (DIRTY) oder der Benutzer tippt "Jetzt synchronisieren": den normalen
 * Abgleich anstossen. Der holt die Aenderungen aus den Android-Datenbanken, schickt sie zum Server und
 * spiegelt danach zurueck (siehe [SyncWorker], [ProviderSync]).
 */
class DmSyncAdapter(ctx: Context) : AbstractThreadedSyncAdapter(ctx, true, false) {
    override fun onPerformSync(account: android.accounts.Account, extras: Bundle?, authority: String?, provider: ContentProviderClient?, syncResult: SyncResult?) {
        val current = AndroidAccount.current(context)
        if (current == null || current != account) return
        SyncWorker.now(context)
    }
}

open class BaseSyncService : Service() {
    private val adapter by lazy { DmSyncAdapter(applicationContext) }
    override fun onBind(intent: Intent?): IBinder? = adapter.syncAdapterBinder
}
class CalendarSyncService : BaseSyncService()
class ContactsSyncService : BaseSyncService()
