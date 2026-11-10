import {
  Badge, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle,
  Menu, MenuItem, MenuPopover, MenuTrigger, Option, Slider, Spinner, Text,
  Textarea, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowDownload20Regular, ArrowSync20Regular, Checkmark20Regular, ChevronDown16Regular, ChevronRight16Regular, Delete20Regular,
  Dismiss20Regular, DocumentText20Regular, Edit20Regular, Folder20Regular, FolderArrowRight20Regular, FolderOpen20Regular, MailInbox20Regular, AppsList20Regular, Grid20Regular, ZoomIn20Regular,
  PanelRight20Regular, Person20Regular, PersonAdd20Regular, MoreHorizontal20Regular, Search20Regular, Settings20Regular, Sparkle16Regular, Tag20Regular, Ribbon20Regular, TextBulletListTree20Regular, ArrowUpload20Regular,
  DocumentBulletList20Regular, NumberSymbol20Regular, ArrowSwap20Regular, LockClosed20Regular, LockOpen20Regular, Hourglass20Regular, Pause16Regular, Pause20Regular, Play20Regular,
} from '@fluentui/react-icons'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { moneyText, parseMoney } from './money'
import { usePrefs } from '../prefs'
import { useNavStyles } from '../shell/nav'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { SidePane, useIsMobile } from '../shell/mobile'
import { askConfirm, ContextMenu, useContextMenu } from '../shell/context'
import { EditEntry, ListsView, useEntryActions } from './DmsLists'
import { Pick, PickButton, PickField, PickPages, usePick } from './DmsPick'
import { MoveAreaDialog } from './DmsMoveArea'
import { closeTresor, OPEN_COLOR, requestTresorOpen, TRESOR_EVENT } from '../tresor/tresor'
import { ColDef, useGridColumns } from '../shell/GridColumns'
import { entriesOf, readEntries } from '../files/FilesView'
import { OfficeEditor, OfficeSession, officeMode, useOffice } from '../files/Office'
import {
  areaQ, contentUrl, defaultTrees, downloadDocs, Doc, Entry, Filter, filterOf, fullName, importedText, Kind, kindLabel, Level, levelLabel, LEVELS, loadDocs, monthName, Node, ordered, sourceLabel, State, Tree, typeLabel,
} from './model'
import { EntrySelect } from './EntrySelect'

