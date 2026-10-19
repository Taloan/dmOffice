package at.dimes.dmoffice.data

import android.content.Context
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Entity
import androidx.room.Index
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.Transaction
import androidx.room.Upsert
import kotlinx.coroutines.flow.Flow

/** Eine CalDAV-Sammlung unter /dav/calendars/<login>/. Kann Termine, Aufgaben oder beides enthalten. */
@Entity(tableName = "calendars")
data class CalendarEntity(
    @PrimaryKey val href: String,
    val name: String,
    val color: Int?,
    val supportsEvents: Boolean,
    val supportsTasks: Boolean,
    val syncToken: String?,
    val ctag: String?,
    val sortOrder: Int,
    val visible: Boolean = true,
)

const val KIND_EVENT = 0
const val KIND_TASK = 1

/**
 * Ein Kalenderobjekt (eine .ics-Datei) mit den Feldern, die Listen und Widgets brauchen.
 * Das vollstaendige iCalendar bleibt in [ics] erhalten, damit beim Schreiben nichts verloren geht.
 */
@Entity(tableName = "items", indices = [Index("calendarHref"), Index("kind", "start"), Index("kind", "due"), Index("dirty")])
data class ItemEntity(
    @PrimaryKey val href: String,
    val calendarHref: String,
    val etag: String?,
    val uid: String,
    val kind: Int,
    val ics: String,
    val summary: String,
    val location: String?,
    val description: String?,
    val allDay: Boolean,
    val start: Long?,
    val end: Long?,
    val due: Long?,
    val recurring: Boolean,
    val completed: Boolean,
    val priority: Int,
    val reminderSeconds: Long?,
    val dirty: Boolean = false,
    val deleted: Boolean = false,
)

@Dao
interface CalendarDao {
    @Query("SELECT * FROM calendars ORDER BY sortOrder, name")
    fun all(): Flow<List<CalendarEntity>>

    @Query("SELECT * FROM calendars ORDER BY sortOrder, name")
    suspend fun list(): List<CalendarEntity>

    @Query("SELECT * FROM calendars WHERE href = :href")
    suspend fun byHref(href: String): CalendarEntity?

    @Upsert
    suspend fun upsert(c: CalendarEntity)

    @Query("DELETE FROM calendars WHERE href NOT IN (:keep)")
    suspend fun deleteMissing(keep: List<String>)

    @Query("UPDATE calendars SET syncToken = :token, ctag = :ctag WHERE href = :href")
    suspend fun setToken(href: String, token: String?, ctag: String?)

    @Query("UPDATE calendars SET visible = :visible WHERE href = :href")
    suspend fun setVisible(href: String, visible: Boolean)

    @Query("UPDATE calendars SET color = :color WHERE href = :href")
    suspend fun setColor(href: String, color: Int?)

    @Query("DELETE FROM calendars")
    suspend fun clear()
}

@Dao
interface ItemDao {
    @Query("SELECT * FROM items WHERE href = :href")
    suspend fun byHref(href: String): ItemEntity?

    @Query("SELECT * FROM items WHERE href = :href")
    fun observe(href: String): Flow<ItemEntity?>

    @Upsert
    suspend fun upsert(i: ItemEntity)

    @Query("DELETE FROM items WHERE href = :href")
    suspend fun delete(href: String)

    @Query("DELETE FROM items WHERE calendarHref = :cal")
    suspend fun deleteCalendar(cal: String)

    @Query("DELETE FROM items WHERE calendarHref = :cal AND dirty = 0 AND deleted = 0")
    suspend fun deleteCleanOf(cal: String)

    @Query("DELETE FROM items WHERE calendarHref NOT IN (:keep)")
    suspend fun deleteOrphans(keep: List<String>)

    @Query("DELETE FROM items")
    suspend fun clear()

    /** Termine, die den Bereich beruehren koennen; Serien immer (werden spaeter ausgefaltet). */
    @Query("SELECT * FROM items WHERE kind = 0 AND deleted = 0 AND (recurring = 1 OR (end > :from AND start < :to))")
    fun eventsInRange(from: Long, to: Long): Flow<List<ItemEntity>>

    @Query("SELECT * FROM items WHERE kind = 0 AND deleted = 0 AND (recurring = 1 OR (end > :from AND start < :to))")
    suspend fun eventsInRangeNow(from: Long, to: Long): List<ItemEntity>

    @Query("SELECT * FROM items WHERE kind = 1 AND deleted = 0 ORDER BY completed, CASE WHEN due IS NULL THEN 1 ELSE 0 END, due, CASE WHEN priority = 0 THEN 10 ELSE priority END, summary")
    fun tasks(): Flow<List<ItemEntity>>

