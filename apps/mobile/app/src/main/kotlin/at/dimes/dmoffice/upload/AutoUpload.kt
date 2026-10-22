package at.dimes.dmoffice.upload

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.database.ContentObserver
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.Uri
import android.os.BatteryManager
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.DocumentsContract
import android.provider.MediaStore
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.ExistingWorkPolicy
import androidx.work.ForegroundInfo
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.OutOfQuotaPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import at.dimes.dmoffice.App
import at.dimes.dmoffice.MainActivity
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.files.FileItem
import at.dimes.dmoffice.files.FilesRepo
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.contentOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MediaType.Companion.toMediaTypeOrNull
import okhttp3.RequestBody
import okio.BufferedSink
import java.io.IOException
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

/**
 * Automatisches Hochladen wie in der Nextcloud-App: Regeln (Quelle auf dem Geraet -> Ordner in der
 * Dateiablage), Erkennen neuer Dateien, Warteschlange mit Wiederholen, Pausieren und Abbrechen.
 *
 * Erkennen: ContentObserver auf den MediaStore, solange die App laeuft; ein WorkManager-Auftrag mit
 * Content-URI-Ausloeser auch ohne laufende App; alle 15 Minuten ein Durchlauf fuer alles (auch frei
 * gewaehlte Ordner, die keine Aenderungen melden).
 *
 * Hochladen ueber /api/files/upload (nicht WebDAV): der Server legt fehlende Datumsordner selbst an,
 * loest Namenskonflikte selbst (skip/rename/replace, ohne Wettlauf zwischen Pruefen und Schreiben),
 * erzeugt gleich das Vorschaubild und nimmt die Aenderungszeit mit (mtime). Der Inhalt wird gestreamt;
 * ab 8 MB direkt ueber TLS statt im Umschlag (der Umschlag haelt alles im Speicher).
 *
 * "Schon vorhanden" bestimmt die Pruefsumme (SHA-256): liegt derselbe Inhalt irgendwo auf dem Server, wird er nicht
 * noch einmal hochgeladen - auch wenn ein anderes Geraet ihn gebracht hat. Nur bei Servern ohne Pruefsummen zaehlen
 * wie frueher Name und Groesse im Zielordner. Darauf baut "Speicherplatz freigeben" auf ([FreeUpLogic]).
 */
object AutoUpload {
    private const val TAG = "AutoUpload"
    const val CHANNEL = "uploads"
    private const val NOTIF_PROGRESS = 7301
    private const val NOTIF_RESULT = 7302
    private const val NOTIF_DELETE = 7303

    private const val WORK_RUN = "autoupload-run"
    private const val WORK_PERIODIC = "autoupload-periodic"
    private const val WORK_TRIGGER = "autoupload-trigger"
    private const val WORK_RETRY = "autoupload-retry"
    private const val WORK_WAIT = "autoupload-wait-"

    /** Gerade laufende Datei samt Fortschritt; [index] von [count] in diesem Durchlauf. */
    data class Progress(val entryId: Long, val name: String, val done: Long, val total: Long, val index: Int, val count: Int)

    private val _progress = MutableStateFlow<Progress?>(null)
    val progress: StateFlow<Progress?> = _progress
    private val _running = MutableStateFlow(false)
    val running: StateFlow<Boolean> = _running
    private val _paused = MutableStateFlow(false)
    val paused: StateFlow<Boolean> = _paused
    /** Letzter Fehler beim Durchsuchen je Regel (z. B. Berechtigung fehlt, Ordner nicht mehr erreichbar). */
    private val _ruleErrors = MutableStateFlow<Map<Long, String>>(emptyMap())
    val ruleErrors: StateFlow<Map<Long, String>> = _ruleErrors
    /** Grund, warum der ganze Lauf stockt (Anmeldung abgelehnt, Speicher voll). */
    private val _blocked = MutableStateFlow<String?>(null)
    val blocked: StateFlow<String?> = _blocked

    private val runLock = Mutex()
    private val dirty = AtomicBoolean(false)
    @Volatile private var currentCall: okhttp3.Call? = null
    @Volatile private var currentId = 0L
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var observer: ContentObserver? = null

    fun dao(context: Context) = UploadDb.get(context).dao()
    private fun sp(context: Context) = context.getSharedPreferences("autoupload", Context.MODE_PRIVATE)

    /** Beim Start der App: Kanal, Zustand, Auftraege und Beobachter; Kontowechsel leert alles. */
    fun init(app: App) {
        ensureChannel(app)
        _paused.value = sp(app).getBoolean("paused", false)
        _blocked.value = sp(app).getString("blocked", null)
        scope.launch { try { refresh(app) } catch (e: Exception) { Log.w(TAG, "Start", e) } }
        scope.launch {
            app.prefs.account.map { it?.let { a -> a.baseUrl + "|" + a.login } }.distinctUntilChanged().drop(1).collect { clear(app) }
        }
    }

