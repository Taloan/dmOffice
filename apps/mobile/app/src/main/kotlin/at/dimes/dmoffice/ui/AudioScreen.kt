package at.dimes.dmoffice.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items as gridItems
import androidx.compose.foundation.lazy.grid.rememberLazyGridState
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.QueueMusic
import androidx.compose.material.icons.automirrored.filled.ViewList
import androidx.compose.material.icons.filled.AutoAwesome
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.GridView
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.HourglassEmpty
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.Pause
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Repeat
import androidx.compose.material.icons.filled.RepeatOne
import androidx.compose.material.icons.filled.Search
import androidx.compose.material.icons.filled.Shuffle
import androidx.compose.material.icons.filled.SkipNext
import androidx.compose.material.icons.filled.SkipPrevious
import androidx.compose.material.icons.filled.TrendingUp
import androidx.compose.material.icons.filled.UnfoldMore
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.SecondaryScrollableTabRow
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.media3.common.Player
import at.dimes.dmoffice.App
import at.dimes.dmoffice.R
import at.dimes.dmoffice.audio.Album
import at.dimes.dmoffice.audio.AudioPlayer
import at.dimes.dmoffice.audio.AudioRepo
import at.dimes.dmoffice.audio.Group
import at.dimes.dmoffice.audio.PlaylistInfo
import at.dimes.dmoffice.audio.Track
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** 3:07, 1:02:15 */
private fun fmtDur(ms: Long): String {
    val s = ms / 1000; val h = s / 3600; val m = (s % 3600) / 60; val r = s % 60
    return if (h > 0) "%d:%02d:%02d".format(h, m, r) else "%d:%02d".format(m, r)
}

/** Cover der Audiothek (vom Server, auf dem Gerät gemerkt); ohne Cover eine Note. [corner]: Ecken (Vorgabe nach Größe). */
@Composable
fun AudioCover(id: String?, size: Dp, modifier: Modifier = Modifier, corner: Dp? = null) {
    val app = App.get(LocalContext.current)
    val bmp by produceState(id?.let { app.audio.cachedCover(it) }, id) { if (id != null && value == null) value = app.audio.cover(id) }
    val shape = RoundedCornerShape(corner ?: if (size > 80.dp) 10.dp else 4.dp)
    if (bmp != null) Image(bmp!!.asImageBitmap(), null, modifier.size(size).clip(shape), contentScale = ContentScale.Crop)
    else Box(modifier.size(size).clip(shape).background(MaterialTheme.colorScheme.surfaceVariant), contentAlignment = Alignment.Center) {
        Icon(Icons.Default.MusicNote, null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(size / 2))
    }
}

/** Cover eines Interpreten oder Genres: bei einem Album dessen Cover, sonst ein Mosaik aus bis zu vier Albumcovern (2×2). */
@Composable
private fun CoverMosaic(covers: List<String>, size: Dp) {
    if (covers.size < 2) { AudioCover(covers.firstOrNull(), size); return }
    // Zwei Cover über Kreuz, drei: das erste noch einmal unten rechts
    val four = when (covers.size) { 2 -> listOf(covers[0], covers[1], covers[1], covers[0]); 3 -> covers + covers[0]; else -> covers.take(4) }
    Column(Modifier.size(size).clip(RoundedCornerShape(if (size > 80.dp) 10.dp else 4.dp))) {
        for (r in 0..1) Row { for (c in 0..1) AudioCover(four[r * 2 + c], size / 2, corner = 0.dp) }
    }
}

/** Quadrat über die ganze Breite (Kacheln) */
@Composable
private fun Square(content: @Composable (Dp) -> Unit) {
    Box(Modifier.fillMaxWidth().aspectRatio(1f)) { BoxWithConstraints(Modifier.fillMaxSize()) { content(maxWidth) } }
}

/** Sprünge aus Titelzeilen: zum Interpreten (Schlüssel = Name klein) und zum Album ([AudioRepo.albumKey]). */
private class AudioNav(val artist: (String) -> Unit, val album: (String) -> Unit)

