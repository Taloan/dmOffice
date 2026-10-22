package at.dimes.dmoffice.files

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject

/** GET /api/office/config: ist ein Office-Server eingestellt, und welche Endungen kann er? */
@Serializable
data class OfficeConfig(val enabled: Boolean = false, val product: String? = null, val edit: List<String> = emptyList(),
                        val view: List<String> = emptyList(), val create: List<String> = emptyList(), val error: String? = null)

/**
 * POST /api/office/open. WOPI (Collabora): [url] + [token] per Formular-POST. ONLYOFFICE Docs-API: [api] (api.js)
 * und die (signierte) [config] fuer DocsAPI.DocEditor.
 */
@Serializable
data class OfficeSession(val url: String? = null, val token: String? = null, val ttl: Long = 0, val api: String? = null,
                         val config: JsonObject? = null, val name: String = "", val path: String? = null,
                         val write: Boolean = false, val product: String? = null)

/** [mobile]: Handy-Oberflaeche des Office-Servers, wenn er eine hat (WOPI-Discovery mobileEdit/mobileView, z. B. Euro-Office). */
@Serializable data class OfficeOpenBody(val path: String, val view: Boolean = false, val mobile: Boolean? = null)
@Serializable data class OfficeNewBody(val dir: String, val kind: String, val name: String)
@Serializable data class OfficeNewResult(val path: String)

/**
 * Office-Dateien im Editor des Office-Servers (wie web/src/files/Office.tsx). Reine Funktionen, in Unit-Tests pruefbar.
 */
object Office {
    const val EDIT = "edit"
    const val VIEW = "view"

    /** "edit", "view" oder null - je nach Endung und was der Office-Server kann (PDF bleibt im eigenen Betrachter). */
    fun mode(item: FileItem, cfg: OfficeConfig?): String? {
        if (cfg == null || !cfg.enabled || item.isDir) return null
        val ext = item.name.substringAfterLast('.', "").lowercase()
        if (ext.isEmpty()) return null
        if (ext in cfg.edit) return EDIT
        if (ext != "pdf" && ext in cfg.view) return VIEW
        return null
    }

    /** Beim Antippen in den Editor? CSV bleibt wie bisher in einer App (im Web im Texteditor). */
    fun opensOnTap(item: FileItem, cfg: OfficeConfig?): Boolean =
        mode(item, cfg) != null && !item.name.endsWith(".csv", ignoreCase = true)

    /** In die WOPI-Editorseite: PostMessage des Editors (an sich selbst, es gibt kein Elternfenster) an die App. */
    const val WOPI_BRIDGE = """(function () {
  if (window.__dmOffice) return; window.__dmOffice = true;
  window.addEventListener('message', function (e) {
    var m = e.data;
    if (typeof m === 'string') { try { m = JSON.parse(m); } catch (x) { return; } }
    var id = m && m.MessageId;
    if (id === 'UI_Close' || id === 'close') { try { dmOffice.close(); } catch (x) {} }
    else if (id === 'App_LoadingStatus') {
      try { dmOffice.ready(); } catch (x) {}
      var st = m.Values && m.Values.Status;
      if (st === 'Frame_Ready' || st === 'Document_Loaded')
        window.postMessage(JSON.stringify({ MessageId: 'Host_PostmessageReady', SendTime: Date.now(), Values: {} }), '*');
    }
  });
})();"""

    /** Formular fuer den WOPI-Editor: Token per POST, so steht es in keiner Adresse und keinem Verlauf. */
    fun wopiForm(s: OfficeSession): ByteArray =
        ("access_token=" + enc(s.token ?: "") + "&access_token_ttl=" + s.ttl).toByteArray(Charsets.UTF_8)

    /**
     * Seite fuer die ONLYOFFICE Docs-API: api.js laden, Editor fuellt die Seite. Meldungen an die App ueber das
     * JavaScript-Objekt "dmOffice" (ready, close, error). [type] "mobile" erzwingt die Handy-Oberflaeche, sonst die normale.
     */
    fun docsHtml(s: OfficeSession, type: String?): String {
        val cfg = (s.config ?: JsonObject(emptyMap())).toString()
        // In einem <script> darf "</" nicht vorkommen; U+2028/2029 sind in alten JS-Engines Zeilenenden
        val js = cfg.replace("</", "<\\/").replace("\u2028", "\\u2028").replace("\u2029", "\\u2029")
        val typeJs = if (type.isNullOrBlank()) "" else "cfg.type = ${jsString(type)};"
        return """<!doctype html>
<html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<style>html,body{margin:0;padding:0;height:100%;overflow:hidden;background:#fff}#office{width:100%;height:100%}</style>
</head><body><div id="office"></div>
<script>
function dmFail(m) { try { dmOffice.error(m || ''); } catch (e) {} }
function dmStart() {
  if (!window.DocsAPI || !DocsAPI.DocEditor) { dmFail('api'); return; }
  var cfg = $js;
  cfg.width = '100%'; cfg.height = '100%';
  $typeJs
  cfg.events = {
    onAppReady: function () { dmOffice.ready(); },
    onDocumentReady: function () { dmOffice.ready(); },
    onRequestClose: function () { dmOffice.close(); },
    onError: function (e) { dmFail(e && e.data && e.data.errorDescription || ''); }
  };
  try { new DocsAPI.DocEditor('office', cfg); } catch (e) { dmFail(String(e)); }
}
</script>
<script src="${attr(s.api ?: "")}" onload="dmStart()" onerror="dmFail('api')"></script>
</body></html>"""
    }

    /** Vorgeschlagener Name fuer ein neues Dokument (ohne Endung, die haengt der Server an). */
    fun newName(base: String, kind: String, existing: Collection<String>): String {
        val taken = existing.map { it.lowercase() }.toSet()
        if ("$base.$kind".lowercase() !in taken) return base
        var n = 2
        while ("$base ($n).$kind".lowercase() in taken) n++
        return "$base ($n)"
    }

    private fun enc(s: String) = java.net.URLEncoder.encode(s, "UTF-8")
    private fun attr(s: String) = s.replace("&", "&amp;").replace("\"", "&quot;").replace("<", "&lt;").replace(">", "&gt;")
    private fun jsString(s: String) = "'" + s.replace("\\", "\\\\").replace("'", "\\'") + "'"
}
