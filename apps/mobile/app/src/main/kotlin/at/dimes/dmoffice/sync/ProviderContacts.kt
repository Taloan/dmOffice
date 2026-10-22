package at.dimes.dmoffice.sync

import android.content.ContentProviderOperation
import android.content.ContentUris
import android.content.ContentValues
import android.content.Context
import android.database.Cursor
import android.net.Uri
import android.provider.ContactsContract
import android.provider.ContactsContract.CommonDataKinds.Email
import android.provider.ContactsContract.CommonDataKinds.Event
import android.provider.ContactsContract.CommonDataKinds.Nickname
import android.provider.ContactsContract.CommonDataKinds.Note
import android.provider.ContactsContract.CommonDataKinds.Organization
import android.provider.ContactsContract.CommonDataKinds.Phone
import android.provider.ContactsContract.CommonDataKinds.Photo
import android.provider.ContactsContract.CommonDataKinds.StructuredName
import android.provider.ContactsContract.CommonDataKinds.StructuredPostal
import android.provider.ContactsContract.CommonDataKinds.Website
import android.provider.ContactsContract.RawContacts
import android.util.Log
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.Address
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.api.Contact
import at.dimes.dmoffice.api.TypedValue
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonPrimitive

/**
 * Kontakte in beide Richtungen: je dmOffice-Kontakt ein RawContact des Android-Kontos
 * (SOURCE_ID = Kontakt-Id, SYNC1 = ETag, SYNC3 = Stand des Fotos). In der Kontakte-App Geaendertes
 * (DIRTY/DELETED) geht ueber die Web-API zum Server, wie aus der App selbst. Bei 409 gewinnt der Server,
 * die Android-Version wird als eigener Kontakt "(Konfliktkopie)" gespeichert.
 */
object ProviderContacts {
    private const val TAG = "dmOffice.Contacts"
    // Alter Einbahn-Spiegel: Geraetekontakte ohne Konto mit SYNC1 = "dmoffice"
    private const val LEGACY_SOURCE = "dmoffice"
    private val lock = Mutex()

    private fun syncUri(uri: Uri, acc: android.accounts.Account): Uri = uri.buildUpon()
        .appendQueryParameter(ContactsContract.CALLER_IS_SYNCADAPTER, "true")
        .appendQueryParameter(RawContacts.ACCOUNT_NAME, acc.name)
        .appendQueryParameter(RawContacts.ACCOUNT_TYPE, acc.type).build()
    private fun plainSync(uri: Uri): Uri = uri.buildUpon().appendQueryParameter(ContactsContract.CALLER_IS_SYNCADAPTER, "true").build()

    private class Raw(val id: Long, val source: String?, val etag: String?, val photoState: String?, val dirty: Boolean, val deleted: Boolean)

    private fun rows(ctx: Context, acc: android.accounts.Account, sel: String? = null): List<Raw> {
        val out = ArrayList<Raw>()
        val where = "${RawContacts.ACCOUNT_NAME}=? AND ${RawContacts.ACCOUNT_TYPE}=?" + (sel?.let { " AND ($it)" } ?: "")
        ctx.contentResolver.query(syncUri(RawContacts.CONTENT_URI, acc),
            arrayOf(RawContacts._ID, RawContacts.SOURCE_ID, RawContacts.SYNC1, RawContacts.SYNC3, RawContacts.DIRTY, RawContacts.DELETED),
            where, arrayOf(acc.name, acc.type), null)?.use { c ->
            while (c.moveToNext()) out += Raw(c.getLong(0), c.getString(1), c.getString(2), c.getString(3), c.getInt(4) == 1, c.getInt(5) == 1)
        }
        return out
    }

    // ---------------- dmOffice -> Android ----------------

