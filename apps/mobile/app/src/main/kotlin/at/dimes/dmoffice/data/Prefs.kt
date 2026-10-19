package at.dimes.dmoffice.data

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** Zugangsdaten zum dmOffice-Server. [baseUrl] ohne /dav, ohne Schraegstrich am Ende. */
data class Account(
    val baseUrl: String, val login: String, val token: String, val displayName: String,
    /** Verschluesselter Umschlag (E2e): Serverschluessel aus dem QR-Code oder beim Einrichten geholt. */
    val serverKey: String? = null,
    val deviceId: String? = null, val userId: String? = null,
    val devicePublic: String? = null, val devicePrivate: String? = null,
    val counter: Long = 0,
) {
    val calendarHome: String get() = "/dav/calendars/${encode(login)}/"
    val encrypted: Boolean get() = deviceId != null && serverKey != null && devicePrivate != null

    companion object {
        fun encode(s: String): String = java.net.URLEncoder.encode(s, "UTF-8").replace("+", "%20")

        /** Server-Adresse saeubern: Schema ergaenzen, /dav und Schraegstriche abschneiden. */
        fun normalizeUrl(input: String): String {
            var u = input.trim()
            if (u.isEmpty()) return u
            if (!u.contains("://")) u = "https://$u"
            u = u.trimEnd('/')
            u = u.removeSuffix("/dav").removeSuffix("/remote.php").trimEnd('/')
            return u
        }

        /** dmoffice://pair?v=1&url=...&user=...&token=...&name=... */
        fun fromPairUri(uri: android.net.Uri): Account? {
            if (uri.scheme != "dmoffice" || uri.host != "pair") return null
            val url = uri.getQueryParameter("url") ?: return null
            val user = uri.getQueryParameter("user") ?: return null
            val token = uri.getQueryParameter("token") ?: return null
            val name = uri.getQueryParameter("name") ?: user
            return Account(normalizeUrl(url), user, token, name, serverKey = uri.getQueryParameter("spk"))
        }
    }
}

/** Konto verschluesselt, Einstellungen im Klartext. Aenderungen als StateFlow fuer Compose. */
class Prefs(context: Context) : at.dimes.dmoffice.api.E2e.Counters {
    private val secure: SharedPreferences = try {
        val key = MasterKey.Builder(context).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build()
        EncryptedSharedPreferences.create(context, "account", key,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV, EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM)
    } catch (_: Exception) {
        // Keystore kaputt (selten nach Backups): neu anfangen statt abstuerzen
        context.deleteSharedPreferences("account")
        context.getSharedPreferences("account_plain", Context.MODE_PRIVATE)
    }
    private val plain: SharedPreferences = context.getSharedPreferences("settings", Context.MODE_PRIVATE)

    private val _account = MutableStateFlow(readAccount())
    val account: StateFlow<Account?> = _account

    private val _settings = MutableStateFlow(readSettings())
    val settings: StateFlow<Settings> = _settings

    private fun readAccount(): Account? {
        val url = secure.getString("url", null) ?: return null
        return Account(url, secure.getString("login", "")!!, secure.getString("token", "")!!, secure.getString("name", "")!!,
            serverKey = secure.getString("serverKey", null), deviceId = secure.getString("deviceId", null), userId = secure.getString("userId", null),
            devicePublic = secure.getString("devicePublic", null), devicePrivate = secure.getString("devicePrivate", null), counter = secure.getLong("counter", 0))
    }

    // Zaehler des Umschlags: eine Quelle fuer alle Clients der App, streng steigend, gespeichert ueber Neustarts
    private val counterLock = Any()
    private var counterDevice: String? = null
    private var counterValue = 0L

    private fun counterFor(deviceId: String): Long {
        if (counterDevice != deviceId) {
            counterDevice = deviceId
            counterValue = if (secure.getString("deviceId", null) == deviceId) secure.getLong("counter", 0) else 0
        }
        return counterValue
    }

    override fun next(deviceId: String): Long = synchronized(counterLock) {
        counterValue = counterFor(deviceId) + 1
        if (secure.getString("deviceId", null) == deviceId) secure.edit().putLong("counter", counterValue).apply()
        counterValue
    }

    override fun skip(deviceId: String, by: Long): Unit = synchronized(counterLock) {
        counterValue = counterFor(deviceId) + by
        if (secure.getString("deviceId", null) == deviceId) secure.edit().putLong("counter", counterValue).apply()
    }

    /** Zuletzt beim Server angemeldetes Push-Token (Geraet|Token), damit nicht bei jedem Start neu. */
    var pushRegistered: String?
        get() = plain.getString("pushRegistered", null)
        set(v) { plain.edit().putString("pushRegistered", v).apply() }

    fun setAccount(a: Account?) {
        // Gleiches Geraet: nie hinter den schon benutzten Zaehler zurueckfallen (Einrichtung zaehlt schon mit)
        val counter = synchronized(counterLock) {
            val c = if (a?.deviceId != null && a.deviceId == counterDevice) maxOf(a.counter, counterValue) else a?.counter ?: 0
            counterDevice = a?.deviceId; counterValue = c; c
        }
        secure.edit().apply {
            if (a == null) clear() else {
                putString("url", a.baseUrl); putString("login", a.login); putString("token", a.token); putString("name", a.displayName)
                putString("serverKey", a.serverKey); putString("deviceId", a.deviceId); putString("userId", a.userId)
                putString("devicePublic", a.devicePublic); putString("devicePrivate", a.devicePrivate); putLong("counter", counter)
            }
        }.apply()
        _account.value = a
    }

