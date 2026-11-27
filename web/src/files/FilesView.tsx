import { importedText, rejectedText } from '../dms/model'
import { closeTresor, CMV, cmvRoots, isCmv, isEncrypted, isTresor, OPEN_COLOR, requestTresorOpen, TRESOR, TRESOR_EVENT, useTresor } from '../tresor/tresor'
import { ArrowDown20Regular, ArrowUp20Regular, Cart20Regular, CloudArrowUp20Regular, CloudOff16Regular, CloudOff20Regular, LockClosed20Regular, LockOpen20Regular , DocumentCopy20Regular } from '@fluentui/react-icons'
import { onlyInFolder, useSaveToAblage, useSaving } from './SaveToAblage'
import {
  Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent,
  DialogSurface, DialogTitle, Field, Input, Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, MenuItemRadio, MenuItemCheckbox,
  Spinner, Table, TableBody, TableCell, TableCellLayout, TableHeader, TableHeaderCell, TableRow, Text, Tooltip,
  makeStyles, mergeClasses, tokens, ProgressBar, Slider, MessageBar, MessageBarBody, Portal } from '@fluentui/react-components'
import {
  Checkmark16Regular, ArrowUpload20Regular, FolderAdd20Regular, Delete20Regular, ArrowDownload20Regular, Rename20Regular, MoreHorizontal20Regular, Home20Regular,
  ChevronRight16Regular, ChevronDown16Regular, FolderOpen20Regular, Folder20Regular, ArrowSort20Regular, PanelLeft20Regular, ArrowLeft20Regular, ArrowRight20Regular,
  FolderArrowRight20Regular, Copy20Regular, FolderArrowUp20Regular, AppsList20Regular, Grid20Regular, Clock20Regular, Star20Regular, Star20Filled,
  Chat20Regular,   Share20Regular, Link20Regular, Info20Regular, Cut20Regular, ClipboardPaste20Regular, ArrowUndo20Regular, Search20Regular,
  Dismiss20Regular, DocumentAdd20Regular, ArrowCounterclockwise20Regular, DeleteDismiss20Regular, ArrowReset20Regular, ArrowSync20Regular,
  Filter20Regular, LinkSquare20Regular, DocumentEdit20Regular, Star16Filled, Link16Regular, ChevronDown20Regular, DocumentSearch20Regular, GroupList20Regular, ZoomIn20Regular, Eye20Regular, EyeOff20Regular, People16Regular, People20Regular,
  ImageOff20Regular, ArrowImport20Regular, Globe20Regular, Server20Regular, HardDrive20Regular, Cloud20Regular, Add16Regular, ImageMultiple20Regular, ImageMultiple24Regular,
  Folder20Filled,
  ColumnTriple20Regular, FullScreenMaximize20Regular, FullScreenMinimize20Regular, TextBulletListSquare20Regular, Stack20Regular, Sparkle20Regular, ScaleFit20Regular, ScaleFill20Regular, Tag20Regular, DocumentBulletList20Regular, DocumentBulletList16Filled, Wand20Regular, Edit20Regular, Edit16Regular, Prohibited16Filled, } from '@fluentui/react-icons'
import { ExcludeFolderDialog, PhotoExcludesDialog } from './PhotoExcludes'
import { RibbonButton, RibbonDivider, RibbonHeader, RibbonSlot, RibbonViewSlot, useSidePane } from '../shell/Ribbon'
import { SidePane, useBackClose, useHistoryClose, useIsMobile } from '../shell/mobile'
import { usePrefs } from '../prefs'
import { lazy, Suspense, useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState, Fragment } from 'react'
import { ApiError, api, featureOn, fmtSize, q } from '../api'
import { comfortOf } from '../settings/FilesComfortSettings'
import { dropUpload, finishToFiles, uploadChunked, UploadError } from './chunked'
import { useApp } from '../App'
import { useDebounced } from '../shell/hooks'
import { useNavStyles } from '../shell/nav'
import { STORAGE_CHANGED, storageLimit, type Storage } from '../shell/StorageInfo'
import { lang, locale, t } from '../i18n'
import { failedText } from './failedText'
import {
  FILTERS, Item, Kind, KindIcon, Thumb, WITH_ME, quietScroll, preloadThumbs, extOf, fmtWhen, hasThumb, inWithMe, inside, join, kindLabel, kindOf, nameOf, ownSource, parentOf, viewKind, stackPhotos, unstack, stackLabel, openStacks,
  ShownName, plainName,
} from './common'
import { SourceCloud } from './SourceCloud'
import { BindExternalDialog, ExternalMount, ExternalMountDialog, ExternalView, type ExtRibbon } from './ExternalView'
import { ExtPickerTree, extTarget, parseExtTarget } from './ExtPickerTree'
import { useFileClip } from './clip'
import { useTransfer } from './TransferDialog'
import { ArchiveView, isArchive } from './ArchiveView'
import { ExternalHits } from './ExternalSearch'
import { COLS, ColHead, ColKey, ColPref, ColumnMenu, META_COLS, SortKey, cleanCols } from './FileColumns'
import { ATTRS, cleanFilterCols, type FilterAttr, dimsOf, exposureLine, filterPhotos, fmtDuration, labelColor, labelName, MARK_LABELS, Meta, PhotoFilterBar, PhotoFilterTop, PhotoQuickBar, PhotoSetBar, quickRule, takenOf, TILE_INFOS } from './PhotoFilter'
import { ImportProfilesDialog } from './ImportProfiles'
import { GalleryFoldersDialog, gallerySource, galleryTileSource, loadSourceNames, isExt, extWritable, extId, extOffline, extPlace, takePhotoFromElsewhere } from './GalleryFolders'
import { Timeline } from './Timeline'
import { SmartAlbumDialog, SmartRule } from './SmartAlbumDialog'
import { galleryAll, galleryLocate, galleryMonth, isUnreadKey, loadGeo, searchGallery, useFacets, useGalleryTimeline } from './GalleryTimeline'
import { Fresh, freshenPhoto, freshenTiles } from './GalleryFreshen'
import { estimateHeight, mosaicRows, ratioOf, rowsHeight, type MosaicRow } from './mosaic'
import { BAR_MIN_W, HEAD_H, PICK_TINT, REJECT_TINT, STAR_HOVER, TileBar, TileHead } from './TileBar'
import { tileHeadOf } from './tileTemplate'
import type { PhotoSettings } from './PhotoEditor'
import { AlbumInfo, AlbumNameDialog, AlbumPicker, AlbumShareDialog, albumKey, albumsApi, isAlb, loadAlbum, AlbumLinksDialog } from './Albums'
import { Viewer } from './Viewer'
import { Details } from './Details'
import { AutoBadge, CategoryNav, LabelsDetail, LabelsDialog, ManageCategories, below, catsApi, catsLoaded, fullName, reloadCats, useFileCats, type Labels } from './FileCategories'
import { sendToChat } from '../chat/SendToChat'
import { Choice, NameDialog, ShareDialog, ShareManyDialog, copyText, linkUrl, useConflict, Link } from './Dialogs'
import { BulkRename } from './BulkRename'
import { EXT_COLOR, FolderTabs } from './FolderTabs'
import { LinksList, TrashList, useLinks, useTrash } from './Lists'
import { ContentSearch } from './ContentSearch'
import { Snippet } from '../shell/Snippet'
import { OfficeEditor, OfficeSession, officeMode, useOffice } from './Office'
import { usePaneWidth } from '../shell/Splitter'

import { PhotoAutoDialog } from './PhotoAutoDialog'
import { PhotoExportDialog } from './PhotoExport' // [feature:photoexport]
import { PhotoInfoDialog } from './PhotoInfoDialog'
import { RAW } from './PhotoEditor'
import { ContextMenu, askConfirm, isTyping, useContextMenu } from '../shell/context'
import { pushUndo as pushSharedUndo, undoAllOn, undoStep, useUndoSteps } from '../shell/undo'
import { basket, BasketButton, TresorDownloadButton } from './Basket'
import { DRAG_PANE1, DRAG_PANE2, dualKey, PaneBox, type DualKeys, QuickView } from './DualPane'
import { createPortal } from 'react-dom'
import { ArrowExportLtr20Regular, ArrowExportRtl20Regular, ArrowSwap20Regular, ColumnDoubleCompare20Regular, SplitVertical20Regular, TabAdd20Regular, ImageAdd20Regular, SaveSearch20Regular } from '@fluentui/react-icons'

