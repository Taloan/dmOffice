package at.dimes.dmoffice.coach

import android.net.Uri
import at.dimes.dmoffice.App
import at.dimes.dmoffice.api.ApiClient
import at.dimes.dmoffice.api.ApiException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable

/** Coach (docs/dmcoach-konzept.md): dieselbe Schnittstelle wie das Web (/api/coach). */

@Serializable data class Nutrients(val kcal: Double = 0.0, val protein: Double = 0.0, val fat: Double = 0.0, val carbs: Double = 0.0,
                                   val fiber: Double? = null, val sugar: Double? = null, val salt: Double? = null, val satFat: Double? = null)
@Serializable data class Portion(val label: String, val grams: Double)
@Serializable data class Food(val id: String, val name: String, val brand: String? = null, val source: String = "", val per100: Nutrients = Nutrients(),
                              val portions: List<Portion> = emptyList(), val liquid: Boolean = false, val barcode: String? = null, val quantity: String? = null)
@Serializable data class Hit(val id: String, val name: String, val brand: String? = null, val source: String = "", val kcalPer100: Double = 0.0,
                             val amount: Double? = null, val portion: String? = null, val mine: Boolean = false, val image: String? = null)

@Serializable data class OffResult(val hits: List<Hit> = emptyList(), val limited: Boolean = false)

@Serializable data class Profile(
    val birth: String? = null, val sex: String? = null, val heightCm: Double? = null, val weightKg: Double? = null, val pal: Double = 1.4,
    val goal: String = "keep", val pace: Double = 0.5, val diet: String = "dge", val allergens: List<String> = emptyList(),
    val intolerances: List<String> = emptyList(), val dislikes: List<String> = emptyList(), val pregnant: Boolean = false,
    val stepMode: String = "fixed", val stepGoal: Int = 7000, val waterMl: Int? = null, val hideNumbers: Boolean = false,
    // Stufe 3 - müssen hier stehen, sonst setzt Speichern in der App sie auf die Vorgaben zurück
    val waterSizes: List<Int> = listOf(250, 500), val aiData: Boolean = false, val nudges: Boolean = false,
    val nudgeWater: Boolean = true, val nudgeMove: Boolean = true, val nudgeWeek: Boolean = true,
)
@Serializable data class Targets(
    val bmr: Double? = null, val tdee: Double? = null, val kcal: Double? = null, val protein: Double? = null, val carbs: Double? = null, val fat: Double? = null,
    val fiber: Double = 30.0, val waterMl: Int = 1500, val age: Int? = null, val bmi: Double? = null, val goal: String = "keep", val diet: String = "dge",
    val notes: List<String> = emptyList(), val complete: Boolean = false,
)
@Serializable data class ProfileResult(val profile: Profile = Profile(), val targets: Targets = Targets(), val sources: List<String> = emptyList())

@Serializable data class Entry(
    val id: Long, val day: String, val meal: String, val food: String? = null, val name: String, val brand: String? = null, val amount: Double? = null,
    val portion: String? = null, val kcal: Double = 0.0, val protein: Double = 0.0, val fat: Double = 0.0, val carbs: Double = 0.0,
    val at: Long = 0,
)
@Serializable data class Eaten(val kcal: Double = 0.0, val protein: Double = 0.0, val carbs: Double = 0.0, val fat: Double = 0.0, val fiber: Double = 0.0)
@Serializable data class WaterEntry(val id: Long, val ml: Int, val at: Long = 0)
/** ml: eigene Einträge und die von der Uhr zusammen; watch: davon von der Uhr (Garmin) */
@Serializable data class Water(val ml: Int = 0, val watch: Int = 0, val goal: Int = 1500, val entries: List<WaterEntry> = emptyList(), val sizes: List<Int> = listOf(250, 500))
@Serializable data class Steps(val steps: Int? = null, val goal: Int = 7000, val mode: String = "fixed")
@Serializable data class Activity(val steps: Int? = null, val activeKcal: Double? = null, val distance: Double? = null, val sleepMin: Int? = null, val restingHr: Int? = null, val source: String = "")
@Serializable data class Extra(val stress: Double? = null, val bodyBatteryHigh: Double? = null, val bodyBatteryLow: Double? = null, val hrv: Double? = null)
/** Aktivität von der Uhr (Garmin); start: Ortszeit „2026-10-27 13:02:10“ */
@Serializable data class GActivity(val id: Long = 0, val name: String = "", val type: String = "", val start: String = "", val durationS: Double? = null,
                                   val distanceM: Double? = null, val kcal: Double? = null)
