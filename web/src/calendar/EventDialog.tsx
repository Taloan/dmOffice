import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Switch,
  Textarea, makeStyles,
} from '@fluentui/react-components'
import { useState } from 'react'
import { createRoot } from 'react-dom/client'
import { FluentProvider } from '@fluentui/react-components'
import { api, Collection } from '../api'
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
}

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px 12px' },
  full: { gridColumn: '1 / -1' },
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
  const { fail } = useApp()
  const [d, setD] = useState<EventDraft>(draft)
  const [moveTo, setMoveTo] = useState<number | undefined>(draft.calendar)
  const [busy, setBusy] = useState(false)
  // Termin aus einem abonnierten Kalender: nur ansehen
  const readOnly = !!calendars.find(c => c.id === draft.calendar)?.subscription && !!draft.uri
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
      await api.post('/api/pim/events', {
        calendar: draft.calendar ?? d.calendar, uri: d.uri, etag: d.etag, title: nl && !nlOff && !d.uri ? nl.title : d.title, location: d.location, description: d.description,
        start: iso(d.start), end: d.allDay ? iso(endDay, true) : iso(d.end), allDay: d.allDay,
        rRule: scope === 'instance' ? null : custom ? null : d.rRule, reminder: d.reminder, busy: d.busy,
        scope, instance: d.instance, moveTo: moveTo !== draft.calendar ? moveTo : undefined,
      })
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
    } else if (!confirm(t('Termin löschen?'))) return
    try {
      await api.del(`/api/pim/events/${draft.calendar}/${encodeURIComponent(d.uri)}` + (instance ? `?instance=${encodeURIComponent(instance)}` : ''))
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
                <Dropdown value={calendars.find(c => c.id === moveTo)?.name ?? ''} selectedOptions={[String(moveTo)]}
                  onOptionSelect={(_, v) => setMoveTo(Number(v.optionValue))}>
                  {calendars.map(c => <Option key={c.id} value={String(c.id)}>{c.name}</Option>)}
                </Dropdown>
              </Field>
              <Field label={t('Beschreibung')} className={s.full}>
                <Textarea value={d.description ?? ''} onChange={(_, v) => set({ description: v.value })} rows={5} resize="vertical" />
              </Field>
              <button type="submit" hidden />
            </form>
          </DialogContent>
          <DialogActions>
            {readOnly && <span style={{ marginRight: 'auto', color: 'var(--colorNeutralForeground3)' }}>{t('Aus einem abonnierten Kalender – nur lesbar.')}</span>}
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
