import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Switch,
  Textarea, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { FluentProvider } from '@fluentui/react-components'
import { Account, api, Attendee, Collection } from '../api'
import { AddressInput } from '../mail/AddressInput'
import { useApp } from '../App'
import { darkTheme, lightTheme, storedTheme } from '../theme'
import { locale, t } from '../i18n'
import { NaturalDate, parseNaturalDate } from '../shell/naturalDate'
import { DateHint } from '../shell/DateHint'

export interface EventDraft {
  calendar?: number
  uri?: string
  /** Stand beim Laden - der Server lehnt mit 409 ab, wenn sich der Termin inzwischen geaendert hat */
  etag?: string
  title: string
  location?: string
  description?: string
  start: Date
  end: Date
  allDay: boolean
  rRule: string
  reminder: number | null
  busy: boolean
  recurring?: boolean
  instance?: string
  /** Teilnehmer und Organisator (Mailadresse) - bei fremden Einladungen nur zum Ansehen */
  attendees?: Attendee[]
  organizer?: string | null
}

/** "Name" <adresse> bzw. adresse aus dem Empfaengerfeld */
const parseAddress = (v: string): Attendee => {
  const m = v.match(/^"?([^"<]*)"?\s*<(.+)>$/)
  return m ? { email: m[2].trim(), name: m[1].trim() || null } : { email: v.trim(), name: null }
}
const formatAddress = (a: Attendee) => (a.name ? `"${a.name.replace(/"/g, '')}" <${a.email}>` : a.email)
const statusLabel = (s?: string) => ({ ACCEPTED: t('Zugesagt'), TENTATIVE: t('Mit Vorbehalt'), DECLINED: t('Abgesagt') } as Record<string, string>)[(s ?? '').toUpperCase()] ?? t('Keine Antwort')
const statusColor = (s?: string) => ({ ACCEPTED: '#2E7D32', TENTATIVE: '#E8A33D', DECLINED: '#C62828' } as Record<string, string>)[(s ?? '').toUpperCase()] ?? 'var(--colorNeutralForeground3)'

/** Vorschlag fuer den Beginn aus einem Text: das erste kuenftige Datum darin ("21.10.2026 10:00", "21.10. um 10 Uhr", "21.10.26"),
 *  sonst die naechste volle Stunde. */
export function guessStart(text: string): Date {
  let start = new Date(); start.setMinutes(0, 0, 0); start.setHours(start.getHours() + 1)
  const rx = /\b(\d{1,2})\.(\d{1,2})\.(\d{2,4})?(?:[^\d\n]{0,12}?(\d{1,2})(?:[:.](\d{2}))?\s*(?:Uhr|h)?)?/g
  for (const hit of text.matchAll(rx)) {
    const day = Number(hit[1]), month = Number(hit[2]) - 1
    let year = hit[3] ? Number(hit[3]) : new Date().getFullYear()
    if (year < 100) year += 2000
    const hour = hit[4] ? Number(hit[4]) : 9, minute = hit[5] ? Number(hit[5]) : 0
    const d = new Date(year, month, day, hour, minute)
    if (d.getDate() === day && d > new Date() && hour < 24) { start = d; break }
  }
  return start
}

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gap: '10px 12px', '@media (max-width: 767px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  full: { gridColumn: '1 / -1' },
  addresses: { border: `1px solid ${tokens.colorNeutralStroke1}`, borderBottomColor: tokens.colorNeutralStrokeAccessible, borderRadius: tokens.borderRadiusMedium, padding: '0 8px', backgroundColor: tokens.colorNeutralBackground1 },
  people: { display: 'flex', flexWrap: 'wrap', gap: '4px 14px', fontSize: '13px', color: tokens.colorNeutralForeground2 },
  dot: { display: 'inline-block', width: '8px', height: '8px', borderRadius: '4px', marginRight: '6px' },
})

