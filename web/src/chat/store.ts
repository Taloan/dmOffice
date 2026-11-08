// Chat: Typen und Abgleich mit dem Server. Ein Long-Poll (/api/chat/wait) wartet auf Neues, dann holt
// /api/chat/changes alles seit dem letzten Stand. Laeuft, solange der Chat-Bereich offen ist.
import { useCallback, useEffect, useRef, useState } from 'react'
import { api, q } from '../api'
import { lang } from '../i18n'
import { finishToChat, uploadChunked } from '../files/chunked'

/** mode: show (Zeigen) | send (Senden); state: ok | expired (abgelaufen) | revoked (zurueckgezogen); forwarded: weitergeleitet */
export interface ChatRef {
  type: 'file' | 'note' | 'folder' | 'event' | 'task' | 'contact' | 'location' | 'album'; name: string; mime?: string | null; size?: number | null; owner: string
  mode: 'show' | 'send' | 'share'; expires?: string | null; state: 'ok' | 'expired' | 'revoked'; forwarded?: boolean
  /** Standort: Breite, Länge, Genauigkeit in Metern */
  lat?: number | null; lon?: number | null; accuracy?: number | null
  /** Live-Teilen: bis wann (danach letzter Standort), Zeit der letzten Position */
  liveUntil?: string | null; at?: string | null
}
export type ChatMode = 'show' | 'send'
export interface ChatSettings {
  deviceMode: ChatMode; filesMode: ChatMode; itemsMode: ChatMode; saveFolder: string; noteFolder: string | null
  reshow: boolean; showFor: 'forever' | '1y' | '6m' | '1m' | '1w' | '24h'; locationRequests: boolean
  /** Pukki (Stufe 5) */
  pukkiDaily: boolean; pukkiTime: string; pukkiReminders: boolean
  /** Ruhezeiten (Chat-Komfort): von/bis Ortszeit, Erwaehnungen trotzdem melden */
  quiet: boolean; quietFrom: string; quietTo: string; quietMentions: boolean
  /** Privatsphäre: anderen zeigen, ob ich gelesen habe */
  readReceipts: boolean
}
/** Pukki, der Assistent (Stufe 5): fester Gesprächspartner, kein Benutzerkonto */
export const PUKKI = 'pukki'
export const isPukki = (r: { kind: string; members: { id: string }[] }) => r.kind === 'direct' && r.members.some(m => m.id === PUKKI)

export interface ChatRights { expiring: boolean; locationRequests: boolean }

/** Einstellungen einmal laden und teilen (Vorgabe-Arten beim Verschicken). */
let settingsCache: Promise<{ settings: ChatSettings; rights: ChatRights }> | null = null
export const chatSettings = (fresh = false) => {
  if (fresh || !settingsCache) settingsCache = api.get('/api/chat/settings')
  return settingsCache
}
export interface ChatReaction { emoji: string; users: string[] }
/** Mit jeder Nachricht: Zeitzone und Sprache dieses Geräts - danach rechnet und antwortet Pukki. */
export const fromDevice = () => ({ zone: Intl.DateTimeFormat().resolvedOptions().timeZone, lang })

export interface ChatMessage {
  id: number; room: number; sender: string; senderName: string; created: string; edited: string | null; deleted: boolean
  kind: 'text' | 'system'; text: string | null; replyTo: number | null; refs: ChatRef[]; reactions: ChatReaction[]; seq: number
  /** @Erwaehnungen: wer gemeint ist (Benutzer-Kennungen) */
  mentions?: string[]
  /** Pukkis Antwort stammt von der KI */
  ai?: boolean
  /** Knöpfe unter Pukkis Antworten (Rückgängig, Auswahl) */
  actions?: { key: string; label: string }[]
  /** Pukkis Stimmung: Pose neben der Nachricht (frage, daumen, checkliste …), null: er erklärt („zeigen“) */
  mood?: string | null
}
export interface ChatMember {
  id: string; name: string; login: string | null; admin: boolean
  /** gelesen bis (0, wenn das Mitglied keine Lesebestätigungen sendet) */
  lastRead: number
  /** zugestellt bis (ein Gerät hat abgeholt) */
  delivered?: number
  /** sendet Lesebestätigungen */
  receipts?: boolean
  /** Profilbild (Version, null: keins) und Akzentfarbe des Benutzers */
  avatar?: string | null
  accent?: string | null
}
export interface ChatRoom {
  id: number; kind: 'direct' | 'group'; title: string; created: string; createdBy: string; seq: number; unread: number
  muted: boolean; admin: boolean; lastRead: number; members: ChatMember[]; last: ChatMessage | null
  /** persoenliche Farbe der Unterhaltung, null: Akzentfarbe */
  color?: string | null
  /** Standort-Anfrage: darf ich den anderen fragen? (Recht + seine Einstellung) */
  askLocation?: boolean
  /** ungelesene Nachrichten, die mich erwaehnen */
  mentioned?: number
}
export interface Person { id: string; login: string; displayName: string }