    data class Settings(
        val lastSync: Long,
        val lastError: String?,
        val showCompleted: Boolean,
        val defaultReminderMinutes: Int,
        val conflictNotice: Boolean,
        val agendaMultiDay: MultiDay,
        val agendaShowTasks: Boolean,
        val agendaDays: Int,
        val appLock: Boolean,
        val mailAccount: String?,
        val androidCalendar: Boolean,
        val androidContacts: Boolean,
        /** Akzentfarbe als ARGB, Vorgabe Violett (ui/Theme.kt). */
        val accent: Long,
        /** Benachrichtigung bei neuen Mails: "off", "all", "vip" (nur von VIP-Kontakten). */
        val mailNotify: String,
        /** Textauszug der Mail in der Benachrichtigung. */
        val mailPreview: Boolean,
        /** Sperrbildschirm: "show" (mit Inhalt) oder "hide" (nur "Neue E-Mail"). */
        val mailLockscreen: String,
        /** Wischgesten in der Mailliste (SwipeAction.key): nach links / rechts, jeweils kurz und weit. */
        val swipeLeft: String,
        val swipeLeftFar: String,
        val swipeRight: String,
        val swipeRightFar: String,
    )

    private fun readSettings() = Settings(
        lastSync = plain.getLong("lastSync", 0),
        lastError = plain.getString("lastError", null),
        showCompleted = plain.getBoolean("showCompleted", false),
        defaultReminderMinutes = plain.getInt("defaultReminder", 15),
        conflictNotice = plain.getBoolean("conflict", false),
        agendaMultiDay = try { MultiDay.valueOf(plain.getString("agendaMulti", null) ?: "Every") } catch (_: Exception) { MultiDay.Every },
        agendaShowTasks = plain.getBoolean("agendaTasks", true),
        agendaDays = plain.getInt("agendaDays", 90),
        appLock = plain.getBoolean("appLock", false),
        mailAccount = plain.getString("mailAccount", null),
        androidCalendar = plain.getBoolean("androidCalendar", false),
        androidContacts = plain.getBoolean("androidContacts", false),
        accent = plain.getLong("accent", at.dimes.dmoffice.ui.DEFAULT_ACCENT),
        mailNotify = plain.getString("mailNotify", null) ?: "all",
        mailPreview = plain.getBoolean("mailPreview", true),
        mailLockscreen = plain.getString("mailLockscreen", null) ?: "show",
        swipeLeft = plain.getString("swipeLeft", null) ?: "delete",
        swipeLeftFar = plain.getString("swipeLeftFar", null) ?: "none",
        swipeRight = plain.getString("swipeRight", null) ?: "archive",
        swipeRightFar = plain.getString("swipeRightFar", null) ?: "none",
    )

    private fun edit(block: SharedPreferences.Editor.() -> Unit) {
        plain.edit().apply(block).apply()
        _settings.value = readSettings()
    }

    fun setSyncResult(error: String?) = edit { if (error == null) putLong("lastSync", System.currentTimeMillis()); putString("lastError", error) }
    fun setShowCompleted(v: Boolean) = edit { putBoolean("showCompleted", v) }
    fun setDefaultReminder(min: Int) = edit { putInt("defaultReminder", min) }
    fun setConflictNotice(v: Boolean) = edit { putBoolean("conflict", v) }
    fun setAgendaMultiDay(v: MultiDay) = edit { putString("agendaMulti", v.name) }
    fun setAgendaShowTasks(v: Boolean) = edit { putBoolean("agendaTasks", v) }
    fun setAgendaDays(v: Int) = edit { putInt("agendaDays", v) }
    fun setAppLock(v: Boolean) = edit { putBoolean("appLock", v) }
    fun setMailAccount(v: String?) = edit { putString("mailAccount", v) }
    fun setMailNotify(v: String) = edit { putString("mailNotify", v) }
    fun setMailPreview(v: Boolean) = edit { putBoolean("mailPreview", v) }
    fun setMailLockscreen(v: String) = edit { putString("mailLockscreen", v) }
    fun setSwipe(key: String, v: String) = edit { putString(key, v) }
    fun setAndroidCalendar(v: Boolean) = edit { putBoolean("androidCalendar", v) }
    fun setAndroidContacts(v: Boolean) = edit { putBoolean("androidContacts", v) }
    fun setAccent(v: Long) = edit { putLong("accent", v) }

    companion object {
        /** Schluessel der SQLCipher-Datenbank: 32 Zufallsbytes, verschluesselt abgelegt (Android Keystore). */
        fun dbKey(context: Context): ByteArray {
            val sp = try {
                val key = MasterKey.Builder(context).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build()
                EncryptedSharedPreferences.create(context, "dbkey", key,
                    EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV, EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM)
            } catch (_: Exception) {
                context.deleteSharedPreferences("dbkey")
                context.getSharedPreferences("dbkey_plain", Context.MODE_PRIVATE)
            }
            sp.getString("k", null)?.let { return android.util.Base64.decode(it, android.util.Base64.NO_WRAP) }
            val bytes = ByteArray(32).also { java.security.SecureRandom().nextBytes(it) }
            sp.edit().putString("k", android.util.Base64.encodeToString(bytes, android.util.Base64.NO_WRAP)).apply()
            return bytes
        }
    }
    fun clearSettings() = edit { clear() }
}
