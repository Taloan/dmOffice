import {
  Avatar, Button, CounterBadge, Input, Menu, MenuDivider, MenuGroup, MenuGroupHeader, MenuItem, MenuList, MenuPopover, MenuTrigger, Spinner, Text, Textarea,
  Tooltip, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowDownload20Regular, ArrowReply20Regular, Attach20Regular, Checkmark12Regular, CheckmarkCircle12Filled, CheckmarkCircle12Regular, Chat48Regular, Clock12Regular, ChatAdd20Regular, PanelLeft20Regular, PanelRight20Regular, Settings20Regular, Filter20Regular,
  Copy20Regular, Delete20Regular, Dismiss20Regular, Edit20Regular, Emoji20Regular, Folder20Regular, ImageMultiple20Regular, MoreHorizontal20Regular,
  Note20Regular, People20Regular, Search20Regular, Send20Filled, Speaker220Regular, SpeakerMute20Regular, ArrowExit20Regular,
  Rename20Regular, DocumentArrowUp20Regular, Save20Regular, Eye16Regular, ArrowForward16Regular, ArrowForward20Regular,
  CalendarLtr20Regular, TaskListSquareLtr20Regular, Person20Regular, Mic20Regular, Location20Regular, Open20Regular, CheckmarkCircle20Regular,
} from '@fluentui/react-icons'
import { Fragment, lazy, Suspense, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { t, tError, locale } from '../i18n'
import { fmtDate, fmtDay, fmtTime } from '../shell/hooks'
import { askConfirm, askText, ColorMenu, ContextMenu, useContextMenu } from '../shell/context'
import { BackBar, useIsMobile, useMobileStyles } from '../shell/mobile'
import { KindIcon } from '../files/common'
import { gallerySource, isExt } from '../files/GalleryFolders'
import { PickPhotoDialog } from '../files/PickPhotoDialog'
import { ChatMessage, ChatMode, ChatRef, ChatRoom, chatRefItem, chatRefSource, chatStatusChanged, fromDevice, isPukki, PUKKI, refUrl, systemText, uploadToRoom, useChat } from './store'
import { ThumbDislike16Filled, ThumbDislike16Regular, ThumbLike16Filled, ThumbLike16Regular } from '@fluentui/react-icons'
import { SpacesBar, SpacesMenu, useSpaces } from './Spaces'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { attach, cancel, offline, queue, useOutbox } from './outbox'
import { ChatInfo, useWide } from './ChatInfo'
import { pukkiPose, RoomAvatar, UserAvatar, usePukkiFigure, usePukkiImage } from './UserAvatar'
import { AlbumRefDialog, CollectionPickDialog, FilePickerDialog, FolderRefDialog, ForwardDialog, ItemDialog,MembersDialog, NewChatDialog, NotePickerDialog, NotePreviewDialog, SaveRefDialog } from './Dialogs'
import { usePaneWidth } from '../shell/Splitter'
import { copyText } from '../shell/browser'

const QUICK = ['👍', '❤️', '😂', '😮', '😢', '🙏']

const useStyles = makeStyles({
  // Menüband oben über die ganze Breite (wie in den anderen Bereichen), darunter Liste und Unterhaltung
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0, minHeight: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0, position: 'relative' },
  list: {
    width: '320px', flexShrink: 0, display: 'flex', flexDirection: 'column', borderRight: `1px solid ${tokens.colorNeutralStroke2}`, minHeight: 0,
  },
  listMobile: { width: 'auto', flex: 1, minWidth: 0, borderRight: 'none' },
  listHead: { display: 'flex', gap: '6px', padding: '8px', alignItems: 'center' },
  rooms: { flex: 1, overflowY: 'auto', padding: '0 4px 8px' },
  room: {
    display: 'flex', gap: '10px', alignItems: 'center', padding: '8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  roomActive: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  roomText: { flex: 1, minWidth: 0 },
  roomTop: { display: 'flex', alignItems: 'baseline', gap: '6px' },
  roomTitle: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', fontWeight: 600 },
  roomTime: { fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3, flexShrink: 0 },
  roomLast: { display: 'flex', gap: '6px', alignItems: 'center', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  roomLastText: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  hitHead: { padding: '10px 12px 4px', fontSize: '12px', fontWeight: 600, color: tokens.colorNeutralForeground3 },
  empty: { margin: 'auto', textAlign: 'center', color: tokens.colorNeutralForeground3, padding: '24px', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px' },

  conv: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', minHeight: 0 },
  // Verlauf links, Infobereich rechts; Verlauf und Eingabe in einer Spalte fester Breite in der Mitte
  convBody: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0 },
  convMain: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0, minHeight: 0 },
  head: { display: 'flex', alignItems: 'center', gap: '10px', padding: '8px 12px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, minHeight: '48px' },
  headText: { flex: 1, minWidth: 0 },
  headSub: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  scroll: { flex: 1, overflowY: 'auto', padding: '12px max(16px, calc((100% - 820px) / 2))', display: 'flex', flexDirection: 'column', gap: '2px', backgroundColor: tokens.colorNeutralBackground2 },
  day: { alignSelf: 'center', margin: '12px 0 6px', padding: '2px 10px', borderRadius: '10px', fontSize: tokens.fontSizeBase200, backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground3 },
  system: { alignSelf: 'center', margin: '6px 0', fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, textAlign: 'center' },
  line: { display: 'flex', alignItems: 'flex-end', gap: '6px', position: 'relative', ':hover .dm-actions': { visibility: 'visible' } },
  lineOwn: { flexDirection: 'row-reverse' },
  bubble: {
    maxWidth: 'min(75%, 640px)', padding: '6px 10px', borderRadius: '12px', backgroundColor: tokens.colorNeutralBackground1,
    boxShadow: tokens.shadow2, overflowWrap: 'anywhere', whiteSpace: 'pre-wrap', minWidth: '60px',
  },
  bubbleOwn: { backgroundColor: tokens.colorBrandBackground2 },
  // Daumen neben Pukkis Antworten: ein Tipp statt Reaktionsmenue
  thumbs: { display: 'flex', flexDirection: 'column', gap: '2px', alignSelf: 'center', color: tokens.colorNeutralForeground3 },
  thumb: {
    border: 'none', background: 'none', padding: '3px', borderRadius: '50%', cursor: 'pointer', color: 'inherit', display: 'flex',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover, color: tokens.colorNeutralForeground1 },
  },
  thumbOn: { color: tokens.colorBrandForeground1 },
  thanks: { fontSize: tokens.fontSizeBase200, color: tokens.colorBrandForeground1, alignSelf: 'center' },
  // „Pukki denkt nach …“: drei Punkte, die nacheinander aufleuchten
  thinking: { display: 'inline-flex', alignItems: 'center', gap: '8px', color: tokens.colorNeutralForeground3, fontStyle: 'italic' },
  // Pukki in der Pose zur Nachricht, links neben der Blase (an der letzten Nachricht einer Folge)
  pose: { width: '56px', height: '56px', objectFit: 'contain', flexShrink: 0, alignSelf: 'flex-end', userSelect: 'none', pointerEvents: 'none' },
  poseSpace: { width: '56px', flexShrink: 0 },
  self: { display: 'flex', flexShrink: 0, alignSelf: 'flex-end' },
  selfSpace: { width: '32px', flexShrink: 0 },
  // Lange nichts gehört: Pukki schläft (Zzz) bzw. träumt
  sleep: { display: 'flex', alignItems: 'flex-end', gap: '10px', marginTop: '16px', color: tokens.colorNeutralForeground3, fontStyle: 'italic' },
  sleepPose: { position: 'relative', width: '72px', height: '72px', flexShrink: 0 },
  zzz: {
    position: 'absolute', right: '-6px', top: '-4px', fontWeight: 700, fontStyle: 'normal', color: tokens.colorBrandForeground1,
    animationName: { '0%': { opacity: 0, transform: 'translate(0, 6px) scale(0.8)' }, '30%': { opacity: 1 }, '100%': { opacity: 0, transform: 'translate(10px, -14px) scale(1.2)' } },
    animationDuration: '2.4s', animationIterationCount: 'infinite',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none', opacity: 0.8 },
  },
  dream: {
    position: 'absolute', right: '-30px', top: '-30px', width: '40px', height: '30px', borderRadius: '50%', display: 'flex', alignItems: 'center', justifyContent: 'center',
    backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, fontStyle: 'normal', fontSize: '16px',
  },
  dots: { display: 'inline-flex', gap: '3px' },
  dot: {
    width: '6px', height: '6px', borderRadius: '50%', backgroundColor: tokens.colorNeutralForeground3,
    animationName: { '0%, 80%, 100%': { opacity: 0.25, transform: 'translateY(0)' }, '40%': { opacity: 1, transform: 'translateY(-3px)' } },
    animationDuration: '1.2s', animationIterationCount: 'infinite',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none', opacity: 0.6 },
  },
  gap: { marginTop: '8px' },
  sender: { fontSize: tokens.fontSizeBase200, fontWeight: 600, color: tokens.colorBrandForeground1, marginBottom: '2px' },
  meta: { display: 'flex', justifyContent: 'flex-end', alignItems: 'center', gap: '4px', fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3, marginTop: '2px' },
  deleted: { fontStyle: 'italic', color: tokens.colorNeutralForeground3 },
  quote: {
    borderLeft: `3px solid ${tokens.colorBrandStroke1}`, padding: '2px 8px', marginBottom: '4px', borderRadius: '4px', cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground3, fontSize: tokens.fontSizeBase200, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis',
  },
  actions: { visibility: 'hidden', display: 'flex', alignSelf: 'center' },
  reactions: { display: 'flex', flexWrap: 'wrap', gap: '4px', marginTop: '4px' },
  reaction: {
    border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: '12px', padding: '0 6px', fontSize: '13px', cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1,
  },
  reactionMine: { border: `1px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  image: { display: 'block', maxWidth: '100%', maxHeight: '320px', borderRadius: '8px', cursor: 'pointer', margin: '2px 0' },
  card: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 8px', margin: '2px 0', borderRadius: '8px', whiteSpace: 'normal',
    backgroundColor: tokens.colorNeutralBackground3, minWidth: '220px',
  },
  cardText: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' },
  gone: { color: tokens.colorNeutralForeground3, fontStyle: 'italic', fontSize: tokens.fontSizeBase200 },
  composer: { display: 'flex', alignItems: 'flex-end', gap: '6px', padding: '8px max(8px, calc((100% - 836px) / 2))', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  bar: { display: 'flex', alignItems: 'center', gap: '8px', padding: '6px max(12px, calc((100% - 820px) / 2))', borderTop: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground2, fontSize: tokens.fontSizeBase200 },
  barText: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  linkHost: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  // Antwort aus der KI: steht über dem Text
  aiMark: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, marginBottom: '2px' },
  // Hinweis über dem Eingabefeld in der Unterhaltung mit Pukki
  aiNote: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, padding: '4px max(8px, calc((100% - 836px) / 2)) 0', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  staged: { display: 'flex', flexWrap: 'wrap', gap: '8px', padding: '8px max(12px, calc((100% - 820px) / 2)) 0', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  stagedItem: {
    position: 'relative', display: 'flex', alignItems: 'center', gap: '6px', maxWidth: '220px', padding: '4px 28px 4px 4px',
    borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground2, fontSize: tokens.fontSizeBase200,
  },
  stagedThumb: { width: '48px', height: '48px', objectFit: 'cover', borderRadius: tokens.borderRadiusSmall, flexShrink: 0 },
  stagedName: { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  stagedRemove: { position: 'absolute', top: '2px', right: '2px', minWidth: 0 },
  // Sprachnachricht
  recording: { flex: 1, display: 'flex', alignItems: 'center', gap: '8px', minHeight: '32px', paddingLeft: '8px' },
  recDot: {
    width: '10px', height: '10px', borderRadius: '50%', backgroundColor: tokens.colorPaletteRedBackground3, flexShrink: 0,
    animationName: { from: { opacity: 1 }, to: { opacity: 0.25 } }, animationDuration: '0.8s', animationIterationCount: 'infinite', animationDirection: 'alternate',
  },
  audio: { display: 'block', width: '260px', maxWidth: '100%', height: '40px' },
  pukkiActions: { display: 'flex', flexWrap: 'wrap', gap: '6px', marginTop: '6px' },
  // @Erwaehnungen
  mention: { color: tokens.colorBrandForeground1, fontWeight: tokens.fontWeightSemibold },
  mentionMe: { backgroundColor: tokens.colorBrandBackground2, borderRadius: '4px', padding: '0 2px' },
  atBadge: {
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center', width: '18px', height: '18px', borderRadius: '50%', flexShrink: 0,
    backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand, fontSize: '12px', fontWeight: tokens.fontWeightBold,
  },
  suggest: { borderTop: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1, padding: '4px' },
  suggestItem: { display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 8px', borderRadius: '4px', cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  suggestActive: { backgroundColor: tokens.colorNeutralBackground1Selected },
  suggestLogin: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  drop: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-6px' },
  mode: { display: 'inline-flex', alignItems: 'center', gap: '4px', fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
  badgeRow: { display: 'flex', alignItems: 'center', gap: '6px', minHeight: '24px' },
})

/** Hintergrund des Verlaufs: leicht in der Farbe der Unterhaltung, ohne eigene Farbe in der Akzentfarbe. */
/** Laengste Sprachnachricht: 5 Minuten, dann wird sie von selbst geschickt. */
const MaxVoice = 5 * 60_000

/** Dauer als m:ss */
const fmtDuration = (ms: number) => { const s = Math.max(0, Math.floor(ms / 1000)); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}` }

const tint = (color?: string | null) => `color-mix(in srgb, ${color ?? tokens.colorBrandBackground} 7%, ${tokens.colorNeutralBackground2})`

const isImage = (r: ChatRef) => (r.mime ?? '').startsWith('image/') && r.mime !== 'image/svg+xml'
// Bildbetrachter der Galerie - erst geladen, wenn jemand ein Foto öffnet
const PhotoViewer = lazy(() => import('../files/Viewer').then(m => ({ default: m.Viewer })))
const snippet = (m: ChatMessage | null | undefined) =>
  !m ? '' : m.deleted ? t('Nachricht gelöscht') : m.text ?? (m.refs.length ? m.refs.map(r => r.type === 'location' ? '📍 ' + (r.liveUntil ? t('Live-Standort') : t('Standort')) : r.name).join(', ') : '')

// Standort-Anfrage: nur mit dem Recht von der Verwaltung (einmal je Seite geladen), nur zu zweit
let locationRightOnce: Promise<boolean> | null = null
function useLocationRight() {
  const [ok, setOk] = useState(false)
  useEffect(() => {
    locationRightOnce ??= api.get<{ rights: { locationRequests: boolean } }>('/api/chat/settings').then(r => r.rights.locationRequests).catch(() => false)
    locationRightOnce.then(setOk)
  }, [])
  return ok
}
const canAskLocation = (room: ChatRoom, right: boolean) => right && room.kind === 'direct' && !isPukki(room)
function askForLocation(room: ChatRoom, notify: (text: string) => void, run: (call: () => Promise<unknown>) => unknown) {
  if (!room.askLocation) { notify(t('{name} möchte nicht nach dem Standort gefragt werden. Erlauben kann {name} das unter Einstellungen → Chat → Standort.', { name: room.title })); return }
  run(() => api.post(`/api/chat/rooms/${room.id}/location-request`))
}

function currentRoom(): number | null {
  const parts = location.hash.replace(/^#\/?/, '').split('/')
  return parts[0] === 'chat' && parts[1] ? Number(parts[1]) || null : null
}

/** Nachricht, zu der gesprungen werden soll (#/chat/<id>/<nachricht>, aus der Suche). */
function currentFocus(): number | null {
  const parts = location.hash.replace(/^#\/?/, '').split('/')
  return parts[0] === 'chat' && parts[2] ? Number(parts[2]) || null : null
}

const LastRoom = 'dm.chat.last.'

/** Treffer der Suche im Chat */
interface SearchHit { room: number; roomTitle: string; message: ChatMessage }

export default function ChatView() {
  const s = useStyles()
  const listW = usePaneWidth('chat.list', 320, 220, 720)
  const ms = useMobileStyles()
  const mobile = useIsMobile()
  const { me } = useApp()
  const chat = useChat()
  const pukkiImage = usePukkiImage()
  const [selected, setSelected] = useState<number | null>(currentRoom())
  const [focus, setFocus] = useState<number | null>(currentFocus())
  const [filter, setFilter] = useState('')
  const [creating, setCreating] = useState(false)

  const restore = useRef(true)
  useEffect(() => {
    const onHash = (e: HashChangeEvent) => {
      if (!location.hash.startsWith('#/chat')) return
      // Aus einem anderen Bereich hierher: die zuletzt offene Unterhaltung darf wieder aufgehen
      if (!new URL(e.oldURL).hash.startsWith('#/chat')) restore.current = true
      setSelected(currentRoom()); setFocus(currentFocus())
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])
  // Die offene Unterhaltung steht in der Adresse (#/chat/<id>) - Zurueck im Browser bzw. am Handy geht damit von selbst.
  // Kein useBackClose: dessen eigener Verlaufseintrag holte beim Wechsel in einen anderen Bereich den Chat zurueck.
  const select = (id: number | null) => { location.hash = id ? `#/chat/${id}` : '#/chat' }
  // Handy, aus der Liste geoeffnet: Zurueck nimmt den Verlaufseintrag wieder heraus statt einen neuen anzulegen
  const fromList = useRef(false)
  const open = (id: number) => { fromList.current = mobile; select(id) }
  const back = () => { if (fromList.current) { fromList.current = false; history.back() } else select(null) }

  const rooms = chat.rooms ?? []
  const room = rooms.find(r => r.id === selected) ?? null
  // Am PC: zuletzt offene Unterhaltung merken und beim Wechsel auf den Chat wieder öffnen (am Handy beginnt der Chat mit der Liste)
  useEffect(() => { if (selected) try { localStorage.setItem(LastRoom + me.id, String(selected)) } catch { /* ohne Speicher */ } }, [selected, me.id])
  useEffect(() => {
    if (!chat.rooms || !restore.current) return
    restore.current = false
    if (mobile || selected) return
    let last: number | null = null
    try { last = Number(localStorage.getItem(LastRoom + me.id)) || null } catch { /* ohne Speicher */ }
    if (last && chat.rooms.some(r => r.id === last)) history.replaceState(null, '', `#/chat/${last}`), setSelected(last)
  }, [selected, chat.rooms, mobile, me.id])
  // Menüband, Ansicht: Liste ein-/ausblenden, nur Ungelesene
  const [listOn, toggleList] = useSidePane('chat')
  const [infoOn, toggleInfo] = useSidePane('chatinfo')
  const [unreadOnly, setUnreadOnly] = useState(false)
  const shown = rooms.filter(r => (!filter || r.title.toLowerCase().includes(filter.toLowerCase())) && (!unreadOnly || r.unread > 0 || r.id === selected))
  const { fail, notify } = useApp()
  const roomCall = async (call: () => Promise<unknown>) => { try { await call(); await chat.refresh(); chatStatusChanged() } catch (e) { fail(e) } }
  // Rechtsklick auf eine Unterhaltung: dieselben Befehle wie im Menüband, ohne sie erst zu öffnen
  const cm = useContextMenu<ChatRoom>()
  const leave = async (r: ChatRoom) => {
    if (!await askConfirm('', r.kind === 'group' ? t('Gruppe „{name}“ verlassen?', { name: r.title }) : t('Unterhaltung mit {name} ausblenden?', { name: r.title }))) return
    roomCall(() => api.post(`/api/chat/rooms/${r.id}/leave`)).then(() => { if (r.id === selected) select(null) })
  }
  const pukki = rooms.find(isPukki)
  const locationRight = useLocationRight()
  // Suche im Chat (Chat-Komfort): ab drei Zeichen auch in den Nachrichten
  const [hits, setHits] = useState<SearchHit[] | null>(null)
  useEffect(() => {
    if (filter.trim().length < 3) { setHits(null); return }
    const h = setTimeout(() => api.get<SearchHit[]>('/api/chat/search?' + q({ q: filter.trim() })).then(setHits).catch(() => setHits([])), 300)
    return () => clearTimeout(h)
  }, [filter])
  const openHit = (x: SearchHit) => { fromList.current = mobile; location.hash = `#/chat/${x.room}/${x.message.id}` }

  const list = (
    <div className={mergeClasses(s.list, mobile && s.listMobile)} style={listW.width ? { width: listW.width, position: 'relative' } : undefined}>
      {listW.handle('right', true)}
      <div className={s.listHead}>
        <Input style={{ flex: 1, minWidth: 0 }} contentBefore={<Search20Regular />} placeholder={t('Unterhaltungen durchsuchen')}
          value={filter} onChange={(_, d) => setFilter(d.value)} />
      </div>
      {cm.ctx && (() => {
        const r = cm.ctx.data
        const run = (f: () => void) => { cm.close(); f() }
        return (
          <ContextMenu at={cm.ctx.at} onClose={cm.close}>
            <MenuItem icon={<Open20Regular />} onClick={() => run(() => open(r.id))}>{t('Öffnen')}</MenuItem>
            {r.unread > 0 && r.last && <MenuItem icon={<CheckmarkCircle20Regular />}
              onClick={() => run(() => roomCall(() => api.post(`/api/chat/rooms/${r.id}/read`, { upTo: r.last!.id })))}>{t('Als gelesen markieren')}</MenuItem>}
            <MenuDivider />
            {r.kind === 'group' && r.admin && <MenuItem icon={<Rename20Regular />} onClick={() => run(async () => {
              const title = await askText(t('Gruppe umbenennen'), r.title)
              if (title) roomCall(() => api.put(`/api/chat/rooms/${r.id}`, { title }))
            })}>{t('Umbenennen')}</MenuItem>}
            <ColorMenu current={r.color} none onPick={color => run(() => roomCall(() => api.put(`/api/chat/rooms/${r.id}`, { color: color ?? '' })))} />
            <MenuItem icon={r.muted ? <Speaker220Regular /> : <SpeakerMute20Regular />}
              onClick={() => run(() => roomCall(() => api.put(`/api/chat/rooms/${r.id}`, { muted: !r.muted })))}>{r.muted ? t('Benachrichtigungen an') : t('Stummschalten')}</MenuItem>
            <MenuItem icon={<ArrowDownload20Regular />} onClick={() => run(() => { location.href = `/api/chat/rooms/${r.id}/export` })}>{t('Unterhaltung exportieren')}</MenuItem>
            <MenuDivider />
            <MenuItem icon={<ArrowExit20Regular />} onClick={() => run(() => leave(r))}>{r.kind === 'group' ? t('Gruppe verlassen') : t('Unterhaltung ausblenden')}</MenuItem>
          </ContextMenu>
        )
      })()}
      <div className={s.rooms}>
        {chat.rooms == null && <Spinner size="small" />}
        {chat.rooms != null && rooms.length === 0 && (
          <div className={s.empty}>
            <Chat48Regular />
            <Text>{t('Noch keine Unterhaltungen. Schreib jemandem auf diesem Server.')}</Text>
            <Button appearance="primary" icon={<Add20Regular />} onClick={() => setCreating(true)}>{t('Neue Unterhaltung')}</Button>
          </div>
        )}
        {shown.map(r => (
          <div key={r.id} className={mergeClasses(s.room, r.id === selected && s.roomActive)} onClick={() => open(r.id)} onContextMenu={e => cm.open(e, r)}
            style={{ boxShadow: `inset 3px 0 0 ${r.color ?? tokens.colorBrandStroke1}` }}>
            <RoomAvatar room={r} me={me.id} size={36} />
            <div className={s.roomText}>
              <div className={s.roomTop}>
                <span className={s.roomTitle}>{r.title}</span>
                {r.last && <span className={s.roomTime}>{fmtDate(r.last.created)}</span>}
              </div>
              <div className={s.roomLast}>
                {r.muted && <SpeakerMute20Regular style={{ width: 14, height: 14 }} />}
                <span className={s.roomLastText}>
                  {r.last?.kind === 'system' ? systemText(r.last.text, r.last.sender, nameIn(r, me.id), t)
                    : r.kind === 'group' && r.last && r.last.sender !== me.id ? `${r.last.senderName}: ${snippet(r.last)}` : snippet(r.last)}
                </span>
                {!!r.mentioned && <span className={s.atBadge} title={t('Du wurdest erwähnt')}>@</span>}
                {r.unread > 0 && <CounterBadge count={r.unread} size="small" color={r.muted ? 'informative' : 'brand'} />}
              </div>
            </div>
          </div>
        ))}
        {hits && (
          <>
            <div className={s.hitHead}>{hits.length ? t('Nachrichten') : t('Keine Nachrichten gefunden.')}</div>
            {hits.map(x => (
              <div key={x.message.id} className={s.room} onClick={() => openHit(x)}>
                <div className={s.roomText}>
                  <div className={s.roomTop}>
                    <span className={s.roomTitle}>{x.roomTitle}</span>
                    <span className={s.roomTime}>{fmtDate(x.message.created)}</span>
                  </div>
                  <div className={s.roomLast}><span className={s.roomLastText}>{x.message.senderName}: {snippet(x.message)}</span></div>
                </div>
              </div>
            ))}
          </>
        )}
      </div>
    </div>
  )

  // Menüband wie in Mail, Kalender & Co.: Start (Neu, die offene Unterhaltung, Pukki, Einstellungen) und Ansicht
  const ribbon = (
    <RibbonHeader area="chat" onMenu={mobile ? undefined : toggleList}
      start={<>
        <RibbonButton primary icon={<ChatAdd20Regular />} label={t('Neue Unterhaltung')} onClick={() => setCreating(true)} />
        <RibbonDivider />
        <RibbonButton icon={room?.muted ? <Speaker220Regular /> : <SpeakerMute20Regular />} label={room?.muted ? t('Benachrichtigungen an') : t('Stummschalten')} disabled={!room}
          onClick={() => room && roomCall(() => api.put(`/api/chat/rooms/${room.id}`, { muted: !room.muted }))} />
        {room && canAskLocation(room, locationRight) && <RibbonButton icon={<Location20Regular />} label={t('Standort anfragen')} onClick={() => room && askForLocation(room, notify, roomCall)} />}
        <RibbonButton icon={<ArrowDownload20Regular />} label={t('Exportieren')} disabled={!room} onClick={() => { if (room) location.href = `/api/chat/rooms/${room.id}/export` }} />
        <RibbonButton icon={<ArrowExit20Regular />} label={room?.kind === 'group' ? t('Gruppe verlassen') : t('Ausblenden')} disabled={!room}
          onClick={async () => {
            if (!room || !await askConfirm('', room.kind === 'group' ? t('Gruppe „{name}“ verlassen?', { name: room.title }) : t('Unterhaltung mit {name} ausblenden?', { name: room.title }))) return
            roomCall(() => api.post(`/api/chat/rooms/${room.id}/leave`)).then(() => select(null))
          }} />
        <RibbonDivider />
        {pukki && <RibbonButton icon={<img src={pukkiImage} alt="" width={24} height={24} style={{ borderRadius: '50%' }} />} label={t('Pukki')} onClick={() => open(pukki.id)} />}
        <RibbonButton icon={<Settings20Regular />} label={t('Einstellungen')} onClick={() => { location.hash = '#/settings/chat' }} />
      </>}
      view={<>
        <RibbonButton icon={<Filter20Regular />} label={t('Nur ungelesene')} pressed={unreadOnly} onClick={() => setUnreadOnly(u => !u)} />
        {!mobile && <><RibbonDivider /><RibbonButton icon={<PanelLeft20Regular />} label={t('Unterhaltungen')} pressed={listOn} onClick={toggleList} />
          <RibbonButton icon={<PanelRight20Regular />} label={t('Infobereich')} pressed={infoOn} onClick={toggleInfo} /></>}
      </>} />
  )

  return (
    <div className={s.outer}>
    {(!mobile || !room) && ribbon}
    <div className={s.root}>
      {(!mobile || !room) && (mobile || listOn || !room) && list}
      {room ? (
        <div className={mergeClasses(s.conv, mobile && ms.full)}>
          <Conversation key={room.id} room={room} chat={chat} onBack={back} focus={focus} />
        </div>
      ) : !mobile && (
        <div className={s.empty}><Chat48Regular /><Text>{t('Wähle links eine Unterhaltung.')}</Text></div>
      )}
      {creating && <NewChatDialog onClose={() => setCreating(false)} onCreated={r => { setCreating(false); chat.putRoom(r); open(r.id) }} />}
    </div>
    </div>
  )
}

/** Namen in einer Unterhaltung; "du" fuer einen selbst. */
const nameIn = (room: ChatRoom, me: string) => (id: string) =>
  id === me ? t('Du') : room.members.find(m => m.id === id)?.name ?? t('Jemand')

/** Ab dieser Pause zwischen zwei Nachrichten steht die Uhrzeit als Trenner im Verlauf. */
const PAUSE = 4 * 3600 * 1000

function Conversation({ room, chat, onBack, focus }: { room: ChatRoom; chat: ReturnType<typeof useChat>; onBack: () => void; focus?: number | null }) {
  const s = useStyles()
  const infoW = usePaneWidth('chat.info', 300, 240, 600)
  const mobile = useIsMobile()
  const { me, fail, notify } = useApp()
  const locationRight = useLocationRight()
  const list = chat.messages[room.id]
  const [text, setText] = useState('')
  const [reply, setReply] = useState<ChatMessage | null>(null)
  const [editing, setEditing] = useState<ChatMessage | null>(null)
  const [picker, setPicker] = useState<'file' | 'note' | 'gallery' | null>(null)
  const [members, setMembers] = useState(false)
  const [preview, setPreview] = useState<{ message: number; index: number } | null>(null)
  // Fotos im Bildbetrachter der Galerie statt in einem neuen Fenster - mehrere einer Nachricht (bzw. im Infobereich alle) zum Durchblättern
  const [photos, setPhotos] = useState<{ items: ReturnType<typeof chatRefItem>[]; index: number } | null>(null)
  const showPhotos = (list: { m: ChatMessage; r: ChatRef; i: number }[], at: { m: ChatMessage; i: number }) =>
    setPhotos({ items: list.map(x => chatRefItem(x.m, x.r, x.i)), index: Math.max(0, list.findIndex(x => x.m.id === at.m.id && x.i === at.i)) })
  const messagePhotos = (m: ChatMessage, ri: number) =>
    showPhotos(m.refs.map((r, i) => ({ m, r, i })).filter(x => x.r.type === 'file' && isImage(x.r) && x.r.state === 'ok'), { m, i: ri })
  const [saving, setSaving] = useState<{ message: ChatMessage; index: number } | null>(null)
  const [forwarding, setForwarding] = useState<ChatMessage | null>(null)
  const [uploading, setUploading] = useState<string | null>(null)
  // Angehängt, aber noch nicht gesendet (Einfügen, Ziehen, Auswählen): erst mit „Senden“ hochgeladen
  const [staged, setStaged] = useState<{ file: File; url: string | null }[]>([])
  const stage = (files: File[]) => {
    if (!files.length) return
    setStaged(list => [...list, ...files.map(file => ({ file, url: file.type.startsWith('image/') ? URL.createObjectURL(file) : null }))])
    input.current?.focus()
  }
  const unstage = (i: number) => setStaged(list => { if (list[i]?.url) URL.revokeObjectURL(list[i].url!); return list.filter((_, j) => j !== i) })
  const stagedRef = useRef(staged)
  stagedRef.current = staged
  useEffect(() => () => stagedRef.current.forEach(x => x.url && URL.revokeObjectURL(x.url)), [])
  const [dragging, setDragging] = useState(false)
  const scroller = useRef<HTMLDivElement>(null)
  const input = useRef<HTMLTextAreaElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)
  const atBottom = useRef(true)
  const names = nameIn(room, me.id)
  // @Erwaehnungen: Hervorheben und Vorschlaege beim Schreiben („@“ + Anfang des Namens)
  const { names: mention, people } = useMemo(() => mentionNames(room, me.id), [room, me.id])
  // Infobereich rechts (nur breit genug, nicht am Handy)
  const [infoOn] = useSidePane('chatinfo')
  const wide = useWide()
  // Postausgang (ohne Verbindung geschrieben): diese Unterhaltung; beim Öffnen gleich versuchen zu senden
  const outbox = useOutbox()
  const waiting = outbox.filter(o => o.user === me.id && o.room === room.id)
  useEffect(() => { attach(me.id, m => { chat.put(m); chatStatusChanged() }) }, [me.id]) // eslint-disable-line react-hooks/exhaustive-deps
  const [suggest, setSuggest] = useState<{ at: number; query: string; active: number } | null>(null)
  const suggestions = !suggest ? [] : [
    ...people.filter(p => p.id !== me.id),
    ...(room.kind === 'group' ? [{ id: '*', name: t('alle'), login: null }] : []),
  ].filter(p => [p.name, ...p.name.split(' '), p.login ?? ''].some(n => n.toLowerCase().startsWith(suggest.query.toLowerCase()))).slice(0, 6)
  const typed = (value: string) => {
    setText(value)
    const caret = input.current?.selectionStart ?? value.length
    const hit = /(^|\s)@([\p{L}\p{N}._-]*)$/u.exec(value.slice(0, caret))
    setSuggest(hit ? { at: caret - hit[2].length - 1, query: hit[2], active: 0 } : null)
  }
  const pick = (p: { name: string }) => {
    if (!suggest) return
    const end = suggest.at + 1 + suggest.query.length
    const value = text.slice(0, suggest.at) + '@' + p.name + ' ' + text.slice(end)
    setText(value); setSuggest(null)
    const caret = suggest.at + p.name.length + 2
    requestAnimationFrame(() => { input.current?.focus(); input.current?.setSelectionRange(caret, caret) })
  }

  useEffect(() => { chat.open(room.id) }, [room.id]) // eslint-disable-line react-hooks/exhaustive-deps
  // Cursor gleich ins Eingabefeld (nicht am Handy: dort käme sonst sofort die Tastatur); Angehängtes gehört zur Unterhaltung
  useEffect(() => {
    if (!mobile) requestAnimationFrame(() => input.current?.focus())
    setStaged(list => { list.forEach(x => x.url && URL.revokeObjectURL(x.url)); return [] })
  }, [room.id]) // eslint-disable-line react-hooks/exhaustive-deps
  // Zurück in den Chat (Reiter links): der Bereich bleibt geladen, darum hier den Cursor wieder ins Feld
  useEffect(() => {
    if (mobile) return
    const on = (e: HashChangeEvent) => {
      if (location.hash.startsWith('#/chat') && !new URL(e.oldURL).hash.startsWith('#/chat')) setTimeout(() => input.current?.focus(), 50)
    }
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [mobile])

  // Gelesen, sobald die neueste Nachricht zu sehen ist (Fenster im Vordergrund)
  const lastId = list?.length ? list[list.length - 1].id : 0
  useEffect(() => {
    const mark = () => { if (!document.hidden && lastId > room.lastRead) chat.markRead(room.id, lastId) }
    mark()
    document.addEventListener('visibilitychange', mark)
    return () => document.removeEventListener('visibilitychange', mark)
  }, [lastId, room.lastRead, room.id]) // eslint-disable-line react-hooks/exhaustive-deps

  // Neue Nachrichten: unten bleiben, wenn man unten war; aeltere oben nachgeladen: Position halten
  const prevHeight = useRef(0)
  const firstId = list?.[0]?.id
  useLayoutEffect(() => {
    const el = scroller.current
    if (!el) return
    if (prevHeight.current && el.scrollTop < 40 && !atBottom.current) el.scrollTop += el.scrollHeight - prevHeight.current
    else if (atBottom.current) el.scrollTop = el.scrollHeight
    prevHeight.current = el.scrollHeight
  }, [list, firstId])
  // „Pukki denkt nach“ erscheint: unten bleiben, damit man es auch sieht
  const thinking = chat.busy.includes(room.id)
  // Wohin Pukki weitergibt, was er nicht selbst beantworten kann
  const [pukkiAi, setPukkiAi] = useState<{ on: boolean; service: string | null } | null>(null)
  useEffect(() => {
    if (isPukki(room)) api.get<{ on: boolean; service: string | null }>('/api/chat/pukki/ai').then(setPukkiAi).catch(() => {})
  }, [room.id]) // eslint-disable-line react-hooks/exhaustive-deps
  const figure = usePukkiFigure()
  const self = room.members.find(x => x.id === me.id)
  /** Pose zu Pukkis Nachricht - nach 👍 freut er sich, nach 👎 entschuldigt er sich */
  const moodOf = (m: ChatMessage) => ratings[m.id] === 1 ? 'herz' : ratings[m.id] === -1 ? 'bitte' : m.mood || 'zeigen'
  // Bewertungen von Pukkis Antworten (1 / -1) und das kurze „Danke!“ nach einem 👍
  const [ratings, setRatings] = useState<Record<number, number>>({})
  const [thanked, setThanked] = useState<number | null>(null)
  useEffect(() => {
    if (isPukki(room)) api.get<Record<number, number>>('/api/chat/pukki/ratings').then(setRatings).catch(() => {})
  }, [room.id]) // eslint-disable-line react-hooks/exhaustive-deps
  /** Wie auf dem Server (Pukki.Rateable): Antworten direkt auf einen eigenen Satz, keine Rueckfragen, keine Auswahl, nicht der Abschluss einer Rueckfrage. */
  const rateable = (m: ChatMessage, prev?: ChatMessage) => isPukki(room) && m.sender === PUKKI && prev?.sender === me.id && m.kind === 'text' && !m.deleted && !!m.text?.trim()
    && !m.text.trimEnd().endsWith('?') && !m.text.startsWith('Danke! Das steht jetzt auf deiner Lernliste')
    && !m.actions?.some(a => a.key.startsWith('say') || a.key.startsWith('pick') || a.key.startsWith('fb-'))
  const rate = async (m: ChatMessage, v: number) => {
    const value = ratings[m.id] === v ? 0 : v
    setRatings(r => ({ ...r, [m.id]: value }))
    if (value === 1) { setThanked(m.id); setTimeout(() => setThanked(x => (x === m.id ? null : x)), 2500) } else setThanked(null)
    if (value === -1) atBottom.current = true
    try { await api.post('/api/chat/pukki/rate', { message: m.id, value }) } catch (e) { fail(e) }
  }
  useLayoutEffect(() => {
    const el = scroller.current
    if (el && thinking && atBottom.current) el.scrollTop = el.scrollHeight
  }, [thinking])

  const onScroll = () => {
    const el = scroller.current
    if (!el) return
    atBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < 60
    if (el.scrollTop < 40 && list?.length && !chat.complete[room.id]) {
      prevHeight.current = el.scrollHeight
      chat.older(room.id, list[0].id).catch(() => {})
    }
  }

  // Aus der Suche: zur Nachricht springen - aeltere Seiten nachladen, bis sie da ist; kurz hervorheben
  const [flash, setFlash] = useState<number | null>(null)
  const focused = useRef<number | null>(null)
  useEffect(() => {
    if (!focus || !list || focused.current === focus) return
    if (list.some(m => m.id === focus)) {
      focused.current = focus
      atBottom.current = false
      requestAnimationFrame(() => document.getElementById(`msg-${focus}`)?.scrollIntoView({ block: 'center' }))
      setFlash(focus)
      setTimeout(() => setFlash(null), 2500)
    } else if (!chat.complete[room.id] && list.length) chat.older(room.id, list[0].id).catch(() => {})
  }, [focus, list]) // eslint-disable-line react-hooks/exhaustive-deps

  // Bilder laden nach dem ersten Zeichnen und machen den Verlauf laenger - wer unten war, bleibt unten
  const stick = () => { const el = scroller.current; if (el && atBottom.current) el.scrollTop = el.scrollHeight }

  const byId = useMemo(() => new Map((list ?? []).map(m => [m.id, m])), [list])
  const others = room.members.filter(m => m.id !== me.id)
  // Pukki liest nicht im eigentlichen Sinn - zählt für Zustellung und Lesestand nicht mit
  const readers = others.filter(m => m.id !== PUKKI)
  const readByAll = (id: number) => readers.length > 0 && readers.every(m => m.lastRead >= id)
  const deliveredToAll = (id: number) => readers.length > 0 && readers.every(m => (m.delivered ?? 0) >= id)
  /** Eigene Nachricht ändern oder löschen: in Gruppen immer, zu zweit nur ungelesen (ohne Lesebestätigung: nicht zugestellt). */
  const changeable = (m: ChatMessage) => room.kind === 'group' || readers.every(o => (o.receipts ?? true) ? o.lastRead < m.id : (o.delivered ?? 0) < m.id)

  // Knopf unter Pukkis Antwort: der Server fuehrt aus, Pukkis Antwort kommt mit den Aenderungen
  const [acting, setActing] = useState<number | null>(null)
  const act = async (m: ChatMessage, key: string) => {
    if (key === 'loc-yes') return sendLocation(m)
    setActing(m.id)
    try { atBottom.current = true; await api.post(`/api/chat/messages/${m.id}/actions/${encodeURIComponent(key)}`) } catch (e) { fail(e) } finally { setActing(null) }
  }

  // Standort-Anfrage beantworten: Position einmal im Browser bestimmen und als Nachricht schicken
  const sendLocation = (m: ChatMessage) => {
    if (!navigator.geolocation) { fail(new Error(t('Dieser Browser kann den Standort nicht bestimmen.'))); return }
    setActing(m.id)
    navigator.geolocation.getCurrentPosition(async pos => {
      try {
        atBottom.current = true
        await api.post(`/api/chat/rooms/${room.id}/location`, { lat: pos.coords.latitude, lon: pos.coords.longitude, accuracy: pos.coords.accuracy, request: m.id })
      } catch (e) { fail(e) } finally { setActing(null) }
    }, err => {
      setActing(null)
      fail(new Error(err.code === err.PERMISSION_DENIED ? t('Der Browser darf deinen Standort nicht abfragen. Erlaube es in den Einstellungen der Website.') : t('Dein Standort ließ sich gerade nicht bestimmen.')))
    }, { enableHighAccuracy: true, timeout: 20000, maximumAge: 60000 })
  }

  const send = async () => {
    const value = text.trim()
    if (staged.length && !editing) {
      const files = staged
      setStaged([])
      setText('')
      for (let i = 0; i < files.length; i++) {
        const { file, url } = files[i]
        setUploading(file.name)
        try { atBottom.current = true; chat.put(await uploadToRoom(room.id, file, i === files.length - 1 && value ? value : undefined)) }
        catch (e) {
          // Nicht gesendet: zurück in die Liste, der Text auch
          fail(new Error(tError(e instanceof Error ? e.message : String(e))))
          setStaged(files.slice(i))
          setText(value)
          break
        }
        if (url) URL.revokeObjectURL(url)
      }
      setUploading(null)
      setReply(null)
      chatStatusChanged()
      input.current?.focus()
      return
    }
    if (!value) return
    try {
      if (editing) chat.put(await api.put<ChatMessage>(`/api/chat/messages/${editing.id}`, { text: value }))
      else chat.put(await api.post<ChatMessage>(`/api/chat/rooms/${room.id}/messages`, { ...fromDevice(), text: value, replyTo: reply?.id }))
      atBottom.current = true
      setText(''); setReply(null); setEditing(null)
      chatStatusChanged()
    } catch (e) {
      // Ohne Verbindung: neue Nachricht in den Postausgang - geht raus, sobald der Server antwortet
      if (!editing && offline(e)) { atBottom.current = true; queue(me.id, room.id, value, reply?.id); setText(''); setReply(null) }
      else fail(e)
    }
    input.current?.focus()
  }

  const upload = async (files: File[]) => {
    for (const f of files) {
      setUploading(f.name)
      try { atBottom.current = true; chat.put(await uploadToRoom(room.id, f)) }
      catch (e) { fail(new Error(tError(e instanceof Error ? e.message : String(e)))); break }
    }
    setUploading(null)
  }

  // Sprachnachricht (Chat-Komfort): aufnehmen, dann senden oder verwerfen - geht als Datei wie ein Foto vom Geraet
  const [recording, setRecording] = useState<{ since: number } | null>(null)
  const recorder = useRef<{ rec: MediaRecorder; chunks: Blob[]; stream: MediaStream; send: boolean } | null>(null)
  const [, tick] = useState(0)
  // Pukki versteht keine Sprachnachrichten (keine Spracherkennung auf dem Server) - dort kein Mikrofon; diktieren geht ueber die Tastatur
  const canRecord = typeof MediaRecorder !== 'undefined' && !!navigator.mediaDevices?.getUserMedia && !isPukki(room)
  useEffect(() => {
    if (!recording) return
    const h = setInterval(() => {
      tick(x => x + 1)
      if (Date.now() - recording.since > MaxVoice) stopRecording(true)
    }, 500)
    return () => clearInterval(h)
  }, [recording]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => () => { recorder.current?.stream.getTracks().forEach(x => x.stop()) }, [])
  const startRecording = async () => {
    let stream: MediaStream
    try { stream = await navigator.mediaDevices.getUserMedia({ audio: true }) } catch { fail(new Error(t('Kein Zugriff auf das Mikrofon.'))); return }
    const type = ['audio/webm;codecs=opus', 'audio/ogg;codecs=opus', 'audio/mp4'].find(x => MediaRecorder.isTypeSupported(x))
    const rec = new MediaRecorder(stream, type ? { mimeType: type } : undefined)
    const r = { rec, chunks: [] as Blob[], stream, send: false }
    rec.ondataavailable = e => { if (e.data.size) r.chunks.push(e.data) }
    rec.onstop = () => {
      stream.getTracks().forEach(x => x.stop())
      recorder.current = null
      setRecording(null)
      if (!r.send || !r.chunks.length) return
      const mime = (rec.mimeType || type || 'audio/webm').split(';')[0]
      const ext = mime.includes('mp4') ? 'm4a' : mime.includes('ogg') ? 'ogg' : 'webm'
      const p = (n: number) => String(n).padStart(2, '0')
      const d = new Date()
      const stamp = `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}.${p(d.getMinutes())}.${p(d.getSeconds())}`
      upload([new File(r.chunks, `${t('Sprachnachricht')} ${stamp}.${ext}`, { type: mime })])
    }
    recorder.current = r
    rec.start()
    setRecording({ since: Date.now() })
  }
  const stopRecording = (send: boolean) => {
    const r = recorder.current
    if (!r || r.rec.state === 'inactive') return
    r.send = send
    r.rec.stop()
  }

  const sendRefs = async (refs:{ type: 'file' | 'note'; fileId?: number; noteId?: number; mode?: ChatMode }[]) => {
    setPicker(null)
    try { atBottom.current = true; chat.put(await api.post<ChatMessage>(`/api/chat/rooms/${room.id}/messages`, { refs })) }
    catch (e) { fail(e) }
  }

  const react = async (m: ChatMessage, emoji: string) => {
    try { chat.put(await api.post<ChatMessage>(`/api/chat/messages/${m.id}/reactions`, { emoji })) } catch (e) { fail(e) }
  }

  const remove = async (m: ChatMessage) => {
    if (!await askConfirm('', t('Nachricht für alle löschen?'))) return
    try { await api.del(`/api/chat/messages/${m.id}`); await chat.refresh() } catch (e) { fail(e) }
  }

  // Bei mir speichern: erst das Ziel waehlen (SaveRefDialog), dann speichern
  const save = (m: ChatMessage, i: number) => setSaving({ message: m, index: i })
  const saveTo = async (m: ChatMessage, i: number, folder: string | null) => {
    setSaving(null)
    try {
      const r = await api.post<{ type: string; path?: string; noteId?: number }>(`/api/chat/messages/${m.id}/refs/${i}/save`, { folder })
      notify(r.type === 'note' ? t('Als Notiz gespeichert') : t('In der Dateiablage gespeichert'), r.path ?? undefined)
    } catch (e) { fail(e) }
  }

  const roomAction = async (f: () => Promise<unknown>) => { try { await f(); await chat.refresh() } catch (e) { fail(e) } }
  // Eigenen Verweis aendern: Art umstellen oder zurueckziehen
  const changeRef = async (m: ChatMessage, i: number, body: { mode?: ChatMode; revoke?: boolean }) => {
    try { chat.put(await api.put<ChatMessage>(`/api/chat/messages/${m.id}/refs/${i}`, body)) } catch (e) { fail(e) }
  }

  const onKey = (e: React.KeyboardEvent) => {
    if (suggestions.length && suggest) {
      if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault()
        const step = e.key === 'ArrowDown' ? 1 : suggestions.length - 1
        setSuggest({ ...suggest, active: (suggest.active + step) % suggestions.length }); return
      }
      if ((e.key === 'Enter' && !e.shiftKey) || e.key === 'Tab') { e.preventDefault(); pick(suggestions[suggest.active] ?? suggestions[0]); return }
      if (e.key === 'Escape') { setSuggest(null); return }
    }
    if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing && !mobile) { e.preventDefault(); send() }
    if (e.key === 'Escape') { setReply(null); if (editing) { setEditing(null); setText('') } }
    if (e.key === 'ArrowUp' && !text && list) {
      const mine = [...list].reverse().find(m => m.sender === me.id && m.kind === 'text' && !m.deleted && m.text)
      if (mine && changeable(mine)) { e.preventDefault(); setEditing(mine); setText(mine.text ?? '') }
    }
  }
  const onPaste = (e: React.ClipboardEvent) => {
    const files = [...e.clipboardData.files]
    if (files.length) { e.preventDefault(); stage(files) }
  }

  // Gruppenraeume (Stufe 4)
  const sp = useSpaces(room)
  const subtitle = room.kind === 'group'
    ? room.members.map(m => (m.id === me.id ? t('Du') : m.name)).join(', ')
    : others[0]?.login ?? ''

  const info = (
    <>
      <RoomAvatar room={room} me={me.id} size={32} />
      <div className={s.headText}>
        <Text weight="semibold" block truncate wrap={false}>{room.title}</Text>
        <div className={s.headSub}>{subtitle}</div>
      </div>
    </>
  )
  const menu = (
      <Menu>
        <MenuTrigger disableButtonEnhancement>
          <Button appearance="subtle" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} />
        </MenuTrigger>
        <MenuPopover>
          <MenuList>
            {room.kind === 'group' && <MenuItem icon={<People20Regular />} onClick={() => setMembers(true)}>{t('Mitglieder')}</MenuItem>}
            {canAskLocation(room, locationRight) && <MenuItem icon={<Location20Regular />} onClick={() => askForLocation(room, notify, roomAction)}>{t('Standort anfragen')}</MenuItem>}
            <SpacesMenu room={room} spaces={sp.spaces} onChanged={sp.setSpaces} fail={fail} />
            {room.kind === 'group' && room.admin && (
              <MenuItem icon={<Rename20Regular />} onClick={async () => {
                const title = await askText(t('Gruppe umbenennen'), room.title)
                if (title) roomAction(() => api.put(`/api/chat/rooms/${room.id}`, { title }))
              }}>{t('Umbenennen')}</MenuItem>
            )}
            <ColorMenu current={room.color} none onPick={color => roomAction(() => api.put(`/api/chat/rooms/${room.id}`, { color: color ?? '' }))} />
            <MenuItem icon={room.muted ? <Speaker220Regular /> : <SpeakerMute20Regular />}
              onClick={() => roomAction(() => api.put(`/api/chat/rooms/${room.id}`, { muted: !room.muted }).then(chatStatusChanged))}>
              {room.muted ? t('Benachrichtigungen an') : t('Stummschalten')}
            </MenuItem>
            {/* Verlauf als Text plus erlaubte Anhaenge (ZIP) */}
            <MenuItem icon={<ArrowDownload20Regular />} onClick={() => { location.href = `/api/chat/rooms/${room.id}/export` }}>{t('Unterhaltung exportieren')}</MenuItem>
            <MenuDivider />
            <MenuItem icon={<ArrowExit20Regular />} onClick={async () => {
              if (!await askConfirm('', room.kind === 'group' ? t('Gruppe „{name}“ verlassen?', { name: room.title }) : t('Unterhaltung mit {name} ausblenden?', { name: room.title }))) return
              roomAction(() => api.post(`/api/chat/rooms/${room.id}/leave`)).then(onBack)
            }}>{room.kind === 'group' ? t('Gruppe verlassen') : t('Unterhaltung ausblenden')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
  )

  let lastDay = ''
  return (
    <>
      {mobile
        ? <BackBar onBack={onBack} title={<div style={{ display: 'flex', alignItems: 'center', gap: 8, minWidth: 0 }}>{info}</div>}>{menu}</BackBar>
        : <div className={s.head}>{info}{menu}</div>}
      <div className={s.convBody}>
      <div className={s.convMain}>
      <SpacesBar spaces={sp.spaces} />
      <div ref={scroller} className={mergeClasses(s.scroll, dragging && s.drop)} onScroll={onScroll} style={{ backgroundColor: tint(room.color) }}
        onDragOver={e => { if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragging(true) } }}
        onDragLeave={() => setDragging(false)}
        onDrop={e => { e.preventDefault(); setDragging(false); stage([...e.dataTransfer.files]) }}>
        {list == null && <Spinner size="small" />}
        {list?.map((m, i) => {
          const day = fmtDay(new Date(m.created))
          const prevAny = list[i - 1]
          // Neuer Tag: Datum; am selben Tag nach mehr als 4 Stunden Pause (etwa morgens nach einer spaeten Nachricht): die Uhrzeit
          const dayLine = day !== lastDay ? (lastDay = day, <div className={s.day}>{dayLabel(m.created)}</div>)
            : prevAny && Date.parse(m.created) - Date.parse(prevAny.created) >= PAUSE ? <div className={s.day}>{fmtTime(new Date(m.created))}</div> : null
          if (m.kind === 'system') return <Fragment key={m.id}>{dayLine}<div className={s.system}>{systemText(m.text, m.sender, names, t)}</div></Fragment>
          const own = m.sender === me.id
          const prev = list[i - 1]
          const first = !prev || prev.sender !== m.sender || prev.kind === 'system' || !!dayLine
          const quoted = m.replyTo ? byId.get(m.replyTo) : undefined
          const next = list[i + 1]
          const fromPukki = m.sender === PUKKI && !m.deleted
          // Pose an der letzten Nachricht einer Folge von Pukki, davor Platz halten (Blasen bleiben bündig)
          // Bewertet (👍/👎): seine Reaktion bleibt sichtbar
          const pose = fromPukki && (!next || next.sender !== m.sender || next.kind === 'system' || ratings[m.id] != null)
          // Eigenes Profilbild an der letzten eigenen Nachricht einer Folge (rechts), davor Platz
          const selfPic = own && (!next || next.sender !== m.sender || next.kind === 'system')
          return (
            <Fragment key={m.id}>
              {dayLine}
              <div id={`msg-${m.id}`} className={mergeClasses(s.line, own && s.lineOwn, first && s.gap)}>
                {fromPukki && (pose ? <img src={pukkiPose(figure, moodOf(m))} alt="" className={s.pose} /> : <span className={s.poseSpace} />)}
                {own && (selfPic && self ? <span className={s.self}><UserAvatar id={self.id} name={self.name} login={self.login} avatar={self.avatar} accent={self.accent} size={32} /></span> : <span className={s.selfSpace} />)}
                <div className={mergeClasses(s.bubble, own && s.bubbleOwn)} style={flash === m.id ? { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: 2 } : undefined}>
                  {first && !own && room.kind === 'group' && <div className={s.sender}>{m.senderName}</div>}
                  {m.replyTo && (
                    <div className={s.quote} onClick={() => document.getElementById(`msg-${m.replyTo}`)?.scrollIntoView({ block: 'center', behavior: 'smooth' })}>
                      <b>{quoted ? names(quoted.sender) : ''}</b> {quoted ? snippet(quoted) : t('Ältere Nachricht')}
                    </div>
                  )}
                  {m.deleted ? <span className={s.deleted}>{t('Nachricht gelöscht')}</span> : (
                    <>
                      {m.refs.map((r, ri) => (
                        <RefView key={ri} message={m} r={r} index={ri} own={own && r.owner === me.id} onPreview={() => setPreview({ message: m.id, index: ri })} onImage={() => messagePhotos(m, ri)}
                          onSave={() => save(m, ri)} onLoad={stick} onMode={mode => changeRef(m, ri, { mode })} onRevoke={revoke => changeRef(m, ri, { revoke })} />
                      ))}
                      {m.ai && <div className={s.aiMark}>{t('Antwort der KI – von dmOffice nicht geprüft')}</div>}
                      {m.text && <Linkified text={m.text} mention={mention} fromAi={m.ai} />}
                      {!!m.actions?.length && m.sender !== me.id && (
                        <div className={s.pukkiActions}>
                          {m.actions.map(a => (
                            <Button key={a.key} size="small" appearance={a.key === 'undo' || a.key === 'loc-no' ? 'secondary' : 'primary'} disabled={acting === m.id}
                              onClick={() => act(m, a.key)}>{a.label}</Button>
                          ))}
                        </div>
                      )}
                    </>
                  )}
                  {m.reactions.length > 0 && (
                    <div className={s.reactions}>
                      {m.reactions.map(x => (
                        <Tooltip key={x.emoji} content={x.users.map(names).join(', ')} relationship="description">
                          <button className={mergeClasses(s.reaction, x.users.includes(me.id) && s.reactionMine)} onClick={() => react(m, x.emoji)}>
                            {x.emoji} {x.users.length > 1 ? x.users.length : ''}
                          </button>
                        </Tooltip>
                      ))}
                    </div>
                  )}
                  <div className={s.meta}>
                    {m.edited && !m.deleted && <span>{t('bearbeitet')}</span>}
                    <span>{fmtTime(new Date(m.created))}</span>
                    {own && !m.deleted && (readByAll(m.id)
                      ? <Tooltip content={t('Gelesen')} relationship="label"><CheckmarkCircle12Filled style={{ color: tokens.colorBrandForeground1 }} /></Tooltip>
                      : deliveredToAll(m.id)
                        ? <Tooltip content={t('Zugestellt')} relationship="label"><CheckmarkCircle12Regular /></Tooltip>
                        : <Tooltip content={t('Gesendet')} relationship="label"><Checkmark12Regular /></Tooltip>)}
                  </div>
                </div>
                {rateable(m, prev) && (
                  <div className={s.thumbs}>
                    <button className={mergeClasses(s.thumb, ratings[m.id] === 1 && s.thumbOn)} onClick={() => rate(m, 1)}
                      aria-label={t('Richtig verstanden')} title={t('Richtig verstanden')} aria-pressed={ratings[m.id] === 1}>
                      {ratings[m.id] === 1 ? <ThumbLike16Filled /> : <ThumbLike16Regular />}
                    </button>
                    <button className={mergeClasses(s.thumb, ratings[m.id] === -1 && s.thumbOn)} onClick={() => rate(m, -1)}
                      aria-label={t('Falsch verstanden')} title={t('Falsch verstanden')} aria-pressed={ratings[m.id] === -1}>
                      {ratings[m.id] === -1 ? <ThumbDislike16Filled /> : <ThumbDislike16Regular />}
                    </button>
                  </div>
                )}
                {thanked === m.id && <span className={s.thanks} role="status">{t('Danke!')}</span>}
                {!m.deleted && (
                  <div className={mergeClasses(s.actions, 'dm-actions')} style={mobile ? { visibility: 'visible' } : undefined}>
                    <Menu>
                      <MenuTrigger disableButtonEnhancement>
                        <Button size="small" appearance="subtle" icon={<Emoji20Regular />} aria-label={t('Reagieren')} />
                      </MenuTrigger>
                      <MenuPopover>
                        <div style={{ display: 'flex', gap: 2, padding: 4 }}>
                          {QUICK.map(e => <Button key={e} size="small" appearance="subtle" onClick={() => react(m, e)}>{e}</Button>)}
                        </div>
                      </MenuPopover>
                    </Menu>
                    <Menu>
                      <MenuTrigger disableButtonEnhancement>
                        <Button size="small" appearance="subtle" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} />
                      </MenuTrigger>
                      <MenuPopover>
                        <MenuList>
                          <MenuItem icon={<ArrowReply20Regular />} onClick={() => { setReply(m); setEditing(null); input.current?.focus() }}>{t('Antworten')}</MenuItem>
                          <MenuItem icon={<ArrowForward20Regular />} onClick={() => setForwarding(m)}>{t('Weiterleiten')}</MenuItem>
                          {m.text && <MenuItem icon={<Copy20Regular />} onClick={() => { void copyText(m.text ?? '') }}>{t('Text kopieren')}</MenuItem>}
                          {own && m.text != null && changeable(m) && (
                            <MenuItem icon={<Edit20Regular />} onClick={() => { setEditing(m); setReply(null); setText(m.text ?? ''); input.current?.focus() }}>{t('Bearbeiten')}</MenuItem>
                          )}
                          {((own && changeable(m)) || (room.kind === 'group' && room.admin)) && <MenuItem icon={<Delete20Regular />} onClick={() => remove(m)}>{t('Für alle löschen')}</MenuItem>}
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                  </div>
                )}
              </div>
            </Fragment>
          )
        })}
        {!thinking && isPukki(room) && list && <PukkiNap last={list[list.length - 1]?.created} figure={figure} s={s} />}
        {thinking && (
          <div className={mergeClasses(s.line, s.gap)} role="status">
            {isPukki(room) && <img src={pukkiPose(figure, 'arbeitet')} alt="" className={s.pose} />}
            <div className={s.bubble}>
              <span className={s.thinking}>
                {isPukki(room) && pukkiAi?.on ? t('Pukki fragt den KI-Dienst {service}', { service: pukkiAi.service ?? '' }) : t('{name} denkt nach', { name: isPukki(room) ? 'Pukki' : room.title })}
                <span className={s.dots}>{[0, 1, 2].map(i => <span key={i} className={s.dot} style={{ animationDelay: `${i * 0.2}s` }} />)}</span>
              </span>
            </div>
          </div>
        )}
        {/* Postausgang: ohne Verbindung geschrieben, wartet auf Versand */}
        {waiting.map(o => (
          <div key={'out-' + o.id} className={mergeClasses(s.line, s.lineOwn)}>
            <span className={s.selfSpace} />
            <div className={mergeClasses(s.bubble, s.bubbleOwn)} style={{ opacity: 0.75 }}>
              <Linkified text={o.text} mention={mention} />
              <div className={s.meta}>
                <Clock12Regular /> {t('Wartet auf Versand')}
                <Button size="small" appearance="transparent" onClick={() => cancel(o.id)}>{t('Nicht senden')}</Button>
              </div>
            </div>
          </div>
        ))}
      </div>
      {(reply || editing) && (
        <div className={s.bar}>
          {reply ? <ArrowReply20Regular /> : <Edit20Regular />}
          <span className={s.barText}>{reply ? <><b>{names(reply.sender)}</b> {snippet(reply)}</> : t('Nachricht bearbeiten')}</span>
          <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} aria-label={t('Abbrechen')}
            onClick={() => { setReply(null); if (editing) { setEditing(null); setText('') } }} />
        </div>
      )}
      {suggestions.length > 0 && (
        <div className={s.suggest} role="listbox">
          {suggestions.map((p, i) => (
            <div key={p.id} role="option" aria-selected={i === suggest?.active} className={mergeClasses(s.suggestItem, i === suggest?.active && s.suggestActive)}
              onMouseDown={e => { e.preventDefault(); pick(p) }}>
              {p.id === '*' ? <Avatar name={p.name} size={20} icon={<People20Regular />} /> : <UserAvatar id={p.id} name={p.name} login={p.login} avatar={(p as any).avatar} accent={(p as any).accent} size={20} />}
              <span>{p.name}</span>{p.login && p.id !== PUKKI && <span className={s.suggestLogin}>{p.login}</span>}
            </div>
          ))}
        </div>
      )}
      {uploading && <div className={s.bar}><Spinner size="extra-tiny" /><span className={s.barText}>{t('Wird hochgeladen: {name}', { name: uploading })}</span></div>}
      {staged.length > 0 && !editing && (
        <div className={s.staged}>
          {staged.map((x, i) => (
            <div key={i} className={s.stagedItem} title={x.file.name}>
              {x.url ? <img src={x.url} alt="" className={s.stagedThumb} /> : <Attach20Regular />}
              <span className={s.stagedName}>{x.file.name || t('Bild')}</span>
              <Button size="small" appearance="subtle" className={s.stagedRemove} icon={<Dismiss20Regular />} aria-label={t('Entfernen')} onClick={() => unstage(i)} />
            </div>
          ))}
        </div>
      )}
      {isPukki(room) && pukkiAi?.on && <div className={s.aiNote}>{t('Was Pukki nicht selbst beantworten kann, geht samt den letzten Nachrichten dieser Unterhaltung an den KI-Dienst {service}.', { service: pukkiAi.service ?? '' })}</div>}
      <div className={s.composer}>
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Button appearance="subtle" icon={<Attach20Regular />} aria-label={t('Anhängen')} disabled={!!editing} />
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              {/* Von diesem Geraet: wird hochgeladen und liegt dann in "Chat"; aus dmOffice: liegt schon auf dem Server, die Nachricht zeigt darauf */}
              <MenuGroup>
                <MenuGroupHeader title={t('Wird hochgeladen und liegt dann in deinem Ordner „Chat“')}>{t('Von diesem Gerät')}</MenuGroupHeader>
                <MenuItem icon={<DocumentArrowUp20Regular />} onClick={() => fileInput.current?.click()}>{t('Datei oder Foto hochladen')}</MenuItem>
              </MenuGroup>
              <MenuDivider />
              <MenuGroup>
                <MenuGroupHeader title={t('Liegt schon auf dem Server – die Nachricht zeigt nur darauf')}>{t('Aus dmOffice')}</MenuGroupHeader>
                {me.files !== false && <MenuItem icon={<ImageMultiple20Regular />} onClick={() => setPicker('gallery')}>{t('Galerie')}</MenuItem>}
                {me.files !== false && <MenuItem icon={<Folder20Regular />} onClick={() => setPicker('file')}>{t('Dateiablage')}</MenuItem>}
                <MenuItem icon={<Note20Regular />} onClick={() => setPicker('note')}>{t('Notiz')}</MenuItem>
              </MenuGroup>
            </MenuList>
          </MenuPopover>
        </Menu>
        <input ref={fileInput} type="file" multiple hidden onChange={e => { stage([...(e.target.files ?? [])]); e.target.value = '' }} />
        {recording ? (
          <div className={s.recording} role="status">
            <span className={s.recDot} />
            <span style={{ flex: 1 }}>{t('Aufnahme')} {fmtDuration(Date.now() - recording.since)}</span>
            <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => stopRecording(false)}>{t('Verwerfen')}</Button>
            <Button appearance="primary" icon={<Send20Filled />} onClick={() => stopRecording(true)} aria-label={t('Senden')} />
          </div>
        ) : (
          <>
            <Textarea ref={input} style={{ flex: 1 }} resize="none" rows={Math.min(6, Math.max(1, text.split('\n').length))}
              placeholder={t('Nachricht schreiben')} value={text} onChange={(_, d) => typed(d.value)} onKeyDown={onKey}
              onBlur={() => setTimeout(() => setSuggest(null), 150)} onPaste={onPaste} />
            {/* Leeres Feld: Mikrofon fuer eine Sprachnachricht, sonst Senden */}
            {!text.trim() && !staged.length && !editing && canRecord
              ? <Button appearance="subtle" icon={<Mic20Regular />} onClick={startRecording} disabled={!!uploading}
                  aria-label={t('Sprachnachricht aufnehmen')} title={t('Sprachnachricht aufnehmen')} />
              : <Button appearance="primary" icon={editing ? <Save20Regular /> : <Send20Filled />} disabled={!text.trim() && !(staged.length && !editing)} onClick={send}
                  aria-label={editing ? t('Speichern') : t('Senden')} />}
          </>
        )}
      </div>
      </div>
      {infoOn && wide && !mobile && infoW.handle('left')}
      {infoOn && wide && !mobile && <ChatInfo width={infoW.width} room={room} me={me.id} messages={list ?? []} spaces={sp.spaces} onPreview={(message, index) => setPreview({ message, index })} onPhotos={showPhotos} />}
      {photos && <Suspense fallback={null}>
        <PhotoViewer items={photos.items} index={photos.index} source={chatRefSource} onIndex={i => setPhotos(p => p && { ...p, index: i })} onClose={() => setPhotos(null)}
          onDownload={i => { const a = document.createElement('a'); a.href = chatRefSource.download(i); a.download = i.name; a.click() }} />
      </Suspense>}
      </div>
      {picker === 'file' && <FilePickerDialog onClose={() => setPicker(null)} onPick={(items, mode) => sendRefs(items.map(i => ({ type: 'file', fileId: i.fileId, mode })))} />}
      {/* Galerie: eigene Fotos zeigt die Nachricht wie aus der Dateiablage; Fotos aus Galerie-Ordnern liegen nicht in der Ablage - die werden geholt und hochgeladen */}
      {picker === 'gallery' && <PickPhotoDialog title={t('Galerie')} onClose={() => setPicker(null)} onPickMany={async items => {
        setPicker(null)
        const own = items.filter(i => !isExt(i) && i.fileId), ext = items.filter(i => isExt(i) || !i.fileId)
        if (own.length) await sendRefs(own.map(i => ({ type: 'file', fileId: i.fileId, mode: 'send' as ChatMode })))
        if (ext.length) {
          try {
            const files = await Promise.all(ext.map(async i => {
              const r = await fetch(gallerySource.download(i), { credentials: 'same-origin' })
              if (!r.ok) throw new Error(i.name)
              return new File([await r.blob()], i.name, { type: i.mime ?? 'image/jpeg' })
            }))
            await upload(files)
          } catch (e) { fail(e) }
        }
      }} />}
      {picker === 'note' && <NotePickerDialog onClose={() => setPicker(null)} onPick={(n, mode) => sendRefs([{ type: 'note', noteId: n.id, mode }])} />}
      {members && <MembersDialog room={room} me={me.id} onClose={() => setMembers(false)} onChanged={() => chat.refresh()} />}
      {forwarding && (
        <ForwardDialog message={forwarding} rooms={(chat.rooms ?? []).filter(r => r.id !== room.id)} onClose={() => setForwarding(null)}
          onDone={list => { setForwarding(null); list.forEach(chat.put); notify(t('Weitergeleitet')) }} />
      )}
      {preview && (() => {
        // Vorschau je nach Art: Notiz, Ordner (blaettern), Termin/Aufgabe/Kontakt
        const pm = byId.get(preview.message)
        const pr = pm?.refs[preview.index]
        if (!pm || !pr) return null
        const canSave = pr.mode !== 'show' && pr.owner !== me.id
        if (pr.type === 'album') return <AlbumRefDialog message={pm.id} index={preview.index} name={pr.name} onClose={() => setPreview(null)} />
        if (pr.type === 'folder') return <FolderRefDialog message={pm.id} index={preview.index} name={pr.name} canSave={canSave} onClose={() => setPreview(null)} />
        if (pr.type === 'note') return (
          <NotePreviewDialog message={preview.message} index={preview.index} onClose={() => setPreview(null)}
            onSave={() => { save(pm, preview.index); setPreview(null) }} />
        )
        return <ItemDialog message={pm.id} index={preview.index} canSave={canSave} onClose={() => setPreview(null)} onSave={() => { save(pm, preview.index); setPreview(null) }} />
      })()}
      {saving && (() => {
        const sr = saving.message.refs[saving.index]
        if (sr.type === 'event' || sr.type === 'task' || sr.type === 'contact') return (
          <CollectionPickDialog type={sr.type} name={sr.name} onClose={() => setSaving(null)} onSave={id => saveTo(saving.message, saving.index, id)} />
        )
        return (
          <SaveRefDialog kind={sr.type === 'note' ? 'note' : 'file'} name={sr.name}
            onClose={() => setSaving(null)} onSave={folder => saveTo(saving.message, saving.index, folder)} />
        )
      })()}
    </>
  )
}

