import { useSyncExternalStore } from 'react'
import type { MailItem } from '../api'

/**
 * Sofort sichtbar: Gelesen, Fahne, Kategorie und die Zahlen an Ordnern und im Seitenmenü ändern sich beim Klick; die Anfrage an
 * den Server läuft danach. Was noch unterwegs ist, steht hier - so überschreibt eine Liste oder Ordnerzahl, die der Server vor dieser
 * Änderung gelesen hat, sie nicht wieder mit dem alten Stand. Schlägt die Anfrage fehl, fällt der Eintrag weg und die Oberfläche
 * nimmt die Änderung zurück.
 *
 * Zeitpunkte (performance.now): sentAt = Anfrage abgeschickt, doneAt = Antwort da. Eine Antwort des Servers, deren Anfrage nach
 * doneAt begann, kennt die Änderung schon. Ist die Anfrage noch nicht abgeschickt, kennt sie sie sicher nicht. Dazwischen ist offen,
 * ob der Server sie schon verarbeitet hatte: Zahlen bleiben dann beim angezeigten Stand, bis der nächste Abruf es klärt.
 */

export type MailRef = { account: string; folder: string; uid: number }
export type MailPatch = { seen?: boolean; flagged?: boolean; keywords?: Record<string, boolean> }
/** Änderung einer Zahl: unread/total am Ordner; tab = Reiter Relevant/Sonstige im Posteingang */
export type CountChange = { account: string; folder: string; inbox?: boolean; tab?: 'focused' | 'other'; unread?: number; total?: number }

interface Op { sentAt: number | null; doneAt: number | null; alive: boolean; failed?: boolean }
interface ItemEntry { op: Op; key: string; patch: MailPatch }
interface CountEntry { op: Op; change: CountChange }

const now = () => performance.now()
let items: ItemEntry[] = []
let counts: CountEntry[] = []
const listeners = new Set<() => void>()
let version = 0
const changed = () => { version++; for (const l of listeners) l() }

export const mailKey = (i: MailRef) => `${i.account}|${i.folder}|${i.uid}`

/** Nach dieser Zeit (ab Antwort) gilt eine Änderung als beim Server angekommen und fällt weg. */
const KEEP_MS = 60_000

export interface PendingOp {
  /** Anfrage geht jetzt raus */
  sent(): void
  /** Server hat sie angenommen */
  done(): void
  /** Abgelehnt oder nicht erreichbar: Eintrag weg (die Oberfläche nimmt die Änderung selbst zurück) */
  failed(): void
}

/** Änderung vormerken: Mails (welche Felder) und Zahlen. Ältere Einträge für dieselben Felder derselben Mail entfallen. */
export function begin(mails: (MailRef & { patch: MailPatch })[], countChanges: CountChange[] = []): PendingOp {
  const op: Op = { sentAt: null, doneAt: null, alive: true }
  for (const m of mails) {
    const key = mailKey(m)
    // Neuere Änderung desselben Felds ersetzt die ältere
    for (const e of items) if (e.key === key) {
      if (m.patch.seen !== undefined) delete e.patch.seen
      if (m.patch.flagged !== undefined) delete e.patch.flagged
      if (m.patch.keywords && e.patch.keywords) for (const k of Object.keys(m.patch.keywords)) delete e.patch.keywords[k]
    }
    items = items.filter(e => e.patch.seen !== undefined || e.patch.flagged !== undefined || Object.keys(e.patch.keywords ?? {}).length > 0)
    items.push({ op, key, patch: { ...m.patch, keywords: m.patch.keywords ? { ...m.patch.keywords } : undefined } })
  }
  for (const c of countChanges) if (c.unread || c.total) counts.push({ op, change: c })
  changed()
  const drop = () => {
    op.alive = false
    items = items.filter(e => e.op !== op)
    counts = counts.filter(e => e.op !== op)
    changed()
  }
  return {
    sent: () => { if (op.sentAt == null) op.sentAt = now() },
    done: () => {
      if (!op.alive) return
      if (op.sentAt == null) op.sentAt = now()
      op.doneAt = now()
      changed()
      setTimeout(drop, KEEP_MS)
    },
    failed: () => { if (op.alive) { op.failed = true; drop() } },
  }
}

