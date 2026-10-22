package at.dimes.dmoffice.upload

import android.content.Context
import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Database
import androidx.room.Delete
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.PrimaryKey
import androidx.room.Query
import androidx.room.Room
import androidx.room.RoomDatabase
import androidx.room.Update
import androidx.room.Upsert
import androidx.room.migration.Migration
import androidx.sqlite.db.SupportSQLiteDatabase
import at.dimes.dmoffice.data.Prefs
import kotlinx.coroutines.flow.Flow

/**
 * Eine Regel des automatischen Hochladens: welcher Ordner auf dem Geraet wohin in die Dateiablage.
 * Werte der Textfelder siehe [UploadLogic] (SRC_*, SUB_*, TYPES_*, CONFLICT_*).
 */
@Entity(tableName = "rules")
data class UploadRule(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val name: String,
    val enabled: Boolean = true,
    val source: String,
    /** MediaStore: Ordner (RELATIVE_PATH) je Zeile; SAF: die Tree-URI. */
    val sourcePaths: String,
    /** Anzeige der Quelle, z. B. "DCIM/Camera" oder der Ordnername aus der Auswahl. */
    val sourceLabel: String,
    val target: String,
    val subfolders: String = UploadLogic.SUB_MONTH,
    val types: String = UploadLogic.TYPES_ALL,
    val conflict: String = UploadLogic.CONFLICT_RENAME,
    /** Nach erfolgreichem Hochladen auf dem Geraet loeschen. */
    val deleteAfter: Boolean = false,
    val wifiOnly: Boolean = true,
    val chargingOnly: Boolean = false,
    val roaming: Boolean = false,
    /** Nur Dateien ab diesem Zeitpunkt (ms); 0 = auch alle vorhandenen. */
    val sinceMs: Long = 0,
    /** Unterordner der Quelle am Ziel nachbilden. */
    val keepStructure: Boolean = false,
    /** An: was mit gleichem Inhalt schon irgendwo in dmOffice liegt, nicht hochladen. Aus: nur der Zielordner zaehlt. */
    @ColumnInfo(defaultValue = "1") val skipDuplicates: Boolean = true,
    val created: Long = System.currentTimeMillis(),
    val lastScan: Long = 0,
)

/**
 * Eine Datei in der Warteschlange bzw. im Verlauf. Bleibt nach dem Hochladen stehen, damit nichts doppelt
 * hochgeladen wird: erkannt wird eine Datei an Regel, Pfad in der Quelle, Groesse und Aenderungszeit.
 */
@Entity(tableName = "uploads", indices = [Index("ruleId", "sourcePath"), Index("status", "nextTry")])
data class UploadEntry(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    val ruleId: Long,
    val uri: String,
    /** Pfad in der Quelle samt Name, z. B. "DCIM/Camera/IMG_1.jpg" (SAF: relativ zum gewaehlten Ordner). */
    val sourcePath: String,
    val name: String,
    val mime: String?,
    val size: Long,
    val modified: Long,
    /** Aufnahmedatum (EXIF bzw. MediaStore), sonst Aenderungszeit: bestimmt den Datumsordner. */
    val taken: Long,
    val targetDir: String,
    val status: Int = QUEUED,
    val attempts: Int = 0,
    val nextTry: Long = 0,
    val error: String? = null,
    val remotePath: String? = null,
    val added: Long = System.currentTimeMillis(),
    val finished: Long = 0,
    /** Lag schon (gleicher Name, gleiche Groesse) am Ziel und wurde nicht erneut uebertragen. */
    val already: Boolean = false,
    /** Soll auf dem Geraet geloescht werden und braucht dafuer noch die Bestaetigung in Android. */
    val deletePending: Boolean = false,
    val localDeleted: Boolean = false,
    /** SHA-256 des Inhalts (hex), vor dem Hochladen gerechnet und beim Senden bestaetigt. */
    @ColumnInfo(defaultValue = "NULL") val sha256: String? = null,
    /** Der Server hat genau diesen Inhalt (gleiche Pruefsumme und Groesse) - Voraussetzung fuer "Speicherplatz freigeben". */
    @ColumnInfo(defaultValue = "0") val backedUp: Boolean = false,
    /** Stand auf dem Geraet beim letzten Durchsuchen ([FreeUpLogic].LOCAL_*). */
    @ColumnInfo(defaultValue = "0") val localState: Int = FreeUpLogic.LOCAL_UNKNOWN,
) {
    companion object {
        const val QUEUED = 0
        const val RUNNING = 1
        const val DONE = 2
        const val FAILED = 3
        const val CANCELLED = 4
        /** Am Ziel gibt es eine andere Datei gleichen Namens und die Regel sagt "ueberspringen". */
        const val SKIPPED = 5
    }
}

