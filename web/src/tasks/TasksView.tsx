import {
  Button, Checkbox, Dropdown, Field, Input, Option, Text, Textarea, makeStyles, mergeClasses, tokens, Link, Switch,
  Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, MenuItemRadio, MenuDivider, MenuGroupHeader, Spinner,
  Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle,
} from '@fluentui/react-components'
import {
  Add20Regular, Star20Filled, Star20Regular, Delete20Regular, CalendarLtr16Regular, Dismiss20Regular, TaskListLtr20Regular,
  WeatherSunny20Regular, CalendarClock20Regular, Important20Regular, MoreVertical16Regular, ChevronDown20Regular, ChevronRight20Regular,
  TaskListAdd20Regular, CheckmarkCircle20Regular, ArrowSort20Regular, PanelLeft20Regular, Eye20Regular, EyeOff20Regular,
  Cart20Regular, CheckboxChecked20Regular, ArrowReset20Regular, Broom20Regular, TextBulletListSquare20Regular,
  People16Regular, People20Regular, PersonDelete20Regular, Flag20Regular as MailFlagIcon, Note20Regular, ArrowImport20Regular,
  ArrowUp20Regular, ArrowDown20Regular, ReOrderDotsVertical20Regular,
} from '@fluentui/react-icons'
import { useEffect, useMemo, useRef, useState } from 'react'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { usePrefs } from '../prefs'
import { ShoppingList, ListMeta, doneMode, sectionWords } from './ShoppingList'
import { composeItem, itemOf } from './shopParse'
import { api, Collection, Task } from '../api'
import { useApp } from '../App'
import { CollectionDialog } from '../calendar/CollectionDialog'
import { locale, t } from '../i18n'
import { MailFollowUps, useFollowUps } from './MailFollowUps'
import { NotiziTasks, useNotiziTasks } from './NotiziTasks'
import { parseNaturalDate } from '../shell/naturalDate'
import { DateHint, taskDue } from '../shell/DateHint'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0 },
  column: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  panes: { flex: 1, display: 'flex', minHeight: 0, minWidth: 0 },
  side: { width: '230px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '12px 8px', display: 'flex', flexDirection: 'column', gap: '2px' },
  // Eine Zeile, auch fett: lange Namen werden abgeschnitten (ganzer Name im Tooltip)
  nav: { display: 'flex', alignItems: 'center', gap: '10px', padding: '7px 10px', whiteSpace: 'nowrap', '& > svg': { flexShrink: 0 }, borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  active: { backgroundColor: tokens.colorBrandBackground2, fontWeight: 600 },
  name: { overflow: 'hidden', textOverflow: 'ellipsis', minWidth: 0 },
  owner: { color: tokens.colorNeutralForeground3, fontWeight: 400 },
  shared: { flexShrink: 0, color: tokens.colorNeutralForeground3 },
  count: { marginLeft: 'auto', flexShrink: 0, fontSize: '12px', color: tokens.colorNeutralForeground3 },
  // flexShrink 0: sonst schrumpfen Zeilen auf null, sobald die Liste laenger als der Platz ist
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, padding: '20px 28px', gap: '12px', overflowY: 'auto', '& > *': { flexShrink: 0 } },
  doneHead: { display: 'flex', alignItems: 'center', gap: '6px', alignSelf: 'flex-start', fontWeight: 600, marginTop: '8px' },
  add: { display: 'flex', gap: '8px' },
  task: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '8px 12px', borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  sel: { outline: `2px solid ${tokens.colorBrandStroke1}` },
  done: { textDecoration: 'line-through', color: tokens.colorNeutralForeground3 },
  title: { flex: 1, minWidth: 0 },
  due: { fontSize: '12px', color: tokens.colorNeutralForeground3, display: 'flex', alignItems: 'center', gap: '4px' },
  late: { color: tokens.colorPaletteRedForeground1 },
  detail: { width: '340px', flexShrink: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, padding: '16px', display: 'flex', flexDirection: 'column', gap: '12px', overflowY: 'auto' },
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
  const [view, setView] = useState<View>('all')
  const [title, setTitle] = useState('')
  const parsed = useMemo(() => parseNaturalDate(title), [title])
  const [selected, setSelected] = useState<Task | null>(null)
  const [showDone, setShowDone] = useState(false)
  const [listDialog, setListDialog] = useState<Collection | 'new' | null>(null)
  const { prefs, update } = usePrefs()
  const sort = prefs.taskSort ?? 'due'
  const [sidePane, toggleSide] = useSidePane('tasks')
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

  const loadLists = () => api.get<Collection[]>('/api/pim/collections/calendars').then(l => setLists(l.filter(c => c.components.includes('VTODO')))).catch(fail)
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

  const save = async (task: Partial<Task> & { list: number; title: string }) => {
    try {
      await api.post('/api/pim/tasks', {
        list: task.list, uri: task.uri, etag: task.etag, title: task.title, description: task.description, due: task.due, dueAllDay: task.dueAllDay ?? true,
        priority: task.priority ?? 0, completed: task.completed ?? false, percent: task.percent, categories: task.categories,
        moveTo: (task as any).moveTo,
        item: task.item, qty: task.qty, unit: task.unit, section: task.section,
      })
      await load()
    } catch (e) { fail(e) }
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
    <div className={mergeClasses(s.nav, view === key && s.active)} onClick={() => setView(key)} title={label}>{icon}<span className={s.name}>{label}</span><span className={s.count}>{n || ''}</span></div>
  )
  const viewTitle = view === 'today' ? t('Mein Tag') : view === 'important' ? t('Wichtig') : view === 'planned' ? t('Geplant') : view === 'all' ? t('Alle Aufgaben') : view === 'mail' ? t('E-Mail-Nachverfolgung') : view === 'notizi' ? t('Notizi-Aufgaben') : lists.find(l => l.id === view)?.name

  const row = (task: Task) => {
    const late = !!task.due && !task.completed && dueDay(task.due) < today()
    return (
      <div key={task.id} className={mergeClasses(s.task, selected?.id === task.id && s.sel)} onClick={() => setSelected(task)}>
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
    <div key={l.id} className={mergeClasses(s.nav, view === l.id && s.active)} onClick={() => setView(l.id)}
      title={l.owner ? t('Geteilt von {name}', { name: l.owner }) : l.sharedWith?.length ? t('Geteilt mit {names}', { names: l.sharedWith.map(x => x.name).join(', ') }) : l.name}>
      {l.listType === 'shop' ? <Cart20Regular color={l.color ?? undefined} /> : l.listType === 'check' ? <CheckboxChecked20Regular color={l.color ?? undefined} />
        : <TaskListLtr20Regular color={l.color ?? undefined} />}
      <span className={s.name}>{l.name}{l.owner && <span className={s.owner}> · {l.owner}</span>}</span>
      {(l.owner || !!l.sharedWith?.length) && <People16Regular className={s.shared} />}
      <span className={s.count}>{count(x => x.list === l.id) || ''}</span>
      <Menu>
        <MenuTrigger disableButtonEnhancement><Button appearance="transparent" size="small" icon={<MoreVertical16Regular />} aria-label={t('Mehr')} onClick={e => e.stopPropagation()} /></MenuTrigger>
        <MenuPopover>
          <MenuList checkedValues={{ type: [l.listType ?? 'tasks'] }} onCheckedValueChange={(_, d) => setListMeta(l.id, { type: d.checkedItems[0] })}>
            {!l.owner && <MenuItem onClick={() => setListDialog(l)}>{t('Bearbeiten …')}</MenuItem>}
            {!l.owner && <MenuItem icon={<People20Regular />} onClick={() => setShareDialog(l)}>{t('Teilen …')}</MenuItem>}
            {l.owner && <MenuItem icon={<PersonDelete20Regular />} onClick={() => leave(l)}>{t('Nicht mehr anzeigen')}</MenuItem>}
            <MenuDivider />
            <MenuGroupHeader>{t('Listentyp')}</MenuGroupHeader>
            <MenuItemRadio name="type" value="tasks" icon={<TaskListLtr20Regular />}>{t('Aufgaben')}</MenuItemRadio>
            <MenuItemRadio name="type" value="shop" icon={<Cart20Regular />}>{t('Einkaufsliste')}</MenuItemRadio>
            <MenuItemRadio name="type" value="check" icon={<CheckboxChecked20Regular />}>{t('Checkliste')}</MenuItemRadio>
          </MenuList>
        </MenuPopover>
      </Menu>
    </div>
  )

  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      <RibbonHeader area="tasks"
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
          <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!selected} onClick={() => selected && confirm(t('Aufgabe löschen?')) && remove(selected)} />
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
      <aside className={s.side} style={sidePane ? undefined : { display: 'none' }}>
        {nav('today', <WeatherSunny20Regular />, t('Mein Tag'), count(x => !!x.due && dueDay(x.due) <= today()))}
        {nav('important', <Important20Regular />, t('Wichtig'), count(x => x.priority > 0 && x.priority <= 4))}
        {nav('planned', <CalendarClock20Regular />, t('Geplant'), count(x => !!x.due))}
        {nav('all', <TaskListLtr20Regular />, t('Alle'), count(() => true))}
        {/* Systemliste: gekennzeichnete Mails - nicht umbenennbar, nicht loeschbar */}
        <div className={mergeClasses(s.nav, view === 'mail' && s.active)} onClick={() => { setView('mail'); followUps.load() }}>
          <MailFlagIcon /><span className={s.name} title={t('E-Mail-Nachverfolgung')}>{t('E-Mail-Nachverfolgung')}</span><span className={s.count}>{followUps.list.length || ''}</span>
        </div>
        {/* Systemliste: Kaestchen aus den Notizen (notizi) */}
        <div className={mergeClasses(s.nav, view === 'notizi' && s.active)} onClick={() => { setView('notizi'); noteTasks.load() }}>
          <Note20Regular /><span className={s.name} title={t('Notizi-Aufgaben')}>{t('Notizi-Aufgaben')}</span><span className={s.count}>{noteTasks.list.filter(x => !x.done).length || ''}</span>
        </div>
        <div style={{ height: 12 }} />
        {lists.filter(l => !l.owner).map(listNav)}
        <Link onClick={() => setListDialog('new')} style={{ padding: '6px 10px' }}>{t('+ Neue Liste')}</Link>
        {lists.some(l => l.owner) && <>
          <Text size={200} weight="semibold" style={{ padding: '14px 10px 4px', color: tokens.colorNeutralForeground3 }}>{t('GETEILT MIT MIR')}</Text>
          {lists.filter(l => l.owner).map(listNav)}
        </>}
      </aside>
      <div className={s.column}>
      <div className={s.panes}>
      <div className={s.main}>
        <Text size={700} weight="semibold">{viewTitle}</Text>
        {view === 'today' && <Text style={{ color: tokens.colorNeutralForeground3 }}>{new Date().toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long' })}</Text>}
        {view === 'mail' && <MailFollowUps list={followUps.list} reload={followUps.load} />}
        {view === 'notizi' && <NotiziTasks list={noteTasks.list} reload={noteTasks.load} showDone={showDone} setShowDone={setShowDone} />}
        {curList && listType !== 'tasks' && meta && (
          <ShoppingList meta={meta} tasks={shown} showHidden={showDone} selectedId={selected?.id} inputRef={addInput} color={curList.color}
            onAdd={async x => { await save({ list: curList.id, ...x }); loadMeta() }}
            onToggle={toggle} onEdit={setSelected}
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

/** Eine eigene Liste mit anderen Benutzern dieses Servers teilen - alle bearbeiten dieselbe Liste. */
function ShareDialog({ list, onClose }: { list: Collection; onClose: (changed: boolean) => void }) {
  const { fail } = useApp()
  const [people, setPeople] = useState<{ id: string; login: string; displayName: string }[] | null>(null)
  const [members, setMembers] = useState<string[]>(() => list.sharedWith?.map(x => x.id) ?? [])
  useEffect(() => { api.get<any[]>('/api/pim/people').then(setPeople).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const save = async () => {
    try { await api.put(`/api/pim/shares/${list.id}`, { members }); onClose(true) } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 420 }}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ teilen', { name: list.name })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
            <Text>{t('Wer hier angehakt ist, sieht die Liste bei sich unter „Geteilt mit mir“ und kann Einträge hinzufügen, abhaken und ändern.')}</Text>
            {!people ? <Spinner size="tiny" /> : people.length === 0
              ? <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('Auf diesem Server gibt es noch keine anderen Benutzer. Die Verwaltung legt sie an.')}</Text>
              : people.map(p => (
                <Checkbox key={p.id} checked={members.includes(p.id)} label={`${p.displayName} (${p.login})`}
                  onChange={(_, d) => setMembers(m => d.checked ? [...m, p.id] : m.filter(x => x !== p.id))} />
              ))}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={save} disabled={!people}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
