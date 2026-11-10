// Player der Audiothek: ein <audio>-Element außerhalb von React - spielt weiter, wenn man den Bereich wechselt.
// Warteschlange, Zufall, Wiederholen, Lautstärke; Medientasten und Sperrbildschirm über die Media Session API.
// Wie lange ein Titel lief, meldet er beim Wechsel an den Server (Hörverlauf: gehört ab halber Dauer bzw. 4 Minuten).
import { useSyncExternalStore } from 'react'
import { api } from '../api'
import { t } from '../i18n'
import { coverUrl, setStat, Stat, streamUrl, Track } from './library'

export interface QTrack { id: string; title: string; artist: string | null; album: string | null; cover: string | null; duration: number | null }
export type Repeat = 'off' | 'all' | 'one'

export interface PlayerState {
  queue: QTrack[]; index: number; playing: boolean; position: number; duration: number
  shuffle: boolean; repeat: Repeat; volume: number; error: string | null
}

const STORE = 'dmoffice.audio.player'
const MAX_QUEUE = 5000

let state: PlayerState = { queue: [], index: -1, playing: false, position: 0, duration: 0, shuffle: false, repeat: 'off', volume: 1, error: null }
/** Reihenfolge vor dem Mischen - beim Ausschalten von Zufall zurück */
let unshuffled: QTrack[] | null = null
let audio: HTMLAudioElement | null = null
/** Wie lange der aktuelle Titel tatsächlich lief (ohne Spulen) */
let listened = 0
let lastTime = 0
let lastSaved = 0
const listeners = new Set<() => void>()

const toQ = (x: Track): QTrack => ({ id: x.id, title: x.title, artist: x.artist || x.albumArtist, album: x.album, cover: x.cover, duration: x.duration })

function set(patch: Partial<PlayerState>) {
  state = { ...state, ...patch }
  listeners.forEach(f => f())
}

function persist() {
  try {
    localStorage.setItem(STORE, JSON.stringify({ queue: state.queue, index: state.index, position: Math.floor(state.position), shuffle: state.shuffle, repeat: state.repeat, volume: state.volume }))
  } catch { /* privat */ }
}

function restore() {
  try {
    const s = JSON.parse(localStorage.getItem(STORE) ?? 'null')
    if (s && Array.isArray(s.queue)) state = { ...state, queue: s.queue, index: Math.min(s.index ?? -1, s.queue.length - 1), position: s.position ?? 0,
      shuffle: !!s.shuffle, repeat: s.repeat ?? 'off', volume: typeof s.volume === 'number' ? s.volume : 1 }
  } catch { /* nichts gemerkt */ }
}
restore()

function el(): HTMLAudioElement {
  if (audio) return audio
  const a = new Audio()
  a.preload = 'auto'
  a.volume = state.volume
  a.addEventListener('timeupdate', () => {
    const now = a.currentTime
    // Nur echtes Abspielen zählt, Sprünge (Spulen) nicht
    if (!a.paused && now > lastTime && now - lastTime < 2) listened += now - lastTime
    lastTime = now
    set({ position: now })
    if (Date.now() - lastSaved > 10_000) { lastSaved = Date.now(); persist() }
  })
  a.addEventListener('seeked', () => { lastTime = a.currentTime })
  a.addEventListener('durationchange', () => set({ duration: isFinite(a.duration) ? a.duration : 0 }))
  a.addEventListener('play', () => { set({ playing: true, error: null }); media() })
  a.addEventListener('pause', () => { set({ playing: false }); persist(); media() })
  a.addEventListener('ended', () => ended())
  a.addEventListener('error', () => {
    if (!a.src) return
    set({ playing: false, error: t('Dieser Titel lässt sich gerade nicht abspielen.') })
    // Nächsten versuchen (etwa externer Ordner nicht erreichbar), aber nicht endlos
    if (state.index < state.queue.length - 1) window.setTimeout(() => { if (state.error) go(state.index + 1, true) }, 1500)
  })
  // Seite wird geschlossen: Gehörtes noch melden
  window.addEventListener('pagehide', () => { report(); persist() })
  audio = a
  return a
}

