package at.dimes.dmoffice.ui

import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.Forward
import androidx.compose.material.icons.automirrored.filled.ReplyAll
import androidx.compose.material.icons.filled.Archive
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Flag
import androidx.compose.material.icons.filled.Label
import androidx.compose.material.icons.filled.MarkEmailUnread
import androidx.compose.material.icons.filled.Report
import androidx.compose.material.icons.filled.Schedule
import androidx.compose.material.icons.filled.SwapHoriz
import androidx.compose.material.icons.filled.Undo
import androidx.compose.material.icons.filled.VerifiedUser
import androidx.compose.material.icons.outlined.Flag
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.MailCategory
import java.time.DayOfWeek
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter
import java.time.format.TextStyle
import java.time.temporal.TemporalAdjusters
import java.util.Locale

/** Faelligkeiten fuer "Kennzeichnen" wie im Web (Outlook): Heute, Morgen, Diese Woche (Freitag), Naechste Woche (Montag), ohne Datum. */
fun followUpOptions(ctx: Context): List<Triple<String, String?, String>> {
    val today = LocalDate.now()
    val fmt = DateTimeFormatter.ofPattern("EE dd.MM.", Locale.getDefault())
    val friday = if (today.dayOfWeek.value <= 5) today.with(TemporalAdjusters.nextOrSame(DayOfWeek.FRIDAY)) else today.with(TemporalAdjusters.next(DayOfWeek.FRIDAY))
    val monday = today.with(TemporalAdjusters.next(DayOfWeek.MONDAY))
    val list = mutableListOf<Triple<String, String?, String>>(
        Triple(ctx.getString(R.string.today), today.toString(), fmt.format(today)),
        Triple(ctx.getString(R.string.tomorrow), today.plusDays(1).toString(), fmt.format(today.plusDays(1))),
    )
    if (friday != today.plusDays(1) && friday != today) list.add(Triple(ctx.getString(R.string.this_week), friday.toString(), fmt.format(friday)))
    list.add(Triple(ctx.getString(R.string.next_week), monday.toString(), fmt.format(monday)))
    list.add(Triple(ctx.getString(R.string.no_date), null, ""))
    return list
}

/** Zeitvorschlaege fuer die Wiedervorlage wie im Web - gleiche Zeitpunkte nur einmal. */
fun snoozeOptions(ctx: Context, now: LocalDateTime = LocalDateTime.now()): List<Pair<String, LocalDateTime>> {
    val list = mutableListOf<Pair<String, LocalDateTime>>()
    fun add(label: Int, at: LocalDateTime) { if (at.isAfter(now) && list.none { it.second == at }) list.add(ctx.getString(label) to at) }
    val inHour = now.plusHours(1).withSecond(0).withNano(0).let { it.withMinute(0).plusMinutes(((it.minute + 14) / 15 * 15).toLong()) }
    add(R.string.snooze_hour, inHour)
    val later = now.plusHours(4).withMinute(0).withSecond(0).withNano(0)
    if (later.toLocalDate() == now.toLocalDate()) add(R.string.snooze_later, later)
    if (now.hour < 17) add(R.string.snooze_evening, now.toLocalDate().atTime(18, 0))
    add(R.string.tomorrow, now.toLocalDate().plusDays(1).atTime(9, 0))
    add(R.string.snooze_tomorrow_afternoon, now.toLocalDate().plusDays(1).atTime(14, 0))
    add(R.string.snooze_day_after, now.toLocalDate().plusDays(2).atTime(9, 0))
    val dow = now.dayOfWeek.value
    if (dow in 1..4) add(R.string.snooze_weekend, now.toLocalDate().with(TemporalAdjusters.next(DayOfWeek.SATURDAY)).atTime(10, 0))
    val monday = now.toLocalDate().with(TemporalAdjusters.next(DayOfWeek.MONDAY)).atTime(9, 0)
    add(R.string.next_week, if (list.any { it.second == monday }) monday.plusWeeks(1) else monday)
    add(R.string.snooze_two_weeks, now.toLocalDate().plusDays(14).atTime(9, 0))
    add(R.string.snooze_next_month, now.toLocalDate().withDayOfMonth(1).plusMonths(1).atTime(9, 0))
    return list
}

fun snoozeHint(at: LocalDateTime): String =
    at.dayOfWeek.getDisplayName(TextStyle.SHORT, Locale.getDefault()) + ", " + DateTimeFormatter.ofPattern("HH:mm").format(at)

fun parseColor(hex: String?): Color = try { Color(android.graphics.Color.parseColor(hex ?: "#0F6CBD")) } catch (_: Exception) { Color(0xFF0F6CBD) }

/** Was das Mail-Menue tun kann - wie das Menueband im Web. */
class MailMenuActions(
    val replyAll: () -> Unit, val forward: () -> Unit, val archive: () -> Unit, val junk: () -> Unit, val notJunk: () -> Unit,
    val markUnread: () -> Unit, val followUp: (due: String?) -> Unit, val followUpDone: () -> Unit,
    val category: (key: String, on: Boolean) -> Unit, val snooze: (LocalDateTime) -> Unit, val unsnooze: () -> Unit,
    /** Prio-Posteingang: nach Relevant (true) / Sonstige verschieben, always = Absender immer. */
    val moveFocus: (focused: Boolean, always: Boolean) -> Unit = { _, _ -> },
)

/**
 * Menue einer Mail mit Unterseiten wie im Web: Kennzeichnen (Faelligkeit), Kategorisieren (farbig),
 * Wiedervorlage, Archivieren, Junk, ungelesen.
 */
