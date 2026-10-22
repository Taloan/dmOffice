import {
  Button, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, SearchBox, Spinner, Text, Toolbar, Tooltip,
  makeStyles, mergeClasses, tokens, MessageBar, MessageBarBody, MessageBarActions,
} from '@fluentui/react-components'
import {
  Note20Regular, Star20Regular, Star20Filled, Archive20Regular, Delete20Regular, Folder20Regular, ArrowSync20Regular,
  ArrowUndo20Regular, Color20Regular, Attach20Regular, Dismiss16Regular, FolderArrowRight20Regular, FolderAdd20Regular,
  TextBold20Regular, TextItalic20Regular, TextUnderline20Regular, TextStrikethrough20Regular, TextBulletListLtr20Regular,
  TextNumberListLtr20Regular, TextColor20Regular, Highlight20Regular, LockClosed16Regular, CheckmarkCircle16Regular, Circle16Filled,
  TextAlignLeft20Regular, TextAlignCenter20Regular, TextAlignRight20Regular, TextIndentIncreaseLtr20Regular, TextIndentDecreaseLtr20Regular,
  CheckboxChecked20Regular, Code20Regular, CalendarAdd20Regular,
} from '@fluentui/react-icons'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { e2eFetch } from '../e2e'
import { api, Collection, fmtSize, q } from '../api'
import { EventDialog, EventDraft, guessStart } from '../calendar/EventDialog'
import { useApp } from '../App'
import { useDebounced, fmtDate } from '../shell/hooks'
import { useIsDark } from '../theme'
import { cleanPaste, plainToHtml } from './cleanPaste'
import { fixContrast } from './contrast'
import { t } from '../i18n'
import { Snippet } from '../shell/Snippet'
import { MenuItemRadio, MenuDivider } from '@fluentui/react-components'
import { NoteAdd20Regular, Database20Regular, PanelLeft20Regular, TextDescription20Regular, Settings20Regular, ArrowLeft20Regular,
  Open20Regular, Rename20Regular, MoreHorizontal16Regular, ArrowSort20Regular, StarOff20Regular, ChevronDown16Regular, ChevronRight16Regular, People20Regular, People16Regular } from '@fluentui/react-icons'
