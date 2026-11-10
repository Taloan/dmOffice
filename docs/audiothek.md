# Music library

English · [Deutsch](audiothek.de.md)

Status: version 2, stages 1 to 3 (web with playlists, Android app) ([concept, German](audiothek-konzept.md)).

The music library plays your own music collection from external folders. dmOffice only reads the files: it does not rename
anything and does not write tags.

## Turning it on

The music library is off until the administrator turns it on: *Administration* → *Services* → *Music library*. It is then
available to every user with file storage. While it is off, scanned tracks and listening history are kept.

## Music folders

*Music library* → *Music folders*: pick an external folder (a server folder shared by the administrator or your own WebDAV
folder, see [External folders](externe-ordner.md)) and, within it, the folder your music is in. Subfolders are included;
hidden folders (`.name`, `@eaDir`, `#recycle`) are skipped.

- **File types:** MP3, M4A, AAC, FLAC, OGG and Opus. The browser plays them directly; nothing is converted.
- **Scanning:** first the list (title from the file name, album from the folder name), then title, artist, album, track and disc
  number, year, genre, duration and cover art from the files. Without embedded cover art dmOffice uses `cover`, `folder`,
  `front`, `album` or `albumart` (JPG or PNG) from the folder.
- **Watch:** rescans every 15 minutes; new tracks appear, deleted ones disappear. Without *Watch* only on request.
- **Not reachable:** if the external folder cannot be reached, its tracks stay in the list, greyed out with a red stop sign,
  and cannot be played.

## Views and search

- **Tracks, Albums, Artists, Genres** in the pane on the left; click an album, artist or genre to see its tracks.
- **Search** finds every word in title, artist, album, genre and year ("beatles 1969").
- A track list can be sorted by clicking a column header. Double-click plays the list from that track (phone: tap).
  Ctrl-click and Shift-click select several tracks, Ctrl+A selects all. The context menu offers *Play next*, *Add to queue*,
  *Add to playlist*, *Open album* and *Open artist*.

## Player

The player bar at the bottom stays visible in all areas and keeps playing when you switch to mail or calendar. It has shuffle,
previous/next, repeat (off, all, this track), position and volume. Media keys and the lock screen of the installed web app work
via the browser's Media Session.

The **queue** (*Music library* → *Queue*, or a click on the player bar) shows what plays next: click a track to jump to it,
drag to reorder, × removes it. The queue is kept in the browser across reloads.

## Playlists

**Own playlists** are listed under *Playlists* on the left; *+* or *New playlist* in the ribbon creates one.

- Add tracks with *Add to playlist* in the context menu (also for a selection), with the *Add to playlist* button of an album,
  artist or genre, or by dragging selected tracks onto a playlist on the left. *Save as playlist* in the queue saves what is queued.
- In an open playlist, drag tracks to reorder them; *Remove from playlist* (context menu) or the Delete key takes them out.
  The same track may appear more than once. *Rename* and *Delete* are next to *Play*; deleting a playlist leaves the music files alone.
- Like the listening history, entries belong to the external folder and path: if a music folder is removed, its tracks
  disappear from the playlist and come back when the folder is added again.

**Auto playlists** are built from the listening history, at most 100 tracks each:

| Auto playlist | Contents |
|---|---|
| Recently added | newest files first (file date) |
| Recently played | last played first |
| Most played | most plays first |
| Rarely played | played before, but not in the last 90 days, fewest plays first |
| Never played | never counted as played, in random order |

Per-genre lists are under *Genres*.

## Android app

The app shows the music library as its own area (*Music library* in the bar at the bottom, or under *More*) when the server
offers it. Three views: **Playlists** (auto playlists and your own), **Albums** and **Tracks**; the search field searches
all tracks. Tapping a track plays the list from there; an album or playlist has *Play* and *Shuffle*.

- It keeps playing in the background, with a notification, on the lock screen and with Bluetooth buttons.
- The current track is shown above the bar at the bottom in every area; tapping it opens the player with position,
  shuffle, repeat and the queue (tap to jump, × removes).
- What you listen to counts towards the listening history; without network the app reports it later.
- Playlists are created and edited in the web interface.
- Home screen widget **dmOffice Music**: cover, title, artist, previous, play/pause and next; tapping it opens the music library.

## Listening history

A track counts as played after half its duration, at most 4 minutes; stopped earlier it counts as skipped. The *Plays* column
shows the count, the tooltip the last time. The history is stored per user and stays when a music folder is removed and added again.