    @Query("SELECT * FROM items WHERE kind = 1 AND deleted = 0 ORDER BY completed, CASE WHEN due IS NULL THEN 1 ELSE 0 END, due, CASE WHEN priority = 0 THEN 10 ELSE priority END, summary")
    suspend fun allTasksNow(): List<ItemEntity>

    @Query("SELECT * FROM items WHERE kind = 1 AND deleted = 0 AND completed = 0 ORDER BY CASE WHEN due IS NULL THEN 1 ELSE 0 END, due, CASE WHEN priority = 0 THEN 10 ELSE priority END, summary")
    suspend fun openTasksNow(): List<ItemEntity>

    @Query("SELECT * FROM items WHERE kind = 1 AND deleted = 0 AND completed = 0 AND due IS NOT NULL AND due < :to")
    fun dueTasks(to: Long): Flow<List<ItemEntity>>

    @Query("SELECT * FROM items WHERE kind = 1 AND deleted = 0 AND completed = 0 AND due IS NOT NULL AND due < :to")
    suspend fun dueTasksNow(to: Long): List<ItemEntity>

    @Query("SELECT * FROM items WHERE dirty = 1 OR deleted = 1")
    suspend fun pending(): List<ItemEntity>

    @Query("SELECT COUNT(*) FROM items WHERE dirty = 1 OR deleted = 1")
    fun pendingCount(): Flow<Int>

    @Query("SELECT * FROM items WHERE deleted = 0 AND reminderSeconds IS NOT NULL AND ((kind = 0 AND (recurring = 1 OR start > :from)) OR (kind = 1 AND completed = 0 AND due > :from))")
    suspend fun withReminders(from: Long): List<ItemEntity>

    @Query("UPDATE items SET completed = :done, dirty = 1, ics = :ics WHERE href = :href")
    suspend fun setCompleted(href: String, done: Boolean, ics: String)
}

// ---------------- Mail-Cache ----------------

@Entity(tableName = "mail_folders", primaryKeys = ["account", "path"])
data class MailFolderEntity(val account: String, val path: String, val name: String, val special: String?, val unread: Int, val total: Int, val sortOrder: Int)

/** Eine Zeile der Mailliste; [json] ist das MailItem der API, die Spalten dienen Sortierung und Anzeige. */
@Entity(tableName = "mail_items", primaryKeys = ["account", "folder", "uid"], indices = [Index("account", "folder", "date")])
data class MailItemEntity(val account: String, val folder: String, val uid: Long, val date: Long, val seen: Boolean, val flagged: Boolean, val json: String)

/** Geladene Mail (bereinigtes HTML und Text), damit sie offline lesbar bleibt. */
@Entity(tableName = "mail_bodies", primaryKeys = ["account", "folder", "uid"])
data class MailBodyEntity(val account: String, val folder: String, val uid: Long, val json: String, val loaded: Long)

@Dao
interface MailDao {
    @Query("SELECT * FROM mail_folders WHERE account = :account ORDER BY sortOrder, name")
    fun folders(account: String): Flow<List<MailFolderEntity>>

    @Query("DELETE FROM mail_folders WHERE account = :account")
    suspend fun clearFolders(account: String)

    @Upsert
    suspend fun upsertFolders(list: List<MailFolderEntity>)

    @Query("SELECT * FROM mail_items WHERE account = :account AND folder = :folder ORDER BY date DESC LIMIT :limit")
    fun items(account: String, folder: String, limit: Int): Flow<List<MailItemEntity>>

    @Upsert
    suspend fun upsertItems(list: List<MailItemEntity>)

    @Query("DELETE FROM mail_items WHERE account = :account AND folder = :folder")
    suspend fun clearItems(account: String, folder: String)

    @Query("DELETE FROM mail_items WHERE account = :account AND folder = :folder AND uid IN (:uids)")
    suspend fun deleteItems(account: String, folder: String, uids: List<Long>)

    @Query("UPDATE mail_items SET seen = :seen WHERE account = :account AND folder = :folder AND uid IN (:uids)")
    suspend fun setSeen(account: String, folder: String, uids: List<Long>, seen: Boolean)

    @Query("UPDATE mail_items SET flagged = :flagged WHERE account = :account AND folder = :folder AND uid IN (:uids)")
    suspend fun setFlagged(account: String, folder: String, uids: List<Long>, flagged: Boolean)

    @Query("SELECT * FROM mail_items WHERE account = :account AND folder = :folder AND uid = :uid")
    fun item(account: String, folder: String, uid: Long): Flow<MailItemEntity?>

    @Query("SELECT * FROM mail_bodies WHERE account = :account AND folder = :folder AND uid = :uid")
    suspend fun body(account: String, folder: String, uid: Long): MailBodyEntity?

    @Upsert
    suspend fun upsertBody(b: MailBodyEntity)

    @Query("DELETE FROM mail_bodies WHERE loaded < :before")
    suspend fun pruneBodies(before: Long)

