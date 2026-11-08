package at.dimes.dmoffice.api

import at.dimes.dmoffice.App
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.withContext

/**
 * Ist der Server gerade erreichbar? (Version 2) Ein Symbol oben rechts zeigt es für die ganze App, statt dass jeder
 * Bereich es selbst meldet. Erreichbar heißt: der Server antwortet überhaupt (auch mit einem Fehler); nicht erreichbar:
 * Adresse nicht gefunden, keine Verbindung, Zeitüberschreitung.
 */
object ServerStatus {
    /** null: noch nicht geprüft */
    val reachable = MutableStateFlow<Boolean?>(null)
    /** Grund, wenn nicht erreichbar (für den Hinweis beim Antippen). */
    val problem = MutableStateFlow<String?>(null)
    val lastOk = MutableStateFlow(0L)

    suspend fun check(app: App): Boolean = withContext(Dispatchers.IO) {
        val api = app.api ?: return@withContext false
        val ok = try {
            api.getText(api.url("/api/chat/status"))
            true
        } catch (e: ApiException) {
            true   // der Server hat geantwortet
        } catch (e: java.net.UnknownHostException) {
            problem.value = "dns:" + (e.message ?: "")
            false
        } catch (e: java.io.IOException) {
            problem.value = e.message ?: e.toString()
            false
        }
        if (ok) { problem.value = null; lastOk.value = System.currentTimeMillis() }
        reachable.value = ok
        ok
    }
}
