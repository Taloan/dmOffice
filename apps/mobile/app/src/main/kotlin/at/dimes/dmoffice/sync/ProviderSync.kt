package at.dimes.dmoffice.sync

import android.Manifest
import android.content.ContentProviderOperation
import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.database.Cursor
import android.net.Uri
import android.provider.CalendarContract
import android.provider.CalendarContract.Events
import android.util.Log
import at.dimes.dmoffice.App
import at.dimes.dmoffice.data.ItemEntity
import at.dimes.dmoffice.data.Items
import at.dimes.dmoffice.data.KIND_EVENT
import at.dimes.dmoffice.ical.Component
import at.dimes.dmoffice.ical.Ical
import at.dimes.dmoffice.ical.IcalTime
import at.dimes.dmoffice.ical.Prop
import at.dimes.dmoffice.ical.Recurrence
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZoneOffset

/**
 * Abgleich der Kalender mit der Android-Datenbank (CalendarContract) in beide Richtungen, ueber das
 * eigene Android-Konto ([AndroidAccount]). Kontakte: [ProviderContacts].
 *
 * dmOffice -> Android: jeder Termin eine Zeile (_SYNC_ID = href, SYNC_DATA1 = Pruefsumme des iCalendar),
 * Ausnahmen einer Serie als eigene Zeilen (ORIGINAL_SYNC_ID). Zeilen mit DIRTY bleiben unangetastet.
 * Android -> dmOffice: Zeilen mit DIRTY/DELETED werden in die lokale Datenbank uebernommen (wie eine
 * Aenderung in der App, dirty = true) und gehen mit dem naechsten [SyncEngine]-Lauf zum Server; 412 dort
 * heisst wie immer: Server gewinnt, die lokale Version bleibt als Konfliktkopie.
 *
 * Was sich nicht sicher abbilden laesst (RDATE, THISANDFUTURE-Ausnahmen, Einladungen anderer), ist in
 * Android schreibgeschuetzt (fremder Organisator); Aenderungen daran werden verworfen und neu gespiegelt.
 * Die Daten liegen dort unverschluesselt.
 */
object ProviderSync {
    private const val TAG = "dmOffice.Provider"
    // Alter Einbahn-Spiegel: lokales Konto "dmOffice" (nur lesbar)
    private const val LEGACY_ACCOUNT = "dmOffice"
    /** Erhoehen, wenn sich die Abbildung aendert: dann schreibt der naechste Abgleich alle Termine neu. */
    private const val MAPPING = 1
    private val lock = Mutex()

    fun hasCalendarPermission(ctx: Context) =
        ctx.checkSelfPermission(Manifest.permission.WRITE_CALENDAR) == PackageManager.PERMISSION_GRANTED && ctx.checkSelfPermission(Manifest.permission.READ_CALENDAR) == PackageManager.PERMISSION_GRANTED
    fun hasContactsPermission(ctx: Context) =
        ctx.checkSelfPermission(Manifest.permission.WRITE_CONTACTS) == PackageManager.PERMISSION_GRANTED && ctx.checkSelfPermission(Manifest.permission.READ_CONTACTS) == PackageManager.PERMISSION_GRANTED

    private fun asSync(uri: Uri, name: String, type: String): Uri = uri.buildUpon()
        .appendQueryParameter(CalendarContract.CALLER_IS_SYNCADAPTER, "true")
        .appendQueryParameter(CalendarContract.Calendars.ACCOUNT_NAME, name)
        .appendQueryParameter(CalendarContract.Calendars.ACCOUNT_TYPE, type).build()
    private fun asSync(uri: Uri, acc: android.accounts.Account): Uri = asSync(uri, acc.name, acc.type)

    private fun hashOf(ics: String, readOnly: Boolean) = "$MAPPING:${ics.hashCode()}:${if (readOnly) 1 else 0}"

    // ---------------- dmOffice -> Android ----------------