@Serializable data class Avg7(val sleepMin: Double? = null, val restingHr: Double? = null, val hrv: Double? = null)
@Serializable data class Day(
    val date: String, val targets: Targets = Targets(), val budget: Double? = null, val activityBonus: Double = 0.0, val movement: Double = 0.0, val eaten: Eaten = Eaten(),
    val meals: Map<String, List<Entry>> = emptyMap(), val water: Water = Water(), val steps: Steps = Steps(), val activity: Activity? = null,
    val weight: Double? = null, val hideNumbers: Boolean = false, val extra: Extra = Extra(), val avg7: Avg7 = Avg7(), val activities: List<GActivity> = emptyList(),
    val trend: List<TrendDay> = emptyList(),
)
/** Körperwerte eines der letzten 14 Tage (für die Verläufe der Körper-Kachel). */
@Serializable data class TrendDay(val day: String, val sleepMin: Int? = null, val restingHr: Int? = null, val hrv: Double? = null, val stress: Double? = null,
                                  val bbHigh: Double? = null, val bbLow: Double? = null)
@Serializable data class DayStat(val day: String, val kcal: Double = 0.0, val protein: Double = 0.0, val carbs: Double = 0.0, val fat: Double = 0.0, val water: Int = 0,
                                 val steps: Int? = null, val stepGoal: Int? = null, val weight: Double? = null, val activeKcal: Double? = null,
                                 val sleepMin: Int? = null, val restingHr: Int? = null, val entries: Int = 0)
@Serializable data class Stats(val days: List<DayStat> = emptyList(), val targets: Targets = Targets())

@Serializable data class DiaryAdd(val date: String, val meal: String, val food: String? = null, val amount: Double? = null, val portion: String? = null,
                                  val name: String? = null, val quick: Nutrients? = null, val remember: String? = null)
@Serializable data class DiaryChange(val amount: Double? = null, val meal: String? = null, val portion: String? = null)
@Serializable data class WaterAdd(val date: String, val ml: Int)
@Serializable data class BodySet(val date: String, val weight: Double? = null)
@Serializable data class OwnFood(val name: String, val brand: String? = null, val barcode: String? = null, val per100: Nutrients, val portions: List<Portion>, val liquid: Boolean)
@Serializable data class ActivityDay(val date: String, val steps: Int? = null, val activeKcal: Double? = null, val distance: Double? = null, val sleepMin: Int? = null,
                                     val restingHr: Int? = null, val weight: Double? = null, val bodyFat: Double? = null)
@Serializable data class ActivityUpload(val source: String, val days: List<ActivityDay>)

// Stufe 2: Beschreiben, Vorlagen, Essensplan, Rezepte, Fasten
@Serializable data class ParseBody(val text: String, val meal: String)
@Serializable data class Proposal(val text: String = "", val name: String, val food: Food? = null, val grams: Double? = null, val portion: String? = null,
                                  val alternatives: List<Hit> = emptyList(), val vague: Boolean = false)
@Serializable data class TemplateItem(val food: String? = null, val name: String, val brand: String? = null, val amount: Double? = null, val portion: String? = null)
@Serializable data class Template(val id: String, val name: String, val meal: String, val items: List<TemplateItem> = emptyList(), val kcal: Double = 0.0)
@Serializable data class TemplateBody(val name: String, val date: String, val meal: String)
@Serializable data class ApplyBody(val date: String, val meal: String)
@Serializable data class CopyBody(val from: String, val to: String, val meal: String, val toMeal: String, val ids: List<Long>? = null)
@Serializable data class Added(val added: Int = 0)
@Serializable data class Planned(val title: String, val recipe: String? = null, val food: String? = null, val kcalPerServing: Double? = null,
                                 val foodName: String? = null, val portion: String? = null, val guessed: Boolean = false)
/** Base: Basisrezept aus der Auslieferung (Ordner „Basisrezepte / Vegan / Frühstück“). */
/** kcalGiven: kcal stammt aus der Angabe „Kalorien je Portion“ im Rezept (nur solche schlägt der Essensplan vor). */
@Serializable data class RecipeEntry(val syncId: String, val title: String, val folder: String? = null, val kcal: Double? = null, val base: Boolean = false,
                                     val kcalGiven: Boolean = false)