/**
 * Lange nichts gehört (Pukki-Unterhaltung): nach sechs Stunden schläft Pukki, nach drei Tagen träumt er. Die nächste
 * Nachricht weckt ihn.
 */
function PukkiNap({ last, figure, s }: { last?: string; figure: ReturnType<typeof usePukkiFigure>; s: ReturnType<typeof useStyles> }) {
  const age = last ? Date.now() - Date.parse(last) : Infinity
  if (age < 6 * 3600_000) return null
  const dreams = age >= 3 * 24 * 3600_000
  return (
    <div className={s.sleep} role="status">
      <span className={s.sleepPose}>
        <img src={pukkiPose(figure, 'bitte')} alt="" width={72} height={72} style={{ objectFit: 'contain' }} />
        {dreams ? <span className={s.dream} aria-hidden>💭</span> : <span className={s.zzz} aria-hidden>Zzz</span>}
      </span>
      <span>{dreams ? t('Pukki träumt vor sich hin – schreib ihm, dann ist er gleich wieder da.') : t('Pukki schläft – schreib ihm, dann wacht er auf.')}</span>
    </div>
  )
}

function dayLabel(iso: string): string {
  const d = new Date(iso)
  const today = new Date()
  const yesterday = new Date(today.getTime() - 86400000)
  if (d.toDateString() === today.toDateString()) return t('Heute')
  if (d.toDateString() === yesterday.toDateString()) return t('Gestern')
  return d.toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long', year: d.getFullYear() === today.getFullYear() ? undefined : 'numeric' })
}

