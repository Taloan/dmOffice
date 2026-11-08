package at.dimes.dmoffice.ui

import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.material.icons.filled.Smartphone
import androidx.compose.material.icons.filled.PhotoLibrary
import androidx.compose.material.icons.filled.Bedtime
import kotlinx.coroutines.withContext
import kotlinx.coroutines.Dispatchers
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.runtime.key
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.material.icons.filled.PhotoCamera
import androidx.compose.material.icons.filled.AddCircleOutline
import androidx.compose.material.icons.filled.Bolt
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.ArrowDropDown
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.FilterChipDefaults
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Surface
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.systemBarsPadding
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.IntrinsicSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.outlined.AddCircleOutline
import androidx.compose.material.icons.filled.ChevronLeft
import androidx.compose.material.icons.filled.ChevronRight
import androidx.compose.material.icons.filled.MoreVert
import androidx.compose.material.icons.filled.QrCodeScanner
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.produceState
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.material.icons.filled.Check
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.PathEffect
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.health.connect.client.PermissionController
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.coach.Day
import at.dimes.dmoffice.coach.DiaryAdd
import at.dimes.dmoffice.coach.DiaryChange
import at.dimes.dmoffice.coach.Entry
import at.dimes.dmoffice.coach.FastingView
import at.dimes.dmoffice.coach.Food
import at.dimes.dmoffice.coach.HealthSync
import at.dimes.dmoffice.coach.Hit
import at.dimes.dmoffice.coach.MEALS
import at.dimes.dmoffice.coach.Nutrients
import at.dimes.dmoffice.coach.OwnFood
import at.dimes.dmoffice.coach.Portion
import at.dimes.dmoffice.coach.Profile
import at.dimes.dmoffice.coach.ProfileResult
import at.dimes.dmoffice.coach.Stats
import at.dimes.dmoffice.coach.mealNow
import at.dimes.dmoffice.coach.CoachLaunch
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import java.text.NumberFormat
import java.time.LocalDate
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import kotlin.math.roundToInt

/** Coach (docs/dmcoach-konzept.md, Stufe 1): Tagebuch, Statistik, Profil - wie im Web. */

private val nf: NumberFormat get() = NumberFormat.getNumberInstance().apply { maximumFractionDigits = 0 }
private fun f0(v: Double?) = if (v == null) "–" else nf.format(v)
private fun f1(v: Double?) = if (v == null) "–" else NumberFormat.getNumberInstance().apply { maximumFractionDigits = 1 }.format(v)
private fun num(s: String) = s.replace(',', '.').trim().toDoubleOrNull()

private val ProteinColor = Color(0xFFE0675B)
private val CarbsColor = Color(0xFFE0B14E)
private val FatColor = Color(0xFF4FB3A0)
private val WaterColor = Color(0xFF4F8FE0)
private val StepsColor = Color(0xFF5DA84E)
internal val StepsDoneColor = Color(0xFF1E7B3E)
internal val StepsExtraColor = Color(0xFF9BE07A)

/** Schritte zum Ziel; darüber steht der Balken für alle Schritte - das Ziel dunkelgrün, was darüber ist, hellgrün. */
@Composable
internal fun StepsBar(steps: Int, goal: Int, modifier: Modifier = Modifier) {
    if (goal > 0 && steps > goal) Row(modifier.height(4.dp).clip(RoundedCornerShape(2.dp))) {
        Box(Modifier.weight(goal.toFloat()).fillMaxHeight().background(StepsDoneColor))
        Box(Modifier.weight((steps - goal).toFloat()).fillMaxHeight().background(StepsExtraColor))
    }
    else LinearProgressIndicator(progress = { if (goal > 0) (steps.toFloat() / goal).coerceIn(0f, 1f) else 0f }, modifier = modifier,
        color = if (goal in 1..steps) StepsDoneColor else StepsColor)
}
private val SleepColor = Color(0xFF8C8FE6)

@Composable
private fun mealName(m: String) = stringResource(when (m) { "breakfast" -> R.string.coach_breakfast; "lunch" -> R.string.coach_lunch; "dinner" -> R.string.coach_dinner; else -> R.string.coach_snack })

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CoachScreen() {
    var tab by rememberSaveable { mutableIntStateOf(0) }
    // Aus dem Widget („+“ oder Barcode): ins Tagebuch
    val launch by CoachLaunch.request
    LaunchedEffect(launch) { if (launch != null) tab = 0 }
    Scaffold(contentWindowInsets = WindowInsets(0), topBar = { TabTopBar("Coach", insets = WindowInsets(0)) }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize()) {
            PrimaryTabRow(selectedTabIndex = tab) {
                listOf(R.string.coach_tab_diary, R.string.coach_tab_recipes, R.string.coach_tab_stats, R.string.coach_tab_profile).forEachIndexed { i, label ->
                    Tab(selected = tab == i, onClick = { tab = i }, text = { Text(stringResource(label), maxLines = 1) })
                }
            }
            when (tab) {
                0 -> CoachToday(onProfile = { tab = 3 })
                1 -> CoachRecipesView()
                2 -> CoachStatsView()
                else -> CoachProfileView()
            }
        }
    }
}

// ---------------- Tagebuch ----------------

@Composable
private fun CoachToday(onProfile: () -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).coach
    val scope = rememberCoroutineScope()
    var date by rememberSaveable { mutableStateOf(LocalDate.now().toString()) }
    var day by remember { mutableStateOf<Day?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var tick by remember { mutableIntStateOf(0) }
    var adding by remember { mutableStateOf<String?>(null) }
    var fasting by remember { mutableStateOf<FastingView?>(null) }
    var templateFor by remember { mutableStateOf<String?>(null) }
    var scanFirst by remember { mutableStateOf(false) }
    var photoFirst by remember { mutableStateOf(false) }
    var showFasting by remember { mutableStateOf(false) }
    var openMeal by remember { mutableStateOf<String?>(null) }
    var openActivity by remember { mutableStateOf<Long?>(null) }
    val launch by CoachLaunch.request
    var askWeight by remember { mutableStateOf(false) }
    LaunchedEffect(launch) {
        val l = launch ?: return@LaunchedEffect
        CoachLaunch.request.value = null
        date = LocalDate.now().toString()
        // Widget: Gewicht eintragen - nur der Dialog, kein Eintragen von Essen
        if (l == "weight") { askWeight = true; return@LaunchedEffect }
        scanFirst = l == "scan"; photoFirst = l == "photo"; adding = mealNow()
    }
    // Widget auf dem Startbildschirm mitziehen, wenn sich hier etwas ändert
    LaunchedEffect(tick) { if (tick > 0) at.dimes.dmoffice.widget.CoachWidget.refresh(ctx) }
    // Bereichssperre: nach dem Entsperren neu laden
    val unlocked by TresorGate.changed.collectAsState()
    LaunchedEffect(date, tick, unlocked) {
        // Beim Öffnen von heute: Uhrdaten frisch holen (wenn verbunden)
        if (tick == 0 && date == LocalDate.now().toString() && HealthSync.enabled(ctx)) try { HealthSync.sync(ctx, 2) } catch (_: Exception) { }
        try { day = repo.day(date); error = null } catch (e: Exception) { error = e.message ?: e.toString() }
        fasting = try { repo.fasting() } catch (_: Exception) { null }
    }
    val copiedText = stringResource(R.string.coach_copied)
    val savedText = stringResource(R.string.coach_template_saved)
    fun run(block: suspend () -> Unit) = scope.launch { try { block(); tick++ } catch (e: Exception) { error = e.message ?: e.toString() } }
    if (askWeight) WeightDialog(onDismiss = { askWeight = false }, onWeight = { w -> askWeight = false; run { repo.weight(LocalDate.now().toString(), w) } })
    val today = LocalDate.now()
    val d = LocalDate.parse(date)
    Box(Modifier.fillMaxSize()) {
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { date = d.minusDays(1).toString() }) { Icon(Icons.Default.ChevronLeft, contentDescription = stringResource(R.string.coach_prev_day)) }
            Text(when (d) { today -> stringResource(R.string.coach_today); today.minusDays(1) -> stringResource(R.string.coach_yesterday)
                else -> d.format(DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM)) },
                modifier = Modifier.weight(1f).clickable { date = today.toString() }, textAlign = TextAlign.Center, style = MaterialTheme.typography.titleMedium)
            IconButton(onClick = { date = d.plusDays(1).toString() }, enabled = d < today) { Icon(Icons.Default.ChevronRight, contentDescription = stringResource(R.string.coach_next_day)) }
        }
        error?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp)) }
        val dd = day
        if (dd == null) { Box(Modifier.fillMaxSize()) { CircularProgressIndicator(Modifier.align(Alignment.Center)) }; return@Column }
        LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(12.dp, 4.dp, 12.dp, 96.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            // Kopf: Ringe, Nährstoffe, Zeile zum Fasten bzw. zur Mahlzeit (antippen: Fasten starten/beenden)
            item(key = "head") {
                DayHeadCard(dd, fasting, date == today.toString(), macros = {
                    if (dd.targets.complete) Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Macro(stringResource(R.string.coach_protein), dd.eaten.protein, dd.targets.protein, ProteinColor, Modifier.weight(1f))
                        Macro(stringResource(R.string.coach_fat), dd.eaten.fat, dd.targets.fat, FatColor, Modifier.weight(1f))
                        Macro(stringResource(R.string.coach_carbs), dd.eaten.carbs, dd.targets.carbs, CarbsColor, Modifier.weight(1f))
                    }
                }, onFastingTap = { showFasting = !showFasting }, onProfile = onProfile)
            }
            fasting?.let { fv -> if (showFasting && fv.settings.enabled && fv.notAllowed == null && date == today.toString()) item(key = "fasting") { FastingCard(fv, onChange = { fasting = it }, onError = { error = it }) } }
            // Mahlzeiten nur als Summe - die Zutaten beim Antippen
            items(MEALS, key = { "m:$it" }) { m -> MealSummary(m, dd.meals[m] ?: emptyList(), dd.budget, dd.hideNumbers, onOpen = { openMeal = m }, onAdd = { adding = m }) }
            // Untereinander (auf dem Handy besser zu erfassen): Schritte, Wasser, Gewicht
            item(key = "steps") { StepsTile(dd) { StepsSync(dd, onSynced = { tick++ }) } }
            item(key = "water") { WaterTile(dd, Modifier.fillMaxWidth(), onWater = { ml -> run { repo.water(date, ml) } }, onUndo = { id -> run { repo.removeWater(id) } }) }
            item(key = "weight") { WeightTile(dd, Modifier.fillMaxWidth()) { w -> run { repo.weight(date, w) } } }
            if (dd.activities.isNotEmpty()) item(key = "activities") { ActivitiesTile(dd) { openActivity = it } }
            if (dd.activity?.sleepMin != null || dd.activity?.restingHr != null || dd.extra.stress != null || dd.extra.hrv != null || dd.extra.bodyBatteryHigh != null)
                item(key = "body") { BodyCard(dd) }
        }
    }
    // Wie in den anderen Reitern: „+“ trägt ein, die Mahlzeit schlägt die Uhrzeit vor (dazwischen Snack)
    if (day != null) AddButton(stringResource(R.string.coach_add_food), Modifier.align(Alignment.BottomEnd).padding(16.dp)) { scanFirst = false; adding = mealNow() }
    }
    openActivity?.let { ActivityDetailScreen(it) { openActivity = null } }
    openMeal?.let { m ->
        day?.let { dd ->
            MealDetail(m, dd, onDismiss = { openMeal = null }, onAdd = { openMeal = null; adding = m },
                onRemove = { id -> run { repo.remove(id) } }, onMove = { id, to -> run { repo.change(id, DiaryChange(meal = to)) } },
                onAmount = { id, g -> run { repo.change(id, DiaryChange(amount = g)) } },
                loadYesterday = { repo.day(LocalDate.parse(date).minusDays(1).toString()).meals[m] ?: emptyList() },
                onYesterday = { ids -> run {
                    val r = repo.copy(LocalDate.parse(date).minusDays(1).toString(), date, m, ids)
                    android.widget.Toast.makeText(ctx, copiedText.format(r.added), android.widget.Toast.LENGTH_SHORT).show()
                } },
                onTemplate = { templateFor = m })
        }
    }
    adding?.let { meal -> AddFoodDialog(date, meal, scan = scanFirst, photo = photoFirst, onDismiss = { adding = null; scanFirst = false; photoFirst = false }, onAdded = { tick++ }) }
    templateFor?.let { meal ->
        TemplateNameDialog(mealName(meal), onDismiss = { templateFor = null }) { name ->
            templateFor = null
            run { repo.saveTemplate(name, date, meal); android.widget.Toast.makeText(ctx, savedText, android.widget.Toast.LENGTH_SHORT).show() }
        }
    }
}