/** Hochgeladene Datei zum Nachfuehren des Stands auf dem Geraet. */
data class UploadedFile(val id: Long, val sourcePath: String, val size: Long, val modified: Long, val localState: Int)

/** Kurzform zum Erkennen schon bekannter Dateien beim Durchsuchen. */
data class KnownFile(val id: Long, val sourcePath: String, val size: Long, val modified: Long, val status: Int)

data class StatusCount(val status: Int, val n: Int)

/**
 * Zwischenspeicher der Pruefsummen von Dateien auf dem Geraet: je Uri, Groesse und Aenderungszeit einmal gerechnet.
 * Aendert sich die Datei, passt der Schluessel nicht mehr und es wird neu gerechnet.
 */
@Entity(tableName = "hash_cache", primaryKeys = ["uri", "size", "modified"])
data class HashCache(val uri: String, val size: Long, val modified: Long, val sha256: String, val computed: Long = System.currentTimeMillis())

/** Was sich auf dem Geraet freigeben laesst: Anzahl und Bytes. */
data class FreeSummary(val n: Int, val bytes: Long)

data class RuleCount(val ruleId: Long, val status: Int, val n: Int)

@Dao
interface UploadDao {
    @Query("SELECT * FROM rules ORDER BY created")
    fun rules(): Flow<List<UploadRule>>

    @Query("SELECT * FROM rules ORDER BY created")
    suspend fun ruleList(): List<UploadRule>

    @Query("SELECT * FROM rules WHERE id = :id")
    suspend fun rule(id: Long): UploadRule?

    @Upsert
    suspend fun saveRule(r: UploadRule): Long

    @Delete
    suspend fun deleteRule(r: UploadRule)

    @Query("UPDATE rules SET lastScan = :t WHERE id = :id")
    suspend fun setScanned(id: Long, t: Long)

    @Query("SELECT id, sourcePath, size, modified, status FROM uploads WHERE ruleId = :rule ORDER BY id")
    suspend fun known(rule: Long): List<KnownFile>

    @Insert
    suspend fun insert(list: List<UploadEntry>)

    @Update
    suspend fun update(e: UploadEntry)

    @Query("SELECT * FROM uploads WHERE id = :id")
    suspend fun entry(id: Long): UploadEntry?

    /** Naechste Datei, deren Regel gerade darf (Netz, Laden) und deren Wartezeit abgelaufen ist. */
    @Query("SELECT * FROM uploads WHERE status = 0 AND nextTry <= :now AND ruleId IN (:rules) ORDER BY added, id LIMIT 1")
    suspend fun next(now: Long, rules: List<Long>): UploadEntry?

    @Query("SELECT * FROM uploads WHERE status = 0 ORDER BY added, id")
    suspend fun queued(): List<UploadEntry>

    @Query("SELECT * FROM uploads WHERE status IN (0, 1) ORDER BY status DESC, added, id")
    fun pending(): Flow<List<UploadEntry>>

    @Query("SELECT * FROM uploads WHERE status = 3 ORDER BY finished DESC, id DESC")
    fun failed(): Flow<List<UploadEntry>>

    @Query("SELECT * FROM uploads WHERE status IN (2, 4, 5) AND finished >= 0 ORDER BY finished DESC, id DESC LIMIT 500")
    fun finished(): Flow<List<UploadEntry>>

    // Zum Loeschen vorgemerkt zaehlt erst, was der Server per Pruefsumme bestaetigt hat (backedUp) - bis dahin wartet es
    @Query("SELECT * FROM uploads WHERE deletePending = 1 AND localDeleted = 0 AND backedUp = 1 ORDER BY id")
    fun deletePending(): Flow<List<UploadEntry>>

    @Query("SELECT * FROM uploads WHERE deletePending = 1 AND localDeleted = 0 AND backedUp = 1 ORDER BY id")
    suspend fun deletePendingList(): List<UploadEntry>

