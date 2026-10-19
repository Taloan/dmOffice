package at.dimes.dmoffice.ui

import androidx.biometric.BiometricManager
import androidx.biometric.BiometricPrompt
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import at.dimes.dmoffice.R

/** App-Sperre: Fingerabdruck, Gesicht oder Geraete-PIN ueber BiometricPrompt. */
@Composable
fun LockScreen(activity: FragmentActivity, onUnlocked: () -> Unit) {
    fun prompt() {
        val allowed = BiometricManager.Authenticators.BIOMETRIC_STRONG or BiometricManager.Authenticators.DEVICE_CREDENTIAL
        if (BiometricManager.from(activity).canAuthenticate(allowed) != BiometricManager.BIOMETRIC_SUCCESS) { onUnlocked(); return }
        val info = BiometricPrompt.PromptInfo.Builder()
            .setTitle(activity.getString(R.string.unlock)).setSubtitle(activity.getString(R.string.unlock_subtitle))
            .setAllowedAuthenticators(allowed).build()
        BiometricPrompt(activity, ContextCompat.getMainExecutor(activity), object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) { onUnlocked() }
        }).authenticate(info)
    }
    LaunchedEffect(Unit) { prompt() }
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center) {
            Icon(Icons.Default.Lock, contentDescription = null, modifier = Modifier.size(64.dp), tint = MaterialTheme.colorScheme.primary)
            Text(stringResource(R.string.unlock), style = MaterialTheme.typography.titleLarge, modifier = Modifier.padding(16.dp))
            Button(onClick = { prompt() }) { Text(stringResource(R.string.unlock_button)) }
        }
    }
}