    suspend fun syncCalendars(ctx: Context) {
        if (!hasCalendarPermission(ctx)) return
        lock.withLock {
            removeLegacyCalendars(ctx)
            val acc = AndroidAccount.ensure(ctx) ?: return
            val app = App.get(ctx)
            val cr = ctx.contentResolver
            val zone = ZoneId.systemDefault()
            try {
                val cals = app.db.calendars().list().filter { it.supportsEvents }
                val existing = HashMap<String, Long>()
                cr.query(asSync(CalendarContract.Calendars.CONTENT_URI, acc), arrayOf(CalendarContract.Calendars._ID, CalendarContract.Calendars._SYNC_ID),
                    "${CalendarContract.Calendars.ACCOUNT_NAME}=? AND ${CalendarContract.Calendars.ACCOUNT_TYPE}=?", arrayOf(acc.name, acc.type), null)?.use { c ->
                    while (c.moveToNext()) existing[c.getString(1) ?: ""] = c.getLong(0)
                }
                val events = app.db.items().eventsInRangeNow(Long.MIN_VALUE, Long.MAX_VALUE).filter { it.kind == KIND_EVENT }.groupBy { it.calendarHref }
                val keep = HashSet<Long>()
                for (cal in cals) {
                    val values = ContentValues().apply {
                        put(CalendarContract.Calendars.ACCOUNT_NAME, acc.name)
                        put(CalendarContract.Calendars.ACCOUNT_TYPE, acc.type)
                        put(CalendarContract.Calendars.NAME, cal.href)
                        put(CalendarContract.Calendars.CALENDAR_DISPLAY_NAME, cal.name)
                        put(CalendarContract.Calendars.CALENDAR_COLOR, cal.color ?: at.dimes.dmoffice.ui.DEFAULT_ACCENT.toInt())
                        // Bearbeitbar in Samsung- und Google-Kalender
                        put(CalendarContract.Calendars.CALENDAR_ACCESS_LEVEL, CalendarContract.Calendars.CAL_ACCESS_OWNER)
                        put(CalendarContract.Calendars.OWNER_ACCOUNT, acc.name)
                        put(CalendarContract.Calendars.SYNC_EVENTS, 1)
                        put(CalendarContract.Calendars._SYNC_ID, cal.href)
                        put(CalendarContract.Calendars.CALENDAR_TIME_ZONE, zone.id)
                        // Eine Erinnerung je Termin wie im iCalendar der App; keine Teilnehmer
                        put(CalendarContract.Calendars.ALLOWED_REMINDERS, "${CalendarContract.Reminders.METHOD_ALERT}")
                        put(CalendarContract.Calendars.ALLOWED_AVAILABILITY, "${Events.AVAILABILITY_BUSY},${Events.AVAILABILITY_FREE}")
                        put(CalendarContract.Calendars.ALLOWED_ATTENDEE_TYPES, "${CalendarContract.Attendees.TYPE_NONE}")
                        put(CalendarContract.Calendars.MAX_REMINDERS, 1)
                    }
                    val id = existing[cal.href]?.also { cr.update(ContentUris.withAppendedId(asSync(CalendarContract.Calendars.CONTENT_URI, acc), it), values, null, null) }
                        ?: ContentUris.parseId(cr.insert(asSync(CalendarContract.Calendars.CONTENT_URI, acc), values.apply { put(CalendarContract.Calendars.VISIBLE, if (cal.visible) 1 else 0) })!!)
                    keep.add(id)
                    syncEvents(ctx, acc, id, events[cal.href].orEmpty(), zone)
                }
                for ((_, id) in existing) if (id !in keep) cr.delete(ContentUris.withAppendedId(asSync(CalendarContract.Calendars.CONTENT_URI, acc), id), null, null)
            } catch (e: Exception) { Log.w(TAG, "calendar sync failed", e) }
        }
    }

    private class Row(val id: Long, val syncId: String?, val dirty: Boolean, val deleted: Boolean, val hash: String?, val origSyncId: String?, val origId: Long?)

    private fun Cursor.str(col: String): String? = getColumnIndex(col).let { if (it < 0 || isNull(it)) null else getString(it) }
    private fun Cursor.long(col: String): Long? = getColumnIndex(col).let { if (it < 0 || isNull(it)) null else getLong(it) }

