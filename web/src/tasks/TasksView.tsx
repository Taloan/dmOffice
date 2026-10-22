import {
  Button, Checkbox, Dropdown, Field, Input, Option, Text, Textarea, makeStyles, mergeClasses, tokens, Link, Switch,
  Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, MenuItemRadio, MenuDivider,
  Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle,
} from '@fluentui/react-components'
import {
  Add20Regular, Star20Filled, Star20Regular, Delete20Regular, CalendarLtr16Regular, Dismiss20Regular, TaskListLtr20Regular,
  WeatherSunny20Regular, CalendarClock20Regular, Important20Regular, ChevronDown20Regular, ChevronRight20Regular,
  TaskListAdd20Regular, CheckmarkCircle20Regular, ArrowSort20Regular, PanelLeft20Regular, Eye20Regular, EyeOff20Regular,
  Cart20Regular, CheckboxChecked20Regular, ArrowReset20Regular, Broom20Regular, TextBulletListSquare20Regular,
  People16Regular, People20Regular, PersonDelete20Regular, Flag20Regular as MailFlagIcon, Note20Regular, ArrowImport20Regular,
  ArrowUp20Regular, ArrowDown20Regular, ReOrderDotsVertical20Regular,
  MoreHorizontal16Regular, Open20Regular, Edit20Regular, Rename20Regular, ArrowExport20Regular,
} from '@fluentui/react-icons'
import { ColorMenu, ContextMenu, TargetMenu, askText, isTyping, useContextMenu, useMultiSelect } from '../shell/context'
import { useNavStyles } from '../shell/nav'
import { useEffect, useMemo, useRef, useState } from 'react'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { SidePane, useBackClose } from '../shell/mobile'
import { usePrefs } from '../prefs'
import { ShoppingList, ListMeta, doneMode, sectionWords } from './ShoppingList'
import { composeItem, itemOf } from './shopParse'
import { api, Collection, Task } from '../api'
import { useApp } from '../App'
import { CollectionDialog, removeCollection } from '../calendar/CollectionDialog'
import { ShareDialog } from '../calendar/ShareDialog'
import { locale, t } from '../i18n'
import { MailFollowUps, useFollowUps } from './MailFollowUps'
import { NotiziTasks, useNotiziTasks } from './NotiziTasks'
import { parseNaturalDate } from '../shell/naturalDate'
import { DateHint, taskDue } from '../shell/DateHint'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0, position: 'relative' },
  column: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  panes: { flex: 1, display: 'flex', minHeight: 0, minWidth: 0 },
  side: { width: '230px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  owner: { color: tokens.colorNeutralForeground3, fontWeight: 400 },
  shared: { flexShrink: 0, color: tokens.colorNeutralForeground3 },
  // flexShrink 0: sonst schrumpfen Zeilen auf null, sobald die Liste laenger als der Platz ist
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, padding: '20px 28px', gap: '6px', overflowY: 'auto', '& > *': { flexShrink: 0 }, outline: 'none',
    '@media (max-width: 767px)': { padding: '12px', gap: '8px' } },
  doneHead: { display: 'flex', alignItems: 'center', gap: '6px', alignSelf: 'flex-start', fontWeight: 600, marginTop: '8px' },
  add: { display: 'flex', gap: '8px' },
  task: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 8px 4px 12px', minHeight: '40px', borderRadius: tokens.borderRadiusMedium, userSelect: 'none',
    backgroundColor: tokens.colorNeutralBackground2, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  sel: { outline: `2px solid ${tokens.colorBrandStroke1}` },
  multi: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  done: { textDecoration: 'line-through', color: tokens.colorNeutralForeground3 },
  title: { flex: 1, minWidth: 0 },
  due: { fontSize: '12px', color: tokens.colorNeutralForeground3, display: 'flex', alignItems: 'center', gap: '4px' },
  late: { color: tokens.colorPaletteRedForeground1 },
  detail: { width: '340px', flexShrink: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, padding: '16px', display: 'flex', flexDirection: 'column', gap: '12px', overflowY: 'auto',
    // Handy: Aufgabe im Vollbild ueber der Liste
    '@media (max-width: 767px)': { position: 'absolute', inset: 0, zIndex: 15, width: 'auto', borderLeft: 'none', backgroundColor: tokens.colorNeutralBackground1 } },
})

type View = 'today' | 'important' | 'planned' | 'all' | 'mail' | 'notizi' | number

const today = () => new Date().toLocaleDateString('sv-SE')
// Faelligkeit als lokales Datum - slice(0,10) waere bei Zeitangaben das UTC-Datum
const pad2 = (n: number) => String(n).padStart(2, '0')
const dueDay = (due: string) => {
  if (/^\d{4}-\d{2}-\d{2}$/.test(due)) return due
  const d = new Date(due)
  return isNaN(d.getTime()) ? due.slice(0, 10) : `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}`
}