@Composable
private fun RingCard(d: Day, onProfile: () -> Unit) {
    val budget = d.budget
    val left = budget?.let { it - d.eaten.kcal }
    val brand = MaterialTheme.colorScheme.primary
    val track = MaterialTheme.colorScheme.surfaceVariant
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.fillMaxWidth().padding(12.dp), horizontalAlignment = Alignment.CenterHorizontally) {
            Box(Modifier.size(140.dp), contentAlignment = Alignment.Center) {
                val part = if (budget != null && budget > 0) (d.eaten.kcal / budget).coerceIn(0.0, 1.0).toFloat() else 0f
                Canvas(Modifier.fillMaxSize().padding(8.dp)) {
                    drawArc(track, -90f, 360f, false, style = Stroke(12.dp.toPx(), cap = StrokeCap.Round))
                    drawArc(brand, -90f, 360f * part, false, style = Stroke(12.dp.toPx(), cap = StrokeCap.Round))
                }
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(if (budget == null) f0(d.eaten.kcal) else if (d.hideNumbers) "${(d.eaten.kcal / budget * 100).roundToInt()} %" else f0(kotlin.math.abs(left!!)),
                        style = MaterialTheme.typography.headlineSmall, fontWeight = FontWeight.SemiBold)
                    Text(stringResource(when { budget == null -> R.string.coach_kcal_eaten; d.hideNumbers -> R.string.coach_of_goal; left!! >= 0 -> R.string.coach_kcal_left; else -> R.string.coach_kcal_over }),
                        style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            if (budget != null && !d.hideNumbers) Text(stringResource(R.string.coach_goal_eaten, f0(d.targets.kcal), f0(d.eaten.kcal)) +
                if (d.activityBonus > 0) " · " + stringResource(R.string.coach_activity_bonus, f0(d.activityBonus)) else "",
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            if (!d.targets.complete) {
                Text(stringResource(R.string.coach_profile_missing), style = MaterialTheme.typography.bodySmall, textAlign = TextAlign.Center, modifier = Modifier.padding(top = 6.dp))
                TextButton(onClick = onProfile) { Text(stringResource(R.string.coach_fill_profile)) }
            }
        }
    }
}

@Composable
private fun Macro(label: String, value: Double, goal: Double?, color: Color, modifier: Modifier) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(3.dp)) {
        Text(label, style = MaterialTheme.typography.labelMedium)
        Text("${f0(value)}${goal?.let { " / " + f0(it) } ?: ""} g", style = MaterialTheme.typography.bodySmall)
        LinearProgressIndicator(progress = { if (goal != null && goal > 0) (value / goal).toFloat().coerceIn(0f, 1f) else 0f }, color = color, modifier = Modifier.fillMaxWidth())
    }
}

