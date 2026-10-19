package at.dimes.dmoffice.sync

import android.Manifest
import android.content.ContentProviderOperation
import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.CalendarContract
import android.provider.ContactsContract
import android.util.Log
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.Contact
import at.dimes.dmoffice.data.Items
import at.dimes.dmoffice.data.KIND_EVENT
import at.dimes.dmoffice.ical.Ical
import at.dimes.dmoffice.ical.Recurrence
import java.time.ZoneId
import java.time.ZoneOffset

/**
 * Spiegelt Kalender und Kontakte auf Wunsch in die Android-Datenbanken (CalendarContract,
 * ContactsContract), damit Samsung-Kalender, Telefon, WhatsApp und andere sie sehen.
 * Einbahnstrasse dmOffice -> Android; dort liegen die Daten im Klartext. Kalender als lokales
 * Konto ("LOCAL", nur lesbar fuer andere Apps), Kontakte als Geraetekontakte mit Quelle "dmoffice".
 */
object ProviderSync {
    private const val TAG = "dmOffice.Provider"
    private const val ACCOUNT = "dmOffice"
    private const val SOURCE = "dmoffice"

    fun hasCalendarPermission(ctx: Context) =
        ctx.checkSelfPermission(Manifest.permission.WRITE_CALENDAR) == PackageManager.PERMISSION_GRANTED && ctx.checkSelfPermission(Manifest.permission.READ_CALENDAR) == PackageManager.PERMISSION_GRANTED
    fun hasContactsPermission(ctx: Context) =
        ctx.checkSelfPermission(Manifest.permission.WRITE_CONTACTS) == PackageManager.PERMISSION_GRANTED && ctx.checkSelfPermission(Manifest.permission.READ_CONTACTS) == PackageManager.PERMISSION_GRANTED

    private fun asSync(uri: Uri): Uri = uri.buildUpon()
        .appendQueryParameter(CalendarContract.CALLER_IS_SYNCADAPTER, "true")
        .appendQueryParameter(CalendarContract.Calendars.ACCOUNT_NAME, ACCOUNT)
        .appendQueryParameter(CalendarContract.Calendars.ACCOUNT_TYPE, CalendarContract.ACCOUNT_TYPE_LOCAL).build()

    // ---------------- Kalender ----------------

    suspend fun syncCalendars(ctx: Context) {
        if (!hasCalendarPermission(ctx)) return
        val app = App.get(ctx)
        val cr = ctx.contentResolver
        val zone = ZoneId.systemDefault()
        try {
            val cals = app.db.calendars().list()
            // Vorhandene Android-Kalender dieses Kontos
            val existing = HashMap<String, Long>()
            cr.query(asSync(CalendarContract.Calendars.CONTENT_URI), arrayOf(CalendarContract.Calendars._ID, CalendarContract.Calendars._SYNC_ID),
                "${CalendarContract.Calendars.ACCOUNT_NAME}=? AND ${CalendarContract.Calendars.ACCOUNT_TYPE}=?", arrayOf(ACCOUNT, CalendarContract.ACCOUNT_TYPE_LOCAL), null)?.use { c ->
                while (c.moveToNext()) existing[c.getString(1) ?: ""] = c.getLong(0)
            }
            val keep = HashSet<Long>()
            for (cal in cals) {
                val values = ContentValues().apply {
                    put(CalendarContract.Calendars.ACCOUNT_NAME, ACCOUNT)
                    put(CalendarContract.Calendars.ACCOUNT_TYPE, CalendarContract.ACCOUNT_TYPE_LOCAL)
                    put(CalendarContract.Calendars.NAME, cal.href)
                    put(CalendarContract.Calendars.CALENDAR_DISPLAY_NAME, "dmOffice · " + cal.name)
                    put(CalendarContract.Calendars.CALENDAR_COLOR, cal.color ?: at.dimes.dmoffice.ui.DEFAULT_ACCENT.toInt())
                    put(CalendarContract.Calendars.CALENDAR_ACCESS_LEVEL, CalendarContract.Calendars.CAL_ACCESS_READ)
                    put(CalendarContract.Calendars.OWNER_ACCOUNT, ACCOUNT)
                    put(CalendarContract.Calendars.VISIBLE, if (cal.visible) 1 else 0)
                    put(CalendarContract.Calendars.SYNC_EVENTS, 1)
                    put(CalendarContract.Calendars._SYNC_ID, cal.href)
                    put(CalendarContract.Calendars.CALENDAR_TIME_ZONE, zone.id)
                }
                val id = existing[cal.href]?.also { cr.update(ContentUris.withAppendedId(asSync(CalendarContract.Calendars.CONTENT_URI), it), values, null, null) }
                    ?: ContentUris.parseId(cr.insert(asSync(CalendarContract.Calendars.CONTENT_URI), values)!!)
                keep.add(id)
                syncEvents(ctx, id, cal.href, zone)
            }
            for ((href, id) in existing) if (id !in keep) cr.delete(ContentUris.withAppendedId(asSync(CalendarContract.Calendars.CONTENT_URI), id), null, null)
        } catch (e: Exception) { Log.w(TAG, "calendar sync failed", e) }
    }

