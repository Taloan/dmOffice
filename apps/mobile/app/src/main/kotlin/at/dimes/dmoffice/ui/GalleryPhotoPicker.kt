package at.dimes.dmoffice.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.files.FileItem
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Fotos aus der Galerie von dmOffice wählen (nicht vom Handy) - ganzer Bildschirm, neueste zuerst, mehrere ankreuzbar.
 * Die Liste kommt wie in der Galerie (FilesRepo.photos, zuletzt geladene sofort).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun GalleryPhotoPicker(onDismiss: () -> Unit, onPick: (List<FileItem>) -> Unit) {
    val ctx = LocalContext.current
    val files = App.get(ctx).files
    var items by remember { mutableStateOf(files.photosCache) }
    var error by remember { mutableStateOf<String?>(null) }
    val picked = remember { mutableStateListOf<FileItem>() }
    LaunchedEffect(Unit) {
        try { items = withContext(Dispatchers.IO) { files.photos() } } catch (e: Exception) { error = e.message; if (items == null) items = emptyList() }
    }
    Dialog(onDismissRequest = onDismiss, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        BackHandler { onDismiss() }
        Scaffold(topBar = {
            TopAppBar(
                title = { Text(stringResource(R.string.gallery)) },
                navigationIcon = { IconButton(onClick = onDismiss) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = stringResource(R.string.close)) } },
                actions = { TextButton(enabled = picked.isNotEmpty(), onClick = { onPick(picked.toList()) }) { Text(stringResource(R.string.note_insert_count, picked.size)) } })
        }) { pad ->
            Column(Modifier.padding(pad).fillMaxSize()) {
                error?.let { Text(it, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(16.dp)) }
                val list = items
                if (list == null) Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                else if (list.isEmpty() && error == null) Text(stringResource(R.string.note_gallery_empty), modifier = Modifier.padding(16.dp))
                else LazyVerticalGrid(GridCells.Adaptive(96.dp), Modifier.fillMaxSize(), contentPadding = PaddingValues(2.dp)) {
                    items(list, key = { it.path }) { f ->
                        val checked = picked.any { it.path == f.path }
                        Box(Modifier.padding(2.dp).aspectRatio(1f).clickable { if (checked) picked.removeAll { it.path == f.path } else picked.add(f) }
                            .then(if (checked) Modifier.border(3.dp, MaterialTheme.colorScheme.primary) else Modifier)) {
                            ThumbOrIcon(f, Modifier.fillMaxSize(), 28.dp)
                            if (checked) Icon(Icons.Default.CheckCircle, contentDescription = null, tint = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.align(Alignment.TopEnd).padding(4.dp).size(24.dp))
                        }
                    }
                }
            }
        }
    }
}

/** Foto der Galerie als JPEG für den Editor: großes Vorschaubild vom Server (aufrecht, höchstens 1600 px). */
internal fun galleryJpeg(ctx: android.content.Context, item: FileItem): ByteArray {
    val data = App.get(ctx).files.thumb(item, 1600) ?: throw IllegalArgumentException(item.name)
    val bmp = android.graphics.BitmapFactory.decodeByteArray(data, 0, data.size) ?: throw IllegalArgumentException(item.name)
    return java.io.ByteArrayOutputStream().use { s -> bmp.compress(android.graphics.Bitmap.CompressFormat.JPEG, 85, s); s.toByteArray() }
}