@Composable
private fun MealCard(meal: String, entries: List<Entry>, onAdd: () -> Unit, onRemove: (Long) -> Unit, onMove: (Long, String) -> Unit,
                     onYesterday: () -> Unit, onTemplate: () -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(horizontal = 12.dp, vertical = 6.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Text(mealName(meal), style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                if (entries.isNotEmpty()) Text(f0(entries.sumOf { it.kcal }) + " kcal", style = MaterialTheme.typography.bodyMedium)
                IconButton(onClick = onAdd) { Icon(Icons.Default.Add, contentDescription = stringResource(R.string.coach_add_to, mealName(meal)), tint = if (meal == mealNow() && entries.isEmpty()) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant) }
                var menu by remember { mutableStateOf(false) }
                Box {
                    IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
                    DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                        DropdownMenuItem(text = { Text(stringResource(R.string.coach_like_yesterday)) }, onClick = { menu = false; onYesterday() })
                        if (entries.isNotEmpty()) DropdownMenuItem(text = { Text(stringResource(R.string.coach_save_template)) }, onClick = { menu = false; onTemplate() })
                    }
                }
            }
            for (e in entries) {
                HorizontalDivider()
                Row(Modifier.padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                    Column(Modifier.weight(1f)) {
                        Text(e.name, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        val sub = listOfNotNull(e.brand, e.portion, e.amount?.takeIf { e.portion?.contains(Regex("\\d\\s*(g|ml)\\b")) != true }?.let { f0(it) + " g" }).joinToString(" · ")
                        if (sub.isNotEmpty()) Text(sub, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                    Text(f0(e.kcal) + " kcal", style = MaterialTheme.typography.bodyMedium)
                    var menu by remember { mutableStateOf(false) }
                    Box {
                        IconButton(onClick = { menu = true }) { Icon(Icons.Default.MoreVert, contentDescription = stringResource(R.string.more)) }
                        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                            for (m in MEALS.filter { it != meal }) DropdownMenuItem(text = { Text(stringResource(R.string.coach_move_to, mealName(m))) }, onClick = { menu = false; onMove(e.id, m) })
                            DropdownMenuItem(text = { Text(stringResource(R.string.delete), color = MaterialTheme.colorScheme.error) }, onClick = { menu = false; onRemove(e.id) })
                        }
                    }
                }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun WaterCard(d: Day, onWater: (Int) -> Unit, onUndo: (Long) -> Unit) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row { Text(stringResource(R.string.coach_water), style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f)); Text("${f1(d.water.ml / 1000.0)} / ${f1(d.water.goal / 1000.0)} l") }
            // Eigene Gläser und Flaschen aus dem Profil (Symbole und „+“-Knöpfe)
            CoachWaterVessels(d.water.ml, d.water.goal, d.water.sizes, onWater)
            d.water.entries.lastOrNull()?.let { last -> TextButton(onClick = { onUndo(last.id) }) { Text(stringResource(R.string.coach_undo_last)) } }
            if (d.water.watch > 0) Text(stringResource(R.string.coach_water_watch, f1(d.water.watch / 1000.0)), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

/** Abgleich mit Health Connect (verbinden, jetzt abholen) - unter den Schritten. */
@Composable
private fun StepsSync(d: Day, onSynced: () -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var busy by remember { mutableStateOf(false) }
    val connect = rememberLauncherForActivityResult(PermissionController.createRequestPermissionResultContract()) { granted ->
        if (granted.intersect(HealthSync.readPermissions).isNotEmpty()) scope.launch { busy = true; try { HealthSync.enable(ctx) } catch (_: Exception) { }; busy = false; onSynced() }
    }
    if (d.steps.mode == "dynamic") Text(stringResource(R.string.coach_goal_dynamic), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    when {
        busy -> LinearProgressIndicator(Modifier.fillMaxWidth())
        HealthSync.enabled(ctx) -> TextButton(onClick = { scope.launch { busy = true; try { HealthSync.sync(ctx, 7) } catch (_: Exception) { }; busy = false; onSynced() } }) { Text(stringResource(R.string.coach_hc_sync_now)) }
        HealthSync.available(ctx) -> OutlinedButton(onClick = { connect.launch(HealthSync.readPermissions + HealthSync.extraPermissions) }) { Text(stringResource(R.string.coach_hc_connect)) }
        HealthSync.needsInstall(ctx) -> Text(stringResource(R.string.coach_hc_install), style = MaterialTheme.typography.bodySmall)
    }
}

/**
 * Körper: Stress, HRV, Ruhepuls, Body Battery und Schlaf von der Uhr - je Wert eine kleine Karte mit dem Schnitt der sieben Tage
 * davor und dem Verlauf der letzten 14 Tage (Balken für Schlaf und Stress, Linie für Ruhepuls und HRV, Spanne für Body Battery),
 * das Wochenende im Verlauf heller hinterlegt.
 */
@Composable
private fun BodyCard(d: Day) {
    fun hm(min: Int) = "%d:%02d".format(min / 60, min % 60)
    val avg = stringResource(R.string.coach_avg)
    val days = stringResource(R.string.coach_trend_14)
    @Composable
    fun Mini(label: String, value: String, sub: String?, modifier: Modifier, chart: @Composable () -> Unit) {
        Column(modifier.clip(RoundedCornerShape(12.dp)).background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.05f)).padding(10.dp)) {
            Text(label, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            Row(verticalAlignment = Alignment.Bottom) {
                Text(value, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                sub?.let { Text("  $it", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 2.dp)) }
            }
            Spacer(Modifier.height(6.dp))
            Box(Modifier.fillMaxWidth().height(28.dp)) { chart() }
        }
    }
    val t = d.trend
    // Samstag und Sonntag bekommen in den Verläufen einen helleren Streifen
    val weekend = t.map { runCatching { LocalDate.parse(it.day).dayOfWeek.value >= 6 }.getOrDefault(false) }
    val cells = buildList<Pair<String, @Composable (Modifier) -> Unit>> {
        d.extra.stress?.let { v -> add("stress" to { m -> Mini(stringResource(R.string.coach_stress), f0(v), null, m) { TrendBars(t.map { it.stress }, Color(0xFF378ADD), weekend) } }) }
        d.extra.hrv?.let { v -> add("hrv" to { m -> Mini("HRV", f0(v) + " ms", d.avg7.hrv?.let { "$avg " + f0(it) }, m) { TrendLine(t.map { it.hrv }, Color(0xFF1D9E75), weekend) } }) }
        d.activity?.restingHr?.let { v -> add("hr" to { m -> Mini(stringResource(R.string.coach_resting_hr), "$v", d.avg7.restingHr?.let { "$avg " + f0(it) }, m) { TrendLine(t.map { it.restingHr?.toDouble() }, Color(0xFFE0675B), weekend) } }) }
        if (d.extra.bodyBatteryHigh != null || d.extra.bodyBatteryLow != null) add("bb" to { m ->
            Mini("Body Battery", "${d.extra.bodyBatteryLow?.let { f0(it) } ?: "–"} → ${d.extra.bodyBatteryHigh?.let { f0(it) } ?: "–"}", null, m) {
                BatteryRange(d.extra.bodyBatteryLow ?: 0.0, d.extra.bodyBatteryHigh ?: 100.0)
            }
        })
        d.activity?.sleepMin?.let { v -> add("sleep" to { m -> Mini(stringResource(R.string.coach_sleep), hm(v), d.avg7.sleepMin?.let { "$avg " + hm(it.toInt()) }, m) { TrendBars(t.map { it.sleepMin?.toDouble() }, SleepColor, weekend) } }) }
    }
    // Im Stil der übrigen Kacheln: getönt in der Farbe des Schlafs
    Tile(SleepColor, Modifier.fillMaxWidth()) {
        Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(Icons.Default.Bedtime, contentDescription = null, tint = SleepColor, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(6.dp))
                Text(stringResource(R.string.coach_body), color = SleepColor, style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f))
                Text(days, style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            // Zwei Spalten; eine übrig gebliebene Karte geht über die ganze Breite
            for (row in cells.chunked(2)) Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                for ((_, cell) in row) cell(Modifier.weight(1f))
            }
        }
    }
}

/** Hellerer Streifen hinter den Tagen des Wochenendes; [step] = Abstand der Tage, [shift] = Versatz der Tagesmitte. */
private fun androidx.compose.ui.graphics.drawscope.DrawScope.weekendStripes(weekend: List<Boolean>, step: Float, shift: Float, color: Color) {
    weekend.forEachIndexed { i, we ->
        if (!we) return@forEachIndexed
        val l = (i * step + shift - step / 2).coerceAtLeast(0f); val r = (i * step + shift + step / 2).coerceAtMost(size.width)
        drawRect(color, Offset(l, 0f), Size(r - l, size.height))
    }
}

/** Balken je Tag (fehlende Tage leer), der letzte (heute) kräftig. */
@Composable
private fun TrendBars(values: List<Double?>, color: Color, weekend: List<Boolean> = emptyList()) {
    val stripe = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.07f)
    Canvas(Modifier.fillMaxSize()) {
        val w = size.width / values.size.coerceAtLeast(1)
        weekendStripes(weekend, w, w / 2, stripe)
        val max = values.maxOfOrNull { it ?: 0.0 }?.takeIf { it > 0 } ?: return@Canvas
        values.forEachIndexed { i, v ->
            if (v == null) return@forEachIndexed
            val h = (v / max).toFloat() * size.height
            drawRoundRect(color.copy(alpha = if (i == values.lastIndex) 1f else 0.55f), Offset(i * w + w * 0.15f, size.height - h), Size(w * 0.7f, h),
                androidx.compose.ui.geometry.CornerRadius(2f, 2f))
        }
    }
}

/** Linie über die Tage mit Werten. */
@Composable
private fun TrendLine(values: List<Double?>, color: Color, weekend: List<Boolean> = emptyList()) {
    val stripe = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.07f)
    Canvas(Modifier.fillMaxSize()) {
        val step = size.width / (values.size - 1).coerceAtLeast(1)
        weekendStripes(weekend, step, 0f, stripe)
        val pts = values.mapIndexedNotNull { i, v -> v?.let { i to it } }
        if (pts.size < 2) return@Canvas
        val min = pts.minOf { it.second }; val span = (pts.maxOf { it.second } - min).takeIf { it > 0 } ?: 1.0
        val path = androidx.compose.ui.graphics.Path()
        pts.forEachIndexed { k, (i, v) ->
            val x = i * step; val y = size.height - 2f - ((v - min) / span).toFloat() * (size.height - 4f)
            if (k == 0) path.moveTo(x, y) else path.lineTo(x, y)
        }
        drawPath(path, color, style = Stroke(width = 2.dp.toPx(), cap = StrokeCap.Round))
        val (li, lv) = pts.last()
        drawCircle(color, 3.dp.toPx(), Offset(li * step, size.height - 2f - ((lv - min) / span).toFloat() * (size.height - 4f)))
    }
}

/** Body Battery: Skala rot → grün, darauf der Bereich vom niedrigsten zum höchsten Wert. */
@Composable
private fun BatteryRange(low: Double, high: Double) = Canvas(Modifier.fillMaxSize()) {
    val y = size.height / 2; val bar = 8.dp.toPx()
    drawRoundRect(androidx.compose.ui.graphics.Brush.horizontalGradient(listOf(Color(0xFFD13438), Color(0xFFE8A33A), Color(0xFF5DA84E))),
        Offset(0f, y - bar / 2), Size(size.width, bar), androidx.compose.ui.geometry.CornerRadius(bar / 2, bar / 2))
    val a = (low / 100).toFloat().coerceIn(0f, 1f) * size.width; val b = (high / 100).toFloat().coerceIn(0f, 1f) * size.width
    drawRoundRect(Color.White, Offset(a, y - bar / 2 - 3f), Size((b - a).coerceAtLeast(4f), bar + 6f), androidx.compose.ui.geometry.CornerRadius(6f, 6f), style = Stroke(2.dp.toPx()))
}

@Composable
private fun WeightCard(d: Day, onWeight: (Double) -> Unit) {
    var v by remember(d.date) { mutableStateOf("") }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row { Text(stringResource(R.string.coach_weight), style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f)); Text(d.weight?.let { f1(it) + " kg" } ?: "–") }
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(v, { v = it }, singleLine = true, label = { Text("kg") }, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal), modifier = Modifier.width(140.dp))
                val n = num(v)
                Button(onClick = { onWeight(n!!); v = "" }, enabled = n != null && n in 20.0..400.0) { Text(stringResource(R.string.coach_enter)) }
            }
        }
    }
}