const FIXED_COLOR = '#1F7FD6'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0, position: 'relative' },
  side: { width: '240px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0 },
  head: { display: 'flex', alignItems: 'center', gap: '8px', padding: '12px 20px 8px', flexShrink: 0, flexWrap: 'wrap', '@media (max-width: 767px)': { padding: '8px 12px' } },
  title: { fontSize: '20px', fontWeight: 600, flex: 1, minWidth: '160px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  search: { width: '280px', maxWidth: '100%', '@media (max-width: 767px)': { width: 'auto', flex: '1 1 100%' } },
  filters: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', padding: '0 20px 8px', minHeight: '40px', boxSizing: 'border-box', '@media (max-width: 767px)': { padding: '0 12px 8px' } },
  date: { width: '150px' },
  batch: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', padding: '4px 20px', minHeight: '40px', boxSizing: 'border-box', marginBottom: '0', backgroundColor: tokens.colorBrandBackground2, flexShrink: 0 },
  list: { flex: 1, minHeight: 0, overflowY: 'auto' },
  row: { display: 'grid', gridTemplateColumns: '32px 96px minmax(160px, 2fr) minmax(100px, 1fr) minmax(90px, 1fr) minmax(100px, 1.4fr)', gap: '10px',
    alignItems: 'center', padding: '4px 20px', minHeight: '36px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, cursor: 'default',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    '@media (max-width: 767px)': { gridTemplateColumns: '32px minmax(0, 1fr)', padding: '6px 12px' } },
  thead: { fontSize: '12px', color: tokens.colorNeutralForeground3, minHeight: '30px', ':hover': { backgroundColor: 'transparent' } },
  sel: { backgroundColor: tokens.colorNeutralBackground1Selected, ':hover': { backgroundColor: tokens.colorNeutralBackground1Selected } },
  cell: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  wide: { '@media (max-width: 767px)': { display: 'none' } },
  narrow: { display: 'none', '@media (max-width: 767px)': { display: 'block', fontSize: '12px', color: tokens.colorNeutralForeground3 } },
  auto: { fontStyle: 'italic', color: tokens.colorNeutralForeground2 },
  muted: { color: tokens.colorNeutralForeground3 },
  chips: { display: 'flex', gap: '4px', flexWrap: 'nowrap', overflow: 'hidden' },
  chip: { borderRadius: '4px', padding: '0 6px', fontSize: '12px', lineHeight: '18px', color: '#1b1b1b', whiteSpace: 'nowrap' },
  detail: { width: '420px', flexShrink: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', minHeight: 0, overflowY: 'auto',
    '@media (max-width: 1100px)': { width: '340px' } },
  preview: { height: '360px', flexShrink: 0, backgroundColor: tokens.colorNeutralBackground3, display: 'flex', alignItems: 'center', justifyContent: 'center',
    '& iframe': { width: '100%', height: '100%', border: 'none' }, '& img': { maxWidth: '100%', maxHeight: '100%', objectFit: 'contain' } },
  frame: { width: '100%', height: '100%', border: 'none' },
  pages: { width: '100%', height: '100%', overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: '8px', padding: '8px', boxSizing: 'border-box',
    cursor: 'zoom-in', '& img': { width: '100%', maxHeight: 'none', height: 'auto', display: 'block', boxShadow: tokens.shadow4, backgroundColor: '#fff' } },
  rangeRow: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  dateRow: { display: 'flex', gap: '8px', alignItems: 'center', '& > :first-child': { flexShrink: 0 } },
  dateFound: { minWidth: 0, flexGrow: 1 },
  dateOption: { display: 'flex', flexDirection: 'column', gap: '2px', maxWidth: '360px', '& > span:last-child': { whiteSpace: 'normal', overflowWrap: 'anywhere' } },
  form: { display: 'flex', flexDirection: 'column', gap: '10px', padding: '12px 16px' },
  // Bearbeitungsansicht: Dokument groß, Felder daneben; die Liste schmal rechts
  editor: { flex: 1, display: 'flex', minWidth: 0, minHeight: 0 },
  editPreview: { flex: 1, minWidth: 0, overflowY: 'auto', padding: '16px', backgroundColor: tokens.colorNeutralBackground3 },
  editForm: { width: '380px', flexShrink: 0, overflowY: 'auto', borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, '@media (max-width: 1300px)': { width: '320px' } },
  side2: { width: '300px', flexShrink: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', minHeight: 0,
    '@media (max-width: 1300px)': { width: '240px' } },
  mini: { display: 'grid', gridTemplateColumns: '40px minmax(0, 1fr)', gap: '8px', alignItems: 'center', padding: '6px 12px', cursor: 'default',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  miniThumb: { width: '40px', height: '56px', backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusSmall, overflow: 'hidden',
    display: 'flex', alignItems: 'center', justifyContent: 'center', '& img': { width: '100%', height: '100%', objectFit: 'cover', objectPosition: 'top', backgroundColor: '#fff' } },
  actions: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  empty: { margin: 'auto', textAlign: 'center', maxWidth: '460px', display: 'flex', flexDirection: 'column', gap: '12px', alignItems: 'center', padding: '24px' },
  drop: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-6px' },
  treeRow: { paddingLeft: '4px' },
  more: { padding: '12px 20px' },
  grid: { display: 'grid', gap: '12px', padding: '8px 20px 12px', alignContent: 'start', '@media (max-width: 767px)': { padding: '8px 12px' } },
  tile: { display: 'flex', flexDirection: 'column', gap: '4px', padding: '6px', borderRadius: tokens.borderRadiusMedium, cursor: 'default', position: 'relative',
    border: '1px solid transparent', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  // Im Tresor: grüner Rahmen um die Vorschau, grüner Streifen an Zeilen - man sieht, woher das Dokument kommt
  tresorThumb: { outline: `2px solid ${OPEN_COLOR}`, outlineOffset: '-2px' },
  tresorRow: { boxShadow: `inset 3px 0 0 ${OPEN_COLOR}` },
  // Fixiert: blauer Rahmen; im Tresor liegt der grüne Rahmen außen darum
  fixedThumb: { outline: `2px solid ${FIXED_COLOR}`, outlineOffset: '-2px' },
  fixedRow: { boxShadow: `inset 3px 0 0 ${FIXED_COLOR}` },
  fixedTresorThumb: { outline: `2px solid ${FIXED_COLOR}`, outlineOffset: '-2px', boxShadow: `0 0 0 2px ${OPEN_COLOR}` },
  fixedTresorRow: { boxShadow: `inset 3px 0 0 ${OPEN_COLOR}, inset 6px 0 0 ${FIXED_COLOR}` },
  areas: { display: 'grid', gridTemplateColumns: '1fr 1fr', margin: '8px 8px 4px', border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: tokens.borderRadiusMedium, overflow: 'hidden' },
  area: { display: 'flex', alignItems: 'center', justifyContent: 'center', gap: '6px', padding: '6px 4px', border: 'none', background: 'transparent', color: 'inherit',
    font: 'inherit', cursor: 'pointer', ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover } },
  areaFiles: { backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground2, ':hover': { backgroundColor: tokens.colorBrandBackground2 } },
  areaTresor: { backgroundColor: tokens.colorPaletteGreenBackground2, color: tokens.colorPaletteGreenForeground2, ':hover': { backgroundColor: tokens.colorPaletteGreenBackground2 } },
  tileSel: { backgroundColor: tokens.colorNeutralBackground1Selected, border: `1px solid ${tokens.colorBrandStroke1}`, ':hover': { backgroundColor: tokens.colorNeutralBackground1Selected } },
  thumb: { aspectRatio: '1 / 1.414', backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusSmall, overflow: 'hidden', display: 'flex',
    alignItems: 'center', justifyContent: 'center', boxShadow: tokens.shadow2,
    '& img': { width: '100%', height: '100%', objectFit: 'cover', objectPosition: 'top', backgroundColor: '#fff', display: 'block' } },
  tileCheck: { position: 'absolute', top: '6px', left: '6px', backgroundColor: tokens.colorNeutralBackground1, borderRadius: tokens.borderRadiusMedium },
  tileTitle: { fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  tileSub: { fontSize: '12px', color: tokens.colorNeutralForeground3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minHeight: '16px' },
  textPreview: { width: '100%', height: '100%', overflowY: 'auto', padding: '12px', boxSizing: 'border-box', display: 'flex', flexDirection: 'column', gap: '8px' },
  textBody: { whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' },
  noPreview: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px' },
  compare: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '8px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3 },
})

type Nav =
  | { kind: 'inbox' } | { kind: 'queue' } | { kind: 'all' } | { kind: 'paperless' } | { kind: 'tresor' } | { kind: 'tresorInbox' } | { kind: 'tresorQueue' } | { kind: 'fixed' } | { kind: 'tresorFixed' }
  /** tresor: derselbe Baum über den Dokumenten im Tresor (eigener Abschnitt, grün) */
  | { kind: 'tree'; tree: number; path: { level: string; key: string | null; name: string }[]; tresor?: boolean }
  | { kind: 'entry'; entry: Entry }
  | { kind: 'manage'; list: Kind }

const PAGE = 200
/** Bäume im Tresor: dieselben Bäume, ihre Nummer plus TRESOR_TREE (Daten und Auf-/Zuklappen getrennt von der Dateiablage) */
const TRESOR_TREE = 1000

/** Spalten der Dokumentliste (Rechtsklick auf die Kopfzeile wählt sie) */
const fmtAmount = (cents: number | null | undefined) => cents == null ? '' : (cents / 100).toLocaleString(lang === 'en' ? 'en-GB' : 'de-DE', { style: 'currency', currency: 'EUR' })

const DOC_COLS: Record<string, ColDef> = {
  amount: { label: () => t('Betrag'), width: 100, sort: 'amount' },
  date: { label: () => t('Datum'), width: 100, sort: 'date' },
  title: { label: () => t('Titel'), width: 380, sort: 'title' },
  correspondent: { label: () => t('Korrespondent'), width: 180, sort: 'correspondent' },
  doctype: { label: () => t('Dokumenttyp'), width: 150, sort: 'doctype' },
  tags: { label: () => t('Tags'), width: 220, sort: 'tags' },
  role: { label: () => t('Rolle'), width: 150, sort: 'role' },
  imported: { label: () => t('Importiert'), width: 140, sort: 'imported' },
  name: { label: () => t('Dateiname'), width: 240, sort: 'name' },
  type: { label: () => t('Dateityp'), width: 130, sort: 'type' },
  size: { label: () => t('Größe'), width: 90, sort: 'size' },
  source: { label: () => t('Quelle'), width: 110, sort: 'source' },
}
const DOC_DEFAULT = ['date', 'title', 'correspondent', 'doctype', 'tags']

/** Rotes „!“ an einem Dokument: der Hinweis dazu als Tooltip. */
const ErrorMark = ({ text }: { text: string }) => <Badge size="small" color="danger" style={{ marginLeft: 6 }} title={t(text)} aria-label={t(text)}>!</Badge>

/** Kurzes Datum (Dokumentdatum ist JJJJ-MM-TT ohne Zeit). */
const fmtDay = (d: string | null) => d ? new Date(d + 'T12:00:00').toLocaleDateString(lang === 'en' ? 'en-GB' : 'de-DE') : ''
const keyName = (level: string, key: string | null, name: string) => key === null ? t('Unbekannt')
  : level === 'doc_month' || level === 'import_month' ? monthName(key, lang === 'en' ? 'en-GB' : 'de-DE') : name

/** DMS: Dokumente mit Korrespondent (WER), Dokumenttyp (WAS) und Tags (THEMA), Inbox, virtuelle Bäume. */
export default function DmsView() {
  const s = useStyles()
  const nv = useNavStyles()
  const { fail, notify } = useApp()
  const mobile = useIsMobile()
  const { prefs, update } = usePrefs()
  const trees: Tree[] = prefs.dmsTrees?.length ? prefs.dmsTrees as Tree[] : defaultTrees()
  const [drawer, toggleSide, closeSide] = useSidePane('dms')
  // Navigationsbereich: am PC immer da (ohne Knopf im Menüband), auf dem Handy aufklappbar
  const sidePane = mobile ? drawer : true
  // Liste oder Kacheln, Kachelgröße: je Person gemerkt
  const tiles = prefs.dmsView === 'tiles'
  const tilePx = prefs.dmsTilePx ?? 180
  // Spalten der Liste wie im Explorer (sortieren, Breite, Reihenfolge, Auswahl), je Person gemerkt
  const grid = useGridColumns('dms-docs', DOC_COLS, DOC_DEFAULT)
  const [state, setState] = useState<State | null>(null)
  const [entries, setEntries] = useState<Entry[]>([])
  const [nav, setNav] = useState<Nav>({ kind: 'inbox' })
  const [query, setQuery] = useState('')
  const [range, setRange] = useState<{ docFrom?: string; docTo?: string }>({})
  const [docs, setDocs] = useState<Doc[] | null>(null)
  const [total, setTotal] = useState(0)
  const [checked, setChecked] = useState<Set<number>>(new Set())
  // Strg+A markiert alle gezeigten Dokumente (nicht in Eingabefeldern, nur solange das DMS zu sehen ist)
  const allIds = useRef<number[]>([])
  const lastCheck = useRef<number | null>(null)
  const shiftNext = useRef(false)
  useEffect(() => {
    const on = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey) || e.key.toLowerCase() !== 'a' || e.shiftKey || e.altKey) return
      if (!location.hash.startsWith('#/dms')) return
      const el = e.target as HTMLElement | null
      // Nur Textfelder behalten Strg+A für sich - ein angeklicktes Kontrollkästchen (Kachel markiert) nicht
      if (el && (el.isContentEditable || /^(TEXTAREA|SELECT)$/.test(el.tagName)
        || (el.tagName === 'INPUT' && !/^(checkbox|radio|button)$/.test((el as HTMLInputElement).type)))) return
      if (document.querySelector('[role=dialog]')) return
      e.preventDefault()
      setChecked(new Set(allIds.current))
    }
    document.addEventListener('keydown', on)
    return () => document.removeEventListener('keydown', on)
  }, [])
  const [current, setCurrent] = useState<number | null>(null)
  const [detail, setDetail] = useState(true)
  const [treeData, setTreeData] = useState<Record<number, Node[]>>({})
  const [open, setOpen] = useState<Set<string>>(new Set())
  const [editTree, setEditTree] = useState<number | 'new' | null>(null)
  const [drag, setDrag] = useState(false)
  const [busy, setBusy] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)

  const filter: Filter = useMemo(() => {
    const base: Filter = nav.kind === 'inbox' ? { inbox: true }
      : nav.kind === 'queue' ? { queued: true }
      : nav.kind === 'paperless' ? { inbox: true, paperlessDiffers: true }
      : nav.kind === 'tresor' ? { area: 'tresor' }
      : nav.kind === 'tresorInbox' ? { area: 'tresor', inbox: true }
      : nav.kind === 'tresorQueue' ? { area: 'tresor', queued: true }
      : nav.kind === 'fixed' ? { fixed: true }
      : nav.kind === 'tresorFixed' ? { area: 'tresor', fixed: true }
      : nav.kind === 'tree' ? { ...filterOf(nav.path), area: nav.tresor ? 'tresor' : undefined }
      : nav.kind === 'entry' ? (nav.entry.kind === 'correspondent' ? { correspondent: nav.entry.id } : nav.entry.kind === 'doctype' ? { doctype: nav.entry.id } : nav.entry.kind === 'role' ? { role: nav.entry.id } : { tags: [nav.entry.id] })
      : {}
    return { ...base, q: query.trim() || undefined, docFrom: range.docFrom ?? base.docFrom, docTo: range.docTo ?? base.docTo, sort: grid.sort?.key, desc: grid.sort?.desc }
  }, [nav, query, range, grid.sort?.key, grid.sort?.desc])

  const [plDiff, setPlDiff] = useState(0)
  const loadState = useCallback(() => {
    api.get<{ differing: number }>('/api/dms/paperless/status').then(r => setPlDiff(r.differing)).catch(() => {})
    // Abzeichen in der linken Leiste mitziehen
    return api.get<State>('/api/dms/state').then(st => { setState(st); window.dispatchEvent(new Event('dmoffice:dmsstatus')) }).catch(fail)
  }, [fail])
  // Tresor anderswo geöffnet oder geschlossen (Kopfzeile, Dateien, Einstellungen): Schloss und Zähler nachziehen
  useEffect(() => {
    const on = () => loadState()
    window.addEventListener(TRESOR_EVENT, on)
    return () => window.removeEventListener(TRESOR_EVENT, on)
  }, [loadState])
  // Jede Ablage hat ihre Inbox und Warteschlange: die Dateiablage oben, der Tresor in seinem Abschnitt
  const inTresor = nav.kind === 'tresor' || nav.kind === 'tresorInbox' || nav.kind === 'tresorQueue' || nav.kind === 'tresorFixed' || (nav.kind === 'tree' && !!nav.tresor)
  // Bereich merken; beim Öffnen des DMS den zuletzt gewählten wieder - den Tresor nur, wenn er schon offen ist
  const restored = useRef(false)
  useEffect(() => {
    if (!state || restored.current) return
    restored.current = true
    if (prefs.dmsArea === 'tresor' && state.tresorOpen) setNav({ kind: 'tresorInbox' })
  }, [state]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!restored.current) return
    const area = inTresor ? 'tresor' : 'files'
    if ((prefs.dmsArea ?? 'files') !== area) update({ dmsArea: area })
  }, [inTresor]) // eslint-disable-line react-hooks/exhaustive-deps
  const isInbox = nav.kind === 'inbox' || nav.kind === 'tresorInbox'
  const isQueue = nav.kind === 'queue' || nav.kind === 'tresorQueue'
  const waiting = (inTresor ? state?.tresorQueued : state?.queued) ?? 0
  // Tresor öffnen bzw. schließen (Klick aufs Schloss, Kontextmenü); geschlossen geht es zurück zur Inbox der Dateiablage
  const tresorMenu = useContextMenu<null>()
  // Bereich wählen (gemerkt); der gesperrte Tresor fragt erst nach dem Entsperren
  const switchArea = async (tresor: boolean) => {
    if (tresor === inTresor) return
    if (!tresor) { setNav({ kind: 'inbox' }); return }
    if (state?.tresorOpen) setNav({ kind: 'tresorInbox' })
    else await toggleTresor()
  }
  const toggleTresor = async () => {
    try {
      if (state?.tresorOpen) { await closeTresor(); if (inTresor) setNav({ kind: 'inbox' }) }
      else if (await requestTresorOpen()) setNav({ kind: 'tresorInbox' })
    } catch (e) { fail(e) }
  }
  const loadEntries = useCallback(() => api.get<Entry[]>('/api/dms/lists').then(setEntries).catch(fail), [fail])
  const loadList = useCallback(async (append = false) => {
    if (nav.kind === 'manage') return
    try {
      const r = await loadDocs(filter, append ? docs?.length ?? 0 : 0, PAGE)
      setDocs(d => append && d ? [...d, ...r.items] : r.items)
      setTotal(r.total)
    } catch (e) { fail(e) }
  }, [filter, nav.kind, docs?.length, fail])
  const loadTree = useCallback((i: number) => {
    const tr = trees[i % TRESOR_TREE]
    if (!tr) return
    api.get<Node[]>('/api/dms/tree?' + q({ levels: tr.levels.join(','), area: i >= TRESOR_TREE ? 'tresor' : undefined }))
      .then(n => setTreeData(x => ({ ...x, [i]: n }))).catch(() => setTreeData(x => { const y = { ...x }; delete y[i]; return y }))
  }, [trees, fail])
  const refresh = useCallback(() => {
    loadState(); loadEntries(); loadList()
    Object.keys(treeData).forEach(k => loadTree(Number(k)))
  }, [loadState, loadEntries, loadList, loadTree, treeData])

  useEffect(() => { loadState(); loadEntries() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Andere Ansicht (etwa Tresor): alte Liste und Auswahl nicht stehen lassen, Stand (Schloss) neu holen
  useEffect(() => { setDocs(null); setCurrent(null); setChecked(new Set()); loadState() }, [nav]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const h = setTimeout(() => { loadList(); setChecked(new Set()) }, query ? 250 : 0)
    return () => clearTimeout(h)
  }, [filter]) // eslint-disable-line react-hooks/exhaustive-deps
  // Solange Dokumente in Arbeit sind, nachladen (angehalten: nicht)
  useEffect(() => {
    if (!(state?.queued || (state?.tresorOpen && state.tresorQueued)) || state.paused) return
    const h = setTimeout(refresh, 4000)
    return () => clearTimeout(h)
  }, [state]) // eslint-disable-line react-hooks/exhaustive-deps
  // Zähler auch ohne Warteschlange gelegentlich nachziehen (Quellen legen im Hintergrund ab, auch vor den geschlossenen Tresor)
  useEffect(() => {
    const h = setInterval(() => { if (!document.hidden) loadState() }, 60000)
    return () => clearInterval(h)
  }, [loadState])
  // Zurück auf den Tab: Zähler und Liste neu holen
  const refreshRef = useRef(refresh)
  refreshRef.current = refresh
  useEffect(() => {
    const on = () => { if (document.visibilityState === 'visible') refreshRef.current() }
    document.addEventListener('visibilitychange', on)
    return () => document.removeEventListener('visibilitychange', on)
  }, [])

  // Warteschlange anhalten: Quellen nehmen weiter an, verarbeitet wird erst nach dem Fortsetzen
  const pause = (paused: boolean) => api.post('/api/dms/queue/pause', { paused }).then(() => loadState()).catch(fail)

  const doc = docs?.find(d => d.id === current) ?? null
  const byId = useMemo(() => new Map(entries.map(e => [e.id, e])), [entries])

  // ---------------- Hochladen ----------------

  const upload = async (files: FileList | File[]) => {
    const list = Array.from(files)
    if (!list.length) return
    setBusy(true)
    try {
      // In Teilen hochladen (ganze Ordner können viele Dateien haben)
      let n = 0, dup = 0
      for (let i = 0; i < list.length; i += 20) {
        const form = new FormData()
        list.slice(i, i + 20).forEach(f => form.append('file', f, f.name))
        const r = await api.form<{ files: { name: string; duplicate: boolean }[] }>('/api/dms/upload' + areaQ(inTresor), form)
        n += r.files.length; dup += r.files.filter(f => f.duplicate).length
      }
      notify(importedText(n - dup, dup))
      if (!inTresor) setNav({ kind: 'inbox' })
      refresh()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  // ---------------- Ändern ----------------

  const change = async (ids: number[], body: Record<string, unknown>) => {
    try {
      const updated = ids.length === 1 && !body.batch
        ? [await api.put<Doc>(`/api/dms/docs/${ids[0]}` + areaQ(inTresor), body)]
        : await api.post<Doc[]>('/api/dms/docs/batch' + areaQ(inTresor), { ids, change: body })
      const map = new Map(updated.map(d => [d.id, d]))
      setDocs(d => d?.map(x => map.get(x.id) ?? x) ?? null)
      // Andere offene Dokumente der Inbox ordnet der Server dabei neu ein: Liste neu holen
      loadState(); loadEntries(); loadList()
      Object.keys(treeData).forEach(k => loadTree(Number(k)))
      return true
    } catch (e) { fail(e); return false }
  }
  /** Nach dem Bestätigen der nächste offene Eintrag: der nächste unter dem gezeigten, am Ende der vorige; undefined, wenn das gezeigte nicht dabei war. */
  const nextAfter = (ids: number[]) => {
    const list = docs ?? []
    const at = current !== null && ids.includes(current) ? list.findIndex(x => x.id === current) : -1
    if (at < 0) return undefined
    const open = (x: Doc) => x.inbox && !ids.includes(x.id)
    return { id: (list.slice(at + 1).find(open) ?? list.slice(0, at).reverse().find(open))?.id ?? null }
  }
  const confirm = (ids: number[]) => {
    // Danach gleich das nächste Dokument zeigen - zügig eins nach dem anderen durchgehen
    const next = nextAfter(ids)
    return change(ids, { confirm: true, batch: ids.length > 1 }).then(() => {
      if (isInbox || nav.kind === 'paperless') { setDocs(d => d?.filter(x => !ids.includes(x.id)) ?? null); setTotal(n => Math.max(0, n - ids.length)); setChecked(new Set()) }
      if (next) setCurrent(next.id)
    })
  }
  // Abweichung: die Angaben aus paperless übernehmen (als bestätigt) - danach wie beim Bestätigen das nächste Dokument
  const takePaperless = async (ids: number[]) => {
    const next = nextAfter(ids)
    try {
      await api.post('/api/dms/paperless/take', { ids })
      setDocs(d => d?.filter(x => !ids.includes(x.id)) ?? null); setTotal(n => Math.max(0, n - ids.length)); setChecked(new Set())
      if (next) setCurrent(next.id)
      loadState(); loadEntries(); Object.keys(treeData).forEach(k => loadTree(Number(k)))
    } catch (e) { fail(e) }
  }
  const remove = async (ids: number[]) => {
    const chosen = (docs ?? []).filter(d => ids.includes(d.id))
    const external = chosen.filter(d => d.mount).length
    // Noch nicht abgelegt (Warteschlange, Fehler): es gibt keine Datei, die in den Papierkorb käme
    const unplaced = chosen.filter(d => !d.fileId && !d.mount).length
    const n = ids.length
    const one = n === 1
    const text = inTresor
      ? (one ? t('1 Dokument wird samt seiner Datei im Tresor endgültig gelöscht. Der Tresor hat keinen Papierkorb.')
        : t('{n} Dokumente werden samt ihren Dateien im Tresor endgültig gelöscht. Der Tresor hat keinen Papierkorb.', { n }))
      : unplaced === n
        ? (one ? t('1 Dokument ist noch nicht abgelegt und wird endgültig gelöscht.') : t('{n} Dokumente sind noch nicht abgelegt und werden endgültig gelöscht.', { n }))
        : external === n
          ? (one ? t('1 Dokument liegt in einem externen Ordner und wird dort samt Original endgültig gelöscht. Dort gibt es keinen Papierkorb.')
            : t('{n} Dokumente liegen in einem externen Ordner und werden dort samt Original endgültig gelöscht. Dort gibt es keinen Papierkorb.', { n }))
          : (one ? t('1 Dokument kommt mit seiner Datei in den Papierkorb der Dateiablage. Dort lässt es sich samt Angaben wiederherstellen.')
            : t('{n} Dokumente kommen mit ihren Dateien in den Papierkorb der Dateiablage. Dort lassen sie sich samt Angaben wiederherstellen.', { n }))
            + (external ? ' ' + (external === 1 ? t('1 davon liegt in einem externen Ordner und wird dort samt Original endgültig gelöscht.')
              : t('{n} davon liegen in einem externen Ordner und werden dort samt Original endgültig gelöscht.', { n: external })) : '')
            + (unplaced ? ' ' + (unplaced === 1 ? t('1 davon ist noch nicht abgelegt und wird endgültig gelöscht.')
              : t('{n} davon sind noch nicht abgelegt und werden endgültig gelöscht.', { n: unplaced })) : '')
    if (!await askConfirm(t('Dokumente löschen'), text, t('Löschen'))) return
    try { await api.post('/api/dms/docs/delete' + areaQ(inTresor), { ids }); setChecked(new Set()); setCurrent(null); refresh() } catch (e) { fail(e) }
  }
  const setFixed = async (list: Doc[], on: boolean) => {
    const note = inTresor ? ' ' + t('Im Tresor gilt die Fixierung für alle Wege in dmOffice, solange der Tresor offen ist. Die verschlüsselten Dateien lassen sich außerhalb von dmOffice verändern.')
      : list.some(d => d.mount) ? ' ' + t('In externen Ordnern gilt die Fixierung nur innerhalb von dmOffice. Außerhalb von dmOffice lassen sich die Dateien weiterhin ändern.') : ''
    if (!await askConfirm(on ? t('Dokument fixieren') : t('Fixierung aufheben'),
      (on ? t('Das Dokument lässt sich dann nicht mehr ändern oder löschen, bis die Fixierung bewusst aufgehoben wird. Angaben wie Titel und Tags bleiben änderbar, auch verschieben lässt es sich weiterhin.') + note
        : t('Das Dokument lässt sich danach wieder ändern und löschen.')), on ? t('Fixieren') : t('Aufheben'))) return
    try { for (const d of list) await api.post(`/api/dms/docs/${d.id}/fixed` + areaQ(inTresor), { fixed: on }) } catch (e) { fail(e) }
    refresh()
  }
  const refile = async (ids: number[]) => {
    try { await api.post('/api/dms/docs/refile' + areaQ(inTresor), { ids }); notify(t('Nach Vorgabe abgelegt')); loadList() } catch (e) { fail(e) }
  }
  // Zwischen offenem Bereich und Tresor verschieben (Tresor muss auf diesem Gerät offen sein; sonst fragt der Öffnen-Dialog)
  // Zwischen offenem Bereich und Tresor: Dialog mit Wahl des Ablageprofils und Zielpfad
  const [moving, setMoving] = useState<Doc[] | null>(null)
  const moveArea = (ids: number[]) => setMoving((docs ?? []).filter(d => ids.includes(d.id)))
  // Ziehen und Ablegen: Dokumente auf „Inbox“/„Alle Dokumente“ des anderen Bereichs (offen ↔ Tresor) = dorthin verschieben
  const DRAG_DOCS = 'application/x-dmoffice-dms-docs'
  const dragDoc = (d: { id: number }) => ({
    draggable: true,
    onDragStart: (e: React.DragEvent) => {
      const ids = checked.has(d.id) ? [...checked] : [d.id]
      e.dataTransfer.setData(DRAG_DOCS, JSON.stringify(ids))
      // Beides erlauben: auf einen Bereich = verschieben, auf einen Eintrag im Baum = zuordnen. Passt die Wirkung am Ziel nicht dazu, verweigert der Browser das Ablegen
      e.dataTransfer.effectAllowed = 'copyMove'
    },
  })
  const [dropOn, setDropOn] = useState<string | null>(null)
  /** Ablegen auf einem Bereich: nur der jeweils andere (aus dem Tresor nach offen und umgekehrt). */
  const areaDrop = (key: string, toTresor: boolean) => inTresor === toTresor ? {} : {
    onDragOver: (e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG_DOCS)) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; setDropOn(key) } },
    onDragLeave: () => setDropOn(k => (k === key ? null : k)),
    onDrop: (e: React.DragEvent) => {
      setDropOn(null)
      const raw = e.dataTransfer.getData(DRAG_DOCS)
      if (!raw) return
      e.preventDefault()
      try { moveArea(JSON.parse(raw) as number[]) } catch { /* fremde Daten */ }
    },
  }

  // Dokumente auf einen Eintrag im Baum ziehen = zuordnen (Korrespondent, Dokumenttyp; Tag kommt dazu). Verweilen auf einem
  // zugeklappten Ast klappt ihn auf, damit sich tiefer liegende Einträge erreichen lassen
  const expandTimer = useRef<{ key: string; h: number } | null>(null)
  const stopExpand = (key?: string) => {
    if (expandTimer.current && (!key || expandTimer.current.key === key)) { clearTimeout(expandTimer.current.h); expandTimer.current = null }
  }
  const startExpand = (key: string, toggle?: { open: boolean; on: () => void }) => {
    if (!toggle || toggle.open || expandTimer.current?.key === key) return
    stopExpand()
    expandTimer.current = { key, h: window.setTimeout(() => { expandTimer.current = null; toggle.on() }, 700) }
  }
  /** Zeile ohne Zuordnung (Baumkopf, Jahr, Monat, „Unbekannt“): kein Ziel, aber Verweilen klappt sie auf. */
  const hoverOpen = (key: string, toggle?: { open: boolean; on: () => void }) => ({
    onDragOver: () => startExpand(key, toggle),
    onDragLeave: (e: React.DragEvent) => { if (!e.currentTarget.contains(e.relatedTarget as globalThis.Node | null)) stopExpand(key) },
  })
  const assign = async (ids: number[], entry: Entry) => {
    const body = entry.kind === 'tag' ? { addTags: [entry.id] } : entry.kind === 'correspondent' || entry.kind === 'doctype' ? { [entry.kind]: entry.id } : null
    // Dokumente in der Warteschlange lässt der Server aus
    const ready = ids.filter(id => docs?.find(d => d.id === id)?.state !== 'queued')
    if (!body || !ready.length) return
    if (!await change(ready, { ...body, batch: true })) return
    // Die Dokumente können aus der gezeigten Liste verschwinden: keine unsichtbare Auswahl stehen lassen
    setChecked(new Set())
    notify(t(ready.length === 1 ? '1 Dokument: {kind} „{name}“' : '{n} Dokumente: {kind} „{name}“', { n: ready.length, kind: kindLabel(entry.kind), name: entry.name }))
  }
  const entryDrop = (entry: Entry, key: string, toggle?: { open: boolean; on: () => void }) => {
    const base = act.dnd(entry)
    return {
      ...base,
      onDragOver: (e: React.DragEvent) => {
        startExpand(key, toggle)
        if (e.dataTransfer.types.includes(DRAG_DOCS)) {
          // Nur im eigenen Bereich zuordnen: Dokumente der Dateiablage nicht auf einen Baum im Tresor (und umgekehrt)
          if (entry.kind === 'role' || (Number(key.split('|')[0]) >= TRESOR_TREE) !== inTresor) return
          e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; setDropOn(key)
          return
        }
        base.onDragOver(e)
      },
      onDragLeave: (e: React.DragEvent) => {
        // Über die Kinder der Zeile hinweg gilt sie nicht als verlassen
        if (e.currentTarget.contains(e.relatedTarget as globalThis.Node | null)) return
        stopExpand(key); setDropOn(k => (k === key ? null : k)); base.onDragLeave()
      },
      onDrop: (e: React.DragEvent) => {
        stopExpand()
        const raw = e.dataTransfer.getData(DRAG_DOCS)
        if (!raw) { base.onDrop(e); return }
        e.preventDefault(); setDropOn(null)
        try { assign(JSON.parse(raw) as number[], entry) } catch { /* fremde Daten */ }
      },
    }
  }

  // Kontextmenü an einem Dokument: gilt für die markierten, wenn es dazugehört, sonst für dieses eine
  const docMenu = useContextMenu<number[]>()
  const docContext = (d: Doc) => ({ onContextMenu: (e: React.MouseEvent) => docMenu.open(e, checked.has(d.id) ? [...checked] : [d.id]) })

  // ---------------- Seitenleiste ----------------

  // Einträge (Korrespondenten, Dokumenttypen, Tags) im Baum: Kontextmenü, „…“, Ziehen auf einen anderen = darunter einordnen
  const act = useEntryActions(entries, () => { loadEntries(); loadList(); Object.keys(treeData).forEach(k => loadTree(Number(k))) })
  const [treePicked, setTreePicked] = useState<Set<string>>(new Set())
  const navRow = (key: string, active: boolean, icon: React.ReactNode, label: string, count: number | null, onClick: () => void, depth = 0,
    toggle?: { open: boolean; on: () => void }, entry?: Entry, menu?: (e: React.MouseEvent) => void, drop?: object, inTree = false) => (
    <div key={key} className={mergeClasses(nv.row, (active || (entry && treePicked.has(entry.id))) && nv.active)} style={{ paddingLeft: 8 + depth * 14, ...((entry && act.over === entry.id) || dropOn === key ? { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: -2 } : {}) }}
      onClick={ev => {
        // Strg+Klick im Baum: mehrere Einträge auswählen, etwa zum Löschen
        if (entry && (ev.ctrlKey || ev.metaKey)) { setTreePicked(p => { const x = new Set(p); if (x.has(entry.id)) x.delete(entry.id); else x.add(entry.id); return x }); return }
        setTreePicked(new Set()); onClick(); if (mobile) closeSide()
      }}
      {...(drop ?? {})}
      {...(entry ? { ...entryDrop(entry, key, toggle), onContextMenu: (e: React.MouseEvent) => {
        const list = treePicked.has(entry.id) && treePicked.size > 1 ? entries.filter(x => treePicked.has(x.id)) : null
        act.openMenu(e, entry.kind, entry, list ? [{ label: t('{n} Einträge löschen', { n: list.length }), onClick: async () => { if (await act.removeMany(list)) setTreePicked(new Set()) } }] : undefined)
      } } : menu ? { onContextMenu: menu } : {})}>
      {/* Baum: Platz für den Pfeil immer frei, das Symbol steht bei allen Einträgen an derselben Stelle */}
      {(toggle || inTree) && <span onClick={toggle ? e => { e.stopPropagation(); toggle.on() } : undefined} style={{ display: 'inline-flex', width: 16, flexShrink: 0 }}>{toggle && (toggle.open ? <ChevronDown16Regular /> : <ChevronRight16Regular />)}</span>}
      {icon}
      <span className={nv.name}>{label}</span>
      {entry && <Button className="dm-more" appearance="transparent" size="small" icon={<MoreHorizontal20Regular />} aria-label={t('Weitere Aktionen')}
        onClick={e => act.openMenu(e, entry.kind, entry)} />}
      {count !== null && <span className={nv.count}>{count}</span>}
    </div>
  )
  const treeRows = (i: number, nodes: Node[], path: { level: string; key: string | null; name: string }[], depth: number): React.ReactNode[] =>
    nodes.flatMap(n => {
      const p = [...path, { level: n.level, key: n.key, name: n.name }]
      const id = `${i}|` + p.map(x => `${x.level}:${x.key}`).join('/')
      const isOpen = open.has(id)
      const tresorTree = i >= TRESOR_TREE
      const active = nav.kind === 'tree' && nav.tree === i % TRESOR_TREE && !!nav.tresor === tresorTree && nav.path.map(x => `${x.level}:${x.key}`).join('/') === p.map(x => `${x.level}:${x.key}`).join('/')
      const entry = n.key !== null && (n.level === 'correspondent' || n.level === 'doctype' || n.level === 'tag' || n.level === 'role') ? byId.get(n.key) : undefined
      const toggle = n.children.length ? { open: isOpen, on: () => setOpen(o => { const x = new Set(o); if (x.has(id)) x.delete(id); else x.add(id); return x }) } : undefined
      const row = navRow(id, active, <Folder20Regular style={tresorTree ? { color: OPEN_COLOR } : undefined} />, keyName(n.level, n.key, n.name), n.count,
        () => setNav({ kind: 'tree', tree: i % TRESOR_TREE, path: p, tresor: tresorTree }), depth,
        toggle, entry, undefined, entry ? undefined : hoverOpen(id, toggle), true)
      return [row, ...(isOpen ? treeRows(i, n.children, p, depth + 1) : [])]
    })

  /** Köpfe der Bäume samt geöffneten Zweigen; base 0 = Dateiablage, TRESOR_TREE = dieselben Bäume über dem Tresor (grün, eingerückt). */
  const treeHeads = (base: number, depth: number) => trees.map((tr, n) => {
    const i = base + n
    const id = `tree-${i}`
    const isOpen = open.has(id)
    const green = base >= TRESOR_TREE ? { color: OPEN_COLOR } : undefined
    const toggle = { open: isOpen, on: () => setOpen(o => { const x = new Set(o); if (x.has(id)) x.delete(id); else { x.add(id); if (!treeData[i]) loadTree(i) } return x }) }
    return [
      <div key={id} className={nv.row} style={{ paddingLeft: 8 + depth * 14 }}
        onClick={toggle.on} {...hoverOpen(id, toggle)}
        onContextMenu={e => {
          // Baumkopf: „Neuer Eintrag …“ für die erste Ebene (wenn sie Einträge hat) und „Baum bearbeiten …“
          const first = tr.levels[0]
          if (first === 'correspondent' || first === 'doctype' || first === 'tag' || first === 'role') act.openMenu(e, first, null, [{ label: t('Baum bearbeiten …'), onClick: () => setEditTree(n) }])
          else { e.preventDefault(); setEditTree(n) }
        }}>
        {isOpen ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
        <TextBulletListTree20Regular style={green} />
        <span className={nv.name}>{tr.name}</span>
        <Button className="dm-more" appearance="transparent" size="small" icon={<Settings20Regular />} aria-label={t('Baum bearbeiten')}
          onClick={e => { e.stopPropagation(); setEditTree(n) }} />
      </div>,
      ...(isOpen ? (treeData[i] ? treeRows(i, treeData[i], [], depth + 1) : [<Spinner key={id + 's'} size="tiny" />]) : []),
    ]
  })

  const side = (
    <SidePane as="aside" className={mergeClasses(s.side, nv.pane)} open={sidePane} onClose={closeSide} width={{ key: 'dms.side', def: 240 }}>
      {/* Zwei Bereiche: Ablage und Tresor. Gezeigt werden nur Dokumente und Bäume des gewählten; Dokumente auf den anderen ziehen = dorthin verschieben */}
      {state?.tresor && <div className={s.areas}>
        <button type="button" className={mergeClasses(s.area, !inTresor && s.areaFiles)} onClick={() => switchArea(false)} {...areaDrop('area-files', false)}
          style={dropOn === 'area-files' ? { outline: `2px solid ${tokens.colorBrandStroke1}` } : undefined}>
          <Folder20Regular />{t('Ablage')}
        </button>
        <button type="button" className={mergeClasses(s.area, inTresor && s.areaTresor)} onClick={() => switchArea(true)} {...areaDrop('area-tresor', true)}
          onContextMenu={e => tresorMenu.open(e, null)} title={state.tresorOpen ? undefined : t('Tresor öffnen')}
          style={dropOn === 'area-tresor' ? { outline: `2px solid ${OPEN_COLOR}` } : undefined}>
          {state.tresorOpen ? <LockOpen20Regular style={{ color: OPEN_COLOR }} /> : <LockClosed20Regular />}{t('Tresor')}
          {!state.tresorOpen && !!state.tresorWaiting && <span className={nv.count}>{state.tresorWaiting}</span>}
        </button>
      </div>}
      <ContextMenu at={tresorMenu.ctx?.at} onClose={tresorMenu.close}>
        <MenuItem icon={state?.tresorOpen ? <LockClosed20Regular /> : <LockOpen20Regular />} onClick={toggleTresor}>
          {state?.tresorOpen ? t('Tresor schließen') : t('Tresor öffnen')}
        </MenuItem>
      </ContextMenu>
      <div className={mergeClasses(nv.section, !state?.tresor && nv.first)}>{t('Dokumente')}</div>
      {!inTresor ? <>
        {navRow('inbox', nav.kind === 'inbox', <MailInbox20Regular />, t('Inbox'), state?.inbox ?? null, () => setNav({ kind: 'inbox' }))}
        {(!!state?.queued || state?.paused || nav.kind === 'queue') && navRow('queue', nav.kind === 'queue',
          state?.paused ? <Pause20Regular /> : <Hourglass20Regular />, state?.paused ? t('Warteschlange (angehalten)') : t('Warteschlange'), state?.queued || null,
          () => setNav({ kind: 'queue' }))}
        {navRow('all', nav.kind === 'all', <DocumentBulletList20Regular />, t('Alle Dokumente'), state?.all || null, () => setNav({ kind: 'all' }))}
        {navRow('fixed', nav.kind === 'fixed', <LockClosed20Regular style={{ color: FIXED_COLOR }} />, t('Fixiert'), null, () => setNav({ kind: 'fixed' }))}
        {(plDiff > 0 || nav.kind === 'paperless') && navRow('paperless', nav.kind === 'paperless', <ArrowSwap20Regular />, t('Abweichung von paperless'), plDiff, () => setNav({ kind: 'paperless' }))}
      </> : <>
        {/* Tresor: die Symbole grün - man sieht, in welchem Bereich man ist */}
        {navRow('tresorInbox', nav.kind === 'tresorInbox', <MailInbox20Regular style={{ color: OPEN_COLOR }} />, t('Inbox'), state?.tresorInbox || null, () => setNav({ kind: 'tresorInbox' }))}
        {(!!state?.tresorQueued || nav.kind === 'tresorQueue') && navRow('tresorQueue', nav.kind === 'tresorQueue',
          state?.paused ? <Pause20Regular style={{ color: OPEN_COLOR }} /> : <Hourglass20Regular style={{ color: OPEN_COLOR }} />,
          state?.paused ? t('Warteschlange (angehalten)') : t('Warteschlange'), state?.tresorQueued || null, () => setNav({ kind: 'tresorQueue' }))}
        {navRow('tresorFixed', nav.kind === 'tresorFixed', <LockClosed20Regular style={{ color: FIXED_COLOR }} />, t('Fixiert'), null, () => setNav({ kind: 'tresorFixed' }))}
        {navRow('tresor', nav.kind === 'tresor', <DocumentBulletList20Regular style={{ color: OPEN_COLOR }} />, t('Alle Dokumente'), state?.tresorAll || null, () => setNav({ kind: 'tresor' }))}
      </>}
      <div className={nv.section}><span style={{ flex: 1 }}>{t('Bäume')}</span>
        <Button appearance="subtle" size="small" icon={<Add20Regular />} aria-label={t('Neuer Baum')} title={t('Neuer Baum')} onClick={() => setEditTree('new')} />
      </div>
      {treeHeads(inTresor ? TRESOR_TREE : 0, 0)}
      <div className={nv.section}>{t('Verwalten')}</div>
      {navRow('m-c', nav.kind === 'manage' && nav.list === 'correspondent', <Person20Regular />, t('Korrespondenten'), entries.filter(e => e.kind === 'correspondent').length, () => setNav({ kind: 'manage', list: 'correspondent' }))}
      {navRow('m-d', nav.kind === 'manage' && nav.list === 'doctype', <NumberSymbol20Regular />, t('Dokumenttypen'), entries.filter(e => e.kind === 'doctype').length, () => setNav({ kind: 'manage', list: 'doctype' }))}
      {navRow('m-t', nav.kind === 'manage' && nav.list === 'tag', <Tag20Regular />, t('Tags'), entries.filter(e => e.kind === 'tag').length, () => setNav({ kind: 'manage', list: 'tag' }))}
      {navRow('m-r', nav.kind === 'manage' && nav.list === 'role', <Ribbon20Regular />, t('Rollen'), entries.filter(e => e.kind === 'role').length, () => setNav({ kind: 'manage', list: 'role' }))}
      {navRow('settings', false, <Settings20Regular />, t('Quellen und Ablageprofile'), null, () => { location.hash = '#/settings/dms-sources' })}
      {act.ui}
    </SidePane>
  )

  // ---------------- Liste ----------------

  const heading = nav.kind === 'fixed' ? t('Fixiert') : nav.kind === 'tresorFixed' ? t('Tresor') + ' › ' + t('Fixiert') : nav.kind === 'inbox' ? t('Inbox') : nav.kind === 'queue' ? t('Warteschlange') : nav.kind === 'all' ? t('Alle Dokumente') : nav.kind === 'tresor' ? t('Tresor') + ' › ' + t('Alle Dokumente')
    : nav.kind === 'tresorInbox' ? t('Tresor') + ' › ' + t('Inbox') : nav.kind === 'tresorQueue' ? t('Tresor') + ' › ' + t('Warteschlange') : nav.kind === 'paperless' ? t('Abweichung von paperless')
    : nav.kind === 'entry' ? `${kindLabel(nav.entry.kind)}: ${fullName(entries, nav.entry.id)}`
    : nav.kind === 'tree' ? [trees[nav.tree]?.name, ...nav.path.map(p => keyName(p.level, p.key, p.name))].join(' › ')
    : ''
  const tagChip = (id: string, auto: boolean) => {
    const e = byId.get(id)
    if (!e) return null
    return <span key={id} className={mergeClasses(s.chip, auto && s.auto)} style={{ backgroundColor: e.color ?? '#d0d0d0' }} title={auto ? t('Noch nicht bestätigt') : undefined}>{e.name}</span>
  }
  const all = docs ?? []
  allIds.current = all.map(d => d.id)
  const allChecked = all.length > 0 && all.every(d => checked.has(d.id))
  const ids = [...checked]
  const download = (list: number[]) => {
    downloadDocs(list, inTresor)
    if (inTresor) notify(t('Aus dem Tresor heruntergeladen'), t('Die Datei liegt jetzt unverschlüsselt in deinem Download-Ordner.'))
  }
  // Unbestätigte (Inbox) werden bestätigt - das legt sie nach dem Ordnermuster ab; „Nach Vorgabe ablegen“ gilt nur für Bestätigte
  const target = checked.size ? (docs ?? []).filter(d => checked.has(d.id)) : doc ? [doc] : []
  const toConfirm = target.filter(d => d.inbox).map(d => d.id)
  const toRefile = target.filter(d => !d.inbox && d.state === 'done').map(d => d.id)
  const inboxView = isInbox || nav.kind === 'paperless'

  const listBody = () => {
    if (docs === null) return <Spinner style={{ margin: 24 }} />
    if (docs.length === 0) return (
      <div className={s.empty}>
        <DocumentText20Regular />
        <Text>{isInbox ? t('Die Inbox ist leer.') : isQueue ? t('Die Warteschlange ist leer.') : query ? t('Nichts gefunden.') : t('Noch keine Dokumente.')}</Text>
        {isInbox && !!waiting && <Text size={200} className={s.muted}>{waiting === 1 ? t('1 Dokument wartet noch auf die Verarbeitung (Warteschlange).') : t('{n} Dokumente warten noch auf die Verarbeitung (Warteschlange).', { n: waiting })}</Text>}
        {isInbox && <Text size={200} className={s.muted}>{t('Dokumente kommen über Hochladen, Ziehen und Ablegen hierher oder über eine Quelle (überwachter Ordner).')}</Text>}
      </div>
    )
    const pick = (d: Doc) => { setCurrent(d.id); setDetail(true) }
    // Eine Zelle der Liste (Spalte col); auf dem Handy nur der Titel mit Datum, Korrespondent und Dokumenttyp darunter
    const cell = (d: Doc, col: string) => {
      switch (col) {
        case 'date': return <span className={s.cell}>{fmtDay(d.docDate)}</span>
        case 'amount': return <span className={s.cell} style={{ textAlign: 'right' }}>{fmtAmount(d.amount)}</span>
        case 'title': return (
          <span className={s.cell}>
            {d.state === 'queued' ? <>{state?.paused ? <Pause16Regular style={{ verticalAlign: 'middle', marginRight: 6 }} />
              : <Spinner size="extra-tiny" style={{ display: 'inline-flex', marginRight: 6 }} />}{d.originalName}</>
              : <>{d.title || d.originalName}{d.byAi && <Sparkle16Regular style={{ marginLeft: 4, verticalAlign: 'middle' }} />}{d.error && <ErrorMark text={d.error} />}</>}
            <div className={s.narrow}>{[fmtDay(d.docDate), d.correspondent && byId.get(d.correspondent)?.name, d.doctype && byId.get(d.doctype)?.name].filter(Boolean).join(' · ')}</div>
          </span>)
        case 'correspondent': return <span className={mergeClasses(s.cell, d.autoCorrespondent && s.auto)}>{d.correspondent ? byId.get(d.correspondent)?.name : ''}</span>
        case 'doctype': return <span className={mergeClasses(s.cell, d.autoDoctype && s.auto)}>{d.doctype ? byId.get(d.doctype)?.name : ''}</span>
        case 'tags': return <span className={s.chips}>{d.tags.map(x => tagChip(x.id, x.auto))}</span>
        // Rolle: die geltende normal, eine frühere Version blass mit Hinweis
        case 'role': return <span className={mergeClasses(s.cell, !!d.role && !d.roleCurrent && s.auto)} title={d.role && !d.roleCurrent ? t('Frühere Version') : undefined}>{d.role ? byId.get(d.role)?.name : ''}</span>
        case 'imported': return <span className={s.cell}>{d.imported ? new Date(d.imported).toLocaleString(lang === 'en' ? 'en-GB' : 'de-DE', { dateStyle: 'short', timeStyle: 'short' }) : ''}</span>
        case 'name': return <span className={s.cell} title={d.originalName}>{d.originalName}</span>
        case 'type': return <span className={s.cell} title={d.mime ?? undefined}>{typeLabel(d.mime, d.originalName)}</span>
        case 'size': return <span className={s.cell} style={{ textAlign: 'right' }}>{d.size ? fmtSize(d.size) : ''}</span>
        default: return <span className={s.cell}>{sourceLabel(d.source)}</span>
      }
    }
    // Mit Umschalt: alles zwischen dem zuletzt angeklickten und diesem wie dieses
    const check = (d: Doc, on: boolean) => {
      const from = shiftNext.current && lastCheck.current != null ? docs.findIndex(x => x.id === lastCheck.current) : -1
      const to = docs.findIndex(x => x.id === d.id)
      shiftNext.current = false
      lastCheck.current = d.id
      setChecked(c => {
        const n = new Set(c)
        const range = from >= 0 && to >= 0 ? docs.slice(Math.min(from, to), Math.max(from, to) + 1) : [d]
        for (const x of range) if (on) n.add(x.id); else n.delete(x.id)
        return n
      })
    }
    const more = docs.length < total && <div className={s.more}><Button onClick={() => loadList(true)}>{t('Weitere laden ({n} von {total})', { n: docs.length, total })}</Button></div>
    const sub = (d: Doc) => [fmtDay(d.docDate), d.correspondent && byId.get(d.correspondent)?.name].filter(Boolean).join(' · ')
    // Kacheln: Vorschau der ersten Seite, Titel, Datum und Korrespondent
    if (tiles) return (
      <div className={s.list}>
        <div className={s.grid} style={{ gridTemplateColumns: `repeat(auto-fill, minmax(${tilePx}px, 1fr))` }}>
          {docs.map(d => (
            <div key={d.id} {...dragDoc(d)} {...docContext(d)} className={mergeClasses(s.tile, current === d.id && s.tileSel)} onClick={() => pick(d)}
              title={[d.title || d.originalName, sub(d)].filter(Boolean).join(' · ')}>
              <div className={mergeClasses(s.thumb, d.fixed ? (inTresor ? s.fixedTresorThumb : s.fixedThumb) : inTresor && s.tresorThumb)}><DocThumb doc={d} tresor={inTresor} px={tilePx} paused={!!state?.paused} /></div>
              <Checkbox className={s.tileCheck} checked={checked.has(d.id)} onClick={e => { e.stopPropagation(); shiftNext.current = e.shiftKey }} onChange={(_, x) => check(d, !!x.checked)} />
              <span className={s.tileTitle}>{d.state === 'queued' ? d.originalName : d.title || d.originalName}
                {d.byAi && <Sparkle16Regular style={{ marginLeft: 4, verticalAlign: 'middle' }} />}{d.error && <ErrorMark text={d.error} />}</span>
              <span className={s.tileSub}>{sub(d)}</span>
            </div>
          ))}
        </div>
        {more}
      </div>
    )
    return (
      <div className={s.list}>
        <div className={mergeClasses(s.row, s.thead)} style={mobile ? undefined : { gridTemplateColumns: grid.template, minWidth: 'max-content' }} onContextMenu={grid.openMenu}>
          <Checkbox checked={allChecked ? true : checked.size ? 'mixed' : false} onChange={() => setChecked(allChecked ? new Set() : new Set(all.map(d => d.id)))} />
          {mobile ? <span>{t('Titel')}</span> : grid.cells}
        </div>
        {grid.columnMenu}
        {docs.map(d => (
          <div key={d.id} {...dragDoc(d)} {...docContext(d)} className={mergeClasses(s.row, current === d.id && s.sel, d.fixed ? (inTresor ? s.fixedTresorRow : s.fixedRow) : inTresor && s.tresorRow)} style={mobile ? undefined : { gridTemplateColumns: grid.template, minWidth: 'max-content' }} onClick={() => pick(d)}>
            <Checkbox checked={checked.has(d.id)} onClick={e => { e.stopPropagation(); shiftNext.current = e.shiftKey }} onChange={(_, x) => check(d, !!x.checked)} />
            {(mobile ? ['title'] : grid.cols.map(c => c.key)).map(k => <span key={k} style={{ display: 'contents' }}>{cell(d, k)}</span>)}
          </div>
        ))}
        {more}
      </div>
    )
  }

  // Bearbeitungsansicht (offenes Dokument am PC): die Liste schmal rechts mit kleiner Vorschau, Klick wechselt das Dokument
  const miniList = () => (
    <aside className={s.side2}>
      <div className={s.head} style={{ padding: '10px 12px 6px' }}>
        <span className={s.title} style={{ fontSize: 16, minWidth: 0 }}>{heading}<Text className={s.muted} style={{ marginLeft: 8, fontWeight: 400 }}>{total || ''}</Text></span>
        <Input size="small" style={{ width: '100%' }} contentBefore={<Search20Regular />} placeholder={t('Inhalt und Titel durchsuchen')} value={query} onChange={(_, d) => setQuery(d.value)} />
      </div>
      <div className={s.list}>
        {(docs ?? []).map(d => (
          <div key={d.id} {...dragDoc(d)} {...docContext(d)} className={mergeClasses(s.mini, current === d.id && s.sel, d.fixed ? (inTresor ? s.fixedTresorRow : s.fixedRow) : inTresor && s.tresorRow)} onClick={() => setCurrent(d.id)}
            title={[d.title || d.originalName, fmtDay(d.docDate)].filter(Boolean).join(' · ')}>
            <div className={s.miniThumb}><DocThumb doc={d} tresor={inTresor} px={40} paused={!!state?.paused} /></div>
            <div style={{ minWidth: 0 }}>
              <div className={s.tileTitle} style={{ fontWeight: d.inbox ? 600 : 400 }}>{d.state === 'queued' ? d.originalName : d.title || d.originalName}</div>
              <div className={s.tileSub}>{[fmtDay(d.docDate), d.correspondent && byId.get(d.correspondent)?.name].filter(Boolean).join(' · ')}</div>
            </div>
          </div>
        ))}
        {docs && docs.length < total && <div className={s.more}><Button size="small" onClick={() => loadList(true)}>{t('Weitere laden ({n} von {total})', { n: docs.length, total })}</Button></div>}
      </div>
    </aside>
  )
  const editing = detail && !!doc && nav.kind !== 'manage' && !mobile

  const content = () => {
    if (nav.kind === 'manage') return <ListsView kind={nav.list} entries={entries} onChanged={() => { loadEntries(); loadList() }}
      onShow={e => setNav({ kind: 'entry', entry: e })} />
    return (
      <>
        <div className={s.head}>
          <span className={s.title}>{heading}<Text className={s.muted} style={{ marginLeft: 8, fontWeight: 400 }}>{total || ''}</Text></span>
          {isQueue && <Button icon={state?.paused ? <Play20Regular /> : <Pause20Regular />} onClick={() => pause(!state?.paused)}>
            {state?.paused ? t('Fortsetzen') : t('Anhalten')}</Button>}
          <Input className={s.search} contentBefore={<Search20Regular />} placeholder={t('Inhalt und Titel durchsuchen')} value={query} onChange={(_, d) => setQuery(d.value)}
            contentAfter={query ? <Button appearance="transparent" size="small" icon={<Dismiss20Regular />} aria-label={t('Suche leeren')} onClick={() => setQuery('')} /> : undefined} />
        </div>
        {/* Vor dem geschlossenen Tresor Wartendes: in der Inbox und Warteschlange der Dateiablage sichtbar, mit Knopf zum Öffnen */}
        {(nav.kind === 'inbox' || nav.kind === 'queue') && state?.tresor && !state.tresorOpen && !!state.tresorWaiting && <MessageBar intent="info" style={{ margin: '0 20px 8px' }}>
          <MessageBarBody>{state.tresorWaiting === 1 ? t('1 Datei wartet, bis der Tresor geöffnet ist.') : t('{n} Dateien warten, bis der Tresor geöffnet ist.', { n: state.tresorWaiting })}</MessageBarBody>
          <MessageBarActions><Button size="small" icon={<LockOpen20Regular />} onClick={toggleTresor}>{t('Tresor öffnen')}</Button></MessageBarActions>
        </MessageBar>}
        {/* Markiert: die Aktionen an Stelle der Filterzeile, gleich hoch - die Liste darunter bleibt, wo sie ist */}
        {checked.size > 0 ? <BatchBar ids={ids} entries={entries} inbox={toConfirm.length > 0}
          onMoveArea={state?.tresor ? () => moveArea(ids) : undefined} tresor={inTresor} onTakePaperless={nav.kind === 'paperless' ? () => takePaperless(ids) : undefined} onChange={body => change(ids, { ...body, batch: true })}
          onConfirm={() => confirm(toConfirm)} onRefile={!inboxView && toRefile.length ? () => refile(toRefile) : undefined} onDelete={() => remove(ids)} onClear={() => setChecked(new Set())} />
        : <div className={s.filters}>
          <Text size={200} className={s.muted}>{t('Dokumentdatum')}</Text>
          <Dropdown size="small" style={{ minWidth: 150 }} placeholder={t('Zeitraum')} value={presetLabel(range)}
            selectedOptions={PRESETS.filter(p => presetRange(p).docFrom === range.docFrom && !range.docTo)} onOptionSelect={(_, d) => setRange(presetRange(d.optionValue as Preset))}>
            {PRESETS.map(p => <Option key={p} value={p}>{presetName(p)}</Option>)}
          </Dropdown>
          <Input className={s.date} type="date" value={range.docFrom ?? ''} onChange={(_, d) => setRange(r => ({ ...r, docFrom: d.value || undefined }))} aria-label={t('von')} />
          <Text size={200} className={s.muted}>–</Text>
          <Input className={s.date} type="date" value={range.docTo ?? ''} onChange={(_, d) => setRange(r => ({ ...r, docTo: d.value || undefined }))} aria-label={t('bis')} />
          {(range.docFrom || range.docTo) && <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} onClick={() => setRange({})}>{t('Zurücksetzen')}</Button>}
        </div>}
        {listBody()}
      </>
    )
  }

  return (
    <div className={s.outer}>
      <RibbonHeader area="dms" onMenu={toggleSide}
        start={<>
          <RibbonButton primary icon={<ArrowUpload20Regular />} label={t('Hochladen')} disabled={busy} onClick={() => fileInput.current?.click()} />
          <RibbonDivider />
          <RibbonButton icon={<Checkmark20Regular />} label={t('Bestätigen')} title={t('Angaben übernehmen und nach dem Ordnermuster ablegen')}
            disabled={!toConfirm.length} onClick={() => confirm(toConfirm)} />
          {!inboxView && <RibbonButton icon={<FolderArrowRight20Regular />} label={t('Nach Vorgabe ablegen')} title={t('Bestätigte Dokumente wieder nach Ordner- und Namensmuster ablegen, auch von Hand verschobene')}
            disabled={!toRefile.length} onClick={() => refile(toRefile)} />}
          <RibbonButton icon={<ArrowDownload20Regular />} label={t('Herunterladen')} disabled={!checked.size && !doc} onClick={() => download(checked.size ? ids : doc ? [doc.id] : [])} />
          <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!checked.size && !doc} onClick={() => remove(checked.size ? ids : doc ? [doc.id] : [])} />
          {doc && !checked.size && doc.state !== 'queued' && <RibbonButton icon={doc.fixed ? <LockOpen20Regular /> : <LockClosed20Regular />} label={doc.fixed ? t('Fixierung aufheben') : t('Fixieren')} onClick={() => setFixed([doc], !doc.fixed)} />}
          <RibbonDivider />
          <RibbonButton icon={<ArrowSync20Regular />} label={t('Aktualisieren')} onClick={refresh} />
          <RibbonButton icon={<Settings20Regular />} label={t('Quellen und Ablageprofile')} onClick={() => { location.hash = '#/settings/dms-sources' }} />
        </>}
        view={<>
          <RibbonButton icon={<AppsList20Regular />} label={t('Liste')} pressed={!tiles} onClick={() => update({ dmsView: 'list' })} />
          <RibbonButton icon={<Grid20Regular />} label={t('Kacheln')} pressed={tiles} onClick={() => update({ dmsView: 'tiles' })} />
          <Menu>
            <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ZoomIn20Regular />} label={t('Kachelgröße')} disabled={!tiles} /></MenuTrigger>
            <MenuPopover>
              {/* Wie in der Galerie: stufenlos */}
              <div style={{ padding: '10px 14px', width: 260 }}>
                <Text size={200} block>{t('Kachelgröße: {px} px', { px: tilePx })}</Text>
                <Slider min={120} max={400} step={4} value={tilePx} onChange={(_, d) => update({ dmsTilePx: d.value })} style={{ width: '100%' }} aria-label={t('Kachelgröße')} />
              </div>
            </MenuPopover>
          </Menu>
          <RibbonDivider />
          <RibbonButton icon={<PanelRight20Regular />} label={t('Detailbereich')} pressed={detail} onClick={() => setDetail(d => !d)} />
        </>} />
      <input ref={fileInput} type="file" multiple hidden onChange={e => { if (e.target.files) upload(e.target.files); e.target.value = '' }} />
      {state && !state.ocr && <Text size={200} className={s.muted} style={{ padding: '4px 20px' }}>{t('Text in Scans und Bildern wird auf diesem Server nicht erkannt. Das lässt sich in der Verwaltung einrichten.')}</Text>}
      <div className={s.root}>
        {side}
        {editing && doc ? <>
          <DocDetail key={doc.id} wide doc={doc} entries={entries} onChange={body => change([doc.id], body)} onConfirm={() => confirm([doc.id])} onTakePaperless={() => takePaperless([doc.id])} tresor={inTresor}
            onEntriesChanged={loadEntries} onClose={() => setCurrent(null)} onReload={refresh} />
          {miniList()}
        </> : <>
        <div className={mergeClasses(s.main, drag && s.drop)}
          onDragOver={e => { if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDrag(true) } }}
          onDragLeave={() => setDrag(false)}
          onDrop={e => {
            e.preventDefault(); setDrag(false)
            // Ordner: alle Dateien darin samt Unterordnern (die Einträge nur im drop-Ereignis abholbar, gelesen wird danach)
            const entries = entriesOf(e.dataTransfer)
            if (entries.some(x => x.isDirectory)) readEntries(entries).then(u => upload(u.map(x => x.file))).catch(fail)
            else if (e.dataTransfer.files.length) upload(e.dataTransfer.files)
          }}>
          {content()}
        </div>
        {detail && doc && nav.kind !== 'manage' &&
          <DocDetail key={doc.id} doc={doc} entries={entries} onChange={body => change([doc.id], body)} onConfirm={() => confirm([doc.id])} onTakePaperless={() => takePaperless([doc.id])} tresor={inTresor}
            onEntriesChanged={loadEntries} onClose={() => setCurrent(null)} onReload={refresh} />}
        </>}
      </div>
      <ContextMenu at={docMenu.ctx?.at} onClose={docMenu.close}>
        {(() => {
          const ids = docMenu.ctx?.data ?? []
          const list = (docs ?? []).filter(d => ids.includes(d.id))
          const one = list.length === 1 ? list[0] : null
          return <>
            {one && <MenuItem icon={<FolderOpen20Regular />} onClick={() => setCurrent(one.id)}>{t('Öffnen')}</MenuItem>}
            {list.some(d => d.inbox) && <MenuItem icon={<Checkmark20Regular />} onClick={() => confirm(list.filter(d => d.inbox).map(d => d.id))}>{t('Bestätigen')}</MenuItem>}
            <MenuItem icon={<FolderArrowRight20Regular />} onClick={() => refile(ids)}>{t('Nach Vorgabe ablegen')}</MenuItem>
            {state?.tresor && <MenuItem icon={inTresor ? <LockOpen20Regular /> : <LockClosed20Regular />} onClick={() => moveArea(ids)}>
              {inTresor ? t('Aus dem Tresor holen') : t('In den Tresor verschieben')}</MenuItem>}
            <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download(ids)}>{t('Herunterladen')}</MenuItem>
            {list.some(d => !d.fixed && d.state !== 'queued') && <MenuItem icon={<LockClosed20Regular />} onClick={() => setFixed(list.filter(d => !d.fixed && d.state !== 'queued'), true)}>{t('Fixieren')}</MenuItem>}
            {list.some(d => d.fixed) && <MenuItem icon={<LockOpen20Regular />} onClick={() => setFixed(list.filter(d => d.fixed), false)}>{t('Fixierung aufheben')}</MenuItem>}
            <MenuItem icon={<Delete20Regular />} onClick={() => remove(ids)}>{ids.length > 1 ? t('{n} Dokumente löschen', { n: ids.length }) : t('Löschen')}</MenuItem>
          </>
        })()}
      </ContextMenu>
      {moving && <MoveAreaDialog docs={moving} toTresor={!inTresor} onClose={() => setMoving(null)}
        onMoved={text => { setMoving(null); notify(text); setChecked(new Set()); setCurrent(null); refresh() }} />}
      {editTree !== null && <TreeDialog trees={trees} index={editTree === 'new' ? null : editTree}
        onSave={list => { update({ dmsTrees: list }); setTreeData({}); setOpen(new Set()); setEditTree(null) }} onClose={() => setEditTree(null)} />}
    </div>
  )
}