/** Gehörtes des aktuellen Titels an den Server (ab 1 s), dann zurücksetzen. */
function report() {
  const cur = state.queue[state.index]
  const ms = Math.round(listened * 1000)
  listened = 0
  if (!cur || ms < 1000) return
  api.post<Stat>('/api/audio/played', { id: cur.id, ms, device: 'Web' }).then(s => setStat(cur.id, s)).catch(() => {})
}

function load(index: number, autoplay: boolean, at = 0) {
  const a = el()
  const cur = state.queue[index]
  if (!cur) return
  lastTime = at
  a.src = streamUrl(cur.id)
  if (at > 0) a.currentTime = at
  set({ index, position: at, duration: cur.duration ?? 0, error: null })
  persist()
  media()
  if (autoplay) a.play().catch(() => set({ playing: false }))
}

function go(index: number, autoplay = true) {
  report()
  if (index < 0 || index >= state.queue.length) return
  load(index, autoplay)
}

function ended() {
  if (state.repeat === 'one') { report(); load(state.index, true); return }
  if (state.index < state.queue.length - 1) go(state.index + 1)
  else if (state.repeat === 'all' && state.queue.length) go(0)
  else { report(); set({ playing: false, position: 0 }); if (audio) audio.currentTime = 0; persist() }
}

function shuffled<T>(list: T[]): T[] {
  const a = [...list]
  for (let i = a.length - 1; i > 0; i--) { const j = Math.floor(Math.random() * (i + 1)); [a[i], a[j]] = [a[j], a[i]] }
  return a
}

// ---- Media Session: Medientasten, Sperrbildschirm, Benachrichtigung ----

let handlers = false
function media() {
  const ms = navigator.mediaSession
  if (!ms) return
  const cur = state.queue[state.index]
  try {
    ms.metadata = cur ? new MediaMetadata({
      title: cur.title, artist: cur.artist ?? '', album: cur.album ?? '',
      artwork: cur.cover ? [{ src: new URL(coverUrl(cur.cover), location.href).href, sizes: '300x300', type: 'image/jpeg' }] : [],
    }) : null
    ms.playbackState = state.playing ? 'playing' : cur ? 'paused' : 'none'
    if (handlers) return
    handlers = true
    ms.setActionHandler('play', () => player.toggle(true))
    ms.setActionHandler('pause', () => player.toggle(false))
    ms.setActionHandler('previoustrack', () => player.prev())
    ms.setActionHandler('nexttrack', () => player.next())
    ms.setActionHandler('seekto', d => { if (d.seekTime != null) player.seek(d.seekTime) })
  } catch { /* Browser ohne Teile der API */ }
}

// ---- Bedienung ----