    private fun syncEvents(ctx: Context, acc: android.accounts.Account, calId: Long, items: List<ItemEntity>, zone: ZoneId) {
        val cr = ctx.contentResolver
        val login = App.get(ctx).prefs.account.value?.login ?: ""
        val rows = ArrayList<Row>()
        cr.query(asSync(Events.CONTENT_URI, acc), arrayOf(Events._ID, Events._SYNC_ID, Events.DIRTY, Events.DELETED, Events.SYNC_DATA1, Events.ORIGINAL_SYNC_ID, Events.ORIGINAL_ID),
            "${Events.CALENDAR_ID}=?", arrayOf(calId.toString()), null)?.use { c ->
            while (c.moveToNext()) rows += Row(c.getLong(0), c.str(Events._SYNC_ID), c.long(Events.DIRTY) == 1L, c.long(Events.DELETED) == 1L,
                c.str(Events.SYNC_DATA1), c.str(Events.ORIGINAL_SYNC_ID), c.long(Events.ORIGINAL_ID))
        }
        val masters = rows.filter { it.origSyncId == null && it.origId == null }
        val bySync = masters.filter { it.syncId != null }.associateBy { it.syncId!! }
        fun exceptionsOf(m: Row) = rows.filter { it.origId == m.id || (m.syncId != null && it.origSyncId == m.syncId) }
        // Lokal geaendert und noch nicht abgeholt: Hauptzeile samt Ausnahmen stehen lassen
        val pending = rows.filter { it.dirty || it.deleted }
        val protectedMasters = HashSet<Long>()
        for (r in pending) protectedMasters.add(if (r.origId != null) r.origId else masters.firstOrNull { m -> r.origSyncId != null && m.syncId == r.origSyncId }?.id ?: r.id)
        val keep = HashSet<Long>(pending.map { it.id })

        // Gebuendelt schreiben (applyBatch): einzeln dauern ein paar hundert Termine mehrere Sekunden
        val ops = ArrayList<ContentProviderOperation>()
        fun flush() { if (ops.isNotEmpty()) { cr.applyBatch(CalendarContract.AUTHORITY, ops); ops.clear() } }
        val eventsUri = asSync(Events.CONTENT_URI, acc)
        val remindersUri = asSync(CalendarContract.Reminders.CONTENT_URI, acc)

        for (it in items) {
            val cal = Items.parseCalendar(it.ics) ?: continue
            val comps = cal.of("VEVENT")
            val m = comps.firstOrNull { c -> c.get("RECURRENCE-ID") == null } ?: continue
            val start = Ical.time(m.get("DTSTART"), zone) ?: continue
            val readOnly = readOnly(cal, login)
            val hash = hashOf(it.ics, readOnly)
            val row = bySync[it.href]
            if (row != null && (row.id in protectedMasters || row.hash == hash)) {
                keep.add(row.id); exceptionsOf(row).forEach { e -> keep.add(e.id) }
                continue
            }
            // Rueckverweise (withValueBackReference) gelten nur innerhalb eines Buendels: nur zwischen Terminen leeren
            if (ops.size >= 300) flush()
            val organizer = if (readOnly) organizerOf(m) ?: "readonly@dmoffice.invalid" else acc.name
            val v = baseValues(calId, m, start, zone, organizer).apply {
                put(Events._SYNC_ID, it.href)
                put(Events.SYNC_DATA1, hash)
                put(Events.UID_2445, it.uid)
                val dur = Recurrence.durationOf(m, zone)
                val rrule = m.get("RRULE")?.value
                if (rrule != null) {
                    put(Events.RRULE, rrule)
                    put(Events.DURATION, if (start.allDay) "P${maxOf(1, dur / 86400)}D" else "P${maxOf(0, dur)}S")
                    putNull(Events.DTEND)
                    val ex = exdateColumn(m)
                    if (ex != null) put(Events.EXDATE, ex) else putNull(Events.EXDATE)
                } else {
                    putNull(Events.RRULE); putNull(Events.DURATION); putNull(Events.EXDATE)
                }
            }
            val reminder = Items.reminderSeconds(m)
            val masterRef: Int
            if (row != null) {
                keep.add(row.id)
                masterRef = -1
                ops += ContentProviderOperation.newUpdate(ContentUris.withAppendedId(eventsUri, row.id)).withValues(v).build()
                ops += ContentProviderOperation.newDelete(remindersUri).withSelection("${CalendarContract.Reminders.EVENT_ID}=?", arrayOf(row.id.toString())).build()
                reminder?.let { r -> ops += reminderOp(remindersUri, r).withValue(CalendarContract.Reminders.EVENT_ID, row.id).build() }
            } else {
                masterRef = ops.size
                ops += ContentProviderOperation.newInsert(eventsUri).withValues(v).build()
                reminder?.let { r -> ops += reminderOp(remindersUri, r).withValueBackReference(CalendarContract.Reminders.EVENT_ID, masterRef).build() }
            }
            // Ausnahmen der Serie (RECURRENCE-ID) als eigene Zeilen
            val oldExceptions = row?.let { r -> exceptionsOf(r).associateBy { e -> e.syncId ?: "" } } ?: emptyMap()
            for (o in comps) {
                val rid = o.get("RECURRENCE-ID") ?: continue
                val inst = instanceTime(rid, zone) ?: continue
                val os = Ical.time(o.get("DTSTART"), zone) ?: continue
                val syncId = it.href + "#" + rid.value
                val ov = baseValues(calId, o, os, zone, organizer, m).apply {
                    put(Events._SYNC_ID, syncId)
                    put(Events.ORIGINAL_SYNC_ID, it.href)
                    put(Events.ORIGINAL_INSTANCE_TIME, inst)
                    put(Events.ORIGINAL_ALL_DAY, if (start.allDay) 1 else 0)
                    putNull(Events.RRULE); putNull(Events.DURATION); putNull(Events.EXDATE)
                }
                val oRem = Items.reminderSeconds(o) ?: reminder
                val old = oldExceptions[syncId]
                if (old != null) {
                    keep.add(old.id)
                    ops += ContentProviderOperation.newUpdate(ContentUris.withAppendedId(eventsUri, old.id)).withValues(ov).withValue(Events.ORIGINAL_ID, row!!.id).build()
                    ops += ContentProviderOperation.newDelete(remindersUri).withSelection("${CalendarContract.Reminders.EVENT_ID}=?", arrayOf(old.id.toString())).build()
                    oRem?.let { r -> ops += reminderOp(remindersUri, r).withValue(CalendarContract.Reminders.EVENT_ID, old.id).build() }
                } else {
                    val b = ContentProviderOperation.newInsert(eventsUri).withValues(ov)
                    if (row != null) b.withValue(Events.ORIGINAL_ID, row.id) else b.withValueBackReference(Events.ORIGINAL_ID, masterRef)
                    val at = ops.size
                    ops += b.build()
                    oRem?.let { r -> ops += reminderOp(remindersUri, r).withValueBackReference(CalendarContract.Reminders.EVENT_ID, at).build() }
                }
            }
        }
        for (r in rows) if (r.id !in keep) ops += ContentProviderOperation.newDelete(ContentUris.withAppendedId(eventsUri, r.id)).build()
        flush()
    }