// ---------------- Kachel-Vorschau ----------------

/** Vorschau einer Kachel: PDF als Bild der ersten Seite (vom Server gerendert), Bild direkt, sonst ein Symbol. */
function DocThumb({ doc, tresor, px, paused }: { doc: Doc; tresor: boolean; px: number; paused: boolean }) {
  const [failed, setFailed] = useState(false)
  if (doc.state === 'queued') return paused ? <Pause20Regular /> : <Spinner size="tiny" />
  const placed = doc.fileId || doc.mount
  const isPdf = doc.mime === 'application/pdf' || doc.path?.toLowerCase().endsWith('.pdf')
  // Breite in zwei Stufen, damit der Browser die Bilder beim Ziehen des Reglers nicht neu holt
  const width = px * (window.devicePixelRatio || 1) > 360 ? 720 : 360
  const src = !placed || failed ? null
    : isPdf ? `/api/dms/docs/${doc.id}/page/0?` + q({ width, area: tresor ? 'tresor' : undefined, tile: true, v: doc.path ?? '' })
    : doc.mime?.startsWith('image/') ? contentUrl(doc, tresor, true) : null
  return src ? <img src={src} alt="" loading="lazy" onError={() => setFailed(true)} /> : <DocumentText20Regular />
}

// ---------------- Mehrfachauswahl ----------------

