package at.dimes.dmoffice.sync

import android.content.Context
import android.util.Log
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.Account
import at.dimes.dmoffice.data.AppDb
import at.dimes.dmoffice.data.CalendarEntity
import at.dimes.dmoffice.data.ItemEntity
import at.dimes.dmoffice.data.Items
import at.dimes.dmoffice.dav.DavClient
import at.dimes.dmoffice.dav.DavException
import at.dimes.dmoffice.dav.InvalidSyncToken
import at.dimes.dmoffice.dav.PreconditionFailed
import at.dimes.dmoffice.notify.Reminders
import at.dimes.dmoffice.widget.Widgets
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import java.time.ZoneId

/**
 * Abgleich mit dem Server: erst lokale Aenderungen hochladen (PUT mit If-Match,
 * DELETE mit If-Match), dann je Kalender sync-collection (RFC 6578). Bei 412
 * gewinnt der Server; die lokale Version bleibt als Kopie unter neuer UID.
 */
class SyncEngine(private val context: Context, private val db: AppDb) {
    companion object {
        private const val TAG = "dmOffice.Sync"
        val lock = Mutex()
    }

    suspend fun run(account: Account): Result<Unit> = withContext(Dispatchers.IO) {
        lock.withLock {
            val app = App.get(context)
            try {
                val dav = DavClient(account, app.prefs)
                val zone = ZoneId.systemDefault()
                val remote = dav.listCalendars()
                if (remote.isEmpty()) throw DavException(404, context.getString(R.string.no_calendars))
                val known = db.calendars().list().associateBy { it.href }
                for (rc in remote) {
                    val old = known[rc.href]
                    db.calendars().upsert(CalendarEntity(
                        href = rc.href, name = rc.name, color = rc.color,
                        supportsEvents = rc.supportsEvents, supportsTasks = rc.supportsTasks,
                        syncToken = old?.syncToken, ctag = old?.ctag, sortOrder = rc.order, visible = old?.visible ?: true,
                    ))
                }
                val keep = remote.map { it.href }
                db.calendars().deleteMissing(keep)
                db.items().deleteOrphans(keep)

                var conflict = false
                // 1. Lokales hochladen
                for (item in db.items().pending()) {
                    try {
                        if (item.deleted) {
                            dav.delete(item.href, item.etag)
                            db.items().delete(item.href)
                        } else {
                            val etag = dav.put(item.href, item.ics, item.etag)
                            if (etag != null) db.items().upsert(item.copy(etag = etag, dirty = false))
                            else {
                                // Kein ETag in der Antwort: nachladen
                                val (ics, e2) = dav.get(item.href)
                                db.items().upsert(Items.fromIcs(item.href, item.calendarHref, e2, ics, zone) ?: item.copy(dirty = false))
                            }
                        }
                    } catch (_: PreconditionFailed) {
                        conflict = true
                        resolveConflict(dav, item, zone)
                    } catch (e: DavException) {
                        if (e.code == 403) rejectLocal(dav, item, zone)   // nur lesend geteilter Kalender: Aenderung verwerfen
                        else if (e.code == 404 && item.deleted) db.items().delete(item.href)
                        else if (e.code == 404 && item.etag != null) {
                            // Auf dem Server inzwischen weg: als neu anlegen
                            val etag = dav.put(item.href, item.ics, null)
                            db.items().upsert(item.copy(etag = etag, dirty = false))
                        } else throw e
                    }
                }

                // 2. Aenderungen vom Server je Kalender
                for (rc in remote) {
                    val cal = db.calendars().byHref(rc.href) ?: continue
                    // ctag unveraendert und Token vorhanden: nichts zu holen
                    if (cal.syncToken != null && cal.ctag != null && cal.ctag == rc.ctag && rc.ctag != null) continue
                    pull(dav, cal, zone)
                    db.calendars().setToken(rc.href, db.calendars().byHref(rc.href)?.syncToken, rc.ctag)
                }

                if (conflict) app.prefs.setConflictNotice(true)
                app.prefs.setSyncResult(null)
                Reminders.reschedule(context)
                Widgets.updateAll(context)
                Result.success(Unit)
            } catch (e: Exception) {
                Log.w(TAG, "sync failed", e)
                val msg = when {
                    e is DavException && e.code == 401 -> context.getString(R.string.unauthorized)
                    else -> e.message ?: e.javaClass.simpleName
                }
                app.prefs.setSyncResult(msg)
                Widgets.updateAll(context)
                Result.failure(e)
            }
        }
    }