    suspend fun sync(ctx: Context) {
        if (!ProviderSync.hasContactsPermission(ctx)) return
        lock.withLock {
            removeLegacy(ctx)
            val acc = AndroidAccount.ensure(ctx) ?: return
            val app = App.get(ctx)
            val cr = ctx.contentResolver
            try {
                ensureVisible(ctx, acc)
                val contacts = app.contacts.allNow()
                val existing = rows(ctx, acc)
                val bySource = existing.filter { it.source != null }.associateBy { it.source!! }
                // Lokal geaendert und noch nicht hochgeladen: nicht ueberschreiben, nicht loeschen
                val keep = HashSet<Long>(existing.filter { it.dirty || it.deleted }.map { it.id })
                for (c in contacts) {
                    val raw = bySource[c.id]
                    if (raw != null) keep.add(raw.id)
                    if (raw != null && (raw.dirty || raw.deleted)) continue
                    if (raw != null && c.etag.isNotEmpty() && raw.etag == c.etag) continue
                    // Die Liste kommt ohne Fotos: geaenderte Kontakte einzeln mit Foto holen
                    val photo = c.photo ?: fetchPhoto(app.api, c)
                    val rawId = write(ctx, acc, c, photo, raw?.id)
                    keep.add(rawId)
                    cr.update(ContentUris.withAppendedId(syncUri(RawContacts.CONTENT_URI, acc), rawId),
                        ContentValues().apply { put(RawContacts.SYNC3, photoState(ctx, acc, rawId)) }, null, null)
                }
                for (r in existing) if (r.id !in keep) cr.delete(ContentUris.withAppendedId(syncUri(RawContacts.CONTENT_URI, acc), r.id), null, null)
            } catch (e: Exception) { Log.w(TAG, "contact sync failed", e) }
        }
    }

    private fun fetchPhoto(api: ApiClient?, c: Contact): String? {
        if (api == null || c.uri.isEmpty()) return null
        return try {
            api.get<Contact>(api.url("/api/pim/contacts/${c.book}/${java.net.URLEncoder.encode(c.uri, "UTF-8").replace("+", "%20")}")).photo
        } catch (e: Exception) { Log.w(TAG, "photo for ${c.id} failed", e); null }
    }

    /** RawContact anlegen bzw. alle Datenzeilen neu schreiben; liefert die RawContact-Id. */
    private fun write(ctx: Context, acc: android.accounts.Account, c: Contact, photo: String?, rawId: Long?): Long {
        val cr = ctx.contentResolver
        val dataUri = syncUri(ContactsContract.Data.CONTENT_URI, acc)
        val ops = ArrayList<ContentProviderOperation>()
        if (rawId == null) {
            ops.add(ContentProviderOperation.newInsert(syncUri(RawContacts.CONTENT_URI, acc))
                .withValue(RawContacts.ACCOUNT_NAME, acc.name).withValue(RawContacts.ACCOUNT_TYPE, acc.type)
                .withValue(RawContacts.SOURCE_ID, c.id).withValue(RawContacts.SYNC1, c.etag).build())
        } else {
            ops.add(ContentProviderOperation.newDelete(dataUri).withSelection("${ContactsContract.Data.RAW_CONTACT_ID}=?", arrayOf(rawId.toString())).build())
            ops.add(ContentProviderOperation.newUpdate(ContentUris.withAppendedId(syncUri(RawContacts.CONTENT_URI, acc), rawId))
                .withValue(RawContacts.SOURCE_ID, c.id).withValue(RawContacts.SYNC1, c.etag).withValue(RawContacts.DIRTY, 0).build())
        }
        fun row(mime: String, vararg kv: Pair<String, Any?>) {
            val b = ContentProviderOperation.newInsert(dataUri).withValue(ContactsContract.Data.MIMETYPE, mime)
            if (rawId == null) b.withValueBackReference(ContactsContract.Data.RAW_CONTACT_ID, 0) else b.withValue(ContactsContract.Data.RAW_CONTACT_ID, rawId)
            for ((k, v) in kv) if (v != null && !(v is String && v.isEmpty())) b.withValue(k, v)
            ops.add(b.build())
        }
        row(StructuredName.CONTENT_ITEM_TYPE,
            StructuredName.DISPLAY_NAME to c.name, StructuredName.GIVEN_NAME to c.given, StructuredName.FAMILY_NAME to c.family,
            StructuredName.MIDDLE_NAME to c.additional, StructuredName.PREFIX to c.prefix, StructuredName.SUFFIX to c.suffix)
        if (c.nickname.isNotBlank()) row(Nickname.CONTENT_ITEM_TYPE, Nickname.NAME to c.nickname)
        for (p in c.phones) row(Phone.CONTENT_ITEM_TYPE, Phone.NUMBER to p.value,
            Phone.TYPE to phoneType(p.type), Phone.LABEL to if (phoneType(p.type) == Phone.TYPE_CUSTOM) p.type else null)
        for (e in c.emails) row(Email.CONTENT_ITEM_TYPE, Email.ADDRESS to e.value, Email.TYPE to emailType(e.type))
        if (c.org.isNotBlank() || c.title.isNotBlank() || c.department.isNotBlank()) row(Organization.CONTENT_ITEM_TYPE,
            Organization.COMPANY to c.org, Organization.TITLE to c.title, Organization.DEPARTMENT to c.department, Organization.TYPE to Organization.TYPE_WORK)
        for (a in c.addresses) row(StructuredPostal.CONTENT_ITEM_TYPE,
            StructuredPostal.STREET to a.street, StructuredPostal.CITY to a.city, StructuredPostal.REGION to a.region,
            StructuredPostal.POSTCODE to a.zip, StructuredPostal.COUNTRY to a.country, StructuredPostal.FORMATTED_ADDRESS to a.text,
            StructuredPostal.TYPE to postalType(a.type))
        for (u in c.urls) row(Website.CONTENT_ITEM_TYPE, Website.URL to u, Website.TYPE to Website.TYPE_OTHER)
        if (c.note.isNotBlank()) row(Note.CONTENT_ITEM_TYPE, Note.NOTE to c.note)
        c.birthday?.takeIf { it.isNotBlank() }?.let { row(Event.CONTENT_ITEM_TYPE, Event.START_DATE to it, Event.TYPE to Event.TYPE_BIRTHDAY) }
        photo?.let { p ->
            try {
                val bytes = android.util.Base64.decode(p.substringAfter("base64,"), android.util.Base64.DEFAULT)
                row(Photo.CONTENT_ITEM_TYPE, Photo.PHOTO to bytes)
            } catch (_: Exception) { }
        }
        val results = cr.applyBatch(ContactsContract.AUTHORITY, ops)
        return rawId ?: ContentUris.parseId(results[0].uri!!)
    }