function BatchBar({ ids, entries, inbox, onChange, onConfirm, onRefile, onDelete, onClear, onTakePaperless, onMoveArea, tresor }: {
  ids: number[]; entries: Entry[]; inbox: boolean; onChange: (body: Record<string, unknown>) => void; onConfirm: () => void; onRefile?: () => void
  onDelete: () => void; onClear: () => void; onTakePaperless?: () => void; onMoveArea?: () => void; tresor?: boolean
}) {
  const s = useStyles()
  const pick = (kind: Kind, label: string, onPick: (id: string) => void) => (
    <Dropdown size="small" placeholder={label} value="" selectedOptions={[]} onOptionSelect={(_, d) => d.optionValue !== undefined && onPick(d.optionValue)} style={{ minWidth: 150 }}>
      {kind !== 'tag' && <Option value="">{t('(keiner)')}</Option>}
      {ordered(entries, kind).map(o => <Option key={o.entry.id} value={o.entry.id} text={o.entry.name}><span style={{ paddingLeft: o.depth * 12 }}>{o.entry.name}</span></Option>)}
    </Dropdown>
  )
  return (
    <div className={s.batch}>
      <Text weight="semibold">{t('{n} ausgewählt', { n: ids.length })}</Text>
      {pick('correspondent', t('Korrespondent setzen'), id => onChange({ correspondent: id }))}
      {pick('doctype', t('Dokumenttyp setzen'), id => onChange({ doctype: id }))}
      {pick('tag', t('Tag hinzufügen'), id => onChange({ addTags: [id] }))}
      {pick('tag', t('Tag entfernen'), id => onChange({ removeTags: [id] }))}
      {inbox && <Button size="small" icon={<Checkmark20Regular />} onClick={onConfirm}>{onTakePaperless ? t('DMS übernehmen') : t('Bestätigen')}</Button>}
      {onTakePaperless && <Button size="small" icon={<ArrowSwap20Regular />} onClick={onTakePaperless}>{t('paperless übernehmen')}</Button>}
      {onRefile && <Button size="small" icon={<FolderArrowRight20Regular />} onClick={onRefile}>{t('Nach Vorgabe ablegen')}</Button>}
      {onMoveArea && <Button size="small" icon={tresor ? <LockOpen20Regular /> : <LockClosed20Regular />} onClick={onMoveArea}>{tresor ? t('Aus dem Tresor holen') : t('In den Tresor verschieben')}</Button>}
      <Button size="small" icon={<Delete20Regular />} onClick={onDelete}>{t('Löschen')}</Button>
      <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} onClick={onClear} aria-label={t('Auswahl aufheben')} />
    </div>
  )
}

