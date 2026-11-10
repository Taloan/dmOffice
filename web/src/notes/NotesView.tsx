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
import { sendToChat } from '../chat/SendToChat'
import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { e2eFetch } from '../e2e'
import { api, Collection, featureOn, fmtSize, q } from '../api'
import { OPEN_COLOR, requestTresorOpen, useTresor } from '../tresor/tresor'
import { LockClosed20Regular, LockOpen20Regular, LockOpen16Regular } from '@fluentui/react-icons'
import { EventDialog, EventDraft, guessStart } from '../calendar/EventDialog'
import { useApp } from '../App'
import { useDebounced, fmtDate } from '../shell/hooks'
import { useIsDark } from '../theme'
import { cleanPaste, plainToHtml } from './cleanPaste'
import { RecipeSearchDialog } from './RecipeSearchDialog'
import { RecipeScanDialog } from './RecipeScanDialog'
import { fixContrast } from './contrast'
import { t, tError } from '../i18n'
import { Snippet } from '../shell/Snippet'
import { MenuItemRadio, MenuDivider } from '@fluentui/react-components'
import { NoteAdd20Regular, Database20Regular, PanelLeft20Regular, TextDescription20Regular, Settings20Regular, ArrowLeft20Regular,
  Open20Regular, Rename20Regular, MoreHorizontal16Regular, ArrowSort20Regular, StarOff20Regular, ChevronDown16Regular, ChevronRight16Regular, People20Regular, People16Regular, Chat20Regular, PersonDelete20Regular } from '@fluentui/react-icons'
import { ColorMenu, ContextMenu, TargetMenu, askConfirm, askText, isTyping, useContextMenu, useMultiSelect } from '../shell/context'
import { pressable, useNavStyles } from '../shell/nav'
import { NoteTags, tagLabel } from './NoteTags'
import { Camera20Regular, Cloud20Regular, Desktop20Regular, ImageMultiple20Regular, Link20Regular, Search20Regular, Tag20Regular } from '@fluentui/react-icons'
import { Item, Source, ownSource } from '../files/common'
import { gallerySource } from '../files/GalleryFolders'
import { PickPhotoDialog } from '../files/PickPhotoDialog'
import { StoredFilesDialog } from '../mail/StoredFilesDialog'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { SidePane, useBackClose, useIsMobile } from '../shell/mobile'
import { usePrefs } from '../prefs'
import { ShareDialog } from '../calendar/ShareDialog'
import { usePaneWidth } from '../shell/Splitter'

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
  // Fokus auf der Liste (per Tab oder Pfeil aus dem Suchfeld): sichtbarer Rahmen
  cardsFocus: { ':focus-visible': { boxShadow: `inset 0 0 0 2px ${tokens.colorStrokeFocus2}` } },
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

/** Grundpfad der Notizen: offener Bereich oder eine notizi-Datenbank im Tresor (/api/notes/vault/<Ordner>). Gilt für die ganze Ansicht samt Editor. */
let NB = '/api/notes'


interface NoteListItem { created?: string; image?: string | null; id: number; syncId: string; folder: string | null; title: string; snippet: string; color: string; modified: string; important: boolean; attachments: number; archived: string | null; deleted: string | null
  /** Mit mir geteilt: Name des Besitzers (die Id ist dann negativ) */
  owner?: string | null
  /** Nur in Suchergebnissen: gefunden in diesem Bild/Anhang (Volltextsuche) */
  match?: { name: string; snippet: string } | null
  /** Schlagwoerter (Version 2) */
  tags?: string[] | null }
interface NoteFolder { syncId: string; parent: string | null; name: string; sharedWith?: { id: string; name: string; readOnly?: boolean; level?: string }[] | null }
/** Ein mit mir geteilter Ordner samt Unterordnern (der oberste ohne Eltern) */
interface SharedRoot { share: number; root: string; owner: string; name: string; canWrite: boolean; showOnly?: boolean; folders: NoteFolder[] }
interface NoteDetail {
  id: number; folder: string | null; customTitle: string | null; title: string; color: string; important: boolean; tags?: string[]
  archived: string | null; deleted: string | null; blocks: string[]; protected: boolean[]; readOnly: boolean
  attachments: { name: string; mime: string | null; size: number; blob: string }[]; pending: boolean; modified: string
  /** Fingerabdruck des Inhalts - beim Speichern zurueck, sonst 409 */
  version: string
  /** Mit mir geteilt: Besitzer; canWrite false = nur lesen */
  owner?: string | null; canWrite?: boolean
  /** Nur gezeigt (Version 2): Anhaenge nur mit Namen, kein Herunterladen */
  showOnly?: boolean
  /** Einzeln mit mir geteilt (Version 2): nicht verschieben, nicht in den Papierkorb */
  single?: boolean
  /** Eigene Notiz: mit wie vielen Personen einzeln geteilt */
  sharedWith?: number | null
}

/** Wo die Schreibmarke steht (Block und Zeichen darin) - damit sie einen Neuaufbau des Editors uebersteht. */
function caretOf(el: HTMLElement): { block: number; offset: number } | null {
  const sel = window.getSelection()
  if (!sel?.rangeCount || !el.contains(sel.anchorNode)) return null
  const r = sel.getRangeAt(0)
  let top: Node = r.startContainer
  while (top !== el && top.parentNode !== el) top = top.parentNode!
  if (top === el) return null
  const before = document.createRange()
  before.selectNodeContents(top)
  before.setEnd(r.startContainer, r.startOffset)
  return { block: Array.from(el.childNodes).indexOf(top as ChildNode), offset: before.toString().length }
}
function setCaret(el: HTMLElement, c: { block: number; offset: number }) {
  const top = el.childNodes[Math.min(c.block, el.childNodes.length - 1)]
  if (!top) return
  const walker = document.createTreeWalker(top, NodeFilter.SHOW_TEXT)
  let left = c.offset, node = walker.nextNode(), last: Node | null = null
  while (node && left > (node.textContent?.length ?? 0)) { left -= node.textContent?.length ?? 0; last = node; node = walker.nextNode() }
  const r = document.createRange()
  if (node) r.setStart(node, left); else if (last) r.setStart(last, last.textContent?.length ?? 0); else r.setStart(top, 0)
  r.collapse(true)
  const sel = window.getSelection()
  sel?.removeAllRanges(); sel?.addRange(r)
}

// single: einzeln mit mir geteilte Notizen (Version 2)
type View = { kind: 'folder'; id: string | null } | { kind: 'tag'; tag: string } | { kind: 'all' | 'important' | 'archive' | 'trash' | 'single' }
/** Eine einzeln mit mir geteilte Notiz (Version 2) */
interface SingleShared { id: number; share: number; title: string; snippet: string; color: string; modified: string; owner: string; canWrite: boolean; showOnly: boolean; image: string | null; attachments: number }

