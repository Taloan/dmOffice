import {
  Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent,
  DialogSurface, DialogTitle, Input, Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, MenuItemRadio,
  Spinner, Table, TableBody, TableCell, TableCellLayout, TableHeader, TableHeaderCell, TableRow, Text, Tooltip,
  makeStyles, mergeClasses, tokens, ProgressBar,
} from '@fluentui/react-components'
import {
  ArrowUpload20Regular, FolderAdd20Regular, Delete20Regular, ArrowDownload20Regular, Rename20Regular, MoreHorizontal20Regular, Home20Regular,
  ChevronRight16Regular, ChevronDown16Regular, FolderOpen20Regular, Folder20Regular, ArrowSort20Regular, PanelLeft20Regular, Settings20Regular,
  FolderArrowRight20Regular, Copy20Regular, FolderArrowUp20Regular, AppsList20Regular, Grid20Regular, Clock20Regular, Star20Regular, Star20Filled,
  Share20Regular, Link20Regular, Image20Regular, Info20Regular, Cut20Regular, ClipboardPaste20Regular, ArrowUndo20Regular, Search20Regular,
  Dismiss20Regular, DocumentAdd20Regular, ArrowCounterclockwise20Regular, DeleteDismiss20Regular, ArrowReset20Regular, ArrowSync20Regular,
  Filter20Regular, LinkSquare20Regular, DocumentEdit20Regular, Star16Filled, Link16Regular, ArrowUp16Regular, ArrowDown16Regular, ChevronDown20Regular, DocumentSearch20Regular, GroupList20Regular, ZoomIn20Regular, People16Regular, People20Regular,
} from '@fluentui/react-icons'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { SidePane, useBackClose, useIsMobile } from '../shell/mobile'
import { usePrefs } from '../prefs'
import { useCallback, useEffect, useMemo, useRef, useState, Fragment } from 'react'
import { ApiError, api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { useDebounced } from '../shell/hooks'
import { locale, t } from '../i18n'
import {
  FILTERS, Item, Kind, KindIcon, Thumb, WITH_ME, extOf, fmtWhen, hasThumb, inWithMe, inside, join, kindLabel, kindOf, nameOf, ownSource, parentOf, viewKind,
} from './common'
import { Viewer } from './Viewer'
import { Details } from './Details'
import { Choice, NameDialog, ShareDialog, copyText, linkUrl, useConflict, Link } from './Dialogs'
import { LinksList, TrashList, useLinks, useTrash } from './Lists'
import { ContentSearch } from './ContentSearch'
import { OfficeEditor, OfficeSession, officeMode, useOffice } from './Office'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { flex: 1, display: 'flex', minWidth: 0, minHeight: 0, position: 'relative' },
  side: { width: '240px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '12px 8px', display: 'flex', flexDirection: 'column', gap: '1px', overflowY: 'auto' },
  nav: { display: 'flex', alignItems: 'center', gap: '6px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  navName: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  navSep: { height: '1px', backgroundColor: tokens.colorNeutralStroke2, margin: '8px 4px' },
  twisty: { width: '16px', flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' },
  active: { backgroundColor: tokens.colorBrandBackground2, fontWeight: 600 },
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, outline: 'none', position: 'relative' },
  sel: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  head: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 16px', minHeight: '40px', flexWrap: 'wrap',
    '@media (max-width: 767px)': { padding: '6px 8px', gap: '4px' },
  },
  crumbs: { flex: 1, minWidth: '120px', overflowX: 'auto' },
  viewTitle: { fontSize: tokens.fontSizeBase400, fontWeight: 600, paddingLeft: '4px' },
  search: { width: '260px', '@media (max-width: 767px)': { width: '100%', order: 3 } },
  table: { flex: 1, overflowY: 'auto', padding: '0 16px 16px', '@media (max-width: 767px)': { padding: '0 4px 72px' } },
  drop: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-8px' },
  target: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: '-2px', backgroundColor: tokens.colorBrandBackground2 },
  row: { cursor: 'pointer', userSelect: 'none' },
  cut: { opacity: 0.45 },
  sortable: { cursor: 'pointer', userSelect: 'none', ':hover': { color: tokens.colorBrandForeground1 } },
  rowIcon: { width: '28px', height: '28px', display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden', borderRadius: '3px' },
  rowThumb: { width: '28px', height: '28px' },
  badges: { display: 'inline-flex', gap: '4px', marginLeft: '6px', verticalAlign: 'middle', color: tokens.colorNeutralForeground3 },
  muted: { color: tokens.colorNeutralForeground3 },
  foot: { padding: '10px 10px 2px', color: tokens.colorNeutralForeground3, fontSize: '12px', display: 'flex', flexDirection: 'column', gap: '6px' },
  progress: { padding: '8px 16px', display: 'flex', flexDirection: 'column', gap: '6px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  groupHead: { display: 'block', fontWeight: 600, fontSize: '13px', padding: '12px 4px 4px', color: tokens.colorNeutralForeground2 },
  groupRow: { ':hover': { backgroundColor: 'transparent' } },
  tiles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(var(--tile, 132px), 1fr))', gap: '8px', paddingTop: '4px',
    '@media (max-width: 767px)': { gridTemplateColumns: 'repeat(auto-fill, minmax(104px, 1fr))', gap: '4px' } },
  tile: {
    position: 'relative', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', padding: '8px 6px',
    borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', userSelect: 'none', border: '1px solid transparent',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  tileSel: { backgroundColor: tokens.colorBrandBackground2, border: `1px solid ${tokens.colorBrandStroke2}`, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  thumb: { width: '100%', aspectRatio: '4 / 3', display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden', borderRadius: tokens.borderRadiusSmall },
  thumbImg: { width: '100%', height: '100%' },
  tileName: { fontSize: '12px', textAlign: 'center', wordBreak: 'break-word', lineHeight: '16px', maxHeight: '32px', overflow: 'hidden', width: '100%' },
  tileCheck: { position: 'absolute', top: '2px', left: '2px', zIndex: 1 },
  tileBadge: { position: 'absolute', top: '6px', right: '6px', display: 'flex', gap: '2px', color: tokens.colorNeutralForeground2 },
  picker: { maxHeight: '50vh', overflowY: 'auto', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, padding: '4px' },
  undo: {
    position: 'absolute', left: '50%', bottom: '16px', transform: 'translateX(-50%)', zIndex: 10, display: 'flex', alignItems: 'center', gap: '12px',
    padding: '8px 8px 8px 16px', borderRadius: tokens.borderRadiusLarge, boxShadow: tokens.shadow16, maxWidth: 'calc(100% - 32px)',
    backgroundColor: tokens.colorNeutralBackgroundInverted, color: tokens.colorNeutralForegroundInverted,
  },
  undoBtn: { color: tokens.colorBrandForegroundInverted },
  empty: { color: tokens.colorNeutralForeground3, padding: '24px 4px', display: 'block' },
})

const DRAG = 'application/x-dmoffice-files'

type Special = 'recent' | 'favorites' | 'photos' | 'shared' | 'trash'
type View = { kind: 'folder'; path: string } | { kind: Special }

/** #/files/a/b = Ordner; #/files/:trash usw. = besondere Ansichten (":" steht in echten Namen kodiert als %3A). */
function parseHash(): View {
  const rest = location.hash.replace(/^#\/?files\/?/, '')
  // #/files/:office/a/b.docx - Dokument im Editor (Link aus der Handy-App): darunter liegt sein Ordner
  const doc = officeHash()
  if (doc !== null) return { kind: 'folder', path: parentOf(doc) }
  if (rest.startsWith(':')) {
    const v = rest.slice(1) as Special
    if (['recent', 'favorites', 'photos', 'shared', 'trash'].includes(v)) return { kind: v }
  }
  try { return { kind: 'folder', path: rest.split('/').filter(Boolean).map(decodeURIComponent).join('/') } } catch { return { kind: 'folder', path: '' } }
}
/** Pfad aus #/files/:office/..., sonst null. */
function officeHash(): string | null {
  const m = location.hash.match(/^#\/?files\/:office\/(.+)$/)
  if (!m) return null
  try { return m[1].split('/').filter(Boolean).map(decodeURIComponent).join('/') } catch { return null }
}
const hashOf = (v: View) => '#/files/' + (v.kind === 'folder' ? v.path.split('/').map(encodeURIComponent).join('/') : ':' + v.kind)

const TILE_STEP = 300

/** Unsichtbare Marke am Ende der Kacheln: kommt sie ins Bild, werden die naechsten gezeigt. */
function MoreOnScroll({ onMore }: { onMore: () => void }) {
  const ref = useRef<HTMLDivElement>(null)
  const more = useRef(onMore)
  more.current = onMore
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const io = new IntersectionObserver(e => { if (e.some(x => x.isIntersecting)) more.current() }, { rootMargin: '800px' })
    io.observe(el)
    return () => io.disconnect()
  })
  return <div ref={ref} style={{ height: 1 }} />
}

const viewTitle = (k: Special) => ({ recent: t('Zuletzt verwendet'), favorites: t('Favoriten'), photos: t('Fotos'), shared: t('Von mir geteilt'), trash: t('Papierkorb') })[k]

const listDirs = (path: string) => api.get<{ items: Item[] }>('/api/files/?' + q({ path })).then(d => d.items.filter(i => i.isDir))

/** Ordnerbaum, Unterordner werden beim Aufklappen geladen. Ordner sind Ablageziele fuer Ziehen und Ablegen. */
function FolderTree({ current, onOpen, onDropOn, onContext, version, exclude, listed }: {
  current: string | null; onOpen: (path: string) => void; onDropOn?: (e: React.DragEvent, path: string) => void
  onContext?: (e: React.MouseEvent, path: string) => void; version: number; exclude?: string[]
  /** Gerade geladene Liste eines Ordners - der Baum uebernimmt deren Unterordner (geloescht/neu auf anderem Geraet) */
  listed?: { path: string; items: Item[] } | null
}) {
  const s = useStyles()
  const [kids, setKids] = useState<Record<string, Item[]>>({})
  const [open, setOpen] = useState<Record<string, boolean>>({ '': true })
  const [over, setOver] = useState<string | null>(null)
  const load = useCallback((p: string) => listDirs(p).then(d => setKids(k => ({ ...k, [p]: d }))).catch(() => {}), [])
  // Neu laden nach Aenderungen: alle aufgeklappten Ebenen
  useEffect(() => { Object.keys(open).filter(p => open[p]).forEach(load) }, [version]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!listed) return
    const dirs = listed.items.filter(i => i.isDir)
    setKids(k => {
      const old = k[listed.path]
      if (old && old.length === dirs.length && old.every((x, n) => x.path === dirs[n].path && x.name === dirs[n].name)) return k
      return { ...k, [listed.path]: dirs }
    })
  }, [listed])
  // Den Weg zum aktuellen Ordner aufklappen
  useEffect(() => {
    if (current === null) return
    const parts = current ? current.split('/') : []
    const chain = [''].concat(parts.map((_, i) => parts.slice(0, i + 1).join('/')))
    setOpen(o => { const n = { ...o }; chain.slice(0, -1).forEach(p => { n[p] = true }); return n })
    chain.slice(0, -1).forEach(p => { if (!kids[p]) load(p) })
  }, [current]) // eslint-disable-line react-hooks/exhaustive-deps

  const toggle = (p: string) => {
    setOpen(o => ({ ...o, [p]: !o[p] }))
    if (!kids[p]) load(p)
  }
  const dropProps = (p: string) => onDropOn ? {
    onDragOver: (e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG) || e.dataTransfer.types.includes('Files')) { e.preventDefault(); e.stopPropagation(); setOver(p) } },
    onDragLeave: () => setOver(o => (o === p ? null : o)),
    onDrop: (e: React.DragEvent) => { e.stopPropagation(); setOver(null); onDropOn(e, p) },
  } : {}
  const render = (p: string, depth: number): JSX.Element[] => (kids[p] ?? []).filter(f => !exclude?.some(x => inside(x, f.path))).flatMap(f => [
    <div key={f.path} className={mergeClasses(s.nav, current === f.path && s.active, over === f.path && s.target)}
      style={{ paddingLeft: 8 + depth * 14 }} onClick={() => onOpen(f.path)} {...dropProps(f.path)}
      onContextMenu={onContext ? e => { e.preventDefault(); e.stopPropagation(); onContext(e, f.path) } : undefined}>
      <span className={s.twisty} onClick={e => { e.stopPropagation(); toggle(f.path) }}>
        {kids[f.path]?.length === 0 ? null : open[f.path] ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
      </span>
      {current === f.path ? <FolderOpen20Regular /> : <Folder20Regular />}<span className={s.navName}>{f.name}</span>
    </div>,
    ...(open[f.path] ? render(f.path, depth + 1) : []),
  ])
  return (
    <>
      <div className={mergeClasses(s.nav, current === '' && s.active, over === '' && s.target)} onClick={() => onOpen('')} {...dropProps('')}
        onContextMenu={onContext ? e => { e.preventDefault(); e.stopPropagation(); onContext(e, '') } : undefined}>
        <span className={s.twisty} onClick={e => { e.stopPropagation(); toggle('') }}>{open[''] ? <ChevronDown16Regular /> : <ChevronRight16Regular />}</span>
        <Home20Regular />{t('Meine Dateien')}
      </div>
      {open[''] && render('', 1)}
    </>
  )
}