    /** Stand des Fotos ("Zeile:Version", leer ohne Foto): daran erkennt der Rueckweg, ob es in Android geaendert wurde. */
    private fun photoState(ctx: Context, acc: android.accounts.Account, rawId: Long): String {
        ctx.contentResolver.query(syncUri(ContactsContract.Data.CONTENT_URI, acc), arrayOf(ContactsContract.Data._ID, ContactsContract.Data.DATA_VERSION),
            "${ContactsContract.Data.RAW_CONTACT_ID}=? AND ${ContactsContract.Data.MIMETYPE}=?", arrayOf(rawId.toString(), Photo.CONTENT_ITEM_TYPE), null)?.use { c ->
            if (c.moveToFirst()) return "${c.getLong(0)}:${c.getLong(1)}"
        }
        return ""
    }

    /** Ohne diese Einstellung zeigen manche Kontakte-Apps Kontakte ohne Gruppe nicht an. */
    private fun ensureVisible(ctx: Context, acc: android.accounts.Account) {
        val cr = ctx.contentResolver
        val uri = syncUri(ContactsContract.Settings.CONTENT_URI, acc)
        val exists = cr.query(uri, arrayOf(ContactsContract.Settings.UNGROUPED_VISIBLE), "${ContactsContract.Settings.ACCOUNT_NAME}=? AND ${ContactsContract.Settings.ACCOUNT_TYPE}=?",
            arrayOf(acc.name, acc.type), null)?.use { it.moveToFirst() } ?: false
        if (exists) return
        cr.insert(uri, ContentValues().apply {
            put(ContactsContract.Settings.ACCOUNT_NAME, acc.name); put(ContactsContract.Settings.ACCOUNT_TYPE, acc.type)
            put(ContactsContract.Settings.UNGROUPED_VISIBLE, 1); put(ContactsContract.Settings.SHOULD_SYNC, 1)
        })
    }

    // ---------------- Android -> dmOffice ----------------