    fun ensureChannel(context: Context) {
        val nm = context.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, context.getString(R.string.channel_uploads), NotificationManager.IMPORTANCE_LOW))
    }

    // ------------------------------------------------------------------
    //  Auftraege
    // ------------------------------------------------------------------

    /** Nach Aenderungen an den Regeln: Auftraege und Beobachter passend ein- oder ausschalten, dann einmal laufen. */
    suspend fun refresh(context: Context) {
        val app = context.applicationContext
        val rules = dao(app).ruleList().filter { it.enabled }
        val wm = WorkManager.getInstance(app)
        if (rules.isEmpty() || App.get(app).prefs.account.value == null) {
            wm.cancelUniqueWork(WORK_PERIODIC); wm.cancelUniqueWork(WORK_TRIGGER)
            withContext(Dispatchers.Main) { unobserve(app) }
            if (dao(app).queued().isEmpty()) return
        } else {
            val periodic = PeriodicWorkRequestBuilder<AutoUploadKick>(15, TimeUnit.MINUTES).setConstraints(net()).build()
            wm.enqueueUniquePeriodicWork(WORK_PERIODIC, ExistingPeriodicWorkPolicy.KEEP, periodic)
            if (rules.any { it.source != UploadLogic.SRC_TREE }) {
                armTrigger(app, ExistingWorkPolicy.KEEP)
                withContext(Dispatchers.Main) { observe(app) }
            } else {
                wm.cancelUniqueWork(WORK_TRIGGER)
                withContext(Dispatchers.Main) { unobserve(app) }
            }
        }
        kick(app)
    }

    private fun net() = Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()

    /** Jetzt durchsuchen und hochladen (eilig, mit Vordergrund-Benachrichtigung); laeuft schon einer: danach noch einmal. */
    fun kick(context: Context) {
        dirty.set(true)
        if (_running.value) return
        // Sofort im laufenden Prozess (binnen Sekunden); der Auftrag dazu haelt den Lauf am Leben, falls die App in den Hintergrund geht
        val app = context.applicationContext
        scope.launch { try { run(app) { } } catch (e: Exception) { Log.w(TAG, "Lauf", e) } }
        enqueueRun(app, ExistingWorkPolicy.KEEP)
    }

    private fun enqueueRun(context: Context, policy: ExistingWorkPolicy) {
        val req = OneTimeWorkRequestBuilder<AutoUploadWorker>().setConstraints(net())
            .setExpedited(OutOfQuotaPolicy.RUN_AS_NON_EXPEDITED_WORK_REQUEST).build()
        WorkManager.getInstance(context).enqueueUniqueWork(WORK_RUN, policy, req)
    }

    /** Auftrag, den Android bei neuen Fotos/Videos startet - auch wenn die App nicht laeuft. */
    fun armTrigger(context: Context, policy: ExistingWorkPolicy) {
        val c = Constraints.Builder()
            .addContentUriTrigger(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, true)
            .addContentUriTrigger(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, true)
            .setTriggerContentUpdateDelay(3, TimeUnit.SECONDS).setTriggerContentMaxDelay(30, TimeUnit.SECONDS).build()
        WorkManager.getInstance(context).enqueueUniqueWork(WORK_TRIGGER, policy, OneTimeWorkRequestBuilder<AutoUploadTrigger>().setConstraints(c).build())
    }

    /** Waehrend die App laeuft: Aenderungen im MediaStore sofort bemerken (gebuendelt nach 3 s Ruhe). */
    private fun observe(context: Context) {
        if (observer != null) return
        val handler = Handler(Looper.getMainLooper())
        val fire = Runnable { kick(context) }
        val o = object : ContentObserver(handler) {
            override fun onChange(selfChange: Boolean) { handler.removeCallbacks(fire); handler.postDelayed(fire, 3_000) }
        }
        try {
            context.contentResolver.registerContentObserver(MediaStore.Images.Media.EXTERNAL_CONTENT_URI, true, o)
            context.contentResolver.registerContentObserver(MediaStore.Video.Media.EXTERNAL_CONTENT_URI, true, o)
            observer = o
        } catch (e: Exception) { Log.w(TAG, "Beobachter: " + e.message) }
    }

    private fun unobserve(context: Context) {
        observer?.let { context.contentResolver.unregisterContentObserver(it) }
        observer = null
    }

    fun setPaused(context: Context, on: Boolean) {
        sp(context).edit().putBoolean("paused", on).apply()
        _paused.value = on
        if (on) currentCall?.cancel()
        else scope.launch { dao(context).clearBackoff(); setBlocked(context, null); kick(context) }
    }

    private fun setBlocked(context: Context, msg: String?) {
        sp(context).edit().putString("blocked", msg).apply()
        _blocked.value = msg
    }

    /** Einen Eintrag abbrechen (auch den gerade laufenden); er wird nicht wieder eingereiht. */
    suspend fun cancel(context: Context, id: Long) {
        val d = dao(context)
        val e = d.entry(id) ?: return
        if (e.status == UploadEntry.QUEUED || e.status == UploadEntry.RUNNING || e.status == UploadEntry.FAILED)
            d.update(e.copy(status = UploadEntry.CANCELLED, finished = System.currentTimeMillis(), error = null))
        if (currentId == id) currentCall?.cancel()
    }

    suspend fun retry(context: Context, id: Long) {
        val d = dao(context)
        val e = d.entry(id) ?: return
        d.update(e.copy(status = UploadEntry.QUEUED, attempts = 0, nextTry = 0, error = null, finished = 0))
        kick(context)
    }

    suspend fun retryAll(context: Context) { dao(context).retryAllFailed(); dao(context).clearBackoff(); setBlocked(context, null); kick(context) }

    /** Regel loeschen: wartende Eintraege fallen weg, der Verlauf dieser Regel auch. */
    suspend fun deleteRule(context: Context, r: UploadRule) {
        if (currentId != 0L && dao(context).entry(currentId)?.ruleId == r.id) currentCall?.cancel()
        dao(context).deleteForRule(r.id)
        dao(context).deleteRule(r)
        refresh(context)
    }

    /** Konto entfernt oder gewechselt: Regeln und Verlauf gehoeren zum alten Konto. */
    suspend fun clear(context: Context) {
        currentCall?.cancel()
        val wm = WorkManager.getInstance(context)
        for (w in listOf(WORK_RUN, WORK_PERIODIC, WORK_TRIGGER, WORK_RETRY)) wm.cancelUniqueWork(w)
        dao(context).clearEntries(); dao(context).clearRules()
        setBlocked(context, null)
        withContext(Dispatchers.Main) { unobserve(context) }
        context.getSystemService(NotificationManager::class.java).cancel(NOTIF_DELETE)
    }

    // ------------------------------------------------------------------
    //  Bedingungen (WLAN, Laden, Roaming)
    // ------------------------------------------------------------------

    /** Warum eine Regel gerade nicht hochlaedt (String-Ressource), null = darf. */
    fun waitReason(context: Context, rule: UploadRule): Int? {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        val caps = cm.activeNetwork?.let { cm.getNetworkCapabilities(it) } ?: return R.string.au_wait_network
        if (!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)) return R.string.au_wait_network
        if (rule.wifiOnly && !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED)) return R.string.au_wait_wifi
        if (!rule.roaming && Build.VERSION.SDK_INT >= 28 && !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_ROAMING)) return R.string.au_wait_roaming
        if (rule.chargingOnly && !charging(context)) return R.string.au_wait_charging
        return null
    }

    private fun charging(context: Context): Boolean {
        val i = context.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) ?: return false
        return i.getIntExtra(BatteryManager.EXTRA_PLUGGED, 0) != 0
    }

    // ------------------------------------------------------------------
    //  Durchlauf
    // ------------------------------------------------------------------

    /** Ein Durchlauf: alle Regeln durchsuchen, dann die Warteschlange abarbeiten; [foreground] schaltet die Benachrichtigung ein. */
    suspend fun run(context: Context, foreground: suspend (ForegroundInfo) -> Unit) {
        if (!runLock.tryLock()) {
            // Laeuft schon (z. B. im Prozess angestossen): noch eine Runde verlangen und warten, bis sie fertig ist -
            // so haelt ein Auftrag von WorkManager den Prozess am Leben, solange hochgeladen wird
            dirty.set(true)
            runLock.lock(); runLock.unlock()
            return
        }
        _running.value = true
        var uploaded = 0; var failed = 0
        try {
            val d = dao(context)
            d.requeueRunning()
            var rounds = 0
            do {
                dirty.set(false)
                scanAll(context)
                if (_paused.value) break
                val (u, f) = uploadAll(context, foreground)
                uploaded += u; failed += f
            } while (dirty.get() && !_paused.value && ++rounds < 20)
            followUps(context)
            // Frueher Hochgeladenes per Pruefsumme vom Server bestaetigen lassen (Grundlage fuer "Speicherplatz freigeben")
            if (!_paused.value) try { confirmUploaded(context) } catch (e: Exception) { Log.w(TAG, "Bestaetigen", e) }
            // Mit "Zugriff auf alle Dateien": auch frueher Vorgemerktes gleich loeschen
            try { deletePendingNow(context) } catch (e: Exception) { Log.w(TAG, "Loeschen", e) }
        } finally {
            currentCall = null; currentId = 0
            _progress.value = null
            _running.value = false
            runLock.unlock()
            context.getSystemService(NotificationManager::class.java).cancel(NOTIF_PROGRESS)
        }
        // Waehrend des Abschlusses neu angestossen: nach diesem Lauf noch einmal (dieser Auftrag laeuft ja noch)
        if (dirty.get() && !_paused.value) enqueueRun(context, ExistingWorkPolicy.APPEND_OR_REPLACE)
        notifyResult(context, uploaded, failed)
    }

    /** Neue Dateien aller eingeschalteten Regeln in die Warteschlange. */
    suspend fun scanAll(context: Context) {
        val d = dao(context)
        val errors = HashMap<Long, String>()
        for (rule in d.ruleList().filter { it.enabled }) {
            try { scanRule(context, rule) }
            catch (e: SecurityException) { errors[rule.id] = context.getString(R.string.au_err_permission) }
            catch (e: Exception) { Log.w(TAG, "Durchsuchen ${rule.name}", e); errors[rule.id] = e.message ?: e.toString() }
        }
        _ruleErrors.value = errors
    }

    /**
     * Startdatum der Regel: zaehlt die Aufnahme. Fehlt dem MediaStore das Aufnahmedatum (zurueckgespielte Sicherung, kopierte
     * Fotos) und wuerde die Datei nur wegen ihres neuen Aenderungsdatums durchgehen, wird das Datum aus dem Foto gelesen (EXIF),
     * sonst waeren nach einem Geraetewechsel alle Fotos "neu".
     */
    private fun passesSince(context: Context, rule: UploadRule, f: LocalFile): Boolean {
        if (UploadLogic.newEnough(rule.sinceMs, f.taken, f.modified, f.added, rule.created)) {
            if (f.taken > 0 || rule.sinceMs <= 0) return true
            // Aufnahmedatum unbekannt, nur das Aenderungs- bzw. Hinzufuegedatum spricht fuer "neu": nachsehen
            if (f.added >= rule.created && rule.created > 0) return true
            return Scanner.takenDate(context, f) >= rule.sinceMs
        }
        return false
    }

    suspend fun scanRule(context: Context, rule: UploadRule) {
        val d = dao(context)
        val files = Scanner.scan(context, rule)
        updateLocalState(d, rule, files)
        val known = d.known(rule.id).groupBy { it.sourcePath }.mapValues { (_, v) -> v.last() }
        val fresh = ArrayList<UploadEntry>()
        for (f in files) {
            if (UploadLogic.ignoredName(f.name) || !UploadLogic.typeMatches(rule.types, f.mime, f.name)) continue
            if (!passesSince(context, rule, f)) continue
            val k = known[f.sourcePath]
            if (k != null && k.size == f.size && k.modified == f.modified) continue
            val taken = if (f.taken > 0) f.taken else if (rule.subfolders != UploadLogic.SUB_NONE) Scanner.takenDate(context, f) else f.modified
            val dir = UploadLogic.targetDir(rule.target, f.relDir, rule.keepStructure, rule.subfolders, taken)
            if (k != null && (k.status == UploadEntry.QUEUED || k.status == UploadEntry.FAILED)) {
                // Noch nicht hochgeladen und inzwischen geaendert: den Eintrag auf den neuen Stand bringen
                d.entry(k.id)?.let { d.update(it.copy(uri = f.uri.toString(), size = f.size, modified = f.modified, taken = taken, targetDir = dir, status = UploadEntry.QUEUED, attempts = 0, nextTry = 0, error = null)) }
                continue
            }
            fresh += UploadEntry(ruleId = rule.id, uri = f.uri.toString(), sourcePath = f.sourcePath, name = f.name, mime = f.mime,
                size = f.size, modified = f.modified, taken = taken, targetDir = dir, added = System.currentTimeMillis())
        }
        // Aelteste zuerst, damit die Reihenfolge am Ziel der Aufnahme folgt
        fresh.sortBy { it.taken }
        for (chunk in fresh.chunked(500)) d.insert(chunk)
        d.setScanned(rule.id, System.currentTimeMillis())
    }

    /** Nebenbei bei jedem Durchsuchen: sind die hochgeladenen Dateien noch da und unveraendert? */
    private suspend fun updateLocalState(d: UploadDao, rule: UploadRule, files: List<LocalFile>) {
        val byPath = files.associateBy { it.sourcePath }
        val changes = HashMap<Int, MutableList<Long>>()
        for (u in d.uploaded(rule.id)) {
            val state = FreeUpLogic.localState(u.size, u.modified, byPath[u.sourcePath]?.let { it.size to it.modified })
            if (state != u.localState) changes.getOrPut(state) { ArrayList() } += u.id
        }
        for ((state, ids) in changes) for (chunk in ids.chunked(500)) d.setLocalState(chunk, state)
    }

    /** Wie viele Dateien der Quelle aelter sind als das Startdatum der Regel (Frage "vorhandene auch hochladen?"). */
    fun countExisting(context: Context, rule: UploadRule): Int = Scanner.scan(context, rule).count {
        !UploadLogic.ignoredName(it.name) && UploadLogic.typeMatches(rule.types, it.mime, it.name) && !passesSince(context, rule, it)
    }

    /** Zielordner zum Zeitpunkt des Hochladens (die Regel kann sich seit dem Einreihen geaendert haben). */
    private fun targetFor(rule: UploadRule, e: UploadEntry): String {
        val dir = e.sourcePath.substringBeforeLast('/', "")
        val rel = if (rule.source == UploadLogic.SRC_TREE) dir
            else Scanner.mediaPaths(rule).firstOrNull { (dir + "/").startsWith(it, ignoreCase = true) }?.let { UploadLogic.relDir(it, dir) } ?: ""
        return UploadLogic.targetDir(rule.target, rel, rule.keepStructure, rule.subfolders, e.taken)
    }

    private class Stop(msg: String) : Exception(msg)

    /** Warteschlange abarbeiten; liefert (hochgeladen, fehlgeschlagen). */
    private suspend fun uploadAll(context: Context, foreground: suspend (ForegroundInfo) -> Unit): Pair<Int, Int> {
        val app = App.get(context)
        val api = app.api ?: return 0 to 0
        val d = dao(context)
        val listings = HashMap<String, MutableList<FileItem>>()
        var uploaded = 0; var failed = 0; var index = 0
        var count = d.queued().size
        var fg = false
        while (!_paused.value) {
            val rules = d.ruleList().associateBy { it.id }
            val allowed = rules.values.filter { it.enabled && waitReason(context, it) == null }.map { it.id }
            if (allowed.isEmpty()) break
            val e = d.next(System.currentTimeMillis(), allowed) ?: break
            val rule = rules[e.ruleId] ?: break
            index++
            if (index > count) count = index
            if (!fg) {
                fg = true
                try { foreground(foregroundInfo(context, Progress(e.id, e.name, 0, e.size, index, count))) }
                catch (ex: Exception) { Log.w(TAG, "Vordergrund nicht moeglich: " + ex.message) }
            }
            try {
                when (uploadOne(context, api, rule, e, listings, index, count)) {
                    UploadEntry.DONE -> uploaded++
                    UploadEntry.FAILED -> failed++
                }
                setBlocked(context, null)
            } catch (s: Stop) {
                setBlocked(context, s.message)
                break
            }
        }
        return uploaded to failed
    }

    /** Eine Datei hochladen; liefert den neuen Status. Wirft [Stop], wenn der ganze Lauf warten muss. */
    private suspend fun uploadOne(context: Context, api: ApiClient, rule: UploadRule, entry: UploadEntry, listings: HashMap<String, MutableList<FileItem>>, index: Int, count: Int): Int {
        val d = dao(context)
        var e = entry.copy(status = UploadEntry.RUNNING)
        d.update(e)
        currentId = e.id
        val now = System.currentTimeMillis()
        fun done(status: Int, error: String? = null, remote: String? = null, already: Boolean = false) =
            e.copy(status = status, error = error, remotePath = remote ?: e.remotePath, already = already, finished = System.currentTimeMillis(), nextTry = 0)
        try {
            val uri = Uri.parse(e.uri)
            val size = withContext(Dispatchers.IO) { localSize(context, uri) }
            if (size == null) { d.update(done(UploadEntry.FAILED, context.getString(R.string.au_err_gone))); return UploadEntry.FAILED }
            if (size != e.size) e = e.copy(size = size)
            val dir = targetFor(rule, e)
            if (UploadLogic.forbiddenTarget(dir)) { d.update(done(UploadEntry.FAILED, context.getString(R.string.au_err_forbidden))); return UploadEntry.FAILED }
            e = e.copy(targetDir = dir)

            // Schon da? Am Inhalt erkannt (Pruefsumme), unter welchem Namen auch immer - mit "Duplikate ueberspringen"
            // irgendwo in dmOffice (auch von einem anderen Geraet), sonst nur im Zielordner.
            // Die Pruefsumme wird je Datei und Stand einmal gerechnet und zwischengespeichert.
            val hash = localHash(context, e.uri, e.size, e.modified) ?: run {
                d.update(done(UploadEntry.FAILED, context.getString(R.string.au_err_gone))); return UploadEntry.FAILED
            }
            e = e.copy(sha256 = hash)
            val answer = withContext(Dispatchers.IO) { lookup(api, listOf(FreeUpLogic.Key(hash, e.size)), dir.takeUnless { rule.skipDuplicates }) }
            val already = FreeUpLogic.already(answer, hash, e.size)
            if (already is FreeUpLogic.Already.ByHash) {
                e = done(UploadEntry.DONE, remote = already.path, already = true).copy(backedUp = true, localState = FreeUpLogic.LOCAL_PRESENT)
                d.update(e)
                afterUpload(context, rule, e)
                return UploadEntry.DONE
            }
            // Server ohne Pruefsummen (oder noch beim Nachrechnen): wie frueher gleicher Name und gleiche Groesse im Zielordner
            val existing = listings.getOrPut(dir) { withContext(Dispatchers.IO) { remoteList(context, dir) } }
            if (already == FreeUpLogic.Already.ByName) existing.firstOrNull { UploadLogic.sameRemote(it.name, it.size, it.isDir, e.name, e.size) }?.let { same ->
                e = done(UploadEntry.DONE, remote = same.path, already = true).copy(localState = FreeUpLogic.LOCAL_PRESENT)
                d.update(e)
                afterUpload(context, rule, e)
                return UploadEntry.DONE
            }
            if (rule.conflict == UploadLogic.CONFLICT_SKIP && existing.any { it.name.equals(e.name, ignoreCase = true) }) {
                d.update(done(UploadEntry.SKIPPED, context.getString(R.string.au_skipped_exists), remote = FilesRepo.join(dir, e.name)))
                return UploadEntry.SKIPPED
            }

            _progress.value = Progress(e.id, e.name, 0, e.size, index, count)
            var lastNotify = 0L
            val body = UriBody(context, readUri(context, uri), e.mime, e.size) { n ->
                _progress.value = Progress(e.id, e.name, n, e.size, index, count)
                val t = System.currentTimeMillis()
                if (t - lastNotify > 1000) { lastNotify = t; notifyProgress(context, _progress.value) }
            }
            notifyProgress(context, _progress.value)
            val resp = withContext(Dispatchers.IO) {
                api.uploadStream(dir, e.name, body, big = e.size > FilesRepo.ENVELOPE_MAX, conflict = rule.conflict, mtime = e.modified.takeIf { it > 0 }) { currentCall = it }
            }
            val file = try { api.json.parseToJsonElement(resp).jsonObject["files"]?.jsonArray?.firstOrNull()?.jsonObject } catch (_: Exception) { null }
            val path = file?.get("path")?.jsonPrimitive?.content ?: FilesRepo.join(dir, e.name)
            if (file?.get("skipped")?.jsonPrimitive?.booleanOrNull == true) {
                d.update(done(UploadEntry.SKIPPED, context.getString(R.string.au_skipped_exists), remote = path))
                return UploadEntry.SKIPPED
            }
            // Pruefsummen vergleichen: vorher gerechnet, beim Senden mitgerechnet, vom Server gespeichert
            when (FreeUpLogic.verdict(hash, body.sha256, file?.get("sha256")?.jsonPrimitive?.contentOrNull)) {
                FreeUpLogic.Verdict.CHANGED -> { d.dropHash(e.uri); throw IOException("Datei hat sich beim Hochladen geaendert") }
                FreeUpLogic.Verdict.SERVER_MISMATCH -> { d.update(done(UploadEntry.FAILED, context.getString(R.string.au_err_hash), remote = path)); return UploadEntry.FAILED }
                FreeUpLogic.Verdict.OK -> e = e.copy(backedUp = true)
                FreeUpLogic.Verdict.SERVER_UNKNOWN -> { }   // aelterer Server: spaeter per Abfrage bestaetigen (confirmUploaded)
            }
            existing.removeAll { it.path.equals(path, ignoreCase = true) }
            existing += FileItem(path, FilesRepo.nameOf(path), false, e.size)
            e = done(UploadEntry.DONE, remote = path).copy(localState = FreeUpLogic.LOCAL_PRESENT)
            d.update(e)
            App.get(context).files.invalidate(dir)
            afterUpload(context, rule, e)
            return UploadEntry.DONE
        } catch (ex: ApiException) {
            Log.w(TAG, "Hochladen ${e.name}: ${ex.code} ${ex.message}")
            return when {
                ex.code == 401 -> { requeue(context, e, now, ex.message); throw Stop(context.getString(R.string.au_err_auth)) }
                ex.code == 507 -> { requeue(context, e, now, ex.message, delayMs = 3600_000); throw Stop(context.getString(R.string.au_err_quota)) }
                ex.code == 413 -> { d.update(done(UploadEntry.FAILED, context.getString(R.string.au_err_too_large))); UploadEntry.FAILED }
                ex.code == 404 -> { d.update(done(UploadEntry.FAILED, context.getString(R.string.au_err_target, ex.message))); UploadEntry.FAILED }
                UploadLogic.permanent(ex.code) -> { d.update(done(UploadEntry.FAILED, ex.message)); UploadEntry.FAILED }
                else -> retryLater(context, e, ex.message)
            }
        } catch (ex: SecurityException) {
            d.update(done(UploadEntry.FAILED, context.getString(R.string.au_err_permission)))
            return UploadEntry.FAILED
        } catch (ex: java.io.FileNotFoundException) {
            d.update(done(UploadEntry.FAILED, context.getString(R.string.au_err_gone)))
            return UploadEntry.FAILED
        } catch (ex: ApiClient.TicketsUnsupported) {
            // Grosse Datei, aber der Server kennt keine Tickets: nicht mit dem App-Passwort am Umschlag vorbei
            d.update(done(UploadEntry.FAILED, context.getString(R.string.files_err_server_old)))
            return UploadEntry.FAILED
        } catch (ex: IOException) {
            val cur = d.entry(e.id)
            if (cur == null || cur.status == UploadEntry.CANCELLED) return UploadEntry.CANCELLED
            if (_paused.value) { d.update(e.copy(status = UploadEntry.QUEUED)); return UploadEntry.QUEUED }
            Log.w(TAG, "Hochladen ${e.name}: ${ex.message}")
            return retryLater(context, e, ex.message ?: ex.toString())
        } finally {
            currentCall = null; currentId = 0
        }
    }

    private suspend fun requeue(context: Context, e: UploadEntry, now: Long, msg: String?, delayMs: Long = 0) =
        dao(context).update(e.copy(status = UploadEntry.QUEUED, error = msg, nextTry = now + delayMs))

    private suspend fun retryLater(context: Context, e: UploadEntry, msg: String?): Int {
        val n = e.attempts + 1
        return if (n >= UploadLogic.MAX_ATTEMPTS) {
            dao(context).update(e.copy(status = UploadEntry.FAILED, attempts = n, error = msg, finished = System.currentTimeMillis()))
            UploadEntry.FAILED
        } else {
            dao(context).update(e.copy(status = UploadEntry.QUEUED, attempts = n, error = msg, nextTry = System.currentTimeMillis() + UploadLogic.backoffMs(n)))
            UploadEntry.QUEUED
        }
    }

    /** Inhalt des Zielordners; fehlt er noch, ist er leer (der Server legt ihn beim Hochladen an). */
    private fun remoteList(context: Context, dir: String): MutableList<FileItem> = try {
        App.get(context).files.listServer(dir).items.toMutableList()
    } catch (e: ApiException) {
        if (e.code == 404) mutableListOf() else throw e
    }

    /** Fotos mit Ortsangaben: ohne ACCESS_MEDIA_LOCATION schneidet Android die GPS-Daten heraus. */
    private fun readUri(context: Context, uri: Uri): Uri =
        if (Build.VERSION.SDK_INT >= 29 && uri.authority == MediaStore.AUTHORITY &&
            context.checkSelfPermission(Manifest.permission.ACCESS_MEDIA_LOCATION) == PackageManager.PERMISSION_GRANTED)
            try { MediaStore.setRequireOriginal(uri) } catch (_: Exception) { uri } else uri

    /** Tatsaechliche Groesse der Datei auf dem Geraet, null = nicht mehr da. */
    private fun localSize(context: Context, uri: Uri): Long? = try {
        context.contentResolver.openAssetFileDescriptor(uri, "r")?.use { fd -> fd.length.takeIf { it >= 0 } ?: fd.parcelFileDescriptor.statSize }
    } catch (_: java.io.FileNotFoundException) { null } catch (_: IllegalArgumentException) { null }

    /** Inhalt direkt aus der Datei auf dem Geraet, erst beim Senden gelesen; meldet den Fortschritt und rechnet die Pruefsumme mit. */
    private class UriBody(private val context: Context, private val uri: Uri, mime: String?, private val size: Long, private val onProgress: (Long) -> Unit) : RequestBody() {
        private val type = mime?.toMediaTypeOrNull() ?: "application/octet-stream".toMediaType()
        /** SHA-256 (hex) des zuletzt vollstaendig gesendeten Inhalts. */
        @Volatile var sha256: String? = null
        override fun contentType() = type
        override fun contentLength() = size
        override fun writeTo(sink: BufferedSink) {
            sha256 = null
            val md = java.security.MessageDigest.getInstance("SHA-256")
            val input = context.contentResolver.openInputStream(uri) ?: throw java.io.FileNotFoundException(uri.toString())
            input.use { s ->
                val buf = ByteArray(64 * 1024)
                var total = 0L
                while (true) {
                    val n = s.read(buf)
                    if (n < 0) break
                    if (total + n > size) throw IOException("Datei hat sich beim Hochladen geaendert")
                    md.update(buf, 0, n)
                    sink.write(buf, 0, n)
                    total += n
                    onProgress(total)
                }
                if (total != size) throw IOException("Datei hat sich beim Hochladen geaendert")
            }
            sha256 = FreeUpLogic.hex(md.digest())
        }
    }

    // ------------------------------------------------------------------
    //  Pruefsummen
    // ------------------------------------------------------------------

    /**
     * SHA-256 einer Datei auf dem Geraet: aus dem Zwischenspeicher (gleiche Uri, Groesse, Aenderungszeit) oder einmal
     * gerechnet. null, wenn die Datei fehlt oder nicht so gross ist wie erwartet.
     */
    private suspend fun localHash(context: Context, uri: String, size: Long, modified: Long): String? {
        val d = dao(context)
        d.cachedHash(uri, size, modified)?.let { return it }
        val result = try {
            withContext(Dispatchers.IO) { context.contentResolver.openInputStream(readUri(context, Uri.parse(uri)))?.use { FreeUpLogic.sha256(it) } }
        } catch (_: java.io.FileNotFoundException) { null } catch (_: IllegalArgumentException) { null }
        val (hash, length) = result ?: return null
        if (length != size) return null
        d.cacheHash(HashCache(uri, size, modified, hash))
        return hash
    }

    /**
     * Welche Inhalte liegen auf dem Server (POST /api/files/hashes)? null: der Server kennt keine Pruefsummen (aeltere
     * Version, 404). Netz- und andere Fehler gehen weiter - beim Loeschen heisst das: nichts loeschen.
     * [dir]: nur direkt in diesem Ordner. Papierkorb, Versionen und der notizi-Ordner zaehlen beim Server nie.
     */
    fun lookup(api: ApiClient, keys: Collection<FreeUpLogic.Key>, dir: String? = null): FreeUpLogic.Answer? {
        var answer = FreeUpLogic.Answer(emptyMap(), 0)
        for (chunk in keys.distinct().chunked(FreeUpLogic.LOOKUP_CHUNK)) {
            val text = try { api.postText(api.url("/api/files/hashes"), FreeUpLogic.requestBody(chunk, dir)) }
                catch (e: ApiException) { if (e.code == 404 || e.code == 405) return null else throw e }
            answer += FreeUpLogic.parseAnswer(text)
        }
        return answer
    }

    /**
     * Hochgeladenes, das der Server noch nicht per Pruefsumme bestaetigt hat (vor dieser Version, ueber Name und Groesse
     * erkannt, oder der Server rechnete noch nach): Pruefsumme rechnen bzw. aus dem Zwischenspeicher, beim Server
     * nachfragen, Treffer als gesichert merken. Portionsweise, unterbrechbar durch Pausieren.
     */
    private suspend fun confirmUploaded(context: Context) {
        val api = App.get(context).api ?: return
        val d = dao(context)
        val seen = HashSet<Long>()
        while (!_paused.value) {
            val batch = d.unconfirmed(seen.size + 200).filter { seen.add(it.id) }
            if (batch.isEmpty()) return
            val hashed = ArrayList<UploadEntry>()
            for (e in batch) {
                if (_paused.value) return
                val h = e.sha256 ?: localHash(context, e.uri, e.size, e.modified) ?: continue
                hashed += e.copy(sha256 = h)
            }
            if (hashed.isEmpty()) continue
            val answer = withContext(Dispatchers.IO) { lookup(api, hashed.map { FreeUpLogic.Key(it.sha256!!, it.size) }) } ?: return
            for (e in hashed) d.update(e.copy(backedUp = answer.found.containsKey(FreeUpLogic.Key(e.sha256!!, e.size))))
        }
    }

    // ------------------------------------------------------------------
    //  Speicherplatz freigeben
    // ------------------------------------------------------------------

    /** Schonfrist in Tagen: juengere Fotos und Videos bleiben auf dem Geraet. */
    fun keepDays(context: Context): Int = sp(context).getInt("free_keep_days", FreeUpLogic.DEFAULT_KEEP_DAYS)

    fun setKeepDays(context: Context, days: Int) = sp(context).edit().putInt("free_keep_days", days).apply()

    /** Ergebnis der Pruefung vor dem Freigeben: was geloescht werden darf, und wie viele aus Vorsicht bleiben. */
    data class FreePlan(val delete: List<UploadEntry>, val kept: Int)

    /**
     * Direkt vor dem Loeschen: jede Datei noch einmal auf dem Geraet pruefen (da, gleiche Groesse und Aenderungszeit)
     * und den Server per Pruefsumme gegenfragen. Nur was er mit gleicher Pruefsumme und Groesse hat, kommt in die Liste.
     * Erreicht man den Server nicht oder kennt er keine Pruefsummen, wirft es - dann wird nichts geloescht.
     */
    suspend fun planFreeUp(context: Context, entries: List<UploadEntry>): FreePlan = withContext(Dispatchers.IO) {
        val api = App.get(context).api ?: throw IOException(context.getString(R.string.au_err_auth))
        val d = dao(context)
        val local = entries.filter { e ->
            val stat = Scanner.mediaStat(context, Uri.parse(e.uri))
            val ok = stat != null && stat.first == e.size && stat.second == e.modified && FreeUpLogic.validHash(e.sha256)
            if (!ok) d.setLocalState(listOf(e.id), if (stat == null) FreeUpLogic.LOCAL_MISSING else FreeUpLogic.LOCAL_CHANGED)
            ok
        }
        val answer = lookup(api, local.map { FreeUpLogic.Key(it.sha256!!, it.size) })
            ?: throw IOException(context.getString(R.string.au_free_no_hashes))
        val confirmed = FreeUpLogic.confirmed(local, answer) { FreeUpLogic.Key(it.sha256!!, it.size) }
        // Was der Server nicht (mehr) hat - etwa dort geloescht -, gilt nicht mehr als gesichert
        val ok = confirmed.map { it.id }.toHashSet()
        for (e in local) if (e.id !in ok) d.update(e.copy(backedUp = false))
        FreePlan(confirmed, entries.size - confirmed.size)
    }

    /** Mit "Zugriff auf alle Dateien" (oder vor Android 11): direkt loeschen. Liefert die Zahl der geloeschten. */
    suspend fun deleteDirect(context: Context, list: List<UploadEntry>): Int = withContext(Dispatchers.IO) {
        val done = list.filter { e ->
            try { context.contentResolver.delete(Uri.parse(e.uri), null, null) > 0 } catch (ex: Exception) { Log.w(TAG, "Freigeben ${e.name}: ${ex.message}"); false }
        }
        for (chunk in done.chunked(500)) dao(context).setDeleted(chunk.map { it.id })
        done.size
    }

    /** Nach dem Systemdialog: nur als geloescht merken, was wirklich weg ist. */
    suspend fun confirmDeleted(context: Context, list: List<UploadEntry>): Int = withContext(Dispatchers.IO) {
        val gone = list.filter { try { Scanner.mediaStat(context, Uri.parse(it.uri)) == null } catch (_: Exception) { false } }
        for (chunk in gone.chunked(500)) dao(context).setDeleted(chunk.map { it.id })
        gone.size
    }

    // ------------------------------------------------------------------
    //  Lokal loeschen
    // ------------------------------------------------------------------

    /**
     * Nach dem Hochladen: auf Wunsch die Datei auf dem Geraet loeschen (nur wenn sie noch unveraendert ist). Wie bei
     * "Speicherplatz freigeben" nur, was der Server per Pruefsumme bestaetigt hat - Name und Groesse allein (aelterer
     * Server, Pruefsumme noch nicht nachgerechnet) reichen nicht. Das bleibt vorgemerkt, bis confirmUploaded es bestaetigt.
     */
    private suspend fun afterUpload(context: Context, rule: UploadRule, e: UploadEntry) {
        if (!rule.deleteAfter || e.status != UploadEntry.DONE) return
        if (!e.backedUp || !FreeUpLogic.validHash(e.sha256)) { dao(context).update(e.copy(deletePending = true)); return }
        val uri = Uri.parse(e.uri)
        if (withContext(Dispatchers.IO) { localSize(context, uri) } != e.size) return
        val deleted = withContext(Dispatchers.IO) {
            try {
                when {
                    DocumentsContract.isDocumentUri(context, uri) -> DocumentsContract.deleteDocument(context.contentResolver, uri)
                    // Ab Android 11 braucht das Loeschen fremder Medien die Bestaetigung in einem Systemdialog -
                    // ausser mit "Zugriff auf alle Dateien" (wie die Nextcloud-App), dann direkt im Hintergrund
                    Build.VERSION.SDK_INT >= 30 && !allFilesAccess() -> false
                    else -> context.contentResolver.delete(uri, null, null) > 0
                }
            } catch (ex: Exception) { Log.w(TAG, "Loeschen ${e.name}: ${ex.message}"); false }
        }
        dao(context).update(e.copy(localDeleted = deleted, deletePending = !deleted))
    }

    /** Darf die App ohne Rueckfrage loeschen? (Android 11+: Sonderzugriff "Zugriff auf alle Dateien") */
    fun allFilesAccess(): Boolean = Build.VERSION.SDK_INT < 30 || android.os.Environment.isExternalStorageManager()

    /**
     * Vorgemerkte Dateien jetzt ohne Rueckfrage loeschen (nach Erteilen von "Zugriff auf alle Dateien" und am Ende
     * jedes Laufs). Nur was noch genau so gross ist wie beim Hochladen; liefert die Anzahl.
     */
    suspend fun deletePendingNow(context: Context): Int {
        if (!allFilesAccess()) return 0
        val d = dao(context)
        var n = 0
        for (e in deletable(context)) {
            val ok = withContext(Dispatchers.IO) {
                try { context.contentResolver.delete(Uri.parse(e.uri), null, null) > 0 } catch (ex: Exception) { Log.w(TAG, "Loeschen ${e.name}: ${ex.message}"); false }
            }
            if (ok) { d.update(e.copy(localDeleted = true, deletePending = false)); n++ }
        }
        if (n > 0) context.getSystemService(NotificationManager::class.java).cancel(NOTIF_DELETE)
        return n
    }

    /** Zum Loeschen vorgemerkte Uris (Systemdialog in der Oberflaeche). */
    suspend fun markDeleted(context: Context, ids: List<Long>) {
        val d = dao(context)
        for (id in ids) d.entry(id)?.let { d.update(it.copy(localDeleted = true, deletePending = false)) }
        context.getSystemService(NotificationManager::class.java).cancel(NOTIF_DELETE)
    }

    /**
     * Nur Dateien, die es noch gibt, die seit dem Hochladen gleich gross geblieben sind und die der Server gerade eben
     * noch einmal per Pruefsumme bestaetigt (Gegenprobe wie bei "Speicherplatz freigeben"). Erreicht man den Server
     * nicht oder kennt er keine Pruefsummen, ist die Liste leer - im Zweifel bleibt die Datei.
     */
    suspend fun deletable(context: Context): List<UploadEntry> = withContext(Dispatchers.IO) {
        val d = dao(context)
        val local = d.deletePendingList().filter { e ->
            val s = localSize(context, Uri.parse(e.uri))
            if (s == null) { d.update(e.copy(localDeleted = true, deletePending = false)); false } else s == e.size && FreeUpLogic.validHash(e.sha256)
        }
        if (local.isEmpty()) return@withContext emptyList()
        val api = App.get(context).api ?: return@withContext emptyList()
        val answer = try { lookup(api, local.map { FreeUpLogic.Key(it.sha256!!, it.size) }) } catch (ex: Exception) { Log.w(TAG, "Gegenprobe: ${ex.message}"); null }
            ?: return@withContext emptyList()
        val confirmed = FreeUpLogic.confirmed(local, answer) { FreeUpLogic.Key(it.sha256!!, it.size) }
        // Was der Server nicht (mehr) hat, gilt nicht mehr als gesichert und wartet wieder auf die Bestaetigung
        val ok = confirmed.map { it.id }.toHashSet()
        for (e in local) if (e.id !in ok) d.update(e.copy(backedUp = false))
        confirmed
    }

    // ------------------------------------------------------------------
    //  Folgeauftraege und Benachrichtigungen
    // ------------------------------------------------------------------

    /** Wartendes spaeter: nach Ablauf der Wartezeit bzw. sobald WLAN/Laden/kein Roaming erfuellt ist. */
    private suspend fun followUps(context: Context) {
        if (_paused.value) return
        val d = dao(context)
        val queued = d.queued()
        if (queued.isEmpty()) return
        val wm = WorkManager.getInstance(context)
        val rules = d.ruleList().associateBy { it.id }
        for (rule in queued.mapNotNull { rules[it.ruleId] }.filter { it.enabled }.distinctBy { it.id }) {
            if (waitReason(context, rule) == null) continue
            val c = Constraints.Builder()
                .setRequiredNetworkType(if (rule.wifiOnly) NetworkType.UNMETERED else if (!rule.roaming) NetworkType.NOT_ROAMING else NetworkType.CONNECTED)
                .setRequiresCharging(rule.chargingOnly).build()
            val key = WORK_WAIT + (if (rule.wifiOnly) "w" else "") + (if (rule.chargingOnly) "c" else "") + (if (rule.roaming) "r" else "")
            wm.enqueueUniqueWork(key, ExistingWorkPolicy.KEEP, OneTimeWorkRequestBuilder<AutoUploadKick>().setConstraints(c).build())
        }
        val at = d.nextRetryAt(System.currentTimeMillis())
        if (at != null) wm.enqueueUniqueWork(WORK_RETRY, ExistingWorkPolicy.REPLACE, OneTimeWorkRequestBuilder<AutoUploadKick>()
            .setConstraints(net()).setInitialDelay(maxOf(1_000, at - System.currentTimeMillis()), TimeUnit.MILLISECONDS).build())
    }

    private fun canNotify(context: Context) = Build.VERSION.SDK_INT < 33 ||
        context.checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    private fun openQueue(context: Context, code: Int): PendingIntent =
        PendingIntent.getActivity(context, code, MainActivity.routeIntent(context, "autoupload/queue"), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)

    private fun progressNotification(context: Context, p: Progress?): android.app.Notification {
        val pause = PendingIntent.getBroadcast(context, 1, Intent(context, UploadActionReceiver::class.java).setAction(UploadActionReceiver.PAUSE),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val b = NotificationCompat.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(if (p == null) context.getString(R.string.au_notif_title) else context.getString(R.string.au_notif_progress, p.index, p.count))
            .setContentText(p?.name ?: "")
            .setOngoing(true).setOnlyAlertOnce(true).setSilent(true)
            .setContentIntent(openQueue(context, 1))
            .addAction(0, context.getString(R.string.au_pause), pause)
        if (p != null && p.total > 0) b.setProgress(1000, (p.done * 1000 / p.total).toInt(), false) else b.setProgress(0, 0, true)
        return b.build()
    }

    fun foregroundInfo(context: Context, p: Progress?): ForegroundInfo =
        if (Build.VERSION.SDK_INT >= 29) ForegroundInfo(NOTIF_PROGRESS, progressNotification(context, p), ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC)
        else ForegroundInfo(NOTIF_PROGRESS, progressNotification(context, p))

    private fun notifyProgress(context: Context, p: Progress?) {
        if (!canNotify(context)) return
        context.getSystemService(NotificationManager::class.java).notify(NOTIF_PROGRESS, progressNotification(context, p))
    }

    /** Nach dem Lauf: nur bei Fehlern oder wenn Dateien auf das Loeschen warten (sonst still wie die Nextcloud-App). */
    private suspend fun notifyResult(context: Context, uploaded: Int, failed: Int) {
        if (!canNotify(context)) return
        val nm = context.getSystemService(NotificationManager::class.java)
        if (failed > 0) nm.notify(NOTIF_RESULT, NotificationCompat.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(context.resources.getQuantityString(R.plurals.au_notif_failed, failed, failed))
            .setContentText(if (uploaded > 0) context.resources.getQuantityString(R.plurals.au_notif_uploaded, uploaded, uploaded) else null)
            .setContentIntent(openQueue(context, 2)).setAutoCancel(true).build())
        val pending = dao(context).deletePendingList().size
        if (pending > 0) nm.notify(NOTIF_DELETE, NotificationCompat.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(context.resources.getQuantityString(R.plurals.au_notif_delete, pending, pending))
            .setContentText(context.getString(R.string.au_notif_delete_text))
            .setContentIntent(openQueue(context, 3)).setAutoCancel(true).build())
        else nm.cancel(NOTIF_DELETE)
    }
}

