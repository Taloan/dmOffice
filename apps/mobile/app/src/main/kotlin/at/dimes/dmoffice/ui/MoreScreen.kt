package at.dimes.dmoffice.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Contacts
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material3.Icon
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import at.dimes.dmoffice.R

/** Reiter "Mehr": Kontakte, Optionen (Notizen haben einen eigenen Reiter). */
@Composable
fun MoreScreen(onContacts: () -> Unit, onSettings: () -> Unit) {
    Column(Modifier.fillMaxSize()) {
        ListItem(headlineContent = { Text(stringResource(R.string.contacts)) }, leadingContent = { Icon(Icons.Default.Contacts, contentDescription = null, tint = MaterialTheme.colorScheme.primary) }, modifier = Modifier.clickable(onClick = onContacts))
        ListItem(headlineContent = { Text(stringResource(R.string.settings)) }, leadingContent = { Icon(Icons.Default.Settings, contentDescription = null, tint = MaterialTheme.colorScheme.primary) }, modifier = Modifier.clickable(onClick = onSettings))
    }
}
