package at.dimes.dmoffice.notify

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import org.json.JSONObject

/**
 * Einstellungen der Mail-Benachrichtigungen je Mailkonto (nur auf diesem Handy) und die zuletzt bekannten
 * Kontonamen, damit Kanaele auch ohne Netz richtig heissen. Was ein Konto nicht selbst einstellt, uebernimmt
 * es von der Vorgabe fuer alle Konten (Einstellungen > E-Mail).
 */
class MailNotifyPrefs private constructor(context: Context) {
    private val sp = context.applicationContext.getSharedPreferences("mailnotify_accounts", Context.MODE_PRIVATE)
    /** Zaehlt hoch, sobald sich etwas aendert - fuer die Oberflaeche. */
    val revision = MutableStateFlow(0)

    fun override(account: String) = MailNotifyOverride(
        mode = sp.getString("$account.mode", null),
        preview = if (sp.contains("$account.preview")) sp.getBoolean("$account.preview", true) else null,
        hideOnLock = if (sp.contains("$account.hide")) sp.getBoolean("$account.hide", false) else null,
    )

    fun setMode(account: String, v: String) = edit { putString("$account.mode", v) }
    fun setPreview(account: String, v: Boolean) = edit { putBoolean("$account.preview", v) }
    fun setHideOnLock(account: String, v: Boolean) = edit { putBoolean("$account.hide", v) }

    /** Zuletzt bekannte Konten (Kennung -> Anzeigename, Adresse). */
    fun known(): List<Known> = try {
        val o = JSONObject(sp.getString("known", "{}") ?: "{}")
        o.keys().asSequence().map { Known(it, o.getJSONObject(it).optString("label"), o.getJSONObject(it).optString("email")) }.toList()
    } catch (_: Exception) { emptyList() }

    fun remember(accounts: List<Known>) {
        val o = JSONObject()
        for (a in accounts) o.put(a.id, JSONObject().put("label", a.label).put("email", a.email))
        sp.edit().putString("known", o.toString()).apply()
        revision.value++
    }

    private fun edit(block: android.content.SharedPreferences.Editor.() -> Unit) { sp.edit().apply(block).apply(); revision.value++ }

    data class Known(val id: String, val label: String, val email: String)

    companion object {
        @Volatile private var instance: MailNotifyPrefs? = null
        fun get(context: Context): MailNotifyPrefs = instance ?: synchronized(this) { instance ?: MailNotifyPrefs(context).also { instance = it } }
    }
}