    @Query("DELETE FROM mail_folders")
    suspend fun clearAllFolders()
    @Query("DELETE FROM mail_items")
    suspend fun clearAllItems()
    @Query("DELETE FROM mail_bodies")
    suspend fun clearAllBodies()
}

// ---------------- Kontakte ----------------

@Entity(tableName = "contacts", indices = [Index("sortKey")])
data class ContactEntity(@PrimaryKey val id: String, val book: Long, val name: String, val sortKey: String, val search: String, val json: String)

@Dao
interface ContactDao {
    @Query("SELECT * FROM contacts ORDER BY sortKey, name")
    fun all(): Flow<List<ContactEntity>>

    @Query("SELECT * FROM contacts WHERE search LIKE '%' || :q || '%' ORDER BY sortKey, name")
    fun search(q: String): Flow<List<ContactEntity>>

    @Query("SELECT * FROM contacts WHERE id = :id")
    suspend fun byId(id: String): ContactEntity?

    @Query("SELECT * FROM contacts ORDER BY sortKey, name")
    suspend fun allNow(): List<ContactEntity>

    @Upsert
    suspend fun upsert(list: List<ContactEntity>)

    @Query("DELETE FROM contacts WHERE id NOT IN (:keep)")
    suspend fun deleteMissing(keep: List<String>)

    @Query("DELETE FROM contacts")
    suspend fun clear()
}

// ---------------- Notizen ----------------

@Entity(tableName = "notes", indices = [Index("modified")])
data class NoteEntity(@PrimaryKey val id: Long, val folder: String?, val title: String, val snippet: String, val modified: String, val important: Boolean, val json: String, val detailJson: String?)

@Dao
interface NoteDao {
    @Query("SELECT * FROM notes ORDER BY important DESC, modified DESC")
    fun all(): Flow<List<NoteEntity>>

    @Query("SELECT * FROM notes WHERE title LIKE '%' || :q || '%' OR snippet LIKE '%' || :q || '%' ORDER BY modified DESC")
    fun search(q: String): Flow<List<NoteEntity>>

    @Query("SELECT * FROM notes WHERE id = :id")
    suspend fun byId(id: Long): NoteEntity?

    @Query("SELECT * FROM notes WHERE id = :id")
    fun observe(id: Long): Flow<NoteEntity?>

    @Upsert
    suspend fun upsert(list: List<NoteEntity>)

    @Query("UPDATE notes SET detailJson = :detail WHERE id = :id")
    suspend fun setDetail(id: Long, detail: String?)

    @Query("DELETE FROM notes WHERE id NOT IN (:keep)")
    suspend fun deleteMissing(keep: List<Long>)

    @Query("DELETE FROM notes")
    suspend fun clear()
}

@Database(
    entities = [CalendarEntity::class, ItemEntity::class, MailFolderEntity::class, MailItemEntity::class, MailBodyEntity::class, ContactEntity::class, NoteEntity::class],
    version = 2, exportSchema = true
)
abstract class AppDb : RoomDatabase() {
    abstract fun calendars(): CalendarDao
    abstract fun items(): ItemDao
    abstract fun mail(): MailDao
    abstract fun contacts(): ContactDao
    abstract fun notes(): NoteDao

    @Transaction
    suspend fun clearAll() {
        items().clear(); calendars().clear()
        mail().clearAllFolders(); mail().clearAllItems(); mail().clearAllBodies()
        contacts().clear(); notes().clear()
    }

    companion object {
        @Volatile private var instance: AppDb? = null

        /**
         * Datenbank mit SQLCipher verschluesselt; der Schluessel liegt im Keystore-geschuetzten
         * Speicher (Prefs.dbKey). Eine alte unverschluesselte Datei wird verworfen - alles kommt
         * beim naechsten Sync wieder vom Server.
         */
        fun get(context: Context): AppDb = instance ?: synchronized(this) {
            instance ?: open(context).also { instance = it }
        }

        private fun open(context: Context): AppDb {
            val app = context.applicationContext
            val key = Prefs.dbKey(app)
            System.loadLibrary("sqlcipher")
            fun build() = Room.databaseBuilder(app, AppDb::class.java, "dmoffice.db")
                .openHelperFactory(net.zetetic.database.sqlcipher.SupportOpenHelperFactory(key))
                .fallbackToDestructiveMigration(true)
                .build()
            val db = build()
            return try {
                db.openHelper.readableDatabase   // Schluessel pruefen
                db
            } catch (e: Exception) {
                android.util.Log.w("dmOffice", "DB nicht lesbar, wird neu angelegt: " + e.message)
                try { db.close() } catch (_: Exception) { }
                app.deleteDatabase("dmoffice.db")
                build()
            }
        }
    }
}
