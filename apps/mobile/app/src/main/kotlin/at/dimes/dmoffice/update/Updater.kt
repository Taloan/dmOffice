package at.dimes.dmoffice.update

import android.content.Context
import android.content.Intent
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import androidx.core.content.pm.PackageInfoCompat
import at.dimes.dmoffice.App
import at.dimes.dmoffice.BuildConfig
import at.dimes.dmoffice.R
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit

/**
 * Updates ohne App-Store: neueste Version von GitHub-Releases pruefen, dmOffice.apk laden,
 * Signatur gegen die installierte App pruefen und den Android-Installer starten.
 * Die Debug-Variante (.debug) ist anders signiert und bekommt kein Update angeboten.
 */
object Updater {
    sealed interface State {
        data object Idle : State
        data object Checking : State
        data class UpToDate(val latest: String?) : State
        data class Available(val release: Release) : State
        data class Downloading(val release: Release, val progress: Float) : State
        /** Geladen und geprueft, aber "Aus dieser Quelle installieren" fehlt noch. */
        data class NeedsPermission(val release: Release) : State
        data class Installing(val release: Release) : State
        data class Failed(val release: Release?, val message: String) : State
    }

    private val _state = MutableStateFlow<State>(State.Idle)
    val state: StateFlow<State> = _state
    /** Hinweis-Dialog sichtbar (nach dem Start, aus der Benachrichtigung oder den Einstellungen). */
    val dialog = MutableStateFlow(false)

    /** Entwickler-Build: anders signiert, ein Update von GitHub liesse sich nicht installieren. */
    val isDevBuild: Boolean = BuildConfig.APPLICATION_ID.endsWith(".debug")

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var job: Job? = null

    /** Ohne automatische Weiterleitung: jedes Ziel wird einzeln auf HTTPS und GitHub geprueft. */
    private val http = OkHttpClient.Builder()
        .followRedirects(false).followSslRedirects(false)
        .connectTimeout(20, TimeUnit.SECONDS).readTimeout(60, TimeUnit.SECONDS)
        .build()

    private fun sp(context: Context) = context.getSharedPreferences("update", Context.MODE_PRIVATE)

    fun autoEnabled(context: Context): Boolean = !isDevBuild && App.get(context).prefs.settings.value.autoUpdate

    /** Zuletzt gefundene Version (ueberlebt Neustarts, damit der Hinweis nicht jedes Mal neu laden muss). */
    private fun cached(context: Context): Release? = sp(context).getString("release", null)?.let { UpdateLogic.parseRelease(it) }

    /** Beim Start der App: hoechstens einmal pro Stunde bei GitHub fragen, sonst den letzten Stand nehmen. */
    fun checkOnStart(context: Context) {
        if (!autoEnabled(context)) return
        val app = context.applicationContext
        scope.launch {
            val r = if (System.currentTimeMillis() - sp(app).getLong("lastCheck", 0) < TimeUnit.HOURS.toMillis(1)) cached(app)?.also { publish(it) }
                else try { check(app) } catch (_: Exception) { null }
            if (r != null && UpdateLogic.isNewer(r, BuildConfig.VERSION_CODE) && sp(app).getString("dismissed", null) != r.tag) dialog.value = true
        }
    }

    /** "Nach Updates suchen" in den Einstellungen; zeigt den Hinweis, wenn es etwas Neues gibt. */
    fun checkNow(context: Context) {
        val app = context.applicationContext
        if (_state.value is State.Checking || _state.value is State.Downloading) return
        scope.launch {
            try {
                val r = check(app)
                if (UpdateLogic.isNewer(r, BuildConfig.VERSION_CODE) && !isDevBuild) dialog.value = true
            } catch (e: Exception) {
                _state.value = State.Failed(null, e.message ?: e.javaClass.simpleName)
            }
        }
    }

    /** GitHub fragen, Ergebnis merken und den Zustand setzen. Wirft bei Netz- oder Formatfehlern. */
    suspend fun check(context: Context): Release = withContext(Dispatchers.IO) {
        _state.value = State.Checking
        try {
            val body = get(UpdateLogic.RELEASES_LATEST, "application/vnd.github+json").use { resp ->
                if (!resp.isSuccessful) throw IOException("GitHub: HTTP ${resp.code}")
                resp.body?.string() ?: throw IOException("GitHub: empty")
            }
            val r = UpdateLogic.parseRelease(body) ?: throw IOException("GitHub: no ${UpdateLogic.APK_NAME}")
            sp(context).edit().putString("release", body).putLong("lastCheck", System.currentTimeMillis()).apply()
            publish(r)
            r
        } catch (e: Exception) {
            if (_state.value is State.Checking) _state.value = State.Idle
            throw e
        }
    }