    private suspend fun syncEvents(ctx: Context, calId: Long, calendarHref: String, zone: ZoneId) {
        val app = App.get(ctx)
        val cr = ctx.contentResolver
        val items = app.db.items().eventsInRangeNow(Long.MIN_VALUE, Long.MAX_VALUE).filter { it.calendarHref == calendarHref && it.kind == KIND_EVENT }
        val existing = HashMap<String, Long>()
        cr.query(asSync(CalendarContract.Events.CONTENT_URI), arrayOf(CalendarContract.Events._ID, CalendarContract.Events._SYNC_ID),
            "${CalendarContract.Events.CALENDAR_ID}=?", arrayOf(calId.toString()), null)?.use { c -> while (c.moveToNext()) existing[c.getString(1) ?: ""] = c.getLong(0) }
        // Gebuendelt schreiben (applyBatch): einzeln dauern ein paar hundert Termine mehrere Sekunden
        val keep = HashSet<Long>()
        val ops = ArrayList<ContentProviderOperation>()
        fun flush() { if (ops.isNotEmpty()) { cr.applyBatch(CalendarContract.AUTHORITY, ops); ops.clear() } }
        for (it in items) {
            val cal = Items.parseCalendar(it.ics) ?: continue
            val m = cal.of("VEVENT").firstOrNull { c -> c.get("RECURRENCE-ID") == null } ?: continue
            val start = Ical.time(m.get("DTSTART"), zone) ?: continue
            val v = ContentValues().apply {
                put(CalendarContract.Events.CALENDAR_ID, calId)
                put(CalendarContract.Events._SYNC_ID, it.href)
                put(CalendarContract.Events.TITLE, it.summary)
                put(CalendarContract.Events.EVENT_LOCATION, it.location)
                put(CalendarContract.Events.DESCRIPTION, it.description)
                put(CalendarContract.Events.ALL_DAY, if (start.allDay) 1 else 0)
                val tz = if (start.allDay) "UTC" else (start.zone ?: zone).id
                put(CalendarContract.Events.EVENT_TIMEZONE, tz)
                val startMs = if (start.allDay) start.date!!.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli() else start.instant!!.toEpochMilli()
                put(CalendarContract.Events.DTSTART, startMs)
                val dur = Recurrence.durationOf(m, zone)
                val rrule = m.get("RRULE")?.value
                if (rrule != null) {
                    put(CalendarContract.Events.RRULE, rrule)
                    put(CalendarContract.Events.DURATION, if (start.allDay) "P${maxOf(1, dur / 86400)}D" else Ical.duration(dur))
                    putNull(CalendarContract.Events.DTEND)
                    m.all("EXDATE").flatMap { ex -> ex.value.split(',') }.takeIf { l -> l.isNotEmpty() }?.let { l -> put(CalendarContract.Events.EXDATE, l.joinToString(",")) }
                } else {
                    put(CalendarContract.Events.DTEND, startMs + (if (start.allDay) maxOf(1, dur / 86400) * 86400_000 else dur * 1000))
                    putNull(CalendarContract.Events.RRULE)
                    putNull(CalendarContract.Events.DURATION)
                }
                put(CalendarContract.Events.HAS_ALARM, if (it.reminderSeconds != null) 1 else 0)
            }
            // Rueckverweise (withValueBackReference) gelten nur innerhalb eines Buendels: nur zwischen Terminen leeren
            if (ops.size >= 300) flush()
            // Erinnerung: eine je Termin, wie im iCalendar
            val reminder = it.reminderSeconds?.let { s ->
                ContentProviderOperation.newInsert(asSync(CalendarContract.Reminders.CONTENT_URI))
                    .withValue(CalendarContract.Reminders.MINUTES, (s / 60).toInt()).withValue(CalendarContract.Reminders.METHOD, CalendarContract.Reminders.METHOD_ALERT)
            }
            val id = existing[it.href]
            if (id != null) {
                keep.add(id)
                ops += ContentProviderOperation.newUpdate(ContentUris.withAppendedId(asSync(CalendarContract.Events.CONTENT_URI), id)).withValues(v).build()
                ops += ContentProviderOperation.newDelete(asSync(CalendarContract.Reminders.CONTENT_URI))
                    .withSelection("${CalendarContract.Reminders.EVENT_ID}=?", arrayOf(id.toString())).build()
                reminder?.let { r -> ops += r.withValue(CalendarContract.Reminders.EVENT_ID, id).build() }
            } else {
                val at = ops.size
                ops += ContentProviderOperation.newInsert(asSync(CalendarContract.Events.CONTENT_URI)).withValues(v).build()
                reminder?.let { r -> ops += r.withValueBackReference(CalendarContract.Reminders.EVENT_ID, at).build() }
            }
        }
        for ((_, id) in existing) if (id !in keep) ops += ContentProviderOperation.newDelete(ContentUris.withAppendedId(asSync(CalendarContract.Events.CONTENT_URI), id)).build()
        flush()
    }

