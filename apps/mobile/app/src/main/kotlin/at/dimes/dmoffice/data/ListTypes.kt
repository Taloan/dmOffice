package at.dimes.dmoffice.data

import android.content.Context
import at.dimes.dmoffice.App
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable

/**
 * Listentyp (Aufgaben, Einkauf, Checkliste) und Abteilungen/Kategorien je Aufgabenliste. CalDAV kennt das nicht,
 * darum kommt es ueber die Web-API; zugeordnet wird ueber die Kennung am Ende der CalDAV-Adresse.
 */
object ListTypes {
    const val TASKS = "tasks"; const val SHOP = "shop"; const val CHECK = "check"

    /** Vorschlag beim Tippen: was schon einmal auf der Liste stand oder eingelesen wurde (Web: Vorschlaege uebernehmen). */
    @Serializable data class Known(val name: String = "", val unit: String? = null, val section: String? = null, val count: Int = 0)

    data class Info(val id: Long, val type: String, val sections: List<String>, val history: List<Known> = emptyList(), val done: String? = null)

    @Serializable private data class Coll(val id: Long, val uri: String = "", val listType: String? = null)
    @Serializable private data class Meta(val type: String = TASKS, val sections: List<String> = emptyList(), val history: List<Known> = emptyList(), val done: String? = null)

    private val state = MutableStateFlow<Map<String, Info>>(emptyMap())
    val byUri: StateFlow<Map<String, Info>> = state

    fun uriOf(calendarHref: String) = calendarHref.trimEnd('/').substringAfterLast('/')

    /** Neu holen; ohne Netz bleibt der letzte Stand (oder alles als normale Aufgabenliste). */
    suspend fun refresh(context: Context) = withContext(Dispatchers.IO) {
        val api = App.get(context).api ?: return@withContext
        try {
            val colls: List<Coll> = api.get(api.url("/api/pim/collections/calendars"))
            state.value = colls.filter { it.uri.isNotEmpty() }.associate { c ->
                val type = c.listType ?: TASKS
                val meta = if (type == TASKS) null else try {
                    api.get<Meta>(api.url("/api/pim/lists/${c.id}"))
                } catch (_: Exception) { null }
                c.uri to Info(c.id, type, meta?.sections.orEmpty(), meta?.history.orEmpty().filter { it.name.isNotBlank() }, meta?.done)
            }
        } catch (_: Exception) { /* offline: alter Stand */ }
    }

    /** Abteilungen bzw. Kategorien einer Liste speichern (Reihenfolge = Laufweg); danach neu laden. */
    suspend fun saveSections(context: Context, id: Long, sections: List<String>): Boolean = withContext(Dispatchers.IO) {
        val api = App.get(context).api ?: return@withContext false
        try {
            api.putText(api.url("/api/pim/lists/$id"), kotlinx.serialization.json.buildJsonObject {
                put("sections", kotlinx.serialization.json.JsonArray(sections.map { kotlinx.serialization.json.JsonPrimitive(it) }))
            }.toString())
        } catch (_: Exception) { return@withContext false }
        refresh(context)
        true
    }

    /** Passende Vorschlaege zum Getippten wie im Web: erst die mit dem Anfang, dann die mit dem Teilwort, haeufige zuerst. */
    fun suggest(history: List<Known>, typed: String, limit: Int = 8): List<Known> {
        val q = typed.trim()
        if (q.isEmpty()) return emptyList()
        val byUse = history.sortedWith(compareBy<Known>({ -it.count }, { it.name.lowercase() }))
        return (byUse.filter { it.name.startsWith(q, true) } + byUse.filter { !it.name.startsWith(q, true) && it.name.contains(q, true) }).take(limit)
    }

    /** Wie abgehakte Eintraege erscheinen: "place", "bottom", "hide" - Vorgabe je Typ wie im Web. */
    fun doneMode(i: Info) = i.done ?: if (i.type == CHECK) "place" else "bottom"

    private fun json(vararg pairs: Pair<String, Any?>) = kotlinx.serialization.json.buildJsonObject {
        for ((k, v) in pairs) when (v) {
            null -> {}
            is String -> put(k, kotlinx.serialization.json.JsonPrimitive(v))
            is Boolean -> put(k, kotlinx.serialization.json.JsonPrimitive(v))
            else -> put(k, kotlinx.serialization.json.JsonPrimitive(v.toString()))
        }
    }.toString()