interface Changes { seq: number; rooms: ChatRoom[]; messages: ChatMessage[]; more: boolean }

/** Den Zaehler am Symbol in der Leiste sofort neu laden. */
export const chatStatusChanged = () => window.dispatchEvent(new Event('dmoffice:chatstatus'))

/** Nachrichten zusammenfuehren: neuere Fassung ersetzt die alte, sortiert nach Kennung. */
export function merge(list: ChatMessage[], incoming: ChatMessage[]): ChatMessage[] {
  if (!incoming.length) return list
  const byId = new Map(list.map(m => [m.id, m]))
  for (const m of incoming) {
    const old = byId.get(m.id)
    if (!old || old.seq <= m.seq) byId.set(m.id, m)
  }
  return [...byId.values()].sort((a, b) => a.id - b.id)
}

const PAGE = 50

export function useChat() {
  const [rooms, setRooms] = useState<ChatRoom[] | null>(null)
  const [messages, setMessages] = useState<Record<number, ChatMessage[]>>({})
  /** Unterhaltungen, deren Anfang erreicht ist (nichts Aelteres mehr zu laden). */
  const [complete, setComplete] = useState<Record<number, boolean>>({})
  const seq = useRef(0)
  const loaded = useRef(new Set<number>())

  const pull = useCallback(async () => {
    for (let i = 0; i < 20; i++) {
      const c = await api.get<Changes>('/api/chat/changes?' + q({ since: seq.current }))
      seq.current = Math.max(seq.current, c.seq)
      setRooms(c.rooms)
      const byRoom = new Map<number, ChatMessage[]>()
      for (const m of c.messages) {
        if (!loaded.current.has(m.room)) continue
        byRoom.set(m.room, [...(byRoom.get(m.room) ?? []), m])
      }
      if (byRoom.size) setMessages(prev => {
        const next = { ...prev }
        for (const [room, list] of byRoom) next[room] = merge(next[room] ?? [], list)
        return next
      })
      if (c.messages.length) chatStatusChanged()
      if (!c.more) break
    }
  }, [])

  // Unterhaltungen, in denen gerade jemand an einer Antwort arbeitet („Pukki denkt nach …“)
  const [busy, setBusy] = useState<number[]>([])
  const busyVersion = useRef(0)

  useEffect(() => {
    let alive = true
    const run = async () => {
      // Erst den Stand, dann die Liste - was dazwischen passiert, holt der erste Abgleich
      try {
        seq.current = (await api.get<{ seq: number }>('/api/chat/status')).seq
        const list = await api.get<ChatRoom[]>('/api/chat/rooms?' + q({ lang }))
        if (alive) setRooms(list)
      } catch { if (alive) setRooms([]) }
      while (alive) {
        try {
          const r = await api.get<{ seq: number; busy?: number; busyRooms?: number[] }>('/api/chat/wait?' + q({ since: seq.current, timeout: 25, busy: busyVersion.current }))
          if (alive && r.busy != null && r.busy !== busyVersion.current) { busyVersion.current = r.busy; setBusy(r.busyRooms ?? []) }
          if (alive && r.seq > seq.current) await pull()
        } catch {
          await new Promise(res => setTimeout(res, 5000))
        }
      }
    }
    run()
    return () => { alive = false }
  }, [pull])

  /** Verlauf einer Unterhaltung laden (beim Oeffnen). */
  const open = useCallback(async (room: number) => {
    if (loaded.current.has(room)) return
    loaded.current.add(room)
    try {
      const list = await api.get<ChatMessage[]>(`/api/chat/rooms/${room}/messages?` + q({ limit: PAGE }))
      setMessages(prev => ({ ...prev, [room]: merge(prev[room] ?? [], list) }))
      setComplete(prev => ({ ...prev, [room]: list.length < PAGE }))
    } catch { loaded.current.delete(room) }
  }, [])

  /** Aeltere Nachrichten nachladen (nach oben scrollen). */
  const older = useCallback(async (room: number, before: number) => {
    const list = await api.get<ChatMessage[]>(`/api/chat/rooms/${room}/messages?` + q({ before, limit: PAGE }))
    setMessages(prev => ({ ...prev, [room]: merge(prev[room] ?? [], list) }))
    if (list.length < PAGE) setComplete(prev => ({ ...prev, [room]: true }))
  }, [])

  /** Antwort des Servers (gesendet, bearbeitet, reagiert) gleich zeigen - der Abgleich bestaetigt es spaeter. */
  const put = useCallback((m: ChatMessage) => {
    if (loaded.current.has(m.room)) setMessages(prev => ({ ...prev, [m.room]: merge(prev[m.room] ?? [], [m]) }))
  }, [])

  const putRoom = useCallback((r: ChatRoom) => {
    setRooms(prev => (prev ? [r, ...prev.filter(x => x.id !== r.id)] : [r]))
  }, [])

  const markRead = useCallback((room: number, upTo: number) => {
    setRooms(prev => prev?.map(r => (r.id === room ? { ...r, unread: 0, mentioned: 0, lastRead: Math.max(r.lastRead, upTo) } : r)) ?? prev)
    api.post(`/api/chat/rooms/${room}/read`, { upTo }).then(chatStatusChanged).catch(() => {})
  }, [])

  return { rooms, messages, complete, open, older, put, putRoom, markRead, refresh: pull, busy }
}