const pad = (n: number) => String(n).padStart(2, '0')
const localDate = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
const localDateTime = (d: Date) => `${localDate(d)}T${pad(d.getHours())}:${pad(d.getMinutes())}`
/** Ganztaegig: Ende liegt exklusiv auf dem Folgetag (Mitternacht) - angezeigt wird der letzte Tag. In Kalendertagen, nicht in 24 h (Sommerzeit). */
const lastDay = (start: Date, end: Date) =>
  end.getHours() === 0 && end.getMinutes() === 0 && localDate(end) > localDate(start)
    ? new Date(end.getFullYear(), end.getMonth(), end.getDate() - 1) : end
const dayNumber = (x: Date) => Math.round(Date.UTC(x.getFullYear(), x.getMonth(), x.getDate()) / 86400000)
const weekday = (d: Date) => ['SU', 'MO', 'TU', 'WE', 'TH', 'FR', 'SA'][d.getDay()]

function repeatOptions(start: Date) {
  const day = start.toLocaleDateString(locale, { weekday: 'long' })
  return [
    { value: '', label: t('Keine Wiederholung') },
    { value: 'FREQ=DAILY', label: t('Täglich') },
    { value: 'FREQ=WEEKLY;BYDAY=MO,TU,WE,TH,FR', label: t('Jeden Werktag (Mo–Fr)') },
    { value: `FREQ=WEEKLY;BYDAY=${weekday(start)}`, label: t('Wöchentlich am {day}', { day }) },
    { value: `FREQ=WEEKLY;INTERVAL=2;BYDAY=${weekday(start)}`, label: t('Alle zwei Wochen am {day}', { day }) },
    { value: `FREQ=MONTHLY;BYMONTHDAY=${start.getDate()}`, label: t('Monatlich am {n}.', { n: start.getDate() }) },
    { value: 'FREQ=YEARLY', label: t('Jährlich am {date}', { date: start.toLocaleDateString(locale, { day: 'numeric', month: 'long' }) }) },
  ]
}

const reminders = [
  { value: '', label: t('Keine') }, { value: '0', label: t('Zum Beginn') }, { value: '5', label: t('5 Minuten vorher') },
  { value: '15', label: t('15 Minuten vorher') }, { value: '30', label: t('30 Minuten vorher') }, { value: '60', label: t('1 Stunde vorher') },
  { value: '120', label: t('2 Stunden vorher') }, { value: '1440', label: t('1 Tag vorher') }, { value: '10080', label: t('1 Woche vorher') },
]