/** Ordner vergleichen (zwei Fenster, wie im Midnight Commander): jedes Fenster meldet seine Liste, das Ereignis lässt beide markieren */
const paneLists: Record<0 | 1, Item[]> = { 0: [], 1: [] }
const PANE_COMPARE = 'dmoffice:pane-compare'
/** Was im anderen Fenster fehlt oder neuer bzw. anders ist (Ordner: nur fehlende) */
function differs(mine: Item[], other: Item[]): string[] {
  const o = new Map(other.map(x => [x.name.toLowerCase(), x]))
  return mine.filter(i => {
    const x = o.get(i.name.toLowerCase())
    if (!x) return true
    if (i.isDir || x.isDir) return false
    const ti = Date.parse(i.modified), tx = Date.parse(x.modified)
    return i.size !== x.size || (ti && tx ? ti - tx > 2000 : false)
  }).map(i => i.path)
}
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
  nav: { display: 'flex', alignItems: 'center', gap: '6px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    '& > svg': { flexShrink: 0 }, ':focus-visible': { outline: `2px solid ${tokens.colorStrokeFocus2}`, outlineOffset: '-2px' } },
  navName: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  navSep: { height: '1px', backgroundColor: tokens.colorNeutralStroke2, margin: '8px 4px' },
  twisty: { width: '16px', flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center' },
  active: { backgroundColor: tokens.colorBrandBackground2, fontWeight: 600 },
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0, outline: 'none', position: 'relative' },
  sel: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  head: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 16px', minHeight: '40px', flexWrap: 'wrap',
    '@media (max-width: 767px)': { padding: '6px 8px', gap: '4px' },
  },
  // Wenig Platz (zwei Fenster): ohne Scrollbalken, das Ende des Pfads bleibt sichtbar
  crumbs: { flex: 1, minWidth: '120px', overflowX: 'auto', scrollbarWidth: 'none', '::-webkit-scrollbar': { display: 'none' } },
  viewTitle: { fontSize: tokens.fontSizeBase400, fontWeight: 600, paddingLeft: '4px' },
  search: { width: '260px', '@media (max-width: 767px)': { width: '100%', order: 3 } },
  table: { flex: 1, overflowY: 'auto', overflowX: 'auto', padding: '0 16px 16px', '@media (max-width: 767px)': { padding: '0 4px 72px' } },
  drop: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-8px' },
  target: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: '-2px', backgroundColor: tokens.colorBrandBackground2 },
  row: { cursor: 'pointer', userSelect: 'none' },
  cut: { opacity: 0.45 },
  cursor: { outline: `1px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-2px' },
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
  // Bildinfos unter dem Bild: unten am Bildrand; Bildinfos auf dem Bild: als Zeile über den Infozeilen (marksRow)
  marks: { position: 'absolute', left: '4px', bottom: '4px', display: 'flex', alignItems: 'center', gap: '4px', padding: '1px 5px', borderRadius: '8px',
    backgroundColor: 'rgba(0,0,0,0.55)', color: '#fff', fontSize: '11px', lineHeight: '16px', pointerEvents: 'none' },
  marksRow: { display: 'flex', alignItems: 'center', gap: '5px', marginBottom: '2px', fontSize: '12px', lineHeight: '15px' },
  markDot: { width: '9px', height: '9px', borderRadius: '50%', display: 'inline-block' },
  rejected: { opacity: 0.4 },
  thumb: { position: 'relative', width: '100%', aspectRatio: '4 / 3', display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden', borderRadius: tokens.borderRadiusSmall },
  thumbImg: { width: '100%', height: '100%' },
  // Nach zwei Zeilen mit „…“ kürzen (voller Name im Tooltip der Kachel)
  tileName: {
    fontSize: '12px', textAlign: 'center', overflowWrap: 'anywhere', lineHeight: '16px', maxHeight: '32px', overflow: 'hidden', width: '100%',
    display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical',
  },
  tileSnippet: {
    fontSize: '11px', lineHeight: '14px', color: tokens.colorNeutralForeground3, overflow: 'hidden', display: '-webkit-box',
    WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflowWrap: 'anywhere', textAlign: 'center', padding: '0 4px',
  },
  // Galerie: je Angabe eine Zeile, zu lang wird gekürzt
  // erste Zeile rückt neben den Auswahlkasten (der oben links auf der Kachel sitzt)
  tileInfos: { width: '100%', display: 'flex', flexDirection: 'column', fontSize: '11px', lineHeight: '14px', color: tokens.colorNeutralForeground3, marginTop: '-2px', padding: '0 1px', boxSizing: 'border-box' },
  // Galerie: Kachel enger, das Bild fuellt sie
  tilePhoto: { padding: '4px', gap: '4px', ':hover [data-tilecheck]': { opacity: 1 } },
  // Kachelmodi (Kennung tilemodes): „Voll“ mit Kopfzeile und Leiste, „Nur Bild“ mit schmalem Rand
  tileFull: { padding: 0, gap: 0, overflow: 'hidden', backgroundColor: tokens.colorNeutralBackground3, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusSmall, ...STAR_HOVER },
  thumbFull: { width: 'calc(100% - 12px)', margin: '6px' },
  tileBare: { padding: '2px', gap: 0 },
  // Mosaik (Kennung mosaicweb): Foto füllt die Kachel, keine Infozeilen; Kennzeichen in den Ecken, Details beim Überfahren
  tileMosaic: {
    padding: 0, gap: 0, border: 'none', borderRadius: tokens.borderRadiusSmall, overflow: 'hidden', alignItems: 'stretch', backgroundColor: tokens.colorNeutralBackground3,
    ':hover': { backgroundColor: tokens.colorNeutralBackground3 },
    ':hover [data-mhover]': { opacity: 1, transform: 'none' }, ':hover [data-mbr]': { opacity: 0 }, ':hover [data-mtl]': { left: '32px' },
  },
  tileMosaicSel: { backgroundColor: tokens.colorNeutralBackground3 },
  mosSelFrame: { position: 'absolute', inset: 0, zIndex: 1, border: `3px solid ${tokens.colorBrandStroke1}`, pointerEvents: 'none' },
  thumbMosaic: { aspectRatio: 'auto', flexGrow: 1, minHeight: 0, borderRadius: 0 },
  mosTL: { position: 'absolute', top: '6px', left: '6px', zIndex: 1, display: 'flex', alignItems: 'center', gap: '4px', fontSize: '11px', lineHeight: '14px', fontWeight: 700, color: '#fff', textShadow: '0 1px 3px rgba(0,0,0,0.7)', pointerEvents: 'none', transitionProperty: 'left', transitionDuration: tokens.durationFaster },
  mosBR: { position: 'absolute', bottom: '6px', right: '6px', zIndex: 1, padding: '1px 6px', borderRadius: '10px', backgroundColor: 'rgba(0,0,0,0.5)', color: '#F5B301', fontSize: '11px', lineHeight: '14px', letterSpacing: '-1px', pointerEvents: 'none', transitionProperty: 'opacity', transitionDuration: tokens.durationFaster },
  mosHover: {
    position: 'absolute', left: 0, right: 0, bottom: 0, padding: '22px 8px 6px', display: 'flex', flexDirection: 'column', fontSize: '11px', lineHeight: '15px', color: '#fff', pointerEvents: 'none',
    textShadow: '0 0 3px rgba(0,0,0,0.8)', background: 'linear-gradient(transparent, rgba(0,0,0,0.72))', opacity: 0, transform: 'translateY(4px)', transitionProperty: 'opacity, transform', transitionDuration: tokens.durationFaster,
  },
  mosHoverTitle: { fontSize: '12px', fontWeight: 600, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  mosHoverLine: { whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', opacity: 0.85 },
  // Galerie: Auswahlkasten erst beim Überfahren (oder wenn schon etwas markiert ist) - sonst verdeckt er Bild und Angaben
  tileCheckQuiet: { opacity: 0, transitionProperty: 'opacity', transitionDuration: tokens.durationFaster, ':focus-within': { opacity: 1 } },
  tileInfo: { display: 'flex', gap: '6px', minWidth: 0 },
  tileInfoL: { flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  tileInfoR: { flexShrink: 1, minWidth: 0, maxWidth: '50%', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', textAlign: 'right' },
  tileCheck: { position: 'absolute', top: '2px', left: '2px', zIndex: 1 },
  tileBadge: { position: 'absolute', top: '6px', right: '6px', display: 'flex', gap: '2px', color: tokens.colorNeutralForeground2 },
  editedBadge: { color: '#fff', background: 'rgba(0,0,0,.55)', borderRadius: '50%', padding: '2px' },
  // Foto aus einem Galerie-Ordner, dessen externer Ordner gerade nicht erreichbar ist: rotes Stoppschild unten links
  dmsMark: { position: 'absolute', right: '-6px', bottom: '-4px', color: tokens.colorBrandForeground1, backgroundColor: tokens.colorNeutralBackground1, borderRadius: '3px' },
  offBadge: { position: 'absolute', left: '6px', bottom: '6px', zIndex: 2, display: 'flex', color: '#E5484D', background: '#fff', borderRadius: '50%' },
  // Stapel JPG+RAW: Kennzeichen unten rechts auf dem Bild
  // Oben rechts im Bild: unten liegen Uhrzeit und Name (Angaben auf dem Bild)
  stackBadge: { position: 'absolute', right: '6px', top: '6px', zIndex: 1, fontSize: '10px', fontWeight: 700, padding: '1px 5px', borderRadius: '4px', backgroundColor: 'rgba(0,0,0,0.6)', color: '#fff', pointerEvents: 'none' },
  stackBadgeLow: { top: '28px' },
  stackBadgeClick: { pointerEvents: 'auto', cursor: 'pointer', ':hover': { backgroundColor: 'rgba(0,0,0,0.85)' } },
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
  // Erster Aufruf ohne Inhalt: wie in der Audiothek (Symbol, Überschrift, Erklärung, Knopf zur Einrichtung)
  firstUse: { margin: '0 auto', minHeight: '100%', boxSizing: 'border-box', justifyContent: 'center', textAlign: 'center', maxWidth: '420px', display: 'flex', flexDirection: 'column', gap: '12px', alignItems: 'center', padding: '24px' },
  firstUseText: { color: tokens.colorNeutralForeground3 },
  // Zwei Fenster mit eigenem Baum: der Baum des rechten Fensters
  paneTree: { width: '200px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '8px 4px', display: 'flex', flexDirection: 'column', gap: '1px', overflowY: 'auto' },
  paneButtons: { display: 'flex', alignItems: 'center', flexShrink: 0 },
})

const DRAG = 'application/x-dmoffice-files'

type Special = 'recent' | 'favorites' | 'photos' | 'shared' | 'trash'
/** Einträge der Seitenleiste, die in den oberen Bereich über „Meine Dateien“ dürfen */
type MovableNav = 'recent' | 'favorites' | 'shared' | 'trash'
const MOVABLE: MovableNav[] = ['recent', 'favorites', 'shared', 'trash']
const NAV_ICONS: Record<MovableNav, JSX.Element> = { recent: <Clock20Regular />, favorites: <Star20Regular />, shared: <LinkSquare20Regular />, trash: <Delete20Regular /> }
/** cat/tag: Kategorie bzw. Tag als virtueller Ordner (docs/datei-kategorien-konzept.md) */
type View = { kind: 'folder'; path: string } | { kind: Special } | { kind: 'cat'; id: string } | { kind: 'tag'; tag: string }

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
  // #/files/:cat/<Kennung>, #/files/:tag/<Tag>
  const cat = rest.match(/^:cat\/([^/]+)$/)
  if (cat) return { kind: 'cat', id: decodeURIComponent(cat[1]) }
  const tag = rest.match(/^:tag\/(.+)$/)
  if (tag) try { return { kind: 'tag', tag: decodeURIComponent(tag[1]) } } catch { /* kaputte Adresse: Ordner */ }
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
  : v.kind === 'cat' ? ':cat/' + encodeURIComponent(v.id) : v.kind === 'tag' ? ':tag/' + encodeURIComponent(v.tag) : ':' + v.kind)

const TILE_STEP = 300
/** Listenansicht: so viele Zeilen auf einmal, weitere beim Scrollen (1.000 Zeilen auf einmal brauchten Sekunden) */
/** So viele Ebenen zeigt die Pfadleiste höchstens am Ende, davor „…“. */
const CRUMBS = 12
const LIST_STEP = 100
/** Ein Stück der fensterweise gezeichneten Galerie: Abschnittskopf, Kacheln oder (Zeitleiste) ein noch nicht geladener Monat. */
interface Block { key: string; label: string; head: boolean; items: Item[]; start: number; off: number; h: number; count: number; bucket?: string; ph?: boolean; rows?: MosaicRow[] }
/** Abstand im Mosaik (Kennung mosaicweb) */
const MOSAIC_GAP = 4
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

function FolderTree({ current, onOpen, onDropOn, onContext, onPaste, version, exclude, listed, showHidden, root = '', rootLabel, rootIcon }: {
  /** Wurzel des Baums: "" = Meine Dateien, TRESOR = der Tresor (Ziel beim Verschieben und Kopieren) */
  root?: string; rootLabel?: string; rootIcon?: JSX.Element
  current: string | null; onOpen: (path: string) => void; onDropOn?: (e: React.DragEvent, path: string) => void
  showHidden?: boolean; onContext?: (e: React.MouseEvent, path: string) => void; version: number; exclude?: string[]
  /** Strg+V auf einem Ordner im Baum: dorthin einfügen */
  onPaste?: (path: string) => void
  /** Gerade geladene Liste eines Ordners - der Baum uebernimmt deren Unterordner (geloescht/neu auf anderem Geraet) */
  listed?: { path: string; items: Item[] } | null
}) {
  const s = useStyles()
  const [kids, setKids] = useState<Record<string, Item[]>>({})
  const kidsRef = useRef(kids)
  kidsRef.current = kids
  const [open, setOpen] = useState<Record<string, boolean>>({ [root]: true })
  const [over, setOver] = useState<string | null>(null)
  const load = useCallback((p: string) => listDirs(p).then(d => setKids(k => ({ ...k, [p]: d }))).catch(() => {}), [])
  // Komfort wie in Directory Opus (Kennung treeauto): andere Zweige zuklappen, Weg zum Ordner hervorheben, beim Ziehen aufklappen
  const { prefs: treePrefs } = usePrefs()
  const comfort = comfortOf(treePrefs.filesComfort)
  const auto = featureOn('treeauto')
  const chainOf = (c: string | null) => {
    if (c === null || (root && c !== root && !c.startsWith(root + '/'))) return [] as string[]
    const rest = c === root ? '' : root ? c.slice(root.length + 1) : c
    const parts = rest ? rest.split('/') : []
    return [root].concat(parts.map((_, i) => join(root, parts.slice(0, i + 1).join('/'))))
  }
  const onPath = new Set(auto && comfort.highlight ? chainOf(current).slice(0, -1) : [])
  // Beim Ziehen über einen zugeklappten Ordner: nach einer Sekunde aufklappen; nach dem Ablegen wieder zu (federbelastet)
  const dragTimer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined)
  const sprung = useRef<string[]>([])
  const springBack = () => {
    clearTimeout(dragTimer.current)
    if (!sprung.current.length) return
    const back = sprung.current
    sprung.current = []
    setOpen(o => { const n = { ...o }; back.forEach(p => { n[p] = false }); return n })
  }
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
    // Mit treeauto: nur der Weg zum aktuellen Ordner bleibt offen, andere Zweige klappen zu
    if (auto && comfort.collapse) setOpen({ [root]: true })
    // Ebene für Ebene: weiter nur, solange das nächste Stück wirklich ein Unterordner ist
    // (ein langer, falscher Pfad in der Adresse löste sonst je Stück eine Anfrage aus)
    let gone = false
    void (async () => {
      for (let i = 0; i < chain.length - 1; i++) {
        const p = chain[i]
        setOpen(o => (o[p] ? o : { ...o, [p]: true }))
        let d = kidsRef.current[p]
        if (!d) {
          try { d = await listDirs(p) } catch { return }
          if (gone) return
          const got = d
          setKids(k => ({ ...k, [p]: got }))
        }
        if (!d.some(x => x.path === chain[i + 1])) return
      }
    })()
    return () => { gone = true }
  }, [current]) // eslint-disable-line react-hooks/exhaustive-deps

  const toggle = (p: string) => {
    setOpen(o => ({ ...o, [p]: !o[p] }))
    if (!kids[p]) load(p)
  }
  const dropProps = (p: string) => onDropOn ? {
    onDragOver: (e: React.DragEvent) => {
      if (!(e.dataTransfer.types.includes(DRAG) || e.dataTransfer.types.includes('Files'))) return
      e.preventDefault(); e.stopPropagation()
      if (over !== p) {
        setOver(p)
        clearTimeout(dragTimer.current)
        if (auto && comfort.dragOpen && !open[p]) dragTimer.current = setTimeout(() => { sprung.current.push(p); setOpen(o => ({ ...o, [p]: true })); if (!kids[p]) load(p) }, comfort.dragDelay)
      }
    },
    onDragLeave: () => { setOver(o => (o === p ? null : o)); clearTimeout(dragTimer.current) },
    onDrop: (e: React.DragEvent) => { e.stopPropagation(); setOver(null); onDropOn(e, p); if (auto) springBack() },
  } : {}
  const render = (p: string, depth: number): JSX.Element[] => (kids[p] ?? []).filter(f => (showHidden || !isHidden(f, false)) && !exclude?.some(x => inside(x, f.path))).flatMap(f => [
    <div key={f.path} className={mergeClasses(s.nav, current === f.path && s.active, over === f.path && s.target)}
      style={{ paddingLeft: 8 + depth * 14, ...(onPath.has(f.path) ? { fontWeight: 600, color: tokens.colorBrandForeground1 } : {}) }} onClick={() => onOpen(f.path)} {...dropProps(f.path)}
      title={f.name} role="button" tabIndex={0} aria-expanded={open[f.path] ? true : false}
      onKeyDown={e => { if (e.target !== e.currentTarget) return
        if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); onOpen(f.path) }
        else if (onPaste && (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'v') { e.preventDefault(); onPaste(f.path) }
        else if ((e.key === 'ArrowRight' && !open[f.path]) || (e.key === 'ArrowLeft' && open[f.path])) { e.preventDefault(); toggle(f.path) } }}
      onContextMenu={onContext ? e => { e.preventDefault(); e.stopPropagation(); onContext(e, f.path) } : undefined}>
      <span className={s.twisty} onClick={e => { e.stopPropagation(); toggle(f.path) }}>
        {kids[f.path] && kids[f.path].filter(k => showHidden || !isHidden(k, false)).length === 0 ? null : open[f.path] ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
      </span>
      {current === f.path ? <FolderOpen20Regular /> : <Folder20Regular />}<span className={s.navName}>{f.name}</span>
    </div>,
    ...(open[f.path] ? render(f.path, depth + 1) : []),
  ])
  return (
    // Ziehen endet woanders (abgebrochen, anderer Bereich): aufgeklappte Ordner wieder zu
    <div style={{ display: 'contents' }} onDragEnd={auto ? springBack : undefined} onDragLeave={auto ? e => { if (!(e.currentTarget as HTMLElement).contains(e.relatedTarget as Node)) springBack() } : undefined}>
      <div className={mergeClasses(s.nav, current === root && s.active, over === root && s.target)} style={onPath.has(root) ? { fontWeight: 600, color: tokens.colorBrandForeground1 } : undefined} onClick={() => onOpen(root)} {...dropProps(root)}
        onContextMenu={onContext ? e => { e.preventDefault(); e.stopPropagation(); onContext(e, root) } : undefined}>
        <span className={s.twisty} onClick={e => { e.stopPropagation(); toggle(root) }}>{open[root] ? <ChevronDown16Regular /> : <ChevronRight16Regular />}</span>
        {rootIcon ?? <Home20Regular />}{rootLabel ?? t('Meine Dateien')}
      </div>
      {open[root] && render(root, 1)}
    </div>
  )
}

/** Datei, die nach dem Öffnen ihres Ordners markiert wird - außerhalb der Komponente, weil die Galerie dafür zum Bereich Dateien wechselt. */
let pendingFocus: string | null = null

/** Zielordner waehlen fuer Verschieben/Kopieren (auch für den Zielordner von „In Ablage sichern“). */
export function FolderPicker({ title, exclude, onPick, onClose, externals, start = '' }: {
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

/** Zweites Fenster (Kennung dualpane): eine weitere vollständige Dateiansicht. Ordner und Ansicht stehen nicht in der Adresse,
 *  sondern im eigenen Zustand; Menüband (über ribbon), Seitenleiste und Detailbereich gehören dem aktiven Fenster. */
interface PaneLink {
  /** Ordner beim Öffnen des Fensters */
  start: string
  /** Auftrag von außen (Seitenleiste, Alt+I, Strg+U): diese Ansicht bzw. diesen externen Ordner zeigen; n zählt die Aufträge */
  req: { view?: View; ext?: { mount: string; path: string }; n: number } | null
  /** Meldet Ansicht und externen Ordner zurück (Hervorhebung in der Seitenleiste, Ziel für F5/F6) */
  onState: (st: { view: View; ext: { mount: string; path: string } | null; arc: boolean }) => void
  active: boolean
  onActivate: () => void
  keys: DualKeys
  /** Ordner des linken Fensters (null: dort ist gerade kein Ordner offen) */
  otherPath: string | null
  /** Schlüssel der Ansicht je Ordner im linken Fenster: gleich - das linke Fenster ist führend, hier gilt eine Änderung nur vorübergehend */
  otherKey: string
  /** Zähler: das linke Fenster hat etwas geändert - neu laden */
  version: number
  /** Hier wurde etwas geändert: das linke Fenster lädt neu */
  onChanged: () => void
  /** Platz für das Menüband (über der Seitenleiste, gehört dem aktiven Fenster) */
  ribbon: HTMLElement | null
  /** Fester Platz des Detailbereichs ganz rechts (gehört dem aktiven Fenster) - sonst wandert er mit und die Fenster springen */
  details: HTMLElement | null
  /** Eigener Ordnerbaum (Einstellung „Jedes Fenster mit eigenem Baum“) */
  ownTree: boolean
  /** Zähler: Fokus in die Liste (Tab) */
  focus: number
  /** Knopf an der Pfadleiste: diesen Ordner im linken Fenster öffnen */
  openInOther: (path: string) => void
  /** Strg+Q: die aktuelle Datei dieses Fensters melden (Schnellansicht im linken Fenster) */
  onCurrent: (i: Item | null) => void
}

export default function FilesView({ gallery = false, pane }: { gallery?: boolean; pane?: PaneLink }) {
  const s = useStyles()
  const nv = useNavStyles()
  const detailsW = usePaneWidth('files.details', 320, 240, 720)
  const { fail, notify, me } = useApp()
  // Zurück in Dateien ohne Ordner in der Adresse (Klick auf den Bereich): wieder dort, wo man zuletzt war
  const [view, setView] = useState<View>(() => {
    if (pane) return { kind: 'folder', path: pane.start }
    if (gallery) return { kind: 'photos' }
    if (/^#\/?files\/?$/.test(location.hash)) try {
      const last = sessionStorage.getItem('dm.files.last')
      if (last && last !== location.hash) { history.replaceState(null, '', last); return parseHash() }
    } catch { /* ohne Speicher */ }
    return parseHash()
  })
  // Externe Ordner (Version 2): Ordner auf dem Server und WebDAV - eigene Ansicht statt der Liste
  const [ext, setExt] = useState<{ mount: string; path: string } | null>(() => pane ? null : parseExt())
  // Geöffnetes Archiv (ZIP, 7z, RAR, TAR): eigene Ansicht statt der Liste
  const [arc, setArc] = useState<{ file: string; dir: string } | null>(gallery || pane ? null : parseArc)
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
    // [feature:sftptrust]
    // Fingerabdruck des SFTP-Servers wartet auf Bestätigung (keyWait): anklickbar lassen, der Ordner zeigt dort, was zu bestätigen ist
    const load = () => api.get<{ mounts: ExternalMount[]; targets: ExternalMount[]; canAdd: boolean; keyWait?: string[] }>('/api/files/external/mounts').then(r => {
      const wait = new Set(r.keyWait ?? [])
      setExtMounts(r.mounts.map(m => wait.has(m.id) ? { ...m, available: true } : m)); setExtTargets(r.targets ?? []); setExtCanAdd(r.canAdd || (r.targets ?? []).length > 0)
    }).catch(() => {})
    // [feature:sftptrust:else]
    // const load = () => api.get<{ mounts: ExternalMount[]; targets: ExternalMount[]; canAdd: boolean }>('/api/files/external/mounts').then(r => { setExtMounts(r.mounts); setExtTargets(r.targets ?? []); setExtCanAdd(r.canAdd || (r.targets ?? []).length > 0) }).catch(() => {})
    // [/feature:sftptrust]
    load()
    // Das zweite Fenster fragt nicht noch einmal regelmäßig - geändert wird über das Ereignis unten
    const id = pane ? undefined : setInterval(load, 60_000)
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
    // Zweites Fenster: ohne Seitenleiste, also ohne Anzeige des Speichers
    if (pane) return
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
  // Externer Ordner: seine Befehle für das Menüband
  const extRibbon = useRef<ExtRibbon | null>(null)
  const [, setExtRibbonKey] = useState('')
  // Fokus in die Liste: die eigene oder, bei einem externen Ordner, dessen Liste
  const [extFocus, setExtFocus] = useState(0)
  const focusList = () => { if (mainRef.current) mainRef.current.focus(); else setExtFocus(n => n + 1) }
  // Gewählte Einträge bleiben beim Wechsel in einen anderen Bereich und zurück (je Ordner, in diesem Fenster)
  const selKey = pane ? 'dm.files.sel2' : 'dm.files.sel'
  const [selected, setSelected] = useState<string[]>(() => { try { return JSON.parse(sessionStorage.getItem(selKey) ?? '[]') } catch { return [] } })
  useEffect(() => { try { sessionStorage.setItem(selKey, JSON.stringify(selected.slice(0, 200))) } catch { /* ohne Speicher */ } }, [selected]) // eslint-disable-line react-hooks/exhaustive-deps
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
  const toAblage = useSaveToAblage()
  const photoDesc = prefs.photoSortDesc ?? true
  // Ansicht je Ordner (wie im Windows Explorer): gilt für diesen Ordner, sonst die Vorgaben; die Galerie hat ihre eigene
  const perFolder = featureOn('folderviews') && !photosView && comfortOf(prefs.filesComfort).perFolder
  const viewKey = ext ? `ext:${ext.mount}/${ext.path}` : view.kind === 'folder' ? `f:${view.path}` : view.kind === 'cat' ? `cat:${view.id}` : view.kind === 'tag' ? `tag:${view.tag}` : view.kind
  type FolderView = NonNullable<NonNullable<typeof prefs.folderViews>[string]>
  // Zweites Fenster im selben Ordner wie das linke: das linke ist führend, Änderungen hier gelten nur vorübergehend in diesem Fenster
  const [tempView, setTempView] = useState<{ key: string; fv: FolderView } | null>(null)
  useEffect(() => { setTempView(x => x && x.key !== viewKey ? null : x) }, [viewKey])
  const fv: FolderView | undefined = perFolder ? (tempView?.key === viewKey ? tempView.fv : prefs.folderViews?.[viewKey]) : undefined
  /** Ansicht ändern: mit Ansicht je Ordner nur für diesen Ordner (höchstens 300 Ordner gemerkt (die Einstellungen sind auf 200 KB begrenzt), die ältesten fallen weg), sonst die Vorgabe */
  const setFolderView = (patch: FolderView, global: Partial<typeof prefs>) => {
    if (!perFolder) { update(global); return }
    if (pane && pane.otherKey === viewKey) { setTempView({ key: viewKey, fv: { ...fv, ...patch } }); return }
    const all = { ...(prefs.folderViews ?? {}) }
    delete all[viewKey]
    all[viewKey] = { ...fv, ...patch }
    const keys = Object.keys(all)
    for (const k of keys.slice(0, Math.max(0, keys.length - 300))) delete all[k]
    update({ folderViews: all })
  }
  const sort = photosView ? prefs.photoSort ?? 'taken' : fv?.sort ?? prefs.fileSort ?? 'name'
  const desc = photosView ? prefs.photoSortDesc ?? true : fv?.desc ?? prefs.fileSortDesc ?? false
  const viewMode = view.kind === 'photos' ? 'tiles' : fv?.view ?? prefs.fileView ?? 'details'
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
  // Einheitliches Rückgängig (Kennung undoall): gemeinsame Leiste und Liste aller Bereiche; danach neu laden (jeweils der aktuelle Stand)
  const sharedUndo = undoAllOn()
  const sharedSteps = useUndoSteps().steps
  const changedRef = useRef<() => void>(() => {})
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
  const backingOut = useRef(false)
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
    const p = pane ? null : officeHash()
    return p ? { path: p, name: nameOf(p) } : null
  })
  const searching = term.length > 0 && view.kind !== 'trash' && view.kind !== 'shared' && !(view.kind === 'folder' && isEncrypted(view.path))
  // Galerie als Zeitleiste vom Server in Monatsabschnitten (GalleryTimeline): erst nur die Monate, dann die Fotos im Bild.
  // Nicht in Alben (klein) und nicht bei der Suche (Serversuche wie bisher).
  const tlOn = photosView && album == null && !searching
  const tlQuery = { sort, desc, stack: prefs.photoStack ?? true, ext: photoExt, filter: baseOn || quick ? JSON.stringify({ cols: ruleCols, sel: ruleSel }) : null,
    group: prefs.photoGroup === 'source' || prefs.photoGroup === 'category' || prefs.photoGroup === 'tag' ? prefs.photoGroup : undefined }
  const tl = useGalleryTimeline(tlOn, tlQuery, fail)
  const facets = useFacets(tlOn && !!prefs.photoFilter, filterCols, photoExt, tlQuery.filter, tl.stamp, fail)
  // Suche in der Galerie: über den Index der Zeitleiste (eigene Fotos und Galerie-Ordner), Bildangaben kommen mit
  const photoSearch = photosView && searching && album == null
  // Eben gesetzte Markierung, Sterne, Farbe: gleich zeigen. Der Stand vom Server kommt beim Neuladen oft noch ohne die Änderung -
  // die Anzeige hinkte sonst einen Tastendruck hinterher. Gilt eine Minute, danach zählt wieder allein der Server.
  const [marked, setMarked] = useState<Record<string, { at: number; pick?: number; rating?: number; label?: string | null; title?: string; description?: string }>>({})
  const metaOf = useCallback((i: { path: string; name: string }) => {
    const base = (tlOn ? tl.metaOf(i.path) : photoSearch || isExt(i) || (photosView && album != null) ? extMeta[i.path] : meta ? meta.byKey[meta.scope === '*' ? i.path : i.name] : undefined) ?? linkedMeta[i.path]
    const m = marked[i.path]
    if (!base || !m || Date.now() - m.at > 60_000) return base
    const { at: _, ...change } = m
    return { ...base, ...change }
  }, [meta, extMeta, tlOn, tl.metaOf, photoSearch, linkedMeta, marked, photosView, album])
  // Eben gelöschte oder weggeschobene Einträge: gleich ausblenden, der Server arbeitet im Hintergrund. Ein laufender Abruf (Monat,
  // Ordner) kann sie noch enthalten - das überschreibt die Ausblendung nicht. Nach einer Minute zählt wieder allein der Server,
  // schlägt die Aktion fehl oder wird sie rückgängig gemacht, sind sie sofort wieder da
  const [gone, setGone] = useState<Map<string, number>>(() => new Map())
  const hideNow = (paths: string[], on: boolean) => setGone(g => {
    const n = new Map(g)
    for (const p of paths) { if (on) n.set(p, Date.now()); else n.delete(p) }
    return n
  })
  // Favorit: ebenso gleich zeigen
  const [favLocal, setFavLocal] = useState<Record<string, { at: number; on: boolean }>>({})
  const withFav = useCallback(<T extends Item>(list: T[]) => Object.keys(favLocal).length
    ? list.map(i => favLocal[i.path] && favLocal[i.path].on !== !!i.favorite ? { ...i, favorite: favLocal[i.path].on } : i) : list, [favLocal])
  // Abgelaufenes aufräumen (dann zeigt die Ansicht wieder den Stand vom Server)
  useEffect(() => {
    if (!gone.size && !Object.keys(favLocal).length) return
    const timer = setTimeout(() => {
      const old = Date.now() - 60_000
      setGone(g => [...g.values()].some(at => at < old) ? new Map([...g].filter(([, at]) => at >= old)) : g)
      setFavLocal(f => Object.values(f).some(x => x.at < old) ? Object.fromEntries(Object.entries(f).filter(([, x]) => x.at >= old)) : f)
    }, 61_000)
    return () => clearTimeout(timer)
  }, [gone, favLocal])
  // Die geladenen Fotos der Zeitleiste sind die Liste der Ansicht (Auswahl, Menüs, Betrachter arbeiten darauf)
  useEffect(() => { if (tlOn && tl.state && data?.items !== tl.items) setData({ items: tl.items }) })

  const go = useCallback((v: View) => { setSearch(''); setView(v); setExt(null); setArc(null) }, [])
  // Ordner-Tabs (Kennung foldertabs): je Fenster mehrere Ordner, gemerkt in den Einstellungen (links filesTabs0, rechts filesTabs1)
  type TabEntry = { view: View; ext?: { mount: string; path: string } | null }
  const tabsOn = featureOn('foldertabs') && comfortOf(prefs.filesComfort).tabs && !gallery && view.kind !== 'photos' && !mobile
  const tabsKey = pane ? 'filesTabs1' : 'filesTabs0'
  const [tabs, setTabs] = useState<{ list: TabEntry[]; i: number }>(() => {
    const saved = prefs[tabsKey] as { list: TabEntry[]; i: number } | undefined
    const here: TabEntry = { view, ext }
    if (!saved?.list?.length) return { list: [here], i: 0 }
    const i = Math.min(Math.max(0, saved.i), saved.list.length - 1)
    return { list: saved.list.map((x, n) => n === i ? here : x), i }
  })
  // Navigation ändert den gerade gewählten Tab
  useEffect(() => {
    if (!tabsOn) return
    setTabs(tb => {
      const cur = tb.list[tb.i]
      if (cur && JSON.stringify(cur.view) === JSON.stringify(view) && JSON.stringify(cur.ext ?? null) === JSON.stringify(ext ?? null)) return tb
      return { ...tb, list: tb.list.map((x, n) => n === tb.i ? { view, ext } : x) }
    })
  }, [JSON.stringify(view), JSON.stringify(ext), tabsOn]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (tabsOn) update({ [tabsKey]: tabs }) }, [JSON.stringify(tabs), tabsOn]) // eslint-disable-line react-hooks/exhaustive-deps
  const showTab = (e: TabEntry) => { go(e.view); if (e.ext) setExt(e.ext) }
  const pickTab = (i: number) => { const e = tabs.list[i]; if (!e) return; setTabs({ ...tabs, i }); showTab(e) }
  const newTab = (v: View, e?: TabEntry['ext']) => {
    setTabs(tb => ({ list: [...tb.list.slice(0, tb.i + 1), { view: v, ext: e ?? null }, ...tb.list.slice(tb.i + 1)], i: tb.i + 1 }))
    showTab({ view: v, ext: e })
  }
  const closeTab = (k: number) => {
    if (tabs.list.length < 2) return
    const list = tabs.list.filter((_, n) => n !== k)
    if (k === tabs.i) { const i = Math.min(k, list.length - 1); setTabs({ list, i }); showTab(list[i]) }
    else setTabs({ list, i: tabs.i > k ? tabs.i - 1 : tabs.i })
  }
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
  // Adresszeile (Kennung addressbar): Pfad eintippen, Zurück/Vor/Nach oben neben dem Pfad, Alt+Pfeiltasten
  const addressBar = featureOn('addressbar') && comfortOf(prefs.filesComfort).addressBar
  // Tasten wie im Norton/Midnight Commander (Kennung fkeys, in Einstellungen → Bedienung einschaltbar)
  const cmdKeys = featureOn('fkeys') && comfortOf(prefs.filesComfort).commander
  const typeAhead = featureOn('typeahead') && comfortOf(prefs.filesComfort).typeAhead && !photosView
  const typed = useRef({ text: '', at: 0 })
  // Zwei Fenster (Kennung dualpane, DualPane.tsx): rechts eine zweite Liste mit eigenem Ordner; nur Dateien, nicht auf dem Handy
  // Das rechte Fenster (pane) ist eine zweite FilesView: dort gilt „zwei Fenster“ immer, Aufbau und Seitenleiste macht das linke
  const dualAvail = pane ? true : featureOn('dualpane') && !gallery && !mobile && view.kind !== 'photos'
  const dual = pane ? true : dualAvail && !!prefs.filesDual
  const [active, setActive] = useState<0 | 1>(0)
  const [path2, setPath2] = useState(() => { try { return sessionStorage.getItem('dm.files.dual') ?? '' } catch { return '' } })
  // Stand des rechten Fensters (meldet es selbst): Hervorhebung in der Seitenleiste, Ziel für F5/F6
  const [pane2, setPane2] = useState<{ view: View; ext: { mount: string; path: string } | null; arc: boolean }>(() => ({ view: { kind: 'folder', path: path2 }, ext: null, arc: false }))
  const path2Live = pane2.ext || pane2.arc || pane2.view.kind !== 'folder' ? null : pane2.view.path
  const onPane2State = useCallback((st: { view: View; ext: { mount: string; path: string } | null; arc: boolean }) => {
    setPane2(st)
    if (!st.ext && st.view.kind === 'folder') { const p = st.view.path; setPath2(p); try { sessionStorage.setItem('dm.files.dual', p) } catch { /* ohne Speicher */ } }
  }, [])
  // Aufträge an das rechte Fenster (Seitenleiste, Alt+I, Strg+U), Neuladen nach Änderungen hier, Fokus (Tab), Platz für sein Menüband
  const [paneReq, setPaneReq] = useState<PaneLink['req']>(null)
  const paneGo = (v: View) => setPaneReq(r => ({ view: v, n: (r?.n ?? 0) + 1 }))
  const [paneTick, setPaneTick] = useState(0)
  const [paneFocus, setPaneFocus] = useState(0)
  const [ribbonSlot, setRibbonSlot] = useState<HTMLElement | null>(null)
  // Platz im Menüband für Abschnitte im Inhalt (Archiv, Atlas, Personen): Start und Ansicht
  const [secSlot, setSecSlot] = useState<HTMLElement | null>(null)
  const [secViewSlot, setSecViewSlot] = useState<HTMLElement | null>(null)
  const [detailsSlot, setDetailsSlot] = useState<HTMLElement | null>(null)
  // F10 schaltet zwei Fenster um, egal wo der Fokus liegt (etwa nach einem Klick ins Menüband) - nur nicht beim Tippen
  useEffect(() => {
    if (pane || !dualAvail || !cmdKeys) return
    const on = (e: KeyboardEvent) => {
      if (e.key !== 'F10' || e.ctrlKey || e.altKey || e.metaKey || e.shiftKey) return
      if (!/^#\/?files/.test(location.hash) || document.querySelector('[role=dialog]')) return
      const el = e.target as HTMLElement | null
      if (el && (el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName))) return
      e.preventDefault(); e.stopPropagation()
      update({ filesDual: !prefs.filesDual })
    }
    window.addEventListener('keydown', on, true)
    return () => window.removeEventListener('keydown', on, true)
  })
  // Aus: beim nächsten Einschalten beginnt das rechte Fenster ohne alte Aufträge
  useEffect(() => { if (!dual) { setActive(0); setPaneReq(null) } }, [dual])
  const dualTreeOwn = comfortOf(prefs.filesComfort).dualTree === 'own'
  /** Dieses Fenster ist das aktive (ohne zwei Fenster: immer) */
  const isActivePane = pane ? pane.active : !dual || active === 0
  /** Ordner des anderen Fensters (Ziel für F5/F6) */
  const otherPath = pane ? pane.otherPath : path2Live
  // Seitenleiste (nur im linken Fenster): steuert das aktive Fenster
  const toPane2 = !pane && dual && active === 1
  const sideGo = (v: View) => { if (toPane2) paneGo(v); else go(v) }
  const sideFolder = (p: string) => sideGo({ kind: 'folder', path: p })
  const sideExt = (e: { mount: string; path: string }) => { if (toPane2) setPaneReq(r => ({ ext: e, n: (r?.n ?? 0) + 1 })); else { setSearch(''); setExt(e) } }
  const navView = toPane2 ? pane2.view : view
  const navExt = toPane2 ? pane2.ext : ext
  const navPath = navView.kind === 'folder' ? navView.path : null
  /** F5/F6 mit zwei Fenstern: ohne Zielauswahl in den Ordner des anderen Fensters, nach kurzer Rückfrage */
  const toOther = async (paths: string[], target: string, copy: boolean) => {
    if (!paths.length) return
    const vars = { n: paths.length, name: nameOf(paths[0]), where: target ? '/' + target : t('Meine Dateien') }
    const text = copy ? (paths.length === 1 ? t('„{name}“ nach „{where}“ kopieren?', vars) : t('{n} Einträge nach „{where}“ kopieren?', vars))
      : (paths.length === 1 ? t('„{name}“ nach „{where}“ verschieben?', vars) : t('{n} Einträge nach „{where}“ verschieben?', vars))
    if (await askConfirm(copy ? t('Kopieren') : t('Verschieben'), text, copy ? t('Kopieren') : t('Verschieben'))) transfer(paths, target, copy)
  }
  /** Tasten für zwei Fenster wie im Midnight Commander (nur mit Commander-Tasten) */
  // Schnellansicht (Strg+Q): das inaktive Fenster zeigt die aktuelle Datei des aktiven
  const [quickOn, setQuickOn] = useState(false)
  const [quickItem, setQuickItem] = useState<Item | null>(null)
  useEffect(() => { if (!dual) setQuickOn(false) }, [dual])
  const dualKeys: DualKeys = pane ? pane.keys : {
    onSwitch: () => { const n = active === 0 ? 1 : 0; setActive(n); if (n === 1) setPaneFocus(x => x + 1); else focusList() },
    onToggle: () => update({ filesDual: !prefs.filesDual }),
    onOtherHere: p => { if (active === 0) paneGo({ kind: 'folder', path: p }); else openFolder(p) },
    onOtherOpen: p => { if (active === 0) paneGo({ kind: 'folder', path: p }); else openFolder(p) },
    onSwap: () => { if (path === null || ext || arc || path2Live === null) return; const l = path; openFolder(path2Live); paneGo({ kind: 'folder', path: l }) },
    onQuick: () => setQuickOn(q => !q),
  }
  /** Knopf an der Pfadleiste: diesen Ordner auch im anderen Fenster öffnen (gleich, welches Fenster aktiv ist) */
  const openInOther = (p: string) => { if (pane) pane.openInOther(p); else paneGo({ kind: 'folder', path: p }) }
  // Zweites Fenster: Aufträge von außen, Fokus (Tab), Stand zurückmelden
  useEffect(() => {
    const r = pane?.req
    if (!r) return
    if (r.ext) { setSearch(''); setArc(null); setExt(r.ext) }
    else if (r.view) go(r.view)
  }, [pane?.req?.n]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (pane?.focus) focusList() }, [pane?.focus]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { pane?.onState({ view, ext, arc: !!arc }) }, [view, ext, arc]) // eslint-disable-line react-hooks/exhaustive-deps
  const [cursor, setCursor] = useState<string | null>(null)
  const [pattern, setPattern] = useState<{ add: boolean; value: string } | null>(null)
  const applyPattern = (add: boolean, value: string) => {
    const parts = value.split(/[;,\s]+/).filter(Boolean)
    if (!parts.length) return
    const res = parts.map(x => new RegExp('^' + x.replace(/[.+^${}()|[\]\\]/g, '\\$&').replace(/\*/g, '.*').replace(/\?/g, '.') + '$', 'i'))
    const hit = new Set(items.filter(i => res.some(r => r.test(i.name))).map(i => i.path))
    setSelected(sl => add ? [...new Set([...sl, ...hit])] : sl.filter(x => !hit.has(x)))
  }
  const [editPath, setEditPath] = useState<string | null>(null)
  const goUp = () => openFolder(view.kind === 'folder' && path ? parentOf(path) : '')
  const goTyped = async (typed: string) => {
    const target = typed.replace(/\\/g, '/').split('/').map(x => x.trim()).filter(Boolean).join('/')
    try { if (target) await listDirs(target); setEditPath(null); openFolder(target) }
    catch { fail(new Error(t('Den Ordner „{path}“ gibt es nicht.', { path: '/' + target }))) }
  }
  useEffect(() => {
    if (!addressBar) return
    const onKey = (e: KeyboardEvent) => {
      if (!e.altKey || e.ctrlKey || e.metaKey || !isActivePane) return
      const el = e.target as HTMLElement | null
      if (el && (el.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName))) return
      if (e.key === 'ArrowLeft') { e.preventDefault(); histGo(-1) }
      else if (e.key === 'ArrowRight') { e.preventDefault(); histGo(1) }
      else if (e.key === 'ArrowUp') { e.preventDefault(); goUp() }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })
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
    sideFolder(p)
    if (mobile) closeSide()
  }
  // Ordner öffnen und die Datei darin markieren und zeigen („Ort“ in den Details, auch aus dem Betrachter der Galerie)
  const showFocus = (p: string) => {
    setSelected([p])
    reveal(p, 'center', 80)
  }
  // „Ort“ in den Details: in der Galerie (eigenes Modul) unter Dateien öffnen - die Galerie selbst zeigt keine Ordner und
  // bliebe sonst als Ordneransicht ohne Ordnerliste stehen
  // Galerie-Ordner („ext:…“): den externen Ordner unter Dateien
  const openPlace = (dir: string, focus?: string) => {
    const place = focus && isExt({ path: focus }) ? extPlace(focus) : null
    if (!gallery && !place) { openFolderAt(dir, focus); return }
    // Betrachter zu: die Galerie merkt sich #/gallery, nicht das Foto
    if (gallery && photoHash()) history.replaceState(null, '', '#/gallery')
    // Im zweiten Fenster ohne Adresse: den externen Ordner hier öffnen statt über den Hash (der gehört dem linken Fenster)
    if (place && pane) {
      const m = place.href.match(/^#\/files\/:ext\/([^/]+)\/?(.*)$/)
      if (m) { setSearch(''); setArc(null); setExt({ mount: decodeURIComponent(m[1]), path: m[2].split('/').filter(Boolean).map(decodeURIComponent).join('/') }) }
      return
    }
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
    // Zweites Fenster: die Adresse gehört dem linken
    if (pane) return
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
  // Auswertung läuft nur einmal je Foto gleichzeitig (Einhängen und hashchange kommen oft zusammen - das zweite sah dann schon
  // „Flag verbraucht“ und schrieb die Adresse auf #/gallery um); beim Zurückgehen aus dem Betrachter wird nichts neu geöffnet
  const photoBusy = useRef<string | null>(null)
  useEffect(() => {
    if (!gallery) return
    const on = async () => {
      const id = photoHash()
      if (!id || backingOut.current || photoBusy.current === id) return
      photoBusy.current = id
      try { await onPhotoHash(id) } finally { photoBusy.current = null }
    }
    const onPhotoHash = async (id: string) => {
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
  // Kategorien und Tags: Baum, Zuordnungen der Dateien im virtuellen Ordner, Dialoge
  const fileCats = useFileCats()
  const [catLabels, setCatLabels] = useState<Record<string, Labels>>({})
  // Die gezeigte Kategorie wurde gelöscht: zu „Meine Dateien“ statt „?“ und einer Fehlermeldung
  useEffect(() => {
    if (view.kind === 'cat' && catsLoaded() && !fileCats.cats.some(c => c.id === view.id)) go({ kind: 'folder', path: '' })
  }, [fileCats.cats]) // eslint-disable-line react-hooks/exhaustive-deps
  const [labelsFor, setLabelsFor] = useState<string[] | null>(null)
  // Markierte Fotos automatisch verbessern (Engine oder KI)
  const [autoFor, setAutoFor] = useState<Item[] | null>(null)
  // [feature:photoexport]
  // Markierte Fotos exportieren, in der Reihenfolge der Auswahl
  const [exportIds, setExportIds] = useState<string[] | null>(null)
  // [/feature:photoexport]
  const [infoFor, setInfoFor] = useState<Item[] | null>(null)
  const [manageCats, setManageCats] = useState(false)
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
      else if (view.kind === 'cat' || view.kind === 'tag') {
        // Gerade gelöschte Kategorie: nicht laden (der Effekt unten wechselt zu „Meine Dateien“)
        if (view.kind === 'cat' && catsLoaded() && !fileCats.cats.some(c => c.id === view.id)) return
        const r = await api.get<{ items: Item[]; labels: Record<string, Labels> }>(view.kind === 'cat'
          ? '/api/files/categories/' + encodeURIComponent(view.id) + '/items' : '/api/files/tags/items?' + q({ tag: view.tag }))
        if (n === req.current) setCatLabels(r.labels)
        d = { items: r.items }
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
  // Schnellsammlung (Kennung quickcoll): was drin ist, für den Kreis auf den Kacheln
  const qcOn = photosView && featureOn('quickcoll')
  const [quickSet, setQuickSet] = useState<Set<string>>(new Set())
  const loadQuick = useCallback(() => { albumsApi.quick().then(r => setQuickSet(new Set(r.paths))).catch(() => {}) }, [])
  useEffect(() => { if (qcOn) loadQuick() }, [qcOn, loadQuick])
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
  const hashWritten = useRef(false)
  useEffect(() => {
    // Ausgeblendet (anderer Bereich aktiv, die Shell hält die Bereiche): die Adresse gehört dem anderen Bereich; das zweite Fenster schreibt sie nie
    if (pane) return
    if (!(gallery ? /^#\/?(gallery|files\/:photos)/ : /^#\/?files/).test(location.hash)) return
    const h = arc ? arcHash(arc) : ext ? extHash(ext) : hashOf(view)
    // Die Galerie ist ein eigenes Modul: hin und zurück über den Hash, damit die Leiste links umschaltet
    if (gallery) { if (view.kind !== 'photos' || ext) location.hash = h; else if (!/^#\/?gallery/.test(location.hash)) history.replaceState(null, '', '#/gallery'); return }
    if (view.kind === 'photos' && !ext) { location.hash = '#/gallery'; return }
    // Ein Ordnerwechsel ist ein eigener Schritt im Verlauf: Zurück (auch Wischen am Handy) geht in den vorigen Ordner.
    // Nur beim ersten Zeichnen ersetzen (aus #/files wird der genaue Ordner, kein zusätzlicher Schritt)
    if (location.hash !== h) {
      if (hashWritten.current) history.pushState(null, '', h)
      else history.replaceState(null, '', h)
    }
    hashWritten.current = true
    try { sessionStorage.setItem('dm.files.last', h) } catch { /* ohne Speicher */ }
  }, [view, ext, arc]) // eslint-disable-line react-hooks/exhaustive-deps
  const loadUsage = () => api.get('/api/files/?path=').then(r => setUsage({ usage: r.usage, trash: r.trash, versions: r.versions, quota: r.quota, used: r.used })).catch(() => {})
  const trash = useTrash(view.kind === 'trash', fail, m => notify(m), () => { setTreeVersion(v => v + 1); loadUsage() }, view.kind === 'trash' ? term : '')
  const links = useLinks(view.kind === 'shared', fail)
  const changed = () => {
    // Aufnahmedaten mit neu holen (Titel, Beschreibung, Sterne aus dem Detailbereich), sonst zeigt er bis zum Neuladen den alten Stand
    setMeta(m => m && { ...m, count: -1 })
    load();setTreeVersion(v => v + 1); setDetailsVersion(v => v + 1); loadFavs(); if (view.kind !== 'folder') loadUsage()
    // Zwei Fenster: das andere lädt auch neu
    if (pane) pane.onChanged(); else if (dual) setPaneTick(n => n + 1)
  }
  changedRef.current = changed
  // DMS: Dateien als Dokumente übernehmen (Inhalt wird nach Ablageprofil abgelegt, die Datei kommt in den Papierkorb)
  const toDms = async (list: Item[]) => {
    if (!await askConfirm(t('Ins DMS aufnehmen'), list.length === 1 ? t('1 Datei wird als Dokument übernommen und nach dem Standard-Ablageprofil abgelegt. Die Datei hier kommt in den Papierkorb.') : t('{n} Dateien werden als Dokumente übernommen und nach dem Standard-Ablageprofil abgelegt. Die Dateien hier kommen in den Papierkorb.', { n: list.length }), t('Aufnehmen'))) return
    try {
      const r = await api.post<{ files: { name: string; duplicate?: boolean; skipped?: boolean; rejected?: boolean }[] }>('/api/dms/import-files', { paths: list.map(i => i.path) })
      const rejected = r.files.filter(f => f.rejected).map(f => f.name)
      if (rejected.length) notify(...rejectedText(rejected), 'warning')
      const dup = r.files.filter(f => f.duplicate).length
      const skipped = r.files.filter(f => f.skipped).length
      if (r.files.length > rejected.length) notify([importedText(r.files.length - dup - skipped - rejected.length, dup),
        skipped ? (skipped === 1 ? t('1 Datei ließ sich nicht aufnehmen (nur eigene Dateien)') : t('{n} Dateien ließen sich nicht aufnehmen (nur eigene Dateien)', { n: skipped })) : ''].filter(Boolean).join(', '))
      changed()
    } catch (e) { fail(e) }
  }
  // Aenderungen von anderen Geraeten (Handy, Explorer, Nextcloud): beim Zurueckkommen ins Fenster und alle 20 s
  // still nachladen - Auswahl und Ansicht bleiben
  const refresh = useRef<() => void>(() => {})
  refresh.current = () => { if (view.kind !== 'trash' && view.kind !== 'shared') load(); setTreeVersion(v => v + 1) }
  // In Ablage sichern: Zwischensymbol, solange der Server kopiert; danach neu laden (Wolke mit Haken)
  const savingDone = useCallback(() => refresh.current(), [])
  // Zweites Fenster: im linken wurde etwas geändert
  useEffect(() => { if (pane?.version) refresh.current() }, [pane?.version]) // eslint-disable-line react-hooks/exhaustive-deps
  const savingIds = useSaving(savingDone)
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
    const fav = withFav(data.items)
    if (tlOn) return fav
    const list = fav.filter(i => (showHidden || photosView || view.kind === 'trash' || !isHidden(i, flatList)) && (!filter || searching || kindOf(i) === filter) && (!photosView || !photoExt || extOf(i.name) === photoExt)
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
  }, [data, filter, searching, view.kind, sort, desc, recentSort, photosView, photoExt, favTop, folderTop, metaOf, showHidden, flatList, tlOn, withFav])
  // Aufgeklappte Stapel (Klick auf „2ד): ihre Fotos als einzelne Kacheln, ein Klick auf „1/3“ usw. klappt sie wieder zu
  const [stacksOpen, setStacksOpen] = useState<Set<string>>(new Set())
  const toggleStack = (lead: string) => setStacksOpen(o => { const n = new Set(o); if (n.has(lead)) n.delete(lead); else n.add(lead); return n })
  // Zeitleiste: eben markierte Fotos (Markierung, Sterne, Farbe), die die Filterregel jetzt ausblendet. Der Server filtert erst
  // beim nächsten Stand - bis dahin blendet die Ansicht sie selbst aus (Kacheln, Anzahl im Kopf, Auswahl, Betrachter)
  // Ebenso eben gelöschte oder weggeschobene (gone)
  const tlGone = useMemo(() => {
    const out = new Set<string>()
    if (!tlOn) return out
    for (const p of gone.keys()) out.add(p)
    if (!filterOn || !Object.keys(marked).length) return out
    for (const i of sortedItems) if (marked[i.path] && !filterPhotos([i], metaOf, ruleCols, ruleSel).length) out.add(i.path)
    return out
  }, [sortedItems, tlOn, filterOn, marked, metaOf, ruleCols.join(), JSON.stringify(ruleSel), gone]) // eslint-disable-line react-hooks/exhaustive-deps
  // Anzahl je Monat ohne die eben ausgeblendeten (nennt die Monatsliste schon einen neueren Stand, zählt sie ihn bereits)
  const tlCount = useCallback((b: { k: string; n: number }) => {
    if (!tlGone.size || !tl.currentOf(b.k)) return b.n
    let n = b.n
    for (const i of tl.loaded(b.k) ?? []) if (tlGone.has(i.path)) n--
    return Math.max(0, n)
  }, [tlGone, tl.currentOf, tl.loaded]) // eslint-disable-line react-hooks/exhaustive-deps
  const tlShown = useMemo(() => tl.state ? tl.state.shown - (tlGone.size ? tl.state.buckets.reduce((d, b) => d + b.n - tlCount(b), 0) : 0) : 0, [tl.state, tlGone, tlCount])
  const items = useMemo(() => {
    // Zeitleiste: aufgeklappte Stapel gehören dazu, sonst fehlen ihre weiteren Fotos in Auswahl, Details und Vorschau
    // Die Zeitleiste filtert der Server; eben geänderte Fotos (Markierung, Sterne, Farbe) hier gleich gegen die Regel prüfen,
    // sonst blieben sie bis zum nächsten Stand vom Server sichtbar
    if (tlOn) return openStacks(tlGone.size ? sortedItems.filter(i => !tlGone.has(i.path)) : sortedItems, stacksOpen)
    const left = gone.size ? sortedItems.filter(i => !gone.has(i.path)) : sortedItems
    const list = filterOn ? filterPhotos(left, metaOf, ruleCols, ruleSel) : left
    // Galerie: JPG + RAW als Stapel
    return photosView && (prefs.photoStack ?? true) ? openStacks(stackPhotos(list), stacksOpen) : list
  }, [sortedItems, filterOn, metaOf, ruleCols.join(), JSON.stringify(ruleSel), photosView, prefs.photoStack, tlOn, stacksOpen, tlGone, gone]) // eslint-disable-line react-hooks/exhaustive-deps
  // Gruppieren nach Art oder Aenderungsdatum (wie im Explorer); die Liste bleibt in Anzeigereihenfolge
  const groupBy = fv?.group ?? prefs.fileGroup ?? 'none'
  // Kachelgröße stufenlos (Schieberegler); ab OVERLAY_PX stehen die Bildinfos auf dem Bild statt darunter
  const tileSize = prefs.fileTile ?? 'medium'
  const tileNum = fv?.tilePx ?? prefs.fileTilePx ?? (tileSize === 'small' ? 96 : tileSize === 'large' ? 220 : 132)
  // Kachelmodus der Galerie (Kennung tilemodes); ohne Wahl die bisherige Kachel. Leiste nur im Modus „Voll“ (Kennung tilebar)
  const tileMode = photosView && featureOn('tilemodes') ? (prefs.photoTileMode === 'mosaic' && !featureOn('mosaicweb') ? undefined : prefs.photoTileMode) : undefined
  // Mosaik (Kennung mosaicweb): Zeilen gleicher Höhe nach Seitenverhältnis statt Raster
  const mosaic = tileMode === 'mosaic'
  // Kopfzeile der Kachel frei belegt (Kennung tilehead); ohne eigene Belegung die Vorgabe
  const tileHead = useMemo(() => featureOn('tilehead') && prefs.photoTileHead ? tileHeadOf(prefs.photoTileHead) : undefined, [prefs.photoTileHead])
  const barOn = tileMode === 'full' && featureOn('tilebar')
  // Im Modus „Voll“ nie schmaler als die Leiste: abgeschnittene Knöpfe wären nicht zu erreichen
  const tilePx = (barOn ? Math.max(tileNum, BAR_MIN_W) : tileNum) + 'px'
  const infoOnImage = photosView && !mobile && tileNum >= OVERLAY_PX && !tileMode
  // Fotos sind eine Zeitleiste: nach Monat gruppiert, neueste zuerst (der Standard der Ansicht)
  const photoGroup = prefs.photoGroup ?? 'month'
  // Zeitleiste nach Name oder Größe: Seiten, keine Abschnitte nach Datum
  const tlTime = sort === 'taken' || sort === 'modified'
  const grouping = photosView ? photoGroup !== 'none' && (!tlOn || tlTime || photoGroup === 'source' || photoGroup === 'category' || photoGroup === 'tag') : groupBy !== 'none' && !(view.kind === 'recent' && !searching)
  const groupOf = useCallback((i: Item): [number, string] => {
    if (photosView) {
      // Zeitleiste: wie der Server einsortiert hat (siehe placedOf), sonst stünde ein beim Ansehen erst gelesenes Datum im falschen Monat
      // Noch nicht ausgelesen: eigener Abschnitt am Ende (galunread), das Dateidatum wäre irreführend
      if (tlOn && tl.unreadOf(i.path)) return [Number.MAX_SAFE_INTEGER, t('Wird noch ausgelesen')]
      const d = new Date((tlOn ? tl.placedOf(i.path) : undefined) ?? takenOf(i, metaOf(i)))
      const start = photoGroup === 'year' ? new Date(d.getFullYear(), 0, 1) : photoGroup === 'day' ? new Date(d.getFullYear(), d.getMonth(), d.getDate()) : new Date(d.getFullYear(), d.getMonth(), 1)
      const label = photoGroup === 'year' ? String(d.getFullYear()) : dateLabel(start, photoGroup === 'day', locale)
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
  }, [groupBy, photosView, photoGroup, photoDesc, favTop, folderTop, metaOf, tlOn, tl.placedOf, tl.unreadOf])
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
  // Kacheln und Zeilen portionsweise zeigen und beim Scrollen nachladen - Tausende Einträge auf einmal machen die Seite zaeh
  const step = viewMode === 'tiles' ? TILE_STEP : LIST_STEP
  const [cap, setCap] = useState(step)
  // Zeitleiste der Galerie (nach Datum sortiert): Monate in Anzeigereihenfolge mit ihrem ersten Eintrag
  const timeline = photosView && (sort === 'taken' || sort === 'modified') && !searching
  const months = useMemo(() => {
    if (!timeline) return []
    if (tlOn) { let first = 0; return (tl.state?.buckets ?? []).map(b => { const m = { key: b.k, first, group: b.l }; first += tlCount(b); return m }) }
    const out: { key: string; first: number; group?: string }[] = []
    shown.forEach((i, n) => {
      const d = new Date(sort === 'taken' ? takenOf(i, metaOf(i)) : Date.parse(i.modified))
      const key = `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`
      if (!out.length || out[out.length - 1].key !== key) out.push({ key, first: n })
    })
    return out
  }, [timeline, shown, sort, metaOf, tlOn, tl.state, tlCount])
  const jumpTo = (index: number, final = true) => {
    if (windowed) {
      const el = listRef.current
      const b = blocksRef.current.list
      const n = b.findIndex(x => !x.head && index >= x.start && index < x.start + x.count)
      if (n < 0 || !el) return
      // Beim Ziehen an der Jahresleiste: gleich dorthin (Platzhalter), geladen wird, wenn es kurz steht
      if (!final) dragAt.current = Date.now()
      if (!tlOn || !final) { pendingJump.current = null; land(index, final); return }
      // Losgelassen: erst die Monate laden, die nach dem Sprung im Bild stehen, dann springen - die Ansicht bleibt bis dahin,
      // wie sie ist, statt erst leer, dann mit Platzhaltern zu erscheinen
      const top = blockTop(b, n)
      const keys = [...new Set(b.filter(x => x.bucket && x.off + x.h > top && x.off < top + el.clientHeight * 1.2).map(x => x.bucket!))]
      pendingJump.current = { index, keys, until: Date.now() + 1500 }
      tl.want(keys, 'jump')
      tryJump()
      setTimeout(tryJump, 1550)
      return
    }
    setCap(c => Math.max(c, index + step))
    const target = shown[index]?.path
    if (target) setTimeout(() => document.querySelector(`[data-path="${CSS.escape(target)}"]`)?.scrollIntoView({ block: 'start' }), 60)
  }
  // Neue Liste: wieder vom Anfang - der gewählte Eintrag (etwa „Ort“ aus den Details) bleibt dabei gezeichnet
  useEffect(() => {
    const p = selected[selected.length - 1]
    const at = p ? shown.findIndex(i => i.path === p) : -1
    setCap(Math.max(step, at + step))
  }, [data, viewMode]) // eslint-disable-line react-hooks/exhaustive-deps
  /** Eintrag ins Bild holen; liegt er hinter dem bisher Gezeichneten, erst so weit nachladen. */
  const reveal = (p: string, block: ScrollLogicalPosition, delay = 0) => {
    const at = shown.findIndex(i => i.path === p)
    const later = at >= cap
    if (later) setCap(c => Math.max(c, at + step))
    const go = () => mainRef.current?.querySelector(`[data-path="${CSS.escape(p)}"]`)?.scrollIntoView({ block })
    if (later || delay) setTimeout(go, Math.max(delay, 60)); else go()
  }
  const cappedGroups = useMemo(() => {
    let left = cap
    return groups.map(g => { const part = g.items.slice(0, Math.max(0, left)); left -= part.length; return { ...g, total: g.items.length, items: part } })
      .filter(g => g.items.length > 0)
  }, [groups, cap])

  // Galerie: nur zeichnen, was sichtbar ist (wie Immich). Die Abschnitte werden in Blöcke zu je einigen Reihen geteilt; gezeichnet
  // werden die Blöcke im Bild plus etwas davor und danach, für den Rest steht nur leerer Platz in der passenden Höhe. Sonst
  // zeichnet React bei zehntausenden Fotos bei jedem Nachladen alle bisherigen Kacheln neu, und das Scrollen hängt Sekunden.
  const windowed = photosView && viewMode === 'tiles'
  // Sprung über die Jahresleiste: wartet, bis die Monate im Ziel geladen sind (höchstens bis until); scrollTo: im nächsten Zeichnen setzen
  const pendingJump = useRef<{ index: number; keys: string[]; until: number; pre?: boolean } | null>(null)
  const scrollTo = useRef<{ top: number; quiet: boolean } | null>(null)
  const dragAt = useRef(0)
  const [dims, setDims] = useState({ row: 0, head: 34, gap: 8 })
  const blocks = useMemo(() => {
    if (!windowed) return { list: [] as Block[], total: 0 }
    const min = mobile ? 104 : parseInt(tilePx)
    const gap = mosaic ? MOSAIC_GAP : mobile ? 4 : dims.gap
    const inner = Math.max(min, listW - (mobile ? 8 : 32))
    // Mosaik: Zielhöhe der Zeilen = Kachelgröße (am Handy klein), Seitenverhältnis aus den Maßen (fehlt es: 1,5)
    const mTarget = mobile ? 96 : Math.max(80, tileNum)
    const ratioFor = (i: Item) => { const m = metaOf(i); return ratioOf(m?.width, m?.height, !!i.mime?.startsWith('video')) }
    const cols = Math.max(1, Math.floor((inner + gap) / (min + gap)))
    // Ohne Messung: Kachel etwa quadratisch plus Bildinfos
    const row = dims.row || Math.round(inner / cols * 0.75 + (tileMode === 'full' ? HEAD_H + 34 : tileMode === 'image' ? 4 : 30 + (infoOnImage ? 0 : tileInfo.length * 14)) + gap)
    const per = cols * 8
    const list: Block[] = []
    let off = 0, start = 0
    const rowsH = (n: number) => mosaic ? estimateHeight(n, inner, mTarget, gap) : Math.ceil(n / cols) * row - gap + 4
    // Teilstücke eines Abschnitts: Raster je `per` Fotos; Mosaik je acht Zeilen (die Zeilen über den ganzen Abschnitt, damit keine Zeile am Stückende ausfranst)
    const chunks = (its: Item[]): { k: number; items: Item[]; h: number; rows?: MosaicRow[] }[] => {
      if (!mosaic) { const out = []; for (let k = 0; k < its.length; k += per) { const part = its.slice(k, k + per); out.push({ k, items: part, h: rowsH(part.length) }) } return out }
      const all = mosaicRows(its.map(ratioFor), inner, mTarget, gap)
      const out = []
      for (let a = 0; a < all.length; a += 8) {
        const part = all.slice(a, a + 8), k = part[0].start
        const rows = part.map(r => ({ ...r, start: r.start - k }))
        out.push({ k, items: its.slice(k, k + part.reduce((n, r) => n + r.count, 0)), h: rowsHeight(rows, gap), rows })
      }
      return out
    }
    if (tlOn) {
      // Je Monat: geladen - Abschnitte (Tag, Monat, Jahr) mit Kacheln; sonst Platz in der Höhe, die seine Anzahl braucht.
      // Eine Überschrift nur, wenn sie sich von der vorigen unterscheidet (Jahr über mehrere Monate)
      let last = ''
      const buckets = tl.state?.buckets ?? []
      const yearCount = new Map<string, number>()
      for (const b of buckets) yearCount.set(b.k.slice(0, 4), (yearCount.get(b.k.slice(0, 4)) ?? 0) + tlCount(b))
      // Nach Quelle: Name vom Server je Abschnitt, Anzahl über alle Abschnitte der Quelle
      // Nach Quelle, Kategorie oder Tag: Name der Gruppe vom Server
      const bySource = photoGroup === 'source' || photoGroup === 'category' || photoGroup === 'tag'
      const sourceCount = new Map<string, number>()
      if (bySource) for (const b of buckets) sourceCount.set(t(b.l ?? ''), (sourceCount.get(t(b.l ?? '')) ?? 0) + tlCount(b))
      const monthName = (k: string) => dateLabel(new Date(+k.slice(0, 4), +k.slice(5) - 1, 1), false, locale)
      for (const b of buckets) {
        const unreadPart = isUnreadKey(b.k)
        const loaded = tl.loaded(b.k)
        const its = loaded && openStacks(withFav(tlGone.size ? loaded.filter(i => !tlGone.has(i.path)) : loaded), stacksOpen)
        const n = tlCount(b)
        if (!its) {
          const label = !grouping ? '' : bySource ? t(b.l ?? '') : unreadPart ? t('Wird noch ausgelesen') : photoGroup === 'year' ? b.k.slice(0, 4) : monthName(b.k)
          const head = !!label && label !== last
          if (label) last = label
          // Raster: genau so hoch wie der geladene Monat (gleiche Teilstücke), dann verschiebt das Laden nichts
          const h = (head ? dims.head : 0) + (mosaic ? rowsH(n) : Math.floor(n / per) * rowsH(per) + (n % per ? rowsH(n % per) : 0))
          list.push({ key: 'ph:' + b.k, label: head ? label : '', head: false, ph: true, items: [], start, off, h, count: n, bucket: b.k })
          off += h; start += n
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
            const count = bySource ? sourceCount.get(g.label) ?? g.items.length : unreadPart ? n : photoGroup === 'year' ? yearCount.get(b.k.slice(0, 4)) ?? g.items.length : photoGroup === 'month' ? n : g.items.length
            list.push({ key: 'h:' + b.k + ':' + g.label, label: g.label, head: true, items: g.items, start, off, h: dims.head, count, bucket: b.k }); off += dims.head
          }
          if (g.label) last = g.label
          for (const c of chunks(g.items)) {
            list.push({ key: b.k + ':' + g.label + ':' + c.k, label: g.label, head: false, items: c.items, start: start + c.k, off, h: c.h, count: c.items.length, bucket: b.k, rows: c.rows })
            off += c.h
          }
          start += g.items.length
        }
      }
      return { list, total: off }
    }
    for (const g of groups) {
      if (g.label) { list.push({ key: 'h:' + g.label, label: g.label, head: true, items: g.items, start, off, h: dims.head, count: g.items.length }); off += dims.head }
      // Reihen samt Abstand, unter der letzten Reihe keiner; dazu der obere Rand des Rasters
      for (const c of chunks(g.items)) {
        list.push({ key: g.label + ':' + c.k, label: g.label, head: false, items: c.items, start: start + c.k, off, h: c.h, count: c.items.length, rows: c.rows })
        off += c.h
      }
      start += g.items.length
    }
    return { list, total: off }
  }, [windowed, groups, listW, mobile, tilePx, dims, tileInfo.length, infoOnImage, tileMode, mosaic, tileNum, metaOf, tlOn, tl.state, tl.loaded, grouping, photoGroup, groupOf, stacksOpen, tlGone, tlCount, withFav])
  const blocksRef = useRef(blocks)
  blocksRef.current = blocks
  // Gezeichneter Bereich aus Scrollposition und Aufbau, im selben Zeichnen berechnet: als gemerkte Blocknummern zeigte er nach
  // jedem geladenen Monat ein Bild lang die falschen Blöcke (die Nummern verschieben sich, wenn aus einem Platzhalter Kacheln werden)
  const [win, setWin] = useState({ top: 0, h: 0 })
  const range = useMemo<[number, number]>(() => win.h ? rangeAt(blocks.list, win.top, win.h) : [0, 12], [blocks, win])
  const prevBlocks = useRef(blocks)
  const visibleRange = useCallback(() => {
    const el = listRef.current
    const b = blocksRef.current.list
    // Ein Sprung steht noch aus (Bereich schon gesetzt, Scrollposition folgt im selben Zeichnen): nicht mit der alten Position überschreiben
    if (!el || b.length === 0 || !el.getClientRects().length || scrollTo.current) return
    const top = el.scrollTop, h = el.clientHeight
    const [a, z] = rangeAt(b, top, h)
    setWin(v => { if (v.h === h) { const [x, y] = rangeAt(b, v.top, v.h); if (x === a && y === z) return v } return { top, h } })
  }, [])
  // An die Stelle des Eintrags: gezeichneter Bereich und Scrollposition in einem Schritt (sonst steht ein Bild lang der leere
  // Platz davor im Bild, bis das Scrollen den Bereich nachzieht)
  // quiet: Vorschaubilder gleich laden (Ziel erreicht); beim Ziehen warten sie wie beim Scrollen, bis es kurz steht
  const land = (index: number, quiet = true) => {
    const el = listRef.current
    const b = blocksRef.current.list
    const n = b.findIndex(x => !x.head && index >= x.start && index < x.start + x.count)
    if (n < 0 || !el) return
    const top = Math.min(blockTop(b, n), Math.max(0, blocksRef.current.total - el.clientHeight))
    scrollTo.current = { top, quiet }
    setWin({ top, h: el.clientHeight })
  }
  useLayoutEffect(() => {
    const el = listRef.current
    if (scrollTo.current == null || !el) return
    if (scrollTo.current.quiet) quietScroll(el, scrollTo.current.top); else el.scrollTop = scrollTo.current.top
    scrollTo.current = null
  }, [win])
  // Sprung wartet auf seine Monate: geladen (oder zu lange gewartet) - jetzt springen
  // Danach noch die Vorschaubilder im Ziel vorladen (kurz), dann erscheint der Monat gleich mit Bildern statt mit leeren Kacheln
  const tryJump = () => {
    const j = pendingJump.current
    if (!j || !tlOn || j.pre) return
    if (Date.now() < j.until && j.keys.some(k => !tl.loaded(k))) return
    j.pre = true
    const go = () => { if (pendingJump.current !== j) return; pendingJump.current = null; land(j.index) }
    const el = listRef.current
    const b = blocksRef.current.list
    const n = b.findIndex(x => !x.head && j.index >= x.start && j.index < x.start + x.count)
    if (!el || n < 0 || Date.now() >= j.until) { go(); return }
    const top = blockTop(b, n), urls: string[] = []
    for (const x of b) if (!x.ph && !x.head && x.off < top + el.clientHeight && x.off + x.h > top)
      for (const i of x.items) if (hasThumb(i) && urls.length < 150) urls.push(galleryTileSource.thumb(i, thumbPxFor(tileNum, extOffline(i))))
    void preloadThumbs(urls, 500).then(go)
  }
  useEffect(() => { tryJump() }, [tl.version]) // eslint-disable-line react-hooks/exhaustive-deps
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
    // Ganz oben bleibt ganz oben: kommt ein neuerer Monat dazu (etwa noch nicht ausgelesene Fotos nach ihrem Dateidatum), zeigt
    // die Ansicht ihn, statt mit dem bisher obersten Monat nach unten zu rutschen
    if (el.scrollTop < 4) return
    const n = old.findIndex(x => x.off + x.h > el.scrollTop)
    if (n < 0) return
    // Gibt es den Monat im Bild nicht mehr (seine Fotos wurden nach dem Lesen des Aufnahmedatums umsortiert): am nächsten noch
    // vorhandenen festhalten, erst darunter, sonst darüber - statt die Ansicht an eine ganz andere Stelle springen zu lassen
    const exists = new Set(blocks.list.map(x => x.bucket).filter(Boolean))
    const ref = old.slice(n).find(x => x.bucket && exists.has(x.bucket)) ?? old.slice(0, n).reverse().find(x => x.bucket && exists.has(x.bucket))
    if (!ref?.bucket) return
    const was = old.find(x => x.bucket === ref.bucket)!.off
    const now = blocks.list.find(x => x.bucket === ref.bucket)
    if (now && now.off !== was) {
      quietScroll(el, el.scrollTop + now.off - was)
      // Bereich gleich mitziehen (noch vor dem Zeichnen), sonst steht ein Bild lang der Inhalt der alten Höhe im Bild
      setWin(v => ({ top: v.top + now.off - was, h: v.h }))
    }
  }, [blocks]) // eslint-disable-line react-hooks/exhaustive-deps
  // Nach jedem Zeichnen: deckt der gezeichnete Bereich die Scrollposition nicht (Sprung und geladener Monat im selben Zeichnen,
  // Bereich aus einem älteren Aufbau), gleich nachziehen - noch vor dem Malen, sonst steht kurz nur leerer Platz im Bild
  useLayoutEffect(() => {
    const el = listRef.current, b = blocks.list
    if (!windowed || !el || !b.length || scrollTo.current || !el.getClientRects().length) return
    const a = b[range[0]], z = b[Math.min(range[1], b.length - 1)]
    const top = el.scrollTop, h = el.clientHeight
    if (!a || !z || (top >= a.off - 1 && top + h <= z.off + z.h + 1)) return
    const [x, y] = rangeAt(b, top, h)
    if (x !== range[0] || y !== range[1]) setWin({ top, h })
  })
  // Zeitleiste: die Monate im Bild (der gezeichnete Bereich reicht schon gut eine Bildschirmhöhe darüber hinaus) und je einer davor
  // und danach laden - erst, wenn das Scrollen kurz steht (beim Ziehen an der Jahresleiste nicht jeden Monat unterwegs)
  // Beim Scrollen höchstens alle 150 ms neu wünschen (so kommen die Monate schon unterwegs), beim Ziehen an der Jahresleiste erst,
  // wenn es kurz steht
  const lastWant = useRef(0)
  useEffect(() => {
    if (!tlOn || !tl.state) return
    const wait = Date.now() - dragAt.current < 400 ? 150 : Math.max(0, 150 - (Date.now() - lastWant.current))
    const h = setTimeout(() => {
      lastWant.current = Date.now()
      const keys = tl.state!.buckets.map(x => x.k)
      const at = new Map(keys.map((k, n) => [k, n]))
      const b = blocksRef.current.list
      const el = listRef.current
      const seen: number[] = [], inView: string[] = []
      for (let n = range[0]; n <= range[1] && n < b.length; n++) {
        const i = b[n].bucket != null ? at.get(b[n].bucket!) : undefined
        if (i == null) continue
        seen.push(i)
        if (el && b[n].off + b[n].h > el.scrollTop && b[n].off < el.scrollTop + el.clientHeight) inView.push(b[n].bucket!)
      }
      if (!seen.length) { if (keys.length) tl.want([keys[0]]); return }
      const lo = Math.max(0, Math.min(...seen) - 1), hi = Math.min(keys.length - 1, Math.max(...seen) + 1)
      // Was im Bild ist, zuerst
      tl.want([...new Set([...inView, ...keys.slice(lo, hi + 1)])])
      if (!pendingJump.current) tl.want([], 'jump')
    }, wait)
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
    const list = openStacks(tl.items, stacksOpen).filter(x => !x.isDir && viewKind(x))
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
    const gap = mosaic ? MOSAIC_GAP : parseFloat(getComputedStyle(grid).rowGap) || 8
    // Im Mosaik stehen die Höhen fest im Layout; die Reihenhöhe des Rasters bleibt unverändert (gilt wieder nach dem Zurückschalten)
    const row = mosaic ? dims.row : Math.round(tile.offsetHeight + gap)
    const h = head ? head.offsetHeight : dims.head
    if (Math.abs(row - dims.row) > 1 || Math.abs(h - dims.head) > 1 || gap !== dims.gap) setDims({ row, head: h, gap })
  })
  // In der Ansicht Fotos gilt eine eigene Reihenfolge (Vorgabe: neueste zuerst), die der Ordner bleibt unberuehrt
  const setSort = (patch: { sort?: SortKey; desc?: boolean }) => photosView
    ? update({ ...(patch.sort ? { photoSort: patch.sort as 'name' | 'modified' | 'size' | 'taken' } : {}), ...(patch.desc !== undefined ? { photoSortDesc: patch.desc } : {}) })
    : setFolderView({ ...(patch.sort ? { sort: patch.sort as NonNullable<typeof fv>['sort'] } : {}), ...(patch.desc !== undefined ? { desc: patch.desc } : {}) },
      { ...(patch.sort ? { fileSort: patch.sort } : {}), ...(patch.desc !== undefined ? { fileSortDesc: patch.desc } : {}) })
  const sortBy = (k: SortKey) => k === sort ? setSort({ desc: !desc }) : setSort({ sort: k, desc: k === 'modified' || k === 'size' || k === 'created' || k === 'taken' })
  // Gewählte Einträge in Listenreihenfolge. Zeitleiste: fällt ein Monat aus dem Speicher (weit weggescrollt, gesprungen), bleiben
  // die gewählten Fotos daraus gewählt und im Detailbereich - sonst hob jeder Sprung über die Jahresleiste die Auswahl scheinbar auf
  const itemAt = useMemo(() => new Map(items.map((i, n) => [i.path, n])), [items])
  const selKnown = useRef(new Map<string, Item>())
  const sel = useMemo(() => {
    const found: Item[] = [], kept = new Map<string, Item>(), rest: Item[] = []
    for (const p of selected) {
      const n = itemAt.get(p)
      const i = n != null ? items[n] : tlOn && !tlGone.has(p) ? selKnown.current.get(p) : undefined
      if (!i) continue
      kept.set(p, i)
      if (n != null) found.push(i); else rest.push(i)
    }
    selKnown.current = kept
    found.sort((a, b) => itemAt.get(a.path)! - itemAt.get(b.path)!)
    return [...found, ...rest]
  }, [items, itemAt, selected, tlOn, tlGone])
  // Liste ↔ Kacheln umgeschaltet: der gewählte Eintrag bleibt im Bild
  const lastMode = useRef(viewMode)
  useEffect(() => {
    if (lastMode.current === viewMode) return
    lastMode.current = viewMode
    const p = selected[selected.length - 1]
    if (p) reveal(p, 'center', 80)
  }, [viewMode]) // eslint-disable-line react-hooks/exhaustive-deps
  // Galerie: Markierung, Sterne, Farbe für die gewählten Fotos setzen (Setter-Leiste, Tasten). Blendet die Filter-Leiste das
  // Ergebnis aus, verschwinden die Fotos sofort und das nächste sichtbare wird gewählt.
  // Nacheinander: schnell hintereinander gedrückte Tasten träfen sonst auf einen Stand der Datei, der gerade neu geschrieben wird
  const markQueue = useRef<Promise<void>>(Promise.resolve())
  // Sterne, Farbe, Markierung: sofort zeigen (Kacheln, Betrachter, rechte Leiste), die Dateien schreibt der Server danach.
  // Schlägt das Schreiben fehl, kommt der vorige Stand zurück und eine Meldung
  const markPhotos = (body: { pick?: number; rating?: number; label?: string; title?: string; description?: string }, list: Item[] = sel) => {
    const photos = list.filter(i => !i.isDir)
    if (!photos.length) return Promise.resolve()
    const change = { ...(body.pick != null ? { pick: body.pick } : {}), ...(body.rating != null ? { rating: body.rating } : {}), ...(body.label != null ? { label: body.label || null } : {}),
      ...(body.title != null ? { title: body.title } : {}), ...(body.description != null ? { description: body.description } : {}) }
    // Gegen die ganze Filterregel prüfen (Filterleiste und schnelle Leiste), nicht nur die schnelle Leiste
    const hides = filterOn && photos.some(i => !filterPhotos([i], x => ({ ...metaOf(x), ...change }) as Meta, ruleCols, ruleSel).length)
    const paths = new Set(photos.map(i => i.path))
    const last = Math.max(...photos.map(i => items.findIndex(x => x.path === i.path)))
    const next = hides ? items.slice(last + 1).find(i => !paths.has(i.path)) ?? [...items.slice(0, last)].reverse().find(i => !paths.has(i.path)) : undefined
    let before: typeof marked = {}
    setMarked(m => {
      before = m
      const n = { ...m }
      for (const i of photos) n[i.path] = { ...n[i.path], ...change, at: Date.now() }
      return n
    })
    if (hides) setSelected(next ? [next.path] : [])
    markQueue.current = markQueue.current.then(() => markNow(body, photos, before))
    return markQueue.current
  }
  const markNow = async (body: { pick?: number; rating?: number; label?: string; title?: string; description?: string }, photos: Item[], before: typeof marked) => {
    let failed: { name: string; error: string }[] = []
    try {
      for (let k = 0; k < photos.length; k += 50) {
        const r = await api.post<{ failed: { name: string; error: string }[] }>('/api/gallery/info', { items: photos.slice(k, k + 50).map(i => i.path), ...body })
        failed.push(...(r.failed ?? []))
      }
    } catch (e) { fail(e); failed = photos.map(i => ({ name: i.name, error: '' })) }
    if (failed.length) {
      if (failed.some(f => f.error)) fail(new Error(failedText(failed.filter(f => f.error))))
      // Nicht gespeichert: den vorigen Stand wieder zeigen
      const back = photos.filter(i => failed.some(f => f.name === i.name))
      setMarked(m => {
        const n = { ...m }
        for (const i of back) { if (before[i.path]) n[i.path] = before[i.path]; else delete n[i.path] }
        return n
      })
    }
    // Stand vom Server nachholen, ohne dass jemand darauf wartet
    if (tlOn) tl.refresh(); else changed()
    setDetailsVersion(v => v + 1)
  }
  // Atlas: sind Fotos markiert, nur diese - und für sie lässt sich der Ort von Hand setzen
  const placeSelected = async (list: Item[], lat: number, lon: number) => {
    const has = list.filter(i => metaOf(i)?.lat != null).length
    if (!await askConfirm('', (list.length === 1 ? t('Ort für „{name}“ hier setzen?', { name: list[0].name }) : t('Ort für {n} Fotos hier setzen?', { n: list.length }))
      + (has ? ' ' + t('{n} davon haben schon einen Ort - er wird ersetzt.', { n: has }) : ''))) return
    const bad: { name: string; error: string }[] = []
    try {
      for (let k = 0; k < list.length; k += 50) {
        const r = await api.post<{ failed: { name: string; error: string }[] }>('/api/gallery/info', { items: list.slice(k, k + 50).map(i => i.path), lat, lon })
        bad.push(...r.failed)
      }
      if (bad.length) fail(new Error(failedText(bad)))
      else notify(t('Ort gesetzt.'))
    } catch (e) { fail(e) }
    changed()
  }
  const onlySelected = (list: Item[]) => { if (!selected.length) return list; const set = new Set(selected); return list.filter(i => set.has(i.path)) }
  const one = sel.length === 1 ? sel[0] : null
  // Strg+Q: aktuelle Datei (Cursor, sonst die einzige markierte) an die Schnellansicht im anderen Fenster
  const quickCur = items.find(x => x.path === (cursor ?? anchor.current)) ?? one ?? null
  // Ordner vergleichen: eigene Liste melden, beim Ereignis markieren, was im anderen Fenster fehlt oder anders ist
  useEffect(() => { if (dual) paneLists[pane ? 1 : 0] = items }, [items, dual]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!dual) return
    const on = () => {
      const marked = differs(items, paneLists[pane ? 0 : 1])
      setSelected(marked); anchor.current = marked[0] ?? null
      if (!pane) setTimeout(() => notify(t('Ordner verglichen'), t('Markiert ist, was im anderen Fenster fehlt, neuer ist oder eine andere Größe hat.')), 0)
    }
    window.addEventListener(PANE_COMPARE, on)
    return () => window.removeEventListener(PANE_COMPARE, on)
  })
  useEffect(() => {
    if (!dual) return
    if (pane) { if (pane.active) pane.onCurrent(quickCur) } else if (active === 0) setQuickItem(quickCur)
  }, [quickCur?.path, quickCur?.etag, dual, pane?.active, active]) // eslint-disable-line react-hooks/exhaustive-deps
  const flat = view.kind !== 'folder' || searching   // Liste aus mehreren Ordnern: Spalte "Ort"
  // Spalten der Detailansicht (FileColumns): beim Ziehen erst nur hier, beim Loslassen gespeichert
  const colCfg = live?.cols ?? cleanCols(prefs.fileCols as ColPref[] | undefined)
  const nameWidth = live ? live.name : prefs.fileNameWidth
  const filler = cols >= 2 && !!nameWidth
  const shownCols: ColPref[] = cols < 2 ? [] : flat && !colCfg.some(c => c.key === 'place') ? [{ key: 'place', width: COLS.place.width }, ...colCfg] : colCfg
  // Gemerkte Namensbreite passt nicht (etwa im Zweifenster): „Name“ gibt bis 220 px nach, die übrigen Spalten bleiben sichtbar
  const nameShown = live || !nameWidth ? nameWidth : Math.min(nameWidth, Math.max(220, listW - 32 - 36 - 44 - shownCols.reduce((n, c) => n + c.width, 0)))
  const saveCols = (c: ColPref[], name = nameWidth) => { setLive(null); update({ fileCols: c, fileNameWidth: name || undefined }) }
  // Aufnahmedaten: in der Galerie immer, in einem Ordner nur mit einer ihrer Spalten, beim Sortieren nach Aufnahmedatum oder bei offenem Detailbereich.
  // Neu geholt beim Wechsel, wenn sich die Anzahl ändert oder noch welche gelesen werden (sonst nicht bei jedem Nachladen)
  // Auch mit offenem Detailbereich: er zeigt Aufnahmedatum, Kamera, Objektiv, Blende … eines Fotos (wie in der Galerie), wenn sie vorhanden sind
  const needsMeta = (view.kind === 'folder' && !searching && (sort === 'taken' || detailsOpen || (viewMode === 'details' && colCfg.some(c => META_COLS.includes(c.key)))))
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
    if (sharedUndo) { pushSharedUndo(u.label, async () => { try { await u.run() } finally { changedRef.current() } }); return }
    undos.current = [...undos.current.slice(-19), u]
    setUndoBar(u)
    clearTimeout(undoTimer.current)
    undoTimer.current = window.setTimeout(() => setUndoBar(null), 8000)
  }
  const undo = async () => {
    if (sharedUndo) { if (!await undoStep()) notify(t('Nichts rückgängig zu machen'), undefined, 'info'); return }
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
    // Änderungsdatum der Datei mitschicken: sonst gilt der Zeitpunkt des Hochladens (auch als Dokumentdatum im DMS)
    try { await finishToFiles(id, dir, mode, x => { xhr.current = x }, file.lastModified) }
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
    if (!pane && officeHash() !== null) history.replaceState(history.state, '', hashOf(view))
    changed()
    mainRef.current?.focus()
  }
  /**
   * Löschen; true, wenn gelöscht (nicht abgebrochen, kein Fehler). Nach den Rückfragen verschwinden die Einträge sofort, der Server
   * löscht im Hintergrund; schlägt das fehl, sind sie wieder da. hidden: läuft, sobald sie ausgeblendet sind
   */
  const remove = async (stacked: Item[], asked = false, hidden?: () => void): Promise<boolean> => {
    const list = unstack(stacked)
    if (!list.length) return false
    // Aus Galerie-Ordnern: im externen Ordner selbst, ohne Papierkorb - darum vorher fragen.
    // Gemischte Auswahl: erst diese, dann die eigenen Dateien wie gewohnt in den Papierkorb.
    const ext = list.filter(isExt)
    const own = list.filter(i => !isExt(i))
    if (ext.length) {
      if (!deletable(list)) return false
      // Über Dateien in externen Ordnern keine Aussage, ob und wie sie sich retten lassen - das liegt außerhalb von dmOffice
      const question = own.length
        ? t('{n} Dateien löschen? Zugehörige .xmp-Dateien werden mitgelöscht.', { n: list.length })
        : ext.length === 1 ? t('„{name}“ aus dem Ordner löschen? Eine zugehörige .xmp-Datei wird mitgelöscht.', { name: ext[0].name })
        : t('{n} Dateien aus dem Ordner löschen? Zugehörige .xmp-Dateien werden mitgelöscht.', { n: ext.length })
      if (!asked && !await askConfirm('', question)) return false
    }
    // Im Tresor und in Cryptomator-Tresoren gibt es keinen Papierkorb: endgültig löschen nur nach Rückfrage
    const enc = own.filter(i => isEncrypted(i.path))
    if (enc.length && !asked && !await askConfirm('', enc.length === 1
      ? t('„{name}“ endgültig löschen? Im Tresor gibt es keinen Papierkorb, das lässt sich nicht rückgängig machen.', { name: enc[0].name })
      : t('{n} Elemente endgültig löschen? Im Tresor gibt es keinen Papierkorb, das lässt sich nicht rückgängig machen.', { n: enc.length }),
      t('Endgültig löschen'))) return false
    // In einem mit mir geteilten Ordner: landet im Papierkorb des Besitzers, ohne Rückgängig
    const foreign = own.filter(i => !isEncrypted(i.path) && mountOf(i.path))
    if (foreign.length && !asked) {
      const owner = [...new Set(foreign.map(i => mountOf(i.path)!.owner))].join(', ')
      if (!await askConfirm(t('Löschen'), foreign.length === 1
        ? t('„{name}“ löschen? Es landet im Papierkorb von {owner} und lässt sich nur dort wiederherstellen.', { name: foreign[0].name, owner })
        : t('{n} Elemente löschen? Sie landen im Papierkorb von {owner} und lassen sich nur dort wiederherstellen.', { n: foreign.length, owner }),
        t('Löschen'))) return false
    }
    // Sofort ausblenden
    const extPaths = ext.map(i => i.path), ownPaths = own.map(i => i.path)
    const all = new Set([...extPaths, ...ownPaths])
    hideNow([...all], true)
    setSelected(s => s.filter(p => !all.has(p)))
    hidden?.()
    if (ext.length) {
      try {
        await api.post('/api/gallery/external/delete', { ids: ext.map(extId) })
        notify(ext.length === 1 ? t('„{name}“ gelöscht', { name: ext[0].name }) : t('{n} Elemente gelöscht', { n: ext.length }))
      } catch (e) { fail(e); hideNow([...all], false); changed(); return false }
      if (!own.length) { changed(); return true }
    }
    try {
      const r = await api.post<{ trash: number[] }>('/api/files/delete', { paths: ownPaths })
      // Rückgängig betrifft nur die eigenen Dateien (Galerie-Ordner haben keinen Papierkorb)
      const label = own.length === 1 ? t('„{name}“ gelöscht', { name: own[0].name }) : t('{n} Elemente gelöscht', { n: own.length })
      // In einem geteilten Ordner liegt das Gelöschte im Papierkorb des Besitzers - zurückholen kann nur er
      if (r.trash.length) pushUndo({ label, run: async () => { hideNow(ownPaths, false); await api.post('/api/files/trash/restore', { ids: r.trash }) } })
      else notify(label)
    } catch (e) { fail(e); hideNow(ownPaths, false); changed(); return false }
    if (path && own.some(i => inside(i.path, path))) openFolder(parentOf(own[0].path))
    changed()
    return true
  }
  // Abgelehnte löschen: Die Ansicht zeigt nur noch die abgelehnten (X), unten steht statt der Markier-Leiste die Rückfrage.
  // So lässt sich vorher durchscrollen und umentscheiden (U/P); Löschen nimmt, was dann noch abgelehnt ist - auch die noch nicht geladenen Monate.
  // Abbrechen lässt die Ansicht so stehen; nach dem Löschen gilt wieder der vorige Filter.
  const rejectBar = useRef<HTMLDivElement>(null)
  const [rejectAsk, setRejectAsk] = useState<{ before: typeof prefs.photoQuick; list: Item[]; all: number } | null>(null)
  const collectRejected = async (): Promise<{ list: Item[]; all: number } | null> => {
    const rule = quickRule({ flags: ['X'], stars: 0, labels: [] })!
    let all: Item[]
    try {
      all = unstack(await galleryAll({ ...tlQuery, stack: false, group: undefined,
        filter: JSON.stringify(baseOn ? { cols: [...filterCols, ...rule.cols], sel: [...photoSel, ...rule.sel] } : rule) }))
    } catch (e) { fail(e); return null }
    // Eben gesetzte Markierungen kennt der Server oft noch nicht: eben mit X markierte gehören dazu, eben anders markierte nicht
    const fresh = (i: Item) => { const m = marked[i.path]; return m && m.pick != null && Date.now() - m.at < 60_000 ? m.pick : undefined }
    const known = new Set(all.map(i => i.path))
    all = [...all.filter(i => fresh(i) == null || fresh(i) === -1),
      ...unstack(sortedItems.filter(i => !known.has(i.path) && !gone.has(i.path) && fresh(i) === -1
        && (!baseOn || filterPhotos([i], metaOf, filterCols, photoSel).length)))]
    return { list: all.filter(i => deletable([i])), all: all.length }
  }
  const rejectQuestion = ({ list, all }: { list: Item[]; all: number }) => {
    // Papierkorb nur nennen, wenn alles aus der eigenen Ablage kommt; über externe Ordner keine Aussage
    const ext = list.filter(isExt).length
    return [list.length === 1 ? t('1 abgelehnte Datei löschen?') : t('{n} abgelehnte Dateien löschen?', { n: list.length }),
      !ext ? t('Gelöschte Dateien kommen in den Papierkorb.') : '',
      ext ? t('Zugehörige .xmp-Dateien werden mitgelöscht.') : '',
      all > list.length ? t('{n} weitere lassen sich nicht löschen und bleiben.', { n: all - list.length }) : ''].filter(Boolean).join(' ')
  }
  const removeRejected = async () => {
    const before = rejectAsk?.before ?? prefs.photoQuick
    update({ photoQuick: { flags: ['X'], stars: 0, labels: [] } })
    const r = await collectRejected()
    if (!r) return
    if (!r.list.length) { setRejectAsk(null); update({ photoQuick: before }); notify(r.all ? t('Die abgelehnten Dateien lassen sich nicht löschen.') : t('Keine abgelehnten Dateien.')); return }
    setRejectAsk({ before, ...r })
  }
  // Während die Rückfrage steht: Umentscheidungen gleich in der Zahl zeigen
  useEffect(() => {
    if (!rejectAsk) return
    let live = true
    collectRejected().then(r => { if (live && r) setRejectAsk(a => a && { ...a, ...r }) })
    return () => { live = false }
  }, [marked]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (!photosView) setRejectAsk(null) }, [photosView])
  // Esc beendet die Rückfrage (außer in Eingabefeldern)
  useEffect(() => {
    if (!rejectAsk) return
    // Nur solange die Galerie sichtbar ist: Esc in einem anderen Bereich gehört dem
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== 'Escape' || e.defaultPrevented || isTyping(e) || !rejectBar.current?.offsetParent) return
      e.preventDefault(); setRejectAsk(null)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [!!rejectAsk]) // eslint-disable-line react-hooks/exhaustive-deps
  const confirmRejected = async () => {
    const ask = rejectAsk
    if (!ask) return
    const r = await collectRejected()
    if (!r) return
    setRejectAsk(null)
    if (!r.list.length) { notify(t('Keine abgelehnten Dateien.')); return }
    // Nach dem Löschen gilt gleich wieder der vorige Filter (die gelöschten sind schon ausgeblendet)
    await remove(r.list, true, () => update({ photoQuick: ask.before }))
  }
  // Mehrere auf einmal umbenennen (Kennung bulkrename): nur im selben Ordner, nur was geändert werden darf
  const [bulk, setBulk] = useState<Item[] | null>(null)
  const bulkable = (list: Item[]) => featureOn('bulkrename') && list.length > 1 && path !== null && view.kind === 'folder' && !flat
    && list.every(i => parentOf(i.path) === path) && canChange(list)
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
    const paths = picked.flatMap(p => [p, ...(items.find(i => i.path === p)?.stack ?? []).filter(x => !x.dupe).map(x => x.path)])
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
    // Aus dem gezeigten Ordner heraus verschoben: gleich ausblenden, der Server verschiebt im Hintergrund. Was übersprungen, nur
    // kopiert oder nicht verschoben wurde (Fehler, Abbruch), kommt danach zurück
    const away = copy || flat || tlOn || view.kind !== 'folder' ? [] : todo.filter(p => parentOf(p) === path)
    hideNow(away, true)
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
    const gotAway = new Set(moved.filter(m => !m.copied).map(m => m.from))
    const back = away.filter(p => !gotAway.has(p))
    if (back.length) hideNow(back, false)
    if (moved.length) {
      // Aus einem geteilten Ordner heraus wird kopiert - das Original bleibt beim Besitzer
      const copiedOut = moved.some(m => m.copied)
      const asCopy = copy || copiedOut
      notify(asCopy ? t('{n} kopiert', { n: moved.length }) : t('{n} verschoben', { n: moved.length }),
        copiedOut ? t('Aus einem geteilten Ordner heraus wird kopiert – das Original bleibt beim Besitzer.') : skipped ? t('{n} übersprungen', { n: skipped }) : undefined)
      pushUndo(asCopy
        ? { label: t('{n} kopiert', { n: moved.length }), run: async () => { await api.post('/api/files/delete', { paths: moved.map(m => m.to) }) } }
        : { label: t('{n} verschoben', { n: moved.length }), run: async () => { hideNow(moved.map(m => m.from), false); for (const m of moved) await api.post('/api/files/move', { from: m.to, to: m.from, copy: false }) } })
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
  /** Bild als PNG fuer die Zwischenablage (JPG, WebP ... umgewandelt - Browser nehmen dort nur PNG). Grundlage ist die grosse Vorschau
   *  (2048 px, meist schon geladen, bei bearbeiteten Fotos die bearbeitete Fassung) - nicht das ganze Original bei jeder Auswahl. */
  const pngOf = (i: Item) => fetch(ownSource.thumb(i, 2048), { credentials: 'same-origin' })
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
  // Sofort zeigen; schlägt das Speichern fehl, kommt der vorige Stand zurück
  const favorite = async (i: Item, on: boolean) => {
    setFavLocal(f => ({ ...f, [i.path]: { at: Date.now(), on } }))
    if (i.isDir) setFavs(f => on ? (f.some(x => x.path === i.path) ? f : [...f, i]) : f.filter(x => x.path !== i.path))
    try { await api.post('/api/files/favorite', { path: i.path, on }) } catch (e) {
      fail(e)
      setFavLocal(f => ({ ...f, [i.path]: { at: Date.now(), on: !on } }))
    }
    changed()
  }
  const quickLink = async (i: Item) => {
    try {
      const l = await api.post<Link>('/api/files/links', { path: i.path, mode: 'read' })
      if (await copyText(linkUrl(l))) notify(t('Öffentlicher Link erstellt und kopiert'), t('Jeder mit dem Link kann „{name}“ ohne Anmeldung ansehen und herunterladen.', { name: i.name }))
      changed()
    } catch (e) { fail(e) }
  }
  // Pfad kopieren (Kennung copypath): so, wie er in die Adresszeile eingetippt werden kann
  const copyPath = async (p: string) => { if (await copyText('/' + p)) notify(t('Pfad kopiert'), '/' + p) }
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
    setCursor(null)
    reveal(p, 'nearest')
  }
  const columns = () => {
    // Mosaik: so viele wie in der ersten Zeile
    if (mosaic) return Math.max(1, mainRef.current?.querySelector('[data-mrow]')?.children.length ?? 1)
    const grid = mainRef.current?.querySelector('[data-grid]') as HTMLElement | null
    return grid ? Math.max(1, getComputedStyle(grid).gridTemplateColumns.split(' ').length) : 1
  }
  /** Commander-Tasten; true, wenn die Taste verbraucht wurde */
  const commanderKey = (e: React.KeyboardEvent): boolean => {
    const cur = items.find(x => x.path === (cursor ?? anchor.current)) ?? one
    const target = sel.length ? sel : cur ? [cur] : []
    const done = () => { e.preventDefault(); return true }
    switch (e.key) {
      case 'F3':
        if (cur && !cur.isDir) { setStartEdit(false); open(cur) }
        return done()
      case 'F4':
        if (cur && !cur.isDir) {
          if (officeMode(cur, office) === 'edit' && !isCmv(cur.path)) setEditing({ path: cur.path, name: cur.name })
          else if (viewKind(cur) === 'text') { setViewer({ list: [cur], index: 0 }); setStartEdit(true) }
          else open(cur)
        }
        return done()
      case 'F5':
        if (target.length && keepable(target)) { if (dual) { if (otherPath !== null) void toOther(target.map(i => i.path), otherPath, true) } else setPicker({ mode: 'copy', paths: target.map(i => i.path) }) }
        return done()
      case 'F6':
        if (e.shiftKey) { if (cur && canChange([cur])) rename(cur) }
        else if (target.length && canChange(target)) { if (dual) { if (otherPath !== null) void toOther(target.map(i => i.path), otherPath, false) } else setPicker({ mode: 'move', paths: target.map(i => i.path) }) }
        return done()
      case 'F7':
        if (path !== null && writableAt(path)) mkdir()
        return done()
      case 'F8':
        if (target.length && deletable(target)) remove(target)
        return done()
      case 'Insert': {
        if (!cur) return done()
        setSelected(sl => sl.includes(cur.path) ? sl.filter(x => x !== cur.path) : [...sl, cur.path])
        const n = items[Math.min(items.length - 1, items.findIndex(x => x.path === cur.path) + 1)]
        anchor.current = n.path; setCursor(n.path)
        reveal(n.path, 'nearest')
        return done()
      }
    }
    // Wie im Midnight Commander auch auf der Haupttastatur: + auswählen, \ abwählen, * umkehren
    if (e.key === '+' && e.code !== 'NumpadAdd') { setPattern({ add: true, value: '*' }); return done() }
    if (e.key === '\\') { setPattern({ add: false, value: '*' }); return done() }
    if (e.key === '*' && e.code !== 'NumpadMultiply') { setSelected(sl => items.map(i => i.path).filter(x => !sl.includes(x))); return done() }
    if (e.code === 'NumpadAdd') { setPattern({ add: true, value: '*' }); return done() }
    if (e.code === 'NumpadSubtract') { setPattern({ add: false, value: '*' }); return done() }
    if (e.code === 'NumpadMultiply') { setSelected(sl => items.map(i => i.path).filter(x => !sl.includes(x))); return done() }
    return false
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
    if (photosView && featureOn('tilemodes') && !ctrl && !e.altKey && !e.shiftKey && k === 'k') {
      e.preventDefault(); update({ photoTileMode: !tileMode ? 'full' : tileMode === 'full' ? 'image' : tileMode === 'image' && featureOn('mosaicweb') ? 'mosaic' : undefined }); return
    }
    if (qcOn && !ctrl && !e.altKey && !e.shiftKey && sel.length && k === 'b') { e.preventDefault(); void toggleQuick(sel); return }
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
    if (cmdKeys && dualAvail) {
      const c = items.find(x => x.path === (cursor ?? anchor.current)) ?? one
      if (dual ? dualKey(e, dualKeys, path, c?.isDir ? c.path : null) : e.key === 'F10' && !ctrl && !e.altKey && dualKey(e, dualKeys, null, null)) return
    }
    if (cmdKeys && !ctrl && !e.altKey && commanderKey(e)) return
    // Tippen springt zum ersten Eintrag, der so beginnt (wie im Explorer; Kennung typeahead, abschaltbar unter Bedienung)
    if (typeAhead && !ctrl && !e.altKey && e.key.length === 1 && /\S| /.test(e.key) && (e.key !== ' ' || typed.current.text)) {
      const now = Date.now()
      typed.current = { text: (now - typed.current.at < 1000 ? typed.current.text : '') + e.key.toLowerCase(), at: now }
      const q = typed.current.text
      const same = q.length > 1 && [...q].every(c => c === q[0])
      const cur = items.findIndex(x => x.path === anchor.current)
      // Derselbe Buchstabe mehrmals: weiter zum nächsten Eintrag mit diesem Anfang
      const order = same ? [...items.slice(cur + 1), ...items.slice(0, cur + 1)] : items
      const hit = order.find(x => x.name.toLowerCase().startsWith(same ? q[0] : q))
      if (hit) {
        e.preventDefault()
        anchor.current = hit.path; setSelected([hit.path]); setCursor(null)
        reveal(hit.path, 'nearest')
        return
      }
      if (e.key !== ' ') return
    }
    // Commander mit Strg/Alt: Strg+R neu einlesen, Alt+. versteckte Dateien, Alt+C Pfad eingeben, Strg+Leertaste Eigenschaften
    if (cmdKeys && ctrl && !e.altKey && !e.shiftKey && e.key.toLowerCase() === 'r') { e.preventDefault(); changed(); return }
    if (cmdKeys && e.altKey && !ctrl && e.key === '.') { e.preventDefault(); update({ fileHidden: !showHidden }); return }
    if (cmdKeys && e.altKey && !ctrl && e.key.toLowerCase() === 'c' && addressBar && view.kind === 'folder' && !inTresor && !isCmv(path) && !ext) { e.preventDefault(); setEditPath('/' + (path ?? '')); return }
    if (cmdKeys && ctrl && e.code === 'Space' && featureOn('folderprops') && view.kind === 'folder') {
      e.preventDefault()
      const f = one?.isDir ? one.path : path
      if (f != null && !isCmv(f)) setProps(f)
      return
    }
    if (e.key === 'Delete') { e.preventDefault(); if (deletable(sel)) remove(sel); else if (sel.length && sel.every(i => isMountRoot(i.path))) leaveShared(sel) }
    else if (e.key === 'F2' && one) { e.preventDefault(); if (canChange([one])) rename(one) }
    else if (e.key === 'F2' && bulkable(sel)) { e.preventDefault(); setBulk(sel) }
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
      if (e.target !== document.body || viewer || editing || naming || share || picker || pattern || bulk || conflict.dialog || !isActivePane) return
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
    // Zwei Fenster: aus welchem Fenster der Zug kommt (das andere nimmt ihn auch auf der freien Fläche an)
    if (dual) e.dataTransfer.setData(pane ? DRAG_PANE2 : DRAG_PANE1, '1')
    // Eine Datei auf den Desktop oder in den Explorer ziehen (Chrome/Edge): der Browser laedt sie angemeldet herunter
    if (paths.length === 1 && !i.isDir && !shownAt(i.path)) {
      const url = new URL(gallerySource.download(i), location.origin).href
      e.dataTransfer.setData('DownloadURL', `${i.mime || 'application/octet-stream'}:${i.name.replace(/:/g, '_')}:${url}`)
    }
    e.dataTransfer.effectAllowed = 'copyMove'
  }
  const otherMark = pane ? DRAG_PANE1 : DRAG_PANE2
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
  // Sofort melden und mitzählen, der Server trägt im Hintergrund ein (schon enthaltene zählt er nicht mit - danach der genaue Stand)
  const addToAlbum = async (id: number, list: Item[]) => {
    notify(t('Zum Album „{name}“ hinzugefügt', { name: albums.find(a => a.id === id)?.name ?? '' }))
    setAlbums(l => l.map(a => a.id === id ? { ...a, count: a.count + list.length } : a))
    try { await albumsApi.add(id, list.map(i => i.path)) } catch (e) { fail(e) }
    loadAlbums()
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
  // Rechtsklick auf die Albumauswahl (ctxall): dieselben Einträge wie „…“ für das gezeigte Album
  const albumCtx = useContextMenu<null>()
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
  const inQuick = (i: Item) => quickSet.has(i.path)
  // Leiste der Kachel: wirkt auf die Auswahl, wenn die Kachel dazugehört; sonst wird sie allein gewählt (wie im Kamera-Importer)
  const barList = (i: Item) => {
    if (selected.includes(i.path)) return sel.filter(x => !x.isDir)
    anchor.current = i.path; setSelected([i.path])
    return [i]
  }
  const [labelMenu, setLabelMenu] = useState<{ at: { x: number; y: number }; list: Item[]; cur?: string | null } | null>(null)
  // Drehen: nicht-destruktiv in den Bearbeitungseinstellungen (wie im Foto-Editor); ein Ausschnitt dreht mit
  const turnQueue = useRef<Promise<void>>(Promise.resolve())
  const turnable = (i: Item) => isPhoto(i) && !isAlb(i) && !isTresor(i.path)
  const turnPhotos = (list: Item[], deg: number) => {
    const photos = list.filter(turnable)
    if (!photos.length) return
    turnQueue.current = turnQueue.current.then(async () => {
      try {
        for (const i of photos) {
          const { edit } = await api.get<{ edit: PhotoSettings | null }>('/api/files/photo-edits?' + q({ path: i.path }))
          const c = edit?.crop
          const crop = !c ? null : deg > 0 ? { x: 1 - (c.y + c.h), y: c.x, w: c.h, h: c.w } : { x: c.y, y: 1 - (c.x + c.w), w: c.h, h: c.w }
          // [feature:lenscorr]
          await api.put('/api/files/photo-edits', { path: i.path, rotate: ((edit?.rotate ?? 0) + deg + 360) % 360, crop, adjust: edit?.adjust ?? null, origin: edit?.origin, lens: edit?.lens ?? null })
          // [feature:lenscorr:else]
          // await api.put('/api/files/photo-edits', { path: i.path, rotate: ((edit?.rotate ?? 0) + deg + 360) % 360, crop, adjust: edit?.adjust ?? null, origin: edit?.origin })
          // [/feature:lenscorr]
        }
      } catch (e) { fail(e) }
      if (tlOn) tl.refresh(); else changed()
    })
  }
  // Taste B wie in Lightroom: das Foto unter dem Cursor (sonst das erste) bestimmt, ob alle gewählten hinein- oder herauskommen (wie im Kamera-Importer)
  const toggleQuick = async (list: Item[], lead?: Item) => {
    const photos = list.filter(i => !i.isDir)
    if (!qcOn || !photos.length) return
    const ref = lead ?? photos.find(i => i.path === (cursor ?? anchor.current)) ?? photos[0]
    const add = !inQuick(ref)
    setQuickSet(q => { const n = new Set(q); for (const i of photos) { if (add) n.add(i.path); else n.delete(i.path) } return n })
    try {
      await albumsApi.toggleQuick(photos.map(i => i.path), add)
      notify(add ? t('{n} in die Schnellsammlung gelegt', { n: photos.length }) : t('{n} aus der Schnellsammlung genommen', { n: photos.length }))
      if (albumNow?.kind === 'quick') load()
      loadAlbums()
    } catch (e) { fail(e) }
    loadQuick()
  }
  const albumItems = albumNow && <>
        {albumNow.mine && albumNow.kind !== 'quick' && <MenuItem onClick={() => setAlbumDialog({ kind: 'rename' })}>{t('Umbenennen …')}</MenuItem>}
        {albumNow.mine && albumNow.kind !== 'quick' && <MenuItem icon={<People20Regular />} onClick={() => setAlbumDialog({ kind: 'share' })}>{t('Teilen …')}</MenuItem>}
        {albumNow.mine && albumNow.kind !== 'quick' && <MenuItem icon={<Globe20Regular />} onClick={() => setLinkAlbum(albumNow)}>{t('Öffentlich zeigen …')}</MenuItem>}
        {albumNow.mine && albumNow.kind === 'smart' && <MenuItem icon={<Sparkle20Regular />} onClick={() => setSmartDialog({ id: albumNow.id })}>{t('Regeln bearbeiten …')}</MenuItem>}
        {albumNow.mine && albumNow.kind === 'smart' && filterOn && <MenuItem onClick={async () => {
          try { await albumsApi.update(albumNow.id, { rule: JSON.stringify({ cols: filterCols, sel: photoSel }) }); setPhotoSel([[], [], [], []]); load(); loadAlbums() } catch (e) { fail(e) }
        }}>{t('Filter übernehmen')}</MenuItem>}
        {albumNow.kind !== 'quick' && <MenuItem icon={<Chat20Regular />} onClick={() => sendToChat([{ type: 'album', fileId: albumNow.id }])}>{t('Im Chat zeigen …')}</MenuItem>}
        {albumNow.kind === 'quick' && <MenuItem icon={<Delete20Regular />} disabled={!items.length} onClick={() => toggleQuick(items.filter(inQuick))}>{t('Schnellsammlung leeren')}</MenuItem>}
        {albumNow.kind !== 'quick' && <MenuDivider />}
        {albumNow.kind !== 'quick' && <MenuItem icon={<Delete20Regular />} onClick={async () => {
          if (!await askConfirm('', albumNow.mine ? t('Album „{name}“ löschen? Die Fotos bleiben, wo sie sind.', { name: albumNow.name }) : t('Album „{name}“ verlassen? Deine Fotos darin werden herausgenommen.', { name: albumNow.name }))) return
          try { await albumsApi.remove(albumNow.id); setAlbum(null); loadAlbums() } catch (e) { fail(e) }
        }}>{albumNow.mine ? t('Album löschen') : t('Album verlassen')}</MenuItem>}
  </>
  // Rechtsklick auf die Albumauswahl; dieselben Befehle stehen im Menüband (Gruppe Album)
  const albumMenu = albumNow && albumCtx.ctx && <ContextMenu at={albumCtx.ctx.at} onClose={albumCtx.close}>{albumItems}</ContextMenu>
  const albumOwn = !!albumNow?.mine && albumNow.kind !== 'quick'
  const albumRibbon = <>
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ImageAdd20Regular />} label={t('Neues Album')} /></MenuTrigger>
      <MenuPopover><MenuList>
        <MenuItem icon={<Add16Regular />} onClick={() => setAlbumDialog({ kind: 'new' })}>{t('Album …')}</MenuItem>
        <MenuItem icon={<Sparkle20Regular />} onClick={() => setSmartDialog({ id: null })}>{t('Smart-Album …')}</MenuItem>
      </MenuList></MenuPopover>
    </Menu>
    {album == null && prefs.photoFilter && <RibbonButton icon={<SaveSearch20Regular />} label={t('Als Smart-Album speichern')} disabled={!baseOn}
      title={baseOn ? undefined : t('Erst in der Filterleiste etwas wählen')} onClick={() => setAlbumDialog({ kind: 'smart' })} />}
    {albumNow && albumNow.kind !== 'quick' && <>
      <RibbonButton icon={<Rename20Regular />} label={t('Album umbenennen')} disabled={!albumOwn} onClick={() => setAlbumDialog({ kind: 'rename' })} />
      <RibbonButton icon={<People20Regular />} label={t('Album teilen')} disabled={!albumOwn} onClick={() => setAlbumDialog({ kind: 'share' })} />
      <RibbonButton icon={<Globe20Regular />} label={t('Öffentlich zeigen')} disabled={!albumOwn} onClick={() => setLinkAlbum(albumNow)} />
      {albumNow.kind === 'smart' && <RibbonButton icon={<Sparkle20Regular />} label={t('Regeln bearbeiten')} disabled={!albumOwn} onClick={() => setSmartDialog({ id: albumNow.id })} />}
      {albumNow.kind === 'smart' && filterOn && <RibbonButton icon={<Filter20Regular />} label={t('Filter übernehmen')} disabled={!albumOwn} onClick={async () => {
        try { await albumsApi.update(albumNow.id, { rule: JSON.stringify({ cols: filterCols, sel: photoSel }) }); setPhotoSel([[], [], [], []]); load(); loadAlbums() } catch (e) { fail(e) }
      }} />}
      <RibbonButton icon={<Chat20Regular />} label={t('Im Chat zeigen')} onClick={() => sendToChat([{ type: 'album', fileId: albumNow.id }])} />
      <RibbonButton icon={<Delete20Regular />} label={albumNow.mine ? t('Album löschen') : t('Album verlassen')} onClick={async () => {
        if (!await askConfirm('', albumNow.mine ? t('Album „{name}“ löschen? Die Fotos bleiben, wo sie sind.', { name: albumNow.name }) : t('Album „{name}“ verlassen? Deine Fotos darin werden herausgenommen.', { name: albumNow.name }))) return
        try { await albumsApi.remove(albumNow.id); setAlbum(null); loadAlbums() } catch (e) { fail(e) }
      }} />
    </>}
    {albumNow?.kind === 'quick' && <RibbonButton icon={<Delete20Regular />} label={t('Schnellsammlung leeren')} disabled={!items.length} onClick={() => toggleQuick(items.filter(inQuick))} />}
    <RibbonDivider />
  </>
  // Farbmenü der Kachel-Leiste; dieselbe Farbe noch einmal: weg
  const labelMenuEl = labelMenu && <ContextMenu at={labelMenu.at} onClose={() => setLabelMenu(null)}>
    {MARK_LABELS.map(l => <MenuItem key={l} icon={<span style={{ display: 'inline-block', width: 14, height: 10, borderRadius: 2, background: labelColor(l) }} />}
      secondaryContent={labelMenu.cur === l ? <Checkmark16Regular /> : undefined}
      onClick={() => { void markPhotos({ label: labelMenu.cur === l ? '' : l }, labelMenu.list); setLabelMenu(null) }}>{labelName(l)}</MenuItem>)}
    <MenuDivider />
    <MenuItem secondaryContent={!labelMenu.cur ? <Checkmark16Regular /> : undefined} onClick={() => { void markPhotos({ label: '' }, labelMenu.list); setLabelMenu(null) }}>{t('Ohne')}</MenuItem>
  </ContextMenu>

  /** Herkunft (Wolke) auf der Kachel: nur in der Galerie, nicht bei Ordnern und nicht neben „nicht erreichbar“ */
  /** Markierung (P/X), Farbe und Sterne eines Fotos; null, wenn nichts gesetzt ist. */
  const marksOf = (i: Item) => {
    const m = photosView ? metaOf(i) : undefined
    if (!m?.pick && !m?.label && !m?.rating) return null
    return <>
      {m.pick === 1 && <span style={{ color: '#4CC38A', fontWeight: 600 }} title={t('Ausgewählt')}>P</span>}
      {m.pick === -1 && <span style={{ color: '#FF6369', fontWeight: 600 }} title={t('Abgelehnt')}>X</span>}
      {m.label && <span className={s.markDot} style={{ background: labelColor(m.label) }} />}
      {m.rating ? <span>{'★'.repeat(m.rating)}</span> : null}
    </>
  }
  const cloudOn = (i: Item) => photosView && !i.isDir && featureOn('galdupes') && !extOffline(i)
  // ---------------- Menues ----------------
  const [ctx, setCtx] = useState<{ x: number; y: number; kind: 'items' | 'empty' | 'folder' | 'nav'; folder?: string; nav?: Special | 'chat' | 'withme' | 'tresor' } | null>(null)
  const menuItems = (list: Item[]) => {
    const single = list.length === 1 ? list[0] : null
    // Aus einem Galerie-Ordner: nur ansehen und herunterladen - ändern geht im externen Ordner selbst
    if (list.some(isExt)) return (
      <MenuList>
        {single && <MenuItem icon={<FolderOpen20Regular />} onClick={() => open(single)}>{t('Öffnen')}</MenuItem>}
        {single?.isDir && tabsOn && !isExt(single) && <MenuItem icon={<TabAdd20Regular />} onClick={() => newTab({ kind: 'folder', path: single.path })}>{t('In neuem Tab öffnen')}</MenuItem>}
        <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download(list)}>{t('Herunterladen')}</MenuItem>
        {toAblage.available && list.some(onlyInFolder) && <MenuItem icon={<CloudArrowUp20Regular />} onClick={() => toAblage.save(list)}>{t('In Ablage sichern')}</MenuItem>}
        {album != null && list.every(isAlb) && albums.find(a => a.id === album)?.kind === 'fixed' && (albums.find(a => a.id === album)?.mine || list.every(i => albumMine[i.path])) &&
          <MenuItem icon={<Delete20Regular />} onClick={() => takeFromAlbum(list)}>{t('Aus dem Album nehmen')}</MenuItem>}
        {album == null && addToAlbumMenu(list)}
        {list.every(extWritable) && list.some(isPhoto) && <MenuItem icon={<Wand20Regular />} onClick={() => setAutoFor(list)}>{featureOn('enhancebtn') ? t('Verbessern …') : t('Automatisch verbessern …')}</MenuItem>}
        {deletable(list) && <MenuDivider />}
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
        {single?.isDir && tabsOn && !isExt(single) && <MenuItem icon={<TabAdd20Regular />} onClick={() => newTab({ kind: 'folder', path: single.path })}>{t('In neuem Tab öffnen')}</MenuItem>}
        {/* Eigener Cryptomator-Tresor irgendwo in den Dateien: von hier schließen bzw. öffnen */}
        {single?.vault && (single.vaultOpen
          ? <MenuItem icon={<LockClosed20Regular />} onClick={() => api.post('/api/files/vault/lock', { path: single.path }).then(() => refresh.current()).catch(fail)}>{t('Tresor schließen')}</MenuItem>
          : <MenuItem icon={<LockOpen20Regular />} onClick={() => open(single)}>{t('Tresor öffnen …')}</MenuItem>)}
        {single && view.kind === 'photos' && !single.isDir && <MenuItem icon={<ImageOff20Regular />} onClick={() => setExcludeFrom(single.path)}>{t('Ordner aus der Galerie ausschließen …')}</MenuItem>}
        {view.kind === 'photos' && album == null && list.every(i => !i.isDir) && addToAlbumMenu(list)}
        {change && list.some(isPhoto) && <MenuItem icon={<Wand20Regular />} onClick={() => setAutoFor(list)}>{featureOn('enhancebtn') ? t('Verbessern …') : t('Automatisch verbessern …')}</MenuItem>}
        {infoable(list) && <MenuItem icon={<Edit20Regular />} onClick={() => setInfoFor(list)}>{t('Titel, Tags, Bewertung …')}</MenuItem>}
        {single && officeMode(single, office) === 'edit' && !isCmv(single.path) && <MenuItem icon={<DocumentEdit20Regular />} onClick={() => setEditing({ path: single.path, name: single.name })}>{t('Im Browser bearbeiten')}</MenuItem>}
        {single && change && <MenuItem icon={<Rename20Regular />} secondaryContent="F2" onClick={() => rename(single)}>{t('Umbenennen')}</MenuItem>}
        {!single && bulkable(list) && <MenuItem icon={<Rename20Regular />} secondaryContent="F2" onClick={() => setBulk(list)}>{t('Umbenennen …')}</MenuItem>}
        {single && flat && <MenuItem icon={<Folder20Regular />} onClick={() => { openFolder(parentOf(single.path)); setTimeout(() => setSelected([single.path]), 300) }}>{t('Speicherort öffnen')}</MenuItem>}
        {keep && <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download(list)}>{list.length > 1 || single?.isDir ? t('Als ZIP herunterladen') : t('Herunterladen')}</MenuItem>}
        {keep && !list.some(isAlb) && <MenuItem icon={<Cart20Regular />} onClick={() => basket.add(list)}>{t('In den Downloadkorb')}</MenuItem>}
        {single && mine && <MenuItem icon={<Share20Regular />} onClick={() => setShare(single)}>{t('Teilen …')}</MenuItem>}
        {!single && mine && list.length > 1 && <MenuItem icon={<Share20Regular />} onClick={() => setShareMany(list)}>{t('Mit Benutzern teilen …')}</MenuItem>}
        {single && mine && <MenuItem icon={<Link20Regular />} onClick={() => quickLink(single)}>{t('Öffentlichen Link erstellen')}</MenuItem>}
        {mine && list.every(i => i.fileId) && <MenuItem icon={<Chat20Regular />} onClick={() => sendToChat(list.map(i => ({ type: i.isDir ? 'folder' as const : 'file' as const, fileId: i.fileId! })))}>{t('Im Chat …')}</MenuItem>}
        {single && mine && <MenuItem icon={single.favorite ? <Star20Filled color="#E8A33D" /> : <Star20Regular />} onClick={() => favorite(single, !single.favorite)}>
          {single.favorite ? t('Aus Favoriten entfernen') : t('Zu Favoriten hinzufügen')}</MenuItem>}
        {single?.isDir && mine && !isExt(single) && !isCmv(single.path) && (
          <MenuItem icon={single.davHidden ? <Cloud20Regular /> : <CloudOff20Regular />} onClick={() => davHide(single, !single.davHidden)}>
            {single.davHidden ? t('In WebDAV und Nextcloud zeigen') : t('In WebDAV und Nextcloud ausblenden')}</MenuItem>
        )}
        {featureOn('filecats') && mine && list.some(i => !i.isDir) && !list.some(isAlb) && <MenuItem icon={<Tag20Regular />} onClick={() => setLabelsFor(list.filter(i => !i.isDir).map(i => i.path))}>{t('Kategorien und Tags …')}</MenuItem>}
        {featureOn('dms') && mine && canChange(list) && list.some(i => !i.isDir) && !list.some(isAlb) && <ToDmsItem files={list.filter(i => !i.isDir)} onPick={toDms} />}
        <MenuDivider />
        {change && <MenuItem icon={<Cut20Regular />} secondaryContent={t('Strg+X')} onClick={() => toClip(list, true)}>{t('Ausschneiden')}</MenuItem>}
        {keep && <MenuItem icon={<Copy20Regular />} secondaryContent={t('Strg+C')} onClick={() => toClip(list, false)}>{t('Kopieren')}</MenuItem>}
        {single?.isDir && clip && writableAt(single.path) && <MenuItem icon={<ClipboardPaste20Regular />} onClick={() => paste(single.path)}>{t('Hier einfügen')}</MenuItem>}
        {featureOn('copypath') && single && !isCmv(single.path) && <MenuItem icon={<DocumentCopy20Regular />} onClick={() => copyPath(single.path)}>{t('Pfad kopieren')}</MenuItem>}
        {change && <MenuItem icon={<FolderArrowRight20Regular />} onClick={() => setPicker({ mode: 'move', paths: list.map(i => i.path) })}>{t('Verschieben nach …')}</MenuItem>}
        {keep && <MenuItem icon={<Copy20Regular />} onClick={() => setPicker({ mode: 'copy', paths: list.map(i => i.path) })}>{t('Kopieren nach …')}</MenuItem>}
        <MenuItem icon={<Info20Regular />} onClick={() => { setSelected(list.map(i => i.path)); setDetails(true) }}>{t('Details')}</MenuItem>
        {/* Mit mir Geteiltes: das Geteilte selbst, in der Galerie auch aus einem Foto eines geteilten Ordners heraus */}
        {list.every(i => isMountRoot(i.path) || (photosView && mountOf(i.path))) && <MenuItem icon={<EyeOff20Regular />} onClick={() => leaveShared(list)}>{list.every(i => isMountRoot(i.path)) ? t('Nicht mehr anzeigen') : t('Geteilten Ordner nicht mehr anzeigen')}</MenuItem>}
        {/* Löschen zuletzt, mit Strich davor */}
        {change && <MenuDivider />}
        {change && <MenuItem icon={<Delete20Regular />} secondaryContent={t('Entf')} onClick={() => remove(list)}>{t('Löschen')}</MenuItem>}
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
      {featureOn('selectpattern') && <>
        <MenuItem onClick={() => setPattern({ add: true, value: '*' })} secondaryContent={cmdKeys ? 'Num+' : undefined}>{t('Auswahl erweitern …')}</MenuItem>
        <MenuItem disabled={!selected.length} onClick={() => setPattern({ add: false, value: '*' })} secondaryContent={cmdKeys ? 'Num−' : undefined}>{t('Auswahl verkleinern …')}</MenuItem>
        <MenuItem onClick={() => setSelected(sl => items.map(i => i.path).filter(x => !sl.includes(x)))} secondaryContent={cmdKeys ? 'Num*' : undefined}>{t('Auswahl umkehren')}</MenuItem>
      </>}
      <MenuItem icon={<ArrowSync20Regular />} onClick={changed}>{t('Aktualisieren')}</MenuItem>
    </MenuList>
  )
  const folderItem = (p: string): Item => ({ path: p, name: nameOf(p), isDir: true, size: 0, mime: null, modified: '', favorite: favs.some(f => f.path === p) })
  const folderMenu = (f: string) => (
    <MenuList>
      <MenuItem icon={<FolderOpen20Regular />} onClick={() => treeOpen(f)}>{t('Öffnen')}</MenuItem>
      {tabsOn && <MenuItem icon={<TabAdd20Regular />} onClick={() => newTab({ kind: 'folder', path: f })}>{t('In neuem Tab öffnen')}</MenuItem>}
      <MenuItem icon={<FolderAdd20Regular />} onClick={() => mkdir(f)}>{t('Neuer Ordner')}</MenuItem>
      {clip && <MenuItem icon={<ClipboardPaste20Regular />} onClick={() => paste(f)}>{t('Hier einfügen')}</MenuItem>}
      {featureOn('copypath') && !isCmv(f) && <MenuItem icon={<DocumentCopy20Regular />} onClick={() => copyPath(f)}>{t('Pfad kopieren')}</MenuItem>}
      <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download([folderItem(f)])}>{t('Als ZIP herunterladen')}</MenuItem>
      <MenuItem icon={<Share20Regular />} onClick={() => setShare(folderItem(f))}>{t('Teilen …')}</MenuItem>
      {f && <>
        <MenuItem icon={favs.some(x => x.path === f) ? <Star20Filled color="#E8A33D" /> : <Star20Regular />}
          onClick={() => favorite(folderItem(f), !favs.some(x => x.path === f))}>{favs.some(x => x.path === f) ? t('Aus Favoriten entfernen') : t('Zu Favoriten hinzufügen')}</MenuItem>
        <MenuItem icon={<Rename20Regular />} onClick={() => rename(folderItem(f))}>{t('Umbenennen')}</MenuItem>
        <MenuItem icon={<FolderArrowRight20Regular />} onClick={() => setPicker({ mode: 'move', paths: [f] })}>{t('Verschieben nach …')}</MenuItem>
        <MenuItem icon={<Copy20Regular />} onClick={() => setPicker({ mode: 'copy', paths: [f] })}>{t('Kopieren nach …')}</MenuItem>
      </>}
      {featureOn('folderprops') && !isCmv(f) && <MenuItem icon={<Info20Regular />} onClick={() => setProps(f)}>{t('Eigenschaften')}</MenuItem>}
      {/* Löschen zuletzt, mit Strich davor */}
      {f && <>
        <MenuDivider />
        <MenuItem icon={<Delete20Regular />} onClick={() => remove([folderItem(f)])}>{t('Löschen')}</MenuItem>
      </>}
    </MenuList>
  )

  // Eigenschaften eines Ordners (Rechtsklick)
  const [props, setProps] = useState<string | null>(null)
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
        // Während des Zurückgehens (zwei Schritte) die Adresse des Fotos nicht noch einmal auswerten: sie öffnete den Betrachter neu
        // oder schrieb den Eintrag auf #/gallery um
        backingOut.current = true
        setTimeout(() => { backingOut.current = false }, 1500)
        if (history.state?.dmBack) window.addEventListener('popstate', () => history.back(), { once: true })
        else history.back()
      }
      else history.replaceState(history.state, '', '#/gallery')
    }
  }, [gallery, viewerPath]) // eslint-disable-line react-hooks/exhaustive-deps
  useBackClose(!!editing, closeEditor)

  // ---------------- Menueband ----------------
  // Externer Ordner: dieselben Befehle wie in „Meine Dateien“, ausgeführt im externen Ordner; was es dort nicht gibt, bleibt grau
  const xr = extRibbon.current
  const extStart = xr && <>
    {!xr.readOnly && <>
      <Menu>
        <MenuTrigger disableButtonEnhancement><RibbonButton primary icon={<DocumentAdd20Regular />} label={t('Neu')} /></MenuTrigger>
        <MenuPopover><MenuList><MenuItem icon={<FolderAdd20Regular />} onClick={() => xr.newFolder()}>{t('Ordner')}</MenuItem></MenuList></MenuPopover>
      </Menu>
      <Menu>
        <MenuTrigger disableButtonEnhancement><RibbonButton icon={xr.busy ? <Spinner size="tiny" /> : <ArrowUpload20Regular />} label={t('Hochladen')} disabled={xr.busy} /></MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem icon={<ArrowUpload20Regular />} onClick={() => xr.uploadFiles()}>{t('Dateien')}</MenuItem>
            {xr.canUploadDir && <MenuItem icon={<FolderArrowUp20Regular />} onClick={() => xr.uploadDir()}>{t('Ordner')}</MenuItem>}
          </MenuList>
        </MenuPopover>
      </Menu>
      <RibbonDivider />
    </>}
    <RibbonButton icon={<FolderOpen20Regular />} label={t('Öffnen')} disabled={!xr.one} onClick={() => xr.open()} />
    <RibbonButton icon={<ArrowDownload20Regular />} label={t('Herunterladen')} disabled={!xr.anyFile} onClick={() => xr.download()} />
    <RibbonButton icon={<Share20Regular />} label={t('Teilen')} disabled onClick={() => {}} />
    <RibbonButton icon={<Rename20Regular />} label={t('Umbenennen')} disabled={!xr.one || xr.readOnly} onClick={() => xr.rename()} />
    <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!xr.count || xr.readOnly || xr.busy} onClick={() => xr.remove()} />
    <RibbonDivider />
    <RibbonButton icon={<Cut20Regular />} label={t('Ausschneiden')} disabled={!xr.count || xr.readOnly} onClick={() => xr.cut()} />
    <RibbonButton icon={<Copy20Regular />} label={t('Kopieren')} disabled={!xr.count} onClick={() => xr.copy()} />
    <RibbonButton icon={<ClipboardPaste20Regular />} label={t('Einfügen')} disabled={!xr.canPaste || xr.busy} onClick={() => xr.paste()} />
    <RibbonButton icon={<FolderArrowRight20Regular />} label={t('Verschieben nach')} disabled={!xr.count || xr.readOnly || xr.busy} onClick={() => xr.moveTo()} />
    <RibbonDivider />
    <RibbonButton icon={<ArrowSync20Regular />} label={t('Aktualisieren')} onClick={() => xr.refresh()} />
  </>
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
    {photosView && albumRibbon}
    <RibbonButton icon={<FolderOpen20Regular />} label={t('Öffnen')} disabled={!one} onClick={() => one && open(one)} />
    <RibbonButton icon={<ArrowDownload20Regular />} label={t('Herunterladen')} disabled={!sel.length || !keepable(sel)} onClick={() => download(sel)} />
    <RibbonButton icon={<Share20Regular />} label={t('Teilen')} disabled={!sel.length || sel.some(i => inWithMe(i.path, anyShared) || isEncrypted(i.path))} onClick={() => one ? setShare(one) : setShareMany(sel)} />
    <RibbonButton icon={<Rename20Regular />} label={t('Umbenennen')} disabled={!(one && canChange([one])) && !bulkable(sel)} onClick={() => one ? rename(one) : setBulk(sel)} />
    <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!deletable(sel)} onClick={() => remove(sel)} />
    {photosView && (rejectAsk
      // Zweiter Klick bestätigt: der Knopf steht dann in der Hervorhebungsfarbe
      ? <><RibbonButton primary icon={<DeleteDismiss20Regular />} label={rejectAsk.list.length === 1 ? t('1 Datei löschen') : t('{n} Dateien löschen', { n: rejectAsk.list.length })} onClick={confirmRejected} />
        <RibbonButton icon={<Dismiss20Regular />} label={t('Abbrechen')} title={t('Abbrechen (Esc)')} onClick={() => setRejectAsk(null)} /></>
      : <RibbonButton icon={<DeleteDismiss20Regular />} label={t('Abgelehnte löschen')} disabled={!tlOn || tl.state?.total === 0} onClick={removeRejected} />)}
    <RibbonDivider />
    <RibbonButton icon={<Cut20Regular />} label={t('Ausschneiden')} disabled={!sel.length || !canChange(sel)} onClick={() => toClip(sel, true)} />
    <RibbonButton icon={<Copy20Regular />} label={t('Kopieren')} disabled={!sel.length || !keepable(sel)} onClick={() => toClip(sel, false)} />
    <RibbonButton icon={<ClipboardPaste20Regular />} label={t('Einfügen')} disabled={!clip || path === null || noAdd} onClick={() => paste()} />
    <RibbonButton icon={<FolderArrowRight20Regular />} label={t('Verschieben nach')} disabled={!sel.length || !canChange(sel)} onClick={() => setPicker({ mode: 'move', paths: sel.map(i => i.path) })} />
    <RibbonButton icon={<ArrowUndo20Regular />} label={t('Rückgängig')} disabled={sharedUndo ? !sharedSteps.length : !undos.current.length} onClick={undo} />
    <RibbonDivider />
    <RibbonButton icon={one?.favorite ? <Star20Filled color="#E8A33D" /> : <Star20Regular />} label={t('Favorit')} disabled={!one || inWithMe(one.path, anyShared)} pressed={!!one?.favorite}
      onClick={() => one && favorite(one, !one.favorite)} />
    {toAblage.available && photosView && <RibbonButton icon={<CloudArrowUp20Regular />} label={t('In Ablage sichern')} disabled={!sel.some(onlyInFolder)} onClick={() => toAblage.save(sel)} />}
    <RibbonButton icon={<Wand20Regular />} label={t('Verbessern')} disabled={!sel.some(isPhoto) || !(canChange(sel) || sel.every(extWritable))} onClick={() => setAutoFor(sel)} />
    {/* [feature:photoexport] */}
    {featureOn('photoexport') && photosView && <RibbonButton icon={<ArrowExportLtr20Regular />} label={t('Exportieren')} disabled={!sel.some(isPhoto)}
      onClick={() => setExportIds(selected.filter(p => sel.some(i => i.path === p && isPhoto(i))))} />}
    {/* [/feature:photoexport] */}
    {featureOn('filecats') && <RibbonButton icon={<Tag20Regular />} label={t('Kategorien')} disabled={!sel.some(i => !i.isDir) || sel.some(i => inWithMe(i.path, anyShared) || isAlb(i) || isExt(i))}
      onClick={() => setLabelsFor(sel.filter(i => !i.isDir).map(i => i.path))} />}
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
    {/* Sortieren und Gruppieren nebeneinander, dann die Ansichtsmodi, dann die Einstellungen */}
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
    {!extMount && <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<GroupList20Regular />} label={t('Gruppieren')} /></MenuTrigger>
      <MenuPopover>
        {photosView ? (
          <MenuList checkedValues={{ group: [photoGroup] }} onCheckedValueChange={(_, d) => update({ photoGroup: d.checkedItems[0] as any })}>
            <MenuItemRadio name="group" value="day">{t('Nach Datum')}</MenuItemRadio>
            <MenuItemRadio name="group" value="month">{t('Nach Monat')}</MenuItemRadio>
            <MenuItemRadio name="group" value="year">{t('Nach Jahr')}</MenuItemRadio>
            <MenuItemRadio name="group" value="source">{t('Nach Quelle')}</MenuItemRadio>
            {featureOn('filecats') && <MenuItemRadio name="group" value="category">{t('Nach Kategorie')}</MenuItemRadio>}
            {featureOn('filecats') && <MenuItemRadio name="group" value="tag">{t('Nach Tag')}</MenuItemRadio>}
            <MenuItemRadio name="group" value="none">{t('Nicht gruppieren')}</MenuItemRadio>
          </MenuList>
        ) : (
        <MenuList checkedValues={{ group: [groupBy] }} onCheckedValueChange={(_, d) => setFolderView({ group: d.checkedItems[0] as any }, { fileGroup: d.checkedItems[0] as any })}>
          <MenuItemRadio name="group" value="none">{t('Nicht gruppieren')}</MenuItemRadio>
          <MenuItemRadio name="group" value="kind">{t('Art')}</MenuItemRadio>
          <MenuItemRadio name="group" value="date">{t('Änderungsdatum')}</MenuItemRadio>
        </MenuList>
        )}
      </MenuPopover>
    </Menu>}
    <RibbonDivider />
    {view.kind !== 'photos' && <RibbonButton icon={<AppsList20Regular />} label={t('Details')} pressed={viewMode === 'details'} onClick={() => setFolderView({ view: 'details' }, { fileView: 'details' })} />}
    {view.kind !== 'photos' && <RibbonButton icon={<Grid20Regular />} label={t('Kacheln')} pressed={viewMode === 'tiles'} onClick={() => setFolderView({ view: 'tiles' }, { fileView: 'tiles' })} />}
    {view.kind !== 'photos' && !extMount && <ColumnMenu cols={colCfg} onChange={c => saveCols(c)} labels={view.kind === 'recent' && !searching ? { modified: t('Verwendet') } : undefined}
      trigger={<MenuTrigger disableButtonEnhancement><RibbonButton icon={<ColumnTriple20Regular />} label={t('Spalten')} disabled={viewMode !== 'details'} /></MenuTrigger>} />}
    {photosView && <RibbonButton icon={prefs.photoFit ? <ScaleFit20Regular /> : <ScaleFill20Regular />} label={t('Eingepasst')} pressed={!!prefs.photoFit} onClick={() => update({ photoFit: !prefs.photoFit })} />}
    {photosView && featureOn('tilemodes') && <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<Grid20Regular />} label={t('Kachelmodus')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ mode: [tileMode ?? ''] }} onCheckedValueChange={(_, d) => update({ photoTileMode: (d.checkedItems[0] || undefined) as 'full' | 'image' | 'mosaic' | undefined })}>
          <MenuItemRadio name="mode" value="" secondaryContent="K">{t('Standard')}</MenuItemRadio>
          <MenuItemRadio name="mode" value="full" secondaryContent="K">{t('Voll')}</MenuItemRadio>
          <MenuItemRadio name="mode" value="image" secondaryContent="K">{t('Nur Bild')}</MenuItemRadio>
          {featureOn('mosaicweb') && <MenuItemRadio name="mode" value="mosaic" secondaryContent="K">{t('Mosaik')}</MenuItemRadio>}
        </MenuList>
      </MenuPopover>
    </Menu>}
    {photosView && <RibbonButton icon={<Stack20Regular />} label={t('Stapeln')} pressed={prefs.photoStack ?? true} onClick={() => update({ photoStack: !(prefs.photoStack ?? true) })} />}
    {photosView && <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<TextBulletListSquare20Regular />} label={t('Bildinfos')} disabled={!!tileMode} /></MenuTrigger>
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
          <Slider min={72} max={480} step={4} value={tileNum} onChange={(_, d) => photosView ? update({ fileTilePx: d.value }) : setFolderView({ tilePx: d.value }, { fileTilePx: d.value })} style={{ width: '100%' }} aria-label={t('Kachelgröße')} />
          {photosView && <Text size={100} block style={{ color: tokens.colorNeutralForeground3 }}>{t('Ab {px} px stehen die Bildinfos auf dem Bild.', { px: OVERLAY_PX })}</Text>}
        </div>
      </MenuPopover>
    </Menu>
    {!photosView && <RibbonButton icon={showHidden ? <Eye20Regular /> : <EyeOff20Regular />} label={t('Versteckte Dateien')} pressed={showHidden} onClick={() => update({ fileHidden: !showHidden })} />}
    {photosView && <RibbonButton icon={<Globe20Regular />} label={t('Atlas')} pressed={atlas} onClick={() => setAtlas(a => !a)} />}
    {photosView && <RibbonButton icon={<Filter20Regular />} label={t('Filterleiste')} pressed={!!prefs.photoFilter} onClick={() => update({ photoFilter: !prefs.photoFilter })} />}
    {photosView && <RibbonButton icon={<Edit20Regular />} label={t('Angaben')} disabled={!infoable(sel)} onClick={() => setInfoFor(sel)} />}
    {photosView && <RibbonDivider />}
    {photosView && <RibbonButton icon={<ImageMultiple20Regular />} label={t('Externe Galerie-Ordner')} onClick={() => setGalleryFolders(true)} />}
    {photosView && <RibbonButton icon={<ImageOff20Regular />} label={t('Ausgeschlossene Ordner')} onClick={() => setExcludes(true)} />}
    {photosView && featureOn('importprofiles') && <RibbonButton icon={<ArrowImport20Regular />} label={t('Importprofile')} onClick={() => setImportProfiles(true)} />}
    <RibbonDivider />
    {!gallery && <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />}
    {dualAvail && <RibbonButton icon={<SplitVertical20Regular />} label={t('Zwei Fenster')} pressed={dual} onClick={() => update({ filesDual: !prefs.filesDual })} />}
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
      {i.sharedWith && <Tooltip content={t('Mit Benutzern geteilt')} relationship="label"><People16Regular /></Tooltip>}
      {i.owner && <Tooltip content={i.showOnly ? t('Von {owner} gezeigt – nur ansehen', { owner: i.owner }) : i.canWrite ? t('Von {owner} geteilt – du darfst bearbeiten', { owner: i.owner }) : t('Von {owner} geteilt – nur lesen', { owner: i.owner })} relationship="label"><People16Regular /></Tooltip>}
    </span>
  )
  // Im virtuellen Ordner einer Kategorie: automatisch Eingeordnetes mit Bestätigen/Entfernen
  const autoIn = (i: Item) => view.kind === 'cat' ? (catLabels[i.path]?.cats ?? []).filter(a => a.auto && below(fileCats.cats, view.id).has(a.id)).map(a => a.id) : []
  const badges = (i: Item) => {
    const a = autoIn(i)
    return a.length ? <>{markBadges(i)}<AutoBadge path={i.path} ids={a} onChanged={load} /></> : markBadges(i)
  }
  const dropOnCat = async (e: React.DragEvent, id: string) => {
    let paths: string[]
    try { paths = JSON.parse(e.dataTransfer.getData(DRAG)) } catch { return }
    try {
      await catsApi.setLabels({ paths, add: [id] })
      await reloadCats()
      notify(t('In „{name}“ eingeordnet.', { name: fullName(fileCats.cats, id) }))
      if (view.kind === 'cat' || view.kind === 'tag') load()
      setDetailsVersion(v => v + 1)
    } catch (err) { fail(err) }
  }
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
      case 'shared': return [i.sharedWith && t('Benutzer'), i.shared && t('Link'), i.owner && t('von {owner}', { owner: i.owner })].filter(Boolean).join(', ')
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
        : view.kind === 'photos' ? (filterOn ? t('Keine Bilder passen zum Filter.')
          : albumNow?.kind === 'smart' ? t('Keine Fotos passen zur Regel dieses Albums.')
          : albumNow ? t('Noch keine Fotos in diesem Album. Rechtsklick auf ein Foto → „Zu Album hinzufügen“.')
          : t('Noch keine Fotos.'))
          : view.kind === 'cat' ? t('Noch keine Dateien in dieser Kategorie. Dateien links auf die Kategorie ziehen oder Rechtsklick auf eine Datei → „Kategorien und Tags …“.')
          : view.kind === 'tag' ? t('Keine Dateien mit diesem Tag.')
          : filter ? t('Keine Einträge dieser Art in diesem Ordner.')
          : inChat ? t('Noch nichts gespeichert. Hier landen Fotos und Dateien, die du im Chat mit „Speichern“ ablegst.')
          : t('Leer. Dateien hierher ziehen oder „Hochladen“.')
  const list = () => {
    if (view.kind === 'trash') return <TrashList trash={trash} tiles={viewMode === 'tiles'} search={search} onSearch={setSearch} />
    if (view.kind === 'shared') return <LinksList links={links} notify={m => notify(m)} fail={fail}
      onOpen={(p, isDir) => isDir ? openFolder(p) : (openFolder(parentOf(p)), setTimeout(() => setSelected([p]), 300))}
      onManage={l => l.item && setShare(l.item)} />
    if (!data) return <Spinner />
    // Zeitleiste lädt (erster Aufruf oder anderer Filter, andere Sortierung): laden zeigen, nicht „leer“
    if (tlOn && !tl.state) return <Spinner label={t('Fotos werden geladen …')} style={{ marginTop: 48 }} />
    // Galerie noch leer (kein Filter, kein Album, keine Suche): Erklärung und Einrichtung wie in der Audiothek
    if ((tlOn ? (tl.state?.shown ?? 0) === 0 : items.length === 0) && view.kind === 'photos' && !filterOn && !album && !searching) return (
      <div className={s.firstUse}>
        <ImageMultiple24Regular style={{ fontSize: 40 }} />
        <Text weight="semibold">{t('Noch keine Fotos')}</Text>
        <Text className={s.firstUseText}>{t('Hier erscheinen die Fotos und Videos aus deiner Ablage. Ordner aus externen Speichern nimmst du über „Externe Galerie-Ordner“ auf – die Dateien bleiben, wo sie sind.')}</Text>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', justifyContent: 'center' }}>
          <Button appearance="primary" icon={<FolderOpen20Regular />} onClick={() => setGalleryFolders(true)}>{t('Externe Galerie-Ordner einrichten')}</Button>
          <Button onClick={() => { location.hash = '#/files' }}>{t('Zu den Dateien')}</Button>
        </div>
      </div>
    )
    if (tlOn ? (tl.state?.shown ?? 0) === 0 : items.length === 0) return <Text className={s.empty}>{emptyText()}</Text>
    // Eine Kachel (Galerie und Ordner)
    const tileOf = (i: Item & { used?: string }, mz?: { w: number; stretched: boolean }) => {
          const isSel = selected.includes(i.path)
          return (
            <div key={i.path} data-path={i.path} onMouseDown={e => { if (e.button === 1 && tabsOn && i.isDir) e.preventDefault() }} onAuxClick={e => { if (e.button === 1 && tabsOn && i.isDir && !isExt(i)) { e.preventDefault(); newTab({ kind: 'folder', path: i.path }) } }} style={mz ? { flex: mz.stretched ? `${mz.w} 1 0px` : `0 0 ${mz.w}px`, minWidth: 0 } : tileMode === 'full' && !isSel && metaOf(i)?.pick ? { backgroundColor: metaOf(i)?.pick === 1 ? PICK_TINT : REJECT_TINT } : undefined}
              className={mergeClasses(s.tile, photosView && s.tilePhoto, tileMode === 'full' && s.tileFull, tileMode === 'image' && s.tileBare, mosaic && s.tileMosaic, isSel && (mosaic ? s.tileMosaicSel : s.tileSel), overRow === i.path && s.target, clip?.cut && clip.paths.includes(i.path) && s.cut, cmdKeys && cursor === i.path && s.cursor)}
              aria-selected={isSel} title={i.isDir ? i.name : [i.name, `${fmtSize(i.size)} · ${new Date(takenOf(i, metaOf(i))).toLocaleString(locale)}`, metaOf(i)?.camera, exposureLine(metaOf(i))].filter(Boolean).join('\n')}
              draggable={!touch} onDragStart={e => dragStart(e, i)} {...dropTarget(i)}
              onDoubleClick={() => open(i)} onClick={e => click(e, i)} onContextMenu={e => ctxMenuFor(e, i, isSel)}>
              <span data-tilecheck className={mergeClasses(s.tileCheck, photosView && !touch && !isSel && selected.length === 0 && s.tileCheckQuiet)} style={tileMode === 'full' ? { top: HEAD_H + 6, left: 6 } : undefined} onClick={e => { e.stopPropagation(); anchor.current = i.path; setSelected(sl => isSel ? sl.filter(p => p !== i.path) : [...sl, i.path]) }}>
                <Checkbox checked={isSel} aria-label={t('Auswählen')} />
              </span>
              {(i.favorite || i.shared || i.sharedWith || i.owner || (photosView && i.etag?.includes('~e'))) && <span className={s.tileBadge} style={{ ...(cloudOn(i) ? { right: 28 } : {}), ...(tileMode === 'full' ? { top: HEAD_H + 10 } : {}) }}>
                {/* Bearbeitet (nicht-destruktiv): das Original ist unverändert */}
                {photosView && i.etag?.includes('~e') && <Tooltip content={t('Bearbeitet')} relationship="label"><Edit16Regular className={s.editedBadge} /></Tooltip>}
                {i.favorite && <Star16Filled color="#E8A33D" />}{i.shared && <Link16Regular />}{(i.sharedWith || i.owner) && <People16Regular />}</span>}
              {tileMode === 'full' && <TileHead item={i} meta={metaOf(i)} templates={tileHead} />}
              <div className={mergeClasses(s.thumb, tileMode === 'full' && s.thumbFull, mosaic && s.thumbMosaic, photosView && metaOf(i)?.pick === -1 && s.rejected)}
                style={photosView && (prefs.photoTileLabel || tileMode === 'full') && labelColor(metaOf(i)?.label) ? { boxShadow: `${mosaic ? 'inset ' : ''}0 0 0 ${tileMode === 'full' ? 2 : 3}px ${labelColor(metaOf(i)?.label)}` } : undefined}>
                {i.isDir && covers[i.path]?.length ? (
                  <div style={{ position: 'relative', width: '100%', height: '100%' }}>
                    <div className={s.collage}>
                      {covers[i.path].slice(0, 4).map(c => <Thumb key={c.path} item={c} source={ownSource} size={128} className={s.thumbImg} />)}
                    </div>
                    <Folder20Filled className={s.collageMark} />
                  </div>
                ) : hasThumb(i) ? <Thumb item={i} source={galleryTileSource} size={thumbPxFor(tileNum, extOffline(i))} className={s.thumbImg} fallbackLarge={!extOffline(i)} fit={photosView && prefs.photoFit ? 'contain' : 'cover'} /> : <KindIcon item={i} large />}
                {i.stack?.length || i.stackPos ? <span className={mergeClasses(s.stackBadge, s.stackBadgeClick, (i.favorite || i.shared || i.sharedWith || i.owner || (photosView && i.etag?.includes('~e'))) && s.stackBadgeLow)} style={cloudOn(i) ? { right: 26 } : undefined}
                  title={i.stackPos ? t('Stapel zuklappen') : t('Stapel aufklappen') + ': ' + (i.stack ?? []).map(x => x.dupe ? t('Kopie: {name}', { name: x.name }) : x.name).join(', ')}
                  onClick={e => { e.stopPropagation(); toggleStack(i.stackPos?.lead ?? i.path) }} onDoubleClick={e => e.stopPropagation()}>
                  {i.stackPos ? `${i.stackPos.n}/${i.stackPos.of}` : stackLabel(i)}</span> : null}
                {/* Mosaik: Auswahl/Ablehnen und Farbpunkt oben links, Sterne unten rechts, Details beim Überfahren unten */}
                {mosaic && (() => {
                  const m = metaOf(i)
                  return <>
                    {isSel && <span className={s.mosSelFrame} />}
                    {(m?.pick || m?.label) && <span data-mtl className={s.mosTL} style={isSel || selected.length > 0 ? { left: 32 } : undefined}>
                      {m.pick === 1 && <span style={{ background: '#2F8F5B', borderRadius: 4, padding: '1px 4px' }} title={t('Ausgewählt')}>⚑</span>}
                      {m.pick === -1 && <span style={{ background: '#C4403A', borderRadius: 4, padding: '1px 4px' }} title={t('Abgelehnt')}>✕</span>}
                      {m.label && <span className={s.markDot} style={{ background: labelColor(m.label), border: '1.5px solid #fff' }} />}
                    </span>}
                    {m?.rating ? <span data-mbr className={s.mosBR}>{'★'.repeat(m.rating)}</span> : null}
                    {!i.isDir && <div data-mhover className={s.mosHover}>
                      <span className={s.mosHoverTitle}>{m?.title || i.name}</span>
                      <span className={s.mosHoverLine}>{[new Date(takenOf(i, m)).toLocaleDateString(locale, { dateStyle: 'medium' }), m?.camera].filter(Boolean).join(' · ')}</span>
                    </div>}
                  </>
                })()}
                {/* Markierung, Farbe, Sterne: ohne Bildinfos auf dem Bild unten am Bildrand */}
                {photosView && !tileMode && !(infoOnImage && tileInfo.length > 0) && marksOf(i) && <span className={s.marks} style={extOffline(i) ? { left: 28 } : undefined}>{marksOf(i)}</span>}
                {/* Herkunft: Ablage in dmOffice oder Galerie-Ordner (nicht mit dem Hinweis „nicht erreichbar“ an derselben Stelle) */}
                {cloudOn(i) && <SourceCloud path={i.path} saving={savingIds.has(i.path)}
                  onSave={toAblage.available ? () => toAblage.save(selected.includes(i.path) ? sel : [i]) : undefined} />}
                {extOffline(i) && <Tooltip content={t('Der externe Ordner ist gerade nicht erreichbar – nur die Vorschau ist da.')} relationship="label">
                  <span className={s.offBadge}><Prohibited16Filled /></span></Tooltip>}
                {/* Große Kacheln: Bildinfos auf dem Bild, unten in einer hellen Leiste */}
                {infoOnImage && tileInfo.length > 0 && <div className={s.tileOverlay}>
                  {/* Markierung, Farbe, Sterne als eigene Zeile über den Bildinfos */}
                  {marksOf(i) && <div className={s.marksRow}>{marksOf(i)}</div>}
                  {tileInfo.map(k => {
                    const [l, r] = TILE_INFOS[k].value(i, metaOf(i))
                    return <div key={k} className={s.tileInfo} title={[l, r].filter(Boolean).join(' · ')}>
                      <span className={s.tileInfoL}>{l || '\u00a0'}</span>{r && <span className={s.tileInfoR}>{r}</span>}
                    </div>
                  })}
                </div>}
              </div>
              {/* Galerie: Bildinfos unter dem Bild wie eine Bildunterschrift, links und rechts (wie im Kamera-Importer) */}
              {barOn && !i.isDir && <TileBar meta={metaOf(i)} quick={qcOn && !isAlb(i) ? inQuick(i) : undefined} turnable={turnable(i)}
                onPick={pick => void markPhotos({ pick }, barList(i))} onRating={rating => void markPhotos({ rating }, barList(i))}
                onLabelMenu={at => setLabelMenu({ at, list: barList(i), cur: metaOf(i)?.label })}
                onQuick={() => void toggleQuick(barList(i), i)} onTurn={deg => turnPhotos(barList(i), deg)} />}
              {photosView && !tileMode && !infoOnImage && tileInfo.length > 0 && <div className={s.tileInfos}>
                {tileInfo.map(k => {
                  const [l, r] = TILE_INFOS[k].value(i, metaOf(i))
                  return <div key={k} className={s.tileInfo} title={[l, r].filter(Boolean).join(' · ')}>
                    <span className={s.tileInfoL}>{l || '\u00a0'}</span>{r && <span className={s.tileInfoR}>{r}</span>}
                  </div>
                })}
              </div>}
              {!photosView && <span className={s.tileName}><ShownName name={i.name} /></span>}
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
            : b.rows
              // Mosaik: Zeilen gleicher Höhe, jede füllt die Breite
              ? <div key={b.key} data-grid style={{ display: 'flex', flexDirection: 'column', gap: MOSAIC_GAP, paddingTop: 4 }}>
                {b.rows.map(r => <div key={r.start} data-mrow style={{ display: 'flex', gap: MOSAIC_GAP, height: r.h }}>
                  {b.items.slice(r.start, r.start + r.count).map((i, k) => tileOf(i, { w: r.widths[k], stretched: r.stretched }))}</div>)}
              </div>
              : <div key={b.key} className={s.tiles} data-grid style={{ ['--tile' as any]: tilePx }}>{b.items.map(i => tileOf(i))}</div>)}
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
              {g.items.map(i => tileOf(i))}
            </div>
          </Fragment>
        ))}
        {shown.length > cap && <MoreOnScroll onMore={() => setCap(c => c + step)} />}
      </>
    )
    return (
      <>
      <Table size="small" aria-label={t('Dateien')} style={cols >= 2 ? { minWidth: 36 + (nameShown ?? 220) + shownCols.reduce((n, c) => n + c.width, 0) + 44 } : undefined}>
        <TableHeader onContextMenu={e => { if (cols >= 2) { e.preventDefault(); e.stopPropagation(); setColMenu({ x: e.clientX, y: e.clientY }) } }}>
          <TableRow>
            <TableHeaderCell style={{ width: 36 }}>
              <Checkbox checked={sel.length === 0 ? false : sel.length === items.length ? true : 'mixed'} aria-label={t('Alle auswählen')}
                onChange={() => setSelected(sel.length === items.length ? [] : items.map(i => i.path))} />
            </TableHeaderCell>
            <ColHead label={t('Name')} width={nameShown} minWidth={220} sortable={sortable} sorted={sortable && headSort === 'name'} desc={headDesc} onSort={() => headSortBy('name')}
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
          {cappedGroups.flatMap(g => [
            ...(g.label ? [<TableRow key={'g:' + g.label} className={s.groupRow}><TableCell colSpan={shownCols.length + (filler ? 4 : 3)}><span className={s.groupHead} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>{g.label} <span className={s.muted}>{g.total}</span>{groupBox(g.label)}</span></TableCell></TableRow>] : []),
            ...g.items.map(i => {
            const isSel = selected.includes(i.path)
            const when = view.kind === 'recent' && !searching && i.used ? i.used : i.modified
            return (
              <TableRow key={i.path} data-path={i.path} onMouseDown={e => { if (e.button === 1 && tabsOn && i.isDir) e.preventDefault() }} onAuxClick={e => { if (e.button === 1 && tabsOn && i.isDir && !isExt(i)) { e.preventDefault(); newTab({ kind: 'folder', path: i.path }) } }} className={mergeClasses(s.row, isSel && s.sel, overRow === i.path && s.target, clip?.cut && clip.paths.includes(i.path) && s.cut, cmdKeys && cursor === i.path && s.cursor)}
                aria-selected={isSel} draggable={!touch} onDragStart={e => dragStart(e, i)} {...dropTarget(i)}
                onDoubleClick={() => open(i)} onClick={e => click(e, i)} onContextMenu={e => ctxMenuFor(e, i, isSel)}>
                <TableCell onClick={e => { e.stopPropagation(); anchor.current = i.path; setSelected(sl => isSel ? sl.filter(p => p !== i.path) : [...sl, i.path]) }}>
                  <Checkbox checked={isSel} aria-label={t('Auswählen')} />
                </TableCell>
                <TableCell style={{ minWidth: 0, overflow: 'hidden' }}>
                  <TableCellLayout truncate title={plainName(i.name)} media={<span style={{ position: 'relative', display: 'inline-flex' }}><span className={s.rowIcon}>{hasThumb(i) && kindOf(i) === 'image' ? <Thumb item={i} source={gallerySource} className={s.rowThumb} /> : <KindIcon item={i} />}</span>
                    {i.dms && <Tooltip content={t('Dokument im DMS')} relationship="label"><DocumentBulletList16Filled className={s.dmsMark} /></Tooltip>}</span>}
                    description={i.snippet ? <span title={t('Im Inhalt gefunden')}><Snippet text={i.snippet} /></span>
                      : cols < 2 ? [flat ? '/' + parentOf(i.path) : null, fmtWhen(when, locale), i.isDir ? null : fmtSize(i.size)].filter(Boolean).join(' · ') : undefined}>
                    <ShownName name={i.name} />{badges(i)}
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
      {shown.length > cap && <MoreOnScroll onMore={() => setCap(c => c + step)} />}
      {cols >= 2 && <ColumnMenu cols={colCfg} onChange={c => saveCols(c)} at={colMenu} onClose={() => setColMenu(null)} labels={recentView ? { modified: t('Verwendet') } : undefined} />}
      </>
    )
  }

  // Rechtsklick auf die Einträge unter dem Ordnerbaum (Zuletzt verwendet, Favoriten, Chat, Papierkorb, Tresor …)
  const navCtx = (nav: NonNullable<NonNullable<typeof ctx>['nav']>) => (e: React.MouseEvent) => { e.preventDefault(); e.stopPropagation(); setCtx({ x: e.clientX, y: e.clientY, kind: 'nav', nav }) }
  const openNav = (nav: NonNullable<NonNullable<typeof ctx>['nav']>) => {
    if (nav === 'chat') sideFolder(CHAT_DIR)
    else if (nav === 'withme') sideFolder(WITH_ME)
    else if (nav === 'tresor') openTresor(TRESOR)
    else sideGo({ kind: nav })
    if (mobile) closeSide()
  }
  /** Tresor wird geschlossen: Fenster, die darin stehen, vorher hinaus - sonst lädt die Ansicht ihn noch einmal und fragt nach dem Passwort */
  const leaveTresor = () => {
    if (inTresor) openFolder('')
    if (!pane && dual && isTresor(path2Live)) paneGo({ kind: 'folder', path: '' })
  }
  /** Ordner aus der Seitenleiste öffnen: mit einem Baum je Fenster gehört der Baum dem linken, sonst steuert er das aktive */
  const treeOpen = (p: string) => { if (dual && dualTreeOwn) openFolder(p); else sideFolder(p) }
  const emptyTrash = async () => {
    if (!await askConfirm('', t('Papierkorb leeren? Alles darin wird endgültig gelöscht.'))) return
    try { await api.post('/api/files/trash/empty') } catch (e) { fail(e) }
    trash.load(); setTreeVersion(v => v + 1); loadUsage()
  }
  // „Zuletzt verwendet“ leeren: die Dateien selbst bleiben, nur die Liste beginnt neu
  const clearRecent = async () => {
    if (!await askConfirm('', t('Verlauf unter „Zuletzt verwendet“ löschen? Die Dateien selbst bleiben unverändert.'), t('Löschen'))) return
    try { await api.del('/api/files/recent'); if (view.kind === 'recent') load() } catch (e) { fail(e) }
  }
  const navMenu = (nav: NonNullable<NonNullable<typeof ctx>['nav']>) => <MenuList>
    {nav === 'tresor' && tresor && !tresor.exists
      ? <MenuItem icon={<LockOpen20Regular />} onClick={() => openNav(nav)}>{t('Tresor einrichten …')}</MenuItem>
      : <MenuItem icon={<FolderOpen20Regular />} onClick={() => openNav(nav)}>{t('Öffnen')}</MenuItem>}
    {nav === 'trash' && <MenuItem icon={<Delete20Regular />} onClick={emptyTrash}>{t('Papierkorb leeren')}</MenuItem>}
    {featureOn('navtop') && (MOVABLE as string[]).includes(nav) && (() => {
      const cur = prefs.filesNavTop ?? ['favorites']
      const on = cur.includes(nav)
      return <MenuItem icon={on ? <ArrowDown20Regular /> : <ArrowUp20Regular />} onClick={() => update({ filesNavTop: on ? cur.filter(x => x !== nav) : [...cur, nav] })}>
        {on ? t('Nicht mehr oben anzeigen') : t('Oben anzeigen')}</MenuItem>
    })()}
    {nav === 'recent' && featureOn('recentclear') && <MenuItem icon={<Delete20Regular />} onClick={clearRecent}>{t('Verlauf löschen')}</MenuItem>}
    {nav === 'tresor' && (tresor?.open
      ? <MenuItem icon={<LockClosed20Regular />} onClick={() => { closeTresor().then(leaveTresor).catch(fail) }}>{t('Tresor schließen')}</MenuItem>
      : tresor?.exists && <MenuItem icon={<LockOpen20Regular />} onClick={() => openTresor(TRESOR)}>{t('Tresor öffnen …')}</MenuItem>)}
    {nav === 'tresor' && tresor?.exists && !tresor.oldFormat && (
      <MenuItem icon={tresor.settings?.dav ? <CloudOff20Regular /> : <Cloud20Regular />}
        onClick={() => davHide({ path: 'dmVAULT', name: t('Tresor'), isDir: true, size: 0, mime: null, modified: '' }, !!tresor.settings?.dav).then(() => window.dispatchEvent(new Event(TRESOR_EVENT)))}>
        {tresor.settings?.dav ? t('In WebDAV und Nextcloud ausblenden') : t('In WebDAV und Nextcloud zeigen')}</MenuItem>
    )}
  </MenuList>
  const navEntry = (k: Special, icon: JSX.Element) => (
    <div className={mergeClasses(s.nav, navView.kind === k && !navExt && s.active)} onClick={() => { sideGo({ kind: k }); if (mobile) closeSide() }} onContextMenu={navCtx(k)}>
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

  // Oberer Bereich der Seitenleiste: welche Einträge über „Meine Dateien“ stehen
  const top: MovableNav[] = featureOn('navtop') ? (prefs.filesNavTop ?? ['favorites']).filter((k): k is MovableNav => (MOVABLE as string[]).includes(k)) : []
  const favBlock = <>
          <div className={mergeClasses(s.nav, navView.kind === 'favorites' && !navExt && s.active)} onClick={() => { sideGo({ kind: 'favorites' }); if (mobile) closeSide() }} onContextMenu={navCtx('favorites')}>
            <span className={s.twisty} onClick={e => { e.stopPropagation(); toggleFavs() }}>
              {favs.some(f => f.isDir) ? favOpen ? <ChevronDown16Regular /> : <ChevronRight16Regular /> : null}</span>
            <Star20Regular /><span className={s.navName}>{viewTitle('favorites')}</span>
          </div>
          {favOpen && favs.filter(f => f.isDir).map(f => (
            <div key={f.path} className={mergeClasses(s.nav, navPath === f.path && s.active)} style={{ paddingLeft: 28 }} title={'/' + f.path}
              onClick={() => { sideFolder(f.path); if (mobile) closeSide() }}
              onDragOver={e => { if (e.dataTransfer.types.includes(DRAG) || e.dataTransfer.types.includes('Files')) e.preventDefault() }}
              onDrop={e => { e.stopPropagation(); dropOn(e, f.path) }}
              onContextMenu={e => { e.preventDefault(); e.stopPropagation(); setCtx({ x: e.clientX, y: e.clientY, kind: 'folder', folder: f.path }) }}>
              <span className={s.twisty} /><Folder20Regular /><span className={s.navName}>{f.name}</span>
            </div>
          ))}
  </>
  // Zwei Fenster (wie in Directory Opus): Ordner im anderen Fenster öffnen, vergleichen, tauschen, Fenster schließen - für das aktive Fenster
  const dualRibbon = dual && <>
    <RibbonDivider />
    <RibbonButton icon={pane ? <ArrowExportRtl20Regular /> : <ArrowExportLtr20Regular />} label={pane ? t('Im linken Fenster öffnen') : t('Im rechten Fenster öffnen')}
      title={(pane ? t('Diesen Ordner im linken Fenster öffnen') : t('Diesen Ordner im rechten Fenster öffnen')) + (cmdKeys ? ' (Alt+I)' : '')}
      disabled={path === null || !!arc || !!ext} onClick={() => { if (path !== null) openInOther(path) }} />
    <RibbonButton icon={<ColumnDoubleCompare20Regular />} label={t('Vergleichen')} disabled={path === null || otherPath === null || !!arc || !!ext}
      title={t('Ordner vergleichen: markiert in beiden Fenstern, was im anderen fehlt, neuer ist oder eine andere Größe hat')} onClick={() => window.dispatchEvent(new Event(PANE_COMPARE))} />
    <RibbonButton icon={<ArrowSwap20Regular />} label={t('Tauschen')} disabled={path === null || otherPath === null || !!arc || !!ext}
      title={t('Ordner der beiden Fenster tauschen') + (cmdKeys ? ' (Strg+U)' : '')} onClick={() => dualKeys.onSwap()} />
    <RibbonButton icon={<Dismiss20Regular />} label={pane ? t('Rechtes Fenster schließen') : t('Zweifenster-Ansicht schließen')}
      title={(pane ? t('Rechtes Fenster schließen') : t('Zweifenster-Ansicht schließen')) + (cmdKeys ? ' (F10)' : '')} onClick={() => update({ filesDual: false })} />
  </>
  // Abschnitte im Inhalt (Archiv, Atlas, Personen) setzen ihre Knöpfe an den Anfang; im Archiv gelten nur dessen Befehle
  const ribbonEl = <RibbonHeader area="files" onMenu={toggleSide}
    start={<><div ref={setSecSlot} style={{ display: 'contents' }} />{arc ? null : extMount ? extStart : start}{dualRibbon}</>}
    view={<><div ref={setSecViewSlot} style={{ display: 'contents' }} />{viewRibbon}</>} />
  const contentSearchEl = contentSearch && <ContentSearch under={path ?? ''} onGo={p => { setContentSearch(false); openFolder(p) }} onClose={() => setContentSearch(false)} />
  const tabLabel = (e: TabEntry) => {
    if (e.ext) { const m = extMounts.find(x => x.id === e.ext!.mount); return e.ext.path ? nameOf(e.ext.path) : m?.name ?? t('Externer Ordner') }
    const v = e.view
    return v.kind === 'folder' ? (v.path ? nameOf(v.path) : t('Meine Dateien')) : v.kind === 'tag' ? '#' + v.tag : v.kind === 'cat' ? t('Kategorie') : viewTitle(v.kind)
  }
  const tabStrip = tabsOn && <FolderTabs labels={tabs.list.map(tabLabel)} external={tabs.list.map(e => !!e.ext)} active={tabs.i} onPick={pickTab} onClose={closeTab} onNew={() => newTab(view, ext)} />
  const mainInner = <RibbonSlot.Provider value={secSlot}><RibbonViewSlot.Provider value={secViewSlot}>
        {arc ? <ArchiveView key={arc.file} file={arc.file} dir={arc.dir} onDir={d => setArc({ file: arc.file, dir: d })}
              onLeave={folder => { setArc(null); openFolder(folder) }} onPending={n => { arcPending.current = { file: arc.file, n } }} />
          : extMount && ext ? <ExternalView key={ext.mount} mount={extMount} path={ext.path} onPath={p => setExt({ mount: ext.mount, path: p })} ribbon={extRibbon} onRibbonState={setExtRibbonKey}
            look={{ view: viewMode, sort, desc, tilePx }} onLook={(k, d) => setFolderView({ sort: k, desc: d }, { fileSort: k, fileSortDesc: d })}
            paneKey={e => cmdKeys && dual && dualKey(e, dualKeys, null, null)} focusReq={extFocus}
            vaultPanel={(root, raw) => <VaultUnlock path={root} onOpen={p => { cmvRoots.add(root); setExt(null); openFolder(p) }} onRaw={raw} />}
            onVaultOpen={root => { cmvRoots.add(root); setExt(null); openFolder(CMV + '/' + root) }} />
          : lockedVault && path && !lockedVault.unlocked ? <VaultUnlock path={path} onOpen={p => { cmvRoots.add(path); openFolder(p) }} onRaw={() => setRawVaults(r => [...r, path])} /> : (
        <div ref={mainRef} className={mergeClasses(s.main, dragging && s.drop)} tabIndex={0} onKeyDown={keys}
          onClick={e => { if (e.target === e.currentTarget) setSelected([]) }}
          onDragOver={e => { if (path !== null && !noAdd && (e.dataTransfer.types.includes('Files') || (dual && e.dataTransfer.types.includes(otherMark)))) { e.preventDefault(); setDragging(true) } }}
          onDragLeave={e => { if (e.currentTarget === e.target) setDragging(false) }}
          onDrop={e => { if (path !== null && !noAdd && ((e.dataTransfer.types.includes('Files') && !e.dataTransfer.types.includes(DRAG)) || (dual && e.dataTransfer.types.includes(otherMark)))) dropOn(e, path); else { e.preventDefault(); setDragging(false) } }}>
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
            {addressBar && !photosView && !searching && <div style={{ display: 'flex', alignItems: 'center', flexShrink: 0 }}>
              <Button size="small" appearance="subtle" icon={<ArrowLeft20Regular />} disabled={!hist.current.back.length} key={'ab' + histTick} onClick={() => histGo(-1)} aria-label={t('Zurück')} title={t('Zurück (Alt+←)')} />
              <Button size="small" appearance="subtle" icon={<ArrowRight20Regular />} disabled={!hist.current.fwd.length} key={'af' + histTick} onClick={() => histGo(1)} aria-label={t('Vor')} title={t('Vor (Alt+→)')} />
              <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} disabled={view.kind === 'folder' && !path} onClick={goUp} aria-label={t('Eine Ebene höher')} title={t('Eine Ebene höher (Alt+↑)')} />
            </div>}
            <div className={s.crumbs} ref={el => { if (el) el.scrollLeft = el.scrollWidth }} title={addressBar && view.kind === 'folder' && !inTresor && !isCmv(path) && !ext ? t('In die freie Fläche klicken, um einen Pfad einzugeben') : undefined}
              onClick={e => { if (addressBar && e.target === e.currentTarget && view.kind === 'folder' && !inTresor && !isCmv(path) && !ext && !searching) setEditPath('/' + (path ?? '')) }}>
              {editPath !== null ? <Input autoFocus size="small" value={editPath} style={{ width: '100%' }} aria-label={t('Pfad')}
                onChange={(_, d) => setEditPath(d.value)} onBlur={() => setEditPath(null)}
                onKeyDown={e => { if (e.key === 'Enter') void goTyped(editPath); else if (e.key === 'Escape') setEditPath(null) }} />
              : searching ? <span className={s.viewTitle}>{t('Suchergebnisse für „{q}“', { q: term })}</span>
                : view.kind !== 'folder' ? <div style={{ display: 'flex', alignItems: 'center', gap: 16, minWidth: 0, flexWrap: 'wrap' }}>{!photosView && <span className={s.viewTitle}>{(view.kind === 'cat' ? fullName(fileCats.cats, view.id) : view.kind === 'tag' ? '#' + view.tag : viewTitle(view.kind))}</span>}
                  {photosView && <span style={{ display: 'inline-flex', alignItems: 'center', gap: 2 }}
                    onContextMenu={albumNow && featureOn('ctxall') ? e => albumCtx.open(e, null) : undefined}>
                    <AlbumPicker albums={albums} current={album} onPick={setAlbum} />{albumMenu}</span>}
                  {photosView && data && <PhotoQuickBar inline value={prefs.photoQuick} onChange={q => update({ photoQuick: q })} />}
                  {photosView && prefs.photoFilter && data && <PhotoFilterTop total={tlOn ? tl.state?.total ?? 0 : sortedItems.length} cols={filterCols} onCols={c => update({ photoFilterCols: c })}
                    sel={photoSel} onSel={setPhotoSel} shown={tlOn ? tlShown : items.length} pending={tlOn ? 0 : meta?.pending ?? 0} />}</div> : (
                  <Breadcrumb>
                    {inTresor
                      ? <BreadcrumbItem><BreadcrumbButton icon={<LockOpen20Regular style={{ color: OPEN_COLOR }} />} onClick={() => openFolder(TRESOR)} current={parts.length <= 1}
                          onDragOver={(e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG)) e.preventDefault() }} onDrop={(e: React.DragEvent) => dropOn(e, TRESOR)}>{t('Tresor')}</BreadcrumbButton></BreadcrumbItem>
                      : <BreadcrumbItem><BreadcrumbButton icon={<Home20Regular />} onClick={() => openFolder('')} current={!parts.length}
                      onDragOver={(e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG)) e.preventDefault() }} onDrop={(e: React.DragEvent) => dropOn(e, '')}>{t('Meine Dateien')}</BreadcrumbButton></BreadcrumbItem>}
                    {parts.map((p, i) => {
                      // Sehr tiefe Pfade: nur die ersten und letzten Ebenen, dazwischen „…“ (Hunderte Schaltflächen hielten die Seite an)
                      const cut = parts.length > CRUMBS && i > 0 && i < parts.length - CRUMBS + 1
                      if (cut && i !== parts.length - CRUMBS) return null
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
                          onDragOver={(e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG)) e.preventDefault() }} onDrop={(e: React.DragEvent) => dropOn(e, target)}
                          title={cut ? p : undefined}>{cut ? '…' : p}</BreadcrumbButton></BreadcrumbItem>,
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
            {
              photosView && atlas
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
          {timeline && !atlas && months.length > 2 && <Timeline months={months} total={tlOn ? tlShown : shown.length} top={listRef.current?.offsetTop ?? 0} onJump={jumpTo} scroller={listRef} />}
          {undoBar && !sharedUndo && (() => {
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
          {labelMenuEl}
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
          {photosView && data && rejectAsk && <div ref={rejectBar} role="alertdialog" aria-label={t('Abgelehnte löschen')} style={{ display: 'flex', flexWrap: 'wrap', alignItems: 'center', gap: 8, padding: '6px 12px',
            borderTop: `1px solid ${tokens.colorNeutralStroke2}`, background: tokens.colorNeutralBackground3 }}>
            <span style={{ flex: '1 1 300px', minWidth: 0 }}>{rejectQuestion(rejectAsk)} {t('Zum Löschen oben auf „{label}“ klicken.', { label: rejectAsk.list.length === 1 ? t('1 Datei löschen') : t('{n} Dateien löschen', { n: rejectAsk.list.length }) })}</span>
          </div>}
          {photosView && data && !rejectAsk && <PhotoSetBar count={sel.filter(i => !i.isDir).length} onPick={p => markPhotos({ pick: p })} onRating={r => markPhotos({ rating: r })}
            onLabel={l => markPhotos({ label: l })} onDelete={deletable(sel) ? () => remove(sel) : undefined} />}
          {props !== null && <FolderProps path={props} onClose={() => setProps(null)} />}
      {bulk && <BulkRename items={bulk} dir={path ?? ''} siblings={items.map(i => i.name)} onClose={() => setBulk(null)}
        onDone={moves => {
          setBulk(null)
          pushUndo({ label: t('{n} umbenannt', { n: moves.length }), run: async () => { for (const m of [...moves].reverse()) await api.post('/api/files/move', { from: m.to, to: m.from, copy: false }) } })
          changed(); setSelected(moves.map(m => m.to))
        }} />}
      {pattern && <Dialog open onOpenChange={(_, d) => { if (!d.open) setPattern(null) }}>
        <DialogSurface style={{ maxWidth: 400 }}>
          <DialogBody>
            <DialogTitle>{pattern.add ? t('Auswahl erweitern') : t('Auswahl verkleinern')}</DialogTitle>
            <DialogContent>
              <Field label={t('Muster')} hint={t('* steht für beliebig viele Zeichen, ? für eines. Mehrere Muster mit Semikolon trennen, z. B. *.pdf;*.docx')}>
                <Input autoFocus value={pattern.value} onChange={(_, d) => setPattern({ ...pattern, value: d.value })}
                  onFocus={e => e.target.select()}
                  onKeyDown={e => { if (e.key === 'Enter') { applyPattern(pattern.add, pattern.value); setPattern(null) } }} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={() => setPattern(null)}>{t('Abbrechen')}</Button>
              <Button appearance="primary" onClick={() => { applyPattern(pattern.add, pattern.value); setPattern(null) }}>{pattern.add ? t('Auswählen') : t('Abwählen')}</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
          {/* Rechtsklick-Menue an der Mausposition */}
          {ctx && (ctx.kind !== 'items' || sel.length > 0) && (
            <Menu open onOpenChange={(_, d) => !d.open && setCtx(null)} positioning={{ autoSize: 'height', target: { getBoundingClientRect: () => ({ x: ctx.x, y: ctx.y, top: ctx.y, left: ctx.x, bottom: ctx.y, right: ctx.x, width: 0, height: 0 }) } as any }}>
              <MenuPopover onClick={() => setCtx(null)}>
                {ctx.kind === 'items' ? menuItems(sel) : ctx.kind === 'empty' ? emptyMenu : ctx.kind === 'nav' && ctx.nav ? navMenu(ctx.nav) : folderMenu(ctx.folder ?? '')}
              </MenuPopover>
            </Menu>
          )}
        </div>
        )}
  </RibbonViewSlot.Provider></RibbonSlot.Provider>
  const mainArea = tabsOn ? <div style={{ flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0 }}>{tabStrip}{mainInner}</div> : mainInner
  const detailsEl = <>
        {detailsOpen && inFiles && !extMount && !mobile && detailsW.handle('left')}
        {detailsOpen && inFiles && !extMount && (
          <Details onMark={photosView ? (b, list) => { markPhotos(b, list) } : undefined} width={mobile ? undefined : detailsW.width} item={detailItem} meta={detailItem ? metaOf(detailItem) : undefined} metaOf={metaOf}
            onFilter={photosView && detailItem && (!prefs.photoFilter || (pickNext != null && pickNext < 4)) ? what => pickFilter(detailItem, metaOf(detailItem), what) : undefined} selection={sel} version={detailsVersion} onClose={() => setDetails(false)}
            onOpen={open} onOpenFolder={(p, f) => { openPlace(p, f); if (mobile) setDetails(false) }} onShare={setShare}
            onFavorite={favorite} onDownload={keepable(detailItem ? [detailItem, ...sel] : sel) ? download : undefined} fail={fail} notify={m => notify(m)} onChanged={changed}
            onLabelsMany={featureOn('filecats') ? items => { const own = items.filter(i => !inWithMe(i.path, anyShared) && !isAlb(i) && !isExt(i)); if (own.length) setLabelsFor(own.map(i => i.path)); else notify(t('Kategorien und Tags gibt es nur für eigene Dateien, nicht für Ordner.')) } : undefined}
            labels={featureOn('filecats') && detailItem && !detailItem.isDir && !inWithMe(detailItem.path, anyShared) && !isAlb(detailItem) && !isExt(detailItem)
              ? <LabelsDetail path={detailItem.path} version={detailsVersion} onEdit={() => setLabelsFor([detailItem.path])}
                  onOpenCat={id => go({ kind: 'cat', id })} onOpenTag={tag => go({ kind: 'tag', tag })} /> : undefined} />
        )}
  </>
  const dialogs = <>
      {labelsFor && <LabelsDialog paths={labelsFor} onClose={() => setLabelsFor(null)} onDone={changed} />}
      {toAblage.dialog}
      {autoFor && <PhotoAutoDialog items={autoFor}
        onClose={() => setAutoFor(null)} onStarted={changed} />}
      {/* [feature:photoexport] */}
      {exportIds && <PhotoExportDialog ids={exportIds} onClose={() => setExportIds(null)} />}
      {/* [/feature:photoexport] */}
      {infoFor && <PhotoInfoDialog items={infoFor} metaOf={metaOf} onClose={() => setInfoFor(null)} onDone={changed} />}
      {manageCats && <ManageCategories onClose={() => setManageCats(false)} />}
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
          onSaveToAblage={toAblage.available ? (i => toAblage.save([i])) : undefined}
          onEnhance={featureOn('enhancebtn') ? (i => setAutoFor([i])) : undefined}
          onClose={() => { setViewer(null); setStartEdit(false); mainRef.current?.focus() }}
          onDownload={keepable(viewer.list) ? (i => download([i])) : undefined} onDetails={i => { setViewer(null); setSelected([i.path]); setViewerDetail(i); setDetails(true) }}
          // [feature:lenscorr]
          onPhotoEdit={async (i, rotate, crop, overwrite, adjust, lens) => {
            try {
              const r = await api.post<{ path: string }>('/api/files/photo-edit', { path: i.path, rotate, crop, overwrite, adjust, lens: lens ?? null })
          // [feature:lenscorr:else]
          // onPhotoEdit={async (i, rotate, crop, overwrite, adjust) => {
          //   try {
          //     const r = await api.post<{ path: string }>('/api/files/photo-edit', { path: i.path, rotate, crop, overwrite, adjust })
          // [/feature:lenscorr]
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
              // [feature:lenscorr]
              if (st) fp = (await api.put<{ fingerprint: string | null }>('/api/files/photo-edits', { path: i.path, rotate: st.rotate, crop: st.crop, adjust: st.adjust, lens: st.lens ?? null })).fingerprint
              // [feature:lenscorr:else]
              // if (st) fp = (await api.put<{ fingerprint: string | null }>('/api/files/photo-edits', { path: i.path, rotate: st.rotate, crop: st.crop, adjust: st.adjust })).fingerprint
              // [/feature:lenscorr]
              else await api.del('/api/files/photo-edits?' + q({ path: i.path }))
              // Neuer Etag mit Fingerabdruck: Betrachter und Raster holen die bearbeitete Fassung
              const base = (i.etag ?? '').split('~e')[0]
              const etag = fp ? base + '~e' + fp : base
              setViewer(v => v && { ...v, list: v.list.map(x => x.path === i.path ? { ...x, etag } : x) })
              notify(st ? t('Bearbeitung gespeichert - das Original bleibt unverändert.') : t('Bearbeitung entfernt.'))
              changed()
            } catch (e) { fail(e); throw e }
          }}
          side={mobile ? undefined : (i, close) => <Details inViewer onMark={photosView ? (b, list) => { markPhotos(b, list) } : undefined} item={i} meta={metaOf(i)} selection={[]} version={detailsVersion} onClose={close}
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
  </>
  // Zweites Fenster: eingebettet in den Bereich des linken - Menüband per Portal (nur aktiv), ohne Seitenleiste, Detailbereich nur aktiv
  if (pane) return (
    <>
      {pane.ribbon && pane.active && createPortal(ribbonEl, pane.ribbon)}
      {contentSearchEl}
      <PaneBox on active={pane.active} onActivate={pane.onActivate}>
        {pane.ownTree && <div className={s.paneTree}>
          <FolderTree showHidden={showHidden} current={ext || inTresor || isCmv(path) ? null : path} onOpen={openFolder} onDropOn={dropOn} version={treeVersion} listed={listed} exclude={[CHAT_DIR]}
            onPaste={f => { if (clip && writableAt(f)) paste(f) }}
            onContext={(e, f) => setCtx({ x: e.clientX, y: e.clientY, kind: 'folder', folder: f })} />
        </div>}
        {mainArea}
      </PaneBox>
      {pane.active && pane.details && createPortal(detailsEl, pane.details)}
      {dialogs}
    </>
  )
  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      {/* Zwei Fenster: hier steht das Menüband des aktiven Fensters (das rechte setzt seines per Portal hier ein) */}
      {dual && <div ref={setRibbonSlot} style={{ display: 'contents' }} />}
      {!full && !toPane2 && ribbonEl}
      {contentSearchEl}
      <div className={s.root}>
        {!gallery && <SidePane as="aside" className={s.side} open={sidePane} onClose={closeSide} width={{ key: 'files.side', def: 240 }}>
          {/* Oberer Bereich (Kennung navtop): Einträge, die man öfter braucht, über „Meine Dateien“ - Vorgabe die Favoriten */}
          {top.length > 0 && <>{top.map(k => <Fragment key={k}>{k === 'favorites' ? favBlock : navEntry(k, NAV_ICONS[k])}</Fragment>)}<div className={s.navSep} /></>}
          <FolderTree showHidden={showHidden} current={dual && dualTreeOwn ? (ext || inTresor || isCmv(path) ? null : path) : navExt || isTresor(navPath) || isCmv(navPath) ? null : navPath}
            onOpen={p => { treeOpen(p); if (mobile) closeSide() }} onDropOn={dropOn} version={treeVersion} listed={listed} exclude={[CHAT_DIR]}
            onPaste={f => { if (clip && writableAt(f)) paste(f) }}
            onContext={(e, f) => setCtx({ x: e.clientX, y: e.clientY, kind: 'folder', folder: f })} />
          <div className={s.navSep} />
          {!top.includes('recent') && navEntry('recent', <Clock20Regular />)}
          {!top.includes('favorites') && favBlock}
          {anyShared && <>
            <div className={mergeClasses(s.nav, inWithMe(navPath ?? '', anyShared) && navPath === WITH_ME && s.active)}
              onClick={() => { sideFolder(WITH_ME); if (mobile) closeSide() }} onContextMenu={navCtx('withme')}>
              <span className={s.twisty} /><People20Regular /><span className={s.navName}>{t(WITH_ME)}</span>
            </div>
            {/* In der Seitenleiste nur Ordner; geteilte Dateien stehen im Ordner „Mit mir geteilt“ */}
            {withMe.filter(m => m.isDir !== false).map(m => (
              <div key={m.id} className={mergeClasses(s.nav, navPath !== null && (navPath === m.path || navPath.startsWith(m.path + '/')) && s.active)} style={{ paddingLeft: 30 }}
                title={t('Von {owner}', { owner: m.owner }) + (m.showOnly ? ' · ' + t('nur ansehen') : m.canWrite ? '' : ' · ' + t('nur lesen'))}
                onClick={() => { sideFolder(m.path); if (mobile) closeSide() }}>
                <Folder20Regular /><span className={s.navName}>{m.name}</span>
              </div>
            ))}
          </>}
          <div className={mergeClasses(s.nav, !navExt && navPath !== null && (navPath === CHAT_DIR || navPath.startsWith(CHAT_DIR + '/')) && s.active)} title={t('Fotos und Dateien aus dem Chat')}
            onClick={() => { sideFolder(CHAT_DIR); if (mobile) closeSide() }} onContextMenu={navCtx('chat')}>
            <span className={s.twisty} /><Chat20Regular /><span className={s.navName}>{t('Chat')}</span>
          </div>
          {!top.includes('shared') && navEntry('shared', <LinkSquare20Regular />)}
          {!top.includes('trash') && navEntry('trash', <Delete20Regular />)}
          {tresor && featureOn('vault') && <>
            <div className={mergeClasses(s.nav, navPath === TRESOR && s.active)} onClick={() => openTresor(TRESOR)} onContextMenu={navCtx('tresor')}
              title={tresor.open ? t('Tresor – offen') : tresor.exists ? t('Tresor – geschlossen') : t('Tresor')}>
              <span className={s.twisty} />
              {/* Klick aufs Schloss: offen → schließen, geschlossen → öffnen (wie im DMS) */}
              <span role="button" title={tresor.open ? t('Tresor schließen') : t('Tresor öffnen')} style={{ display: 'inline-flex', cursor: 'pointer' }}
                onClick={e => {
                  e.stopPropagation()
                  // Erst aus dem Tresor heraus, dann schließen - sonst lädt die Ansicht ihn noch einmal und fragt nach dem Passwort
                  if (tresor.open) { leaveTresor(); closeTresor().catch(fail) }
                  else openTresor(TRESOR)
                }}>
                {tresor.open ? <LockOpen20Regular style={{ color: OPEN_COLOR }} /> : <LockClosed20Regular />}
              </span>
              <span className={s.navName}>{t('Tresor')}</span>
            </div>
            {tresor.open && tresorFolders.map(f => (
              <div key={f.path} className={mergeClasses(s.nav, (navPath === f.path || navPath?.startsWith(f.path + '/')) && s.active)} style={{ paddingLeft: 28 }} onClick={() => openTresor(f.path)}>
                <span className={s.twisty} /><Folder20Regular /><span className={s.navName}>{f.name}</span>
              </div>
            ))}
          </>}
          {/* Release-Liste: Datei-Kategorien erst mit ihrer Version */}
          {featureOn('filecats') && <><div className={s.navSep} />
          <CategoryNav cls={{ nav: s.nav, active: s.active, target: s.target, twisty: s.twisty, navName: s.navName }}
            current={!navExt && navView.kind === 'cat' ? navView.id : null} currentTag={!navExt && navView.kind === 'tag' ? navView.tag : null}
            onOpen={id => { sideGo({ kind: 'cat', id }); if (mobile) closeSide() }} onOpenTag={tag => { sideGo({ kind: 'tag', tag }); if (mobile) closeSide() }}
            accepts={e => e.dataTransfer.types.includes(DRAG)} onDrop={dropOnCat} onManage={() => setManageCats(true)} /></>}
          {(extMounts.length > 0 || extCanAdd) && <>
            <div className={s.navSep} />
            <div className={nv.section} style={{ cursor: 'pointer', marginTop: 4 }}
              onClick={foldExt} title={extFolded ? t('Aufklappen') : t('Zuklappen')}>
              {extFolded ? <ChevronRight16Regular /> : <ChevronDown16Regular />}
              <span style={{ flex: 1, marginLeft: 4 }}>{t('Externe Ordner')}</span>
              {extCanAdd && <Button size="small" appearance="subtle" icon={<Add16Regular />} aria-label={t('Externen Ordner einbinden')} title={t('Externen Ordner einbinden')} onClick={e => { e.stopPropagation(); setAddExt(true) }} />}
            </div>
            {!extFolded && extMounts.map(m => (
              <div key={m.id} className={mergeClasses(s.nav, navExt?.mount === m.id && s.active)} style={m.available === false ? { opacity: 0.5 } : undefined}
                title={m.available === false ? t('Gerade nicht erreichbar') : m.kind === 'webdav' ? (m.sftp ? t('SFTP-Ordner – Rechtsklick: umbenennen, Ordner auswählen oder entfernen') : t('WebDAV-Ordner – Rechtsklick: umbenennen, Ordner auswählen oder entfernen')) : m.kind === 'onedrive' ? t('OneDrive-Ordner – Rechtsklick: umbenennen, Ordner auswählen oder entfernen') : m.kind === 'pcloud' ? t('pCloud-Ordner – Rechtsklick: umbenennen, Ordner auswählen oder entfernen') : t('Ordner auf dem Server')}
                onClick={() => { if (m.available === false) { notify(t('Ordner nicht erreichbar'), t('{name} ist gerade nicht erreichbar.', { name: m.name })); return } sideExt({ mount: m.id, path: '' }); if (mobile) closeSide() }}
                onContextMenu={m.kind !== 'folder' ? e => { e.preventDefault(); setExtSetup({ mount: m }) } : undefined}>
                <span className={s.twisty} /><span style={{ display: 'inline-flex', color: EXT_COLOR }}>{m.sftp ? <Server20Regular /> : m.kind === 'webdav' ? <Globe20Regular /> : m.kind === 'onedrive' || m.kind === 'pcloud' ? <Cloud20Regular /> : <HardDrive20Regular />}</span><span className={s.navName}>{m.name}</span>
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
        <PaneBox on={dual} active={active === 0} onActivate={() => setActive(0)}>
          {quickOn && active === 1 && <QuickView item={quickItem} src={i => gallerySource.download(i, true)} />}
          <div style={{ display: quickOn && active === 1 ? 'none' : 'contents' }}>{mainArea}</div>
        </PaneBox>
        {dual && quickOn && active === 0 && <PaneBox on active={false} onActivate={() => setActive(1)}><QuickView item={quickItem} src={i => gallerySource.download(i, true)} /></PaneBox>}
        {dual && <div style={{ display: quickOn && active === 0 ? 'none' : 'contents' }}><FilesView pane={{ start: path2, req: paneReq, onState: onPane2State, active: active === 1, onActivate: () => setActive(1), keys: dualKeys,
          otherPath: ext || arc ? null : path, otherKey: viewKey, version: paneTick, onChanged: () => refresh.current(), ribbon: ribbonSlot, details: detailsSlot, ownTree: dualTreeOwn,
          focus: paneFocus, openInOther: openFolder, onCurrent: setQuickItem }} /></div>}
        {isActivePane && detailsEl}
        {dual && <div ref={setDetailsSlot} style={{ display: 'contents' }} />}
      </div>
      {dialogs}
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

/** Oberkante des Blocks n (beim ersten Block eines Abschnitts samt Kopf). */
// Überschriften der Galerie (Monat oder Tag): je Zeitpunkt einmal formatiert - toLocaleDateString legt bei jedem Aufruf ein neues
// Format an und kostete bei einigen tausend geladenen Fotos bei jedem Nachladen eines Monats spürbar Zeit
const dateLabels = new Map<string, string>()
const dateFormats = new Map<string, Intl.DateTimeFormat>()
function dateLabel(d: Date, day: boolean, locale: string) {
  const k = locale + (day ? 'd' : 'm') + d.getTime()
  let v = dateLabels.get(k)
  if (v == null) {
    const fk = locale + (day ? 'd' : 'm')
    let f = dateFormats.get(fk)
    if (!f) { f = new Intl.DateTimeFormat(locale, day ? { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' } : { month: 'long', year: 'numeric' }); dateFormats.set(fk, f) }
    v = f.format(d)
    if (dateLabels.size > 20000) dateLabels.clear()
    dateLabels.set(k, v)
  }
  return v
}
function blockTop(b: Block[], n: number) { return n > 0 && b[n - 1].head ? b[n - 1].off : b[n].off }
/** Zu zeichnende Blöcke für die Scrollposition top: das Sichtbare plus anderthalb Bildschirme davor und zweieinhalb danach. */
function rangeAt(b: Block[], scrollTop: number, height: number): [number, number] {
  const top = scrollTop - height * 1.5, bottom = scrollTop + height * 2.5
  let a = b.findIndex(x => x.off + x.h >= top)
  if (a < 0) a = Math.max(0, b.length - 1)
  let z = a
  while (z < b.length - 1 && b[z + 1].off <= bottom) z++
  return [a, z]
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
  // [feature:lenscorr]
  onPhotoEdit?: (i: Item, rotate: number, crop: import('./PhotoEditor').CropRect | null, overwrite: boolean, adjust: import('./PhotoEditor').Adjust | null,
    lens?: import('./PhotoEditor').PhotoSettings['lens']) => Promise<void>
  // [feature:lenscorr:else]
  // onPhotoEdit?: (i: Item, rotate: number, crop: import('./PhotoEditor').CropRect | null, overwrite: boolean, adjust: import('./PhotoEditor').Adjust | null) => Promise<void>
  // [/feature:lenscorr]
  onPhotoSettings?: (i: Item, s: import('./PhotoEditor').PhotoSettings | null) => Promise<void>
  markOf?: (i: Item) => Meta | undefined
  onMark?: (i: Item, body: { pick?: number; rating?: number; label?: string }) => void
  onShare?: (i: Item) => void; onFavorite?: (i: Item) => void; onDelete?: (i: Item) => void
  groupOf?: (i: Item) => string
  onSaveToAblage?: (i: Item) => void
  onEnhance?: (i: Item) => void
}) {
  return <Viewer items={viewer.list} index={viewer.index} source={gallerySource} startEdit={startEdit} {...rest} />
}

/**
 * „Ins DMS aufnehmen“ nur für Dateien, die noch kein Dokument sind (fragt beim Öffnen des Menüs nach). Ist eine einzelne Datei
 * schon ein Dokument: „Im DMS zeigen“; sind mehrere schon im DMS, entfällt der Eintrag.
 */
function ToDmsItem({ files, onPick }: { files: Item[]; onPick: (list: Item[]) => void }) {
  const [docs, setDocs] = useState<Map<string, number> | null>(null)
  const key = files.map(i => i.path).join('|')
  useEffect(() => {
    let live = true
    api.post<{ docs: { path: string; id: number }[] }>('/api/dms/doc-paths', { paths: files.map(i => i.path) })
      .then(r => { if (live) setDocs(new Map(r.docs.map(d => [d.path, d.id]))) }).catch(() => { if (live) setDocs(new Map()) })
    return () => { live = false }
  }, [key]) // eslint-disable-line react-hooks/exhaustive-deps
  if (!docs) return null
  const rest = files.filter(i => !docs.has(i.path))
  if (rest.length) return <MenuItem icon={<DocumentBulletList20Regular />} onClick={() => onPick(rest)}>{t('Ins DMS aufnehmen')}</MenuItem>
  if (featureOn('dmsmark') && files.length === 1 && docs.get(files[0].path) != null)
    return <MenuItem icon={<DocumentBulletList20Regular />} onClick={() => { location.hash = '#/dms/doc/' + docs.get(files[0].path) }}>{t('Im DMS zeigen')}</MenuItem>
  return null
}

/** Eigenschaften eines Ordners: Anzahl Dateien und Unterordner, Gesamtgröße, Zeitpunkte (Rechtsklick → Eigenschaften). */
function FolderProps({ path, onClose }: { path: string; onClose: () => void }) {
  const { fail } = useApp()
  const [p, setP] = useState<{ files: number; folders: number; bytes: number; modified: string | null; created: string | null; changed: string | null } | null>(null)
  useEffect(() => { api.get<NonNullable<typeof p>>('/api/files/props?' + q({ path })).then(setP).catch(e => { fail(e); onClose() }) }, [path]) // eslint-disable-line react-hooks/exhaustive-deps
  const when = (v: string | null) => v ? new Date(v).toLocaleString(locale) : '–'
  const row = (label: string, value: string) => <><Text style={{ color: tokens.colorNeutralForeground3 }}>{label}</Text><Text style={{ minWidth: 0, overflowWrap: 'anywhere' }}>{value}</Text></>
  return <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
    <DialogSurface style={{ maxWidth: 440 }}>
      <DialogBody>
        <DialogTitle>{t('Eigenschaften von „{name}“', { name: path ? path.split('/').pop()! : t('Meine Dateien') })}</DialogTitle>
        <DialogContent>
          {!p ? <Spinner size="small" /> : <div style={{ display: 'grid', gridTemplateColumns: 'max-content minmax(0, 1fr)', gap: '6px 16px' }}>
            {row(t('Ort'), '/' + path)}
            {row(t('Inhalt'), [p.files === 1 ? t('1 Datei') : t('{n} Dateien', { n: p.files.toLocaleString(locale) }), p.folders === 1 ? t('1 Ordner') : t('{n} Ordner', { n: p.folders.toLocaleString(locale) })].join(', '))}
            {row(t('Größe'), fmtSize(p.bytes) + (p.bytes >= 1024 ? ` (${p.bytes.toLocaleString(locale)} Bytes)` : ''))}
            {row(t('Zuletzt geändert darin'), when(p.modified))}
            {path && row(t('Angelegt'), when(p.created))}
          </div>}
        </DialogContent>
        <DialogActions><Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button></DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}
