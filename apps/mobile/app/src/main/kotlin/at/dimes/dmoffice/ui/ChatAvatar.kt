package at.dimes.dmoffice.ui

import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.luminance
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.selection.selectable
import androidx.compose.runtime.collectAsState
import androidx.compose.ui.res.painterResource
import at.dimes.dmoffice.App
import kotlinx.coroutines.launch
import androidx.compose.runtime.setValue

/** Signaturfarbe von dmOffice - Vorgabe, wenn jemand keine Akzentfarbe gewählt hat. */
private val Signature = Color(0xFF7B1DD3)

/**
 * Profilbild (Version 2): eigenes Bild, sonst der erste Buchstabe des Benutzernamens auf der Akzentfarbe des Benutzers -
 * weiß, auf sehr hellen Farben dunkel.
 */
@Composable
fun PersonAvatar(id: String, name: String, login: String?, avatar: String?, accent: String?, size: Int = 40) {
    val app = App.get(LocalContext.current)
    val photo by produceState<android.graphics.Bitmap?>(null, id, avatar) { value = avatar?.let { app.chat.avatar(id, it) } }
    val bg = accent?.takeIf { Regex("^#[0-9a-fA-F]{6}$").matches(it) }?.let { Color(android.graphics.Color.parseColor(it)) } ?: Signature
    Box(Modifier.size(size.dp).clip(CircleShape).background(bg), contentAlignment = Alignment.Center) {
        val p = photo
        if (p != null) Image(p.asImageBitmap(), contentDescription = null, Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
        else Text(((login?.takeIf { it.isNotBlank() } ?: name).trim().firstOrNull() ?: '?').uppercase(),
            color = if (bg.luminance() > 0.45f) Color(0xFF1F1F1F) else Color.White, fontWeight = FontWeight.SemiBold, fontSize = (size * 0.45f).sp)
    }
}

/**
 * Einstellungen -> Chat: eigenes Profilbild (Version 2). Das Foto geht unverändert an den Server - der dreht es richtig
 * herum und schneidet es quadratisch aus der Mitte zu.
 */
@Composable
fun ProfilePhotoSection() {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    var me by androidx.compose.runtime.remember { androidx.compose.runtime.mutableStateOf<at.dimes.dmoffice.chat.ChatStatus?>(null) }
    var busy by androidx.compose.runtime.remember { androidx.compose.runtime.mutableStateOf(false) }
    androidx.compose.runtime.LaunchedEffect(Unit) { me = try { app.chat.status() } catch (_: Exception) { null } }
    fun toast(msg: String?) = android.widget.Toast.makeText(ctx, msg ?: "?", android.widget.Toast.LENGTH_SHORT).show()
    val pick = androidx.activity.compose.rememberLauncherForActivityResult(androidx.activity.result.contract.ActivityResultContracts.PickVisualMedia()) { uri ->
        if (uri != null) scope.launch {
            busy = true
            try {
                val cr = ctx.contentResolver
                val bytes = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) { cr.openInputStream(uri)!!.use { it.readBytes() } }
                val v = app.chat.setAvatar(bytes, cr.getType(uri) ?: "image/jpeg")
                me = me?.copy(avatar = v)
            } catch (e: Exception) { toast(e.message) } finally { busy = false }
        }
    }
    val account = app.prefs.account.value
    SectionHeader(androidx.compose.ui.res.stringResource(at.dimes.dmoffice.R.string.chat_profile_photo))
    androidx.compose.foundation.layout.Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(12.dp)) {
        PersonAvatar(me?.me ?: "", account?.displayName ?: "", account?.login, me?.avatar, me?.accent, 64)
        androidx.compose.material3.OutlinedButton(enabled = !busy && me != null, onClick = {
            pick.launch(androidx.activity.result.PickVisualMediaRequest(androidx.activity.result.contract.ActivityResultContracts.PickVisualMedia.ImageOnly))
        }) { Text(androidx.compose.ui.res.stringResource(if (me?.avatar != null) at.dimes.dmoffice.R.string.chat_profile_change else at.dimes.dmoffice.R.string.chat_profile_choose)) }
        if (me?.avatar != null) androidx.compose.material3.TextButton(enabled = !busy, onClick = {
            scope.launch { try { app.chat.removeAvatar(); me = me?.copy(avatar = null) } catch (e: Exception) { toast(e.message) } }
        }) { Text(androidx.compose.ui.res.stringResource(at.dimes.dmoffice.R.string.chat_profile_remove)) }
    }
    Text(androidx.compose.ui.res.stringResource(at.dimes.dmoffice.R.string.chat_profile_hint), style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 6.dp))
}

/** Figuren für Pukki; die erste ist die Vorgabe. */
val PukkiFigures = listOf("panda", "walross", "baer", "zauberer", "walross-zauberer")

private fun pukkiDrawable(figure: String) = when (figure) {
    "walross" -> at.dimes.dmoffice.R.drawable.pukki_walross
    "baer" -> at.dimes.dmoffice.R.drawable.pukki_baer
    "zauberer" -> at.dimes.dmoffice.R.drawable.pukki_zauberer
    "walross-zauberer" -> at.dimes.dmoffice.R.drawable.pukki_walross_zauberer
    else -> at.dimes.dmoffice.R.drawable.pukki_panda
}