    /**
     * In der Kontakte-App Geaendertes zum Server schicken, danach DIRTY zuruecksetzen. Vor
     * ContactsRepo.refresh() aufrufen; true, wenn etwas hochgeladen wurde.
     */
    suspend fun upload(ctx: Context): Boolean {
        val app = App.get(ctx)
        if (!app.prefs.settings.value.androidContacts || !ProviderSync.hasContactsPermission(ctx)) return false
        return lock.withLock {
            val acc = AndroidAccount.current(ctx) ?: return@withLock false
            val api = app.api ?: return@withLock false
            val cr = ctx.contentResolver
            var changed = false
            for (r in rows(ctx, acc, "${RawContacts.DIRTY}=1 OR ${RawContacts.DELETED}=1")) {
                val rawUri = ContentUris.withAppendedId(syncUri(RawContacts.CONTENT_URI, acc), r.id)
                try {
                    val cached = r.source?.let { app.contacts.byId(it) }
                    if (r.deleted) {
                        if (cached != null) try {
                            api.delete(api.url("/api/pim/contacts/${cached.book}/${java.net.URLEncoder.encode(cached.uri, "UTF-8").replace("+", "%20")}"))
                        } catch (e: ApiException) { if (e.code != 404) throw e }
                        cr.delete(rawUri, null, null)
                        changed = true
                        continue
                    }
                    val base = cached ?: Contact(book = defaultBook(app, api) ?: continue)
                    val c = read(ctx, acc, r, base)
                    if (c == null) { cr.update(rawUri, ContentValues().apply { put(RawContacts.DIRTY, 0) }, null, null); continue }
                    val id = try { save(api, c) } catch (e: ApiException) {
                        when (e.code) {
                            // Auf dem Server inzwischen geaendert: Server gewinnt, Android-Version als Kopie
                            409, 412 -> { save(api, asNew(c).copy(displayName = ctx.getString(R.string.conflict_copy, c.name))); r.source }
                            // Auf dem Server inzwischen geloescht: neu anlegen
                            404 -> save(api, asNew(c))
                            else -> throw e
                        }
                    }
                    // SYNC1 leeren: der naechste Spiegel schreibt die Serverfassung (mit neuem ETag) zurueck
                    cr.update(rawUri, ContentValues().apply { put(RawContacts.SOURCE_ID, id); put(RawContacts.SYNC1, ""); put(RawContacts.DIRTY, 0) }, null, null)
                    changed = true
                } catch (e: Exception) { Log.w(TAG, "upload of raw ${r.id} failed", e) }
            }
            changed
        }
    }

    private fun asNew(c: Contact) = c.copy(id = "", uri = "", etag = "", uid = null)

    /** Speichern ueber die Web-API (wie die App selbst); liefert die Kontakt-Id. */
    private fun save(api: ApiClient, c: Contact): String {
        val res: JsonObject = api.postFor(api.url("/api/pim/contacts"), c)
        return res["id"]?.jsonPrimitive?.content ?: c.id
    }

    @kotlinx.serialization.Serializable private data class Book(val id: Long)

    /** Neue Kontakte aus Android: ins Adressbuch, in dem die meisten liegen, sonst das erste. */
    private suspend fun defaultBook(app: App, api: ApiClient): Long? {
        app.contacts.allNow().groupingBy { it.book }.eachCount().maxByOrNull { it.value }?.let { return it.key }
        return try { api.get<List<Book>>(api.url("/api/pim/collections/addressbooks")).firstOrNull()?.id } catch (_: Exception) { null }
    }

    private fun Cursor.str(col: String): String? = getColumnIndex(col).let { if (it < 0 || isNull(it)) null else getString(it) }?.trim()?.takeIf { it.isNotEmpty() }
    private fun Cursor.int(col: String): Int? = getColumnIndex(col).let { if (it < 0 || isNull(it)) null else getInt(it) }

