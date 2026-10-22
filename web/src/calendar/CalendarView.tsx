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
  Button, Menu, MenuDivider, MenuItem, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, Text, Toolbar, ToolbarButton, Tooltip,
  makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import {
  ChevronLeft20Regular, ChevronRight20Regular, CalendarToday20Regular, CalendarAdd20Regular, TaskListSquareLtr16Regular, Rss20Regular, Rss16Regular,
  CalendarDay20Regular, CalendarWorkWeek20Regular, CalendarWeekNumbers20Regular, CalendarMonth20Regular, CalendarAgenda20Regular, CalendarLtr20Regular,
  PanelLeft20Regular, Settings20Regular, TaskListLtr20Regular, Clock20Regular, CalendarWeekStart20Regular,
  MoreHorizontal16Regular, Open20Regular, Edit20Regular, Rename20Regular, ArrowImport20Regular, ArrowExport20Regular, Delete20Regular,
  ArrowSync20Regular, Eye20Regular, CheckmarkCircle20Regular, CalendarEmpty20Regular, People16Regular, People20Regular, PersonDelete20Regular, ChevronDown16Regular, ChevronRight16Regular,
} from '@fluentui/react-icons'
import { ColorMenu, ContextMenu, TargetMenu, askText, isTyping, useContextMenu } from '../shell/context'
import { useNavStyles } from '../shell/nav'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { SidePane, useIsMobile } from '../shell/mobile'
import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { api, CalEvent, Collection, q, Task } from '../api'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import { EventDialog, EventDraft, askScope } from './EventDialog'
import { CollectionDialog, importInto, removeCollection } from './CollectionDialog'
import { SubscribeDialog } from './SubscribeDialog'
import { ShareDialog } from './ShareDialog'
import { CALENDAR_KINDS, inNormalViews, kindOf } from './kinds'
import { lang, locale, t } from '../i18n'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0, position: 'relative' },
  side: { width: '240px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  bar: { borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, padding: '4px 8px', gap: '4px' },
  title: { fontSize: '18px', fontWeight: 600, marginLeft: '8px', marginRight: 'auto' },
  cal: { flex: 1, minHeight: 0, padding: '0 8px 8px' },
  mini: { display: 'grid', gridTemplateColumns: 'repeat(7, 1fr)', gap: '1px', fontSize: '12px', textAlign: 'center' },
  miniDay: { padding: '4px 0', borderRadius: '50%', cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  miniHead: { color: tokens.colorNeutralForeground3, padding: '2px 0' },
  miniToday: { backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand, ':hover': { backgroundColor: tokens.colorBrandBackgroundHover } },
  miniOther: { color: tokens.colorNeutralForeground4 },
})

const views = [
  { key: 'timeGridDay', label: t('Tag'), icon: <CalendarDay20Regular /> },
  { key: 'timeGridWorkWeek', label: t('Arbeits­woche'), icon: <CalendarWorkWeek20Regular /> },
  { key: 'timeGridWeek', label: t('Woche'), icon: <CalendarWeekStart20Regular /> },
  { key: 'dayGridMonth', label: t('Monat'), icon: <CalendarMonth20Regular /> },
  { key: 'listMonth', label: t('Liste'), icon: <CalendarAgenda20Regular /> },
]

const HIDDEN = 'dmoffice.calendars.hidden'
const COLLAPSED = 'dmoffice.calendars.collapsed'
const PLAN = 'dmoffice.calendar.plan'
// Sprache von FullCalendar folgt der Oberflaeche; Wochenbeginn usw. setzen die Einstellungen darueber
const fcLocale = lang === 'en' ? enGbLocale : deLocale

