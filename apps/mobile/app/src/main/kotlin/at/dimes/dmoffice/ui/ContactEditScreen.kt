package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshots.SnapshotStateList
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.Address
import at.dimes.dmoffice.api.Contact
import at.dimes.dmoffice.api.TypedValue
import kotlinx.coroutines.launch

/** Arten von Telefonnummern und Adressen wie im vCard (TYPE) - mit Beschriftung. */
private val phoneTypes = listOf("cell" to R.string.contact_type_cell, "home" to R.string.contact_type_home, "work" to R.string.contact_type_work, "other" to R.string.contact_type_other)
private val mailTypes = listOf("home" to R.string.contact_type_home, "work" to R.string.contact_type_work, "other" to R.string.contact_type_other)

/**
 * Kontakt anlegen oder bearbeiten (Version 2): Name, Firma, Telefonnummern, E-Mail-Adressen, Anschrift, Geburtstag,
 * Notiz. Was die App nicht zeigt (Foto, weitere Felder aus dem Web), bleibt beim Bearbeiten erhalten.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun ContactEditScreen(id: String?, onClose: () -> Unit, onSaved: (String) -> Unit) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var base by remember { mutableStateOf<Contact?>(null) }
    var loaded by remember { mutableStateOf(id == null) }
    var given by remember { mutableStateOf("") }
    var family by remember { mutableStateOf("") }
    var org by remember { mutableStateOf("") }
    var title by remember { mutableStateOf("") }
    val phones = remember { mutableStateListOf<TypedValue>() }
    val mails = remember { mutableStateListOf<TypedValue>() }
    var street by remember { mutableStateOf("") }
    var zip by remember { mutableStateOf("") }
    var city by remember { mutableStateOf("") }
    var birthday by remember { mutableStateOf("") }
    var note by remember { mutableStateOf("") }
    var saving by remember { mutableStateOf(false) }

    LaunchedEffect(id) {
        if (id == null) { phones.add(TypedValue("cell", "")); mails.add(TypedValue("home", "")); return@LaunchedEffect }
        val c = app.contacts.byId(id) ?: run { onClose(); return@LaunchedEffect }
        base = c
        given = c.given; family = c.family; org = c.org; title = c.title
        phones.addAll(c.phones.ifEmpty { listOf(TypedValue("cell", "")) })
        mails.addAll(c.emails.ifEmpty { listOf(TypedValue("home", "")) })
        c.addresses.firstOrNull()?.let { street = it.street; zip = it.zip; city = it.city }
        birthday = c.birthday ?: ""
        note = c.note
        loaded = true
    }

    val canSave = given.isNotBlank() || family.isNotBlank() || org.isNotBlank()
    fun save() {
        scope.launch {
            saving = true
            try {
                val c = base ?: Contact(book = app.contacts.defaultBook())
                val first = c.addresses.firstOrNull() ?: Address(type = "home")
                val address = first.copy(street = street.trim(), zip = zip.trim(), city = city.trim())
                val saved = app.contacts.save(c.copy(
                    displayName = "", given = given.trim(), family = family.trim(), org = org.trim(), title = title.trim(),
                    phones = phones.filter { it.value.isNotBlank() }.map { it.copy(value = it.value.trim()) },
                    emails = mails.filter { it.value.isNotBlank() }.map { it.copy(value = it.value.trim()) },
                    addresses = (if (address.street.isNotBlank() || address.zip.isNotBlank() || address.city.isNotBlank()) listOf(address) else emptyList()) + c.addresses.drop(1),
                    birthday = birthday.trim().ifBlank { null }, note = note.trim(),
                    photo = null,   // null: Foto bleibt, wie es ist
                ))
                onSaved(saved.id)
            } catch (e: Exception) {
                android.widget.Toast.makeText(ctx, e.message ?: "", android.widget.Toast.LENGTH_LONG).show()
            } finally { saving = false }
        }
    }

    Scaffold(topBar = {
        TopAppBar(title = { Text(stringResource(if (id == null) R.string.contact_new else R.string.contact_edit)) },
            navigationIcon = { IconButton(onClick = onClose) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
            actions = {
                if (saving) CircularProgressIndicator(Modifier.padding(12.dp).width(24.dp))
                else TextButton(onClick = { save() }, enabled = canSave && loaded) { Text(stringResource(R.string.save)) }
            })
    }) { pad ->
        if (!loaded) return@Scaffold
        Column(Modifier.padding(pad).fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedTextField(given, { given = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.contact_given)) }, singleLine = true)
            OutlinedTextField(family, { family = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.contact_family)) }, singleLine = true)
            OutlinedTextField(org, { org = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.contact_org)) }, singleLine = true)
            OutlinedTextField(title, { title = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.contact_job)) }, singleLine = true)

            TypedValues(stringResource(R.string.contact_phones), phones, phoneTypes, "cell", KeyboardType.Phone)
            TypedValues(stringResource(R.string.contact_emails), mails, mailTypes, "home", KeyboardType.Email)

            Text(stringResource(R.string.contact_address), style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 8.dp))
            OutlinedTextField(street, { street = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.contact_street)) }, singleLine = true)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedTextField(zip, { zip = it }, Modifier.weight(0.35f), label = { Text(stringResource(R.string.contact_zip)) }, singleLine = true)
                OutlinedTextField(city, { city = it }, Modifier.weight(0.65f), label = { Text(stringResource(R.string.contact_city)) }, singleLine = true)
            }
            OutlinedTextField(birthday, { birthday = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.contact_birthday)) },
                placeholder = { Text("1980-05-17") }, singleLine = true)
            OutlinedTextField(note, { note = it }, Modifier.fillMaxWidth(), label = { Text(stringResource(R.string.contact_note)) }, minLines = 2)
        }
    }
}

/** Telefonnummern bzw. E-Mail-Adressen: je Zeile Art und Wert, weitere mit „+“. */
@Composable
private fun TypedValues(label: String, items: SnapshotStateList<TypedValue>, types: List<Pair<String, Int>>, newType: String, keyboard: KeyboardType) {
    Text(label, style = MaterialTheme.typography.titleSmall, modifier = Modifier.padding(top = 8.dp))
    for (i in items.indices) {
        val item = items[i]
        Row(verticalAlignment = Alignment.CenterVertically) {
            var open by remember { mutableStateOf(false) }
            TextButton(onClick = { open = true }, modifier = Modifier.width(96.dp)) {
                Text(stringResource(types.firstOrNull { it.first.equals(item.type, ignoreCase = true) }?.second ?: types.last().second), maxLines = 1)
                DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                    for ((key, res) in types) DropdownMenuItem(text = { Text(stringResource(res)) }, onClick = { items[i] = item.copy(type = key); open = false })
                }
            }
            OutlinedTextField(item.value, { items[i] = item.copy(value = it) }, Modifier.weight(1f), singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = keyboard))
            IconButton(onClick = { items.removeAt(i) }) { Icon(Icons.Default.Close, contentDescription = stringResource(R.string.contact_remove)) }
        }
    }
    TextButton(onClick = { items.add(TypedValue(newType, "")) }) {
        Icon(Icons.Default.Add, contentDescription = null)
        Text(" " + stringResource(R.string.contact_add_more))
    }
}
