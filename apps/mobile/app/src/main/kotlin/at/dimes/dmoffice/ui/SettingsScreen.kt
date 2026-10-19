package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.material.icons.filled.Check
import androidx.compose.foundation.clickable
import androidx.compose.foundation.background
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.ui.draw.clip
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.AccountCircle
import androidx.compose.material.icons.outlined.CalendarMonth
import androidx.compose.material.icons.outlined.Email
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Palette
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.work.WorkInfo
import androidx.work.WorkManager
import at.dimes.dmoffice.App
import at.dimes.dmoffice.BuildConfig
import at.dimes.dmoffice.R
import at.dimes.dmoffice.sync.SyncWorker
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class, androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
fun SettingsScreen(onSignedOut: () -> Unit, onClose: () -> Unit = {}) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    val account by app.prefs.account.collectAsState()
    val settings by app.prefs.settings.collectAsState()
    val cals by app.repo.calendars.collectAsState(initial = emptyList())
    val pending by app.repo.pendingCount.collectAsState(initial = 0)
    val work by WorkManager.getInstance(ctx).getWorkInfosForUniqueWorkFlow("sync-now").collectAsState(initial = emptyList())
    val syncing = work.any { it.state == WorkInfo.State.RUNNING || it.state == WorkInfo.State.ENQUEUED }
    var confirmRemove by remember { mutableStateOf(false) }
    var colorFor by remember { mutableStateOf<at.dimes.dmoffice.data.CalendarEntity?>(null) }
    // Bereich der Optionen (null = Uebersicht); Zurueck fuehrt erst zur Uebersicht
    var page by androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf<String?>(null) }
    androidx.activity.compose.BackHandler(enabled = page != null) { page = null }
    val hasMail by app.mail.hasAccounts.collectAsState()
    val pageTitle = when (page) {
        "account" -> R.string.settings_account; "calendar" -> R.string.settings_calendar; "mail" -> R.string.settings_mail
        "look" -> R.string.settings_look; "android" -> R.string.android_section; "security" -> R.string.security_section; else -> R.string.settings
    }

    androidx.compose.material3.Scaffold(topBar = {
        androidx.compose.material3.TopAppBar(title = { Text(stringResource(pageTitle)) },
            navigationIcon = { androidx.compose.material3.IconButton(onClick = { if (page != null) page = null else onClose() }) { androidx.compose.material3.Icon(androidx.compose.material.icons.Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } })
    }) { pad ->
    Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        when (page) {
            null -> {
                // Uebersicht: Bereiche wie in den Android-Einstellungen, darunter Abmelden und Version
                SettingsEntry(Icons.Outlined.AccountCircle, stringResource(R.string.settings_account), stringResource(R.string.settings_account_sub)) { page = "account" }
                SettingsEntry(Icons.Outlined.CalendarMonth, stringResource(R.string.settings_calendar), stringResource(R.string.settings_calendar_sub)) { page = "calendar" }
                if (hasMail) SettingsEntry(Icons.Outlined.Email, stringResource(R.string.settings_mail), stringResource(R.string.settings_mail_sub)) { page = "mail" }
                SettingsEntry(Icons.Outlined.Palette, stringResource(R.string.settings_look), stringResource(R.string.settings_look_sub)) { page = "look" }
                SettingsEntry(Icons.Outlined.PhoneAndroid, stringResource(R.string.android_section), stringResource(R.string.settings_android_sub)) { page = "android" }
                SettingsEntry(Icons.Outlined.Lock, stringResource(R.string.security_section), stringResource(R.string.settings_security_sub)) { page = "security" }
        HorizontalDivider(Modifier.padding(vertical = 12.dp))
                Row(Modifier.padding(horizontal = 16.dp)) { OutlinedButton(onClick = { confirmRemove = true }) { Text(stringResource(R.string.sign_out)) } }
                Spacer(Modifier.height(16.dp))
                Text(stringResource(R.string.version, BuildConfig.VERSION_NAME), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            "account" -> {
                SectionHeader(stringResource(R.string.account))
                ListItem(headlineContent = { Text(account?.displayName ?: "") }, supportingContent = { Text((account?.login ?: "") + " · " + (account?.baseUrl ?: "")) })
                Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                    Button(onClick = { SyncWorker.now(ctx) }, enabled = !syncing) { Text(stringResource(if (syncing) R.string.syncing else R.string.sync_now)) }
                }
                Text(
                    stringResource(R.string.last_sync, if (settings.lastSync == 0L) stringResource(R.string.never) else Fmt.dateTime(ctx, settings.lastSync, false)),
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                settings.lastError?.let { Text(stringResource(R.string.sync_error, it), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error) }
                if (pending > 0) Text(stringResource(R.string.pending_changes, pending), modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodySmall)

            }
            "calendar" -> {
                SectionHeader(stringResource(R.string.calendars))
                Text(stringResource(R.string.tap_to_change_color), modifier = Modifier.padding(horizontal = 16.dp),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                for (c in cals) ListItem(
                    modifier = Modifier.clickable { colorFor = c },
                    headlineContent = { Text(c.name) },
                    supportingContent = { Text(listOfNotNull(if (c.supportsEvents) stringResource(R.string.event) else null, if (c.supportsTasks) stringResource(R.string.tasks) else null).joinToString(" · ")) },
                    leadingContent = { androidx.compose.foundation.Canvas(Modifier.padding(4.dp).height(16.dp).fillMaxWidth(0.06f)) { drawCircle(colorOf(c.color), radius = 8.dp.toPx()) } },
                    trailingContent = { Checkbox(checked = c.visible, onCheckedChange = { v -> scope.launch { app.repo.setCalendarVisible(c.href, v) } }) }
                )

                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                ListItem(headlineContent = { Text(stringResource(R.string.show_completed)) }, trailingContent = { Switch(checked = settings.showCompleted, onCheckedChange = { app.prefs.setShowCompleted(it) }) })
                val reminderOptions = listOf(-1, 0, 5, 10, 15, 30, 60, 120, 1440)
                Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                    Picker(stringResource(R.string.default_reminder), settings.defaultReminderMinutes, reminderOptions,
                        { m -> Fmt.reminderLabel(ctx, if (m < 0) null else m * 60L) }, { app.prefs.setDefaultReminder(it) })
                }

                colorFor?.let { c ->
                    val failed = stringResource(R.string.color_failed)
                    CalendarColorDialog(c.name, c.color, onDismiss = { colorFor = null }) { argb ->
                        colorFor = null
                        scope.launch {
                            if (!app.repo.setCalendarColor(c.href, argb))
                                android.widget.Toast.makeText(ctx, failed, android.widget.Toast.LENGTH_LONG).show()
                        }
                    }
                }

                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                SectionHeader(stringResource(R.string.agenda_settings))
                ListItem(headlineContent = { Text(stringResource(R.string.agenda_show_tasks)) }, trailingContent = { Switch(checked = settings.agendaShowTasks, onCheckedChange = { app.prefs.setAgendaShowTasks(it) }) })
                Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(12.dp)) {
                    Picker(stringResource(R.string.multi_day), settings.agendaMultiDay, at.dimes.dmoffice.data.MultiDay.entries, { ctx.getString(it.label) }, { app.prefs.setAgendaMultiDay(it) })
                    Picker(stringResource(R.string.days_ahead), settings.agendaDays, listOf(14, 30, 60, 90, 180, 365), { ctx.resources.getQuantityString(R.plurals.n_days, it, it) }, { app.prefs.setAgendaDays(it) })
                }

            }
            "mail" -> {
                // Benachrichtigungen bei neuen Mails: aus, alle, nur VIPs; Textauszug; Sperrbildschirm
                SectionHeader(stringResource(R.string.mail_notifications))
                Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)) {
                    Picker(stringResource(R.string.mail_notify_when), settings.mailNotify, listOf("all", "vip", "off"),
                        { ctx.getString(when (it) { "vip" -> R.string.mail_notify_vip; "off" -> R.string.mail_notify_off; else -> R.string.mail_notify_all }) },
                        { app.prefs.setMailNotify(it) })
                    Picker(stringResource(R.string.mail_lockscreen), settings.mailLockscreen, listOf("show", "hide"),
                        { ctx.getString(if (it == "hide") R.string.mail_lock_hide else R.string.mail_lock_show) }, { app.prefs.setMailLockscreen(it) })
                }
                ListItem(headlineContent = { Text(stringResource(R.string.mail_preview)) },
                    trailingContent = { Switch(checked = settings.mailPreview, onCheckedChange = { app.prefs.setMailPreview(it) }) })
                Text(stringResource(R.string.mail_notify_hint), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

                // Wischgesten in der Mailliste: je Richtung kurz und weit
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                SectionHeader(stringResource(R.string.swipe_gestures))
                Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)) {
                    val all = SwipeAction.entries.toList()
                    for ((key, label, value) in listOf(
                        Triple("swipeLeft", R.string.swipe_left, settings.swipeLeft), Triple("swipeLeftFar", R.string.swipe_left_far, settings.swipeLeftFar),
                        Triple("swipeRight", R.string.swipe_right, settings.swipeRight), Triple("swipeRightFar", R.string.swipe_right_far, settings.swipeRightFar))) {
                        Picker(stringResource(label), SwipeAction.of(value), all, { ctx.getString(if (it == SwipeAction.None && key.endsWith("Far")) R.string.swipe_same else it.label) },
                            { app.prefs.setSwipe(key, it.key) })
                    }
                }
                Text(stringResource(R.string.swipe_hint), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

            }
            "look" -> {
                SectionHeader(stringResource(R.string.accent_color))
                androidx.compose.foundation.layout.FlowRow(
                    Modifier.padding(horizontal = 16.dp, vertical = 4.dp),
                    horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(12.dp),
                    verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(12.dp)
                ) {
                    for ((name, argb) in ACCENTS) {
                        val selected = settings.accent == argb
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            androidx.compose.foundation.layout.Box(
                                Modifier.size(40.dp).clip(CircleShape).background(androidx.compose.ui.graphics.Color(argb.toInt()))
                                    .clickable { app.prefs.setAccent(argb); scope.launch { at.dimes.dmoffice.widget.Widgets.updateAll(ctx) } },
                                contentAlignment = Alignment.Center
                            ) { if (selected) androidx.compose.material3.Icon(androidx.compose.material.icons.Icons.Default.Check, contentDescription = null, tint = androidx.compose.ui.graphics.Color.White) }
                            Text(name, style = MaterialTheme.typography.labelSmall, color = if (selected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                }

            }
            "android" -> {
                SectionHeader(stringResource(R.string.android_section))
                val calPerm = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions()) { r ->
                    if (r.values.all { it }) { app.prefs.setAndroidCalendar(true); scope.launch { withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.ProviderSync.syncCalendars(ctx) } } }
                    else android.widget.Toast.makeText(ctx, ctx.getString(R.string.permission_needed), android.widget.Toast.LENGTH_SHORT).show()
                }
                val conPerm = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions()) { r ->
                    if (r.values.all { it }) { app.prefs.setAndroidContacts(true); scope.launch { withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.ProviderSync.syncContacts(ctx) } } }
                    else android.widget.Toast.makeText(ctx, ctx.getString(R.string.permission_needed), android.widget.Toast.LENGTH_SHORT).show()
                }
                ListItem(headlineContent = { Text(stringResource(R.string.android_calendar)) },
                    trailingContent = { Switch(checked = settings.androidCalendar, onCheckedChange = { on ->
                        if (on) calPerm.launch(arrayOf(android.Manifest.permission.READ_CALENDAR, android.Manifest.permission.WRITE_CALENDAR))
                        else { app.prefs.setAndroidCalendar(false); scope.launch { withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.ProviderSync.removeCalendars(ctx) } } }
                    }) })
                ListItem(headlineContent = { Text(stringResource(R.string.android_contacts)) },
                    trailingContent = { Switch(checked = settings.androidContacts, onCheckedChange = { on ->
                        if (on) conPerm.launch(arrayOf(android.Manifest.permission.READ_CONTACTS, android.Manifest.permission.WRITE_CONTACTS))
                        else { app.prefs.setAndroidContacts(false); scope.launch { withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.ProviderSync.removeContacts(ctx) } } }
                    }) })
                Text(stringResource(R.string.android_plaintext), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

            }
            "security" -> {
                SectionHeader(stringResource(R.string.security_section))
                ListItem(headlineContent = { Text(stringResource(R.string.app_lock)) }, supportingContent = { Text(stringResource(R.string.app_lock_desc)) },
                    trailingContent = { Switch(checked = settings.appLock, onCheckedChange = { app.prefs.setAppLock(it) }) })
                Text(stringResource(if (account?.encrypted == true) R.string.e2e_on else R.string.e2e_off), modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodySmall,
                    color = if (account?.encrypted == true) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error)
                Text(stringResource(R.string.db_encrypted), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

            }
        }
    }
    }

    if (confirmRemove) AlertDialog(
        onDismissRequest = { confirmRemove = false },
        title = { Text(stringResource(R.string.sign_out)) },
        text = { Text(stringResource(R.string.sign_out_confirm)) },
        confirmButton = {
            TextButton(onClick = {
                confirmRemove = false
                scope.launch {
                    SyncWorker.cancel(ctx)
                    withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.ProviderSync.removeCalendars(ctx); at.dimes.dmoffice.sync.ProviderSync.removeContacts(ctx); app.db.clearAll() }
                    app.prefs.setAccount(null)
                    app.prefs.clearSettings()
                    at.dimes.dmoffice.widget.Widgets.updateAll(ctx)
                    onSignedOut()
                }
            }) { Text(stringResource(R.string.sign_out)) }
        },
        dismissButton = { TextButton(onClick = { confirmRemove = false }) { Text(stringResource(R.string.cancel)) } }
    )
}