    private fun reminderOp(uri: Uri, seconds: Long) = ContentProviderOperation.newInsert(uri)
        .withValue(CalendarContract.Reminders.MINUTES, (seconds / 60).toInt()).withValue(CalendarContract.Reminders.METHOD, CalendarContract.Reminders.METHOD_ALERT)

    /** Gemeinsame Felder fuer Hauptzeile und Ausnahme; Ausnahmen erben fehlende Texte von der Serie [parent]. */
    private fun baseValues(calId: Long, c: Component, start: IcalTime, zone: ZoneId, organizer: String, parent: Component? = null) = ContentValues().apply {
        put(Events.CALENDAR_ID, calId)
        put(Events.TITLE, (c.text("SUMMARY") ?: parent?.text("SUMMARY"))?.trim().orEmpty())
        put(Events.EVENT_LOCATION, c.text("LOCATION") ?: parent?.text("LOCATION"))
        put(Events.DESCRIPTION, c.text("DESCRIPTION") ?: parent?.text("DESCRIPTION"))
        put(Events.ALL_DAY, if (start.allDay) 1 else 0)
        put(Events.EVENT_TIMEZONE, if (start.allDay) "UTC" else (start.zone ?: zone).id)
        val startMs = startMs(start)
        put(Events.DTSTART, startMs)
        if (parent != null || c.get("RRULE") == null) {
            val dur = Recurrence.durationOf(c, zone)
            put(Events.DTEND, startMs + (if (start.allDay) maxOf(1, dur / 86400) * 86400_000 else maxOf(0, dur) * 1000))
        }
        put(Events.ORGANIZER, organizer)
        put(Events.GUESTS_CAN_MODIFY, 0)
        put(Events.HAS_ATTENDEE_DATA, 1)
        put(Events.STATUS, when (c.text("STATUS")?.uppercase()) {
            "CANCELLED" -> Events.STATUS_CANCELED; "TENTATIVE" -> Events.STATUS_TENTATIVE; else -> Events.STATUS_CONFIRMED
        })
        put(Events.AVAILABILITY, if (c.text("TRANSP").equals("TRANSPARENT", true)) Events.AVAILABILITY_FREE else Events.AVAILABILITY_BUSY)
        put(Events.HAS_ALARM, if (Items.reminderSeconds(c) ?: parent?.let { p -> Items.reminderSeconds(p) } != null) 1 else 0)
    }

    /** Android rechnet ganztaegige Termine in UTC (Mitternacht), alles andere als Zeitpunkt. */
    private fun startMs(t: IcalTime): Long = if (t.allDay) t.date!!.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli() else t.instant!!.toEpochMilli()

    private fun instanceTime(rid: Prop, zone: ZoneId): Long? = Ical.time(rid, zone)?.let { startMs(it) }

