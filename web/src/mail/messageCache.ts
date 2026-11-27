import { api, MailItem, Message, q } from '../api'

/**
 * Mails vorausladen: Beim Durchgehen der Liste (Loeschen, Archivieren, Pfeiltaste) springt die Auswahl zur
 * naechsten Mail - deren Inhalt liegt dann schon hier und erscheint sofort. Gemerkt werden die letzten Mails kurz.
 */
const MAX = 40
const TTL = 5 * 60 * 1000
const cache = new Map<string, { at: number; message: Message }>()
const pending = new Map<string, Promise<Message | null>>()
/** Anfragen, die gerade laufen */
let active = 0

/** Zählt hoch, wenn der Speicher ungültig wird: Was vorher angefragt wurde, kommt danach nicht mehr hinein. */
let generation = 0

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

/**
 * Alles vergessen: etwa nach „Bilder immer anzeigen“ für einen Absender - vorgeladene Mails tragen noch „blockiert“
 * und zeigten sonst beim Zurückkehren wieder keine Bilder.
 */
export function clearAll() { cache.clear(); pending.clear(); generation++ }

export function forget(item: Pick<MailItem, 'account' | 'folder' | 'uid'>) { cache.delete(keyOf(item)) }

/** Im Hintergrund laden, immer nur wenige zugleich und nach der Reihe. Erfüllt, wenn alle da (oder gescheitert) sind. */
export function prefetch(items: MailItem[]): Promise<void> {
  const jobs: Promise<unknown>[] = []
  for (const item of items) {
    const key = keyOf(item)
    if (peek(item)) continue
    const running = pending.get(key)
    if (running) { jobs.push(running); continue }
    const gen = generation
    const job = (async () => {
      // hoechstens drei gleichzeitig (wartende zaehlen nicht mit - sonst warteten ab drei wartenden alle aufeinander)
      while (active >= 3) await new Promise(r => setTimeout(r, 100))
      active++
      try {
        const m = await api.get<Message>(`/api/mail/${item.account}/message?` + q({ folder: item.folder, uid: item.uid, images: false, markSeen: false }))
        if (gen === generation) remember(item, m)
        return m
      } catch { return null } finally { active--; if (gen === generation) pending.delete(key) }
    })()
    pending.set(key, job)
    jobs.push(job)
  }
  return Promise.all(jobs).then(() => {})
}