@Composable
fun MailMenu(
    expanded: Boolean, onDismiss: () -> Unit, flagged: Boolean, inJunk: Boolean, inSnoozed: Boolean,
    categories: List<MailCategory>, keywords: Set<String>, a: MailMenuActions,
    /** Nur im Posteingang mit Prio-Posteingang: aktuelle Einordnung (true Relevant); null = kein Menuepunkt. */
    focus: Boolean? = null,
) {
    val ctx = LocalContext.current
    var page by remember { mutableStateOf("main") }
    fun close() { page = "main"; onDismiss() }
    DropdownMenu(expanded = expanded, onDismissRequest = { close() }) {
        when (page) {
            "followup" -> {
                DropdownMenuItem(text = { Text(stringResource(R.string.flag)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.ArrowBack, null) }, onClick = { page = "main" })
                HorizontalDivider()
                for ((label, due, hint) in followUpOptions(ctx)) DropdownMenuItem(
                    text = { Text(label) }, trailingIcon = { if (hint.isNotEmpty()) Text(hint, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) },
                    onClick = { close(); a.followUp(due) })
                if (flagged) {
                    HorizontalDivider()
                    DropdownMenuItem(text = { Text(stringResource(R.string.mark_done)) }, leadingIcon = { Icon(Icons.Default.Check, null) }, onClick = { close(); a.followUpDone() })
                }
            }
            "category" -> {
                DropdownMenuItem(text = { Text(stringResource(R.string.categorize)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.ArrowBack, null) }, onClick = { page = "main" })
                HorizontalDivider()
                if (categories.isEmpty()) DropdownMenuItem(text = { Text(stringResource(R.string.no_categories)) }, onClick = { close() }, enabled = false)
                for (c in categories) {
                    val on = c.key in keywords
                    DropdownMenuItem(
                        text = { Text(c.name) },
                        leadingIcon = { Box(Modifier.size(14.dp).clip(CircleShape).background(parseColor(c.color))) },
                        trailingIcon = { if (on) Icon(Icons.Default.Check, null) },
                        onClick = { close(); a.category(c.key, !on) })
                }
            }
            "focus" -> {
                val target = focus == false
                DropdownMenuItem(text = { Text(stringResource(if (target) R.string.move_focused else R.string.move_other)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.ArrowBack, null) }, onClick = { page = "main" })
                HorizontalDivider()
                DropdownMenuItem(text = { Text(stringResource(R.string.only_this_mail)) }, onClick = { close(); a.moveFocus(target, false) })
                DropdownMenuItem(text = { Text(stringResource(R.string.always_this_sender)) }, onClick = { close(); a.moveFocus(target, true) })
            }
            "snooze" -> {
                DropdownMenuItem(text = { Text(stringResource(R.string.snooze)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.ArrowBack, null) }, onClick = { page = "main" })
                HorizontalDivider()
                for ((label, at) in snoozeOptions(ctx)) DropdownMenuItem(
                    text = { Text(label) }, trailingIcon = { Text(snoozeHint(at), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant) },
                    onClick = { close(); a.snooze(at) })
            }
            else -> {
                DropdownMenuItem(text = { Text(stringResource(R.string.reply_all)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.ReplyAll, null, tint = ActionColors.Reply) }, onClick = { close(); a.replyAll() })
                DropdownMenuItem(text = { Text(stringResource(R.string.forward)) }, leadingIcon = { Icon(Icons.AutoMirrored.Filled.Forward, null, tint = ActionColors.Forward) }, onClick = { close(); a.forward() })
                HorizontalDivider()
                DropdownMenuItem(text = { Text(stringResource(R.string.flag_menu)) }, leadingIcon = { Icon(if (flagged) Icons.Default.Flag else Icons.Outlined.Flag, null, tint = Color(0xFFD13438)) }, onClick = { page = "followup" })
                DropdownMenuItem(text = { Text(stringResource(R.string.categorize)) }, leadingIcon = { Icon(Icons.Default.Label, null, tint = categories.firstOrNull { it.key in keywords }?.let { parseColor(it.color) } ?: MaterialTheme.colorScheme.onSurfaceVariant) }, onClick = { page = "category" })
                if (inSnoozed) DropdownMenuItem(text = { Text(stringResource(R.string.unsnooze)) }, leadingIcon = { Icon(Icons.Default.Undo, null) }, onClick = { close(); a.unsnooze() })
                else DropdownMenuItem(text = { Text(stringResource(R.string.snooze)) }, leadingIcon = { Icon(Icons.Default.Schedule, null) }, onClick = { page = "snooze" })
                DropdownMenuItem(text = { Text(stringResource(R.string.archive)) }, leadingIcon = { Icon(Icons.Default.Archive, null) }, onClick = { close(); a.archive() })
                if (focus != null) DropdownMenuItem(text = { Text(stringResource(if (focus == false) R.string.move_focused else R.string.move_other)) },
                    leadingIcon = { Icon(Icons.Default.SwapHoriz, null) }, onClick = { page = "focus" })
                if (inJunk) DropdownMenuItem(text = { Text(stringResource(R.string.not_junk)) }, leadingIcon = { Icon(Icons.Default.VerifiedUser, null, tint = Color(0xFF107C10)) }, onClick = { close(); a.notJunk() })
                else DropdownMenuItem(text = { Text(stringResource(R.string.report_junk)) }, leadingIcon = { Icon(Icons.Default.Report, null, tint = ActionColors.Junk) }, onClick = { close(); a.junk() })
                DropdownMenuItem(text = { Text(stringResource(R.string.mark_unread)) }, leadingIcon = { Icon(Icons.Default.MarkEmailUnread, null, tint = ActionColors.Forward) }, onClick = { close(); a.markUnread() })
            }
        }
    }
}
