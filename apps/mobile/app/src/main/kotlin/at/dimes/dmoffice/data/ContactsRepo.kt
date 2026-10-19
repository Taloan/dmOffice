package at.dimes.dmoffice.data

import android.content.Context
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import at.dimes.dmoffice.api.Contact
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json

/** Kontakte ueber die Web-API, komplett lokal gecacht (Suche offline). */
class ContactsRepo(private val context: Context, private val db: AppDb) {
    private val json = Json { ignoreUnknownKeys = true; explicitNulls = false; coerceInputValues = true }
    private fun api(): ApiClient = App.get(context).api ?: throw ApiException(401, "no account")

    private fun decode(e: ContactEntity): Contact = json.decodeFromString(e.json)

    fun all(): Flow<List<Contact>> = db.contacts().all().map { l -> l.map { decode(it) } }
    fun search(q: String): Flow<List<Contact>> = (if (q.isBlank()) db.contacts().all() else db.contacts().search(q.trim().lowercase())).map { l -> l.map { decode(it) } }
    suspend fun byId(id: String): Contact? = db.contacts().byId(id)?.let { decode(it) }
    suspend fun allNow(): List<Contact> = db.contacts().allNow().map { decode(it) }

    suspend fun refresh() = withContext(Dispatchers.IO) {
        val list: List<Contact> = api().get(api().url("/api/pim/contacts"))
        val entities = list.map { c ->
            val search = listOf(c.name, c.given, c.family, c.nickname, c.org, c.department, c.title, c.note,
                c.emails.joinToString(" ") { it.value }, c.phones.joinToString(" ") { it.value }, c.addresses.joinToString(" ") { it.text })
                .joinToString(" ").lowercase()
            ContactEntity(c.id, c.book, c.name, c.sortKey, search, json.encodeToString(c.copy(photo = c.photo?.take(200_000))))
        }
        db.contacts().upsert(entities)
        db.contacts().deleteMissing(entities.map { it.id }.ifEmpty { listOf("") })
    }

    suspend fun save(c: Contact): Contact = withContext(Dispatchers.IO) {
        val saved: Contact = api().postFor(api().url("/api/pim/contacts"), c)
        refresh()
        saved
    }

    suspend fun delete(c: Contact) = withContext(Dispatchers.IO) {
        api().delete(api().url("/api/pim/contacts/${c.book}/${java.net.URLEncoder.encode(c.uri, "UTF-8")}"))
        db.contacts().deleteMissing(db.contacts().allNow().map { it.id }.filter { it != c.id }.ifEmpty { listOf("") })
    }
}
