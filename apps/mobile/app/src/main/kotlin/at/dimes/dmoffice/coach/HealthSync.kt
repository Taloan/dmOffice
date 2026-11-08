package at.dimes.dmoffice.coach

import android.content.Context
import android.util.Log
import androidx.health.connect.client.HealthConnectClient
import androidx.health.connect.client.HealthConnectFeatures
import androidx.health.connect.client.permission.HealthPermission
import androidx.health.connect.client.records.ActiveCaloriesBurnedRecord
import androidx.health.connect.client.records.BodyFatRecord
import androidx.health.connect.client.records.DistanceRecord
import androidx.health.connect.client.records.RestingHeartRateRecord
import androidx.health.connect.client.records.SleepSessionRecord
import androidx.health.connect.client.records.StepsRecord
import androidx.health.connect.client.records.WeightRecord
import androidx.health.connect.client.request.AggregateGroupByPeriodRequest
import androidx.health.connect.client.request.ReadRecordsRequest
import androidx.health.connect.client.time.TimeRangeFilter
import androidx.work.Constraints
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.NetworkType
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import at.dimes.dmoffice.App
import java.time.LocalDate
import java.time.Period
import java.time.ZoneId
import java.util.concurrent.TimeUnit

/**
 * Health Connect (Android 14+, auf älteren Geräten die App „Health Connect“): Schritte, aktive Kalorien, Strecke, Schlaf,
 * Ruhepuls, Gewicht und Körperfett - von jeder Uhr, die dort hineinschreibt (Garmin seit 2025, Samsung, Withings ...).
 * Liest je Tag zusammengefasst und lädt nach /api/coach/activity hoch; stündlich im Hintergrund, wenn erlaubt.
 */
object HealthSync {
    private const val TAG = "HealthSync"
    private const val PREFS = "coach_health"
    private const val WORK = "coach-health"

    /** Was der Coach liest (Lesen genügt, er schreibt nichts). */
    val readPermissions: Set<String> = setOf(
        HealthPermission.getReadPermission(StepsRecord::class),
        HealthPermission.getReadPermission(ActiveCaloriesBurnedRecord::class),
        HealthPermission.getReadPermission(DistanceRecord::class),
        HealthPermission.getReadPermission(SleepSessionRecord::class),
        HealthPermission.getReadPermission(RestingHeartRateRecord::class),
        HealthPermission.getReadPermission(WeightRecord::class),
        HealthPermission.getReadPermission(BodyFatRecord::class),
    )
    /** Zusätzlich: im Hintergrund lesen und ältere Daten als 30 Tage vor der Freigabe. */
    val extraPermissions: Set<String> = setOf(
        HealthPermission.PERMISSION_READ_HEALTH_DATA_IN_BACKGROUND,
        HealthPermission.PERMISSION_READ_HEALTH_DATA_HISTORY,
    )

    fun available(ctx: Context): Boolean = HealthConnectClient.getSdkStatus(ctx) == HealthConnectClient.SDK_AVAILABLE

    /** Health Connect fehlt oder muss aktualisiert werden (ältere Android-Versionen). */
    fun needsInstall(ctx: Context): Boolean = HealthConnectClient.getSdkStatus(ctx) == HealthConnectClient.SDK_UNAVAILABLE_PROVIDER_UPDATE_REQUIRED

    fun enabled(ctx: Context) = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean("enabled", false)
    fun lastSync(ctx: Context) = ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getLong("last", 0L)

    suspend fun granted(ctx: Context): Set<String> =
        if (!available(ctx)) emptySet() else try { HealthConnectClient.getOrCreate(ctx).permissionController.getGrantedPermissions() } catch (e: Exception) { emptySet() }

    /** Nach der Freigabe: merken, stündlichen Abgleich planen, sofort die letzten 30 Tage holen. */
    suspend fun enable(ctx: Context): Int {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("enabled", true).apply()
        schedule(ctx)
        return sync(ctx, 30)
    }

    fun disable(ctx: Context) {
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("enabled", false).apply()
        WorkManager.getInstance(ctx).cancelUniqueWork(WORK)
    }

    fun schedule(ctx: Context) {
        val req = PeriodicWorkRequestBuilder<HealthSyncWorker>(1, TimeUnit.HOURS)
            .setConstraints(Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()).build()
        WorkManager.getInstance(ctx).enqueueUniquePeriodicWork(WORK, ExistingPeriodicWorkPolicy.KEEP, req)
    }

