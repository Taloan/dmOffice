package at.dimes.dmoffice.ui

import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Warning
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.PhishFinding

/** Warnung bei einem starken Merkmal oder zwei schwachen, sonst ein Hinweis (wie im Web). */
fun phishWarns(found: List<PhishFinding>) = found.any { it.strong } || found.size >= 2

/** Ein Satz je Merkmal - die Domains kommen vom Server, der Wortlaut aus den Ressourcen. */
fun phishText(ctx: Context, f: PhishFinding): String = when (f.kind) {
    "auth" -> if (f.detail == "dmarc") ctx.getString(R.string.phish_auth_dmarc, f.domain ?: "") else ctx.getString(R.string.phish_auth_spf, f.domain ?: "")
    "link" -> ctx.getString(R.string.phish_link, f.shown ?: "", f.target ?: "")
    "lookalike" -> when (f.detail) {
        "mixed" -> ctx.getString(R.string.phish_mixed, f.domain ?: "")
        "sender" -> ctx.getString(R.string.phish_lookalike_sender, f.domain ?: "", f.like ?: "")
        else -> ctx.getString(R.string.phish_lookalike_link, f.domain ?: "", f.like ?: "")
    }
    "newsender" -> ctx.getString(R.string.phish_newsender, f.domain ?: "", f.target ?: "")
    else -> ""
}

/** Warnbalken ueber dem Inhalt der Mail. Nur ein Hinweis - die Mail wird weder verschoben noch gesperrt. */
@Composable
fun PhishCard(found: List<PhishFinding>, onJunk: (() -> Unit)?) {
    val ctx = LocalContext.current
    val texts = found.map { phishText(ctx, it) }.filter { it.isNotEmpty() }
    if (texts.isEmpty()) return
    val warn = phishWarns(found)
    val colors = MaterialTheme.colorScheme
    Card(Modifier.fillMaxWidth().padding(vertical = 6.dp),
        colors = CardDefaults.cardColors(containerColor = if (warn) colors.errorContainer else colors.secondaryContainer,
            contentColor = if (warn) colors.onErrorContainer else colors.onSecondaryContainer)) {
        Column(Modifier.padding(12.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(if (warn) Icons.Outlined.Warning else Icons.Outlined.Info, contentDescription = null, modifier = Modifier.size(18.dp))
                Text(stringResource(if (warn) R.string.phish_title_warn else R.string.phish_title_hint),
                    fontWeight = FontWeight.SemiBold, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(start = 8.dp))
            }
            for (t in texts) Text("• $t", style = MaterialTheme.typography.bodySmall)
            Text(stringResource(R.string.phish_advice), style = MaterialTheme.typography.bodySmall)
            if (onJunk != null) TextButton(onClick = onJunk) { Text(stringResource(R.string.report_junk)) }
        }
    }
}
