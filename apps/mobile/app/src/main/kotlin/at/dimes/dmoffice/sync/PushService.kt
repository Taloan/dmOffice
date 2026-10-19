package at.dimes.dmoffice.sync

import android.content.Context
import android.util.Log
import at.dimes.dmoffice.App
import at.dimes.dmoffice.BuildConfig
import com.google.firebase.FirebaseApp
import com.google.firebase.FirebaseOptions
import com.google.firebase.messaging.FirebaseMessaging
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.tasks.await
import kotlinx.serialization.Serializable

/**
 * Push ueber Firebase Cloud Messaging: dmOffice schickt bei Aenderungen ein inhaltsloses Signal,
 * die App startet daraufhin den Abgleich. Die Firebase-Werte kommen beim Bauen aus
 * google-services.json (build.gradle.kts); fehlt die Datei, ist Push aus und die App fragt
 * wie bisher alle 15 Minuten.
 */
object Push {
    private const val TAG = "dmOffice.Push"
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    val configured: Boolean get() = BuildConfig.FCM_APP_ID.isNotEmpty()

    /** Firebase von Hand starten - ohne google-services-Plugin, damit der Build auch ohne Datei geht. */
    fun init(context: Context) {
        if (!configured || FirebaseApp.getApps(context).isNotEmpty()) return
        try {
            FirebaseApp.initializeApp(context, FirebaseOptions.Builder()
                .setApplicationId(BuildConfig.FCM_APP_ID).setApiKey(BuildConfig.FCM_API_KEY)
                .setProjectId(BuildConfig.FCM_PROJECT_ID).setGcmSenderId(BuildConfig.FCM_SENDER_ID).build())
        } catch (e: Exception) { Log.w(TAG, "Firebase nicht gestartet", e) }
    }

    @Serializable private data class RegisterBody(val token: String)

    /** Token beim Server anmelden (ueber den Umschlag); nur wenn es neu ist oder das Konto gewechselt hat. */
    fun register(context: Context, force: Boolean = false) {
        if (!configured) return
        val app = App.get(context)
        val account = app.prefs.account.value ?: return
        scope.launch {
            try {
                init(context)
                val token = FirebaseMessaging.getInstance().token.await()
                val key = account.deviceId + "|" + token
                if (!force && app.prefs.pushRegistered == key) return@launch
                val api = app.api ?: return@launch
                api.post(api.url("/api/push/register"), RegisterBody(token))
                app.prefs.pushRegistered = key
            } catch (e: Exception) { Log.w(TAG, "Anmelden beim Server fehlgeschlagen: ${e.message}") }
        }
    }
}

/** Empfaengt die Signale von dmOffice und das neue Token, wenn Firebase es wechselt. */
class PushService : FirebaseMessagingService() {
    override fun onNewToken(token: String) { Push.register(applicationContext, force = true) }

    override fun onMessageReceived(message: RemoteMessage) {
        if (message.data["t"] == "sync") SyncWorker.now(applicationContext)
    }
}
