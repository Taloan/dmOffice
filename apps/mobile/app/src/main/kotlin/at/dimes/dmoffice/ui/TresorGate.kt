package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import okhttp3.Interceptor
import okhttp3.Response

/**
 * Tresor geschlossen (docs/tresor-konzept.md): Antwortet der Server mit 423 { locked: "tresor" }, zeigt [TresorHost] überall
 * denselben Öffnen-Dialog und [TresorInterceptor] wiederholt danach die Anfrage. Offen ist der Tresor je Gerät (App-Passwort).
 */
object TresorGate {
    class Ask(val area: String, val done: CompletableDeferred<Boolean>)

    val ask = MutableStateFlow<Ask?>(null)
    /** Zählt hoch nach Öffnen oder Schließen - Ansichten und Einstellungen laden dann neu. */
    val changed = MutableStateFlow(0)
    /** Wie viele [TresorHost] sichtbar sind; ohne einen (Hintergrund, Home-Widget) kommt 423 einfach als Fehler zurück. */
    @Volatile var hosts = 0

    private fun start(area: String): CompletableDeferred<Boolean> = synchronized(this) {
        ask.value?.done ?: CompletableDeferred<Boolean>().also { ask.value = Ask(area, it) }
    }

    /** Abgebrochen: kurz nicht noch einmal fragen (eine Ansicht lädt oft mehreres zugleich). */
    @Volatile private var cancelledAt = 0L

    /** Aus dem Interceptor (IO-Thread): Dialog zeigen und auf die Antwort warten. */
    fun awaitBlocking(area: String): Boolean {
        if (hosts == 0 || System.currentTimeMillis() - cancelledAt < 5_000) return false
        val d = start(area)
        return runBlocking { withTimeoutOrNull(180_000) { d.await() } ?: false }
    }

    /** Aus der Oberfläche (Knopf „Tresor öffnen“). */
    suspend fun prompt(area: String): Boolean = start(area).await()

    fun finish(ok: Boolean) {
        val a = synchronized(this) { ask.value.also { ask.value = null } }
        a?.done?.complete(ok)
        if (ok) changed.value++ else cancelledAt = System.currentTimeMillis()
    }
}

/** 423 { locked: "tresor" }: Dialog, dann dieselbe Anfrage noch einmal. Vor dem Umschlag eingehängt. */
class TresorInterceptor : Interceptor {
    override fun intercept(chain: Interceptor.Chain): Response {
        val req = chain.request()
        val res = chain.proceed(req)
        if (res.code != 423) return res
        val path = req.url.encodedPath
        if (path.startsWith("/api/tresor") && !path.startsWith("/api/tresor/files")) return res
        val area = try { Json.parseToJsonElement(res.peekBody(4096).string()).jsonObject["locked"]?.jsonPrimitive?.content } catch (_: Exception) { null }
        if (area != "tresor" || !TresorGate.awaitBlocking(area)) return res
        res.close()
        return chain.proceed(req)
    }
}

/** Der gemeinsame Öffnen-Dialog des Tresors - einmal im Hauptbildschirm eingehängt. */
@Composable
fun TresorHost() {
    DisposableEffect(Unit) { TresorGate.hosts++; onDispose { TresorGate.hosts-- } }
    val ask by TresorGate.ask.collectAsState()
    if (ask == null) return
    TresorOpenDialog { TresorGate.finish(it) }
}

@kotlinx.serialization.Serializable
data class SecurityLogEntry(val at: String, val what: String, val by: String? = null)

/** Sicherheitsprotokoll (Tresor Stufe 1): Änderungen der Verwaltung mit Name, eigene ohne. */
@Composable
fun SecurityLogList() {
    val ctx = LocalContext.current
    var log by remember { mutableStateOf<List<SecurityLogEntry>?>(null) }
    LaunchedEffect(Unit) {
        log = try { withContext(Dispatchers.IO) { App.get(ctx).api?.let { it.get<List<SecurityLogEntry>>(it.url("/api/settings/security-log")) } } } catch (_: Exception) { null }
    }
    val list = log ?: return
    SectionHeader(stringResource(R.string.security_log))
    Text(stringResource(R.string.security_log_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
    if (list.isEmpty()) Text(stringResource(R.string.security_log_empty), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp))
    val fmt = java.text.DateFormat.getDateTimeInstance(java.text.DateFormat.MEDIUM, java.text.DateFormat.SHORT)
    for (e in list.take(30)) {
        val at = try { fmt.format(java.util.Date.from(java.time.OffsetDateTime.parse(e.at).toInstant())) } catch (_: Exception) { e.at }
        ListItem(headlineContent = { Text(e.what) },
            supportingContent = { Text(at + (e.by?.let { " – " + stringResource(R.string.security_log_by, it) } ?: "")) })
    }
}
