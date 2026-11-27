import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Input, Option, Text, makeStyles, tokens } from '@fluentui/react-components'
import { Add16Regular, Dismiss16Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { CalendarRemindDefault, Collection, featureOn, RemindRule } from '../api'
import { t } from '../i18n'
import { FilterSelect } from '../shell/FilterSelect'

/** Höchstens vier Wochen vorher (wie der Server). */
export const MAX_REMIND = 28 * 1440

type Unit = 'min' | 'hour' | 'day' | 'week' | 'at'
const factor: Record<Exclude<Unit, 'at'>, number> = { min: 1, hour: 60, day: 1440, week: 10080 }

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '6px' },
  row: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '6px' },
  // Zeitpunkt und Art bleiben je für sich zusammen; umgebrochen wird dazwischen
  part: { display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: '6px' },
  num: { width: '72px' },
  time: { width: '124px' },
  hint: { color: tokens.colorNeutralForeground3 },
})

const pad = (n: number) => String(n).padStart(2, '0')

/** Ganztägig: Minuten vor 0:00 → Tage vorher und Uhrzeit (360 → 1 Tag, 18:00; -480 → 0 Tage, 08:00). */
export const toDayTime = (m: number) => {
  const days = m > 0 ? Math.ceil(m / 1440) : 0
  const ofDay = days * 1440 - m
  return { days, time: `${pad(Math.floor(ofDay / 60) % 24)}:${pad(ofDay % 60)}` }
}
const fromDayTime = (days: number, time: string) => {
  const [h, mm] = time.split(':').map(Number)
  return days * 1440 - ((h || 0) * 60 + (mm || 0))
}

/** Einheit für die Anzeige: „Tage vorher um“ bei at (ganztägig), sonst glatte Minuten/Stunden/Tage/Wochen. */
const unitOf = (r: RemindRule, allDay: boolean | 'mixed'): Unit => {
  if (r.at && allDay !== false) return 'at'
  const m = r.minutes
  if (m > 0 && m % 10080 === 0) return 'week'
  if (m > 0 && m % 1440 === 0) return 'day'
  if (m > 0 && m % 60 === 0) return 'hour'
  return 'min'
}

/** Kurzer Text einer Erinnerung, z. B. „Am Vortag um 18:00“ oder „15 Minuten vorher“ (ganztägig: vor 9:00). */
export function remindText(r: RemindRule, allDay: boolean, lists?: Collection[]): string {
  let text: string
  if (allDay && r.at) {
    const { days, time } = toDayTime(r.minutes)
    text = days === 0 ? t('Am Tag um {time}', { time }) : days === 1 ? t('Am Vortag um {time}', { time }) : t('{n} Tage vorher um {time}', { n: days, time })
  } else {
    const m = r.minutes
    text = m === 0 ? t('Zum Beginn')
      : m % 10080 === 0 ? (m === 10080 ? t('1 Woche vorher') : t('{n} Wochen vorher', { n: m / 10080 }))
      : m % 1440 === 0 ? (m === 1440 ? t('1 Tag vorher') : t('{n} Tage vorher', { n: m / 1440 }))
      : m % 60 === 0 ? (m === 60 ? t('1 Stunde vorher') : t('{n} Stunden vorher', { n: m / 60 }))
      : t('{n} Minuten vorher', { n: m })
  }
  if (r.list != null) {
    const name = lists?.find(l => l.id === r.list)?.name ?? '?'
    text += ' · ' + t('Aufgabe in „{list}“', { list: name })
  }
  return text
}

/** Zusammenfassung des Kalender-Standards, z. B. für den Hinweis im Termin. */
export function defaultText(d: CalendarRemindDefault | null | undefined, title: string, allDay: boolean, lists?: Collection[]) {
  if (!d) return ''
  const rule = d.rules.find(r => r.contains.trim() && title.toLocaleLowerCase().includes(r.contains.trim().toLocaleLowerCase()))
  const list = rule?.reminders ?? d.reminders
  return list.length ? list.map(r => remindText(r, allDay, lists)).join(', ') : t('Keine')
}