// ---------------- Detailbereich ----------------

function DocDetail({ doc, entries, onChange, onConfirm, onTakePaperless, onEntriesChanged, onClose, onReload, tresor, wide }: {
  doc: Doc; entries: Entry[]; onChange: (body: Record<string, unknown>) => Promise<unknown>; onConfirm: () => void; onTakePaperless: () => void; onEntriesChanged: () => void; tresor: boolean
  onClose: () => void; onReload: () => void
  /** Bearbeitungsansicht: das Dokument groß, die Felder daneben, an jedem Feld „Aus dem Dokument übernehmen“ */
  wide?: boolean
}) {
  const s = useStyles()
  const { fail } = useApp()
  // Aus einem Rahmen erkannter Name ohne passenden Eintrag: kommt ins Suchfeld der Liste (mit ähnlichen Einträgen), angelegt wird nichts
  const [prefill, setPrefill] = useState<{ kind: Kind; text: string; n: number } | null>(null)
  const pick = usePick({ doc, tresor, entries, onChange, onEntriesChanged, onText: (kind, text) => setPrefill(p => ({ kind, text, n: (p?.n ?? 0) + 1 })) })
  // Mit neuem key erscheint das Feld neu: Text im Suchfeld, Liste offen
  const prefillFor = (kind: Kind) => prefill?.kind === kind ? { key: prefill.n, prefill: prefill.text } : { key: 0, prefill: null }
  // Getippter Name: gleich anlegen und zuordnen; ohne Namen („Neu …“) der Dialog mit Oberbegriff und Regeln
  const createNamed = async (kind: Kind, name: string) => {
    if (!name) { setNewEntry({ kind, name: '' }); return }
    try {
      const created = await api.post<Entry>('/api/dms/lists', { kind, name })
      onEntriesChanged()
      await onChange(kind === 'tag' ? { addTags: [created.id] } : { [kind]: created.id })
    } catch (e) { fail(e) }
  }
  // Beschriftung eines Feldes; in der Bearbeitungsansicht mit Knopf zum Übernehmen aus dem Dokument
  const lab = (text: string, field: PickField) => wide && isPdf && doc.state === 'done'
    ? <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>{text}<PickButton pick={pick} field={field} /></span> : text
  const [title, setTitle] = useState(doc.title)
  const [date, setDate] = useState(doc.docDate ?? '')
  const [amount, setAmount] = useState(doc.amount == null ? '' : moneyText(doc.amount))
  useEffect(() => { setAmount(doc.amount == null ? '' : moneyText(doc.amount)) }, [doc.amount])
  const [text, setText] = useState<string | null>(null)
  const [newEntry, setNewEntry] = useState<{ kind: Kind; name: string } | null>(null)
  // Vorschlag der KI, den es als Eintrag schon gibt (Groß- und Kleinschreibung, Leerzeichen egal): nur noch zuordnen
  const nameKey = (n: string) => n.replace(/\s+/g, '').toLowerCase()
  const suggestionKnown = !!doc.aiSuggestion && entries.some(e => e.kind === 'correspondent' && nameKey(e.name) === nameKey(doc.aiSuggestion!))
  useEffect(() => { setTitle(doc.title); setDate(doc.docDate ?? '') }, [doc.title, doc.docDate])
  // Alle plausiblen Daten aus dem Text zur Auswahl (nicht nach dem Importtag)
  const [dates, setDates] = useState<{ date: string; count: number; context: string; labeled: boolean }[]>([])
  useEffect(() => {
    setDates([])
    if (doc.state !== 'done') return
    api.get<typeof dates>(`/api/dms/docs/${doc.id}/dates` + areaQ(tresor)).then(setDates).catch(() => {})
  }, [doc.id, doc.state, tresor])
  const dayText = (iso: string) => new Date(iso + 'T00:00:00').toLocaleDateString()
  const inline = doc.fileId || doc.mount ? contentUrl(doc, tresor, true) : null
  const isPdf = doc.mime === 'application/pdf' || doc.path?.toLowerCase().endsWith('.pdf')
  const isImage = doc.mime?.startsWith('image/')
  const single = (kind: 'correspondent' | 'doctype', value: string | null, auto: boolean) => {
    return (
      <Field label={lab(kindLabel(kind) + (auto ? ' · ' + t('noch nicht bestätigt') : ''), kind)}>
        <EntrySelect entries={entries} kind={kind} value={value} none={t('(keiner)')} placeholder={t('(keiner)')} key={prefillFor(kind).key} prefill={prefillFor(kind).prefill}
          onPick={id => onChange({ [kind]: id })} onNew={name => createNamed(kind, name)} />
      </Field>
    )
  }
  // Rolle: zu jeder Rolle ein geltendes Dokument - ist sie vergeben, erst nachfragen (wirksam erst mit „Ersetzen“)
  const roleOpts = ordered(entries, 'role')
  const setRole = async (id: string) => {
    if (id === '+') { setNewEntry({ kind: 'role', name: '' }); return }
    if (id) {
      try {
        const h = await api.get<{ holder: { id: number; tresor: boolean; title: string | null; docDate: string | null } | null }>('/api/dms/roles/holder?' + q({ role: id }))
        if (h.holder && !(h.holder.id === doc.id && h.holder.tresor === !!tresor)) {
          const name = entries.find(e => e.id === id)?.name ?? ''
          const text = h.holder.title
            ? t('Ersetzt das bisherige Dokument „{title}“ vom {date} als „{role}“. Es bleibt als frühere Version erhalten.', { title: h.holder.title, date: h.holder.docDate ? dayText(h.holder.docDate) : '', role: name })
            : t('Ersetzt das bisherige Dokument im Tresor als „{role}“. Es bleibt als frühere Version erhalten.', { role: name })
          if (!await askConfirm(t('Rolle zuordnen'), text, t('Ersetzen'))) return
        }
      } catch (e) { fail(e); return }
    }
    onChange({ role: id })
  }
  // Office-Dateien im Editor des Office-Servers, wie in der Dateiablage; Dateien in externen Ordnern öffnet er nicht
  const office = useOffice()
  const [editing, setEditing] = useState(false)
  const fileName = (doc.path ?? '').split('/').pop() ?? ''
  const mode0 = doc.state === 'done' && doc.fileId > 0 && !doc.mount && !isPdf && !isImage ? officeMode({ name: fileName, isDir: false }, office) : null
  // Fixiert: in Office nur ansehen
  const mode = doc.fixed && mode0 ? 'view' : mode0
  // Kein Bild der Seiten und kein Office-Editor: der erkannte Text, damit sich das Dokument prüfen lässt
  const [textShown, setTextShown] = useState('')
  const wantsText = doc.state === 'done' && !isPdf && !isImage && office !== null && !mode
  useEffect(() => {
    setTextShown('')
    if (!wantsText) return
    let live = true
    api.get<{ text: string }>(`/api/dms/docs/${doc.id}/text` + areaQ(tresor)).then(r => { if (live) setTextShown(r.text) }).catch(() => {})
    return () => { live = false }
  }, [doc.id, doc.size, wantsText, tresor])
  const noPreview = textShown.trim() ? <div className={s.textPreview}>
    <Text size={200} className={s.muted}>{t('Erkannter Text')}</Text>
    <Text className={s.textBody}>{textShown}</Text>
  </div> : <div className={s.noPreview}>
    <Text className={s.muted}>{doc.state === 'queued' ? t('Wird verarbeitet …') : t('Keine Vorschau')}</Text>
    {mode && <Button appearance="primary" icon={<Edit20Regular />} onClick={() => setEditing(true)}>{mode === 'edit' ? t('In Office bearbeiten') : t('In Office ansehen')}</Button>}
  </div>
  const form = (
      <div className={s.form}>
        <div className={s.actions} style={{ justifyContent: 'space-between' }}>
          <Text weight="semibold">{doc.inbox ? t('In der Inbox') : t('Geprüft')}</Text>
          <Button appearance="subtle" size="small" icon={<Dismiss20Regular />} aria-label={t('Schließen')} onClick={onClose} />
        </div>
        {doc.error && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{t(doc.error)}</Text>}
        {wide && pick.field && <MessageBar intent="info"><MessageBarBody>{t('Im Dokument einen Rahmen um den Text ziehen')}</MessageBarBody>
          <MessageBarActions><Button size="small" onClick={() => pick.choose(null)}>{t('Abbrechen')}</Button></MessageBarActions></MessageBar>}
        {wide && pick.busy && <Spinner size="tiny" label={t('Text wird gelesen …')} />}
        {wide && pick.last && !pick.busy && !pick.field && <Text size={200} className={s.muted}>{t('Übernommen: „{text}“', { text: pick.last.text })}</Text>}
        {doc.aiSuggestion && <MessageBar intent="info" layout="multiline">
          <MessageBarBody>
            <MessageBarTitle>{suggestionKnown ? t('Korrespondent zuordnen?') : t('Neuer Korrespondent?')}</MessageBarTitle>
            {suggestionKnown ? t('Die KI schlägt „{name}“ vor.', { name: doc.aiSuggestion })
              : t('Die KI schlägt „{name}“ vor. Den Eintrag gibt es noch nicht.', { name: doc.aiSuggestion })}
          </MessageBarBody>
          <MessageBarActions>
            <Button appearance="primary" size="small" icon={<PersonAdd20Regular />}
              onClick={() => api.post(`/api/dms/docs/${doc.id}/adopt-correspondent`).then(() => { onEntriesChanged(); onReload() }).catch(fail)}>
              {suggestionKnown ? t('„{name}“ zuordnen', { name: doc.aiSuggestion }) : t('„{name}“ anlegen und zuordnen', { name: doc.aiSuggestion })}
            </Button>
          </MessageBarActions>
        </MessageBar>}
        <Field label={lab(t('Titel'), 'title')}>
          <Input value={title} onChange={(_, d) => setTitle(d.value)} onBlur={() => title.trim() && title !== doc.title && onChange({ title })}
            onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }} />
        </Field>
        {doc.fixed && <Text weight="semibold" style={{ color: FIXED_COLOR }}><LockClosed20Regular style={{ verticalAlign: 'middle', marginRight: 4 }} />{t('Fixiert')}</Text>}
        <Field label={t('Betrag')}>
          <Input value={amount} inputMode="decimal" contentAfter="€" onChange={(_, d) => setAmount(d.value)} onBlur={() => {
            const cents = amount.trim() ? parseMoney(amount) : null
            if (amount.trim() && cents == null) { setAmount(doc.amount == null ? '' : moneyText(doc.amount)); return }
            if (cents !== (doc.amount ?? null)) onChange({ amount: cents == null ? '' : String(cents) })
          }} />
        </Field>
        {/* Als Einnahme oder Ausgabe in Finanzen buchen - nur, wenn Finanzen freigeschaltet ist */}
        <Field label={lab(t('Dokumentdatum'), 'docDate')}>
          <div className={s.dateRow}>
            <Input type="date" value={date} onChange={(_, d) => { setDate(d.value); if (d.value !== (doc.docDate ?? '')) onChange({ docDate: d.value }) }} />
            {dates.length > 0 && <Dropdown className={s.dateFound} placeholder={t('Im Text gefunden ({n})', { n: dates.length })}
              value={t('Im Text gefunden ({n})', { n: dates.length })} selectedOptions={dates.some(d => d.date === date) ? [date] : []}
              onOptionSelect={(_, d) => { if (d.optionValue && d.optionValue !== date) { setDate(d.optionValue); onChange({ docDate: d.optionValue }) } }}>
              {dates.map(d => <Option key={d.date} value={d.date} text={dayText(d.date)}>
                <div className={s.dateOption}>
                  <Text weight="semibold">{dayText(d.date)}{d.count > 1 ? ` · ${d.count}×` : ''}</Text>
                  <Text size={200} className={s.muted}>{d.context}</Text>
                </div>
              </Option>)}
            </Dropdown>}
          </div>
        </Field>
        {single('correspondent', doc.correspondent, doc.autoCorrespondent)}
        {single('doctype', doc.doctype, doc.autoDoctype)}
        <Field label={t('Rolle')} hint={doc.role && !doc.roleCurrent && doc.roleReplaced
          ? t('Frühere Version – ersetzt am {date}', { date: new Date(doc.roleReplaced).toLocaleDateString() }) : undefined}>
          <Dropdown value={doc.role ? (doc.roleCurrent ? t('Gilt als: {role}', { role: fullName(entries, doc.role) }) : fullName(entries, doc.role)) : ''}
            placeholder={t('(keine)')} selectedOptions={[doc.role ?? '']} onOptionSelect={(_, d) => setRole(d.optionValue ?? '')}>
            <Option value="">{t('(keine)')}</Option>
            {roleOpts.map(o => <Option key={o.entry.id} value={o.entry.id} text={o.entry.name}>{o.entry.name}</Option>)}
            <Option value="+" text={t('Neu …')}>{t('Neu …')}</Option>
          </Dropdown>
        </Field>
        <Field label={lab(t('Tags'), 'tag')}>
          <EntrySelect entries={entries} kind="tag" values={doc.tags.map(x => x.id)} placeholder={t('(keine)')}
            key={prefillFor('tag').key} prefill={prefillFor('tag').prefill} onPickMany={ids => onChange({ tags: ids })} onNew={name => createNamed('tag', name)} />
        </Field>
        {doc.tags.some(x => x.auto) && <Text size={200} className={s.muted}>{t('Kursiv: vom DMS gesetzt, noch nicht bestätigt.')}</Text>}
        {doc.paperless && <div className={s.compare}>
          <Text weight="semibold">{doc.paperless.differs ? t('Angaben aus paperless (abweichend)') : t('Angaben aus paperless (gleich)')}</Text>
          <Text size={200}>{t('Korrespondent')}: {fullName(entries, doc.paperless.correspondent) || '–'}</Text>
          <Text size={200}>{t('Dokumenttyp')}: {fullName(entries, doc.paperless.doctype) || '–'}</Text>
          <Text size={200}>{t('Tags')}: {doc.paperless.tags.map(x => entries.find(e => e.id === x)?.name).filter(Boolean).join(', ') || '–'}</Text>
          {doc.paperless.differs && doc.inbox && <div className={s.actions}>
            <Button size="small" icon={<Checkmark20Regular />} onClick={onConfirm}>{t('DMS übernehmen')}</Button>
            <Button size="small" icon={<ArrowSwap20Regular />} onClick={onTakePaperless}>{t('paperless übernehmen')}</Button>
          </div>}
        </div>}
        <div className={s.actions}>
          {doc.inbox && <Button appearance="primary" icon={<Checkmark20Regular />} onClick={onConfirm}>{t('Bestätigen')}</Button>}
          {(doc.fileId > 0 || !!doc.mount) && <Button icon={<ArrowDownload20Regular />} onClick={() => downloadDocs([doc.id], tresor)}>{t('Herunterladen')}</Button>}
          {doc.hasOriginal && <Button icon={<ArrowDownload20Regular />} as="a" href={`/api/dms/docs/${doc.id}/original` + areaQ(tresor)}>{t('Original herunterladen')}</Button>}
          {doc.path && !tresor && <Button icon={<FolderOpen20Regular />} onClick={() => {
            const dir = (doc.path!.includes('/') ? doc.path!.slice(0, doc.path!.lastIndexOf('/')) : '').split('/').map(encodeURIComponent).join('/')
            location.hash = doc.mount ? '#/files/:ext/' + encodeURIComponent(doc.mount) + (dir ? '/' + dir : '') : '#/files/' + dir
          }}>{t('In Dateien zeigen')}</Button>}
          <Button icon={<DocumentText20Regular />} onClick={() => api.get<{ text: string }>(`/api/dms/docs/${doc.id}/text` + areaQ(tresor)).then(r => setText(r.text)).catch(fail)}>{t('Erkannter Text')}</Button>
          {doc.state === 'error' && <Button icon={<ArrowSync20Regular />} onClick={() => api.post(`/api/dms/docs/${doc.id}/retry` + areaQ(tresor)).then(onReload).catch(fail)}>{t('Erneut verarbeiten')}</Button>}
        </div>
        <Text size={200} className={s.muted}>
          {[doc.mount ? t('Externer Ordner') + ' › ' + doc.path : doc.path, fmtSize(doc.size), doc.archive ? (doc.pdfA ? t('Durchsuchbare PDF/A') : t('Durchsuchbare PDF')) : null,
            doc.manual ? t('Von Hand abgelegt') : null, t('Importiert {when}', { when: new Date(doc.imported).toLocaleString() }), doc.originalName].filter(Boolean).join(' · ')}
        </Text>
      </div>
  )
  const dialogs = <>
      {editing && <OfficeEditor name={fileName} onClose={() => { setEditing(false); onReload() }}
        open={() => api.post<OfficeSession>('/api/office/open?lang=' + lang, tresor ? { path: '', tresor: doc.fileId, view: mode === 'view' || undefined } : { path: doc.path, view: mode === 'view' || undefined })} />}
      {text !== null && <Dialog open onOpenChange={(_, d) => { if (!d.open) setText(null) }}>
        <DialogSurface style={{ maxWidth: 760 }}>
          <DialogBody>
            <DialogTitle>{t('Erkannter Text')}</DialogTitle>
            <DialogContent><Textarea readOnly value={text || t('Kein Text erkannt.')} style={{ width: '100%', height: '60vh' }} resize="vertical" /></DialogContent>
            <DialogActions><Button appearance="primary" onClick={() => setText(null)}>{t('Schließen')}</Button></DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
      {newEntry && <EditEntry kind={newEntry.kind} entries={entries} entry={null} initialName={newEntry.name} onClose={saved => {
        setNewEntry(null)
        if (!saved) return
        onEntriesChanged()
        if (saved.kind === 'tag') onChange({ addTags: [saved.id] })
        else onChange({ [saved.kind]: saved.id })
      }} />}
  </>
  if (wide) return (
    <div className={s.editor}>
      <div className={s.editPreview}>
        {inline && isPdf ? <WidePages doc={doc} tresor={tresor} pick={pick} />
          : inline && isImage ? <img src={inline} alt="" style={{ maxWidth: '100%' }} /> : noPreview}
      </div>
      <div className={s.editForm}>{form}</div>
      {dialogs}
    </div>
  )
  return (
    <aside className={s.detail}>
      <div className={s.preview}>
        {/* PDF als Seitenbilder vom Server: den PDF-Betrachter des Browsers blockiert Vivaldi im Rahmen unter der CSP der Seite */}
        {inline && isPdf ? <PdfPages doc={doc} tresor={tresor} className={s.pages} entries={entries} onChange={onChange} onEntriesChanged={onEntriesChanged} />
          : inline && isImage ? <img src={inline} alt="" /> : noPreview}
      </div>
      {form}
      {dialogs}
    </aside>
  )
}

