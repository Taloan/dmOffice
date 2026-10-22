package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.LifecycleResumeEffect
import at.dimes.dmoffice.BuildConfig
import at.dimes.dmoffice.R
import at.dimes.dmoffice.update.Updater
import at.dimes.dmoffice.update.Updater.State

/** Hinweis auf eine neue Version: Notizen, Aktualisieren mit Fortschritt, Weg zur Installationserlaubnis. */
@Composable
fun UpdateDialog() {
    val ctx = LocalContext.current
    val show by Updater.dialog.collectAsState()
    val state by Updater.state.collectAsState()
    // Zurueck aus der Systemeinstellung: wenn erlaubt, gleich weiter
    LifecycleResumeEffect(Unit) { Updater.resumeAfterPermission(ctx); onPauseOrDispose { } }
    if (!show) return
    val release = when (val s = state) {
        is State.Available -> s.release; is State.Downloading -> s.release; is State.NeedsPermission -> s.release
        is State.Installing -> s.release; is State.Failed -> s.release; else -> null
    } ?: return
    val busy = state is State.Downloading || state is State.Installing
    AlertDialog(
        onDismissRequest = { if (!busy) Updater.dismiss(ctx) },
        title = { Text(stringResource(if (state is State.NeedsPermission) R.string.update_permission_title else R.string.update_available_title)) },
        text = {
            Column(Modifier.fillMaxWidth()) {
                when (val s = state) {
                    is State.NeedsPermission -> Text(stringResource(R.string.update_permission_text))
                    else -> {
                        Text(stringResource(R.string.update_available_text, release.versionName, BuildConfig.VERSION_NAME))
                        if (release.notes.isNotBlank()) {
                            Text(stringResource(R.string.update_notes), style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 12.dp, bottom = 4.dp))
                            Text(release.notes, style = MaterialTheme.typography.bodySmall,
                                modifier = Modifier.heightIn(max = 220.dp).verticalScroll(rememberScrollState()))
                        }
                        if (s is State.Downloading) {
                            Text(stringResource(R.string.update_downloading) + if (s.progress >= 0) " " + (s.progress * 100).toInt() + " %" else "",
                                style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 12.dp, bottom = 4.dp))
                            if (s.progress >= 0) LinearProgressIndicator(progress = { s.progress }, modifier = Modifier.fillMaxWidth())
                            else LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                        }
                        if (s is State.Installing) Text(stringResource(R.string.update_installing), style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 12.dp))
                        if (s is State.Failed) Text(s.message, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(top = 12.dp))
                    }
                }
            }
        },
        confirmButton = {
            when (state) {
                is State.NeedsPermission -> TextButton(onClick = { Updater.openInstallPermission(ctx) }) { Text(stringResource(R.string.update_open_settings)) }
                is State.Available, is State.Failed -> TextButton(onClick = { Updater.update(ctx) }) { Text(stringResource(R.string.update_now)) }
                else -> {}
            }
        },
        dismissButton = { if (!busy) TextButton(onClick = { Updater.dismiss(ctx) }) { Text(stringResource(R.string.update_later)) } }
    )
}