    /** EXDATE-Spalte im Android-Format: je Eigenschaft eine Zeile "Zone;Wert,Wert" (ohne Zone: UTC bzw. Datum). */
    private fun exdateColumn(m: Component): String? {
        val lines = m.all("EXDATE").map { p ->
            val tz = p.param("TZID")
            val values = p.value.split(',').map { it.trim() }.filter { it.isNotEmpty() }.joinToString(",")
            if (tz != null && !values.endsWith("Z") && values.length > 8) "$tz;$values" else values
        }.filter { it.isNotEmpty() }
        return lines.takeIf { it.isNotEmpty() }?.joinToString("\n")
    }

    /** Zeitpunkte (ms wie [startMs]) aus der Android-EXDATE-Spalte. */
    private fun parseExdateColumn(s: String?, zone: ZoneId): Set<Long> {
        if (s.isNullOrBlank()) return emptySet()
        val out = HashSet<Long>()
        for (line in s.split('\n')) {
            val tz = line.substringBefore(';', "").takeIf { it.isNotEmpty() }
            for (v in line.substringAfter(';').split(',')) {
                val p = Prop("EXDATE", v.trim())
                if (tz != null) p.with("TZID", tz)
                Ical.time(p, zone)?.let { out.add(startMs(it)) }
            }
        }
        return out
    }

    private fun organizerOf(m: Component): String? = m.get("ORGANIZER")?.value?.removePrefix("mailto:")?.removePrefix("MAILTO:")?.takeIf { it.isNotBlank() }

    /**
     * Nicht sicher in Android bearbeitbar: zusaetzliche Termine per RDATE, Ausnahmen "diese und folgende"
     * (RANGE) und Einladungen mit fremdem Organisator (Teilnehmer gleicht die App nicht ab).
     */
    private fun readOnly(cal: Component, login: String): Boolean {
        val evs = cal.of("VEVENT")
        val m = evs.firstOrNull { it.get("RECURRENCE-ID") == null } ?: return true
        if (m.get("RDATE") != null) return true
        if (evs.any { it.get("RECURRENCE-ID")?.param("RANGE") != null }) return true
        if (evs.any { it.get("ATTENDEE") != null }) {
            val org = organizerOf(m) ?: return false
            val me = login.lowercase()
            val o = org.lowercase()
            return !(o == me || (!me.contains('@') && o.substringBefore('@') == me))
        }
        return false
    }

    fun removeCalendars(ctx: Context) {
        if (!hasCalendarPermission(ctx)) return
        try {
            removeLegacyCalendars(ctx)
            // Der Kalender-Provider verlangt beim Sync-Adapter-Zugriff Kontoname und -typ in der Adresse
            for (acc in android.accounts.AccountManager.get(ctx).getAccountsByType(AndroidAccount.type(ctx)))
                ctx.contentResolver.delete(asSync(CalendarContract.Calendars.CONTENT_URI, acc),
                    "${CalendarContract.Calendars.ACCOUNT_NAME}=? AND ${CalendarContract.Calendars.ACCOUNT_TYPE}=?", arrayOf(acc.name, acc.type))
        } catch (e: Exception) { Log.w(TAG, "remove calendars failed", e) }
    }

    /** Umstieg vom alten Einbahn-Spiegel: lokales Konto "dmOffice" entfernen, sonst stuende alles doppelt da. */
    private fun removeLegacyCalendars(ctx: Context) {
        try {
            ctx.contentResolver.delete(asSync(CalendarContract.Calendars.CONTENT_URI, LEGACY_ACCOUNT, CalendarContract.ACCOUNT_TYPE_LOCAL),
                "${CalendarContract.Calendars.ACCOUNT_NAME}=? AND ${CalendarContract.Calendars.ACCOUNT_TYPE}=?", arrayOf(LEGACY_ACCOUNT, CalendarContract.ACCOUNT_TYPE_LOCAL))
        } catch (e: Exception) { Log.w(TAG, "remove legacy calendars failed", e) }
    }

    // ---------------- Android -> dmOffice ----------------

    private class AndroidEvent(
        val id: Long, val syncId: String?, val calId: Long, val dirty: Boolean, val deleted: Boolean,
        val origId: Long?, val origSyncId: String?, val origInstance: Long?,
        val title: String?, val location: String?, val description: String?,
        val dtstart: Long?, val dtend: Long?, val duration: String?, val allDay: Boolean, val tz: String?,
        val rrule: String?, val exdate: String?, val status: Int?,
    )

    private val EVENT_COLUMNS = arrayOf(Events._ID, Events._SYNC_ID, Events.CALENDAR_ID, Events.DIRTY, Events.DELETED, Events.ORIGINAL_ID, Events.ORIGINAL_SYNC_ID,
        Events.ORIGINAL_INSTANCE_TIME, Events.TITLE, Events.EVENT_LOCATION, Events.DESCRIPTION, Events.DTSTART, Events.DTEND, Events.DURATION,
        Events.ALL_DAY, Events.EVENT_TIMEZONE, Events.RRULE, Events.EXDATE, Events.STATUS)

