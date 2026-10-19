import FullCalendar from '@fullcalendar/react'
import dayGridPlugin from '@fullcalendar/daygrid'
import timeGridPlugin from '@fullcalendar/timegrid'
import listPlugin from '@fullcalendar/list'
import interactionPlugin from '@fullcalendar/interaction'
import deLocale from '@fullcalendar/core/locales/de'
import enGbLocale from '@fullcalendar/core/locales/en-gb'
import type { DateSelectArg, EventClickArg, EventDropArg, EventInput as FcEvent } from '@fullcalendar/core'
import type { EventResizeDoneArg } from '@fullcalendar/interaction'
import {
  Button, Checkbox, Menu, MenuItem, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, Text, Toolbar, ToolbarButton, Tooltip,
  makeStyles, tokens, Switch } from '@fluentui/react-components'
import {
  ChevronLeft20Regular, ChevronRight20Regular, CalendarToday20Regular, MoreVertical16Regular, CalendarAdd20Regular, TaskListSquareLtr16Regular, Rss20Regular, Rss16Regular,
  CalendarDay20Regular, CalendarWorkWeek20Regular, CalendarWeekNumbers20Regular, CalendarMonth20Regular, CalendarAgenda20Regular, CalendarLtr20Regular,
  PanelLeft20Regular, Settings20Regular, TaskListLtr20Regular, Clock20Regular, CalendarWeekStart20Regular,
} from '@fluentui/react-icons'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { useCallback, useEffect, useRef, useState } from 'react'
import { api, CalEvent, Collection, q, Task } from '../api'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import { EventDialog, EventDraft, askScope } from './EventDialog'
import { CollectionDialog, importInto } from './CollectionDialog'
import { SubscribeDialog } from './SubscribeDialog'
import { lang, locale, t } from '../i18n'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0 },
  side: { width: '240px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '12px', display: 'flex', flexDirection: 'column', gap: '12px', overflowY: 'auto' },
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  bar: { borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, padding: '4px 8px', gap: '4px' },
  title: { fontSize: '18px', fontWeight: 600, marginLeft: '8px', marginRight: 'auto' },
  cal: { flex: 1, minHeight: 0, padding: '0 8px 8px' },
  calRow: { display: 'flex', alignItems: 'center', gap: '4px' },
  dot: { width: '10px', height: '10px', borderRadius: '3px', flexShrink: 0 },
  mini: { display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', gap: '1px', fontSize: '12px', textAlign: 'center' },
  miniDay: { padding: '4px 0', borderRadius: '50%', cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  miniHead: { color: tokens.colorNeutralForeground3, padding: '2px 0' },
  miniToday: { backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand, ':hover': { backgroundColor: tokens.colorBrandBackgroundHover } },
  miniOther: { color: tokens.colorNeutralForeground4 },
  section: { fontWeight: 600, fontSize: '12px', color: tokens.colorNeutralForeground2, textTransform: 'uppercase', letterSpacing: '.04em' },
})

const views = [
  { key: 'timeGridDay', label: t('Tag'), icon: <CalendarDay20Regular /> },
  { key: 'timeGridWorkWeek', label: t('Arbeits­woche'), icon: <CalendarWorkWeek20Regular /> },
  { key: 'timeGridWeek', label: t('Woche'), icon: <CalendarWeekStart20Regular /> },
  { key: 'dayGridMonth', label: t('Monat'), icon: <CalendarMonth20Regular /> },
  { key: 'listMonth', label: t('Liste'), icon: <CalendarAgenda20Regular /> },
]

const HIDDEN = 'dmoffice.calendars.hidden'
// Sprache von FullCalendar folgt der Oberflaeche; Wochenbeginn usw. setzen die Einstellungen darueber
const fcLocale = lang === 'en' ? enGbLocale : deLocale

export default function CalendarView() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const fc = useRef<FullCalendar>(null)
  const { prefs, update } = usePrefs()
  const [sidePane, toggleSide] = useSidePane('calendar')
  const [calendars, setCalendars] = useState<Collection[]>([])
  const [hidden, setHidden] = useState<number[]>(() => { try { return JSON.parse(localStorage.getItem(HIDDEN) || '[]') } catch { return [] } })
  const [view, setView] = useState(() => localStorage.getItem('dmoffice.calendar.view') || 'timeGridWorkWeek')
  const [title, setTitle] = useState('')
  const [draft, setDraft] = useState<EventDraft | null>(null)
  const [editColl, setEditColl] = useState<Collection | 'new' | null>(null)
  const [subscribe, setSubscribe] = useState<Collection | 'new' | null>(null)
  const refreshSub = async (c: Collection) => {
    try {
      const r = await api.post<{ count: number }>(`/api/pim/subscriptions/${c.id}/refresh`)
      notify(t('Aktualisiert'), r.count >= 0 ? (r.count === 1 ? t('1 Termin') : t('{n} Termine', { n: r.count })) : undefined)
      loadCalendars()
    } catch (e) { fail(e); loadCalendars() }
  }
  const endSub = async (c: Collection) => {
    if (!confirm(t('Abo „{name}“ beenden? Die Termine verschwinden aus dem Kalender.', { name: c.name }))) return
    try { await api.del(`/api/pim/collections/${c.id}`); loadCalendars() } catch (e) { fail(e) }
  }
  const [miniMonth, setMiniMonth] = useState(() => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), 1) })
  const importInput = useRef<HTMLInputElement>(null)
  const importTarget = useRef<number>(0)

  const [taskLists, setTaskLists] = useState<Collection[]>([])
  const showTasks = prefs.calendarTasks ?? true
  const loadCalendars = useCallback(() =>
    api.get<Collection[]>('/api/pim/collections/calendars').then(list => {
      setCalendars(list.filter(c => c.components.includes('VEVENT')))
      setTaskLists(list.filter(c => c.components.includes('VTODO')))
    }).catch(fail), [fail])
  useEffect(() => { loadCalendars() }, [loadCalendars])

  const refetch = () => fc.current?.getApi().refetchEvents()
  useEffect(() => { refetch() }, [hidden, calendars, showTasks])
  useEffect(() => { try { localStorage.setItem(HIDDEN, JSON.stringify(hidden)) } catch { /* egal */ } }, [hidden])

  const colorOf = (id: number) => [...calendars, ...taskLists].find(c => c.id === id)?.color || '#0F6CBD'

  const fetchEvents = useCallback(async (info: { startStr: string; endStr: string; start: Date; end: Date }) => {
    const visible = calendars.filter(c => !hidden.includes(c.id)).map(c => c.id)
    const list = visible.length
      ? await api.get<CalEvent[]>('/api/pim/events?' + q({ from: info.start.toISOString(), to: info.end.toISOString(), calendars: visible.join(',') }))
      : []
    const events = list.map<FcEvent>(e => ({
      id: e.id, title: e.title, start: e.start, end: e.end, allDay: e.allDay,
      backgroundColor: colorOf(e.calendar), borderColor: colorOf(e.calendar), textColor: '#fff',
      editable: !calendars.find(c => c.id === e.calendar)?.subscription,
      classNames: e.busy ? [] : ['dm-free'], extendedProps: { ev: e },
    }))
    // Aufgaben mit Faelligkeit: am Faelligkeitstag, mit Kaestchen zum Abhaken
    const lists = taskLists.filter(c => !hidden.includes(c.id)).map(c => c.id)
    if (!showTasks || !lists.length) return events
    const tasks = await api.get<Task[]>('/api/pim/tasks?' + q({ lists: lists.join(',') })).catch(() => [] as Task[])
    for (const task of tasks) {
      if (!task.due) continue
      const due = new Date(task.due)
      if (due < info.start || due >= info.end) continue
      const color = colorOf(task.list)
      events.push({
        id: 'task:' + task.id, title: task.title, allDay: task.dueAllDay, start: task.dueAllDay ? task.due.slice(0, 10) : task.due,
        end: task.dueAllDay ? undefined : new Date(due.getTime() + 30 * 60000).toISOString(),
        backgroundColor: 'transparent', borderColor: color, textColor: 'inherit', editable: false,
        classNames: ['dm-task', task.completed ? 'dm-task-done' : ''], extendedProps: { task, color },
      } as FcEvent)
    }
    return events
  }, [calendars, taskLists, hidden, showTasks]) // eslint-disable-line react-hooks/exhaustive-deps

  const toggleTask = async (task: Task) => {
    try {
      await api.post('/api/pim/tasks', {
        list: task.list, uri: task.uri, etag: task.etag, title: task.title, description: task.description, due: task.due, dueAllDay: task.dueAllDay,
        priority: task.priority, completed: !task.completed, percent: task.completed ? 0 : 100, categories: task.categories,
      })
      refetch()
    } catch (e) { fail(e) }
  }

  // Listenansicht: zum heutigen Tag (oder dem naechsten mit Terminen) scrollen
  const calBox = useRef<HTMLDivElement>(null)
  const scrollToToday = () => setTimeout(() => {
    const api = fc.current?.getApi()
    if (!api || !api.view.type.startsWith('list')) return
    const today = new Date().toLocaleDateString('sv-SE')
    const days = Array.from(calBox.current?.querySelectorAll<HTMLElement>('.fc-list-day[data-date]') ?? [])
    const day = days.find(d => (d.dataset.date ?? '') >= today)
    if (day) day.scrollIntoView({ block: 'start' })
  }, 50)
  // Nach einem Wechsel des Zeitraums einmal scrollen, sobald die Termine da sind - nicht bei jedem Nachladen
  const wantScroll = useRef(true)
  const goToday = () => { fc.current?.getApi().today(); scrollToToday() }

  const changeView = (v: string) => {
    setView(v)
    localStorage.setItem('dmoffice.calendar.view', v)
    fc.current?.getApi().changeView(v)
  }

  const newEvent = (start?: Date, end?: Date, allDay = false) => {
    const now = new Date()
    const st = start ?? new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours() + 1)
    setDraft({
      calendar: calendars.find(c => !hidden.includes(c.id))?.id ?? calendars[0]?.id,
      title: '', start: st, end: end ?? new Date(st.getTime() + 3600000), allDay, rRule: '', reminder: prefs.defaultReminder === undefined ? 15 : prefs.defaultReminder, busy: true,
    })
  }

  const onSelect = (a: DateSelectArg) => {
    newEvent(a.start, a.end, a.allDay)
    fc.current?.getApi().unselect()
  }

  const onClick = (a: EventClickArg) => {
    const task: Task | undefined = a.event.extendedProps.task
    if (task) {
      // Kaestchen hakt ab, der Rest oeffnet die Aufgaben
      if ((a.jsEvent.target as HTMLElement).closest('.dm-task-check')) toggleTask(task)
      else location.hash = '#/tasks'
      return
    }
    const e: CalEvent = a.event.extendedProps.ev
    setDraft({
      calendar: e.calendar, uri: e.uri, etag: e.etag, title: e.title, location: e.location, description: e.description,
      start: a.event.start!, end: a.event.end ?? a.event.start!, allDay: e.allDay, rRule: e.rRule ?? '',
      reminder: e.reminder ?? null, busy: e.busy, recurring: e.recurring, instance: e.recurrenceId,
    })
  }

  // Ziehen und Groesse aendern
  const onMove = async (a: EventDropArg | EventResizeDoneArg) => {
    const e: CalEvent = a.event.extendedProps.ev
    let scope: 'series' | 'instance' = 'series'
    if (e.recurring) {
      const r = await askScope(t('Termin verschieben'))
      if (!r) { a.revert(); return }
      scope = r
    }
    const allDay = a.event.allDay
    const iso = (d: Date) => (allDay ? d.toLocaleDateString('sv-SE') : d.toISOString())
    try {
      await api.post('/api/pim/events', {
        calendar: e.calendar, uri: e.uri, etag: e.etag, title: e.title, location: e.location, description: e.description,
        start: iso(a.event.start!), end: iso(a.event.end ?? new Date(a.event.start!.getTime() + (allDay ? 86400000 : 3600000))),
        allDay, reminder: e.reminder, busy: e.busy, categories: e.categories, scope, instance: e.recurrenceId,
      })
      refetch()
    } catch (err) { fail(err); a.revert() }
  }

  const onImport = async (file: File) => {
    try {
      const r = await importInto(importTarget.current, file)
      notify(r.added === 1 ? t('1 Termin übernommen') : t('{n} Termine übernommen', { n: r.added }),
        r.failed ? (r.failed === 1 ? t('1 Eintrag ließ sich nicht lesen') : t('{n} ließen sich nicht lesen', { n: r.failed })) : undefined)
      loadCalendars()
    } catch (e) { fail(e) }
  }

  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      <RibbonHeader area="calendar"
        start={<>
          <RibbonButton primary icon={<CalendarAdd20Regular />} label={t('Neuer Termin')} onClick={() => newEvent()} />
          <RibbonDivider />
          <RibbonButton icon={<CalendarToday20Regular />} label={t('Heute')} onClick={goToday} />
          <RibbonDivider />
          {views.map(v => <RibbonButton key={v.key} icon={v.icon} label={v.label} pressed={view === v.key} onClick={() => changeView(v.key)} />)}
          <RibbonDivider />
          <RibbonButton icon={<CalendarLtr20Regular />} label={t('Kalender hinzufügen')} onClick={() => setEditColl('new')} />
          <RibbonButton icon={<Rss20Regular />} label={t('Kalender abonnieren')} onClick={() => setSubscribe('new')} />
        </>}
        view={<>
          {views.map(v => <RibbonButton key={v.key} icon={v.icon} label={v.label} pressed={view === v.key} onClick={() => changeView(v.key)} />)}
          <RibbonDivider />
          <Menu>
            <MenuTrigger disableButtonEnhancement><RibbonButton icon={<Clock20Regular />} label={t('Zeitraster')} /></MenuTrigger>
            <MenuPopover>
              <MenuList checkedValues={{ slot: [String(prefs.slotMinutes ?? 30)] }} onCheckedValueChange={(_, d) => update({ slotMinutes: Number(d.checkedItems[0]) })}>
                {[5, 10, 15, 30, 60].map(m => <MenuItemRadio key={m} name="slot" value={String(m)}>{t('{n} Minuten', { n: m })}</MenuItemRadio>)}
              </MenuList>
            </MenuPopover>
          </Menu>
          <RibbonButton icon={<CalendarWeekNumbers20Regular />} label={t('Kalender­wochen')} pressed={prefs.weekNumbers ?? true}
            onClick={() => update({ weekNumbers: !(prefs.weekNumbers ?? true) })} />
          <RibbonButton icon={<TaskListLtr20Regular />} label={t('Fällige Aufgaben')} pressed={showTasks} onClick={() => update({ calendarTasks: !showTasks })} />
          <RibbonDivider />
          <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />
          <RibbonDivider />
          <RibbonButton icon={<Settings20Regular />} label={t('Ansicht­einstellungen')} onClick={() => { location.hash = '#/settings/calview' }} />
        </>} />
      <div className={s.root}>
      <aside className={s.side} style={sidePane ? undefined : { display: 'none' }}>
        <MiniMonth month={miniMonth} setMonth={setMiniMonth} onPick={d => fc.current?.getApi().gotoDate(d)} />
        <div className={s.calRow}>
          <span className={s.section} style={{ flex: 1 }}>{t('Meine Kalender')}</span>
          <Tooltip content={t('Kalender hinzufügen')} relationship="label">
            <Button appearance="subtle" size="small" icon={<CalendarAdd20Regular />} onClick={() => setEditColl('new')} />
          </Tooltip>
          <Tooltip content={t('Kalender abonnieren')} relationship="label">
            <Button appearance="subtle" size="small" icon={<Rss20Regular />} onClick={() => setSubscribe('new')} />
          </Tooltip>
        </div>
        {calendars.map(c => (
          <div key={c.id} className={s.calRow}>
            <Checkbox checked={!hidden.includes(c.id)} onChange={(_, d) => setHidden(h => d.checked ? h.filter(x => x !== c.id) : [...h, c.id])}
              label={<span style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                <span className={s.dot} style={{ background: c.color || '#0F6CBD' }} />{c.name}
                {c.components.includes('VTODO') && <Tooltip content={t('Enthält auch Aufgaben')} relationship="label"><TaskListSquareLtr16Regular style={{ opacity: .6 }} /></Tooltip>}
                {c.subscription && <Tooltip relationship="description" content={c.subscription.lastError
                  ? t('Abo – letzter Abruf fehlgeschlagen: {error}', { error: c.subscription.lastError })
                  : t('Abo – zuletzt geholt: {when}', { when: c.subscription.lastFetch ? new Date(c.subscription.lastFetch).toLocaleString(locale) : '–' })}>
                  <Rss16Regular style={{ opacity: .7, color: c.subscription.lastError ? 'var(--colorPaletteRedForeground1)' : undefined }} />
                </Tooltip>}
              </span>} />
            <span style={{ flex: 1 }} />
            <Menu>
              <MenuTrigger disableButtonEnhancement><Button appearance="transparent" size="small" icon={<MoreVertical16Regular />} aria-label={t('Mehr')} /></MenuTrigger>
              <MenuPopover>
                <MenuList>
                  {c.subscription ? <>
                    <MenuItem onClick={() => refreshSub(c)}>{t('Jetzt aktualisieren')}</MenuItem>
                    <MenuItem onClick={() => setSubscribe(c)}>{t('Abo bearbeiten …')}</MenuItem>
                    <MenuItem onClick={() => endSub(c)}>{t('Abo beenden')}</MenuItem>
                  </> : <>
                  <MenuItem onClick={() => setEditColl(c)}>{t('Bearbeiten …')}</MenuItem>
                  <MenuItem onClick={() => { importTarget.current = c.id; importInput.current?.click() }}>{t('Importieren (.ics) …')}</MenuItem>
                  </>}
                  <MenuItem onClick={() => window.open(`/api/pim/export/${c.id}`)}>{t('Exportieren (.ics)')}</MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          </div>
        ))}
        {taskLists.length > 0 && (
          <>
            <div className={s.calRow} style={{ marginTop: 8 }}>
              <span className={s.section} style={{ flex: 1 }}>{t('Aufgaben')}</span>
            </div>
            <Switch checked={showTasks} label={t('Fällige Aufgaben im Kalender')} onChange={(_, d) => update({ calendarTasks: d.checked })} />
            {showTasks && taskLists.filter(c => !calendars.some(x => x.id === c.id)).map(c => (
              <div key={c.id} className={s.calRow}>
                <Checkbox checked={!hidden.includes(c.id)} onChange={(_, d) => setHidden(h => d.checked ? h.filter(x => x !== c.id) : [...h, c.id])}
                  label={<span style={{ display: 'flex', alignItems: 'center', gap: 6 }}><TaskListSquareLtr16Regular style={{ color: c.color || '#0F6CBD' }} />{c.name}</span>} />
              </div>
            ))}
          </>
        )}
        <input ref={importInput} type="file" accept=".ics,text/calendar" hidden onChange={e => { const f = e.target.files?.[0]; if (f) onImport(f); e.target.value = '' }} />
      </aside>
      <div className={s.main}>
        <Toolbar className={s.bar} size="small">
          <ToolbarButton icon={<CalendarToday20Regular />} onClick={goToday}>{t('Heute')}</ToolbarButton>
          <ToolbarButton icon={<ChevronLeft20Regular />} onClick={() => fc.current?.getApi().prev()} aria-label={t('Zurück')} />
          <ToolbarButton icon={<ChevronRight20Regular />} onClick={() => fc.current?.getApi().next()} aria-label={t('Weiter')} />
          <Text className={s.title}>{title}</Text>
        </Toolbar>
        <div className={s.cal} ref={calBox}>
          <FullCalendar
            ref={fc}
            plugins={[dayGridPlugin, timeGridPlugin, listPlugin, interactionPlugin]}
            locale={fcLocale}
            initialView={view}
            headerToolbar={false}
            height="100%"
            firstDay={prefs.firstDay ?? 1}
            weekNumbers={prefs.weekNumbers ?? true}
            eventTimeFormat={prefs.timeFormat === '12' ? { hour: 'numeric', minute: '2-digit', meridiem: 'short' } : { hour: '2-digit', minute: '2-digit', hour12: false }}
            slotLabelFormat={prefs.timeFormat === '12' ? { hour: 'numeric', meridiem: 'short' } : { hour: '2-digit', minute: '2-digit', hour12: false }}
            nowIndicator
            selectable
            selectMirror
            editable
            dayMaxEvents
            scrollTime="07:00:00"
            slotDuration={`00:${String(prefs.slotMinutes ?? 30).padStart(2, '0')}:00`}
            businessHours={{ daysOfWeek: prefs.workDays ?? [1, 2, 3, 4, 5], startTime: prefs.workStart ?? '08:00', endTime: prefs.workEnd ?? '18:00' }}
            scrollTimeReset={false}
            views={{ dayGridMonth: { businessHours: false }, timeGridWorkWeek: { type: 'timeGrid', duration: { weeks: 1 }, hiddenDays: [0, 1, 2, 3, 4, 5, 6].filter(d => !(prefs.workDays ?? [1, 2, 3, 4, 5]).includes(d)), buttonText: t('Arbeitswoche') } }}
            // Monatsansicht: freie Tage je Zelle einfaerben - die Flaeche von FullCalendar laege ueber Sa+So und verdeckt den Trennstrich
            dayCellClassNames={arg => arg.view.type.startsWith('dayGrid') && !(prefs.workDays ?? [1, 2, 3, 4, 5]).includes(arg.date.getDay()) ? ['dm-offday'] : []}
            events={fetchEvents as any}
            eventContent={arg => {
              const task: Task | undefined = arg.event.extendedProps.task
              if (!task) return true // normale Termine: Standarddarstellung
              const color: string = arg.event.extendedProps.color
              // Monatsansicht zeigt Termine mit Uhrzeit als Punkt-Zeile ohne Rahmen - dort Farbstreifen selbst zeichnen
              const dot = arg.view.type.startsWith('dayGrid') && !arg.event.allDay
              const time = !arg.event.allDay && !arg.view.type.startsWith('list') && arg.event.start
                ? arg.event.start.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit', hour12: prefs.timeFormat === '12' }) : ''
              return (
                <span title={t('Aufgabe')} style={{
                  display: 'flex', alignItems: 'center', gap: 4, overflow: 'hidden', whiteSpace: 'nowrap', padding: '0 2px', minWidth: 0, width: '100%',
                  ...(dot ? { borderLeft: `3px solid ${color}`, paddingLeft: 4, borderRadius: 2 } : {}),
                }}>
                  <span className="dm-task-check" role="checkbox" aria-checked={task.completed} style={{ color }}>{task.completed ? '☑' : '☐'}</span>
                  {time && <span style={{ opacity: .8, flexShrink: 0 }}>{time}</span>}
                  <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 }}>{arg.event.title}</span>
                </span>
              )
            }}
            select={onSelect}
            eventClick={onClick}
            eventDrop={onMove}
            eventResize={onMove}
            datesSet={a => { setTitle(a.view.title); wantScroll.current = true }}
            eventsSet={() => { if (wantScroll.current) { wantScroll.current = false; scrollToToday() } }}
          />
        </div>
      </div>
      {draft && <EventDialog draft={draft} calendars={calendars} onClose={changed => { setDraft(null); if (changed) refetch() }} />}
      {subscribe && <SubscribeDialog collection={subscribe === 'new' ? null : subscribe} onClose={c => { setSubscribe(null); if (c) loadCalendars() }} />}
      {editColl && <CollectionDialog kind="calendars" collection={editColl === 'new' ? null : editColl} onClose={changed => { setEditColl(null); if (changed) loadCalendars() }} />}
      </div>
    </div>
  )
}