@Composable
private fun TrackRow(t: Track, current: Boolean, onClick: () -> Unit, nav: AudioNav, number: Int? = null) {
    ListItem(
        modifier = Modifier.clickable(onClick = onClick),
        leadingContent = {
            if (number != null) Text(number.toString(), Modifier.width(28.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            else AudioCover(t.cover, 44.dp)
        },
        headlineContent = { Text(t.title, maxLines = 1, overflow = TextOverflow.Ellipsis, fontWeight = if (current) FontWeight.SemiBold else null,
            color = if (current) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface) },
        supportingContent = { TrackLinks(t, nav) },
        trailingContent = { t.duration?.let { Text(fmtDur((it * 1000).toLong()), style = MaterialTheme.typography.bodySmall) } },
    )
}

/** Interpret · Album, beide antippbar */
@Composable
private fun TrackLinks(t: Track, nav: AudioNav) {
    val artist = t.artistShown.takeIf { it.isNotEmpty() }
    val album = t.album
    Row(verticalAlignment = Alignment.CenterVertically) {
        if (artist != null) Text(artist, Modifier.weight(1f, fill = false).clickable { nav.artist(artist.lowercase()) }, maxLines = 1, overflow = TextOverflow.Ellipsis)
        if (artist != null && album != null) Text(" · ", maxLines = 1)
        if (album != null) Text(album, Modifier.weight(1f, fill = false).clickable { nav.album(AudioRepo.albumKey(t)) }, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

/** Titel einer Liste mit Kopf (Name, Abspielen, Zufällig); Tippen spielt die Liste ab diesem Titel. */
@Composable
private fun TrackList(tracks: List<Track>, nav: AudioNav, header: (@Composable () -> Unit)? = null, numbered: Boolean = false) {
    val ctx = LocalContext.current
    val now by AudioPlayer.now.collectAsState()
    LazyColumn(Modifier.fillMaxSize()) {
        if (header != null) item { header() }
        itemsIndexed(tracks, key = { i, t -> "$i/${t.id}" }) { i, t ->
            TrackRow(t, t.id == now.id, { AudioPlayer.play(ctx, tracks, i, shuffle = false) }, nav, number = if (numbered) t.track ?: (i + 1) else null)
        }
    }
}

@Composable
private fun DetailHead(title: String, sub: String?, covers: List<String>, tracks: List<Track>, onBack: () -> Unit) {
    val ctx = LocalContext.current
    Column(Modifier.fillMaxWidth().padding(16.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null) }
            Spacer(Modifier.width(4.dp))
            CoverMosaic(covers, 96.dp)
            Spacer(Modifier.width(14.dp))
            Column(Modifier.weight(1f)) {
                Text(title, style = MaterialTheme.typography.titleLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                if (!sub.isNullOrEmpty()) Text(sub, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val total = tracks.sumOf { it.duration ?: 0.0 }
                Text(stringResource(R.string.audio_n_tracks, tracks.size) + if (total > 0) " · " + fmtDur((total * 1000).toLong()) else "",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
        Row(Modifier.padding(top = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button(onClick = { AudioPlayer.play(ctx, tracks, 0, shuffle = false) }, enabled = tracks.isNotEmpty()) {
                Icon(Icons.Default.PlayArrow, null); Spacer(Modifier.width(6.dp)); Text(stringResource(R.string.audio_play))
            }
            OutlinedButton(onClick = { AudioPlayer.play(ctx, tracks, shuffle = true) }, enabled = tracks.isNotEmpty()) {
                Icon(Icons.Default.Shuffle, null); Spacer(Modifier.width(6.dp)); Text(stringResource(R.string.audio_shuffle))
            }
        }
    }
}

/** Kopf eines Interpreten oder Genres: dazu seine Alben in einer Zeile zum Wischen. */
@Composable
private fun GroupHead(g: Group, onAlbum: (Album) -> Unit, onBack: () -> Unit) {
    DetailHead(g.name, stringResource(R.string.audio_n_albums, g.albums.size), g.covers, g.tracks, onBack)
    if (g.albums.isNotEmpty()) {
        Text(stringResource(R.string.audio_albums), Modifier.padding(horizontal = 16.dp), style = MaterialTheme.typography.titleSmall)
        LazyRow(contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            items(g.albums, key = { it.key }) { a -> Box(Modifier.width(130.dp)) { AlbumTile(a) { onAlbum(a) } } }
        }
        HorizontalDivider()
    }
}

private enum class AudioView(val pref: String, val tilesDefault: Boolean) {
    Playlists("playlists", false), Albums("albums", true), Artists("artists", true), Genres("genres", true), Tracks("tracks", false)
}

/** Was gerade offen ist: Playlist („auto:Art“ oder Kennung), Album, Interpret oder Genre */
private sealed interface Open {
    data class Playlist(val key: String, val name: String) : Open
    data class Albm(val key: String) : Open
    data class Artist(val key: String) : Open
    data class Genre(val key: String) : Open
}

/** Anfangsbuchstabe für den Schnellscroll: Umlaute wie der Grundbuchstabe, Ziffern und Zeichen als „#“. */
private fun initial(s: String): String {
    val c = s.trim().firstOrNull { it.isLetterOrDigit() } ?: return "#"
    if (!c.isLetter()) return "#"
    return java.text.Normalizer.normalize(c.toString(), java.text.Normalizer.Form.NFD).first().uppercase()
}

/**
 * Audiothek in der App (Version 2, Stufe 3): Playlists und Auto-Playlists vom Server, Alben, Interpreten, Genres, alle Titel.
 * Die Suche filtert den gewählten Reiter; je Reiter Liste oder Kacheln (gemerkt) und Schnellscroll nach Buchstaben. Abspielen über
 * [AudioPlayer]/[at.dimes.dmoffice.audio.AudioService] - läuft im Hintergrund weiter. Playlists bearbeitet man im Web.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AudioScreen() {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val tracks by app.audio.tracks.collectAsState()
    val playlists by app.audio.playlists.collectAsState()
    val autoKinds by app.audio.autoKinds.collectAsState()
    var error by remember { mutableStateOf<String?>(null) }
    // Start: Interpreten
    var view by rememberSaveable { mutableStateOf(AudioView.Artists) }
    var query by rememberSaveable { mutableStateOf("") }
    // Geöffnetes (Album, Interpret ...) als Stapel: Zurück führt dahin, woher man kam
    val stack = remember { mutableStateListOf<Open>() }
    fun push(o: Open) { if (stack.lastOrNull() != o) stack.add(o) }
    fun pop() { if (stack.isNotEmpty()) stack.removeAt(stack.lastIndex) }
    val tiles = remember { mutableStateMapOf<AudioView, Boolean>() }
    fun tilesOf(v: AudioView) = tiles[v] ?: app.prefs.audioTiles(v.pref, v.tilesDefault)
    LaunchedEffect(Unit) {
        AudioPlayer.connect(ctx)
        try { app.audio.refresh(); error = null } catch (e: Exception) { if (tracks == null) error = e.message }
        try { app.audio.refreshPlaylists() } catch (_: Exception) { }
    }
    BackHandler(stack.isNotEmpty()) { pop() }
    val various = stringResource(R.string.audio_various)
    val unknown = stringResource(R.string.audio_unknown_album)
    val unknownArtist = stringResource(R.string.audio_unknown_artist)
    val noGenre = stringResource(R.string.audio_no_genre)
    val all = remember(tracks) { (tracks ?: emptyList()).sortedWith(AudioRepo.byLibrary) }
    val albums = remember(all) { AudioRepo.albums(all, various, unknown) }
    val artists = remember(all) { AudioRepo.artists(all, various, unknown, unknownArtist) }
    val genres = remember(all) { AudioRepo.genres(all, various, unknown, noGenre) }
    val nav = remember { AudioNav(artist = { push(Open.Artist(it)) }, album = { push(Open.Albm(it)) }) }

    when (val o = stack.lastOrNull()) {
        is Open.Playlist -> {
            val list by produceState<List<Track>?>(null, o.key, tracks) { value = try { app.audio.playlistTracks(o.key) } catch (_: Exception) { emptyList() } }
            if (list == null) Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            else TrackList(list!!, nav, header = {
                DetailHead(o.name, null, listOfNotNull(list!!.firstNotNullOfOrNull { it.cover }), list!!) { pop() }
                if (list!!.isEmpty()) Text(stringResource(R.string.audio_playlist_empty), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            })
            return
        }
        is Open.Albm -> {
            val a = albums.firstOrNull { it.key == o.key }
            if (a == null) { LaunchedEffect(o) { stack.remove(o) }; return }
            TrackList(a.tracks, nav, header = {
                DetailHead(a.name, listOfNotNull(a.artist.takeIf { it.isNotEmpty() }, a.year?.toString()).joinToString(" · "), listOfNotNull(a.cover), a.tracks) { pop() }
            }, numbered = true)
            return
        }
        is Open.Artist, is Open.Genre -> {
            val g = if (o is Open.Artist) artists.firstOrNull { it.key == o.key } else genres.firstOrNull { it.key == (o as Open.Genre).key }
            if (g == null) { LaunchedEffect(o) { stack.remove(o) }; return }
            TrackList(g.tracks, nav, header = { GroupHead(g, { push(Open.Albm(it.key)) }) { pop() } })
            return
        }
        null -> {}
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(start = 12.dp, end = 4.dp, top = 8.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            OutlinedTextField(query, { query = it }, Modifier.weight(1f), singleLine = true,
                placeholder = { Text(stringResource(R.string.audio_search), maxLines = 1, overflow = TextOverflow.Ellipsis) },
                leadingIcon = { Icon(Icons.Default.Search, null) },
                trailingIcon = { if (query.isNotEmpty()) IconButton(onClick = { query = "" }) { Icon(Icons.Default.Close, null) } })
            val t = tilesOf(view)
            IconButton(onClick = { tiles[view] = !t; app.prefs.setAudioTiles(view.pref, !t) }) {
                Icon(if (t) Icons.AutoMirrored.Filled.ViewList else Icons.Default.GridView,
                    contentDescription = stringResource(if (t) R.string.audio_view_list else R.string.audio_view_tiles))
            }
        }
        // Der gewählte Reiter bleibt beim Suchen; die Suche filtert ihn
        SecondaryScrollableTabRow(selectedTabIndex = view.ordinal, edgePadding = 8.dp) {
            for (v in AudioView.entries) Tab(selected = view == v, onClick = { view = v }, text = {
                Text(stringResource(when (v) {
                    AudioView.Playlists -> R.string.audio_playlists; AudioView.Albums -> R.string.audio_albums; AudioView.Artists -> R.string.audio_artists
                    AudioView.Genres -> R.string.audio_genres; AudioView.Tracks -> R.string.audio_tracks
                }), maxLines = 1)
            })
        }
        // Treffer der Suche als Kennungen: Alben, Interpreten und Genres mit mindestens einem Treffer bleiben stehen
        val found = remember(all, query) { AudioRepo.search(all, query) }
        val hits = remember(found, query) { if (query.isBlank()) null else found.mapTo(HashSet()) { it.id } }
        fun <T> filter(list: List<T>, tracksOf: (T) -> List<Track>): List<T> = if (hits == null) list else list.filter { x -> tracksOf(x).any { it.id in hits } }
        val tileMode = tilesOf(view)
        when {
            tracks == null && error != null -> Text(stringResource(R.string.audio_error) + "\n" + error, Modifier.padding(16.dp))
            tracks == null -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            all.isEmpty() -> Text(stringResource(R.string.audio_empty), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant)
            else -> key(view) {
                when (view) {
                    AudioView.Playlists -> PlaylistsTab(autoKinds, playlists, query, tileMode) { key, name -> push(Open.Playlist(key, name)) }
                    AudioView.Albums -> {
                        val shown = remember(albums, hits) { filter(albums) { it.tracks } }
                        Browse(shown, query, tileMode, key = { it.key }, letter = { initial(it.artist.ifEmpty { it.name }) },
                            tile = { a -> AlbumTile(a) { push(Open.Albm(a.key)) } },
                            row = { a -> AlbumRow(a) { push(Open.Albm(a.key)) } })
                    }
                    AudioView.Artists, AudioView.Genres -> {
                        val source = if (view == AudioView.Artists) artists else genres
                        val shown = remember(source, hits) { filter(source) { it.tracks } }
                        val open: (Group) -> Unit = { g -> push(if (view == AudioView.Artists) Open.Artist(g.key) else Open.Genre(g.key)) }
                        Browse(shown, query, tileMode, key = { it.key }, letter = { initial(it.name) },
                            tile = { g -> GroupTile(g) { open(g) } }, row = { g -> GroupRow(g) { open(g) } })
                    }
                    AudioView.Tracks -> {
                        val now by AudioPlayer.now.collectAsState()
                        val index = remember(found) { found.withIndex().associate { it.value.id to it.index } }
                        Browse(found, query, tileMode, key = { it.id }, letter = { initial(it.albumArtist ?: it.artist ?: "") },
                            tile = { t -> TrackTile(t, t.id == now.id) { AudioPlayer.play(ctx, found, index[t.id] ?: 0, shuffle = false) } },
                            row = { t -> TrackRow(t, t.id == now.id, { AudioPlayer.play(ctx, found, index[t.id] ?: 0, shuffle = false) }, nav) })
                    }
                }
            }
        }
    }
}

/**
 * Inhalt eines Reiters als Liste oder Kacheln, rechts der Schnellscroll nach Anfangsbuchstaben ([letter] passend zur Sortierung).
 * Neue Suche: wieder nach oben.
 */
@Composable
private fun <T> Browse(items: List<T>, query: String, tiles: Boolean, key: (T) -> Any, letter: (T) -> String,
                       tile: @Composable (T) -> Unit, row: @Composable (T) -> Unit) {
    if (items.isEmpty()) { Text(stringResource(R.string.audio_no_results), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant); return }
    val letters = remember(items) { items.map(letter) }
    Box(Modifier.fillMaxSize()) {
        if (tiles) {
            val state = rememberLazyGridState()
            LaunchedEffect(query) { state.scrollToItem(0) }
            LazyVerticalGrid(GridCells.Adaptive(150.dp), Modifier.fillMaxSize(), state = state, contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                gridItems(items, key = key) { tile(it) }
            }
            LetterScroller(letters, state.firstVisibleItemIndex, state.isScrollInProgress, { state.scrollToItem(it) }, Modifier.align(Alignment.TopEnd))
        } else {
            val state = rememberLazyListState()
            LaunchedEffect(query) { state.scrollToItem(0) }
            LazyColumn(Modifier.fillMaxSize(), state = state) { items(items, key = key) { row(it) } }
            LetterScroller(letters, state.firstVisibleItemIndex, state.isScrollInProgress, { state.scrollToItem(it) }, Modifier.align(Alignment.TopEnd))
        }
    }
}

/**
 * Schnellscroll wie in der Galerie: beim Scrollen erscheint rechts ein Griff; ihn ziehen springt durch die Liste, dabei stehen
 * die Anfangsbuchstaben am Rand und eine Blase zeigt den Buchstaben an dieser Stelle. Nur bei langen Listen.
 */
@Composable
private fun LetterScroller(letters: List<String>, first: Int, scrolling: Boolean, scrollTo: suspend (Int) -> Unit, modifier: Modifier) {
    val total = letters.size
    // Erste Stelle jedes Buchstabens (in der Reihenfolge der Liste)
    val starts = remember(letters) { letters.withIndex().filter { (i, l) -> i == 0 || letters[i - 1] != l }.distinctBy { it.value }.map { it.index to it.value } }
    if (total < 24 || starts.size < 2) return
    val scope = rememberCoroutineScope()
    val density = LocalDensity.current
    var height by remember { mutableStateOf(1) }
    var dragY by remember { mutableStateOf<Float?>(null) }
    var visible by remember { mutableStateOf(false) }
    LaunchedEffect(scrolling, dragY != null) {
        if (scrolling || dragY != null) visible = true
        else { delay(1500); visible = false }
    }
    val handleH = with(density) { 48.dp.toPx() }
    val labelH = with(density) { 20.dp.toPx() }
    val track = (height - handleH).coerceAtLeast(1f)
    val frac = dragY?.let { (it / track).coerceIn(0f, 1f) } ?: (first.toFloat() / (total - 1).coerceAtLeast(1))
    Box(modifier.fillMaxSize().onSizeChanged { height = it.height }) {
        if (!visible) return@Box
        // Buchstaben am Rand, so viele, wie ohne Überlappen passen
        if (dragY != null) {
            var last = -labelH
            for ((i, l) in starts) {
                val top = i.toFloat() / (total - 1).coerceAtLeast(1) * track + (handleH - labelH) / 2
                if (top - last < labelH) continue
                last = top
                Text(l, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurface,
                    modifier = Modifier.align(Alignment.TopEnd).offset { IntOffset(-with(density) { 44.dp.roundToPx() }, top.toInt()) }
                        .background(MaterialTheme.colorScheme.surface.copy(alpha = 0.85f), RoundedCornerShape(10.dp)).padding(horizontal = 6.dp))
            }
        }
        val y = (frac * track).toInt()
        dragY?.let {
            val idx = (frac * (total - 1)).toInt().coerceIn(0, total - 1)
            Text(letters[idx], style = MaterialTheme.typography.headlineSmall, color = MaterialTheme.colorScheme.onPrimaryContainer,
                modifier = Modifier.align(Alignment.TopEnd).offset { IntOffset(-with(density) { 84.dp.roundToPx() }, y) }
                    .background(MaterialTheme.colorScheme.primaryContainer, RoundedCornerShape(24.dp)).padding(horizontal = 18.dp, vertical = 6.dp))
        }
        Box(Modifier.align(Alignment.TopEnd).offset { IntOffset(0, y) }.size(width = 36.dp, height = 48.dp)
            .background(MaterialTheme.colorScheme.primaryContainer, RoundedCornerShape(topStart = 24.dp, bottomStart = 24.dp))
            .pointerInput(total) {
                detectVerticalDragGestures(
                    onDragStart = { dragY = frac * track },
                    onDragEnd = { dragY = null }, onDragCancel = { dragY = null }) { change, amount ->
                    change.consume()
                    val ny = ((dragY ?: 0f) + amount).coerceIn(0f, track)
                    dragY = ny
                    val target = ((ny / track) * (total - 1)).toInt()
                    scope.launch { scrollTo(target) }
                }
            }, contentAlignment = Alignment.Center) {
            Icon(Icons.Default.UnfoldMore, contentDescription = null, tint = MaterialTheme.colorScheme.onPrimaryContainer)
        }
    }
}

/** Playlists und Auto-Playlists; die Suche filtert nach Namen. */
@Composable
private fun PlaylistsTab(autoKinds: List<String>, playlists: List<PlaylistInfo>, query: String, tiles: Boolean, open: (String, String) -> Unit) {
    val words = query.lowercase().split(Regex("\\s+")).filter { it.isNotEmpty() }
    fun match(name: String) = words.all { name.lowercase().contains(it) }
    val autos = autoKinds.map { it to stringResource(autoLabel(it)) }.filter { match(it.second) }
    val lists = playlists.filter { match(it.name) }
    if (words.isNotEmpty() && autos.isEmpty() && lists.isEmpty()) {
        Text(stringResource(R.string.audio_no_results), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant); return
    }
    if (tiles) LazyVerticalGrid(GridCells.Adaptive(150.dp), Modifier.fillMaxSize(), contentPadding = PaddingValues(12.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
        gridItems(autos, key = { "auto:" + it.first }) { (k, name) ->
            Column(Modifier.clickable { open("auto:$k", name) }) {
                Square { size ->
                    Box(Modifier.size(size).clip(RoundedCornerShape(10.dp)).background(MaterialTheme.colorScheme.primaryContainer), contentAlignment = Alignment.Center) {
                        Icon(autoIcon(k), null, tint = MaterialTheme.colorScheme.onPrimaryContainer, modifier = Modifier.size(size / 3))
                    }
                }
                Text(name, Modifier.padding(top = 4.dp), style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        gridItems(lists, key = { it.id }) { p ->
            Column(Modifier.clickable { open(p.id, p.name) }) {
                Square { AudioCover(p.cover, it) }
                Text(p.name, Modifier.padding(top = 4.dp), style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(stringResource(R.string.audio_n_tracks, p.count), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
    else LazyColumn(Modifier.fillMaxSize()) {
        items(autos, key = { "auto:" + it.first }) { (k, name) ->
            ListItem(modifier = Modifier.clickable { open("auto:$k", name) },
                leadingContent = { Icon(autoIcon(k), null, tint = MaterialTheme.colorScheme.primary, modifier = Modifier.size(28.dp)) },
                headlineContent = { Text(name) })
        }
        if (autos.isNotEmpty() && lists.isNotEmpty()) item { HorizontalDivider() }
        items(lists, key = { it.id }) { p ->
            ListItem(modifier = Modifier.clickable { open(p.id, p.name) },
                leadingContent = { AudioCover(p.cover, 44.dp) },
                headlineContent = { Text(p.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
                supportingContent = { Text(stringResource(R.string.audio_n_tracks, p.count)) })
        }
        if (playlists.isEmpty()) item { Text(stringResource(R.string.audio_no_playlists), Modifier.padding(16.dp), color = MaterialTheme.colorScheme.onSurfaceVariant) }
    }
}

private fun albumSub(a: Album) = listOfNotNull(a.artist.takeIf { it.isNotEmpty() }, a.year?.toString()).joinToString(" · ")

@Composable
private fun AlbumTile(a: Album, onClick: () -> Unit) {
    Column(Modifier.clickable(onClick = onClick)) {
        Square { AudioCover(a.cover, it) }
        Text(a.name, Modifier.padding(top = 4.dp), style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(albumSub(a), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun AlbumRow(a: Album, onClick: () -> Unit) {
    ListItem(modifier = Modifier.clickable(onClick = onClick),
        leadingContent = { AudioCover(a.cover, 48.dp) },
        headlineContent = { Text(a.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = { Text(albumSub(a), maxLines = 1, overflow = TextOverflow.Ellipsis) },
        trailingContent = { Text(a.tracks.size.toString(), style = MaterialTheme.typography.bodySmall) })
}

@Composable
private fun groupSub(g: Group) = stringResource(R.string.audio_n_albums, g.albums.size) + " · " + stringResource(R.string.audio_n_tracks, g.tracks.size)

@Composable
private fun GroupTile(g: Group, onClick: () -> Unit) {
    Column(Modifier.clickable(onClick = onClick)) {
        Square { CoverMosaic(g.covers, it) }
        Text(g.name, Modifier.padding(top = 4.dp), style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
        Text(groupSub(g), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

@Composable
private fun GroupRow(g: Group, onClick: () -> Unit) {
    ListItem(modifier = Modifier.clickable(onClick = onClick),
        leadingContent = { CoverMosaic(g.covers, 48.dp) },
        headlineContent = { Text(g.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = { Text(groupSub(g), maxLines = 1, overflow = TextOverflow.Ellipsis) })
}

@Composable
private fun TrackTile(t: Track, current: Boolean, onClick: () -> Unit) {
    Column(Modifier.clickable(onClick = onClick)) {
        Square { AudioCover(t.cover, it) }
        Text(t.title, Modifier.padding(top = 4.dp), style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis,
            color = if (current) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface)
        Text(t.artistShown, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
    }
}

private fun autoLabel(k: String) = when (k) {
    "added" -> R.string.audio_auto_added; "recent" -> R.string.audio_auto_recent; "most" -> R.string.audio_auto_most
    "rare" -> R.string.audio_auto_rare; else -> R.string.audio_auto_never
}

private fun autoIcon(k: String) = when (k) {
    "added" -> Icons.Default.AutoAwesome; "recent" -> Icons.Default.History; "most" -> Icons.Default.TrendingUp
    "rare" -> Icons.Default.HourglassEmpty; else -> Icons.Default.VisibilityOff
}

/**
 * Laufender Titel über der Leiste unten - in allen Bereichen, solange etwas geladen ist. Tippen öffnet den Player mit
 * Spulen, Zufall, Wiederholen und Warteschlange.
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AudioMiniBar() {
    val ctx = LocalContext.current
    val app = App.get(ctx)
    val enabled by app.audio.enabled.collectAsState()
    LaunchedEffect(enabled) { if (enabled) AudioPlayer.connect(ctx) }
    val now by AudioPlayer.now.collectAsState()
    var sheet by remember { mutableStateOf(false) }
    if (!enabled || now.id == null) return
    var pos by remember { mutableLongStateOf(0L) }
    LaunchedEffect(now.id, now.playing) { while (true) { pos = AudioPlayer.position(); delay(1000) } }
    Surface(tonalElevation = 3.dp) {
        Column {
            // Fortschritt als dünner Strich
            Box(Modifier.fillMaxWidth().height(2.dp).background(MaterialTheme.colorScheme.surfaceVariant)) {
                Box(Modifier.fillMaxWidth(if (now.duration > 0) (pos.toFloat() / now.duration).coerceIn(0f, 1f) else 0f).height(2.dp).background(MaterialTheme.colorScheme.primary))
            }
            Row(Modifier.fillMaxWidth().clickable { sheet = true }.padding(horizontal = 10.dp, vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                AudioCover(now.cover, 40.dp)
                Column(Modifier.weight(1f).padding(horizontal = 10.dp)) {
                    Text(now.title, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyMedium, fontWeight = FontWeight.SemiBold)
                    Text(listOf(now.artist, now.album).filter { it.isNotEmpty() }.joinToString(" · "), maxLines = 1, overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                // Zurück: wie im Player (am Anfang des Titels der vorige, sonst an den Anfang)
                IconButton(onClick = { AudioPlayer.prev(ctx) }) { Icon(Icons.Default.SkipPrevious, stringResource(R.string.audio_prev)) }
                IconButton(onClick = { AudioPlayer.toggle(ctx) }) {
                    Icon(if (now.playing) Icons.Default.Pause else Icons.Default.PlayArrow, stringResource(if (now.playing) R.string.audio_pause else R.string.audio_play))
                }
                IconButton(onClick = { AudioPlayer.next(ctx) }, enabled = now.index < now.count - 1 || now.repeat == Player.REPEAT_MODE_ALL) {
                    Icon(Icons.Default.SkipNext, stringResource(R.string.audio_next))
                }
                // Beenden: Wiedergabe stoppen und Warteschlange leeren - die Leiste verschwindet
                IconButton(onClick = { AudioPlayer.stop(ctx) }) { Icon(Icons.Default.Close, stringResource(R.string.audio_stop)) }
            }
        }
    }
    if (sheet) ModalBottomSheet(onDismissRequest = { sheet = false }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)) { PlayerSheet() }
}

/** Großer Player: Cover, Spulen, Steuerung, Warteschlange. */
@Composable
private fun PlayerSheet() {
    val ctx = LocalContext.current
    val now by AudioPlayer.now.collectAsState()
    val queue by AudioPlayer.queue.collectAsState()
    var pos by remember { mutableLongStateOf(AudioPlayer.position()) }
    var dragging by remember { mutableStateOf(false) }
    var dragTo by remember { mutableFloatStateOf(0f) }
    LaunchedEffect(now.id) { while (true) { if (!dragging) pos = AudioPlayer.position(); delay(500) } }
    val listState = rememberLazyListState()
    val scope = rememberCoroutineScope()
    LaunchedEffect(Unit) { if (now.index > 0) scope.launch { listState.scrollToItem(now.index + 1) } }
    LazyColumn(Modifier.fillMaxWidth(), state = listState) {
        item {
            Column(Modifier.fillMaxWidth().padding(horizontal = 24.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                AudioCover(now.cover, 260.dp)
                Spacer(Modifier.height(16.dp))
                Text(now.title, style = MaterialTheme.typography.titleLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(listOf(now.artist, now.album).filter { it.isNotEmpty() }.joinToString(" · "), style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
                val dur = now.duration.coerceAtLeast(1)
                Slider(value = if (dragging) dragTo else (pos.toFloat() / dur).coerceIn(0f, 1f),
                    onValueChange = { dragging = true; dragTo = it },
                    onValueChangeFinished = { AudioPlayer.seek(ctx, (dragTo * dur).toLong()); pos = (dragTo * dur).toLong(); dragging = false })
                Row(Modifier.fillMaxWidth()) {
                    Text(fmtDur(if (dragging) (dragTo * dur).toLong() else pos), style = MaterialTheme.typography.bodySmall)
                    Spacer(Modifier.weight(1f))
                    Text(fmtDur(now.duration), style = MaterialTheme.typography.bodySmall)
                }
                Row(Modifier.fillMaxWidth().padding(vertical = 8.dp), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
                    IconButton(onClick = { AudioPlayer.setShuffle(ctx, !now.shuffle) }) {
                        Icon(Icons.Default.Shuffle, stringResource(R.string.audio_shuffle), tint = if (now.shuffle) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    IconButton(onClick = { AudioPlayer.prev(ctx) }) { Icon(Icons.Default.SkipPrevious, stringResource(R.string.audio_prev), Modifier.size(32.dp)) }
                    FilledIconButton(onClick = { AudioPlayer.toggle(ctx) }, Modifier.size(64.dp)) {
                        Icon(if (now.playing) Icons.Default.Pause else Icons.Default.PlayArrow, stringResource(if (now.playing) R.string.audio_pause else R.string.audio_play), Modifier.size(36.dp))
                    }
                    IconButton(onClick = { AudioPlayer.next(ctx) }) { Icon(Icons.Default.SkipNext, stringResource(R.string.audio_next), Modifier.size(32.dp)) }
                    IconButton(onClick = { AudioPlayer.cycleRepeat(ctx) }) {
                        Icon(if (now.repeat == Player.REPEAT_MODE_ONE) Icons.Default.RepeatOne else Icons.Default.Repeat,
                            stringResource(when (now.repeat) { Player.REPEAT_MODE_ONE -> R.string.audio_repeat_one; Player.REPEAT_MODE_ALL -> R.string.audio_repeat_all; else -> R.string.audio_repeat_off }),
                            tint = if (now.repeat != Player.REPEAT_MODE_OFF) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                Row(Modifier.fillMaxWidth().padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.AutoMirrored.Filled.QueueMusic, null, tint = MaterialTheme.colorScheme.primary)
                    Spacer(Modifier.width(8.dp))
                    Text(stringResource(R.string.audio_queue), style = MaterialTheme.typography.titleSmall)
                }
            }
        }
        itemsIndexed(queue, key = { i, m -> "$i/${m.mediaId}" }) { i, m ->
            val md = m.mediaMetadata
            ListItem(modifier = Modifier.clickable { AudioPlayer.jump(ctx, i) },
                leadingContent = { AudioCover(md.extras?.getString("cover"), 40.dp) },
                headlineContent = { Text(md.title?.toString() ?: "", maxLines = 1, overflow = TextOverflow.Ellipsis,
                    fontWeight = if (i == now.index) FontWeight.SemiBold else null, color = if (i == now.index) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurface) },
                supportingContent = { Text(md.artist?.toString() ?: "", maxLines = 1, overflow = TextOverflow.Ellipsis) },
                trailingContent = { if (i != now.index) IconButton(onClick = { AudioPlayer.remove(ctx, i) }) { Icon(Icons.Default.Close, stringResource(R.string.audio_remove)) } })
        }
        item { Spacer(Modifier.height(24.dp)) }
    }
}
