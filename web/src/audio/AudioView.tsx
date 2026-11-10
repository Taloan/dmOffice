import {
  Button, Input, Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, Spinner, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Album20Regular, ArrowDown16Regular, ArrowShuffle20Regular, ArrowUp16Regular, Delete20Regular, Dismiss20Regular, FolderOpen20Regular, MusicNote120Regular,
  MusicNote224Regular, PanelLeft20Regular, Person20Regular, Play16Filled, Play20Filled, Prohibited16Filled, Search20Regular, Tag20Regular,
  TextBulletListLtr20Regular, TextBulletListSquare20Regular, TextGrammarArrowRight20Regular,
  Add20Regular, AppsListDetail20Regular, ArrowTrending20Regular, EyeOff20Regular, History20Regular, Hourglass20Regular, Rename20Regular, Save20Regular,
  Sparkle20Regular, TextBulletListAdd20Regular, AppsList20Regular, Grid20Regular,
} from '@fluentui/react-icons'
import { Fragment, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useApp } from '../App'
import { t } from '../i18n'
import { usePrefs } from '../prefs'
import { AlphaBar, BAR_WIDTH, useLetters } from './AlphaBar'
import { useNavStyles } from '../shell/nav'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { SidePane, useIsMobile } from '../shell/mobile'
import { askConfirm, askText } from '../shell/context'
import { AudioFoldersDialog } from './AudioFolders'
import {
  Album, albumKey, albumsOf, artistOf, artistsOf, byLibrary, coverUrl, coversOf, fmtDuration, genresList, genresOf, Group, norm, offline, refreshTracks, search, Stat, Track,
  useReach, useStats, useTracks, autoName, PlaylistInfo, playlistApi, usePlaylists,
} from './library'
import { player, useCurrent, usePlayer } from './player'

type View = 'tracks' | 'albums' | 'artists' | 'genres' | 'queue' | 'playlist'
const VIEWS: View[] = ['tracks', 'albums', 'artists', 'genres', 'queue', 'playlist']

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0, position: 'relative' },
  side: { width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0 },
  head: { display: 'flex', alignItems: 'center', gap: '12px', padding: '12px 20px 8px', flexShrink: 0, flexWrap: 'wrap',
    '@media (max-width: 767px)': { padding: '8px 12px' } },
  title: { fontSize: '20px', fontWeight: 600, flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  search: { width: '280px', maxWidth: '100%', boxSizing: 'border-box', '@media (max-width: 767px)': { width: 'auto', flex: '1 1 100%', minWidth: 0 } },
  muted: { color: tokens.colorNeutralForeground3 },
  empty: { margin: 'auto', textAlign: 'center', maxWidth: '420px', display: 'flex', flexDirection: 'column', gap: '12px', alignItems: 'center', padding: '24px' },
  // Titelliste: Kopf fest, Zeilen virtuell (zehntausende Titel)
  table: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' },
  thead: { display: 'grid', alignItems: 'center', gap: '12px', padding: '0 20px', minHeight: '30px', flexShrink: 0, fontSize: '12px', color: tokens.colorNeutralForeground3,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  th: { cursor: 'pointer', userSelect: 'none', display: 'flex', alignItems: 'center', gap: '2px', overflow: 'hidden', whiteSpace: 'nowrap', ':hover': { color: tokens.colorNeutralForeground1 } },
  // Rahmen um einen Scrollbereich: hält die A-Z-Leiste
  pane: { flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column', position: 'relative' },
  scroll: { flex: 1, minHeight: 0, overflowY: 'auto', position: 'relative', outline: 'none' },
  row: { display: 'grid', alignItems: 'center', gap: '12px', padding: '0 20px', position: 'absolute', left: 0, right: 0, boxSizing: 'border-box', cursor: 'default',
    userSelect: 'none', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover }, '& .dm-play': { display: 'none' }, ':hover .dm-play': { display: 'inline-flex' },
    ':hover .dm-lead': { display: 'none' }, '@media (max-width: 767px)': { padding: '0 12px' } },
  sel: { backgroundColor: tokens.colorNeutralBackground1Selected, ':hover': { backgroundColor: tokens.colorNeutralBackground1Selected } },
  playingRow: { color: tokens.colorBrandForeground1, fontWeight: 600 },
  off: { color: tokens.colorNeutralForegroundDisabled },
  cell: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums', color: tokens.colorNeutralForeground3 },
  lead: { width: '32px', height: '32px', display: 'flex', alignItems: 'center', justifyContent: 'center', position: 'relative', '& .dm-lead': { display: 'contents' } },
  thumb: { width: '32px', height: '32px', borderRadius: tokens.borderRadiusSmall, objectFit: 'cover', backgroundColor: tokens.colorNeutralBackground3,
    display: 'flex', alignItems: 'center', justifyContent: 'center', color: tokens.colorNeutralForeground3 },
  thumbLarge: { width: '44px', height: '44px' },
  offIcon: { color: '#E5484D', display: 'inline-flex' },
  two: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  // Kacheln (Raster in TileGrid)
  card: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0, cursor: 'pointer', '& .dm-play': { opacity: 0 }, ':hover .dm-play': { opacity: 1 } },
  cardCover: { width: '100%', aspectRatio: '1', borderRadius: tokens.borderRadiusMedium, objectFit: 'cover', backgroundColor: tokens.colorNeutralBackground3,
    display: 'flex', alignItems: 'center', justifyContent: 'center', color: tokens.colorNeutralForeground3, boxShadow: tokens.shadow4 },
  cardBox: { position: 'relative' },
  // Bis zu vier Albumcover als 2×2
  mosaic: { display: 'grid', gridTemplateColumns: '1fr 1fr', gridTemplateRows: '1fr 1fr', overflow: 'hidden', padding: 0,
    '& img': { width: '100%', height: '100%', objectFit: 'cover', display: 'block', minWidth: 0, minHeight: 0 } },
  playingTitle: { color: tokens.colorBrandForeground1 },
  cardPlay: { position: 'absolute', right: '8px', bottom: '8px', transition: 'opacity .15s', boxShadow: tokens.shadow8 },
  // Kopf einer Detailansicht (Album, Interpret, Genre)
  hero: { display: 'flex', gap: '20px', padding: '16px 20px', alignItems: 'flex-end', flexShrink: 0, '@media (max-width: 767px)': { padding: '12px', gap: '12px' } },
  heroCover: { width: '160px', height: '160px', borderRadius: tokens.borderRadiusLarge, objectFit: 'cover', flexShrink: 0, boxShadow: tokens.shadow8,
    backgroundColor: tokens.colorNeutralBackground3, display: 'flex', alignItems: 'center', justifyContent: 'center', color: tokens.colorNeutralForeground3,
    '@media (max-width: 767px)': { width: '110px', height: '110px' } },
  heroText: { display: 'flex', flexDirection: 'column', gap: '4px', minWidth: 0 },
  heroTitle: { fontSize: '26px', fontWeight: 700, lineHeight: '32px', '@media (max-width: 767px)': { fontSize: '20px', lineHeight: '26px' } },
  link: { cursor: 'pointer', ':hover': { textDecoration: 'underline' } },
  actions: { display: 'flex', gap: '8px', marginTop: '8px', flexWrap: 'wrap' },
  strip: { display: 'flex', gap: '14px', overflowX: 'auto', padding: '4px 20px 12px', flexShrink: 0, '& > *': { width: '130px', flexShrink: 0 } },
  // Gruppen (Interpreten, Genres)
  groupRow: { display: 'flex', alignItems: 'center', gap: '12px', padding: '0 20px', position: 'absolute', left: 0, right: 0, boxSizing: 'border-box', cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover }, '@media (max-width: 767px)': { padding: '0 12px' } },
  dragOver: { boxShadow: `inset 0 2px 0 ${tokens.colorBrandStroke1}` },
})