export const player = {
  /** Warteschlange jetzt (zum Speichern als Playlist) */
  queue: (): QTrack[] => state.queue,
  /** Liste abspielen, ab dem Titel start; mit Zufall gemischt (der gewählte Titel zuerst) */
  play(tracks: Track[], start = 0) {
    if (!tracks.length) return
    report()
    let q = tracks.slice(0, MAX_QUEUE).map(toQ)
    let i = Math.min(start, q.length - 1)
    unshuffled = null
    if (state.shuffle) { unshuffled = q; const first = q[i]; q = [first, ...shuffled(q.filter((_, k) => k !== i))]; i = 0 }
    state = { ...state, queue: q }
    load(i, true)
  },
  /** Gemischt abspielen (schaltet Zufall ein) */
  shuffleAll(tracks: Track[]) {
    if (!tracks.length) return
    set({ shuffle: true })
    player.play(tracks, Math.floor(Math.random() * tracks.length))
  },
  /** Nach dem aktuellen Titel einreihen */
  playNext(tracks: Track[]) {
    if (!state.queue.length) { player.play(tracks); return }
    const q = [...state.queue]
    q.splice(state.index + 1, 0, ...tracks.map(toQ))
    set({ queue: q.slice(0, MAX_QUEUE) })
    persist()
  },
  /** Ans Ende der Warteschlange */
  enqueue(tracks: Track[]) {
    if (!state.queue.length) { player.play(tracks); return }
    set({ queue: [...state.queue, ...tracks.map(toQ)].slice(0, MAX_QUEUE) })
    persist()
  },
  toggle(on?: boolean) {
    const a = el()
    if (state.index < 0) return
    // Nach dem Neuladen: Titel erst laden, an der gemerkten Stelle
    if (!a.src) { load(state.index, on !== false, state.position); return }
    if (on ?? a.paused) a.play().catch(() => {}); else a.pause()
  },
  next() { if (state.index < state.queue.length - 1) go(state.index + 1); else if (state.repeat === 'all' && state.queue.length) go(0) },
  prev() {
    if (state.position > 3 || state.index <= 0) { player.seek(0); return }
    go(state.index - 1)
  },
  jump(index: number) { go(index) },
  seek(sec: number) {
    const a = el()
    if (!a.src) { set({ position: sec }); return }
    a.currentTime = sec
    lastTime = sec
    set({ position: sec })
  },
  setVolume(v: number) { const a = el(); a.volume = v; set({ volume: v }); persist() },
  toggleShuffle() {
    const on = !state.shuffle
    const cur = state.queue[state.index]
    if (on) {
      unshuffled = state.queue
      const rest = shuffled(state.queue.filter((_, k) => k !== state.index))
      set({ shuffle: true, queue: cur ? [cur, ...rest] : rest, index: cur ? 0 : -1 })
    } else {
      const back = unshuffled ?? state.queue
      unshuffled = null
      set({ shuffle: false, queue: back, index: cur ? Math.max(0, back.findIndex(x => x.id === cur.id)) : -1 })
    }
    persist()
  },
  cycleRepeat() { set({ repeat: state.repeat === 'off' ? 'all' : state.repeat === 'all' ? 'one' : 'off' }); persist() },
  remove(index: number) {
    if (index === state.index) {
      report()
      const q = state.queue.filter((_, k) => k !== index)
      state = { ...state, queue: q }
      if (q.length === 0) { player.clear(); return }
      load(Math.min(index, q.length - 1), state.playing)
      return
    }
    set({ queue: state.queue.filter((_, k) => k !== index), index: index < state.index ? state.index - 1 : state.index })
    persist()
  },
  /** Titel in der Warteschlange verschieben */
  move(from: number, to: number) {
    if (from === to) return
    const q = [...state.queue]
    const [x] = q.splice(from, 1)
    q.splice(to, 0, x)
    const cur = state.queue[state.index]
    set({ queue: q, index: cur ? q.indexOf(cur) : -1 })
    persist()
  },
  clear() {
    report()
    if (audio) { audio.pause(); audio.removeAttribute('src'); audio.load() }
    unshuffled = null
    set({ queue: [], index: -1, playing: false, position: 0, duration: 0, error: null })
    persist()
    media()
  },
}

const subscribe = (f: () => void) => { listeners.add(f); return () => { listeners.delete(f) } }

/** Ganzer Stand - ändert sich beim Abspielen mehrmals je Sekunde (Leiste, Warteschlange) */
export function usePlayer(): PlayerState {
  return useSyncExternalStore(subscribe, () => state)
}

/** Nur aktueller Titel und ob er läuft - für Listen, die nicht bei jeder Sekunde neu zeichnen sollen */
export function useCurrent(): [string | null, boolean] {
  const id = useSyncExternalStore(subscribe, () => state.queue[state.index]?.id ?? null)
  const playing = useSyncExternalStore(subscribe, () => state.playing)
  return [id, playing]
}

/** Etwas in der Warteschlange? (Leiste ein- oder ausblenden) */
export function useHasQueue(): boolean {
  return useSyncExternalStore(subscribe, () => state.queue.length > 0)
}