// ---------------- Erfassen ----------------

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun AddFoodDialog(date: String, initialMeal: String, scan: Boolean = false, photo: Boolean = false, onDismiss: () -> Unit, onAdded: () -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).coach
    val scope = rememberCoroutineScope()
    var meal by remember { mutableStateOf(initialMeal) }
    var mode by remember { mutableIntStateOf(if (photo) 4 else 0) }   // 0 Suchen, 1 Nur kcal, 2 Eigenes, 3 Text/Sprache, 4 Foto (aus dem großen Widget)
    var list by remember { mutableIntStateOf(0) }   // ohne Suchtext: 0 Zuletzt, 1 Häufig, 2 Plan und Vorlagen
    var frequent by remember { mutableStateOf<List<Hit>?>(null) }
    var photoOk by remember { mutableStateOf(false) }
    var text by remember { mutableStateOf("") }
    var hits by remember { mutableStateOf<List<Hit>?>(null) }
    var offHits by remember { mutableStateOf<List<Hit>?>(null) }
    var recent by remember { mutableStateOf<List<Hit>?>(null) }
    var food by remember { mutableStateOf<Food?>(null) }
    var preset by remember { mutableStateOf<Hit?>(null) }
    var unknown by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(meal) { recent = try { repo.recent(meal) } catch (_: Exception) { emptyList() } }
    LaunchedEffect(list) { if (list == 1 && frequent == null) frequent = try { repo.frequent() } catch (_: Exception) { emptyList() } }
    LaunchedEffect(Unit) { photoOk = try { repo.photoAvailable() } catch (_: Exception) { false } }
    LaunchedEffect(text, meal) {
        if (text.isBlank()) { hits = null; return@LaunchedEffect }
        delay(250)
        hits = try { repo.search(text.trim(), meal) } catch (e: Exception) { error = e.message; emptyList() }
    }
    // Open Food Facts erst nach einer Tipp-Pause (höchstens 10 Suchen pro Minute)
    LaunchedEffect(text) {
        offHits = null
        if (text.trim().length < 3) return@LaunchedEffect
        delay(800)
        offHits = try { repo.searchOff(text.trim()).hits } catch (_: Exception) { emptyList() }
    }
    val scanner = rememberLauncherForActivityResult(ScanContract()) { result ->
        val code = result.contents ?: return@rememberLauncherForActivityResult
        scope.launch {
            try { val f = repo.barcode(code); if (f != null) { food = f; preset = null } else { unknown = code; mode = 2 } }
            catch (e: Exception) { error = e.message ?: e.toString() }
        }
    }
    fun startScan() = scanner.launch(ScanOptions().setDesiredBarcodeFormats(ScanOptions.EAN_13, ScanOptions.EAN_8, ScanOptions.UPC_A, ScanOptions.UPC_E)
        .setPrompt(ctx.getString(R.string.coach_scan_prompt)).setBeepEnabled(false).setOrientationLocked(false))
    // Aus dem Widget mit Barcode: Scanner sofort öffnen
    LaunchedEffect(Unit) { if (scan) startScan() }
    fun add(body: DiaryAdd) = scope.launch {
        try { repo.add(body); onAdded(); onDismiss() } catch (e: Exception) { error = e.message ?: e.toString() }
    }
    // Schnell: „+“ in der Trefferliste trägt die übliche Portion ein (bei schon Gegessenem die Menge von damals); der Dialog bleibt offen
    var quick by remember { mutableIntStateOf(0) }
    // Gleich lostippen: Suchfeld fokussieren und Tastatur zeigen (auch zurück aus der Detailansicht)
    val searchFocus = remember { androidx.compose.ui.focus.FocusRequester() }
    val keyboard = androidx.compose.ui.platform.LocalSoftwareKeyboardController.current
    LaunchedEffect(mode, food) {
        if (mode == 0 && food == null) { delay(200); try { searchFocus.requestFocus(); keyboard?.show() } catch (_: IllegalStateException) { } }
    }
    val quickAdd: (Hit) -> Unit = { h -> scope.launch {
        try {
            repo.add(DiaryAdd(date, meal, food = h.id, amount = if (h.mine) h.amount else null, portion = if (h.mine) h.portion else null)); quick++; onAdded()
            android.widget.Toast.makeText(ctx, ctx.getString(R.string.coach_quick_added, h.name), android.widget.Toast.LENGTH_SHORT).show()
        }
        catch (e: Exception) { error = e.message ?: e.toString() }
    } }
    // Vollbild statt Dialog (lesbarer): oben das Suchfeld in der Farbe der Mahlzeit, darunter die Wege zum Eintragen als Kacheln
    val accent = mealColor(meal)
    androidx.compose.ui.window.Dialog(onDismissRequest = onDismiss, properties = androidx.compose.ui.window.DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.surface) {
    Column(Modifier.fillMaxSize().systemBarsPadding().imePadding().padding(horizontal = 14.dp, vertical = 8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { if (food != null) food = null else if (mode != 0) mode = 0 else onDismiss() }) {
                Icon(if (food != null || mode != 0) Icons.AutoMirrored.Filled.ArrowBack else Icons.Default.Close, contentDescription = stringResource(R.string.close))
            }
            // Mahlzeit wechseln (in ihrer Farbe)
            var open by remember { mutableStateOf(false) }
            Box {
                TextButton(onClick = { open = true }) {
                    Text(mealName(meal), color = accent, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.SemiBold)
                    Icon(Icons.Default.ArrowDropDown, contentDescription = null, tint = accent)
                }
                DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                    for (m in MEALS) DropdownMenuItem(text = { Text(mealName(m), color = mealColor(m)) }, onClick = { meal = m; open = false })
                }
            }
        }
        error?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
        val f = food
        if (f != null) Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) {
            PortionPicker(f, preset, repo) { grams, label -> add(DiaryAdd(date, meal, food = f.id, amount = grams, portion = label)) }
        }
        else {
            if (mode == 0) OutlinedTextField(text, { text = it }, singleLine = true, shape = RoundedCornerShape(14.dp),
                leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                placeholder = { Text(stringResource(when (meal) { "breakfast" -> R.string.coach_ask_breakfast; "lunch" -> R.string.coach_ask_lunch; "dinner" -> R.string.coach_ask_dinner; else -> R.string.coach_ask_snack })) },
                colors = OutlinedTextFieldDefaults.colors(focusedBorderColor = accent, unfocusedBorderColor = accent.copy(alpha = 0.6f), cursorColor = accent),
                modifier = Modifier.fillMaxWidth().focusRequester(searchFocus))
            // Wege zum Eintragen; die gewählte Kachel noch einmal antippen: zurück zur Suche
            Row(Modifier.fillMaxWidth().padding(vertical = 10.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                @Composable fun T(icon: ImageVector, label: Int, tint: Color, selected: Boolean, onClick: () -> Unit) =
                    Column(Modifier.weight(1f), horizontalAlignment = Alignment.CenterHorizontally) {
                        Box(Modifier.fillMaxWidth().height(50.dp).clip(RoundedCornerShape(14.dp))
                            .background(if (selected) tint.copy(alpha = 0.22f) else MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.6f))
                            .border(if (selected) 2.dp else 1.dp, if (selected) tint else MaterialTheme.colorScheme.outlineVariant, RoundedCornerShape(14.dp))
                            .clickable(onClick = onClick), contentAlignment = Alignment.Center) { Icon(icon, contentDescription = null, tint = tint) }
                        Text(stringResource(label), style = MaterialTheme.typography.labelSmall, maxLines = 2, textAlign = androidx.compose.ui.text.style.TextAlign.Center, modifier = Modifier.padding(top = 3.dp))
                    }
                T(Icons.Default.QrCodeScanner, R.string.coach_tile_barcode, Color(0xFFB9A6F5), false) { startScan() }
                T(Icons.Default.Mic, R.string.coach_tile_text, Color(0xFF6FA8EE), mode == 3) { mode = if (mode == 3) 0 else 3 }
                T(Icons.Default.Bolt, R.string.coach_tile_quick, Color(0xFFF2B53C), mode == 1) { mode = if (mode == 1) 0 else 1 }
                T(Icons.Default.AddCircleOutline, R.string.coach_tile_own, Color(0xFF3CC4A0), mode == 2) { mode = if (mode == 2) 0 else 2 }
                if (photoOk) T(Icons.Default.PhotoCamera, R.string.coach_tile_photo, Color(0xFFE07AD6), mode == 4) { mode = if (mode == 4) 0 else 4 }
            }
            when (mode) {
                0 -> {
                    if (text.isBlank()) Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        listOf(R.string.coach_chip_recent, R.string.coach_chip_frequent, R.string.coach_chip_plan).forEachIndexed { i, l ->
                            FilterChip(selected = list == i, onClick = { list = i }, label = { Text(stringResource(l), maxLines = 1) },
                                colors = FilterChipDefaults.filterChipColors(selectedContainerColor = accent, selectedLabelColor = Color.White))
                        }
                    }
                    val shown = if (text.isNotBlank()) hits else when (list) { 0 -> recent; 1 -> frequent; else -> emptyList() }
                    if (text.isBlank() && list == 0 && recent?.isEmpty() == true) Text(stringResource(R.string.coach_search_empty_hint), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 6.dp))
                    if (shown == null) CircularProgressIndicator(Modifier.padding(8.dp).size(24.dp))
                    LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
                        // Plan und Vorlagen: was laut Essensplan dran ist, gespeicherte Mahlzeiten
                        if (text.isBlank() && list == 2) item(key = "plan") {
                            Column {
                                PlannedAndTemplates(date, meal,
                                    onPlanned = { p -> scope.launch { try { food = repo.food(p.food!!); preset = Hit(p.food!!, p.title, portion = p.portion ?: "1 Portion") } catch (e: Exception) { error = e.message } } },
                                    onApplied = { onAdded(); onDismiss() }, onError = { error = it })
                            }
                        }
                        val pickHit: (Hit) -> Unit = { h -> scope.launch { try { food = repo.food(h.id); preset = h } catch (e: Exception) { error = e.message } } }
                        items(shown ?: emptyList(), key = { it.id + (it.amount ?: "") }) { h -> HitRow(h, repo, accent, onQuick = { quickAdd(h) }) { pickHit(h) } }
                        if (text.trim().length >= 3 && hits != null) {
                            val known = hits!!.map { it.id }.toSet()
                            val brands = offHits?.filter { it.id !in known }
                            item(key = "off") { Text(stringResource(R.string.coach_off_brands), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 10.dp, bottom = 2.dp)) }
                            when {
                                brands == null -> item(key = "off-wait") { CircularProgressIndicator(Modifier.padding(8.dp).size(24.dp)) }
                                brands.isEmpty() -> item(key = "off-none") { Text(stringResource(R.string.coach_off_none), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(8.dp)) }
                                else -> items(brands, key = { "off-" + it.id }) { h -> HitRow(h, repo, accent, onQuick = { quickAdd(h) }) { pickHit(h) } }
                            }
                        }
                    }
                }
                else -> Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) {
                    when (mode) {
                        1 -> QuickForm { name, n -> add(DiaryAdd(date, meal, name = name, quick = n)) }
                        3 -> DescribeForm(date, meal, onDone = { onAdded(); onDismiss() }, onError = { error = it }, outer = CoachWork)
                        4 -> PhotoForm(date, meal, onDone = { onAdded(); onDismiss() }, onError = { error = it })
                        else -> OwnFoodForm(unknown) { body ->
                            scope.launch { try { food = repo.saveFood(body); preset = null; mode = 0 } catch (e: Exception) { error = e.message ?: e.toString() } }
                        }
                    }
                }
            }
        }
        // Mehrere nacheinander mit „+“: unten zählt mit, „Fertig“ schließt
        if (quick > 0 && food == null) Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            Box(Modifier.size(46.dp).border(2.dp, accent, CircleShape), contentAlignment = Alignment.Center) { Text("$quick", color = accent, fontWeight = FontWeight.SemiBold) }
            Spacer(Modifier.width(10.dp))
            Button(onClick = onDismiss, modifier = Modifier.weight(1f).height(46.dp), shape = CircleShape,
                colors = ButtonDefaults.buttonColors(containerColor = accent, contentColor = Color.White)) { Text(stringResource(R.string.done)) }
        }
    }
    }
    }
}