    /** Kontakt aus den Android-Datenzeilen; was Android nicht kennt (VIP, Kategorien ...), bleibt aus [base]. null ohne Inhalt. */
    private fun read(ctx: Context, acc: android.accounts.Account, r: Raw, base: Contact): Contact? {
        val cr = ctx.contentResolver
        var c = base.copy(displayName = "", prefix = "", given = "", additional = "", family = "", suffix = "", nickname = "",
            org = "", department = "", title = "", emails = emptyList(), phones = emptyList(), addresses = emptyList(), urls = emptyList(),
            birthday = null, note = "", photo = null)
        val phones = ArrayList<TypedValue>(); val emails = ArrayList<TypedValue>(); val addresses = ArrayList<Address>(); val urls = ArrayList<String>()
        var photoRow: String? = null
        var any = false
        val cols = arrayOf(ContactsContract.Data._ID, ContactsContract.Data.MIMETYPE, ContactsContract.Data.DATA_VERSION) +
            (1..14).map { "data$it" }.toTypedArray()
        cr.query(syncUri(ContactsContract.Data.CONTENT_URI, acc), cols, "${ContactsContract.Data.RAW_CONTACT_ID}=?", arrayOf(r.id.toString()), null)?.use { d ->
            while (d.moveToNext()) {
                when (d.getString(1)) {
                    StructuredName.CONTENT_ITEM_TYPE -> {
                        c = c.copy(displayName = d.str(StructuredName.DISPLAY_NAME) ?: "", given = d.str(StructuredName.GIVEN_NAME) ?: "",
                            family = d.str(StructuredName.FAMILY_NAME) ?: "", additional = d.str(StructuredName.MIDDLE_NAME) ?: "",
                            prefix = d.str(StructuredName.PREFIX) ?: "", suffix = d.str(StructuredName.SUFFIX) ?: "")
                        any = any || c.displayName.isNotEmpty() || c.given.isNotEmpty() || c.family.isNotEmpty()
                    }
                    Nickname.CONTENT_ITEM_TYPE -> d.str(Nickname.NAME)?.let { c = c.copy(nickname = it) }
                    Phone.CONTENT_ITEM_TYPE -> d.str(Phone.NUMBER)?.let { v ->
                        val t = d.int(Phone.TYPE) ?: Phone.TYPE_OTHER
                        val label = d.str(Phone.LABEL)
                        val old = base.phones.firstOrNull { o -> digits(o.value) == digits(v) }
                        val type = if (old != null && phoneType(old.type) == t && (t != Phone.TYPE_CUSTOM || old.type == label)) old.type else phoneName(t, label)
                        phones += TypedValue(type, v); any = true
                    }
                    Email.CONTENT_ITEM_TYPE -> d.str(Email.ADDRESS)?.let { v ->
                        val t = d.int(Email.TYPE) ?: Email.TYPE_OTHER
                        val old = base.emails.firstOrNull { o -> o.value.equals(v, true) }
                        val type = if (old != null && emailType(old.type) == t) old.type else emailName(t, d.str(Email.LABEL))
                        emails += TypedValue(type, v); any = true
                    }
                    StructuredPostal.CONTENT_ITEM_TYPE -> {
                        val t = d.int(StructuredPostal.TYPE) ?: StructuredPostal.TYPE_OTHER
                        var a = Address(street = d.str(StructuredPostal.STREET) ?: "", city = d.str(StructuredPostal.CITY) ?: "",
                            region = d.str(StructuredPostal.REGION) ?: "", zip = d.str(StructuredPostal.POSTCODE) ?: "", country = d.str(StructuredPostal.COUNTRY) ?: "")
                        if (a.text.isEmpty()) a = a.copy(street = d.str(StructuredPostal.FORMATTED_ADDRESS) ?: "")
                        if (a.text.isNotEmpty()) {
                            val old = base.addresses.firstOrNull { o -> o.street == a.street && o.city == a.city }
                            addresses += a.copy(type = if (old != null && postalType(old.type) == t) old.type else postalName(t)); any = true
                        }
                    }
                    Organization.CONTENT_ITEM_TYPE -> if (c.org.isEmpty() && c.title.isEmpty()) {
                        c = c.copy(org = d.str(Organization.COMPANY) ?: "", title = d.str(Organization.TITLE) ?: "", department = d.str(Organization.DEPARTMENT) ?: "")
                        any = any || c.org.isNotEmpty()
                    }
                    Website.CONTENT_ITEM_TYPE -> d.str(Website.URL)?.let { urls += it }
                    Note.CONTENT_ITEM_TYPE -> d.str(Note.NOTE)?.let { c = c.copy(note = it) }
                    Event.CONTENT_ITEM_TYPE -> if (d.int(Event.TYPE) == Event.TYPE_BIRTHDAY) d.str(Event.START_DATE)?.let { c = c.copy(birthday = it) }
                    Photo.CONTENT_ITEM_TYPE -> photoRow = "${d.getLong(0)}:${d.getLong(2)}"
                }
            }
        }
        if (!any) return null
        // Foto: unveraendert -> null (Server laesst es stehen), entfernt -> "", neu -> data:-URL
        val oldState = r.photoState ?: ""
        val photo = when {
            photoRow == null -> if (oldState.isNotEmpty()) "" else null
            photoRow == oldState -> null
            else -> readPhoto(ctx, r.id)
        }
        return c.copy(phones = phones, emails = emails, addresses = addresses, urls = urls, photo = photo)
    }

