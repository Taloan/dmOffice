package at.dimes.dmoffice.ui

import android.graphics.Bitmap
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculateCentroid
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.journal.Media

/**
 * Bilder eines Tagebucheintrags groß (Tippen auf das Bild im Eintrag): Wischen blättert, zwei Finger oder Doppeltippen zoomen.
 * Gezoomt verschiebt Wischen den Ausschnitt statt zu blättern. Geladen wird eine große Vorschau vom Server (2048 px).
 */
@Composable
internal fun JournalPhotoViewer(journal: Long, pics: List<Media>, start: Int, onClose: () -> Unit) {
    val pager = rememberPagerState(initialPage = start.coerceIn(0, (pics.size - 1).coerceAtLeast(0))) { pics.size }
    var zoomed by remember { mutableStateOf(false) }
    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        BackHandler { onClose() }
        Box(Modifier.fillMaxSize().background(Color.Black)) {
            HorizontalPager(pager, Modifier.fillMaxSize(), userScrollEnabled = !zoomed, key = { pics[it].path }) { i ->
                JournalZoomImage(journal, pics[i], onZoom = { if (i == pager.currentPage) zoomed = it })
            }
            IconButton(onClick = onClose, modifier = Modifier.align(Alignment.TopEnd).statusBarsPadding().padding(8.dp)) {
                Icon(Icons.Default.Close, contentDescription = stringResource(R.string.close), tint = Color.White)
            }
            if (pics.size > 1) Text("${pager.currentPage + 1} / ${pics.size}", Modifier.align(Alignment.BottomCenter).padding(24.dp)
                .clip(RoundedCornerShape(8.dp)).background(Color.Black.copy(alpha = 0.55f)).padding(horizontal = 10.dp, vertical = 3.dp), color = Color.White)
        }
    }
}

@Composable
private fun JournalZoomImage(journal: Long, m: Media, onZoom: (Boolean) -> Unit) {
    val app = App.get(LocalContext.current)
    val bmp by produceState<Bitmap?>(null, journal, m.path) { value = app.journal.thumb(journal, m.path, 2048) ?: app.journal.thumb(journal, m.path, 1024) }
    var scale by remember { mutableFloatStateOf(1f) }
    var offset by remember { mutableStateOf(Offset.Zero) }
    LaunchedEffect(scale) { onZoom(scale > 1.01f) }
    Box(Modifier.fillMaxSize()
        .pointerInput(m.path) {
            detectTapGestures(onDoubleTap = { p ->
                if (scale > 1.01f) { scale = 1f; offset = Offset.Zero } else { scale = 2.5f; offset = (Offset(size.width / 2f, size.height / 2f) - p) * 1.5f }
            })
        }
        .pointerInput(m.path) {
            // Nur mit zwei Fingern oder gezoomt - sonst bleibt das Wischen beim Blättern
            awaitEachGesture {
                awaitFirstDown(requireUnconsumed = false)
                do {
                    val event = awaitPointerEvent()
                    if (event.changes.size > 1 || scale > 1.01f) {
                        val s = (scale * event.calculateZoom()).coerceIn(1f, 6f)
                        val pan = event.calculatePan()
                        val c = event.calculateCentroid()
                        val mid = Offset(size.width / 2f, size.height / 2f)
                        val o = if (c == Offset.Unspecified) offset else (offset - (c - mid)) * (s / scale) + (c - mid)
                        scale = s
                        val maxX = (size.width * (s - 1)) / 2; val maxY = (size.height * (s - 1)) / 2
                        offset = if (s <= 1.01f) Offset.Zero else Offset((o.x + pan.x).coerceIn(-maxX, maxX), (o.y + pan.y).coerceIn(-maxY, maxY))
                        event.changes.forEach { if (it.positionChanged()) it.consume() }
                    }
                } while (event.changes.any { it.pressed })
            }
        }, contentAlignment = Alignment.Center) {
        val b = bmp
        if (b == null) CircularProgressIndicator(color = Color.White)
        else Image(b.asImageBitmap(), contentDescription = null, contentScale = ContentScale.Fit,
            modifier = Modifier.fillMaxSize().graphicsLayer { scaleX = scale; scaleY = scale; translationX = offset.x; translationY = offset.y })
    }
}
