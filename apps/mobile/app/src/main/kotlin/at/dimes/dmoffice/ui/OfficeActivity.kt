package at.dimes.dmoffice.ui

import android.annotation.SuppressLint
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.webkit.CookieManager
import android.webkit.JavascriptInterface
import android.webkit.ValueCallback
import android.webkit.WebChromeClient
import android.webkit.WebResourceError
import android.webkit.WebResourceRequest
import android.webkit.WebView
import android.webkit.WebViewClient
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import at.dimes.dmoffice.AppForeground
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.Office
import at.dimes.dmoffice.files.OfficeSession
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

/**
 * Office-Dokument im Editor des Office-Servers (Collabora ueber WOPI, ONLYOFFICE/Euro-Office ueber die Docs-API),
 * wie web/src/files/Office.tsx. Eigene Activity, damit Drehen und Tastatur den Editor nicht neu laden.
 * Zurueck schliesst den Editor; gespeichert wird vom Office-Server ueber dmOffice.
 */
class OfficeActivity : ComponentActivity() {
    private var fileCallback: ValueCallback<Array<Uri>>? = null
    private val pickFile = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { r ->
        fileCallback?.onReceiveValue(WebChromeClient.FileChooserParams.parseResult(r.resultCode, r.data))
        fileCallback = null
    }

    /** Dateiauswahl fuer den Editor (Bild einfuegen o. ae.). */
    fun chooseFile(callback: ValueCallback<Array<Uri>>, params: WebChromeClient.FileChooserParams): Boolean {
        fileCallback?.onReceiveValue(null)
        fileCallback = callback
        return try { pickFile.launch(params.createIntent()); true } catch (_: Exception) { fileCallback = null; false }
    }

    override fun onStart() {
        super.onStart()
        // Laenger als eine Minute weg und App-Sperre an: Editor zu, MainActivity sperrt
        if (AppForeground.started() && App.get(this).prefs.settings.value.appLock) { AppForeground.lockPending = true; finish() }
    }

    override fun onStop() { super.onStop(); AppForeground.stopped() }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        val path = intent.getStringExtra(EXTRA_PATH) ?: run { finish(); return }
        val name = intent.getStringExtra(EXTRA_NAME) ?: path.substringAfterLast('/')
        val view = intent.getBooleanExtra(EXTRA_VIEW, false)
        setResult(RESULT_OK, Intent().putExtra(EXTRA_PATH, path))
        setContent { AppTheme { OfficeEditor(path, name, view, onClose = { finish() }) } }
    }

    companion object {
        const val EXTRA_PATH = "path"
        const val EXTRA_NAME = "name"
        const val EXTRA_VIEW = "view"
        private const val PREFS = "office"
        private const val KEY_MOBILE = "mobile_ui"

        fun intent(context: Context, path: String, name: String, view: Boolean): Intent =
            Intent(context, OfficeActivity::class.java).putExtra(EXTRA_PATH, path).putExtra(EXTRA_NAME, name).putExtra(EXTRA_VIEW, view)

        /** ONLYOFFICE: Handy-Oberflaeche (type "mobile") statt der normalen. Die freie Ausgabe bearbeitet dort nicht. */
        fun mobileUi(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_MOBILE, false)
        fun setMobileUi(context: Context, on: Boolean) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_MOBILE, on).apply()
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun OfficeEditor(path: String, name: String, view: Boolean, onClose: () -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    var session by remember { mutableStateOf<OfficeSession?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var loaded by remember { mutableStateOf(false) }
    var attempt by remember { mutableIntStateOf(0) }
    var mobile by remember { mutableStateOf(OfficeActivity.mobileUi(ctx)) }
    var menu by remember { mutableStateOf(false) }
    val textUnreachable = stringResource(R.string.office_unreachable)
    val textFailed = stringResource(R.string.office_failed)
    BackHandler(onBack = onClose)

    LaunchedEffect(attempt) {
        session = null; error = null; loaded = false
        try { session = withContext(Dispatchers.IO) { app.files.officeOpen(path, view) } }
        catch (e: Exception) { error = e.message ?: e.toString() }
    }
    // Kein Signal vom Editor (anderer Office-Server): die Ladeanzeige nach einer Weile trotzdem weg
    LaunchedEffect(session, mobile) { if (session != null) { delay(15_000); loaded = true } }

    Scaffold(topBar = {
        TopAppBar(
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            title = {
                Column {
                    Text(name, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium)
                    val s = session
                    if (s != null && !s.write) Text(stringResource(R.string.office_view_only), style = MaterialTheme.typography.labelSmall)
                }
            },
            actions = {
                Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text(stringResource(R.string.office_reload)) }, onClick = { menu = false; attempt++ })
                        if (session?.api != null) DropdownMenuItem(
                            text = { Text((if (mobile) "✓  " else "     ") + stringResource(R.string.office_mobile_ui)) },
                            onClick = { menu = false; mobile = !mobile; OfficeActivity.setMobileUi(ctx, mobile); loaded = false })
                    }
                }
            })
    }) { pad ->
        Box(Modifier.padding(pad).fillMaxSize().background(Color.White)) {
            val s = session
            if (s != null) androidx.compose.runtime.key(s, mobile) {
                OfficeWebView(s, if (s.api != null && mobile) "mobile" else null,
                    onReady = { loaded = true }, onClose = onClose,
                    onError = { msg -> error = when (msg) { "api" -> textUnreachable; "" -> textFailed; else -> msg } })
            }
            val e = error
            if (e != null) Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surface).padding(24.dp),
                verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
                Text(stringResource(R.string.error_generic, e), color = MaterialTheme.colorScheme.error)
                Button(onClick = { attempt++ }, modifier = Modifier.padding(top = 16.dp)) { Text(stringResource(R.string.office_reload)) }
                Button(onClick = onClose, modifier = Modifier.padding(top = 8.dp)) { Text(stringResource(R.string.close)) }
            } else if (!loaded) Column(Modifier.fillMaxSize().background(MaterialTheme.colorScheme.surface),
                verticalArrangement = Arrangement.Center, horizontalAlignment = Alignment.CenterHorizontally) {
                CircularProgressIndicator()
                Text(stringResource(R.string.office_loading), modifier = Modifier.padding(top = 12.dp))
            }
        }
    }
}