    private fun Cursor.event() = AndroidEvent(
        getLong(0), str(Events._SYNC_ID), getLong(2), long(Events.DIRTY) == 1L, long(Events.DELETED) == 1L,
        long(Events.ORIGINAL_ID), str(Events.ORIGINAL_SYNC_ID), long(Events.ORIGINAL_INSTANCE_TIME),
        str(Events.TITLE), str(Events.EVENT_LOCATION), str(Events.DESCRIPTION),
        long(Events.DTSTART), long(Events.DTEND), str(Events.DURATION), long(Events.ALL_DAY) == 1L, str(Events.EVENT_TIMEZONE),
        str(Events.RRULE), str(Events.EXDATE), long(Events.STATUS)?.toInt(),
    )

    /**
     * In Samsung-/Google-Kalender Geaendertes in die lokale Datenbank holen (danach DIRTY zuruecksetzen).
     * Vor dem [SyncEngine]-Lauf aufrufen, der es zum Server schickt. true, wenn etwas uebernommen wurde.
     */
    suspend fun collectCalendar(ctx: Context): Boolean {
        val app = App.get(ctx)
        if (!app.prefs.settings.value.androidCalendar || !hasCalendarPermission(ctx)) return false
        return lock.withLock {
            val acc = AndroidAccount.current(ctx) ?: return@withLock false
            try { collect(ctx, acc) } catch (e: Exception) { Log.w(TAG, "collect calendar failed", e); false }
        }
    }