    fun removeCalendars(ctx: Context) {
        if (!hasCalendarPermission(ctx)) return
        try {
            ctx.contentResolver.delete(asSync(CalendarContract.Calendars.CONTENT_URI), "${CalendarContract.Calendars.ACCOUNT_NAME}=? AND ${CalendarContract.Calendars.ACCOUNT_TYPE}=?", arrayOf(ACCOUNT, CalendarContract.ACCOUNT_TYPE_LOCAL))
        } catch (e: Exception) { Log.w(TAG, "remove calendars failed", e) }
    }

    // ---------------- Kontakte ----------------

    private fun rawUri(): Uri = ContactsContract.RawContacts.CONTENT_URI.buildUpon().appendQueryParameter(ContactsContract.CALLER_IS_SYNCADAPTER, "true").build()
    private fun dataUri(): Uri = ContactsContract.Data.CONTENT_URI.buildUpon().appendQueryParameter(ContactsContract.CALLER_IS_SYNCADAPTER, "true").build()

    suspend fun syncContacts(ctx: Context) {
        if (!hasContactsPermission(ctx)) return
        val app = App.get(ctx)
        val cr = ctx.contentResolver
        try {
            val contacts = app.contacts.allNow()
            val existing = HashMap<String, Long>()
            cr.query(rawUri(), arrayOf(ContactsContract.RawContacts._ID, ContactsContract.RawContacts.SOURCE_ID), "${ContactsContract.RawContacts.SYNC1}=?", arrayOf(SOURCE), null)?.use { c ->
                while (c.moveToNext()) existing[c.getString(1) ?: ""] = c.getLong(0)
            }
            val keep = HashSet<Long>()
            for (c in contacts) {
                val ops = ArrayList<ContentProviderOperation>()
                val rawId = existing[c.id]
                val ref: Int
                if (rawId == null) {
                    ops.add(ContentProviderOperation.newInsert(rawUri())
                        .withValue(ContactsContract.RawContacts.ACCOUNT_NAME, null).withValue(ContactsContract.RawContacts.ACCOUNT_TYPE, null)
                        .withValue(ContactsContract.RawContacts.SOURCE_ID, c.id).withValue(ContactsContract.RawContacts.SYNC1, SOURCE).build())
                    ref = 0
                } else {
                    // Alle Datenzeilen neu schreiben
                    ops.add(ContentProviderOperation.newDelete(dataUri()).withSelection("${ContactsContract.Data.RAW_CONTACT_ID}=?", arrayOf(rawId.toString())).build())
                    ref = -1
                }
                fun row(mime: String, vararg kv: Pair<String, Any?>) {
                    val b = ContentProviderOperation.newInsert(dataUri()).withValue(ContactsContract.Data.MIMETYPE, mime)
                    if (rawId == null) b.withValueBackReference(ContactsContract.Data.RAW_CONTACT_ID, ref) else b.withValue(ContactsContract.Data.RAW_CONTACT_ID, rawId)
                    for ((k, v) in kv) if (v != null && !(v is String && v.isEmpty())) b.withValue(k, v)
                    ops.add(b.build())
                }
                row(ContactsContract.CommonDataKinds.StructuredName.CONTENT_ITEM_TYPE,
                    ContactsContract.CommonDataKinds.StructuredName.DISPLAY_NAME to c.name, ContactsContract.CommonDataKinds.StructuredName.GIVEN_NAME to c.given,
                    ContactsContract.CommonDataKinds.StructuredName.FAMILY_NAME to c.family, ContactsContract.CommonDataKinds.StructuredName.MIDDLE_NAME to c.additional,
                    ContactsContract.CommonDataKinds.StructuredName.PREFIX to c.prefix, ContactsContract.CommonDataKinds.StructuredName.SUFFIX to c.suffix)
                if (c.nickname.isNotBlank()) row(ContactsContract.CommonDataKinds.Nickname.CONTENT_ITEM_TYPE, ContactsContract.CommonDataKinds.Nickname.NAME to c.nickname)
                for (p in c.phones) row(ContactsContract.CommonDataKinds.Phone.CONTENT_ITEM_TYPE, ContactsContract.CommonDataKinds.Phone.NUMBER to p.value,
                    ContactsContract.CommonDataKinds.Phone.TYPE to phoneType(p.type), ContactsContract.CommonDataKinds.Phone.LABEL to if (phoneType(p.type) == ContactsContract.CommonDataKinds.Phone.TYPE_CUSTOM) p.type else null)
                for (e in c.emails) row(ContactsContract.CommonDataKinds.Email.CONTENT_ITEM_TYPE, ContactsContract.CommonDataKinds.Email.ADDRESS to e.value,
                    ContactsContract.CommonDataKinds.Email.TYPE to emailType(e.type))
                if (c.org.isNotBlank() || c.title.isNotBlank()) row(ContactsContract.CommonDataKinds.Organization.CONTENT_ITEM_TYPE,
                    ContactsContract.CommonDataKinds.Organization.COMPANY to c.org, ContactsContract.CommonDataKinds.Organization.TITLE to c.title,
                    ContactsContract.CommonDataKinds.Organization.DEPARTMENT to c.department, ContactsContract.CommonDataKinds.Organization.TYPE to ContactsContract.CommonDataKinds.Organization.TYPE_WORK)
                for (a in c.addresses) row(ContactsContract.CommonDataKinds.StructuredPostal.CONTENT_ITEM_TYPE,
                    ContactsContract.CommonDataKinds.StructuredPostal.STREET to a.street, ContactsContract.CommonDataKinds.StructuredPostal.CITY to a.city,
                    ContactsContract.CommonDataKinds.StructuredPostal.REGION to a.region, ContactsContract.CommonDataKinds.StructuredPostal.POSTCODE to a.zip,
                    ContactsContract.CommonDataKinds.StructuredPostal.COUNTRY to a.country, ContactsContract.CommonDataKinds.StructuredPostal.FORMATTED_ADDRESS to a.text,
                    ContactsContract.CommonDataKinds.StructuredPostal.TYPE to if (a.type.equals("home", true)) ContactsContract.CommonDataKinds.StructuredPostal.TYPE_HOME else ContactsContract.CommonDataKinds.StructuredPostal.TYPE_WORK)
                for (u in c.urls) row(ContactsContract.CommonDataKinds.Website.CONTENT_ITEM_TYPE, ContactsContract.CommonDataKinds.Website.URL to u)
                if (c.note.isNotBlank()) row(ContactsContract.CommonDataKinds.Note.CONTENT_ITEM_TYPE, ContactsContract.CommonDataKinds.Note.NOTE to c.note)
                c.birthday?.takeIf { it.isNotBlank() }?.let { row(ContactsContract.CommonDataKinds.Event.CONTENT_ITEM_TYPE, ContactsContract.CommonDataKinds.Event.START_DATE to it, ContactsContract.CommonDataKinds.Event.TYPE to ContactsContract.CommonDataKinds.Event.TYPE_BIRTHDAY) }
                c.photo?.let { p -> try { val bytes = android.util.Base64.decode(p.substringAfter("base64,"), android.util.Base64.DEFAULT); row(ContactsContract.CommonDataKinds.Photo.CONTENT_ITEM_TYPE, ContactsContract.CommonDataKinds.Photo.PHOTO to bytes) } catch (_: Exception) { } }
                val results = cr.applyBatch(ContactsContract.AUTHORITY, ops)
                keep.add(rawId ?: ContentUris.parseId(results[0].uri!!))
            }
            for ((_, id) in existing) if (id !in keep) cr.delete(ContentUris.withAppendedId(rawUri(), id), null, null)
        } catch (e: Exception) { Log.w(TAG, "contact sync failed", e) }
    }

