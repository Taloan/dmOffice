package at.dimes.dmoffice.ui

import android.Manifest
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material3.IconButton
import androidx.compose.material3.Icon
import androidx.compose.material.icons.Icons
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.AssistChip
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.InputChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.material3.FilterChip
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.coach.CoachLocation
import at.dimes.dmoffice.coach.DiaryAdd
import at.dimes.dmoffice.coach.FastingView
import at.dimes.dmoffice.coach.Hit
import at.dimes.dmoffice.coach.Planned
import at.dimes.dmoffice.coach.Profile
import at.dimes.dmoffice.coach.Proposal
import at.dimes.dmoffice.coach.RecipeEntry
import at.dimes.dmoffice.coach.RecipeNutrition
import at.dimes.dmoffice.coach.Template
import at.dimes.dmoffice.coach.mealNow
import kotlinx.coroutines.launch
import java.text.NumberFormat
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlin.math.roundToInt

/** Coach Stufe 2 und 3 in der App: Beschreiben, Vorlagen, Essensplan, Rezepte, Fasten, Trinkgefäße, Pukki als Coach. */

private fun n0(v: Double?) = if (v == null) "–" else NumberFormat.getNumberInstance().apply { maximumFractionDigits = 0 }.format(v)
private fun n1(v: Double?) = if (v == null) "–" else NumberFormat.getNumberInstance().apply { maximumFractionDigits = 1 }.format(v)
private fun liters(ml: Int) = NumberFormat.getNumberInstance().apply { maximumFractionDigits = 2 }.format(ml / 1000.0) + " l"
private val WaterBlue = Color(0xFF4F8FE0)
private val WaterOver = Color(0xFF3FA34D)

// ---------------- Wasser mit eigenen Gefäßen ----------------

/** Wasser als 1-l-Flaschen mit Füllstand (über dem Ziel grün), je eigenem Gefäß ein „+“-Knopf. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun CoachWaterVessels(ml: Int, goal: Int, sizes: List<Int>, onWater: (Int) -> Unit) {
    val list = sizes.ifEmpty { listOf(250, 500) }
    WaterBottles(ml, goal)
    // Umbrechen statt quetschen: bei vier Gefäßen passt eine Zeile am Handy nicht
    FlowRow(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        for (v in list) OutlinedButton(onClick = { onWater(v) }) { Text("+ " + liters(v), maxLines = 1) }
    }
}

/** 1-l-Flaschen, die sich füllen - bis zum Ziel blau, darüber grün. */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun WaterBottles(ml: Int, goal: Int) {
    val count = maxOf(1, (goal + 999) / 1000, (ml + 999) / 1000).coerceAtMost(12)
    fun part(v: Int) = (v / 1000f).coerceIn(0f, 1f)
    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        for (i in 0 until count) {
            val f = part(ml - i * 1000)
            val b = minOf(f, part(goal - i * 1000))
            val edge = if (i * 1000 < goal) WaterBlue else WaterOver
            val shape = RoundedCornerShape(7.dp, 7.dp, 4.dp, 4.dp)
            val level = Brush.verticalGradient(
                0f to Color.Transparent, (1f - f) to Color.Transparent, (1f - f) to WaterOver.copy(alpha = 0.85f),
                (1f - b) to WaterOver.copy(alpha = 0.85f), (1f - b) to WaterBlue.copy(alpha = 0.85f), 1f to WaterBlue.copy(alpha = 0.85f))
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Box(Modifier.size(9.dp, 6.dp).border(1.5.dp, edge, RoundedCornerShape(2.dp, 2.dp, 0.dp, 0.dp)))
                Box(Modifier.size(24.dp, 36.dp).border(1.5.dp, edge, shape).background(level, shape))
            }
        }
    }
}