    private suspend fun collect(ctx: Context, acc: android.accounts.Account): Boolean {
        val app = App.get(ctx)
        val cr = ctx.contentResolver
        val zone = ZoneId.systemDefault()
        val login = app.prefs.account.value?.login ?: ""
        val calHref = HashMap<Long, String>()
        cr.query(asSync(CalendarContract.Calendars.CONTENT_URI, acc), arrayOf(CalendarContract.Calendars._ID, CalendarContract.Calendars._SYNC_ID),
            "${CalendarContract.Calendars.ACCOUNT_NAME}=? AND ${CalendarContract.Calendars.ACCOUNT_TYPE}=?", arrayOf(acc.name, acc.type), null)?.use { c ->
            while (c.moveToNext()) c.getString(1)?.let { calHref[c.getLong(0)] = it }
        }
        if (calHref.isEmpty()) return false
        val eventsUri = asSync(Events.CONTENT_URI, acc)
        fun query(sel: String, args: Array<String>?): List<AndroidEvent> {
            val out = ArrayList<AndroidEvent>()
            cr.query(eventsUri, EVENT_COLUMNS, sel, args, null)?.use { c -> while (c.moveToNext()) out += c.event() }
            return out
        }
        val calSel = "${Events.CALENDAR_ID} IN (${calHref.keys.joinToString(",")})"
        val pendingSel = "$calSel AND (${Events.DIRTY}=1 OR ${Events.DELETED}=1)"
        val isMaster: (AndroidEvent) -> Boolean = { it.origId == null && it.origSyncId == null }
        var changed = false
        fun rowUri(id: Long) = ContentUris.withAppendedId(eventsUri, id)
        fun clean(id: Long, extra: ContentValues.() -> Unit = {}) { cr.update(rowUri(id), ContentValues().apply { put(Events.DIRTY, 0); extra() }, null, null) }

        // 1. Hauptzeilen (einzelne Termine und Serien)
        for (r in query(pendingSel, null).filter(isMaster)) {
            val cal = calHref[r.calId] ?: continue
            if (r.deleted) {
                r.syncId?.let { app.repo.deleteLocal(it) }
                cr.delete(eventsUri, "${Events.ORIGINAL_ID}=?", arrayOf(r.id.toString()))
                cr.delete(rowUri(r.id), null, null)
                changed = true
                continue
            }
            val existing = r.syncId?.let { app.db.items().byHref(it) }
            if (existing != null && Items.parseCalendar(existing.ics)?.let { readOnly(it, login) } != false) {
                // Schreibgeschuetzt: Android-Aenderung verwerfen, naechster Spiegel schreibt die dmOffice-Version zurueck
                clean(r.id) { put(Events.SYNC_DATA1, "") }
                continue
            }
            val input = inputOf(r, reminderOf(cr, acc, r.id), zone)
            if (input == null) { clean(r.id); continue }
            val uid = existing?.uid ?: Items.newUid()
            var href = r.syncId?.takeIf { it.startsWith(cal) } ?: Items.hrefFor(cal, uid)
            var etag = existing?.etag
            if (existing != null && existing.calendarHref != cal) {
                // In einen anderen Kalender verschoben: dort neu, hier loeschen
                app.repo.deleteLocal(existing.href)
                href = Items.hrefFor(cal, uid); etag = null
            }
            var ics = Items.writeEvent(existing?.ics, uid, input, zone)
            ics = addExdates(ics, parseExdateColumn(r.exdate, zone), zone)
            app.repo.storeLocal(href, cal, etag, ics)
            clean(r.id) { put(Events._SYNC_ID, href); put(Events.SYNC_DATA1, hashOf(ics, false)) }
            changed = true
        }

        // 2. Ausnahmen einzelner Vorkommen (neu abfragen: die Hauptzeilen haben jetzt ihre _SYNC_ID)
        for (r in query(pendingSel, null).filterNot(isMaster)) {
            val masterRow = r.origId?.let { id -> query("${Events._ID}=?", arrayOf(id.toString())).firstOrNull() }
                ?: r.origSyncId?.let { s -> query("$calSel AND ${Events._SYNC_ID}=?", arrayOf(s)).firstOrNull() }
            val masterHref = masterRow?.syncId ?: r.origSyncId
            val master = masterHref?.let { app.db.items().byHref(it) }
            val cal = master?.let { Items.parseCalendar(it.ics) }
            val m = cal?.of("VEVENT")?.firstOrNull { it.get("RECURRENCE-ID") == null }
            if (master == null || cal == null || m == null || r.origInstance == null || readOnly(cal, login)) {
                // Ohne sichere Serie: lokale Ausnahme verwerfen, gespiegelte neu schreiben lassen
                if (r.syncId == null || r.deleted) cr.delete(rowUri(r.id), null, null) else clean(r.id)
                masterRow?.let { mr -> clean(mr.id) { put(Events.SYNC_DATA1, "") } }
                continue
            }
            val rid = recurrenceId(m, r.origInstance, zone)
            cal.children.removeAll { c -> c.name == "VEVENT" && c.get("RECURRENCE-ID")?.let { p -> instanceTime(p, zone) } == r.origInstance }
            if (r.deleted || r.status == Events.STATUS_CANCELED) {
                // Ein Vorkommen geloescht: EXDATE an der Serie
                m.props.add(Prop("EXDATE", rid.value).also { p -> p.params.addAll(rid.params) })
                if (r.deleted) cr.delete(rowUri(r.id), null, null) else clean(r.id)
            } else {
                val input = inputOf(r, reminderOf(cr, acc, r.id), zone)
                if (input == null) { clean(r.id); continue }
                val o = Component("VEVENT")
                o.set("UID", master.uid)
                o.props.add(rid)
                o.set("DTSTAMP", Ical.stamp())
                o.set("LAST-MODIFIED", Ical.stamp())
                o.set("SEQUENCE", ((m.text("SEQUENCE")?.toIntOrNull() ?: 0) + 1).toString())
                o.setText("SUMMARY", input.summary)
                o.setText("LOCATION", input.location)
                o.setText("DESCRIPTION", input.description)
                Ical.setTime(o, "DTSTART", input.start, zone)
                Ical.setTime(o, "DTEND", input.end, zone)
                Items.setReminder(o, input.reminderSeconds)
                cal.children.add(o)
                clean(r.id) { put(Events._SYNC_ID, master.href + "#" + rid.value) }
            }
            m.set("DTSTAMP", Ical.stamp())
            m.set("LAST-MODIFIED", Ical.stamp())
            val ics = cal.toString()
            app.repo.storeLocal(master.href, master.calendarHref, master.etag, ics)
            masterRow?.takeIf { mr -> !mr.dirty }?.let { mr -> clean(mr.id) { put(Events.SYNC_DATA1, hashOf(ics, false)) } }
            changed = true
        }
        return changed
    }

    /** RECURRENCE-ID/EXDATE fuer ein Vorkommen im Format des DTSTART der Serie. */
    private fun recurrenceId(m: Component, instance: Long, zone: ZoneId): Prop {
        val ds = m.get("DTSTART")
        val st = Ical.time(ds, zone)
        val i = Instant.ofEpochMilli(instance)
        val tz = ds?.param("TZID")
        return when {
            st?.allDay == true -> Prop("RECURRENCE-ID", Ical.date(LocalDate.ofInstant(i, ZoneOffset.UTC))).with("VALUE", "DATE")
            tz != null -> Prop("RECURRENCE-ID", Ical.local(i, Ical.zone(tz) ?: zone)).with("TZID", tz)
            else -> Prop("RECURRENCE-ID", Ical.utc(i))
        }
    }