/** Text mit anklickbaren Adressen - sonst nichts an Auszeichnung. */
/** @Erwaehnungen: Namen, die als „@…“ hervorgehoben werden, und welche davon mich meinen */
interface MentionNames { all: string[]; mine: string[] }

/** Namen fuer @Erwaehnungen in einer Unterhaltung: Anzeigename, Vorname, Login - in Gruppen auch Pukki und „alle“. */
function mentionNames(room: ChatRoom, me: string): { names: MentionNames; people: { id: string; name: string; login: string | null }[] } {
  const people: { id: string; name: string; login: string | null; avatar?: string | null; accent?: string | null }[] = room.members.map(m => ({ id: m.id, name: m.name, login: m.login, avatar: m.avatar, accent: m.accent }))
  if (room.kind === 'group' && !people.some(p => p.id === PUKKI)) people.push({ id: PUKKI, name: 'Pukki', login: null })
  const of = (p: { name: string; login: string | null }) => [p.name, p.name.split(' ')[0], p.login ?? ''].filter(Boolean)
  const everyone = ['alle', 'all', 'everyone']
  const all = [...new Set([...people.flatMap(of), ...(room.kind === 'group' ? everyone : [])])]
  const mine = [...new Set([...people.filter(p => p.id === me).flatMap(of), ...everyone])]
  return { names: { all, mine }, people }
}

