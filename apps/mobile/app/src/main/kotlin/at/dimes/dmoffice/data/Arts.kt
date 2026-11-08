package at.dimes.dmoffice.data

/**
 * Arten von Terminen (Version 2, Pukki Schritt 2): stehen als Kategorie im Termin (deutscher Name, wie auf dem Server in
 * Chat/PukkiArts.cs). Die App zeigt das Symbol vor dem Titel; ausgewählt wird die Art im Web oder von Pukki gesetzt.
 */
object Arts {
    private val labels: Map<String, String> = listOf(
        "Gesundheit" to "🩺", "Health" to "🩺", "Schule & Kita" to "🎒", "School & daycare" to "🎒",
        "Abholen & Bringen" to "🚸", "Pick-up & drop-off" to "🚸", "Sport" to "⚽", "Sports" to "⚽",
        "Musik & Kurse" to "🎵", "Music & classes" to "🎵", "Familie & Feiern" to "🎉", "Family & celebrations" to "🎉",
        "Freizeit & Kultur" to "🎭", "Leisure & culture" to "🎭", "Reisen" to "✈️", "Travel" to "✈️", "Arbeit" to "💼", "Work" to "💼",
        "Behörden & Finanzen" to "📄", "Authorities & finance" to "📄", "Haushalt & Handwerker" to "🔧", "Household & repairs" to "🔧",
        "Auto" to "🚗", "Car" to "🚗", "Tiere" to "🐾", "Pets" to "🐾",
    ).associate { it.first.lowercase() to it.second }

    /** Symbol der Art aus den CATEGORIES eines Termins (auch gefaltete Zeilen); null: keine Art. */
    fun iconOf(ics: String): String? {
        if (!ics.contains("CATEGORIES", ignoreCase = true)) return null
        val unfolded = ics.replace("\r\n ", "").replace("\n ", "")
        for (line in unfolded.lineSequence()) {
            if (!line.startsWith("CATEGORIES", ignoreCase = true)) continue
            val value = line.substringAfter(':', "")
            for (c in value.split(Regex("(?<!\\\\),")))
                labels[c.replace("\\,", ",").replace("\\;", ";").trim().lowercase()]?.let { return it }
        }
        return null
    }

    /** Das Vorkommen mit Symbol vor dem Titel - nur für die Anzeige (bearbeitet wird der Termin aus der Datenbank). */
    fun withIcon(o: Occurrence): Occurrence {
        if (o.isTask) return o
        val icon = iconOf(o.item.ics) ?: return o
        return o.copy(item = o.item.copy(summary = icon + " " + o.item.summary))
    }
}
