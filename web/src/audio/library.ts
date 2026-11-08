// Audiothek: Titelliste vom Server (mit ETag, im Speicher gehalten, solange der Tab offen ist), Hörverlauf,
// Erreichbarkeit der Audiothek-Ordner und die Gruppierung nach Album, Interpret und Genre.
import { useEffect, useState } from 'react'
import { api, ApiError } from '../api'
import { e2eFetch } from '../e2e'
import { t } from '../i18n'

export interface Track {
  id: string; title: string; artist: string | null; albumArtist: string | null; album: string | null
  track: number | null; disc: number | null; year: number | null; genre: string | null; duration: number | null
  cover: string | null; modified: number
  /** Kennung des Audiothek-Ordners (vor dem ersten „/“ der Kennung) */
  source: string
  /** Kleingeschrieben, ohne Akzente: alles, was die Suche findet */
  text: string
}

/** Hörverlauf je Titel: gehört, übersprungen, zuletzt gehört (ms) */
export interface Stat { plays: number; skips: number; last: number | null }

export interface Album { key: string; name: string; artist: string; year: number | null; cover: string | null; tracks: Track[] }
export interface Group { name: string; tracks: Track[]; albums: Album[] }

export const norm = (s: string) => s.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '')

export const coverUrl = (id: string) => '/api/audio/cover/' + encodeURIComponent(id)
export const streamUrl = (id: string) => '/api/audio/stream?id=' + encodeURIComponent(id)