/** Texts: die Zutatenzeile für 1 bis 6 Portionen („½ rote Paprikaschote (80 g)“). */
@Serializable data class BaseIngredient(val name: String = "", val texts: List<String> = emptyList())
@Serializable data class BaseInfo(val prep: Int = 0, val cook: Int = 0, val minutes: Int = 0, val allergens: List<String> = emptyList(), val steps: List<String> = emptyList(),
                                  val tip: String? = null, val hint: String? = null, val ingredients: List<BaseIngredient> = emptyList())
@Serializable data class RecipeLine(val text: String, val name: String? = null, val food: Hit? = null, val grams: Double? = null, val portion: String? = null,
                                    val kcal: Double = 0.0, val alternatives: List<Hit> = emptyList(), val vague: Boolean = false)
@Serializable data class RecipeNutrition(val syncId: String, val title: String, val servings: Int = 4, val servingsGuessed: Boolean = false,
                                         val lines: List<RecipeLine> = emptyList(), val total: Nutrients = Nutrients(), val perServing: Nutrients = Nutrients(),
                                         val weight: Double = 0.0, val unmatched: Int = 0, val base: BaseInfo? = null, val kcalGiven: Double? = null)
@Serializable data class ServingsBody(val servings: Int)
@Serializable data class MapBody(val name: String, val food: String)
@Serializable data class FastingSettings(val enabled: Boolean = false, val plan: String = "16:8", val customHours: Int = 16, val eatFrom: String = "12:00",
                                         val fastDays: List<Int> = listOf(1, 4), val remind: Boolean = true)
@Serializable data class FastNow(val id: Long, val start: String, val goal: Double, val hours: Double, val ends: String)
@Serializable data class FastingView(val settings: FastingSettings = FastingSettings(), val notAllowed: String? = null, val goalHours: Double = 16.0,
                                     val plannedStart: String? = null, val current: FastNow? = null)
@Serializable data class AtBody(val at: String? = null)
@Serializable data class PhotoAvailable(val available: Boolean = false)
/** Foto aus der Galerie während einer Aktivität (kind: file oder ext, path wie in der Ablage) */
@Serializable data class ActivityPhoto(val kind: String = "file", val path: String, val name: String, val mime: String? = null, val etag: String? = null,
                                       val taken: Long? = null, val lat: Double? = null, val lon: Double? = null)
/** track: Punkte [Breite, Länge]; trackError: warum es keine Strecke gibt; unlocated: Fotos ohne bekannten Ort */
@Serializable data class ActivityDetail(val id: Long = 0, val name: String = "", val type: String = "", val start: String = "", val durationS: Double? = null,
                                        val distanceM: Double? = null, val kcal: Double? = null, val track: List<List<Double>> = emptyList(),
                                        val trackError: String? = null, val photos: List<ActivityPhoto> = emptyList(), val unlocated: Int = 0)
/** Vorschau des Verortens je Foto ohne Ort: state ok (Stelle lat/lon), outside, readonly, unsupported; target file oder sidecar */
@Serializable data class GeoItem(val path: String, val name: String, val mime: String? = null, val etag: String? = null, val taken: Long? = null,
                                 val state: String = "", val lat: Double? = null, val lon: Double? = null, val rough: Boolean = false, val target: String? = null)
@Serializable data class GeoPreview(val timed: Boolean = false, val trackError: String? = null, val photos: Int = 0, val located: Int = 0, val items: List<GeoItem> = emptyList())
@Serializable data class GeoFailure(val path: String = "", val name: String = "", val error: String = "")
@Serializable data class GeoState(val running: Boolean = false, val total: Int = 0, val done: Int = 0, val failed: List<GeoFailure> = emptyList())
@Serializable data class GeoBody(val offset: Double, val paths: List<String>)
/** text: was die KI erkannt hat („200 g Spaghetti, …“), items: daraus wie beim Beschreiben */
@Serializable data class PhotoResult(val text: String = "", val items: List<Proposal> = emptyList())
@Serializable data class LocationBody(val lat: Double, val lon: Double)

val MEALS = listOf("breakfast", "lunch", "dinner", "snack")
/** Mahlzeit nach Uhrzeit vorschlagen (wie im Web): Frühstück 5–10 Uhr, Mittagessen 11:30–14:30, Abendessen 17:30–21 Uhr, dazwischen Snack. */
fun mealNow(): String {
    val t = java.time.LocalTime.now().let { it.hour * 60 + it.minute }
    return when (t) { in 300 until 600 -> "breakfast"; in 690 until 870 -> "lunch"; in 1050 until 1260 -> "dinner"; else -> "snack" }
}

