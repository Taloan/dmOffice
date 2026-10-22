import { api, MailItem, Message, q } from '../api'

/**
 * Mails vorausladen: Beim Durchgehen der Liste (Loeschen, Archivieren, Pfeiltaste) springt die Auswahl zur
 * naechsten Mail - deren Inhalt liegt dann schon hier und erscheint sofort. Gemerkt werden die letzten Mails kurz.
 */
const MAX = 40
const TTL = 5 * 60 * 1000
const cache = new Map<string, { at: number; message: Message }>()
const pending = new Map<string, Promise<Message | null>>()

const keyOf = (i: Pick<MailItem, 'account' | 'folder' | 'uid'>) => `${i.account}|${i.folder}|${i.uid}`

/** Inhalt, falls schon geladen (ohne entfernte Bilder - die laden nur auf Wunsch). */
export function peek(item: Pick<MailItem, 'account' | 'folder' | 'uid'>): Message | null {
  const hit = cache.get(keyOf(item))
  if (!hit) return null
  if (Date.now() - hit.at > TTL) { cache.delete(keyOf(item)); return null }
  return hit.message
}

export function remember(item: Pick<MailItem, 'account' | 'folder' | 'uid'>, message: Message) {
  const key = keyOf(item)
  cache.delete(key)
  cache.set(key, { at: Date.now(), message })
  while (cache.size > MAX) cache.delete(cache.keys().next().value as string)
}

export function forget(item: Pick<MailItem, 'account' | 'folder' | 'uid'>) { cache.delete(keyOf(item)) }

/** Im Hintergrund laden, immer nur wenige zugleich und nach der Reihe. */
export function prefetch(items: MailItem[]) {
  for (const item of items) {
    const key = keyOf(item)
    if (peek(item) || pending.has(key)) continue
    const job = (async () => {
      // hoechstens zwei gleichzeitig
      while (pending.size > 2) await new Promise(r => setTimeout(r, 150))
      try {
        const m = await api.get<Message>(`/api/mail/${item.account}/message?` + q({ folder: item.folder, uid: item.uid, images: false, markSeen: false }))
        remember(item, m)
        return m
      } catch { return null } finally { pending.delete(key) }
    })()
    pending.set(key, job)
  }
}
