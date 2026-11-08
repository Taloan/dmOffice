package at.dimes.dmoffice.ui

import android.content.Context
import at.dimes.dmoffice.App
import at.dimes.dmoffice.files.Thumbs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

/**
 * Galerie und Tagebuch still vorladen, damit der Reiter beim Antippen sofort da ist: Liste in den Zwischenspeicher der
 * Repos, dazu die Vorschaubilder der ersten Seite (Plattenspeicher von [Thumbs]). Einmal nach dem Start und nach
 * längerer Pause - nicht ständig (Akku, Mobilfunk).
 */
object Preload {
    private const val FIRST_THUMBS = 90
    private const val PAUSE_MS = 30 * 60_000L
    @Volatile private var last = 0L

    suspend fun run(ctx: Context) {
        val now = System.currentTimeMillis()
        if (now - last < PAUSE_MS) return
        last = now
        // Erst die Startseite fertig zeigen lassen
        delay(1500)
        val app = App.get(ctx)
        val (order, more) = TabBar.get(ctx)
        val tabs = order + more
        if (Tab.Journal in tabs) try { app.journal.entries() } catch (_: Exception) { }
        if (Tab.Gallery in tabs) try {
            val photos = withContext(Dispatchers.IO) { app.files.photos() }
            for (p in photos.take(FIRST_THUMBS)) try { Thumbs.load(ctx, p, Thumbs.SMALL) } catch (_: Exception) { }
        } catch (_: Exception) { }
    }
}
