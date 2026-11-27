// Ein einziger Long-Poll (/api/chat/wait) für die ganze Oberfläche und alle Tabs desselben Browsers.
// Der Browser erlaubt je Server nur wenige gleichzeitige Verbindungen; zwei Long-Polls je Tab ließen bei
// mehreren offenen Tabs andere Anfragen (etwa das Senden) bis zum Ablauf eines Polls warten.
// Ein Tab führt (Herzschlag in localStorage) und reicht jede Antwort per BroadcastChannel an die anderen weiter.
// Ohne localStorage oder BroadcastChannel wartet jeder Tab selbst, aber nur einmal.
import { api, q } from '../api'
import { featureOn } from '../api' // [feature:mailpush]

/** mail: Version des Mail-Bereichs (wie /api/mail/status) - der Server weckt den Long-Poll auch, wenn sie sich ändert (mailpush) */
export interface ChatWait { seq: number; busy?: number; busyRooms?: number[]; mail?: number }
type Listener = (w: ChatWait) => void

const KEY = 'dmoffice.chatwait'
/** Ohne Herzschlag so lange übernimmt ein anderer Tab (ein Poll dauert höchstens 25 s). */
const STALE = 40_000
const me = Math.random().toString(36).slice(2) + Date.now().toString(36)
const listeners = new Set<Listener>()
let last: ChatWait | null = null
let running = false
let leading = false
let channel: BroadcastChannel | null = null
let watch: ReturnType<typeof setInterval> | undefined

const sleep = (ms: number) => new Promise(res => setTimeout(res, ms))

function storage(): Storage | null {
  try { const s = window.localStorage; s.getItem(KEY); return s } catch { return null }
}
function readBeat(): { id: string; at: number } | null {
  try { return JSON.parse(storage()?.getItem(KEY) ?? 'null') } catch { return null }
}
function writeBeat(at = Date.now()) {
  try { storage()?.setItem(KEY, JSON.stringify({ id: me, at })) } catch { /* ohne Speicher: jeder Tab selbst */ }
}
const shared = () => channel != null && storage() != null

function deliver(w: ChatWait) {
  last = w
  for (const l of [...listeners]) { try { l(w) } catch { /* ein Abnehmer stört die anderen nicht */ } }
}

async function lead() {
  if (leading) return
  leading = true
  while (leading && running) {
    if (shared()) {
      // Ein anderer Tab hat inzwischen übernommen (etwa nach einer Pause): zurücktreten
      const b = readBeat()
      if (b && b.id !== me && Date.now() - b.at < STALE) { leading = false; break }
      writeBeat()
    }
    try {
      const params: Record<string, number> = { since: last?.seq ?? 0, timeout: 25, busy: last?.busy ?? 0 }
      if (featureOn('mailpush')) params.mail = last?.mail ?? 0 // [feature:mailpush]
      const r = await api.get<ChatWait>('/api/chat/wait?' + q(params))
      if (shared() && leading) writeBeat()
      deliver(r)
      channel?.postMessage({ type: 'wait', w: r })
    } catch {
      await sleep(5000)
    }
  }
}

function release() {
  if (!leading) return
  leading = false
  if (shared() && readBeat()?.id === me) {
    writeBeat(0)
    channel?.postMessage({ type: 'bye' })
  }
}

/** Führung übernehmen, wenn kein anderer Tab lebt (Herzschlag fehlt oder ist alt). */
function claim() {
  if (!running || leading) return
  if (!shared()) { lead(); return }
  const b = readBeat()
  if (b && b.id !== me && Date.now() - b.at < STALE) return
  writeBeat()
  // Zwei Tabs gleichzeitig: wer zuletzt geschrieben hat, führt
  setTimeout(() => { if (running && !leading && readBeat()?.id === me) lead() }, 150)
}

function start() {
  if (running) return
  running = true
  if (typeof BroadcastChannel !== 'undefined' && !channel) {
    try {
      channel = new BroadcastChannel(KEY)
      channel.onmessage = e => {
        const m = e.data as { type: string; w?: ChatWait }
        if (m?.type === 'wait' && m.w && !leading) deliver(m.w)
        else if (m?.type === 'hello' && leading && last) channel?.postMessage({ type: 'wait', w: last })
        else if (m?.type === 'bye') setTimeout(claim, Math.random() * 300)
      }
      window.addEventListener('pagehide', release)
    } catch { channel = null }
  }
  if (shared()) {
    channel?.postMessage({ type: 'hello' })
    watch = setInterval(claim, 5000)
  }
  claim()
}

function stop() {
  running = false
  if (watch) clearInterval(watch)
  watch = undefined
  release()
}

/**
 * Meldet jede Antwort des Long-Polls (Stand seq, Version und Liste der Unterhaltungen, in denen gerade
 * geantwortet wird). Ein neuer Abnehmer bekommt sofort den letzten bekannten Stand. Gibt das Abmelden zurück.
 */
export function onChatWait(l: Listener): () => void {
  listeners.add(l)
  if (last) { const w = last; queueMicrotask(() => { if (listeners.has(l)) l(w) }) }
  start()
  return () => {
    listeners.delete(l)
    if (!listeners.size) stop()
  }
}