/** Zielordner waehlen fuer Verschieben/Kopieren. */
function FolderPicker({ title, exclude, onPick, onClose }: { title: string; exclude: string[]; onPick: (path: string) => void; onClose: () => void }) {
  const s = useStyles()
  const [target, setTarget] = useState('')
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 440 }}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>
            <div className={s.picker}><FolderTree current={target} onOpen={setTarget} version={0} exclude={exclude.length ? exclude : undefined} /></div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={() => onPick(target)}>{t('Hierher')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Datei mit Pfad relativ zum Zielordner (Ordner hochladen, Ordner vom Desktop ziehen). */
interface Upload { file: File; rel: string }

/** Ordner vom Desktop: die Eintraege muessen noch im drop-Ereignis abgeholt werden, gelesen wird danach. */
function entriesOf(dt: DataTransfer): any[] {
  return Array.from(dt.items ?? []).filter(i => i.kind === 'file').map(i => (i as any).webkitGetAsEntry?.()).filter(Boolean)
}
async function readEntries(entries: any[]): Promise<Upload[]> {
  const out: Upload[] = []
  const walk = async (e: any, prefix: string): Promise<void> => {
    if (e.isFile) out.push({ file: await new Promise<File>((res, rej) => e.file(res, rej)), rel: prefix + e.name })
    else if (e.isDirectory) {
      const reader = e.createReader()
      for (;;) {
        const batch: any[] = await new Promise((res, rej) => reader.readEntries(res, rej))
        if (!batch.length) break
        for (const c of batch) await walk(c, prefix + e.name + '/')
      }
    }
  }
  for (const e of entries) await walk(e, '')
  return out
}

/** Gemeinsamer Ordner einer Auswahl (Namen im ZIP relativ dazu). */
function commonParent(paths: string[]): string {
  if (!paths.length) return ''
  let base = parentOf(paths[0])
  while (base && !paths.every(p => p.startsWith(base + '/'))) base = parentOf(base)
  return base
}

interface Undo { label: string; run: () => Promise<void> }

/** Die Dateien im eigenen WebDAV - dieselben, die Programme unter /dav/files sehen. */
export default function FilesView() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [view, setView] = useState<View>(parseHash)
  const path = view.kind === 'folder' ? view.path : null
  const [data, setData] = useState<{ items: (Item & { used?: string })[]; folder?: Item } | null>(null)
  const [usage, setUsage] = useState<{ usage: number; trash: number; versions: number; quota: number; used?: number } | null>(null)
  const [busy, setBusy] = useState(false)
  const [dragging, setDragging] = useState(false)
  const [overRow, setOverRow] = useState<string | null>(null)
  const upload = useRef<HTMLInputElement>(null)
  const uploadDir = useRef<HTMLInputElement>(null)
  const mainRef = useRef<HTMLDivElement>(null)
  const [selected, setSelected] = useState<string[]>([])
  const anchor = useRef<string | null>(null)
  const [picker, setPicker] = useState<{ mode: 'move' | 'copy'; paths: string[] } | null>(null)
  const [treeVersion, setTreeVersion] = useState(0)
  const [listed, setListed] = useState<{ path: string; items: Item[] } | null>(null)
  const [detailsVersion, setDetailsVersion] = useState(0)
  const { prefs, update } = usePrefs()
  const photosView = view.kind === 'photos'
  const photoDesc = prefs.photoSortDesc ?? true
  const sort = photosView ? prefs.photoSort ?? 'modified' : prefs.fileSort ?? 'name'
  const desc = photosView ? prefs.photoSortDesc ?? true : prefs.fileSortDesc ?? false
  const viewMode = view.kind === 'photos' ? 'tiles' : prefs.fileView ?? 'details'
  const [sidePane, toggleSide, closeSide] = useSidePane('files')
  const mobile = useIsMobile()
  // Spalten nach der Breite der Liste (nicht des Fensters): zuerst entfaellt "Art", dann Datum und Groesse (stehen dann unter dem Namen)
  const listRef = useRef<HTMLDivElement>(null)
  const [listW, setListW] = useState(1000)
  useEffect(() => {
    const el = listRef.current
    if (!el) return
    const ro = new ResizeObserver(() => setListW(el.clientWidth))
    ro.observe(el)
    return () => ro.disconnect()
  })
  const cols = mobile ? 0 : listW >= 700 ? 3 : listW >= 480 ? 2 : 0
  // Handy: Detailbereich deckt die Liste ab - dort nur auf Wunsch, nicht aus der Einstellung vom Desktop
  const [mobileDetails, setMobileDetails] = useState(false)
  const detailsOpen = mobile ? mobileDetails : prefs.fileDetails ?? false
  const setDetails = (on: boolean) => mobile ? setMobileDetails(on) : update({ fileDetails: on })
  // Auf Touch-Geraeten oeffnet ein Tippen, sonst waehlt ein Klick aus und Doppelklick oeffnet
  const touch = typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches
  const [search, setSearch] = useState('')
  const [scope, setScope] = useState<'here' | 'all'>('all')
  const term = useDebounced(search.trim(), 300)
  const [filter, setFilter] = useState<Kind | ''>('')
  // Ansicht Fotos: nach Dateiendung filtern (nur die, die es gibt, mit Anzahl)
  const [photoExt, setPhotoExt] = useState('')
  const [clip, setClip] = useState<{ paths: string[]; cut: boolean } | null>(null)
  const undos = useRef<Undo[]>([])
  const [undoBar, setUndoBar] = useState<Undo | null>(null)
  const undoTimer = useRef<number>()
  const [viewer, setViewer] = useState<{ list: Item[]; index: number } | null>(null)
  const [share, setShare] = useState<Item | null>(null)
  const [naming, setNaming] = useState<{ title: string; initial: string; label?: string; done: (name: string) => void } | null>(null)
  const [favs, setFavs] = useState<Item[]>([])
  // Mit mir geteilte Ordner ("Mit mir geteilt"): Pfad, Besitzer, ob ich dort schreiben darf
  const [withMe, setWithMe] = useState<{ id: number; path: string; name: string; owner: string; canWrite: boolean }[]>([])
  const anyShared = withMe.length > 0
  /** Darf ich an diesem Pfad schreiben? Eigene Ablage immer; "Mit mir geteilt" selbst nie; sonst nach dem Recht an dem geteilten Ordner. */
  const writableAt = (p: string) => {
    if (!inWithMe(p, anyShared)) return true
    const m = withMe.find(x => p === x.path || p.startsWith(x.path + '/'))
    return !!m?.canWrite
  }
  /** Der geteilte Ordner selbst (erste Ebene in "Mit mir geteilt"): umbenennen, verschieben, loeschen kann nur der Besitzer. */
  const isMountRoot = (p: string) => withMe.some(x => x.path === p)
  const canChange = (list: Item[]) => list.every(i => writableAt(i.path) && !isMountRoot(i.path))
  const conflict = useConflict()
  const office = useOffice()
  // Office-Dokument im Editor (Vollbild); beim Start schon gesetzt, wenn die Adresse darauf zeigt
  const [editing, setEditing] = useState<{ path: string; name: string; view?: boolean } | null>(() => {
    const p = officeHash()
    return p ? { path: p, name: nameOf(p) } : null
  })
  const searching = term.length > 0 && view.kind !== 'trash' && view.kind !== 'shared'

  const go = useCallback((v: View) => { setSearch(''); setView(v) }, [])
  const openFolder = useCallback((p: string) => go({ kind: 'folder', path: p }), [go])
  useEffect(() => {
    const on = () => {
      const doc = officeHash()
      if (doc) setEditing({ path: doc, name: nameOf(doc) })
      const v = parseHash(); setView(cur => JSON.stringify(cur) === JSON.stringify(v) ? cur : v)
    }
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [])

  // Nur die Antwort der neuesten Anfrage uebernehmen
  const req = useRef(0)
  const load = useCallback(async () => {
    const n = ++req.current
    try {
      let d: { items: (Item & { used?: string })[]; folder?: Item }
      if (searching) {
        d = await api.get('/api/files/search?' + q({ q: term, path: scope === 'here' && path ? path : '', type: filter || undefined }))
      } else if (view.kind === 'folder') {
        const r = await api.get('/api/files/?' + q({ path: view.path }))
        setUsage({ usage: r.usage, trash: r.trash, versions: r.versions, quota: r.quota, used: r.used })
        d = { items: r.items, folder: { path: view.path, name: nameOf(view.path), isDir: true, size: 0, mime: null, modified: '', favorite: r.favorite } }
      } else if (view.kind === 'recent') {
        const r = await api.get<{ items: { item: Item; used: string }[] }>('/api/files/recent')
        d = { items: r.items.map(x => ({ ...x.item, used: x.used })) }
      } else if (view.kind === 'favorites') d = await api.get('/api/files/favorites')
      else if (view.kind === 'photos') d = await api.get('/api/files/search?' + q({ q: '', type: 'image' }))
      else d = { items: [] }
      if (n === req.current) { setData(d); if (view.kind === 'folder' && !searching) setListed({ path: view.path, items: d.items }) }
    } catch (e) {
      if (n !== req.current) return
      fail(e)
      if (view.kind === 'folder' && view.path) setView({ kind: 'folder', path: '' })
    }
  }, [view, searching, term, scope, filter, path, fail])
  const loadFavs = useCallback(() => {
    api.get<{ items: Item[] }>('/api/files/favorites').then(d => setFavs(d.items)).catch(() => {})
    api.get<{ items: typeof withMe }>('/api/files/shared').then(d => setWithMe(d.items)).catch(() => {})
  }, [])
  useEffect(() => {
    if (!usage) api.get('/api/files/?path=').then(r => setUsage({ usage: r.usage, trash: r.trash, versions: r.versions, quota: r.quota, used: r.used })).catch(() => {})
    loadFavs()
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { setSelected([]); setData(null); load() }, [view, searching, term, scope]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (searching) load() }, [filter]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { const h = hashOf(view); if (location.hash !== h) history.replaceState(null, '', h) }, [view])
  const loadUsage = () => api.get('/api/files/?path=').then(r => setUsage({ usage: r.usage, trash: r.trash, versions: r.versions, quota: r.quota, used: r.used })).catch(() => {})
  const trash = useTrash(view.kind === 'trash', fail, m => notify(m), () => { setTreeVersion(v => v + 1); loadUsage() })
  const links = useLinks(view.kind === 'shared', fail)
  const changed = () => { load(); setTreeVersion(v => v + 1); setDetailsVersion(v => v + 1); loadFavs(); if (view.kind !== 'folder') loadUsage() }
  // Aenderungen von anderen Geraeten (Handy, Explorer, Nextcloud): beim Zurueckkommen ins Fenster und alle 20 s
  // still nachladen - Auswahl und Ansicht bleiben
  const refresh = useRef<() => void>(() => {})
  refresh.current = () => { if (view.kind !== 'trash' && view.kind !== 'shared') load(); setTreeVersion(v => v + 1) }
  useEffect(() => {
    const on = () => { if (!document.hidden) refresh.current() }
    const iv = setInterval(on, 20000)
    window.addEventListener('focus', on)
    document.addEventListener('visibilitychange', on)
    return () => { clearInterval(iv); window.removeEventListener('focus', on); document.removeEventListener('visibilitychange', on) }
  }, [])

  // Sortieren: Ordner zuerst; Klick auf denselben Spaltenkopf dreht die Richtung
  const items = useMemo(() => {
    if (!data) return []
    const list = data.items.filter(i => (!filter || searching || kindOf(i) === filter) && (!photosView || !photoExt || extOf(i.name) === photoExt))
    if (view.kind === 'recent' && !searching) return list
    const dir = desc ? -1 : 1
    return [...list].sort((a, b) => Number(b.isDir) - Number(a.isDir) || dir * (
      sort === 'modified' ? a.modified.localeCompare(b.modified) : sort === 'size' ? a.size - b.size
        : sort === 'kind' ? kindLabel(kindOf(a)).localeCompare(kindLabel(kindOf(b)), locale) || a.name.localeCompare(b.name, locale, { sensitivity: 'base', numeric: true })
        : a.name.localeCompare(b.name, locale, { sensitivity: 'base', numeric: true })))
  }, [data, filter, searching, view.kind, sort, desc, photosView, photoExt])
  // Gruppieren nach Art oder Aenderungsdatum (wie im Explorer); die Liste bleibt in Anzeigereihenfolge
  const groupBy = prefs.fileGroup ?? 'none'
  const tileSize = prefs.fileTile ?? 'medium'
  const tilePx = tileSize === 'small' ? '96px' : tileSize === 'large' ? '220px' : '132px'
  // Fotos sind eine Zeitleiste: nach Monat gruppiert, neueste zuerst (der Standard der Ansicht)
  const photoGroup = prefs.photoGroup ?? 'month'
  const grouping = photosView ? photoGroup !== 'none' : groupBy !== 'none' && !(view.kind === 'recent' && !searching)
  const groupOf = useCallback((i: Item): [number, string] => {
    if (photosView) {
      const d = new Date(i.modified)
      const start = photoGroup === 'year' ? new Date(d.getFullYear(), 0, 1) : photoGroup === 'day' ? new Date(d.getFullYear(), d.getMonth(), d.getDate()) : new Date(d.getFullYear(), d.getMonth(), 1)
      const label = photoGroup === 'year' ? String(d.getFullYear())
        : photoGroup === 'day' ? d.toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })
        : d.toLocaleDateString(locale, { month: 'long', year: 'numeric' })
      return [photoDesc ? -start.getTime() : start.getTime(), label]
    }
    if (groupBy === 'kind') { const k = kindOf(i); return [k === 'folder' ? -1 : 0, kindLabel(k)] }
    const d = new Date(i.modified), now = new Date()
    const day = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime()
    const diff = Math.round((day(now) - day(d)) / 86400000)
    const weekday = (now.getDay() + 6) % 7
    if (diff <= 0) return [0, t('Heute')]
    if (diff === 1) return [1, t('Gestern')]
    if (diff <= weekday) return [2, t('Diese Woche')]
    if (diff <= weekday + 7) return [3, t('Letzte Woche')]
    if (d.getFullYear() === now.getFullYear() && d.getMonth() === now.getMonth()) return [4, t('Diesen Monat')]
    if (d.getFullYear() === now.getFullYear()) return [5, t('Dieses Jahr')]
    return [6, t('Älter')]
  }, [groupBy, photosView, photoGroup, photoDesc])
  const shown = useMemo(() => {
    if (!grouping) return items
    const keyed = items.map((i, n) => ({ i, n, g: groupOf(i) }))
    keyed.sort((a, b) => a.g[0] - b.g[0] || a.g[1].localeCompare(b.g[1], locale) || a.n - b.n)
    return keyed.map(x => x.i)
  }, [items, grouping, groupOf])
  const groups = useMemo(() => {
    if (!grouping) return [{ label: '', items: shown }]
    const out: { label: string; items: (Item & { used?: string })[] }[] = []
    for (const i of shown) {
      const label = groupOf(i)[1]
      if (out.length && out[out.length - 1].label === label) out[out.length - 1].items.push(i)
      else out.push({ label, items: [i] })
    }
    return out
  }, [shown, grouping, groupOf])
  // Kacheln portionsweise zeigen und beim Scrollen nachladen - Tausende Fotos auf einmal machen die Seite zaeh
  const [cap, setCap] = useState(TILE_STEP)
  useEffect(() => setCap(TILE_STEP), [data])
  const cappedGroups = useMemo(() => {
    let left = cap
    return groups.map(g => { const part = g.items.slice(0, Math.max(0, left)); left -= part.length; return { ...g, total: g.items.length, items: part } })
      .filter(g => g.items.length > 0)
  }, [groups, cap])
  // In der Ansicht Fotos gilt eine eigene Reihenfolge (Vorgabe: neueste zuerst), die der Ordner bleibt unberuehrt
  const setSort = (patch: { sort?: 'name' | 'modified' | 'size' | 'kind'; desc?: boolean }) => photosView
    ? update({ ...(patch.sort ? { photoSort: patch.sort as 'name' | 'modified' | 'size' } : {}), ...(patch.desc !== undefined ? { photoSortDesc: patch.desc } : {}) })
    : update({ ...(patch.sort ? { fileSort: patch.sort } : {}), ...(patch.desc !== undefined ? { fileSortDesc: patch.desc } : {}) })
  const sortBy = (k: 'name' | 'modified' | 'size' | 'kind') => k === sort ? setSort({ desc: !desc }) : setSort({ sort: k, desc: k === 'modified' || k === 'size' })
  const sel = items.filter(i => selected.includes(i.path))
  const one = sel.length === 1 ? sel[0] : null
  const flat = view.kind !== 'folder' || searching   // Liste aus mehreren Ordnern: Spalte "Ort"

  // ---------------- Rueckgaengig ----------------
  const pushUndo = (u: Undo) => {
    undos.current = [...undos.current.slice(-19), u]
    setUndoBar(u)
    clearTimeout(undoTimer.current)
    undoTimer.current = window.setTimeout(() => setUndoBar(null), 8000)
  }
  const undo = async () => {
    const u = undos.current.pop()
    setUndoBar(null)
    if (!u) { notify(t('Nichts rückgängig zu machen'), undefined, 'info'); return }
    try { await u.run(); notify(t('Rückgängig: {what}', { what: u.label })) } catch (e) { fail(e) }
    changed()
  }

  // ---------------- Hochladen ----------------
  const [progress, setProgress] = useState<{ done: number; total: number; bytes: number; totalBytes: number; name: string; failed: number } | null>(null)
  const xhr = useRef<XMLHttpRequest | null>(null)
  const cancelled = useRef(false)
  const sendOne = (file: File, rel: string, dir: string, mode: string, onBytes: (n: number) => void) => new Promise<void>((resolve, reject) => {
    const f = new FormData()
    f.append('files', file, rel)
    const r = new XMLHttpRequest()
    xhr.current = r
    r.open('POST', '/api/files/upload?' + q({ path: dir, conflict: mode }))
    r.withCredentials = true
    r.upload.onprogress = e => onBytes(e.loaded)
    r.onload = () => {
      if (r.status >= 200 && r.status < 300) resolve()
      else { let msg = r.statusText; try { msg = JSON.parse(r.responseText).error ?? msg } catch { /* kein JSON */ } reject(new Error(msg)) }
    }
    r.onerror = () => reject(new Error(t('Verbindung unterbrochen')))
    r.onabort = () => reject(new Error('abort'))
    r.send(f)
  })
  const send = async (list: Upload[], dir = path ?? '') => {
    if (!list.length) return
    if (!writableAt(dir)) { fail(new Error(t('Nur lesbar – der Besitzer hat dir hier keine Bearbeitung erlaubt.'))); return }
    // Belegte Namen vorher erfragen: ersetzen, beide behalten, ueberspringen - auf Wunsch fuer alle
    let existing: string[] = []
    try { existing = (await api.post<{ existing: string[] }>('/api/files/exists', { paths: list.map(u => join(dir, u.rel)) })).existing } catch { /* dann eben umbenennen */ }
    conflict.start()
    const modes: string[] = []
    for (const u of list) {
      if (!existing.includes(join(dir, u.rel))) { modes.push('rename'); continue }
      const c = await conflict.resolve(u.rel, false, existing.length > 1)
      if (!c) return
      modes.push(c)
    }
    const todo = list.map((u, i) => ({ ...u, mode: modes[i] })).filter(u => u.mode !== 'skip')
    if (!todo.length) { notify(t('Nichts hochgeladen – alles übersprungen'), undefined, 'info'); return }
    const totalBytes = todo.reduce((n, x) => n + x.file.size, 0)
    let base = 0, failed = 0, lastError: unknown = null
    cancelled.current = false
    setBusy(true)
    setProgress({ done: 0, total: todo.length, bytes: 0, totalBytes, name: '', failed: 0 })
    for (let n = 0; n < todo.length && !cancelled.current; n++) {
      const x = todo[n]
      setProgress(p => p && { ...p, name: x.rel })
      try {
        await sendOne(x.file, x.rel, dir, x.mode, loaded => setProgress(p => p && { ...p, bytes: base + Math.min(loaded, x.file.size) }))
      } catch (e) {
        if (cancelled.current) break
        failed++; lastError = e
        // Speicher voll: weitere Versuche sind sinnlos
        if (e instanceof Error && /Speicher voll|507/.test(e.message)) break
      }
      base += x.file.size
      setProgress(p => p && { ...p, done: n + 1, bytes: base, failed })
    }
    xhr.current = null
    setBusy(false)
    setProgress(null)
    if (cancelled.current) notify(t('Hochladen abgebrochen'))
    else if (failed) fail(lastError ?? new Error(t('{n} Dateien nicht hochgeladen', { n: failed })))
    else notify(todo.length === 1 ? t('Hochgeladen') : t('{n} Dateien hochgeladen', { n: todo.length }))
    changed()
  }
  const fromInput = (files: FileList) => Array.from(files).map(f => ({ file: f, rel: (f as any).webkitRelativePath || f.name }))
  const cancelUpload = () => { cancelled.current = true; xhr.current?.abort() }

  // ---------------- Aktionen ----------------
  const mkdir = (dir = path ?? '') => setNaming({
    title: t('Neuer Ordner'), initial: t('Neuer Ordner'), done: async name => {
      setNaming(null)
      try {
        const r = await api.post<{ path: string }>('/api/files/mkdir', { path: join(dir, name) })
        pushUndo({ label: t('Ordner „{name}“ angelegt', { name }), run: async () => { await api.post('/api/files/delete', { paths: [r.path] }) } })
        changed(); setSelected([r.path])
      } catch (e) { fail(e) }
    },
  })
  const newText = (ext: '.txt' | '.md') => setNaming({
    title: ext === '.md' ? t('Neues Markdown-Dokument') : t('Neue Textdatei'), initial: t('Neues Dokument') + ext, done: async name => {
      setNaming(null)
      const dir = path ?? ''
      try {
        await api.post('/api/files/save', { path: join(dir, name), text: '', create: true })
        changed()
        const r = await api.get<{ item: Item }>('/api/files/info?' + q({ path: join(dir, name) }))
        setViewer({ list: [r.item], index: 0 }); setStartEdit(true)
      } catch (e) { fail(e) }
    },
  })
  const [startEdit, setStartEdit] = useState(false)
  const OFFICE_NEW: Record<string, string> = { docx: t('Word-Dokument'), xlsx: t('Excel-Arbeitsmappe'), pptx: t('PowerPoint-Präsentation') }
  const OFFICE_TITLE: Record<string, string> = { docx: t('Neues Word-Dokument'), xlsx: t('Neue Excel-Arbeitsmappe'), pptx: t('Neue PowerPoint-Präsentation') }
  const newOffice = (kind: string) => setNaming({
    title: OFFICE_TITLE[kind] ?? kind, initial: t('Neues Dokument') + '.' + kind, done: async name => {
      setNaming(null)
      try {
        const r = await api.post<{ path: string }>('/api/office/new', { dir: path ?? '', kind, name })
        pushUndo({ label: t('„{name}“ angelegt', { name: nameOf(r.path) }), run: async () => { await api.post('/api/files/delete', { paths: [r.path] }) } })
        changed(); setSelected([r.path])
        setEditing({ path: r.path, name: nameOf(r.path) })
      } catch (e) { fail(e instanceof ApiError && e.status === 409 ? new Error(t('„{name}“ gibt es hier schon.', { name })) : e) }
    },
  })
  const officeNew = (office?.create ?? []).map(k => (
    <MenuItem key={k} icon={<DocumentAdd20Regular />} onClick={() => newOffice(k)}>{OFFICE_NEW[k] ?? k}</MenuItem>
  ))
  const closeEditor = () => {
    setEditing(null)
    if (officeHash() !== null) history.replaceState(history.state, '', hashOf(view))
    changed()
    mainRef.current?.focus()
  }
  const remove = async (list: Item[]) => {
    if (!list.length) return
    try {
      const r = await api.post<{ trash: number[] }>('/api/files/delete', { paths: list.map(i => i.path) })
      const label = list.length === 1 ? t('„{name}“ gelöscht', { name: list[0].name }) : t('{n} Elemente gelöscht', { n: list.length })
      // In einem geteilten Ordner liegt das Gelöschte im Papierkorb des Besitzers - zurückholen kann nur er
      if (r.trash.length) pushUndo({ label, run: async () => { await api.post('/api/files/trash/restore', { ids: r.trash }) } })
      else notify(label)
    } catch (e) { fail(e) }
    if (path && list.some(i => inside(i.path, path))) openFolder(parentOf(list[0].path))
    changed()
  }
  const rename = (i: Item) => setNaming({
    title: t('Umbenennen'), initial: i.name, label: t('Neuer Name'), done: async name => {
      setNaming(null)
      if (name === i.name) return
      const to = join(parentOf(i.path), name)
      try {
        await api.post('/api/files/move', { from: i.path, to, copy: false })
        pushUndo({ label: t('Umbenannt in „{name}“', { name }), run: async () => { await api.post('/api/files/move', { from: to, to: i.path, copy: false }) } })
        if (path && inside(i.path, path)) openFolder(to + path.slice(i.path.length))
        changed(); setSelected([to])
      } catch (e) { fail(e instanceof ApiError && e.status === 409 ? new Error(t('„{name}“ gibt es hier schon.', { name })) : e) }
    },
  })
  /** Verschieben oder kopieren in einen Ordner; fragt bei belegten Namen; ueberspringt Unsinniges (in sich selbst, schon dort). */
  const transfer = async (paths: string[], target: string, copy: boolean) => {
    const todo = paths.filter(p => !inside(p, target) && (copy || parentOf(p) !== target))
    if (!todo.length) return
    if (!writableAt(target)) { fail(new Error(t('Nur lesbar – der Besitzer hat dir hier keine Bearbeitung erlaubt.'))); return }
    conflict.start()
    const moved: { from: string; to: string; copied?: boolean }[] = []
    let skipped = 0
    try {
      for (const p of todo) {
        let mode: Choice | 'fail' = 'fail'
        for (;;) {
          try {
            const r = await api.post<{ path: string; skipped: boolean; copied?: boolean }>('/api/files/move', { from: p, to: join(target, nameOf(p)), copy, conflict: mode })
            if (r.skipped) skipped++; else moved.push({ from: p, to: r.path, copied: r.copied })
            break
          } catch (e) {
            if (!(e instanceof ApiError && e.status === 409 && e.data?.conflict) || mode !== 'fail') throw e
            const c = await conflict.resolve(nameOf(p), paths.length > 0 && !!data?.items.find(i => i.path === p)?.isDir, todo.length > 1)
            if (!c) throw new Error(t('Abgebrochen'))
            mode = c
          }
        }
      }
    } catch (e) { if (!(e instanceof Error && e.message === t('Abgebrochen'))) fail(e) }
    if (moved.length) {
      // Aus einem geteilten Ordner heraus wird kopiert - das Original bleibt beim Besitzer
      const copiedOut = moved.some(m => m.copied)
      const asCopy = copy || copiedOut
      notify(asCopy ? t('{n} kopiert', { n: moved.length }) : t('{n} verschoben', { n: moved.length }),
        copiedOut ? t('Aus einem geteilten Ordner heraus wird kopiert – das Original bleibt beim Besitzer.') : skipped ? t('{n} übersprungen', { n: skipped }) : undefined)
      pushUndo(asCopy
        ? { label: t('{n} kopiert', { n: moved.length }), run: async () => { await api.post('/api/files/delete', { paths: moved.map(m => m.to) }) } }
        : { label: t('{n} verschoben', { n: moved.length }), run: async () => { for (const m of moved) await api.post('/api/files/move', { from: m.to, to: m.from, copy: false }) } })
    }
    changed()
  }
  const paste = (target = path ?? '') => {
    if (!clip) return
    const c = clip
    if (c.cut) setClip(null)
    // Ausschneiden aus einem nur lesbaren Ordner: es wird kopiert (der Server legt eine Kopie an und laesst das Original)
    transfer(c.paths, target, !c.cut || c.paths.some(p => !writableAt(p) || isMountRoot(p)))
  }
  const toClip = (list: Item[], cut: boolean) => {
    if (!list.length) return
    setClip({ paths: list.map(i => i.path), cut })
    // Beim Kopieren eines Bildes zusaetzlich das Bild selbst in die Zwischenablage von Windows/macOS -
    // dann laesst es sich in Chat, Mail oder Office mit Strg+V einfuegen
    if (!cut && copyImage(list[0])) return
    notify(cut ? t('{n} ausgeschnitten – mit Einfügen (Strg+V) ablegen', { n: list.length }) : t('{n} kopiert – mit Einfügen (Strg+V) ablegen', { n: list.length }), undefined, 'info')
  }
  /** Bild als PNG fuer die Zwischenablage (JPG, WebP ... umgewandelt - Browser nehmen dort nur PNG). */
  const pngOf = (i: Item) => fetch(ownSource.download(i, true), { credentials: 'same-origin' })
    .then(r => { if (!r.ok) throw new Error(String(r.status)); return r.blob() })
    .then(async b => {
      if (b.type === 'image/png') return b
      const bmp = await createImageBitmap(b)
      const c = document.createElement('canvas')
      c.width = bmp.width; c.height = bmp.height
      c.getContext('2d')!.drawImage(bmp, 0, 0)
      bmp.close()
      return await new Promise<Blob>((ok, no) => c.toBlob(x => x ? ok(x) : no(new Error('png')), 'image/png'))
    })
  // Schon beim Auswaehlen vorbereiten: manche Browser schreiben nur, wenn das Bild beim Tastendruck schon fertig ist
  const prepared = useRef<{ path: string; png: Promise<Blob>; ready?: Blob } | null>(null)
  useEffect(() => {
    const one = selected.length === 1 ? items.find(x => x.path === selected[0]) : undefined
    if (!one || one.isDir || kindOf(one) !== 'image' || one.size > 40 * 1024 * 1024) { prepared.current = null; return }
    if (prepared.current?.path === one.path) return
    const entry: { path: string; png: Promise<Blob>; ready?: Blob } = { path: one.path, png: pngOf(one) }
    entry.png.then(b => { entry.ready = b }).catch(() => { /* beim Kopieren erneut */ })
    prepared.current = entry
  }, [selected, items]) // eslint-disable-line react-hooks/exhaustive-deps
  /** Bild als PNG in die Zwischenablage des Systems. false: kein Bild oder der Browser kann es nicht. */
  const copyImage = (i: Item) => {
    if (i.isDir || kindOf(i) !== 'image' || typeof ClipboardItem === 'undefined' || !navigator.clipboard?.write) return false
    const p = prepared.current?.path === i.path ? prepared.current : null
    // Fertiges Bild direkt, sonst als Promise (Chrome/Edge warten darauf, solange der Tastendruck gilt)
    const content = p?.ready ?? p?.png ?? pngOf(i)
    navigator.clipboard.write([new ClipboardItem({ 'image/png': content })])
      .then(() => notify(t('Bild kopiert – in anderen Programmen mit Strg+V einfügen, hier in einem Ordner ablegen'), undefined, 'info'))
      .catch((e: unknown) => notify(t('Das Bild konnte nicht in die Zwischenablage von Windows – der Browser hat es abgelehnt ({reason}). In dmOffice ist es zum Einfügen kopiert.', { reason: e instanceof Error ? e.message : String(e) }), undefined, 'warning'))
    return true
  }
  const favorite = async (i: Item, on: boolean) => {
    try { await api.post('/api/files/favorite', { path: i.path, on }); changed() } catch (e) { fail(e) }
  }
  const quickLink = async (i: Item) => {
    try {
      const l = await api.post<Link>('/api/files/links', { path: i.path, mode: 'read' })
      if (await copyText(linkUrl(l))) notify(t('Link kopiert'), t('Jeder mit dem Link kann „{name}“ ansehen.', { name: i.name }))
      changed()
    } catch (e) { fail(e) }
  }
  const open = (i: Item) => {
    if (i.isDir) { openFolder(i.path); return }
    // Office-Dateien (ohne eigenen Betrachter, also nicht CSV) im Editor des Office-Servers
    if (!viewKind(i) && officeMode(i, office)) { setEditing({ path: i.path, name: i.name, view: officeMode(i, office) === 'view' }); return }
    if (viewKind(i)) {
      const list = items.filter(x => !x.isDir && viewKind(x))
      setStartEdit(false)
      setViewer({ list, index: Math.max(0, list.findIndex(x => x.path === i.path)) })
    } else download([i])
  }
  /** Eine Datei direkt, sonst (Ordner, mehrere) als ZIP - gestreamt vom Server. */
  const download = (list: Item[]) => {
    if (!list.length) return
    if (list.length === 1 && !list[0].isDir) {
      const a = document.createElement('a')
      a.href = '/api/files/download?' + q({ path: list[0].path })
      a.download = list[0].name
      a.click()
      return
    }
    const form = document.createElement('form')
    form.method = 'POST'; form.action = '/api/files/zip'; form.style.display = 'none'
    const add = (k: string, v: string) => { const inp = document.createElement('input'); inp.type = 'hidden'; inp.name = k; inp.value = v; form.appendChild(inp) }
    list.forEach(i => add('paths', i.path))
    const base = commonParent(list.map(i => i.path))
    add('base', base)
    add('name', list.length === 1 ? list[0].name || t('Dateien') : nameOf(base) || t('Dateien'))
    document.body.appendChild(form); form.submit(); form.remove()
  }
  const saveText = async (i: Item, text: string, force: boolean) => {
    const r = await api.post<{ etag: string }>('/api/files/save', { path: i.path, text, etag: force ? undefined : i.etag })
    const next = { ...i, etag: r.etag, size: new Blob([text]).size, modified: new Date().toISOString() }
    setViewer(v => v && { ...v, list: v.list.map(x => x.path === i.path ? next : x) })
    changed()
    return next
  }

  // ---------------- Auswahl, Tastatur ----------------
  const click = (e: React.MouseEvent, i: Item) => {
    if (touch && !selected.length) { open(i); return }
    // Ordner: ein Klick wechselt hinein (auswaehlen ueber das Kaestchen oder mit Strg/Umschalt)
    if (i.isDir && !e.ctrlKey && !e.metaKey && !e.shiftKey && !touch) { open(i); return }
    if (e.shiftKey && anchor.current) {
      const a = items.findIndex(x => x.path === anchor.current), b = items.findIndex(x => x.path === i.path)
      const range = items.slice(Math.min(a, b), Math.max(a, b) + 1).map(x => x.path)
      setSelected(e.ctrlKey || e.metaKey ? Array.from(new Set([...selected, ...range])) : range)
      return
    }
    anchor.current = i.path
    if (e.ctrlKey || e.metaKey || touch) setSelected(sel => sel.includes(i.path) ? sel.filter(p => p !== i.path) : [...sel, i.path])
    else setSelected([i.path])
  }
  const moveFocus = (delta: number, extend: boolean) => {
    if (!items.length) return
    const cur = items.findIndex(x => x.path === (anchor.current && selected.includes(anchor.current) ? anchor.current : selected[selected.length - 1]))
    const next = Math.max(0, Math.min(items.length - 1, cur < 0 ? 0 : cur + delta))
    const p = items[next].path
    if (extend && anchor.current) {
      const a = items.findIndex(x => x.path === anchor.current)
      setSelected(items.slice(Math.min(a, next), Math.max(a, next) + 1).map(x => x.path))
    } else { anchor.current = p; setSelected([p]) }
    mainRef.current?.querySelector(`[data-path="${CSS.escape(p)}"]`)?.scrollIntoView({ block: 'nearest' })
  }
  const columns = () => {
    const grid = mainRef.current?.querySelector('[data-grid]') as HTMLElement | null
    return grid ? Math.max(1, getComputedStyle(grid).gridTemplateColumns.split(' ').length) : 1
  }
  const keys = (e: React.KeyboardEvent) => {
    const tag = (e.target as HTMLElement).tagName
    if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return
    const ctrl = e.ctrlKey || e.metaKey
    if (view.kind === 'trash') {
      if (e.key === 'Delete') { e.preventDefault(); trash.purge() }
      return
    }
    if (e.key === 'Delete') { e.preventDefault(); if (canChange(sel)) remove(sel) }
    else if (e.key === 'F2' && one) { e.preventDefault(); if (canChange([one])) rename(one) }
    else if (e.key === 'Enter' && one) { e.preventDefault(); open(one) }
    else if (e.key === ' ' && one && !one.isDir) { e.preventDefault(); open(one) }
    else if (ctrl && e.key.toLowerCase() === 'a') { e.preventDefault(); setSelected(items.map(i => i.path)) }
    else if (ctrl && e.key.toLowerCase() === 'c') { e.preventDefault(); toClip(sel, false) }
    else if (ctrl && e.key.toLowerCase() === 'x') { e.preventDefault(); if (canChange(sel)) toClip(sel, true) }
    else if (ctrl && e.key.toLowerCase() === 'v' && path !== null) { e.preventDefault(); if (writableAt(path)) paste() }
    else if (ctrl && e.key.toLowerCase() === 'z') { e.preventDefault(); undo() }
    else if (ctrl && e.shiftKey && e.key.toLowerCase() === 'n') { e.preventDefault(); if (path !== null && writableAt(path)) mkdir() }
    else if ((e.key === 'Backspace' || (e.altKey && e.key === 'ArrowUp')) && path) { e.preventDefault(); openFolder(parentOf(path)) }
    else if (e.key === 'ArrowDown') { e.preventDefault(); moveFocus(viewMode === 'tiles' ? columns() : 1, e.shiftKey) }
    else if (e.key === 'ArrowUp') { e.preventDefault(); moveFocus(viewMode === 'tiles' ? -columns() : -1, e.shiftKey) }
    else if (e.key === 'ArrowRight' && viewMode === 'tiles') { e.preventDefault(); moveFocus(1, e.shiftKey) }
    else if (e.key === 'ArrowLeft' && viewMode === 'tiles') { e.preventDefault(); moveFocus(-1, e.shiftKey) }
    else if (e.key === 'Home') { e.preventDefault(); moveFocus(-items.length, e.shiftKey) }
    else if (e.key === 'End') { e.preventDefault(); moveFocus(items.length, e.shiftKey) }
    else if (e.key === 'F5') { e.preventDefault(); changed() }
    else if (e.key === 'Escape') { if (search) setSearch(''); else setSelected([]) }
  }

  // Nach Dialogen liegt der Fokus oft auf der Seite selbst - die Tasten gelten dann trotzdem der Liste
  const keysRef = useRef(keys)
  keysRef.current = keys
  useEffect(() => {
    const on = (e: KeyboardEvent) => {
      if (e.target !== document.body || viewer || editing || naming || share || picker || conflict.dialog) return
      keysRef.current(e as unknown as React.KeyboardEvent)
    }
    window.addEventListener('keydown', on)
    return () => window.removeEventListener('keydown', on)
  })

  // Ziehen: die Auswahl (oder die gezogene Zeile); Ablegen auf Ordner verschiebt, mit Strg kopiert
  const dragStart = (e: React.DragEvent, i: Item) => {
    const paths = selected.includes(i.path) ? selected : [i.path]
    if (!selected.includes(i.path)) setSelected([i.path])
    e.dataTransfer.setData(DRAG, JSON.stringify(paths))
    // Eine Datei auf den Desktop oder in den Explorer ziehen (Chrome/Edge): der Browser laedt sie angemeldet herunter
    if (paths.length === 1 && !i.isDir) {
      const url = new URL(ownSource.download(i), location.origin).href
      e.dataTransfer.setData('DownloadURL', `${i.mime || 'application/octet-stream'}:${i.name.replace(/:/g, '_')}:${url}`)
    }
    e.dataTransfer.effectAllowed = 'copyMove'
  }
  const dropOn = (e: React.DragEvent, target: string) => {
    e.preventDefault()
    setDragging(false); setOverRow(null)
    const own = e.dataTransfer.getData(DRAG)
    if (own) { transfer(JSON.parse(own), target, e.ctrlKey || e.altKey); return }
    const entries = entriesOf(e.dataTransfer)
    if (entries.length) readEntries(entries).then(list => send(list, target)).catch(fail)
    else if (e.dataTransfer.files.length) send(fromInput(e.dataTransfer.files), target)
  }

  // ---------------- Menues ----------------
  const [ctx, setCtx] = useState<{ x: number; y: number; kind: 'items' | 'empty' | 'folder'; folder?: string } | null>(null)
  const menuItems = (list: Item[]) => {
    const single = list.length === 1 ? list[0] : null
    // In einem mit mir geteilten Ordner: Teilen, Link und Favoriten sind Sache des Besitzers; ohne Bearbeitungsrecht nichts, was ändert
    const mine = !list.some(i => inWithMe(i.path, anyShared))
    const change = canChange(list)
    return (
      <MenuList>
        {single && <MenuItem icon={<FolderOpen20Regular />} onClick={() => open(single)}>{t('Öffnen')}</MenuItem>}
        {single && officeMode(single, office) === 'edit' && <MenuItem icon={<DocumentEdit20Regular />} onClick={() => setEditing({ path: single.path, name: single.name })}>{t('Im Browser bearbeiten')}</MenuItem>}
        {single && flat && <MenuItem icon={<Folder20Regular />} onClick={() => { openFolder(parentOf(single.path)); setTimeout(() => setSelected([single.path]), 300) }}>{t('Speicherort öffnen')}</MenuItem>}
        <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download(list)}>{list.length > 1 || single?.isDir ? t('Als ZIP herunterladen') : t('Herunterladen')}</MenuItem>
        {single && mine && <MenuItem icon={<Share20Regular />} onClick={() => setShare(single)}>{t('Teilen …')}</MenuItem>}
        {single && mine && <MenuItem icon={<Link20Regular />} onClick={() => quickLink(single)}>{t('Link kopieren')}</MenuItem>}
        {single && mine && <MenuItem icon={single.favorite ? <Star20Filled color="#E8A33D" /> : <Star20Regular />} onClick={() => favorite(single, !single.favorite)}>
          {single.favorite ? t('Aus Favoriten entfernen') : t('Zu Favoriten hinzufügen')}</MenuItem>}
        <MenuDivider />
        {change && <MenuItem icon={<Cut20Regular />} secondaryContent="Strg+X" onClick={() => toClip(list, true)}>{t('Ausschneiden')}</MenuItem>}
        <MenuItem icon={<Copy20Regular />} secondaryContent="Strg+C" onClick={() => toClip(list, false)}>{t('Kopieren')}</MenuItem>
        {single?.isDir && clip && writableAt(single.path) && <MenuItem icon={<ClipboardPaste20Regular />} onClick={() => paste(single.path)}>{t('Hier einfügen')}</MenuItem>}
        {single && change && <MenuItem icon={<Rename20Regular />} secondaryContent="F2" onClick={() => rename(single)}>{t('Umbenennen')}</MenuItem>}
        {change && <MenuItem icon={<FolderArrowRight20Regular />} onClick={() => setPicker({ mode: 'move', paths: list.map(i => i.path) })}>{t('Verschieben nach …')}</MenuItem>}
        <MenuItem icon={<Copy20Regular />} onClick={() => setPicker({ mode: 'copy', paths: list.map(i => i.path) })}>{t('Kopieren nach …')}</MenuItem>
        {change && <MenuDivider />}
        {change && <MenuItem icon={<Delete20Regular />} secondaryContent="Entf" onClick={() => remove(list)}>{t('Löschen')}</MenuItem>}
        <MenuItem icon={<Info20Regular />} onClick={() => { setSelected(list.map(i => i.path)); setDetails(true) }}>{t('Details')}</MenuItem>
      </MenuList>
    )
  }
  const readOnly = path !== null && !writableAt(path)
  const emptyMenu = (
    <MenuList>
      {!readOnly && <>
        <MenuItem icon={<FolderAdd20Regular />} onClick={() => mkdir()}>{t('Neuer Ordner')}</MenuItem>
        <MenuItem icon={<DocumentAdd20Regular />} onClick={() => newText('.txt')}>{t('Neue Textdatei')}</MenuItem>
        {officeNew}
        <MenuItem icon={<ArrowUpload20Regular />} onClick={() => upload.current?.click()}>{t('Dateien hochladen')}</MenuItem>
        <MenuItem icon={<FolderArrowUp20Regular />} onClick={() => uploadDir.current?.click()}>{t('Ordner hochladen')}</MenuItem>
        {clip && <MenuItem icon={<ClipboardPaste20Regular />} secondaryContent="Strg+V" onClick={() => paste()}>{t('Einfügen')}</MenuItem>}
        <MenuDivider />
      </>}
      <MenuItem onClick={() => setSelected(items.map(i => i.path))} secondaryContent="Strg+A">{t('Alle auswählen')}</MenuItem>
      <MenuItem icon={<ArrowSync20Regular />} onClick={changed}>{t('Aktualisieren')}</MenuItem>
    </MenuList>
  )
  const folderItem = (p: string): Item => ({ path: p, name: nameOf(p), isDir: true, size: 0, mime: null, modified: '', favorite: favs.some(f => f.path === p) })
  const folderMenu = (f: string) => (
    <MenuList>
      <MenuItem icon={<FolderOpen20Regular />} onClick={() => openFolder(f)}>{t('Öffnen')}</MenuItem>
      <MenuItem icon={<FolderAdd20Regular />} onClick={() => mkdir(f)}>{t('Neuer Ordner')}</MenuItem>
      {clip && <MenuItem icon={<ClipboardPaste20Regular />} onClick={() => paste(f)}>{t('Hier einfügen')}</MenuItem>}
      <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download([folderItem(f)])}>{t('Als ZIP herunterladen')}</MenuItem>
      <MenuItem icon={<Share20Regular />} onClick={() => setShare(folderItem(f))}>{t('Teilen …')}</MenuItem>
      {f && <>
        <MenuItem icon={favs.some(x => x.path === f) ? <Star20Filled color="#E8A33D" /> : <Star20Regular />}
          onClick={() => favorite(folderItem(f), !favs.some(x => x.path === f))}>{favs.some(x => x.path === f) ? t('Aus Favoriten entfernen') : t('Zu Favoriten hinzufügen')}</MenuItem>
        <MenuItem icon={<Rename20Regular />} onClick={() => rename(folderItem(f))}>{t('Umbenennen')}</MenuItem>
        <MenuItem icon={<FolderArrowRight20Regular />} onClick={() => setPicker({ mode: 'move', paths: [f] })}>{t('Verschieben nach …')}</MenuItem>
        <MenuItem icon={<Copy20Regular />} onClick={() => setPicker({ mode: 'copy', paths: [f] })}>{t('Kopieren nach …')}</MenuItem>
        <MenuDivider />
        <MenuItem icon={<Delete20Regular />} onClick={() => remove([folderItem(f)])}>{t('Löschen')}</MenuItem>
      </>}
    </MenuList>
  )

  // Handy: Detailbereich und Betrachter schliessen mit der Zurueck-Taste
  useBackClose(mobile && detailsOpen, () => setDetails(false))
  useBackClose(!!viewer, () => setViewer(null))
  useBackClose(!!editing, closeEditor)

  // ---------------- Menueband ----------------
  const inFiles = view.kind !== 'trash' && view.kind !== 'shared'
  const start = view.kind === 'trash' ? <>
    <RibbonButton primary icon={<ArrowReset20Regular />} label={t('Wiederherstellen')} disabled={!trash.selected.length} onClick={() => trash.restore()} />
    <RibbonButton icon={<DeleteDismiss20Regular />} label={t('Endgültig löschen')} disabled={!trash.selected.length} onClick={() => trash.purge()} />
    <RibbonDivider />
    <RibbonButton icon={<Delete20Regular />} label={t('Papierkorb leeren')} disabled={!trash.data?.items.length} onClick={trash.empty} />
  </> : view.kind === 'shared' ? <>
    <RibbonButton icon={<ArrowSync20Regular />} label={t('Aktualisieren')} onClick={links.load} />
  </> : <>
    {path !== null && !readOnly && <>
      <Menu>
        <MenuTrigger disableButtonEnhancement><RibbonButton primary icon={<DocumentAdd20Regular />} label={t('Neu')} /></MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem icon={<FolderAdd20Regular />} onClick={() => mkdir()}>{t('Ordner')}</MenuItem>
            <MenuItem icon={<DocumentAdd20Regular />} onClick={() => newText('.txt')}>{t('Textdatei')}</MenuItem>
            <MenuItem icon={<DocumentAdd20Regular />} onClick={() => newText('.md')}>{t('Markdown-Dokument')}</MenuItem>
            {officeNew.length > 0 && <MenuDivider />}
            {officeNew}
          </MenuList>
        </MenuPopover>
      </Menu>
      <Menu>
        <MenuTrigger disableButtonEnhancement><RibbonButton icon={busy ? <Spinner size="tiny" /> : <ArrowUpload20Regular />} label={t('Hochladen')} /></MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem icon={<ArrowUpload20Regular />} onClick={() => upload.current?.click()}>{t('Dateien')}</MenuItem>
            <MenuItem icon={<FolderArrowUp20Regular />} onClick={() => uploadDir.current?.click()}>{t('Ordner')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
      <RibbonDivider />
    </>}
    <RibbonButton icon={<FolderOpen20Regular />} label={t('Öffnen')} disabled={!one} onClick={() => one && open(one)} />
    <RibbonButton icon={<ArrowDownload20Regular />} label={t('Herunterladen')} disabled={!sel.length} onClick={() => download(sel)} />
    <RibbonButton icon={<Share20Regular />} label={t('Teilen')} disabled={!one || inWithMe(one.path, anyShared)} onClick={() => one && setShare(one)} />
    <RibbonButton icon={<Rename20Regular />} label={t('Umbenennen')} disabled={!one || !canChange([one])} onClick={() => one && rename(one)} />
    <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!sel.length || !canChange(sel)} onClick={() => remove(sel)} />
    <RibbonDivider />
    <RibbonButton icon={<Cut20Regular />} label={t('Ausschneiden')} disabled={!sel.length || !canChange(sel)} onClick={() => toClip(sel, true)} />
    <RibbonButton icon={<Copy20Regular />} label={t('Kopieren')} disabled={!sel.length} onClick={() => toClip(sel, false)} />
    <RibbonButton icon={<ClipboardPaste20Regular />} label={t('Einfügen')} disabled={!clip || path === null || readOnly} onClick={() => paste()} />
    <RibbonButton icon={<FolderArrowRight20Regular />} label={t('Verschieben nach')} disabled={!sel.length || !canChange(sel)} onClick={() => setPicker({ mode: 'move', paths: sel.map(i => i.path) })} />
    <RibbonButton icon={<ArrowUndo20Regular />} label={t('Rückgängig')} disabled={!undos.current.length} onClick={undo} />
    <RibbonDivider />
    <RibbonButton icon={one?.favorite ? <Star20Filled color="#E8A33D" /> : <Star20Regular />} label={t('Favorit')} disabled={!one || inWithMe(one.path, anyShared)} pressed={!!one?.favorite}
      onClick={() => one && favorite(one, !one.favorite)} />
    <RibbonButton icon={<Info20Regular />} label={t('Details')} pressed={detailsOpen} onClick={() => setDetails(!detailsOpen)} />
    <RibbonDivider />
    <RibbonButton icon={<DocumentSearch20Regular />} label={t('Inhalte durchsuchen')} onClick={() => setContentSearch(true)} />
  </>
  const viewRibbon = <>
    <RibbonButton icon={<AppsList20Regular />} label={t('Details')} pressed={viewMode === 'details'} disabled={view.kind === 'photos'} onClick={() => update({ fileView: 'details' })} />
    <RibbonButton icon={<Grid20Regular />} label={t('Kacheln')} pressed={viewMode === 'tiles'} onClick={() => update({ fileView: 'tiles' })} />
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ArrowSort20Regular />} label={t('Sortieren')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ sort: [sort], dir: [desc ? 'desc' : 'asc'] }}
          onCheckedValueChange={(_, d) => d.name === 'sort' ? setSort({ sort: d.checkedItems[0] as any }) : setSort({ desc: d.checkedItems[0] === 'desc' })}>
          <MenuItemRadio name="sort" value="name">{t('Name')}</MenuItemRadio>
          <MenuItemRadio name="sort" value="modified">{t('Geändert')}</MenuItemRadio>
          <MenuItemRadio name="sort" value="size">{t('Größe')}</MenuItemRadio>
          {!photosView && <MenuItemRadio name="sort" value="kind">{t('Art')}</MenuItemRadio>}
          <MenuDivider />
          <MenuItemRadio name="dir" value="asc">{t('Aufsteigend')}</MenuItemRadio>
          <MenuItemRadio name="dir" value="desc">{t('Absteigend')}</MenuItemRadio>
        </MenuList>
      </MenuPopover>
    </Menu>
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ZoomIn20Regular />} label={t('Kachelgröße')} disabled={viewMode !== 'tiles' && view.kind !== 'photos'} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ tile: [tileSize] }} onCheckedValueChange={(_, d) => update({ fileTile: d.checkedItems[0] as any })}>
          <MenuItemRadio name="tile" value="small">{t('Klein')}</MenuItemRadio>
          <MenuItemRadio name="tile" value="medium">{t('Mittel')}</MenuItemRadio>
          <MenuItemRadio name="tile" value="large">{t('Groß')}</MenuItemRadio>
        </MenuList>
      </MenuPopover>
    </Menu>
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<GroupList20Regular />} label={t('Gruppieren')} /></MenuTrigger>
      <MenuPopover>
        {photosView ? (
          <MenuList checkedValues={{ group: [photoGroup] }} onCheckedValueChange={(_, d) => update({ photoGroup: d.checkedItems[0] as any })}>
            <MenuItemRadio name="group" value="day">{t('Nach Tag')}</MenuItemRadio>
            <MenuItemRadio name="group" value="month">{t('Nach Monat')}</MenuItemRadio>
            <MenuItemRadio name="group" value="year">{t('Nach Jahr')}</MenuItemRadio>
            <MenuItemRadio name="group" value="none">{t('Nicht gruppieren')}</MenuItemRadio>
          </MenuList>
        ) : (
        <MenuList checkedValues={{ group: [groupBy] }} onCheckedValueChange={(_, d) => update({ fileGroup: d.checkedItems[0] as any })}>
          <MenuItemRadio name="group" value="none">{t('Nicht gruppieren')}</MenuItemRadio>
          <MenuItemRadio name="group" value="kind">{t('Art')}</MenuItemRadio>
          <MenuItemRadio name="group" value="date">{t('Änderungsdatum')}</MenuItemRadio>
        </MenuList>
        )}
      </MenuPopover>
    </Menu>
    <RibbonDivider />
    <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />
    <RibbonButton icon={<Info20Regular />} label={t('Detailbereich')} pressed={detailsOpen} onClick={() => setDetails(!detailsOpen)} />
    <RibbonDivider />
    <RibbonButton icon={<Settings20Regular />} label={t('WebDAV-Zugang')} onClick={() => { location.hash = '#/settings/devices' }} />
  </>

  // ---------------- Liste ----------------
  const parts = path ? path.split('/') : []
  const ctxMenuFor = (e: React.MouseEvent, i: Item, isSel: boolean) => {
    e.preventDefault(); e.stopPropagation()
    if (!isSel) { setSelected([i.path]); anchor.current = i.path }
    setCtx({ x: e.clientX, y: e.clientY, kind: 'items' })
  }
  const dropTarget = (i: Item) => ({
    onDragOver: (e: React.DragEvent) => {
      if (!i.isDir) return
      if (e.dataTransfer.types.includes(DRAG) || e.dataTransfer.types.includes('Files')) { e.preventDefault(); e.stopPropagation(); setOverRow(i.path) }
    },
    onDragLeave: () => setOverRow(o => (o === i.path ? null : o)),
    onDrop: (e: React.DragEvent) => { if (i.isDir) { e.stopPropagation(); dropOn(e, i.path) } },
  })
  const badges = (i: Item) => (i.favorite || i.shared || i.sharedWith || i.owner) && (
    <span className={s.badges}>
      {i.favorite && <Tooltip content={t('Favorit')} relationship="label"><Star16Filled color="#E8A33D" /></Tooltip>}
      {i.shared && <Tooltip content={t('Über Link geteilt')} relationship="label"><Link16Regular /></Tooltip>}
      {i.sharedWith && <Tooltip content={t('Mit Personen geteilt')} relationship="label"><People16Regular /></Tooltip>}
      {i.owner && <Tooltip content={i.canWrite ? t('Von {owner} geteilt – du darfst bearbeiten', { owner: i.owner }) : t('Von {owner} geteilt – nur lesen', { owner: i.owner })} relationship="label"><People16Regular /></Tooltip>}
    </span>
  )
  const sortHead = (k: 'name' | 'modified' | 'size' | 'kind', label: string, style?: React.CSSProperties) => {
    const sortable = !(view.kind === 'recent' && !searching) && !(view.kind === 'photos' && !searching)
    return (
      <TableHeaderCell style={style} className={sortable ? s.sortable : undefined} onClick={sortable ? () => sortBy(k) : undefined}
        aria-sort={sortable && sort === k ? (desc ? 'descending' : 'ascending') : undefined}>
        {label}{sortable && sort === k && (desc ? <ArrowDown16Regular style={{ marginLeft: 4 }} /> : <ArrowUp16Regular style={{ marginLeft: 4 }} />)}
      </TableHeaderCell>
    )
  }
  const emptyText = () => searching ? t('Nichts gefunden.')
    : view.kind === 'recent' ? t('Noch nichts verwendet.')
      : view.kind === 'favorites' ? t('Noch keine Favoriten. Rechtsklick auf eine Datei oder einen Ordner → „Zu Favoriten hinzufügen“.')
        : view.kind === 'photos' ? t('Keine Bilder in deiner Ablage.')
          : filter ? t('Keine Einträge dieser Art in diesem Ordner.') : t('Leer. Dateien hierher ziehen oder „Hochladen“.')
  const list = () => {
    if (view.kind === 'trash') return <TrashList trash={trash} />
    if (view.kind === 'shared') return <LinksList links={links} notify={m => notify(m)} fail={fail}
      onOpen={(p, isDir) => isDir ? openFolder(p) : (openFolder(parentOf(p)), setTimeout(() => setSelected([p]), 300))}
      onManage={l => l.item && setShare(l.item)} />
    if (!data) return <Spinner />
    if (items.length === 0) return <Text className={s.empty}>{emptyText()}</Text>
    if (viewMode === 'tiles') return (
      <>
        {cappedGroups.map(g => (
          <Fragment key={g.label || 'alle'}>
            {g.label && <div className={s.groupHead}>{g.label} <span className={s.muted}>{g.total}</span></div>}
            <div className={s.tiles} data-grid style={{ ['--tile' as any]: tilePx }}>
              {g.items.map(i => {
          const isSel = selected.includes(i.path)
          return (
            <div key={i.path} data-path={i.path} className={mergeClasses(s.tile, isSel && s.tileSel, overRow === i.path && s.target, clip?.cut && clip.paths.includes(i.path) && s.cut)}
              aria-selected={isSel} title={i.isDir ? i.name : `${i.name}\n${fmtSize(i.size)} · ${new Date(i.modified).toLocaleString(locale)}`}
              draggable={!touch} onDragStart={e => dragStart(e, i)} {...dropTarget(i)}
              onDoubleClick={() => open(i)} onClick={e => click(e, i)} onContextMenu={e => ctxMenuFor(e, i, isSel)}>
              <span className={s.tileCheck} onClick={e => { e.stopPropagation(); anchor.current = i.path; setSelected(sl => isSel ? sl.filter(p => p !== i.path) : [...sl, i.path]) }}>
                <Checkbox checked={isSel} aria-label={t('Auswählen')} />
              </span>
              {(i.favorite || i.shared || i.sharedWith || i.owner) && <span className={s.tileBadge}>{i.favorite && <Star16Filled color="#E8A33D" />}{i.shared && <Link16Regular />}{(i.sharedWith || i.owner) && <People16Regular />}</span>}
              <div className={s.thumb}>
                {hasThumb(i) ? <Thumb item={i} source={ownSource} className={s.thumbImg} fallbackLarge /> : <KindIcon item={i} large />}
              </div>
              <span className={s.tileName}>{i.name}</span>
            </div>
          )
              })}
            </div>
          </Fragment>
        ))}
        {shown.length > cap && <MoreOnScroll onMore={() => setCap(c => c + TILE_STEP)} />}
      </>
    )
    return (
      <Table size="small" aria-label={t('Dateien')}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell style={{ width: 36 }}>
              <Checkbox checked={sel.length === 0 ? false : sel.length === items.length ? true : 'mixed'} aria-label={t('Alle auswählen')}
                onChange={() => setSelected(sel.length === items.length ? [] : items.map(i => i.path))} />
            </TableHeaderCell>
            {sortHead('name', t('Name'))}
            {cols >= 2 && flat && <TableHeaderCell>{t('Ort')}</TableHeaderCell>}
            {cols >= 2 && (view.kind === 'recent' && !searching
              ? <TableHeaderCell style={{ width: 160 }}>{t('Verwendet')}</TableHeaderCell>
              : sortHead('modified', t('Geändert'), { width: 160 }))}
            {cols >= 3 && !flat && sortHead('kind', t('Art'), { width: 110 })}
            {cols >= 2 && sortHead('size', t('Größe'), { width: 100 })}
            <TableHeaderCell style={{ width: 44 }} />
          </TableRow>
        </TableHeader>
        <TableBody>
          {groups.flatMap(g => [
            ...(g.label ? [<TableRow key={'g:' + g.label} className={s.groupRow}><TableCell colSpan={8}><span className={s.groupHead}>{g.label} <span className={s.muted}>{g.items.length}</span></span></TableCell></TableRow>] : []),
            ...g.items.map(i => {
            const isSel = selected.includes(i.path)
            const when = view.kind === 'recent' && !searching && i.used ? i.used : i.modified
            return (
              <TableRow key={i.path} data-path={i.path} className={mergeClasses(s.row, isSel && s.sel, overRow === i.path && s.target, clip?.cut && clip.paths.includes(i.path) && s.cut)}
                aria-selected={isSel} draggable={!touch} onDragStart={e => dragStart(e, i)} {...dropTarget(i)}
                onDoubleClick={() => open(i)} onClick={e => click(e, i)} onContextMenu={e => ctxMenuFor(e, i, isSel)}>
                <TableCell onClick={e => { e.stopPropagation(); anchor.current = i.path; setSelected(sl => isSel ? sl.filter(p => p !== i.path) : [...sl, i.path]) }}>
                  <Checkbox checked={isSel} aria-label={t('Auswählen')} />
                </TableCell>
                <TableCell style={{ minWidth: 0, overflow: 'hidden' }}>
                  <TableCellLayout truncate title={i.name} media={<span className={s.rowIcon}>{hasThumb(i) && kindOf(i) === 'image' ? <Thumb item={i} source={ownSource} className={s.rowThumb} /> : <KindIcon item={i} />}</span>}
                    description={cols < 2 ? [flat ? '/' + parentOf(i.path) : null, fmtWhen(when, locale), i.isDir ? null : fmtSize(i.size)].filter(Boolean).join(' · ') : undefined}>
                    {i.name}{badges(i)}
                  </TableCellLayout>
                </TableCell>
                {cols >= 2 && flat && <TableCell className={s.muted} style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>/{parentOf(i.path)}</TableCell>}
                {cols >= 2 && <TableCell>{fmtWhen(when, locale)}</TableCell>}
                {cols >= 3 && !flat && <TableCell className={s.muted}>{kindLabel(kindOf(i))}</TableCell>}
                {cols >= 2 && <TableCell>{i.isDir ? '' : fmtSize(i.size)}</TableCell>}
                <TableCell>
                  <Menu>
                    <MenuTrigger disableButtonEnhancement><Button appearance="subtle" size="small" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} onClick={e => e.stopPropagation()} /></MenuTrigger>
                    <MenuPopover>{menuItems(isSel && sel.length > 1 ? sel : [i])}</MenuPopover>
                  </Menu>
                </TableCell>
              </TableRow>
            )
            }),
          ])}
        </TableBody>
      </Table>
    )
  }

  const navEntry = (k: Special, icon: JSX.Element) => (
    <div className={mergeClasses(s.nav, view.kind === k && s.active)} onClick={() => { go({ kind: k }); if (mobile) closeSide() }}>
      <span className={s.twisty} />{icon}<span className={s.navName}>{viewTitle(k)}</span>
    </div>
  )
  // Mit Kontingent zaehlt alles, was der Benutzer belegt (auch Mails, Kalender, Kontakte, Notizen), sonst nur die Dateiablage
  const total = usage ? (usage.quota > 0 && usage.used != null ? usage.used : usage.usage + usage.trash + usage.versions) : 0
  const detailItem = one ?? (sel.length === 0 && path !== null && data?.folder ? data.folder : null)
  // Volltextsuche in Dateiinhalten (Search/TextIndex)
  const [contentSearch, setContentSearch] = useState(false)

  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      <RibbonHeader area="files" onMenu={toggleSide} start={start} view={viewRibbon} />
      {contentSearch && <ContentSearch under={path ?? ''} onGo={p => { setContentSearch(false); openFolder(p) }} onClose={() => setContentSearch(false)} />}
      <div className={s.root}>
        <SidePane as="aside" className={s.side} open={sidePane} onClose={closeSide}>
          <FolderTree current={path} onOpen={p => { openFolder(p); if (mobile) closeSide() }} onDropOn={dropOn} version={treeVersion} listed={listed}
            onContext={(e, f) => setCtx({ x: e.clientX, y: e.clientY, kind: 'folder', folder: f })} />
          <div className={s.navSep} />
          {navEntry('recent', <Clock20Regular />)}
          {navEntry('favorites', <Star20Regular />)}
          {favs.filter(f => f.isDir).map(f => (
            <div key={f.path} className={mergeClasses(s.nav, path === f.path && s.active)} style={{ paddingLeft: 30 }} title={'/' + f.path}
              onClick={() => { openFolder(f.path); if (mobile) closeSide() }}
              onDragOver={e => { if (e.dataTransfer.types.includes(DRAG) || e.dataTransfer.types.includes('Files')) e.preventDefault() }}
              onDrop={e => { e.stopPropagation(); dropOn(e, f.path) }}
              onContextMenu={e => { e.preventDefault(); e.stopPropagation(); setCtx({ x: e.clientX, y: e.clientY, kind: 'folder', folder: f.path }) }}>
              <Folder20Regular /><span className={s.navName}>{f.name}</span>
            </div>
          ))}
          {anyShared && <>
            <div className={mergeClasses(s.nav, inWithMe(path ?? '', anyShared) && path === WITH_ME && s.active)}
              onClick={() => { openFolder(WITH_ME); if (mobile) closeSide() }}>
              <span className={s.twisty} /><People20Regular /><span className={s.navName}>{t(WITH_ME)}</span>
            </div>
            {withMe.map(m => (
              <div key={m.id} className={mergeClasses(s.nav, path !== null && (path === m.path || path.startsWith(m.path + '/')) && s.active)} style={{ paddingLeft: 30 }}
                title={t('Von {owner}', { owner: m.owner }) + (m.canWrite ? '' : ' · ' + t('nur lesen'))}
                onClick={() => { openFolder(m.path); if (mobile) closeSide() }}>
                <Folder20Regular /><span className={s.navName}>{m.name}</span>
              </div>
            ))}
          </>}
          {navEntry('photos', <Image20Regular />)}
          {navEntry('shared', <LinkSquare20Regular />)}
          {navEntry('trash', <Delete20Regular />)}
          <div style={{ flex: 1 }} />
          {usage && (
            <div className={s.foot}>
              {usage.quota > 0 ? <>
                <ProgressBar value={Math.min(1, total / usage.quota)} color={total / usage.quota > 0.9 ? 'error' : 'brand'} />
                <span>{t('{used} von {quota} belegt', { used: fmtSize(total), quota: fmtSize(usage.quota) })}</span>
              </> : <span>{t('Belegt: {used}', { used: fmtSize(total) })}</span>}
              {(usage.trash > 0 || usage.versions > 0) && <span>{t('davon Papierkorb {trash}, frühere Versionen {versions}', { trash: fmtSize(usage.trash), versions: fmtSize(usage.versions) })}</span>}
            </div>
          )}
        </SidePane>
        <div ref={mainRef} className={mergeClasses(s.main, dragging && s.drop)} tabIndex={0} onKeyDown={keys}
          onClick={e => { if (e.target === e.currentTarget) setSelected([]) }}
          onDragOver={e => { if (path !== null && !readOnly && e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragging(true) } }}
          onDragLeave={e => { if (e.currentTarget === e.target) setDragging(false) }}
          onDrop={e => { if (path !== null && e.dataTransfer.types.includes('Files') && !e.dataTransfer.types.includes(DRAG)) dropOn(e, path); else { e.preventDefault(); setDragging(false) } }}>
          <input ref={upload} type="file" multiple hidden onChange={e => { if (e.target.files) send(fromInput(e.target.files)); e.target.value = '' }} />
          {/* @ts-expect-error webkitdirectory: ganzen Ordner samt Unterordnern hochladen */}
          <input ref={uploadDir} type="file" multiple hidden webkitdirectory="" onChange={e => { if (e.target.files) send(fromInput(e.target.files)); e.target.value = '' }} />
          {progress && (
            <div className={s.progress}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                <Text size={200} style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
                  {t('Lade hoch: {done} von {total} · {pct} %', { done: progress.done, total: progress.total, pct: progress.totalBytes ? Math.round(progress.bytes / progress.totalBytes * 100) : 0 })}
                  {progress.name && <> · {progress.name}</>}
                  {progress.failed > 0 && <> · {t('{n} fehlgeschlagen', { n: progress.failed })}</>}
                </Text>
                <Button size="small" onClick={cancelUpload}>{t('Abbrechen')}</Button>
              </div>
              <ProgressBar value={progress.totalBytes ? progress.bytes / progress.totalBytes : progress.done / progress.total} thickness="large" />
            </div>
          )}
          <div className={s.head}>
            <div className={s.crumbs}>
              {searching ? <span className={s.viewTitle}>{t('Suchergebnisse für „{q}“', { q: term })}</span>
                : view.kind !== 'folder' ? <span className={s.viewTitle}>{viewTitle(view.kind)}</span> : (
                  <Breadcrumb>
                    <BreadcrumbItem><BreadcrumbButton icon={<Home20Regular />} onClick={() => openFolder('')} current={!parts.length}
                      onDragOver={(e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG)) e.preventDefault() }} onDrop={(e: React.DragEvent) => dropOn(e, '')}>{t('Meine Dateien')}</BreadcrumbButton></BreadcrumbItem>
                    {parts.map((p, i) => {
                      const target = parts.slice(0, i + 1).join('/')
                      return [
                        <BreadcrumbDivider key={'d' + i} />,
                        <BreadcrumbItem key={i}><BreadcrumbButton onClick={() => openFolder(target)} current={i === parts.length - 1}
                          onDragOver={(e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG)) e.preventDefault() }} onDrop={(e: React.DragEvent) => dropOn(e, target)}>{p}</BreadcrumbButton></BreadcrumbItem>,
                      ]
                    })}
                  </Breadcrumb>
                )}
            </div>
            {inFiles && !searching && readOnly && inWithMe(path ?? '', anyShared) && path !== WITH_ME && <Text size={200} className={s.muted}>{t('Nur lesen')}</Text>}
            {inFiles && <>
              {photosView ? (() => {
                const counts = new Map<string, number>()
                for (const i of data?.items ?? []) counts.set(extOf(i.name), (counts.get(extOf(i.name)) ?? 0) + 1)
                const exts = [...counts.entries()].sort((a, b) => b[1] - a[1])
                if (exts.length < 2 && !photoExt) return null
                return (
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      <Button appearance={photoExt ? 'primary' : 'subtle'} size="small" icon={<Filter20Regular />} iconPosition="before" aria-label={t('Nach Format filtern')}>
                        {mobile ? undefined : photoExt ? photoExt.slice(1).toUpperCase() : t('Alle Formate')}{!mobile && <ChevronDown20Regular style={{ width: 14 }} />}
                      </Button>
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList checkedValues={{ ext: [photoExt || 'all'] }} onCheckedValueChange={(_, d) => setPhotoExt(d.checkedItems[0] === 'all' ? '' : d.checkedItems[0])}>
                        <MenuItemRadio name="ext" value="all">{t('Alle Formate')}</MenuItemRadio>
                        {exts.map(([e, n]) => <MenuItemRadio key={e} name="ext" value={e}>{(e ? e.slice(1).toUpperCase() : t('ohne Endung')) + ' (' + n + ')'}</MenuItemRadio>)}
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                )
              })() : <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button appearance={filter ? 'primary' : 'subtle'} size="small" icon={<Filter20Regular />} iconPosition="before"
                    aria-label={t('Nach Art filtern')}>{mobile ? undefined : filter ? kindLabel(filter) : t('Alle Arten')}{!mobile && <ChevronDown20Regular style={{ width: 14 }} />}</Button>
                </MenuTrigger>
                <MenuPopover>
                  <MenuList checkedValues={{ kind: [filter || 'all'] }} onCheckedValueChange={(_, d) => setFilter(d.checkedItems[0] === 'all' ? '' : d.checkedItems[0] as Kind)}>
                    <MenuItemRadio name="kind" value="all">{t('Alle Arten')}</MenuItemRadio>
                    {FILTERS.map(k => <MenuItemRadio key={k} name="kind" value={k}>{kindLabel(k)}</MenuItemRadio>)}
                  </MenuList>
                </MenuPopover>
              </Menu>}
              <Input className={s.search} size="small" value={search} onChange={(_, d) => setSearch(d.value)} placeholder={path ? t('In „{name}“ oder überall suchen', { name: nameOf(path) }) : t('Dateien suchen')}
                contentBefore={<Search20Regular />} aria-label={t('Dateien suchen')}
                onKeyDown={e => { if (e.key === 'Escape') { setSearch(''); mainRef.current?.focus() } }}
                contentAfter={search ? <Button size="small" appearance="transparent" icon={<Dismiss20Regular />} onClick={() => setSearch('')} aria-label={t('Suche löschen')} /> : undefined} />
              {searching && path && (
                <Menu>
                  <MenuTrigger disableButtonEnhancement><Button size="small" appearance="subtle">{scope === 'here' ? t('Nur hier') : t('Überall')}</Button></MenuTrigger>
                  <MenuPopover>
                    <MenuList checkedValues={{ scope: [scope] }} onCheckedValueChange={(_, d) => setScope(d.checkedItems[0] as any)}>
                      <MenuItemRadio name="scope" value="here">{t('In „{name}“ und darunter', { name: nameOf(path) || t('Meine Dateien') })}</MenuItemRadio>
                      <MenuItemRadio name="scope" value="all">{t('Überall')}</MenuItemRadio>
                    </MenuList>
                  </MenuPopover>
                </Menu>
              )}
            </>}
          </div>
          <div ref={listRef} className={s.table} onClick={e => { if (e.target === e.currentTarget) setSelected([]) }}
            onContextMenu={e => {
              // Freie Flaeche (auch die Leer-Meldung): Neuer Ordner, Hochladen, Einfuegen
              if (path === null || (e.target as HTMLElement).closest('[data-path]')) return
              e.preventDefault(); setSelected([]); setCtx({ x: e.clientX, y: e.clientY, kind: 'empty' })
            }}>
            {list()}
          </div>
          {undoBar && (
            <div className={s.undo} role="status">
              <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{undoBar.label}</span>
              <Button appearance="transparent" className={s.undoBtn} icon={<ArrowCounterclockwise20Regular />} onClick={undo}>{t('Rückgängig')}</Button>
              <Button appearance="transparent" className={s.undoBtn} icon={<Dismiss20Regular />} aria-label={t('Schließen')} onClick={() => setUndoBar(null)} />
            </div>
          )}
          {/* Rechtsklick-Menue an der Mausposition */}
          {ctx && (ctx.kind !== 'items' || sel.length > 0) && (
            <Menu open onOpenChange={(_, d) => !d.open && setCtx(null)} positioning={{ target: { getBoundingClientRect: () => ({ x: ctx.x, y: ctx.y, top: ctx.y, left: ctx.x, bottom: ctx.y, right: ctx.x, width: 0, height: 0 }) } as any }}>
              <MenuPopover onClick={() => setCtx(null)}>
                {ctx.kind === 'items' ? menuItems(sel) : ctx.kind === 'empty' ? emptyMenu : folderMenu(ctx.folder ?? '')}
              </MenuPopover>
            </Menu>
          )}
        </div>
        {detailsOpen && inFiles && (
          <Details item={detailItem} selection={sel} version={detailsVersion} onClose={() => setDetails(false)}
            onOpen={open} onOpenFolder={p => { openFolder(p); if (mobile) setDetails(false) }} onShare={setShare}
            onFavorite={favorite} onDownload={download} fail={fail} notify={m => notify(m)} onChanged={changed} />
        )}
      </div>
      {picker && (
        <FolderPicker title={picker.mode === 'move' ? t('{n} verschieben nach …', { n: picker.paths.length }) : t('{n} kopieren nach …', { n: picker.paths.length })}
          exclude={picker.mode === 'move' ? picker.paths : []}
          onClose={() => setPicker(null)}
          onPick={target => { const p = picker; setPicker(null); transfer(p.paths, target, p.mode === 'copy') }} />
      )}
      {viewer && (
        <ViewerWithEdit viewer={viewer} startEdit={startEdit} onIndex={i => setViewer(v => v && { ...v, index: i })}
          onClose={() => { setViewer(null); setStartEdit(false); mainRef.current?.focus() }}
          onDownload={i => download([i])} onDetails={i => { setViewer(null); setSelected([i.path]); setDetails(true) }} onSave={saveText} />
      )}
      {editing && (
        <OfficeEditor key={editing.path} name={editing.name} onClose={closeEditor}
          open={() => api.post<OfficeSession>('/api/office/open', { path: editing.path, view: editing.view || undefined })} />
      )}
      {share && <ShareDialog item={share} onClose={() => { setShare(null); changed() }} notify={m => notify(m)} fail={fail} />}
      {naming && <NameDialog title={naming.title} initial={naming.initial} label={naming.label} onDone={naming.done} onClose={() => setNaming(null)} />}
      {conflict.dialog}
    </div>
  )
}

function ViewerWithEdit({ viewer, startEdit, ...rest }: {
  viewer: { list: Item[]; index: number }; startEdit: boolean; onIndex: (i: number) => void; onClose: () => void
  onDownload: (i: Item) => void; onDetails: (i: Item) => void; onSave: (i: Item, text: string, force: boolean) => Promise<Item>
}) {
  return <Viewer items={viewer.list} index={viewer.index} source={ownSource} startEdit={startEdit} {...rest} />
}
