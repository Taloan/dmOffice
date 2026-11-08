package at.dimes.dmoffice.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.data.ShownCalendars

/**
 * Ein Termin aus einem gezeigten Kalender (Version 2): nur ansehen - nicht bearbeiten, nicht in den Android-Kalender
 * und kein Bildschirmfoto.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ShownEventScreen(href: String, onClose: () -> Unit) {
    BackHandler(onBack = onClose)
    val ctx = LocalContext.current
    // Keine Bildschirmfoto-Sperre mehr (Entscheidung): abfotografieren laesst sich nie verhindern (Web, zweites Handy) - Zeigen heisst nur, nicht speichern, herunterladen oder weiterleiten
    val shown = ShownCalendars.get(href)
    Scaffold(topBar = {
        TopAppBar(title = { Text(shown?.item?.summary ?: "", maxLines = 1) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } })
    }) { pad ->
        Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            if (shown == null) { Text(stringResource(R.string.chat_gone)); return@Column }
            val it = shown.item
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(Modifier.size(12.dp).clip(CircleShape).background(shown.color?.let { c -> Color(c) } ?: MaterialTheme.colorScheme.primary))
                Text(shown.calendar, Modifier.padding(start = 8.dp), style = MaterialTheme.typography.labelLarge)
            }
            val start = it.start ?: 0L
            val end = it.end ?: start
            // Ganztaegig endet exklusiv um Mitternacht: als letzten Tag den Vortag zeigen
            Text(if (it.allDay) Fmt.dateTime(ctx, start, true) + (if (end - start > 86_400_000L) " – " + Fmt.dateTime(ctx, end - 86_400_000L, true) else "")
                 else Fmt.dateTime(ctx, start, false) + " – " + Fmt.time(ctx, end), style = MaterialTheme.typography.titleMedium)
            if (it.recurring) Text(stringResource(R.string.chat_repeats), color = MaterialTheme.colorScheme.onSurfaceVariant)
            it.location?.takeIf { l -> l.isNotBlank() }?.let { l -> Text(l) }
            it.description?.takeIf { d -> d.isNotBlank() }?.let { d -> Text(d) }
            Text(if (shown.owner != null) stringResource(R.string.cal_shown_by, shown.owner) else stringResource(R.string.cal_shown),
                style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}
