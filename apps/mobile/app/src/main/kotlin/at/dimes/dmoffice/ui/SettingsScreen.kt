package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Arrangement
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
import androidx.compose.material.icons.outlined.CloudUpload
import androidx.compose.material.icons.outlined.Email
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Palette
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.work.WorkInfo
import androidx.work.WorkManager
import at.dimes.dmoffice.App
import at.dimes.dmoffice.notify.MailNotify
import at.dimes.dmoffice.notify.MailNotifyPrefs
import at.dimes.dmoffice.notify.MailNotifyRules
import at.dimes.dmoffice.BuildConfig
import at.dimes.dmoffice.R
import at.dimes.dmoffice.sync.SyncWorker
import at.dimes.dmoffice.api.E2e
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

@Composable
@OptIn(androidx.compose.material3.ExperimentalMaterial3Api::class, androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
fun SettingsScreen(onSignedOut: () -> Unit, onClose: (() -> Unit)? = null, onNavigate: ((String) -> Unit)? = null) {
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
    androidx.activity.compose.BackHandler(enabled = page != null) { page = if (page?.startsWith("mailacc:") == true) "mail" else null }
    val hasMail by app.mail.hasAccounts.collectAsState()
    val mailAcc = page?.removePrefix("mailacc:")?.takeIf { page?.startsWith("mailacc:") == true }
    val pageTitle = when (page) {
        "account" -> R.string.settings_account; "calendar" -> R.string.settings_calendar; "mail" -> R.string.settings_mail
        "look" -> R.string.settings_look; "android" -> R.string.android_section; "security" -> R.string.security_section; "about" -> R.string.settings_about; else -> R.string.settings
    }

    // Als Reiter (onClose == null): ohne Zurueck-Pfeil, Systemleisten beruecksichtigt schon der Hauptbildschirm
    val insets = if (onClose == null) androidx.compose.foundation.layout.WindowInsets(0) else null
    // Zurueck aus einem Unterbereich auch mit der System-Zurueck-Geste
    androidx.compose.material3.Scaffold(contentWindowInsets = insets ?: androidx.compose.material3.ScaffoldDefaults.contentWindowInsets, topBar = {
        androidx.compose.material3.TopAppBar(title = { Text(if (mailAcc != null) MailNotifyPrefs.get(ctx).known().firstOrNull { it.id == mailAcc }?.label ?: mailAcc else stringResource(pageTitle)) }, windowInsets = insets ?: androidx.compose.material3.TopAppBarDefaults.windowInsets,
            navigationIcon = {
                if (page != null || onClose != null) androidx.compose.material3.IconButton(onClick = { if (mailAcc != null) page = "mail" else if (page != null) page = null else onClose?.invoke() }) { androidx.compose.material3.Icon(androidx.compose.material.icons.Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) }
            })
    }) { pad ->
    Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        when (page) {
            null -> {
                // Uebersicht: Bereiche wie in den Android-Einstellungen, darunter Abmelden und Version
                SettingsEntry(Icons.Outlined.AccountCircle, stringResource(R.string.settings_account), stringResource(R.string.settings_account_sub)) { page = "account" }
                SettingsEntry(Icons.Outlined.CalendarMonth, stringResource(R.string.settings_calendar), stringResource(R.string.settings_calendar_sub)) { page = "calendar" }
                if (hasMail) SettingsEntry(Icons.Outlined.Email, stringResource(R.string.settings_mail), stringResource(R.string.settings_mail_sub)) { page = "mail" }
                SettingsEntry(Icons.Outlined.Palette, stringResource(R.string.settings_look), stringResource(R.string.settings_look_sub)) { page = "look" }
                if (onNavigate != null) SettingsEntry(Icons.Outlined.CloudUpload, stringResource(R.string.au_title), stringResource(R.string.au_settings_sub)) { onNavigate("autoupload") }
                SettingsEntry(Icons.Outlined.PhoneAndroid, stringResource(R.string.android_section), stringResource(R.string.settings_android_sub)) { page = "android" }
                SettingsEntry(Icons.Outlined.Lock, stringResource(R.string.security_section), stringResource(R.string.settings_security_sub)) { page = "security" }
                SettingsEntry(Icons.Outlined.Info, stringResource(R.string.settings_about), stringResource(R.string.settings_about_sub)) { page = "about" }
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
                SectionHeader(stringResource(R.string.mail_accounts_notify))
                val notifyPrefs = MailNotifyPrefs.get(ctx)
                val rev by notifyPrefs.revision.collectAsState()
                val known = remember(rev) { notifyPrefs.known() }
                if (known.isEmpty()) Text(stringResource(R.string.mail_account_missing), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                for (a in known) {
                    val cfg = remember(rev, settings.mailNotify, settings.mailPreview, settings.mailLockscreen) { MailNotify.config(ctx, a.id) }
                    ListItem(modifier = Modifier.clickable { page = "mailacc:" + a.id },
                        headlineContent = { Text(a.label) },
                        supportingContent = { Text(listOfNotNull(a.email.takeIf { it.isNotBlank() && it != a.label }, ctx.getString(modeLabel(cfg.mode))).joinToString(" · "), maxLines = 1) })
                }
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                SectionHeader(stringResource(R.string.mail_account_default))
                Text(stringResource(R.string.mail_account_default_hint), modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)) {
                    Picker(stringResource(R.string.mail_notify_when), settings.mailNotify, MailNotifyRules.MODES,
                        { ctx.getString(modeLabel(it)) },
                        { app.prefs.setMailNotify(it) })
                    if (settings.mailNotify != MailNotifyRules.OFF) Picker(stringResource(R.string.mail_lockscreen), settings.mailLockscreen, listOf("show", "hide"),
                        { ctx.getString(if (it == "hide") R.string.mail_lock_hide else R.string.mail_lock_show) }, { app.prefs.setMailLockscreen(it) })
                }
                if (settings.mailNotify != MailNotifyRules.OFF) ListItem(headlineContent = { Text(stringResource(R.string.mail_preview)) },
                    trailingContent = { Switch(checked = settings.mailPreview, onCheckedChange = { app.prefs.setMailPreview(it) }) })
                Text(stringResource(R.string.mail_notify_hint), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

                // Lesen: grosse Bilder in der Mail erst auf Tipp (Anhaenge ohnehin nur auf Tipp)
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                SectionHeader(stringResource(R.string.mail_reading))
                Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) {
                    Picker(stringResource(R.string.mail_image_limit), settings.mailImageMb, listOf(1, 5, 10, 25, 0),
                        { if (it == 0) ctx.getString(R.string.mail_image_all) else "$it MB" }, { app.prefs.setMailImageMb(it) })
                }
                Text(stringResource(R.string.mail_image_hint), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

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
                    if (r.values.all { it }) { app.prefs.setAndroidCalendar(true); scope.launch { withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.AndroidAccount.update(ctx); at.dimes.dmoffice.sync.ProviderSync.syncCalendars(ctx) } } }
                    else android.widget.Toast.makeText(ctx, ctx.getString(R.string.permission_needed), android.widget.Toast.LENGTH_SHORT).show()
                }
                val conPerm = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions()) { r ->
                    if (r.values.all { it }) { app.prefs.setAndroidContacts(true); scope.launch { withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.AndroidAccount.update(ctx); at.dimes.dmoffice.sync.ProviderContacts.sync(ctx) } } }
                    else android.widget.Toast.makeText(ctx, ctx.getString(R.string.permission_needed), android.widget.Toast.LENGTH_SHORT).show()
                }
                ListItem(headlineContent = { Text(stringResource(R.string.android_calendar)) },
                    trailingContent = { Switch(checked = settings.androidCalendar, onCheckedChange = { on ->
                        if (on) calPerm.launch(arrayOf(android.Manifest.permission.READ_CALENDAR, android.Manifest.permission.WRITE_CALENDAR))
                        else { app.prefs.setAndroidCalendar(false); scope.launch { withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.ProviderSync.removeCalendars(ctx); at.dimes.dmoffice.sync.AndroidAccount.update(ctx) } } }
                    }) })
                ListItem(headlineContent = { Text(stringResource(R.string.android_contacts)) },
                    trailingContent = { Switch(checked = settings.androidContacts, onCheckedChange = { on ->
                        if (on) conPerm.launch(arrayOf(android.Manifest.permission.READ_CONTACTS, android.Manifest.permission.WRITE_CONTACTS))
                        else { app.prefs.setAndroidContacts(false); scope.launch { withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.ProviderContacts.remove(ctx); at.dimes.dmoffice.sync.AndroidAccount.update(ctx) } } }
                    }) })
                Text(stringResource(R.string.android_plaintext), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

            }
            "security" -> {
                SectionHeader(stringResource(R.string.security_section))
                ListItem(headlineContent = { Text(stringResource(R.string.app_lock)) }, supportingContent = { Text(stringResource(R.string.app_lock_desc)) },
                    trailingContent = { Switch(checked = settings.appLock, onCheckedChange = { app.prefs.setAppLock(it) }) })
                Text(stringResource(if (account?.encrypted == true) R.string.e2e_on else R.string.e2e_off), modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp), style = MaterialTheme.typography.bodySmall,
                    color = if (account?.encrypted == true) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error)
                // Fingerabdruck zum Vergleich mit dmOffice (Einstellungen -> Geraete & Sync, Server-Log beim Start)
                val key = account?.serverKey?.takeIf { account?.encrypted == true }
                if (key != null) ListItem(headlineContent = { Text(stringResource(R.string.key_fingerprint)) },
                    supportingContent = { Text(E2e.fingerprint(key), fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace) })
                // Server bietet den Umschlag an, der Schluessel kam aber nicht aus dem QR-Code: erst nach Vergleich umstellen
                val offered by app.pendingServerKey.collectAsState()
                offered?.takeIf { account?.encrypted != true }?.let { k ->
                    var upgrading by remember { mutableStateOf(false) }
                    var upgradeError by remember { mutableStateOf<String?>(null) }
                    Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(stringResource(R.string.key_upgrade_text), style = MaterialTheme.typography.bodyMedium)
                        Text(E2e.fingerprint(k), style = MaterialTheme.typography.titleMedium, fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace, color = MaterialTheme.colorScheme.primary)
                        Text(stringResource(R.string.key_confirm_hint), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        Button(enabled = !upgrading, onClick = {
                            upgrading = true; upgradeError = null
                            scope.launch {
                                try { withContext(Dispatchers.IO) { app.confirmServerKey(k) } } catch (e: Exception) { upgradeError = e.message ?: e.toString() }
                                upgrading = false
                            }
                        }) { Text(stringResource(R.string.key_confirm_ok)) }
                        upgradeError?.let { Text(stringResource(R.string.error_generic, it), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
                    }
                }
                Text(stringResource(R.string.db_encrypted), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)

            }
            "about" -> {
                // Version und Updates von GitHub-Releases (die App kommt nicht aus einem App-Store)
                val update by at.dimes.dmoffice.update.Updater.state.collectAsState()
                SectionHeader(stringResource(R.string.settings_about))
                ListItem(headlineContent = { Text(stringResource(R.string.app_name)) },
                    supportingContent = { Text(stringResource(R.string.version, BuildConfig.VERSION_NAME) + " (" + BuildConfig.VERSION_CODE + ")") })
                if (at.dimes.dmoffice.update.Updater.isDevBuild) {
                    Text(stringResource(R.string.update_dev_build), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                } else {
                    ListItem(headlineContent = { Text(stringResource(R.string.update_auto)) }, supportingContent = { Text(stringResource(R.string.update_auto_desc)) },
                        trailingContent = { Switch(checked = settings.autoUpdate, onCheckedChange = { app.prefs.setAutoUpdate(it); at.dimes.dmoffice.update.UpdateWorker.schedule(ctx) }) })
                }
                val checking = update is at.dimes.dmoffice.update.Updater.State.Checking
                Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Button(onClick = { at.dimes.dmoffice.update.Updater.checkNow(ctx) }, enabled = !checking) { Text(stringResource(if (checking) R.string.update_checking else R.string.update_check)) }
                }
                when (val u = update) {
                    is at.dimes.dmoffice.update.Updater.State.UpToDate -> Text(
                        if (at.dimes.dmoffice.update.Updater.isDevBuild) stringResource(R.string.update_latest, u.latest ?: "?") else stringResource(R.string.update_up_to_date),
                        modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall)
                    is at.dimes.dmoffice.update.Updater.State.Available -> Row(Modifier.padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(stringResource(R.string.update_available_text, u.release.versionName, BuildConfig.VERSION_NAME), style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f))
                        TextButton(onClick = { at.dimes.dmoffice.update.Updater.dialog.value = true }) { Text(stringResource(R.string.update_now)) }
                    }
                    is at.dimes.dmoffice.update.Updater.State.Downloading, is at.dimes.dmoffice.update.Updater.State.NeedsPermission, is at.dimes.dmoffice.update.Updater.State.Installing ->
                        TextButton(onClick = { at.dimes.dmoffice.update.Updater.dialog.value = true }, modifier = Modifier.padding(horizontal = 8.dp)) { Text(stringResource(R.string.update_available_title)) }
                    is at.dimes.dmoffice.update.Updater.State.Failed -> Text(u.message, modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.error)
                    else -> {}
                }

            }
            else -> if (mailAcc != null) {
                // Benachrichtigungen dieses Mailkontos: eigene Werte, sonst die Vorgabe
                val notifyPrefs = MailNotifyPrefs.get(ctx)
                val rev by notifyPrefs.revision.collectAsState()
                val cfg = remember(rev, settings.mailNotify, settings.mailPreview, settings.mailLockscreen) { MailNotify.config(ctx, mailAcc) }
                SectionHeader(stringResource(R.string.mail_notifications))
                Column(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)) {
                    Picker(stringResource(R.string.mail_notify_when), cfg.mode, MailNotifyRules.MODES, { ctx.getString(modeLabel(it)) }, { notifyPrefs.setMode(mailAcc, it) })
                    if (cfg.mode != MailNotifyRules.OFF) Picker(stringResource(R.string.mail_lockscreen), if (cfg.hideOnLock) "hide" else "show", listOf("show", "hide"),
                        { ctx.getString(if (it == "hide") R.string.mail_lock_hide else R.string.mail_lock_show) }, { notifyPrefs.setHideOnLock(mailAcc, it == "hide") })
                }
                if (cfg.mode != MailNotifyRules.OFF) {
                ListItem(headlineContent = { Text(stringResource(R.string.mail_preview)) },
                    trailingContent = { Switch(checked = cfg.preview, onCheckedChange = { notifyPrefs.setPreview(mailAcc, it) }) })
                ListItem(modifier = Modifier.clickable { try { ctx.startActivity(MailNotify.channelSettings(ctx, mailAcc)) } catch (_: Exception) { } },
                    headlineContent = { Text(stringResource(R.string.mail_sound_channel)) },
                    supportingContent = { Text(stringResource(R.string.mail_sound_channel_sub)) })
                }
                Text(stringResource(R.string.mail_notify_hint), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
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
                    withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.ProviderSync.removeCalendars(ctx); at.dimes.dmoffice.sync.ProviderContacts.remove(ctx); at.dimes.dmoffice.sync.AndroidAccount.removeAll(ctx); app.db.clearAll() }
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
private fun modeLabel(mode: String) = when (mode) {
    MailNotifyRules.VIP -> R.string.mail_notify_vip; MailNotifyRules.RELEVANT -> R.string.mail_notify_relevant
    MailNotifyRules.OFF -> R.string.mail_notify_off; else -> R.string.mail_notify_all
}

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