@Composable
private fun pukkiName(figure: String) = androidx.compose.ui.res.stringResource(when (figure) {
    "walross" -> at.dimes.dmoffice.R.string.pukki_walross
    "baer" -> at.dimes.dmoffice.R.string.pukki_baer
    "zauberer" -> at.dimes.dmoffice.R.string.pukki_zauberer
    "walross-zauberer" -> at.dimes.dmoffice.R.string.pukki_walross_zauberer
    else -> at.dimes.dmoffice.R.string.pukki_panda
})

/** Pukki als die Figur, die man sich ausgesucht hat. */
@Composable
fun PukkiAvatar(size: Int = 40) {
    val app = App.get(LocalContext.current)
    val figure by app.chat.pukkiFigure.collectAsState()
    androidx.compose.runtime.LaunchedEffect(Unit) { if (!app.chat.statusKnown) try { app.chat.status() } catch (_: Exception) { } }
    Image(painterResource(pukkiDrawable(figure)), contentDescription = null, Modifier.size(size.dp).clip(CircleShape), contentScale = ContentScale.Crop)
}

/** Posen aus den Bögen (assets/pukki/<figur>-<pose>.webp), einmal geladen. */
private val poses = object : android.util.LruCache<String, android.graphics.Bitmap>(6 * 1024 * 1024) {
    override fun sizeOf(key: String, value: android.graphics.Bitmap) = value.byteCount
}

/**
 * Pukki in einer Pose in der gewählten Figur: hallo, winken, daumen, frage, idee, herz, jubel, bitte, lesen, laptop, lupe,
 * zeigen, checkliste, arbeitet, staunen, kaffee.
 */
@Composable
fun PukkiPose(pose: String, size: Int = 48, modifier: Modifier = Modifier) {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val figure by app.chat.pukkiFigure.collectAsState()
    androidx.compose.runtime.LaunchedEffect(Unit) { if (!app.chat.statusKnown) try { app.chat.status() } catch (_: Exception) { } }
    val key = "$figure-$pose"
    val bmp by produceState(poses.get(key), key) {
        if (value == null) value = kotlinx.coroutines.withContext(kotlinx.coroutines.Dispatchers.IO) {
            try { ctx.assets.open("pukki/$key.webp").use { android.graphics.BitmapFactory.decodeStream(it) }?.also { poses.put(key, it) } }
            catch (_: java.io.IOException) { null }
        }
    }
    Box(modifier.size(size.dp)) {
        bmp?.let { Image(it.asImageBitmap(), contentDescription = null, Modifier.fillMaxSize(), contentScale = ContentScale.Fit) }
    }
}

/** Einstellungen -> Chat: als welche Figur Pukki erscheint - nur für einen selbst. */
@Composable
fun PukkiFigureSection() {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val scope = androidx.compose.runtime.rememberCoroutineScope()
    val current by app.chat.pukkiFigure.collectAsState()
    androidx.compose.runtime.LaunchedEffect(Unit) { if (!app.chat.statusKnown) try { app.chat.status() } catch (_: Exception) { } }
    SectionHeader(androidx.compose.ui.res.stringResource(at.dimes.dmoffice.R.string.pukki_figure))
    // Umbrechen statt einer Reihe: fuenf Figuren passen nicht nebeneinander, sonst fiel das Zauber-Walross rechts heraus
    @OptIn(androidx.compose.foundation.layout.ExperimentalLayoutApi::class)
    androidx.compose.foundation.layout.FlowRow(Modifier.fillMaxWidth(), horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(4.dp, Alignment.CenterHorizontally)) {
        for (f in PukkiFigures) {
            val chosen = f == current
            androidx.compose.foundation.layout.Column(
                Modifier.clip(androidx.compose.foundation.shape.RoundedCornerShape(12.dp))
                    .background(if (chosen) MaterialTheme.colorScheme.secondaryContainer else Color.Transparent)
                    .selectable(selected = chosen, role = androidx.compose.ui.semantics.Role.RadioButton) {
                        scope.launch {
                            try { app.chat.setPukkiFigure(f) }
                            catch (e: Exception) { android.widget.Toast.makeText(ctx, e.message ?: "?", android.widget.Toast.LENGTH_SHORT).show() }
                        }
                    }
                    .padding(8.dp),
                horizontalAlignment = Alignment.CenterHorizontally) {
                Image(painterResource(pukkiDrawable(f)), contentDescription = null,
                    Modifier.size(56.dp).clip(CircleShape)
                        .then(if (chosen) Modifier.border(2.dp, MaterialTheme.colorScheme.primary, CircleShape) else Modifier),
                    contentScale = ContentScale.Crop)
                Text(pukkiName(f), style = MaterialTheme.typography.bodySmall, fontWeight = if (chosen) FontWeight.SemiBold else FontWeight.Normal,
                    modifier = Modifier.padding(top = 4.dp))
            }
        }
    }
    Text(androidx.compose.ui.res.stringResource(at.dimes.dmoffice.R.string.pukki_figure_hint), style = MaterialTheme.typography.bodySmall,
        color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(top = 6.dp))
}