function Row({ rule, onChange, onRemove, allDay, taskLists }: {
  rule: RemindRule; onChange: (r: RemindRule) => void; onRemove: () => void; allDay: boolean | 'mixed'; taskLists: Collection[]
}) {
  const s = useStyles()
  const [unit, setUnit] = useState<Unit>(() => unitOf(rule, allDay))
  const units: [Unit, string][] = [
    ...(allDay !== false ? [['at', t('Tage vorher um')] as [Unit, string]] : []),
    ['min', t('Minuten vorher')], ['hour', t('Stunden vorher')], ['day', t('Tage vorher')], ['week', t('Wochen vorher')],
  ]
  const set = (minutes: number, u: Unit = unit) => onChange({ ...rule, minutes: Math.max(-1440, Math.min(MAX_REMIND, minutes)), at: u === 'at' })
  const dt = toDayTime(rule.minutes)
  const amount = unit === 'at' ? dt.days : Math.round(rule.minutes / factor[unit])
  const changeUnit = (u: Unit) => {
    setUnit(u)
    if (u === 'at') set(fromDayTime(Math.max(1, Math.ceil(Math.max(rule.minutes, 0) / 1440)), '18:00'), u)
    else set(Math.max(0, Math.round(rule.minutes / factor[u])) * factor[u], u)
  }
  const tasks = featureOn('calremtask') && taskLists.length > 0
  return (
    <div className={s.row}>
      <div className={s.part}>
      <Input className={s.num} type="number" min={0} max={unit === 'at' ? 28 : Math.floor(MAX_REMIND / factor[unit as Exclude<Unit, 'at'>])} value={String(amount)}
        aria-label={t('Anzahl')}
        onChange={(_, v) => { const n = Math.max(0, Number(v.value) || 0); set(unit === 'at' ? fromDayTime(n, dt.time) : n * factor[unit]) }} />
      <Dropdown style={{ minWidth: 150 }} value={units.find(u => u[0] === unit)?.[1] ?? ''} selectedOptions={[unit]} aria-label={t('Einheit')}
        onOptionSelect={(_, v) => changeUnit(v.optionValue as Unit)}>
        {units.map(([u, l]) => <Option key={u} value={u}>{l}</Option>)}
      </Dropdown>
      {unit === 'at' && <Input className={s.time} type="time" value={dt.time} aria-label={t('Uhrzeit')} onChange={(_, v) => v.value && set(fromDayTime(dt.days, v.value))} />}
      {unit === 'at' && dt.days <= 1 && <Text size={200} className={s.hint}>{dt.days === 0 ? t('(am Tag)') : t('(am Vortag)')}</Text>}
      </div>
      <div className={s.part}>
      {tasks && <Dropdown style={{ minWidth: 150 }} value={rule.list != null ? t('Aufgabe anlegen') : t('Benachrichtigung')} selectedOptions={[rule.list != null ? 'task' : 'note']}
        aria-label={t('Art der Erinnerung')}
        onOptionSelect={(_, v) => onChange({ ...rule, list: v.optionValue === 'task' ? taskLists[0].id : null })}>
        <Option value="note">{t('Benachrichtigung')}</Option>
        <Option value="task">{t('Aufgabe anlegen')}</Option>
      </Dropdown>}
      {tasks && rule.list != null && <FilterSelect style={{ width: 200 }} value={String(rule.list)} aria-label={t('Aufgabenliste')}
        items={taskLists.map(l => ({ id: String(l.id), label: l.name }))} onChange={v => onChange({ ...rule, list: Number(v) })} />}
      <Button appearance="subtle" size="small" icon={<Dismiss16Regular />} title={t('Entfernen')} aria-label={t('Entfernen')} onClick={onRemove} />
      </div>
    </div>
  )
}

/**
 * Mehrere Erinnerungen bearbeiten: Zahl und Einheit, ganztägig „N Tage vorher um HH:MM“; mit calremtask wahlweise
 * „Aufgabe anlegen“ in einer Aufgabenliste. allDay 'mixed': Kalender-Standard (gilt für Termine mit und ohne Uhrzeit).
 */
export function ReminderList({ value, onChange, allDay, taskLists, disabled, noHint }: {
  value: RemindRule[]; onChange: (v: RemindRule[]) => void; allDay: boolean | 'mixed'; taskLists: Collection[]; disabled?: boolean
  /** Hinweis zu „Tage vorher um“ zeigt der Aufrufer selbst (einmal für mehrere Listen) */
  noHint?: boolean
}) {
  const s = useStyles()
  // Schlüssel je Zeile, damit die gewählte Einheit beim Entfernen anderer Zeilen bleibt
  const [keys, setKeys] = useState<number[]>(() => value.map((_, i) => i))
  const [next, setNext] = useState(value.length)
  const ks = keys.length === value.length ? keys : value.map((_, i) => i)
  const add = () => {
    // Nächster freier Vorschlag: mit Uhrzeit 15 Min., 1 Std., 1 Tag …; ganztägig Vortag 18:00, 2 Tage vorher 18:00 …
    const at = allDay !== false
    const offers = at ? [360, 1800, 3240, -480, 4680] : [15, 60, 1440, 10080, 5, 30, 120]
    const minutes = offers.find(m => !value.some(r => r.minutes === m && !!r.at === at)) ?? offers[0]
    onChange([...value, at ? { minutes, at } : { minutes }])
    setKeys([...ks, next]); setNext(next + 1)
  }
  return (
    <div className={s.list} style={disabled ? { opacity: .6, pointerEvents: 'none' } : undefined}>
      {value.length === 0 && <Text className={s.hint}>{t('Keine Erinnerung')}</Text>}
      {value.map((r, i) => <Row key={ks[i]} rule={r} allDay={allDay} taskLists={taskLists}
        onChange={x => onChange(value.map((y, j) => j === i ? x : y))}
        onRemove={() => { onChange(value.filter((_, j) => j !== i)); setKeys(ks.filter((_, j) => j !== i)) }} />)}
      {value.length < 20 && !disabled && <div><Button size="small" icon={<Add16Regular />} onClick={add}>{t('Erinnerung hinzufügen')}</Button></div>}
      {!noHint && <RemindHints lists={[value]} allDay={allDay} />}
    </div>
  )
}