/** Profil: eigene Gläser und Flaschen (höchstens vier, das erste bestimmt die Symbole). */
@OptIn(ExperimentalLayoutApi::class)
@Composable
fun WaterSizesEditor(sizes: List<Int>, onChange: (List<Int>) -> Unit) {
    val list = sizes.ifEmpty { listOf(250, 500) }
    Text(stringResource(R.string.coach_water_sizes), style = MaterialTheme.typography.labelLarge)
    Text(stringResource(R.string.coach_water_sizes_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        list.forEachIndexed { i, v ->
            InputChip(selected = i == 0, onClick = { if (list.size > 1) onChange(list - v) }, label = { Text(liters(v) + if (list.size > 1) "  ✕" else "") })
        }
    }
    if (list.size < 4) FlowRow(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        for (v in listOf(200, 250, 300, 330, 500, 600, 750, 1000, 1500).filter { it !in list })
            AssistChip(onClick = { onChange(list + v) }, label = { Text("+ " + liters(v)) })
    }
}

// ---------------- Beschreiben ----------------

private class Row1(val p: Proposal, on: Boolean, chosen: Hit?, amount: String) {
    var on by mutableStateOf(on)
    var chosen by mutableStateOf(chosen)
    var amount by mutableStateOf(amount)
    var changed by mutableStateOf(false)
}

/** „2 Brötchen mit Käse und ein Kaffee“ erkennen, je Posten Lebensmittel und Menge ändern, alles eintragen (andere Wahl wird gemerkt). */
private fun rowsOf(list: List<Proposal>) = list.map { p ->
    val hit = p.food?.let { Hit(it.id, it.name, it.brand, it.source, it.per100.kcal) }
    Row1(p, hit != null && p.grams != null, hit, p.grams?.let { n0(it) } ?: "")
}

/** Auch gesprochen (Spracherkennung des Handys, ohne KI); [initialText]/[initial]: schon erkannt (Foto vom Teller). */
@Composable
fun DescribeForm(date: String, meal: String, onDone: () -> Unit, onError: (String) -> Unit, initialText: String = "", initial: List<Proposal>? = null,
                 outer: kotlinx.coroutines.CoroutineScope? = null) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).coach
    // Läuft im Bereich des ganzen Eintragens weiter - Spracheingabe und Wechsel zwischen den Kacheln brechen sie nicht ab
    val own = rememberCoroutineScope()
    val scope = outer ?: own
    var text by remember { mutableStateOf(initialText) }
    var rows by remember { mutableStateOf(initial?.let { rowsOf(it) }) }
    var busy by remember { mutableStateOf(false) }
    fun recognize() = scope.launch {
        busy = true
        try { rows = rowsOf(repo.parse(text.trim(), meal)) } catch (e: kotlinx.coroutines.CancellationException) { throw e } catch (e: Exception) { onError(e.message ?: e.toString()) }
        busy = false
    }
    // Gesprochen: das Gehörte anhängen und gleich erkennen
    val speech = rememberLauncherForActivityResult(ActivityResultContracts.StartActivityForResult()) { res ->
        val said = res.data?.getStringArrayListExtra(android.speech.RecognizerIntent.EXTRA_RESULTS)?.firstOrNull()
        if (!said.isNullOrBlank()) { text = (text.trim() + (if (text.isBlank()) "" else ", ") + said).trim(); recognize() }
    }
    Column(Modifier.fillMaxWidth().padding(top = 8.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        OutlinedTextField(text, { text = it }, placeholder = { Text(stringResource(R.string.coach_describe_hint)) }, modifier = Modifier.fillMaxWidth(), minLines = 2,
            trailingIcon = {
                IconButton(onClick = {
                    try {
                        speech.launch(android.content.Intent(android.speech.RecognizerIntent.ACTION_RECOGNIZE_SPEECH)
                            .putExtra(android.speech.RecognizerIntent.EXTRA_LANGUAGE_MODEL, android.speech.RecognizerIntent.LANGUAGE_MODEL_FREE_FORM)
                            .putExtra(android.speech.RecognizerIntent.EXTRA_LANGUAGE, java.util.Locale.getDefault().toLanguageTag())
                            .putExtra(android.speech.RecognizerIntent.EXTRA_PROMPT, ctx.getString(R.string.coach_speak_prompt)))
                    } catch (_: android.content.ActivityNotFoundException) { onError(ctx.getString(R.string.coach_speak_missing)) }
                }) { Icon(Icons.Default.Mic, contentDescription = stringResource(R.string.coach_speak)) }
            })
        Button(onClick = { recognize() }, enabled = text.isNotBlank() && !busy) { Text(stringResource(R.string.coach_recognize)) }
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())
        val rr = rows
        if (rr != null && rr.isEmpty()) Text(stringResource(R.string.coach_nothing_recognized), style = MaterialTheme.typography.bodySmall)
        if (!rr.isNullOrEmpty()) {
            LazyColumn(Modifier.fillMaxWidth().height(300.dp)) {
                items(rr) { r ->
                    Row(Modifier.fillMaxWidth().padding(vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
                        Checkbox(r.on, { r.on = it })
                        Column(Modifier.weight(1f)) {
                            Text(r.p.name + (r.p.portion?.let { " · $it" } ?: ""), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            val options = listOfNotNull(r.chosen) + r.p.alternatives.filter { it.id != r.chosen?.id }
                            var open by remember { mutableStateOf(false) }
                            Box {
                                Text(r.chosen?.name ?: stringResource(R.string.coach_no_match), maxLines = 2, overflow = TextOverflow.Ellipsis,
                                    color = if (r.chosen == null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface,
                                    modifier = Modifier.clickable(enabled = options.size > 1) { open = true })
                                DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                                    for (o in options) DropdownMenuItem(text = { Text(o.name + (o.brand?.let { " · $it" } ?: "")) },
                                        onClick = { open = false; r.chosen = o; r.changed = true; r.on = true })
                                }
                            }
                        }
                        NumberField(r.amount, { r.amount = it }, suffix = { Text("g") }, modifier = Modifier.width(96.dp))
                    }
                    HorizontalDivider()
                }
            }
            Button(onClick = {
                scope.launch {
                    busy = true
                    try {
                        for (r in rr.filter { it.on && it.chosen != null }) {
                            val g = r.amount.replace(',', '.').toDoubleOrNull() ?: continue
                            if (g <= 0) continue
                            repo.add(DiaryAdd(date, meal, food = r.chosen!!.id, amount = g, portion = r.p.portion, remember = if (r.changed) r.p.name else null))
                        }
                        onDone()
                    } catch (e: kotlinx.coroutines.CancellationException) { throw e } catch (e: Exception) { onError(e.message ?: e.toString()) }
                    busy = false
                }
            }, enabled = !busy && rr.any { it.on && it.chosen != null }) { Text(stringResource(R.string.coach_add_all)) }
        }
    }
}

// ---------------- Laut Essensplan und Vorlagen (oben in der Suche) ----------------

@Composable
fun PlannedAndTemplates(date: String, meal: String, onPlanned: (Planned) -> Unit, onApplied: () -> Unit, onError: (String) -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).coach
    val scope = rememberCoroutineScope()
    var planned by remember { mutableStateOf<List<Planned>>(emptyList()) }
    var templates by remember { mutableStateOf<List<Template>>(emptyList()) }
    LaunchedEffect(date, meal) { planned = try { repo.planned(date, meal) } catch (_: Exception) { emptyList() } }
    LaunchedEffect(Unit) { templates = try { repo.templates() } catch (_: Exception) { emptyList() } }
    if (planned.isNotEmpty()) {
        Text(stringResource(R.string.coach_from_plan), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 6.dp))
        for (p in planned) Row(Modifier.fillMaxWidth().clickable(enabled = p.food != null) { onPlanned(p) }.padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(p.title)
                Text(if (p.guessed) stringResource(R.string.coach_plan_guessed, p.foodName ?: "", p.portion ?: "") else stringResource(if (p.food != null) R.string.coach_plan_recipe else R.string.coach_plan_no_recipe), style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            p.kcalPerServing?.let { Text(n0(it) + " kcal", style = MaterialTheme.typography.bodySmall) }
        }
    }
    if (templates.isNotEmpty()) {
        Text(stringResource(R.string.coach_templates), style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(top = 6.dp))
        val applied = stringResource(R.string.coach_template_added)
        for (tpl in templates.sortedByDescending { it.meal == meal }) Row(Modifier.fillMaxWidth().clickable {
            scope.launch {
                try { val r = repo.applyTemplate(tpl.id, date, meal); android.widget.Toast.makeText(ctx, applied.format(tpl.name, r.added), android.widget.Toast.LENGTH_SHORT).show(); onApplied() }
                catch (e: Exception) { onError(e.message ?: e.toString()) }
            }
        }.padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(tpl.name)
                Text(tpl.items.joinToString(", ") { it.name }, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            Text(n0(tpl.kcal) + " kcal", style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { scope.launch { try { repo.deleteTemplate(tpl.id); templates = templates - tpl } catch (e: Exception) { onError(e.message ?: e.toString()) } } }) { Text("✕") }
        }
    }
}

/** Name für „Als Vorlage speichern“. */
@Composable
fun TemplateNameDialog(initial: String, onDismiss: () -> Unit, onSave: (String) -> Unit) {
    var name by remember { mutableStateOf(initial) }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.coach_save_template_title)) },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(stringResource(R.string.coach_save_template_text), style = MaterialTheme.typography.bodySmall)
                OutlinedTextField(name, { name = it }, singleLine = true, label = { Text(stringResource(R.string.coach_template_name)) }, modifier = Modifier.fillMaxWidth())
            }
        },
        confirmButton = { TextButton(onClick = { onSave(name.trim()) }, enabled = name.isNotBlank()) { Text(stringResource(R.string.save)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}

// ---------------- Rezepte ----------------

@Composable
fun CoachRecipesView() {
    val ctx = LocalContext.current
    val repo = App.get(ctx).coach
    var list by remember { mutableStateOf<List<RecipeEntry>?>(null) }
    var query by remember { mutableStateOf("") }
    var open by remember { mutableStateOf<String?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var source by remember { mutableIntStateOf(0) }   // 0 alle, 1 eigene, 2 Basisrezepte
    var reload by remember { mutableIntStateOf(0) }
    LaunchedEffect(reload) { list = try { repo.recipes() } catch (e: Exception) { error = e.message; emptyList() } }
    Column(Modifier.fillMaxSize().padding(horizontal = 12.dp)) {
        Text(stringResource(R.string.coach_recipes_intro), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(vertical = 8.dp))
        error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
        OutlinedTextField(query, { query = it }, singleLine = true, placeholder = { Text(stringResource(R.string.coach_recipe_search)) }, modifier = Modifier.fillMaxWidth())
        Row(Modifier.padding(top = 6.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf(R.string.coach_recipes_all, R.string.coach_recipes_own, R.string.coach_recipes_base).forEachIndexed { i, l ->
                FilterChip(selected = source == i, onClick = { source = i }, label = { Text(stringResource(l)) })
            }
        }
        // Suche auch im Ordner: „vegan“, „Frühstück“ finden die Basisrezepte dazu
        val shown = (list ?: emptyList()).filter { (source == 0 || (source == 2) == it.base) &&
            (query.isBlank() || it.title.contains(query.trim(), ignoreCase = true) || it.folder?.contains(query.trim(), ignoreCase = true) == true) }
        when {
            list == null -> CircularProgressIndicator(Modifier.padding(16.dp))
            shown.isEmpty() -> Text(stringResource(R.string.coach_recipes_none), modifier = Modifier.padding(16.dp))
            else -> LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 96.dp)) {
                items(shown, key = { it.syncId }) { r ->
                    Column(Modifier.fillMaxWidth().clickable { open = r.syncId }.padding(vertical = 10.dp)) {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            Text(r.title, Modifier.weight(1f))
                            r.kcal?.let { Text(n0(it) + " kcal", style = MaterialTheme.typography.bodySmall, fontWeight = FontWeight.SemiBold) }
                        }
                        r.folder?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) }
                        if (!r.base && !r.kcalGiven) Text("⚠ " + stringResource(R.string.coach_recipe_no_kcal), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    HorizontalDivider()
                }
            }
        }
    }
    open?.let { id -> RecipeDialog(id, onAdopted = { reload++ }) { open = null } }
}

// Allergene der Basisrezepte kommen als deutsche Bezeichnung (wie im Web)
private val BASE_ALLERGENS = mapOf("Gluten" to R.string.coach_al_gluten, "Krebstiere" to R.string.coach_al_crustaceans, "Eier" to R.string.coach_al_eggs,
    "Fisch" to R.string.coach_al_fish, "Erdnüsse" to R.string.coach_al_peanuts, "Soja" to R.string.coach_al_soy, "Milch" to R.string.coach_al_milk,
    "Schalenfrüchte" to R.string.coach_al_nuts, "Sellerie" to R.string.coach_al_celery, "Senf" to R.string.coach_al_mustard, "Sesam" to R.string.coach_al_sesame,
    "Sulfite" to R.string.coach_al_sulphites, "Lupinen" to R.string.coach_al_lupin, "Weichtiere" to R.string.coach_al_molluscs)

@Composable
private fun allergenLabel(label: String): String = BASE_ALLERGENS[label]?.let { stringResource(it) } ?: label

@Composable
private fun RecipeDialog(syncId: String, onAdopted: () -> Unit, onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    val repo = App.get(ctx).coach
    val scope = rememberCoroutineScope()
    var r by remember { mutableStateOf<RecipeNutrition?>(null) }
    var error by remember { mutableStateOf<String?>(null) }
    var baseServings by remember { mutableIntStateOf(1) }
    LaunchedEffect(syncId) { try { r = repo.recipe(syncId) } catch (e: Exception) { error = e.message } }
    val logged = stringResource(R.string.coach_logged)
    AlertDialog(onDismissRequest = onDismiss, title = { Text(r?.title ?: stringResource(R.string.coach_recipe)) },
        text = {
            val rr = r
            Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                error?.let { Text(it, color = MaterialTheme.colorScheme.error) }
                if (rr == null) { CircularProgressIndicator(); return@Column }
                // Basisrezept: Nährwerte aus dem BLS je Portion, Zutaten für 1 bis 6 Portionen, Zubereitung und Allergene
                rr.base?.let { b ->
                    val muted = MaterialTheme.colorScheme.onSurfaceVariant
                    Text(stringResource(R.string.coach_base_serving, n0(rr.weight), n0(rr.perServing.kcal)), fontWeight = FontWeight.SemiBold)
                    Text("${stringResource(R.string.coach_protein)} ${n1(rr.perServing.protein)} g · ${stringResource(R.string.coach_carbs)} ${n1(rr.perServing.carbs)} g · ${stringResource(R.string.coach_fat)} ${n1(rr.perServing.fat)} g",
                        style = MaterialTheme.typography.bodySmall)
                    Text(if (b.cook > 0) stringResource(R.string.coach_base_times, b.prep, b.cook) else stringResource(R.string.coach_base_prep, b.prep),
                        style = MaterialTheme.typography.bodySmall)
                    HorizontalDivider()
                    Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Text(stringResource(R.string.coach_servings))
                        TextButton(onClick = { baseServings-- }, enabled = baseServings > 1) { Text("−") }
                        Text(baseServings.toString(), fontWeight = FontWeight.SemiBold)
                        TextButton(onClick = { baseServings++ }, enabled = baseServings < 6) { Text("+") }
                    }
                    for (i in b.ingredients) Text("• " + (i.texts.getOrNull(baseServings - 1) ?: i.name), style = MaterialTheme.typography.bodyMedium)
                    Text(stringResource(R.string.coach_base_steps), fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 4.dp))
                    b.steps.forEachIndexed { i, s -> Text("${i + 1}. $s", style = MaterialTheme.typography.bodyMedium) }
                    b.tip?.let { Text(stringResource(R.string.coach_base_tip, it), style = MaterialTheme.typography.bodySmall, color = muted) }
                    Text(stringResource(R.string.coach_base_more, n1(rr.perServing.fiber ?: 0.0), n1(rr.perServing.sugar ?: 0.0), n1(rr.perServing.salt ?: 0.0)),
                        style = MaterialTheme.typography.bodySmall, color = muted)
                    Text(if (b.allergens.isEmpty()) stringResource(R.string.coach_base_allergens_none) else stringResource(R.string.coach_base_allergens, b.allergens.map { allergenLabel(it) }.joinToString(", ")),
                        style = MaterialTheme.typography.bodySmall, color = muted)
                    b.hint?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = muted) }
                    Text(stringResource(R.string.coach_base_estimated), style = MaterialTheme.typography.bodySmall, color = muted)
                    val adopted = stringResource(R.string.coach_base_adopted)
                    OutlinedButton(onClick = {
                        scope.launch { try { repo.adoptRecipe(syncId); android.widget.Toast.makeText(ctx, adopted, android.widget.Toast.LENGTH_SHORT).show(); onAdopted(); onDismiss() } catch (e: Exception) { error = e.message } }
                    }) { Text(stringResource(R.string.coach_base_adopt)) }
                    return@Column
                }
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.coach_servings))
                    TextButton(onClick = { scope.launch { try { r = repo.recipeServings(syncId, rr.servings - 1) } catch (e: Exception) { error = e.message } } }, enabled = rr.servings > 1) { Text("−") }
                    Text(rr.servings.toString(), fontWeight = FontWeight.SemiBold)
                    TextButton(onClick = { scope.launch { try { r = repo.recipeServings(syncId, rr.servings + 1) } catch (e: Exception) { error = e.message } } }, enabled = rr.servings < 50) { Text("+") }
                    if (rr.servingsGuessed) Text(stringResource(R.string.coach_servings_guessed), style = MaterialTheme.typography.bodySmall)
                }
                rr.kcalGiven?.let { Text(stringResource(R.string.coach_kcal_given, n0(it)), fontWeight = FontWeight.SemiBold) }
                Text(stringResource(if (rr.kcalGiven != null) R.string.coach_kcal_from_ingredients else R.string.coach_per_serving, n0(rr.perServing.kcal)),
                    fontWeight = if (rr.kcalGiven != null) null else FontWeight.SemiBold)
                if (rr.kcalGiven == null) Text(stringResource(R.string.coach_kcal_missing), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Text("${stringResource(R.string.coach_protein)} ${n1(rr.perServing.protein)} g · ${stringResource(R.string.coach_carbs)} ${n1(rr.perServing.carbs)} g · ${stringResource(R.string.coach_fat)} ${n1(rr.perServing.fat)} g",
                    style = MaterialTheme.typography.bodySmall)
                if (rr.unmatched > 0) Text(stringResource(R.string.coach_unmatched, rr.unmatched), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                HorizontalDivider()
                for (l in rr.lines) {
                    Column(Modifier.fillMaxWidth().padding(vertical = 2.dp)) {
                        Text(l.text, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            val options = listOfNotNull(l.food) + l.alternatives.filter { it.id != l.food?.id }
                            var open by remember { mutableStateOf(false) }
                            Box(Modifier.weight(1f)) {
                                Text(l.food?.name ?: stringResource(if (l.vague) R.string.coach_vague else R.string.coach_not_matched), maxLines = 2, overflow = TextOverflow.Ellipsis,
                                    modifier = Modifier.clickable(enabled = l.name != null && options.size > 1) { open = true })
                                DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                                    for (o in options) DropdownMenuItem(text = { Text(o.name) }, onClick = {
                                        open = false
                                        scope.launch { try { repo.foodmap(l.name!!, o.id); r = repo.recipe(syncId) } catch (e: Exception) { error = e.message } }
                                    })
                                }
                            }
                            Text((l.grams?.let { n0(it) + " g" } ?: "–") + (if (l.kcal > 0) " · " + n0(l.kcal) + " kcal" else ""), style = MaterialTheme.typography.bodySmall)
                        }
                    }
                }
            }
        },
        confirmButton = {
            val rr = r
            TextButton(enabled = rr != null && rr.weight > 0, onClick = {
                scope.launch {
                    try {
                        repo.add(DiaryAdd(LocalDate.now().toString(), mealNow(), food = "recipe:$syncId", amount = ((rr!!.weight / rr.servings) * 10).roundToInt() / 10.0, portion = "1 Portion"))
                        android.widget.Toast.makeText(ctx, logged, android.widget.Toast.LENGTH_SHORT).show(); onDismiss()
                    } catch (e: Exception) { error = e.message }
                }
            }) { Text(stringResource(R.string.coach_log_serving)) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.close)) } })
}