/** Hinweis im Verlauf (vom Server als JSON) in Worten. */
export function systemText(text: string | null, sender: string, names: (id: string) => string, t: (s: string, v?: Record<string, string | number>) => string): string {
  let p: { ev?: string; title?: string; users?: string[]; kind?: string } = {}
  try { p = JSON.parse(text ?? '{}') } catch { /* alter Text */ }
  const by = names(sender)
  const who = (p.users ?? []).map(names).join(', ')
  switch (p.ev) {
    case 'created': return t('{by} hat die Gruppe „{title}“ erstellt', { by, title: p.title ?? '' })
    case 'renamed': return t('{by} hat die Gruppe in „{title}“ umbenannt', { by, title: p.title ?? '' })
    case 'added': return t('{by} hat {who} hinzugefügt', { by, who })
    case 'removed': return t('{by} hat {who} entfernt', { by, who })
    case 'left': return t('{who} hat die Gruppe verlassen', { who })
    // Gruppenraum eingerichtet (Stufe 4)
    case 'space': return t('{by} hat für die Gruppe einen Raum eingerichtet: {kind} „{title}“', { by, title: p.title ?? '', kind: p.kind === 'calendar' ? t('Kalender') : p.kind === 'list' ? t('Liste') : p.kind === 'folder' ? t('Ordner') : t('Notizordner') })
    default: return text ?? ''
  }
}

// ---------------- Adressen der Verweise ----------------

export const refUrl = (message: number, i: number, what: 'file' | 'thumb' | 'note' | 'item' | 'folder' | 'folder/file' | 'folder/thumb' | 'album' | 'album/thumb' | 'album/file', params: Record<string, string | number | boolean | undefined> = {}) =>
  `/api/chat/messages/${message}/refs/${i}/${what}` + (Object.keys(params).length ? '?' + q(params) : '')