/** Ansicht und Auswahl aus der Adresse: #/audio/albums/<Schlüssel> */
function route(): { view: View; key: string | null } {
  const parts = location.hash.replace(/^#\/?/, '').split('/')
  const view = (VIEWS.includes(parts[1] as View) ? parts[1] : 'tracks') as View
  return { view, key: parts[2] ? decodeURIComponent(parts.slice(2).join('/')) : null }
}
const goto = (view: View, key?: string | null) => { location.hash = '#/audio/' + view + (key ? '/' + encodeURIComponent(key) : '') }

const playable = (x: Track) => !offline.has(x.source)

/** Liste abspielen ab einem Titel - Titel aus nicht erreichbaren Ordnern bleiben draußen. */
function playFrom(list: Track[], start: Track | null, notify: (title: string, body?: string, intent?: 'success' | 'error' | 'info' | 'warning') => void) {
  const ok = list.filter(playable)
  if (!ok.length) { notify(t('Nicht erreichbar'), t('Der externe Ordner ist gerade nicht erreichbar.'), 'warning'); return }
  player.play(ok, start ? Math.max(0, ok.indexOf(start)) : 0)
}

function Cover({ id, className, icon }: { id: string | null; className: string; icon?: JSX.Element }) {
  const [broken, setBroken] = useState(false)
  useEffect(() => setBroken(false), [id])
  if (id && !broken) return <img className={className} src={coverUrl(id)} alt="" loading="lazy" onError={() => setBroken(true)} />
  return <div className={className}>{icon ?? <MusicNote224Regular />}</div>
}

/** Bild einer Gruppe: ein Cover für sich, zwei bis vier als 2×2-Mosaik, ohne Cover das Symbol */
function Mosaic({ covers, className, icon }: { covers: string[]; className: string; icon?: JSX.Element }) {
  const s = useStyles()
  if (covers.length < 2) return <Cover id={covers[0] ?? null} className={className} icon={icon} />
  // Zwei Cover über Kreuz, drei mit dem ersten noch einmal
  const four = covers.length === 2 ? [covers[0], covers[1], covers[1], covers[0]] : [0, 1, 2, 3].map(i => covers[i % covers.length])
  return (
    <div className={mergeClasses(className, s.mosaic)}>
      {four.map((c, i) => <img key={i} src={coverUrl(c)} alt="" loading="lazy" onError={e => { e.currentTarget.style.visibility = 'hidden' }} />)}
    </div>
  )
}

/** Scrollbereich: Stand und Größe, neu bei jedem Scrollen und jeder Größenänderung */
function useScrollBox() {
  const ref = useRef<HTMLDivElement>(null)
  const [box, setBox] = useState({ top: 0, height: 600, width: 800 })
  const read = (el: HTMLDivElement) => setBox({ top: el.scrollTop, height: el.clientHeight, width: el.clientWidth })
  useEffect(() => {
    const el = ref.current
    if (!el) return
    const ro = new ResizeObserver(() => read(el))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])
  return { ref, box, onScroll: (e: React.UIEvent<HTMLDivElement>) => read(e.currentTarget) }
}

/** Feste Zeilenhöhe, nur die sichtbaren Zeilen im DOM. */
function useVirtual(count: number, rowHeight: number) {
  const { ref, box, onScroll } = useScrollBox()
  const first = Math.max(0, Math.floor(box.top / rowHeight) - 8)
  const last = Math.min(count, Math.ceil((box.top + box.height) / rowHeight) + 8)
  return { ref, first, last, total: count * rowHeight, onScroll }
}

/** Platz für die A-Z-Leiste: Scrollbereich rechts frei, eigene Scrollleiste aus (wie in der Galerie) */
const barSpace = (on: boolean): React.CSSProperties | undefined => on ? { paddingRight: BAR_WIDTH, scrollbarWidth: 'none' } : undefined

/** Verweis in einer Zeile: springt zu Interpret, Album oder Genre, ohne die Zeile zu wählen oder abzuspielen */
function Link({ label, onGo }: { label: string; onGo: () => void }) {
  const s = useStyles()
  return <span className={s.link} onClick={e => { e.stopPropagation(); onGo() }} onDoubleClick={e => e.stopPropagation()}>{label}</span>
}

/** Kontextmenü: die gewählten Titel; rows = ihre Stellen in der angezeigten Playlist (zum Entfernen) */
type Ctx = { x: number; y: number; tracks: Track[]; rows?: number[] }
type SortCol = 'title' | 'artist' | 'album' | 'year' | 'genre' | 'plays' | 'duration'
/** Gezogene Titel (Kennungen) - Ablage auf eine Playlist in der Seitenleiste */
const DRAG_TYPE = 'application/x-dmoffice-tracks'

/**
 * Titelliste: Doppelklick (Handy: Tippen) spielt die Liste ab diesem Titel, Rechtsklick öffnet das Menü. Mehrfachauswahl mit
 * Strg und Umschalt; gewählte Titel lassen sich auf eine Playlist ziehen. mode album: Titelnummer statt Cover;
 * playlist: Stelle in der Playlist, mit onReorder per Ziehen umsortierbar (dann ohne Sortieren über die Spalten).
 */
function TrackTable({ tracks, mode, stats, onMenu, onReorder, onRemove, alpha }: {
  tracks: Track[]; mode?: 'album' | 'playlist'; stats: Map<string, Stat>; onMenu: (c: Ctx) => void
  /** A-Z-Leiste rechts (Bereich Titel), nach der Spalte, nach der sortiert ist */
  alpha?: boolean
  /** Neue Reihenfolge als Stellen der bisherigen Liste */
  onReorder?: (order: number[]) => void
  /** Entf in einer eigenen Playlist: diese Stellen herausnehmen */
  onRemove?: (rows: number[]) => void
}) {
  const s = useStyles()
  const { notify } = useApp()
  const mobile = useIsMobile()
  const [current, playing] = useCurrent()
  const [sort, setSort] = useState<{ col: SortCol; desc: boolean } | null>(null)
  // Auswahl als Stellen in tracks (eine Playlist darf einen Titel mehrmals haben)
  const [sel, setSel] = useState<Set<number>>(new Set())
  const [anchor, setAnchor] = useState<number | null>(null)
  const [dropAt, setDropAt] = useState<number | null>(null)
  const dragging = useRef<number[] | null>(null)
  useEffect(() => { setSort(null) }, [mode])
  useEffect(() => { setSel(new Set()); setAnchor(null) }, [tracks])
  const numbered = mode === 'album'
  const list = useMemo(() => {
    const rows = tracks.map((x, pos) => ({ x, pos }))
    if (!sort) return rows
    const v = (x: Track): string | number => sort.col === 'plays' ? stats.get(x.id)?.plays ?? 0 : sort.col === 'artist' ? artistOf(x).toLowerCase()
      : sort.col === 'year' ? x.year ?? 0 : sort.col === 'duration' ? x.duration ?? 0 : ((x[sort.col] as string | null) ?? '').toLowerCase()
    return rows.sort((a, b) => { const A = v(a.x), B = v(b.x); return (A < B ? -1 : A > B ? 1 : byLibrary(a.x, b.x)) * (sort.desc ? -1 : 1) })
  }, [tracks, sort, stats])
  // Ohne Sortierung gilt die Bibliotheksreihenfolge (Interpret zuerst); nach Jahr, Gehört, Dauer keine Buchstaben
  const names = useMemo(() => {
    const col = sort?.col ?? 'artist'
    if (!alpha || !['artist', 'title', 'album', 'genre'].includes(col)) return null
    return list.map(({ x }) => col === 'artist' ? artistOf(x) : col === 'title' ? x.title : (col === 'album' ? x.album : x.genre) ?? '')
  }, [alpha, list, sort])
  const letters = useLetters(names)
  const rowHeight = mobile ? 56 : 36
  const vl = useVirtual(list.length, rowHeight)
  const cols = mobile ? '44px minmax(0, 1fr) auto'
    : numbered ? '32px minmax(160px, 4fr) minmax(100px, 3fr) 70px 60px'
    : '32px minmax(160px, 3fr) minmax(100px, 2fr) minmax(100px, 2fr) 52px minmax(70px, 1fr) 62px 56px'
  const head = (col: SortCol, label: string, right?: boolean) => (
    <div className={s.th} style={right ? { justifyContent: 'flex-end' } : undefined}
      onClick={() => { if (!onReorder) setSort(x => x?.col === col ? (x.desc ? null : { col, desc: true }) : { col, desc: false }) }}>
      {label}{sort?.col === col && (sort.desc ? <ArrowDown16Regular /> : <ArrowUp16Regular />)}
    </div>
  )
  const play = (x: Track) => playFrom(list.map(r => r.x), x, notify)
  /** Gewählte Stellen in Anzeigereihenfolge; ein Titel außerhalb der Auswahl steht für sich allein */
  const chosen = (extra?: number) => {
    const set = extra != null && !sel.has(extra) ? new Set([extra]) : sel
    return list.filter(r => set.has(r.pos)).map(r => r.pos)
  }
  const click = (e: React.MouseEvent, row: number, pos: number) => {
    if (e.shiftKey && anchor != null) {
      const a = list.findIndex(r => r.pos === anchor)
      const [from, to] = a < row ? [a, row] : [row, a]
      setSel(new Set(list.slice(from, to + 1).map(r => r.pos)))
      return
    }
    if (e.ctrlKey || e.metaKey) setSel(x => { const n = new Set(x); if (n.has(pos)) n.delete(pos); else n.add(pos); return n })
    else setSel(new Set([pos]))
    setAnchor(pos)
  }
  const menu = (e: React.MouseEvent, pos: number) => {
    e.preventDefault()
    const rows = chosen(pos)
    if (!sel.has(pos)) { setSel(new Set([pos])); setAnchor(pos) }
    onMenu({ x: e.clientX, y: e.clientY, tracks: rows.map(p => tracks[p]), rows: mode === 'playlist' ? rows : undefined })
  }
  const select = (n: number) => {
    const r = list[n]
    if (!r) return
    setSel(new Set([r.pos])); setAnchor(r.pos)
    const el = vl.ref.current
    if (el && (n * rowHeight < el.scrollTop || (n + 1) * rowHeight > el.scrollTop + el.clientHeight)) el.scrollTop = n * rowHeight - el.clientHeight / 2
  }
  return (
    <div className={s.table}>
      {!mobile && <div className={s.thead} style={{ gridTemplateColumns: cols, paddingRight: letters ? 20 + BAR_WIDTH : undefined }}>
        <div className={s.num}>{mode ? '#' : ''}</div>
        {head('title', t('Titel'))}
        {head('artist', t('Interpret'))}
        {!numbered && head('album', t('Album'))}
        {!numbered && head('year', t('Jahr'), true)}
        {!numbered && head('genre', t('Genre'))}
        {head('plays', t('Gehört'), true)}
        {head('duration', t('Dauer'), true)}
      </div>}
      <div className={s.pane}>
      <div ref={vl.ref} className={s.scroll} style={barSpace(!!letters)} onScroll={vl.onScroll} tabIndex={0}
        onKeyDown={e => {
          const i = anchor == null ? -1 : list.findIndex(r => r.pos === anchor)
          if (e.key === 'Enter' && i >= 0) play(list[i].x)
          else if ((e.key === 'a' || e.key === 'A') && (e.ctrlKey || e.metaKey)) { e.preventDefault(); setSel(new Set(list.map(r => r.pos))) }
          else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') { e.preventDefault(); select(Math.max(0, Math.min(list.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))) }
          else if (e.key === 'Delete' && onRemove && sel.size) { e.preventDefault(); onRemove(chosen()) }
        }}
        onDragOver={e => { if (onReorder && dragging.current) e.preventDefault() }}
        onDrop={e => {
          if (!onReorder || !dragging.current) return
          e.preventDefault()
          const moved = dragging.current
          const at = dropAt ?? tracks.length
          const rest = tracks.map((_, p) => p).filter(p => !moved.includes(p))
          const before = rest.filter(p => p < at).length
          onReorder([...rest.slice(0, before), ...moved, ...rest.slice(before)])
          dragging.current = null; setDropAt(null)
        }}>
        <div style={{ height: vl.total, position: 'relative' }}>
          {list.slice(vl.first, vl.last).map(({ x, pos }, k) => {
            const i = vl.first + k
            const on = x.id === current
            const off = !playable(x)
            const st = stats.get(x.id)
            const lead = off ? <span className={s.offIcon} title={t('Der externe Ordner ist gerade nicht erreichbar.')}><Prohibited16Filled /></span>
              : on && playing ? <MusicNote120Regular />
              : numbered ? <span className={s.num}>{x.track ?? ''}</span>
              : mode === 'playlist' ? <span className={s.num}>{pos + 1}</span>
              : <Cover id={x.cover} className={mergeClasses(s.thumb, mobile && s.thumbLarge)} />
            return (
              <div key={pos} className={mergeClasses(s.row, sel.has(pos) && s.sel, on && s.playingRow, off && s.off, dropAt === pos && s.dragOver)}
                style={{ top: i * rowHeight, height: rowHeight, gridTemplateColumns: cols }}
                draggable={!mobile}
                onDragStart={e => {
                  const rows = chosen(pos)
                  if (!sel.has(pos)) { setSel(new Set([pos])); setAnchor(pos) }
                  dragging.current = onReorder ? rows : null
                  e.dataTransfer.effectAllowed = onReorder ? 'copyMove' : 'copy'
                  e.dataTransfer.setData(DRAG_TYPE, JSON.stringify(rows.map(p => tracks[p].id)))
                  e.dataTransfer.setData('text/plain', rows.map(p => tracks[p].title).join('\n'))
                }}
                onDragOver={() => { if (onReorder && dragging.current) setDropAt(pos) }}
                onDragEnd={() => { dragging.current = null; setDropAt(null) }}
                onClick={e => { click(e, i, pos); if (mobile) play(x) }} onDoubleClick={() => play(x)} onContextMenu={e => menu(e, pos)}>
                <div className={s.lead}>
                  <span className={off || mobile ? undefined : 'dm-lead'}>{lead}</span>
                  {!mobile && !off && <Button className="dm-play" size="small" appearance="transparent" icon={<Play16Filled />} aria-label={t('Abspielen')}
                    onClick={e => { e.stopPropagation(); play(x) }} />}
                </div>
                {mobile ? <>
                  <div className={s.two}>
                    <span className={s.cell}>{x.title}</span>
                    <Text size={200} className={mergeClasses(s.cell, s.muted)}>{[x.artist || x.albumArtist, x.album].filter(Boolean).join(' · ')}</Text>
                  </div>
                  <span className={s.num}>{fmtDuration(x.duration)}</span>
                </> : <>
                  <span className={s.cell} title={x.title}>{x.title}</span>
                  <span className={s.cell} title={x.artist || x.albumArtist || undefined}>
                    {(x.artist || x.albumArtist) && <Link label={(x.artist || x.albumArtist)!} onGo={() => goto('artists', x.artist || x.albumArtist)} />}
                  </span>
                  {!numbered && <span className={s.cell} title={x.album ?? undefined}>{x.album && <Link label={x.album} onGo={() => goto('albums', albumKey(x))} />}</span>}
                  {!numbered && <span className={s.num}>{x.year ?? ''}</span>}
                  {!numbered && <span className={s.cell} title={genresOf(x).join(', ') || undefined}>
                    {genresOf(x).map((g, n) => <Fragment key={n}>{n > 0 && ', '}<Link label={g} onGo={() => goto('genres', g)} /></Fragment>)}
                  </span>}
                  <span className={s.num} title={st?.last ? t('Zuletzt gehört: {when}', { when: new Date(st.last).toLocaleString() }) : undefined}>{st?.plays || ''}</span>
                  <span className={s.num}>{fmtDuration(x.duration)}</span>
                </>}
              </div>
            )
          })}
        </div>
      </div>
      {letters && <AlphaBar letters={letters} total={list.length} scroller={vl.ref} onJump={i => { if (vl.ref.current) vl.ref.current.scrollTop = i * rowHeight }} />}
      </div>
    </div>
  )
}

/** Kachel: Bild (Cover oder Mosaik), Name, Unterzeile; der Knopf spielt alle Titel der Kachel */
function Card({ covers, icon, name, sub, tracks, onOpen }: {
  covers: string[]; icon?: JSX.Element; name: string; sub?: string; tracks: Track[]; onOpen: () => void
}) {
  const s = useStyles()
  const { notify } = useApp()
  return (
    <div className={s.card} onClick={onOpen} title={name}>
      <div className={s.cardBox}>
        <Mosaic covers={covers} className={s.cardCover} icon={icon} />
        <Button className={mergeClasses(s.cardPlay, 'dm-play')} appearance="primary" shape="circular" icon={<Play20Filled />} aria-label={t('Abspielen')}
          onClick={e => { e.stopPropagation(); playFrom(tracks, null, notify) }} />
      </div>
      <Text weight="semibold" className={s.cell}>{name}</Text>
      <Text size={200} className={mergeClasses(s.cell, s.muted)}>{sub}</Text>
    </div>
  )
}

function AlbumCard({ a, onOpen }: { a: Album; onOpen: () => void }) {
  return <Card covers={a.cover ? [a.cover] : []} name={a.name} sub={[a.artist, a.year].filter(Boolean).join(' · ')} tracks={a.tracks} onOpen={onOpen} />
}

/** Kachel eines Titels: Klick spielt die Liste ab diesem Titel, der Interpret springt zum Interpreten */
function TrackCard({ x, list, onMenu }: { x: Track; list: Track[]; onMenu: (c: Ctx) => void }) {
  const s = useStyles()
  const { notify } = useApp()
  const [current] = useCurrent()
  const artist = x.artist || x.albumArtist
  return (
    <div className={mergeClasses(s.card, !playable(x) && s.off)} title={x.title} onClick={() => playFrom(list, x, notify)}
      onContextMenu={e => { e.preventDefault(); onMenu({ x: e.clientX, y: e.clientY, tracks: [x] }) }}>
      <Cover id={x.cover} className={s.cardCover} />
      <Text weight="semibold" className={mergeClasses(s.cell, x.id === current && s.playingTitle)}>{x.title}</Text>
      <Text size={200} className={mergeClasses(s.cell, s.muted)}>{artist && <Link label={artist} onGo={() => goto('artists', artist)} />}</Text>
    </div>
  )
}

/**
 * Kacheln, zeilenweise virtuell: Spalten aus der Breite, nur die sichtbaren Reihen im DOM. letterOf: Name für die A-Z-Leiste
 * (in der Reihenfolge der Liste).
 */
function TileGrid<T>({ items, keyOf, render, letterOf }: {
  items: T[]; keyOf: (x: T) => string; render: (x: T) => JSX.Element; letterOf?: (x: T) => string
}) {
  const mobile = useIsMobile()
  const s = useStyles()
  const { ref, box, onScroll } = useScrollBox()
  const names = useMemo(() => letterOf ? items.map(letterOf) : null, [items]) // eslint-disable-line react-hooks/exhaustive-deps
  const letters = useLetters(names)
  const padL = mobile ? 12 : 20, padR = padL + (letters ? BAR_WIDTH : 0), padTop = 8
  const gap = mobile ? 10 : 16, rowGap = mobile ? 14 : 18, minW = mobile ? 130 : 150
  const inner = Math.max(minW, box.width - padL - padR)
  const cols = Math.max(1, Math.floor((inner + gap) / (minW + gap)))
  const colW = (inner - gap * (cols - 1)) / cols
  // Cover quadratisch, darunter zwei Zeilen Text
  const cardH = colW + 48, rowH = cardH + rowGap
  const rows = Math.ceil(items.length / cols)
  const first = Math.max(0, Math.floor((box.top - padTop) / rowH) - 2)
  const last = Math.min(rows, Math.ceil((box.top + box.height) / rowH) + 2)
  const out: JSX.Element[] = []
  for (let r = first; r < last; r++) {
    out.push(
      <div key={r} style={{ position: 'absolute', top: padTop + r * rowH, left: padL, right: padR, height: cardH, display: 'grid',
        gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, columnGap: gap }}>
        {items.slice(r * cols, r * cols + cols).map(x => <Fragment key={keyOf(x)}>{render(x)}</Fragment>)}
      </div>,
    )
  }
  return (
    <div className={s.pane}>
      <div ref={ref} className={s.scroll} style={letters ? { scrollbarWidth: 'none' } : undefined} onScroll={onScroll}>
        <div style={{ height: padTop + rows * rowH + 12, position: 'relative' }}>{out}</div>
      </div>
      {letters && <AlphaBar letters={letters} total={items.length} scroller={ref}
        onJump={i => { if (ref.current) ref.current.scrollTop = padTop + Math.floor(i / cols) * rowH }} />}
    </div>
  )
}

function Hero({ cover, icon, kind, title, sub, tracks, onSub, extra }: {
  cover: string | null; icon?: JSX.Element; kind: string; title: string; sub?: string; tracks: Track[]; onSub?: () => void
  /** Weitere Knöpfe (Zur Playlist, Umbenennen …) */
  extra?: React.ReactNode
}) {
  const s = useStyles()
  const { notify } = useApp()
  const total = tracks.reduce((n, x) => n + (x.duration ?? 0), 0)
  return (
    <div className={s.hero}>
      <Cover id={cover} className={s.heroCover} icon={icon} />
      <div className={s.heroText}>
        <Text size={200} className={s.muted}>{kind}</Text>
        <span className={s.heroTitle}>{title}</span>
        {sub && <Text weight="semibold" className={onSub ? s.link : undefined} onClick={onSub}>{sub}</Text>}
        <Text size={200} className={s.muted}>{t('{n} Titel', { n: tracks.length.toLocaleString() })}{total ? ' · ' + fmtDuration(total) : ''}</Text>
        <div className={s.actions}>
          <Button appearance="primary" icon={<Play20Filled />} onClick={() => playFrom(tracks, null, notify)}>{t('Abspielen')}</Button>
          <Button icon={<ArrowShuffle20Regular />} onClick={() => { const ok = tracks.filter(playable); if (ok.length) player.shuffleAll(ok) }}>{t('Zufällig')}</Button>
          <Button icon={<TextBulletListLtr20Regular />} onClick={() => { player.enqueue(tracks.filter(playable)); notify(t('Zur Warteschlange hinzugefügt')) }}>{t('Zur Warteschlange')}</Button>
          {extra}
        </div>
      </div>
    </div>
  )
}

/** Liste der Alben, Interpreten oder Genres: virtuell, mit A-Z-Leiste nach letterOf (in der Reihenfolge der Liste) */
function RowList<T>({ items, keyOf, render, onOpen, letterOf, rowHeight }: {
  items: T[]; keyOf: (x: T) => string; render: (x: T) => JSX.Element; onOpen: (x: T) => void; letterOf: (x: T) => string; rowHeight: number
}) {
  const s = useStyles()
  const vl = useVirtual(items.length, rowHeight)
  const names = useMemo(() => items.map(letterOf), [items]) // eslint-disable-line react-hooks/exhaustive-deps
  const letters = useLetters(names)
  return (
    <div className={s.pane}>
      <div ref={vl.ref} className={s.scroll} style={barSpace(!!letters)} onScroll={vl.onScroll}>
        <div style={{ height: vl.total, position: 'relative' }}>
          {items.slice(vl.first, vl.last).map((x, k) => (
            <div key={keyOf(x)} className={s.groupRow} style={{ top: (vl.first + k) * rowHeight, height: rowHeight }} onClick={() => onOpen(x)}>{render(x)}</div>
          ))}
        </div>
      </div>
      {letters && <AlphaBar letters={letters} total={items.length} scroller={vl.ref} onJump={i => { if (vl.ref.current) vl.ref.current.scrollTop = i * rowHeight }} />}
    </div>
  )
}

/** Unterzeile eines Interpreten oder Genres: Alben (nur Interpreten) und Titel */
const groupSub = (g: Group, albums: boolean) => albums
  ? t('{a} Alben · {n} Titel', { a: g.albums.length.toLocaleString(), n: g.tracks.length.toLocaleString() })
  : t('{n} Titel', { n: g.tracks.length.toLocaleString() })

/** Einträge „Zur Playlist hinzufügen“: eigene Playlists und „Neue Playlist …“ */
function PlaylistTargets({ lists, onPick }: { lists: PlaylistInfo[]; onPick: (id: string | null) => void }) {
  return <>
    <MenuItem icon={<Add20Regular />} onClick={() => onPick(null)}>{t('Neue Playlist …')}</MenuItem>
    {lists.length > 0 && <MenuDivider />}
    {lists.map(p => <MenuItem key={p.id} icon={<AppsListDetail20Regular />} onClick={() => onPick(p.id)}>{p.name}</MenuItem>)}
  </>
}

/** Knopf mit Menü „Zur Playlist“ für Album, Interpret, Genre */
function AddToPlaylistButton({ lists, onPick }: { lists: PlaylistInfo[]; onPick: (id: string | null) => void }) {
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement><Button icon={<TextBulletListAdd20Regular />}>{t('Zur Playlist')}</Button></MenuTrigger>
      <MenuPopover><MenuList><PlaylistTargets lists={lists} onPick={onPick} /></MenuList></MenuPopover>
    </Menu>
  )
}