/** Foto vom Teller: Kamera, die KI des Servers erkennt Lebensmittel und Mengen, danach wie beim Beschreiben (prüfen, alle eintragen). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PhotoForm(date: String, meal: String, onDone: () -> Unit, onError: (String) -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).coach
    var busy by remember { mutableStateOf(false) }
    var result by remember { mutableStateOf<at.dimes.dmoffice.coach.PhotoResult?>(null) }
    var round by remember { mutableIntStateOf(0) }
    val file = remember { java.io.File(ctx.cacheDir, "camera").apply { mkdirs() }.resolve("teller.jpg") }
    val uri = remember { androidx.core.content.FileProvider.getUriForFile(ctx, ctx.packageName + ".files", file) }
    // Verkleinert hochladen (1600 px reichen zum Erkennen; der Server verkleinert noch einmal) - gespeichert wird das Foto nicht.
    // Läuft außerhalb der Ansicht weiter: Kamera und Galerie sind eigene Bildschirme
    fun send(open: () -> java.io.InputStream?) = CoachWork.launch {
        busy = true; result = null
        try {
            val jpeg = withContext(Dispatchers.IO) {
                val b = android.graphics.BitmapFactory.Options().apply { inJustDecodeBounds = true }
                open()?.use { android.graphics.BitmapFactory.decodeStream(it, null, b) }
                var sample = 1
                while (maxOf(b.outWidth, b.outHeight) / (sample * 2) >= 1600) sample *= 2
                val bmp = open()?.use { android.graphics.BitmapFactory.decodeStream(it, null, android.graphics.BitmapFactory.Options().apply { inSampleSize = sample }) }
                    ?: throw IllegalStateException(ctx.getString(R.string.coach_photo_unreadable))
                java.io.ByteArrayOutputStream().also { bmp.compress(android.graphics.Bitmap.CompressFormat.JPEG, 85, it) }.toByteArray()
            }
            result = repo.photo(jpeg); round++
        } catch (e: kotlinx.coroutines.CancellationException) { throw e } catch (e: Exception) { onError(e.message ?: e.toString()) }
        finally { file.delete(); busy = false }
    }
    val camera = rememberLauncherForActivityResult(ActivityResultContracts.TakePicture()) { ok -> if (ok) send { file.inputStream() } }
    // Aus der dmOffice-Galerie: die Fotos dieses Tages (eigene Ablage und Galerie-Ordner), die große Vorschau reicht zum Erkennen
    var own by remember { mutableStateOf<List<at.dimes.dmoffice.journal.DayPhoto>?>(null) }
    var showOwn by remember { mutableStateOf(false) }
    LaunchedEffect(showOwn) {
        if (showOwn && own == null) own = try { App.get(ctx).journal.suggestDay(date).photos.filter { it.mime?.startsWith("image/") != false }.reversed() } catch (_: Exception) { emptyList() }
    }
    fun sendItem(item: at.dimes.dmoffice.files.FileItem): Unit { CoachWork.launch {
        showOwn = false; busy = true; result = null
        try {
            val bmp = at.dimes.dmoffice.files.Thumbs.load(ctx, item, at.dimes.dmoffice.files.Thumbs.LARGE) ?: throw IllegalStateException(ctx.getString(R.string.coach_photo_unreadable))
            val jpeg = withContext(Dispatchers.IO) { java.io.ByteArrayOutputStream().also { bmp.compress(android.graphics.Bitmap.CompressFormat.JPEG, 85, it) }.toByteArray() }
            result = repo.photo(jpeg); round++
        } catch (e: kotlinx.coroutines.CancellationException) { throw e } catch (e: Exception) { onError(e.message ?: e.toString()) }
        finally { busy = false }
    } }
    fun sendOwn(p: at.dimes.dmoffice.journal.DayPhoto) = sendItem(at.dimes.dmoffice.files.FileItem(path = p.path, name = p.name, isDir = false, mime = p.mime, etag = p.etag))
    // Ältere Fotos: aus der ganzen Galerie oder aus den Dateien von dmOffice (das erste gewählte)
    var pickAll by remember { mutableStateOf(false) }
    var pickFiles by remember { mutableStateOf(false) }
    if (pickAll) GalleryPhotoPicker(onDismiss = { pickAll = false }) { picked -> pickAll = false; picked.firstOrNull()?.let { sendItem(it) } }
    if (pickFiles) StoredFilePicker(onDismiss = { pickFiles = false }) { picked -> pickFiles = false; picked.firstOrNull { !it.isDir }?.let { sendItem(it) } }
    // Schon vorhandenes Foto aus der Galerie (Bildauswahl von Android, ohne Speicherfreigabe)
    val gallery = rememberLauncherForActivityResult(ActivityResultContracts.PickVisualMedia()) { picked ->
        if (picked != null) send { ctx.contentResolver.openInputStream(picked) }
    }
    // Die App meldet die Kamera an (Barcode) - dann will Android die Freigabe auch für die Kamera-App
    val permission = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { ok -> if (ok) camera.launch(uri) }
    fun shoot() {
        if (androidx.core.content.ContextCompat.checkSelfPermission(ctx, android.Manifest.permission.CAMERA) == android.content.pm.PackageManager.PERMISSION_GRANTED) camera.launch(uri)
        else permission.launch(android.Manifest.permission.CAMERA)
    }
    fun pick() = gallery.launch(androidx.activity.result.PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly))
    Column(Modifier.fillMaxWidth(), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        if (busy) { Text(stringResource(R.string.coach_photo_busy), style = MaterialTheme.typography.bodyMedium); LinearProgressIndicator(Modifier.fillMaxWidth()) }
        val r = result
        if (r != null && r.items.isEmpty()) Text(stringResource(R.string.coach_photo_none), style = MaterialTheme.typography.bodyMedium)
        if (r != null && r.items.isNotEmpty()) key(round) { DescribeForm(date, meal, onDone, onError, initialText = r.text, initial = r.items, outer = CoachWork) }
        // Drei Wege als Knöpfe mit Symbol über der Beschriftung (nebeneinander passt „Aus Galerie“ nicht)
        if (!busy) Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            val pad = androidx.compose.foundation.layout.PaddingValues(horizontal = 4.dp, vertical = 6.dp)
            @Composable fun Label(icon: ImageVector, text: Int) = Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Icon(icon, contentDescription = null); Text(stringResource(text), maxLines = 1, style = MaterialTheme.typography.labelMedium)
            }
            Button(onClick = { shoot() }, modifier = Modifier.weight(1f).height(64.dp), shape = RoundedCornerShape(14.dp), contentPadding = pad) {
                Label(Icons.Default.PhotoCamera, R.string.coach_photo_take)
            }
            OutlinedButton(onClick = { showOwn = !showOwn }, modifier = Modifier.weight(1f).height(64.dp), shape = RoundedCornerShape(14.dp), contentPadding = pad) {
                Label(Icons.Default.PhotoLibrary, R.string.coach_photo_pick)
            }
            OutlinedButton(onClick = { pick() }, modifier = Modifier.weight(1f).height(64.dp), shape = RoundedCornerShape(14.dp), contentPadding = pad) {
                Label(Icons.Default.Smartphone, R.string.coach_photo_device)
            }
        }
        // Fotos des Tages aus der Galerie zum Antippen
        if (showOwn && !busy) {
            val list = own
            when {
                list == null -> LinearProgressIndicator(Modifier.fillMaxWidth())
                list.isEmpty() -> Text(stringResource(R.string.coach_photo_own_none), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                // Vier Spalten über die ganze Breite
                else -> Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (row in list.take(60).chunked(4)) Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                        for (p in row) Box(Modifier.weight(1f).aspectRatio(1f).clip(RoundedCornerShape(8.dp)).clickable { sendOwn(p) }) {
                            ThumbOrIcon(at.dimes.dmoffice.files.FileItem(path = p.path, name = p.name, isDir = false, mime = p.mime, etag = p.etag), Modifier.fillMaxSize(), 32.dp)
                        }
                        repeat(4 - row.size) { Spacer(Modifier.weight(1f)) }
                    }
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                TextButton(onClick = { pickAll = true }) { Text(stringResource(R.string.attach_from_gallery)) }
                TextButton(onClick = { pickFiles = true }) { Text(stringResource(R.string.attach_from_files)) }
            }
        }
    }
}

@Composable
private fun sourceName(s: String) = when (s) { "bls" -> "BLS"; "off" -> "Open Food Facts"; "own" -> stringResource(R.string.coach_household); "rw" -> stringResource(R.string.coach_guide_value); else -> s }

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun PortionPicker(food: Food, preset: Hit?, repo: at.dimes.dmoffice.coach.CoachRepo, onAdd: (Double, String) -> Unit) {
    val unit = if (food.liquid) "ml" else "g"
    // Portion mit Menge im Namen: „1 Glas (200 ml)“, „1 Apfel (mittel, 150 g)“ - steht so auch im Tagebuch
    fun withAmount(label: String, amount: Double) = if (Regex("\\d\\s*(g|ml)\\b").containsMatchIn(label)) label
        else if (label.endsWith(")")) label.dropLast(1) + ", ${f0(amount)} $unit)" else "$label (${f0(amount)} $unit)"
    val initial = preset?.portion?.let { p -> food.portions.firstOrNull { p.substringAfter(" × ").startsWith(it.label) } } ?: food.portions.firstOrNull()
    var portion by remember(food.id) { mutableStateOf<Portion?>(initial) }
    var count by remember(food.id) { mutableStateOf(if (preset?.amount != null && initial != null) ((preset.amount / initial.grams * 2).roundToInt() / 2.0).takeIf { it > 0 } ?: 1.0 else 1.0) }
    var grams by remember(food.id) { mutableStateOf(f1(preset?.amount ?: initial?.grams ?: 100.0)) }
    val g = num(grams) ?: 0.0
    val n = food.per100
    val fac = g / 100
    val label = portion?.takeIf { kotlin.math.abs(it.grams * count - g) < 0.05 }?.let { if (count == 1.0) withAmount(it.label, g) else "${f1(count)} × " + withAmount(it.label, g) } ?: "${f1(g)} $unit"
    val picture by produceState<android.graphics.Bitmap?>(null, food.id) { value = repo.image("/api/coach/foods/" + android.net.Uri.encode(food.id) + "/image") }
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        picture?.let {
            androidx.compose.foundation.Image(it.asImageBitmap(), contentDescription = food.name, contentScale = androidx.compose.ui.layout.ContentScale.Fit,
                modifier = Modifier.size(160.dp).background(Color.White, androidx.compose.foundation.shape.RoundedCornerShape(8.dp)))
        }
        Text(food.name, style = MaterialTheme.typography.titleMedium)
        Text(listOfNotNull(food.brand, food.quantity, sourceName(food.source)).joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            for (p in food.portions) FilterChip(selected = portion?.label == p.label, onClick = { portion = p; count = 1.0; grams = f1(p.grams) },
                label = { Text(withAmount(p.label, p.grams)) })
        }
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { val c = (count - 0.5).coerceAtLeast(0.5); count = c; portion?.let { grams = f1(it.grams * c) } }, enabled = portion != null) { Text("−") }
            Text(f1(count) + " ×")
            OutlinedButton(onClick = { val c = count + 0.5; count = c; portion?.let { grams = f1(it.grams * c) } }, enabled = portion != null) { Text("+") }
            NumberField(grams, { grams = it }, label = { Text(unit) }, modifier = Modifier.width(110.dp))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Text(f0(n.kcal * fac) + " kcal", fontWeight = FontWeight.SemiBold)
            Text("E " + f1(n.protein * fac) + " g", color = ProteinColor)
            Text("K " + f1(n.carbs * fac) + " g", color = CarbsColor)
            Text("F " + f1(n.fat * fac) + " g", color = FatColor)
        }
        Button(onClick = { onAdd(g, label) }, enabled = g > 0, modifier = Modifier.fillMaxWidth()) { Text(stringResource(R.string.coach_add)) }
    }
}

@Composable
private fun QuickForm(onAdd: (String, Nutrients) -> Unit) {
    var name by remember { mutableStateOf("") }
    var kcal by remember { mutableStateOf("") }
    var p by remember { mutableStateOf("") }
    var c by remember { mutableStateOf("") }
    var f by remember { mutableStateOf("") }
    Column(verticalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.padding(top = 6.dp)) {
        OutlinedTextField(name, { name = it }, singleLine = true, label = { Text(stringResource(R.string.coach_description)) }, modifier = Modifier.fillMaxWidth())
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            NumField("kcal", kcal, Modifier.weight(1f)) { kcal = it }
            NumField(stringResource(R.string.coach_protein_g), p, Modifier.weight(1f)) { p = it }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            NumField(stringResource(R.string.coach_carbs_g), c, Modifier.weight(1f)) { c = it }
            NumField(stringResource(R.string.coach_fat_g), f, Modifier.weight(1f)) { f = it }
        }
        Button(onClick = { onAdd(name, Nutrients(num(kcal)!!, num(p) ?: 0.0, num(f) ?: 0.0, num(c) ?: 0.0)) }, enabled = (num(kcal) ?: -1.0) >= 0) { Text(stringResource(R.string.coach_enter)) }
    }
}

@Composable
private fun NumField(label: String, value: String, modifier: Modifier, onChange: (String) -> Unit) =
    NumberField(value, onChange, label = { Text(label, maxLines = 1) }, modifier = modifier)

/** Neues Lebensmittel für den ganzen Haushalt (z. B. unbekannter Barcode). */
@Composable
private fun OwnFoodForm(barcode: String?, onSave: (OwnFood) -> Unit) {
    var name by remember { mutableStateOf("") }
    var brand by remember { mutableStateOf("") }
    var code by remember(barcode) { mutableStateOf(barcode ?: "") }
    var kcal by remember { mutableStateOf("") }
    var p by remember { mutableStateOf("") }
    var c by remember { mutableStateOf("") }
    var f by remember { mutableStateOf("") }
    var portion by remember { mutableStateOf("") }
    var grams by remember { mutableStateOf("") }
    var liquid by remember { mutableStateOf(false) }
    Column(Modifier.verticalScroll(rememberScrollState()).padding(top = 6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        if (barcode != null) Text(stringResource(R.string.coach_unknown_product), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
        OutlinedTextField(name, { name = it }, singleLine = true, label = { Text(stringResource(R.string.coach_name)) }, modifier = Modifier.fillMaxWidth())
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            OutlinedTextField(brand, { brand = it }, singleLine = true, label = { Text(stringResource(R.string.coach_brand)) }, modifier = Modifier.weight(1f))
            OutlinedTextField(code, { code = it.filter(Char::isDigit) }, singleLine = true, label = { Text(stringResource(R.string.coach_barcode)) },
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.weight(1f))
        }
        Text(stringResource(if (liquid) R.string.coach_per_100ml else R.string.coach_per_100g), style = MaterialTheme.typography.labelMedium)
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            NumField("kcal", kcal, Modifier.weight(1f)) { kcal = it }
            NumField(stringResource(R.string.coach_protein_g), p, Modifier.weight(1f)) { p = it }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            NumField(stringResource(R.string.coach_carbs_g), c, Modifier.weight(1f)) { c = it }
            NumField(stringResource(R.string.coach_fat_g), f, Modifier.weight(1f)) { f = it }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            OutlinedTextField(portion, { portion = it }, singleLine = true, label = { Text(stringResource(R.string.coach_portion_optional)) }, modifier = Modifier.weight(1f))
            NumField(if (liquid) "ml" else "g", grams, Modifier.weight(1f)) { grams = it }
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.coach_drink), Modifier.weight(1f)); Switch(liquid, { liquid = it })
        }
        Button(enabled = name.isNotBlank() && (num(kcal) ?: -1.0) >= 0, onClick = {
            val portions = if (portion.isNotBlank() && (num(grams) ?: 0.0) > 0) listOf(Portion(portion.trim(), num(grams)!!)) else emptyList()
            onSave(OwnFood(name.trim(), brand.trim().ifEmpty { null }, code.ifEmpty { null }, Nutrients(num(kcal)!!, num(p) ?: 0.0, num(f) ?: 0.0, num(c) ?: 0.0), portions, liquid))
        }) { Text(stringResource(R.string.save)) }
    }
}

