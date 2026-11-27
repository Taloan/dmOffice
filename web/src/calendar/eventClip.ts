import { useEffect, useState } from 'react'
import { api, CalEvent } from '../api'
import { locale } from '../i18n'
import { copyText } from '../shell/browser'

/**
 * Zwischenablage für Termine (Kennung calclip): Kopieren oder Ausschneiden im Kalender oder im Essensplan, Einfügen an einer
 * anderen Stelle - auch vom einen in den anderen. Ausgeschnitten wird beim Einfügen verschoben, kopiert entsteht ein neuer Termin.
 * Zusätzlich steht der Termin als Text in der Zwischenablage des Systems (zum Einfügen in andere Programme).
 */
export interface EventClip { ev: CalEvent; cut: boolean }

let clip: EventClip | null = null
const listeners = new Set<() => void>()

export function setEventClip(ev: CalEvent, cut: boolean) {
  clip = { ev, cut }
  listeners.forEach(f => f())
  const start = new Date(ev.start)
  const when = ev.allDay
    ? start.toLocaleDateString(locale, { weekday: 'short', day: 'numeric', month: 'numeric', year: 'numeric' })
    : start.toLocaleString(locale, { weekday: 'short', day: 'numeric', month: 'numeric', year: 'numeric', hour: '2-digit', minute: '2-digit' })
  copyText([ev.title, when, ev.location].filter(Boolean).join(', ')).catch(() => { })
}

export function clearEventClip() {
  clip = null
  listeners.forEach(f => f())
}

export const eventClip = () => clip

export function useEventClip(): EventClip | null {
  const [, force] = useState(0)
  useEffect(() => {
    const f = () => force(n => n + 1)
    listeners.add(f)
    return () => { listeners.delete(f) }
  }, [])
  return clip
}

/**
 * Termin an neuer Stelle speichern: als Kopie (neuer Termin, bei Serien nur dieses Vorkommen) oder - mit move - den Termin selbst
 * verschieben. categories: andere Kategorien (Essensplan: die Mahlzeit), sonst die des Termins.
 */
export async function placeEvent(e: CalEvent, at: { start: Date; end: Date; allDay: boolean; calendar: number; categories?: string[]; move?: boolean; notify?: boolean }) {
  const iso = (d: Date) => (at.allDay ? d.toLocaleDateString('sv-SE') : d.toISOString())
  const base = {
    calendar: at.calendar, title: e.title, location: e.location, description: e.description, start: iso(at.start), end: iso(at.end), allDay: at.allDay,
    reminder: e.reminder, busy: e.busy, categories: at.categories ?? e.categories, recipe: e.recipe ?? '',
  }
  // notify false: Teilnehmer bekommen die neue Zeit nicht (Rückfrage beim Verschieben)
  if (at.move) await api.post('/api/pim/events', { ...base, calendar: e.calendar, uri: e.uri, etag: e.etag, rRule: e.rRule ?? '', scope: 'series', notify: at.notify ?? true })
  else await api.post('/api/pim/events', { ...base, rRule: e.recurring ? '' : e.rRule ?? '' })
}

/** Dauer eines Termins in Millisekunden (mindestens eine Viertelstunde bzw. ein Tag) */
export const durationOf = (e: CalEvent) => {
  const d = new Date(e.end).getTime() - new Date(e.start).getTime()
  return Math.max(d, e.allDay ? 86400000 : 900000)
}
