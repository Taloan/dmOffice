package at.dimes.dmoffice.ui.dms

import android.widget.Toast
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.selection.selectable
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import kotlinx.coroutines.launch

/** Das DMS ist für dieses Konto da (Release-Liste, Hauptschalter, Dateiablage nicht abgeschaltet). */
fun dmsOn(): Boolean = at.dimes.dmoffice.data.Features.let { !it.filesOff.value && it.on(it.DMS) }

/**
 * „Ins DMS ablegen“ aus der Mail-Ansicht (wie die Auswahl im Menüband des Web): ganze Mail (PDF und .eml), Anhänge oder
 * beides; bei mehreren Ablageprofilen das Profil.
 */
@Composable
fun DmsMailDialog(account: String, folder: String, uid: Long, hasAttachments: Boolean, onDismiss: () -> Unit) {
    val ctx = LocalContext.current
    val repo = remember { DmsRepo(ctx) }
    val scope = rememberCoroutineScope()
    var profiles by remember { mutableStateOf<List<DmsProfile>?>(null) }
    var profile by remember { mutableStateOf<String?>(null) }
    var what by remember { mutableStateOf("whole") }
    var busy by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        try { profiles = repo.profiles().sortedByDescending { it.default }; profile = profiles?.firstOrNull()?.id }
        catch (e: Exception) { Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show(); onDismiss() }
    }
    fun file() {
        busy = true
        scope.launch {
            try {
                val n = repo.fromMail(account, folder, uid, whole = what != "attachments", attachments = what != "whole", parts = null, profile = profile)
                Toast.makeText(ctx, ctx.resources.getQuantityString(R.plurals.dms_mail_done, n, n), Toast.LENGTH_SHORT).show()
                onDismiss()
            } catch (e: Exception) { busy = false; Toast.makeText(ctx, e.message ?: e.toString(), Toast.LENGTH_LONG).show() }
        }
    }
    @Composable fun Choice(selected: Boolean, label: String, onClick: () -> Unit) =
        Row(Modifier.fillMaxWidth().selectable(selected, role = Role.RadioButton, onClick = onClick).padding(vertical = 2.dp), verticalAlignment = Alignment.CenterVertically) {
            RadioButton(selected, null, Modifier.padding(8.dp)); Text(label)
        }
    AlertDialog(onDismissRequest = onDismiss, title = { Text(stringResource(R.string.dms_file_mail)) },
        text = {
            val list = profiles
            if (list == null || busy) { CircularProgressIndicator(); return@AlertDialog }
            Column {
                if (hasAttachments) {
                    Choice(what == "whole", stringResource(R.string.dms_mail_whole)) { what = "whole" }
                    Choice(what == "attachments", stringResource(R.string.dms_mail_attachments)) { what = "attachments" }
                    Choice(what == "both", stringResource(R.string.dms_mail_both)) { what = "both" }
                } else Text(stringResource(R.string.dms_mail_whole))
                if (list.size > 1) {
                    HorizontalDivider(Modifier.padding(vertical = 8.dp))
                    Text(stringResource(R.string.dms_pick_profile), style = MaterialTheme.typography.labelLarge)
                    list.forEach { p -> Choice(profile == p.id, p.name + if (p.target == "tresor") " · " + stringResource(R.string.dms_tresor) else "") { profile = p.id } }
                }
            }
        },
        confirmButton = { TextButton(onClick = { file() }, enabled = !busy && profiles != null) { Text(stringResource(R.string.dms_file_mail)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } })
}