// ---------------- Statistik ----------------

@Composable
private fun CoachStatsView() {
    val repo = App.get(LocalContext.current).coach
    var days by rememberSaveable { mutableIntStateOf(30) }
    var stats by remember { mutableStateOf<Stats?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(days) { stats = null; try { stats = repo.stats(days) } catch (e: Exception) { error = e.message ?: e.toString() } }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(12.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf(7 to R.string.coach_week, 30 to R.string.coach_month, 90 to R.string.coach_3months, 365 to R.string.coach_year).forEach { (d, l) ->
                FilterChip(selected = days == d, onClick = { days = d }, label = { Text(stringResource(l)) })
            }
        }
        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        val s = stats ?: run { CircularProgressIndicator(); return@Column }
        val list = s.days
        val logged = list.filter { it.entries > 0 }
        val weights = list.filter { it.weight != null }
        val steps = list.filter { it.steps != null }
        val water = list.filter { it.water > 0 }
        val sleep = list.filter { it.sleepMin != null }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Kpi(if (weights.size >= 2) (if (weights.last().weight!! >= weights.first().weight!!) "+" else "") + f1(weights.last().weight!! - weights.first().weight!!) + " kg" else "–", stringResource(R.string.coach_kpi_weight), Modifier.weight(1f))
            Kpi(if (logged.isEmpty()) "–" else f0(logged.map { it.kcal }.average()), stringResource(R.string.coach_kpi_kcal), Modifier.weight(1f))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Kpi(if (steps.isEmpty()) "–" else f0(steps.map { it.steps!!.toDouble() }.average()), stringResource(R.string.coach_kpi_steps), Modifier.weight(1f))
            Kpi(if (water.isEmpty()) "–" else f1(water.map { it.water.toDouble() }.average() / 1000) + " l", stringResource(R.string.coach_kpi_water), Modifier.weight(1f))
        }
        ChartCard(stringResource(R.string.coach_chart_weight)) { if (weights.isEmpty()) null else WeightChart(list.map { it.weight }) }
        ChartCard(stringResource(R.string.coach_chart_kcal)) { if (logged.isEmpty()) null else BarChart(list.map { if (it.entries > 0) it.kcal else null }, list.map { s.targets.kcal }, MaterialTheme.colorScheme.primary) }
        ChartCard(stringResource(R.string.coach_chart_steps)) { if (steps.isEmpty()) null else BarChart(list.map { it.steps?.toDouble() }, list.map { it.stepGoal?.toDouble() }, StepsColor) }
        ChartCard(stringResource(R.string.coach_water)) { if (water.isEmpty()) null else BarChart(list.map { it.water.toDouble().takeIf { w -> w > 0 } }, list.map { s.targets.waterMl.toDouble() }, WaterColor) }
        ChartCard(stringResource(R.string.coach_sleep)) { if (sleep.isEmpty()) null else BarChart(list.map { it.sleepMin?.let { m -> m / 60.0 } }, null, SleepColor) }
    }
}

