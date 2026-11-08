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
import androidx.compose.material.icons.automirrored.outlined.Chat
import androidx.compose.material.icons.outlined.CalendarMonth
import androidx.compose.material.icons.outlined.CloudUpload
import androidx.compose.material.icons.outlined.Email
import androidx.compose.material.icons.outlined.Info
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Notifications
import androidx.compose.material.icons.outlined.Palette
import androidx.compose.material.icons.outlined.Reorder
import androidx.compose.material.icons.outlined.PhotoLibrary
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.OutlinedTextField
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import kotlinx.coroutines.Dispatchers
import kotlinx.serialization.json.booleanOrNull
import kotlinx.serialization.json.intOrNull
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
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
    var deleteOpen by remember { mutableStateOf(false) }
    var deleteText by remember { mutableStateOf("") }
    var deleteTwoFactor by remember { mutableStateOf(false) }
    var deleteBlocked by remember { mutableStateOf(false) }
    var deletePassword by remember { mutableStateOf("") }
    var deleteCode by remember { mutableStateOf("") }
    var deleteError by remember { mutableStateOf<String?>(null) }
    var deleteBusy by remember { mutableStateOf(false) }
    val shareWarning = stringResource(R.string.delete_account_shares)
    val deleteFailed = stringResource(R.string.delete_account_failed)
    // Konto vom Gerät entfernen: alle lokalen Daten, danach zur Anmeldung
    val removeLocal: suspend () -> Unit = {
        SyncWorker.cancel(ctx)
        withContext(Dispatchers.IO) { at.dimes.dmoffice.sync.ProviderSync.removeCalendars(ctx); at.dimes.dmoffice.sync.ProviderContacts.remove(ctx); at.dimes.dmoffice.sync.AndroidAccount.removeAll(ctx); app.db.clearAll(); app.chat.cache.clear() }
        app.prefs.setAccount(null)
        app.prefs.clearSettings()
        at.dimes.dmoffice.widget.Widgets.updateAll(ctx)
        onSignedOut()
    }
    var colorFor by remember { mutableStateOf<at.dimes.dmoffice.data.CalendarEntity?>(null) }
    // Bereich der Optionen (null = Uebersicht); Zurueck fuehrt erst zur Uebersicht
    var page by androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf<String?>(null) }
    var tabBarDialog by remember { mutableStateOf(false) }
    if (tabBarDialog) TabBarDialog { tabBarDialog = false }
    // Aus „Benachrichtigungen“ geöffnete Seiten führen dorthin zurück
    var fromNotify by androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf(false) }
    fun back() {
        page = when {
            page?.startsWith("mailacc:") == true -> "mail"
            page == "openpgp" -> "mail"
            page == "oss" -> "about"
            fromNotify && page != "notify" -> "notify"
            else -> null
        }
        if (page == "notify" || page == null) fromNotify = false
    }
    androidx.activity.compose.BackHandler(enabled = page != null) { back() }
    val hasMail by app.mail.hasAccounts.collectAsState()
    val mailAcc = page?.removePrefix("mailacc:")?.takeIf { page?.startsWith("mailacc:") == true }
    val pageTitle = when (page) {
        "account" -> R.string.settings_account; "calendar" -> R.string.settings_calendar; "mail" -> R.string.settings_mail
        "look" -> R.string.settings_look; "files" -> R.string.settings_files; "chat" -> R.string.chat; "notify" -> R.string.notify_title; "security" -> R.string.security_section; "tresor" -> R.string.tresor; "openpgp" -> R.string.pgp_title; "about" -> R.string.settings_about; "oss" -> R.string.oss_title; else -> R.string.settings
    }

    // Als Reiter (onClose == null): ohne Zurueck-Pfeil, Systemleisten beruecksichtigt schon der Hauptbildschirm
    val insets = if (onClose == null) androidx.compose.foundation.layout.WindowInsets(0) else null
    // Zurueck aus einem Unterbereich auch mit der System-Zurueck-Geste
    androidx.compose.material3.Scaffold(contentWindowInsets = insets ?: androidx.compose.material3.ScaffoldDefaults.contentWindowInsets, topBar = {
        androidx.compose.material3.TopAppBar(title = { Text(if (mailAcc != null) MailNotifyPrefs.get(ctx).known().firstOrNull { it.id == mailAcc }?.label ?: mailAcc else stringResource(pageTitle)) }, windowInsets = insets ?: androidx.compose.material3.TopAppBarDefaults.windowInsets,
            navigationIcon = {
                if (page != null || onClose != null) androidx.compose.material3.IconButton(onClick = { if (page != null) back() else onClose?.invoke() }) { androidx.compose.material3.Icon(androidx.compose.material.icons.Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) }
            })
    }) { pad ->
    Column(Modifier.padding(pad).fillMaxSize().verticalScroll(rememberScrollState()).padding(bottom = 24.dp)) {
        when (page) {
            null -> {
                // Uebersicht: Bereiche wie in den Android-Einstellungen, darunter Abmelden und Version
                // Reihenfolge: Konto, Darstellung, Sicherheit - dann die Bereiche mit eigenen Einstellungen - dann Über
                SettingsEntry(Icons.Outlined.AccountCircle, stringResource(R.string.settings_account), stringResource(R.string.settings_account_sub)) { page = "account" }
                SettingsEntry(Icons.Outlined.Palette, stringResource(R.string.settings_look), stringResource(R.string.settings_look_sub)) { page = "look" }
                SettingsEntry(Icons.Outlined.Lock, stringResource(R.string.security_section), stringResource(R.string.settings_security_sub)) { page = "security" }
                SettingsEntry(Icons.Outlined.Notifications, stringResource(R.string.notify_title), stringResource(R.string.notify_sub)) { page = "notify" }
                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                SettingsEntry(Icons.Outlined.CalendarMonth, stringResource(R.string.settings_calendar), stringResource(R.string.settings_calendar_sub)) { page = "calendar" }
                if (hasMail) SettingsEntry(Icons.Outlined.Email, stringResource(R.string.settings_mail), stringResource(R.string.settings_mail_sub)) { page = "mail" }
                SettingsEntry(Icons.AutoMirrored.Outlined.Chat, stringResource(R.string.chat), stringResource(R.string.chat_settings_sub)) { page = "chat" }
                if (!at.dimes.dmoffice.data.Features.filesOff.value) SettingsEntry(Icons.Outlined.PhotoLibrary, stringResource(R.string.settings_files), stringResource(R.string.settings_files_sub)) { page = "files" }
                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                SettingsEntry(Icons.Outlined.Info, stringResource(R.string.settings_about), stringResource(R.string.settings_about_sub)) { page = "about" }
        HorizontalDivider(Modifier.padding(vertical = 12.dp))
                Row(Modifier.padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    // Neues App-Passwort, ohne abzumelden (Einstellungen und Auto-Upload bleiben)
                    if (onNavigate != null) OutlinedButton(onClick = { onNavigate("reconnect") }) { Text(stringResource(R.string.reconnect)) }
                    OutlinedButton(onClick = { confirmRemove = true }) { Text(stringResource(R.string.sign_out)) }
                }
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
                OutlinedButton(
                    modifier = Modifier.padding(horizontal = 16.dp, vertical = 6.dp),
                    onClick = {
                        deletePassword = ""; deleteCode = ""; deleteError = null; deleteText = ""; deleteBlocked = false; deleteOpen = true
                        scope.launch {
                            try {
                                val a = app.api ?: return@launch
                                val info = withContext(Dispatchers.IO) { a.json.parseToJsonElement(a.getText(a.url("/api/auth/delete-account/impact"))).jsonObject }
                                deleteTwoFactor = info["twoFactor"]?.jsonPrimitive?.booleanOrNull == true
                                deleteBlocked = info["lastAdmin"]?.jsonPrimitive?.booleanOrNull == true
                                val shares = info["shares"]?.jsonPrimitive?.intOrNull ?: 0
                                val names = info["members"]?.jsonArray?.joinToString(", ") { it.jsonPrimitive.content } ?: ""
                                deleteText = if (shares > 0) shareWarning.format(shares, names) else ""
                            } catch (e: java.io.IOException) { deleteError = e.message ?: deleteFailed }
                        }
                    },
                ) { Text(stringResource(R.string.delete_account)) }
                if (onNavigate != null) {
                    HorizontalDivider(Modifier.padding(vertical = 12.dp))
                    SettingsEntry(Icons.Outlined.CloudUpload, stringResource(R.string.au_title), stringResource(R.string.au_settings_sub)) { onNavigate("autoupload") }
                }

            }
            "calendar" -> {
                // Abschnitte wie im Web: eigene, je Microsoft-Konto, abonniert
                val infos by at.dimes.dmoffice.data.ListTypes.byUri.collectAsState()
                androidx.compose.runtime.LaunchedEffect(Unit) { at.dimes.dmoffice.data.ListTypes.refresh(ctx) }
                val groups = cals.groupBy { at.dimes.dmoffice.data.ListTypes.sectionOf(infos[at.dimes.dmoffice.data.ListTypes.uriOf(it.href)]) }.toList().sortedWith(compareBy({ at.dimes.dmoffice.data.ListTypes.sectionOrder(it.first) }, { it.first }))
                for ((key, list) in groups) {
                SectionHeader(when { key.startsWith("g:") -> stringResource(R.string.outlook_section, key.removePrefix("g:")); key == "s" -> stringResource(R.string.subscribed); else -> stringResource(R.string.calendars) })
                if (key.isEmpty()) Text(stringResource(R.string.tap_to_change_color), modifier = Modifier.padding(horizontal = 16.dp),
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                for (c in list) ListItem(
                    modifier = Modifier.clickable { colorFor = c },
                    headlineContent = { Text(c.name) },
                    supportingContent = {
                        val err = infos[at.dimes.dmoffice.data.ListTypes.uriOf(c.href)]?.graphError
                        if (err != null) Text(stringResource(R.string.graph_error, err), color = MaterialTheme.colorScheme.error)
                        else Text(listOfNotNull(if (c.supportsEvents) stringResource(R.string.event) else null, if (c.supportsTasks) stringResource(R.string.tasks) else null).joinToString(" · "))
                    },
                    leadingContent = { androidx.compose.foundation.Canvas(Modifier.padding(4.dp).height(16.dp).fillMaxWidth(0.06f)) { drawCircle(colorOf(c.color), radius = 8.dp.toPx()) } },
                    trailingContent = { Checkbox(checked = c.visible, onCheckedChange = { v -> scope.launch { app.repo.setCalendarVisible(c.href, v) } }) }
                )
                }

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

                // Kalender und Kontakte in anderen Apps (Android-Konto)
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
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

                // OpenPGP: eigene Schlüssel, Signieren, Schlüssel anderer (ui/OpenPgp.kt)
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                ListItem(modifier = Modifier.clickable { page = "openpgp" }, headlineContent = { Text(stringResource(R.string.pgp_title)) },
                    supportingContent = { Text(stringResource(R.string.pgp_entry_sub)) })
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
                // Hell, dunkel oder wie das Handy
                SectionHeader(stringResource(R.string.theme_mode))
                val themeMode by app.prefs.themeMode.collectAsState()
                androidx.compose.foundation.layout.Row(Modifier.padding(horizontal = 16.dp, vertical = 4.dp), horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)) {
                    for ((k, l) in listOf("system" to R.string.theme_system, "light" to R.string.theme_light, "dark" to R.string.theme_dark))
                        androidx.compose.material3.FilterChip(selected = themeMode == k, onClick = { app.prefs.setThemeMode(k) }, label = { Text(stringResource(l)) })
                }
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

                // Leiste unten: Symbolgröße (die Höhe folgt) und Bereiche anordnen
                HorizontalDivider(Modifier.padding(vertical = 12.dp))
                SectionHeader(stringResource(R.string.tabbar_title))
                TabBarSizePicker(Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
                SettingsEntry(Icons.Outlined.Reorder, stringResource(R.string.tabbar_arrange), stringResource(R.string.tabbar_sub)) { tabBarDialog = true }

            }
            "files" -> {
                // Teilen von Fotos aus Dateien und Galerie: Original oder verkleinert (lange drücken im Betrachter: jedes Mal fragen)
                SectionHeader(stringResource(R.string.share_quality))
                var shareSmall by remember { mutableStateOf(app.prefs.shareSmall) }
                Text(stringResource(R.string.share_quality_hint), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                for ((small, label) in listOf(false to R.string.share_original, true to R.string.share_small))
                    ListItem(headlineContent = { Text(stringResource(label)) }, modifier = Modifier.clickable { shareSmall = small; app.prefs.shareSmall = small },
                        trailingContent = { androidx.compose.material3.RadioButton(selected = shareSmall == small, onClick = { shareSmall = small; app.prefs.shareSmall = small }) })
            }
            "chat" -> ChatSettingsContent()
            "notify" -> {
                // Erlaubnis von Android; ohne sie zeigt keine Benachrichtigung etwas
                val lifecycle = androidx.lifecycle.compose.LocalLifecycleOwner.current
                var allowed by remember { mutableStateOf(androidx.core.app.NotificationManagerCompat.from(ctx).areNotificationsEnabled()) }
                androidx.compose.runtime.DisposableEffect(lifecycle) {
                    val o = androidx.lifecycle.LifecycleEventObserver { _, e -> if (e == androidx.lifecycle.Lifecycle.Event.ON_RESUME) allowed = androidx.core.app.NotificationManagerCompat.from(ctx).areNotificationsEnabled() }
                    lifecycle.lifecycle.addObserver(o)
                    onDispose { lifecycle.lifecycle.removeObserver(o) }
                }
                val system = {
                    try { ctx.startActivity(android.content.Intent(android.provider.Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(android.provider.Settings.EXTRA_APP_PACKAGE, ctx.packageName)) } catch (_: Exception) { }
                }
                if (!allowed) Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(stringResource(R.string.notify_off), color = MaterialTheme.colorScheme.error)
                    Button(onClick = system) { Text(stringResource(R.string.notify_allow)) }
                }
                if (hasMail) SettingsEntry(Icons.Outlined.Email, stringResource(R.string.settings_mail), stringResource(R.string.notify_mail_sub)) { fromNotify = true; page = "mail" }
                SettingsEntry(Icons.AutoMirrored.Outlined.Chat, stringResource(R.string.chat), stringResource(R.string.notify_chat_sub)) { fromNotify = true; page = "chat" }
                SettingsEntry(Icons.Outlined.CalendarMonth, stringResource(R.string.settings_calendar), stringResource(R.string.notify_cal_sub)) { fromNotify = true; page = "calendar" }
                if (at.dimes.dmoffice.data.Features.on(at.dimes.dmoffice.data.Features.COACH)) Text(stringResource(R.string.notify_coach_hint), modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                SettingsEntry(Icons.Outlined.Notifications, stringResource(R.string.notify_system), stringResource(R.string.notify_system_sub), system)
            }
            "tresor" -> TresorSettingsScreen()
            "openpgp" -> OpenPgpSettingsContent()
            "security" -> {
                SectionHeader(stringResource(R.string.security_section))
                ListItem(headlineContent = { Text(stringResource(R.string.app_lock)) }, supportingContent = { Text(stringResource(R.string.app_lock_desc)) },
                    trailingContent = { Switch(checked = settings.appLock, onCheckedChange = { app.prefs.setAppLock(it) }) })
                if (at.dimes.dmoffice.data.Features.on("vault")) SettingsEntry(Icons.Outlined.Lock, stringResource(R.string.tresor), stringResource(R.string.tresor_entry_sub)) { page = "tresor" }
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
                SecurityLogList()

                // Datenschutz: was Coach weitergibt (dieselben Schalter wie in Coach → Profil)
                if (at.dimes.dmoffice.data.Features.on(at.dimes.dmoffice.data.Features.COACH)) CoachPrivacy()

            }
            "about" -> {
                // Version und Updates von GitHub-Releases (die App kommt nicht aus einem App-Store)
                val update by at.dimes.dmoffice.update.Updater.state.collectAsState()
                SectionHeader(stringResource(R.string.settings_about))
                ListItem(headlineContent = { Text(stringResource(R.string.app_name)) },
                    supportingContent = { Text(stringResource(R.string.version, BuildConfig.VERSION_NAME) + " (" + BuildConfig.VERSION_CODE + ")") })
                if (at.dimes.dmoffice.update.Updater.isPlay) {
                    // Updates kommen aus Google Play
                } else if (at.dimes.dmoffice.update.Updater.isDevBuild) {
                    Text(stringResource(R.string.update_dev_build), modifier = Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                } else {
                    ListItem(headlineContent = { Text(stringResource(R.string.update_auto)) }, supportingContent = { Text(stringResource(R.string.update_auto_desc)) },
                        trailingContent = { Switch(checked = settings.autoUpdate, onCheckedChange = { app.prefs.setAutoUpdate(it); at.dimes.dmoffice.update.UpdateWorker.schedule(ctx) }) })
                }
                val checking = update is at.dimes.dmoffice.update.Updater.State.Checking
                if (!at.dimes.dmoffice.update.Updater.isPlay) Row(Modifier.padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
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
                HorizontalDivider(Modifier.padding(vertical = 8.dp))
                // Fremdbestandteile mit Lizenz (assets/third-party.json)
                SettingsEntry(Icons.Outlined.Info, stringResource(R.string.oss_title), stringResource(R.string.oss_sub)) { page = "oss" }
            }
            "oss" -> OpenSourceList()
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
                scope.launch { removeLocal() }
            }) { Text(stringResource(R.string.sign_out)) }
        },
        dismissButton = { TextButton(onClick = { confirmRemove = false }) { Text(stringResource(R.string.cancel)) } }
    )

    if (deleteOpen) AlertDialog(
        onDismissRequest = { if (!deleteBusy) deleteOpen = false },
        title = { Text(stringResource(R.string.delete_account)) },
        text = {
            Column(Modifier.verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (deleteBlocked) Text(stringResource(R.string.delete_account_last_admin))
                else {
                    Text(stringResource(R.string.delete_account_text))
                    if (deleteText.isNotEmpty()) Text(deleteText)
                    OutlinedTextField(value = deletePassword, onValueChange = { deletePassword = it }, singleLine = true, label = { Text(stringResource(R.string.delete_account_password)) },
                        visualTransformation = PasswordVisualTransformation(), keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password), modifier = Modifier.fillMaxWidth())
                    if (deleteTwoFactor) OutlinedTextField(value = deleteCode, onValueChange = { deleteCode = it }, singleLine = true, label = { Text(stringResource(R.string.delete_account_code)) },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number), modifier = Modifier.fillMaxWidth())
                }
                deleteError?.let { Text(it, color = MaterialTheme.colorScheme.error) }
            }
        },
        confirmButton = {
            if (!deleteBlocked) TextButton(enabled = !deleteBusy && deletePassword.isNotEmpty(), onClick = {
                deleteBusy = true; deleteError = null
                scope.launch {
                    try {
                        val a = app.api ?: return@launch
                        val body = kotlinx.serialization.json.buildJsonObject { put("password", kotlinx.serialization.json.JsonPrimitive(deletePassword)); put("code", kotlinx.serialization.json.JsonPrimitive(deleteCode)) }.toString()
                        withContext(Dispatchers.IO) { a.postText(a.url("/api/auth/delete-account"), body) }
                        deleteOpen = false
                        removeLocal()
                    } catch (e: java.io.IOException) { deleteError = e.message ?: deleteFailed }
                    finally { deleteBusy = false }
                }
            }) { Text(stringResource(R.string.delete_account_confirm)) }
        },
        dismissButton = { TextButton(enabled = !deleteBusy, onClick = { deleteOpen = false }) { Text(stringResource(R.string.cancel)) } }
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
        supportingContent = { Text(sub) },
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

/** Datenschutz-Schalter des Coach (KI-Nutzung der eigenen Werte, Standort fürs Wetter) - ohne Coach-Profil nichts. */
@Composable
private fun CoachPrivacy() {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = rememberCoroutineScope()
    var profile by remember { mutableStateOf<at.dimes.dmoffice.coach.Profile?>(null) }
    androidx.compose.runtime.LaunchedEffect(Unit) { profile = try { app.coach.profile().profile } catch (_: Exception) { null } }
    val p = profile ?: return
    var loc by remember { mutableStateOf(at.dimes.dmoffice.coach.CoachLocation.enabled(ctx) && at.dimes.dmoffice.coach.CoachLocation.permitted(ctx)) }
    val ask = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.RequestPermission()) { granted ->
        at.dimes.dmoffice.coach.CoachLocation.setEnabled(ctx, granted); loc = granted
        if (granted) scope.launch { at.dimes.dmoffice.coach.CoachLocation.sendIfWanted(ctx, force = true) }
    }
    HorizontalDivider(Modifier.padding(vertical = 12.dp))
    SectionHeader(stringResource(R.string.privacy_section))
    ListItem(headlineContent = { Text(stringResource(R.string.coach_ai_data)) }, supportingContent = { Text(stringResource(R.string.coach_ai_data_hint)) },
        trailingContent = { Switch(checked = p.aiData, onCheckedChange = { on ->
            profile = p.copy(aiData = on)
            scope.launch { try { profile = app.coach.saveProfile(p.copy(aiData = on)).profile } catch (e: Exception) { profile = p; android.widget.Toast.makeText(ctx, e.message, android.widget.Toast.LENGTH_SHORT).show() } }
        }) })
    ListItem(headlineContent = { Text(stringResource(R.string.coach_location)) }, supportingContent = { Text(stringResource(R.string.coach_location_hint)) },
        trailingContent = { Switch(checked = loc, onCheckedChange = { on ->
            if (!on) { at.dimes.dmoffice.coach.CoachLocation.setEnabled(ctx, false); loc = false }
            else if (at.dimes.dmoffice.coach.CoachLocation.permitted(ctx)) { at.dimes.dmoffice.coach.CoachLocation.setEnabled(ctx, true); loc = true; scope.launch { at.dimes.dmoffice.coach.CoachLocation.sendIfWanted(ctx, force = true) } }
            else ask.launch(android.Manifest.permission.ACCESS_COARSE_LOCATION)
        }) })
}