    private fun publish(r: Release) {
        _state.value = if (UpdateLogic.isNewer(r, BuildConfig.VERSION_CODE) && !isDevBuild) State.Available(r) else State.UpToDate(r.versionName)
    }

    /** Tipp auf die Benachrichtigung: Hinweis zeigen, mit [install] gleich laden und installieren. */
    fun fromNotification(context: Context, install: Boolean) {
        if (isDevBuild) return
        UpdateWorker.cancelNotification(context)
        val s = _state.value
        if (s !is State.Downloading && s !is State.NeedsPermission && s !is State.Installing) {
            val r = cached(context)?.takeIf { UpdateLogic.isNewer(it, BuildConfig.VERSION_CODE) } ?: return
            _state.value = State.Available(r)
        }
        dialog.value = true
        if (install) update(context)
    }

    /** Fuer die Benachrichtigung im Hintergrund: Version nur einmal melden. */
    fun shouldNotify(context: Context, r: Release): Boolean {
        val sp = sp(context)
        if (sp.getString("notified", null) == r.tag || sp.getString("dismissed", null) == r.tag) return false
        sp.edit().putString("notified", r.tag).apply()
        return true
    }

    /** "Spaeter": diese Version beim Start nicht mehr von selbst anbieten (in den Einstellungen weiter moeglich). */
    fun dismiss(context: Context) {
        dialog.value = false
        val s = _state.value
        val r = (s as? State.Available)?.release ?: (s as? State.Failed)?.release
        if (r != null) sp(context).edit().putString("dismissed", r.tag).apply()
        if (s is State.Failed) _state.value = r?.let { State.Available(it) } ?: State.Idle
    }

    /** "Aktualisieren": laden, pruefen, installieren. Laeuft unabhaengig vom Bildschirm weiter. */
    fun update(context: Context) {
        if (isDevBuild) return
        val app = context.applicationContext
        val r = when (val s = _state.value) {
            is State.Available -> s.release
            is State.Failed -> s.release
            is State.NeedsPermission -> { install(app, s.release); return }
            else -> null
        } ?: cached(app)?.takeIf { UpdateLogic.isNewer(it, BuildConfig.VERSION_CODE) } ?: return
        if (job?.isActive == true) return
        job = scope.launch {
            try {
                // Schon geladen (z. B. Installer abgebrochen): nicht noch einmal laden
                val file = apkFile(app, r).takeIf { it.exists() && verify(app, it) == null }
                    ?: run { _state.value = State.Downloading(r, -1f); download(app, r) }
                verify(app, file)?.let { msg -> file.delete(); _state.value = State.Failed(r, msg); return@launch }
                install(app, r)
            } catch (e: Exception) {
                _state.value = State.Failed(r, app.getString(R.string.update_failed, e.message ?: e.javaClass.simpleName))
            }
        }
    }

    /**
     * App wieder im Vordergrund. Aus der Systemeinstellung zurueck: wenn jetzt erlaubt, gleich installieren.
     * Aus dem Installer zurueck (abgebrochen oder im Hintergrund nicht gestartet): Hinweis wieder anbieten.
     */
    fun resumeAfterPermission(context: Context) {
        when (val s = _state.value) {
            is State.NeedsPermission -> if (context.packageManager.canRequestPackageInstalls()) install(context.applicationContext, s.release)
            is State.Installing -> { _state.value = State.NeedsPermission(s.release).takeIf { !context.packageManager.canRequestPackageInstalls() } ?: State.Available(s.release); dialog.value = true }
            else -> {}
        }
    }