/** 3:07, 1:02:15 */
export function fmtDuration(sec: number | null | undefined): string {
  if (sec == null || !isFinite(sec)) return ''
  const s = Math.floor(sec), h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), r = s % 60
  return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(r).padStart(2, '0')}` : `${m}:${String(r).padStart(2, '0')}`
}

/** Interpret für Listen und Gruppen: Album-Interpret, sonst Interpret */
export const artistOf = (x: Track) => x.albumArtist || x.artist || ''

/** Genres eines Titels (mehrere mit „;“ getrennt) */
export const genresOf = (x: Track) => (x.genre ?? '').split(';').map(g => g.trim()).filter(Boolean)

/** Ordner der Datei - fasst Alben ohne Album-Interpret (Sampler) zusammen; „CD1“, „Disc 2“ zählen zum selben Album */
const folderOf = (x: Track) => {
  const p = x.id.slice(0, x.id.lastIndexOf('/'))
  return norm(p.replace(/\/(cd|disc|disk)\s*\d+$/i, ''))
}

// ---- Laden ----

let cache: { etag: string | null; tracks: Track[] } | null = null
const listeners = new Set<() => void>()

function decode(fields: string[], rows: unknown[][]): Track[] {
  const at = (n: string) => fields.indexOf(n)
  const ix = { id: at('id'), title: at('title'), artist: at('artist'), albumArtist: at('albumArtist'), album: at('album'), track: at('track'), disc: at('disc'),
    year: at('year'), genre: at('genre'), duration: at('duration'), cover: at('cover'), modified: at('modified') }
  return rows.map(r => {
    const id = r[ix.id] as string
    const x: Track = {
      id, title: (r[ix.title] as string) || id.slice(id.lastIndexOf('/') + 1), artist: r[ix.artist] as string | null, albumArtist: r[ix.albumArtist] as string | null,
      album: r[ix.album] as string | null, track: r[ix.track] as number | null, disc: r[ix.disc] as number | null, year: r[ix.year] as number | null,
      genre: r[ix.genre] as string | null, duration: r[ix.duration] as number | null, cover: r[ix.cover] as string | null, modified: r[ix.modified] as number,
      source: id.slice(0, id.indexOf('/')), text: '',
    }
    x.text = norm([x.title, x.artist, x.albumArtist, x.album, x.genre, x.year].filter(Boolean).join(' '))
    return x
  })
}

/** Titelliste holen; unverändert (304) bleibt die im Speicher. */
export async function refreshTracks(): Promise<Track[]> {
  const r = await e2eFetch('/api/audio/tracks', { credentials: 'same-origin', headers: cache?.etag ? { 'If-None-Match': cache.etag } : {} })
  if (r.status === 304 && cache) return cache.tracks
  const data = await r.json().catch(() => null)
  if (!r.ok) throw new ApiError(data?.error || `${r.status} ${r.statusText}`, r.status, data)
  cache = { etag: r.headers.get('ETag'), tracks: decode(data.fields, data.tracks) }
  listeners.forEach(f => f())
  return cache.tracks
}

/** Titelliste als Hook: sofort aus dem Speicher, dann vom Server; alle 60 s nachgefragt (neu eingelesene Titel). */
export function useTracks(onError: (e: unknown) => void): Track[] | null {
  const [tracks, setTracks] = useState<Track[] | null>(cache?.tracks ?? null)
  useEffect(() => {
    const on = () => setTracks(cache?.tracks ?? null)
    listeners.add(on)
    let alive = true
    const load = () => refreshTracks().then(x => { if (alive) setTracks(x) }).catch(e => { if (alive) onError(e) })
    load()
    const h = window.setInterval(load, 60_000)
    return () => { alive = false; listeners.delete(on); clearInterval(h) }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  return tracks
}

// ---- Hörverlauf ----

let stats = new Map<string, Stat>()
const statListeners = new Set<() => void>()

export async function refreshStats() {
  const r = await api.get<Record<string, [number, number, number | null]>>('/api/audio/stats')
  stats = new Map(Object.entries(r).map(([k, v]) => [k, { plays: v[0], skips: v[1], last: v[2] }]))
  statListeners.forEach(f => f())
}

export function setStat(id: string, s: Stat) {
  stats = new Map(stats).set(id, s)
  statListeners.forEach(f => f())
}

export function useStats(): Map<string, Stat> {
  const [s, setS] = useState(stats)
  useEffect(() => {
    const on = () => setS(stats)
    statListeners.add(on)
    refreshStats().catch(() => {})
    return () => { statListeners.delete(on) }
  }, [])
  return s
}

// ---- Audiothek-Ordner ----

export interface Mount { id: string; name: string; kind: 'folder' | 'webdav' | 'onedrive' | 'pcloud'; readOnly: boolean }
export interface AudioSource {
  id: string; mount: string; mountName: string | null; path: string; available: boolean; watch: boolean
  lastScan: string | null; error: string | null; count: number; scanning: boolean; pending: number
}

/** Audiothek-Ordner, deren externer Ordner gerade nicht erreichbar ist - deren Titel lassen sich nicht abspielen. */
export const offline = new Set<string>()

export async function loadSources() {
  const d = await api.get<{ sources: AudioSource[]; mounts: Mount[] }>('/api/audio/sources')
  offline.clear()
  for (const x of d.sources) if (!x.available) offline.add(x.id)
  return d
}

/** Erreichbarkeit alle 60 s neu (wie die Galerie). Gibt einen Zähler zurück, der sich mit jedem Stand ändert. */
export function useReach(): number {
  const [n, setN] = useState(0)
  useEffect(() => {
    let alive = true
    const load = () => loadSources().then(() => { if (alive) setN(x => x + 1) }).catch(() => {})
    load()
    const h = window.setInterval(load, 60_000)
    return () => { alive = false; clearInterval(h) }
  }, [])
  return n
}

export const sourceName = (x: AudioSource, mounts: Mount[]) =>
  (x.mountName ?? mounts.find(m => m.id === x.mount)?.name ?? x.mount) + (x.path ? ' › ' + x.path.split('/').join(' › ') : '')

// ---- Gruppen ----

/** Reihenfolge innerhalb eines Albums: CD, Titelnummer, Name */
export const byAlbumOrder = (a: Track, b: Track) =>
  (a.disc ?? 1) - (b.disc ?? 1) || (a.track ?? 9999) - (b.track ?? 9999) || a.id.localeCompare(b.id)

/** Bibliotheksreihenfolge: Interpret, Album, CD, Titelnummer */
export const byLibrary = (a: Track, b: Track) =>
  artistOf(a).localeCompare(artistOf(b)) || (a.album ?? '').localeCompare(b.album ?? '') || byAlbumOrder(a, b)

/** Schlüssel des Albums eines Titels (wie in albumsOf - auch für Verweise auf das Album) */
export const albumKey = (x: Track) => norm(x.album ?? '') + '|' + (x.albumArtist ? norm(x.albumArtist) : folderOf(x))

/** Bis zu vier Cover verschiedener Alben (Kacheln für Interpreten und Genres) */
export function coversOf(tracks: Track[]): string[] {
  const seen = new Set<string>(), out: string[] = []
  for (const x of tracks) {
    if (!x.cover) continue
    const k = albumKey(x)
    if (seen.has(k)) continue
    seen.add(k); out.push(x.cover)
    if (out.length === 4) break
  }
  return out
}

export function albumsOf(tracks: Track[]): Album[] {
  const map = new Map<string, Track[]>()
  for (const x of tracks) {
    const key = albumKey(x)
    const list = map.get(key)
    if (list) list.push(x); else map.set(key, [x])
  }
  return [...map.entries()].map(([key, list]) => {
    list.sort(byAlbumOrder)
    const artists = new Set(list.map(x => x.albumArtist || x.artist || '').filter(Boolean))
    const first = list[0]
    return {
      key, name: first.album || t('Unbekanntes Album'),
      artist: first.albumArtist || (artists.size === 1 ? [...artists][0] : artists.size > 1 ? t('Verschiedene Interpreten') : ''),
      year: list.find(x => x.year)?.year ?? null, cover: list.find(x => x.cover)?.cover ?? null, tracks: list,
    }
  }).sort((a, b) => a.artist.localeCompare(b.artist) || (a.year ?? 0) - (b.year ?? 0) || a.name.localeCompare(b.name))
}

export function artistsOf(tracks: Track[]): Group[] {
  const map = new Map<string, { name: string; tracks: Track[] }>()
  for (const x of tracks) {
    const names = new Set([x.albumArtist, x.artist].filter((n): n is string => !!n))
    for (const name of names.size ? names : [t('Unbekannter Interpret')]) {
      const k = norm(name)
      const g = map.get(k)
      if (g) g.tracks.push(x); else map.set(k, { name, tracks: [x] })
    }
  }
  return [...map.values()].map(g => ({ ...g, albums: albumsOf(g.tracks) })).sort((a, b) => a.name.localeCompare(b.name))
}

export function genresList(tracks: Track[]): Group[] {
  const map = new Map<string, { name: string; tracks: Track[] }>()
  for (const x of tracks) {
    const gs = genresOf(x)
    for (const name of gs.length ? gs : [t('Ohne Genre')]) {
      const k = norm(name)
      const g = map.get(k)
      if (g) g.tracks.push(x); else map.set(k, { name, tracks: [x] })
    }
  }
  return [...map.values()].map(g => ({ ...g, albums: [] })).sort((a, b) => a.name.localeCompare(b.name))
}

/** Suche: jedes Wort muss in Titel, Interpret, Album, Genre oder Jahr vorkommen */
export function search(tracks: Track[], query: string): Track[] {
  const words = norm(query).split(/\s+/).filter(Boolean)
  if (!words.length) return tracks
  return tracks.filter(x => words.every(w => x.text.includes(w)))
}

// ---- Playlists ----

export interface PlaylistInfo { id: string; name: string; count: number; modified: number; cover: string | null }

let playlists: PlaylistInfo[] | null = null
let autoKinds: string[] = []
const playlistListeners = new Set<() => void>()

/** Eigene Playlists neu vom Server (nach jeder Änderung) */
export async function refreshPlaylists() {
  const r = await api.get<{ playlists: PlaylistInfo[]; auto: string[] }>('/api/audio/playlists')
  playlists = r.playlists
  autoKinds = r.auto
  playlistListeners.forEach(f => f())
}

export function usePlaylists(): { playlists: PlaylistInfo[]; auto: string[] } {
  const [, setN] = useState(0)
  useEffect(() => {
    const on = () => setN(x => x + 1)
    playlistListeners.add(on)
    refreshPlaylists().catch(() => {})
    return () => { playlistListeners.delete(on) }
  }, [])
  return { playlists: playlists ?? [], auto: autoKinds }
}

/** Name der Auto-Playlists */
export const autoName = (kind: string) => ({
  added: t('Zuletzt hinzugefügt'), recent: t('Zuletzt gehört'), most: t('Am meisten gehört'), rare: t('Selten gehört'), never: t('Nie gehört'),
} as Record<string, string>)[kind] ?? kind

export const playlistApi = {
  get: (id: string) => api.get<{ id: string; name: string; tracks: string[] }>('/api/audio/playlists/' + encodeURIComponent(id)),
  auto: (kind: string) => api.get<{ tracks: string[] }>('/api/audio/playlists/auto/' + encodeURIComponent(kind)),
  create: async (name: string, tracks: string[] = []) => {
    const r = await api.post<{ id: string }>('/api/audio/playlists', { name, tracks })
    await refreshPlaylists()
    return r.id
  },
  add: async (id: string, tracks: string[]) => { await api.post('/api/audio/playlists/' + encodeURIComponent(id) + '/add', { tracks }); await refreshPlaylists() },
  setTracks: async (id: string, tracks: string[]) => { await api.put('/api/audio/playlists/' + encodeURIComponent(id), { tracks }); await refreshPlaylists() },
  rename: async (id: string, name: string) => { await api.put('/api/audio/playlists/' + encodeURIComponent(id), { name }); await refreshPlaylists() },
  remove: async (id: string) => { await api.del('/api/audio/playlists/' + encodeURIComponent(id)); await refreshPlaylists() },
}