@Composable
private fun Kpi(value: String, label: String, modifier: Modifier) {
    Card(modifier) { Column(Modifier.padding(10.dp)) { Text(value, style = MaterialTheme.typography.titleLarge); Text(label, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) } }
}

@Composable
private fun ChartCard(title: String, chart: @Composable () -> Unit?) {
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp)) {
            Text(title, style = MaterialTheme.typography.titleSmall)
            Spacer(Modifier.height(6.dp))
            if (chart() == null) Text(stringResource(R.string.coach_no_data), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

/** Balken je Tag, Ziel als gestrichelter Strich. */
@Composable
private fun BarChart(values: List<Double?>, goals: List<Double?>?, color: Color) {
    val grid = MaterialTheme.colorScheme.outlineVariant
    val goalColor = MaterialTheme.colorScheme.onSurface
    val max = (values.filterNotNull() + (goals?.filterNotNull() ?: emptyList())).maxOrNull()?.times(1.1) ?: 1.0
    Canvas(Modifier.fillMaxWidth().height(130.dp)) {
        val bw = size.width / values.size.coerceAtLeast(1)
        for (i in 1..3) { val y = size.height * i / 4; drawLine(grid, Offset(0f, y), Offset(size.width, y)) }
        values.forEachIndexed { i, v ->
            if (v == null) return@forEachIndexed
            val h = (v / max * size.height).toFloat()
            val under = goals?.getOrNull(i)?.let { v < it } == true
            drawRect(color.copy(alpha = if (under) 0.55f else 0.95f), Offset(i * bw + bw * 0.15f, size.height - h), Size(bw * 0.7f, h))
        }
        goals?.forEachIndexed { i, g ->
            if (g == null) return@forEachIndexed
            val y = size.height - (g / max * size.height).toFloat()
            drawLine(goalColor, Offset(i * bw, y), Offset((i + 1) * bw, y), strokeWidth = 2f, pathEffect = PathEffect.dashPathEffect(floatArrayOf(6f, 4f)))
        }
    }
}

/** Gewicht: Messpunkte und geglätteter Trend. */
@Composable
private fun WeightChart(values: List<Double?>) {
    val dot = MaterialTheme.colorScheme.onSurfaceVariant
    val line = MaterialTheme.colorScheme.primary
    val known = values.filterNotNull()
    val lo = known.min() - 0.5; val hi = known.max() + 0.5
    var t: Double? = null
    val trend = values.map { v -> if (v == null) t else { t = t?.let { it + 0.25 * (v - it) } ?: v; t } }
    Canvas(Modifier.fillMaxWidth().height(130.dp)) {
        fun x(i: Int) = size.width * i / (values.size - 1).coerceAtLeast(1)
        fun y(v: Double) = (size.height - (v - lo) / (hi - lo) * size.height).toFloat()
        values.forEachIndexed { i, v -> if (v != null) drawCircle(dot, 4f, Offset(x(i), y(v))) }
        var prev: Offset? = null
        trend.forEachIndexed { i, v -> if (v != null) { val p = Offset(x(i), y(v)); prev?.let { drawLine(line, it, p, strokeWidth = 5f) }; prev = p } }
    }
}

// ---------------- Profil ----------------

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun CoachProfileView() {
    val ctx = LocalContext.current
    val repo = App.get(ctx).coach
    val scope = rememberCoroutineScope()
    var data by remember { mutableStateOf<ProfileResult?>(null) }
    var p by remember { mutableStateOf<Profile?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var saved by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) { try { val r = repo.profile(); data = r; p = r.profile } catch (e: Exception) { error = e.message ?: e.toString() } }
    val pp = p ?: run { Box(Modifier.fillMaxSize()) { CircularProgressIndicator(Modifier.align(Alignment.Center)) }; return }
    fun up(np: Profile) { p = np; saved = false }
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        Text(stringResource(R.string.coach_about_you), style = MaterialTheme.typography.titleMedium)
        var birth by remember { mutableStateOf(pp.birth?.let { LocalDate.parse(it).format(DateTimeFormatter.ofPattern("dd.MM.yyyy")) } ?: "") }
        OutlinedTextField(birth, { v -> birth = v; up(pp.copy(birth = parseBirth(v))) }, singleLine = true, label = { Text(stringResource(R.string.coach_birth)) },
            placeholder = { Text("TT.MM.JJJJ") }, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.fillMaxWidth(),
            isError = birth.isNotBlank() && parseBirth(birth) == null)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            NumField(stringResource(R.string.coach_height_cm), pp.heightCm?.let { f1(it) } ?: "", Modifier.weight(1f)) { up(pp.copy(heightCm = num(it))) }
            NumField(stringResource(R.string.coach_weight_kg), pp.weightKg?.let { f1(it) } ?: "", Modifier.weight(1f)) { up(pp.copy(weightKg = num(it))) }
        }
        Text(stringResource(R.string.coach_sex), style = MaterialTheme.typography.labelLarge)
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("f" to R.string.coach_female, "m" to R.string.coach_male, "d" to R.string.coach_diverse).forEach { (v, l) ->
                FilterChip(selected = pp.sex == v, onClick = { up(pp.copy(sex = v)) }, label = { Text(stringResource(l)) })
            }
        }
        Text(stringResource(R.string.coach_daily_activity), style = MaterialTheme.typography.labelLarge)
        listOf(1.2 to R.string.coach_pal_12, 1.4 to R.string.coach_pal_14, 1.6 to R.string.coach_pal_16, 1.8 to R.string.coach_pal_18, 2.0 to R.string.coach_pal_20).forEach { (v, l) ->
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().clickable { up(pp.copy(pal = v)) }) {
                RadioButton(selected = pp.pal == v, onClick = { up(pp.copy(pal = v)) }); Text(stringResource(l))
            }
        }
        if (pp.sex != "m") Row(verticalAlignment = Alignment.CenterVertically) { Text(stringResource(R.string.coach_pregnant), Modifier.weight(1f)); Switch(pp.pregnant, { up(pp.copy(pregnant = it)) }) }

        Text(stringResource(R.string.coach_goal), style = MaterialTheme.typography.titleMedium)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("keep" to R.string.coach_goal_keep, "lose" to R.string.coach_goal_lose, "gain" to R.string.coach_goal_gain, "muscle" to R.string.coach_goal_muscle).forEach { (v, l) ->
                FilterChip(selected = pp.goal == v, onClick = { up(pp.copy(goal = v)) }, label = { Text(stringResource(l)) })
            }
        }
        if (pp.goal == "lose" || pp.goal == "gain") FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf(0.25, 0.5, 0.75).forEach { v -> FilterChip(selected = pp.pace == v, onClick = { up(pp.copy(pace = v)) }, label = { Text(stringResource(R.string.coach_pace, f1(v))) }) }
        }

        Text(stringResource(R.string.coach_diet), style = MaterialTheme.typography.titleMedium)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("dge" to R.string.coach_diet_dge, "med" to R.string.coach_diet_med, "vegetarian" to R.string.coach_diet_vegetarian, "vegan" to R.string.coach_diet_vegan,
                "protein" to R.string.coach_diet_protein, "lowcarb" to R.string.coach_diet_lowcarb, "keto" to R.string.coach_diet_keto).forEach { (v, l) ->
                FilterChip(selected = pp.diet == v, onClick = { up(pp.copy(diet = v)) }, label = { Text(stringResource(l)) })
            }
        }
        Text(stringResource(R.string.coach_allergies), style = MaterialTheme.typography.labelLarge)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            ALLERGENS.forEach { (k, l) ->
                FilterChip(selected = k in pp.allergens, onClick = { up(pp.copy(allergens = if (k in pp.allergens) pp.allergens - k else pp.allergens + k)) }, label = { Text(stringResource(l)) })
            }
        }
        Text(stringResource(R.string.coach_intolerances), style = MaterialTheme.typography.labelLarge)
        FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("lactose" to R.string.coach_int_lactose, "fructose" to R.string.coach_int_fructose, "histamine" to R.string.coach_int_histamine, "gluten" to R.string.coach_int_gluten).forEach { (k, l) ->
                FilterChip(selected = k in pp.intolerances, onClick = { up(pp.copy(intolerances = if (k in pp.intolerances) pp.intolerances - k else pp.intolerances + k)) }, label = { Text(stringResource(l)) })
            }
        }
        var dislikes by remember { mutableStateOf(pp.dislikes.joinToString(", ")) }
        OutlinedTextField(dislikes, { v -> dislikes = v; up(pp.copy(dislikes = v.split(',').map { it.trim() }.filter { it.isNotEmpty() })) },
            label = { Text(stringResource(R.string.coach_dislikes)) }, modifier = Modifier.fillMaxWidth())

        Text(stringResource(R.string.coach_steps_goal), style = MaterialTheme.typography.titleMedium)
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().clickable { up(pp.copy(stepMode = "fixed")) }) {
            RadioButton(selected = pp.stepMode == "fixed", onClick = { up(pp.copy(stepMode = "fixed")) }); Text(stringResource(R.string.coach_steps_fixed))
        }
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.fillMaxWidth().clickable { up(pp.copy(stepMode = "dynamic")) }) {
            RadioButton(selected = pp.stepMode == "dynamic", onClick = { up(pp.copy(stepMode = "dynamic")) }); Text(stringResource(R.string.coach_steps_dynamic))
        }
        if (pp.stepMode == "fixed") NumField(stringResource(R.string.coach_steps_per_day), pp.stepGoal.toString(), Modifier.width(180.dp)) { up(pp.copy(stepGoal = it.filter(Char::isDigit).toIntOrNull() ?: 0)) }
        NumField(stringResource(R.string.coach_water_goal_ml), pp.waterMl?.toString() ?: "", Modifier.width(180.dp)) { up(pp.copy(waterMl = it.filter(Char::isDigit).toIntOrNull())) }
        WaterSizesEditor(pp.waterSizes) { up(pp.copy(waterSizes = it)) }
        Row(verticalAlignment = Alignment.CenterVertically) { Text(stringResource(R.string.coach_hide_numbers), Modifier.weight(1f)); Switch(pp.hideNumbers, { up(pp.copy(hideNumbers = it)) }) }
        CoachPukkiSettings(pp) { up(it) }

        Button(onClick = { scope.launch { try { val r = repo.saveProfile(pp); data = r; p = r.profile; saved = true; error = null } catch (e: Exception) { error = e.message ?: e.toString() } } },
            modifier = Modifier.fillMaxWidth()) { Text(stringResource(if (saved) R.string.coach_saved else R.string.save)) }

        data?.targets?.let { t ->
            Card(Modifier.fillMaxWidth()) {
                Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    Text(stringResource(R.string.coach_your_goals), style = MaterialTheme.typography.titleMedium)
                    if (t.complete) {
                        TargetLine(stringResource(R.string.coach_bmr), f0(t.bmr) + " kcal")
                        TargetLine(stringResource(R.string.coach_tdee), f0(t.tdee) + " kcal")
                        TargetLine(stringResource(R.string.coach_goal), f0(t.kcal) + " kcal")
                        TargetLine(stringResource(R.string.coach_protein), f0(t.protein) + " g")
                        TargetLine(stringResource(R.string.coach_carbs), f0(t.carbs) + " g")
                        TargetLine(stringResource(R.string.coach_fat), f0(t.fat) + " g")
                    }
                    TargetLine(stringResource(R.string.coach_water), f1(t.waterMl / 1000.0) + " l")
                    t.bmi?.let { TargetLine("BMI", f1(it)) }
                    for (n in t.notes) Text(n, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary)
                    Text(stringResource(R.string.coach_disclaimer), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text(stringResource(R.string.coach_sources) + ": " + (data?.sources ?: emptyList()).joinToString(" · "), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
        }
        HealthConnectCard()
        Spacer(Modifier.height(80.dp))
    }
}

private val ALLERGENS = listOf("gluten" to R.string.coach_al_gluten, "crustaceans" to R.string.coach_al_crustaceans, "eggs" to R.string.coach_al_eggs,
    "fish" to R.string.coach_al_fish, "peanuts" to R.string.coach_al_peanuts, "soy" to R.string.coach_al_soy, "milk" to R.string.coach_al_milk,
    "nuts" to R.string.coach_al_nuts, "celery" to R.string.coach_al_celery, "mustard" to R.string.coach_al_mustard, "sesame" to R.string.coach_al_sesame,
    "sulphites" to R.string.coach_al_sulphites, "lupin" to R.string.coach_al_lupin, "molluscs" to R.string.coach_al_molluscs)

private fun parseBirth(s: String): String? = try { LocalDate.parse(s.trim(), DateTimeFormatter.ofPattern("d.M.yyyy")).toString() } catch (_: Exception) { null }

@Composable
private fun TargetLine(label: String, value: String) = Row(Modifier.fillMaxWidth()) { Text(label, Modifier.weight(1f)); Text(value) }

/** Verbindung zu Health Connect (Uhr, Waage): verbinden, Stand, trennen. */
@Composable
private fun HealthConnectCard() {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var enabled by remember { mutableStateOf(HealthSync.enabled(ctx)) }
    var busy by remember { mutableStateOf(false) }
    var info by remember { mutableStateOf<String?>(null) }
    val textDays = stringResource(R.string.coach_hc_days)
    val connect = rememberLauncherForActivityResult(PermissionController.createRequestPermissionResultContract()) { granted ->
        if (granted.intersect(HealthSync.readPermissions).isNotEmpty()) scope.launch {
            busy = true
            info = try { textDays.format(HealthSync.enable(ctx)) } catch (e: Exception) { e.message }
            enabled = true; busy = false
        }
    }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(stringResource(R.string.coach_hc_title), style = MaterialTheme.typography.titleMedium)
            Text(stringResource(R.string.coach_hc_text), style = MaterialTheme.typography.bodySmall)
            when {
                !HealthSync.available(ctx) -> Text(stringResource(if (HealthSync.needsInstall(ctx)) R.string.coach_hc_install else R.string.coach_hc_unavailable), style = MaterialTheme.typography.bodySmall)
                enabled -> {
                    val last = HealthSync.lastSync(ctx)
                    if (last > 0) Text(stringResource(R.string.coach_hc_last, java.text.DateFormat.getDateTimeInstance(java.text.DateFormat.SHORT, java.text.DateFormat.SHORT).format(java.util.Date(last))), style = MaterialTheme.typography.bodySmall)
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedButton(onClick = { scope.launch { busy = true; info = try { textDays.format(HealthSync.sync(ctx, 30)) } catch (e: Exception) { e.message }; busy = false } }) { Text(stringResource(R.string.coach_hc_sync_now)) }
                        TextButton(onClick = { HealthSync.disable(ctx); enabled = false }) { Text(stringResource(R.string.coach_hc_disconnect)) }
                    }
                }
                else -> Button(onClick = { connect.launch(HealthSync.readPermissions + HealthSync.extraPermissions) }) { Text(stringResource(R.string.coach_hc_connect)) }
            }
            if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
            info?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
        }
    }
}