/** Kennt eine Antwort, deren Anfrage um fetchedAt begann, diese Änderung? true = ja, false = nein, null = offen */
const known = (op: Op, fetchedAt: number): boolean | null =>
  op.doneAt != null && op.doneAt < fetchedAt ? true : op.sentAt == null ? false : null

/** Mails vom Server mit den noch ausstehenden Änderungen. fetchedAt = Beginn der Anfrage (0: gemerkte Liste, alles anwenden). */
export function overlay<T extends MailItem>(list: T[], fetchedAt: number): T[] {
  if (!items.length) return list
  const byKey = new Map<string, ItemEntry[]>()
  for (const e of items) if (known(e.op, fetchedAt) !== true || fetchedAt === 0) byKey.set(e.key, [...(byKey.get(e.key) ?? []), e])
  if (!byKey.size) return list
  return list.map(i => {
    const es = byKey.get(mailKey(i))
    return es ? es.reduce((x, e) => applyPatch(x, e.patch), i) : i
  })
}

export function applyPatch<T extends MailItem>(i: T, p: MailPatch): T {
  let kw = i.keywords
  if (p.keywords) {
    const set = new Set(kw ?? [])
    for (const [k, on] of Object.entries(p.keywords)) if (on) set.add(k); else set.delete(k)
    kw = [...set]
  }
  return { ...i, seen: p.seen ?? i.seen, flagged: p.flagged ?? i.flagged, keywords: kw }
}

/** Angezeigte Zahl und welche ausstehenden Änderungen (mit welchem Betrag) darin stecken */
export interface Shown { value: number; applied: Map<object, number> }

/**
 * Zahl vom Server mit den ausstehenden Änderungen. prev = bisher angezeigte Zahl: Ist offen, ob der Server eine Änderung schon
 * mitgezählt hat (Anfrage lief, während die Änderung unterwegs war), bleibt die angezeigte Zahl stehen - nur neue Änderungen kommen
 * dazu, abgelehnte fallen heraus. Der nächste Abruf nach der Antwort klärt es.
 */
export function adjustCount(match: (c: CountChange) => boolean, field: 'unread' | 'total', server: number, fetchedAt: number, prev?: Shown): Shown {
  const live = new Map<object, number>()
  let open = false
  for (const e of counts) {
    const v = e.change[field]
    if (!v || !match(e.change)) continue
    const k = known(e.op, fetchedAt)
    if (k === true) continue
    if (k === null) open = true
    live.set(e.op, (live.get(e.op) ?? 0) + v)
  }
  const sum = (m: Map<object, number>) => [...m.values()].reduce((a, b) => a + b, 0)
  if (!open || !prev) return { value: Math.max(0, server + sum(live)), applied: live }
  let value = prev.value
  const applied = new Map<object, number>()
  for (const [op, d] of prev.applied) {
    if ((op as Op).failed) value -= d
    else applied.set(op, d)
  }
  for (const [op, d] of live) if (!applied.has(op)) { value += d; applied.set(op, d) }
  return { value: Math.max(0, value), applied }
}

/** Ungelesene im Posteingang eines Kontos (Seitenmenü) */
export const adjustInbox = (account: string, server: number, fetchedAt: number, prev?: Shown) =>
  adjustCount(c => c.account === account && !!c.inbox && !c.tab, 'unread', server, fetchedAt, prev)

/** Neu zeichnen, sobald sich etwas Ausstehendes ändert (Seitenmenü) */
export function usePendingVersion() {
  return useSyncExternalStore(l => { listeners.add(l); return () => { listeners.delete(l) } }, () => version)
}

export const pendingNow = now

/** Ist für diese Mail eine Änderung dieses Felds unterwegs? */
export const isPending = (i: MailRef, field: 'seen' | 'flagged') => items.some(e => e.key === mailKey(i) && e.patch[field] !== undefined)