    /**
     * Die letzten [days] Tage lesen und hochladen. Nur, was freigegeben ist - fehlende Freigaben lassen die Werte weg.
     * Schlaf zählt für den Tag, an dem er endet; Gewicht und Körperfett: der letzte Wert des Tages.
     */
    suspend fun sync(ctx: Context, days: Int = 3): Int {
        if (!available(ctx)) return 0
        val client = HealthConnectClient.getOrCreate(ctx)
        val granted = client.permissionController.getGrantedPermissions()
        fun has(c: kotlin.reflect.KClass<out androidx.health.connect.client.records.Record>) = HealthPermission.getReadPermission(c) in granted
        val zone = ZoneId.systemDefault()
        val today = LocalDate.now(zone)
        val first = today.minusDays((days - 1).toLong())
        val range = TimeRangeFilter.between(first.atStartOfDay(), today.plusDays(1).atStartOfDay())
        val byDay = linkedMapOf<LocalDate, ActivityDay>()
        fun day(d: LocalDate) = byDay.getOrPut(d) { ActivityDay(d.toString()) }

        val metrics = buildSet {
            if (has(StepsRecord::class)) add(StepsRecord.COUNT_TOTAL)
            if (has(ActiveCaloriesBurnedRecord::class)) add(ActiveCaloriesBurnedRecord.ACTIVE_CALORIES_TOTAL)
            if (has(DistanceRecord::class)) add(DistanceRecord.DISTANCE_TOTAL)
            if (has(RestingHeartRateRecord::class)) add(RestingHeartRateRecord.BPM_AVG)
        }
        if (metrics.isNotEmpty()) {
            for (g in client.aggregateGroupByPeriod(AggregateGroupByPeriodRequest(metrics, range, Period.ofDays(1)))) {
                val d = g.startTime.toLocalDate()
                val r = g.result
                byDay[d] = day(d).copy(
                    steps = r[StepsRecord.COUNT_TOTAL]?.toInt(),
                    activeKcal = r[ActiveCaloriesBurnedRecord.ACTIVE_CALORIES_TOTAL]?.inKilocalories?.let { Math.round(it).toDouble() },
                    distance = r[DistanceRecord.DISTANCE_TOTAL]?.inMeters,
                    restingHr = r[RestingHeartRateRecord.BPM_AVG]?.toInt(),
                )
            }
        }
        if (has(SleepSessionRecord::class)) {
            // Auch den Schlaf der Nacht vor dem ersten Tag (endet am ersten Tag)
            val sleepRange = TimeRangeFilter.between(first.minusDays(1).atStartOfDay(), today.plusDays(1).atStartOfDay())
            val minutes = mutableMapOf<LocalDate, Long>()
            for (s in client.readRecords(ReadRecordsRequest(SleepSessionRecord::class, sleepRange)).records) {
                val end = s.endTime.atZone(zone).toLocalDate()
                if (end < first) continue
                minutes[end] = (minutes[end] ?: 0) + java.time.Duration.between(s.startTime, s.endTime).toMinutes()
            }
            for ((d, m) in minutes) byDay[d] = day(d).copy(sleepMin = m.toInt().coerceAtMost(1440))
        }
        if (has(WeightRecord::class))
            for (w in client.readRecords(ReadRecordsRequest(WeightRecord::class, range)).records.sortedBy { it.time }) {
                val d = w.time.atZone(zone).toLocalDate()
                byDay[d] = day(d).copy(weight = Math.round(w.weight.inKilograms * 10) / 10.0)
            }
        if (has(BodyFatRecord::class))
            for (f in client.readRecords(ReadRecordsRequest(BodyFatRecord::class, range)).records.sortedBy { it.time }) {
                val d = f.time.atZone(zone).toLocalDate()
                byDay[d] = day(d).copy(bodyFat = f.percentage.value)
            }
        val list = byDay.values.filter { it.steps != null || it.activeKcal != null || it.sleepMin != null || it.weight != null || it.restingHr != null || it.bodyFat != null }
        App.get(ctx).coach.uploadActivity(list, "healthconnect")
        ctx.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putLong("last", System.currentTimeMillis()).apply()
        Log.i(TAG, "${list.size} Tage hochgeladen")
        return list.size
    }

    /** Kann Health Connect im Hintergrund lesen (ab Android 14 bzw. neuerem Health Connect)? */
    fun backgroundSupported(ctx: Context): Boolean = try {
        HealthConnectClient.getOrCreate(ctx).features.getFeatureStatus(HealthConnectFeatures.FEATURE_READ_HEALTH_DATA_IN_BACKGROUND) ==
            HealthConnectFeatures.FEATURE_STATUS_AVAILABLE
    } catch (e: Exception) { false }
}

/** Stündlich: die letzten drei Tage aus Health Connect hochladen (nur, wenn der Coach verbunden ist). */
class HealthSyncWorker(context: Context, params: WorkerParameters) : CoroutineWorker(context, params) {
    override suspend fun doWork(): Result {
        if (!HealthSync.enabled(applicationContext) || App.get(applicationContext).api == null) return Result.success()
        return try { HealthSync.sync(applicationContext, 3); Result.success() }
        catch (e: SecurityException) { Result.success() }   // Freigabe zurückgenommen
        catch (e: Exception) { Log.w("HealthSync", "Abgleich", e); Result.retry() }
    }
}
