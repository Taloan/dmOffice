import { Avatar, Button, Checkbox, Input, SearchBox, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { ChevronLeft16Regular, ChevronRight16Regular, Add16Regular, Dismiss16Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useState } from 'react'
import { api, CalEvent, Collection, Contact, q, Task } from '../api'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import { fmtDay, fmtTime } from '../shell/hooks'
import { locale, t } from '../i18n'
import { parseNaturalDate } from '../shell/naturalDate'
import { taskDue } from '../shell/DateHint'

// Aufgabenleiste wie in Outlook: rechts neben der Mail Kalender, Personen und Aufgaben auf einen Blick.

const useStyles = makeStyles({
  root: {
    width: '290px', flexShrink: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, overflowY: 'auto',
    display: 'flex', flexDirection: 'column', backgroundColor: tokens.colorNeutralBackground2,
  },
  section: { padding: '10px 12px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', gap: '6px' },
  head: { display: 'flex', alignItems: 'center', gap: '4px' },
  title: { flex: 1, fontWeight: 600 },
  mini: { display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', gap: '1px', fontSize: '11px', textAlign: 'center' },
  miniHead: { color: tokens.colorNeutralForeground3, padding: '2px 0' },
  day: { padding: '3px 0', borderRadius: '50%', cursor: 'pointer', position: 'relative', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  today: { backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand, ':hover': { backgroundColor: tokens.colorBrandBackgroundHover } },
  picked: { outline: `1px solid ${tokens.colorBrandStroke1}` },
  other: { color: tokens.colorNeutralForeground4 },
  dot: { position: 'absolute', bottom: '1px', left: '50%', width: '3px', height: '3px', marginLeft: '-1.5px', borderRadius: '50%', backgroundColor: tokens.colorBrandForeground1 },
  event: { display: 'flex', gap: '8px', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  bar: { width: '3px', borderRadius: '2px', flexShrink: 0 },
  small: { fontSize: '12px', color: tokens.colorNeutralForeground3 },
  dayHead: { fontSize: '12px', fontWeight: 600, color: tokens.colorNeutralForeground2, marginTop: '4px' },
  person: { display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  ellipsis: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  task: { display: 'flex', alignItems: 'center', gap: '2px' },
  late: { color: tokens.colorPaletteRedForeground1 },
})

const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate())

export function TodoBar({ onWrite }: { onWrite: (address: string, name?: string) => void }) {
  const s = useStyles()
  const { prefs, update } = usePrefs()
  const parts = prefs.todoBar ?? []
  const close = (p: string) => update({ todoBar: parts.filter(x => x !== p) })
  return (
    <aside className={s.root}>
      {parts.includes('calendar') && <CalendarPart onClose={() => close('calendar')} />}
      {parts.includes('people') && <PeoplePart onWrite={onWrite} onClose={() => close('people')} />}
      {parts.includes('tasks') && <TasksPart onClose={() => close('tasks')} />}
    </aside>
  )
}

function Head({ title, onClose, children }: { title: string; onClose: () => void; children?: React.ReactNode }) {
  const s = useStyles()
  return (
    <div className={s.head}>
      <Text className={s.title}>{title}</Text>
      {children}
      <Button appearance="subtle" size="small" icon={<Dismiss16Regular />} onClick={onClose} aria-label={t('Ausblenden')} />
    </div>
  )
}

function CalendarPart({ onClose }: { onClose: () => void }) {
  const s = useStyles()
  const { prefs } = usePrefs()
  const [month, setMonth] = useState(() => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1) })
  const [picked, setPicked] = useState(startOfDay(new Date()))
  const [events, setEvents] = useState<CalEvent[]>([])
  const [colors, setColors] = useState<Record<number, string>>({})

  // Monat fuer die Punkte im Kalender und die naechsten Tage ab dem gewaehlten Tag
  useEffect(() => {
    const from = new Date(Math.min(month.getTime(), picked.getTime()) - 7 * 86400000)
    const to = new Date(Math.max(new Date(month.getFullYear(), month.getMonth() + 1, 7).getTime(), picked.getTime() + 8 * 86400000))
    api.get<CalEvent[]>('/api/pim/events?' + q({ from: from.toISOString(), to: to.toISOString() })).then(setEvents).catch(() => {})
  }, [month, picked])
  useEffect(() => {
    api.get<Collection[]>('/api/pim/collections/calendars').then(c => setColors(Object.fromEntries(c.map(x => [x.id, x.color ?? '#0F6CBD'])))).catch(() => {})
  }, [])

  const dayOf = (e: CalEvent) => (e.allDay ? new Date(e.start + 'T00:00') : new Date(e.start))
  const busyDays = useMemo(() => new Set(events.map(e => startOfDay(dayOf(e)).toDateString())), [events])
  const first = (prefs.firstDay ?? 1)
  const offset = (month.getDay() - first + 7) % 7
  const gridStart = new Date(month.getFullYear(), month.getMonth(), 1 - offset)
  const days = Array.from({ length: 42 }, (_, i) => new Date(gridStart.getFullYear(), gridStart.getMonth(), gridStart.getDate() + i))
  const names = [t('So'), t('Mo'), t('Di'), t('Mi'), t('Do'), t('Fr'), t('Sa')]
  const today = new Date().toDateString()

  // Die naechsten sieben Tage ab dem gewaehlten, nach Tag gruppiert
  const upcoming = events
    .filter(e => { const d = startOfDay(dayOf(e)); return d >= picked && d < new Date(picked.getTime() + 7 * 86400000) && (!e.allDay || true) })
    .filter(e => e.allDay || new Date(e.end) > new Date())
    .sort((a, b) => dayOf(a).getTime() - dayOf(b).getTime())
  const groups = new Map<string, CalEvent[]>()
  upcoming.forEach(e => { const k = startOfDay(dayOf(e)).toDateString(); groups.set(k, [...(groups.get(k) ?? []), e]) })

  return (
    <div className={s.section}>
      <Head title={month.toLocaleDateString(locale, { month: 'long', year: 'numeric' })} onClose={onClose}>
        <Button appearance="subtle" size="small" icon={<ChevronLeft16Regular />} onClick={() => setMonth(new Date(month.getFullYear(), month.getMonth() - 1, 1))} />
        <Button appearance="subtle" size="small" icon={<ChevronRight16Regular />} onClick={() => setMonth(new Date(month.getFullYear(), month.getMonth() + 1, 1))} />
      </Head>
      <div className={s.mini}>
        {Array.from({ length: 7 }, (_, i) => <div key={i} className={s.miniHead}>{names[(i + first) % 7]}</div>)}
        {days.map(d => (
          <div key={d.toISOString()} onClick={() => setPicked(startOfDay(d))}
            className={mergeClasses(s.day, d.toDateString() === today && s.today, d.getMonth() !== month.getMonth() && s.other,
              d.toDateString() === picked.toDateString() && d.toDateString() !== today && s.picked)}>
            {d.getDate()}
            {busyDays.has(d.toDateString()) && d.toDateString() !== today && <span className={s.dot} />}
          </div>
        ))}
      </div>
      {groups.size === 0 && <Text className={s.small}>{t('Keine Termine in den nächsten sieben Tagen.')}</Text>}
      {[...groups.entries()].map(([k, list]) => {
        const d = new Date(k)
        const label = d.toDateString() === today ? t('Heute') : d.toDateString() === new Date(Date.now() + 86400000).toDateString() ? t('Morgen')
          : `${d.toLocaleDateString(locale, { weekday: 'long' })}, ${fmtDay(d, false)}`
        return (
          <div key={k}>
            <div className={s.dayHead}>{label}</div>
            {list.map(e => (
              <div key={e.id} className={s.event} onClick={() => { location.hash = '#/calendar' }} title={e.location ?? ''}>
                <span className={s.bar} style={{ backgroundColor: colors[e.calendar] ?? tokens.colorBrandBackground }} />
                <div className={s.ellipsis}>
                  <div className={s.ellipsis}>{e.title || t('(ohne Titel)')}</div>
                  <div className={s.small}>{e.allDay ? t('Ganztägig') : `${fmtTime(new Date(e.start))} – ${fmtTime(new Date(e.end))}`}{e.location ? ` · ${e.location}` : ''}</div>
                </div>
              </div>
            ))}
          </div>
        )
      })}
    </div>
  )
}

function PeoplePart({ onWrite, onClose }: { onWrite: (address: string, name?: string) => void; onClose: () => void }) {
  const s = useStyles()
  const [all, setAll] = useState<Contact[]>([])
  const [term, setTerm] = useState('')
  useEffect(() => { api.get<Contact[]>('/api/pim/contacts').then(setAll).catch(() => {}) }, [])
  const needle = term.trim().toLowerCase()
  const shown = needle
    ? all.filter(c => [c.displayName, c.org, ...c.emails.map(e => e.value), ...c.phones.map(p => p.value)].some(x => x.toLowerCase().includes(needle))).slice(0, 30)
    : []
  return (
    <div className={s.section}>
      <Head title={t('Personen')} onClose={onClose} />
      <SearchBox size="small" placeholder={t('Person suchen')} value={term} onChange={(_, d) => setTerm(d.value)} />
      {needle && shown.length === 0 && <Text className={s.small}>{t('Niemand gefunden.')}</Text>}
      {shown.map(c => (
        <div key={c.id} className={s.person} title={c.emails[0] ? t('E-Mail an {address}', { address: c.emails[0].value }) : ''}
          onClick={() => c.emails[0] && onWrite(c.emails[0].value, c.displayName)}>
          <Avatar name={c.displayName} size={28} color="colorful" />
          <div className={s.ellipsis}>
            <div className={s.ellipsis}>{c.displayName}</div>
            <div className={mergeClasses(s.small, s.ellipsis)}>{c.emails[0]?.value ?? c.phones[0]?.value ?? c.org}</div>
          </div>
        </div>
      ))}
    </div>
  )
}

function TasksPart({ onClose }: { onClose: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [tasks, setTasks] = useState<Task[]>([])
  const [lists, setLists] = useState<Collection[]>([])
  const [title, setTitle] = useState('')
  const load = () => api.get<Task[]>('/api/pim/tasks').then(setTasks).catch(() => {})
  useEffect(() => {
    load()
    api.get<Collection[]>('/api/pim/collections/calendars').then(l => setLists(l.filter(c => c.components.includes('VTODO')))).catch(() => {})
  }, [])
  const open = tasks.filter(x => !x.completed).sort((a, b) => (a.due ?? '9999').localeCompare(b.due ?? '9999'))
  const today = new Date().toLocaleDateString('sv-SE')
  const save = async (x: Partial<Task> & { list: number; title: string }) => {
    try {
      await api.post('/api/pim/tasks', { list: x.list, uri: x.uri, etag: x.etag, title: x.title, description: x.description, due: x.due, dueAllDay: x.dueAllDay ?? true,
        priority: x.priority ?? 0, completed: x.completed ?? false, categories: x.categories })
      load()
    } catch (e) { fail(e) }
  }
  const add = async () => {
    const list = lists.find(l => !l.components.includes('VEVENT'))?.id ?? lists[0]?.id
    if (!title.trim() || !list) return
    const nd = parseNaturalDate(title)
    await save({ list, title: nd?.title ?? title.trim(), ...(nd ? taskDue(nd) : {}) })
    setTitle('')
  }
  return (
    <div className={s.section}>
      <Head title={t('Aufgaben')} onClose={onClose}>
        <Text className={s.small}>{open.length || ''}</Text>
      </Head>
      <form onSubmit={e => { e.preventDefault(); add() }}>
        <Input size="small" style={{ width: '100%' }} placeholder={t('Aufgabe hinzufügen')} value={title} onChange={(_, d) => setTitle(d.value)} contentBefore={<Add16Regular />} />
      </form>
      {parseNaturalDate(title) && <Text size={100} className={s.small}>📅 {(() => { const nd = parseNaturalDate(title)!; return nd.date.toLocaleDateString(locale, { weekday: 'short', day: '2-digit', month: '2-digit' }) + (nd.hasTime ? ' ' + fmtTime(nd.date) : '') })()}</Text>}
      {open.length === 0 && <Text className={s.small}>{t('Nichts offen.')}</Text>}
      {open.slice(0, 25).map(task => {
        const late = !!task.due && task.due.slice(0, 10) < today
        return (
          <div key={task.id} className={s.task}>
            <Checkbox shape="circular" size="medium" checked={false} onChange={() => { setTasks(x => x.map(y => y.id === task.id ? { ...y, completed: true } : y)); save({ ...task, completed: true }) }} />
            <div className={s.ellipsis} style={{ flex: 1 }}>
              <div className={s.ellipsis}>{task.title}</div>
              {task.due && <div className={mergeClasses(s.small, late && s.late)}>{t('fällig {date}', { date: fmtDay(new Date(task.due.slice(0, 10) + 'T00:00'), false) })}</div>}
            </div>
          </div>
        )
      })}
      {open.length > 25 && <Button appearance="transparent" size="small" onClick={() => { location.hash = '#/tasks' }}>{t('Alle {n} Aufgaben', { n: open.length })}</Button>}
    </div>
  )
}