    /** Systemeinstellung "Unbekannte Apps installieren" fuer dmOffice oeffnen. */
    fun openInstallPermission(context: Context) {
        context.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:" + context.packageName))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    }

    private fun apkFile(context: Context, r: Release) = File(File(context.cacheDir, "updates"), "dmOffice-${r.versionName}.apk")

    private fun install(context: Context, r: Release) {
        val file = apkFile(context, r)
        if (!file.exists()) { _state.value = State.Available(r); return }
        if (!context.packageManager.canRequestPackageInstalls()) { _state.value = State.NeedsPermission(r); return }
        // Vor jedem Start des Installers noch einmal pruefen (die Datei koennte seit dem Laden ersetzt worden sein)
        verify(context, file)?.let { msg -> file.delete(); _state.value = State.Failed(r, msg); return }
        val uri = FileProvider.getUriForFile(context, context.packageName + ".files", file)
        _state.value = State.Installing(r)
        dialog.value = false
        @Suppress("DEPRECATION")
        context.startActivity(Intent(Intent.ACTION_INSTALL_PACKAGE).setDataAndType(uri, "application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
            .putExtra(Intent.EXTRA_NOT_UNKNOWN_SOURCE, true))
    }

    /** GET mit Pruefung jedes Ziels (auch nach Weiterleitungen): nur HTTPS, nur GitHub. */
    private fun get(url: String, accept: String?): Response {
        var u = url
        repeat(6) {
            if (!UpdateLogic.isAllowedUrl(u)) throw IOException("blocked: " + (try { java.net.URI(u).host } catch (_: Exception) { "?" }))
            val req = Request.Builder().url(u).header("User-Agent", "dmOffice-Android/" + BuildConfig.VERSION_NAME)
                .apply { if (accept != null) header("Accept", accept) }.build()
            val resp = http.newCall(req).execute()
            if (!resp.isRedirect) return resp
            val next = resp.header("Location")?.let { resp.request.url.resolve(it)?.toString() }
            resp.close()
            u = next ?: throw IOException("redirect without target")
        }
        throw IOException("too many redirects")
    }

    private fun download(context: Context, r: Release): File {
        val dir = File(context.cacheDir, "updates").apply { mkdirs() }
        // Alte Downloads weg, nur die aktuelle Datei bleibt
        dir.listFiles()?.forEach { it.delete() }
        val part = File(dir, "dmOffice-${r.versionName}.part")
        get(r.apkUrl, "application/octet-stream").use { resp ->
            if (!resp.isSuccessful) throw IOException("HTTP ${resp.code}")
            val body = resp.body ?: throw IOException("empty")
            val total = body.contentLength().takeIf { it > 0 } ?: r.apkSize
            if (total > UpdateLogic.MAX_APK_BYTES) throw IOException("too large")
            var done = 0L
            var shown = -1
            body.byteStream().use { input ->
                part.outputStream().use { out ->
                    val buf = ByteArray(64 * 1024)
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        out.write(buf, 0, n)
                        done += n
                        if (done > UpdateLogic.MAX_APK_BYTES) throw IOException("too large")
                        val pct = if (total > 0) (done * 100 / total).toInt() else -1
                        if (pct != shown) { shown = pct; _state.value = State.Downloading(r, if (total > 0) done.toFloat() / total else -1f) }
                    }
                }
            }
            if (r.apkSize > 0 && done != r.apkSize) { part.delete(); throw IOException("incomplete ($done / ${r.apkSize})") }
        }
        val file = apkFile(context, r)
        if (!part.renameTo(file)) throw IOException("rename failed")
        return file
    }

    /** null = in Ordnung, sonst die Fehlermeldung. Paketname, hoehere Version und gleiche Signatur wie die installierte App. */
    private fun verify(context: Context, file: File): String? {
        val pm = context.packageManager
        val flags = if (Build.VERSION.SDK_INT >= 28) PackageManager.GET_SIGNING_CERTIFICATES else @Suppress("DEPRECATION") PackageManager.GET_SIGNATURES
        val apk = pm.getPackageArchiveInfo(file.path, flags) ?: return context.getString(R.string.update_invalid)
        if (apk.packageName != context.packageName) return context.getString(R.string.update_invalid)
        val installed = pm.getPackageInfo(context.packageName, flags)
        if (PackageInfoCompat.getLongVersionCode(apk) <= PackageInfoCompat.getLongVersionCode(installed)) return context.getString(R.string.update_invalid)
        val (apkNow, apkHistory) = signers(pm, file, apk)
        val (installedNow, _) = signers(pm, null, installed)
        return if (UpdateLogic.signaturesMatch(installedNow, apkNow, apkHistory)) null else context.getString(R.string.update_signature_mismatch)
    }

    /** SHA-256 der aktuellen Signaturzertifikate und der Signaturgeschichte (Schluesselrotation). */
    private fun signers(pm: PackageManager, file: File?, info: PackageInfo): Pair<Set<String>, Set<String>> {
        if (Build.VERSION.SDK_INT >= 28) {
            val si = info.signingInfo
            if (si != null) {
                val now = si.apkContentsSigners.orEmpty().map { UpdateLogic.sha256(it.toByteArray()) }.toSet()
                val history = if (si.hasMultipleSigners()) emptySet() else si.signingCertificateHistory.orEmpty().map { UpdateLogic.sha256(it.toByteArray()) }.toSet()
                if (now.isNotEmpty()) return now to history
            }
        }
        // Aeltere Android-Versionen bzw. fehlende signingInfo bei Archiven
        @Suppress("DEPRECATION")
        val sigs = (if (file != null) pm.getPackageArchiveInfo(file.path, PackageManager.GET_SIGNATURES)
            else pm.getPackageInfo(info.packageName, PackageManager.GET_SIGNATURES))?.signatures
        return sigs.orEmpty().map { UpdateLogic.sha256(it.toByteArray()) }.toSet() to emptySet()
    }
}
