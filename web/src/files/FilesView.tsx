import { importedText } from '../dms/model'
import { closeTresor, CMV, cmvRoots, isCmv, isEncrypted, isTresor, OPEN_COLOR, requestTresorOpen, TRESOR, TRESOR_EVENT, useTresor } from '../tresor/tresor'
import { Cart20Regular, CloudOff16Regular, CloudOff20Regular, LockClosed20Regular, LockOpen20Regular } from '@fluentui/react-icons'
import {
  Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent,
  DialogSurface, DialogTitle, Input, Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, MenuItemRadio, MenuItemCheckbox,
  Spinner, Table, TableBody, TableCell, TableCellLayout, TableHeader, TableHeaderCell, TableRow, Text, Tooltip,
  makeStyles, mergeClasses, tokens, ProgressBar, Slider, MessageBar, MessageBarBody, Portal } from '@fluentui/react-components'
import {
  ArrowUpload20Regular, FolderAdd20Regular, Delete20Regular, ArrowDownload20Regular, Rename20Regular, MoreHorizontal20Regular, Home20Regular,
  ChevronRight16Regular, ChevronDown16Regular, FolderOpen20Regular, Folder20Regular, ArrowSort20Regular, PanelLeft20Regular, ArrowLeft20Regular, ArrowRight20Regular,
  FolderArrowRight20Regular, Copy20Regular, FolderArrowUp20Regular, AppsList20Regular, Grid20Regular, Clock20Regular, Star20Regular, Star20Filled,
  Chat20Regular,   Share20Regular, Link20Regular, Info20Regular, Cut20Regular, ClipboardPaste20Regular, ArrowUndo20Regular, Search20Regular,
  Dismiss20Regular, DocumentAdd20Regular, ArrowCounterclockwise20Regular, DeleteDismiss20Regular, ArrowReset20Regular, ArrowSync20Regular,
  Filter20Regular, LinkSquare20Regular, DocumentEdit20Regular, Star16Filled, Link16Regular, ChevronDown20Regular, DocumentSearch20Regular, GroupList20Regular, ZoomIn20Regular, Eye20Regular, EyeOff20Regular, People16Regular, People20Regular,
  ImageOff20Regular, ArrowImport20Regular, Globe20Regular, HardDrive20Regular, Cloud20Regular, Add16Regular, ImageMultiple20Regular,
  Folder20Filled,
  ColumnTriple20Regular, FullScreenMaximize20Regular, FullScreenMinimize20Regular, TextBulletListSquare20Regular, Stack20Regular, Sparkle20Regular, ScaleFit20Regular, ScaleFill20Regular, DocumentBulletList20Regular, Wand20Regular, Edit20Regular, Edit16Regular, Prohibited16Filled, } from '@fluentui/react-icons'
import { ExcludeFolderDialog, PhotoExcludesDialog } from './PhotoExcludes'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { SidePane, useBackClose, useHistoryClose, useIsMobile } from '../shell/mobile'
import { usePrefs } from '../prefs'
import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, Fragment } from 'react'
import { ApiError, api, featureOn, fmtSize, q } from '../api'
import { dropUpload, finishToFiles, uploadChunked, UploadError } from './chunked'
import { useApp } from '../App'
import { useDebounced } from '../shell/hooks'
import { STORAGE_CHANGED, storageLimit, type Storage } from '../shell/StorageInfo'
import { lang, locale, t } from '../i18n'
import {
  FILTERS, Item, Kind, KindIcon, Thumb, WITH_ME, extOf, fmtWhen, hasThumb, inWithMe, inside, join, kindLabel, kindOf, nameOf, ownSource, parentOf, viewKind, stackPhotos, unstack,
} from './common'
import { BindExternalDialog, ExternalMount, ExternalMountDialog, ExternalView } from './ExternalView'
import { ExtPickerTree, extTarget, parseExtTarget } from './ExtPickerTree'
import { useFileClip } from './clip'
import { useTransfer } from './TransferDialog'
import { ArchiveView, isArchive } from './ArchiveView'
import { ExternalHits } from './ExternalSearch'
import { COLS, ColHead, ColKey, ColPref, ColumnMenu, META_COLS, SortKey, cleanCols } from './FileColumns'
import { ATTRS, cleanFilterCols, type FilterAttr, dimsOf, exposureLine, filterPhotos, fmtDuration, labelColor, MARK_LABELS, Meta, PhotoFilterBar, PhotoFilterTop, PhotoQuickBar, PhotoSetBar, quickRule, takenOf, TILE_INFOS } from './PhotoFilter'
import { ImportProfilesDialog } from './ImportProfiles'
import { GalleryFoldersDialog, gallerySource, galleryTileSource, loadSourceNames, isExt, extWritable, extId, extOffline, extPlace, takePhotoFromElsewhere } from './GalleryFolders'
import { Timeline } from './Timeline'
import { SmartAlbumDialog, SmartRule } from './SmartAlbumDialog'
import { galleryAll, galleryLocate, galleryMonth, loadGeo, searchGallery, useFacets, useGalleryTimeline } from './GalleryTimeline'
import { Fresh, freshenPhoto, freshenTiles } from './GalleryFreshen'
import { AlbumInfo, AlbumNameDialog, AlbumPicker, AlbumShareDialog, albumKey, albumsApi, isAlb, loadAlbum, AlbumLinksDialog } from './Albums'
import { Viewer } from './Viewer'
import { Details } from './Details'
import { sendToChat } from '../chat/SendToChat'
import { Choice, NameDialog, ShareDialog, ShareManyDialog, copyText, linkUrl, useConflict, Link } from './Dialogs'
import { LinksList, TrashList, useLinks, useTrash } from './Lists'
import { ContentSearch } from './ContentSearch'
import { Snippet } from '../shell/Snippet'
import { OfficeEditor, OfficeSession, officeMode, useOffice } from './Office'
import { usePaneWidth } from '../shell/Splitter'

import { PhotoAutoDialog } from './PhotoAutoDialog'
import { PhotoInfoDialog } from './PhotoInfoDialog'
import { RAW } from './PhotoEditor'
import { askConfirm } from '../shell/context'
import { basket, BasketButton, TresorDownloadButton } from './Basket'
/** Fotos, die sich verbessern lassen: JPG und RAW */
const isPhoto = (i: Item) => !i.isDir && (/\.jpe?g$/i.test(i.name) || RAW.test(i.name))

