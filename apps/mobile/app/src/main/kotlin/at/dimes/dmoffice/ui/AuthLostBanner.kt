package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R

/** Der Server lehnt das App-Passwort ab: Hinweis über der Leiste unten, „Neu verbinden“ führt zur Anmeldung ohne Abmelden. */
@Composable
fun AuthLostBanner(modifier: Modifier = Modifier, onReconnect: () -> Unit) {
    Card(
        modifier.fillMaxWidth().padding(horizontal = 12.dp).padding(bottom = 72.dp).navigationBarsPadding(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.errorContainer, contentColor = MaterialTheme.colorScheme.onErrorContainer),
    ) {
        Column(Modifier.padding(14.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            Text(stringResource(R.string.auth_rejected), style = MaterialTheme.typography.bodyMedium)
            Button(onClick = onReconnect) { Text(stringResource(R.string.reconnect)) }
        }
    }
}