/** Eintrag der Uebersicht: Symbol, Titel, kurze Beschreibung, fuehrt in den Bereich. */
@Composable
private fun SettingsEntry(icon: androidx.compose.ui.graphics.vector.ImageVector, title: String, sub: String, onClick: () -> Unit) {
    ListItem(
        modifier = Modifier.clickable(onClick = onClick),
        leadingContent = { androidx.compose.material3.Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.primary) },
        headlineContent = { Text(title) },
        supportingContent = { Text(sub, maxLines = 1) },
    )
}

/** Dieselben Farben wie im Web (Kalender und Listen bearbeiten). */
private val CALENDAR_COLORS = listOf(0xFF0F6CBD, 0xFF13A10E, 0xFFCA5010, 0xFFC239B3, 0xFFE3008C, 0xFF986F0B, 0xFF038387, 0xFF8764B8, 0xFFD13438, 0xFF69797E)

@OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
@Composable
private fun CalendarColorDialog(name: String, current: Int?, onDismiss: () -> Unit, onPick: (Int) -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(name) },
        text = {
            Column {
                Text(stringResource(R.string.choose_color), style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(bottom = 12.dp))
                androidx.compose.foundation.layout.FlowRow(
                    horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(12.dp),
                    verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(12.dp)
                ) {
                    for (argb in CALENDAR_COLORS) {
                        val selected = current != null && (current and 0xFFFFFF) == (argb.toInt() and 0xFFFFFF)
                        androidx.compose.foundation.layout.Box(
                            Modifier.size(44.dp).clip(CircleShape).background(androidx.compose.ui.graphics.Color(argb.toInt()))
                                .clickable { onPick(argb.toInt()) },
                            contentAlignment = Alignment.Center
                        ) { if (selected) androidx.compose.material3.Icon(androidx.compose.material.icons.Icons.Default.Check, contentDescription = null, tint = androidx.compose.ui.graphics.Color.White) }
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = { androidx.compose.material3.TextButton(onClick = onDismiss) { Text(stringResource(android.R.string.cancel)) } }
    )
}
