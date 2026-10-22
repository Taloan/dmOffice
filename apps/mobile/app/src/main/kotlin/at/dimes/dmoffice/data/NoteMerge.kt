package at.dimes.dmoffice.data

/**
 * Dreiseitiger Vergleich von Blocklisten wie NoteMerge.Diff3 in Notizi.Core: Stellen, an denen
 * alle drei uebereinstimmen, sind fest; dazwischen gilt, was sich geaendert hat. Haben beide
 * Seiten dieselbe Stelle verschieden geaendert: null.
 */
object NoteMerge {
    fun diff3(origin: List<String>, mine: List<String>, theirs: List<String>): List<String>? {
        val toMine = match(origin, mine)
        val toTheirs = match(origin, theirs)
        val result = ArrayList<String>()
        var o = 0; var m = 0; var t = 0
        while (true) {
            var n = o
            while (n < origin.size && (toMine[n] < 0 || toTheirs[n] < 0)) n++
            val mEnd = if (n < origin.size) toMine[n] else mine.size
            val tEnd = if (n < origin.size) toTheirs[n] else theirs.size
            val keyO = origin.subList(o, n)
            val keyM = mine.subList(m, mEnd)
            val keyT = theirs.subList(t, tEnd)
            when {
                keyM == keyO -> result.addAll(keyT)
                keyT == keyO || keyT == keyM -> result.addAll(keyM)
                // Eine Seite hat nur angefuegt oder vorangestellt (Einkaufsliste ergaenzt), die andere dort geaendert
                // (abgehakt): beides uebernehmen statt Konflikt
                else -> result.addAll(around(keyO, keyM, keyT) ?: around(keyO, keyT, keyM) ?: return null)
            }
            if (n >= origin.size) break
            result.add(mine[mEnd])
            o = n + 1; m = mEnd + 1; t = tEnd + 1
        }
        return result
    }

    /** added = Ausgang plus Bloecke davor/dahinter, changed = die andere Seite: changed mit den zusaetzlichen Bloecken. */
    private fun around(origin: List<String>, added: List<String>, changed: List<String>): List<String>? {
        if (origin.isEmpty() || added.size <= origin.size) return null
        val extra = added.size - origin.size
        return when {
            added.subList(0, origin.size) == origin -> changed + added.subList(origin.size, added.size)
            added.subList(extra, added.size) == origin -> added.subList(0, extra) + changed
            else -> null
        }
    }

    /** Laengste gemeinsame Teilfolge: fuer jeden Block des Ausgangs seine Stelle in der anderen Version, oder -1. */
    private fun match(a: List<String>, b: List<String>): IntArray {
        val lcs = Array(a.size + 1) { IntArray(b.size + 1) }
        for (i in a.indices.reversed()) for (j in b.indices.reversed())
            lcs[i][j] = if (a[i] == b[j]) lcs[i + 1][j + 1] + 1 else maxOf(lcs[i + 1][j], lcs[i][j + 1])
        val result = IntArray(a.size) { -1 }
        var x = 0; var y = 0
        while (x < a.size && y < b.size) {
            when {
                a[x] == b[y] -> { result[x] = y; x++; y++ }
                lcs[x + 1][y] >= lcs[x][y + 1] -> x++
                else -> y++
            }
        }
        return result
    }
}