    fun removeContacts(ctx: Context) {
        if (!hasContactsPermission(ctx)) return
        try { ctx.contentResolver.delete(rawUri(), "${ContactsContract.RawContacts.SYNC1}=?", arrayOf(SOURCE)) } catch (e: Exception) { Log.w(TAG, "remove contacts failed", e) }
    }

    private fun phoneType(t: String) = when (t.lowercase()) {
        "cell", "mobile" -> ContactsContract.CommonDataKinds.Phone.TYPE_MOBILE; "home" -> ContactsContract.CommonDataKinds.Phone.TYPE_HOME
        "work" -> ContactsContract.CommonDataKinds.Phone.TYPE_WORK; "fax" -> ContactsContract.CommonDataKinds.Phone.TYPE_FAX_WORK; "" -> ContactsContract.CommonDataKinds.Phone.TYPE_OTHER
        else -> ContactsContract.CommonDataKinds.Phone.TYPE_CUSTOM
    }
    private fun emailType(t: String) = when (t.lowercase()) {
        "home" -> ContactsContract.CommonDataKinds.Email.TYPE_HOME; "work" -> ContactsContract.CommonDataKinds.Email.TYPE_WORK; else -> ContactsContract.CommonDataKinds.Email.TYPE_OTHER
    }

    /** Nach jedem Abgleich aufrufen: spiegelt, was eingeschaltet ist. */
    suspend fun afterSync(ctx: Context) {
        val s = App.get(ctx).prefs.settings.value
        if (s.androidCalendar) syncCalendars(ctx)
        if (s.androidContacts) syncContacts(ctx)
    }

    /** Alle Vorkommen zwischen zwei Zeitpunkten brauchen wir hier nicht - der Android-Kalender faltet RRULE selbst aus. */
    @Suppress("unused") private val unused = Contact::class
}