/** Hinweise zu „Tage vorher um“ und zu relativen Angaben bei ganztägigen Terminen. */
export function RemindHints({ lists, allDay }: { lists: RemindRule[][]; allDay: boolean | 'mixed' }) {
  const s = useStyles()
  const all = lists.flat()
  const at = allDay === 'mixed' && all.some(r => unitOf(r, 'mixed') === 'at')
  const rel = allDay !== false && all.some(r => unitOf(r, allDay) !== 'at')
  return <>
    {at && <Text size={200} className={s.hint}>{t('„Tage vorher um“ gilt für ganztägige Termine; bei Terminen mit Uhrzeit zählt derselbe Abstand vor dem Beginn, „Am Tag um“ entfällt.')}</Text>}
    {rel && <Text size={200} className={s.hint}>{t('Bei ganztägigen Terminen zählen Minuten, Stunden, Tage und Wochen ab 9:00.')}</Text>}
  </>
}

/** Kalender-Standard bearbeiten: Vorgabe und Regeln nach Titel („enthält Papier“). readOnly: geteilter Kalender, nur ansehen. */
export function RemindDefaultEditor({ value, onChange, taskLists, readOnly }: {
  value: CalendarRemindDefault; onChange: (v: CalendarRemindDefault) => void; taskLists: Collection[]; readOnly?: boolean
}) {
  const s = useStyles()
  return (
    <div className={s.list} style={{ gap: 12 }}>
      <Text className={s.hint} size={200}>{t('Gilt für jeden Termin dieses Kalenders ohne eigene Erinnerung. Die Termine selbst bleiben unverändert.')}</Text>
      {readOnly && <Text size={200} weight="semibold">{t('Nur der Besitzer kann die Standard-Erinnerung ändern.')}</Text>}
      <div>
        <Text weight="semibold" block style={{ marginBottom: 4 }}>{t('Standard')}</Text>
        <ReminderList value={value.reminders} allDay="mixed" noHint disabled={readOnly} taskLists={taskLists} onChange={reminders => onChange({ ...value, reminders })} />
      </div>
      <div>
        <Text weight="semibold" block>{t('Regeln nach Titel')}</Text>
        <Text className={s.hint} size={200} block style={{ marginBottom: 4 }}>{t('Die erste passende Regel ersetzt den Standard.')}</Text>
        <div className={s.list} style={{ gap: 10 }}>
          {value.rules.map((r, i) => (
            <div key={i} style={{ borderLeft: `2px solid ${tokens.colorNeutralStroke2}`, paddingLeft: 8 }}>
              <div className={s.row} style={{ marginBottom: 4 }}>
                <Text>{t('Titel enthält')}</Text>
                <Input value={r.contains} style={{ flex: 1, minWidth: 120 }} aria-label={t('Titel enthält')} readOnly={readOnly}
                  onChange={(_, v) => onChange({ ...value, rules: value.rules.map((x, j) => j === i ? { ...x, contains: v.value } : x) })} />
                {!readOnly && <Button appearance="subtle" size="small" icon={<Dismiss16Regular />} title={t('Regel entfernen')} aria-label={t('Regel entfernen')}
                  onClick={() => onChange({ ...value, rules: value.rules.filter((_, j) => j !== i) })} />}
              </div>
              <ReminderList value={r.reminders} allDay="mixed" noHint disabled={readOnly} taskLists={taskLists}
                onChange={reminders => onChange({ ...value, rules: value.rules.map((x, j) => j === i ? { ...x, reminders } : x) })} />
            </div>
          ))}
          {readOnly && value.rules.length === 0 && <Text className={s.hint}>{t('Keine')}</Text>}
          {!readOnly && <div><Button size="small" icon={<Add16Regular />} onClick={() => onChange({ ...value, rules: [...value.rules, { contains: '', reminders: [{ minutes: 360, at: true }] }] })}>{t('Regel hinzufügen')}</Button></div>}
        </div>
      </div>
      <RemindHints lists={[value.reminders, ...value.rules.map(r => r.reminders)]} allDay="mixed" />
    </div>
  )
}

/** Geteilter Kalender: Standard-Erinnerung des Besitzers ansehen (ändern kann nur er). */
export function SharedRemindDialog({ collection, onClose }: { collection: Collection; onClose: () => void }) {
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{t('Standard-Erinnerung: {name}', { name: collection.name })}</DialogTitle>
          <DialogContent>
            <RemindDefaultEditor value={collection.remind ?? { reminders: [], rules: [] }} onChange={() => {}} taskLists={[]} readOnly />
          </DialogContent>
          <DialogActions><Button onClick={onClose}>{t('Schließen')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