/** Der eigentliche Lauf (eilig, mit Vordergrund-Benachrichtigung Typ dataSync). */
class AutoUploadWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun getForegroundInfo(): ForegroundInfo = AutoUpload.foregroundInfo(applicationContext, null)

    override suspend fun doWork(): Result {
        if (App.get(applicationContext).prefs.account.value == null) return Result.success()
        AutoUpload.run(applicationContext) { setForeground(it) }
        return Result.success()
    }
}

/** Anstoss (alle 15 Minuten, nach Wartezeit, sobald WLAN/Laden da ist): startet den eigentlichen Lauf. */
class AutoUploadKick(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun getForegroundInfo(): ForegroundInfo = AutoUpload.foregroundInfo(applicationContext, null)

    override suspend fun doWork(): Result {
        if (App.get(applicationContext).prefs.account.value != null) AutoUpload.run(applicationContext) { setForeground(it) }
        return Result.success()
    }
}

/** Neue Fotos/Videos im MediaStore: Lauf anstossen und fuer die naechste Aenderung neu scharf schalten. */
class AutoUploadTrigger(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun getForegroundInfo(): ForegroundInfo = AutoUpload.foregroundInfo(applicationContext, null)

    override suspend fun doWork(): Result {
        if (App.get(applicationContext).prefs.account.value == null) return Result.success()
        // Gleich fuer die naechste Aenderung scharf schalten, dann selbst durchsuchen und hochladen (auch bei geschlossener App)
        AutoUpload.armTrigger(applicationContext, ExistingWorkPolicy.APPEND_OR_REPLACE)
        AutoUpload.run(applicationContext) { setForeground(it) }
        return Result.success()
    }
}

/** "Pausieren" aus der Benachrichtigung. */
class UploadActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == PAUSE) AutoUpload.setPaused(context.applicationContext, true)
    }
    companion object { const val PAUSE = "at.dimes.dmoffice.upload.PAUSE" }
}