/** Fotos aus dem Chat im Bildbetrachter der Galerie: Pfad „Nachricht:Index“, Bilder über die Adressen des Chats. */
export const chatRefSource = {
  download: (i: { path: string }, inline?: boolean) => { const [m, x] = i.path.split(':').map(Number); return refUrl(m, x, 'file', inline ? { inline: true } : {}) },
  thumb: (i: { path: string }, size = 256) => { const [m, x] = i.path.split(':').map(Number); return refUrl(m, x, 'thumb', { size }) },
}

/** Ein Foto einer Nachricht als Eintrag für den Bildbetrachter. */
export const chatRefItem = (m: ChatMessage, r: ChatRef, i: number) =>
  ({ path: `${m.id}:${i}`, name: r.name, isDir: false, size: r.size ?? 0, mime: r.mime ?? null, modified: m.created })

export const refBlobUrl = (message: number, i: number, name: string) => `/api/chat/messages/${message}/refs/${i}/blob/${encodeURIComponent(name)}`

/** Datei roh hochladen (nicht durch den Umschlag - wie das Hochladen in der Dateiablage). */
export async function uploadToRoom(room: number, file: File, text?: string, onProgress?: (done: number, total: number) => void, mode?: ChatMode): Promise<ChatMessage> {
  // Version 2: in Stuecken (fortsetzbar, ohne Groessengrenze), danach als Anhang verschicken
  const id = await uploadChunked(file, file.name || 'Bild.png', loaded => onProgress?.(loaded, file.size))
  const params: Record<string, string> = {}
  if (text) params.text = text
  if (mode) params.mode = mode
  return finishToChat(id, room, params)
}

/**
 * HTML einer fremden Notiz zum Anzeigen: nur einfache Auszeichnung, keine Skripte, keine Ereignisse, keine fremden
 * Adressen. Bilder ("blob:NAME" wie in notizi) zeigen auf den Verweis im Chat.
 */
export function safeNoteHtml(html: string, blob: (name: string) => string): string {
  const doc = new DOMParser().parseFromString(`<div>${html}</div>`, 'text/html')
  const allowed = new Set(['DIV', 'P', 'BR', 'SPAN', 'B', 'STRONG', 'I', 'EM', 'U', 'S', 'STRIKE', 'UL', 'OL', 'LI', 'H1', 'H2', 'H3', 'H4',
    'BLOCKQUOTE', 'PRE', 'CODE', 'A', 'IMG', 'TABLE', 'THEAD', 'TBODY', 'TR', 'TD', 'TH', 'HR', 'INPUT', 'LABEL'])
  const walk = (el: Element) => {
    for (const child of [...el.children]) {
      if (!allowed.has(child.tagName)) {
        // Unbekanntes weg, sein Inhalt bleibt - vorher bereinigt, denn die Schleife sieht ihn danach nicht mehr
        if (['SCRIPT', 'STYLE', 'IFRAME', 'OBJECT', 'EMBED', 'TEMPLATE', 'SVG', 'MATH'].includes(child.tagName.toUpperCase())) { child.remove(); continue }
        walk(child)
        child.replaceWith(...child.childNodes)
        continue
      }
      for (const a of [...child.attributes]) {
        const n = a.name.toLowerCase()
        const keep = n === 'href' || n === 'src' || n === 'type' || n === 'checked' || n === 'colspan' || n === 'rowspan'
        if (!keep) child.removeAttribute(a.name)
      }
      if (child.tagName === 'INPUT') { if (child.getAttribute('type') !== 'checkbox') child.remove(); else child.setAttribute('disabled', '') }
      if (child.tagName === 'A') {
        const href = child.getAttribute('href') ?? ''
        if (!/^(https?:|mailto:)/i.test(href)) child.removeAttribute('href')
        child.setAttribute('target', '_blank')
        child.setAttribute('rel', 'noopener noreferrer')
      }
      if (child.tagName === 'IMG') {
        const src = child.getAttribute('src') ?? ''
        if (src.startsWith('blob:')) child.setAttribute('src', blob(src.slice(5)))
        else child.remove()
        child.setAttribute('style', 'max-width:100%')
      }
      walk(child)
    }
  }
  walk(doc.body)
  return doc.body.firstElementChild?.innerHTML ?? ''
}