/** Sprung aus dem Coach-Widget: „add“ öffnet das Eintragen, „scan“ gleich den Barcode-Scanner. */
object CoachLaunch { val request = androidx.compose.runtime.mutableStateOf<String?>(null) }

/** Blockiert - aus Coroutinen aufrufen (schaltet selbst auf IO). */
class CoachRepo(private val app: App) {
    private fun api(): ApiClient = app.api ?: throw IllegalStateException("Kein Konto")
    private suspend fun <T> io(block: () -> T): T = withContext(Dispatchers.IO) { block() }
    private fun enc(s: String) = Uri.encode(s)

    suspend fun profile(): ProfileResult = io { val a = api(); a.get(a.url("/api/coach/profile")) }
    suspend fun saveProfile(p: Profile): ProfileResult = io { val a = api(); a.json.decodeFromString(a.putText(a.url("/api/coach/profile"), a.json.encodeToString(Profile.serializer(), p))) }
    suspend fun day(date: String): Day = io { val a = api(); a.get(a.url("/api/coach/day?date=" + enc(date))) }
    suspend fun search(q: String, meal: String): List<Hit> = io { val a = api(); a.get(a.url("/api/coach/foods/search?q=" + enc(q) + "&meal=" + meal)) }
    /** Markenprodukte bei Open Food Facts nach Namen. */
    suspend fun searchOff(q: String): OffResult = io { val a = api(); a.get(a.url("/api/coach/foods/search/off?q=" + enc(q))) }
    private val images = android.util.LruCache<String, android.graphics.Bitmap>(80)
    /** Produktbild (klein, vom eigenen Server); null: keins. */
    suspend fun image(path: String): android.graphics.Bitmap? = images.get(path) ?: io {
        try { val a = api(); val (bytes, _) = a.getBytes(a.url(path)); android.graphics.BitmapFactory.decodeByteArray(bytes, 0, bytes.size) } catch (_: Exception) { null }
    }?.also { images.put(path, it) }
    suspend fun recent(meal: String): List<Hit> = io { val a = api(); a.get(a.url("/api/coach/foods/recent?meal=$meal")) }
    /** Am öftesten eingetragen, gleich zu welcher Mahlzeit. */
    suspend fun frequent(): List<Hit> = io { val a = api(); a.get(a.url("/api/coach/foods/frequent")) }
    /** Aktivität mit Strecke (von Garmin) und den Fotos aus ihrem Zeitraum. */
    suspend fun activity(id: Long): ActivityDetail = io { val a = api(); a.get(a.url("/api/coach/activities/$id")) }
    /** Fotos ohne Ort verorten: Vorschau (offset: Versatz der Kamera-Uhr in Sekunden), im Hintergrund schreiben, Stand. */
    suspend fun geotagPreview(id: Long, offset: Int): GeoPreview = io { val a = api(); a.get(a.url("/api/coach/activities/$id/geotag?offset=$offset")) }
    suspend fun geotagStart(id: Long, offset: Int, paths: List<String>) = io { val a = api(); a.post(a.url("/api/coach/activities/$id/geotag"), GeoBody(offset.toDouble(), paths)) }
    suspend fun geotagState(id: Long): GeoState = io { val a = api(); a.get(a.url("/api/coach/activities/$id/geotag/state")) }
    /** Foto vom Teller: nur, wenn auf dem Server eine KI für einen freigeschaltet ist. */
    suspend fun photoAvailable(): Boolean = io { val a = api(); a.get<PhotoAvailable>(a.url("/api/coach/photo")).available }
    suspend fun photo(jpeg: ByteArray): PhotoResult = io { val a = api(); a.json.decodeFromString(PhotoResult.serializer(), a.putBytes(a.url("/api/coach/photo"), "image/jpeg", jpeg)) }
    suspend fun food(id: String): Food = io { val a = api(); a.get(a.url("/api/coach/foods/" + enc(id))) }
    /** Produkt zum Barcode; null: noch niemandem bekannt (dann anlegen). */
    suspend fun barcode(code: String): Food? = io {
        val a = api()
        try { a.get<Food>(a.url("/api/coach/foods/barcode/" + enc(code))) } catch (e: ApiException) { if (e.code == 404) null else throw e }
    }
    suspend fun saveFood(f: OwnFood): Food = io { val a = api(); a.postFor<OwnFood, Food>(a.url("/api/coach/foods"), f) }
    suspend fun add(body: DiaryAdd): Entry = io { val a = api(); a.postFor<DiaryAdd, Entry>(a.url("/api/coach/diary"), body) }
    suspend fun change(id: Long, body: DiaryChange) = io { val a = api(); a.putText(a.url("/api/coach/diary/$id"), a.json.encodeToString(DiaryChange.serializer(), body)) }
    suspend fun remove(id: Long) = io { val a = api(); a.delete(a.url("/api/coach/diary/$id")) }
    suspend fun water(date: String, ml: Int) = io { val a = api(); a.post(a.url("/api/coach/water"), WaterAdd(date, ml)) }
    suspend fun removeWater(id: Long) = io { val a = api(); a.delete(a.url("/api/coach/water/$id")) }
    suspend fun weight(date: String, weight: Double?) = io { val a = api(); a.putText(a.url("/api/coach/body"), a.json.encodeToString(BodySet.serializer(), BodySet(date, weight))) }
    suspend fun stats(days: Int): Stats = io { val a = api(); a.get(a.url("/api/coach/stats?days=$days")) }
    // Stufe 2
    suspend fun parse(text: String, meal: String): List<Proposal> = io { val a = api(); a.postFor<ParseBody, List<Proposal>>(a.url("/api/coach/parse"), ParseBody(text, meal)) }
    suspend fun templates(): List<Template> = io { val a = api(); a.get(a.url("/api/coach/templates")) }
    suspend fun saveTemplate(name: String, date: String, meal: String): Template = io { val a = api(); a.postFor<TemplateBody, Template>(a.url("/api/coach/templates"), TemplateBody(name, date, meal)) }
    suspend fun applyTemplate(id: String, date: String, meal: String): Added = io { val a = api(); a.postFor<ApplyBody, Added>(a.url("/api/coach/templates/" + enc(id) + "/apply"), ApplyBody(date, meal)) }
    suspend fun deleteTemplate(id: String) = io { val a = api(); a.delete(a.url("/api/coach/templates/" + enc(id))) }
    /** ids: nur diese Einträge (Auswahl bei „Wie gestern“) */
    suspend fun copy(from: String, to: String, meal: String, ids: List<Long>? = null): Added = io { val a = api(); a.postFor<CopyBody, Added>(a.url("/api/coach/diary/copy"), CopyBody(from, to, meal, meal, ids)) }
    suspend fun planned(date: String, meal: String): List<Planned> = io { val a = api(); a.get(a.url("/api/coach/planned?date=" + enc(date) + "&meal=" + meal)) }
    suspend fun recipes(): List<RecipeEntry> = io { val a = api(); a.get(a.url("/api/coach/recipes")) }
    /** Basisrezept als eigene Rezept-Notiz übernehmen. */
    suspend fun adoptRecipe(syncId: String) = io { val a = api(); a.postText(a.url("/api/coach/recipes/" + enc(syncId) + "/adopt"), "{}") }
    suspend fun recipe(syncId: String): RecipeNutrition = io { val a = api(); a.get(a.url("/api/coach/recipes/" + enc(syncId))) }
    suspend fun recipeServings(syncId: String, servings: Int): RecipeNutrition = io {
        val a = api(); a.json.decodeFromString(a.putText(a.url("/api/coach/recipes/" + enc(syncId) + "/servings"), a.json.encodeToString(ServingsBody.serializer(), ServingsBody(servings))))
    }
    suspend fun foodmap(name: String, food: String) = io { val a = api(); a.putText(a.url("/api/coach/foodmap"), a.json.encodeToString(MapBody.serializer(), MapBody(name, food))) }
    suspend fun fasting(): FastingView = io { val a = api(); a.get(a.url("/api/coach/fasting")) }
    suspend fun fastingStart(): FastingView = io { val a = api(); a.postFor<AtBody, FastingView>(a.url("/api/coach/fasting/start"), AtBody()) }
    suspend fun fastingStop(): FastingView = io { val a = api(); a.postFor<AtBody, FastingView>(a.url("/api/coach/fasting/stop"), AtBody()) }
    /** Ungefährer Ort fürs Wetter bei Pukkis Bewegungs-Erinnerung (der Server rundet und hält ihn sechs Stunden). */
    fun sendLocation(lat: Double, lon: Double) { val a = api(); a.putText(a.url("/api/coach/location"), a.json.encodeToString(LocationBody.serializer(), LocationBody(lat, lon))) }

    /** Tageswerte von Health Connect hochladen. */
    fun uploadActivity(days: List<ActivityDay>, source: String) { if (days.isEmpty()) return; val a = api(); a.post(a.url("/api/coach/activity"), ActivityUpload(source, days)) }
}