/** Bearbeitungsansicht: alle Seiten groß untereinander; mit gewähltem Feld lässt sich ein Rahmen ziehen. */
function WidePages({ doc, tresor, pick }: { doc: Doc; tresor: boolean; pick: Pick }) {
  const [count, setCount] = useState<number | null>(null)
  // Nur beim Wechsel des Dokuments neu zählen: Umbenennen nach einer Änderung lässt Seiten und Scrollstand stehen
  useEffect(() => {
    setCount(null)
    api.get<{ count: number }>(`/api/dms/docs/${doc.id}/pages` + areaQ(tresor)).then(r => setCount(r.count)).catch(() => setCount(0))
  }, [doc.id, tresor])
  if (count === null) return <Spinner />
  if (count === 0) return <Text>{t('Die PDF lässt sich nicht anzeigen.')}</Text>
  // Ohne Pfad im Schlüssel: Umbenennen lädt die Bilder nicht neu (der Inhalt bleibt gleich)
  const src = (n: number, width: number) => `/api/dms/docs/${doc.id}/page/${n}?` + q({ width, area: tresor ? 'tresor' : undefined, v: doc.state })
  return pick.pagesView(Array.from({ length: Math.min(count, 50) }, (_, i) => i), src, 1600)
}

// ---------------- Bäume ----------------