export default function TasksView() {
  const s = useStyles()
  const { fail } = useApp()
  const [lists, setLists] = useState<Collection[]>([])
  const [tasks, setTasks] = useState<Task[]>([])
  const [view, setView] = useState<View>(() => {
    const m = location.hash.match(/^#\/tasks\/(-?\d+)$/)
    if (m) return Number(m[1])
    // Wie in der Mail: beim Zurueckwechseln die zuletzt gewaehlte Liste
    try { const last = JSON.parse(sessionStorage.getItem('dmoffice.tasks.view') || 'null'); if (last != null) return last as View } catch { /* egal */ }
    return 'all'
  })
  useEffect(() => { try { sessionStorage.setItem('dmoffice.tasks.view', JSON.stringify(view)) } catch { /* egal */ } }, [view])
  useEffect(() => {
    const open = () => {
      const m = location.hash.match(/^#\/tasks\/(-?\d+)$/)
      if (!m) return
      history.replaceState(null, '', '#/tasks')
      setView(Number(m[1]))
    }
    open()
    window.addEventListener('hashchange', open)
    return () => window.removeEventListener('hashchange', open)
  }, [])
  const [title, setTitle] = useState('')
  const parsed = useMemo(() => parseNaturalDate(title), [title])
  const [selected, setSelected] = useState<Task | null>(null)
  // Handy: Zurueck-Taste schliesst die Aufgabe im Vollbild
  useBackClose(!!selected, () => setSelected(null))
  const [showDone, setShowDone] = useState(false)
  const [listDialog, setListDialog] = useState<Collection | 'new' | null>(null)
  const { prefs, update } = usePrefs()
  const sort = prefs.taskSort ?? 'due'
  const [sidePane, toggleSide, closeSide] = useSidePane('tasks')
  const addInput = useRef<HTMLInputElement>(null)
  const curList = typeof view === 'number' ? lists.find(l => l.id === view) : undefined
  const listType = curList?.listType ?? 'tasks'
  const [meta, setMeta] = useState<ListMeta | null>(null)
  const loadMeta = () => { if (curList && listType !== 'tasks') api.get<ListMeta>(`/api/pim/lists/${curList.id}`).then(setMeta).catch(fail); else setMeta(null) }
  useEffect(() => { setMeta(null); loadMeta() }, [curList?.id, listType]) // eslint-disable-line react-hooks/exhaustive-deps
  const [sectionsDialog, setSectionsDialog] = useState(false)
  const [importDialog, setImportDialog] = useState(false)
  const [shareDialog, setShareDialog] = useState<Collection | null>(null)
  const leave = async (l: Collection) => {
    if (!confirm(t('„{name}“ nicht mehr anzeigen? {owner} kann die Liste wieder mit dir teilen.', { name: l.name, owner: l.owner ?? '' }))) return
    try { await api.post(`/api/pim/shares/${l.id}/leave`); if (view === l.id) setView('all'); loadLists(); load() } catch (e) { fail(e) }
  }
  const setListMeta = async (id: number, body: Partial<{ type: string; sections: string[]; done: string }>) => {
    try { await api.put(`/api/pim/lists/${id}`, body); await loadLists(); loadMeta() } catch (e) { fail(e) }
  }
  const listAction = async (what: 'reset' | 'clear') => {
    if (!curList) return
    if (what === 'clear' && !confirm(t('Alle abgehakten Einträge löschen?'))) return
    try { await api.post(`/api/pim/lists/${curList.id}/${what}`); load() } catch (e) { fail(e) }
  }
  const newTask = () => {
    if (view === 'mail' || view === 'notizi') setView('all')
    setTimeout(() => addInput.current?.focus(), 0)
  }

  const loadLists = () => api.get<Collection[]>('/api/pim/collections/calendars').then(l => {
    const todo = l.filter(c => c.components.includes('VTODO'))
    setLists(todo)
    // Gemerkte Liste gibt es nicht mehr: zurueck auf "Alle"
    setView(v => typeof v === 'number' && !todo.some(c => c.id === v) ? 'all' : v)
  }).catch(fail)
  const load = () => api.get<Task[]>('/api/pim/tasks').then(setTasks).catch(fail)
  const followUps = useFollowUps()
  const noteTasks = useNotiziTasks()
  // Systemlisten (gekennzeichnete Mails, Kaestchen aus Notizen) haben keine Eingabezeile
  const system = view === 'mail' || view === 'notizi'
  const [ignoreDate, setIgnoreDate] = useState(false)
  useEffect(() => { loadLists(); load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Geteilte Listen aendern auch andere - solange sichtbar alle 20 Sekunden nachladen
  const anyShared = lists.some(l => l.owner || l.sharedWith?.length)
  useEffect(() => {
    if (!anyShared) return
    const iv = setInterval(() => { if (!document.hidden) load() }, 20000)
    return () => clearInterval(iv)
  }, [anyShared]) // eslint-disable-line react-hooks/exhaustive-deps

  const shown = useMemo(() => {
    const now = today()
    let list = tasks
    if (view === 'today') list = list.filter(x => x.due && dueDay(x.due) <= now)
    else if (view === 'important') list = list.filter(x => x.priority > 0 && x.priority <= 4)
    else if (view === 'planned') list = list.filter(x => x.due)
    else if (typeof view === 'number') list = list.filter(x => x.list === view)
    const imp = (x: Task) => (x.priority > 0 ? x.priority : 10)
    const byDue = (a: Task, b: Task) => (a.due ?? '9999').localeCompare(b.due ?? '9999') || a.created.localeCompare(b.created)
    return [...list].sort(sort === 'created' ? (a, b) => b.created.localeCompare(a.created)
      : sort === 'title' ? (a, b) => a.title.localeCompare(b.title, locale, { sensitivity: 'base' })
      : sort === 'importance' ? (a, b) => imp(a) - imp(b) || byDue(a, b)
      : byDue)
  }, [tasks, view, sort])
  const open = shown.filter(x => !x.completed)
  const done = shown.filter(x => x.completed)

  // ---------------- Auswahl, Kontextmenue, Tastatur, Ziehen ----------------
  const nv = useNavStyles()
  const order = useMemo(() => (listType === 'tasks' ? [...open, ...(showDone ? done : [])] : shown).map(x => x.id), [open, done, shown, showDone, listType]) // eslint-disable-line react-hooks/exhaustive-deps
  const ms = useMultiSelect<string>(order)
  useEffect(() => { ms.clear() }, [view]) // eslint-disable-line react-hooks/exhaustive-deps
  const picked = tasks.filter(x => ms.selected.includes(x.id))
  type Ctx = { kind: 'task'; ids: string[] } | { kind: 'list'; l: Collection } | { kind: 'side' } | { kind: 'main' }
  const cm = useContextMenu<Ctx>()
  const mainRef = useRef<HTMLDivElement>(null)
  const isImportant = (x: Task) => x.priority > 0 && x.priority <= 4
  const dayIn = (n: number) => { const d = new Date(); d.setDate(d.getDate() + n); return d.toLocaleDateString('sv-SE') }
  const nextMonday = () => { const d = new Date(); d.setDate(d.getDate() + ((8 - d.getDay()) % 7 || 7)); return d.toLocaleDateString('sv-SE') }
  const clickRow = (e: React.MouseEvent, task: Task) => {
    ms.click(e, task.id)
    // Einfacher Klick oeffnet die Aufgabe rechts, Strg/Umschalt waehlt nur aus
    setSelected(e.ctrlKey || e.metaKey || e.shiftKey ? null : task)
    mainRef.current?.focus({ preventScroll: true })
  }
  const rowMenu = (e: React.MouseEvent, task: Task) => {
    const ids = ms.selected.includes(task.id) ? ms.selected : [task.id]
    ms.pick(task.id)
    cm.open(e, { kind: 'task', ids })
  }
  const DRAG = 'application/x-dmoffice-tasks'
  const dragRow = (e: React.DragEvent, task: Task) => {
    const ids = ms.selected.includes(task.id) ? ms.selected : [task.id]
    e.dataTransfer.setData(DRAG, JSON.stringify(ids))
    e.dataTransfer.effectAllowed = 'copyMove'
  }
  const [dropOn, setDropOn] = useState<View | null>(null)
  /** Ablegen auf Liste (verschieben, mit Strg kopieren), "Mein Tag" (heute faellig) oder "Wichtig". */
  const dropTarget = (key: View) => ({
    onDragOver: (e: React.DragEvent) => {
      if (!e.dataTransfer.types.includes(DRAG)) return
      e.preventDefault()
      e.dataTransfer.dropEffect = typeof key === 'number' && e.ctrlKey ? 'copy' : 'move'
      setDropOn(key)
    },
    onDragLeave: () => setDropOn(d => (d === key ? null : d)),
    onDrop: (e: React.DragEvent) => {
      setDropOn(null)
      const ids: string[] = JSON.parse(e.dataTransfer.getData(DRAG) || '[]')
      const list = tasks.filter(x => ids.includes(x.id))
      if (!list.length) return
      e.preventDefault()
      if (key === 'today') patchMany(list, () => ({ due: today(), dueAllDay: true }))
      else if (key === 'important') patchMany(list, () => ({ priority: 1 }))
      else if (typeof key === 'number') {
        if (e.ctrlKey) copyMany(list, key)
        else patchMany(list.filter(x => x.list !== key), () => ({ moveTo: key }))
      }
    },
  })
  useEffect(() => {
    // Mit den Pfeiltasten durchgehen: rechts die Aufgabe mitnehmen
    if (selected && ms.selected.length === 1 && ms.selected[0] !== selected.id) {
      const x = tasks.find(y => y.id === ms.selected[0])
      if (x) setSelected(x)
    }
  }, [ms.selected]) // eslint-disable-line react-hooks/exhaustive-deps
  const onListKey = (e: React.KeyboardEvent) => {
    if (isTyping(e) || system) return
    const k = e.key
    if (k === 'Delete') { e.preventDefault(); removeMany(picked) }
    else if (k === 'Escape') { ms.clear(); setSelected(null) }
    else if (e.ctrlKey && k.toLowerCase() === 'a') { e.preventDefault(); ms.all() }
    else if (e.ctrlKey && k.toLowerCase() === 'n') { e.preventDefault(); newTask() }
    else if ((k === 'Enter' || k === 'F2') && picked.length) { e.preventDefault(); setSelected(picked[0]) }
    else if (k === ' ' && picked.length) { e.preventDefault(); const to = !picked.every(x => x.completed); patchMany(picked, () => ({ completed: to, percent: to ? 100 : 0 })) }
    else if (k === 'ArrowDown' || k === 'ArrowUp') { e.preventDefault(); ms.step(k === 'ArrowDown' ? 1 : -1, e.shiftKey) }
  }
  const renameList = async (l: Collection) => {
    const name = await askText(t('Umbenennen'), l.name)
    if (!name || name === l.name) return
    try { await api.put(`/api/pim/collections/${l.id}`, { name }); loadLists() } catch (err) { fail(err) }
  }
  const colorList = async (l: Collection, color: string) => {
    try { await api.put(`/api/pim/collections/${l.id}`, { color }); loadLists() } catch (err) { fail(err) }
  }
  const deleteList = async (l: Collection) => {
    try { if (await removeCollection(l, 'calendars', true)) { if (view === l.id) setView('all'); loadLists(); load() } } catch (err) { fail(err) }
  }
  const ownLists = lists.filter(l => !l.owner)

  const ctxMenu = () => {
    const x = cm.ctx?.data
    if (!x) return null
    if (x.kind === 'side') return <MenuItem icon={<TaskListAdd20Regular />} onClick={() => setListDialog('new')}>{t('Neue Liste')}</MenuItem>
    if (x.kind === 'main') return <>
      {!system && <MenuItem icon={<TaskListAdd20Regular />} onClick={newTask}>{listType === 'tasks' ? t('Neue Aufgabe') : t('Neuer Eintrag')}</MenuItem>}
      {!system && order.length > 0 && <MenuItem icon={<CheckboxChecked20Regular />} onClick={ms.all}>{t('Alle auswählen')}</MenuItem>}
      <MenuItem icon={showDone ? <EyeOff20Regular /> : <Eye20Regular />} onClick={() => setShowDone(v => !v)}>{showDone ? t('Erledigte ausblenden') : t('Erledigte zeigen')}</MenuItem>
    </>
    if (x.kind === 'list') {
      const l = x.l
      return <>
        <MenuItem icon={<Open20Regular />} onClick={() => setView(l.id)}>{t('Öffnen')}</MenuItem>
        <MenuItem icon={<TaskListAdd20Regular />} onClick={() => { setView(l.id); setTimeout(() => addInput.current?.focus(), 50) }}>{l.listType === 'tasks' || !l.listType ? t('Neue Aufgabe') : t('Neuer Eintrag')}</MenuItem>
        <MenuDivider />
        {!l.owner && <>
          <MenuItem icon={<Edit20Regular />} onClick={() => setListDialog(l)}>{t('Bearbeiten …')}</MenuItem>
          <MenuItem icon={<Rename20Regular />} onClick={() => renameList(l)}>{t('Umbenennen')}</MenuItem>
          <ColorMenu current={l.color} onPick={c => { cm.close(); if (c) colorList(l, c) }} />
        </>}
        <Menu>
          <MenuTrigger disableButtonEnhancement><MenuItem icon={<TextBulletListSquare20Regular />}>{t('Listentyp')}</MenuItem></MenuTrigger>
          <MenuPopover>
            <MenuList checkedValues={{ type: [l.listType ?? 'tasks'] }} onCheckedValueChange={(_, d) => { cm.close(); setListMeta(l.id, { type: d.checkedItems[0] }) }}>
              <MenuItemRadio name="type" value="tasks" icon={<TaskListLtr20Regular />}>{t('Aufgaben')}</MenuItemRadio>
              <MenuItemRadio name="type" value="shop" icon={<Cart20Regular />}>{t('Einkaufsliste')}</MenuItemRadio>
              <MenuItemRadio name="type" value="check" icon={<CheckboxChecked20Regular />}>{t('Checkliste')}</MenuItemRadio>
            </MenuList>
          </MenuPopover>
        </Menu>
        {!l.owner && <MenuItem icon={<People20Regular />} onClick={() => setShareDialog(l)}>{t('Teilen …')}</MenuItem>}
        {!l.owner && <MenuItem icon={<ArrowExport20Regular />} onClick={() => window.open(`/api/pim/export/${l.id}`)}>{t('Exportieren (.ics)')}</MenuItem>}
        <MenuDivider />
        {l.owner
          ? <MenuItem icon={<PersonDelete20Regular />} onClick={() => leave(l)}>{t('Nicht mehr anzeigen')}</MenuItem>
          : <MenuItem icon={<Delete20Regular />} onClick={() => deleteList(l)}>{t('Löschen')}</MenuItem>}
      </>
    }
    const sel = tasks.filter(y => x.ids.includes(y.id))
    if (!sel.length) return null
    const one = sel.length === 1 ? sel[0] : null
    const allDone = sel.every(y => y.completed)
    const allImp = sel.every(isImportant)
    const lists1 = new Set(sel.map(y => y.list))
    return <>
      {sel.length > 1 && <MenuItem disabled>{t('{n} Aufgaben ausgewählt', { n: sel.length })}</MenuItem>}
      {one && <MenuItem icon={<Open20Regular />} onClick={() => setSelected(one)}>{t('Öffnen')}</MenuItem>}
      <MenuDivider />
      <MenuItem icon={<CheckmarkCircle20Regular />} onClick={() => patchMany(sel, () => ({ completed: !allDone, percent: allDone ? 0 : 100 }))}>
        {allDone ? t('Als nicht erledigt markieren') : t('Als erledigt markieren')}
      </MenuItem>
      <MenuItem icon={allImp ? <Star20Filled /> : <Star20Regular />} onClick={() => patchMany(sel, () => ({ priority: allImp ? 0 : 1 }))}>
        {allImp ? t('Nicht mehr wichtig') : t('Als wichtig markieren')}
      </MenuItem>
      <Menu>
        <MenuTrigger disableButtonEnhancement><MenuItem icon={<CalendarClock20Regular />}>{t('Fällig')}</MenuItem></MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem onClick={() => { cm.close(); patchMany(sel, () => ({ due: today(), dueAllDay: true })) }}>{t('Heute')}</MenuItem>
            <MenuItem onClick={() => { cm.close(); patchMany(sel, () => ({ due: dayIn(1), dueAllDay: true })) }}>{t('Morgen')}</MenuItem>
            <MenuItem onClick={() => { cm.close(); patchMany(sel, () => ({ due: nextMonday(), dueAllDay: true })) }}>{t('Nächste Woche')}</MenuItem>
            <MenuDivider />
            <MenuItem onClick={() => { cm.close(); patchMany(sel, () => ({ due: null })) }}>{t('Kein Fälligkeitsdatum')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
      <MenuDivider />
      <TargetMenu kind="move" targets={lists.filter(l => !(lists1.size === 1 && lists1.has(l.id))).map(l => ({ id: l.id, label: l.owner ? `${l.name} · ${l.owner}` : l.name }))}
        onPick={id => patchMany(sel.filter(y => y.list !== id), () => ({ moveTo: Number(id) }))} />
      <TargetMenu kind="copy" targets={lists.map(l => ({ id: l.id, label: l.owner ? `${l.name} · ${l.owner}` : l.name }))}
        onPick={id => copyMany(sel, Number(id))} />
      <MenuDivider />
      <MenuItem icon={<Delete20Regular />} onClick={() => removeMany(sel)}>{t('Löschen')}</MenuItem>
    </>
  }

  const body = (task: Partial<Task> & { list: number; title: string; moveTo?: number }) => ({
    list: task.list, uri: task.uri, etag: task.etag, title: task.title, description: task.description, due: task.due, dueAllDay: task.dueAllDay ?? true,
    priority: task.priority ?? 0, completed: task.completed ?? false, percent: task.percent, categories: task.categories,
    moveTo: task.moveTo,
    item: task.item, qty: task.qty, unit: task.unit, section: task.section,
  })
  const save = async (task: Partial<Task> & { list: number; title: string }) => {
    try {
      await api.post('/api/pim/tasks', body(task))
      await load()
    } catch (e) { fail(e) }
  }
  /** Mehrere Aufgaben auf einmal aendern (Rechtsklick, Ziehen) - danach einmal neu laden. */
  const patchMany = async (list: Task[], patch: (x: Task) => Partial<Task> & { moveTo?: number }) => {
    try { for (const x of list) await api.post('/api/pim/tasks', body({ ...x, ...patch(x) })) } catch (e) { fail(e) }
    await load()
  }
  /** Kopien in eine Liste (neue Aufgaben, Original bleibt). */
  const copyMany = async (list: Task[], target: number) => {
    try { for (const x of list) await api.post('/api/pim/tasks', body({ ...x, list: target, uri: undefined, etag: undefined })) } catch (e) { fail(e) }
    await load()
  }
  const removeMany = async (list: Task[]) => {
    if (!list.length) return
    if (!confirm(list.length === 1 ? t('Aufgabe löschen?') : t('{n} Aufgaben löschen?', { n: list.length }))) return
    try { for (const x of list) await api.del(`/api/pim/tasks/${x.list}/${encodeURIComponent(x.uri)}`) } catch (e) { fail(e) }
    setSelected(null); ms.clear(); load()
  }

  const add = async () => {
    if (!title.trim()) return
    const list = typeof view === 'number' ? view : lists[0]?.id
    if (!list) return
    // Datum aus dem Text ("morgen Nachmittag") - ausser es wurde weggeklickt
    const nd = ignoreDate ? null : parseNaturalDate(title)
    const when = nd ? taskDue(nd) : { due: view === 'today' ? today() : null, dueAllDay: true }
    await save({ list, title: nd?.title ?? title.trim(), ...when, priority: view === 'important' ? 1 : 0 })
    setTitle('')
    setIgnoreDate(false)
  }

  const toggle = (task: Task) => {
    setTasks(list => list.map(x => x.id === task.id ? { ...x, completed: !x.completed } : x))
    save({ ...task, completed: !task.completed })
  }
  const star = (task: Task) => save({ ...task, priority: task.priority > 0 && task.priority <= 4 ? 0 : 1 })
  const remove = async (task: Task) => {
    try { await api.del(`/api/pim/tasks/${task.list}/${encodeURIComponent(task.uri)}`); setSelected(null); load() } catch (e) { fail(e) }
  }

  const count = (f: (x: Task) => boolean) => tasks.filter(x => !x.completed && f(x)).length
  const nav = (key: View, icon: JSX.Element, label: string, n: number) => (
    <div className={mergeClasses(nv.row, view === key && nv.active, dropOn === key && nv.drop)} onClick={() => setView(key)} title={label}
      {...(key === 'today' || key === 'important' ? dropTarget(key) : {})}>
      {icon}<span className={nv.name}>{label}</span><span className={nv.count}>{n || ''}</span>
    </div>
  )
  const viewTitle = view === 'today' ? t('Mein Tag') : view === 'important' ? t('Wichtig') : view === 'planned' ? t('Geplant') : view === 'all' ? t('Alle Aufgaben') : view === 'mail' ? t('E-Mail-Nachverfolgung') : view === 'notizi' ? t('Notizi-Aufgaben') : lists.find(l => l.id === view)?.name

  const row = (task: Task) => {
    const late = !!task.due && !task.completed && dueDay(task.due) < today()
    return (
      <div key={task.id} className={mergeClasses(s.task, ms.selected.includes(task.id) && s.multi, selected?.id === task.id && s.sel)}
        onClick={e => clickRow(e, task)} onContextMenu={e => rowMenu(e, task)} draggable onDragStart={e => dragRow(e, task)}>
        <Checkbox checked={task.completed} onChange={() => toggle(task)} onClick={e => e.stopPropagation()} shape="circular"
          aria-label={task.completed ? t('Als nicht erledigt markieren') : t('Als erledigt markieren')} />
        <div className={s.title}>
          <div className={task.completed ? s.done : undefined}>{task.title}</div>
          {task.due && <div className={mergeClasses(s.due, late && s.late)}><CalendarLtr16Regular />{new Date(task.due).toLocaleDateString(locale, { weekday: 'short', day: '2-digit', month: '2-digit' })}
            {typeof view !== 'number' && <> · {lists.find(l => l.id === task.list)?.name}</>}</div>}
        </div>
        <Button appearance="transparent" icon={task.priority > 0 && task.priority <= 4 ? <Star20Filled color={tokens.colorPaletteYellowForeground1} /> : <Star20Regular />}
          onClick={e => { e.stopPropagation(); star(task) }} aria-label={t('Wichtig')} />
      </div>
    )
  }

  // Eine Liste im Navigationsbereich; geteilte zeigen, mit wem bzw. von wem
  const listNav = (l: Collection) => (
    <div key={l.id} className={mergeClasses(nv.row, view === l.id && nv.active, dropOn === l.id && nv.drop)} onClick={() => setView(l.id)}
      onContextMenu={e => cm.open(e, { kind: 'list', l })} tabIndex={0} {...dropTarget(l.id)}
      onKeyDown={e => {
        if (e.key === 'Enter') setView(l.id)
        else if (e.key === 'F2' && !l.owner) { e.preventDefault(); renameList(l) }
        else if (e.key === 'Delete' && !l.owner) { e.preventDefault(); deleteList(l) }
      }}
      title={l.owner ? t('Geteilt von {name}', { name: l.owner }) : l.sharedWith?.length ? t('Geteilt mit {names}', { names: l.sharedWith.map(x => x.name).join(', ') }) : l.name}>
      {l.listType === 'shop' ? <Cart20Regular color={l.color ?? undefined} /> : l.listType === 'check' ? <CheckboxChecked20Regular color={l.color ?? undefined} />
        : <TaskListLtr20Regular color={l.color ?? undefined} />}
      <span className={nv.name}>{l.name}{l.owner && <span className={s.owner}> · {l.owner}</span>}</span>
      {(l.owner || !!l.sharedWith?.length) && <People16Regular className={s.shared} />}
      <span className={nv.count}>{count(x => x.list === l.id) || ''}</span>
      <Button className="dm-more" appearance="transparent" size="small" icon={<MoreHorizontal16Regular />} aria-label={t('Mehr')}
        onClick={e => { e.stopPropagation(); cm.open(e, { kind: 'list', l }) }} />
    </div>
  )

  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      <RibbonHeader area="tasks" onMenu={toggleSide}
        start={<>
          <RibbonButton primary icon={<TaskListAdd20Regular />} label={listType === 'tasks' ? t('Neue Aufgabe') : t('Neuer Eintrag')} onClick={newTask} />
          <RibbonDivider />
          {listType === 'shop' && <RibbonButton icon={<Broom20Regular />} label={t('Abgehakte löschen')} onClick={() => listAction('clear')} />}
          {listType === 'check' && <RibbonButton icon={<ArrowReset20Regular />} label={t('Zurück\u00ADsetzen')} title={t('Alle Haken entfernen – für das nächste Mal')} onClick={() => listAction('reset')} />}
          {listType !== 'tasks' && <RibbonButton icon={<TextBulletListSquare20Regular />} label={sectionWords(listType === 'check').many} onClick={() => setSectionsDialog(true)} />}
          {listType !== 'tasks' && <RibbonButton icon={<ArrowImport20Regular />} label={t('Vorschläge importieren')} onClick={() => setImportDialog(true)} />}
          {listType !== 'tasks' && <RibbonDivider />}
          <RibbonButton icon={<CheckmarkCircle20Regular />} label={selected?.completed ? t('Nicht erledigt') : t('Erledigt')} disabled={!selected}
            onClick={() => { if (selected) { toggle(selected); setSelected(null) } }} />
          <RibbonButton icon={<Star20Regular />} label={t('Wichtig')} disabled={!selected} pressed={!!selected && selected.priority > 0 && selected.priority <= 4}
            onClick={() => { if (selected) { star(selected); setSelected(null) } }} />
          <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!selected && !picked.length} onClick={() => removeMany(picked.length ? picked : selected ? [selected] : [])} />
          <RibbonDivider />
          <RibbonButton icon={<TaskListLtr20Regular />} label={t('Neue Liste')} onClick={() => setListDialog('new')} />
        </>}
        view={<>
          <Menu>
            <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ArrowSort20Regular />} label={t('Sortieren')} /></MenuTrigger>
            <MenuPopover>
              <MenuList checkedValues={{ sort: [sort] }} onCheckedValueChange={(_, d) => update({ taskSort: d.checkedItems[0] as any })}>
                <MenuItemRadio name="sort" value="due">{t('Fälligkeit')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="importance">{t('Wichtigkeit')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="title">{t('Titel')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="created">{t('Erstellt (neueste zuerst)')}</MenuItemRadio>
              </MenuList>
            </MenuPopover>
          </Menu>
          {curList && listType !== 'tasks' && (
            <Menu>
              <MenuTrigger disableButtonEnhancement><RibbonButton icon={<CheckboxChecked20Regular />} label={t('Abgehakte')} /></MenuTrigger>
              <MenuPopover>
                <MenuList checkedValues={{ done: [doneMode(meta)] }} onCheckedValueChange={(_, d) => setListMeta(curList.id, { done: d.checkedItems[0] })}>
                  <MenuItemRadio name="done" value="place">{t('Bleiben an ihrem Platz')}</MenuItemRadio>
                  <MenuItemRadio name="done" value="bottom">{t('Rutschen nach unten')}</MenuItemRadio>
                  <MenuItemRadio name="done" value="hide">{t('Werden ausgeblendet')}</MenuItemRadio>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          <RibbonButton icon={showDone ? <Eye20Regular /> : <EyeOff20Regular />} label={t('Erledigte zeigen')} pressed={showDone} onClick={() => setShowDone(v => !v)} />
          <RibbonDivider />
          <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />
        </>} />
      <div className={s.root}>
      <SidePane as="aside" className={mergeClasses(s.side, nv.pane)} open={sidePane} onClose={closeSide}>
        <div style={{ display: 'flex', flexDirection: 'column', flex: 1 }} onContextMenu={e => cm.open(e, { kind: 'side' })}>
        {nav('today', <WeatherSunny20Regular />, t('Mein Tag'), count(x => !!x.due && dueDay(x.due) <= today()))}
        {nav('important', <Important20Regular />, t('Wichtig'), count(x => x.priority > 0 && x.priority <= 4))}
        {nav('planned', <CalendarClock20Regular />, t('Geplant'), count(x => !!x.due))}
        {nav('all', <TaskListLtr20Regular />, t('Alle'), count(() => true))}
        {/* Systemliste: gekennzeichnete Mails - nicht umbenennbar, nicht loeschbar */}
        <div className={mergeClasses(nv.row, view === 'mail' && nv.active)} onClick={() => { setView('mail'); followUps.load() }}>
          <MailFlagIcon /><span className={nv.name} title={t('E-Mail-Nachverfolgung')}>{t('E-Mail-Nachverfolgung')}</span><span className={nv.count}>{followUps.list.length || ''}</span>
        </div>
        {/* Systemliste: Kaestchen aus den Notizen (notizi) */}
        <div className={mergeClasses(nv.row, view === 'notizi' && nv.active)} onClick={() => { setView('notizi'); noteTasks.load() }}>
          <Note20Regular /><span className={nv.name} title={t('Notizi-Aufgaben')}>{t('Notizi-Aufgaben')}</span><span className={nv.count}>{noteTasks.list.filter(x => !x.done).length || ''}</span>
        </div>
        <div className={nv.section}><span style={{ flex: 1 }}>{t('Meine Listen')}</span>
          <Button appearance="subtle" size="small" icon={<TaskListAdd20Regular />} aria-label={t('Neue Liste')} title={t('Neue Liste')} onClick={() => setListDialog('new')} />
        </div>
        {ownLists.map(listNav)}
        <Link className={nv.link} onClick={() => setListDialog('new')}>{t('+ Neue Liste')}</Link>
        {lists.some(l => l.owner) && <>
          <div className={nv.section}>{t('Geteilt mit mir')}</div>
          {lists.filter(l => l.owner).map(listNav)}
        </>}
        <div style={{ flex: 1, minHeight: 24 }} />
        </div>
      </SidePane>
      <div className={s.column}>
      <div className={s.panes}>
      <div className={s.main} ref={mainRef} tabIndex={-1} onKeyDown={onListKey}
        onClick={e => { if (e.target === e.currentTarget) ms.clear() }}
        onContextMenu={e => { if (!(e.target as HTMLElement).closest('input, textarea, a')) cm.open(e, { kind: 'main' }) }}>
        <Text size={700} weight="semibold">{viewTitle}</Text>
        {view === 'today' && <Text style={{ color: tokens.colorNeutralForeground3 }}>{new Date().toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long' })}</Text>}
        {view === 'mail' && <MailFollowUps list={followUps.list} reload={followUps.load} />}
        {view === 'notizi' && <NotiziTasks list={noteTasks.list} reload={noteTasks.load} showDone={showDone} setShowDone={setShowDone} />}
        {curList && listType !== 'tasks' && meta && (
          <ShoppingList meta={meta} tasks={shown} showHidden={showDone} selectedId={selected?.id} inputRef={addInput} color={curList.color}
            onAdd={async x => { await save({ list: curList.id, ...x }); loadMeta() }}
            onToggle={toggle} onEdit={setSelected} onContext={rowMenu} onDragStart={dragRow}
            onForget={async item => { try { await api.post(`/api/pim/lists/${curList.id}/forget`, { item }); loadMeta() } catch (e) { fail(e) } }} />
        )}
        {!system && listType === 'tasks' && <form className={s.add} onSubmit={e => { e.preventDefault(); add() }}>
          <Input ref={addInput} style={{ flex: 1 }} placeholder={t('Aufgabe hinzufügen – z. B. „Zahnarzt morgen Nachmittag“')} value={title} onChange={(_, d) => { setTitle(d.value); if (!d.value) setIgnoreDate(false) }} contentBefore={<Add20Regular />} />
          <Button appearance="primary" type="submit" disabled={!title.trim()}>{t('Hinzufügen')}</Button>
        </form>}
        {!system && listType === 'tasks' && !ignoreDate && parsed && <DateHint nd={parsed} onIgnore={() => setIgnoreDate(true)} />}
        {!system && listType === 'tasks' && open.map(row)}
        {!system && listType === 'tasks' && done.length > 0 && (
          <>
            <Button appearance="subtle" className={s.doneHead} icon={showDone ? <ChevronDown20Regular /> : <ChevronRight20Regular />}
              onClick={() => setShowDone(v => !v)} aria-expanded={showDone}>{t('Erledigt {n}', { n: done.length })}</Button>
            {showDone && done.map(row)}
          </>
        )}
      </div>
      {selected && (
        <TaskDetail key={selected.id} task={selected} lists={lists} sections={meta?.sections} check={listType === 'check'} onClose={() => setSelected(null)}
          onSave={async x => { await save(x); setSelected(null) }} onDelete={() => remove(selected)} />
      )}
      </div>
      </div>
      <ContextMenu at={cm.ctx?.at} onClose={cm.close}>{ctxMenu()}</ContextMenu>
      {shareDialog && <ShareDialog list={shareDialog} onClose={c => { setShareDialog(null); if (c) loadLists() }} />}
      {sectionsDialog && curList && meta && (
        <SectionsDialog sections={meta.sections} check={listType === 'check'} onClose={async v => { setSectionsDialog(false); if (v) await setListMeta(curList.id, { sections: v }) }} />
      )}
      {importDialog && curList && <ImportDialog listId={curList.id} onClose={c => { setImportDialog(false); if (c) loadMeta() }} />}
      {listDialog && <CollectionDialog kind="calendars" tasks collection={listDialog === 'new' ? null : listDialog} onClose={c => { setListDialog(null); if (c) loadLists() }} />}
      </div>
    </div>
  )
}

function TaskDetail({ task, lists, sections, check, onSave, onClose, onDelete }: {
  task: Task; lists: Collection[]; sections?: string[]; check?: boolean; onSave: (x: any) => void; onClose: () => void; onDelete: () => void
}) {
  const s = useStyles()
  // Einkaufs-/Checkliste: Titel aus Menge, Einheit und Artikel
  const typed = !!sections
  const [cur, setCur] = useState(() => typed ? { ...task, ...itemOf(task), section: task.section ?? '' } : task)
  const [moveTo, setMoveTo] = useState(task.list)
  const set = (p: Partial<Task>) => setCur(x => ({ ...x, ...p }))
  return (
    <div className={s.detail}>
      <div style={{ display: 'flex', alignItems: 'center' }}>
        <Text weight="semibold" style={{ flex: 1 }}>{t('Aufgabe')}</Text>
        <Button appearance="subtle" icon={<Dismiss20Regular />} onClick={onClose} aria-label={t('Schließen')} />
      </div>
      {typed ? <>
        <Field label={t('Artikel')}><Input value={cur.item ?? ''} onChange={(_, d) => set({ item: d.value })} /></Field>
        <div style={{ display: 'flex', gap: 8 }}>
          <Field label={t('Menge')} style={{ flex: 1 }}><Input value={cur.qty ?? ''} onChange={(_, d) => set({ qty: d.value })} /></Field>
          <Field label={t('Einheit')} style={{ flex: 1 }}><Input value={cur.unit ?? ''} onChange={(_, d) => set({ unit: d.value })} placeholder="kg, l, Stk …" /></Field>
        </div>
        <Field label={sectionWords(!!check).one}>
          <Dropdown value={cur.section || sectionWords(!!check).none} selectedOptions={[cur.section ?? '']} onOptionSelect={(_, d) => set({ section: d.optionValue ?? '' })}>
            <Option value="" text={sectionWords(!!check).none}>{sectionWords(!!check).none}</Option>
            {sections!.map(x => <Option key={x} value={x}>{x}</Option>)}
          </Dropdown>
        </Field>
      </> : <Field label={t('Titel')}><Input value={cur.title} onChange={(_, d) => set({ title: d.value })} /></Field>}
      {!typed && <Field label={t('Fällig')}><Input type="date" value={cur.due ? dueDay(cur.due) : ''} onChange={(_, d) => set({ due: d.value || null, dueAllDay: true })} /></Field>}
      {!typed && <Field label={t('Priorität')}>
        <Dropdown value={cur.priority === 0 ? t('Keine') : cur.priority <= 4 ? t('Hoch') : cur.priority === 5 ? t('Mittel') : t('Niedrig')}
          selectedOptions={[String(cur.priority)]} onOptionSelect={(_, d) => set({ priority: Number(d.optionValue) })}>
          <Option value="0">{t('Keine')}</Option><Option value="1">{t('Hoch')}</Option><Option value="5">{t('Mittel')}</Option><Option value="9">{t('Niedrig')}</Option>
        </Dropdown>
      </Field>}
      <Switch label={t('Erledigt')} checked={cur.completed} onChange={(_, d) => set({ completed: d.checked })} />
      <Field label={t('Liste')}>
        <Dropdown value={lists.find(l => l.id === moveTo)?.name ?? ''} selectedOptions={[String(moveTo)]} onOptionSelect={(_, d) => setMoveTo(Number(d.optionValue))}>
          {lists.map(l => <Option key={l.id} value={String(l.id)}>{l.name}</Option>)}
        </Dropdown>
      </Field>
      <Field label={t('Notiz')}><Textarea value={cur.description ?? ''} onChange={(_, d) => set({ description: d.value })} rows={8} resize="vertical" /></Field>
      <div style={{ display: 'flex', gap: 8 }}>
        <Button appearance="primary" onClick={() => onSave({
          ...cur, moveTo: moveTo !== task.list ? moveTo : undefined,
          ...(typed ? { title: composeItem({ qty: cur.qty ?? '', unit: cur.unit ?? '', item: cur.item ?? '' }) || cur.title } : {}),
        })}>{t('Speichern')}</Button>
        <Button icon={<Delete20Regular />} onClick={() => confirm(t('Aufgabe löschen?')) && onDelete()}>{t('Löschen')}</Button>
      </div>
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Angelegt {date}', { date: new Date(cur.created).toLocaleString(locale) })}</Text>
    </div>
  )
}

/** Abteilungen (Einkauf) bzw. Kategorien (Checkliste) in der gewuenschten Reihenfolge: ziehen oder mit den Pfeilen verschieben. */
function SectionsDialog({ sections, check, onClose }: { sections: string[]; check: boolean; onClose: (v: string[] | null) => void }) {
  const words = sectionWords(check)
  const [rows, setRows] = useState(() => sections.map((name, i) => ({ id: i, name })))
  const [drag, setDrag] = useState<number | null>(null)
  const [over, setOver] = useState<number | null>(null)
  const [grab, setGrab] = useState<number | null>(null)   // nur am Griff ziehen, damit man im Feld Text markieren kann
  const [added, setAdded] = useState('')
  const nextId = useRef(sections.length)
  const move = (from: number, to: number) => setRows(r => {
    if (to < 0 || to >= r.length || from === to) return r
    const x = [...r]; const [it] = x.splice(from, 1); x.splice(to, 0, it); return x
  })
  const add = () => {
    const name = added.trim()
    if (!name) return
    setRows(r => [...r, { id: nextId.current++, name }]); setAdded('')
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(null)}>
      <DialogSurface style={{ maxWidth: 440 }}>
        <DialogBody>
          <DialogTitle>{words.many}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Text>{words.intro}</Text>
            <div style={{ display: 'flex', flexDirection: 'column', maxHeight: 380, overflowY: 'auto' }}>
              {rows.map((r, i) => (
                <div key={r.id} draggable={grab === r.id}
                  onDragStart={e => { setDrag(i); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', r.name) }}
                  onDragOver={e => { if (drag === null) return; e.preventDefault(); setOver(i) }}
                  onDrop={e => { e.preventDefault(); if (drag !== null) move(drag, i); setDrag(null); setOver(null) }}
                  onDragEnd={() => { setDrag(null); setOver(null); setGrab(null) }}
                  style={{
                    display: 'flex', alignItems: 'center', gap: 4, padding: '2px 0', opacity: drag === i ? 0.4 : 1,
                    borderTop: `2px solid ${over === i && drag !== null && drag > i ? tokens.colorBrandStroke1 : 'transparent'}`,
                    borderBottom: `2px solid ${over === i && drag !== null && drag < i ? tokens.colorBrandStroke1 : 'transparent'}`,
                  }}>
                  <ReOrderDotsVertical20Regular style={{ color: tokens.colorNeutralForeground3, flexShrink: 0, cursor: 'grab' }}
                    onMouseDown={() => setGrab(r.id)} onMouseUp={() => setGrab(null)} />
                  <Input appearance="underline" value={r.name} style={{ flex: 1, minWidth: 0 }}
                    onChange={(_, d) => setRows(x => x.map(y => y.id === r.id ? { ...y, name: d.value } : y))} />
                  <Button appearance="subtle" size="small" icon={<ArrowUp20Regular />} title={t('Nach oben')} disabled={i === 0} onClick={() => move(i, i - 1)} />
                  <Button appearance="subtle" size="small" icon={<ArrowDown20Regular />} title={t('Nach unten')} disabled={i === rows.length - 1} onClick={() => move(i, i + 1)} />
                  <Button appearance="subtle" size="small" icon={<Dismiss20Regular />} title={t('Entfernen')} onClick={() => setRows(x => x.filter(y => y.id !== r.id))} />
                </div>
              ))}
            </div>
            <div style={{ display: 'flex', gap: 6 }}>
              <Input value={added} placeholder={words.add} style={{ flex: 1 }} onChange={(_, d) => setAdded(d.value)}
                onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); add() } }} />
              <Button icon={<Add20Regular />} disabled={!added.trim()} onClick={add}>{t('Hinzufügen')}</Button>
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={() => onClose([...rows.map(r => r.name), added].map(x => x.trim()).filter(Boolean))}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Vorschläge aus AnyList (kopiertes HTML der Vorschlagsliste) oder einer einfachen Liste übernehmen. */
function ImportDialog({ listId, onClose }: { listId: number; onClose: (changed: boolean) => void }) {
  const { fail } = useApp()
  const [text, setText] = useState('')
  const [check, setCheck] = useState<{ found: number; added: number; sample: string[] } | null>(null)
  const [busy, setBusy] = useState(false)
  const run = async (apply: boolean) => {
    setBusy(true)
    try {
      const r = await api.post<{ found: number; added: number; sample: string[] }>(`/api/pim/lists/${listId}/import`, { text, apply })
      if (apply) onClose(r.added > 0); else setCheck(r)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{t('Vorschläge importieren')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Text>{t('Einen Eintrag je Zeile einfügen – oder aus AnyList im Browser die Vorschlagsliste als HTML kopieren und hier einfügen. Die Einträge erscheinen beim Tippen als Vorschlag, auf die Liste selbst kommt nichts.')}</Text>
            <Textarea value={text} onChange={(_, d) => { setText(d.value); setCheck(null) }} rows={12} resize="vertical" />
            {check && (check.found === 0
              ? <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{t('Keine Einträge gefunden.')}</Text>
              : <Text>{t('{found} Einträge gefunden, davon {added} neu. Zum Beispiel: {sample}', { found: String(check.found), added: String(check.added), sample: check.sample.join(', ') })}</Text>)}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            {check && check.added > 0
              ? <Button appearance="primary" disabled={busy} onClick={() => run(true)}>{t('{n} übernehmen', { n: String(check.added) })}</Button>
              : <Button appearance="primary" disabled={busy || !text.trim()} onClick={() => run(false)}>{t('Prüfen')}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