// ---------------- Fasten ----------------

/** Fasten auf dem Tagebuch: läuft (Stunden, Ziel, Ende) mit „Beenden“, sonst „Starten“ - nur wenn im Web eingeschaltet. */
@Composable
fun FastingCard(view: FastingView, onChange: (FastingView) -> Unit, onError: (String) -> Unit) {
    if (!view.settings.enabled || view.notAllowed != null) return
    val repo = App.get(LocalContext.current).coach
    val scope = rememberCoroutineScope()
    val fmt = DateTimeFormatter.ofPattern("HH:mm")
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Row { Text(stringResource(R.string.coach_fasting), style = MaterialTheme.typography.titleSmall, modifier = Modifier.weight(1f)); Text(view.settings.plan) }
            val cur = view.current
            if (cur != null) {
                LinearProgressIndicator(progress = { (cur.hours / cur.goal).toFloat().coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth())
                val ends = Instant.parse(cur.ends.let { if (it.endsWith("Z")) it else it + "Z" }).atZone(ZoneId.systemDefault()).format(fmt)
                Text(stringResource(R.string.coach_fasting_since, n1(cur.hours), n0(cur.goal)) + " · " +
                    (if (cur.hours >= cur.goal) stringResource(R.string.coach_fasting_reached) else stringResource(R.string.coach_fasting_ends, ends)),
                    style = MaterialTheme.typography.bodySmall)
                OutlinedButton(onClick = { scope.launch { try { onChange(repo.fastingStop()) } catch (e: Exception) { onError(e.message ?: e.toString()) } } }) { Text(stringResource(R.string.coach_fasting_stop)) }
            } else Button(onClick = { scope.launch { try { onChange(repo.fastingStart()) } catch (e: Exception) { onError(e.message ?: e.toString()) } } }) { Text(stringResource(R.string.coach_fasting_start)) }
        }
    }
}