/** Ein Treffer der Lebensmittelsuche, mit Produktbild, wenn es eins gibt. */
@Composable
private fun HitRow(h: Hit, repo: at.dimes.dmoffice.coach.CoachRepo, accent: Color = MaterialTheme.colorScheme.primary, onQuick: (() -> Unit)? = null, onClick: () -> Unit) {
    val bmp by produceState<android.graphics.Bitmap?>(null, h.image) { value = h.image?.let { repo.image(it) } }
    // Eingetragen: ein Lichtstreif wischt vom Plus nach links über die Zeile, das Häkchen springt auf, die kcal steigen auf (wie im Web)
    val scope = rememberCoroutineScope()
    val haptic = androidx.compose.ui.platform.LocalHapticFeedback.current
    var added by remember(h.id) { mutableStateOf(false) }
    var reset by remember { mutableStateOf<kotlinx.coroutines.Job?>(null) }
    val sweep = remember { androidx.compose.animation.core.Animatable(1f) }
    val pop = remember { androidx.compose.animation.core.Animatable(1f) }
    val rise = remember { androidx.compose.animation.core.Animatable(1f) }
    val green = Color(0xFF2E9E5B)
    val brand = MaterialTheme.colorScheme.primary
    val quick: (() -> Unit)? = onQuick?.let { q -> {
        q(); added = true
        haptic.performHapticFeedback(androidx.compose.ui.hapticfeedback.HapticFeedbackType.LongPress)
        scope.launch { sweep.snapTo(1f); sweep.animateTo(-1f, androidx.compose.animation.core.tween(750, easing = androidx.compose.animation.core.FastOutSlowInEasing)) }
        scope.launch { pop.snapTo(0.3f); pop.animateTo(1f, androidx.compose.animation.core.spring(dampingRatio = 0.35f, stiffness = 400f)) }
        scope.launch { rise.snapTo(0f); rise.animateTo(1f, androidx.compose.animation.core.tween(1100)) }
        // Nach der Animation wieder „+“: derselbe Posten lässt sich noch einmal eintragen
        reset?.cancel(); reset = scope.launch { kotlinx.coroutines.delay(1200); added = false }
    } }
    Box {
    Row(Modifier.fillMaxWidth().drawBehind {
        val x = sweep.value
        if (x > -1f && x < 1f) drawRect(
            androidx.compose.ui.graphics.Brush.horizontalGradient(
                0f to Color.Transparent, 0.4f to green.copy(alpha = 0.35f), 0.6f to brand.copy(alpha = 0.3f), 1f to Color.Transparent,
                startX = x * size.width, endX = x * size.width + size.width),
            alpha = if (x < -0.4f) (x + 1f) / 0.6f else 1f)
    }.clickable { onClick() }.padding(vertical = 11.dp), verticalAlignment = Alignment.CenterVertically) {
        bmp?.let {
            androidx.compose.foundation.Image(it.asImageBitmap(), contentDescription = null,
                modifier = Modifier.size(40.dp).background(Color.White, RoundedCornerShape(4.dp)).padding(2.dp))
            Spacer(Modifier.width(10.dp))
        }
        Column(Modifier.weight(1f)) {
            Text(h.name, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            val sub = listOfNotNull(h.brand, h.portion ?: stringResource(R.string.coach_per100)).joinToString(" · ")
            Text(sub, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        Text(f0(if (h.amount != null) h.kcalPer100 * h.amount / 100 else h.kcalPer100), style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
        Text(" kcal", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        if (quick != null) Box(Modifier.padding(start = 10.dp).size(34.dp).clip(CircleShape)
            .then(if (added) Modifier.background(accent) else Modifier.border(2.dp, accent, CircleShape)).clickable(onClick = quick), contentAlignment = Alignment.Center) {
            if (added) Icon(Icons.Default.Check, contentDescription = stringResource(R.string.coach_quick_add), tint = Color.White,
                modifier = Modifier.size(20.dp).graphicsLayer { scaleX = pop.value; scaleY = pop.value; rotationZ = (1f - pop.value) * -40f })
            else Icon(Icons.Default.Add, contentDescription = stringResource(R.string.coach_quick_add), tint = accent, modifier = Modifier.size(20.dp))
        }
    }
    // „+124 kcal“ steigt über dem Plus auf, wenn die Menge bekannt ist
    if (h.mine && h.amount != null && rise.value < 1f) Text("+" + f0(h.kcalPer100 * h.amount / 100) + " kcal",
        style = MaterialTheme.typography.labelMedium, fontWeight = FontWeight.SemiBold, color = green,
        modifier = Modifier.align(Alignment.CenterEnd).padding(end = 48.dp)
            .graphicsLayer { translationY = -rise.value * 28.dp.toPx(); alpha = if (rise.value < 0.25f) rise.value * 4 else 1f - (rise.value - 0.25f) / 0.75f })
    }
    HorizontalDivider()
}