    /** "Nach dem Hochladen loeschen" nachtraeglich eingeschaltet: was diese Regel schon hochgeladen hat, auch zum Loeschen vormerken. */
    @Query("UPDATE uploads SET deletePending = 1 WHERE ruleId = :rule AND status = 2 AND localDeleted = 0")
    suspend fun markDoneForDelete(rule: Long): Int

    /** Stand je Regel: hochgeladen, wartend, fehlgeschlagen ... (Verlauf leeren zaehlt weiter mit). */
    @Query("SELECT ruleId, status, COUNT(*) AS n FROM uploads GROUP BY ruleId, status")
    fun ruleCounts(): Flow<List<RuleCount>>

    @Query("SELECT status, COUNT(*) AS n FROM uploads GROUP BY status")
    fun counts(): Flow<List<StatusCount>>

    /** Abgebrochen gebliebene Laeufe (App beendet) wieder einreihen. */
    @Query("UPDATE uploads SET status = 0 WHERE status = 1")
    suspend fun requeueRunning()

    @Query("UPDATE uploads SET status = 0, attempts = 0, nextTry = 0, error = NULL WHERE status = 3")
    suspend fun retryAllFailed()

    /** Wartende sofort versuchen (Knopf "Jetzt hochladen"). */
    @Query("UPDATE uploads SET nextTry = 0 WHERE status = 0")
    suspend fun clearBackoff()

    @Query("SELECT MIN(nextTry) FROM uploads WHERE status = 0 AND nextTry > :now")
    suspend fun nextRetryAt(now: Long): Long?

    /** Verlauf leeren: Eintraege bleiben als Gedaechtnis erhalten (sonst kaeme alles wieder), nur nicht mehr in der Liste. */
    @Query("UPDATE uploads SET finished = -1 WHERE status IN (2, 4, 5) AND finished > 0")
    suspend fun hideFinished()

    @Query("UPDATE uploads SET status = 4, finished = :t WHERE status IN (0, 3) AND ruleId = :rule")
    suspend fun cancelRule(rule: Long, t: Long)

    /** Noch nicht begonnene Eintraege einer Regel (nach dem Bearbeiten neu durchsuchen). */
    @Query("DELETE FROM uploads WHERE ruleId = :rule AND status = 0")
    suspend fun deleteQueued(rule: Long)

    @Query("DELETE FROM uploads WHERE ruleId = :rule")
    suspend fun deleteForRule(rule: Long)

    @Query("UPDATE uploads SET deletePending = 0 WHERE id IN (:ids)")
    suspend fun keepLocal(ids: List<Long>)

    @Query("DELETE FROM uploads")
    suspend fun clearEntries()

    // ---------------- Pruefsummen und Speicherplatz freigeben ----------------

    @Query("SELECT sha256 FROM hash_cache WHERE uri = :uri AND size = :size AND modified = :modified")
    suspend fun cachedHash(uri: String, size: Long, modified: Long): String?

    @Upsert
    suspend fun cacheHash(h: HashCache)

    @Query("DELETE FROM hash_cache WHERE uri = :uri")
    suspend fun dropHash(uri: String)

    /** Hochgeladene Dateien einer Regel, deren Stand auf dem Geraet beim Durchsuchen nachgefuehrt wird. */
    @Query("SELECT id, sourcePath, size, modified, localState FROM uploads WHERE ruleId = :rule AND status = 2 AND localDeleted = 0")
    suspend fun uploaded(rule: Long): List<UploadedFile>

    @Query("UPDATE uploads SET localState = :state WHERE id IN (:ids)")
    suspend fun setLocalState(ids: List<Long>, state: Int)

    /** Hochgeladen, aber (noch) nicht per Pruefsumme vom Server bestaetigt - etwa vor dieser Version oder ueber Name und Groesse.
     *  Medien (fuer "Speicherplatz freigeben") und alles, was "Nach dem Hochladen loeschen" vorgemerkt hat. */
    @Query("SELECT * FROM uploads WHERE status = 2 AND backedUp = 0 AND localDeleted = 0 AND ((localState = 1 AND uri LIKE 'content://media/%') OR deletePending = 1) ORDER BY id LIMIT :limit")
    suspend fun unconfirmed(limit: Int): List<UploadEntry>