import { ColorMenu, ContextMenu, TargetMenu, askText, isTyping, useContextMenu, useMultiSelect } from '../shell/context'
import { useNavStyles } from '../shell/nav'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { SidePane, useBackClose, useIsMobile } from '../shell/mobile'
import { usePrefs } from '../prefs'
import { ShareDialog } from '../calendar/ShareDialog'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0, position: 'relative' },
  column: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  panes: { flex: 1, display: 'flex', minHeight: 0, minWidth: 0 },
  side: { width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  twisty: { width: '16px', flexShrink: 0, display: 'inline-flex', alignItems: 'center', justifyContent: 'center', marginRight: '-4px' },
  list: { width: '300px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', minHeight: 0,
    '@media (max-width: 767px)': { width: 'auto', flex: 1, minWidth: 0, borderRight: 'none' } },
  cards: { flex: 1, overflowY: 'auto', padding: '4px 10px 10px', display: 'flex', flexDirection: 'column', gap: '6px', outline: 'none' },
  // Ruhig wie die Mailliste: neutrale Karte, schmaler Balken in der Farbe der Notiz, alle gleich hoch
  card: {
    padding: '8px 12px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', flexShrink: 0, boxSizing: 'border-box', userSelect: 'none',
    display: 'flex', flexDirection: 'column', height: '92px', overflow: 'hidden', position: 'relative',
    color: tokens.colorNeutralForeground1, backgroundColor: tokens.colorNeutralBackground2,
    borderTop: '3px solid #FFF9A8',
    ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  cardShort: { height: '58px' },
  // Erstes Bild der Notiz rechts in der Karte
  cardThumb: { position: 'absolute', right: '8px', top: '10px', bottom: '8px', width: '64px', height: 'calc(100% - 18px)', objectFit: 'cover', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground4 },
  cardSel: { outline: `2px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorNeutralBackground2Selected },
  cardMulti: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  cardTitle: { fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flexShrink: 0 },
  cardText: { fontSize: '12px', color: tokens.colorNeutralForeground3, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden', marginTop: '2px', lineHeight: '16px' },
  cardDate: { fontSize: '11px', color: tokens.colorNeutralForeground4, marginTop: 'auto' },
  editor: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  // Handy: offene Notiz im Vollbild ueber der Liste; am Desktop ohne eigene Box
  editorWrap: { display: 'contents', '@media (max-width: 767px)': { display: 'flex', flexDirection: 'column', position: 'absolute', inset: 0, zIndex: 15, backgroundColor: tokens.colorNeutralBackground1 } },
  head: { display: 'flex', alignItems: 'center', gap: '6px', padding: '8px 12px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  titleInput: { flex: 1 },
  bar: { borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, padding: '2px 8px', flexWrap: 'wrap' },
  // Neutral wie die App, die Notizfarbe nur als Streifen oben
  paper: { flex: 1, overflowY: 'auto', padding: '20px 28px', '@media (max-width: 767px)': { padding: '12px 14px' }, fontSize: '15px', color: tokens.colorNeutralForeground1, backgroundColor: tokens.colorNeutralBackground1 },
  stripe: { height: '6px', flexShrink: 0 },
  atts: { display: 'flex', flexWrap: 'wrap', gap: '6px', padding: '8px 12px', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  att: { display: 'flex', alignItems: 'center', gap: '6px', padding: '2px 4px 2px 10px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, fontSize: '13px' },
  swatches: { display: 'grid', gridTemplateColumns: 'repeat(4, 28px)', gap: '6px', padding: '8px' },
  swatch: { width: '28px', height: '28px', borderRadius: '6px', border: `1px solid ${tokens.colorNeutralStroke1}`, cursor: 'pointer', padding: 0 },
  empty: { margin: 'auto', textAlign: 'center', color: tokens.colorNeutralForeground3, maxWidth: '420px' },
  emptyPane: { '@media (max-width: 767px)': { display: 'none' } },
})

// Notizfarben wie die Kurznotizen
const noteColors = ['#FFF9A8', '#D9F2C4', '#FFD9E8', '#E6D9FF', '#CFE8FF', '#FFE2C2', '#EDEDED', '#3B3B3B']
const textColors = ['#000000', '#C00000', '#E8590C', '#2E7D32', '#0F6CBD', '#7B1FA2']
const marks = ['#FFF176', '#A5D6A7', '#90CAF9', '#F48FB1', 'transparent']


interface NoteListItem { created?: string; image?: string | null; id: number; syncId: string; folder: string | null; title: string; snippet: string; color: string; modified: string; important: boolean; attachments: number; archived: string | null; deleted: string | null
  /** Mit mir geteilt: Name des Besitzers (die Id ist dann negativ) */
  owner?: string | null
  /** Nur in Suchergebnissen: gefunden in diesem Bild/Anhang (Volltextsuche) */
  match?: { name: string; snippet: string } | null }
interface NoteFolder { syncId: string; parent: string | null; name: string; sharedWith?: { id: string; name: string; readOnly?: boolean }[] | null }
/** Ein mit mir geteilter Ordner samt Unterordnern (der oberste ohne Eltern) */
interface SharedRoot { share: number; root: string; owner: string; name: string; canWrite: boolean; folders: NoteFolder[] }
interface NoteDetail {
  id: number; folder: string | null; customTitle: string | null; title: string; color: string; important: boolean
  archived: string | null; deleted: string | null; blocks: string[]; protected: boolean[]; readOnly: boolean
  attachments: { name: string; mime: string | null; size: number; blob: string }[]; pending: boolean; modified: string
  /** Fingerabdruck des Inhalts - beim Speichern zurueck, sonst 409 */
  version: string
  /** Mit mir geteilt: Besitzer; canWrite false = nur lesen */
  owner?: string | null; canWrite?: boolean
}

type View = { kind: 'folder'; id: string | null } | { kind: 'all' | 'important' | 'archive' | 'trash' }

export default function NotesView() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [status, setStatus] = useState<any>(null)
  const [config, setConfig] = useState<any>(null)
  const [folders, setFolders] = useState<NoteFolder[]>([])
  const [shared, setShared] = useState<SharedRoot[]>([])
  const [shareDialog, setShareDialog] = useState<NoteFolder | null>(null)
  const [view, setView] = useState<View>({ kind: 'all' })
  const [notes, setNotes] = useState<{ items: NoteListItem[]; archived: number; trashed: number } | null>(null)
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<number | null>(null)
  // Datenbanken unter der Sync-Wurzel (wie die Auswahl "Datenbank" in notizi)
  const [dbs, setDbs] = useState<{ current: string; list: { folder: string; title: string; passwordFits: boolean }[] | null } | null>(null)
  const [switching, setSwitching] = useState(false)
  const loadDbs = () => api.get('/api/notes/databases').then(setDbs).catch(() => {})
  useEffect(() => { loadDbs() }, [])
  // Direkt eine Notiz oeffnen: #/notes/<id> (aus "Notizi-Aufgaben")
  useEffect(() => {
    const open = () => {
      const m = location.hash.match(/^#\/notes\/(\d+)$/)
      if (!m) return
      history.replaceState(null, '', '#/notes')
      setSearch('')
      setView({ kind: 'all' })
      setSelected(Number(m[1]))
    }
    open()
    window.addEventListener('hashchange', open)
    return () => window.removeEventListener('hashchange', open)
  }, [])
  const switchDb = async (folder: string) => {
    if (folder === '__new') {
      const name = prompt(t('Name der neuen Datenbank (wird im Abgleichordner angelegt):'))
      if (!name?.trim()) return
      folder = name.trim()
    }
    setSwitching(true)
    setSelected(null)
    try {
      setStatus(await api.post('/api/notes/database', { folder }))
      await Promise.all([loadDbs(), loadNotes(), loadFolders()])
    } catch (e) { fail(e) } finally { setSwitching(false) }
  }
  const query = useDebounced(search, 300)
  const { prefs, update } = usePrefs()
  const preview = prefs.notePreview ?? true
  const [sidePane, toggleSide, closeSide] = useSidePane('notes')
  // Handy: Zurueck-Taste schliesst die offene Notiz
  useBackClose(selected != null, () => setSelected(null))

  const loadFolders = () => Promise.all([
    api.get<NoteFolder[]>('/api/notes/folders').then(setFolders).catch(() => {}),
    api.get<SharedRoot[]>('/api/notes/shared').then(setShared).catch(() => {}),
  ])
  /** Der mit mir geteilte Ordner, zu dem dieser Ordner gehoert. */
  const shareOf = (id: string | null | undefined) => (id ? shared.find(x => x.folders.some(f => f.syncId === id)) : undefined)
  /** Mit mir geteilte Ordner, in die ich schreiben darf - Ziele fuer eigene Notizen (sie werden dorthin kopiert). */
  const sharedTargets = () => shared.filter(x => x.canWrite).flatMap(sh => folderTargets([], sh.folders)
    .map(x => (x.id === sh.root ? { ...x, label: t('{name} (von {owner})', { name: x.label, owner: sh.owner }) } : x)))
  const loadNotes = useCallback(() => {
    const params = query ? { q: query } : view.kind === 'folder' ? { folder: view.id ?? '' } : { view: view.kind }
    return api.get('/api/notes?' + q(params as any)).then(setNotes).catch(fail)
  }, [view, query, fail])

  useEffect(() => {
    api.get('/api/settings/notizi').then(setConfig).catch(() => setConfig({}))
    api.get('/api/notes/status').then(setStatus).catch(() => {})
    loadFolders()
  }, [])
  useEffect(() => { loadNotes() }, [loadNotes])
  // Stabil fuer den Editor
  const onChanged = useCallback(() => { loadNotes() }, [loadNotes])
  const onGone = useCallback(() => { setSelected(null); loadNotes() }, [loadNotes])
  const onOpen = useCallback(async (nid: number) => { await loadNotes(); setSelected(nid) }, [loadNotes])
  // Was von anderen Geraeten kommt, alle 30 Sekunden nachladen
  useEffect(() => {
    const iv = setInterval(() => { if (!document.hidden) { loadNotes(); api.get('/api/notes/status').then(setStatus).catch(() => {}) } }, 30000)
    return () => clearInterval(iv)
  }, [loadNotes])

  const sync = async () => {
    try { setStatus(await api.post('/api/notes/sync')); loadNotes(); loadFolders() } catch (e) { fail(e) }
  }

  const curShare = view.kind === 'folder' ? shareOf(view.id) : undefined
  const canCreate = !curShare || curShare.canWrite
  const create = async () => {
    if (!canCreate) return
    try {
      const r = await api.post('/api/notes', { folder: view.kind === 'folder' ? view.id : null })
      await loadNotes()
      setSelected(r.id)
    } catch (e) { fail(e) }
  }

  const newFolder = async (parent: string | null = null) => {
    const name = await askText(parent ? t('Neuer Unterordner') : t('Neuer Ordner'), '', t('Name des Ordners'), t('Anlegen'))
    if (!name) return
    try { await api.post('/api/notes/folders', { name, parent }); loadFolders() } catch (e) { fail(e) }
  }

  // ---------------- Auswahl, Kontextmenue, Tastatur, Ziehen ----------------
  const nv = useNavStyles()
  // Sortieren (Ansicht -> Sortieren); "Eigene Reihenfolge" ist die Reihenfolge aus notizi bzw. vom Server
  const noteSort = prefs.noteSort ?? 'manual'
  const noteDesc = prefs.noteSortDesc ?? noteSort !== 'title'
  const items = useMemo(() => {
    const list = notes?.items ?? []
    if (noteSort === 'manual') return list
    const key = (n: NoteListItem) => noteSort === 'title' ? n.title : noteSort === 'created' ? (n.created ?? n.modified) : n.modified
    const sorted = [...list].sort((a, b) => noteSort === 'title'
      ? a.title.localeCompare(b.title, undefined, { sensitivity: 'base', numeric: true })
      : key(a).localeCompare(key(b)))
    return noteDesc ? sorted.reverse() : sorted
  }, [notes, noteSort, noteDesc])
  const mx = useMultiSelect<number>(useMemo(() => items.map(n => n.id), [items]))
  const picked = items.filter(n => mx.selected.includes(n.id))
  useEffect(() => { mx.clear() }, [view, query]) // eslint-disable-line react-hooks/exhaustive-deps
  type Ctx = { kind: 'note'; ids: number[] } | { kind: 'folder'; f: NoteFolder; sh?: SharedRoot } | { kind: 'side' } | { kind: 'list' }
  const cm = useContextMenu<Ctx>()
  const cardsRef = useRef<HTMLDivElement>(null)
  // Geaenderte Notiz offen: Editor neu laden (Farbe, Ordner ...)
  const [editorRev, setEditorRev] = useState(0)
  // F2 / Umbenennen: Titel dieser Notiz fokussieren, sobald sie offen ist
  const [renameId, setRenameId] = useState<number | null>(null)
  const mobile = useIsMobile()
  const [closed, setClosed] = useState<string[]>([])
  const inTrash = view.kind === 'trash'
  const inArchive = view.kind === 'archive'
  const afterChange = async (ids: number[], gone: boolean) => {
    if (selected != null && ids.includes(selected)) { if (gone) setSelected(null); else setEditorRev(r => r + 1) }
    mx.clear()
    await loadNotes()
  }
  const metaMany = async (ids: number[], body: any) => {
    try { for (const id of ids) await api.put(`/api/notes/${id}/meta`, body) } catch (e) { fail(e) }
    // Weg aus der Liste: im Ordner verschoben, oder in einen geteilten Ordner (dort unter neuer Kennung)
    afterChange(ids, body.folder !== undefined && (view.kind === 'folder' || !!shareOf(body.folder)))
  }
  // Termin aus einer Notiz: Titel und Text uebernehmen, ein Datum im Text als Vorschlag
  const [eventDraft, setEventDraft] = useState<{ draft: EventDraft; calendars: Collection[] } | null>(null)
  const eventFrom = async (id: number) => {
    try {
      const [n, cals] = await Promise.all([api.get<NoteDetail>(`/api/notes/${id}`), api.get<Collection[]>('/api/pim/collections/calendars')])
      const calendars = cals.filter(c => c.components.includes('VEVENT'))
      if (calendars.length === 0) { notify(t('Kein Kalender vorhanden'), undefined, 'warning'); return }
      const doc = new DOMParser().parseFromString(n.blocks.join('\n'), 'text/html')
      doc.querySelectorAll('br').forEach(b => b.replaceWith('\n'))
      doc.querySelectorAll('p,div,li,h1,h2,h3,h4,tr').forEach(b => b.append('\n'))
      const text = (doc.body.textContent ?? '').replace(/\n{3,}/g, '\n\n').trim().slice(0, 8000)
      const start = guessStart(`${n.title}\n${text}`)
      setEventDraft({
        calendars,
        draft: { calendar: calendars[0].id, title: n.title, description: text, start, end: new Date(start.getTime() + 3600000), allDay: false, rRule: '', reminder: 15, busy: true },
      })
    } catch (e) { fail(e) }
  }
  const actionMany = async (ids: number[], a: 'archive' | 'restore' | 'trash') => {
    try { for (const id of ids) await api.post(`/api/notes/${id}/${a}`) } catch (e) { fail(e) }
    afterChange(ids, true)
  }
  const destroyMany = async (ids: number[]) => {
    if (!confirm(ids.length === 1 ? t('Endgültig löschen? Auch auf allen anderen Geräten.') : t('{n} Notizen endgültig löschen? Auch auf allen anderen Geräten.', { n: ids.length }))) return
    try { for (const id of ids) await api.del(`/api/notes/${id}`) } catch (e) { fail(e) }
    afterChange(ids, true)
  }
  const removeMany = (ids: number[]) => { if (ids.length) (inTrash ? destroyMany(ids) : actionMany(ids, 'trash')) }
  const clickCard = (e: React.MouseEvent, id: number) => {
    mx.click(e, id)
    if (!(e.ctrlKey || e.metaKey || e.shiftKey)) setSelected(id)
    cardsRef.current?.focus({ preventScroll: true })
  }
  const cardMenu = (e: React.MouseEvent, id: number) => {
    const ids = mx.selected.includes(id) ? mx.selected : [id]
    mx.pick(id)
    cm.open(e, { kind: 'note', ids })
  }
  useEffect(() => {
    if (mx.selected.length === 1 && selected != null && mx.selected[0] !== selected) setSelected(mx.selected[0])
  }, [mx.selected]) // eslint-disable-line react-hooks/exhaustive-deps
  const onCardsKey = (e: React.KeyboardEvent) => {
    if (isTyping(e)) return
    const k = e.key
    const ids = picked.length ? picked.map(n => n.id) : selected != null ? [selected] : []
    if (k === 'Delete') { e.preventDefault(); removeMany(ids) }
    else if (k === 'Escape') { mx.clear(); setSelected(null) }
    else if (e.ctrlKey && k.toLowerCase() === 'a') { e.preventDefault(); mx.all() }
    else if (e.ctrlKey && k.toLowerCase() === 'n') { e.preventDefault(); create() }
    else if (k === 'Enter' && ids.length) { e.preventDefault(); setSelected(ids[0]) }
    else if (k === 'F2' && ids.length === 1) { e.preventDefault(); setSelected(ids[0]); setRenameId(ids[0]) }
    else if (k === 'ArrowDown' || k === 'ArrowUp') { e.preventDefault(); mx.step(k === 'ArrowDown' ? 1 : -1, e.shiftKey) }
  }
  const renameFolder = async (f: NoteFolder) => {
    const name = await askText(t('Umbenennen'), f.name)
    if (!name || name === f.name) return
    try { await api.put(`/api/notes/folders/${f.syncId}`, { name }); loadFolders() } catch (err) { fail(err) }
  }
  const deleteFolder = async (f: NoteFolder) => {
    if (!confirm(t('Ordner „{name}“ löschen?', { name: f.name }))) return
    try {
      await api.del(`/api/notes/folders/${f.syncId}`)
      if (view.kind === 'folder' && view.id === f.syncId) setView({ kind: 'all' })
      loadFolders()
    } catch (err) { fail(err) }
  }
  const leaveShare = async (sh: SharedRoot) => {
    if (!confirm(t('„{name}“ nicht mehr anzeigen? Der Besitzer kann den Ordner erneut teilen.', { name: sh.name }))) return
    try {
      await api.post(`/api/notes/shares/${sh.share}/leave`)
      if (view.kind === 'folder' && sh.folders.some(f => f.syncId === view.id)) setView({ kind: 'all' })
      loadFolders()
    } catch (err) { fail(err) }
  }
  const moveFolder = async (id: string, parent: string | null) => {
    try { await api.post(`/api/notes/folders/${id}/move`, { parent }); loadFolders() } catch (err) { fail(err) }
  }
  /** Ordner selbst und alle darunter - dorthin laesst er sich nicht verschieben. */
  const below = (id: string): string[] => [id, ...folders.filter(f => f.parent === id).flatMap(f => below(f.syncId))]
  const depthOf = (f: NoteFolder): number => (f.parent ? 1 + depthOf(folders.find(x => x.syncId === f.parent) ?? { ...f, parent: null }) : 0)
  // Ordner in Baumreihenfolge fuer "Verschieben nach"
  const folderTargets = (exclude: string[] = [], list: NoteFolder[] = folders) => {
    const walk = (parent: string | null): NoteFolder[] => list.filter(f => f.parent === parent).flatMap(f => [f, ...walk(f.syncId)])
    const depth = (f: NoteFolder): number => (f.parent ? 1 + depth(list.find(x => x.syncId === f.parent) ?? { ...f, parent: null }) : 0)
    return walk(null).map(f => ({ id: f.syncId, label: f.name, depth: depth(f), disabled: exclude.includes(f.syncId) }))
  }
  const DRAG_NOTES = 'application/x-dmoffice-notes'
  const DRAG_FOLDER = 'application/x-dmoffice-notefolder'
  const dragFolder = useRef<string | null>(null)
  const [dropOn, setDropOn] = useState<string | null>(null)
  /** Ablageziel: Ordner (Notizen und Ordner), "Ohne Ordner", "Wichtig", "Archiv", "Papierkorb". */
  const dropTarget = (key: string, accept: { notes?: (ids: number[]) => void; folder?: (id: string) => void }) => ({
    onDragOver: (e: React.DragEvent) => {
      const notesOk = accept.notes && e.dataTransfer.types.includes(DRAG_NOTES)
      const folderOk = accept.folder && e.dataTransfer.types.includes(DRAG_FOLDER) && dragFolder.current && !below(dragFolder.current).includes(key)
      if (!notesOk && !folderOk) return
      e.preventDefault(); e.stopPropagation(); setDropOn(key)
    },
    onDragLeave: () => setDropOn(d => (d === key ? null : d)),
    onDrop: (e: React.DragEvent) => {
      setDropOn(null)
      if (e.dataTransfer.types.includes(DRAG_NOTES) && accept.notes) {
        e.preventDefault(); e.stopPropagation()
        accept.notes(JSON.parse(e.dataTransfer.getData(DRAG_NOTES) || '[]'))
      } else if (e.dataTransfer.types.includes(DRAG_FOLDER) && accept.folder && dragFolder.current) {
        e.preventDefault(); e.stopPropagation()
        const id = dragFolder.current
        dragFolder.current = null
        if (!below(id).includes(key)) accept.folder(id)
      }
    },
  })

  const ctxMenu = () => {
    const x = cm.ctx?.data
    if (!x) return null
    if (x.kind === 'side') return <MenuItem icon={<FolderAdd20Regular />} onClick={() => newFolder()}>{t('Neuer Ordner')}</MenuItem>
    if (x.kind === 'list') return <>
      <MenuItem icon={<NoteAdd20Regular />} onClick={create} disabled={!canCreate}>{t('Neue Notiz')}</MenuItem>
      {items.length > 0 && <MenuItem icon={<CheckboxChecked20Regular />} onClick={mx.all}>{t('Alle auswählen')}</MenuItem>}
    </>
    if (x.kind === 'folder' && x.sh) {
      const f = x.f, sh = x.sh
      return <>
        <MenuItem icon={<Open20Regular />} onClick={() => { setView({ kind: 'folder', id: f.syncId }); setSearch('') }}>{t('Öffnen')}</MenuItem>
        {sh.canWrite && <MenuItem icon={<NoteAdd20Regular />} onClick={async () => {
          try { const r = await api.post('/api/notes', { folder: f.syncId }); setView({ kind: 'folder', id: f.syncId }); setSearch(''); setSelected(r.id) } catch (err) { fail(err) }
        }}>{t('Neue Notiz')}</MenuItem>}
        {sh.canWrite && <MenuItem icon={<FolderAdd20Regular />} onClick={() => newFolder(f.syncId)}>{t('Neuer Unterordner')}</MenuItem>}
        {f.syncId === sh.root && <>
          <MenuDivider />
          <MenuItem icon={<Dismiss16Regular />} onClick={() => leaveShare(sh)}>{t('Nicht mehr anzeigen')}</MenuItem>
        </>}
      </>
    }
    if (x.kind === 'folder') {
      const f = x.f
      const parentOf = folders.find(y => y.syncId === f.parent)
      return <>
        <MenuItem icon={<Open20Regular />} onClick={() => { setView({ kind: 'folder', id: f.syncId }); setSearch('') }}>{t('Öffnen')}</MenuItem>
        <MenuItem icon={<NoteAdd20Regular />} onClick={async () => {
          try { const r = await api.post('/api/notes', { folder: f.syncId }); setView({ kind: 'folder', id: f.syncId }); setSearch(''); setSelected(r.id) } catch (err) { fail(err) }
        }}>{t('Neue Notiz')}</MenuItem>
        <MenuItem icon={<FolderAdd20Regular />} onClick={() => newFolder(f.syncId)}>{t('Neuer Unterordner')}</MenuItem>
        <MenuItem icon={<People20Regular />} onClick={() => setShareDialog(f)}>{t('Teilen …')}</MenuItem>
        <MenuDivider />
        <MenuItem icon={<Rename20Regular />} onClick={() => renameFolder(f)}>{t('Umbenennen')}</MenuItem>
        <TargetMenu kind="move" targets={[{ id: '', label: t('Oberste Ebene'), disabled: !f.parent }, ...folderTargets(below(f.syncId).concat(parentOf ? [parentOf.syncId] : []))]}
          onPick={id => moveFolder(f.syncId, id ? String(id) : null)} />
        <MenuDivider />
        <MenuItem icon={<Delete20Regular />} onClick={() => deleteFolder(f)}>{t('Löschen')}</MenuItem>
      </>
    }
    return noteMenuItems(x.ids)
  }
  /** Aktionen fuer eine oder mehrere Notizen - im Kontextmenue und im Bereich "n Notizen ausgewaehlt". */
  const noteMenuItems = (idsIn: number[]) => {
    const sel = items.filter(n => idsIn.includes(n.id))
    if (!sel.length) return null
    const ids = sel.map(n => n.id)
    const one = sel.length === 1 ? sel[0] : null
    const allImp = sel.every(n => n.important)
    // Mit mir geteilte Notizen: nur Bearbeiten innerhalb ihres Ordners, nichts archivieren oder endgültig löschen
    const theirs = sel.some(n => n.id < 0)
    const sh = theirs ? shareOf(sel[0].folder) : undefined
    if (theirs) {
      const mixed = sel.some(n => n.id >= 0 || shareOf(n.folder) !== sh)
      const edit = !mixed && !!sh?.canWrite
      return <>
        {!one && <MenuItem disabled>{t('{n} Notizen ausgewählt', { n: sel.length })}</MenuItem>}
        {one && <MenuItem icon={<Open20Regular />} onClick={() => setSelected(one.id)}>{t('Öffnen')}</MenuItem>}
        {edit && <>
          {one && <MenuItem icon={<Rename20Regular />} onClick={() => { setSelected(one.id); setRenameId(one.id) }}>{t('Umbenennen')}</MenuItem>}
          <MenuDivider />
          <MenuItem icon={allImp ? <StarOff20Regular /> : <Star20Regular />} onClick={() => metaMany(ids, { important: !allImp })}>{allImp ? t('Nicht mehr wichtig') : t('Als wichtig markieren')}</MenuItem>
          <ColorMenu colors={noteColors} square current={one?.color} label={t('Farbe der Notiz')} onPick={c => { cm.close(); if (c) metaMany(ids, { color: c }) }} />
          <MenuDivider />
          <TargetMenu kind="move" targets={folderTargets([], sh!.folders)} onPick={id => metaMany(ids, { folder: String(id) })} />
          <MenuDivider />
          <MenuItem icon={<Delete20Regular />} onClick={() => actionMany(ids, 'trash')}>{t('Löschen')}</MenuItem>
        </>}
      </>
    }
    return <>
      {!one && <MenuItem disabled>{t('{n} Notizen ausgewählt', { n: sel.length })}</MenuItem>}
      {one && <MenuItem icon={<Open20Regular />} onClick={() => setSelected(one.id)}>{t('Öffnen')}</MenuItem>}
      {one && !inTrash && <MenuItem icon={<Rename20Regular />} onClick={() => { setSelected(one.id); setRenameId(one.id) }}>{t('Umbenennen')}</MenuItem>}
      {!inTrash && <>
        <MenuDivider />
        <MenuItem icon={allImp ? <StarOff20Regular /> : <Star20Regular />} onClick={() => metaMany(ids, { important: !allImp })}>{allImp ? t('Nicht mehr wichtig') : t('Als wichtig markieren')}</MenuItem>
        <ColorMenu colors={noteColors} square current={one?.color} label={t('Farbe der Notiz')} onPick={c => { cm.close(); if (c) metaMany(ids, { color: c }) }} />
        {one && <MenuItem icon={<CalendarAdd20Regular />} onClick={() => eventFrom(one.id)}>{t('Termin erstellen')}</MenuItem>}
        <MenuDivider />
        <TargetMenu kind="move" targets={[{ id: '', label: t('Ohne Ordner') }, ...folderTargets(), ...sharedTargets()]} onPick={id => metaMany(ids, { folder: String(id) })} />
      </>}
      <MenuDivider />
      {inTrash || inArchive
        ? <MenuItem icon={<ArrowUndo20Regular />} onClick={() => actionMany(ids, 'restore')}>{t('Wiederherstellen')}</MenuItem>
        : <MenuItem icon={<Archive20Regular />} onClick={() => actionMany(ids, 'archive')}>{t('Archivieren')}</MenuItem>}
      {inTrash
        ? <MenuItem icon={<Delete20Regular />} onClick={() => destroyMany(ids)}>{t('Endgültig löschen')}</MenuItem>
        : <MenuItem icon={<Delete20Regular />} onClick={() => actionMany(ids, 'trash')}>{t('Löschen')}</MenuItem>}
    </>
  }

  const tree = (parent: string | null, depth: number, list: NoteFolder[] = folders, sh?: SharedRoot): JSX.Element[] =>
    list.filter(f => f.parent === parent).flatMap(f => {
      const kids = list.some(x => x.parent === f.syncId)
      const shut = closed.includes(f.syncId)
      return [
        <div key={f.syncId} className={mergeClasses(nv.row, view.kind === 'folder' && view.id === f.syncId && nv.active, dropOn === f.syncId && nv.drop)}
          style={{ paddingLeft: 4 + depth * 14 }} tabIndex={0} title={sh && f.syncId === sh.root ? t('{name} (von {owner})', { name: f.name, owner: sh.owner }) : f.name}
          onClick={() => { setView({ kind: 'folder', id: f.syncId }); setSearch('') }}
          onContextMenu={e => cm.open(e, { kind: 'folder', f, sh })}
          onKeyDown={e => {
            if (e.key === 'Enter') { setView({ kind: 'folder', id: f.syncId }); setSearch('') }
            else if (sh) return
            else if (e.key === 'F2') { e.preventDefault(); renameFolder(f) }
            else if (e.key === 'Delete') { e.preventDefault(); deleteFolder(f) }
          }}
          draggable={!sh} onDragStart={e => { dragFolder.current = f.syncId; e.dataTransfer.setData(DRAG_FOLDER, f.syncId); e.dataTransfer.effectAllowed = 'move' }}
          onDragEnd={() => { dragFolder.current = null }}
          {...dropTarget(f.syncId, sh ? { notes: ids => metaMany(ids, { folder: f.syncId }) } : { notes: ids => metaMany(ids, { folder: f.syncId }), folder: id => moveFolder(id, f.syncId) })}>
          <span className={s.twisty} onClick={e => { if (kids) { e.stopPropagation(); setClosed(c => shut ? c.filter(x => x !== f.syncId) : [...c, f.syncId]) } }}>
            {kids && (shut ? <ChevronRight16Regular /> : <ChevronDown16Regular />)}
          </span>
          <Folder20Regular /><span className={nv.name}>{sh && f.syncId === sh.root ? t('{name} (von {owner})', { name: f.name, owner: sh.owner }) : f.name}</span>
          {!sh && !!f.sharedWith?.length && <People16Regular title={t('Geteilt mit {names}', { names: f.sharedWith.map(m => m.name).join(', ') })} />}
          <Button className="dm-more" appearance="transparent" size="small" icon={<MoreHorizontal16Regular />} aria-label={t('Mehr')}
            onClick={e => { e.stopPropagation(); cm.open(e, { kind: 'folder', f, sh }) }} />
        </div>,
        ...(shut ? [] : tree(f.syncId, depth + 1, list, sh)),
      ]
    })


  const nav = (v: View, icon: JSX.Element, label: string, n?: number) => {
    const key = v.kind === 'folder' ? 'nofolder' : v.kind
    // Notizen ablegen: ohne Ordner, wichtig, archivieren, in den Papierkorb
    const drop = v.kind === 'folder' ? { notes: (ids: number[]) => metaMany(ids, { folder: '' }), folder: (id: string) => moveFolder(id, null) }
      : v.kind === 'important' ? { notes: (ids: number[]) => metaMany(ids, { important: true }) }
      : v.kind === 'archive' ? { notes: (ids: number[]) => actionMany(ids, 'archive') }
      : v.kind === 'trash' ? { notes: (ids: number[]) => actionMany(ids, 'trash') }
      : v.kind === 'all' ? { folder: (id: string) => moveFolder(id, null) } : {}
    return (
      <div className={mergeClasses(nv.row, view.kind === v.kind && (v.kind !== 'folder' || (view as any).id === (v as any).id) && nv.active, dropOn === key && nv.drop)}
        onClick={() => { setView(v); setSearch('') }} {...dropTarget(key, drop)}>
        {icon}<span className={nv.name}>{label}</span><span className={nv.count}>{n || ''}</span>
      </div>
    )
  }

  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      <RibbonHeader area="notes" onMenu={toggleSide}
        start={<>
          <RibbonButton primary icon={<NoteAdd20Regular />} label={t('Neue Notiz')} onClick={create} disabled={switching || !canCreate} />
          <RibbonDivider />
          <RibbonButton icon={<FolderAdd20Regular />} label={t('Neuer Ordner')} onClick={() => newFolder()} />
          <RibbonDivider />
          {dbs?.list && (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                {/* Der Knopf zeigt, welche Datenbank offen ist - ein Klick wechselt */}
                <RibbonButton icon={<Database20Regular />} disabled={switching}
                  label={switching ? t('Wechsle …') : dbs.list.find(d => d.folder === dbs.current)?.title ?? t('Datenbank')}
                  title={t('Datenbank wechseln')} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList checkedValues={{ db: [dbs.current] }} onCheckedValueChange={(_, d) => d.checkedItems[0] && switchDb(d.checkedItems[0])}>
                  {dbs.list.map(d => (
                    <MenuItemRadio key={d.folder} name="db" value={d.folder} disabled={!d.passwordFits}>
                      {d.title}{!d.passwordFits && ' ' + t('(anderes Passwort)')}
                    </MenuItemRadio>
                  ))}
                  <MenuDivider />
                  <MenuItem onClick={() => switchDb('__new')}>{t('Neue Datenbank …')}</MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          <RibbonButton icon={<ArrowSync20Regular />} label={t('Abgleichen')} onClick={sync} disabled={status?.running} />
        </>}
        view={<>
          <RibbonButton icon={<TextDescription20Regular />} label={t('Vorschau­text')} pressed={preview} onClick={() => update({ notePreview: !preview })} />
          <Menu>
            <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ArrowSort20Regular />} label={t('Sortieren')} /></MenuTrigger>
            <MenuPopover>
              <MenuList checkedValues={{ sort: [noteSort], dir: [noteDesc ? 'desc' : 'asc'] }}
                onCheckedValueChange={(_, d) => d.name === 'sort'
                  ? update({ noteSort: d.checkedItems[0] as any, noteSortDesc: d.checkedItems[0] !== 'title' })
                  : update({ noteSortDesc: d.checkedItems[0] === 'desc' })}>
                <MenuItemRadio name="sort" value="manual">{t('Eigene Reihenfolge (wie in notizi)')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="modified">{t('Geändert')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="created">{t('Erstellt')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="title">{t('Titel')}</MenuItemRadio>
                {noteSort !== 'manual' && <>
                  <MenuDivider />
                  <MenuItemRadio name="dir" value="asc">{noteSort === 'title' ? t('A bis Z') : t('Älteste zuerst')}</MenuItemRadio>
                  <MenuItemRadio name="dir" value="desc">{noteSort === 'title' ? t('Z bis A') : t('Neueste zuerst')}</MenuItemRadio>
                </>}
              </MenuList>
            </MenuPopover>
          </Menu>
          <RibbonDivider />
          <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />
          <RibbonDivider />
          <RibbonButton icon={<Settings20Regular />} label={t('Notiz­einstellungen')} onClick={() => { location.hash = '#/settings/notes' }} />
        </>} />
      <div className={s.root}>
      <SidePane as="aside" className={mergeClasses(s.side, nv.pane)} open={sidePane} onClose={closeSide}>
        <div style={{ display: 'flex', flexDirection: 'column', flex: 1 }} onContextMenu={e => cm.open(e, { kind: 'side' })}>
        {nav({ kind: 'all' }, <Note20Regular />, t('Alle Notizen'))}
        {nav({ kind: 'folder', id: null }, <Note20Regular />, t('Ohne Ordner'))}
        {nav({ kind: 'important' }, <Star20Regular />, t('Wichtig'))}
        <div className={nv.section}>
          <span style={{ flex: 1 }}>{t('Ordner')}</span>
          <Tooltip content={t('Neuer Ordner')} relationship="label"><Button appearance="subtle" size="small" icon={<FolderAdd20Regular />} onClick={() => newFolder()} /></Tooltip>
        </div>
        {tree(null, 0)}
        {shared.length > 0 && <>
          <div className={nv.section}><span style={{ flex: 1 }}>{t('Geteilt mit mir')}</span></div>
          {shared.flatMap(sh => tree(null, 0, sh.folders, sh))}
        </>}
        <div style={{ flex: 1, minHeight: 16 }} />
        {nav({ kind: 'archive' }, <Archive20Regular />, t('Archiv'), notes?.archived)}
        {nav({ kind: 'trash' }, <Delete20Regular />, t('Papierkorb'), notes?.trashed)}
        <div className={nv.row} onClick={sync} title={status?.error ?? status?.report ?? ''}>
          <ArrowSync20Regular />
          <span style={{ fontSize: 12 }}>
            {status?.running ? t('Gleicht ab …') : status?.error ? <span style={{ color: tokens.colorPaletteRedForeground1 }}>{t('Abgleich: Fehler')}</span>
              : status?.lastRun ? t('Abgeglichen {date}', { date: fmtDate(status.lastRun) }) : t('Abgleichen')}
          </span>
        </div>
        </div>
      </SidePane>
      <div className={s.column}>
      <div className={s.panes}>
      <div className={s.list}>
        {config && !config.enabled && (
          <MessageBar intent="info" style={{ margin: '10px 10px 0' }}>
            <MessageBarBody>
              {t('Tipp: Mit der App notizi hast du deine Notizen auch auf dem Windows-Desktop – mit Erinnerungen, Texterkennung und Abgleich.')}{' '}
              <a href="#/settings/notes">{t('Abgleich einrichten')}</a>
            </MessageBarBody>
          </MessageBar>
        )}
        <div style={{ padding: 10 }}><SearchBox placeholder={t('Notizen durchsuchen')} value={search} onChange={(_, d) => setSearch(d.value)} style={{ width: '100%' }} /></div>
        <div className={s.cards} ref={cardsRef} tabIndex={-1} onKeyDown={onCardsKey} onContextMenu={e => cm.open(e, { kind: 'list' })}
          onClick={e => { if (e.target === e.currentTarget) mx.clear() }}>
          {!notes ? <Spinner /> : items.length === 0 ? <Text style={{ padding: 8, color: tokens.colorNeutralForeground3 }}>{t('Keine Notizen.')}</Text>
            : items.map(n => (
              <div key={n.id} className={mergeClasses(s.card, !preview && s.cardShort, mx.selected.includes(n.id) && s.cardMulti, selected === n.id && s.cardSel)}
                onClick={e => clickCard(e, n.id)} onContextMenu={e => cardMenu(e, n.id)} draggable={!mobile}
                onDragStart={e => { e.dataTransfer.setData(DRAG_NOTES, JSON.stringify(mx.selected.includes(n.id) ? mx.selected : [n.id])); e.dataTransfer.effectAllowed = 'move' }}
                style={{ borderTopColor: n.color || '#FFF9A8', paddingRight: n.image ? 84 : undefined }}>
                {n.image && <img className={s.cardThumb} src={`/api/notes/blob/${encodeURIComponent(n.image)}?thumb=160`} alt="" loading="lazy" draggable={false}
                  onError={e => { (e.currentTarget as HTMLImageElement).style.display = 'none' }} />}
                <div className={s.cardTitle}>{n.important && '★ '}{n.title}</div>
                {n.match ? <div className={s.cardText}>📎 {t('im Anhang {name}', { name: n.match.name })}: <Snippet text={n.match.snippet} /></div>
                  : preview && n.snippet && <div className={s.cardText}>{n.snippet}</div>}
                <div className={s.cardDate}>{fmtDate(n.modified)}{n.attachments > 0 && ` · 📎 ${n.attachments}`}{n.owner && ` · ${t('von {name}', { name: n.owner })}`}</div>
              </div>
            ))}
        </div>
      </div>
      {picked.length > 1 ? (
        // Mehrere markiert: Aktionen fuer alle statt des Editors einer einzelnen Notiz (wie in Outlook)
        <div className={mergeClasses(s.empty, s.emptyPane)} style={{ flexDirection: 'column', gap: 12 }}>
          <Text size={500} weight="semibold">{t('{n} Notizen ausgewählt', { n: picked.length })}</Text>
          <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center' }}>
            {view.kind === 'archive' || view.kind === 'trash'
              ? <Button icon={<ArrowUndo20Regular />} onClick={() => actionMany(picked.map(n => n.id), 'restore')}>{t('Wiederherstellen')}</Button>
              : picked.every(n => n.id >= 0) && <Button icon={<Archive20Regular />} onClick={() => actionMany(picked.map(n => n.id), 'archive')}>{t('Archivieren')}</Button>}
            <Button icon={<Delete20Regular />} onClick={() => removeMany(picked.map(n => n.id))}>{view.kind === 'trash' ? t('Endgültig löschen') : t('Löschen')}</Button>
            <Menu>
              <MenuTrigger disableButtonEnhancement><Button icon={<MoreHorizontal16Regular />}>{t('Weitere Aktionen')}</Button></MenuTrigger>
              <MenuPopover><MenuList>{noteMenuItems(picked.map(n => n.id))}</MenuList></MenuPopover>
            </Menu>
          </div>
          <Button appearance="subtle" onClick={() => mx.clear()}>{t('Auswahl aufheben')}</Button>
        </div>
      ) : selected ? (
        <div className={s.editorWrap}>
          <NoteEditor key={`${selected}-${editorRev}`} id={selected} folders={folders} shared={shared} sharedTargets={sharedTargets} focusTitle={renameId === selected} onTitleFocused={() => setRenameId(null)}
            onChanged={onChanged} onGone={onGone} onOpen={onOpen} notify={notify} fail={fail} onBack={() => setSelected(null)} />
        </div>
      ) : (
        <div className={mergeClasses(s.empty, s.emptyPane)}><Text>{t('Wähle eine Notiz aus oder lege eine neue an.')}</Text></div>
      )}
      </div>
      </div>
      <ContextMenu at={cm.ctx?.at} onClose={cm.close}>{ctxMenu()}</ContextMenu>
      {shareDialog && <ShareDialog notesFolder={shareDialog.syncId} list={{ id: 0, name: shareDialog.name, components: [], sharedWith: shareDialog.sharedWith }}
        onClose={changed => { setShareDialog(null); if (changed) loadFolders() }} />}
      {eventDraft && (
        <EventDialog draft={eventDraft.draft} calendars={eventDraft.calendars}
          onClose={saved => { setEventDraft(null); if (saved) notify(t('Termin angelegt')) }} />
      )}
      </div>
    </div>
  )
}

function NoteEditor({ id, folders, shared, sharedTargets, onChanged, onGone, onOpen, notify, fail, onBack, focusTitle, onTitleFocused }: {
  id: number; folders: NoteFolder[]; shared: SharedRoot[]; sharedTargets: () => { id: string; label: string; depth: number }[]; onChanged: () => void; onGone: () => void; onOpen: (id: number) => void; onBack: () => void; focusTitle?: boolean; onTitleFocused?: () => void
  notify: (t: string, b?: string, i?: any) => void; fail: (e: unknown) => void
}) {
  const s = useStyles()
  const mobile = useIsMobile()
  const [note, setNote] = useState<NoteDetail | null>(null)
  const [title, setTitle] = useState('')
  const titleRef = useRef<HTMLInputElement>(null)
  const [saving, setSaving] = useState<'idle' | 'dirty' | 'saving' | 'saved'>('idle')
  const root = useRef<HTMLDivElement | null>(null)
  // Das Element ueber das Aushaengen hinaus festhalten - beim Notizwechsel ist root.current schon null
  const lastEl = useRef<HTMLDivElement | null>(null)
  const setRoot = useCallback((el: HTMLDivElement | null) => { root.current = el; if (el) lastEl.current = el }, [])
  const ser0 = useRef<string[]>([])
  const timer = useRef<number | undefined>(undefined)
  const fileInput = useRef<HTMLInputElement>(null)
  const isDark = useIsDark()
  const [rev, setRev] = useState(0)
  const [rejected, setRejected] = useState<string | null>(null)
  const rejectedRef = useRef(false)
  rejectedRef.current = !!rejected
  // Version, auf die sich die Blocknummern im Editor beziehen, und welche Bloecke gesperrt sind
  const version = useRef<string | undefined>(undefined)
  const prot = useRef<boolean[]>([])
  // Ungespeicherte Aenderungen im Editor
  const dirty = useRef(false)
  // Speichern nacheinander, sonst schickt der zweite Aufruf eine veraltete Version
  const chain = useRef<Promise<void>>(Promise.resolve())

  /** Laedt die Notiz. Den Editor nur neu aufbauen, wenn sich der Inhalt geaendert hat und nichts offen ist. */
  const load = useCallback(async (force = false) => {
    try {
      const n = await api.get<NoteDetail>(`/api/notes/${id}`)
      setNote(n)
      setTitle(n.customTitle ?? '')
      if (force || version.current === undefined || (n.version !== version.current && !dirty.current)) {
        version.current = n.version
        dirty.current = false
        setRev(r => r + 1)
      }
    } catch (e) { fail(e) }
  }, [id, fail])
  useEffect(() => { load() }, [load])
  // Mit mir geteilt: schaut jemand anderes in dieselbe Notiz? Ungespeichertes bleibt, der Hinweis bietet Kopie oder Verwerfen
  useEffect(() => {
    if (!note?.owner) return
    const iv = setInterval(async () => {
      if (document.hidden) return
      try {
        const n = await api.get<NoteDetail>(`/api/notes/${id}`)
        if (n.version === version.current) return
        if (dirty.current) setRejected(t('Die Notiz wurde inzwischen von jemand anderem geändert.'))
        else load()
      } catch { /* weg oder nicht mehr geteilt: die Liste räumt auf */ }
    }, 15000)
    return () => clearInterval(iv)
  }, [note?.owner, id, load])
  // F2 / "Umbenennen": in den Titel
  useEffect(() => {
    if (!focusTitle || !note) return
    setTimeout(() => { titleRef.current?.focus(); titleRef.current?.select() }, 0)
    onTitleFocused?.()
  }, [focusTitle, note?.id]) // eslint-disable-line react-hooks/exhaustive-deps

  // Bloecke in den Editor - jeder mit seiner Nummer, gesperrte nicht bearbeitbar
  useEffect(() => {
    const el = root.current
    if (!el || !note) return
    prot.current = note.protected
    el.innerHTML = ''
    note.blocks.forEach((html, i) => {
      const tpl = document.createElement('template')
      tpl.innerHTML = html.trim().startsWith('<') ? html : `<p>${html}</p>`
      const nodes = Array.from(tpl.content.children) as HTMLElement[]
      const node = nodes[0] ?? document.createElement('p')
      node.setAttribute('data-i', String(i))
      if (note.protected[i] || note.readOnly) node.setAttribute('contenteditable', 'false')
      node.querySelectorAll('img').forEach(img => {
        const src = img.getAttribute('src') || ''
        if (src.startsWith('blob:')) { img.setAttribute('data-blob', src.slice(5)); img.setAttribute('src', `/api/notes/blob/${src.slice(5)}`) }
      })
      el.appendChild(node)
      nodes.slice(1).forEach(n => el.appendChild(n))
    })
    if (!el.children.length) el.appendChild(document.createElement('p'))
    fixContrast(el)
    ser0.current = Array.from(el.children).map(c => ser(c as HTMLElement))
  }, [note?.id, rev]) // eslint-disable-line react-hooks/exhaustive-deps

  // Hell/Dunkel gewechselt: Kontrast neu pruefen (Inhalt bleibt)
  useEffect(() => { if (root.current) fixContrast(root.current) }, [isDark])

  const ser = (node: HTMLElement): string => {
    const clone = node.cloneNode(true) as HTMLElement
    clone.removeAttribute('data-i')
    clone.removeAttribute('contenteditable')
    clone.removeAttribute('data-dm-contrast')
    clone.querySelectorAll('[data-dm-contrast]').forEach(e => e.removeAttribute('data-dm-contrast'))
    clone.querySelectorAll('img[data-blob]').forEach(img => {
      img.setAttribute('src', 'blob:' + img.getAttribute('data-blob'))
      img.removeAttribute('data-blob')
    })
    return clone.outerHTML
  }

  const textBlock = (txt: string) => `<p>${txt.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</p>`

  const doSave = async (plain?: string) => {
    const el = root.current ?? lastEl.current
    if (!el || !note || note.readOnly) return
    const used = new Set<number>()
    const blocks: ({ i: number } | { html: string })[] = []
    // Je Block der Knoten und sein Stand beim Absenden - danach die Nummern nachfuehren
    const nodes: (HTMLElement | null)[] = []
    const snap: string[] = []
    el.childNodes.forEach(n => {
      if (n.nodeType === 3) { if (n.textContent?.trim()) { blocks.push({ html: textBlock(n.textContent) }); nodes.push(null); snap.push('') } return }
      if (n.nodeType !== 1) return
      const node = n as HTMLElement
      const now = ser(node)
      const i = node.hasAttribute('data-i') ? Number(node.getAttribute('data-i')) : -1
      if (i >= 0 && !used.has(i) && (prot.current[i] || now === ser0.current[i])) { used.add(i); blocks.push({ i }) }
      else { if (i >= 0) used.add(i); blocks.push({ html: node.tagName === 'DIV' && !node.hasAttribute('data-i') ? `<p>${node.innerHTML}</p>` : now }) }
      nodes.push(node); snap.push(now)
    })
    dirty.current = false
    setSaving('saving')
    try {
      const r = await e2eFetch(`/api/notes/${id}/content`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin',
        body: JSON.stringify({ blocks, plain: plain ?? el.innerText, version: version.current }),
      })
      const data = await r.json().catch(() => null)
      if (!r.ok) throw new Error(data?.error || r.statusText)
      version.current = data?.version
      renumber(el, blocks, nodes, snap, data?.protected ?? null)
      setSaving(dirty.current ? 'dirty' : 'saved')
      setRejected(null)
      onChanged()
    } catch (e) {
      dirty.current = true
      setSaving('dirty')
      setRejected(e instanceof Error ? e.message : String(e))
    }
  }

  /** Nach dem Speichern: Nummern im Editor auf die neue Version umstellen. */
  const renumber = (el: HTMLElement, blocks: ({ i: number } | { html: string })[], nodes: (HTMLElement | null)[], snap: string[], flags: boolean[] | null) => {
    el.querySelectorAll(':scope > [data-i]').forEach(n => n.removeAttribute('data-i'))
    ser0.current = []
    if (!flags) { prot.current = []; return }
    prot.current = flags
    if (flags.length === blocks.length) {
      // Ein Block je Knoten: Nummer gleich Position, Stand vom Absenden als Vergleich
      nodes.forEach((node, k) => { if (node) { node.setAttribute('data-i', String(k)); ser0.current[k] = snap[k] } })
      return
    }
    // Sonst nur die gesperrten Bloecke zuordnen (Reihenfolge bleibt), der Rest geht beim naechsten Mal als HTML
    const target = flags.map((f, k) => (f ? k : -1)).filter(k => k >= 0)
    const sent = nodes.filter((node, k) => node && 'i' in blocks[k] && node.getAttribute('contenteditable') === 'false')
    if (sent.length === target.length) sent.forEach((node, k) => node!.setAttribute('data-i', String(target[k])))
  }

  // Immer die aktuelle Version aufrufen (keine veralteten Closures), nacheinander
  const saveRef = useRef(doSave)
  saveRef.current = doSave
  const save = useCallback((plain?: string) => (chain.current = chain.current.then(() => saveRef.current(plain))), [])

  const changed = () => {
    dirty.current = true
    setSaving('dirty')
    clearTimeout(timer.current)
    timer.current = undefined
    // Nach abgelehntem Speichern nicht dauernd neu versuchen
    if (rejectedRef.current) return
    timer.current = window.setTimeout(() => { timer.current = undefined; save() }, 1200)
  }
  /** Ausstehende Aenderungen sofort speichern (vor Meta-Aktionen und beim Wechsel). */
  const flush = async () => {
    if (timer.current !== undefined) { clearTimeout(timer.current); timer.current = undefined; await save() }
    else await chain.current
  }
  // Beim Wechseln der Notiz sofort sichern - als Layout-Cleanup, da steht der Editor noch im DOM
  useLayoutEffect(() => () => {
    // Text jetzt lesen - ausgehaengt liefert innerText keine Zeilenumbrueche mehr
    if (timer.current !== undefined) { clearTimeout(timer.current); timer.current = undefined; save(root.current?.innerText) }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const meta = async (body: any) => {
    try {
      await flush(); await api.put(`/api/notes/${id}/meta`, body)
      // In einen geteilten Ordner kopiert: die Notiz hat dort eine neue Kennung, die Liste zeigt sie unter „Geteilt mit mir“
      if (id > 0 && body.folder && shared.some(x => x.folders.some(f => f.syncId === body.folder))) { onGone(); return }
      await load(); onChanged()
    } catch (e) { fail(e) }
  }
  const action = async (a: 'archive' | 'restore' | 'trash') => {
    try { await flush(); await api.post(`/api/notes/${id}/${a}`); a === 'restore' ? (await load(), onChanged()) : onGone() } catch (e) { fail(e) }
  }
  /** Aenderungen verwerfen und den Stand vom Server laden. */
  const discard = () => {
    clearTimeout(timer.current); timer.current = undefined
    dirty.current = false
    setRejected(null); setSaving('saved')
    load(true)
  }
  /** Bei Konflikt: den Editorinhalt als neue Notiz sichern, diese bleibt unveraendert. */
  const saveAsCopy = async () => {
    const el = root.current
    if (!el || !note) return
    const blocks: { html: string }[] = []
    el.childNodes.forEach(n => {
      if (n.nodeType === 3) { if (n.textContent?.trim()) blocks.push({ html: textBlock(n.textContent) }); return }
      if (n.nodeType !== 1) return
      const node = n as HTMLElement
      blocks.push({ html: node.tagName === 'DIV' && !node.hasAttribute('data-i') ? `<p>${node.innerHTML}</p>` : ser(node) })
    })
    try {
      const r = await api.post<{ id: number }>('/api/notes', { folder: note.folder })
      await api.put(`/api/notes/${r.id}/content`, { blocks, plain: el.innerText })
      clearTimeout(timer.current); timer.current = undefined
      dirty.current = false
      setRejected(null)
      notify(t('Als Kopie gespeichert'))
      onOpen(r.id)
    } catch (e) { fail(e) }
  }
  const destroy = async () => {
    if (!confirm(t('Endgültig löschen? Auch auf allen anderen Geräten.'))) return
    try { await api.del(`/api/notes/${id}`); onGone() } catch (e) { fail(e) }
  }
  const exec = (cmd: string, value?: string) => { document.execCommand('styleWithCSS', false, 'true'); document.execCommand(cmd, false, value); changed() }

  /** Der Absatz bzw. Listenpunkt, in dem die Schreibmarke steht (innerhalb des Editors). */
  const currentBlock = (): HTMLElement | null => {
    const sel = window.getSelection()
    let n: Node | null = sel?.anchorNode ?? null
    while (n && n !== root.current) {
      if (n.nodeType === 1 && ['P', 'LI', 'DIV', 'H1', 'H2', 'H3'].includes((n as HTMLElement).tagName)) return n as HTMLElement
      n = n.parentNode
    }
    return null
  }

  /** Codeblock wie in notizi: feste Schrittweite auf durchscheinendem Grau (CodeBlockTools). */
  const toggleCode = () => {
    const b = currentBlock()
    if (!b || b.tagName === 'LI') return
    if (/consolas|courier/i.test(b.style.fontFamily)) {
      b.style.removeProperty('font-family'); b.style.removeProperty('background-color')
      b.style.removeProperty('padding'); b.style.removeProperty('margin')
    } else {
      b.style.fontFamily = 'Consolas, "Courier New"'
      b.style.backgroundColor = '#80808022'
      b.style.padding = '6px 8px'
      b.style.margin = '4px 0px 8px'
      // Zeichenweise Schrift und Markierung heraus, sonst schlaegt sie die des Absatzes (wie in notizi)
      b.querySelectorAll<HTMLElement>('[style]').forEach(el => { el.style.removeProperty('font-family'); el.style.removeProperty('background-color') })
    }
    if (!b.getAttribute('style')) b.removeAttribute('style')
    changed()
  }

  /** Kaestchen wie in notizi: ☐ am Zeilenanfang; noch einmal entfernt es. */
  const toggleCheckbox = () => {
    const b = currentBlock()
    if (!b) return
    const first = b.firstChild
    if (first && first.nodeType === 3 && /^[\u2610\u2611] ?/.test(first.textContent ?? '')) {
      first.textContent = (first.textContent ?? '').replace(/^[\u2610\u2611] ?/, '')
    } else {
      b.insertBefore(document.createTextNode('\u2610 '), b.firstChild)
    }
    changed()
  }

  /** Klick auf ☐/☑ hakt ab bzw. oeffnet wieder. */
  const onEditorClick = () => {
    const sel = window.getSelection()
    const node = sel?.anchorNode
    if (!node || node.nodeType !== 3 || !sel?.isCollapsed) return
    const text = node.textContent ?? ''
    const o = sel.anchorOffset
    for (const i of [o, o - 1]) {
      const c = text[i]
      if (c === '\u2610' || c === '\u2611') {
        node.textContent = text.slice(0, i) + (c === '\u2610' ? '\u2611' : '\u2610') + text.slice(i + 1)
        const r = document.createRange()
        r.setStart(node, Math.min(i + 1, node.textContent.length))
        sel.removeAllRanges(); sel.addRange(r)
        changed()
        return
      }
    }
  }

  /** Bilder kommen an die Schreibmarke (wie in notizi), alles andere wird Anhang. */
  const addFiles = async (files: File[]) => {
    const images = files.filter(f => f.type.startsWith('image/'))
    const others = files.filter(f => !f.type.startsWith('image/'))
    if (images.length && !note?.readOnly) {
      // Schreibmarke merken - das Hochladen dauert
      const sel = window.getSelection()
      const range = sel && sel.rangeCount && root.current?.contains(sel.anchorNode) ? sel.getRangeAt(0).cloneRange() : null
      try {
        let html = ''
        for (const f of images) {
          const form = new FormData()
          form.append('file', f, f.name || 'bild.png')
          const { name } = await api.form<{ name: string }>(`/api/notes/images?note=${id}`, form)
          html += `<p><img src="/api/notes/blob/${name}" data-blob="${name}" style="max-width:100%" /></p>`
        }
        root.current?.focus()
        if (range) { const s2 = window.getSelection(); s2?.removeAllRanges(); s2?.addRange(range) }
        document.execCommand('insertHTML', false, html)
        changed()
      } catch (e) { fail(e) }
    }
    if (others.length) {
      const dt = new DataTransfer()
      others.forEach(f => dt.items.add(f))
      upload(dt.files)
    }
  }

  const upload = async (files: FileList | null) => {
    if (!files?.length) return
    const f = new FormData()
    Array.from(files).forEach(x => f.append('files', x, x.name))
    try { await flush(); await api.form(`/api/notes/${id}/attachments`, f); await load(); notify(t('Angehängt')) } catch (e) { fail(e) }
  }

  if (!note) return <Spinner style={{ margin: 'auto' }} />
  const bg = note.color || '#FFF9A8'
  const inTrash = !!note.deleted
  // Mit mir geteilt: Besitzer, und ob ich nur lesen darf
  const locked = note.canWrite === false
  const moveFolders = note.owner ? (shared.find(x => x.folders.some(f => f.syncId === note.folder))?.folders ?? []) : folders

  const tb = (label: string, icon: JSX.Element, cmd: string, value?: string) => (
    <Tooltip content={label} relationship="label">
      <Button appearance="subtle" size="small" icon={icon} onMouseDown={e => e.preventDefault()} onClick={() => exec(cmd, value)} disabled={note.readOnly || inTrash} />
    </Tooltip>
  )
  const picker = (label: string, icon: JSX.Element, values: string[], cmd: string) => (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content={label} relationship="label"><Button appearance="subtle" size="small" icon={icon} onMouseDown={e => e.preventDefault()} disabled={note.readOnly || inTrash} /></Tooltip>
      </MenuTrigger>
      <MenuPopover>
        <div className={s.swatches}>
          {values.map(c => <button key={c} className={s.swatch} style={{ background: c === 'transparent' ? '#fff' : c }} onMouseDown={e => e.preventDefault()} onClick={() => exec(cmd, c)} />)}
        </div>
      </MenuPopover>
    </Menu>
  )

  return (
    <div className={s.editor}>
      <div className={s.head}>
        {mobile && <Button appearance="subtle" icon={<ArrowLeft20Regular />} onClick={onBack} aria-label={t('Zurück')} title={t('Zurück')} />}
        <Input ref={titleRef} className={s.titleInput} appearance="underline" placeholder={note.title} value={title} readOnly={locked}
          onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); (e.target as HTMLInputElement).blur() } }}
          onChange={(_, d) => setTitle(d.value)} onBlur={() => title !== (note.customTitle ?? '') && meta(title ? { customTitle: title } : { clearCustomTitle: true })} />
        <Tooltip content={saving === 'saving' ? t('Speichert …') : saving === 'dirty' ? t('Ungespeichert') : note.pending ? t('Gespeichert, noch nicht abgeglichen') : t('Gespeichert und abgeglichen')} relationship="label">
          <span style={{ display: 'inline-flex', color: tokens.colorNeutralForeground3 }}>
            {saving === 'saving' ? <Spinner size="extra-tiny" /> : saving === 'dirty' || note.pending ? <Circle16Filled color={tokens.colorPaletteYellowForeground1} /> : <CheckmarkCircle16Regular />}
          </span>
        </Tooltip>
        <Tooltip content={t('Wichtig')} relationship="label">
          <Button appearance="subtle" disabled={locked} icon={note.important ? <Star20Filled color={tokens.colorPaletteYellowForeground1} /> : <Star20Regular />} onClick={() => meta({ important: !note.important })} />
        </Tooltip>
        <Menu>
          <MenuTrigger disableButtonEnhancement><Tooltip content={t('Farbe der Notiz')} relationship="label"><Button appearance="subtle" disabled={locked} icon={<Color20Regular />} /></Tooltip></MenuTrigger>
          <MenuPopover>
            <div className={s.swatches}>{noteColors.map(c => <button key={c} className={s.swatch} style={{ background: c }} onClick={() => meta({ color: c })} />)}</div>
          </MenuPopover>
        </Menu>
        <Menu>
          <MenuTrigger disableButtonEnhancement><Tooltip content={t('In Ordner verschieben')} relationship="label"><Button appearance="subtle" disabled={locked} icon={<FolderArrowRight20Regular />} /></Tooltip></MenuTrigger>
          <MenuPopover>
            <MenuList>
              {!note.owner && <MenuItem onClick={() => meta({ folder: '' })}>{t('Ohne Ordner')}</MenuItem>}
              {moveFolders.map(f => <MenuItem key={f.syncId} onClick={() => meta({ folder: f.syncId })}>{f.name}</MenuItem>)}
              {!note.owner && sharedTargets().map(x => <MenuItem key={x.id} onClick={() => meta({ folder: x.id })}>{x.label}</MenuItem>)}
            </MenuList>
          </MenuPopover>
        </Menu>
        <Tooltip content={t('Datei anhängen')} relationship="label"><Button appearance="subtle" disabled={locked} icon={<Attach20Regular />} onClick={() => fileInput.current?.click()} /></Tooltip>
        {note.owner ? null : inTrash || note.archived
          ? <Tooltip content={t('Wiederherstellen')} relationship="label"><Button appearance="subtle" icon={<ArrowUndo20Regular />} onClick={() => action('restore')} /></Tooltip>
          : <Tooltip content={t('Archivieren')} relationship="label"><Button appearance="subtle" icon={<Archive20Regular />} onClick={() => action('archive')} /></Tooltip>}
        {inTrash
          ? <Button appearance="subtle" icon={<Delete20Regular />} onClick={destroy}>{t('Endgültig löschen')}</Button>
          : <Tooltip content={t('In den Papierkorb')} relationship="label"><Button appearance="subtle" disabled={locked} icon={<Delete20Regular />} onClick={() => action('trash')} /></Tooltip>}
        <input ref={fileInput} type="file" multiple hidden onChange={e => { upload(e.target.files); e.target.value = '' }} />
      </div>
      <Toolbar size="small" className={s.bar}>
        {tb(t('Fett'), <TextBold20Regular />, 'bold')}
        {tb(t('Kursiv'), <TextItalic20Regular />, 'italic')}
        {tb(t('Unterstrichen'), <TextUnderline20Regular />, 'underline')}
        {tb(t('Durchgestrichen'), <TextStrikethrough20Regular />, 'strikeThrough')}
        {picker(t('Textfarbe'), <TextColor20Regular />, textColors, 'foreColor')}
        {picker(t('Hervorheben'), <Highlight20Regular />, marks, 'hiliteColor')}
        {tb(t('Aufzählung'), <TextBulletListLtr20Regular />, 'insertUnorderedList')}
        {tb(t('Nummerierung'), <TextNumberListLtr20Regular />, 'insertOrderedList')}
        <Tooltip content={t('Kästchen (Aufgabe) – anklicken hakt ab')} relationship="label">
          <Button appearance="subtle" size="small" icon={<CheckboxChecked20Regular />} onMouseDown={e => e.preventDefault()} onClick={toggleCheckbox} disabled={note.readOnly || inTrash} />
        </Tooltip>
        {tb(t('Linksbündig'), <TextAlignLeft20Regular />, 'justifyLeft')}
        {tb(t('Zentriert'), <TextAlignCenter20Regular />, 'justifyCenter')}
        {tb(t('Rechtsbündig'), <TextAlignRight20Regular />, 'justifyRight')}
        {tb(t('Einzug verkleinern'), <TextIndentDecreaseLtr20Regular />, 'outdent')}
        {tb(t('Einzug vergrößern'), <TextIndentIncreaseLtr20Regular />, 'indent')}
        <Tooltip content={t('Codeblock (Strg+Umschalt+C)')} relationship="label">
          <Button appearance="subtle" size="small" icon={<Code20Regular />} onMouseDown={e => e.preventDefault()} onClick={toggleCode} disabled={note.readOnly || inTrash} />
        </Tooltip>
        <span style={{ flex: 1 }} />
        {note.protected.some(Boolean) && (
          <Tooltip content={t('Tabellen und Bilder bearbeitet notizi am PC – hier bleiben sie unverändert.')} relationship="description">
            <Text size={200} style={{ color: tokens.colorNeutralForeground3, display: 'inline-flex', gap: 4, alignItems: 'center' }}><LockClosed16Regular /> {t('gesperrte Blöcke')}</Text>
          </Tooltip>
        )}
      </Toolbar>
      {rejected && (
        <MessageBar intent="error">
          <MessageBarBody>{rejected}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={saveAsCopy}>{t('Als Kopie speichern')}</Button>
            <Button size="small" onClick={discard}>{t('Änderungen verwerfen')}</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {note.owner && (
        <MessageBar intent="info"><MessageBarBody>{locked
          ? t('Geteilt von {name} – du darfst sie nur lesen.', { name: note.owner })
          : t('Geteilt von {name} – Änderungen sind sofort für alle da.', { name: note.owner })}</MessageBarBody></MessageBar>
      )}
      {note.readOnly && !locked && <MessageBar intent="warning"><MessageBarBody>{t('Diese Notiz lässt sich hier nur lesen.')}</MessageBarBody></MessageBar>}
      {inTrash && (
        <MessageBar intent="info"><MessageBarBody>{t('Im Papierkorb.')}</MessageBarBody><MessageBarActions><Button size="small" onClick={() => action('restore')}>{t('Wiederherstellen')}</Button></MessageBarActions></MessageBar>
      )}
      <div className={s.stripe} style={{ backgroundColor: bg }} />
      <div className={`${s.paper} ${isDark ? 'dm-paper-dark' : 'dm-paper-light'}`}>
        <div ref={setRoot} className="dm-editor" contentEditable={!note.readOnly && !inTrash} suppressContentEditableWarning onInput={changed} spellCheck
          onClick={e => {
            // Links: der Ruecklink zur Mail mit einfachem Klick, andere mit Strg+Klick (wie in Word)
            const link = (e.target as HTMLElement).closest('a')
            if (link && (e.ctrlKey || e.metaKey || link.href.includes('#/mail/open/'))) {
              e.preventDefault()
              if (link.href.startsWith(location.origin)) location.href = link.href
              // nur sichere Schemata oeffnen - nie javascript:/data: aus einem Notiz-Link
              else if (/^(https?|mailto):/i.test(link.href)) window.open(link.href, '_blank', 'noopener,noreferrer')
              return
            }
            onEditorClick()
          }}
          onPaste={e => {
            const files = Array.from(e.clipboardData.files)
            if (files.length) { e.preventDefault(); addFiles(files); return }
            const html = e.clipboardData.getData('text/html')
            const text = e.clipboardData.getData('text/plain')
            e.preventDefault()
            if (html) document.execCommand('insertHTML', false, cleanPaste(html) || plainToHtml(text))
            else if (text) document.execCommand(/[\r\n]/.test(text) ? 'insertHTML' : 'insertText', false, /[\r\n]/.test(text) ? plainToHtml(text) : text)
            changed()
          }}
          onDrop={e => {
            // Nichts ungefiltert hineinziehen - Dateien werden Anhaenge, HTML wird bereinigt
            e.preventDefault()
            if (e.dataTransfer.files.length) {
              const r = (document as any).caretRangeFromPoint?.(e.clientX, e.clientY) as Range | undefined
              if (r) { const sel = window.getSelection(); sel?.removeAllRanges(); sel?.addRange(r) }
              addFiles(Array.from(e.dataTransfer.files)); return
            }
            const html = e.dataTransfer.getData('text/html'), text = e.dataTransfer.getData('text/plain')
            if (!html && !text) return
            const r = (document as any).caretRangeFromPoint?.(e.clientX, e.clientY) as Range | undefined
            if (r) { const sel = window.getSelection(); sel?.removeAllRanges(); sel?.addRange(r) }
            document.execCommand('insertHTML', false, html ? cleanPaste(html) || plainToHtml(text) : plainToHtml(text))
            changed()
          }}
          onKeyDown={e => {
            if (e.ctrlKey && e.key === 's') { e.preventDefault(); clearTimeout(timer.current); timer.current = undefined; save() }
            else if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'c') { e.preventDefault(); toggleCode() }
          }} />
      </div>
      {note.attachments.length > 0 && (
        <div className={s.atts}>
          {note.attachments.map(a => (
            <span key={a.name} className={s.att}>
              <a href={`/api/notes/blob/${a.blob}?` + q({ download: true, file: a.name })} style={{ color: 'inherit' }}>{a.name}</a>
              <Text size={200}>({fmtSize(a.size)})</Text>
              <Button size="small" appearance="transparent" icon={<Dismiss16Regular />} aria-label={t('Entfernen')}
                onClick={async () => { if (!confirm(t('„{name}“ entfernen?', { name: a.name }))) return; await api.del(`/api/notes/${id}/attachments/${encodeURIComponent(a.name)}`).catch(fail); load() }} />
            </span>
          ))}
        </div>
      )}
    </div>
  )
}