const useStyles = makeStyles({
  // Ordnerkachel mit Bildern: 2x2, ein Bild fuellt die Kachel, zwei nebeneinander, drei: eins gross links
  collageMark: { position: 'absolute', left: '4px', bottom: '4px', color: '#E8A33D', filter: 'drop-shadow(0 0 2px rgba(0,0,0,.6))' },
  collage: { width: '100%', height: '100%', display: 'grid', gridTemplateColumns: '1fr 1fr', gridAutoRows: '1fr', gap: '2px', borderRadius: '6px', overflow: 'hidden',
    '& > :only-child': { gridColumn: '1 / 3', gridRow: '1 / 3' }, '& > :first-child:nth-last-child(2), & > :first-child:nth-last-child(2) ~ *': { gridRow: '1 / 3' },
    '& > :first-child:nth-last-child(3)': { gridRow: '1 / 3' } },
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
  table: { flex: 1, overflowY: 'auto', overflowX: 'auto', padding: '0 16px 16px', '@media (max-width: 767px)': { padding: '0 4px 72px' } },
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
  tileOverlay: {
    // Dunkle Leiste mit weißer Schrift, nur so hoch wie die Angaben (ein langer Verlauf ließ die Bilder dunkel wirken)
    position: 'absolute', left: 0, right: 0, bottom: 0, padding: '10px 8px 4px', display: 'flex', flexDirection: 'column', gap: '1px',
    // Stark durchscheinend; ein dunkler Schein um die Schrift hält sie auch auf hellen Stellen lesbar
    fontSize: '12px', lineHeight: '15px', color: '#fff', pointerEvents: 'none', textShadow: '0 0 3px rgba(0,0,0,0.9), 0 0 1px rgba(0,0,0,0.9)',
    background: 'linear-gradient(to bottom, rgba(0,0,0,0) 0, rgba(0,0,0,0.25) 8px, rgba(0,0,0,0.35) 100%)',
  },
  // Galerie: Markierung, Farbe, Sterne auf der Kachel (wie Lightroom); abgelehnte blass
  marks: { position: 'absolute', right: '4px', top: '4px', display: 'flex', alignItems: 'center', gap: '4px', padding: '1px 5px', borderRadius: '8px',
    backgroundColor: 'rgba(0,0,0,0.55)', color: '#fff', fontSize: '11px', lineHeight: '16px', pointerEvents: 'none' },
  markDot: { width: '9px', height: '9px', borderRadius: '50%', display: 'inline-block' },
  rejected: { opacity: 0.4 },
  thumb: { position: 'relative', width: '100%', aspectRatio: '4 / 3', display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden', borderRadius: tokens.borderRadiusSmall },
  thumbImg: { width: '100%', height: '100%' },
  tileName: { fontSize: '12px', textAlign: 'center', wordBreak: 'break-word', lineHeight: '16px', maxHeight: '32px', overflow: 'hidden', width: '100%' },
  tileSnippet: {
    fontSize: '11px', lineHeight: '14px', color: tokens.colorNeutralForeground3, overflow: 'hidden', display: '-webkit-box',
    WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflowWrap: 'anywhere', textAlign: 'center', padding: '0 4px',
  },
  // Galerie: je Angabe eine Zeile, zu lang wird gekürzt
  // erste Zeile rückt neben den Auswahlkasten (der oben links auf der Kachel sitzt)
  tileInfos: { width: '100%', display: 'flex', flexDirection: 'column', fontSize: '11px', lineHeight: '14px', color: tokens.colorNeutralForeground3, marginTop: '-2px', padding: '0 1px', boxSizing: 'border-box' },
  // Galerie: Kachel enger, das Bild fuellt sie
  tilePhoto: { padding: '4px', gap: '4px', ':hover [data-tilecheck]': { opacity: 1 } },
  // Galerie: Auswahlkasten erst beim Überfahren (oder wenn schon etwas markiert ist) - sonst verdeckt er Bild und Angaben
  tileCheckQuiet: { opacity: 0, transitionProperty: 'opacity', transitionDuration: tokens.durationFaster, ':focus-within': { opacity: 1 } },
  tileInfo: { display: 'flex', gap: '6px', minWidth: 0 },
  tileInfoL: { flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  tileInfoR: { flexShrink: 1, minWidth: 0, maxWidth: '50%', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', textAlign: 'right' },
  tileCheck: { position: 'absolute', top: '2px', left: '2px', zIndex: 1 },
  tileBadge: { position: 'absolute', top: '6px', right: '6px', display: 'flex', gap: '2px', color: tokens.colorNeutralForeground2 },
  editedBadge: { color: '#fff', background: 'rgba(0,0,0,.55)', borderRadius: '50%', padding: '2px' },
  // Foto aus einem Galerie-Ordner, dessen externer Ordner gerade nicht erreichbar ist: rotes Stoppschild unten links
  offBadge: { position: 'absolute', left: '6px', bottom: '6px', zIndex: 2, display: 'flex', color: '#E5484D', background: '#fff', borderRadius: '50%' },
  // Stapel JPG+RAW: Kennzeichen unten rechts auf dem Bild
  // Oben rechts im Bild: unten liegen Uhrzeit und Name (Angaben auf dem Bild)
  stackBadge: { position: 'absolute', right: '6px', top: '6px', zIndex: 1, fontSize: '10px', fontWeight: 700, padding: '1px 5px', borderRadius: '4px', backgroundColor: 'rgba(0,0,0,0.6)', color: '#fff', pointerEvents: 'none' },
  stackBadgeLow: { top: '28px' },
  groupCheck: { marginLeft: 'auto', cursor: 'pointer' },
  picker: { maxHeight: '50vh', overflowY: 'auto', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, padding: '4px' },
  undo: {
    position: 'absolute', left: '50%', bottom: '16px', transform: 'translateX(-50%)', zIndex: 10, display: 'flex', alignItems: 'center', gap: '12px',
    padding: '8px 8px 8px 16px', borderRadius: tokens.borderRadiusLarge, boxShadow: tokens.shadow16, maxWidth: 'calc(100% - 32px)',
    backgroundColor: tokens.colorNeutralBackgroundInverted, color: tokens.colorNeutralForegroundInverted,
  },
  // Über der Foto-Ansicht (dort liegt das Foto bildschirmfüllend): eigene Ebene, oberhalb der Blätter-Leiste
  undoOverViewer: { position: 'fixed', bottom: '72px', zIndex: 1001 },
  undoBtn: { color: tokens.colorBrandForegroundInverted },
  empty: { color: tokens.colorNeutralForeground3, padding: '24px 4px', display: 'block' },
})

const DRAG = 'application/x-dmoffice-files'

type Special = 'recent' | 'favorites' | 'photos' | 'shared' | 'trash'
type View = { kind: 'folder'; path: string } | { kind: Special }

/** #/files/a/b = Ordner; #/files/:trash usw. = besondere Ansichten (":" steht in echten Namen kodiert als %3A). */
function parseHash(): View {
  const rest = location.hash.replace(/^#\/?files\/?/, '')
  // #/files/:ext/... - externer Ordner: eigene Ansicht, darunter „Meine Dateien“
  if (parseExt()) return { kind: 'folder', path: '' }
  // #/files/:office/a/b.docx - Dokument im Editor (Link aus der Handy-App): darunter liegt sein Ordner
  const doc = officeHash()
  if (doc !== null) return { kind: 'folder', path: parentOf(doc) }
  // #/files/:arc/... - geöffnetes Archiv: darunter liegt sein Ordner
  const arc = parseArc()
  if (arc) return { kind: 'folder', path: parentOf(arc.file) }
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
/** #/files/:ext/<Kennung>/<Pfad> = ein externer Ordner (Ordner auf dem Server, WebDAV). */
function parseExt(): { mount: string; path: string } | null {
  const m = location.hash.match(/^#\/?files\/:ext\/([^/]+)\/?(.*)$/)
  if (!m) return null
  try { return { mount: decodeURIComponent(m[1]), path: m[2].split('/').filter(Boolean).map(decodeURIComponent).join('/') } } catch { return null }
}
/** #/files/:arc/<Pfad des Archivs, als ein Teil kodiert>/<Ordner im Archiv> = ein Archiv wie einen Ordner öffnen. */
function parseArc(): { file: string; dir: string } | null {
  const m = location.hash.match(/^#\/?files\/:arc\/([^/]+)\/?(.*)$/)
  if (!m) return null
  try { return { file: decodeURIComponent(m[1]), dir: m[2].split('/').filter(Boolean).map(decodeURIComponent).join('/') } } catch { return null }
}
const arcHash = (a: { file: string; dir: string }) => '#/files/:arc/' + encodeURIComponent(a.file) + (a.dir ? '/' + a.dir.split('/').map(encodeURIComponent).join('/') : '')
const extHash = (e: { mount: string; path: string }) => '#/files/:ext/' + encodeURIComponent(e.mount) + (e.path ? '/' + e.path.split('/').map(encodeURIComponent).join('/') : '')
const hashOf = (v: View) => '#/files/' + (v.kind === 'folder' ? v.path.split('/').map(encodeURIComponent).join('/')
  : ':' + v.kind)

const TILE_STEP = 300
/** Ein Stück der fensterweise gezeichneten Galerie: Abschnittskopf, Kacheln oder (Zeitleiste) ein noch nicht geladener Monat. */
interface Block { key: string; label: string; head: boolean; items: Item[]; start: number; off: number; h: number; count: number; bucket?: string; ph?: boolean }
// Karte erst laden, wenn man den Atlas öffnet (Leaflet)
const GalleryAtlas = lazy(() => import('./GalleryAtlas'))

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

const viewTitle = (k: Special) => ({ recent: t('Zuletzt verwendet'), favorites: t('Favoriten'), photos: t('Galerie'), shared: t('Von mir geteilt'), trash: t('Papierkorb') })[k]

const listDirs = (path: string) => api.get<{ items: Item[] }>('/api/files/?' + q({ path })).then(d => d.items.filter(i => i.isDir))

/** Ordnerbaum, Unterordner werden beim Aufklappen geladen. Ordner sind Ablageziele fuer Ziehen und Ablegen. */
/** Ordner für Dateien aus dem Chat (fest, siehe ChatApi.UploadFolder): unten bei den festen Einträgen statt im Baum. */
const CHAT_DIR = 'Chat'

/** Versteckt ist, was mit einem Punkt beginnt (.trash, .obsidian ...); bei Listen aus mehreren Ordnern zählt jeder Teil des Pfads */
const isHidden = (i: { name: string; path: string }, whole: boolean) => (whole ? i.path.split('/') : [i.name]).some(x => x.startsWith('.'))

function FolderTree({ current, onOpen, onDropOn, onContext, version, exclude, listed, showHidden, root = '', rootLabel, rootIcon }: {
  /** Wurzel des Baums: "" = Meine Dateien, TRESOR = der Tresor (Ziel beim Verschieben und Kopieren) */
  root?: string; rootLabel?: string; rootIcon?: JSX.Element
  current: string | null; onOpen: (path: string) => void; onDropOn?: (e: React.DragEvent, path: string) => void
  showHidden?: boolean; onContext?: (e: React.MouseEvent, path: string) => void; version: number; exclude?: string[]
  /** Gerade geladene Liste eines Ordners - der Baum uebernimmt deren Unterordner (geloescht/neu auf anderem Geraet) */
  listed?: { path: string; items: Item[] } | null
}) {
  const s = useStyles()
  const [kids, setKids] = useState<Record<string, Item[]>>({})
  const [open, setOpen] = useState<Record<string, boolean>>({ [root]: true })
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
    if (current === null || (root && current !== root && !current.startsWith(root + '/'))) return
    const rest = current === root ? '' : root ? current.slice(root.length + 1) : current
    const parts = rest ? rest.split('/') : []
    const chain = [root].concat(parts.map((_, i) => join(root, parts.slice(0, i + 1).join('/'))))
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
  const render = (p: string, depth: number): JSX.Element[] => (kids[p] ?? []).filter(f => (showHidden || !isHidden(f, false)) && !exclude?.some(x => inside(x, f.path))).flatMap(f => [
    <div key={f.path} className={mergeClasses(s.nav, current === f.path && s.active, over === f.path && s.target)}
      style={{ paddingLeft: 8 + depth * 14 }} onClick={() => onOpen(f.path)} {...dropProps(f.path)}
      onContextMenu={onContext ? e => { e.preventDefault(); e.stopPropagation(); onContext(e, f.path) } : undefined}>
      <span className={s.twisty} onClick={e => { e.stopPropagation(); toggle(f.path) }}>
        {kids[f.path] && kids[f.path].filter(k => showHidden || !isHidden(k, false)).length === 0 ? null : open[f.path] ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
      </span>
      {current === f.path ? <FolderOpen20Regular /> : <Folder20Regular />}<span className={s.navName}>{f.name}</span>
    </div>,
    ...(open[f.path] ? render(f.path, depth + 1) : []),
  ])
  return (
    <>
      <div className={mergeClasses(s.nav, current === root && s.active, over === root && s.target)} onClick={() => onOpen(root)} {...dropProps(root)}
        onContextMenu={onContext ? e => { e.preventDefault(); e.stopPropagation(); onContext(e, root) } : undefined}>
        <span className={s.twisty} onClick={e => { e.stopPropagation(); toggle(root) }}>{open[root] ? <ChevronDown16Regular /> : <ChevronRight16Regular />}</span>
        {rootIcon ?? <Home20Regular />}{rootLabel ?? t('Meine Dateien')}
      </div>
      {open[root] && render(root, 1)}
    </>
  )
}

/** Datei, die nach dem Öffnen ihres Ordners markiert wird - außerhalb der Komponente, weil die Galerie dafür zum Bereich Dateien wechselt. */
let pendingFocus: string | null = null

/** Zielordner waehlen fuer Verschieben/Kopieren. */
function FolderPicker({ title, exclude, onPick, onClose, externals, start = '' }: {
  title: string; exclude: string[]; onPick: (path: string) => void; onClose: () => void
  /** Externe Ordner als weitere Ziele (Übertragung zwischen Ablage und extern) */
  externals?: ExternalMount[]
  start?: string
}) {
  const s = useStyles()
  const { prefs } = usePrefs()
  const { fail } = useApp()
  const [target, setTarget] = useState(start)
  const [version, setVersion] = useState(0)
  // Der Tresor als Ziel (offen: seine Ordner; geschlossen: erst öffnen)
  const tresor = useTresor()
  // Neuer Ordner im gewaehlten Ziel, ohne den Dialog zu verlassen
  const [naming, setNaming] = useState<string | null>(null)
  const create = async () => {
    const name = naming?.trim()
    if (!name || /[\\/]/.test(name) || name === '.' || name === '..') return
    try {
      // Im externen Ordner: dort anlegen und gleich als Ziel nehmen
      const x = parseExtTarget(target)
      if (x) {
        const p = x.path ? x.path + '/' + name : name
        await api.post('/api/files/external/mkdir', { mount: x.mount, path: p })
        setNaming(null)
        setTarget(extTarget(x.mount, p))
        return
      }
      const r = await api.post<{ path: string }>('/api/files/mkdir', { path: join(target, name) })
      setNaming(null)
      setTarget(r.path)
      setVersion(v => v + 1)
    } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 440 }}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <div className={s.picker}><FolderTree showHidden={prefs.fileHidden} current={target} onOpen={setTarget} version={version} exclude={exclude.length ? exclude : undefined} />
              {tresor?.exists && featureOn('vault') && (tresor.open
                ? <FolderTree root={TRESOR} rootLabel={t('Tresor')} rootIcon={<LockOpen20Regular style={{ color: OPEN_COLOR }} />} current={target} onOpen={setTarget} version={version}
                    exclude={exclude.length ? exclude : undefined} />
                : <div className={s.nav} onClick={() => { requestTresorOpen() }}><span className={s.twisty} /><LockClosed20Regular />{t('Tresor öffnen …')}</div>)}
              {externals && externals.length > 0 && <ExtPickerTree mounts={externals} current={target} onOpen={setTarget} />}</div>
            {naming === null
              ? <Button appearance="subtle" icon={<FolderAdd20Regular />} style={{ alignSelf: 'flex-start' }} onClick={() => setNaming('')}>{t('Neuer Ordner')}</Button>
              : <div style={{ display: 'flex', gap: 8 }}>
                <Input autoFocus style={{ flex: 1 }} value={naming} placeholder={t('Name des Ordners')} onChange={(_, d) => setNaming(d.value)}
                  onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); create() } else if (e.key === 'Escape') { e.stopPropagation(); setNaming(null) } }} />
                <Button appearance="primary" disabled={!naming.trim()} onClick={create}>{t('Anlegen')}</Button>
                <Button onClick={() => setNaming(null)}>{t('Abbrechen')}</Button>
              </div>}
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
export interface Upload { file: File; rel: string }

/** Ordner vom Desktop: die Eintraege muessen noch im drop-Ereignis abgeholt werden, gelesen wird danach. */
export function entriesOf(dt: DataTransfer): any[] {
  return Array.from(dt.items ?? []).filter(i => i.kind === 'file').map(i => (i as any).webkitGetAsEntry?.()).filter(Boolean)
}
export async function readEntries(entries: any[]): Promise<Upload[]> {
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
/** gallery: die Galerie als eigenes Modul (#/gallery) - nur die Fotos und Videos, ohne Ordnerbereich wie in der Handy-App. */
/** Ab dieser Kachelgröße stehen die Bildinfos auf dem Bild (mit Verlauf) statt darunter. */
const OVERLAY_PX = 180

export default function FilesView({ gallery = false }: { gallery?: boolean }) {
  const s = useStyles()
  const detailsW = usePaneWidth('files.details', 320, 240, 720)
  const { fail, notify, me } = useApp()
  // Zurück in Dateien ohne Ordner in der Adresse (Klick auf den Bereich): wieder dort, wo man zuletzt war
  const [view, setView] = useState<View>(() => {
    if (gallery) return { kind: 'photos' }
    if (/^#\/?files\/?$/.test(location.hash)) try {
      const last = sessionStorage.getItem('dm.files.last')
      if (last && last !== location.hash) { history.replaceState(null, '', last); return parseHash() }
    } catch { /* ohne Speicher */ }
    return parseHash()
  })
  // Externe Ordner (Version 2): Ordner auf dem Server und WebDAV - eigene Ansicht statt der Liste
  const [ext, setExt] = useState<{ mount: string; path: string } | null>(parseExt)
  // Geöffnetes Archiv (ZIP, 7z, RAR, TAR): eigene Ansicht statt der Liste
  const [arc, setArc] = useState<{ file: string; dir: string } | null>(gallery ? null : parseArc)
  // Offene Änderungen im geöffneten Archiv - beim Verlassen nachfragen; dazu liegengebliebene in anderen Archiven
  const arcPending = useRef<{ file: string; n: number }>({ file: '', n: 0 })
  const [arcLeave, setArcLeave] = useState<string | null>(null)
  const [arcOpen, setArcOpen] = useState<{ path: string; expires: string }[]>([])
  const loadArcOpen = useCallback(() => {
    if (gallery) return
    api.get<{ items: { path: string; expires: string }[] }>('/api/files/archive/pending').then(r => setArcOpen(r.items)).catch(() => setArcOpen([]))
  }, [gallery])
  const prevArc = useRef(arc?.file ?? null)
  useEffect(() => {
    const before = prevArc.current
    prevArc.current = arc?.file ?? null
    if (before && before !== arc?.file && arcPending.current.file === before && arcPending.current.n > 0) setArcLeave(before)
    if (!arc) loadArcOpen()
  }, [arc?.file]) // eslint-disable-line react-hooks/exhaustive-deps
  const arcSave = async (file: string, discard: boolean) => {
    setArcLeave(null)
    try {
      if (discard) await api.post('/api/files/archive/discard', { path: file })
      else {
        await api.post('/api/files/archive/save', { path: file, copy: false })
        notify(t('Archiv gespeichert'), t('Der vorige Stand liegt unter Versionen.'))
      }
      arcPending.current = { file: '', n: 0 }
    } catch (e) {
      // Passwort vergessen oder inzwischen geändert: im Archiv selbst klären
      fail(e)
      setArc({ file, dir: '' })
      return
    }
    loadArcOpen(); load()
  }
  const [extMounts, setExtMounts] = useState<ExternalMount[]>([])
  const [extCanAdd, setExtCanAdd] = useState(false)
  const [addExt, setAddExt] = useState(false)
  // Welche eigenen Ordner sich einbinden lassen (schaltet der Admin frei)
  const [extTargets, setExtTargets] = useState<ExternalMount[]>([])
  // Eigener WebDAV-Ordner: Auswahl der Ordner (gleich nach dem Einbinden bzw. per Rechtsklick)
  const [extSetup, setExtSetup] = useState<{ mount: ExternalMount; added?: boolean } | null>(null)
  useEffect(() => {
    // Mit Erreichbarkeit - alle 60 s neu, damit eine wieder eingehängte Freigabe von selbst zurückkommt
    const load = () => api.get<{ mounts: ExternalMount[]; targets: ExternalMount[]; canAdd: boolean }>('/api/files/external/mounts').then(r => { setExtMounts(r.mounts); setExtTargets(r.targets ?? []); setExtCanAdd(r.canAdd || (r.targets ?? []).length > 0) }).catch(() => {})
    load()
    const id = setInterval(load, 60_000)
    // Unter Einstellungen → Externe Ziele geändert: gleich neu
    window.addEventListener('dmoffice:external-changed', load)
    return () => { clearInterval(id); window.removeEventListener('dmoffice:external-changed', load) }
  }, [])
  const extMount = ext ? extMounts.find(m => m.id === ext.mount) : undefined
  const path = view.kind === 'folder' ? view.path : null
  const [data, setData] = useState<{ items: (Item & { used?: string })[]; folder?: Item } | null>(null)
  const [usage, setUsage] = useState<{ usage: number; trash: number; versions: number; quota: number; used?: number } | null>(null)
  // Belegt/verfügbar wie in der Kopfzeile (Kontingent, höchstens der freie Platz des Servers)
  const [storage, setStorage] = useState<Storage | null>(null)
  useEffect(() => {
    const load = () => { api.get<Storage>('/api/settings/storage').then(setStorage).catch(() => {}) }
    load()
    window.addEventListener(STORAGE_CHANGED, load)
    return () => window.removeEventListener(STORAGE_CHANGED, load)
  }, [usage])
  const [busy, setBusy] = useState(false)
  // Galerie im Vollbild: ohne Menüband
  const [full, setFull] = useState(false)
  useEffect(() => {
    if (!full) return
    const esc = (e: KeyboardEvent) => { if (e.key === 'Escape' && !document.querySelector('[role=dialog]') && (gallery ? /^#\/?gallery/ : /^#\/?files/).test(location.hash)) setFull(false) }
    window.addEventListener('keydown', esc)
    return () => window.removeEventListener('keydown', esc)
  }, [full])
  const [dragging, setDragging] = useState(false)
  const [overRow, setOverRow] = useState<string | null>(null)
  const upload = useRef<HTMLInputElement>(null)
  const uploadDir = useRef<HTMLInputElement>(null)
  const mainRef = useRef<HTMLDivElement>(null)
  // Gewählte Einträge bleiben beim Wechsel in einen anderen Bereich und zurück (je Ordner, in diesem Fenster)
  const [selected, setSelected] = useState<string[]>(() => { try { return JSON.parse(sessionStorage.getItem('dm.files.sel') ?? '[]') } catch { return [] } })
  useEffect(() => { try { sessionStorage.setItem('dm.files.sel', JSON.stringify(selected.slice(0, 200))) } catch { /* ohne Speicher */ } }, [selected])
  const anchor = useRef<string | null>(null)
  const [picker, setPicker] = useState<{ mode: 'move' | 'copy'; paths: string[] } | null>(null)
  const [treeVersion, setTreeVersion] = useState(0)
  const [listed, setListed] = useState<{ path: string; items: Item[] } | null>(null)
  const [detailsVersion, setDetailsVersion] = useState(0)
  const { prefs, update } = usePrefs()
  // Galerie: Angaben unter den Bildern (höchstens drei; Vorgabe der Dateiname)
  const tileInfo = (prefs.photoTileInfo ?? ['name']).filter(k => k in TILE_INFOS).slice(0, 3)
  // Galerie als Karte (Atlas) statt Kacheln
  const [atlas, setAtlas] = useState(false)
  const [geo, setGeo] = useState<{ items: Item[]; meta: Map<string, Meta> } | null>(null)
  const photosView = view.kind === 'photos'
  const photoDesc = prefs.photoSortDesc ?? true
  const sort = photosView ? prefs.photoSort ?? 'taken' : prefs.fileSort ?? 'name'
  const desc = photosView ? prefs.photoSortDesc ?? true : prefs.fileSortDesc ?? false
  const viewMode = view.kind === 'photos' ? 'tiles' : prefs.fileView ?? 'details'
  const [sidePane, toggleSide, closeSide] = useSidePane('files')
  const mobile = useIsMobile()
  // Spalten nach der Breite der Liste (nicht des Fensters): zuerst entfaellt "Art", dann Datum und Groesse (stehen dann unter dem Namen)
  const listRef = useRef<HTMLDivElement>(null)
  const [listW, setListW] = useState(1000)
  // Spaltenbreiten waehrend des Ziehens und das Spaltenmenue an der Kopfzeile
  const [live, setLive] = useState<{ cols: ColPref[]; name?: number } | null>(null)
  const [colMenu, setColMenu] = useState<{ x: number; y: number } | null>(null)
  useEffect(() => {
    const el = listRef.current
    if (!el) return
    // Ausgeblendet (die Shell hält die Bereiche) bleibt die Breite: sonst wäre die Liste beim Zurückkommen anders aufgebaut
    const ro = new ResizeObserver(() => { if (el.getClientRects().length) setListW(el.clientWidth) })
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
  // Aufnahmedaten (FileMeta): Galerie alle (Schlüssel Pfad), ein Ordner nur seine (Schlüssel Name) - und die Filterleiste
  const [meta, setMeta] = useState<{ scope: string; byKey: Record<string, Meta>; pending: number; count: number } | null>(null)
  // Galerie-Ordner (externe Speicher): Aufnahmedaten kommen mit der Liste
  const [extMeta, setExtMeta] = useState<Record<string, Meta>>({})
  // Alben der Galerie: Liste, gewähltes Album (null: alle Fotos), wem welches Foto im Album gehört, offene Dialoge
  const [albums, setAlbums] = useState<AlbumInfo[]>([])
  const [album, setAlbum] = useState<number | null>(null)
  const [albumMine, setAlbumMine] = useState<Record<string, boolean>>({})
  const [albumDialog, setAlbumDialog] = useState<{ kind: 'new' | 'rename' | 'smart' | 'share'; add?: string[] } | null>(null)
  const loadAlbums = useCallback(() => { albumsApi.list().then(setAlbums).catch(() => {}) }, [])
  const [galleryFolders, setGalleryFolders] = useState(false)
  // Abschnitt „Externe Ordner“ in der Leiste eingeklappt; bleibt im Browser gemerkt
  const [extFolded, setExtFolded] = useState(() => { try { return localStorage.getItem('files.ext.folded') === '1' } catch { return false } })
  const foldExt = () => setExtFolded(v => { try { localStorage.setItem('files.ext.folded', v ? '0' : '1') } catch { /* egal */ } return !v })
  const [importProfiles, setImportProfiles] = useState(false)
  const [photoSel, setPhotoSel] = useState<string[][]>([[], [], [], []])
  const filterCols = cleanFilterCols(prefs.photoFilterCols)
  // Schnelle Filter-Leiste (Markierung P/X/U, Sterne ab, Farben): bleibt gespeichert, hängt als weitere Spalten an die Filterregel
  const quick = photosView ? quickRule(prefs.photoQuick) : null
  const baseOn = !!prefs.photoFilter && photoSel.some(x => x.length)
  const ruleCols: FilterAttr[] = [...filterCols, ...(quick?.cols ?? [])]
  const ruleSel: string[][] = [...(prefs.photoFilter ? photoSel : filterCols.map(() => [])), ...(quick?.sel ?? [])]
  const filterOn = photosView && (baseOn || !!quick)
  // Angabe im Detailbereich angeklickt (nur bei geschlossener Filterleiste): Der erste Klick öffnet die Leiste, leert alle Filter
  // und setzt Filter 1, jeder weitere Klick den nächsten (Datum: Jahr und Monat, also zwei). Mit dem Schließen der Leiste endet die Folge.
  const [pickNext, setPickNext] = useState<number | null>(null)
  useEffect(() => { if (!prefs.photoFilter) setPickNext(null) }, [prefs.photoFilter])
  const pickFilter = (i: Item, m: Meta | undefined, what: [FilterAttr, string?][]) => {
    const vals = what.map(([a, v]) => [a, v ?? ATTRS[a].values(i, m).find(x => x !== '') ?? ''] as [FilterAttr, string]).filter(([, v]) => v !== '')
    if (!vals.length) return
    const start = !prefs.photoFilter
    if (!start && pickNext == null) return
    let cols = [...filterCols], sel: string[][] = start ? cols.map(() => []) : photoSel.map(x => [...x])
    let k = start ? 0 : pickNext!
    for (const [a, v] of vals) {
      const at = cols.indexOf(a)
      // Schon gesetzt: nur den Wert tauschen; sonst in die nächste Spalte (eine weiter hinten stehende gleiche Spalte tauscht den Platz)
      if (at >= 0 && at < k) { sel[at] = [v]; continue }
      if (k > 3) break
      if (at > k) cols[at] = cols[k]
      cols[k] = a; sel[k] = [v]; k++
    }
    update({ photoFilter: true, photoFilterCols: cols }); setPhotoSel(sel); setPickNext(k)
  }
  // Gemeinsam mit den externen Ordnern (mount null = aus „Meine Dateien“)
  const [clip, setClip] = useFileClip()
  const undos = useRef<Undo[]>([])
  const [undoBar, setUndoBar] = useState<Undo | null>(null)
  // Zuletzt verwendet: Vorgabe ist die Reihenfolge der Verwendung; Klick auf einen Spaltenkopf sortiert nur für diese Ansicht
  const [recentSort, setRecentSort] = useState<{ key: SortKey; desc: boolean } | null>(null)
  const undoTimer = useRef<number>()
  // tl: Betrachter über die Zeitleiste der Galerie - die Liste wächst, wenn er an den Rand der geladenen Monate kommt
  const [viewer, setViewer] = useState<{ list: Item[]; index: number; tl?: boolean } | null>(null)
  const viewerOpen = useRef(false)
  viewerOpen.current = !!viewer
  /** Betrachter kam aus einem anderen Bereich: beim Schließen dorthin zurück. */
  const backOut = useRef(false)
  // Foto, dessen Details aus dem Betrachter geöffnet wurden, und Aufnahmedaten von Fotos, die über ihre Adresse kamen
  const [viewerDetail, setViewerDetail] = useState<Item | null>(null)
  const [linkedMeta, setLinkedMeta] = useState<Record<string, Meta>>({})
  const [share, setShare] = useState<Item | null>(null)
  // Mehrere markiert: mit Personen teilen (ohne Link)
  const [shareMany, setShareMany] = useState<Item[] | null>(null)
  // Ansicht Fotos: Ordner ausschliessen (Bild gewaehlt) und die Liste verwalten
  const [excludeFrom, setExcludeFrom] = useState<string | null>(null)
  const [excludes, setExcludes] = useState(false)
  const [naming, setNaming] = useState<{ title: string; initial: string; label?: string; done: (name: string) => void } | null>(null)
  const [favs, setFavs] = useState<Item[]>([])
  // Favorisierte Ordner links unter „Favoriten“ - auf- und zuklappbar wie die anderen Ordner (gemerkt je Browser, anfangs zu)
  const [favOpen, setFavOpen] = useState(() => { try { return localStorage.getItem('dm.files.favOpen') === '1' } catch { return false } })
  const toggleFavs = () => setFavOpen(o => { try { localStorage.setItem('dm.files.favOpen', o ? '0' : '1') } catch { /* egal */ } return !o })
  // Mit mir geteilte Ordner ("Mit mir geteilt"): Pfad, Besitzer, ob ich dort schreiben darf
  const [withMe, setWithMe] = useState<{ id: number; path: string; name: string; owner: string; canWrite: boolean; showOnly?: boolean; isDir?: boolean }[]>([])
  const anyShared = withMe.length > 0
  /** Darf ich an diesem Pfad schreiben? Eigene Ablage immer; "Mit mir geteilt" selbst nie; sonst nach dem Recht an dem geteilten Ordner. */
  const writableAt = (p: string) => {
    if (!inWithMe(p, anyShared)) return true
    const m = withMe.find(x => p === x.path || p.startsWith(x.path + '/'))
    return !!m?.canWrite
  }
  /** Der geteilte Ordner selbst (erste Ebene in "Mit mir geteilt"): umbenennen, verschieben, loeschen kann nur der Besitzer. */
  const isMountRoot = (p: string) => withMe.some(x => x.path === p)
  const mountOf = (p: string) => inWithMe(p, anyShared) ? withMe.find(x => p === x.path || p.startsWith(x.path + '/')) : undefined
  /** Geteiltes loswerden: der Besitzer behält es, bei mir verschwindet es aus Dateien, Galerie und Übersicht. */
  const leaveShared = async (list: Item[]) => {
    const mounts = [...new Set(list.map(i => mountOf(i.path)).filter(Boolean))] as typeof withMe
    if (!mounts.length) return
    if (!await askConfirm(t('Nicht mehr anzeigen'), mounts.length === 1
      ? t('„{name}“ von {owner} nicht mehr anzeigen? Es bleibt beim Besitzer. Du siehst es erst wieder, wenn es noch einmal mit dir geteilt wird.', { name: mounts[0].name, owner: mounts[0].owner })
      : t('{n} geteilte Einträge nicht mehr anzeigen? Sie bleiben beim Besitzer. Du siehst sie erst wieder, wenn sie noch einmal mit dir geteilt werden.', { n: mounts.length }), t('Nicht mehr anzeigen'))) return
    try {
      for (const m of mounts) await api.post('/api/files/shared/leave', { id: m.id })
      setViewer(null); setSelected([])
      if (path !== null && mounts.some(m => path === m.path || path.startsWith(m.path + '/'))) openFolder(WITH_ME)
      changed(); loadFavs()
    } catch (e) { fail(e) }
  }
  const canChange = (list: Item[]) => list.every(i => !isExt(i) && writableAt(i.path) && !isMountRoot(i.path))
  /** Löschen: eigene Dateien, Fotos aus beschreibbaren Galerie-Ordnern oder beides gemischt. */
  const deletable = (list: Item[]) => list.length > 0 && list.every(i => isExt(i) ? extWritable(i) : canChange([i]))
  /** Nur gezeigt (Version 2): ansehen ja, herunterladen und kopieren nicht. */
  const shownAt = (p: string) => inWithMe(p, anyShared) && !!withMe.find(x => p === x.path || p.startsWith(x.path + '/'))?.showOnly
  const keepable = (list: Item[]) => !list.some(i => shownAt(i.path))
  /** Titel, Beschreibung, Bewertung: eigene Fotos (beschreibbar) und Fotos der Galerie-Ordner (Schreibschutz meldet der Server). */
  const infoable = (list: Item[]) => list.length > 0 && list.every(i => isPhoto(i) && (isExt(i) || canChange([i])))
  const notShown = () => fail(new Error(t('Nur gezeigt – herunterladen und kopieren ist nicht erlaubt.')))
  const conflict = useConflict()
  const office = useOffice()
  // Office-Dokument im Editor (Vollbild); beim Start schon gesetzt, wenn die Adresse darauf zeigt
  const [editing, setEditing] = useState<{ path: string; name: string; view?: boolean } | null>(() => {
    const p = officeHash()
    return p ? { path: p, name: nameOf(p) } : null
  })
  const searching = term.length > 0 && view.kind !== 'trash' && view.kind !== 'shared' && !(view.kind === 'folder' && isEncrypted(view.path))
  // Galerie als Zeitleiste vom Server in Monatsabschnitten (GalleryTimeline): erst nur die Monate, dann die Fotos im Bild.
  // Nicht in Alben (klein) und nicht bei der Suche (Serversuche wie bisher).
  const tlOn = photosView && album == null && !searching
  const tlQuery = { sort, desc, stack: prefs.photoStack ?? true, ext: photoExt, filter: baseOn || quick ? JSON.stringify({ cols: ruleCols, sel: ruleSel }) : null,
    group: prefs.photoGroup === 'source' ? prefs.photoGroup : undefined }
  const tl = useGalleryTimeline(tlOn, tlQuery, fail)
  const facets = useFacets(tlOn && !!prefs.photoFilter, filterCols, photoExt, tlQuery.filter, tl.stamp, fail)
  // Suche in der Galerie: über den Index der Zeitleiste (eigene Fotos und Galerie-Ordner), Bildangaben kommen mit
  const photoSearch = photosView && searching && album == null
  // Eben gesetzte Markierung, Sterne, Farbe: gleich zeigen. Der Stand vom Server kommt beim Neuladen oft noch ohne die Änderung -
  // die Anzeige hinkte sonst einen Tastendruck hinterher. Gilt eine Minute, danach zählt wieder allein der Server.
  const [marked, setMarked] = useState<Record<string, { at: number; pick?: number; rating?: number; label?: string | null }>>({})
  const metaOf = useCallback((i: { path: string; name: string }) => {
    const base = (tlOn ? tl.metaOf(i.path) : photoSearch || isExt(i) ? extMeta[i.path] : meta ? meta.byKey[meta.scope === '*' ? i.path : i.name] : undefined) ?? linkedMeta[i.path]
    const m = marked[i.path]
    if (!base || !m || Date.now() - m.at > 60_000) return base
    const { at: _, ...change } = m
    return { ...base, ...change }
  }, [meta, extMeta, tlOn, tl.metaOf, photoSearch, linkedMeta, marked])
  // Die geladenen Fotos der Zeitleiste sind die Liste der Ansicht (Auswahl, Menüs, Betrachter arbeiten darauf)
  useEffect(() => { if (tlOn && tl.state && data?.items !== tl.items) setData({ items: tl.items }) })

  const go = useCallback((v: View) => { setSearch(''); setView(v); setExt(null); setArc(null) }, [])
  // Tresor (docs/tresor-cryptomator-konzept.md, Stufe 3a): ein Ordner der Dateiansicht mit Pfad „tresor:“ - dieselbe Bedienung wie
  // „Meine Dateien“; der Server bedient „tresor:“-Pfade über dieselben Wege (TresorPaths)
  const tresor = useTresor()
  const inTresor = isTresor(path)
  // Eigene Cryptomator-Tresore (Stufe 3b): Angaben aus der Ordnerliste, gezeigte verschlüsselte Ordner („Verschlüsselte Dateien anzeigen“)
  const [vaultInfo, setVaultInfo] = useState<{ path: string; vault: { unlocked: boolean } | null; root: string | null } | null>(null)
  const [rawVaults, setRawVaults] = useState<string[]>([])
  const lockedVault = path !== null && vaultInfo?.path === path && vaultInfo.vault && !rawVaults.includes(path) ? vaultInfo.vault : null
  // Schon entsperrt: gleich hinein
  useEffect(() => { if (lockedVault?.unlocked && path) go({ kind: 'folder', path: CMV + '/' + path }) }, [lockedVault, path]) // eslint-disable-line react-hooks/exhaustive-deps
  const cmvRoot = isCmv(path) && vaultInfo?.path === path ? vaultInfo.root : null
  const [tresorFolders, setTresorFolders] = useState<{ path: string; name: string }[]>([])
  const loadTresorFolders = () => {
    if (!tresor?.open) { setTresorFolders([]); return }
    api.get<{ items: Item[] }>('/api/files/?' + q({ path: TRESOR }))
      .then(r => setTresorFolders(r.items.filter(i => i.isDir).map(i => ({ path: i.path, name: i.name })))).catch(() => setTresorFolders([]))
  }
  // Neu laden, wenn sich der Tresor ändert, und bei Wechseln im Tresor (neue Ordner). Nicht beim Verlassen: sonst kommt die
  // Anfrage nach dem Schließen an und der Dialog „Tresor öffnen“ erscheint
  useEffect(loadTresorFolders, [tresor]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (isTresor(path)) loadTresorFolders() }, [path]) // eslint-disable-line react-hooks/exhaustive-deps
  const openFolder = useCallback((p: string) => go({ kind: 'folder', path: p }), [go])
  // Zuletzt geöffnete Ordner: Zurück und Vor wie im Explorer (nur die Ordner dieser Ansicht, nicht der ganze Browserverlauf)
  const hist = useRef<{ back: string[]; fwd: string[]; cur: string | null; jumping: boolean }>({ back: [], fwd: [], cur: null, jumping: false })
  const [histTick, setHistTick] = useState(0)
  useEffect(() => {
    const h = hist.current
    if (path == null || path === h.cur) return
    if (!h.jumping && h.cur != null) { h.back.push(h.cur); if (h.back.length > 50) h.back.shift(); h.fwd = [] }
    h.jumping = false; h.cur = path; setHistTick(n => n + 1)
  }, [path])
  const histGo = (d: -1 | 1) => {
    const h = hist.current
    const target = d < 0 ? h.back.pop() : h.fwd.pop()
    if (target == null) return
    if (h.cur != null) (d < 0 ? h.fwd : h.back).push(h.cur)
    h.jumping = true; openFolder(target)
  }
  const openTresor = async (p: string) => {
    // Noch kein Tresor: zur Einrichtung statt einer leeren Ansicht mit Fehler
    if (tresor && !tresor.exists) { location.hash = '#/settings/tresor'; if (mobile) closeSide(); return }
    // Zustand frisch prüfen: nach Neustart oder „Automatisch schließen“ ist der Tresor zu, auch wenn die Seite ihn noch offen kennt
    if (tresor?.exists && !await requestTresorOpen()) return
    openFolder(p)
    if (mobile) closeSide()
  }
  // Ordner öffnen und die Datei darin markieren und zeigen („Ort“ in den Details, auch aus dem Betrachter der Galerie)
  const showFocus = (p: string) => {
    setSelected([p])
    setTimeout(() => mainRef.current?.querySelector(`[data-path="${CSS.escape(p)}"]`)?.scrollIntoView({ block: 'center' }), 80)
  }
  // „Ort“ in den Details: in der Galerie (eigenes Modul) unter Dateien öffnen - die Galerie selbst zeigt keine Ordner und
  // bliebe sonst als Ordneransicht ohne Ordnerliste stehen
  // Galerie-Ordner („ext:…“): den externen Ordner unter Dateien
  const openPlace = (dir: string, focus?: string) => {
    const place = focus && isExt({ path: focus }) ? extPlace(focus) : null
    if (!gallery && !place) { openFolderAt(dir, focus); return }
    // Betrachter zu: die Galerie merkt sich #/gallery, nicht das Foto
    if (gallery && photoHash()) history.replaceState(null, '', '#/gallery')
    if (place) { location.hash = place.href; return }
    pendingFocus = focus ?? null
    location.hash = hashOf({ kind: 'folder', path: dir })
  }
  const openFolderAt = (dir: string, focus?: string) => {
    if (focus && view.kind === 'folder' && view.path === dir && !searching && data) { showFocus(focus); return }
    pendingFocus = focus ?? null
    openFolder(dir)
  }
  useEffect(() => {
    const f = pendingFocus
    if (!f || !data?.items.some(i => i.path === f)) return
    pendingFocus = null
    showFocus(f)
  }, [data]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const on = () => {
      const doc = officeHash()
      if (doc) setEditing({ path: doc, name: nameOf(doc) })
      if (gallery) return
      // Ausgeblendet (anderer Bereich aktiv, Shell hält die Bereiche): nicht reagieren
      if (!/^#\/?files/.test(location.hash)) return
      const a = parseArc()
      setArc(cur => JSON.stringify(cur) === JSON.stringify(a) ? cur : a)
      // Auch der externe Ordner folgt der Adresse (etwa „Ort“ aus der Galerie), sonst schriebe der Effekt unten den alten zurück
      const x = parseExt()
      setExt(cur => JSON.stringify(cur) === JSON.stringify(x) ? cur : x)
      if (a || x) return
      const v = parseHash(); setView(cur => JSON.stringify(cur) === JSON.stringify(v) ? cur : v)
    }
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [])
  // Namen der Galerie-Ordner für die Quelle (Filterleiste, Smart-Alben)
  // Namen und Erreichbarkeit der Galerie-Ordner; alle 60 s neu (srcTick zeichnet die Stoppschilder neu)
  const [, setSrcTick] = useState(0)
  useEffect(() => {
    if (!photosView) return
    const load = () => loadSourceNames().then(() => setSrcTick(n => n + 1))
    load()
    const id = setInterval(load, 60_000)
    return () => clearInterval(id)
  }, [photosView])
  // Eigene Adresse je Foto wie bei Immich: #/gallery/photo/<Pfad> öffnet den Betrachter auf diesem Foto, mit den Fotos seines Monats
  // zum Weiterblättern (Übersicht, Lesezeichen, Neuladen)
  const photoHash = () => { const m = location.hash.match(/^#\/?gallery\/photo\/(.+)$/); return m ? decodeURIComponent(m[1]) : null }
  useEffect(() => {
    if (!gallery) return
    const on = async () => {
      const id = photoHash()
      if (!id) return
      const k = await galleryLocate(id)
      const month = k ? await galleryMonth(k).catch(() => null) : null
      const list = month?.items ?? []
      if (month) setLinkedMeta(m => ({ ...m, ...month.meta }))
      const n = list.findIndex(x => x.path === id)
      // Von außen geöffnet: Schließen führt zurück in den Bereich davor (Übersicht) bzw. bei Lesezeichen und Neuladen in die
      // Galerie - nie wieder auf die Adresse des Fotos und damit erneut in den Betrachter
      if (n >= 0 && !viewerOpen.current && photoHash() === id) {
        if (takePhotoFromElsewhere()) backOut.current = true
        else history.replaceState(history.state, '', '#/gallery')
      }
      if (n >= 0) setViewer(v => v?.list[v.index]?.path === id ? v : { list, index: n })
      else { fail(new Error(t('Dieses Foto gibt es nicht mehr.'))); history.replaceState(null, '', '#/gallery') }
    }
    on()
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [gallery]) // eslint-disable-line react-hooks/exhaustive-deps
  // Beim Blättern im Betrachter wandert die Adresse mit; zu: wieder die Galerie
  const viewerPath = viewer?.list[viewer.index]?.path

  // Nur die Antwort der neuesten Anfrage uebernehmen
  const req = useRef(0)
  // Ordnerkacheln: die neuesten vier Bilder aus dem Ordner (und darunter) als Collage
  const [covers, setCovers] = useState<Record<string, Item[]>>({})
  // Markierte Fotos automatisch verbessern (Engine oder KI)
  const [autoFor, setAutoFor] = useState<Item[] | null>(null)
  const [infoFor, setInfoFor] = useState<Item[] | null>(null)
  const load = useCallback(async () => {
    const n = ++req.current
    try {
      let d: { items: (Item & { used?: string })[]; folder?: Item }
      if (searching && view.kind === 'photos' && album == null) {
        const r = await searchGallery(term, tlQuery)
        if (n === req.current) setExtMeta(r.meta)
        d = { items: r.items }
      } else if (searching) {
        d = await api.get('/api/files/search?' + q({ q: term, path: scope === 'here' && path ? path : '', type: filter || undefined, content: true }))
      } else if (view.kind === 'folder') {
        const r = await api.get('/api/files/?' + q({ path: view.path }))
        setUsage({ usage: r.usage, trash: r.trash, versions: r.versions, quota: r.quota, used: r.used })
        // Tresor: das Suchfeld filtert nur nach Namen im Ordner
        if (isEncrypted(view.path) && term) r.items = (r.items as Item[]).filter(i => i.name.toLocaleLowerCase().includes(term.toLocaleLowerCase()))
        // Eigene Cryptomator-Tresore: in der Liste gekennzeichnet; der Ordner selbst gesperrt oder entsperrt
        if (r.vaults?.length) r.items = (r.items as Item[]).map(i => r.vaults.includes(i.path) ? { ...i, vault: true, vaultOpen: !!r.openVaults?.includes(i.path) } : i)
        if (n === req.current) setVaultInfo({ path: view.path, vault: r.vault ?? null, root: r.vaultRoot ?? null })
        d = { items: r.items, folder: { path: view.path, name: nameOf(view.path), isDir: true, size: 0, mime: null, modified: '', favorite: r.favorite } }
      } else if (view.kind === 'recent') {
        const r = await api.get<{ items: { item: Item; used: string }[] }>('/api/files/recent')
        d = { items: r.items.map(x => ({ ...x.item, used: x.used })) }
      } else if (view.kind === 'favorites') d = await api.get('/api/files/favorites')
      else if (view.kind === 'photos' && album != null) {
        // Ein Album: seine Fotos, Vorschau und Original über das Album
        const r = await loadAlbum(album)
        if (n === req.current) { setExtMeta(r.meta); setAlbumMine(r.mine) }
        d = { items: r.items }
      }
      else if (view.kind === 'photos') {
        // Zeitleiste vom Server: hier nur die Monate (klein, unverändert 304) - die Fotos holt der Bildausschnitt
        void tl.refresh()
        return
      }
      else d = { items: [] }
      if (n === req.current) { setData(d); if (view.kind === 'folder' && !searching) setListed({ path: view.path, items: d.items }) }
      if (view.kind === 'folder' && !searching && d.items.some(i => i.isDir))
        api.get<Record<string, Item[]>>('/api/files/covers?' + q({ path: view.path })).then(c => { if (n === req.current) setCovers(c) }).catch(() => setCovers({}))
      else setCovers({})
    } catch (e) {
      if (n !== req.current) return
      // Eigener Cryptomator-Tresor gesperrt (Leerlauf, anderes Gerät): zurück zum Ordner des Tresors, dort wieder das Passwort
      if (view.kind === 'folder' && isCmv(view.path) && e instanceof ApiError && e.status === 423) {
        const rest = view.path.slice(CMV.length + 1)
        const root = cmvRoots.get().filter(r => rest === r || rest.startsWith(r + '/')).sort((a, b) => b.length - a.length)[0]
        if (root?.startsWith('x:')) { const [m, ...rest2] = root.slice(2).split('/'); setExt({ mount: m, path: rest2.join('/') }) }
        else setView({ kind: 'folder', path: root ?? '' })
        return
      }
      fail(e)
      if (view.kind === 'folder' && view.path) setView({ kind: 'folder', path: '' })
    }
  }, [view, searching, term, scope, filter, path, fail, album, me.id, tl.refresh, photoExt, prefs.photoStack]) // eslint-disable-line react-hooks/exhaustive-deps
  const loadFavs = useCallback(() => {
    api.get<{ items: Item[] }>('/api/files/favorites').then(d => setFavs(d.items)).catch(() => {})
    api.get<{ items: typeof withMe }>('/api/files/shared').then(d => setWithMe(d.items)).catch(() => {})
  }, [])
  useEffect(() => {
    if (!usage) api.get('/api/files/?path=').then(r => setUsage({ usage: r.usage, trash: r.trash, versions: r.versions, quota: r.quota, used: r.used })).catch(() => {})
    loadFavs()
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Beim ersten Laden bleibt die gemerkte Auswahl (zurück aus einem anderen Bereich), danach leert ein Wechsel sie
  const firstLoad = useRef(true)
  useEffect(() => {
    if (firstLoad.current) firstLoad.current = false; else setSelected([])
    setData(null); load()
  }, [view, searching, term, scope, album]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (photosView) loadAlbums() }, [photosView, loadAlbums])
  useEffect(() => {
    if (!atlas || !tlOn) { setGeo(null); return }
    let gone = false
    loadGeo(tlQuery).then(g => { if (!gone) setGeo(g) }).catch(e => { if (!gone) fail(e) })
    return () => { gone = true }
  }, [atlas, tlOn, tl.stamp, JSON.stringify(tlQuery)]) // eslint-disable-line react-hooks/exhaustive-deps
  // Die Karte steht im selben Bereich wie die Fotos: Stelle merken und beim Zurückgehen wiederherstellen
  const listTop = useRef(0)
  const overlay = photosView && atlas
  const wasOverlay = useRef(false)
  useEffect(() => {
    if (overlay && !wasOverlay.current) listTop.current = listRef.current?.scrollTop ?? 0
    else if (!overlay && wasOverlay.current) {
      const top = listTop.current
      // Zweimal: erst stehen die Platzhalter der Abschnitte, dann die Fotos im Bild
      requestAnimationFrame(() => { if (listRef.current) listRef.current.scrollTop = top; requestAnimationFrame(() => { if (listRef.current) listRef.current.scrollTop = top }) })
    }
    wasOverlay.current = overlay
  }, [overlay])
  // Zurück in die Galerie aus einem anderen Bereich: an dieselbe Stelle. Beim Ausblenden (display: none) verliert die Liste ihre
  // Scrollposition - gemerkt wird sie deshalb nur, solange die Liste sichtbar ist
  const galleryTop = useRef(0)
  const galleryBack = useRef(false)
  useEffect(() => {
    if (!gallery) return
    const away = { current: false }
    const on = () => {
      if (!/^#\/?(gallery|files\/:photos)/.test(location.hash)) { away.current = true; return }
      if (!away.current) return
      away.current = false
      const top = galleryTop.current
      galleryBack.current = true
      const put = () => { if (listRef.current) listRef.current.scrollTop = top }
      // Zweimal: erst stehen die Platzhalter der Abschnitte, dann die Fotos im Bild
      requestAnimationFrame(() => { put(); requestAnimationFrame(() => { put(); galleryBack.current = false }) })
    }
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [gallery])
  useEffect(() => { if (searching) load() }, [filter]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    // Ausgeblendet (anderer Bereich aktiv, die Shell hält die Bereiche): die Adresse gehört dem anderen Bereich
    if (!(gallery ? /^#\/?(gallery|files\/:photos)/ : /^#\/?files/).test(location.hash)) return
    const h = arc ? arcHash(arc) : ext ? extHash(ext) : hashOf(view)
    // Die Galerie ist ein eigenes Modul: hin und zurück über den Hash, damit die Leiste links umschaltet
    if (gallery) { if (view.kind !== 'photos' || ext) location.hash = h; else if (!/^#\/?gallery/.test(location.hash)) history.replaceState(null, '', '#/gallery'); return }
    if (view.kind === 'photos' && !ext) { location.hash = '#/gallery'; return }
    if (location.hash !== h) history.replaceState(null, '', h)
    try { sessionStorage.setItem('dm.files.last', h) } catch { /* ohne Speicher */ }
  }, [view, ext, arc]) // eslint-disable-line react-hooks/exhaustive-deps
  const loadUsage = () => api.get('/api/files/?path=').then(r => setUsage({ usage: r.usage, trash: r.trash, versions: r.versions, quota: r.quota, used: r.used })).catch(() => {})
  const trash = useTrash(view.kind === 'trash', fail, m => notify(m), () => { setTreeVersion(v => v + 1); loadUsage() }, view.kind === 'trash' ? term : '')
  const links = useLinks(view.kind === 'shared', fail)
  const changed = () => { load(); setTreeVersion(v => v + 1); setDetailsVersion(v => v + 1); loadFavs(); if (view.kind !== 'folder') loadUsage() }
  // DMS: Dateien als Dokumente übernehmen (Inhalt wird nach Ablageprofil abgelegt, die Datei kommt in den Papierkorb)
  const toDms = async (list: Item[]) => {
    if (!await askConfirm(t('Ins DMS aufnehmen'), list.length === 1 ? t('1 Datei wird als Dokument übernommen und nach dem Standard-Ablageprofil abgelegt. Die Datei hier kommt in den Papierkorb.') : t('{n} Dateien werden als Dokumente übernommen und nach dem Standard-Ablageprofil abgelegt. Die Dateien hier kommen in den Papierkorb.', { n: list.length }), t('Aufnehmen'))) return
    try {
      const r = await api.post<{ files: { duplicate?: boolean; skipped?: boolean }[] }>('/api/dms/import-files', { paths: list.map(i => i.path) })
      const dup = r.files.filter(f => f.duplicate).length
      const skipped = r.files.filter(f => f.skipped).length
      notify([importedText(r.files.length - dup - skipped, dup),
        skipped ? (skipped === 1 ? t('1 Datei ließ sich nicht aufnehmen (nur eigene Dateien)') : t('{n} Dateien ließen sich nicht aufnehmen (nur eigene Dateien)', { n: skipped })) : ''].filter(Boolean).join(', '))
      changed()
    } catch (e) { fail(e) }
  }
  // Aenderungen von anderen Geraeten (Handy, Explorer, Nextcloud): beim Zurueckkommen ins Fenster und alle 20 s
  // still nachladen - Auswahl und Ansicht bleiben
  const refresh = useRef<() => void>(() => {})
  refresh.current = () => { if (view.kind !== 'trash' && view.kind !== 'shared') load(); setTreeVersion(v => v + 1) }
  useEffect(() => {
    const on = () => { if (!document.hidden) refresh.current() }
    const iv = setInterval(on, 20000)
    window.addEventListener('focus', on)
    // Hintergrundarbeit beendet (Importprofil, Einlesen eines Galerie-Ordners): gleich nachladen statt beim nächsten Takt
    window.addEventListener('dmoffice:files-changed', on)
    document.addEventListener('visibilitychange', on)
    return () => { clearInterval(iv); window.removeEventListener('focus', on); window.removeEventListener('dmoffice:files-changed', on); document.removeEventListener('visibilitychange', on) }
  }, [])

  // Favoriten immer oben (Ansicht → Sortieren); nicht in den Ansichten Fotos und Favoriten selbst
  const favTop = !!prefs.fileFavTop && !photosView && view.kind !== 'favorites'
  // Ordner oben (Vorgabe) - vor den Favoriten
  const folderTop = prefs.fileFolderTop !== false && !photosView
  // Sortieren: erst Ordner, dann Favoriten (je nach Schalter); Klick auf denselben Spaltenkopf dreht die Richtung
  // Versteckte Dateien (Name mit Punkt) - Vorgabe ausgeblendet; im Papierkorb und in der Galerie immer alles
  const showHidden = prefs.fileHidden ?? false
  const flatList = view.kind !== 'folder' || searching
  const sortedItems = useMemo(() => {
    if (!data) return []
    if (tlOn) return data.items
    const list = data.items.filter(i => (showHidden || photosView || view.kind === 'trash' || !isHidden(i, flatList)) && (!filter || searching || kindOf(i) === filter) && (!photosView || !photoExt || extOf(i.name) === photoExt)
      && !(path === '' && !searching && i.isDir && i.path === CHAT_DIR))
    const recent = view.kind === 'recent' && !searching
    if (recent && !recentSort) return list
    const by = recent ? recentSort!.key : sort
    const dir = (recent ? recentSort!.desc : desc) ? -1 : 1
    return [...list].sort((a, b) => (!recent && folderTop ? Number(b.isDir) - Number(a.isDir) : 0) || (!recent && favTop ? Number(!!b.favorite) - Number(!!a.favorite) : 0) || dir * (
      recent && by === 'modified' ? ((a as Item & { used?: string }).used ?? a.modified).localeCompare((b as Item & { used?: string }).used ?? b.modified)
        : by === 'modified' ? a.modified.localeCompare(b.modified) : by === 'size' ? a.size - b.size
        : by === 'kind' ? kindLabel(kindOf(a)).localeCompare(kindLabel(kindOf(b)), locale) || a.name.localeCompare(b.name, locale, { sensitivity: 'base', numeric: true })
        : by === 'created' ? (a.created ?? '').localeCompare(b.created ?? '')
        : by === 'taken' ? takenOf(a, metaOf(a)) - takenOf(b, metaOf(b))
        : by === 'ext' ? extOf(a.name).localeCompare(extOf(b.name)) || a.name.localeCompare(b.name, locale, { sensitivity: 'base', numeric: true })
        : a.name.localeCompare(b.name, locale, { sensitivity: 'base', numeric: true })))
  }, [data, filter, searching, view.kind, sort, desc, recentSort, photosView, photoExt, favTop, folderTop, metaOf, showHidden, flatList, tlOn])
  const items = useMemo(() => {
    if (tlOn) return sortedItems
    const list = filterOn ? filterPhotos(sortedItems, metaOf, ruleCols, ruleSel) : sortedItems
    // Galerie: JPG + RAW als Stapel
    return photosView && (prefs.photoStack ?? true) ? stackPhotos(list) : list
  }, [sortedItems, filterOn, metaOf, ruleCols.join(), JSON.stringify(ruleSel), photosView, prefs.photoStack, tlOn]) // eslint-disable-line react-hooks/exhaustive-deps
  // Gruppieren nach Art oder Aenderungsdatum (wie im Explorer); die Liste bleibt in Anzeigereihenfolge
  const groupBy = prefs.fileGroup ?? 'none'
  // Kachelgröße stufenlos (Schieberegler); ab OVERLAY_PX stehen die Bildinfos auf dem Bild statt darunter
  const tileSize = prefs.fileTile ?? 'medium'
  const tileNum = prefs.fileTilePx ?? (tileSize === 'small' ? 96 : tileSize === 'large' ? 220 : 132)
  const tilePx = tileNum + 'px'
  const infoOnImage = photosView && !mobile && tileNum >= OVERLAY_PX
  // Fotos sind eine Zeitleiste: nach Monat gruppiert, neueste zuerst (der Standard der Ansicht)
  const photoGroup = prefs.photoGroup ?? 'month'
  // Zeitleiste nach Name oder Größe: Seiten, keine Abschnitte nach Datum
  const tlTime = sort === 'taken' || sort === 'modified'
  const grouping = photosView ? photoGroup !== 'none' && (!tlOn || tlTime || photoGroup === 'source') : groupBy !== 'none' && !(view.kind === 'recent' && !searching)
  const groupOf = useCallback((i: Item): [number, string] => {
    if (photosView) {
      const d = new Date(takenOf(i, metaOf(i)))
      const start = photoGroup === 'year' ? new Date(d.getFullYear(), 0, 1) : photoGroup === 'day' ? new Date(d.getFullYear(), d.getMonth(), d.getDate()) : new Date(d.getFullYear(), d.getMonth(), 1)
      const label = photoGroup === 'year' ? String(d.getFullYear())
        : photoGroup === 'day' ? d.toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })
        : d.toLocaleDateString(locale, { month: 'long', year: 'numeric' })
      return [photoDesc ? -start.getTime() : start.getTime(), label]
    }
    if (folderTop && i.isDir && groupBy !== 'kind') return [Number.MIN_SAFE_INTEGER, t('Ordner')]
    if (favTop && i.favorite) return [Number.MIN_SAFE_INTEGER + 1, t('Favoriten')]
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
    // Aelter: je Monat („Dezember 2023“), juengste zuerst - ein einziges „Aelter“ hilft bei einem Ordner voller alter Fotos nicht
    const back = (now.getFullYear() - d.getFullYear()) * 12 + now.getMonth() - d.getMonth()
    return [6 + back, d.toLocaleDateString(locale, { month: 'long', year: 'numeric' })]
  }, [groupBy, photosView, photoGroup, photoDesc, favTop, folderTop, metaOf])
  const shown = useMemo(() => {
    if (!grouping || tlOn) return items
    const keyed = items.map((i, n) => ({ i, n, g: groupOf(i) }))
    keyed.sort((a, b) => a.g[0] - b.g[0] || a.g[1].localeCompare(b.g[1], locale) || a.n - b.n)
    return keyed.map(x => x.i)
  }, [items, grouping, groupOf, tlOn])
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
  // Ganzen Abschnitt markieren bzw. die Markierung aufheben (Kasten am Abschnittskopf)
  const groupBox = (label: string) => {
    const paths = groups.find(g => g.label === label)?.items.map(i => i.path) ?? []
    const on = paths.length > 0 && paths.every(p => selected.includes(p))
    const some = !on && paths.some(p => selected.includes(p))
    return <Checkbox className={s.groupCheck} checked={on ? true : some ? 'mixed' : false} aria-label={t('Abschnitt markieren')}
      onChange={() => setSelected(sl => on ? sl.filter(p => !paths.includes(p)) : [...new Set([...sl, ...paths])])} />
  }
  // Kacheln portionsweise zeigen und beim Scrollen nachladen - Tausende Fotos auf einmal machen die Seite zaeh
  const [cap, setCap] = useState(TILE_STEP)
  // Zeitleiste der Galerie (nach Datum sortiert): Monate in Anzeigereihenfolge mit ihrem ersten Eintrag
  const timeline = photosView && (sort === 'taken' || sort === 'modified') && !searching
  const months = useMemo(() => {
    if (!timeline) return []
    if (tlOn) { let first = 0; return (tl.state?.buckets ?? []).map(b => { const m = { key: b.k, first, group: b.l }; first += b.n; return m }) }
    const out: { key: string; first: number; group?: string }[] = []
    shown.forEach((i, n) => {
      const d = new Date(sort === 'taken' ? takenOf(i, metaOf(i)) : Date.parse(i.modified))
      const key = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`
      if (!out.length || out[out.length - 1].key !== key) out.push({ key, first: n })
    })
    return out
  }, [timeline, shown, sort, metaOf, tlOn, tl.state])
  const jumpTo = (index: number) => {
    if (windowed) {
      // Zum Block mit dem Eintrag (beim ersten Block eines Abschnitts samt Kopf)
      const b = blocksRef.current.list
      const n = b.findIndex(x => !x.head && index >= x.start && index < x.start + x.count)
      if (n >= 0 && listRef.current) listRef.current.scrollTop = n > 0 && b[n - 1].head ? b[n - 1].off : b[n].off
      return
    }
    setCap(c => Math.max(c, index + TILE_STEP))
    const target = shown[index]?.path
    if (target) setTimeout(() => document.querySelector(`[data-path="${CSS.escape(target)}"]`)?.scrollIntoView({ block: 'start' }), 60)
  }
  useEffect(() => setCap(TILE_STEP), [data])
  const cappedGroups = useMemo(() => {
    let left = cap
    return groups.map(g => { const part = g.items.slice(0, Math.max(0, left)); left -= part.length; return { ...g, total: g.items.length, items: part } })
      .filter(g => g.items.length > 0)
  }, [groups, cap])

  // Galerie: nur zeichnen, was sichtbar ist (wie Immich). Die Abschnitte werden in Blöcke zu je einigen Reihen geteilt; gezeichnet
  // werden die Blöcke im Bild plus etwas davor und danach, für den Rest steht nur leerer Platz in der passenden Höhe. Sonst
  // zeichnet React bei zehntausenden Fotos bei jedem Nachladen alle bisherigen Kacheln neu, und das Scrollen hängt Sekunden.
  const windowed = photosView && viewMode === 'tiles'
  const [dims, setDims] = useState({ row: 0, head: 34, gap: 8 })
  const blocks = useMemo(() => {
    if (!windowed) return { list: [] as Block[], total: 0 }
    const min = mobile ? 104 : parseInt(tilePx)
    const gap = mobile ? 4 : dims.gap
    const inner = Math.max(min, listW - (mobile ? 8 : 32))
    const cols = Math.max(1, Math.floor((inner + gap) / (min + gap)))
    // Ohne Messung: Kachel etwa quadratisch plus Bildinfos
    const row = dims.row || Math.round(inner / cols * 0.75 + 30 + (infoOnImage ? 0 : tileInfo.length * 14) + gap)
    const per = cols * 8
    const list: Block[] = []
    let off = 0, start = 0
    const rowsH = (n: number) => Math.ceil(n / cols) * row - gap + 4
    if (tlOn) {
      // Je Monat: geladen - Abschnitte (Tag, Monat, Jahr) mit Kacheln; sonst Platz in der Höhe, die seine Anzahl braucht.
      // Eine Überschrift nur, wenn sie sich von der vorigen unterscheidet (Jahr über mehrere Monate)
      let last = ''
      const buckets = tl.state?.buckets ?? []
      const yearCount = new Map<string, number>()
      for (const b of buckets) yearCount.set(b.k.slice(0, 4), (yearCount.get(b.k.slice(0, 4)) ?? 0) + b.n)
      // Nach Quelle: Name vom Server je Abschnitt, Anzahl über alle Abschnitte der Quelle
      const bySource = photoGroup === 'source'
      const sourceCount = new Map<string, number>()
      if (bySource) for (const b of buckets) sourceCount.set(t(b.l ?? ''), (sourceCount.get(t(b.l ?? '')) ?? 0) + b.n)
      const monthName = (k: string) => new Date(+k.slice(0, 4), +k.slice(5) - 1, 1).toLocaleDateString(locale, { month: 'long', year: 'numeric' })
      for (const b of buckets) {
        const its = tl.loaded(b.k)
        if (!its) {
          const label = !grouping ? '' : bySource ? t(b.l ?? '') : photoGroup === 'year' ? b.k.slice(0, 4) : monthName(b.k)
          const head = !!label && label !== last
          if (label) last = label
          const h = (head ? dims.head : 0) + rowsH(b.n)
          list.push({ key: 'ph:' + b.k, label: head ? label : '', head: false, ph: true, items: [], start, off, h, count: b.n, bucket: b.k })
          off += h; start += b.n
          continue
        }
        const parts: { label: string; items: Item[] }[] = []
        for (const i of its) {
          const label = !grouping ? '' : bySource ? t(b.l ?? '') : groupOf(i)[1]
          if (parts.length && parts[parts.length - 1].label === label) parts[parts.length - 1].items.push(i)
          else parts.push({ label, items: [i] })
        }
        for (const g of parts) {
          if (g.label && g.label !== last) {
            const count = bySource ? sourceCount.get(g.label) ?? g.items.length : photoGroup === 'year' ? yearCount.get(b.k.slice(0, 4)) ?? g.items.length : photoGroup === 'month' ? b.n : g.items.length
            list.push({ key: 'h:' + b.k + ':' + g.label, label: g.label, head: true, items: g.items, start, off, h: dims.head, count, bucket: b.k }); off += dims.head
          }
          if (g.label) last = g.label
          for (let k = 0; k < g.items.length; k += per) {
            const part = g.items.slice(k, k + per)
            const h = rowsH(part.length)
            list.push({ key: b.k + ':' + g.label + ':' + k, label: g.label, head: false, items: part, start: start + k, off, h, count: part.length, bucket: b.k })
            off += h
          }
          start += g.items.length
        }
      }
      return { list, total: off }
    }
    for (const g of groups) {
      if (g.label) { list.push({ key: 'h:' + g.label, label: g.label, head: true, items: g.items, start, off, h: dims.head, count: g.items.length }); off += dims.head }
      for (let k = 0; k < g.items.length; k += per) {
        const part = g.items.slice(k, k + per)
        // Reihen samt Abstand, unter der letzten Reihe keiner; dazu der obere Rand des Rasters
        const h = rowsH(part.length)
        list.push({ key: g.label + ':' + k, label: g.label, head: false, items: part, start: start + k, off, h, count: part.length })
        off += h
      }
      start += g.items.length
    }
    return { list, total: off }
  }, [windowed, groups, listW, mobile, tilePx, dims, tileInfo.length, infoOnImage, tlOn, tl.state, tl.loaded, grouping, photoGroup, groupOf])
  const blocksRef = useRef(blocks)
  blocksRef.current = blocks
  const [range, setRange] = useState<[number, number]>([0, 12])
  const prevBlocks = useRef(blocks)
  const visibleRange = useCallback(() => {
    const el = listRef.current
    const b = blocksRef.current.list
    if (!el || b.length === 0 || !el.getClientRects().length) return
    const top = el.scrollTop - el.clientHeight * 1.5, bottom = el.scrollTop + el.clientHeight * 2.5
    let a = b.findIndex(x => x.off + x.h >= top)
    if (a < 0) a = Math.max(0, b.length - 1)
    let z = a
    while (z < b.length - 1 && b[z + 1].off <= bottom) z++
    setRange(r => r[0] === a && r[1] === z ? r : [a, z])
  }, [])
  useEffect(() => {
    const el = listRef.current
    if (!windowed || !el) return
    let raf = 0
    const on = () => { if (!raf) raf = requestAnimationFrame(() => { raf = 0; visibleRange() }) }
    const ro = new ResizeObserver(on)
    ro.observe(el)
    el.addEventListener('scroll', on, { passive: true })
    return () => { el.removeEventListener('scroll', on); ro.disconnect(); cancelAnimationFrame(raf) }
  }, [windowed, data, visibleRange])
  useEffect(() => { visibleRange() }, [blocks, visibleRange])
  // Zeitleiste: ändert sich die Höhe darüber (Monat geladen, Reihenhöhe gemessen), an derselben Stelle bleiben. Die Stelle
  // aus der jetzigen Scrollposition und dem bisherigen Aufbau - eine beim letzten Scrollen gemerkte war bei laufendem Nachladen
  // (Vorschaubilder kommen dazu) oft schon veraltet und zog die Ansicht immer wieder dorthin zurück
  useLayoutEffect(() => {
    const el = listRef.current, old = prevBlocks.current.list
    prevBlocks.current = blocks
    if (!tlOn || !el || old === blocks.list) return
    const at = old.find(x => x.off + x.h > el.scrollTop)
    if (!at?.bucket) return
    const was = old.find(x => x.bucket === at.bucket)!.off
    const now = blocks.list.find(x => x.bucket === at.bucket)
    if (now && now.off !== was) el.scrollTop += now.off - was
  }, [blocks]) // eslint-disable-line react-hooks/exhaustive-deps
  // Zeitleiste: die Monate im Bild (der gezeichnete Bereich reicht schon gut eine Bildschirmhöhe darüber hinaus) und je einer davor
  // und danach laden - erst, wenn das Scrollen kurz steht (beim Ziehen an der Jahresleiste nicht jeden Monat unterwegs)
  useEffect(() => {
    if (!tlOn || !tl.state) return
    const h = setTimeout(() => {
      const keys = tl.state!.buckets.map(x => x.k)
      const at = new Map(keys.map((k, n) => [k, n]))
      const b = blocksRef.current.list
      const seen: number[] = []
      for (let n = range[0]; n <= range[1] && n < b.length; n++) { const i = b[n].bucket != null ? at.get(b[n].bucket!) : undefined; if (i != null) seen.push(i) }
      if (!seen.length) { if (keys.length) tl.want([keys[0]]); return }
      const lo = Math.max(0, Math.min(...seen) - 1), hi = Math.min(keys.length - 1, Math.max(...seen) + 1)
      tl.want(keys.slice(lo, hi + 1))
    }, 150)
    return () => clearTimeout(h)
  }, [tlOn, tl.state, range, blocks]) // eslint-disable-line react-hooks/exhaustive-deps
  // Betrachter in der Zeitleiste: am Rand der geladenen Fotos den Nachbarmonat holen; die Liste wächst mit, das Foto bleibt
  useEffect(() => {
    if (!viewer?.tl) { tl.want([], 'viewer'); return }
    if (!tl.state) return
    const cur = viewer.list[viewer.index]
    const k = cur && tl.bucketOf.get(cur.path)
    if (!k) return
    const keys = tl.state.buckets.map(b => b.k), n = keys.indexOf(k)
    const near = viewer.index >= viewer.list.length - 3 || viewer.index <= 2
    tl.want(near ? keys.slice(Math.max(0, n - 1), n + 2) : [k], 'viewer')
  }, [viewer?.index, viewer?.tl, viewer?.list.length]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!viewer?.tl) return
    const list = tl.items.filter(x => !x.isDir && viewKind(x))
    if (list.length === viewer.list.length) return
    const n = list.findIndex(x => x.path === viewer.list[viewer.index]?.path)
    if (n >= 0) setViewer(v => v && { ...v, list, index: n })
  }, [tl.items]) // eslint-disable-line react-hooks/exhaustive-deps
  // Höhe einer Reihe und eines Abschnittskopfs nach dem Zeichnen messen - dann stimmen die Platzhalter
  useLayoutEffect(() => {
    if (!windowed || !listRef.current?.getClientRects().length) return
    const grid = listRef.current?.querySelector('[data-grid]') as HTMLElement | null
    const tile = grid?.querySelector('[data-path]') as HTMLElement | null
    const head = listRef.current?.querySelector('[data-grouphead]') as HTMLElement | null
    if (!grid || !tile) return
    const gap = parseFloat(getComputedStyle(grid).rowGap) || 8
    const row = Math.round(tile.offsetHeight + gap)
    const h = head ? head.offsetHeight : dims.head
    if (Math.abs(row - dims.row) > 1 || Math.abs(h - dims.head) > 1 || gap !== dims.gap) setDims({ row, head: h, gap })
  })
  // In der Ansicht Fotos gilt eine eigene Reihenfolge (Vorgabe: neueste zuerst), die der Ordner bleibt unberuehrt
  const setSort = (patch: { sort?: SortKey; desc?: boolean }) => photosView
    ? update({ ...(patch.sort ? { photoSort: patch.sort as 'name' | 'modified' | 'size' | 'taken' } : {}), ...(patch.desc !== undefined ? { photoSortDesc: patch.desc } : {}) })
    : update({ ...(patch.sort ? { fileSort: patch.sort } : {}), ...(patch.desc !== undefined ? { fileSortDesc: patch.desc } : {}) })
  const sortBy = (k: SortKey) => k === sort ? setSort({ desc: !desc }) : setSort({ sort: k, desc: k === 'modified' || k === 'size' || k === 'created' || k === 'taken' })
  const sel = items.filter(i => selected.includes(i.path))
  // Liste ↔ Kacheln umgeschaltet: der gewählte Eintrag bleibt im Bild
  const lastMode = useRef(viewMode)
  useEffect(() => {
    if (lastMode.current === viewMode) return
    lastMode.current = viewMode
    const p = selected[selected.length - 1]
    if (p) setTimeout(() => mainRef.current?.querySelector(`[data-path="${CSS.escape(p)}"]`)?.scrollIntoView({ block: 'center' }), 80)
  }, [viewMode]) // eslint-disable-line react-hooks/exhaustive-deps
  // Galerie: Markierung, Sterne, Farbe für die gewählten Fotos setzen (Setter-Leiste, Tasten). Blendet die Filter-Leiste das
  // Ergebnis aus, verschwinden die Fotos sofort und das nächste sichtbare wird gewählt.
  // Nacheinander: schnell hintereinander gedrückte Tasten träfen sonst auf einen Stand der Datei, der gerade neu geschrieben wird
  const markQueue = useRef<Promise<void>>(Promise.resolve())
  const markPhotos = (body: { pick?: number; rating?: number; label?: string }, list: Item[] = sel) => {
    markQueue.current = markQueue.current.then(() => markNow(body, list))
    return markQueue.current
  }
  const markNow = async (body: { pick?: number; rating?: number; label?: string }, list: Item[]) => {
    const photos = list.filter(i => !i.isDir)
    if (!photos.length) return
    const q = prefs.photoQuick
    const hides = !!q && (body.pick != null && q.flags.length > 0 && !q.flags.includes(body.pick === 1 ? 'P' : body.pick === -1 ? 'X' : 'U')
      || body.rating != null && q.stars > 0 && body.rating < q.stars
      || body.label != null && q.labels.length > 0 && !q.labels.includes(body.label))
    const paths = new Set(photos.map(i => i.path))
    const last = Math.max(...photos.map(i => items.findIndex(x => x.path === i.path)))
    const next = hides ? items.slice(last + 1).find(i => !paths.has(i.path)) ?? [...items.slice(0, last)].reverse().find(i => !paths.has(i.path)) : undefined
    try {
      const failed: { name: string; error: string }[] = []
      for (let k = 0; k < photos.length; k += 50) {
        const r = await api.post<{ failed: { name: string; error: string }[] }>('/api/gallery/info', { items: photos.slice(k, k + 50).map(i => i.path), ...body })
        failed.push(...(r.failed ?? []))
      }
      if (failed.length) fail(failed.map(f => `${f.name}: ${f.error}`).join(', '))
      if (hides) setSelected(next ? [next.path] : [])
      // Gespeichertes gleich zeigen (Kopf des Betrachters, rechte Leiste, Kacheln)
      const ok = photos.filter(i => !failed.some(f => f.name === i.name))
      if (ok.length) setMarked(m => {
        const n = { ...m }
        const change = { ...(body.pick != null ? { pick: body.pick } : {}), ...(body.rating != null ? { rating: body.rating } : {}), ...(body.label != null ? { label: body.label || null } : {}) }
        for (const i of ok) n[i.path] = { ...n[i.path], ...change, at: Date.now() }
        return n
      })
      if (tlOn) await tl.refresh(); else changed()
      setDetailsVersion(v => v + 1)
    } catch (e) { fail(e) }
  }
  // Atlas: sind Fotos markiert, nur diese - und für sie lässt sich der Ort von Hand setzen
  const placeSelected = async (list: Item[], lat: number, lon: number) => {
    const has = list.filter(i => metaOf(i)?.lat != null).length
    if (!await askConfirm('', (list.length === 1 ? t('Ort für „{name}“ hier setzen?', { name: list[0].name }) : t('Ort für {n} Fotos hier setzen?', { n: list.length }))
      + (has ? ' ' + t('{n} davon haben schon einen Ort - er wird ersetzt.', { n: has }) : ''))) return
    const bad: string[] = []
    try {
      for (let k = 0; k < list.length; k += 50) {
        const r = await api.post<{ failed: { name: string; error: string }[] }>('/api/gallery/info', { items: list.slice(k, k + 50).map(i => i.path), lat, lon })
        bad.push(...r.failed.map(f => `${f.name}: ${f.error}`))
      }
      if (bad.length) fail(new Error(bad.slice(0, 5).join('\n')))
      else notify(t('Ort gesetzt.'))
    } catch (e) { fail(e) }
    changed()
  }
  const onlySelected = (list: Item[]) => { if (!selected.length) return list; const set = new Set(selected); return list.filter(i => set.has(i.path)) }
  const one = sel.length === 1 ? sel[0] : null
  const flat = view.kind !== 'folder' || searching   // Liste aus mehreren Ordnern: Spalte "Ort"
  // Spalten der Detailansicht (FileColumns): beim Ziehen erst nur hier, beim Loslassen gespeichert
  const colCfg = live?.cols ?? cleanCols(prefs.fileCols as ColPref[] | undefined)
  const nameWidth = live ? live.name : prefs.fileNameWidth
  const filler = cols >= 2 && !!nameWidth
  const shownCols: ColPref[] = cols < 2 ? [] : flat && !colCfg.some(c => c.key === 'place') ? [{ key: 'place', width: COLS.place.width }, ...colCfg] : colCfg
  const saveCols = (c: ColPref[], name = nameWidth) => { setLive(null); update({ fileCols: c, fileNameWidth: name || undefined }) }
  // Aufnahmedaten: in der Galerie immer, in einem Ordner nur mit einer ihrer Spalten oder beim Sortieren nach Aufnahmedatum.
  // Neu geholt beim Wechsel, wenn sich die Anzahl ändert oder noch welche gelesen werden (sonst nicht bei jedem Nachladen)
  const needsMeta = (view.kind === 'folder' && !searching && (sort === 'taken' || (viewMode === 'details' && colCfg.some(c => META_COLS.includes(c.key)))))
  const metaScope = !needsMeta ? null : photosView ? '*' : view.kind === 'folder' ? view.path : null
  useEffect(() => {
    if (!data || metaScope === null) { if (metaScope === null) setMeta(null); return }
    if (meta && meta.scope === metaScope && meta.count === data.items.length && meta.pending === 0) return
    api.get<{ items: Meta[]; pending: number }>('/api/files/meta' + (metaScope === '*' ? '' : '?' + q({ path: metaScope })))
      .then(r => setMeta({ scope: metaScope, byKey: Object.fromEntries(r.items.map(m => [m.key, m])), pending: r.pending, count: data.items.length }))
      .catch(() => setMeta(null))
  }, [data, metaScope]) // eslint-disable-line react-hooks/exhaustive-deps
  const setWidth = (k: ColKey | 'name', w: number) => setLive({
    cols: k === 'name' ? colCfg : colCfg.map(c => (c.key === k ? { ...c, width: w || COLS[k].width } : c)),
    name: k === 'name' ? w || undefined : nameWidth,
  })
  // Loslassen (Doppelklick auf die Kante: 0 = Standardbreite): aus den gespeicherten Spalten, nicht aus dem Zwischenstand
  const commitWidth = (k: ColKey | 'name', w: number) => {
    const saved = cleanCols(prefs.fileCols as ColPref[] | undefined)
    setLive(null)
    if (k === 'name') update({ fileCols: saved, fileNameWidth: w || undefined })
    else update({ fileCols: saved.map(c => (c.key === k ? { ...c, width: w || COLS[k].width } : c)) })
  }
  const moveCol = (from: ColKey, to: ColKey, after: boolean) => {
    const f = colCfg.find(c => c.key === from), rest = colCfg.filter(c => c.key !== from)
    const at = rest.findIndex(c => c.key === to) + (after ? 1 : 0)
    if (f && rest.some(c => c.key === to)) saveCols([...rest.slice(0, at), f, ...rest.slice(at)])
  }

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
  // Version 2: in Stuecken - fortsetzbar nach Netzabbruechen, ohne Groessengrenze je Datei
  const sendOne = async (file: File, rel: string, dir: string, mode: string, onBytes: (n: number) => void) => {
    const id = await uploadChunked(file, rel, onBytes, x => { xhr.current = x })
    if (cancelled.current) { dropUpload(id); throw new Error('abort') }
    try { await finishToFiles(id, dir, mode, x => { xhr.current = x }) }
    catch (e) {
      if (e instanceof UploadError && e.status === 0) throw new Error(t('Verbindung unterbrochen'))
      throw e instanceof UploadError && e.status === 507 ? new Error(e.message || 'Speicher voll (507)') : e
    }
  }
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
  /** Löschen; true, wenn gelöscht (nicht abgebrochen, kein Fehler) */
  const remove = async (stacked: Item[], asked = false): Promise<boolean> => {
    const list = unstack(stacked)
    if (!list.length) return false
    // Aus Galerie-Ordnern: im externen Ordner selbst, ohne Papierkorb - darum vorher fragen.
    // Gemischte Auswahl: erst diese, dann die eigenen Dateien wie gewohnt in den Papierkorb.
    const ext = list.filter(isExt)
    const own = list.filter(i => !isExt(i))
    if (ext.length) {
      if (!deletable(list)) return false
      // Gemischt: beides nennen, sonst klingt es, als würden nur die Dateien der Galerie-Ordner gelöscht
      const files = (n: number) => n === 1 ? t('1 Datei') : t('{n} Dateien', { n })
      const question = own.length
        ? t('{own} in den Papierkorb verschieben und {ext} aus Galerie-Ordnern endgültig löschen? Für Galerie-Ordner gibt es keinen Papierkorb. Zugehörige .xmp-Dateien werden mitgelöscht.', { own: files(own.length), ext: files(ext.length) })
        : ext.length === 1 ? t('„{name}“ endgültig aus dem Ordner löschen? Eine zugehörige .xmp-Datei wird mitgelöscht.', { name: ext[0].name })
        : t('{n} Dateien endgültig aus dem Ordner löschen? Zugehörige .xmp-Dateien werden mitgelöscht.', { n: ext.length })
      if (!asked && !await askConfirm('', question)) return false
      try {
        await api.post('/api/gallery/external/delete', { ids: ext.map(extId) })
        notify(ext.length === 1 ? t('„{name}“ gelöscht', { name: ext[0].name }) : t('{n} Elemente gelöscht', { n: ext.length }))
        setSelected([])
      } catch (e) { fail(e); changed(); return false }
      if (!own.length) { changed(); return true }
    }
    try {
      const r = await api.post<{ trash: number[] }>('/api/files/delete', { paths: own.map(i => i.path) })
      // Rückgängig betrifft nur die eigenen Dateien (Galerie-Ordner haben keinen Papierkorb)
      const label = own.length === 1 ? t('„{name}“ gelöscht', { name: own[0].name }) : t('{n} Elemente gelöscht', { n: own.length })
      // In einem geteilten Ordner liegt das Gelöschte im Papierkorb des Besitzers - zurückholen kann nur er
      if (r.trash.length) pushUndo({ label, run: async () => { await api.post('/api/files/trash/restore', { ids: r.trash }) } })
      else notify(label)
    } catch (e) { fail(e); changed(); return false }
    if (path && own.some(i => inside(i.path, path))) openFolder(parentOf(own[0].path))
    changed()
    return true
  }
  // Abgelehnte löschen: Die Ansicht zeigt nur noch die abgelehnten (X), dann eine Rückfrage für alle - auch die noch nicht geladenen Monate.
  // Abbrechen lässt die Ansicht so stehen; nach dem Löschen gilt wieder der vorige Filter.
  const removeRejected = async () => {
    const before = prefs.photoQuick
    const xOnly = { flags: ['X'], stars: 0, labels: [] }
    update({ photoQuick: xOnly })
    const rule = quickRule(xOnly)!
    let all: Item[]
    try {
      all = unstack(await galleryAll({ ...tlQuery, stack: false, group: undefined,
        filter: JSON.stringify(baseOn ? { cols: [...filterCols, ...rule.cols], sel: [...photoSel, ...rule.sel] } : rule) }))
    } catch (e) { fail(e); return }
    const list = all.filter(i => deletable([i]))
    if (!list.length) { notify(all.length ? t('Die abgelehnten Dateien lassen sich nicht löschen.') : t('Keine abgelehnten Dateien.')); return }
    const ext = list.filter(isExt).length, own = list.length - ext
    const files = (n: number) => n === 1 ? t('1 Datei') : t('{n} Dateien', { n })
    const question = [list.length === 1 ? t('1 abgelehnte Datei löschen?') : t('{n} abgelehnte Dateien löschen?', { n: list.length }),
      !ext ? t('Gelöschte Dateien kommen in den Papierkorb.')
        : own ? t('{own} kommen in den Papierkorb, {ext} aus Galerie-Ordnern werden endgültig gelöscht.', { own: files(own), ext: files(ext) })
        : t('Die Dateien liegen in Galerie-Ordnern und werden endgültig gelöscht. Dafür gibt es keinen Papierkorb.'),
      ext ? t('Zugehörige .xmp-Dateien werden mitgelöscht.') : '',
      all.length > list.length ? t('{n} weitere lassen sich nicht löschen und bleiben.', { n: all.length - list.length }) : ''].filter(Boolean).join(' ')
    if (!await askConfirm(t('Abgelehnte löschen'), question, t('Löschen'))) return
    if (await remove(list, true)) update({ photoQuick: before })
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
  const transfer = async (picked: string[], target: string, copy: boolean) => {
    // Stapel: die RAW-Dateien gehen mit
    const paths = picked.flatMap(p => [p, ...(items.find(i => i.path === p)?.stack ?? []).map(x => x.path)])
    const todo = paths.filter(p => !inside(p, target) && (copy || parentOf(p) !== target))
    if (!todo.length) return
    if (copy && todo.some(shownAt)) { notShown(); return }
    if (!writableAt(target)) { fail(new Error(t('Nur lesbar – der Besitzer hat dir hier keine Bearbeitung erlaubt.'))); return }
    // Tresor: Herausnehmen ist ein bewusstes Entschlüsseln; Hineinverschieben löscht die Originale endgültig (nicht in den Papierkorb)
    const out = todo.filter(p => isEncrypted(p) && !isEncrypted(target))
    if (out.length && !await askConfirm(t('Aus dem Tresor nehmen'), out.length === 1 ? t('„{name}“ liegt danach unverschlüsselt in „Meine Dateien“.', { name: nameOf(out[0]) })
      : t('{n} Einträge liegen danach unverschlüsselt in „Meine Dateien“.', { n: out.length }), copy ? t('Kopieren') : t('Verschieben'))) return
    const into = todo.filter(p => !isEncrypted(p) && isEncrypted(target))
    if (into.length && !copy && !await askConfirm(t('In den Tresor verschieben'), (into.length === 1 ? t('„{name}“ wird verschlüsselt in den Tresor gelegt.', { name: nameOf(into[0]) })
      : t('{n} Einträge werden verschlüsselt in den Tresor gelegt.', { n: into.length })) + ' ' + t('Die Originale werden danach endgültig gelöscht, nicht in den Papierkorb gelegt, damit nichts unverschlüsselt zurückbleibt.'), t('Verschieben'))) return
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
  // Übertragung zwischen „Meine Dateien“ und einem externen Ordner: nur in der eigenen Ablage, nicht im Tresor, im Chat-Ordner oder in geteilten Ordnern
  const ownPlace = (p: string) => !isEncrypted(p) && !isTresor(p) && !inWithMe(p, anyShared) && p !== CHAT_DIR && !p.startsWith(CHAT_DIR + '/')
  // Mit Rückfrage (Quelle, Ziel, Umfang), sobald Ordner dabei sind oder im Ziel schon etwas gleich heißt
  const xfer = useTransfer()
  const across = (fromMount: string | null, paths: string[], toMount: string | null, to: string, move: boolean) =>
    xfer.start({ fromMount, paths, toMount, to, move })
  const paste = (target = path ?? '') => {
    if (!clip || target === CHAT_DIR || target.startsWith(CHAT_DIR + '/')) return
    const c = clip
    // Aus einem externen Ordner hierher: der Server überträgt im Hintergrund
    if (c.mount) {
      if (!ownPlace(target)) { fail(new Error(t('Aus externen Ordnern geht es nur in „Meine Dateien“, nicht in den Tresor oder geteilte Ordner.'))); return }
      if (c.cut) setClip(null)
      across(c.mount, c.paths, null, target, c.cut)
      return
    }
    if (c.cut) setClip(null)
    // Ausschneiden aus einem nur lesbaren Ordner: es wird kopiert (der Server legt eine Kopie an und laesst das Original)
    transfer(c.paths, target, !c.cut || c.paths.some(p => !writableAt(p) || isMountRoot(p)))
  }
  const toClip = (list: Item[], cut: boolean) => {
    if (!list.length) return
    if (!cut && !keepable(list)) { notShown(); return }
    setClip({ mount: null, paths: list.map(i => i.path), cut })
    // Beim Kopieren eines Bildes zusaetzlich das Bild selbst in die Zwischenablage von Windows/macOS -
    // dann laesst es sich in Chat, Mail oder Office mit Strg+V einfuegen
    if (!cut && copyImage(list[0], list.length)) return
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
  /** count: so viele sind markiert - mehr als ein Bild nimmt die Zwischenablage aus dem Browser nicht, das wird dann dazugesagt. */
  const copyImage = (i: Item, count = 1) => {
    if (i.isDir || kindOf(i) !== 'image' || typeof ClipboardItem === 'undefined' || !navigator.clipboard?.write) return false
    const p = prepared.current?.path === i.path ? prepared.current : null
    // Fertiges Bild direkt, sonst als Promise (Chrome/Edge warten darauf, solange der Tastendruck gilt)
    const content = p?.ready ?? p?.png ?? pngOf(i)
    navigator.clipboard.write([new ClipboardItem({ 'image/png': content })])
      .then(() => notify(count > 1
        ? t('{n} kopiert – hier in einem Ordner alle ablegen; in andere Programme lässt sich nur das erste Bild einfügen, mehr gibt der Browser nicht in die Zwischenablage', { n: count })
        : t('Bild kopiert – in anderen Programmen mit Strg+V einfügen, hier in einem Ordner ablegen'), undefined, 'info'))
      .catch((e: unknown) => notify(t('Das Bild konnte nicht in die Zwischenablage von Windows – der Browser hat es abgelehnt ({reason}). In dmOffice ist es zum Einfügen kopiert.', { reason: e instanceof Error ? e.message : String(e) }), undefined, 'warning'))
    return true
  }
  // Ordner für WebDAV und Nextcloud aus- oder einblenden (beim Tresor-Ordner dmVAULT: „bereitstellen“ des Tresors)
  const davHide = async (i: Item, hidden: boolean) => {
    if (hidden && !await askConfirm(t('In WebDAV und Nextcloud ausblenden'),
      t('Der Ordner „{name}“ verschwindet samt Inhalt für Nextcloud-Clients und WebDAV-Programme; sie entfernen ihre lokale Kopie. Änderungen, die auf einem Gerät noch nicht übertragen sind, gehen dort verloren. In dmOffice und in der App bleibt er sichtbar.', { name: i.name }),
      t('Ausblenden'))) return
    try { await api.post('/api/files/dav-hidden', { path: i.path, hidden }); changed() } catch (e) { fail(e) }
  }
  const favorite = async (i: Item, on: boolean) => {
    try { await api.post('/api/files/favorite', { path: i.path, on }); changed() } catch (e) { fail(e) }
  }
  const quickLink = async (i: Item) => {
    try {
      const l = await api.post<Link>('/api/files/links', { path: i.path, mode: 'read' })
      if (await copyText(linkUrl(l))) notify(t('Öffentlicher Link erstellt und kopiert'), t('Jeder mit dem Link kann „{name}“ ohne Anmeldung ansehen und herunterladen.', { name: i.name }))
      changed()
    } catch (e) { fail(e) }
  }
  const offlineNote = () => notify(t('Ordner nicht erreichbar'), t('Der externe Ordner ist gerade nicht erreichbar – nur die Vorschau ist da.'))
  const open = (i: Item) => {
    if (i.isDir) { openFolder(i.path); return }
    if (extOffline(i)) { offlineNote(); return }
    // Archiv (eigene Ablage und geteilte Ordner, nicht Galerie-Ordner): wie einen Ordner öffnen
    if (isArchive(i.name) && !isExt(i) && !isEncrypted(i.path)) { setSearch(''); setArc({ file: i.path, dir: '' }); return }
    // Office-Dateien (ohne eigenen Betrachter, also nicht CSV) im Editor des Office-Servers
    // PDF und Office-Dateien über den Office-Server (EuroOffice), wenn er sie kann; sonst der eigene Betrachter
    if ((!viewKind(i) || viewKind(i) === 'pdf') && officeMode(i, office) && !isCmv(i.path)) { setEditing({ path: i.path, name: i.name, view: officeMode(i, office) === 'view' }); return }
    if (viewKind(i)) {
      const list = items.filter(x => !x.isDir && viewKind(x) && !extOffline(x))
      setStartEdit(false)
      setViewer({ list, index: Math.max(0, list.findIndex(x => x.path === i.path)), tl: tlOn })
    } else download([i])
  }
  /** Eine Datei direkt, sonst (Ordner, mehrere) als ZIP - gestreamt vom Server. */
  const download = (stacked: Item[]) => {
    if (stacked.some(extOffline)) { offlineNote(); return }
    const list = unstack(stacked)
    if (!list.length) return
    if (!keepable(list)) { notShown(); return }
    // Galerie-Ordner: einzeln aus dem externen Speicher
    if (list.some(isExt)) {
      for (const i of list.filter(isExt)) { const a = document.createElement('a'); a.href = gallerySource.download(i); a.download = i.name; a.click() }
      return
    }
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
    // Klicks aus Menüs und Dialogen dieser Zeile (Portale) kommen auch hier an - die zählen nicht
    if (!e.currentTarget.contains(e.target as Node)) return
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
    // Galerie wie Lightroom: P ausgewählt, X abgelehnt, U ohne Markierung, 0–5 Sterne, 6–9 Rot/Gelb/Grün/Blau (noch einmal: weg)
    const k = e.key.toLowerCase()
    // Wie im Camera Importer: Strg+1 bis Strg+5 Rot, Gelb, Grün, Blau, Lila; Strg+0 ohne Farbe; dieselbe Farbe noch einmal: weg
    if (photosView && ctrl && !e.altKey && !e.shiftKey && sel.length && /^Digit[0-5]$/.test(e.code)) {
      e.preventDefault()
      const n = +e.code.slice(5)
      const l = n === 0 ? '' : MARK_LABELS[n - 1]
      markPhotos({ label: l && sel.every(i => metaOf(i)?.label === l) ? '' : l })
      return
    }
    if (photosView && !ctrl && !e.altKey && sel.length && /^[pxu0-9]$/.test(k)) {
      e.preventDefault()
      if (k === 'p') markPhotos({ pick: 1 })
      else if (k === 'x') markPhotos({ pick: -1 })
      else if (k === 'u') markPhotos({ pick: 0 })
      else if (+k <= 5) markPhotos({ rating: +k })
      else {
        const l = ['red', 'yellow', 'green', 'blue'][+k - 6]
        markPhotos({ label: sel.every(i => metaOf(i)?.label === l) ? '' : l })
      }
      return
    }
    if (e.key === 'Delete') { e.preventDefault(); if (deletable(sel)) remove(sel); else if (sel.length && sel.every(i => isMountRoot(i.path))) leaveShared(sel) }
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
      // Ausgeblendet (anderer Bereich aktiv): nicht reagieren
      if (!(gallery ? /^#\/?gallery/ : /^#\/?files/).test(location.hash)) return
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
    if (paths.length === 1 && !i.isDir && !shownAt(i.path)) {
      const url = new URL(gallerySource.download(i), location.origin).href
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

  // ---------------- Alben ----------------
  const albumNow = albums.find(a => a.id === album) ?? null
  const fixedAlbums = albums.filter(a => a.kind === 'fixed')
  const addToAlbum = async (id: number, list: Item[]) => {
    try {
      const r = await albumsApi.add(id, list.map(i => i.path))
      notify(t('{n} zum Album „{name}“ hinzugefügt', { n: r.added, name: albums.find(a => a.id === id)?.name ?? '' }))
      loadAlbums()
    } catch (e) { fail(e) }
  }
  const takeFromAlbum = async (list: Item[]) => {
    if (album == null) return
    try { await albumsApi.take(album, list.map(albumKey)); load(); loadAlbums() } catch (e) { fail(e) }
  }
  const addToAlbumMenu = (list: Item[]) => (
    <Menu>
      <MenuTrigger disableButtonEnhancement><MenuItem icon={<ImageMultiple20Regular />}>{t('Zu Album hinzufügen')}</MenuItem></MenuTrigger>
      <MenuPopover><MenuList>
        {fixedAlbums.map(a => <MenuItem key={a.id} onClick={() => { addToAlbum(a.id, list); setCtx(null) }}>{a.name}</MenuItem>)}
        {fixedAlbums.length > 0 && <MenuDivider />}
        <MenuItem icon={<Add16Regular />} onClick={() => { setAlbumDialog({ kind: 'new', add: list.map(i => i.path) }); setCtx(null) }}>{t('Neues Album …')}</MenuItem>
      </MenuList></MenuPopover>
    </Menu>
  )
  // Album extern zeigen (öffentlicher Link)
  const [linkAlbum, setLinkAlbum] = useState<AlbumInfo | null>(null)
  // Smart-Album wie in Lightroom: Name und Bedingungen in einem Dialog (neu oder Regeln eines bestehenden ändern)
  const [smartDialog, setSmartDialog] = useState<{ id: number | null } | null>(null)
  const smartDialogEl = smartDialog && (() => {
    const a = smartDialog.id != null ? albums.find(x => x.id === smartDialog.id) : null
    let initial: SmartRule | null = null
    try { initial = a?.rule ? JSON.parse(a.rule) : filterOn && album == null ? { cols: filterCols, sel: photoSel } as SmartRule : null } catch { initial = null }
    return <SmartAlbumDialog title={a ? t('Smart-Album „{name}“', { name: a.name }) : t('Neues Smart-Album')} initialName={a?.name ?? ''} initial={initial}
      onClose={() => setSmartDialog(null)}
      onSave={async (name, rule) => {
        try {
          if (a) { await albumsApi.update(a.id, { name, rule }); load() }
          else { const { id } = await albumsApi.create(name, 'smart', rule); setAlbum(id) }
          loadAlbums(); setSmartDialog(null)
        } catch (e) { fail(e) }
      }} />
  })()
  const albumNamed = async (name: string | null) => {
    const d = albumDialog
    setAlbumDialog(null)
    if (!name || !d) return
    try {
      if (d.kind === 'new') {
        const { id } = await albumsApi.create(name)
        if (d.add?.length) await albumsApi.add(id, d.add)
        loadAlbums()
        if (!d.add?.length) setAlbum(id)
        else notify(t('Album „{name}“ angelegt', { name }))
      } else if (d.kind === 'smart') {
        const { id } = await albumsApi.create(name, 'smart', JSON.stringify({ cols: filterCols, sel: photoSel }))
        loadAlbums(); setPhotoSel([[], [], [], []]); setAlbum(id)
      } else if (d.kind === 'rename' && album != null) { await albumsApi.update(album, { name }); loadAlbums() }
    } catch (e) { fail(e) }
  }
  const albumMenu = albumNow && (
    <Menu>
      <MenuTrigger disableButtonEnhancement><Button size="small" appearance="subtle" icon={<MoreHorizontal20Regular />} aria-label={t('Album')} /></MenuTrigger>
      <MenuPopover><MenuList>
        {albumNow.mine && <MenuItem onClick={() => setAlbumDialog({ kind: 'rename' })}>{t('Umbenennen …')}</MenuItem>}
        {albumNow.mine && <MenuItem icon={<People20Regular />} onClick={() => setAlbumDialog({ kind: 'share' })}>{t('Teilen …')}</MenuItem>}
        {albumNow.mine && <MenuItem icon={<Globe20Regular />} onClick={() => setLinkAlbum(albumNow)}>{t('Öffentlich zeigen …')}</MenuItem>}
        {albumNow.mine && albumNow.kind === 'smart' && <MenuItem icon={<Sparkle20Regular />} onClick={() => setSmartDialog({ id: albumNow.id })}>{t('Regeln bearbeiten …')}</MenuItem>}
        {albumNow.mine && albumNow.kind === 'smart' && filterOn && <MenuItem onClick={async () => {
          try { await albumsApi.update(albumNow.id, { rule: JSON.stringify({ cols: filterCols, sel: photoSel }) }); setPhotoSel([[], [], [], []]); load(); loadAlbums() } catch (e) { fail(e) }
        }}>{t('Filter übernehmen')}</MenuItem>}
        <MenuItem icon={<Chat20Regular />} onClick={() => sendToChat([{ type: 'album', fileId: albumNow.id }])}>{t('Im Chat zeigen …')}</MenuItem>
        <MenuDivider />
        <MenuItem icon={<Delete20Regular />} onClick={async () => {
          if (!await askConfirm('', albumNow.mine ? t('Album „{name}“ löschen? Die Fotos bleiben, wo sie sind.', { name: albumNow.name }) : t('Album „{name}“ verlassen? Deine Fotos darin werden herausgenommen.', { name: albumNow.name }))) return
          try { await albumsApi.remove(albumNow.id); setAlbum(null); loadAlbums() } catch (e) { fail(e) }
        }}>{albumNow.mine ? t('Album löschen') : t('Album verlassen')}</MenuItem>
      </MenuList></MenuPopover>
    </Menu>
  )

  // ---------------- Menues ----------------
  const [ctx, setCtx] = useState<{ x: number; y: number; kind: 'items' | 'empty' | 'folder' | 'nav'; folder?: string; nav?: Special | 'chat' | 'withme' | 'tresor' } | null>(null)
  const menuItems = (list: Item[]) => {
    const single = list.length === 1 ? list[0] : null
    // Aus einem Galerie-Ordner: nur ansehen und herunterladen - ändern geht im externen Ordner selbst
    if (list.some(isExt)) return (
      <MenuList>
        {single && <MenuItem icon={<FolderOpen20Regular />} onClick={() => open(single)}>{t('Öffnen')}</MenuItem>}
        <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download(list)}>{t('Herunterladen')}</MenuItem>
        {album != null && list.every(isAlb) && albums.find(a => a.id === album)?.kind === 'fixed' && (albums.find(a => a.id === album)?.mine || list.every(i => albumMine[i.path])) &&
          <MenuItem icon={<Delete20Regular />} onClick={() => takeFromAlbum(list)}>{t('Aus dem Album nehmen')}</MenuItem>}
        {album == null && addToAlbumMenu(list)}
        {list.every(extWritable) && list.some(isPhoto) && <MenuItem icon={<Wand20Regular />} onClick={() => setAutoFor(list)}>{t('Automatisch verbessern …')}</MenuItem>}
        {deletable(list) && <MenuItem icon={<Delete20Regular />} secondaryContent={t('Entf')} onClick={() => remove(list)}>{t('Löschen')}</MenuItem>}
      </MenuList>
    )
    // In einem mit mir geteilten Ordner: Teilen, Link und Favoriten sind Sache des Besitzers; ohne Bearbeitungsrecht nichts, was ändert
    const mine = !list.some(i => inWithMe(i.path, anyShared) || isEncrypted(i.path))
    const change = canChange(list)
    const keep = keepable(list)
    return (
      <MenuList>
        {single && <MenuItem icon={<FolderOpen20Regular />} onClick={() => open(single)}>{t('Öffnen')}</MenuItem>}
        {/* Eigener Cryptomator-Tresor irgendwo in den Dateien: von hier schließen bzw. öffnen */}
        {single?.vault && (single.vaultOpen
          ? <MenuItem icon={<LockClosed20Regular />} onClick={() => api.post('/api/files/vault/lock', { path: single.path }).then(() => refresh.current()).catch(fail)}>{t('Tresor schließen')}</MenuItem>
          : <MenuItem icon={<LockOpen20Regular />} onClick={() => open(single)}>{t('Tresor öffnen …')}</MenuItem>)}
        {single && view.kind === 'photos' && !single.isDir && <MenuItem icon={<ImageOff20Regular />} onClick={() => setExcludeFrom(single.path)}>{t('Ordner aus der Galerie ausschließen …')}</MenuItem>}
        {view.kind === 'photos' && album == null && list.every(i => !i.isDir) && addToAlbumMenu(list)}
        {change && list.some(isPhoto) && <MenuItem icon={<Wand20Regular />} onClick={() => setAutoFor(list)}>{t('Automatisch verbessern …')}</MenuItem>}
        {infoable(list) && <MenuItem icon={<Edit20Regular />} onClick={() => setInfoFor(list)}>{t('Titel, Stichwörter, Bewertung …')}</MenuItem>}
        {single && officeMode(single, office) === 'edit' && !isCmv(single.path) && <MenuItem icon={<DocumentEdit20Regular />} onClick={() => setEditing({ path: single.path, name: single.name })}>{t('Im Browser bearbeiten')}</MenuItem>}
        {single && flat && <MenuItem icon={<Folder20Regular />} onClick={() => { openFolder(parentOf(single.path)); setTimeout(() => setSelected([single.path]), 300) }}>{t('Speicherort öffnen')}</MenuItem>}
        {keep && <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download(list)}>{list.length > 1 || single?.isDir ? t('Als ZIP herunterladen') : t('Herunterladen')}</MenuItem>}
        {keep && !list.some(isAlb) && <MenuItem icon={<Cart20Regular />} onClick={() => basket.add(list)}>{t('In den Downloadkorb')}</MenuItem>}
        {single && mine && <MenuItem icon={<Share20Regular />} onClick={() => setShare(single)}>{t('Teilen …')}</MenuItem>}
        {!single && mine && list.length > 1 && <MenuItem icon={<Share20Regular />} onClick={() => setShareMany(list)}>{t('Mit Personen teilen …')}</MenuItem>}
        {single && mine && <MenuItem icon={<Link20Regular />} onClick={() => quickLink(single)}>{t('Öffentlichen Link erstellen')}</MenuItem>}
        {mine && list.every(i => i.fileId) && <MenuItem icon={<Chat20Regular />} onClick={() => sendToChat(list.map(i => ({ type: i.isDir ? 'folder' as const : 'file' as const, fileId: i.fileId! })))}>{t('Im Chat …')}</MenuItem>}
        {single && mine && <MenuItem icon={single.favorite ? <Star20Filled color="#E8A33D" /> : <Star20Regular />} onClick={() => favorite(single, !single.favorite)}>
          {single.favorite ? t('Aus Favoriten entfernen') : t('Zu Favoriten hinzufügen')}</MenuItem>}
        {single?.isDir && mine && !isExt(single) && !isCmv(single.path) && (
          <MenuItem icon={single.davHidden ? <Cloud20Regular /> : <CloudOff20Regular />} onClick={() => davHide(single, !single.davHidden)}>
            {single.davHidden ? t('In WebDAV und Nextcloud zeigen') : t('In WebDAV und Nextcloud ausblenden')}</MenuItem>
        )}
        {featureOn('dms') && mine && canChange(list) && list.some(i => !i.isDir) && !list.some(isAlb) && <MenuItem icon={<DocumentBulletList20Regular />} onClick={() => toDms(list.filter(i => !i.isDir))}>{t('Ins DMS aufnehmen')}</MenuItem>}
        <MenuDivider />
        {change && <MenuItem icon={<Cut20Regular />} secondaryContent={t('Strg+X')} onClick={() => toClip(list, true)}>{t('Ausschneiden')}</MenuItem>}
        {keep && <MenuItem icon={<Copy20Regular />} secondaryContent={t('Strg+C')} onClick={() => toClip(list, false)}>{t('Kopieren')}</MenuItem>}
        {single?.isDir && clip && writableAt(single.path) && <MenuItem icon={<ClipboardPaste20Regular />} onClick={() => paste(single.path)}>{t('Hier einfügen')}</MenuItem>}
        {single && change && <MenuItem icon={<Rename20Regular />} secondaryContent="F2" onClick={() => rename(single)}>{t('Umbenennen')}</MenuItem>}
        {change && <MenuItem icon={<FolderArrowRight20Regular />} onClick={() => setPicker({ mode: 'move', paths: list.map(i => i.path) })}>{t('Verschieben nach …')}</MenuItem>}
        {keep && <MenuItem icon={<Copy20Regular />} onClick={() => setPicker({ mode: 'copy', paths: list.map(i => i.path) })}>{t('Kopieren nach …')}</MenuItem>}
        {change && <MenuDivider />}
        {change && <MenuItem icon={<Delete20Regular />} secondaryContent={t('Entf')} onClick={() => remove(list)}>{t('Löschen')}</MenuItem>}
        {/* Mit mir Geteiltes: das Geteilte selbst, in der Galerie auch aus einem Foto eines geteilten Ordners heraus */}
        {list.every(i => isMountRoot(i.path) || (photosView && mountOf(i.path))) && <MenuItem icon={<EyeOff20Regular />} onClick={() => leaveShared(list)}>{list.every(i => isMountRoot(i.path)) ? t('Nicht mehr anzeigen') : t('Geteilten Ordner nicht mehr anzeigen')}</MenuItem>}
        <MenuItem icon={<Info20Regular />} onClick={() => { setSelected(list.map(i => i.path)); setDetails(true) }}>{t('Details')}</MenuItem>
      </MenuList>
    )
  }
  const readOnly = path !== null && !writableAt(path)
  // Chat: hier landet nur, was im Chat mit „Speichern“ abgelegt wird - kein Hochladen, Anlegen, Einfügen oder Hineinziehen
  const inChat = path !== null && (path === CHAT_DIR || path.startsWith(CHAT_DIR + '/'))
  const noAdd = readOnly || inChat
  const emptyMenu = (
    <MenuList>
      {!noAdd && <>
        <MenuItem icon={<FolderAdd20Regular />} onClick={() => mkdir()}>{t('Neuer Ordner')}</MenuItem>
        <MenuItem icon={<DocumentAdd20Regular />} onClick={() => newText('.txt')}>{t('Neue Textdatei')}</MenuItem>
        {officeNew}
        <MenuItem icon={<ArrowUpload20Regular />} onClick={() => upload.current?.click()}>{t('Dateien hochladen')}</MenuItem>
        <MenuItem icon={<FolderArrowUp20Regular />} onClick={() => uploadDir.current?.click()}>{t('Ordner hochladen')}</MenuItem>
        {clip && <MenuItem icon={<ClipboardPaste20Regular />} secondaryContent={t('Strg+V')} onClick={() => paste()}>{t('Einfügen')}</MenuItem>}
        <MenuDivider />
      </>}
      <MenuItem onClick={() => setSelected(items.map(i => i.path))} secondaryContent={t('Strg+A')}>{t('Alle auswählen')}</MenuItem>
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
  useHistoryClose(!!viewer, () => setViewer(null))
  // Beim Blättern im Betrachter wandert die Adresse mit, im eigenen Verlaufseintrag des Betrachters (darum nach useHistoryClose);
  // zu: wieder die Galerie. So führt Zurück aus dem Foto in die Galerie, nicht in den Bereich davor.
  useEffect(() => {
    if (!gallery) return
    if (viewerPath) { const h = '#/gallery/photo/' + encodeURIComponent(viewerPath); if (location.hash !== h) history.replaceState(history.state, '', h) }
    else if (photoHash()) {
      if (backOut.current) {
        // Noch ein Schritt zurück, über den Eintrag mit der Adresse des Fotos hinweg. Schließt der Betrachter selbst, geht er
        // erst noch einen Schritt zurück (useHistoryClose) - danach; hat Zurück ihn geschlossen, sofort
        backOut.current = false
        if (history.state?.dmBack) window.addEventListener('popstate', () => history.back(), { once: true })
        else history.back()
      }
      else history.replaceState(history.state, '', '#/gallery')
    }
  }, [gallery, viewerPath]) // eslint-disable-line react-hooks/exhaustive-deps
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
    {path !== null && !noAdd && <>
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
    <RibbonButton icon={<ArrowDownload20Regular />} label={t('Herunterladen')} disabled={!sel.length || !keepable(sel)} onClick={() => download(sel)} />
    <RibbonButton icon={<Share20Regular />} label={t('Teilen')} disabled={!sel.length || sel.some(i => inWithMe(i.path, anyShared) || isEncrypted(i.path))} onClick={() => one ? setShare(one) : setShareMany(sel)} />
    <RibbonButton icon={<Rename20Regular />} label={t('Umbenennen')} disabled={!one || !canChange([one])} onClick={() => one && rename(one)} />
    <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!deletable(sel)} onClick={() => remove(sel)} />
    {photosView && <RibbonButton icon={<DeleteDismiss20Regular />} label={t('Abgelehnte löschen')} disabled={!tlOn} onClick={removeRejected} />}
    <RibbonDivider />
    <RibbonButton icon={<Cut20Regular />} label={t('Ausschneiden')} disabled={!sel.length || !canChange(sel)} onClick={() => toClip(sel, true)} />
    <RibbonButton icon={<Copy20Regular />} label={t('Kopieren')} disabled={!sel.length || !keepable(sel)} onClick={() => toClip(sel, false)} />
    <RibbonButton icon={<ClipboardPaste20Regular />} label={t('Einfügen')} disabled={!clip || path === null || noAdd} onClick={() => paste()} />
    <RibbonButton icon={<FolderArrowRight20Regular />} label={t('Verschieben nach')} disabled={!sel.length || !canChange(sel)} onClick={() => setPicker({ mode: 'move', paths: sel.map(i => i.path) })} />
    <RibbonButton icon={<ArrowUndo20Regular />} label={t('Rückgängig')} disabled={!undos.current.length} onClick={undo} />
    <RibbonDivider />
    <RibbonButton icon={one?.favorite ? <Star20Filled color="#E8A33D" /> : <Star20Regular />} label={t('Favorit')} disabled={!one || inWithMe(one.path, anyShared)} pressed={!!one?.favorite}
      onClick={() => one && favorite(one, !one.favorite)} />
    <RibbonButton icon={<Wand20Regular />} label={t('Verbessern')} disabled={!sel.some(isPhoto) || !(canChange(sel) || sel.every(extWritable))} onClick={() => setAutoFor(sel)} />
    <RibbonButton icon={<Info20Regular />} label={t('Details')} pressed={detailsOpen} onClick={() => setDetails(!detailsOpen)} />
    <RibbonDivider />
    <RibbonButton icon={<DocumentSearch20Regular />} label={t('Inhalte durchsuchen')} onClick={() => setContentSearch(true)} />
    <BasketButton />
    {inTresor && tresor?.open && <TresorDownloadButton />}
    {/* Tresor schließen: der System-Tresor oder der eigene Cryptomator-Tresor, in dem man gerade steht */}
    {((inTresor && tresor?.open) || cmvRoot) && <>
      <RibbonDivider />
      <RibbonButton icon={<LockClosed20Regular />} label={t('Tresor schließen')} onClick={async () => {
        try {
          if (cmvRoot) {
            await api.post('/api/files/vault/lock', { path: cmvRoot })
            // Tresor in einem externen Ordner (Wurzel „x:Kennung/Pfad“): zurück in diesen Ordner
            if (cmvRoot.startsWith('x:')) { const [m, ...rest] = cmvRoot.slice(2).split('/'); setExt({ mount: m, path: rest.join('/') }) }
            else openFolder(cmvRoot)
          }
          else { await closeTresor(); openFolder('') }
        } catch (e) { fail(e) }
      }} />
    </>}
  </>
  const viewRibbon = <>
    {view.kind !== 'photos' && <RibbonButton icon={<AppsList20Regular />} label={t('Details')} pressed={viewMode === 'details'} onClick={() => update({ fileView: 'details' })} />}
    {view.kind !== 'photos' && <RibbonButton icon={<Grid20Regular />} label={t('Kacheln')} pressed={viewMode === 'tiles'} onClick={() => update({ fileView: 'tiles' })} />}
    {view.kind !== 'photos' && !extMount && <ColumnMenu cols={colCfg} onChange={c => saveCols(c)} labels={view.kind === 'recent' && !searching ? { modified: t('Verwendet') } : undefined}
      trigger={<MenuTrigger disableButtonEnhancement><RibbonButton icon={<ColumnTriple20Regular />} label={t('Spalten')} disabled={viewMode !== 'details'} /></MenuTrigger>} />}
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ArrowSort20Regular />} label={t('Sortieren')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ sort: [sort], dir: [desc ? 'desc' : 'asc'], fav: prefs.fileFavTop ? ['top'] : [], folder: prefs.fileFolderTop !== false ? ['top'] : [] }}
          onCheckedValueChange={(_, d) => d.name === 'fav' ? update({ fileFavTop: d.checkedItems.includes('top') }) : d.name === 'folder' ? update({ fileFolderTop: d.checkedItems.includes('top') }) : d.name === 'sort' ? setSort({ sort: d.checkedItems[0] as any }) : setSort({ desc: d.checkedItems[0] === 'desc' })}>
          <MenuItemRadio name="sort" value="name">{t('Name')}</MenuItemRadio>
          <MenuItemRadio name="sort" value="modified">{t('Geändert')}</MenuItemRadio>
          {!extMount && <MenuItemRadio name="sort" value="taken">{t('Aufnahmedatum')}</MenuItemRadio>}
          <MenuItemRadio name="sort" value="size">{t('Größe')}</MenuItemRadio>
          {!photosView && !extMount && <MenuItemRadio name="sort" value="kind">{t('Art')}</MenuItemRadio>}
          <MenuDivider />
          <MenuItemRadio name="dir" value="asc">{t('Aufsteigend')}</MenuItemRadio>
          <MenuItemRadio name="dir" value="desc">{t('Absteigend')}</MenuItemRadio>
          {!photosView && <>
            <MenuDivider />
            <MenuItemCheckbox name="folder" value="top">{t('Ordner oben')}</MenuItemCheckbox>
            {!extMount && <MenuItemCheckbox name="fav" value="top">{t('Favoriten oben')}</MenuItemCheckbox>}
          </>}
        </MenuList>
      </MenuPopover>
    </Menu>
    {photosView && <RibbonButton icon={prefs.photoFit ? <ScaleFit20Regular /> : <ScaleFill20Regular />} label={t('Eingepasst')} pressed={!!prefs.photoFit} onClick={() => update({ photoFit: !prefs.photoFit })} />}
    {photosView && <RibbonButton icon={<Stack20Regular />} label={t('Stapeln')} pressed={prefs.photoStack ?? true} onClick={() => update({ photoStack: !(prefs.photoStack ?? true) })} />}
    {photosView && <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<TextBulletListSquare20Regular />} label={t('Bildinfos')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ info: tileInfo }} onCheckedValueChange={(_, d) => update({ photoTileInfo: d.checkedItems.slice(0, 3) })}>
          {Object.entries(TILE_INFOS).map(([k, x]) => (
            <MenuItemCheckbox key={k} name="info" value={k} disabled={tileInfo.length >= 3 && !tileInfo.includes(k)}>{x.label()}</MenuItemCheckbox>
          ))}
        </MenuList>
        {/* Eigener Schalter, zählt nicht zu den drei Angaben */}
        <MenuDivider />
        <MenuList checkedValues={{ frame: prefs.photoTileLabel ? ['label'] : [] }} onCheckedValueChange={(_, d) => update({ photoTileLabel: d.checkedItems.includes('label') })}>
          <MenuItemCheckbox name="frame" value="label">{t('Farbmarkierung als Rahmen')}</MenuItemCheckbox>
        </MenuList>
      </MenuPopover>
    </Menu>}
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ZoomIn20Regular />} label={t('Kachelgröße')} disabled={viewMode !== 'tiles' && view.kind !== 'photos'} /></MenuTrigger>
      <MenuPopover>
        {/* Stufenlos wie in Lightroom */}
        <div style={{ padding: '10px 14px', width: 260 }}>
          <Text size={200} block>{t('Kachelgröße: {px} px', { px: tileNum })}</Text>
          <Slider min={72} max={480} step={4} value={tileNum} onChange={(_, d) => update({ fileTilePx: d.value })} style={{ width: '100%' }} aria-label={t('Kachelgröße')} />
          {photosView && <Text size={100} block style={{ color: tokens.colorNeutralForeground3 }}>{t('Ab {px} px stehen die Bildinfos auf dem Bild.', { px: OVERLAY_PX })}</Text>}
        </div>
      </MenuPopover>
    </Menu>
    {!photosView && <RibbonButton icon={showHidden ? <Eye20Regular /> : <EyeOff20Regular />} label={t('Versteckte Dateien')} pressed={showHidden} onClick={() => update({ fileHidden: !showHidden })} />}
    {!extMount && <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<GroupList20Regular />} label={t('Gruppieren')} /></MenuTrigger>
      <MenuPopover>
        {photosView ? (
          <MenuList checkedValues={{ group: [photoGroup] }} onCheckedValueChange={(_, d) => update({ photoGroup: d.checkedItems[0] as any })}>
            <MenuItemRadio name="group" value="day">{t('Nach Datum')}</MenuItemRadio>
            <MenuItemRadio name="group" value="month">{t('Nach Monat')}</MenuItemRadio>
            <MenuItemRadio name="group" value="year">{t('Nach Jahr')}</MenuItemRadio>
            <MenuItemRadio name="group" value="source">{t('Nach Quelle')}</MenuItemRadio>
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
    </Menu>}
    {photosView && <RibbonButton icon={<Edit20Regular />} label={t('Angaben')} disabled={!infoable(sel)} onClick={() => setInfoFor(sel)} />}
    {photosView && <RibbonButton icon={<Globe20Regular />} label={t('Atlas')} pressed={atlas} onClick={() => setAtlas(a => !a)} />}
    {photosView && <RibbonButton icon={<Filter20Regular />} label={t('Filterleiste')} pressed={!!prefs.photoFilter} onClick={() => update({ photoFilter: !prefs.photoFilter })} />}
    {photosView && <RibbonButton icon={<ImageMultiple20Regular />} label={t('Galerie-Ordner')} onClick={() => setGalleryFolders(true)} />}
    {photosView && <RibbonButton icon={<ImageOff20Regular />} label={t('Ausgeschlossene Ordner')} onClick={() => setExcludes(true)} />}
    {photosView && featureOn('importprofiles') && <RibbonButton icon={<ArrowImport20Regular />} label={t('Importprofile')} onClick={() => setImportProfiles(true)} />}
    <RibbonDivider />
    {!gallery && <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />}
    {!extMount && <RibbonButton icon={<Info20Regular />} label={t('Detailbereich')} pressed={detailsOpen} onClick={() => setDetails(!detailsOpen)} />}
    {/* WebDAV gehört zur Dateiablage (Ordner am PC einbinden), nicht in die Galerie */}
    {!gallery && <RibbonDivider />}
    {!gallery && <>
      <RibbonButton icon={<ArrowLeft20Regular />} label={t('Zurück')} disabled={!hist.current.back.length} key={'b' + histTick} onClick={() => histGo(-1)} />
      <RibbonButton icon={<ArrowRight20Regular />} label={t('Vor')} disabled={!hist.current.fwd.length} key={'f' + histTick} onClick={() => histGo(1)} />
    </>}
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
  const markBadges = (i: Item) => (i.favorite || i.shared || i.sharedWith || i.owner || i.davHidden) && (
    <span className={s.badges}>
      {i.davHidden && <Tooltip content={t('Für WebDAV und Nextcloud ausgeblendet')} relationship="label"><CloudOff16Regular /></Tooltip>}
      {i.favorite && <Tooltip content={t('Favorit')} relationship="label"><Star16Filled color="#E8A33D" /></Tooltip>}
      {i.shared && <Tooltip content={t('Über Link geteilt')} relationship="label"><Link16Regular /></Tooltip>}
      {i.sharedWith && <Tooltip content={t('Mit Personen geteilt')} relationship="label"><People16Regular /></Tooltip>}
      {i.owner && <Tooltip content={i.showOnly ? t('Von {owner} gezeigt – nur ansehen', { owner: i.owner }) : i.canWrite ? t('Von {owner} geteilt – du darfst bearbeiten', { owner: i.owner }) : t('Von {owner} geteilt – nur lesen', { owner: i.owner })} relationship="label"><People16Regular /></Tooltip>}
    </span>
  )
  const recentView = view.kind === 'recent' && !searching
  const sortable = !(view.kind === 'photos' && !searching)
  const headSort: SortKey = recentView ? recentSort?.key ?? ('' as SortKey) : sort
  const headDesc = recentView ? !!recentSort?.desc : desc
  const headSortBy = (k: SortKey) => recentView
    ? setRecentSort(x => x?.key === k ? { key: k, desc: !x.desc } : { key: k, desc: k === 'modified' || k === 'size' || k === 'created' })
    : sortBy(k)
  const cellOf = (k: ColKey, i: Item & { used?: string }, when: string): React.ReactNode => {
    switch (k) {
      case 'modified': return fmtWhen(when, locale)
      case 'created': return i.created ? fmtWhen(i.created, locale) : ''
      case 'kind': return kindLabel(kindOf(i))
      case 'ext': return i.isDir ? '' : extOf(i.name)
      case 'mime': return i.isDir ? '' : i.mime ?? ''
      case 'size': return i.isDir ? '' : fmtSize(i.size)
      case 'place': return extPlace(i.path)?.label ?? '/' + parentOf(i.path)
      case 'shared': return [i.sharedWith && t('Personen'), i.shared && t('Link'), i.owner && t('von {owner}', { owner: i.owner })].filter(Boolean).join(', ')
      case 'favorite': return i.favorite ? <Star16Filled color="#E8A33D" aria-label={t('Favorit')} /> : null
    }
    const m = metaOf(i)
    switch (k) {
      case 'taken': return m?.taken ? fmtWhen(new Date(m.taken).toISOString(), locale) : ''
      case 'camera': return m?.camera ?? ''
      case 'lens': return m?.lens ?? ''
      case 'exposure': return exposureLine(m)
      case 'dims': return dimsOf(m)
      case 'duration': return m?.duration ? fmtDuration(m.duration) : ''
      case 'rating': return m?.rating ? '★'.repeat(m.rating) : ''
      case 'keywords': return m?.keywords?.split('\n').join(', ') ?? ''
    }
  }
  const emptyText = () => searching ? t('Nichts gefunden.')
    : view.kind === 'recent' ? t('Noch nichts verwendet.')
      : view.kind === 'favorites' ? t('Noch keine Favoriten. Rechtsklick auf eine Datei oder einen Ordner → „Zu Favoriten hinzufügen“.')
        : view.kind === 'photos' ? (filterOn ? t('Keine Bilder passen zum Filter.') : t('Noch keine Fotos. Fotos, die du unter Dateien hochlädst, erscheinen hier.'))
          : filter ? t('Keine Einträge dieser Art in diesem Ordner.')
          : inChat ? t('Noch nichts gespeichert. Hier landen Fotos und Dateien, die du im Chat mit „Speichern“ ablegst.')
          : t('Leer. Dateien hierher ziehen oder „Hochladen“.')
  const list = () => {
    if (view.kind === 'trash') return <TrashList trash={trash} tiles={viewMode === 'tiles'} search={search} onSearch={setSearch} />
    if (view.kind === 'shared') return <LinksList links={links} notify={m => notify(m)} fail={fail}
      onOpen={(p, isDir) => isDir ? openFolder(p) : (openFolder(parentOf(p)), setTimeout(() => setSelected([p]), 300))}
      onManage={l => l.item && setShare(l.item)} />
    if (!data) return <Spinner />
    if (tlOn ? (tl.state?.shown ?? 0) === 0 : items.length === 0) return <>
      <Text className={s.empty}>{emptyText()}</Text>
      {view.kind === 'photos' && !filterOn && !album && <Button appearance="primary" style={{ marginLeft: 4 }} onClick={() => { location.hash = '#/files' }}>{t('Zu den Dateien')}</Button>}
    </>
    // Eine Kachel (Galerie und Ordner)
    const tileOf = (i: Item & { used?: string }) => {
          const isSel = selected.includes(i.path)
          return (
            <div key={i.path} data-path={i.path} className={mergeClasses(s.tile, photosView && s.tilePhoto, isSel && s.tileSel, overRow === i.path && s.target, clip?.cut && clip.paths.includes(i.path) && s.cut)}
              aria-selected={isSel} title={i.isDir ? i.name : [i.name, `${fmtSize(i.size)} · ${new Date(takenOf(i, metaOf(i))).toLocaleString(locale)}`, metaOf(i)?.camera, exposureLine(metaOf(i))].filter(Boolean).join('\n')}
              draggable={!touch} onDragStart={e => dragStart(e, i)} {...dropTarget(i)}
              onDoubleClick={() => open(i)} onClick={e => click(e, i)} onContextMenu={e => ctxMenuFor(e, i, isSel)}>
              <span data-tilecheck className={mergeClasses(s.tileCheck, photosView && !touch && !isSel && selected.length === 0 && s.tileCheckQuiet)} onClick={e => { e.stopPropagation(); anchor.current = i.path; setSelected(sl => isSel ? sl.filter(p => p !== i.path) : [...sl, i.path]) }}>
                <Checkbox checked={isSel} aria-label={t('Auswählen')} />
              </span>
              {(i.favorite || i.shared || i.sharedWith || i.owner || (photosView && i.etag?.includes('~e'))) && <span className={s.tileBadge}>
                {/* Bearbeitet (nicht-destruktiv): das Original ist unverändert */}
                {photosView && i.etag?.includes('~e') && <Tooltip content={t('Bearbeitet')} relationship="label"><Edit16Regular className={s.editedBadge} /></Tooltip>}
                {i.favorite && <Star16Filled color="#E8A33D" />}{i.shared && <Link16Regular />}{(i.sharedWith || i.owner) && <People16Regular />}</span>}
              <div className={mergeClasses(s.thumb, photosView && metaOf(i)?.pick === -1 && s.rejected)} style={photosView && prefs.photoTileLabel && labelColor(metaOf(i)?.label) ? { boxShadow: `0 0 0 3px ${labelColor(metaOf(i)?.label)}` } : undefined}>
                {i.isDir && covers[i.path]?.length ? (
                  <div style={{ position: 'relative', width: '100%', height: '100%' }}>
                    <div className={s.collage}>
                      {covers[i.path].slice(0, 4).map(c => <Thumb key={c.path} item={c} source={ownSource} size={128} className={s.thumbImg} />)}
                    </div>
                    <Folder20Filled className={s.collageMark} />
                  </div>
                ) : hasThumb(i) ? <Thumb item={i} source={galleryTileSource} size={thumbPxFor(tileNum, extOffline(i))} className={s.thumbImg} fallbackLarge={!extOffline(i)} fit={photosView && prefs.photoFit ? 'contain' : 'cover'} /> : <KindIcon item={i} large />}
                {i.stack?.length ? <span className={mergeClasses(s.stackBadge, (i.favorite || i.shared || i.sharedWith || i.owner || (photosView && i.etag?.includes('~e'))) && s.stackBadgeLow)} title={i.stack.map(x => x.name).join(', ')}>{[...new Set(i.stack.map(x => extOf(x.name).slice(1).toUpperCase()))].join('+')}</span> : null}
                {photosView && (() => {
                  const m = metaOf(i)
                  if (!m?.pick && !m?.label && !m?.rating) return null
                  return <span className={s.marks} style={i.stack?.length ? { top: 24 } : undefined}>
                    {m.pick === 1 && <span style={{ color: '#4CC38A', fontWeight: 600 }} title={t('Ausgewählt')}>P</span>}
                    {m.pick === -1 && <span style={{ color: '#FF6369', fontWeight: 600 }} title={t('Abgelehnt')}>X</span>}
                    {m.label && <span className={s.markDot} style={{ background: labelColor(m.label) }} />}
                    {m.rating ? <span>{'★'.repeat(m.rating)}</span> : null}
                  </span>
                })()}
                {extOffline(i) && <Tooltip content={t('Der externe Ordner ist gerade nicht erreichbar – nur die Vorschau ist da.')} relationship="label">
                  <span className={s.offBadge}><Prohibited16Filled /></span></Tooltip>}
                {/* Große Kacheln: Bildinfos auf dem Bild, unten in einer hellen Leiste */}
                {infoOnImage && tileInfo.length > 0 && <div className={s.tileOverlay}>
                  {tileInfo.map(k => {
                    const [l, r] = TILE_INFOS[k].value(i, metaOf(i))
                    return <div key={k} className={s.tileInfo} title={[l, r].filter(Boolean).join(' · ')}>
                      <span className={s.tileInfoL}>{l || '\u00a0'}</span>{r && <span className={s.tileInfoR}>{r}</span>}
                    </div>
                  })}
                </div>}
              </div>
              {/* Galerie: Bildinfos unter dem Bild wie eine Bildunterschrift, links und rechts (wie im Kamera-Importer) */}
              {photosView && !infoOnImage && tileInfo.length > 0 && <div className={s.tileInfos}>
                {tileInfo.map(k => {
                  const [l, r] = TILE_INFOS[k].value(i, metaOf(i))
                  return <div key={k} className={s.tileInfo} title={[l, r].filter(Boolean).join(' · ')}>
                    <span className={s.tileInfoL}>{l || '\u00a0'}</span>{r && <span className={s.tileInfoR}>{r}</span>}
                  </div>
                })}
              </div>}
              {!photosView && <span className={s.tileName}>{i.name}</span>}
              {i.snippet && <span className={s.tileSnippet} title={t('Im Inhalt gefunden')}><Snippet text={i.snippet} /></span>}
            </div>
          )
    }
    // Galerie: nur die Blöcke im sichtbaren Bereich, davor und danach leerer Platz in der geschätzten Höhe
    if (windowed) {
      const [a, z] = range
      const shownBlocks = blocks.list.slice(a, z + 1)
      const before = shownBlocks[0]?.off ?? 0
      const last = shownBlocks[shownBlocks.length - 1]
      const after = last ? blocks.total - last.off - last.h : 0
      return (
        <>
          <div style={{ height: before }} />
          {shownBlocks.map(b => b.ph
            // Monat noch nicht geladen: Überschrift und Platz in der richtigen Höhe (kommt beim Stehenbleiben)
            ? <div key={b.key} style={{ height: b.h }}>{b.label && <div className={s.groupHead} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>{b.label} <span className={s.muted}>{b.count}</span></div>}</div>
            : b.head
            ? <div key={b.key} className={s.groupHead} data-grouphead style={{ display: 'flex', alignItems: 'center', gap: 6 }}>{b.label} <span className={s.muted}>{b.count}</span>{groupBox(b.label)}</div>
            : <div key={b.key} className={s.tiles} data-grid style={{ ['--tile' as any]: tilePx }}>{b.items.map(tileOf)}</div>)}
          <div style={{ height: Math.max(0, after) }} />
        </>
      )
    }
    if (viewMode === 'tiles') return (
      <>
        {cappedGroups.map(g => (
          <Fragment key={g.label || 'alle'}>
            {g.label && <div className={s.groupHead} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>{g.label} <span className={s.muted}>{g.total}</span>
              {groupBox(g.label)}</div>}
            <div className={s.tiles} data-grid style={{ ['--tile' as any]: tilePx }}>
              {g.items.map(tileOf)}
            </div>
          </Fragment>
        ))}
        {shown.length > cap && <MoreOnScroll onMore={() => setCap(c => c + TILE_STEP)} />}
      </>
    )
    return (
      <>
      <Table size="small" aria-label={t('Dateien')} style={cols >= 2 ? { minWidth: 36 + (nameWidth ?? 220) + shownCols.reduce((n, c) => n + c.width, 0) + 44 } : undefined}>
        <TableHeader onContextMenu={e => { if (cols >= 2) { e.preventDefault(); e.stopPropagation(); setColMenu({ x: e.clientX, y: e.clientY }) } }}>
          <TableRow>
            <TableHeaderCell style={{ width: 36 }}>
              <Checkbox checked={sel.length === 0 ? false : sel.length === items.length ? true : 'mixed'} aria-label={t('Alle auswählen')}
                onChange={() => setSelected(sel.length === items.length ? [] : items.map(i => i.path))} />
            </TableHeaderCell>
            <ColHead label={t('Name')} width={nameWidth} minWidth={220} sortable={sortable} sorted={sortable && headSort === 'name'} desc={headDesc} onSort={() => headSortBy('name')}
              onWidth={w => setWidth('name', w)} onWidthDone={w => commitWidth('name', w)} />
            {shownCols.map(c => {
              const used = c.key === 'modified' && view.kind === 'recent' && !searching
              const k = COLS[c.key].sort
              return (
                <ColHead key={c.key} label={used ? t('Verwendet') : COLS[c.key].label()} width={c.width}
                  sortable={sortable && !!k} sorted={sortable && !!k && headSort === k} desc={headDesc} onSort={() => k && headSortBy(k)}
                  onWidth={w => setWidth(c.key, w)} onWidthDone={w => commitWidth(c.key, w)}
                  moveKey={colCfg.some(x => x.key === c.key) ? c.key : undefined} onMove={moveCol} />
              )
            })}
            {/* Hat auch „Name“ eine feste Breite, nimmt diese leere Spalte den Rest auf - sonst verteilt der Browser ihn auf alle */}
            {filler && <TableHeaderCell aria-hidden />}
            <TableHeaderCell style={{ width: 44 }} />
          </TableRow>
        </TableHeader>
        <TableBody>
          {groups.flatMap(g => [
            ...(g.label ? [<TableRow key={'g:' + g.label} className={s.groupRow}><TableCell colSpan={shownCols.length + (filler ? 4 : 3)}><span className={s.groupHead} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>{g.label} <span className={s.muted}>{g.items.length}</span>{groupBox(g.label)}</span></TableCell></TableRow>] : []),
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
                  <TableCellLayout truncate title={i.name} media={<span className={s.rowIcon}>{hasThumb(i) && kindOf(i) === 'image' ? <Thumb item={i} source={gallerySource} className={s.rowThumb} /> : <KindIcon item={i} />}</span>}
                    description={i.snippet ? <span title={t('Im Inhalt gefunden')}><Snippet text={i.snippet} /></span>
                      : cols < 2 ? [flat ? '/' + parentOf(i.path) : null, fmtWhen(when, locale), i.isDir ? null : fmtSize(i.size)].filter(Boolean).join(' · ') : undefined}>
                    {i.name}{markBadges(i)}
                  </TableCellLayout>
                </TableCell>
                {shownCols.map(c => (
                  <TableCell key={c.key} className={c.key === 'modified' || c.key === 'size' || c.key === 'created' || c.key === 'favorite' ? undefined : s.muted}
                    style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{cellOf(c.key, i, when)}</TableCell>
                ))}
                {filler && <TableCell />}
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
      {cols >= 2 && <ColumnMenu cols={colCfg} onChange={c => saveCols(c)} at={colMenu} onClose={() => setColMenu(null)} labels={recentView ? { modified: t('Verwendet') } : undefined} />}
      </>
    )
  }

  // Rechtsklick auf die Einträge unter dem Ordnerbaum (Zuletzt verwendet, Favoriten, Chat, Papierkorb, Tresor …)
  const navCtx = (nav: NonNullable<NonNullable<typeof ctx>['nav']>) => (e: React.MouseEvent) => { e.preventDefault(); e.stopPropagation(); setCtx({ x: e.clientX, y: e.clientY, kind: 'nav', nav }) }
  const openNav = (nav: NonNullable<NonNullable<typeof ctx>['nav']>) => {
    if (nav === 'chat') openFolder(CHAT_DIR)
    else if (nav === 'withme') openFolder(WITH_ME)
    else if (nav === 'tresor') openTresor(TRESOR)
    else go({ kind: nav })
    if (mobile) closeSide()
  }
  const emptyTrash = async () => {
    if (!await askConfirm('', t('Papierkorb leeren? Alles darin wird endgültig gelöscht.'))) return
    try { await api.post('/api/files/trash/empty') } catch (e) { fail(e) }
    trash.load(); setTreeVersion(v => v + 1); loadUsage()
  }
  const navMenu = (nav: NonNullable<NonNullable<typeof ctx>['nav']>) => <MenuList>
    {nav === 'tresor' && tresor && !tresor.exists
      ? <MenuItem icon={<LockOpen20Regular />} onClick={() => openNav(nav)}>{t('Tresor einrichten …')}</MenuItem>
      : <MenuItem icon={<FolderOpen20Regular />} onClick={() => openNav(nav)}>{t('Öffnen')}</MenuItem>}
    {nav === 'trash' && <MenuItem icon={<Delete20Regular />} onClick={emptyTrash}>{t('Papierkorb leeren')}</MenuItem>}
    {nav === 'tresor' && (tresor?.open
      ? <MenuItem icon={<LockClosed20Regular />} onClick={() => { closeTresor().then(() => { if (inTresor) openFolder('') }).catch(fail) }}>{t('Tresor schließen')}</MenuItem>
      : tresor?.exists && <MenuItem icon={<LockOpen20Regular />} onClick={() => openTresor(TRESOR)}>{t('Tresor öffnen …')}</MenuItem>)}
    {nav === 'tresor' && tresor?.exists && !tresor.oldFormat && (
      <MenuItem icon={tresor.settings?.dav ? <CloudOff20Regular /> : <Cloud20Regular />}
        onClick={() => davHide({ path: 'dmVAULT', name: t('Tresor'), isDir: true, size: 0, mime: null, modified: '' }, !!tresor.settings?.dav).then(() => window.dispatchEvent(new Event(TRESOR_EVENT)))}>
        {tresor.settings?.dav ? t('In WebDAV und Nextcloud ausblenden') : t('In WebDAV und Nextcloud zeigen')}</MenuItem>
    )}
  </MenuList>
  const navEntry = (k: Special, icon: JSX.Element) => (
    <div className={mergeClasses(s.nav, view.kind === k && !ext && s.active)} onClick={() => { go({ kind: k }); if (mobile) closeSide() }} onContextMenu={navCtx(k)}>
      <span className={s.twisty} />{icon}<span className={s.navName}>{viewTitle(k)}</span>
    </div>
  )
  // Mit Kontingent zaehlt alles, was der Benutzer belegt (auch Mails, Kalender, Kontakte, Notizen), sonst nur die Dateiablage
  const total = usage ? (usage.quota > 0 && usage.used != null ? usage.used : usage.usage + usage.trash + usage.versions) : 0
  // Aus dem Betrachter: das Foto auch dann, wenn es nicht in der geladenen Liste steht (über seine Adresse geöffnet, anderer Monat)
  const fromViewer = !one && viewerDetail && selected.length === 1 && selected[0] === viewerDetail.path ? viewerDetail : null
  const detailItem = one ?? fromViewer ?? (sel.length === 0 && path !== null && data?.folder ? data.folder : null)
  // Abgleich beim Ansehen (Galerie-Ordner): Ergebnis gleich in Kacheln, Details und Betrachter
  const applyFresh = (list: Fresh[]) => {
    const hits = list.filter(f => f.changed && !f.gone && f.meta && f.etag != null)
    if (!hits.length) return
    const patches = hits.map(f => ({ path: f.key, meta: f.meta!, etag: f.etag!, size: f.size ?? 0, modified: f.modified ?? '' }))
    const by = new Map(patches.map(p => [p.path, p]))
    tl.patch(patches)
    setExtMeta(m => ({ ...m, ...Object.fromEntries(patches.map(p => [p.path, p.meta])) }))
    // Suche und Alben: die Liste steht nicht in der Zeitleiste
    if (!tlOn) setData(d => d && d.items.some(i => by.has(i.path)) ? { ...d, items: d.items.map(i => { const p = by.get(i.path); return p ? { ...i, etag: p.etag, size: p.size, modified: p.modified } : i }) } : d)
    // Die Datei selbst hat sich geändert: der Betrachter holt das Bild neu
    const files = new Map(hits.filter(f => f.fileChanged).map(f => [f.key, by.get(f.key)!]))
    if (files.size) setViewer(v => v && v.list.some(i => files.has(i.path)) ? { ...v, list: v.list.map(i => { const p = files.get(i.path); return p ? { ...i, etag: p.etag, size: p.size, modified: p.modified } : i }) } : v)
  }
  const applyFreshRef = useRef(applyFresh)
  applyFreshRef.current = applyFresh
  // Das gezeigte Foto (Betrachter oder Detailbereich), nicht die vorgeladenen Nachbarn; das Bild wartet nicht darauf
  const shownPhoto = viewer ? viewer.list[viewer.index] : detailsOpen ? detailItem : null
  useEffect(() => {
    if (!photosView || !shownPhoto || shownPhoto.isDir || !isExt(shownPhoto) || extOffline(shownPhoto)) return
    const h = setTimeout(() => { void freshenPhoto(shownPhoto.path).then(r => { if (r) applyFreshRef.current([r]) }) }, 400)
    return () => clearTimeout(h)
  }, [photosView, shownPhoto?.path]) // eslint-disable-line react-hooks/exhaustive-deps
  // Sichtbare Kacheln aus Galerie-Ordnern abgleichen, wenn das Scrollen steht - immer nur ein Aufruf
  const tileTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const tileBusy = useRef(false)
  const checkTiles = useRef<() => Promise<void>>(async () => {})
  const tilesSoon = useCallback(() => { clearTimeout(tileTimer.current); tileTimer.current = setTimeout(() => void checkTiles.current(), 800) }, [])
  checkTiles.current = async () => {
    const el = listRef.current
    if (!photosView || viewer || tileBusy.current || document.hidden || !el || !el.getClientRects().length) return
    const box = el.getBoundingClientRect()
    const paths = [...el.querySelectorAll<HTMLElement>('[data-path^="ext:"]')].filter(x => { const r = x.getBoundingClientRect(); return r.bottom > box.top && r.top < box.bottom })
      .map(x => x.dataset.path!).filter(p => !extOffline({ path: p }))
    tileBusy.current = true
    let again = false
    try {
      const r = await freshenTiles(paths)
      if (r) { applyFreshRef.current(r.items); again = true }
    } finally { tileBusy.current = false }
    // Es hat gedauert oder nicht alle kamen dran: die jetzt sichtbaren noch einmal ansehen
    if (again) tilesSoon()
  }
  useEffect(() => {
    if (photosView) tilesSoon()
    return () => clearTimeout(tileTimer.current)
  }, [photosView, tl.version, data, viewer == null, tilesSoon]) // eslint-disable-line react-hooks/exhaustive-deps
  // Volltextsuche in Dateiinhalten (Search/TextIndex)
  const [contentSearch, setContentSearch] = useState(false)

  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      {!full && <RibbonHeader area="files" onMenu={toggleSide} start={extMount ? null : start} view={viewRibbon} />}
      {contentSearch && <ContentSearch under={path ?? ''} onGo={p => { setContentSearch(false); openFolder(p) }} onClose={() => setContentSearch(false)} />}
      <div className={s.root}>
        {!gallery && <SidePane as="aside" className={s.side} open={sidePane} onClose={closeSide} width={{ key: 'files.side', def: 240 }}>
          <FolderTree showHidden={showHidden} current={ext || inTresor || isCmv(path) ? null : path} onOpen={p => { openFolder(p); if (mobile) closeSide() }} onDropOn={dropOn} version={treeVersion} listed={listed} exclude={[CHAT_DIR]}
            onContext={(e, f) => setCtx({ x: e.clientX, y: e.clientY, kind: 'folder', folder: f })} />
          <div className={s.navSep} />
          {navEntry('recent', <Clock20Regular />)}
          <div className={mergeClasses(s.nav, view.kind === 'favorites' && !ext && s.active)} onClick={() => { go({ kind: 'favorites' }); if (mobile) closeSide() }} onContextMenu={navCtx('favorites')}>
            <span className={s.twisty} onClick={e => { e.stopPropagation(); toggleFavs() }}>
              {favs.some(f => f.isDir) ? favOpen ? <ChevronDown16Regular /> : <ChevronRight16Regular /> : null}</span>
            <Star20Regular /><span className={s.navName}>{viewTitle('favorites')}</span>
          </div>
          {favOpen && favs.filter(f => f.isDir).map(f => (
            <div key={f.path} className={mergeClasses(s.nav, path === f.path && s.active)} style={{ paddingLeft: 28 }} title={'/' + f.path}
              onClick={() => { openFolder(f.path); if (mobile) closeSide() }}
              onDragOver={e => { if (e.dataTransfer.types.includes(DRAG) || e.dataTransfer.types.includes('Files')) e.preventDefault() }}
              onDrop={e => { e.stopPropagation(); dropOn(e, f.path) }}
              onContextMenu={e => { e.preventDefault(); e.stopPropagation(); setCtx({ x: e.clientX, y: e.clientY, kind: 'folder', folder: f.path }) }}>
              <span className={s.twisty} /><Folder20Regular /><span className={s.navName}>{f.name}</span>
            </div>
          ))}
          {anyShared && <>
            <div className={mergeClasses(s.nav, inWithMe(path ?? '', anyShared) && path === WITH_ME && s.active)}
              onClick={() => { openFolder(WITH_ME); if (mobile) closeSide() }} onContextMenu={navCtx('withme')}>
              <span className={s.twisty} /><People20Regular /><span className={s.navName}>{t(WITH_ME)}</span>
            </div>
            {/* In der Seitenleiste nur Ordner; geteilte Dateien stehen im Ordner „Mit mir geteilt“ */}
            {withMe.filter(m => m.isDir !== false).map(m => (
              <div key={m.id} className={mergeClasses(s.nav, path !== null && (path === m.path || path.startsWith(m.path + '/')) && s.active)} style={{ paddingLeft: 30 }}
                title={t('Von {owner}', { owner: m.owner }) + (m.showOnly ? ' · ' + t('nur ansehen') : m.canWrite ? '' : ' · ' + t('nur lesen'))}
                onClick={() => { openFolder(m.path); if (mobile) closeSide() }}>
                <Folder20Regular /><span className={s.navName}>{m.name}</span>
              </div>
            ))}
          </>}
          <div className={mergeClasses(s.nav, !ext && path !== null && (path === CHAT_DIR || path.startsWith(CHAT_DIR + '/')) && s.active)} title={t('Fotos und Dateien aus dem Chat')}
            onClick={() => { openFolder(CHAT_DIR); if (mobile) closeSide() }} onContextMenu={navCtx('chat')}>
            <span className={s.twisty} /><Chat20Regular /><span className={s.navName}>{t('Chat')}</span>
          </div>
          {navEntry('shared', <LinkSquare20Regular />)}
          {navEntry('trash', <Delete20Regular />)}
          {tresor && featureOn('vault') && <>
            <div className={mergeClasses(s.nav, path === TRESOR && s.active)} onClick={() => openTresor(TRESOR)} onContextMenu={navCtx('tresor')}
              title={tresor.open ? t('Tresor – offen') : tresor.exists ? t('Tresor – geschlossen') : t('Tresor')}>
              <span className={s.twisty} />
              {/* Klick aufs Schloss: offen → schließen, geschlossen → öffnen (wie im DMS) */}
              <span role="button" title={tresor.open ? t('Tresor schließen') : t('Tresor öffnen')} style={{ display: 'inline-flex', cursor: 'pointer' }}
                onClick={e => {
                  e.stopPropagation()
                  // Erst aus dem Tresor heraus, dann schließen - sonst lädt die Ansicht ihn noch einmal und fragt nach dem Passwort
                  if (tresor.open) { if (inTresor) openFolder(''); closeTresor().catch(fail) }
                  else openTresor(TRESOR)
                }}>
                {tresor.open ? <LockOpen20Regular style={{ color: OPEN_COLOR }} /> : <LockClosed20Regular />}
              </span>
              <span className={s.navName}>{t('Tresor')}</span>
            </div>
            {tresor.open && tresorFolders.map(f => (
              <div key={f.path} className={mergeClasses(s.nav, (path === f.path || path?.startsWith(f.path + '/')) && s.active)} style={{ paddingLeft: 28 }} onClick={() => openTresor(f.path)}>
                <span className={s.twisty} /><Folder20Regular /><span className={s.navName}>{f.name}</span>
              </div>
            ))}
          </>}
          {(extMounts.length > 0 || extCanAdd) && <>
            <div className={s.navSep} />
            <div style={{ display: 'flex', alignItems: 'center', padding: '2px 8px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, cursor: 'pointer' }}
              onClick={foldExt} title={extFolded ? t('Aufklappen') : t('Zuklappen')}>
              {extFolded ? <ChevronRight16Regular /> : <ChevronDown16Regular />}
              <span style={{ flex: 1, marginLeft: 4 }}>{t('Externe Ordner')}</span>
              {extCanAdd && <Button size="small" appearance="subtle" icon={<Add16Regular />} aria-label={t('Externen Ordner einbinden')} title={t('Externen Ordner einbinden')} onClick={e => { e.stopPropagation(); setAddExt(true) }} />}
            </div>
            {!extFolded && extMounts.map(m => (
              <div key={m.id} className={mergeClasses(s.nav, ext?.mount === m.id && s.active)} style={m.available === false ? { opacity: 0.5 } : undefined}
                title={m.available === false ? t('Gerade nicht erreichbar') : m.kind === 'webdav' ? t('WebDAV-Ordner – Rechtsklick: umbenennen, Ordner auswählen oder entfernen') : m.kind === 'onedrive' ? t('OneDrive-Ordner – Rechtsklick: umbenennen, Ordner auswählen oder entfernen') : m.kind === 'pcloud' ? t('pCloud-Ordner – Rechtsklick: umbenennen, Ordner auswählen oder entfernen') : t('Ordner auf dem Server')}
                onClick={() => { if (m.available === false) { notify(t('Ordner nicht erreichbar'), t('{name} ist gerade nicht erreichbar.', { name: m.name })); return } setSearch(''); setExt({ mount: m.id, path: '' }); if (mobile) closeSide() }}
                onContextMenu={m.kind !== 'folder' ? e => { e.preventDefault(); setExtSetup({ mount: m }) } : undefined}>
                <span className={s.twisty} />{m.kind === 'webdav' ? <Globe20Regular /> : m.kind === 'onedrive' || m.kind === 'pcloud' ? <Cloud20Regular /> : <HardDrive20Regular />}<span className={s.navName}>{m.name}</span>
                {m.available === false && <Prohibited16Filled color="#E5484D" style={{ flexShrink: 0 }} />}
              </div>
            ))}
          </>}
          <div style={{ flex: 1 }} />
          {usage && (
            <div className={s.foot}>
              {(() => {
                const lim = storage ? storageLimit(storage) : null
                if (!lim || lim.total <= 0) return <span>{t('Belegt: {used}', { used: fmtSize(lim?.used ?? total) })}</span>
                const share = Math.min(1, lim.used / lim.total)
                return <>
                  <ProgressBar value={share} color={share > 0.9 ? 'error' : share > 0.75 ? 'warning' : 'brand'} />
                  <span>{t('{used} von {quota} belegt', { used: fmtSize(lim.used), quota: fmtSize(lim.total) })}</span>
                  {lim.capped && lim.diskFree != null && <span>{t('Kontingent {quota}, auf dem Server noch {free} frei', { quota: fmtSize(lim.quota), free: fmtSize(lim.diskFree) })}</span>}
                </>
              })()}
              {(usage.trash > 0 || usage.versions > 0) && <span>{t('davon Papierkorb {trash}, frühere Versionen {versions}', { trash: fmtSize(usage.trash), versions: fmtSize(usage.versions) })}</span>}
            </div>
          )}
        </SidePane>}
        {arc ? <ArchiveView key={arc.file} file={arc.file} dir={arc.dir} onDir={d => setArc({ file: arc.file, dir: d })}
              onLeave={folder => { setArc(null); openFolder(folder) }} onPending={n => { arcPending.current = { file: arc.file, n } }} />
          : extMount && ext ? <ExternalView key={ext.mount} mount={extMount} path={ext.path} onPath={p => setExt({ mount: ext.mount, path: p })}
            vaultPanel={(root, raw) => <VaultUnlock path={root} onOpen={p => { cmvRoots.add(root); setExt(null); openFolder(p) }} onRaw={raw} />}
            onVaultOpen={root => { cmvRoots.add(root); setExt(null); openFolder(CMV + '/' + root) }} />
          : lockedVault && path && !lockedVault.unlocked ? <VaultUnlock path={path} onOpen={p => { cmvRoots.add(path); openFolder(p) }} onRaw={() => setRawVaults(r => [...r, path])} /> : (
        <div ref={mainRef} className={mergeClasses(s.main, dragging && s.drop)} tabIndex={0} onKeyDown={keys}
          onClick={e => { if (e.target === e.currentTarget) setSelected([]) }}
          onDragOver={e => { if (path !== null && !noAdd && e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragging(true) } }}
          onDragLeave={e => { if (e.currentTarget === e.target) setDragging(false) }}
          onDrop={e => { if (path !== null && !noAdd && e.dataTransfer.types.includes('Files') && !e.dataTransfer.types.includes(DRAG)) dropOn(e, path); else { e.preventDefault(); setDragging(false) } }}>
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
          {arcOpen.length > 0 && view.kind === 'folder' && !searching && (
            <MessageBar intent="warning" style={{ margin: '8px 16px 0' }}>
              <MessageBarBody>
                {t('Nicht gespeicherte Änderungen in Archiven:')}{' '}
                {arcOpen.map((a, n) => <span key={a.path}>{n > 0 && ', '}<a href="#" onClick={e => { e.preventDefault(); setArc({ file: a.path, dir: '' }) }}>{nameOf(a.path)}</a></span>)}
              </MessageBarBody>
            </MessageBar>
          )}
          <div className={s.head}>
            <div className={s.crumbs}>
              {searching ? <span className={s.viewTitle}>{t('Suchergebnisse für „{q}“', { q: term })}</span>
                : view.kind !== 'folder' ? <div style={{ display: 'flex', alignItems: 'center', gap: 16, minWidth: 0, flexWrap: 'wrap' }}>{!photosView && <span className={s.viewTitle}>{viewTitle(view.kind)}</span>}
                  {photosView && <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}>
                    <AlbumPicker albums={albums} current={album} onPick={setAlbum} onNew={() => setAlbumDialog({ kind: 'new' })}
                      onNewSmart={() => setSmartDialog({ id: null })} />{albumMenu}</span>}
                  {photosView && data && <PhotoQuickBar inline value={prefs.photoQuick} onChange={q => update({ photoQuick: q })} />}
                  {photosView && prefs.photoFilter && data && <PhotoFilterTop total={tlOn ? tl.state?.total ?? 0 : sortedItems.length} cols={filterCols} onCols={c => update({ photoFilterCols: c })}
                    sel={photoSel} onSel={setPhotoSel} shown={tlOn ? tl.state?.shown ?? 0 : items.length} pending={tlOn ? 0 : meta?.pending ?? 0}
                    onSaveSmart={album == null ? () => setAlbumDialog({ kind: 'smart' }) : undefined} />}</div> : (
                  <Breadcrumb>
                    {inTresor
                      ? <BreadcrumbItem><BreadcrumbButton icon={<LockOpen20Regular style={{ color: OPEN_COLOR }} />} onClick={() => openFolder(TRESOR)} current={parts.length <= 1}
                          onDragOver={(e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG)) e.preventDefault() }} onDrop={(e: React.DragEvent) => dropOn(e, TRESOR)}>{t('Tresor')}</BreadcrumbButton></BreadcrumbItem>
                      : <BreadcrumbItem><BreadcrumbButton icon={<Home20Regular />} onClick={() => openFolder('')} current={!parts.length}
                      onDragOver={(e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG)) e.preventDefault() }} onDrop={(e: React.DragEvent) => dropOn(e, '')}>{t('Meine Dateien')}</BreadcrumbButton></BreadcrumbItem>}
                    {parts.map((p, i) => {
                      let target = parts.slice(0, i + 1).join('/')
                      if (inTresor && i === 0) return null
                      // Im Cryptomator-Tresor: bis zum Tresor Ordner der Ablage, ab dem Tresor „cmv:“-Pfade
                      if (isCmv(path)) {
                        if (i === 0) return null
                        const plain = parts.slice(1, i + 1).join('/')
                        target = cmvRoot && (plain === cmvRoot || plain.startsWith(cmvRoot + '/')) ? CMV + '/' + plain : plain
                      }
                      return [
                        <BreadcrumbDivider key={'d' + i} />,
                        <BreadcrumbItem key={i}><BreadcrumbButton onClick={() => openFolder(target)} current={i === parts.length - 1}
                          onDragOver={(e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG)) e.preventDefault() }} onDrop={(e: React.DragEvent) => dropOn(e, target)}>{p}</BreadcrumbButton></BreadcrumbItem>,
                      ]
                    })}
                  </Breadcrumb>
                )}
            </div>
            {inFiles && !searching && readOnly && inWithMe(path ?? '', anyShared) && path !== WITH_ME && <Text size={200} className={s.muted}>{shownAt(path ?? '') ? t('Nur ansehen') : t('Nur lesen')}</Text>}
            {inFiles && <>
              {photosView ? (() => {
                const counts = new Map<string, number>()
                if (tlOn) for (const [e, n] of Object.entries(tl.state?.types ?? {})) counts.set(e, n)
                else for (const i of data?.items ?? []) counts.set(extOf(i.name), (counts.get(extOf(i.name)) ?? 0) + 1)
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
              {/* Vollbild: das Menüband weg, die Galerie nimmt den ganzen Bereich ein (Esc beendet) */}
              {photosView && !mobile && <Tooltip content={full ? t('Vollbild beenden') : t('Vollbild')} relationship="label">
                <Button size="small" appearance="subtle" icon={full ? <FullScreenMinimize20Regular /> : <FullScreenMaximize20Regular />} onClick={() => setFull(f => !f)} />
              </Tooltip>}
              <Input className={s.search} size="small" value={search} onChange={(_, d) => setSearch(d.value)} placeholder={isEncrypted(path) ? t('Nach Namen filtern') : path ? t('In „{name}“ oder überall suchen', { name: nameOf(path) }) : t('Dateien suchen')}
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
          {photosView && prefs.photoFilter && data && (
            <PhotoFilterBar items={sortedItems} metaOf={metaOf} facets={tlOn ? facets : undefined} cols={filterCols} onCols={c => update({ photoFilterCols: c })}
              sel={photoSel} onSel={setPhotoSel} rows={prefs.photoFilterRows ?? 6} onRows={n => update({ photoFilterRows: n })} />
          )}
          <div ref={listRef} className={s.table} style={{ ...(timeline && months.length > 2 ? { paddingRight: 52, scrollbarWidth: 'none' } : {}), ...(tlOn ? { overflowAnchor: 'none' } : {}) }} onClick={e => { if (e.target === e.currentTarget) setSelected([]) }}
            onScroll={photosView ? e => {
              if (!galleryBack.current && e.currentTarget.getClientRects().length) galleryTop.current = e.currentTarget.scrollTop
              tilesSoon()
            } : undefined}
            onContextMenu={e => {
              // Freie Flaeche (auch die Leer-Meldung): Neuer Ordner, Hochladen, Einfuegen
              if (path === null || (e.target as HTMLElement).closest('[data-path]')) return
              e.preventDefault(); setSelected([]); setCtx({ x: e.clientX, y: e.clientY, kind: 'empty' })
            }}>
            {photosView && atlas
              ? <Suspense fallback={<Spinner style={{ marginTop: 32 }} />}>
                  {tlOn && !geo ? <Spinner style={{ marginTop: 32 }} />
                    : <GalleryAtlas items={onlySelected(tlOn ? geo!.items : shown)} metaOf={tlOn ? (i => geo!.meta.get(i.path)) : metaOf} source={gallerySource} onOpen={(i, l) => setViewer({ list: l, index: Math.max(0, l.findIndex(x => x.path === i.path)) })}
                        place={infoable(sel) ? { count: sel.length, onPlace: (lat, lon) => placeSelected(sel, lat, lon) } : undefined} />}
                </Suspense>
              : <>
                {list()}
                {/* Suche: dazu Treffer in Ordnern auf dem Server, die der Admin durchsuchbar gemacht hat (nicht bei „Nur hier“) */}
                {searching && !photosView && !(scope === 'here' && path) && extMounts.some(m => m.search) && (
                  <ExternalHits term={term} heading onOpen={(m, dir) => { setSearch(''); setExt({ mount: m, path: dir }) }} />
                )}
              </>}
          </div>
          {timeline && !atlas && months.length > 2 && <Timeline months={months} total={tlOn ? tl.state?.shown ?? 0 : shown.length} top={listRef.current?.offsetTop ?? 0} onJump={jumpTo} scroller={listRef} />}
          {undoBar && (() => {
            const bar = (
              <div className={mergeClasses(s.undo, viewer ? s.undoOverViewer : undefined)} role="status">
                <span style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{undoBar.label}</span>
                <Button appearance="transparent" className={s.undoBtn} icon={<ArrowCounterclockwise20Regular />} onClick={undo}>{t('Rückgängig')}</Button>
                <Button appearance="transparent" className={s.undoBtn} icon={<Dismiss20Regular />} aria-label={t('Schließen')} onClick={() => setUndoBar(null)} />
              </div>
            )
            return viewer ? <Portal>{bar}</Portal> : bar
          })()}
          {excludeFrom && <ExcludeFolderDialog path={excludeFrom} onClose={changed => { setExcludeFrom(null); if (changed) load() }} />}
          {importProfiles && <ImportProfilesDialog onClose={changed => { setImportProfiles(false); if (changed) load() }} />}
          {excludes && <PhotoExcludesDialog onClose={changed => { setExcludes(false); if (changed) load() }} />}
          {smartDialogEl}
          {linkAlbum && <AlbumLinksDialog album={linkAlbum} onClose={() => setLinkAlbum(null)} />}
          {albumDialog && albumDialog.kind !== 'share' && <AlbumNameDialog
            title={albumDialog.kind === 'rename' ? t('Album umbenennen') : albumDialog.kind === 'smart' ? t('Smart-Album aus dem Filter') : t('Neues Album')}
            initial={albumDialog.kind === 'rename' ? albumNow?.name : ''} onClose={albumNamed} />}
          {albumDialog?.kind === 'share' && albumNow && <AlbumShareDialog album={albumNow} onClose={ch => { setAlbumDialog(null); if (ch) loadAlbums() }} />}
          {galleryFolders && <GalleryFoldersDialog onClose={changed => { setGalleryFolders(false); if (changed) load() }} onShare={async src => {
            // Quelle teilen = Smart-Album „Quelle = dieser Ordner“: einmal anlegen, dann wie jedes Album teilen, im Chat oder öffentlich zeigen
            try {
              const rule = JSON.stringify({ cols: ['source'], sel: [['ext:' + src.id]] })
              const list = await albumsApi.list()
              const id = list.find(a => a.mine && a.kind === 'smart' && a.rule === rule)?.id ?? (await albumsApi.create(src.name, 'smart', rule)).id
              setAlbums(await albumsApi.list()); setGalleryFolders(false); setAlbum(id); setAlbumDialog({ kind: 'share' })
            } catch (e) { fail(e) }
          }} />}
          {photosView && data && <PhotoSetBar count={sel.filter(i => !i.isDir).length} onPick={p => markPhotos({ pick: p })} onRating={r => markPhotos({ rating: r })}
            onLabel={l => markPhotos({ label: l })} onDelete={deletable(sel) ? () => remove(sel) : undefined} />}
          {/* Rechtsklick-Menue an der Mausposition */}
          {ctx && (ctx.kind !== 'items' || sel.length > 0) && (
            <Menu open onOpenChange={(_, d) => !d.open && setCtx(null)} positioning={{ target: { getBoundingClientRect: () => ({ x: ctx.x, y: ctx.y, top: ctx.y, left: ctx.x, bottom: ctx.y, right: ctx.x, width: 0, height: 0 }) } as any }}>
              <MenuPopover onClick={() => setCtx(null)}>
                {ctx.kind === 'items' ? menuItems(sel) : ctx.kind === 'empty' ? emptyMenu : ctx.kind === 'nav' && ctx.nav ? navMenu(ctx.nav) : folderMenu(ctx.folder ?? '')}
              </MenuPopover>
            </Menu>
          )}
        </div>
        )}
        {detailsOpen && inFiles && !extMount && !mobile && detailsW.handle('left')}
        {detailsOpen && inFiles && !extMount && (
          <Details width={mobile ? undefined : detailsW.width} item={detailItem} meta={detailItem ? metaOf(detailItem) : undefined} metaOf={metaOf}
            onFilter={photosView && detailItem && (!prefs.photoFilter || (pickNext != null && pickNext < 4)) ? what => pickFilter(detailItem, metaOf(detailItem), what) : undefined} selection={sel} version={detailsVersion} onClose={() => setDetails(false)}
            onOpen={open} onOpenFolder={(p, f) => { openPlace(p, f); if (mobile) setDetails(false) }} onShare={setShare}
            onFavorite={favorite} onDownload={keepable(detailItem ? [detailItem, ...sel] : sel) ? download : undefined} fail={fail} notify={m => notify(m)} onChanged={changed} />
        )}
      </div>
      {autoFor && <PhotoAutoDialog items={autoFor}
        onClose={() => setAutoFor(null)} onStarted={changed} />}
      {infoFor && <PhotoInfoDialog items={infoFor} metaOf={metaOf} onClose={() => setInfoFor(null)} onDone={changed} />}
      {arcLeave && (
        <Dialog open onOpenChange={(_, d) => { if (!d.open) setArcLeave(null) }}>
          <DialogSurface style={{ maxWidth: 460 }}>
            <DialogBody>
              <DialogTitle>{t('Änderungen im Archiv')}</DialogTitle>
              <DialogContent>{t('„{name}“ hat Änderungen, die noch nicht gespeichert sind. Beim Speichern bleibt der vorige Stand als Version erhalten.', { name: nameOf(arcLeave) })}</DialogContent>
              <DialogActions>
                <Button onClick={() => { setArcLeave(null); loadArcOpen() }}>{t('Später')}</Button>
                <Button onClick={() => arcSave(arcLeave, true)}>{t('Verwerfen')}</Button>
                <Button appearance="primary" onClick={() => arcSave(arcLeave, false)}>{t('Speichern')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
      {/* Einbinden: ein externes Ziel (Einstellungen → Externe Ziele) und ein Ordner darin; danach die Ordner der obersten Ebene wählen */}
      {addExt && <BindExternalDialog targets={extTargets} onClose={() => setAddExt(false)} onBound={m => { setExtMounts(x => [...x, m]); setAddExt(false); setExtSetup({ mount: m, added: true }) }} />}
      {extSetup && <ExternalMountDialog mount={extSetup.mount} added={extSetup.added}
        onClose={() => { const m = extSetup.mount; setExtSetup(null); setExt({ mount: m.id, path: '' }) }}
        onRemoved={() => { const id = extSetup.mount.id; setExtSetup(null); setExtMounts(x => x.filter(y => y.id !== id)); if (ext?.mount === id) setExt(null) }} />}
      {picker && (
        <FolderPicker title={picker.mode === 'move' ? t('{n} verschieben nach …', { n: picker.paths.length }) : t('{n} kopieren nach …', { n: picker.paths.length })}
          exclude={picker.mode === 'move' ? picker.paths : []}
          externals={picker.paths.every(ownPlace) ? extMounts : undefined}
          onClose={() => setPicker(null)}
          onPick={target => {
            const p = picker; setPicker(null)
            const x = parseExtTarget(target)
            if (x) across(null, p.paths, x.mount, x.path, p.mode === 'move')
            else transfer(p.paths, target, p.mode === 'copy')
          }} />
      )}
      {viewer && (
        <ViewerWithEdit viewer={viewer} startEdit={startEdit} onIndex={i => setViewer(v => v && { ...v, index: i })}
          onSeen={i => { if (!isExt(i) && !isTresor(i.path)) api.post('/api/files/used', { path: i.path }).catch(() => {}) }}
          groupOf={grouping ? (i => groupOf(i)[1]) : undefined}
          onClose={() => { setViewer(null); setStartEdit(false); mainRef.current?.focus() }}
          onDownload={keepable(viewer.list) ? (i => download([i])) : undefined} onDetails={i => { setViewer(null); setSelected([i.path]); setViewerDetail(i); setDetails(true) }}
          onPhotoEdit={async (i, rotate, crop, overwrite, adjust) => {
            try {
              const r = await api.post<{ path: string }>('/api/files/photo-edit', { path: i.path, rotate, crop, overwrite, adjust })
              notify(overwrite ? t('Foto gespeichert - die vorige Version liegt unter Versionen.') : t('Als Kopie gespeichert: {name}', { name: r.path.split('/').pop() ?? '' }))
              // Im Betrachter gleich das Ergebnis zeigen: die Kopie hinter dem Original einreihen und öffnen, Überschriebenes neu laden
              let fresh: Item = { ...i, path: r.path, name: r.path.split('/').pop() ?? i.name, etag: String(Date.now()), modified: new Date().toISOString(), fileId: overwrite ? i.fileId : undefined }
              // Eigene Ablage: Größe, Stand und Kennung der neuen Fassung vom Server (sonst zeigte der Betrachter die des Originals)
              if (!isExt(i)) { try { fresh = { ...fresh, ...(await api.get<{ item: Item }>('/api/files/info?' + q({ path: r.path }))).item } } catch { /* Schätzung bleibt */ } }
              setViewer(v => {
                if (!v) return v
                const at = v.list.findIndex(x => x.path === i.path)
                if (at < 0) return v
                const list = [...v.list]
                if (overwrite) { list[at] = { ...list[at], etag: fresh.etag, modified: fresh.modified, size: fresh.size }; return { ...v, list } }
                list.splice(at + 1, 0, fresh)
                return { ...v, list, index: at + 1 }
              })
              changed()
            } catch (e) { fail(e); throw e }
          }}
          onPhotoSettings={async (i, st) => {
            try {
              let fp: string | null = null
              if (st) fp = (await api.put<{ fingerprint: string | null }>('/api/files/photo-edits', { path: i.path, rotate: st.rotate, crop: st.crop, adjust: st.adjust })).fingerprint
              else await api.del('/api/files/photo-edits?' + q({ path: i.path }))
              // Neuer Etag mit Fingerabdruck: Betrachter und Raster holen die bearbeitete Fassung
              const base = (i.etag ?? '').split('~e')[0]
              const etag = fp ? base + '~e' + fp : base
              setViewer(v => v && { ...v, list: v.list.map(x => x.path === i.path ? { ...x, etag } : x) })
              notify(st ? t('Bearbeitung gespeichert - das Original bleibt unverändert.') : t('Bearbeitung entfernt.'))
              changed()
            } catch (e) { fail(e); throw e }
          }}
          side={mobile ? undefined : (i, close) => <Details inViewer item={i} meta={metaOf(i)} selection={[]} version={detailsVersion} onClose={close}
            onOpen={() => { }} onOpenFolder={(p, f) => { setViewer(null); openPlace(p, f) }} onShare={setShare}
            onFavorite={favorite} onDownload={keepable([i]) ? download : undefined} fail={fail} notify={m => notify(m)} onChanged={changed} />}
          onChat={i => i.fileId && sendToChat([{ type: 'file', fileId: i.fileId }])} onSave={saveText}
          // Galerie: markieren wie in der Liste; blendet der Filter das Foto dann aus, verschwindet es auch hier (das nächste rückt nach)
          markOf={photosView ? i => metaOf(i) : undefined}
          onMark={photosView ? (i, body) => {
            const q = prefs.photoQuick
            const hides = !!q && (body.pick != null && q.flags.length > 0 && !q.flags.includes(body.pick === 1 ? 'P' : body.pick === -1 ? 'X' : 'U')
              || body.rating != null && q.stars > 0 && body.rating < q.stars || body.label != null && q.labels.length > 0 && !q.labels.includes(body.label))
            void markPhotos(body, [i])
            if (hides) setViewer(v => {
              if (!v) return v
              const list = v.list.filter(x => x.path !== i.path)
              return list.length ? { ...v, list, index: Math.min(v.index, list.length - 1) } : null
            })
          } : undefined}
          onShare={i => setShare(i)}
          onFavorite={i => !isExt(i) && favorite(i, !i.favorite)}
          onDelete={i => (canChange([i]) || extWritable(i)) && remove([i]).then(ok => ok && setViewer(v => {
            if (!v) return v
            const list = v.list.filter(x => x.path !== i.path)
            return list.length ? { ...v, list, index: Math.min(v.index, list.length - 1) } : null
          }))} />
      )}
      {editing && (
        <OfficeEditor key={editing.path} name={editing.name} onClose={closeEditor}
          open={() => api.post<OfficeSession>('/api/office/open?lang=' + lang, { path: editing.path, view: editing.view || undefined })} />
      )}
      {share && <ShareDialog item={share} onClose={() => { setShare(null); changed() }} notify={m => notify(m)} fail={fail} />}
      {shareMany && <ShareManyDialog items={shareMany} onClose={() => { setShareMany(null); changed() }} notify={m => notify(m)} fail={fail} />}
      {naming && <NameDialog title={naming.title} initial={naming.initial} label={naming.label} onDone={naming.done} onClose={() => setNaming(null)} />}
      {conflict.dialog}
      {xfer.dialog}
    </div>
  )
}

/** Gesperrter eigener Cryptomator-Tresor (Stufe 3b): Passwort statt der verschlüsselten Dateien. */
function VaultUnlock({ path, onOpen, onRaw }: { path: string; onOpen: (p: string) => void; onRaw: () => void }) {
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const unlock = async () => {
    if (!password) return
    setBusy(true); setError(null)
    try { onOpen((await api.post<{ path: string }>('/api/files/vault/unlock', { path, password })).path) }
    catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(false); setPassword('') }
  }
  return (
    <div style={{ flex: 1, display: 'flex', alignItems: 'flex-start', justifyContent: 'center', padding: '48px 16px' }}>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 420, width: '100%' }}>
        <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}><LockClosed20Regular /><Text weight="semibold">{nameOf(path)}</Text></div>
        <Text>{t('Cryptomator-Tresor. Mit dem Passwort öffnen; die Dateien bleiben verschlüsselt gespeichert und lassen sich auch mit der Cryptomator-App öffnen.')}</Text>
        <Input type="password" autoFocus value={password} placeholder={t('Passwort')} onChange={(_, d) => setPassword(d.value)}
          onKeyDown={e => { if (e.key === 'Enter') unlock() }} />
        {error && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>}
        <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
          <Button appearance="primary" disabled={!password || busy} onClick={unlock}>{t('Entsperren')}</Button>
          <Button appearance="transparent" size="small" style={{ marginLeft: 'auto' }} onClick={onRaw}>{t('Verschlüsselte Dateien anzeigen')}</Button>
        </div>
      </div>
    </div>
  )
}

/** Vorschaugröße für eine Kachel: nach den echten Pixeln (Skalierung des Bildschirms), sonst wirkt sie unscharf. */
function thumbPxFor(tile: number, offline: boolean) {
  if (offline) return 256
  const px = tile * (window.devicePixelRatio || 1)
  return px > 512 ? 2048 : px > 256 ? 512 : 256
}

function ViewerWithEdit({ viewer, startEdit, ...rest }: {
  viewer: { list: Item[]; index: number }; startEdit: boolean; onIndex: (i: number) => void; onClose: () => void; onSeen?: (i: Item) => void
  onDownload?: (i: Item) => void; onDetails: (i: Item) => void; onChat?: (i: Item) => void; onSave: (i: Item, text: string, force: boolean) => Promise<Item>
  side?: (i: Item, close: () => void) => React.ReactNode
  onPhotoEdit?: (i: Item, rotate: number, crop: import('./PhotoEditor').CropRect | null, overwrite: boolean, adjust: import('./PhotoEditor').Adjust | null) => Promise<void>
  onPhotoSettings?: (i: Item, s: import('./PhotoEditor').PhotoSettings | null) => Promise<void>
  markOf?: (i: Item) => Meta | undefined
  onMark?: (i: Item, body: { pick?: number; rating?: number; label?: string }) => void
  onShare?: (i: Item) => void; onFavorite?: (i: Item) => void; onDelete?: (i: Item) => void
  groupOf?: (i: Item) => string
}) {
  return <Viewer items={viewer.list} index={viewer.index} source={gallerySource} startEdit={startEdit} {...rest} />
}
