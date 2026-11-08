package at.dimes.dmoffice.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.ServerStatus
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/**
 * Oben rechts (Version 2): ein kleiner grüner Punkt, solange der Server antwortet; sonst ein roter Hinweis
 * „Server nicht erreichbar“. Antippen zeigt den Grund und prüft neu. Geprüft wird jede Minute, ohne Verbindung öfter.
 */
@Composable
fun ServerBadge(modifier: Modifier = Modifier) {
    val app = App.get(LocalContext.current)
    val scope = rememberCoroutineScope()
    val reachable by ServerStatus.reachable.collectAsState()
    val problem by ServerStatus.problem.collectAsState()
    val lastOk by ServerStatus.lastOk.collectAsState()
    var details by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        while (true) {
            val ok = ServerStatus.check(app)
            delay(if (ok) 60_000L else 15_000L)
        }
    }
    when (reachable) {
        true -> Box(modifier.padding(4.dp).size(8.dp).clip(CircleShape).background(Color(0xFF2E7D32)).clickable { details = true })
        false -> Row(modifier.padding(4.dp).clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.errorContainer)
            .clickable { details = true }.padding(horizontal = 8.dp, vertical = 3.dp), verticalAlignment = Alignment.CenterVertically) {
            Icon(Icons.Default.CloudOff, contentDescription = null, Modifier.size(14.dp), tint = MaterialTheme.colorScheme.onErrorContainer)
            Text(" " + stringResource(R.string.server_unreachable), fontSize = 11.sp, color = MaterialTheme.colorScheme.onErrorContainer)
        }
        null -> {}
    }
    if (details) AlertDialog(onDismissRequest = { details = false },
        title = { Text(stringResource(if (reachable == true) R.string.server_reachable else R.string.server_unreachable)) },
        text = {
            Column {
                val account = app.prefs.account.value
                Text(account?.baseUrl ?: "")
                problem?.let { p ->
                    Text(if (p.startsWith("dns:")) stringResource(R.string.server_problem_dns, p.removePrefix("dns:")) else p,
                        Modifier.padding(top = 8.dp), color = MaterialTheme.colorScheme.error)
                }
                if (lastOk > 0) Text(stringResource(R.string.server_last_ok, Fmt.dateTime(LocalContext.current, lastOk, false)), Modifier.padding(top = 8.dp),
                    style = MaterialTheme.typography.bodySmall)
            }
        },
        confirmButton = { TextButton(onClick = { scope.launch { ServerStatus.check(app) } }) { Text(stringResource(R.string.server_check)) } },
        dismissButton = { TextButton(onClick = { details = false }) { Text(stringResource(R.string.close)) } })
}