/** WebView mit dem Editor: WOPI per Formular-POST, Docs-API ueber eine eigene Seite mit api.js. */
@SuppressLint("SetJavaScriptEnabled", "JavascriptInterface")
@Composable
private fun OfficeWebView(s: OfficeSession, type: String?, onReady: () -> Unit, onClose: () -> Unit, onError: (String) -> Unit) {
    val ctx = LocalContext.current
    val base = App.get(ctx).prefs.account.value?.baseUrl ?: ""
    AndroidView(modifier = Modifier.fillMaxSize(), factory = { c ->
        WebView(c).apply {
            // Sonst wrap_content: Seiten mit height:100% (der Editor) fallen auf 0 zusammen
            layoutParams = android.view.ViewGroup.LayoutParams(android.view.ViewGroup.LayoutParams.MATCH_PARENT, android.view.ViewGroup.LayoutParams.MATCH_PARENT)
            settings.javaScriptEnabled = true
            settings.domStorageEnabled = true
            settings.mediaPlaybackRequiresUserGesture = true
            settings.setSupportZoom(true)
            settings.builtInZoomControls = true
            settings.displayZoomControls = false
            settings.setSupportMultipleWindows(false)
            CookieManager.getInstance().setAcceptCookie(true)
            CookieManager.getInstance().setAcceptThirdPartyCookies(this, true)
            // Meldungen der Editor-Seite (nur bei der Docs-API; Collabora schickt sie an das Elternfenster, das es hier nicht gibt)
            addJavascriptInterface(object {
                @JavascriptInterface fun ready() { post(onReady) }
                @JavascriptInterface fun close() { post(onClose) }
                @JavascriptInterface fun error(msg: String?) { post { onError(msg ?: "") } }
            }, "dmOffice")
            webViewClient = object : WebViewClient() {
                override fun onPageFinished(view: WebView, url: String?) {
                    if (s.url == null || url == null || url == "about:blank") return
                    onReady()
                    // WOPI ohne einbettende Seite: der Editor schickt seine Nachrichten an window.parent - das ist er selbst.
                    // Schliessen-Knopf (UI_Close) an die App weitergeben, Host_PostmessageReady beantworten.
                    view.evaluateJavascript(Office.WOPI_BRIDGE, null)
                }
                override fun onReceivedError(view: WebView, request: WebResourceRequest, error: WebResourceError) {
                    if (request.isForMainFrame) onError(error.description?.toString() ?: "")
                }
                override fun shouldOverrideUrlLoading(view: WebView, request: WebResourceRequest): Boolean {
                    val scheme = request.url.scheme?.lowercase()
                    if (scheme == "http" || scheme == "https") return false
                    try { view.context.startActivity(Intent(Intent.ACTION_VIEW, request.url)) } catch (_: Exception) { }
                    return true
                }
            }
            webChromeClient = object : WebChromeClient() {
                override fun onShowFileChooser(view: WebView, callback: ValueCallback<Array<Uri>>, params: FileChooserParams): Boolean =
                    (view.context as? OfficeActivity)?.chooseFile(callback, params) ?: false
                override fun onConsoleMessage(m: android.webkit.ConsoleMessage): Boolean {
                    android.util.Log.d("Office", "${m.messageLevel()} ${m.message()} (${m.sourceId()}:${m.lineNumber()})"); return true
                }
            }
            // "Herunterladen als" im Editor: im Browser bzw. einer App oeffnen
            setDownloadListener { url, _, _, _, _ -> try { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url))) } catch (_: Exception) { } }
            if (s.url != null) postUrl(s.url, Office.wopiForm(s))
            else loadDataWithBaseURL("$base/", Office.docsHtml(s, type), "text/html", "utf-8", null)
        }
    }, onRelease = { w ->
        w.stopLoading()
        w.loadUrl("about:blank")
        w.destroy()
    })
}