export function EventDialog({ draft, calendars, onClose }: { draft: EventDraft; calendars: Collection[]; onClose: (changed: boolean) => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [d, setD] = useState<EventDraft>(draft)
  // Teilnehmer: eigene Termine (oder neue) einladen; Einladungen anderer nur ansehen
  const [accounts, setAccounts] = useState<Account[]>([])
  useEffect(() => { api.get<Account[]>('/api/mail/accounts').then(setAccounts).catch(() => {}) }, [])
  const mine = accounts.map(a => a.email.toLowerCase())
  const foreign = !!draft.organizer && accounts.length > 0 && !mine.includes(draft.organizer.toLowerCase())
  const [people, setPeople] = useState<string[]>((draft.attendees ?? []).filter(a => !mine.includes(a.email.toLowerCase())).map(formatAddress))
  const [account, setAccount] = useState<string | undefined>(undefined)
  const [notifyPeople, setNotifyPeople] = useState(true)
  const hadPeople = (draft.attendees ?? []).length > 0
  const [moveTo, setMoveTo] = useState<number | undefined>(draft.calendar)
  const [busy, setBusy] = useState(false)
  // Termin aus einem abonnierten Kalender: nur ansehen
  const home = calendars.find(c => c.id === draft.calendar)
  const sharedRO = home?.canWrite === false && !!draft.uri
  const readOnly = (!!home?.subscription && !!draft.uri) || sharedRO
  const set = (p: Partial<EventDraft>) => setD(x => ({ ...x, ...p }))
  // Neuer Termin: Datum aus dem Titel ("Zahnarzt morgen 15 Uhr") - laufend uebernehmen, mit x verwerfen
  const [nl, setNl] = useState<NaturalDate | null>(null)
  const [nlOff, setNlOff] = useState(false)
  const [orig] = useState({ start: draft.start, end: draft.end, allDay: draft.allDay })
  const typeTitle = (title: string) => {
    if (d.uri || nlOff) { set({ title }); return }
    const nd = parseNaturalDate(title)
    setNl(nd)
    if (!nd) { set({ title, ...orig }); return }
    if (nd.hasTime) {
      const len = Math.max(orig.allDay ? 3600000 : orig.end.getTime() - orig.start.getTime(), 15 * 60000)
      set({ title, start: nd.date, end: new Date(nd.date.getTime() + len), allDay: false })
    } else {
      const day = nd.date
      set({ title, start: day, end: new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1), allDay: true })
    }
  }
  const options = repeatOptions(d.start)
  const custom = d.rRule && !options.some(o => o.value === d.rRule)

  const save = async () => {
    let scope: 'series' | 'instance' = 'series'
    if (d.recurring && d.uri) {
      const r = await askScope(t('Termin speichern'))
      if (!r) return
      scope = r
    }
    setBusy(true)
    try {
      const iso = (x: Date, end = false) => {
        if (!d.allDay) return x.toISOString()
        // Ganztaegig: Ende ist der Tag danach (wie in iCalendar)
        const y = end ? new Date(x.getFullYear(), x.getMonth(), x.getDate() + 1) : x
        return localDate(y)
      }
      const endDay = d.allDay ? lastDay(d.start, d.end) : d.end
      const r = await api.post('/api/pim/events', {
        calendar: draft.calendar ?? d.calendar, uri: d.uri, etag: d.etag, title: nl && !nlOff && !d.uri ? nl.title : d.title, location: d.location, description: d.description,
        start: iso(d.start), end: d.allDay ? iso(endDay, true) : iso(d.end), allDay: d.allDay,
        rRule: scope === 'instance' ? null : custom ? null : d.rRule, reminder: d.reminder, busy: d.busy,
        scope, instance: d.instance, moveTo: moveTo !== draft.calendar ? moveTo : undefined,
        // Teilnehmer nur bei eigenen Terminen; null laesst sie unveraendert
        attendees: foreign || readOnly ? null : people.map(parseAddress).filter(a => a.email.includes('@')),
        account: account ?? accounts[0]?.id, notify: notifyPeople,
      }) as { invited?: number; cancelled?: number; error?: string }
      if (r?.error) notify(r.error, undefined, 'warning')
      else if (r?.invited || r?.cancelled) notify(r.invited ? t('Einladung an {n} Teilnehmer im Postausgang', { n: r.invited }) : t('Absage an {n} Teilnehmer im Postausgang', { n: r.cancelled ?? 0 }))
      onClose(true)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  const remove = async () => {
    if (!d.uri || draft.calendar == null) return
    let instance: string | undefined
    if (d.recurring) {
      const r = await askScope(t('Termin löschen'))
      if (!r) return
      if (r === 'instance') instance = d.instance
    } else if (!confirm(hadPeople && !foreign ? t('Termin löschen? Die Teilnehmer bekommen eine Absage.') : t('Termin löschen?'))) return
    try {
      const r = await api.del(`/api/pim/events/${draft.calendar}/${encodeURIComponent(d.uri)}` + (instance ? `?instance=${encodeURIComponent(instance)}` : '')) as { cancelled?: number } | undefined
      if (r?.cancelled) notify(t('Absage an {n} Teilnehmer im Postausgang', { n: r.cancelled }))
      onClose(true)
    } catch (e) { fail(e) }
  }

  const setStart = (v: string) => {
    const start = d.allDay ? new Date(v + 'T00:00') : new Date(v)
    if (isNaN(start.getTime())) return
    if (d.allDay) {
      // Dauer in Kalendertagen mitnehmen
      const days = Math.max(dayNumber(d.end) - dayNumber(d.start), 0)
      set({ start, end: new Date(start.getFullYear(), start.getMonth(), start.getDate() + days) })
      return
    }
    const len = d.end.getTime() - d.start.getTime()
    set({ start, end: new Date(start.getTime() + Math.max(len, 0)) })
  }
  const setEnd = (v: string) => {
    const end = d.allDay ? new Date(v + 'T00:00') : new Date(v)
    if (!isNaN(end.getTime())) set({ end })
  }
  const endShown = d.allDay ? lastDay(d.start, d.end) : d.end

  return (
    <Dialog open onOpenChange={(_, x) => !x.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>{d.uri ? t('Termin') : t('Neuer Termin')}</DialogTitle>
          <DialogContent>
            <form className={s.grid} onSubmit={e => { e.preventDefault(); save() }}>
              <Field label={t('Titel')} className={s.full}>
                <Input value={d.title} onChange={(_, v) => typeTitle(v.value)} autoFocus placeholder={d.uri ? undefined : t('z. B. „Zahnarzt morgen 15 Uhr“')} />
                {nl && !nlOff && !d.uri && <DateHint nd={nl} onIgnore={() => { setNlOff(true); set({ ...orig }) }} />}
              </Field>
              <Field label={t('Ort')} className={s.full}><Input value={d.location ?? ''} onChange={(_, v) => set({ location: v.value })} /></Field>
              {foreign || readOnly ? (hadPeople && (
                <Field label={t('Teilnehmer')} className={s.full}>
                  <div className={s.people}>
                    {draft.organizer && <span>{t('Organisator: {who}', { who: draft.organizer })}</span>}
                    {(draft.attendees ?? []).map(a => (
                      <span key={a.email} title={a.email}><span className={s.dot} style={{ backgroundColor: statusColor(a.status) }} />{a.name || a.email} · {statusLabel(a.status)}</span>
                    ))}
                  </div>
                </Field>
              )) : <>
                <Field label={t('Teilnehmer einladen')} className={s.full} hint={people.length ? undefined : t('Adressen oder Namen aus den Kontakten – die Einladung geht per Mail, auch an Adressen außerhalb von dmOffice.')}>
                  <div className={s.addresses}><AddressInput value={people} onChange={setPeople} /></div>
                </Field>
                {(draft.attendees ?? []).some(a => !mine.includes(a.email.toLowerCase())) && (
                  <div className={mergeClasses(s.full, s.people)}>
                    {(draft.attendees ?? []).filter(a => !mine.includes(a.email.toLowerCase())).map(a => (
                      <span key={a.email} title={a.email}><span className={s.dot} style={{ backgroundColor: statusColor(a.status) }} />{a.name || a.email} · {statusLabel(a.status)}</span>
                    ))}
                  </div>
                )}
                {(people.length > 0 || hadPeople) && accounts.length === 0 && (
                  <span className={mergeClasses(s.full, s.people)}>{t('Zum Einladen braucht es ein Mailkonto – die Teilnehmer werden gespeichert, aber nicht benachrichtigt.')}</span>
                )}
                {(people.length > 0 || hadPeople) && accounts.length > 0 && <>
                  {accounts.length > 1 && !draft.organizer && (
                    <Field label={t('Einladen als')}>
                      <Dropdown value={(accounts.find(a => a.id === account) ?? accounts[0])?.email ?? ''} selectedOptions={[account ?? accounts[0]?.id ?? '']}
                        onOptionSelect={(_, v) => setAccount(v.optionValue)}>
                        {accounts.map(a => <Option key={a.id} value={a.id}>{a.email}</Option>)}
                      </Dropdown>
                    </Field>
                  )}
                  <Switch className={accounts.length > 1 && !draft.organizer ? undefined : s.full} checked={notifyPeople} onChange={(_, v) => setNotifyPeople(v.checked)}
                    label={d.uri ? t('Teilnehmer über die Änderung informieren') : t('Einladung per Mail senden')} />
                </>}
              </>}
              <Field label={t('Beginn')}>
                <Input type={d.allDay ? 'date' : 'datetime-local'} value={d.allDay ? localDate(d.start) : localDateTime(d.start)} onChange={(_, v) => setStart(v.value)} />
              </Field>
              <Field label={t('Ende')}>
                <Input type={d.allDay ? 'date' : 'datetime-local'} value={d.allDay ? localDate(endShown) : localDateTime(d.end)} onChange={(_, v) => setEnd(v.value)} />
              </Field>
              <Switch label={t('Ganztägig')} checked={d.allDay} onChange={(_, v) => set({ allDay: v.checked, ...(v.checked ? {} : { end: new Date(d.start.getTime() + 3600000) }) })} />
              <Switch label={t('Als beschäftigt zeigen')} checked={d.busy} onChange={(_, v) => set({ busy: v.checked })} />
              <Field label={t('Wiederholen')}>
                <Dropdown value={custom ? t('Eigene Regel (bleibt)') : options.find(o => o.value === d.rRule)?.label ?? ''} selectedOptions={[d.rRule]}
                  onOptionSelect={(_, v) => set({ rRule: v.optionValue ?? '' })}>
                  {custom && <Option value={d.rRule}>{t('Eigene Regel (bleibt)')}</Option>}
                  {options.map(o => <Option key={o.value} value={o.value}>{o.label}</Option>)}
                </Dropdown>
              </Field>
              <Field label={t('Erinnerung')}>
                <Dropdown value={reminders.find(r => r.value === (d.reminder == null ? '' : String(d.reminder)))?.label ?? (d.reminder === 1 ? t('1 Minute vorher') : t('{n} Minuten vorher', { n: d.reminder ?? 0 }))}
                  selectedOptions={[d.reminder == null ? '' : String(d.reminder)]}
                  onOptionSelect={(_, v) => set({ reminder: v.optionValue ? Number(v.optionValue) : null })}>
                  {reminders.map(r => <Option key={r.value} value={r.value}>{r.label}</Option>)}
                </Dropdown>
              </Field>
              <Field label={t('Kalender')} className={s.full}>
                <Dropdown value={(c => c?.owner ? t('{name} (von {owner})', { name: c.name, owner: c.owner }) : c?.name ?? '')(calendars.find(c => c.id === moveTo))} selectedOptions={[String(moveTo)]}
                  onOptionSelect={(_, v) => setMoveTo(Number(v.optionValue))}>
                  {calendars.filter(c => c.canWrite !== false || c.id === moveTo).map(c => <Option key={c.id} value={String(c.id)}>{c.owner ? t('{name} (von {owner})', { name: c.name, owner: c.owner }) : c.name}</Option>)}
                </Dropdown>
              </Field>
              <Field label={t('Beschreibung')} className={s.full}>
                <Textarea value={d.description ?? ''} onChange={(_, v) => set({ description: v.value })} rows={5} resize="vertical" />
              </Field>
              <button type="submit" hidden />
            </form>
          </DialogContent>
          <DialogActions>
            {readOnly && <span style={{ marginRight: 'auto', color: 'var(--colorNeutralForeground3)' }}>{sharedRO ? t('Der Besitzer hat diesen Kalender nur zum Lesen mit dir geteilt.') : t('Aus einem abonnierten Kalender – nur lesbar.')}</span>}
            {d.uri && !readOnly && <Button onClick={remove} style={{ marginRight: 'auto' }}>{t('Löschen')}</Button>}
            <Button onClick={() => onClose(false)}>{readOnly ? t('Schließen') : t('Abbrechen')}</Button>
            {!readOnly && <Button appearance="primary" onClick={save} disabled={busy}>{t('Speichern')}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Bei Serien: nur dieses Vorkommen oder alle? Null: abgebrochen. */
export function askScope(title: string): Promise<'series' | 'instance' | null> {
  return new Promise(resolve => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    const done = (r: 'series' | 'instance' | null) => { root.unmount(); host.remove(); resolve(r) }
    const dark = storedTheme() === 'dark' || (storedTheme() === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
    root.render(
      <FluentProvider theme={dark ? darkTheme : lightTheme}>
        <Dialog open onOpenChange={(_, x) => !x.open && done(null)}>
          <DialogSurface style={{ maxWidth: 420 }}>
            <DialogBody>
              <DialogTitle>{title}</DialogTitle>
              <DialogContent>{t('Der Termin gehört zu einer Serie.')}</DialogContent>
              <DialogActions>
                <Button onClick={() => done(null)}>{t('Abbrechen')}</Button>
                <Button onClick={() => done('instance')}>{t('Nur diesen')}</Button>
                <Button appearance="primary" onClick={() => done('series')}>{t('Ganze Serie')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      </FluentProvider>,
    )
  })
}
