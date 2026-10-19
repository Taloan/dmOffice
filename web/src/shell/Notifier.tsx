import { useEffect, useRef } from 'react'
import { api, CalEvent, MailItem, q } from '../api'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import { fmtTime } from './hooks'
import { t } from '../i18n'

/** Desktop-Benachrichtigungen gibt der Browser nur unter HTTPS oder localhost frei. */
export const desktopPossible = () => typeof Notification !== 'undefined' && window.isSecureContext

function beep() {
  try {
    const ctx = new AudioContext()
    const o = ctx.createOscillator(), g = ctx.createGain()
    o.frequency.value = 880
    g.gain.setValueAtTime(0.0001, ctx.currentTime)
    g.gain.exponentialRampToValueAtTime(0.2, ctx.currentTime + 0.02)
    g.gain.exponentialRampToValueAtTime(0.0001, ctx.currentTime + 0.35)
    o.connect(g).connect(ctx.destination)
    o.start()
    o.stop(ctx.currentTime + 0.4)
  } catch { /* ohne Ton */ }
}

/**
 * Hinweise wie in Outlook: neue Mail im Posteingang, Erinnerungen an Termine.
 * Als Meldung in dmOffice und - wenn erlaubt - als Desktop-Benachrichtigung.
 */
export function Notifier({ status }: { status: any }) {
  const { notify } = useApp()
  const { prefs } = usePrefs()
  const lastUnread = useRef<Record<string, number> | null>(null)
  const prefsRef = useRef(prefs)
  prefsRef.current = prefs

  const show = (title: string, body: string, onClick?: () => void) => {
    const p = prefsRef.current
    notify(title, body, 'info')
    if ((p.notifySound ?? true)) beep()
    if ((p.notifyDesktop ?? true) && desktopPossible() && Notification.permission === 'granted' && document.hidden) {
      const n = new Notification(title, { body, icon: '/icon-128.png', tag: title + body })
      n.onclick = () => { window.focus(); onClick?.(); n.close() }
    }
  }

  // Neue Mail: der Ungelesen-Zaehler eines Posteingangs steigt
  useEffect(() => {
    if (!status?.accounts) return
    const now: Record<string, number> = Object.fromEntries(Object.entries<any>(status.accounts).map(([id, a]) => [id, a.unread ?? 0]))
    const before = lastUnread.current
    lastUnread.current = now
    if (!before || !(prefsRef.current.notifyMail ?? true)) return
    for (const [account, count] of Object.entries(now)) {
      if (count <= (before[account] ?? count)) continue
      api.get(`/api/mail/${account}/folders`).then(async (folders: any[]) => {
        const inbox = folders.find(f => f.special === 'inbox')
        if (!inbox) return
        const r = await api.get(`/api/mail/${account}/messages?` + q({ folder: inbox.path, size: 1, filter: 'unread' }))
        const m: MailItem | undefined = r.items[0]
        if (m) show(m.from?.name || m.from?.address || t('Neue E-Mail'), m.subject || t('(ohne Betreff)'), () => { location.hash = '#/mail' })
      }).catch(() => {})
    }
  }, [status]) // eslint-disable-line react-hooks/exhaustive-deps

  // Terminerinnerungen: die naechsten acht Tage im Blick, jede Minute pruefen
  useEffect(() => {
    let events: CalEvent[] = []
    const fired = new Set<string>(JSON.parse(sessionStorage.getItem('dmoffice.reminders') || '[]'))
    const load = () => {
      const from = new Date(), to = new Date(Date.now() + 8 * 86400000)
      api.get<CalEvent[]>('/api/pim/events?' + q({ from: from.toISOString(), to: to.toISOString() })).then(e => { events = e }).catch(() => {})
    }
    const check = () => {
      if (!(prefsRef.current.notifyCalendar ?? true)) return
      const now = Date.now()
      for (const e of events) {
        if (e.reminder == null) continue
        const start = e.allDay ? new Date(e.start + 'T09:00').getTime() : new Date(e.start).getTime()
        const at = start - e.reminder * 60000
        const key = e.id + '|' + e.start
        // Faellig seit hoechstens zehn Minuten (Rechner kam aus dem Ruhezustand) und noch nicht gezeigt
        if (now >= at && now - at < 600000 && !fired.has(key)) {
          fired.add(key)
          sessionStorage.setItem('dmoffice.reminders', JSON.stringify([...fired]))
          const when = e.allDay ? t('ganztägig') : fmtTime(new Date(e.start))
          show(t('Erinnerung: {title}', { title: e.title || t('Termin') }), `${when}${e.location ? ' · ' + e.location : ''}`, () => { location.hash = '#/calendar' })
        }
      }
    }
    load()
    const t1 = setInterval(load, 5 * 60000)
    const t2 = setInterval(check, 30000)
    setTimeout(check, 3000)
    return () => { clearInterval(t1); clearInterval(t2) }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  return null
}