    /** Foto in voller Groesse (Foto-Datei bzw. display_photo), sonst das Vorschaubild. */
    private fun readPhoto(ctx: Context, rawId: Long): String? {
        val cr = ctx.contentResolver
        var fileId: Long? = null
        var thumb: ByteArray? = null
        cr.query(ContactsContract.Data.CONTENT_URI, arrayOf(Photo.PHOTO_FILE_ID, Photo.PHOTO),
            "${ContactsContract.Data.RAW_CONTACT_ID}=? AND ${ContactsContract.Data.MIMETYPE}=?", arrayOf(rawId.toString(), Photo.CONTENT_ITEM_TYPE), null)?.use { c ->
            if (c.moveToFirst()) { if (!c.isNull(0)) fileId = c.getLong(0); if (!c.isNull(1)) thumb = c.getBlob(1) }
        }
        fun open(uri: Uri): ByteArray? = try {
            cr.openAssetFileDescriptor(uri, "r")?.use { fd -> fd.createInputStream().use { it.readBytes() } }?.takeIf { it.isNotEmpty() }
        } catch (e: Exception) { Log.d(TAG, "photo $uri: $e"); null }
        val bytes = fileId?.let { open(ContentUris.withAppendedId(ContactsContract.DisplayPhoto.CONTENT_URI, it)) }
            ?: open(Uri.withAppendedPath(ContentUris.withAppendedId(RawContacts.CONTENT_URI, rawId), RawContacts.DisplayPhoto.CONTENT_DIRECTORY))
            ?: thumb ?: return null
        Log.d(TAG, "photo of raw $rawId: ${bytes.size} bytes (file $fileId)")
        return "data:image/jpeg;base64," + android.util.Base64.encodeToString(bytes, android.util.Base64.NO_WRAP)
    }

    private fun digits(s: String) = s.filter { it.isDigit() || it == '+' }

    private fun phoneType(t: String) = when (t.lowercase()) {
        "cell", "mobile" -> Phone.TYPE_MOBILE; "home" -> Phone.TYPE_HOME
        "work" -> Phone.TYPE_WORK; "fax" -> Phone.TYPE_FAX_WORK; "" -> Phone.TYPE_OTHER
        "main" -> Phone.TYPE_MAIN; "pager" -> Phone.TYPE_PAGER
        else -> Phone.TYPE_CUSTOM
    }
    private fun phoneName(t: Int, label: String?) = when (t) {
        Phone.TYPE_MOBILE -> "cell"; Phone.TYPE_HOME -> "home"; Phone.TYPE_WORK -> "work"
        Phone.TYPE_FAX_WORK, Phone.TYPE_FAX_HOME, Phone.TYPE_OTHER_FAX -> "fax"
        Phone.TYPE_MAIN -> "main"; Phone.TYPE_PAGER -> "pager"
        Phone.TYPE_CUSTOM -> label ?: ""
        else -> ""
    }
    private fun emailType(t: String) = when (t.lowercase()) {
        "home" -> Email.TYPE_HOME; "work" -> Email.TYPE_WORK; else -> Email.TYPE_OTHER
    }
    private fun emailName(t: Int, label: String?) = when (t) {
        Email.TYPE_HOME -> "home"; Email.TYPE_WORK -> "work"; Email.TYPE_CUSTOM -> label ?: ""; else -> ""
    }
    private fun postalType(t: String) = when (t.lowercase()) {
        "home" -> StructuredPostal.TYPE_HOME; "work" -> StructuredPostal.TYPE_WORK; else -> StructuredPostal.TYPE_OTHER
    }
    private fun postalName(t: Int) = when (t) { StructuredPostal.TYPE_HOME -> "home"; StructuredPostal.TYPE_WORK -> "work"; else -> "" }

    fun remove(ctx: Context) {
        if (!ProviderSync.hasContactsPermission(ctx)) return
        try {
            removeLegacy(ctx)
            for (acc in android.accounts.AccountManager.get(ctx).getAccountsByType(AndroidAccount.type(ctx)))
                ctx.contentResolver.delete(syncUri(RawContacts.CONTENT_URI, acc), "${RawContacts.ACCOUNT_NAME}=? AND ${RawContacts.ACCOUNT_TYPE}=?", arrayOf(acc.name, acc.type))
        } catch (e: Exception) { Log.w(TAG, "remove contacts failed", e) }
    }

    /** Umstieg vom alten Einbahn-Spiegel: Geraetekontakte mit Quelle "dmoffice" entfernen, sonst stuende alles doppelt da. */
    private fun removeLegacy(ctx: Context) {
        try {
            // Samsung haengt kontolose Kontakte an sein Geraetekonto: daher nur "nicht unser Konto" pruefen
            ctx.contentResolver.delete(plainSync(RawContacts.CONTENT_URI), "${RawContacts.SYNC1}=? AND (${RawContacts.ACCOUNT_TYPE} IS NULL OR ${RawContacts.ACCOUNT_TYPE}<>?)",
                arrayOf(LEGACY_SOURCE, AndroidAccount.type(ctx)))
        } catch (e: Exception) { Log.w(TAG, "remove legacy contacts failed", e) }
    }
}