// ---------------- Pukki als Coach (Profil) ----------------

@Composable
fun CoachPukkiSettings(p: Profile, onChange: (Profile) -> Unit) {
    val ctx = LocalContext.current
    val scope = rememberCoroutineScope()
    var loc by remember { mutableStateOf(CoachLocation.enabled(ctx) && CoachLocation.permitted(ctx)) }
    val ask = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        CoachLocation.setEnabled(ctx, granted); loc = granted
        if (granted) scope.launch { CoachLocation.sendIfWanted(ctx, force = true) }
    }
    Text(stringResource(R.string.coach_pukki), style = MaterialTheme.typography.titleMedium)
    Text(stringResource(R.string.coach_nudges_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    SwitchLine(stringResource(R.string.coach_nudges), p.nudges) { onChange(p.copy(nudges = it)) }
    if (p.nudges) Column(Modifier.padding(start = 16.dp)) {
        SwitchLine(stringResource(R.string.coach_nudge_water), p.nudgeWater) { onChange(p.copy(nudgeWater = it)) }
        SwitchLine(stringResource(R.string.coach_nudge_move), p.nudgeMove) { onChange(p.copy(nudgeMove = it)) }
        SwitchLine(stringResource(R.string.coach_nudge_week), p.nudgeWeek) { onChange(p.copy(nudgeWeek = it)) }
        if (p.nudgeMove) {
            SwitchLine(stringResource(R.string.coach_location), loc) { on ->
                if (!on) { CoachLocation.setEnabled(ctx, false); loc = false }
                else if (CoachLocation.permitted(ctx)) { CoachLocation.setEnabled(ctx, true); loc = true; scope.launch { CoachLocation.sendIfWanted(ctx, force = true) } }
                else ask.launch(Manifest.permission.ACCESS_COARSE_LOCATION)
            }
            Text(stringResource(R.string.coach_location_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
    SwitchLine(stringResource(R.string.coach_ai_data), p.aiData) { onChange(p.copy(aiData = it)) }
    Text(stringResource(R.string.coach_ai_data_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
    Text(stringResource(R.string.coach_garmin_web), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
}

@Composable
private fun SwitchLine(label: String, checked: Boolean, onChange: (Boolean) -> Unit) =
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) { Text(label, Modifier.weight(1f)); Switch(checked, onChange) }
