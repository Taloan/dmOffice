package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FloatingActionButton
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextField
import androidx.compose.material3.TextFieldDefaults
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.style.TextOverflow
import at.dimes.dmoffice.R

/** Suche eines Reiters: offen (Lupe angetippt) und der eingegebene Text. */
@Stable
class TabSearch(open: Boolean = false, query: String = "") {
    var open by mutableStateOf(open)
    var query by mutableStateOf(query)
    /** Gesucht wird erst, wenn etwas eingegeben ist. */
    val active: Boolean get() = open && query.isNotBlank()
    fun close() { open = false; query = "" }
}

@Composable
fun rememberTabSearch(): TabSearch {
    var open by rememberSaveable { mutableStateOf(false) }
    var query by rememberSaveable { mutableStateOf("") }
    val s = remember { TabSearch(open, query) }
    // Drehen des Handys: offen und Text bleiben
    LaunchedEffect(s.open, s.query) { open = s.open; query = s.query }
    return s
}

/**
 * Kopfzeile eines Reiters (Version 2, überall gleich): links die Überschrift, rechts die Lupe und eigene Knöpfe.
 * Die Lupe macht aus der Zeile ein Suchfeld; der Pfeil links schließt die Suche wieder.
 * insets: eingebettet in die Startseite WindowInsets(0) (die Startseite kümmert sich um die Statusleiste).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TabTopBar(
    title: String,
    search: TabSearch? = null,
    searchHint: String? = null,
    insets: WindowInsets? = null,
    navigationIcon: @Composable () -> Unit = {},
    actions: @Composable RowScope.() -> Unit = {},
) {
    val windowInsets = insets ?: TopAppBarDefaults.windowInsets
    // Zurück schließt zuerst die Suche
    androidx.activity.compose.BackHandler(enabled = search?.open == true) { search?.close() }
    if (search != null && search.open) {
        TopAppBar(windowInsets = windowInsets,
            navigationIcon = { IconButton(onClick = { search.close() }) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            title = {
                val focus = remember { FocusRequester() }
                LaunchedEffect(Unit) { try { focus.requestFocus() } catch (_: Exception) { } }
                TextField(value = search.query, onValueChange = { search.query = it }, singleLine = true, modifier = Modifier.fillMaxWidth().focusRequester(focus),
                    placeholder = { Text(searchHint ?: stringResource(R.string.search)) },
                    colors = TextFieldDefaults.colors(focusedContainerColor = Color.Transparent, unfocusedContainerColor = Color.Transparent))
            },
            actions = { if (search.query.isNotEmpty()) IconButton(onClick = { search.query = "" }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.cancel)) } })
    } else {
        TopAppBar(windowInsets = windowInsets, title = { Text(title, maxLines = 1, overflow = TextOverflow.Ellipsis) }, navigationIcon = navigationIcon,
            actions = {
                if (search != null) IconButton(onClick = { search.open = true }) { Icon(Icons.Default.Search, contentDescription = stringResource(R.string.search)) }
                actions()
                // Erreicht die App den Server? (grüner Punkt bzw. roter Hinweis)
                ServerBadge(Modifier.align(androidx.compose.ui.Alignment.CenterVertically))
            })
    }
}

/** Neu anlegen (Version 2, überall gleich): runder Knopf mit „+“, die Beschreibung nur für die Sprachausgabe. */
@Composable
fun AddButton(description: String, modifier: Modifier = Modifier, onClick: () -> Unit) {
    FloatingActionButton(onClick = onClick, modifier = modifier) { Icon(Icons.Default.Add, contentDescription = description) }
}