    private suspend fun pull(dav: DavClient, cal: CalendarEntity, zone: ZoneId) {
        var token = cal.syncToken
        val result = try {
            dav.sync(cal.href, token)
        } catch (_: InvalidSyncToken) {
            // Token veraltet: alles neu, lokal Unveraendertes vorher weg
            db.items().deleteCleanOf(cal.href)
            token = null
            dav.sync(cal.href, null)
        }
        if (token == null) {
            // Vollabgleich: alles, was der Server nicht mehr nennt, ist weg (ausser lokal Geaendertem)
            db.items().deleteCleanOf(cal.href)
        }
        val missing = mutableListOf<String>()
        for (o in result.objects) {
            if (o.deleted) {
                val local = db.items().byHref(o.href)
                if (local != null && !local.dirty) db.items().delete(o.href)
                continue
            }
            if (o.ics == null) { missing.add(o.href); continue }
            store(cal, o.href, o.etag, o.ics, zone)
        }
        for (chunk in missing.chunked(50)) for (o in dav.multiget(cal.href, chunk)) {
            if (!o.deleted && o.ics != null) store(cal, o.href, o.etag, o.ics, zone)
        }
        db.calendars().setToken(cal.href, result.syncToken, cal.ctag)
    }

    private suspend fun store(cal: CalendarEntity, href: String, etag: String?, ics: String, zone: ZoneId) {
        val local = db.items().byHref(href)
        // Lokale, noch nicht hochgeladene Aenderung nicht ueberschreiben - der naechste PUT klaert das per If-Match
        if (local != null && (local.dirty || local.deleted)) return
        val entity = Items.fromIcs(href, cal.href, etag, ics, zone) ?: return
        db.items().upsert(entity)
    }

    /** 403: Der Kalender ist nur zum Lesen geteilt - lokale Aenderung verwerfen, Serverfassung wiederherstellen (neu angelegt: weg). */
    private suspend fun rejectLocal(dav: DavClient, item: ItemEntity, zone: ZoneId) {
        if (item.etag == null) { db.items().delete(item.href); return }
        try {
            val (ics, etag) = dav.get(item.href)
            val server = Items.fromIcs(item.href, item.calendarHref, etag, ics, zone)
            if (server != null) db.items().upsert(server) else db.items().delete(item.href)
        } catch (e: DavException) {
            if (e.code == 404) db.items().delete(item.href) else throw e
        }
    }

    /** 412: Serverfassung uebernehmen, eigene Version als Kopie behalten (bei Loeschung: Loeschung verwerfen). */
    private suspend fun resolveConflict(dav: DavClient, item: ItemEntity, zone: ZoneId) {
        val (ics, etag) = try { dav.get(item.href) } catch (e: DavException) {
            if (e.code == 404) { db.items().delete(item.href); return } else throw e
        }
        val server = Items.fromIcs(item.href, item.calendarHref, etag, ics, zone)
        if (server != null) db.items().upsert(server) else db.items().delete(item.href)
        if (item.deleted) return
        val uid = Items.newUid()
        val copy = Items.copyWithNewUid(item.ics, uid, " " + context.getString(R.string.conflict_copy, "").trim()) ?: return
        val href = Items.hrefFor(item.calendarHref, uid)
        val entity = Items.fromIcs(href, item.calendarHref, null, copy, zone, dirty = true) ?: return
        try {
            val e2 = dav.put(href, copy, null)
            db.items().upsert(entity.copy(etag = e2, dirty = false))
        } catch (_: Exception) {
            db.items().upsert(entity)
        }
    }
}