const escapeRe = (s: string) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')

/** Link aus einer KI-Antwort: Ziel sichtbar, geöffnet wird erst nach einer Rückfrage. */
function AiLink({ url, label }: { url: string; label: string }) {
  const s = useStyles()
  let host = ''
  try { host = new URL(url).host } catch { /* ungültig: nur der Text */ }
  if (!host) return <>{label}</>
  const open = async (e: React.MouseEvent) => {
    e.preventDefault()
    if (await askConfirm(t('Link aus einer KI-Antwort'), t('Diesen Link hat die KI geschrieben, dmOffice hat ihn nicht geprüft. Er führt zu {host}. Gib dort keine Passwörter ein, auch wenn die Seite danach fragt.', { host }), t('Öffnen')))
      window.open(url, '_blank', 'noopener,noreferrer')
  }
  return <><a href={url} title={url} onClick={open} rel="noopener noreferrer">{label}</a>{label !== url && <span className={s.linkHost}> ({host})</span>}</>
}

function Linkified({ text, mention, fromAi }: { text: string; mention?: MentionNames; fromAi?: boolean }) {
  const s = useStyles()
  // Klickbar: Web-Adressen, Mail-Adressen (neue Mail in dmOffice), Telefonnummern (tel:)
  // [Titel](Adresse) - etwa Pukkis Schlagzeilen - zeigt nur den Titel als Link
  const parts = text.split(/(\[[^\]\n]{1,300}\]\((?:https?:\/\/|#\/)[^\s)]+\)|https?:\/\/[^\s<>"]+|[\w.+-]+@[\w-]+(?:\.[\w-]+)+|(?<![\w/.])(?:\+\d|0\d)[\d \-/()]{5,}\d)/g)
  const link = (p: string, i: number) => {
    const md = /^\[([^\]\n]+)\]\(((?:https?:\/\/|#\/)[^\s)]+)\)$/.exec(p)
    // Text aus der KI: keine Sprünge innerhalb von dmOffice, Links nur mit Rückfrage
    if (fromAi && md) return md[2].startsWith('#/') ? <Fragment key={i}>{md[1]}</Fragment> : <AiLink key={i} url={md[2]} label={md[1]} />
    if (fromAi && /^https?:/i.test(p)) return <AiLink key={i} url={p} label={p} />
    // Interne Links (#/journal/entry/…): in dmOffice selbst öffnen
    if (md && md[2].startsWith('#/')) return <a key={i} href={md[2]}>{md[1]}</a>
    // Fremde Adresse hinter einem Titel: das Ziel steht dabei - sonst ließe sich über eine untergeschobene Nachricht (etwa ein
    // Mail-Betreff, den die KI im Verlauf sieht) ein harmlos klingender Titel mit einem fremden Ziel zeigen
    if (md) {
      let host = ''
      try { host = new URL(md[2]).host } catch { /* ungültig: nur der Titel */ }
      return <Fragment key={i}><a href={md[2]} target="_blank" rel="noopener noreferrer" title={md[2]}>{md[1]}</a>{host && <span className={s.linkHost}> ({host})</span>}</Fragment>
    }
    if (/^https?:/i.test(p)) return <a key={i} href={p} target="_blank" rel="noopener noreferrer">{p}</a>
    if (p.includes('@')) return (
      <a key={i} href={`mailto:${p}`} onClick={e => { e.preventDefault(); sessionStorage.setItem('dmoffice.compose.to', p); location.hash = '#/mail' }}>{p}</a>
    )
    return <a key={i} href={`tel:${p.replace(/[^\d+]/g, '')}`}>{p}</a>
  }
  // „@Name“ als eigenes Wort (nicht in Mail-Adressen) - laengste Namen zuerst, damit „@Ben Test“ vor „@Ben“ greift
  const re = mention?.all.length
    ? new RegExp(`(?<![\\p{L}\\p{N}])(@(?:${[...mention.all].sort((a, b) => b.length - a.length).map(escapeRe).join('|')}))(?![\\p{L}\\p{N}])`, 'giu')
    : null
  const mine = new Set(mention?.mine.map(n => '@' + n.toLowerCase()))
  const marked = (p: string, key: number) => !re ? p : p.split(re).map((x, j) => j % 2
    ? <span key={`${key}-${j}`} className={mergeClasses(s.mention, mine.has(x.toLowerCase()) && s.mentionMe)}>{x}</span> : x)
  return <div>{parts.map((p, i) => (i % 2 ? link(p, i) : marked(p, i)))}</div>
}

/** Kennzeichen der Art: Auge = gezeigt (nur ansehen), Pfeil = gesendet (darf gespeichert werden), mit Ablauf. */
function ModeBadge({ r }: { r: ChatRef }) {
  const s = useStyles()
  const until = r.expires ? ' · ' + t('bis {date}', { date: fmtDay(new Date(r.expires)) }) : ''
  return (
    <span className={s.mode}>
      {r.mode === 'show' ? <Eye16Regular /> : <ArrowForward16Regular />}
      {(r.mode === 'show' ? t('Gezeigt') : t('Gesendet')) + until}
      {r.forwarded && ' · ' + t('weitergeleitet')}
    </span>
  )
}

/** Adresse für die Karten-App: iPhone/iPad Apple Karten, Android geo: (fragt nach der App), sonst OpenStreetMap. */
export function mapUrl(lat: number, lon: number) {
  const ua = navigator.userAgent
  if (/iPhone|iPad|iPod|Macintosh/.test(ua) && 'ontouchend' in document) return `https://maps.apple.com/?q=${lat},${lon}`
  if (/Android/.test(ua)) return `geo:${lat},${lon}?q=${lat},${lon}`
  return `https://www.openstreetmap.org/?mlat=${lat}&mlon=${lon}#map=17/${lat}/${lon}`
}

const LocationMap = lazy(() => import('./LocationMap'))

/** Live noch n Minuten? null: nicht (mehr) live. Tickt, solange es live ist. */
function useLiveMinutes(until?: string | null) {
  const [now, setNow] = useState(Date.now())
  const end = until ? new Date(until).getTime() : 0
  useEffect(() => {
    if (end <= Date.now()) return
    const timer = setInterval(() => setNow(Date.now()), 15000)
    return () => clearInterval(timer)
  }, [end])
  return end > now ? Math.max(1, Math.ceil((end - now) / 60000)) : null
}

function LocationCard({ r, message, own }: { r: ChatRef; message: ChatMessage; own: boolean }) {
  const s = useStyles()
  const live = useLiveMinutes(r.liveUntil)
  const [stopping, setStopping] = useState(false)
  if (r.lat == null || r.lon == null) return (
    <div className={s.card}><span className={s.gone}>{r.state === 'revoked' ? t('{name} – zurückgezogen', { name: t('Standort') }) : t('{name} – abgelaufen', { name: t('Standort') })}</span></div>
  )
  const stop = async () => {
    setStopping(true)
    try { await api.post(`/api/chat/rooms/${message.room}/location/${message.id}/stop`) } catch { /* nächster Abgleich zeigt den Stand */ } finally { setStopping(false) }
  }
  const title = live != null ? t('Live-Standort') : r.liveUntil ? t('Letzter Standort') : t('Standort')
  const detail = [
    live != null ? t('live noch {n} Min', { n: live }) : null,
    r.liveUntil && r.at ? t('Stand {time}', { time: fmtTime(new Date(r.at)) }) : null,
    r.accuracy ? t('auf etwa {m} m genau', { m: Math.round(r.accuracy) }) : null,
  ].filter(Boolean).join(' · ')
  return (
    <div className={s.card} style={{ flexDirection: 'column', alignItems: 'stretch', width: '280px', maxWidth: '100%' }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
        <Location20Regular />
        <span className={s.cardText}>
          <b>{title}</b><br />
          {detail && <span className={s.gone} style={{ fontStyle: 'normal' }}>{detail}</span>}
        </span>
      </div>
      <Suspense fallback={<div style={{ height: '150px' }} />}><LocationMap lat={r.lat} lon={r.lon} accuracy={r.accuracy} /></Suspense>
      <div style={{ display: 'flex', gap: '12px', marginTop: '4px', alignItems: 'center' }}>
        <a href={mapUrl(r.lat, r.lon)} target="_blank" rel="noopener noreferrer" style={{ color: 'inherit', textDecoration: 'underline' }}>{t('In Karten-App öffnen')}</a>
        {own && !r.forwarded && live != null && <Button size="small" appearance="subtle" disabled={stopping} onClick={stop}>{t('Teilen beenden')}</Button>}
      </div>
    </div>
  )
}

function RefView({ message, r, index, own, onPreview, onImage, onSave, onLoad, onMode, onRevoke }: {
  message: ChatMessage; r: ChatRef; index: number; own: boolean; onPreview: () => void; onImage: () => void; onSave: () => void; onLoad: () => void
  onMode: (mode: 'show' | 'send') => void; onRevoke: (revoke: boolean) => void
}) {
  const s = useStyles()
  const [gone, setGone] = useState(false)
  const label = r.type === 'location' ? t('Standort') : r.name
  // Abgelaufen oder zurueckgezogen: Empfaenger sehen nur noch den Hinweis, der Besitzer kann wieder freigeben
  if (r.state !== 'ok' && !own) return (
    <div className={s.card}><span className={s.gone}>{r.state === 'expired' ? t('{name} – abgelaufen', { name: label }) : t('{name} – zurückgezogen', { name: label })}</span></div>
  )
  const canSave = !own && r.mode !== 'show'
  const canDownload = own || r.mode !== 'show'
  // Eigenes Original: Art umstellen, zurueckziehen (nicht bei Weiterleitungen)
  const ownerMenu = own && !r.forwarded && (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <Button size="small" appearance="subtle" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} />
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          {r.type === 'location' ? null : r.mode === 'show'
            ? <MenuItem icon={<ArrowForward16Regular />} onClick={() => onMode('send')}>{t('Speichern erlauben (Senden)')}</MenuItem>
            : <MenuItem icon={<Eye16Regular />} onClick={() => onMode('show')}>{t('Nur zeigen')}</MenuItem>}
          {r.state === 'revoked'
            ? <MenuItem onClick={() => onRevoke(false)}>{t('Wieder freigeben')}</MenuItem>
            : <MenuItem icon={<Dismiss20Regular />} onClick={() => onRevoke(true)}>{t('Zurückziehen')}</MenuItem>}
        </MenuList>
      </MenuPopover>
    </Menu>
  )
  const badge = <div className={s.badgeRow}><ModeBadge r={r} />{own && r.state !== 'ok' && <span className={s.gone}>{r.state === 'expired' ? t('abgelaufen') : t('zurückgezogen')}</span>}{ownerMenu}</div>
  // Ton (Sprachnachricht): gleich abspielen
  if (r.type === 'file' && (r.mime ?? '').startsWith('audio/') && r.state === 'ok') return (
    <div>
      <audio className={s.audio} controls preload="metadata" src={refUrl(message.id, index, 'file', { inline: true })} />
      {badge}
    </div>
  )
  if (r.type === 'file' && isImage(r) && !gone) return (
    <div>
      <img className={s.image} src={refUrl(message.id, index, 'thumb', { size: 512 })} alt={r.name} loading="lazy"
        onClick={onImage} onLoad={onLoad} onError={() => setGone(true)} />
      <div className={s.badgeRow}>
        <ModeBadge r={r} />
        {canSave && <Tooltip content={t('Bei mir speichern')} relationship="label"><Button size="small" appearance="subtle" icon={<Save20Regular />} onClick={onSave} /></Tooltip>}
        {ownerMenu}
      </div>
    </div>
  )
  // Ordner, Termin, Aufgabe, Kontakt: Karte, antippen oeffnet die Ansicht
  if (r.type === 'folder' || r.type === 'event' || r.type === 'task' || r.type === 'contact' || r.type === 'album') {
    const KindGlyph = r.type === 'album' ? ImageMultiple20Regular : r.type === 'folder' ? Folder20Regular : r.type === 'event' ? CalendarLtr20Regular : r.type === 'task' ? TaskListSquareLtr20Regular : Person20Regular
    const kind = { folder: t('Ordner'), event: t('Termin'), task: t('Aufgabe'), contact: t('Kontakt'), album: t('Album') }[r.type]
    return (
      <div>
        <div className={s.card}>
          <KindGlyph />
          <span className={s.cardText} style={{ cursor: 'pointer' }} onClick={onPreview}><b>{r.name}</b><br /><span className={s.gone} style={{ fontStyle: 'normal' }}>{kind}</span></span>
          {canSave && r.type !== 'folder' && r.type !== 'album' && <Tooltip content={t('Bei mir speichern')} relationship="label"><Button size="small" appearance="subtle" icon={<Save20Regular />} onClick={onSave} /></Tooltip>}
        </div>
        {badge}
      </div>
    )
  }
  if (r.type === 'location') return <div><LocationCard r={r} message={message} own={own} />{badge}</div>
  if (r.type === 'note') return (
    <div>
      <div className={s.card}>
        <Note20Regular />
        <span className={s.cardText} style={{ cursor: 'pointer' }} onClick={onPreview}><b>{r.name}</b><br /><span className={s.gone} style={{ fontStyle: 'normal' }}>{t('Notiz')}</span></span>
        {canSave && <Tooltip content={t('Bei mir speichern')} relationship="label"><Button size="small" appearance="subtle" icon={<Save20Regular />} onClick={onSave} /></Tooltip>}
      </div>
      {badge}
    </div>
  )
  return (
    <div>
      <div className={s.card}>
        <KindIcon item={{ name: r.name, isDir: false, mime: r.mime }} />
        <span className={s.cardText}>
          {gone ? <span className={s.gone}>{t('{name} – nicht mehr verfügbar', { name: r.name })}</span> : (
            <a href={refUrl(message.id, index, 'file', { inline: true })} target="_blank" rel="noopener noreferrer" style={{ color: 'inherit' }}>{r.name}</a>
          )}
          {r.size != null && <><br /><span className={s.gone} style={{ fontStyle: 'normal' }}>{fmtSize(r.size)}</span></>}
        </span>
        {!gone && canDownload && (
          <Tooltip content={t('Herunterladen')} relationship="label">
            <Button size="small" appearance="subtle" icon={<ArrowDownload20Regular />} as="a" href={refUrl(message.id, index, 'file')} />
          </Tooltip>
        )}
        {!gone && canSave && <Tooltip content={t('Bei mir speichern')} relationship="label"><Button size="small" appearance="subtle" icon={<Save20Regular />} onClick={onSave} /></Tooltip>}
      </div>
      {badge}
    </div>
  )
}