function TreeDialog({ trees, index, onSave, onClose }: { trees: Tree[]; index: number | null; onSave: (list: Tree[]) => void; onClose: () => void }) {
  const [name, setName] = useState(index !== null ? trees[index].name : '')
  const [levels, setLevels] = useState<Level[]>(index !== null ? trees[index].levels : ['correspondent', 'doctype', 'doc_year'])
  const setLevel = (i: number, v: string) => setLevels(l => (v ? [...l.slice(0, i), v as Level, ...l.slice(i + 1)] : l.slice(0, i)).filter((x, j, a) => a.indexOf(x) === j))
  const save = () => {
    const tree = { name: name.trim(), levels }
    onSave(index === null ? [...trees, tree] : trees.map((x, i) => i === index ? tree : x))
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{index === null ? t('Neuer Baum') : t('Baum bearbeiten')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Name')} required><Input value={name} onChange={(_, d) => setName(d.value)} /></Field>
            {[0, 1, 2, 3].filter(i => i <= levels.length).map(i => (
              <Field key={i} label={t('Ebene {n}', { n: i + 1 })}>
                <Dropdown value={levels[i] ? levelLabel(levels[i]) : ''} placeholder={t('(keine)')} selectedOptions={[levels[i] ?? '']} onOptionSelect={(_, d) => setLevel(i, d.optionValue ?? '')}>
                  {i > 0 && <Option value="">{t('(keine)')}</Option>}
                  {LEVELS.filter(l => l === levels[i] || !levels.includes(l)).map(l => <Option key={l} value={l}>{levelLabel(l)}</Option>)}
                </Dropdown>
              </Field>
            ))}
            <Text size={200}>{t('Ordner entstehen aus den Angaben, unabhängig vom Ort in der Dateiablage. Ein Dokument mit mehreren Tags steht unter jedem seiner Tags.')}</Text>
            <Text size={200}>{t('Hier werden nur die Ebenen festgelegt. Einträge wie „Versicherung › Kfz“ legt man über Rechtsklick bzw. „…“ an einem Eintrag im Baum an oder unter „Verwalten“; ein Eintrag erscheint im Baum, sobald ein Dokument ihn hat.')}</Text>
          </DialogContent>
          <DialogActions>
            {index !== null && <Button icon={<Delete20Regular />} onClick={() => onSave(trees.filter((_, i) => i !== index))}>{t('Löschen')}</Button>}
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!name.trim() || levels.length === 0} onClick={save}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

// ---------------- Feste Zeiträume (Dokumentdatum) ----------------

type Preset = '7d' | '30d' | '6m' | '1y' | 'year'
const PRESETS: Preset[] = ['7d', '30d', '6m', '1y', 'year']
const presetName = (p: Preset) => ({ '7d': t('Letzte 7 Tage'), '30d': t('Letzte 30 Tage'), '6m': t('Letzte 6 Monate'), '1y': t('Letztes Jahr (12 Monate)'), year: t('Dieses Jahr') })[p]
const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
/** Von-Datum des Zeitraums bis heute (bis bleibt offen). */
function presetRange(p: Preset): { docFrom?: string; docTo?: string } {
  const d = new Date()
  if (p === '7d') d.setDate(d.getDate() - 7)
  else if (p === '30d') d.setDate(d.getDate() - 30)
  else if (p === '6m') d.setMonth(d.getMonth() - 6)
  else if (p === '1y') d.setFullYear(d.getFullYear() - 1)
  else { d.setMonth(0); d.setDate(1) }
  return { docFrom: iso(d) }
}
const presetLabel = (r: { docFrom?: string; docTo?: string }) => {
  const p = PRESETS.find(x => presetRange(x).docFrom === r.docFrom && !r.docTo)
  return p ? presetName(p) : ''
}

/** PDF-Vorschau als Seitenbilder (GET /api/dms/docs/{id}/pages und /page/{n}); Klick zeigt die Seiten groß. */
function PdfPages({ doc, tresor, className, entries, onChange, onEntriesChanged }: {
  doc: Doc; tresor: boolean; className: string; entries: Entry[]; onChange: (body: Record<string, unknown>) => Promise<unknown>; onEntriesChanged: () => void
}) {
  const [count, setCount] = useState<number | null>(null)
  const [big, setBig] = useState(false)
  useEffect(() => {
    setCount(null)
    api.get<{ count: number }>(`/api/dms/docs/${doc.id}/pages` + areaQ(tresor)).then(r => setCount(r.count)).catch(() => setCount(0))
  }, [doc.id, doc.path, tresor])
  const src = (n: number, width: number) => `/api/dms/docs/${doc.id}/page/${n}?` + q({ width, area: tresor ? 'tresor' : undefined, v: doc.path ?? '' })
  if (count === null) return <Spinner />
  if (count === 0) return <Text>{t('Die PDF lässt sich nicht anzeigen.')}</Text>
  const pages = Array.from({ length: Math.min(count, 50) }, (_, i) => i)
  return (
    <>
      <div className={className} onClick={() => setBig(true)} title={t('Groß anzeigen')}>
        {pages.map(n => <img key={n} src={src(n, 800)} alt={t('Seite {n}', { n: n + 1 })} loading="lazy" />)}
        {count > 50 && <Text size={200}>{t('Weitere {n} Seiten: Herunterladen', { n: count - 50 })}</Text>}
      </div>
      {big && <Dialog open onOpenChange={(_, d) => { if (!d.open) setBig(false) }}>
        <DialogSurface style={{ maxWidth: 'min(1400px, 96vw)', width: '96vw' }}>
          <DialogBody>
            <DialogTitle action={<Button appearance="subtle" icon={<Dismiss20Regular />} aria-label={t('Schließen')} onClick={() => setBig(false)} />}>{doc.title}</DialogTitle>
            <DialogContent>
              <PickPages doc={doc} tresor={tresor} pages={pages} src={src} entries={entries} onChange={onChange} onEntriesChanged={onEntriesChanged} />
            </DialogContent>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
    </>
  )
}
