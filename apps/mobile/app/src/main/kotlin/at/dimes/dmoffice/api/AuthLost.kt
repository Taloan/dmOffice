package at.dimes.dmoffice.api

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Der Server lehnt das App-Passwort ab (401) - etwa weil es in den Einstellungen gelöscht wurde. Die App zeigt dann
 * „Neu verbinden“: neues App-Passwort eintippen oder den QR-Code scannen, ohne abzumelden. Einstellungen, Auto-Upload und
 * Zwischenspeicher bleiben dabei, nur die Zugangsdaten werden ersetzt.
 */
object AuthLost {
    private val _rejected = MutableStateFlow(false)
    val rejected: StateFlow<Boolean> = _rejected

    /**
     * 401 mit diesen Zugangsdaten. Nur melden, wenn es noch die aktuellen sind - nach „Neu verbinden“ laufen noch Anfragen
     * (oder Bereiche mit eigenem Client) mit dem alten App-Passwort, deren 401 sonst den Hinweis gleich wieder zeigte.
     */
    fun report(used: at.dimes.dmoffice.data.Account) {
        val current = at.dimes.dmoffice.App.lastContext?.let { at.dimes.dmoffice.App.get(it).prefs.account.value }
        if (current != null && (current.token != used.token || current.deviceId != used.deviceId)) return
        _rejected.value = true
    }
    fun clear() { _rejected.value = false }
}
