package at.dimes.dmoffice.dav

import org.xmlpull.v1.XmlPullParser
import org.xmlpull.v1.XmlPullParserFactory
import java.io.Reader

/**
 * Namespace-bewusster Leser fuer Multistatus-Antworten (RFC 4918). Vergleicht
 * immer Namespace + lokalen Namen, nie das Praefix - d:, D:, x1: oder gar keins,
 * alles gleich.
 */
object Ns {
    const val DAV = "DAV:"
    const val CALDAV = "urn:ietf:params:xml:ns:caldav"
    const val CS = "http://calendarserver.org/ns/"
    const val ICAL = "http://apple.com/ns/ical/"
}

class DavResponse(val href: String) {
    var status: Int? = null                 // Status direkt am response (404 bei geloescht)
    val props = mutableMapOf<Pair<String, String>, DavProp>()   // nur die mit 200
    fun prop(ns: String, name: String): DavProp? = props[ns to name]
    fun text(ns: String, name: String): String? = prop(ns, name)?.text?.takeIf { it.isNotEmpty() }
    val etag: String? get() = text(Ns.DAV, "getetag")
}

class DavProp {
    val sb = StringBuilder()
    val text: String get() = sb.toString().trim()
    /** Kind-Elemente als (Namespace, Name, Attribute). */
    val children = mutableListOf<Triple<String, String, Map<String, String>>>()
    val hrefs = mutableListOf<String>()
}

class Multistatus(val responses: List<DavResponse>, val syncToken: String?)

object DavXml {
    fun parseMultistatus(reader: Reader): Multistatus {
        val f = XmlPullParserFactory.newInstance()
        f.isNamespaceAware = true
        val p = f.newPullParser()
        p.setInput(reader)
        val responses = mutableListOf<DavResponse>()
        var syncToken: String? = null
        var ev = p.eventType
        while (ev != XmlPullParser.END_DOCUMENT) {
            if (ev == XmlPullParser.START_TAG && p.namespace == Ns.DAV) {
                when (p.name) {
                    "response" -> responses.add(readResponse(p))
                    "sync-token" -> if (p.depth == 2) syncToken = p.nextText().trim()
                }
            }
            ev = p.next()
        }
        return Multistatus(responses, syncToken)
    }

    private fun readResponse(p: XmlPullParser): DavResponse {
        val depth = p.depth
        var href = ""
        var status: Int? = null
        val props = mutableMapOf<Pair<String, String>, DavProp>()
        var ev = p.next()
        while (!(ev == XmlPullParser.END_TAG && p.depth == depth)) {
            if (ev == XmlPullParser.START_TAG && p.namespace == Ns.DAV) {
                when (p.name) {
                    "href" -> if (p.depth == depth + 1) href = p.nextText().trim()
                    "status" -> if (p.depth == depth + 1) status = statusCode(p.nextText())
                    "propstat" -> readPropstat(p, props)
                }
            }
            ev = p.next()
        }
        return DavResponse(href).also { it.status = status; it.props.putAll(props) }
    }

    private fun readPropstat(p: XmlPullParser, out: MutableMap<Pair<String, String>, DavProp>) {
        val depth = p.depth
        val local = mutableMapOf<Pair<String, String>, DavProp>()
        var status = 200
        var ev = p.next()
        while (!(ev == XmlPullParser.END_TAG && p.depth == depth)) {
            if (ev == XmlPullParser.START_TAG && p.namespace == Ns.DAV) {
                when (p.name) {
                    "status" -> status = statusCode(p.nextText())
                    "prop" -> readProp(p, local)
                }
            }
            ev = p.next()
        }
        if (status in 200..299) out.putAll(local)
    }

    private fun readProp(p: XmlPullParser, out: MutableMap<Pair<String, String>, DavProp>) {
        val depth = p.depth
        var ev = p.next()
        while (!(ev == XmlPullParser.END_TAG && p.depth == depth)) {
            if (ev == XmlPullParser.START_TAG && p.depth == depth + 1) {
                val key = p.namespace to p.name
                out[key] = readPropValue(p)
            }
            ev = p.next()
        }
    }

    /** Text, Kind-Elemente (z. B. comp name="VEVENT", collection, calendar) und hrefs sammeln. */
    private fun readPropValue(p: XmlPullParser): DavProp {
        val depth = p.depth
        val prop = DavProp()
        var ev = p.next()
        while (!(ev == XmlPullParser.END_TAG && p.depth == depth)) {
            when (ev) {
                XmlPullParser.TEXT, XmlPullParser.CDSECT -> if (p.depth == depth) prop.sb.append(p.text)
                XmlPullParser.START_TAG -> {
                    val attrs = (0 until p.attributeCount).associate { p.getAttributeName(it) to p.getAttributeValue(it) }
                    prop.children.add(Triple(p.namespace, p.name, attrs))
                    if (p.namespace == Ns.DAV && p.name == "href") prop.hrefs.add(p.nextText().trim())
                }
            }
            ev = p.next()
        }
        return prop
    }

    private fun statusCode(s: String): Int = Regex("""HTTP/\d\.\d\s+(\d{3})""").find(s)?.groupValues?.get(1)?.toInt() ?: 200

    fun escape(s: String): String = s.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")
}
