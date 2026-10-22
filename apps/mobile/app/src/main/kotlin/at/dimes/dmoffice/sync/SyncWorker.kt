package at.dimes.dmoffice.sync

import android.content.Context
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import at.dimes.dmoffice.App
import java.util.concurrent.TimeUnit

/** Hintergrund-Abgleich: alle 15 Minuten (Minimum von WorkManager) und sofort nach lokalen Aenderungen. */
class SyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        val app = App.get(applicationContext)
        val account = app.prefs.account.value ?: return Result.success()
        // Aenderungen aus dem Android-Kalender zuerst in die lokale Datenbank, dann laedt sie der Abgleich hoch
        try { ProviderSync.beforeSync(applicationContext) } catch (_: Exception) { }
        val r = SyncEngine(applicationContext, app.db).run(account)
        // Mail, Kontakte, Notizen: Fehler hier brechen den Kalenderabgleich nicht ab
        // Alle Mailkonten abgleichen (Benachrichtigungen gibt es je Konto); das gewaehlte zuerst
        var mailAccounts: List<String> = emptyList()
        try {
            val list = app.mail.accounts()
            at.dimes.dmoffice.notify.MailNotify.syncAccounts(applicationContext, list)
            val selected = app.prefs.settings.value.mailAccount?.takeIf { s -> list.any { it.id == s } } ?: list.firstOrNull()?.id?.also { app.prefs.setMailAccount(it) }
            mailAccounts = list.map { it.id }.sortedBy { it != selected }
        } catch (_: Exception) { }
        for (acc in mailAccounts) {
            try { app.mail.refreshFolders(acc); app.mail.refresh(acc, "INBOX", 0, 100) } catch (_: Exception) { }
        }
        // Aenderungen aus der Android-Kontakte-App vor dem Neuladen hochschicken
        try { ProviderContacts.upload(applicationContext) } catch (_: Exception) { }
        try { app.contacts.refresh() } catch (_: Exception) { }
        // Neue Mails melden (nach den Kontakten: VIPs aktuell)
        for (acc in mailAccounts) try { at.dimes.dmoffice.notify.MailNotify.check(applicationContext, acc) } catch (_: Exception) { }
        // Offline gespeicherte Notizen (Checklisten im Funkloch abgehakt) vor dem Neuladen hochbringen
        try { app.notes.flushPending() } catch (_: Exception) { }
        try { app.notes.refresh() } catch (_: Exception) { }
        try { ProviderSync.afterSync(applicationContext) } catch (_: Exception) { }
        return if (r.isSuccess) Result.success() else if (runAttemptCount < 3) Result.retry() else Result.failure()
    }

    companion object {
        private const val PERIODIC = "sync-periodic"
        private const val NOW = "sync-now"

        fun schedule(context: Context) {
            val constraints = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()
            val req = PeriodicWorkRequestBuilder<SyncWorker>(15, TimeUnit.MINUTES).setConstraints(constraints).build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(PERIODIC, ExistingPeriodicWorkPolicy.KEEP, req)
        }

        fun now(context: Context) {
            val constraints = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()
            val req = OneTimeWorkRequestBuilder<SyncWorker>().setConstraints(constraints).build()
            WorkManager.getInstance(context).enqueueUniqueWork(NOW, ExistingWorkPolicy.APPEND_OR_REPLACE, req)
        }

        fun cancel(context: Context) {
            WorkManager.getInstance(context).cancelUniqueWork(PERIODIC)
            WorkManager.getInstance(context).cancelUniqueWork(NOW)
        }
    }
}