    /** Listentyp und/oder Abgehakte-Modus setzen. */
    suspend fun setMeta(context: Context, id: Long, type: String? = null, done: String? = null): Boolean = withContext(Dispatchers.IO) {
        val api = App.get(context).api ?: return@withContext false
        try { api.putText(api.url("/api/pim/lists/$id"), json("type" to type, "done" to done)) } catch (_: Exception) { return@withContext false }
        refresh(context)
        true
    }

    /** "clear" (Einkaufsliste: Abgehakte loeschen) oder "reset" (Checkliste: alle Haken weg); danach abgleichen. */
    suspend fun action(context: Context, id: Long, what: String): Boolean = withContext(Dispatchers.IO) {
        val api = App.get(context).api ?: return@withContext false
        try { api.postText(api.url("/api/pim/lists/$id/$what"), "{}") } catch (_: Exception) { return@withContext false }
        at.dimes.dmoffice.sync.SyncWorker.now(context)
        true
    }

    /** Einen Vorschlag vergessen (Tippfehler). */
    suspend fun forget(context: Context, id: Long, item: String): Boolean = withContext(Dispatchers.IO) {
        val api = App.get(context).api ?: return@withContext false
        try { api.postText(api.url("/api/pim/lists/$id/forget"), json("item" to item)) } catch (_: Exception) { return@withContext false }
        refresh(context)
        true
    }

    @Serializable data class ImportResult(val found: Int = 0, val added: Int = 0, val sample: List<String> = emptyList())

    /** Vorschlaege importieren (AnyList-HTML oder ein Eintrag je Zeile); ohne [apply] nur zaehlen. */
    suspend fun importText(context: Context, id: Long, text: String, apply: Boolean): ImportResult? = withContext(Dispatchers.IO) {
        val api = App.get(context).api ?: return@withContext null
        val r = try {
            kotlinx.serialization.json.Json { ignoreUnknownKeys = true }.decodeFromString<ImportResult>(
                api.postText(api.url("/api/pim/lists/$id/import"), json("text" to text, "apply" to apply)))
        } catch (_: Exception) { return@withContext null }
        if (apply) refresh(context)
        r
    }

    /** Neue Aufgabenliste anlegen (wie im Web), Typ setzen und abgleichen, damit sie gleich erscheint. */
    suspend fun createList(context: Context, name: String, type: String): Boolean = withContext(Dispatchers.IO) {
        val api = App.get(context).api ?: return@withContext false
        val id = try {
            val res = api.postText(api.url("/api/pim/collections/calendars"), json("name" to name, "components" to "VTODO"))
            kotlinx.serialization.json.Json.parseToJsonElement(res).let { (it as kotlinx.serialization.json.JsonObject)["id"].toString().trim('"').toLong() }
        } catch (_: Exception) { return@withContext false }
        if (type != TASKS) try { api.putText(api.url("/api/pim/lists/$id"), json("type" to type)) } catch (_: Exception) { }
        at.dimes.dmoffice.sync.SyncWorker.now(context)
        refresh(context)
        true
    }

    // Abteilung raten - nur, wenn es sie in der Liste gibt; laengster Treffer gewinnt (wie shopParse.ts im Web)
    private val guesses: List<Pair<String, List<String>>> = listOf(
        "Obst & Gemüse" to listOf("apfel", "äpfel", "banane", "birne", "tomate", "gurke", "salat", "kartoffel", "zwiebel", "knoblauch", "paprika",
            "möhre", "karotte", "zitrone", "limette", "orange", "mandarine", "beeren", "trauben", "pilz", "champignon", "zucchini", "brokkoli",
            "blumenkohl", "lauch", "porree", "kräuter", "petersilie", "schnittlauch", "basilikum", "avocado", "ingwer", "obst", "gemüse",
            "radieschen", "sellerie", "spargel", "kohl", "mango", "ananas", "kiwi", "melone"),
        "Brot & Backwaren" to listOf("brot", "brötchen", "toast", "croissant", "brezel", "baguette", "kuchen", "semmel"),
        "Kühlregal" to listOf("milch", "joghurt", "jogurt", "quark", "butter", "sahne", "eier", "margarine", "schmand", "frischkäse", "pudding",
            "hefe", "skyr", "kefir", "buttermilch", "tofu"),
        "Fleisch & Wurst" to listOf("fleisch", "hack", "hähnchen", "huhn", "pute", "schnitzel", "wurst", "schinken", "salami", "steak", "speck",
            "würstchen", "aufschnitt", "lachs", "fisch"),
        "Käse" to listOf("käse", "gouda", "mozzarella", "parmesan", "feta", "camembert", "emmentaler"),
        "Tiefkühl" to listOf("tiefkühl", "tk-", "tk ", "pizza", "eis", "pommes", "fischstäbchen"),
        "Konserven & Vorrat" to listOf("nudeln", "pasta", "spaghetti", "reis", "mehl", "zucker", "salz", "pfeffer", "öl", "essig", "tomatenmark",
            "passierte", "müsli", "haferflocken", "kaffee", "tee", "honig", "marmelade", "gewürz", "brühe", "linsen", "bohnen", "mais",
            "senf", "ketchup", "mayo", "backpulver", "konserve"),
        "Getränke" to listOf("wasser", "saft", "bier", "wein", "cola", "limo", "sprudel", "schorle", "sekt"),
        "Süßes & Snacks" to listOf("schokolade", "schoki", "chips", "kekse", "gummibärchen", "nüsse", "bonbons", "riegel", "cracker"),
        "Drogerie" to listOf("shampoo", "duschgel", "zahnpasta", "zahnbürste", "zahnseide", "deo", "seife", "creme", "taschentücher", "windeln",
            "rasier", "pflaster", "wattestäbchen", "sonnencreme"),
        "Haushalt" to listOf("spülmittel", "waschmittel", "weichspüler", "müllbeutel", "küchenrolle", "toilettenpapier", "klopapier", "alufolie",
            "frischhaltefolie", "backpapier", "schwamm", "batterien", "glühbirne", "spültabs", "reiniger"),
    )