    @Query("SELECT COUNT(*) AS n, COALESCE(SUM(size), 0) AS bytes FROM uploads WHERE id IN (SELECT MAX(id) FROM uploads WHERE status = 2 AND backedUp = 1 AND sha256 IS NOT NULL AND localDeleted = 0 AND localState = 1 AND MAX(taken, modified) < :cutoff AND uri LIKE 'content://media/%' GROUP BY uri)")
    fun freeSummary(cutoff: Long): Flow<FreeSummary>

    @Query("SELECT * FROM uploads WHERE id IN (SELECT MAX(id) FROM uploads WHERE status = 2 AND backedUp = 1 AND sha256 IS NOT NULL AND localDeleted = 0 AND localState = 1 AND MAX(taken, modified) < :cutoff AND uri LIKE 'content://media/%' GROUP BY uri) ORDER BY MAX(taken, modified), id")
    fun freeList(cutoff: Long): Flow<List<UploadEntry>>

    @Query("SELECT * FROM uploads WHERE id IN (SELECT MAX(id) FROM uploads WHERE status = 2 AND backedUp = 1 AND sha256 IS NOT NULL AND localDeleted = 0 AND localState = 1 AND MAX(taken, modified) < :cutoff AND uri LIKE 'content://media/%' GROUP BY uri) ORDER BY MAX(taken, modified), id")
    suspend fun freeListNow(cutoff: Long): List<UploadEntry>

    /** Nach dem Freigeben: auf dem Geraet geloescht. */
    @Query("UPDATE uploads SET localDeleted = 1, deletePending = 0, localState = 3 WHERE id IN (:ids)")
    suspend fun setDeleted(ids: List<Long>)

    @Query("DELETE FROM rules")
    suspend fun clearRules()
}

/**
 * Eigene Datenbank fuer Regeln und Warteschlange: die Haupt-DB ist ein Zwischenspeicher, der bei
 * Schema-Aenderungen verworfen wird - hier stuende damit das Gedaechtnis des Hochgeladenen auf dem Spiel.
 * Gleicher SQLCipher-Schluessel wie die Haupt-DB.
 */
@Database(entities = [UploadRule::class, UploadEntry::class, HashCache::class], version = 2, exportSchema = true)
abstract class UploadDb : RoomDatabase() {
    abstract fun dao(): UploadDao

    companion object {
        /** 1 -> 2: Pruefsummen, Stand auf dem Geraet (Speicherplatz freigeben), "Duplikate ueberspringen". Bestehendes bleibt erhalten. */
        val MIGRATION_1_2 = object : Migration(1, 2) {
            override fun migrate(db: SupportSQLiteDatabase) {
                db.execSQL("ALTER TABLE uploads ADD COLUMN sha256 TEXT DEFAULT NULL")
                db.execSQL("ALTER TABLE uploads ADD COLUMN backedUp INTEGER NOT NULL DEFAULT 0")
                db.execSQL("ALTER TABLE uploads ADD COLUMN localState INTEGER NOT NULL DEFAULT 0")
                db.execSQL("ALTER TABLE rules ADD COLUMN skipDuplicates INTEGER NOT NULL DEFAULT 1")
                db.execSQL("CREATE TABLE IF NOT EXISTS hash_cache (uri TEXT NOT NULL, size INTEGER NOT NULL, modified INTEGER NOT NULL, " +
                    "sha256 TEXT NOT NULL, computed INTEGER NOT NULL, PRIMARY KEY(uri, size, modified))")
            }
        }

        @Volatile private var instance: UploadDb? = null

        fun get(context: Context): UploadDb = instance ?: synchronized(this) { instance ?: open(context).also { instance = it } }

        private fun open(context: Context): UploadDb {
            val app = context.applicationContext
            val key = Prefs.dbKey(app)
            System.loadLibrary("sqlcipher")
            fun build() = Room.databaseBuilder(app, UploadDb::class.java, "autoupload.db")
                .openHelperFactory(net.zetetic.database.sqlcipher.SupportOpenHelperFactory(key))
                .addMigrations(MIGRATION_1_2)
                .build()
            val db = build()
            return try {
                db.openHelper.readableDatabase
                db
            } catch (e: Exception) {
                // Schluessel verloren (Keystore zurueckgesetzt): neu anfangen; doppelt Hochgeladenes verhindert der Abgleich mit dem Ziel
                android.util.Log.w("dmOffice", "Upload-DB nicht lesbar, wird neu angelegt: " + e.message)
                try { db.close() } catch (_: Exception) { }
                app.deleteDatabase("autoupload.db")
                build()
            }
        }
    }
}