    /** Neue Ausnahmedaten aus der Android-Spalte an die Serie haengen (Entfernen kommt in Android nicht vor). */
    private fun addExdates(ics: String, android: Set<Long>, zone: ZoneId): String {
        if (android.isEmpty()) return ics
        val cal = Items.parseCalendar(ics) ?: return ics
        val m = cal.of("VEVENT").firstOrNull { it.get("RECURRENCE-ID") == null } ?: return ics
        if (m.get("RRULE") == null) return ics
        val known = m.all("EXDATE").flatMap { p -> p.value.split(',').mapNotNull { v -> instanceTime(Prop("EXDATE", v.trim()).also { n -> n.params.addAll(p.params) }, zone) } }.toSet()
        val add = android - known
        if (add.isEmpty()) return ics
        for (t in add.sorted()) {
            val rid = recurrenceId(m, t, zone)
            m.props.add(Prop("EXDATE", rid.value).also { p -> p.params.addAll(rid.params) })
        }
        return cal.toString()
    }

    private fun reminderOf(cr: android.content.ContentResolver, acc: android.accounts.Account, eventId: Long): Long? {
        var best: Int? = null
        cr.query(asSync(CalendarContract.Reminders.CONTENT_URI, acc), arrayOf(CalendarContract.Reminders.MINUTES, CalendarContract.Reminders.METHOD),
            "${CalendarContract.Reminders.EVENT_ID}=?", arrayOf(eventId.toString()), null)?.use { c ->
            while (c.moveToNext()) {
                val min = c.getInt(0)
                val method = c.getInt(1)
                if (min < 0 || (method != CalendarContract.Reminders.METHOD_ALERT && method != CalendarContract.Reminders.METHOD_DEFAULT)) continue
                if (best == null || min < best!!) best = min
            }
        }
        return best?.let { it * 60L }
    }

    /** Dauer aus der Android-Spalte: RFC 5545 (PT1H, P1D) oder das Android-uebliche P3600S. */
    private fun androidDuration(s: String?): Long? {
        if (s.isNullOrBlank()) return null
        Ical.durationSeconds(s)?.let { return it }
        Regex("""^([+-])?P(\d+)S$""").find(s.trim())?.let { m -> return (if (m.groupValues[1] == "-") -1 else 1) * m.groupValues[2].toLong() }
        return null
    }

    private fun zoneOf(id: String?, fallback: ZoneId): ZoneId {
        if (id.isNullOrBlank()) return fallback
        if (id.equals("UTC", true) || id.equals("GMT", true) || id.equals("Etc/UTC", true)) return ZoneOffset.UTC
        return try { ZoneId.of(id) } catch (_: Exception) { fallback }
    }

    private fun inputOf(r: AndroidEvent, reminder: Long?, zone: ZoneId): Items.EventInput? {
        val startMs = r.dtstart ?: return null
        val dur = r.dtend?.let { (it - startMs) / 1000 } ?: androidDuration(r.duration) ?: if (r.allDay) 86400 else 3600
        val start: IcalTime
        val end: IcalTime
        if (r.allDay) {
            val d = LocalDate.ofInstant(Instant.ofEpochMilli(startMs), ZoneOffset.UTC)
            start = IcalTime(d, null, null)
            end = IcalTime(d.plusDays(maxOf(1L, (dur + 86399) / 86400)), null, null)
        } else {
            val z = zoneOf(r.tz, zone)
            val s = Instant.ofEpochMilli(startMs)
            start = IcalTime(null, s, z)
            end = IcalTime(null, s.plusSeconds(maxOf(0L, dur)), z)
        }
        return Items.EventInput(r.title?.trim().orEmpty(), r.location?.trim()?.ifEmpty { null }, r.description?.trim()?.ifEmpty { null },
            start, end, r.rrule?.takeIf { it.isNotBlank() }, reminder)
    }

    // ---------------- Ablauf ----------------

    /** Vor dem Abgleich mit dem Server: Aenderungen aus Android uebernehmen. */
    suspend fun beforeSync(ctx: Context) {
        collectCalendar(ctx)
    }

    /** Nach jedem Abgleich aufrufen: spiegelt, was eingeschaltet ist. */
    suspend fun afterSync(ctx: Context) {
        val s = App.get(ctx).prefs.settings.value
        if (s.androidCalendar) syncCalendars(ctx)
        if (s.androidContacts) ProviderContacts.sync(ctx)
    }
}