// Kopfzeile des Monatsblatts ab Montag; Englisch einbuchstabig wie in Outlook (1.1.2024 ist ein Montag)
const miniHeads = lang === 'de' ? ['Mo', 'Di', 'Mi', 'Do', 'Fr', 'Sa', 'So']
  : Array.from({ length: 7 }, (_, i) => new Date(2024, 0, 1 + i).toLocaleDateString(locale, { weekday: 'narrow' }))

function MiniMonth({ month, setMonth, onPick }: { month: Date; setMonth: (d: Date) => void; onPick: (d: Date) => void }) {
  const s = useStyles()
  const first = new Date(month)
  const offset = (first.getDay() + 6) % 7
  const start = new Date(first.getFullYear(), first.getMonth(), 1 - offset)
  const days = Array.from({ length: 42 }, (_, i) => new Date(start.getFullYear(), start.getMonth(), start.getDate() + i))
  const today = new Date().toDateString()
  return (
    <div>
      <div style={{ display: 'flex', alignItems: 'center' }}>
        <Text weight="semibold" style={{ flex: 1 }}>{month.toLocaleDateString(locale, { month: 'long', year: 'numeric' })}</Text>
        <Button appearance="subtle" size="small" aria-label={t('Vorheriger Monat')} icon={<ChevronLeft20Regular />} onClick={() => setMonth(new Date(month.getFullYear(), month.getMonth() - 1, 1))} />
        <Button appearance="subtle" size="small" aria-label={t('Nächster Monat')} icon={<ChevronRight20Regular />} onClick={() => setMonth(new Date(month.getFullYear(), month.getMonth() + 1, 1))} />
      </div>
      <div className={s.mini}>
        {miniHeads.map((d, i) => <div key={i} className={s.miniHead}>{d}</div>)}
        {days.map(d => (
          <div key={d.toISOString()} onClick={() => onPick(d)}
            className={`${s.miniDay} ${d.toDateString() === today ? s.miniToday : ''} ${d.getMonth() !== month.getMonth() ? s.miniOther : ''}`}>
            {d.getDate()}
          </div>
        ))}
      </div>
    </div>
  )
}