const AUTO_ICONS: Record<string, JSX.Element> = {
  added: <Sparkle20Regular />, recent: <History20Regular />, most: <ArrowTrending20Regular />, rare: <Hourglass20Regular />, never: <EyeOff20Regular />,
}

/** Titel einer Playlist (eigene: Kennung, Auto-Playlist: „auto:Art“) - neu geladen, wenn sich Playlists oder Hörverlauf ändern. */
function usePlaylistData(key: string | null, lists: PlaylistInfo[], fail: (e: unknown) => void) {
  const [data, setData] = useState<{ key: string; name: string; ids: string[] } | null>(null)
  const stamp = lists.find(p => p.id === key)?.modified
  const load = useCallback(() => {
    if (!key) { setData(null); return }
    const auto = key.startsWith('auto:') ? key.slice(5) : null
    ;(auto ? playlistApi.auto(auto).then(r => ({ name: autoName(auto), tracks: r.tracks })) : playlistApi.get(key))
      .then(r => setData({ key, name: r.name, ids: r.tracks }))
      .catch(e => { setData(null); fail(e) })
  }, [key]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(load, [load, stamp])
  return data?.key === key ? data : null
}

function QueueView() {
  const s = useStyles()
  const mobile = useIsMobile()
  const p = usePlayer()
  const [drag, setDrag] = useState<number | null>(null)
  const [over, setOver] = useState<number | null>(null)
  const rowHeight = mobile ? 56 : 44
  const vl = useVirtual(p.queue.length, rowHeight)
  // Beim Öffnen zum laufenden Titel
  useEffect(() => { const el = vl.ref.current; if (el && p.index > 3) el.scrollTop = (p.index - 2) * rowHeight }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!p.queue.length) return <div className={s.empty}><TextBulletListLtr20Regular /><Text className={s.muted}>{t('Die Warteschlange ist leer.')}</Text></div>
  return (
    <div ref={vl.ref} className={s.scroll} onScroll={vl.onScroll}>
      <div style={{ height: vl.total }}>
        {p.queue.slice(vl.first, vl.last).map((x, k) => {
          const i = vl.first + k
          return (
            <div key={i + ':' + x.id} className={mergeClasses(s.groupRow, i === p.index && s.playingRow, over === i && drag !== null && s.dragOver)}
              style={{ top: i * rowHeight, height: rowHeight, opacity: i < p.index ? 0.6 : 1 }}
              draggable={!mobile} onDragStart={() => setDrag(i)} onDragOver={e => { if (drag !== null) { e.preventDefault(); setOver(i) } }}
              onDrop={e => { e.preventDefault(); if (drag !== null) player.move(drag, i); setDrag(null); setOver(null) }}
              onDragEnd={() => { setDrag(null); setOver(null) }}
              onClick={() => player.jump(i)}>
              <Cover id={x.cover} className={mergeClasses(s.thumb, mobile && s.thumbLarge)} />
              <div className={s.two} style={{ flex: 1 }}>
                <span className={s.cell}>{x.title}</span>
                <Text size={200} className={mergeClasses(s.cell, s.muted)}>{[x.artist, x.album].filter(Boolean).join(' · ')}</Text>
              </div>
              <span className={s.num}>{fmtDuration(x.duration)}</span>
              <Button appearance="subtle" size="small" icon={<Dismiss20Regular />} aria-label={t('Aus der Warteschlange nehmen')} title={t('Aus der Warteschlange nehmen')}
                onClick={e => { e.stopPropagation(); player.remove(i) }} />
            </div>
          )
        })}
      </div>
    </div>
  )
}

/** Audiothek: Musik aus den Audiothek-Ordnern - Titel, Alben, Interpreten, Genres, Warteschlange. */
export default function AudioView() {
  const s = useStyles()
  const nv = useNavStyles()
  const { fail, notify } = useApp()
  const tracks = useTracks(fail)
  const stats = useStats()
  const reach = useReach()
  const queueLength = usePlayer().queue.length
  const [{ view, key }, setRoute] = useState(route)
  const [query, setQuery] = useState(() => { try { return sessionStorage.getItem('dmoffice.audio.q') ?? '' } catch { return '' } })
  const [folders, setFolders] = useState(false)
  const [ctx, setCtx] = useState<Ctx | null>(null)
  const [sidePane, toggleSide, closeSide] = useSidePane('audio')
  const mobile = useIsMobile()
  const { playlists, auto } = usePlaylists()
  const [dropOn, setDropOn] = useState<string | null>(null)
  const plKey = view === 'playlist' ? key : null
  const plData = usePlaylistData(plKey, playlists, fail)
  const ownPlaylist = plKey && !plKey.startsWith('auto:') ? plKey : null
  // Liste oder Kacheln je Bereich (Vorgabe: Alben als Kacheln, sonst Liste)
  const { prefs, update } = usePrefs()
  const shapeOf = (v: View) => prefs.audioView?.[v] ?? (v === 'albums' ? 'tiles' : 'list')
  const setShape = (shape: 'list' | 'tiles') => update({ audioView: { ...prefs.audioView, [view]: shape } })
  useEffect(() => {
    const on = () => { if (!location.hash.startsWith('#/audio')) return; setRoute(route()); closeSide() }
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { try { sessionStorage.setItem('dmoffice.audio.q', query) } catch { /* privat */ } }, [query])

  const all = useMemo(() => [...(tracks ?? [])].sort(byLibrary), [tracks])
  const found = useMemo(() => search(all, query), [all, query])
  const albums = useMemo(() => albumsOf(found), [found])
  const artists = useMemo(() => artistsOf(found), [found])
  const genres = useMemo(() => genresList(found), [found])
  // Detailansichten suchen im ganzen Bestand (der Schlüssel kommt aus der Adresse)
  const album = view === 'albums' && key ? albumsOf(all).find(a => a.key === key) ?? null : null
  const artist = view === 'artists' && key ? artistsOf(all).find(g => norm(g.name) === norm(key)) ?? null : null
  const genre = view === 'genres' && key ? genresList(all).find(g => norm(g.name) === norm(key)) ?? null : null
  void reach   // Erreichbarkeit neu: Stoppschilder neu zeichnen

  const byId = useMemo(() => new Map(all.map(x => [x.id, x])), [all])
  const plTracks = useMemo(() => (plData?.ids ?? []).map(id => byId.get(id)).filter((x): x is Track => !!x), [plData, byId])

  /** Was „Alle abspielen“ gerade meint */
  const visible: Track[] = plKey ? plTracks : album ? album.tracks : artist ? [...artist.tracks].sort(byLibrary) : genre ? genre.tracks : found

  /** Titel zu einer Playlist; null: neue Playlist mit diesen Titeln (fragt den Namen) */
  const addTo = async (id: string | null, list: Track[]) => {
    try {
      const ids = list.map(x => x.id)
      if (id == null) {
        const name = await askText(t('Neue Playlist'), '', t('Name'), t('Anlegen'))
        if (!name?.trim()) return
        const nid = await playlistApi.create(name.trim(), ids)
        notify(t('Playlist „{name}“ angelegt', { name: name.trim() }))
        if (!ids.length) goto('playlist', nid)
        return
      }
      await playlistApi.add(id, ids)
      notify(t('Zu „{name}“ hinzugefügt', { name: playlists.find(p => p.id === id)?.name ?? '' }))
    } catch (e) { fail(e) }
  }
  /** Eigene Playlist: Titel an diesen Stellen herausnehmen */
  const removeRows = async (rows: number[]) => {
    if (!ownPlaylist || !plData) return
    const drop = new Set(rows.map(r => plTracks[r]))
    try { await playlistApi.setTracks(ownPlaylist, plTracks.filter(x => !drop.has(x)).map(x => x.id)) } catch (e) { fail(e) }
  }
  const reorder = async (order: number[]) => {
    if (!ownPlaylist) return
    try { await playlistApi.setTracks(ownPlaylist, order.map(i => plTracks[i].id)) } catch (e) { fail(e) }
  }
  const renamePlaylist = async () => {
    if (!ownPlaylist || !plData) return
    const name = await askText(t('Playlist umbenennen'), plData.name, t('Name'), t('Umbenennen'))
    if (name?.trim()) playlistApi.rename(ownPlaylist, name.trim()).catch(fail)
  }
  const deletePlaylist = async () => {
    if (!ownPlaylist || !plData) return
    if (!await askConfirm(t('Playlist löschen'), t('„{name}“ löschen? Die Musikdateien bleiben.', { name: plData.name }), t('Löschen'))) return
    try { await playlistApi.remove(ownPlaylist); goto('tracks') } catch (e) { fail(e) }
  }
  /** Gezogene Titel auf eine Playlist der Seitenleiste */
  const dropProps = (id: string) => ({
    onDragOver: (e: React.DragEvent) => { if (e.dataTransfer.types.includes(DRAG_TYPE)) { e.preventDefault(); e.dataTransfer.dropEffect = 'copy'; setDropOn(id) } },
    onDragLeave: () => setDropOn(x => x === id ? null : x),
    onDrop: (e: React.DragEvent) => {
      e.preventDefault(); setDropOn(null)
      try { const ids = JSON.parse(e.dataTransfer.getData(DRAG_TYPE)) as string[]; addTo(id, ids.map(i => byId.get(i)).filter((x): x is Track => !!x)) } catch { /* nichts Brauchbares */ }
    },
  })
  const openAlbum = (x: Track) => goto('albums', albumKey(x))

  const nav = (v: View, icon: JSX.Element, label: string, count?: number) => (
    <div className={mergeClasses(nv.row, view === v && nv.active)} onClick={() => goto(v)}>
      {icon}<span className={nv.name}>{label}</span><span className={nv.count}>{count ? count.toLocaleString() : ''}</span>
    </div>
  )
  const plNav = (k: string, icon: JSX.Element, label: string, count?: number, drop?: boolean) => (
    <div key={k} className={mergeClasses(nv.row, plKey === k && nv.active, dropOn === k && nv.drop)} onClick={() => goto('playlist', k)} {...(drop ? dropProps(k) : {})}>
      {icon}<span className={nv.name} title={label}>{label}</span><span className={nv.count}>{count ? count.toLocaleString() : ''}</span>
    </div>
  )

  const content = () => {
    if (!tracks) return <Spinner style={{ margin: 'auto' }} />
    if (!tracks.length) return (
      <div className={s.empty}>
        <MusicNote224Regular style={{ fontSize: 40 }} />
        <Text weight="semibold">{t('Noch keine Musik')}</Text>
        <Text className={s.muted}>{t('Lege fest, in welchen Ordnern deine Musik liegt. dmOffice liest Titel, Interpret, Album, Genre und Cover aus den Dateien.')}</Text>
        <Button appearance="primary" icon={<FolderOpen20Regular />} onClick={() => setFolders(true)}>{t('Audiothek-Ordner einrichten')}</Button>
      </div>
    )
    if (view === 'queue') return <QueueView />
    if (!key && query && !found.length) return <div className={s.empty}><Text className={s.muted}>{t('Keine Treffer.')}</Text></div>
    if (plKey) return !plData ? <Spinner style={{ margin: 'auto' }} /> : <>
      <Hero cover={plTracks.find(x => x.cover)?.cover ?? null} icon={<AppsListDetail20Regular style={{ fontSize: 48 }} />}
        kind={ownPlaylist ? t('Playlist') : t('Auto-Playlist')} title={plData.name} tracks={plTracks}
        extra={ownPlaylist ? <>
          <Button icon={<Rename20Regular />} onClick={renamePlaylist}>{t('Umbenennen')}</Button>
          <Button icon={<Delete20Regular />} onClick={deletePlaylist}>{t('Löschen')}</Button>
        </> : <AddToPlaylistButton lists={playlists} onPick={id => addTo(id, plTracks)} />} />
      {plTracks.length === 0
        ? <div className={s.empty}><Text className={s.muted}>{ownPlaylist ? t('Noch leer. Titel kommen über das Kontextmenü „Zur Playlist hinzufügen“ oder per Ziehen auf die Playlist links dazu.') : t('Noch keine passenden Titel.')}</Text></div>
        : <TrackTable tracks={plTracks} mode="playlist" stats={stats} onMenu={setCtx}
          onReorder={ownPlaylist ? reorder : undefined} onRemove={ownPlaylist ? removeRows : undefined} />}
    </>
    if (album) return <>
      <Hero cover={album.cover} kind={t('Album')} title={album.name} sub={[album.artist, album.year].filter(Boolean).join(' · ')} tracks={album.tracks}
        onSub={album.artist ? () => goto('artists', album.artist) : undefined} extra={<AddToPlaylistButton lists={playlists} onPick={id => addTo(id, album.tracks)} />} />
      <TrackTable tracks={album.tracks} mode="album" stats={stats} onMenu={setCtx} />
    </>
    if (artist) return <>
      <Hero cover={artist.albums.find(a => a.cover)?.cover ?? null} icon={<Person20Regular style={{ fontSize: 48 }} />} kind={t('Interpret')} title={artist.name} tracks={artist.tracks}
        extra={<AddToPlaylistButton lists={playlists} onPick={id => addTo(id, [...artist.tracks].sort(byLibrary))} />} />
      {artist.albums.length > 0 && <div className={s.strip}>{artist.albums.map(a => <AlbumCard key={a.key} a={a} onOpen={() => goto('albums', a.key)} />)}</div>}
      <TrackTable tracks={[...artist.tracks].sort(byLibrary)} stats={stats} onMenu={setCtx} />
    </>
    if (genre) return <>
      <Hero cover={genre.tracks.find(x => x.cover)?.cover ?? null} icon={<Tag20Regular style={{ fontSize: 48 }} />} kind={t('Genre')} title={genre.name} tracks={genre.tracks}
        extra={<AddToPlaylistButton lists={playlists} onPick={id => addTo(id, genre.tracks)} />} />
      <TrackTable tracks={genre.tracks} stats={stats} onMenu={setCtx} />
    </>
    const tiles = shapeOf(view) === 'tiles'
    const big = { style: { fontSize: 40 } }
    if (view === 'albums') return tiles
      ? <TileGrid items={albums} keyOf={a => a.key} letterOf={a => a.artist} render={a => <AlbumCard a={a} onOpen={() => goto('albums', a.key)} />} />
      : <RowList items={albums} keyOf={a => a.key} letterOf={a => a.artist} rowHeight={mobile ? 56 : 48} onOpen={a => goto('albums', a.key)} render={a => <>
        <Cover id={a.cover} className={mergeClasses(s.thumb, mobile && s.thumbLarge)} />
        <div className={s.two} style={{ flex: 1 }}>
          <span className={s.cell}>{a.name}</span>
          <Text size={200} className={mergeClasses(s.cell, s.muted)}>{[a.artist, a.year].filter(Boolean).join(' · ')}</Text>
        </div>
        <Text size={200} className={s.muted}>{t('{n} Titel', { n: a.tracks.length.toLocaleString() })}</Text>
      </>} />
    if (view === 'artists' || view === 'genres') {
      const isArtists = view === 'artists'
      const list = isArtists ? artists : genres
      const icon = (props?: { style: React.CSSProperties }) => isArtists ? <Person20Regular {...props} /> : <Tag20Regular {...props} />
      return tiles
        ? <TileGrid items={list} keyOf={g => g.name} letterOf={g => g.name} render={g =>
          <Card covers={coversOf(g.tracks)} icon={icon(big)} name={g.name} sub={groupSub(g, isArtists)} tracks={g.tracks} onOpen={() => goto(view, g.name)} />} />
        : <RowList items={list} keyOf={g => g.name} letterOf={g => g.name} rowHeight={mobile ? 52 : 40} onOpen={g => goto(view, g.name)} render={g => <>
          {icon()}
          <span className={s.cell} style={{ flex: 1 }}>{g.name}</span>
          <Text size={200} className={s.muted}>{groupSub(g, isArtists)}</Text>
        </>} />
    }
    return tiles
      ? <TileGrid items={found} keyOf={x => x.id} letterOf={artistOf} render={x => <TrackCard x={x} list={found} onMenu={setCtx} />} />
      : <TrackTable tracks={found} stats={stats} onMenu={setCtx} alpha />
  }

  const heading = view === 'queue' ? t('Warteschlange') : view === 'albums' ? t('Alben') : view === 'artists' ? t('Interpreten') : view === 'genres' ? t('Genres') : t('Titel')
  const listView = !key && view !== 'playlist'
  // Liste oder Kacheln gibt es in den vier Bereichen der Bibliothek
  const shapeable = listView && view !== 'queue'
  const count = view === 'queue' ? queueLength : view === 'albums' ? albums.length : view === 'artists' ? artists.length : view === 'genres' ? genres.length : found.length
  return (
    <div className={s.outer}>
      <RibbonHeader area="audio" onMenu={toggleSide}
        start={<>
          <RibbonButton primary icon={<Play20Filled />} label={t('Alle abspielen')} disabled={!visible.length || view === 'queue'} onClick={() => playFrom(visible, null, notify)} />
          <RibbonButton icon={<ArrowShuffle20Regular />} label={t('Zufällig abspielen')} disabled={!visible.length || view === 'queue'}
            onClick={() => { const ok = visible.filter(playable); if (ok.length) player.shuffleAll(ok) }} />
          <RibbonDivider />
          <RibbonButton icon={<TextBulletListLtr20Regular />} label={t('Warteschlange')} pressed={view === 'queue'} onClick={() => goto('queue')} />
          {view === 'queue' && <RibbonButton icon={<Save20Regular />} label={t('Als Playlist speichern')} disabled={!queueLength}
            onClick={() => addTo(null, player.queue().map(q => byId.get(q.id)).filter((x): x is Track => !!x))} />}
          {view === 'queue' && <RibbonButton icon={<Delete20Regular />} label={t('Warteschlange leeren')} disabled={!queueLength} onClick={() => player.clear()} />}
          <RibbonButton icon={<Add20Regular />} label={t('Neue Playlist')} onClick={() => addTo(null, [])} />
          <RibbonDivider />
          <RibbonButton icon={<FolderOpen20Regular />} label={t('Audiothek-Ordner')} onClick={() => setFolders(true)} />
        </>}
        view={<>
          <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />
          <RibbonDivider />
          <RibbonButton icon={<AppsList20Regular />} label={t('Liste')} disabled={!shapeable} pressed={shapeable && shapeOf(view) === 'list'} onClick={() => setShape('list')} />
          <RibbonButton icon={<Grid20Regular />} label={t('Kacheln')} disabled={!shapeable} pressed={shapeable && shapeOf(view) === 'tiles'} onClick={() => setShape('tiles')} />
        </>} />
      <div className={s.root}>
        <SidePane as="aside" className={mergeClasses(s.side, nv.pane)} open={sidePane} onClose={closeSide} width={{ key: 'audio.side', def: 220 }}>
          <div className={mergeClasses(nv.section, nv.first)}>{t('Bibliothek')}</div>
          {nav('tracks', <MusicNote120Regular />, t('Titel'), all.length)}
          {nav('albums', <Album20Regular />, t('Alben'))}
          {nav('artists', <Person20Regular />, t('Interpreten'))}
          {nav('genres', <Tag20Regular />, t('Genres'))}
          <div className={nv.section}>{t('Wiedergabe')}</div>
          {nav('queue', <TextBulletListSquare20Regular />, t('Warteschlange'), queueLength)}
          <div className={nv.section}><span style={{ flex: 1 }}>{t('Playlists')}</span>
            <Button appearance="subtle" size="small" icon={<Add20Regular />} aria-label={t('Neue Playlist')} title={t('Neue Playlist')} onClick={() => addTo(null, [])} />
          </div>
          {auto.map(k => plNav('auto:' + k, AUTO_ICONS[k] ?? <AppsListDetail20Regular />, autoName(k)))}
          {playlists.map(p => plNav(p.id, <AppsListDetail20Regular />, p.name, p.count, true))}
        </SidePane>
        <div className={s.main}>
          {(listView || mobile) && tracks && tracks.length > 0 && <div className={s.head}>
            {listView && <span className={s.title}>{heading}{count ? <Text className={s.muted} style={{ marginLeft: 8, fontWeight: 400 }}>{count.toLocaleString()}</Text> : null}</span>}
            {view !== 'queue' && <Input className={s.search} contentBefore={<Search20Regular />} placeholder={t('Titel, Interpret, Album, Genre suchen')} value={query}
              onChange={(_, d) => { setQuery(d.value); if (key) goto(view) }}
              contentAfter={query ? <Button appearance="transparent" size="small" icon={<Dismiss20Regular />} aria-label={t('Suche leeren')} onClick={() => setQuery('')} /> : undefined} />}
          </div>}
          {content()}
        </div>
      </div>
      {ctx && (
        <Menu open onOpenChange={(_, d) => !d.open && setCtx(null)}
          positioning={{ target: { getBoundingClientRect: () => ({ x: ctx.x, y: ctx.y, top: ctx.y, left: ctx.x, bottom: ctx.y, right: ctx.x, width: 0, height: 0 }) } as any }}>
          <MenuPopover>
            <MenuList>
              <MenuItem icon={<Play20Filled />} disabled={!ctx.tracks.some(playable)} onClick={() => playFrom(ctx.tracks, null, notify)}>{t('Abspielen')}</MenuItem>
              <MenuItem icon={<TextGrammarArrowRight20Regular />} disabled={!ctx.tracks.some(playable)} onClick={() => player.playNext(ctx.tracks.filter(playable))}>{t('Als Nächstes abspielen')}</MenuItem>
              <MenuItem icon={<TextBulletListLtr20Regular />} disabled={!ctx.tracks.some(playable)}
                onClick={() => { player.enqueue(ctx.tracks.filter(playable)); notify(t('Zur Warteschlange hinzugefügt')) }}>{t('Zur Warteschlange hinzufügen')}</MenuItem>
              <Menu>
                <MenuTrigger disableButtonEnhancement><MenuItem icon={<TextBulletListAdd20Regular />}>{t('Zur Playlist hinzufügen')}</MenuItem></MenuTrigger>
                <MenuPopover><MenuList><PlaylistTargets lists={playlists} onPick={id => { const list = ctx.tracks; setCtx(null); addTo(id, list) }} /></MenuList></MenuPopover>
              </Menu>
              {ownPlaylist && ctx.rows && <MenuItem icon={<Dismiss20Regular />} onClick={() => removeRows(ctx.rows!)}>{t('Aus der Playlist entfernen')}</MenuItem>}
              <MenuDivider />
              {ctx.tracks.length === 1 && <MenuItem icon={<Album20Regular />} onClick={() => openAlbum(ctx.tracks[0])}>{t('Album öffnen')}</MenuItem>}
              {ctx.tracks.length === 1 && artistOf(ctx.tracks[0]) && <MenuItem icon={<Person20Regular />} onClick={() => goto('artists', ctx.tracks[0].artist || artistOf(ctx.tracks[0]))}>{t('Interpret öffnen')}</MenuItem>}
            </MenuList>
          </MenuPopover>
        </Menu>
      )}
      {folders && <AudioFoldersDialog onClose={changed => { setFolders(false); if (changed) refreshTracks().catch(fail) }} />}
    </div>
  )
}
