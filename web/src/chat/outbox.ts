// Postausgang des Chats im Browser (Version 2): Nachrichten, die ohne Verbindung zum Server abgeschickt wurden, bleiben
// im Browser (localStorage, je Benutzer) und gehen raus, sobald der Server wieder antwortet - beim Wiederverbinden,
// wenn der Browser wieder online ist und sonst alle 15 Sekunden.
import { useSyncExternalStore } from 'react'
import { api, ApiError } from '../api'
import { fromDevice } from './store'
import type { ChatMessage } from './store'

export interface OutMessage { id: string; user: string; room: number; text: string; replyTo?: number | null; created: number }

const KEY = 'dmoffice.chat.outbox'
const listeners = new Set<() => void>()

function load(): OutMessage[] {
  try { return JSON.parse(localStorage.getItem(KEY) ?? '[]') } catch { return [] }
}
let items: OutMessage[] = load()

function store(next: OutMessage[]) {
  items = next
  try { localStorage.setItem(KEY, JSON.stringify(items)) } catch { /* nur fuer diese Sitzung */ }
  listeners.forEach(l => l())
}

/** Keine Verbindung: fetch scheitert ganz (TypeError) oder der Proxy findet den Server nicht. */
export const offline = (e: unknown) => e instanceof TypeError || (e instanceof ApiError && [502, 503, 504].includes(e.status))

const newId = () => (typeof crypto !== 'undefined' && 'randomUUID' in crypto ? crypto.randomUUID() : Math.random().toString(36).slice(2) + Date.now())

export function queue(user: string, room: number, text: string, replyTo?: number | null) {
  store([...items, { id: newId(), user, room, text, replyTo: replyTo ?? null, created: Date.now() }])
  start()
}

export function cancel(id: string) { store(items.filter(o => o.id !== id)) }

let flushing = false
/** Der Reihe nach senden; ohne Verbindung aufhören. Lehnt der Server ab (Unterhaltung weg), fällt die Nachricht weg. */
export async function flush(user: string, onSent?: (m: ChatMessage) => void) {
  if (flushing) return
  flushing = true
  try {
    for (const o of items.filter(x => x.user === user)) {
      try {
        const m = await api.post<ChatMessage>(`/api/chat/rooms/${o.room}/messages`, { text: o.text, replyTo: o.replyTo, ...fromDevice() })
        onSent?.(m)
      } catch (e) { if (offline(e)) return }
      store(items.filter(x => x.id !== o.id))
    }
  } finally { flushing = false }
}

let me: string | null = null
let sent: ((m: ChatMessage) => void) | undefined
let timer: number | undefined
/** Automatisch weiterversuchen, solange etwas wartet. */
function start() {
  if (timer !== undefined || !me) return
  timer = window.setInterval(() => {
    if (!me || !items.some(o => o.user === me)) { window.clearInterval(timer); timer = undefined; return }
    flush(me, sent)
  }, 15000)
}

/** Einmal aus der Chat-Ansicht: wer angemeldet ist und wohin Gesendetes geht; sofort versuchen. */
export function attach(user: string, onSent: (m: ChatMessage) => void) {
  me = user
  sent = onSent
  if (items.some(o => o.user === user)) { flush(user, onSent); start() }
}

window.addEventListener('online', () => { if (me) flush(me, sent) })

export function useOutbox(): OutMessage[] {
  return useSyncExternalStore(l => { listeners.add(l); return () => listeners.delete(l) }, () => items)
}
