package at.dimes.dmoffice.ui

import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TimePicker
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.material3.rememberTimePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import at.dimes.dmoffice.R
import java.time.Instant
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneOffset

/** Knopf mit Datum, oeffnet den Material-Datumswaehler. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DateButton(date: LocalDate, onChange: (LocalDate) -> Unit, modifier: Modifier = Modifier) {
    var open by remember { mutableStateOf(false) }
    OutlinedButton(onClick = { open = true }, modifier = modifier) { Text(Fmt.shortDate(date) + " " + date.year) }
    if (open) {
        val state = rememberDatePickerState(initialSelectedDateMillis = date.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli())
        DatePickerDialog(
            onDismissRequest = { open = false },
            confirmButton = {
                TextButton(onClick = {
                    state.selectedDateMillis?.let { onChange(Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate()) }
                    open = false
                }) { Text(stringResource(android.R.string.ok)) }
            },
            dismissButton = { TextButton(onClick = { open = false }) { Text(stringResource(R.string.cancel)) } }
        ) { DatePicker(state = state) }
    }
}

/** Knopf mit Uhrzeit, oeffnet den Material-Zeitwaehler. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TimeButton(time: LocalTime, onChange: (LocalTime) -> Unit, modifier: Modifier = Modifier) {
    val ctx = LocalContext.current
    var open by remember { mutableStateOf(false) }
    val is24 = android.text.format.DateFormat.is24HourFormat(ctx)
    OutlinedButton(onClick = { open = true }, modifier = modifier) {
        Text(java.time.format.DateTimeFormatter.ofPattern(if (is24) "HH:mm" else "h:mm a").format(time))
    }
    if (open) {
        val state = rememberTimePickerState(initialHour = time.hour, initialMinute = time.minute, is24Hour = is24)
        AlertDialog(
            onDismissRequest = { open = false },
            confirmButton = { TextButton(onClick = { onChange(LocalTime.of(state.hour, state.minute)); open = false }) { Text(stringResource(android.R.string.ok)) } },
            dismissButton = { TextButton(onClick = { open = false }) { Text(stringResource(R.string.cancel)) } },
            text = { TimePicker(state = state) }
        )
    }
}

@Composable
fun DateTimeRow(date: LocalDate, time: LocalTime, allDay: Boolean, onDate: (LocalDate) -> Unit, onTime: (LocalTime) -> Unit) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)) {
        DateButton(date, onDate, Modifier.weight(1.4f))
        if (!allDay) TimeButton(time, onTime, Modifier.weight(1f))
    }
}