export default function NotesView() {
  const s = useStyles()
  const listW = usePaneWidth('notes.list', 300, 220, 720)
  const { fail, notify } = useApp()
  const [status, setStatus] = useState<any>(null)
  const [config, setConfig] = useState<any>(null)
  const [folders, setFolders] = useState<NoteFolder[]>([])
  // Schlagwoerter aller eigenen Notizen mit Anzahl (Seitenleiste, Vorschlaege)
  const [allTags, setAllTags] = useState<{ tag: string; count: number }[]>([])
  const loadTags = () => api.get<{ tag: string; count: number }[]>(NB + '/tags').then(setAllTags).catch(() => {})
  const [shared, setShared] = useState<SharedRoot[]>([])
  const [shareDialog, setShareDialog] = useState<NoteFolder | null>(null)
  const [singles, setSingles] = useState<SingleShared[]>([])
  const [shareNote, setShareNote] = useState<{ id: number; title: string } | null>(null)
  const [view, setView] = useState<View>({ kind: 'all' })
  const [notes, setNotes] = useState<{ items: NoteListItem[]; archived: number; trashed: number } | null>(null)
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<number | null>(null)
  // Nach Archivieren, Löschen, Verschieben: gleich die nächste Notiz der Liste öffnen (die letzte: die davor).
  // Am Handy zurück zur Liste – dort sind Liste und Notiz getrennte Seiten.
  const listRef = useRef<NoteListItem[]>([])
  const mobileRef = useRef(false)
  const nextOf = (cur: number | null, gone: number[]) => {
    const list = listRef.current, i = list.findIndex(n => n.id === cur)
    if (mobileRef.current || i < 0) return null
    return list.slice(i + 1).find(n => !gone.includes(n.id))?.id ?? list.slice(0, i).reverse().find(n => !gone.includes(n.id))?.id ?? null
  }
  // Datenbanken unter der Sync-Wurzel (wie die Auswahl "Datenbank" in notizi)
  const [dbs, setDbs] = useState<{ current: string; list: { folder: string; title: string; passwordFits: boolean }[] | null
    tresor?: { folder: string; name: string | null }[]; tresorOpen?: boolean; tresorExists?: boolean } | null>(null)
  // notizi-Datenbank im Tresor, die gerade gezeigt wird (Ordnername) - null: die gewählte im offenen Bereich
  const [vault, setVault] = useState<string | null>(null)
  NB = vault ? `/api/notes/vault/${encodeURIComponent(vault)}` : '/api/notes'
  useEffect(() => () => { NB = '/api/notes' }, [])
  const tresorSt = useTresor()
  const vaultName = vault ? dbs?.tresor?.find(d => d.folder === vault)?.name ?? vault : null
  const [switching, setSwitching] = useState(false)
  const loadDbs = () => api.get('/api/notes/databases').then(setDbs).catch(() => {})
  useEffect(() => { loadDbs() }, [tresorSt?.open])
  // Tresor geschlossen (auch auf einem anderen Weg): zurück in den offenen Bereich, nichts bleibt stehen
  useEffect(() => { if (vault && tresorSt && !tresorSt.open) { setVault(null); setSelected(null) } }, [tresorSt, vault])
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
    if (vault) { NB = '/api/notes'; setVault(null) }
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
    api.get<NoteFolder[]>(NB + '/folders').then(setFolders).catch(() => {}),
    // Im Tresor gibt es keine Freigaben
    vault ? setShared([]) : api.get<SharedRoot[]>('/api/notes/shared').then(setShared).catch(() => {}),
    vault ? setSingles([]) : api.get<SingleShared[]>('/api/notes/single-shared').then(setSingles).catch(() => {}),
    loadTags(),
  ])
  /** Der mit mir geteilte Ordner, zu dem dieser Ordner gehoert. */
  const shareOf = (id: string | null | undefined) => (id ? shared.find(x => x.folders.some(f => f.syncId === id)) : undefined)
  /** Mit mir geteilte Ordner, in die ich schreiben darf - Ziele fuer eigene Notizen (sie werden dorthin kopiert). */
  const sharedTargets = () => shared.filter(x => x.canWrite).flatMap(sh => folderTargets([], sh.folders)
    .map(x => (x.id === sh.root ? { ...x, label: t('{name} (von {owner})', { name: x.label, owner: sh.owner }) } : x)))
  const loadNotes = useCallback(() => {
    // Einzeln mit mir geteilte Notizen: eigene Liste, Zaehler fuer Archiv und Papierkorb bleiben stehen
    if (!query && view.kind === 'single')
      return api.get<SingleShared[]>('/api/notes/single-shared').then(list => {
        setSingles(list)
        setNotes(old => ({ archived: old?.archived ?? 0, trashed: old?.trashed ?? 0, items: list.map(x => ({
          id: x.id, syncId: '', folder: null, title: x.title, snippet: x.snippet, color: x.color, modified: x.modified, created: x.modified,
          important: false, attachments: x.attachments, archived: null, deleted: null, image: x.image, owner: x.owner })) }))
      }).catch(fail)
    const params = query ? { q: query } : view.kind === 'folder' ? { folder: view.id ?? '' } : view.kind === 'tag' ? { view: 'tag', tag: view.tag } : { view: view.kind }
    return api.get(NB + '?' + q(params as any)).then(setNotes).catch(fail)
  }, [view, query, fail, vault]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    api.get('/api/settings/notizi').then(setConfig).catch(() => setConfig({}))
    api.get(NB + '/status').then(setStatus).catch(() => {})
    loadFolders()
  }, [vault]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { loadNotes() }, [loadNotes])
  // Stabil fuer den Editor
  const onChanged = useCallback(() => { loadNotes(); loadTags() }, [loadNotes])
  const onGone = useCallback(() => { setSelected(cur => nextOf(cur, cur != null ? [cur] : [])); loadNotes() }, [loadNotes]) // eslint-disable-line react-hooks/exhaustive-deps
  const onOpen = useCallback(async (nid: number) => { await loadNotes(); setSelected(nid) }, [loadNotes])
  // Was von anderen Geraeten kommt, alle 30 Sekunden nachladen
  useEffect(() => {
    const iv = setInterval(() => { if (!document.hidden) { loadNotes(); api.get(NB + '/status').then(setStatus).catch(() => {}) } }, 30000)
    return () => clearInterval(iv)
  }, [loadNotes])

  const sync = async () => {
    try { setStatus(await api.post(NB + '/sync')); loadNotes(); loadFolders() } catch (e) { fail(e) }
  }

  const curShare = view.kind === 'folder' ? shareOf(view.id) : undefined
  const canCreate = !curShare || curShare.canWrite
  const creating = useRef(false)
  const create = async () => {
    if (!canCreate || creating.current) return
    creating.current = true
    // Weiter getippte Zeichen sollen nicht den Knopf auslösen, bis der Text bereit ist
    if (document.activeElement instanceof HTMLElement) document.activeElement.blur()
    try {
      const r = await api.post(NB, { folder: view.kind === 'folder' ? view.id : null })
      setSelected(r.id)
      setTypeInId(r.id)
      void loadNotes()
    } catch (e) { fail(e) } finally { creating.current = false }
  }

  // ---------------- notizi-Datenbanken im Tresor ----------------
  const openVault = async (folder: string) => {
    if (!await requestTresorOpen()) return
    setSelected(null); setSearch(''); setView({ kind: 'all' }); setVault(folder)
    if (mobileRef.current) closeSide()
  }
  const toTresor = async (d: { folder: string; title: string }) => {
    if (!await askConfirm(t('In den Tresor verschieben'), t('„{name}“ in den Tresor verschieben? Die Notizen sind danach nur noch bei geöffnetem Tresor zu sehen. Andere Geräte erhalten Änderungen erst, wenn der Tresor in dmOffice geöffnet ist.', { name: d.title }), t('Verschieben'))) return
    if (!await requestTresorOpen()) return
    setSwitching(true); setSelected(null)
    try {
      await api.post(`/api/notes/databases/${encodeURIComponent(d.folder)}/tresor`)
      await loadDbs()
      setView({ kind: 'all' }); setVault(d.folder)
      notify(t('In den Tresor verschoben'))
    } catch (e) { fail(e) } finally { setSwitching(false) }
  }
  const fromTresor = async (folder: string) => {
    const name = dbs?.tresor?.find(d => d.folder === folder)?.name ?? folder
    if (!await askConfirm(t('Aus dem Tresor holen'), t('„{name}“ aus dem Tresor holen? Die Notizen liegen danach wieder im offenen Bereich.', { name }), t('Holen'))) return
    if (!await requestTresorOpen()) return
    setSwitching(true)
    try {
      await api.post(`/api/notes/databases/${encodeURIComponent(folder)}/out`)
      if (vault === folder) { setSelected(null); setVault(null) }
      await loadDbs()
      notify(t('Aus dem Tresor geholt'))
    } catch (e) { fail(e) } finally { setSwitching(false) }
  }
  const tresorRow = async () => {
    if (!tresorSt?.open) { if (await requestTresorOpen()) loadDbs(); return }
    if (!dbs?.tresor?.length) notify(t('Im Tresor liegt noch keine notizi-Datenbank. Verschieben geht über „Datenbank“ → „In den Tresor verschieben“.'))
  }

  // Rezept per Link: der Server liest das schema.org-Rezept der Seite und legt eine Notiz in der Rezeptvorlage an
  const [importing, setImporting] = useState(false)
  const [recipeSearch, setRecipeSearch] = useState(false)
  const [recipeScan, setRecipeScan] = useState(false)
  const importRecipe = async () => {
    if (!canCreate) return
    const url = await askText(t('Rezept aus Link'), '', t('Adresse der Rezeptseite'), t('Übernehmen'))
    if (!url?.trim()) return
    setImporting(true)
    try {
      const r = await api.post<{ id: number }>('/api/notes/recipes/import', { url: url.trim(), folder: view.kind === 'folder' ? view.id : null })
      await loadNotes()
      setSelected(r.id)
      notify(t('Rezept übernommen'))
    } catch (e) { fail(e) } finally { setImporting(false) }
  }

  const newFolder = async (parent: string | null = null) => {
    const name = await askText(parent ? t('Neuer Unterordner') : t('Neuer Ordner'), '', t('Name des Ordners'), t('Anlegen'))
    if (!name) return
    try { await api.post(NB + '/folders', { name, parent }); loadFolders() } catch (e) { fail(e) }
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
  listRef.current = items
  const mx = useMultiSelect<number>(useMemo(() => items.map(n => n.id), [items]))
  const picked = items.filter(n => mx.selected.includes(n.id))
  useEffect(() => { mx.clear() }, [view, query]) // eslint-disable-line react-hooks/exhaustive-deps
  type Ctx = { kind: 'note'; ids: number[] } | { kind: 'folder'; f: NoteFolder; sh?: SharedRoot } | { kind: 'side' } | { kind: 'list' } | { kind: 'vault'; folder: string }
  const cm = useContextMenu<Ctx>()
  const cardsRef = useRef<HTMLDivElement>(null)
  // Geaenderte Notiz offen: Editor neu laden (Farbe, Ordner ...)
  const [editorRev, setEditorRev] = useState(0)
  // F2 / Umbenennen: Titel dieser Notiz fokussieren, sobald sie offen ist
  const [renameId, setRenameId] = useState<number | null>(null)
  // Neue Notiz, Enter auf einer Karte: gleich in den Text
  const [typeInId, setTypeInId] = useState<number | null>(null)
  const mobile = useIsMobile()
  mobileRef.current = mobile
  const [closed, setClosed] = useState<string[]>([])
  const inTrash = view.kind === 'trash'
  const inArchive = view.kind === 'archive'
  const afterChange = async (ids: number[], gone: boolean) => {
    if (selected != null && ids.includes(selected)) { if (gone) setSelected(nextOf(selected, ids)); else setEditorRev(r => r + 1) }
    mx.clear()
    await loadNotes()
  }
  const metaMany = async (ids: number[], body: any) => {
    try { for (const id of ids) await api.put(`${NB}/${id}/meta`, body) } catch (e) { fail(e) }
    // Weg aus der Liste: im Ordner verschoben, oder in einen geteilten Ordner (dort unter neuer Kennung)
    afterChange(ids, body.folder !== undefined && (view.kind === 'folder' || !!shareOf(body.folder)))
  }
  // Termin aus einer Notiz: Titel und Text uebernehmen, ein Datum im Text als Vorschlag
  const [eventDraft, setEventDraft] = useState<{ draft: EventDraft; calendars: Collection[] } | null>(null)
  const eventFrom = async (id: number) => {
    try {
      const [n, cals] = await Promise.all([api.get<NoteDetail>(`${NB}/${id}`), api.get<Collection[]>('/api/pim/collections/calendars')])
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
    try { for (const id of ids) await api.post(`${NB}/${id}/${a}`) } catch (e) { fail(e) }
    afterChange(ids, true)
  }
  const destroyMany = async (ids: number[]) => {
    if (!await askConfirm('', ids.length === 1 ? t('Endgültig löschen? Auch auf allen anderen Geräten.') : t('{n} Notizen endgültig löschen? Auch auf allen anderen Geräten.', { n: ids.length }))) return
    try { for (const id of ids) await api.del(`${NB}/${id}`) } catch (e) { fail(e) }
    afterChange(ids, true)
  }
  const removeMany = (ids: number[]) => { if (ids.length) (inTrash ? destroyMany(ids) : actionMany(ids, 'trash')) }
  // Menüband (wie bei Mail und Aufgaben): wirkt auf die markierten Notizen, sonst auf die offene
  const target = picked.length ? picked : items.filter(n => n.id === selected)
  const targetIds = target.map(n => n.id)
  const targetTheirs = target.some(n => n.id < 0)
  const targetImportant = target.length > 0 && target.every(n => n.important)
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
    else if (k === 'Enter' && ids.length) { e.preventDefault(); setSelected(ids[0]); setTypeInId(ids[0]) }
    else if (k === 'F2' && ids.length === 1) { e.preventDefault(); setSelected(ids[0]); setRenameId(ids[0]) }
    else if (k === 'ArrowDown' || k === 'ArrowUp') { e.preventDefault(); mx.step(k === 'ArrowDown' ? 1 : -1, e.shiftKey) }
  }
  // Per Pfeil gewaehlt: die Karte bleibt im sichtbaren Bereich
  useEffect(() => {
    const id = mx.selected[mx.selected.length - 1]
    if (id != null) cardsRef.current?.querySelector(`[data-note="${id}"]`)?.scrollIntoView({ block: 'nearest' })
  }, [mx.selected])
  const renameFolder = async (f: NoteFolder) => {
    const name = await askText(t('Umbenennen'), f.name)
    if (!name || name === f.name) return
    try { await api.put(`${NB}/folders/${f.syncId}`, { name }); loadFolders() } catch (err) { fail(err) }
  }
  const deleteFolder = async (f: NoteFolder) => {
    if (!await askConfirm('', t('Ordner „{name}“ löschen?', { name: f.name }))) return
    try {
      await api.del(`${NB}/folders/${f.syncId}`)
      if (view.kind === 'folder' && view.id === f.syncId) setView({ kind: 'all' })
      loadFolders()
    } catch (err) { fail(err) }
  }
  const leaveShare = async (sh: SharedRoot) => {
    if (!await askConfirm('', t('„{name}“ nicht mehr anzeigen? Der Besitzer kann den Ordner erneut teilen.', { name: sh.name }))) return
    try {
      await api.post(`/api/notes/shares/${sh.share}/leave`)
      if (view.kind === 'folder' && sh.folders.some(f => f.syncId === view.id)) setView({ kind: 'all' })
      loadFolders()
    } catch (err) { fail(err) }
  }
  const moveFolder = async (id: string, parent: string | null) => {
    try { await api.post(`${NB}/folders/${id}/move`, { parent }); loadFolders() } catch (err) { fail(err) }
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
    if (x.kind === 'vault') return <>
      <MenuItem icon={<Open20Regular />} onClick={() => openVault(x.folder)}>{t('Öffnen')}</MenuItem>
      <MenuItem icon={<LockOpen20Regular />} onClick={() => fromTresor(x.folder)}>{t('Aus dem Tresor holen')}</MenuItem>
    </>
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
          try { const r = await api.post(NB, { folder: f.syncId }); setView({ kind: 'folder', id: f.syncId }); setSearch(''); setSelected(r.id) } catch (err) { fail(err) }
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
          try { const r = await api.post(NB, { folder: f.syncId }); setView({ kind: 'folder', id: f.syncId }); setSearch(''); setSelected(r.id) } catch (err) { fail(err) }
        }}>{t('Neue Notiz')}</MenuItem>
        <MenuItem icon={<FolderAdd20Regular />} onClick={() => newFolder(f.syncId)}>{t('Neuer Unterordner')}</MenuItem>
        {!vault && <MenuItem icon={<People20Regular />} onClick={() => setShareDialog(f)}>{t('Teilen …')}</MenuItem>}
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
        {one && singleOf(one.id) && <MenuItem icon={<PersonDelete20Regular />} onClick={() => leaveSingle(singleOf(one.id)!)}>{t('Nicht mehr anzeigen')}</MenuItem>}
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
        {one && !vault && <MenuItem icon={<CalendarAdd20Regular />} onClick={() => eventFrom(one.id)}>{t('Termin erstellen')}</MenuItem>}
        {ids.every(id => id > 0) && !vault && <MenuItem icon={<Chat20Regular />} onClick={() => sendToChat(ids.map(id => ({ type: 'note', noteId: id })))}>{t('Im Chat …')}</MenuItem>}
        {one && one.id > 0 && !vault && <MenuItem icon={<People20Regular />} onClick={() => setShareNote({ id: one.id, title: one.title })}>{t('Teilen …')}</MenuItem>}
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


  /** Einzeln mit mir geteilte Notiz zu einer Kennung (Version 2) */
  const singleOf = (id: number) => singles.find(x => x.id === id)
  const leaveSingle = async (x: SingleShared) => {
    try {
      await api.post(`/api/notes/single-shared/${x.share}/leave`)
      if (selected === x.id) setSelected(null)
      await loadFolders(); loadNotes()
    } catch (e) { fail(e) }
  }

  const nav = (v: View, icon: JSX.Element, label: string, n?: number) => {
    const key = v.kind === 'folder' ? 'nofolder' : v.kind === 'tag' ? 'tag:' + v.tag : v.kind
    // Notizen ablegen: ohne Ordner, wichtig, archivieren, in den Papierkorb
    const drop = v.kind === 'folder' ? { notes: (ids: number[]) => metaMany(ids, { folder: '' }), folder: (id: string) => moveFolder(id, null) }
      : v.kind === 'important' ? { notes: (ids: number[]) => metaMany(ids, { important: true }) }
      : v.kind === 'archive' ? { notes: (ids: number[]) => actionMany(ids, 'archive') }
      : v.kind === 'trash' ? { notes: (ids: number[]) => actionMany(ids, 'trash') }
      : v.kind === 'all' ? { folder: (id: string) => moveFolder(id, null) } : {}
    return (
      <div className={mergeClasses(nv.row, view.kind === v.kind && (v.kind !== 'folder' || (view as any).id === (v as any).id) && (v.kind !== 'tag' || (view as any).tag === v.tag) && nv.active, dropOn === key && nv.drop)}
        onClick={() => { setView(v); setSearch('') }} {...pressable(() => { setView(v); setSearch('') })} {...dropTarget(key, drop)}>
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
          <RibbonButton icon={<Link20Regular />} label={importing ? t('Lädt …') : t('Rezept aus Link')} onClick={importRecipe} disabled={switching || !canCreate || importing || !!vault} />
          <RibbonButton icon={<Camera20Regular />} label={t('Rezept aus Foto')} onClick={() => setRecipeScan(true)} disabled={switching || !canCreate || !!vault} />
          <RibbonButton icon={<Search20Regular />} label={t('Rezepte suchen')} onClick={() => setRecipeSearch(true)} disabled={switching || !!vault} />
          <RibbonDivider />
          <RibbonButton icon={<FolderAdd20Regular />} label={t('Neuer Ordner')} onClick={() => newFolder()} />
          <RibbonDivider />
          <RibbonButton icon={targetImportant ? <Star20Filled color={tokens.colorPaletteYellowForeground1} /> : <Star20Regular />} label={t('Wichtig')}
            pressed={targetImportant} disabled={!target.length || inTrash} onClick={() => metaMany(targetIds, { important: !targetImportant })} />
          <Menu>
            <MenuTrigger disableButtonEnhancement><RibbonButton icon={<Color20Regular />} label={t('Farbe')} disabled={!target.length || inTrash} /></MenuTrigger>
            <MenuPopover>
              <div className={s.swatches}>{noteColors.map(c => <button key={c} className={s.swatch} style={{ background: c }} aria-label={c} onClick={() => metaMany(targetIds, { color: c })} />)}</div>
            </MenuPopover>
          </Menu>
          <Menu>
            <MenuTrigger disableButtonEnhancement><RibbonButton icon={<FolderArrowRight20Regular />} label={t('Verschieben')} disabled={!target.length || inTrash || targetTheirs} /></MenuTrigger>
            <MenuPopover>
              <MenuList>
                {[{ id: '', label: t('Ohne Ordner') }, ...folderTargets(), ...sharedTargets()].map(x => <MenuItem key={String(x.id)} onClick={() => metaMany(targetIds, { folder: String(x.id) })}>{x.label}</MenuItem>)}
              </MenuList>
            </MenuPopover>
          </Menu>
          {inTrash || inArchive
            ? <RibbonButton icon={<ArrowUndo20Regular />} label={t('Wiederherstellen')} disabled={!target.length || targetTheirs} onClick={() => actionMany(targetIds, 'restore')} />
            : <RibbonButton icon={<Archive20Regular />} label={t('Archivieren')} disabled={!target.length || targetTheirs} onClick={() => actionMany(targetIds, 'archive')} />}
          <RibbonButton icon={<Delete20Regular />} label={inTrash ? t('Endgültig löschen') : t('Löschen')} disabled={!target.length} onClick={() => removeMany(targetIds)} />
          <RibbonDivider />
          {dbs?.list && (
            <Menu>
              <MenuTrigger disableButtonEnhancement>
                {/* Der Knopf zeigt, welche Datenbank offen ist - ein Klick wechselt */}
                <RibbonButton icon={vault ? <LockOpen20Regular style={{ color: OPEN_COLOR }} /> : <Database20Regular />} disabled={switching}
                  label={switching ? t('Wechsle …') : vaultName ?? dbs.list.find(d => d.folder === dbs.current)?.title ?? t('Datenbank')}
                  title={t('Datenbank wechseln')} />
              </MenuTrigger>
              <MenuPopover>
                <MenuList checkedValues={{ db: [vault ? '' : dbs.current] }} onCheckedValueChange={(_, d) => d.checkedItems[0] && switchDb(d.checkedItems[0])}>
                  {dbs.list.map(d => (
                    <MenuItemRadio key={d.folder} name="db" value={d.folder} disabled={!d.passwordFits}>
                      {d.title}{!d.passwordFits && ' ' + t('(anderes Passwort)')}
                    </MenuItemRadio>
                  ))}
                  <MenuDivider />
                  <MenuItem onClick={() => switchDb('__new')}>{t('Neue Datenbank …')}</MenuItem>
                  {featureOn('vault') && dbs.tresorExists && <>
                    <MenuDivider />
                    {(dbs.tresor ?? []).map(d => (
                      <MenuItem key={'t:' + d.folder} icon={dbs.tresorOpen ? <LockOpen20Regular style={{ color: OPEN_COLOR }} /> : <LockClosed20Regular />}
                        onClick={() => openVault(d.folder)}>{d.name ?? d.folder}</MenuItem>
                    ))}
                    <Menu>
                      <MenuTrigger disableButtonEnhancement><MenuItem>{t('In den Tresor verschieben')}</MenuItem></MenuTrigger>
                      <MenuPopover><MenuList>
                        {dbs.list.filter(d => d.passwordFits).length === 0 && <MenuItem disabled>{t('Keine Datenbank im offenen Bereich')}</MenuItem>}
                        {dbs.list.filter(d => d.passwordFits).map(d => <MenuItem key={d.folder} onClick={() => toTresor(d)}>{d.title}</MenuItem>)}
                      </MenuList></MenuPopover>
                    </Menu>
                    {(dbs.tresor ?? []).length > 0 && <Menu>
                      <MenuTrigger disableButtonEnhancement><MenuItem>{t('Aus dem Tresor holen')}</MenuItem></MenuTrigger>
                      <MenuPopover><MenuList>
                        {(dbs.tresor ?? []).map(d => <MenuItem key={d.folder} onClick={() => fromTresor(d.folder)}>{d.name ?? d.folder}</MenuItem>)}
                      </MenuList></MenuPopover>
                    </Menu>}
                  </>}
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          {/* Abgleich mit notizi: erst nach der Einrichtung, vorher täte der Knopf nichts */}
          {config?.enabled && <RibbonButton icon={<ArrowSync20Regular />} label={t('Abgleichen')} onClick={sync} disabled={status?.running} />}
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
      <SidePane as="aside" className={mergeClasses(s.side, nv.pane)} open={sidePane} onClose={closeSide} width={{ key: 'notes.side', def: 220 }}>
        <div style={{ display: 'flex', flexDirection: 'column', flex: 1 }} onContextMenu={e => cm.open(e, { kind: 'side' })}>
        {vault && (
          <div className={nv.section} style={{ gap: 4 }}>
            <LockOpen16Regular style={{ color: OPEN_COLOR, flexShrink: 0 }} />
            <span style={{ flex: 1, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{t('Im Tresor: {name}', { name: vaultName ?? '' })}</span>
            <Tooltip content={t('Zurück zu den Notizen')} relationship="label">
              <Button appearance="subtle" size="small" icon={<ArrowLeft20Regular />} onClick={() => { setSelected(null); setView({ kind: 'all' }); setVault(null) }} />
            </Tooltip>
          </div>
        )}
        {nav({ kind: 'all' }, <Note20Regular />, t('Alle Notizen'))}
        {nav({ kind: 'folder', id: null }, <Note20Regular />, t('Ohne Ordner'))}
        {nav({ kind: 'important' }, <Star20Regular />, t('Wichtig'))}
        <div className={nv.section}>
          <span style={{ flex: 1 }}>{t('Ordner')}</span>
          <Tooltip content={t('Neuer Ordner')} relationship="label"><Button appearance="subtle" size="small" icon={<FolderAdd20Regular />} onClick={() => newFolder()} /></Tooltip>
        </div>
        {tree(null, 0)}
        {allTags.length > 0 && <>
          <div className={nv.section}><span style={{ flex: 1 }}>{t('Schlagwörter')}</span></div>
          {allTags.map(x => <div key={x.tag}>{nav({ kind: 'tag', tag: x.tag }, <Tag20Regular />, tagLabel(x.tag), x.count)}</div>)}
        </>}
        {(shared.length > 0 || singles.length > 0) && <>
          <div className={nv.section}><span style={{ flex: 1 }}>{t('Mit mir geteilt')}</span></div>
          {shared.flatMap(sh => tree(null, 0, sh.folders, sh))}
          {singles.length > 0 && nav({ kind: 'single' }, <People20Regular />, t('Einzelne Notizen'), singles.length)}
        </>}
        <div style={{ flex: 1, minHeight: 16 }} />
        {nav({ kind: 'archive' }, <Archive20Regular />, t('Archiv'), notes?.archived)}
        {nav({ kind: 'trash' }, <Delete20Regular />, t('Papierkorb'), notes?.trashed)}
        {tresorSt && featureOn('vault') && <>
          <div className={nv.row} onClick={tresorRow} {...pressable(tresorRow)}
            title={tresorSt.open ? t('Tresor – offen') : tresorSt.exists ? t('Tresor – geschlossen') : t('Tresor')}>
            {tresorSt.open ? <LockOpen20Regular style={{ color: OPEN_COLOR }} /> : <LockClosed20Regular />}<span className={nv.name}>{t('Tresor')}</span>
          </div>
          {tresorSt.open && (dbs?.tresor ?? []).map(d => (
            <div key={d.folder} className={mergeClasses(nv.row, vault === d.folder && nv.active)} style={{ paddingLeft: 28 }}
              onClick={() => openVault(d.folder)} {...pressable(() => openVault(d.folder))} onContextMenu={e => { e.stopPropagation(); cm.open(e, { kind: 'vault', folder: d.folder }) }}>
              <Database20Regular /><span className={nv.name}>{d.name ?? d.folder}</span>
            </div>
          ))}
        </>}
        {config?.enabled && <div className={nv.row} onClick={sync} {...pressable(sync)} title={status?.error ?? status?.report ?? ''}>
          <ArrowSync20Regular />
          <span style={{ fontSize: 12 }}>
            {status?.running ? t('Gleicht ab …') : status?.error ? <span style={{ color: tokens.colorPaletteRedForeground1 }}>{t('Abgleich: Fehler')}</span>
              : status?.lastRun ? t('Abgeglichen {date}', { date: fmtDate(status.lastRun) }) : t('Abgleichen')}
          </span>
        </div>}
        </div>
      </SidePane>
      <div className={s.column}>
      <div className={s.panes}>
      <div className={s.list} style={listW.width ? { width: listW.width, position: 'relative' } : undefined}>
        {listW.handle('right', true)}
        {config && !config.enabled && !prefs.notiziTipHidden && (
          <MessageBar intent="info" style={{ margin: '10px 10px 0' }}>
            <MessageBarBody>
              {t('Tipp: Mit der App notizi hast du deine Notizen auch auf dem Windows-Desktop – mit Erinnerungen, Texterkennung und Abgleich.')}{' '}
              <a href="#/settings/notes">{t('Abgleich einrichten')}</a>
            </MessageBarBody>
            <MessageBarActions containerAction={<Button appearance="transparent" size="small" icon={<Dismiss16Regular />} aria-label={t('Ausblenden')}
              onClick={() => update({ notiziTipHidden: true })} />} />
          </MessageBar>
        )}
        <div style={{ padding: 10 }}><SearchBox placeholder={t('Notizen durchsuchen')} value={search} onChange={(_, d) => setSearch(d.value)} style={{ width: '100%' }}
          onKeyDown={e => { if (e.key === 'ArrowDown') { e.preventDefault(); cardsRef.current?.focus(); mx.step(1, false) } }} /></div>
        <div className={mergeClasses(s.cards, s.cardsFocus)} ref={cardsRef} tabIndex={0} role="listbox" aria-multiselectable aria-label={t('Notizen')} onKeyDown={onCardsKey} onContextMenu={e => cm.open(e, { kind: 'list' })}
          onClick={e => { if (e.target === e.currentTarget) mx.clear() }}>
          {!notes ? <Spinner /> : items.length === 0 ? <Text style={{ padding: 8, color: tokens.colorNeutralForeground3 }}>{t('Keine Notizen.')}</Text>
            : items.map(n => (
              <div key={n.id} data-note={n.id} role="option" aria-selected={mx.selected.includes(n.id) || selected === n.id} className={mergeClasses(s.card, !preview && s.cardShort, mx.selected.includes(n.id) && s.cardMulti, selected === n.id && s.cardSel)}
                onClick={e => clickCard(e, n.id)} onContextMenu={e => cardMenu(e, n.id)} draggable={!mobile}
                onDragStart={e => { e.dataTransfer.setData(DRAG_NOTES, JSON.stringify(mx.selected.includes(n.id) ? mx.selected : [n.id])); e.dataTransfer.effectAllowed = 'move' }}
                style={{ borderTopColor: n.color || '#FFF9A8', paddingRight: n.image ? 84 : undefined }}>
                {n.image && <img className={s.cardThumb} src={`${NB}/blob/${encodeURIComponent(n.image)}?thumb=160`} alt="" loading="lazy" draggable={false}
                  onError={e => { (e.currentTarget as HTMLImageElement).style.display = 'none' }} />}
                <div className={s.cardTitle}>{n.important && '★ '}{n.title}</div>
                {n.match ? <div className={s.cardText}>📎 {t('im Anhang {name}', { name: n.match.name })}: <Snippet text={n.match.snippet} /></div>
                  : preview && n.snippet && <div className={s.cardText}>{n.snippet}</div>}
                <div className={s.cardDate}>{fmtDate(n.modified)}{n.attachments > 0 && ` · 📎 ${n.attachments}`}{n.owner && ` · ${t('von {name}', { name: n.owner })}`}{n.tags?.length ? ' · ' + n.tags.map(x => '#' + tagLabel(x)).join(' ') : ''}</div>
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
          <NoteEditor key={`${selected}-${editorRev}`} id={selected} folders={folders} shared={shared} sharedTargets={sharedTargets} focusTitle={renameId === selected} onTitleFocused={() => setRenameId(null)} focusText={typeInId === selected} onTextFocused={() => setTypeInId(null)}
            onChanged={onChanged} onGone={onGone} onOpen={onOpen} notify={notify} fail={fail} onBack={() => setSelected(null)} knownTags={allTags.map(x => x.tag)} />
        </div>
      ) : (
        <div className={mergeClasses(s.empty, s.emptyPane)}><Text>{t('Wähle eine Notiz aus oder lege eine neue an.')}</Text></div>
      )}
      </div>
      </div>
      <ContextMenu at={cm.ctx?.at} onClose={cm.close}>{ctxMenu()}</ContextMenu>
      {shareDialog && <ShareDialog notesFolder={shareDialog.syncId} list={{ id: 0, name: shareDialog.name, components: [], sharedWith: shareDialog.sharedWith }}
        onClose={changed => { setShareDialog(null); if (changed) loadFolders() }} />}
      {recipeScan && <RecipeScanDialog folder={view.kind === 'folder' ? view.id : null}
        onClose={async id => { setRecipeScan(false); if (id != null) { await loadNotes(); setSelected(id); notify(t('Rezept übernommen')) } }} />}
      {recipeSearch && <RecipeSearchDialog folder={view.kind === 'folder' ? view.id : null}
        dbName={dbs?.list?.find(d => d.folder === dbs.current)?.title ?? null}
        targets={[...folderTargets(), ...shared.filter(r => r.canWrite && !r.showOnly).flatMap(r => r.folders.map(f => ({ id: f.syncId, label: `${f.name} (${r.owner})`, depth: 0 })))]}
        onClose={n => { setRecipeSearch(false); if (n) loadNotes() }} />}
      {shareNote && <ShareDialog noteId={shareNote.id} list={{ id: 0, name: shareNote.title, components: [], sharedWith: null }}
        onClose={changed => { setShareNote(null); if (changed) loadNotes() }} />}
      {eventDraft && (
        <EventDialog draft={eventDraft.draft} calendars={eventDraft.calendars}
          onClose={saved => { setEventDraft(null); if (saved) notify(t('Termin angelegt')) }} />
      )}
      </div>
    </div>
  )
}

function NoteEditor({ id, folders, shared, sharedTargets, onChanged, onGone, onOpen, notify, fail, onBack, focusTitle, onTitleFocused, focusText, onTextFocused, knownTags }: {
  knownTags: string[]; focusText?: boolean; onTextFocused?: () => void
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
  // Abgelehnt, weil es inzwischen einen neueren Stand gibt (nicht wegen fehlender Verbindung oder Rechte)
  const [conflict, setConflict] = useState(false)
  const conflictRef = useRef(false)
  conflictRef.current = conflict
  // Speichern unterwegs; Zaehler, damit eine vor dem Speichern gestellte Abfrage danach nicht mehr zaehlt
  const inFlight = useRef(false)
  const saveRun = useRef(0)
  // Der Editor steht noch (false: die Person ist schon bei einer anderen Notiz)
  const alive = useRef(true)
  // „Beides behalten“: diese Bloecke kommen unter den neu geladenen Stand
  const pendingAdd = useRef<string[] | null>(null)
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
      const n = await api.get<NoteDetail>(`${NB}/${id}`)
      setNote(n)
      // Einen gerade getippten Titel nicht ueberschreiben
      if (document.activeElement !== titleRef.current) setTitle(n.customTitle ?? '')
      if (force || version.current === undefined || (n.version !== version.current && !dirty.current)) {
        version.current = n.version
        dirty.current = false
        setRev(r => r + 1)
      }
      return true
    } catch (e) { fail(e); return false }
  }, [id, fail])
  useEffect(() => { load() }, [load])
  // Geteilt (mit mir oder von mir): alle paar Sekunden schauen, ob jemand anderes geändert hat; sonst seltener (andere Geräte).
  // Nichts offen: der neue Stand erscheint. Getipptes bleibt stehen, der Hinweis bietet die Wahl
  const inSharedFolder = (() => {
    for (let f = folders.find(x => x.syncId === note?.folder), n = 0; f && n < 50; f = folders.find(x => x.syncId === f!.parent), n++)
      if (f.sharedWith?.length) return true
    return false
  })()
  const sharedNote = !!note?.owner || !!note?.sharedWith || inSharedFolder
  useEffect(() => {
    const iv = setInterval(async () => {
      if (document.hidden || inFlight.current || version.current === undefined) return
      const run = saveRun.current
      try {
        const n = await api.get<NoteDetail>(`${NB}/${id}`)
        if (run !== saveRun.current || inFlight.current || n.version === version.current) return
        if (dirty.current) {
          setConflict(true)
          setRejected(sharedNote ? t('Die Notiz wurde inzwischen von jemand anderem geändert.') : t('Die Notiz wurde inzwischen auf einem anderen Gerät geändert.'))
        } else load()
      } catch { /* weg oder nicht mehr geteilt: die Liste räumt auf */ }
    }, sharedNote ? 5000 : 30000)
    return () => clearInterval(iv)
  }, [sharedNote, id, load])
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
    const caret = caretOf(el)
    el.innerHTML = ''
    const nodesOf = (html: string) => {
      const tpl = document.createElement('template')
      tpl.innerHTML = html.trim().startsWith('<') ? html : `<p>${html}</p>`
      const nodes = Array.from(tpl.content.children) as HTMLElement[]
      nodes.forEach(node => node.querySelectorAll('img').forEach(img => {
        const src = img.getAttribute('src') || ''
        if (src.startsWith('blob:')) { img.setAttribute('data-blob', src.slice(5)); img.setAttribute('src', `${NB}/blob/${src.slice(5)}`) }
      }))
      return nodes
    }
    note.blocks.forEach((html, i) => {
      const nodes = nodesOf(html)
      const node = nodes[0] ?? document.createElement('p')
      node.setAttribute('data-i', String(i))
      if (note.protected[i] || note.readOnly) node.setAttribute('contenteditable', 'false')
      el.appendChild(node)
      nodes.slice(1).forEach(n => el.appendChild(n))
    })
    if (!el.children.length) el.appendChild(document.createElement('p'))
    fixContrast(el)
    ser0.current = Array.from(el.children).map(c => ser(c as HTMLElement))
    if (caret) setCaret(el, caret)
    // „Beides behalten“: was nur im Editor stand, kommt unter den neueren Stand
    const add = pendingAdd.current
    pendingAdd.current = null
    if (add) {
      const fresh = add.filter(html => !ser0.current.includes(html))
      fresh.forEach(html => nodesOf(html).forEach(n => el.appendChild(n)))
      if (fresh.length) { fixContrast(el); changed() }
    }
  }, [note?.id, rev]) // eslint-disable-line react-hooks/exhaustive-deps

  // Neue Notiz oder Enter auf der Karte: die Tastatur schreibt gleich in den Text
  useEffect(() => {
    const el = root.current
    if (!focusText || !note || !el) return
    onTextFocused?.()
    if (note.readOnly || note.deleted) return
    el.focus()
    setCaret(el, { block: el.childNodes.length - 1, offset: el.lastChild?.textContent?.length ?? 0 })
  }, [focusText, note?.id, rev]) // eslint-disable-line react-hooks/exhaustive-deps

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

  const doSave = async (plain?: string, leaving = false) => {
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
    const text = plain ?? el.innerText
    // Fuer den Fall, dass der neuere Stand die Speicherung ablehnt und die Notiz schon verlassen ist
    const copy = blocksOf(el)
    dirty.current = false
    setSaving('saving')
    inFlight.current = true
    saveRun.current++
    let stale = false
    try {
      const body = JSON.stringify({ blocks, plain: text, version: version.current })
      const r = await e2eFetch(`${NB}/${id}/content`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin', body,
        // Beim Verlassen der Seite soll die Anfrage das Schließen überstehen; der Browser erlaubt das nur für kleine Anfragen
        keepalive: leaving && body.length < 40000,
      })
      const data = await r.json().catch(() => null)
      stale = r.status === 409
      if (!r.ok) throw new Error(data?.error || r.statusText)
      version.current = data?.version
      renumber(el, blocks, nodes, snap, data?.protected ?? null)
      setSaving(dirty.current ? 'dirty' : 'saved')
      setRejected(null)
      setConflict(false)
      onChanged()
    } catch (e) {
      const message = e instanceof TypeError ? t('Keine Verbindung zum Server.') : tError(e instanceof Error ? e.message : String(e))
      if (!alive.current) {
        // Schon woanders: der Hinweis im Editor wäre nicht mehr zu sehen
        if (stale) keepAsCopy(copy, text)
        else fail(new Error(message))
        return
      }
      dirty.current = true
      setSaving('dirty')
      setConflict(stale)
      setRejected(message)
    } finally { inFlight.current = false }
  }

  /** Alle Bloecke des Editors als HTML (fuer Kopie und „Beides behalten“). */
  const blocksOf = (el: HTMLElement) => {
    const blocks: string[] = []
    el.childNodes.forEach(n => {
      if (n.nodeType === 3) { if (n.textContent?.trim()) blocks.push(textBlock(n.textContent)); return }
      if (n.nodeType !== 1) return
      const node = n as HTMLElement
      blocks.push(node.tagName === 'DIV' && !node.hasAttribute('data-i') ? `<p>${node.innerHTML}</p>` : ser(node))
    })
    return blocks
  }

  /** Das Getippte als eigene neue Notiz sichern: im selben Ordner, wenn das geht, sonst bei den eigenen Notizen. */
  const copyToNote = async (blocks: string[], text: string) => {
    const r = await api.post<{ id: number }>(NB, { folder: note?.single ? null : note?.folder })
      .catch(() => api.post<{ id: number }>(NB, { folder: null }))
    await api.put(`${NB}/${r.id}/content`, { blocks: blocks.map(html => ({ html })), plain: text })
    return r.id
  }
  /** Nach dem Verlassen der Notiz: nichts geht verloren, die Meldung sagt, wo der Text jetzt liegt. */
  const keepAsCopy = (blocks: string[], text: string) => {
    copyToNote(blocks, text)
      .then(() => { notify(t('Die Notiz wurde inzwischen geändert. Dein Text liegt jetzt in einer eigenen Kopie.'), undefined, 'warning'); onChanged() })
      .catch(fail)
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
  const keepAsCopyRef = useRef(keepAsCopy)
  keepAsCopyRef.current = keepAsCopy
  const save = useCallback((plain?: string, leaving = false) => (chain.current = chain.current.then(() => saveRef.current(plain, leaving))), [])

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
  useLayoutEffect(() => {
    alive.current = true
    return () => {
      alive.current = false
      // Text jetzt lesen - ausgehaengt liefert innerText keine Zeilenumbrueche mehr
      if (timer.current !== undefined) { clearTimeout(timer.current); timer.current = undefined; save(root.current?.innerText) }
      else if (dirty.current && root.current) {
        // Abgelehnt und nicht entschieden: der neuere Stand bleibt, das Getippte wird eine Kopie
        if (conflictRef.current) keepAsCopyRef.current(blocksOf(root.current), root.current.innerText)
        else save(root.current.innerText)
      }
    }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  // Seite neu laden oder schließen: Offenes sofort abschicken und warnen, solange es nicht sicher angekommen ist.
  // Tab im Hintergrund: gleich speichern, statt auf die Pause zu warten
  const titleSaved = useRef('')
  titleSaved.current = note?.customTitle ?? ''
  useEffect(() => {
    const sendNow = () => { if (timer.current !== undefined) { clearTimeout(timer.current); timer.current = undefined; save(undefined, true) } }
    const onLeave = (e: BeforeUnloadEvent) => {
      const typed = titleRef.current?.value
      const titleOpen = typed !== undefined && !titleRef.current?.readOnly && typed !== titleSaved.current
      if (titleOpen) api.put(`${NB}/${id}/meta`, typed ? { customTitle: typed } : { clearCustomTitle: true }).catch(() => {})
      if (!titleOpen && !dirty.current && !inFlight.current && timer.current === undefined) return
      sendNow()
      e.preventDefault()
      e.returnValue = ''
    }
    const onHidden = () => { if (document.visibilityState === 'hidden') sendNow() }
    window.addEventListener('beforeunload', onLeave)
    document.addEventListener('visibilitychange', onHidden)
    window.addEventListener('pagehide', sendNow)
    return () => {
      window.removeEventListener('beforeunload', onLeave)
      document.removeEventListener('visibilitychange', onHidden)
      window.removeEventListener('pagehide', sendNow)
    }
  }, [id, save])

  const meta = async (body: any) => {
    try {
      await flush(); await api.put(`${NB}/${id}/meta`, body)
      // In einen geteilten Ordner kopiert: die Notiz hat dort eine neue Kennung, die Liste zeigt sie unter „Mit mir geteilt“
      if (id > 0 && body.folder && shared.some(x => x.folders.some(f => f.syncId === body.folder))) { onGone(); return }
      await load(); onChanged()
    } catch (e) { fail(e) }
  }
  const action = async (a: 'archive' | 'restore' | 'trash') => {
    try { await flush(); await api.post(`${NB}/${id}/${a}`); a === 'restore' ? (await load(), onChanged()) : onGone() } catch (e) { fail(e) }
  }
  /** Aenderungen verwerfen und den Stand vom Server laden. */
  const discard = () => {
    clearTimeout(timer.current); timer.current = undefined
    dirty.current = false
    setRejected(null); setConflict(false); setSaving('saved')
    saveRun.current++
    load(true)
  }
  /** Bei Konflikt: der neuere Stand bleibt, was nur hier im Editor steht, kommt darunter. */
  const keepBoth = async () => {
    const el = root.current
    if (!el) return
    const message = rejected
    clearTimeout(timer.current); timer.current = undefined
    const before = new Set(ser0.current)
    pendingAdd.current = blocksOf(el).filter(html => {
      if (before.has(html)) return false
      const d = document.createElement('div')
      d.innerHTML = html
      return !!d.textContent?.trim() || !!d.querySelector('img')
    })
    saveRun.current++
    setRejected(null); setConflict(false)
    if (await load(true)) return
    // Nicht geladen: alles bleibt, wie es war
    pendingAdd.current = null
    setRejected(message); setConflict(true)
  }
  /** Nach einem Fehler ohne Konflikt (Verbindung weg): noch einmal versuchen. */
  const retry = () => { setRejected(null); save() }
  /** Bei Konflikt: den Editorinhalt als neue Notiz sichern, diese bleibt unveraendert. */
  const saveAsCopy = async () => {
    const el = root.current
    if (!el || !note) return
    try {
      const copy = await copyToNote(blocksOf(el), el.innerText)
      clearTimeout(timer.current); timer.current = undefined
      dirty.current = false
      setRejected(null); setConflict(false)
      notify(t('Als Kopie gespeichert'))
      onOpen(copy)
    } catch (e) { fail(e) }
  }
  const destroy = async () => {
    if (!await askConfirm('', t('Endgültig löschen? Auch auf allen anderen Geräten.'))) return
    try { await api.del(`${NB}/${id}`); onGone() } catch (e) { fail(e) }
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

  // Aus der Galerie oder den Dateien von dmOffice: im Browser holen, dann wie vom Gerät (Bilder an die Schreibmarke, sonst Anhang)
  const [pickFrom, setPickFrom] = useState<'gallery' | 'files' | null>(null)
  const fromServer = async (items: Item[], source: Source) => {
    try {
      const files = await Promise.all(items.filter(i => !i.isDir).map(async i => {
        const r = await fetch(source.download(i), { credentials: 'same-origin' })
        if (!r.ok) throw new Error(i.name)
        const blob = await r.blob()
        return new File([blob], i.name, { type: i.mime || blob.type || 'application/octet-stream' })
      }))
      await addFiles(files)
    } catch (e) { fail(e) }
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
          const { name } = await api.form<{ name: string }>(`${NB}/images?note=${id}`, form)
          html += `<p><img src="${NB}/blob/${name}" data-blob="${name}" style="max-width:100%" /></p>`
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
    try { await flush(); await api.form(`${NB}/${id}/attachments`, f); await load(); notify(t('Angehängt')) } catch (e) { fail(e) }
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
        {/* Sichtbar, ob das Getippte schon angekommen ist */}
        {!mobile && saving !== 'idle' && <Text size={200} role="status" style={{ color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' }}>
          {saving === 'saving' ? t('Speichert …') : saving === 'dirty' ? t('Ungespeichert') : t('Gespeichert')}
        </Text>}
        {mobile && <><Tooltip content={t('Wichtig')} relationship="label">
          <Button appearance="subtle" disabled={locked} icon={note.important ? <Star20Filled color={tokens.colorPaletteYellowForeground1} /> : <Star20Regular />} onClick={() => meta({ important: !note.important })} />
        </Tooltip>
        <Menu>
          <MenuTrigger disableButtonEnhancement><Tooltip content={t('Farbe der Notiz')} relationship="label"><Button appearance="subtle" disabled={locked} icon={<Color20Regular />} /></Tooltip></MenuTrigger>
          <MenuPopover>
            <div className={s.swatches}>{noteColors.map(c => <button key={c} className={s.swatch} style={{ background: c }} onClick={() => meta({ color: c })} />)}</div>
          </MenuPopover>
        </Menu>
        <Menu>
          <MenuTrigger disableButtonEnhancement><Tooltip content={t('In Ordner verschieben')} relationship="label"><Button appearance="subtle" disabled={locked || note.single} icon={<FolderArrowRight20Regular />} /></Tooltip></MenuTrigger>
          <MenuPopover>
            <MenuList>
              {!note.owner && <MenuItem onClick={() => meta({ folder: '' })}>{t('Ohne Ordner')}</MenuItem>}
              {moveFolders.map(f => <MenuItem key={f.syncId} onClick={() => meta({ folder: f.syncId })}>{f.name}</MenuItem>)}
              {!note.owner && sharedTargets().map(x => <MenuItem key={x.id} onClick={() => meta({ folder: x.id })}>{x.label}</MenuItem>)}
            </MenuList>
          </MenuPopover>
        </Menu></>}
        {/* Anhängen: vom Gerät, aus der Galerie oder aus den Dateien von dmOffice (die werden geholt und wie vom Gerät eingefügt) */}
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Tooltip content={t('Datei anhängen')} relationship="label"><Button appearance="subtle" disabled={locked} icon={<Attach20Regular />} /></Tooltip>
          </MenuTrigger>
          <MenuPopover><MenuList>
            <MenuItem icon={<Desktop20Regular />} onClick={() => fileInput.current?.click()}>{t('Vom Computer')}</MenuItem>
            <MenuItem icon={<ImageMultiple20Regular />} onClick={() => setPickFrom('gallery')}>{t('Aus der dmOffice-Galerie')}</MenuItem>
            <MenuItem icon={<Cloud20Regular />} onClick={() => setPickFrom('files')}>{t('Aus den dmOffice-Dateien')}</MenuItem>
          </MenuList></MenuPopover>
        </Menu>
        {pickFrom === 'gallery' && <PickPhotoDialog title={t('Aus der dmOffice-Galerie')} onClose={() => setPickFrom(null)} onPickMany={items => { setPickFrom(null); fromServer(items, gallerySource) }} />}
        {pickFrom === 'files' && <StoredFilesDialog onClose={() => setPickFrom(null)} onPick={items => { setPickFrom(null); fromServer(items, ownSource) }} />}
        {!mobile ? null : note.owner ? null : inTrash || note.archived
          ? <Tooltip content={t('Wiederherstellen')} relationship="label"><Button appearance="subtle" icon={<ArrowUndo20Regular />} onClick={() => action('restore')} /></Tooltip>
          : <Tooltip content={t('Archivieren')} relationship="label"><Button appearance="subtle" icon={<Archive20Regular />} onClick={() => action('archive')} /></Tooltip>}
        {!mobile ? null : inTrash
          ? <Button appearance="subtle" icon={<Delete20Regular />} onClick={destroy}>{t('Endgültig löschen')}</Button>
          : <Tooltip content={t('In den Papierkorb')} relationship="label"><Button appearance="subtle" disabled={locked || note.single} icon={<Delete20Regular />} onClick={() => action('trash')} /></Tooltip>}
        <input ref={fileInput} type="file" multiple hidden onChange={e => { upload(e.target.files); e.target.value = '' }} />
      </div>
      <NoteTags tags={note.tags ?? []} known={knownTags} readOnly={locked || inTrash} onChange={tags => meta({ tags })} />
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
          <MessageBarBody>{rejected} {t('Dein Text ist noch nicht gespeichert und bleibt hier stehen.')}</MessageBarBody>
          <MessageBarActions>
            {conflict
              ? <Tooltip content={t('Der neuere Stand bleibt, dein Text kommt darunter.')} relationship="description"><Button size="small" appearance="primary" onClick={keepBoth}>{t('Beides behalten')}</Button></Tooltip>
              : <Button size="small" appearance="primary" onClick={retry}>{t('Erneut versuchen')}</Button>}
            <Button size="small" onClick={saveAsCopy}>{t('Als Kopie speichern')}</Button>
            <Button size="small" onClick={discard}>{t('Änderungen verwerfen')}</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {note.owner && (
        <MessageBar intent="info"><MessageBarBody>{locked
          ? t('Geteilt von {name} – du darfst sie nur lesen.', { name: note.owner })
          : t('Geteilt von {name} – Änderungen werden automatisch gespeichert, andere sehen sie nach wenigen Sekunden. Schreiben zwei gleichzeitig, fragt dmOffice nach.', { name: note.owner })}</MessageBarBody></MessageBar>
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
              {note.showOnly
                ? <Text title={t('Nur gezeigt – herunterladen ist nicht erlaubt.')}>{a.name}</Text>
                : <a href={`${NB}/blob/${a.blob}?` + q({ download: true, file: a.name })} style={{ color: 'inherit' }}>{a.name}</a>}
              <Text size={200}>({fmtSize(a.size)})</Text>
              <Button size="small" appearance="transparent" icon={<Dismiss16Regular />} aria-label={t('Entfernen')}
                onClick={async () => { if (!await askConfirm('', t('„{name}“ entfernen?', { name: a.name }))) return; await api.del(`${NB}/${id}/attachments/${encodeURIComponent(a.name)}`).catch(fail); load() }} />
            </span>
          ))}
        </div>
      )}
    </div>
  )
}