    fun guessSection(item: String, sections: List<String>): String? {
        val name = item.lowercase()
        var best: Pair<String, Int>? = null
        for ((section, words) in guesses) {
            if (section !in sections) continue
            for (w in words) if (name.contains(w) && (best == null || w.length > best.second)) best = section to w.length
        }
        return best?.first
    }

    data class ShopItem(val qty: String, val unit: String, val item: String) {
        /** Titel wie im Web: "2 kg Kartoffeln". */
        fun title() = listOf(qty, unit, item).filter { it.isNotBlank() }.joinToString(" ")
    }

    private val units = listOf("kg", "g", "mg", "l", "ml", "cl", "dl", "Stk", "Stück", "St", "Pck", "Pkg", "Packung", "Packungen", "Pack", "Dose", "Dosen",
        "Flasche", "Flaschen", "Fl", "Glas", "Gläser", "Becher", "Bund", "Beutel", "Tüte", "Tüten", "Netz", "Schale", "Schalen",
        "Kiste", "Kisten", "Kasten", "Rolle", "Rollen", "Tube", "Tuben", "Prise", "EL", "TL", "Tasse", "Tassen", "Scheibe", "Scheiben",
        "x", "pcs", "pack", "can", "cans", "bottle", "bottles", "jar", "bag", "box", "lb", "oz")
    private val itemRe = Regex("^\\s*(\\d+(?:[.,]\\d+)?|½|¼|¾)\\s*(?:(" + units.joinToString("|") { Regex.escape(it) } + ")\\.?(?=\\s|$))?\\s+(.+)$", RegexOption.IGNORE_CASE)

    /** "2 kg Kartoffeln" in Menge, Einheit, Artikel zerlegen (wie shopParse.ts im Web). */
    fun parseItem(text: String): ShopItem {
        val m = itemRe.find(text) ?: return ShopItem("", "", text.trim())
        val u = m.groupValues[2].let { g -> if (g.isEmpty()) "" else units.firstOrNull { it.equals(g, true) } ?: g }
        return ShopItem(m.groupValues[1], u, m.groupValues[3].trim())
    }

    private fun prop(ics: String, name: String): String? =
        Regex("(?m)^" + name + ":(.*)\r?$").find(ics)?.groupValues?.get(1)?.replace("\\,", ",")?.replace("\\;", ";")?.replace("\\\\", "\\")?.trim()

    /** Menge, Einheit, Artikel aus den X-DM-Feldern - oder aus dem Titel, wenn er anderswo geaendert wurde. */
    fun itemOf(ics: String, title: String): ShopItem {
        val f = ShopItem(prop(ics, "X-DM-QTY") ?: "", prop(ics, "X-DM-UNIT") ?: "", prop(ics, "X-DM-ITEM") ?: "")
        return if (f.item.isNotEmpty() && f.title() == title.trim()) f else parseItem(title)
    }

    private val sectionRe = Regex("(?m)^X-DM-SECTION:(.*)\r?$")
    /** Abteilung bzw. Kategorie aus dem gespeicherten VTODO. */
    fun sectionOf(ics: String): String? =
        sectionRe.find(ics)?.groupValues?.get(1)?.replace("\\,", ",")?.replace("\\;", ";")?.replace("\\\\", "\\")?.trim()?.ifEmpty { null }
}