export default function CalendarView() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const fc = useRef<FullCalendar>(null)
  const { prefs, update } = usePrefs()
  const [sidePane, toggleSide, closeSide] = useSidePane('calendar')
  // Handy: eigene Ansicht merken (Standard Tag) - die Arbeitswoche passt nicht auf die schmale Breite
  const mobile = useIsMobile()
  const viewKey = mobile ? 'dmoffice.calendar.view.mobile' : 'dmoffice.calendar.view'
  const [calendars, setCalendars] = useState<Collection[]>([])
  const [hidden, setHidden] = useState<number[]>(() => { try { return JSON.parse(localStorage.getItem(HIDDEN) || '[]') } catch { return [] } })
  const [collapsed, setCollapsed] = useState<string[]>(() => { try { return JSON.parse(localStorage.getItem(COLLAPSED) || '[]') } catch { return [] } })
  const [view, setView] = useState(() => localStorage.getItem(viewKey) || (mobile ? 'timeGridDay' : 'timeGridWorkWeek'))
  const [title, setTitle] = useState('')
  const [draft, setDraft] = useState<EventDraft | null>(null)
  const [editColl, setEditColl] = useState<Collection | 'new' | null>(null)
  const [newKind, setNewKind] = useState<string | undefined>()
  const [subscribe, setSubscribe] = useState<Collection | 'new' | null>(null)
  // Planungsansicht einer Kalenderart (z. B. "meal" = Essensplan) statt der normalen Kalenderansicht; null: normale Ansicht
  const [plan, setPlan] = useState<string | null>(() => localStorage.getItem(PLAN) || (localStorage.getItem('dmoffice.calendar.meal') === '1' ? 'meal' : null))
  const togglePlan = (key: string) => setPlan(p => { const n = p === key ? null : key; try { localStorage.setItem(PLAN, n ?? ''); localStorage.removeItem('dmoffice.calendar.meal') } catch { /* egal */ } return n })
  const planKind = CALENDAR_KINDS.find(k => k.key === plan && k.plan)
  const toggleGroup = (key: string) => setCollapsed(l => { const n = l.includes(key) ? l.filter(x => x !== key) : [...l, key]; try { localStorage.setItem(COLLAPSED, JSON.stringify(n)) } catch { /* egal */ } return n })
  const [shareColl, setShareColl] = useState<Collection | null>(null)
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
  const isOff = (c: Collection) => hidden.includes(c.id)

  const colorOf = (id: number) => [...calendars, ...taskLists].find(c => c.id === id)?.color || '#0F6CBD'

  const fetchEvents = useCallback(async (info: { startStr: string; endStr: string; start: Date; end: Date }) => {
    const visible = calendars.filter(c => inNormalViews(c) && !isOff(c)).map(c => c.id)
    const list = visible.length
      ? await api.get<CalEvent[]>('/api/pim/events?' + q({ from: info.start.toISOString(), to: info.end.toISOString(), calendars: visible.join(',') }))
      : []
    const events = list.map<FcEvent>(e => ({
      id: e.id, title: e.title, start: e.start, end: e.end, allDay: e.allDay,
      backgroundColor: colorOf(e.calendar), borderColor: colorOf(e.calendar), textColor: '#fff',
      editable: !calendars.find(c => c.id === e.calendar)?.subscription && e.canWrite !== false,
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
        backgroundColor: 'transparent', borderColor: color, textColor: 'inherit',
        editable: !taskLists.find(c => c.id === task.list)?.subscription && taskLists.find(c => c.id === task.list)?.canWrite !== false, durationEditable: false,
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
    localStorage.setItem(viewKey, v)
    fc.current?.getApi().changeView(v)
  }

  const newEvent = (start?: Date, end?: Date, allDay = false, calendar?: number) => {
    const now = new Date()
    const st = start ?? new Date(now.getFullYear(), now.getMonth(), now.getDate(), now.getHours() + 1)
    setDraft({
      calendar: calendar ?? calendars.find(c => inNormalViews(c) && !isOff(c) && !c.subscription && c.canWrite !== false)?.id ?? writable.find(inNormalViews)?.id ?? writable[0]?.id ?? calendars[0]?.id,
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
    openEvent(a.event.extendedProps.ev, a.event.start!, a.event.end ?? a.event.start!)
  }

  // Ziehen und Groesse aendern
  // Aufgabe auf einen anderen Tag (oder eine andere Zeit) ziehen: neue Faelligkeit, alles andere bleibt
  const onMoveTask = async (a: EventDropArg | EventResizeDoneArg) => {
    const task: Task = a.event.extendedProps.task
    const start = a.event.start!
    const allDay = a.event.allDay
    try {
      await api.post('/api/pim/tasks', {
        list: task.list, uri: task.uri, etag: task.etag, title: task.title, description: task.description,
        due: allDay ? start.toLocaleDateString('sv-SE') : start.toISOString(), dueAllDay: allDay,
        priority: task.priority, completed: task.completed, percent: task.percent, categories: task.categories,
      })
      refetch()
    } catch (err) { fail(err); a.revert() }
  }

  const onMove = async (a: EventDropArg | EventResizeDoneArg) => {
    if (a.event.extendedProps.task) return onMoveTask(a)
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


  // ---------------- Kontextmenues ----------------
  type Ctx = { kind: 'cal'; c: Collection } | { kind: 'list'; c: Collection } | { kind: 'side' }
    | { kind: 'event'; e: CalEvent; start: Date; end: Date } | { kind: 'task'; task: Task }
    | { kind: 'slot'; start: Date; allDay: boolean }
  const cm = useContextMenu<Ctx>()
  const writable = calendars.filter(c => !c.subscription && c.canWrite !== false)
  const nv = useNavStyles()
  const toggleHidden = (c: Collection) => setHidden(h => h.includes(c.id) ? h.filter(x => x !== c.id) : [...h, c.id])
  // Nur diesen anzeigen: die anderen derselben Gruppe aus, andere Gruppen bleiben wie sie sind
  const onlyThis = (c: Collection) => {
    const group = kindOf(c).key
    const mine = [...calendars, ...taskLists].filter(x => (calendars.includes(x) ? kindOf(x).key : 'normal') === group).map(x => x.id)
    setHidden(h => [...h.filter(x => !mine.includes(x)), ...mine.filter(x => x !== c.id)])
  }
  const setCollection = async (c: Collection, body: { name?: string; color?: string }) => {
    try { await api.put(`/api/pim/collections/${c.id}`, body); loadCalendars() } catch (e) { fail(e) }
  }
  const renameCollection = async (c: Collection) => {
    const name = await askText(t('Umbenennen'), c.name)
    if (name && name !== c.name) setCollection(c, { name })
  }
  const leaveShared = async (c: Collection) => {
    if (!confirm(t('„{name}“ nicht mehr anzeigen? {owner} kann den Kalender wieder mit dir teilen.', { name: c.name, owner: c.owner ?? '' }))) return
    try { await api.post(`/api/pim/shares/${c.id}/leave`); setHidden(h => h.filter(x => x !== c.id)); loadCalendars() } catch (e) { fail(e) }
  }
  const deleteCollection = async (c: Collection, tasks?: boolean) => {
    try { if (await removeCollection(c, 'calendars', tasks)) loadCalendars() } catch (e) { fail(e) }
  }
  const openEvent = (e: CalEvent, start: Date, end: Date) => setDraft({
    calendar: e.calendar, uri: e.uri, etag: e.etag, title: e.title, location: e.location, description: e.description,
    start, end, allDay: e.allDay, rRule: e.rRule ?? '',
    reminder: e.reminder ?? null, busy: e.busy, recurring: e.recurring, instance: e.recurrenceId,
    attendees: e.attendees ?? [], organizer: e.organizer ?? null,
  })
  /** Termin so speichern wie geladen, mit einer Aenderung (anderer Kalender, frei/beschaeftigt). */
  const resave = async (e: CalEvent, change: { moveTo?: number; busy?: boolean }) => {
    try {
      await api.post('/api/pim/events', {
        calendar: e.calendar, uri: e.uri, etag: e.etag, title: e.title, location: e.location, description: e.description,
        start: e.start, end: e.end, allDay: e.allDay, reminder: e.reminder, busy: change.busy ?? e.busy, categories: e.categories,
        // Serie: gilt fuer die ganze Serie - in einen anderen Kalender geht ohnehin das ganze Objekt
        scope: 'series', moveTo: change.moveTo,
      })
      refetch()
    } catch (err) { fail(err) }
  }
  /** Kopie in einen Kalender - bei Serien nur dieses Vorkommen als einzelner Termin. */
  const copyEvent = async (e: CalEvent, start: Date, end: Date, calendar: number) => {
    const iso = (d: Date) => (e.allDay ? d.toLocaleDateString('sv-SE') : d.toISOString())
    try {
      await api.post('/api/pim/events', {
        calendar, title: e.title, location: e.location, description: e.description, start: iso(start), end: iso(end), allDay: e.allDay,
        rRule: e.recurring ? '' : e.rRule ?? '', reminder: e.reminder, busy: e.busy, categories: e.categories,
      })
      refetch()
      notify(t('Kopiert'))
    } catch (err) { fail(err) }
  }
  const deleteEvent = async (e: CalEvent) => {
    let instance: string | undefined
    if (e.recurring) {
      const r = await askScope(t('Termin löschen'))
      if (!r) return
      if (r === 'instance') instance = e.recurrenceId
    } else if (!confirm(t('„{name}“ löschen?', { name: e.title || t('Termin') }))) return
    try {
      await api.del(`/api/pim/events/${e.calendar}/${encodeURIComponent(e.uri)}` + (instance ? `?instance=${encodeURIComponent(instance)}` : ''))
      refetch()
    } catch (err) { fail(err) }
  }
  // Rechtsklick auf einen Termin: FullCalendar zeichnet die Elemente selbst - Handler ueber eine Referenz, sonst veraltet
  const eventMenu = useRef<(ev: MouseEvent, fcEv: any) => void>(() => {})
  eventMenu.current = (ev, fcEv) => {
    const task: Task | undefined = fcEv.extendedProps.task
    if (task) { cm.open(ev, { kind: 'task', task }); return }
    const e: CalEvent = fcEv.extendedProps.ev
    cm.open(ev, { kind: 'event', e, start: fcEv.start, end: fcEv.end ?? fcEv.start })
  }
  /** Rechtsklick auf freie Flaeche im Kalender: welcher Tag, welche Uhrzeit? */
  const slotMenu = (ev: React.MouseEvent) => {
    if ((ev.target as HTMLElement).closest('.fc-event')) return
    const els = document.elementsFromPoint(ev.clientX, ev.clientY) as HTMLElement[]
    const day = els.find(x => x.matches('.fc-timegrid-col[data-date], .fc-daygrid-day[data-date], .fc-list-day[data-date]'))
    if (!day) return
    const [y, m, d] = day.dataset.date!.split('-').map(Number)
    const lane = day.matches('.fc-timegrid-col') ? els.find(x => x.matches('.fc-timegrid-slot-lane[data-time]')) : undefined
    const [hh, mm] = (lane?.dataset.time ?? '09:00').split(':').map(Number)
    cm.open(ev, { kind: 'slot', start: new Date(y, m - 1, d, lane ? hh : 9, lane ? mm : 0), allDay: !lane })
  }
  // Termin auf einen Kalender in der Seitenleiste gezogen: dorthin verschieben (mit Strg: kopieren)
  const dropOnCalendar = (a: { event: any; jsEvent: MouseEvent }) => {
    const row = (document.elementsFromPoint(a.jsEvent.clientX, a.jsEvent.clientY) as HTMLElement[]).find(x => x.dataset?.cal)
    const e: CalEvent | undefined = a.event.extendedProps.ev
    if (!row || !e) return
    const target = Number(row.dataset.cal)
    if (target === e.calendar || !writable.some(c => c.id === target)) return
    if (a.jsEvent.ctrlKey) copyEvent(e, a.event.start, a.event.end ?? a.event.start, target)
    else if (!calendars.find(c => c.id === e.calendar)?.subscription && e.canWrite !== false) resave(e, { moveTo: target })
  }
  const goDay = (d: Date) => { changeView('timeGridDay'); fc.current?.getApi().gotoDate(d) }

  // Tastatur wie in Outlook: Strg+N neuer Termin, Bild auf/ab blaettert, Strg+Alt+1..5 Ansicht
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (isTyping(e) || draft || editColl || subscribe || cm.ctx) return
      const cal = fc.current?.getApi()
      if (e.ctrlKey && !e.altKey && e.key.toLowerCase() === 'n') { e.preventDefault(); newEvent() }
      else if (e.key === 'PageDown') { e.preventDefault(); cal?.next() }
      else if (e.key === 'PageUp') { e.preventDefault(); cal?.prev() }
      else if (e.ctrlKey && e.altKey && /^[1-5]$/.test(e.key)) { e.preventDefault(); changeView(views[Number(e.key) - 1].key) }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  const ctxMenu = () => {
    const x = cm.ctx?.data
    if (!x) return null
    if (x.kind === 'side') return <>
      <MenuItem icon={<CalendarLtr20Regular />} onClick={() => setEditColl('new')}>{t('Kalender hinzufügen')}</MenuItem>
      <MenuItem icon={<Rss20Regular />} onClick={() => setSubscribe('new')}>{t('Kalender abonnieren')}</MenuItem>
    </>
    if (x.kind === 'slot') {
      const day = new Date(x.start.getFullYear(), x.start.getMonth(), x.start.getDate())
      const nextDay = new Date(day.getFullYear(), day.getMonth(), day.getDate() + 1)
      return <>
        {!x.allDay && <MenuItem icon={<CalendarAdd20Regular />} onClick={() => newEvent(x.start, new Date(x.start.getTime() + (prefs.slotMinutes ?? 30) * 60000))}>
          {t('Neuer Termin um {time}', { time: x.start.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit', hour12: prefs.timeFormat === '12' }) })}
        </MenuItem>}
        <MenuItem icon={<CalendarEmpty20Regular />} onClick={() => newEvent(day, nextDay, true)}>{t('Neuer ganztägiger Termin')}</MenuItem>
        <MenuDivider />
        <MenuItem icon={<CalendarDay20Regular />} disabled={view === 'timeGridDay'} onClick={() => goDay(day)}>{t('Tagesansicht')}</MenuItem>
        <MenuItem icon={<CalendarToday20Regular />} onClick={goToday}>{t('Heute')}</MenuItem>
      </>
    }
    if (x.kind === 'task') return <>
      <MenuItem icon={<Open20Regular />} onClick={() => { location.hash = `#/tasks/${x.task.list}` }}>{t('In Aufgaben öffnen')}</MenuItem>
      <MenuItem icon={<CheckmarkCircle20Regular />} onClick={() => toggleTask(x.task)}>{x.task.completed ? t('Als nicht erledigt markieren') : t('Als erledigt markieren')}</MenuItem>
    </>
    if (x.kind === 'event') {
      const { e } = x
      const ro = !!calendars.find(c => c.id === e.calendar)?.subscription || e.canWrite === false
      const targets = writable.filter(c => c.components.includes('VEVENT'))
      return <>
        <MenuItem icon={<Open20Regular />} onClick={() => openEvent(e, x.start, x.end)}>{t('Öffnen')}</MenuItem>
        {!ro && <>
          <MenuDivider />
          <Menu>
            <MenuTrigger disableButtonEnhancement><MenuItem icon={<Clock20Regular />}>{t('Anzeigen als')}</MenuItem></MenuTrigger>
            <MenuPopover>
              <MenuList checkedValues={{ busy: [e.busy ? 'busy' : 'free'] }} onCheckedValueChange={(_, d) => { cm.close(); resave(e, { busy: d.checkedItems[0] === 'busy' }) }}>
                <MenuItemRadio name="busy" value="busy">{t('Beschäftigt')}</MenuItemRadio>
                <MenuItemRadio name="busy" value="free">{t('Frei')}</MenuItemRadio>
              </MenuList>
            </MenuPopover>
          </Menu>
        </>}
        <MenuDivider />
        {!ro && <TargetMenu kind="move" targets={targets.filter(c => c.id !== e.calendar).map(c => ({ id: c.id, label: c.name }))}
          onPick={id => resave(e, { moveTo: Number(id) })} />}
        <TargetMenu kind="copy" targets={targets.map(c => ({ id: c.id, label: c.name }))} onPick={id => copyEvent(e, x.start, x.end, Number(id))} />
        <MenuItem icon={<ArrowExport20Regular />} onClick={() => window.open(`/api/pim/events/${e.calendar}/${encodeURIComponent(e.uri)}/ics`)}>{t('Exportieren (.ics)')}</MenuItem>
        {!ro && <>
          <MenuDivider />
          <MenuItem icon={<Delete20Regular />} onClick={() => deleteEvent(e)}>{t('Löschen')}</MenuItem>
        </>}
      </>
    }
    const c = x.c
    if (x.kind === 'list') return <>
      <MenuItem icon={<Open20Regular />} onClick={() => { location.hash = `#/tasks/${c.id}` }}>{t('In Aufgaben öffnen')}</MenuItem>
      <MenuItem icon={<Eye20Regular />} onClick={() => onlyThis(c)}>{t('Nur diese anzeigen')}</MenuItem>
      <MenuDivider />
      <MenuItem icon={<Rename20Regular />} onClick={() => renameCollection(c)}>{t('Umbenennen')}</MenuItem>
      <ColorMenu current={c.color} onPick={color => { cm.close(); if (color) setCollection(c, { color }) }} />
      <MenuDivider />
      <MenuItem icon={<Delete20Regular />} onClick={() => deleteCollection(c, true)}>{t('Löschen')}</MenuItem>
    </>
    return <>
      <MenuItem icon={<Eye20Regular />} onClick={() => onlyThis(c)}>{t('Nur diesen anzeigen')}</MenuItem>
      {!c.subscription && c.canWrite !== false && c.components.includes('VEVENT') && <MenuItem icon={<CalendarAdd20Regular />} onClick={() => { setHidden(h => h.filter(i => i !== c.id)); newEvent(undefined, undefined, false, c.id) }}>{t('Neuer Termin')}</MenuItem>}
      <MenuDivider />
      {c.owner ? null : c.subscription ? <>
        <MenuItem icon={<ArrowSync20Regular />} onClick={() => refreshSub(c)}>{t('Jetzt aktualisieren')}</MenuItem>
        <MenuItem icon={<Edit20Regular />} onClick={() => setSubscribe(c)}>{t('Abo bearbeiten …')}</MenuItem>
      </> : <>
        <MenuItem icon={<Edit20Regular />} onClick={() => setEditColl(c)}>{t('Bearbeiten …')}</MenuItem>
        <MenuItem icon={<Rename20Regular />} onClick={() => renameCollection(c)}>{t('Umbenennen')}</MenuItem>
        <MenuItem icon={<People20Regular />} onClick={() => setShareColl(c)}>{t('Teilen …')}</MenuItem>
      </>}
      {/* Name und Farbe eines geteilten Kalenders bestimmt sein Besitzer */}
      {!c.owner && <ColorMenu current={c.color} onPick={color => { cm.close(); if (color) setCollection(c, { color }) }} />}
      <MenuDivider />
      {!c.subscription && c.canWrite !== false && <MenuItem icon={<ArrowImport20Regular />} onClick={() => { importTarget.current = c.id; importInput.current?.click() }}>{t('Importieren (.ics) …')}</MenuItem>}
      <MenuItem icon={<ArrowExport20Regular />} onClick={() => window.open(`/api/pim/export/${c.id}`)}>{t('Exportieren (.ics)')}</MenuItem>
      <MenuDivider />
      {c.owner
        ? <MenuItem icon={<PersonDelete20Regular />} onClick={() => leaveShared(c)}>{t('Nicht mehr anzeigen')}</MenuItem>
        : c.subscription
          ? <MenuItem icon={<Delete20Regular />} onClick={() => endSub(c)}>{t('Abo beenden')}</MenuItem>
          : <MenuItem icon={<Delete20Regular />} onClick={() => deleteCollection(c)}>{t('Löschen')}</MenuItem>}
    </>
  }

  /** Eine Zeile in der Seitenleiste: farbiges Kaestchen zum Ein-/Ausblenden, Name, Menue. */
  const toggleRow = (c: Collection, _kind: 'cal' | 'list') => toggleHidden(c)
  const calRow = (c: Collection, kind: 'cal' | 'list') => {
    const on = !hidden.includes(c.id)
    const color = c.color || '#0F6CBD'
    const data: Ctx = kind === 'cal' ? { kind: 'cal', c } : { kind: 'list', c }
    return (
      <div key={kind + c.id} className={nv.row} data-cal={kind === 'cal' && !c.subscription && c.canWrite !== false ? c.id : undefined}
        role="checkbox" aria-checked={on} tabIndex={0} onClick={() => toggleRow(c, kind)} onContextMenu={e => cm.open(e, data)}
        onKeyDown={e => {
          if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); toggleRow(c, kind) }
          else if (e.key === 'F2' && !c.subscription && !c.owner) { e.preventDefault(); renameCollection(c) }
          else if (e.key === 'Delete' && !c.subscription && !c.owner) { e.preventDefault(); deleteCollection(c, kind === 'list') }
          else if (e.key === 'ContextMenu' || (e.shiftKey && e.key === 'F10')) { const r = e.currentTarget.getBoundingClientRect(); cm.open({ clientX: r.left + 24, clientY: r.bottom, preventDefault: () => e.preventDefault(), stopPropagation: () => e.stopPropagation() }, data) }
        }}
        title={c.subscription ? (c.subscription.lastError
          ? t('Abo – letzter Abruf fehlgeschlagen: {error}', { error: c.subscription.lastError })
          : t('Abo – zuletzt geholt: {when}', { when: c.subscription.lastFetch ? new Date(c.subscription.lastFetch).toLocaleString(locale) : '–' }))
          : c.owner ? t('{name} (von {owner})', { name: c.name, owner: c.owner }) + (c.canWrite === false ? ' · ' + t('nur lesbar') : '')
          : c.sharedWith?.length ? t('Geteilt mit {names}', { names: c.sharedWith.map(x => x.name).join(', ') }) : c.name}>
        <span className={nv.check} style={{ borderColor: color, background: on ? color : 'transparent' }}>{on && '✓'}</span>
        {kind === 'list' && <TaskListSquareLtr16Regular style={{ color }} />}
        <span className={nv.name}>{c.owner ? t('{name} (von {owner})', { name: c.name, owner: c.owner }) : c.name}</span>
        {(c.owner || !!c.sharedWith?.length) && <People16Regular style={{ opacity: .7 }} />}
        {kind === 'cal' && c.components.includes('VTODO') && <TaskListSquareLtr16Regular style={{ opacity: .6 }} aria-label={t('Enthält auch Aufgaben')} />}
        {c.subscription && <Rss16Regular style={{ opacity: .7, color: c.subscription.lastError ? 'var(--colorPaletteRedForeground1)' : undefined }} />}
        <Button className="dm-more" appearance="transparent" size="small" icon={<MoreHorizontal16Regular />} aria-label={t('Mehr')} onClick={e => { e.stopPropagation(); cm.open(e, data) }} />
      </div>
    )
  }
  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      <RibbonHeader area="calendar" onMenu={toggleSide}
        start={<>
          <RibbonButton primary icon={<CalendarAdd20Regular />} label={t('Neuer Termin')} onClick={() => newEvent()} />
          <RibbonDivider />
          <RibbonButton icon={<CalendarToday20Regular />} label={t('Heute')} onClick={goToday} />
          <RibbonDivider />
          {views.map(v => <RibbonButton key={v.key} icon={v.icon} label={v.label} pressed={view === v.key} onClick={() => changeView(v.key)} />)}
          <RibbonDivider />
          <RibbonButton icon={<CalendarLtr20Regular />} label={t('Kalender hinzufügen')} onClick={() => setEditColl('new')} />
          <RibbonButton icon={<Rss20Regular />} label={t('Kalender abonnieren')} onClick={() => setSubscribe('new')} />
          <RibbonDivider />
          {CALENDAR_KINDS.flatMap(k => k.plan ? [{ key: k.key, ...k.plan }] : []).map(({ key, icon: Icon, label }) => <RibbonButton key={key} icon={<Icon />} label={label()} pressed={plan === key} onClick={() => togglePlan(key)} />)}
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
      <SidePane as="aside" className={mergeClasses(s.side, nv.pane)} open={sidePane} onClose={closeSide}>
        <div onContextMenu={e => cm.open(e, { kind: 'side' })} style={{ display: 'flex', flexDirection: 'column', flex: 1 }}>
        <div style={{ padding: '0 4px 4px' }}><MiniMonth month={miniMonth} setMonth={setMiniMonth} onPick={d => fc.current?.getApi().gotoDate(d)} /></div>
        {CALENDAR_KINDS.map(k => {
          const own = calendars.filter(c => kindOf(c).key === k.key)
          if (k.key !== 'normal' && own.length === 0) return null
          const closed = collapsed.includes(k.key)
          return (
            <Fragment key={k.key}>
              <div className={nv.section}>
                <button type="button" style={{ flex: 1, display: 'flex', alignItems: 'center', gap: 4, padding: 0, background: 'none', border: 'none', color: 'inherit', font: 'inherit', textTransform: 'inherit', letterSpacing: 'inherit', cursor: 'pointer', minWidth: 0 }}
                  aria-expanded={!closed} onClick={() => toggleGroup(k.key)}>
                  {closed ? <ChevronRight16Regular /> : <ChevronDown16Regular />}
                  <k.groupIcon />
                  <span style={{ flex: 1, textAlign: 'left' }}>{k.group()}</span>
                </button>
                {k.key === 'normal' && <>
                  <Tooltip content={t('Kalender hinzufügen')} relationship="label">
                    <Button appearance="subtle" size="small" icon={<CalendarAdd20Regular />} onClick={() => setEditColl('new')} />
                  </Tooltip>
                  <Tooltip content={t('Kalender abonnieren')} relationship="label">
                    <Button appearance="subtle" size="small" icon={<Rss20Regular />} onClick={() => setSubscribe('new')} />
                  </Tooltip>
                </>}
              </div>
              {!closed && own.map(c => calRow(c, 'cal'))}
            </Fragment>
          )
        })}
        {taskLists.length > 0 && (
          <>
            <div className={nv.section}><span style={{ flex: 1 }}>{t('Aufgaben')}</span></div>
            {/* Schalter als Zeile wie die Kalender - ein Fluent-Switch waere doppelt so hoch */}
            <div className={nv.row} role="checkbox" aria-checked={showTasks} tabIndex={0} onClick={() => update({ calendarTasks: !showTasks })}
              onKeyDown={e => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); update({ calendarTasks: !showTasks }) } }}>
              <span className={nv.check} style={{ borderColor: tokens.colorNeutralForeground3, background: showTasks ? tokens.colorNeutralForeground3 : 'transparent' }}>{showTasks && '✓'}</span>
              <span className={nv.name}>{t('Fällige Aufgaben im Kalender')}</span>
            </div>
            {showTasks && taskLists.filter(c => !calendars.some(x => x.id === c.id)).map(c => calRow(c, 'list'))}
          </>
        )}
        <div style={{ flex: 1, minHeight: 24 }} />
        </div>
        <input ref={importInput} type="file" accept=".ics,text/calendar" hidden onChange={e => { const f = e.target.files?.[0]; if (f) onImport(f); e.target.value = '' }} />
      </SidePane>
      <div className={s.main}>
        <Toolbar className={s.bar} size="small" style={planKind ? { display: 'none' } : undefined}>
          <ToolbarButton icon={<CalendarToday20Regular />} onClick={goToday}>{t('Heute')}</ToolbarButton>
          <ToolbarButton icon={<ChevronLeft20Regular />} onClick={() => fc.current?.getApi().prev()} aria-label={t('Zurück')} />
          <ToolbarButton icon={<ChevronRight20Regular />} onClick={() => fc.current?.getApi().next()} aria-label={t('Weiter')} />
          <Text className={s.title} style={mobile ? { fontSize: 15, marginLeft: 4 } : undefined}>{title}</Text>
        </Toolbar>
        {planKind?.plan && <planKind.plan.view calendars={calendars.filter(c => c.calendarKind === planKind.key && !c.subscription && !isOff(c))}
          total={calendars.filter(c => c.calendarKind === planKind.key && !c.subscription).length} onCreate={() => { setNewKind(planKind.key); setEditColl('new') }} />}
        <div className={s.cal} ref={calBox} onContextMenu={slotMenu} style={planKind ? { display: 'none' } : undefined}>
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
            eventDidMount={arg => arg.el.addEventListener('contextmenu', ev => eventMenu.current(ev, arg.event))}
            eventDragStop={dropOnCalendar}
            select={onSelect}
            eventClick={onClick}
            eventDrop={onMove}
            eventResize={onMove}
            datesSet={a => { setTitle(a.view.title); wantScroll.current = true }}
            eventsSet={() => { if (wantScroll.current) { wantScroll.current = false; scrollToToday() } }}
          />
        </div>
      </div>
      <ContextMenu at={cm.ctx?.at} onClose={cm.close}>{ctxMenu()}</ContextMenu>
      {draft && <EventDialog draft={draft} calendars={calendars} onClose={changed => { setDraft(null); if (changed) refetch() }} />}
      {subscribe && <SubscribeDialog collection={subscribe === 'new' ? null : subscribe} onClose={c => { setSubscribe(null); if (c) loadCalendars() }} />}
      {shareColl && <ShareDialog list={shareColl} onClose={changed => { setShareColl(null); if (changed) loadCalendars() }} />}
      {editColl && <CollectionDialog kind="calendars" collection={editColl === 'new' ? null : editColl} defaultKind={newKind} onClose={changed => { setEditColl(null); setNewKind(undefined); if (changed) loadCalendars() }} />}
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
