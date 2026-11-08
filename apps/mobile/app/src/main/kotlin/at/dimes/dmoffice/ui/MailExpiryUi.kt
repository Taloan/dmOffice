package at.dimes.dmoffice.ui

import android.content.Context
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.HourglassBottom
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import at.dimes.dmoffice.api.MailExpiry
import java.time.Instant
import java.time.LocalDate
import java.time.LocalDateTime
import java.time.LocalTime
import java.time.OffsetDateTime
import java.time.ZoneId

// Entfernen nach Zeit (Server: Mail/MailExpiry.cs) - in den Papierkorb nach … oder vernichten nach …, ausgefuehrt auf dem Server

fun expiryInstant(e: MailExpiry): Instant? =
    try { Instant.parse(e.at) } catch (_: Exception) { try { OffsetDateTime.parse(e.at).toInstant() } catch (_: Exception) { null } }

fun expiryText(ctx: Context, e: MailExpiry): String {
    val at = expiryInstant(e)?.let { Fmt.dateTime(ctx, it.toEpochMilli(), false) } ?: e.at
    return ctx.getString(if (e.kind == "destroy") R.string.expire_destroy_at else R.string.expire_trash_at, at)
}

fun LocalDateTime.toIso(): String = atZone(ZoneId.systemDefault()).toOffsetDateTime().toString()

private val DestroyRed = Color(0xFFD13438)

/** In der Mailliste: Sanduhr mit Datum; Vernichten in Rot. */
@Composable
fun ExpiryTag(e: MailExpiry) {
    val ctx = LocalContext.current
    val tint = if (e.kind == "destroy") DestroyRed else MaterialTheme.colorScheme.onSurfaceVariant
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(Icons.Default.HourglassBottom, contentDescription = expiryText(ctx, e), modifier = Modifier.size(12.dp), tint = tint)
        Spacer(Modifier.width(2.dp))
        Text(expiryInstant(e)?.let { Fmt.shortDate(it.atZone(ZoneId.systemDefault()).toLocalDate()) } ?: "",
            style = MaterialTheme.typography.labelSmall, color = tint)
    }
}

/** In der geoeffneten Mail: der ganze Satz und „Zurücknehmen“. */
@Composable
fun ExpiryBanner(e: MailExpiry, onUndo: () -> Unit) {
    val ctx = LocalContext.current
    val tint = if (e.kind == "destroy") DestroyRed else MaterialTheme.colorScheme.primary
    Row(Modifier.fillMaxWidth().padding(top = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Icon(Icons.Default.HourglassBottom, contentDescription = null, modifier = Modifier.size(16.dp), tint = tint)
        Spacer(Modifier.width(6.dp))
        Text(expiryText(ctx, e), style = MaterialTheme.typography.bodySmall, color = tint, modifier = Modifier.weight(1f))
        TextButton(onClick = onUndo) { Text(stringResource(R.string.expire_undo)) }
    }
}

/** Eigener Zeitpunkt („Am …“): Datum und Uhrzeit, Vorgabe in sieben Tagen. */
@Composable
fun ExpiryDateDialog(kind: String, onDismiss: () -> Unit, onPick: (LocalDateTime) -> Unit) {
    val start = LocalDateTime.now().plusDays(7)
    var date by remember { mutableStateOf<LocalDate>(start.toLocalDate()) }
    var time by remember { mutableStateOf<LocalTime>(start.toLocalTime().withSecond(0).withNano(0)) }
    val at = date.atTime(time)
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(if (kind == "destroy") R.string.expire_destroy_on else R.string.expire_trash_on)) },
        text = {
            Column {
                DateButton(date, { date = it })
                TimeButton(time, { time = it })
            }
        },
        confirmButton = { TextButton(enabled = at.isAfter(LocalDateTime.now()), onClick = { onPick(at) }) { Text(stringResource(android.R.string.ok)) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}

/** Vor dem Vernichten einmal klar sagen, dass es endgueltig ist. */
@Composable
fun DestroyConfirmDialog(at: LocalDateTime, onDismiss: () -> Unit, onConfirm: () -> Unit) {
    val ctx = LocalContext.current
    val whenText = Fmt.dateTime(ctx, at.atZone(ZoneId.systemDefault()).toInstant().toEpochMilli(), false)
    AlertDialog(
        onDismissRequest = onDismiss,
        title = { Text(stringResource(R.string.expire_destroy_title)) },
        text = { Text(stringResource(R.string.expire_destroy_text, whenText)) },
        confirmButton = { TextButton(onClick = onConfirm) { Text(stringResource(R.string.expire_destroy_ok), color = DestroyRed) } },
        dismissButton = { TextButton(onClick = onDismiss) { Text(stringResource(R.string.cancel)) } },
    )
}
