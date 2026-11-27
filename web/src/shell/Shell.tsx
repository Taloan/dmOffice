import {
  Button,
  Menu, MenuDivider, MenuItem, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, Spinner, Text, Tooltip,
  makeStyles, mergeClasses, tokens, CounterBadge,
} from '@fluentui/react-components'
import {
  bundleIcon,
  Grid24Filled, Grid24Regular, Mail24Filled, Mail24Regular, CalendarLtr24Filled, CalendarLtr24Regular, People24Filled, People24Regular,
  TaskListSquareLtr24Filled, TaskListSquareLtr24Regular, Note24Filled, Note24Regular, Folder24Filled, Folder24Regular, Image24Filled, Image24Regular,
  BookOpen24Filled, BookOpen24Regular, // [feature:journal]
  Settings24Regular, Settings24Filled, ShieldPersonRegular, ShieldPersonFilled, SignOut20Regular, LockClosed20Regular, WeatherMoon20Regular,
  LineHorizontal120Regular, Delete20Regular,
  ArrowSync20Regular, CloudOff20Regular, PuzzlePiece24Filled, PuzzlePiece24Regular, MoreHorizontal24Regular, Chat24Filled, Chat24Regular, MusicNote224Filled, MusicNote224Regular, DocumentBulletList24Filled, DocumentBulletList24Regular,
} from '@fluentui/react-icons'
import { Component, lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react'
import { ZoneHint } from './ZoneHint'
import { adjustInbox, pendingNow, type Shown, usePendingVersion } from '../mail/pending'
import { shownInbox, statusSeen, usePreloadVersion } from '../mail/preload'
import { loadPlugins, pluginViews } from '../plugins/registry'
import { e2eForget } from '../e2e'
import { forgetLocalData } from '../localData'
import { api, demoMode, featureOn, setUnlockHandler } from '../api'
import { useApp } from '../App'
import { t, tError } from '../i18n'
import { brandLogo, storedTheme, storeTheme, ThemeChoice } from '../theme'
import { REACHABLE_RECHECK, usePoll, useReachable, useVersion } from './hooks'
import { useMailPush } from '../mail/mailPush' // [feature:mailpush]
import { StorageInfo } from './StorageInfo'
import { EmergencyBadge } from './EmergencyBadge'
import { HelpMenu } from './Help'
import { copyText } from './browser'
import { Notifier } from './Notifier'
import { Bookmarks } from './Bookmarks'
import { ContextMenu, useContextMenu } from './context'
import { ColorMenu } from './context' // [feature:navcolors]
import { navPalette, navShade, setNavColor } from './navcolors' // [feature:navcolors]
import { useIsDark } from '../theme' // [feature:navcolors]
import { fitRail, fullOrder, insertSep, isSep, moveEntry, railLayout, removeSep } from './railsep'
import { ActivityButton } from './Activity'
import { useIsMobile } from './mobile'
import { installDialogBehaviour } from './dialogs'
import { closeTresor, requestTresorOpen, TRESOR_EVENT, useTresor } from '../tresor/tresor'
import { ChatNotifier, useChatStatus } from '../chat/ChatNotifier'
import { SendToChatHost } from '../chat/SendToChat'
import { UserAvatar } from '../chat/UserAvatar'
import { usePrefs } from '../prefs'
import { PlayerBar } from '../audio/PlayerBar'
import { player, useHasQueue } from '../audio/player'

// Nach einem Update gibt es die Teilstuecke des alten Builds nicht mehr - dann einmal neu laden
function load<T>(f: () => Promise<T>): () => Promise<T> {
  return () => f().catch(err => {
    if (!sessionStorage.getItem('dmoffice.reloaded')) {
      sessionStorage.setItem('dmoffice.reloaded', '1')
      location.reload()
      return new Promise<T>(() => {})
    }
    throw err
  })
}
const DashboardView = lazy(load(() => import('../dashboard/DashboardView')))
const MailView = lazy(load(() => import('../mail/MailView')))
const ChatView = lazy(load(() => import('../chat/ChatView')))
const CalendarView = lazy(load(() => import('../calendar/CalendarView')))
const ContactsView = lazy(load(() => import('../contacts/ContactsView')))
const TasksView = lazy(load(() => import('../tasks/TasksView')))
const NotesView = lazy(load(() => import('../notes/NotesView')))
// [feature:journal]
const JournalView = lazy(load(() => import('../journal/JournalView')))
// [/feature:journal]
const FilesView = lazy(load(() => import('../files/FilesView')))
const AudioView = lazy(load(() => import('../audio/AudioView')))
const DmsView = lazy(load(() => import('../dms/DmsView')))
const SettingsView = lazy(load(() => import('../settings/SettingsView')))
const AdminView = lazy(load(() => import('../admin/AdminView')))

class Boundary extends Component<{ children: React.ReactNode; resetKey: string }, { error: Error | null }> {
  state = { error: null as Error | null }
  static getDerivedStateFromError(error: Error) { return { error } }
  componentDidUpdate(prev: { resetKey: string }) { if (prev.resetKey !== this.props.resetKey && this.state.error) this.setState({ error: null }) }
  render() {
    if (!this.state.error) return this.props.children
    return (
      <div style={{ margin: 'auto', textAlign: 'center', maxWidth: 480 }}>
        <Text size={500} block>{t('Hier ist etwas schiefgegangen.')}</Text>
        <Text block style={{ margin: '8px 0 16px', color: tokens.colorNeutralForeground3 }}>{tError(this.state.error.message)}</Text>
        <Button appearance="primary" onClick={() => location.reload()}>{t('Neu laden')}</Button>
      </div>
    )
  }
}

const DashboardIcon = bundleIcon(Grid24Filled, Grid24Regular)
const MailIcon = bundleIcon(Mail24Filled, Mail24Regular)
const ChatIcon = bundleIcon(Chat24Filled, Chat24Regular)
const CalIcon = bundleIcon(CalendarLtr24Filled, CalendarLtr24Regular)
const PeopleIcon = bundleIcon(People24Filled, People24Regular)
const TaskIcon = bundleIcon(TaskListSquareLtr24Filled, TaskListSquareLtr24Regular)
const NoteIcon = bundleIcon(Note24Filled, Note24Regular)
// [feature:journal]
const JournalIcon = bundleIcon(BookOpen24Filled, BookOpen24Regular)
// [/feature:journal]
const FilesIcon = bundleIcon(Folder24Filled, Folder24Regular)
const GalleryIcon = bundleIcon(Image24Filled, Image24Regular)
const AudioIcon = bundleIcon(MusicNote224Filled, MusicNote224Regular)
const DmsIcon = bundleIcon(DocumentBulletList24Filled, DocumentBulletList24Regular)
const SettingsIcon = bundleIcon(Settings24Filled, Settings24Regular)
const AdminIcon = bundleIcon(ShieldPersonFilled, ShieldPersonRegular)
const PluginIcon = bundleIcon(PuzzlePiece24Filled, PuzzlePiece24Regular)

export type Module = 'dashboard' | 'mail' | 'chat' | 'calendar' | 'contacts' | 'tasks' | 'notes' | 'journal' | 'coach' | 'gallery' | 'audio' | 'dms' | 'files' | 'settings' | 'admin'

const modules: { key: Module; label: string; icon: any }[] = [
  { key: 'dashboard', label: t('Übersicht'), icon: DashboardIcon },
  { key: 'mail', label: t('E-Mail'), icon: MailIcon },
  { key: 'chat', label: t('Chat'), icon: ChatIcon },
  { key: 'calendar', label: t('Kalender'), icon: CalIcon },
  { key: 'contacts', label: t('Kontakte'), icon: PeopleIcon },
  { key: 'tasks', label: t('Aufgaben'), icon: TaskIcon },
  { key: 'notes', label: t('Notizen'), icon: NoteIcon },
  // [feature:journal]
  { key: 'journal', label: t('Tagebuch'), icon: JournalIcon },
  // [/feature:journal]
  { key: 'gallery', label: t('Galerie'), icon: GalleryIcon },
  { key: 'audio', label: t('Audiothek'), icon: AudioIcon },
  { key: 'dms', label: 'DMS', icon: DmsIcon },
  { key: 'files', label: t('Dateien'), icon: FilesIcon },
]

/** W1: Bereiche der Plugins kommen nach dem Laden hinten an die Liste (Kennung = Adresse #/id); gleichnamige Kernbereiche gehen vor. */
const pluginComponents = new Map<string, React.ComponentType>()
function addPluginAreas(): boolean {
  let added = false
  for (const v of pluginViews()) {
    if (modules.some(m => m.key === v.id) || ['settings', 'admin'].includes(v.id)) continue
    modules.push({ key: v.id as Module, label: v.label, icon: v.icon ?? PluginIcon })
    pluginComponents.set(v.id, v.component)
    added = true
  }
  return added
}

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', height: '100%', backgroundColor: tokens.colorNeutralBackground3 },
  // Kopfzeile neutral wie in Office (dunkel: Anthrazit) - die Signaturfarbe traegt nur das Logo
  top: {
    height: '48px', flexShrink: 0, display: 'flex', alignItems: 'center', gap: '8px', padding: '0 12px',
    backgroundColor: tokens.colorNeutralBackground3, color: tokens.colorNeutralForeground1,
  },
  brand: { display: 'flex', alignItems: 'center', gap: '8px', fontWeight: 600, fontSize: '16px', minWidth: '220px' },
  release: { fontWeight: 400, fontSize: '13px', color: tokens.colorNeutralForeground2, whiteSpace: 'nowrap', cursor: 'pointer' },
  spacer: { flex: 1 },
  // Bereiche in der linken Leiste: füllen den Platz über Verwaltung und Einstellungen, der Rest steht hinter „…“
  railTop: { flex: 1, minHeight: 0, width: '100%', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '4px', overflow: 'hidden' },
  topButton: { color: tokens.colorNeutralForeground2, minWidth: 'auto', ':hover': { color: tokens.colorNeutralForeground1, backgroundColor: tokens.colorNeutralBackground3Hover } },
  body: { flex: 1, display: 'flex', minHeight: 0 },
  rail: {
    width: '56px', flexShrink: 0, display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '4px', paddingTop: '8px',
    backgroundColor: tokens.colorNeutralBackground3,
  },
  railButton: {
    width: '44px', height: '44px', borderRadius: tokens.borderRadiusLarge, position: 'relative', color: tokens.colorNeutralForeground2,
    ':hover': { backgroundColor: tokens.colorNeutralBackground3Hover },
  },
  // Alle Bereichssymbole in der eingestellten Akzentfarbe
  tinted: { color: tokens.colorBrandForeground1, ':hover': { color: tokens.colorBrandForeground1 } },
  railActive: {
    color: tokens.colorBrandForeground1, backgroundColor: tokens.colorNeutralBackground1,
    '::before': { content: '""', position: 'absolute', left: '-6px', top: '12px', bottom: '12px', width: '3px', borderRadius: '2px', backgroundColor: tokens.colorBrandBackground },
  },
  badge: { position: 'absolute', top: '2px', right: '0px', pointerEvents: 'none' },
  // Trenner in der Leiste (railsep): dünne Linie wie über den Lesezeichen; die Hülle ist zum Greifen und Rechtsklicken etwas höher
  sep: { width: '44px', padding: '3px 0', display: 'flex', justifyContent: 'center', alignItems: 'center', flexShrink: 0, cursor: 'grab' },
  sepLine: { width: '28px', height: '1px', backgroundColor: tokens.colorNeutralStroke2 },
  sepMobile: { width: '9px', height: '42px', padding: '0 4px', cursor: 'default' },
  sepLineMobile: { width: '1px', height: '28px' },
  main: {
    flex: 1, minWidth: 0, display: 'flex', backgroundColor: tokens.colorNeutralBackground1, borderTopLeftRadius: tokens.borderRadiusXLarge,
    overflow: 'hidden', boxShadow: tokens.shadow4,
  },
  loading: { margin: 'auto' },
  away: { margin: 'auto', padding: '24px', maxWidth: '480px', textAlign: 'center', color: tokens.colorNeutralForeground2 },
  // Für Hilfsprogramme: benennt die Seite, ohne Platz zu brauchen
  pageName: { position: 'absolute', width: '1px', height: '1px', overflow: 'hidden', clipPath: 'inset(50%)', whiteSpace: 'nowrap', margin: 0 },
  // Handy: Kopfzeile ohne feste Markenbreite, Bereiche als Leiste unten, Inhalt ohne Rahmen
  brandMobile: { minWidth: 'auto' },
  bodyMobile: { flexDirection: 'column' },
  railMobile: {
    // Mehr Bereiche als Platz: Leiste wischen, statt die ganze Seite breiter zu machen (dann liefen auch Dialoge und Menüs aus dem Bild)
    width: '100%', maxWidth: '100vw', height: 'auto', flexDirection: 'row', justifyContent: 'space-between', paddingTop: 0, gap: 0, order: 2,
    overflowX: 'auto', overflowY: 'hidden', scrollbarWidth: 'none',
    padding: '2px 4px calc(2px + env(safe-area-inset-bottom, 0px))', borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    '& > *': { flexShrink: 0 },
  },
  railButtonMobile: { width: '42px', minWidth: '42px', height: '42px' },
  railActiveMobile: { '::before': { left: '12px', right: '12px', top: 'auto', bottom: '-2px', width: 'auto', height: '3px' } },
  mainMobile: { borderTopLeftRadius: 0, boxShadow: 'none', position: 'relative' },
})

/** Release-Liste und Hauptschalter der Verwaltung: Bereiche, die nicht freigeschaltet oder ausgeschaltet sein können. */
const moduleFeature: Partial<Record<Module, string>> = { tasks: 'tasks', notes: 'notes', gallery: 'gallery', audio: 'audio', dms: 'dms',
  journal: 'journal', // [feature:journal]
}
const moduleOn = (m: Module) => !moduleFeature[m] || featureOn(moduleFeature[m]!)

/** Gehört die Adresse zu diesem Bereich? Sonst taugt sie nicht als seine letzte Adresse. */
const hashBelongs = (hash: string, m: Module) => hash.replace(/^#\/?/, '').split('/')[0] === m

function currentModule(): Module {
  // Alte Links auf die Galerie in den Dateien (#/files/:photos) führen ins eigene Modul
  const h = (/^#\/?files\/:photos/.test(location.hash) ? 'gallery' : location.hash.replace(/^#\/?/, '').split('/')[0]) as Module
  return [...modules.map(m => m.key).filter(moduleOn), 'settings', 'admin'].includes(h) ? h : 'dashboard'
}

/** Statt einer leeren Seite: Lesezeichen und Links können auf einen Bereich zeigen, der für dieses Konto nicht eingeschaltet ist. */
function NotAvailable({ text }: { text: string }) {
  const s = useStyles()
  return (
    <div className={s.away}>
      <Text block>{text}</Text>
      <Button appearance="primary" style={{ marginTop: 12 }} onClick={() => { location.hash = '#/dashboard' }}>{t('Zur Übersicht')}</Button>
    </div>
  )
}

export function Shell() {
  const s = useStyles()
  const { prefs, update: updatePrefs } = usePrefs()
  // Leiste links umsortieren: mit gedrückter Umschalt- oder Strg-Taste ziehen (Trenner, Kennung railsep: ohne Taste)
  const [dragMod, setDragMod] = useState<string | null>(null)
  const [dropAt, setDropAt] = useState<string | null>(null)
  // Rechtsklick in der Leiste (Kennung railsep): auf einen Bereich, einen Trenner oder freie Fläche; „color“: Symbol ohne Trenner-Einträge
  const railMenu = useContextMenu<{ kind: 'mod' | 'sep' | 'end' | 'color'; key?: string }>()
  // [feature:navcolors]
  // Farben der Symbole in der Leiste (Kennung navcolors, ab 2.2): je Bereich aus der Akzent-Palette, unabhängig von der Akzentfarbe
  const colorsOn = featureOn('navcolors')
  const dark = useIsDark()
  const navTint = (k: string): string | undefined => { const c = colorsOn ? prefs.navColors?.[k] : undefined; return c ? navShade(c, dark) : undefined }
  // [feature:navcolors:else]
  // const colorsOn = false
  // const navTint = (_k: string): string | undefined => undefined
  // [/feature:navcolors]
  // Plugins (Kennung plugins): nach dem Laden die Leiste neu aufbauen; danach gilt eine gemerkte Adresse eines Plugin-Bereichs
  const [pluginTick, setPluginTick] = useState(0)
  useEffect(() => {
    const on = () => { if (addPluginAreas()) { setPluginTick(n => n + 1); const m = currentModule(); setModule(m); setVisited(v => v.includes(m) ? v : [...v, m]) } }
    window.addEventListener('dmoffice:plugins', on)
    loadPlugins().then(on)
    return () => window.removeEventListener('dmoffice:plugins', on)
  }, [])
  const ordered = useMemo(() => {
    const order = prefs.moduleOrder ?? []
    const at = (k: string) => { const i = order.indexOf(k); return i < 0 ? order.length + modules.findIndex(m => m.key === k) : i }
    return [...modules].sort((a, b) => at(a.key) - at(b.key))
  }, [prefs.moduleOrder, pluginTick]) // eslint-disable-line react-hooks/exhaustive-deps
  const moveModule = (from: string, to: string | null) => {
    if (from === to) return
    // Trenner (railsep): die ganze gespeicherte Reihenfolge samt Trennern umstellen; ohne Ziel ans Ende
    if (sepOn) { updatePrefs({ moduleOrder: moveEntry(fullOrder(prefs.moduleOrder, ordered.map(m => m.key)), from, to) }); return }
    if (to == null) return
    const keys: string[] = ordered.map(m => m.key).filter(k => k !== from)
    keys.splice(keys.indexOf(to), 0, from)
    updatePrefs({ moduleOrder: keys })
  }
  const mobile = useIsMobile()
  // Wie viele Bereichsknöpfe (44 px + 4 px Abstand) in die Leiste passen; der Rest kommt ins Menü „…“
  const railTop = useRef<HTMLDivElement>(null)
  // Handy: Leiste unten rollt quer - an der Seite mit verborgenen Einträgen blendet ein Verlauf aus (Hinweis aufs Wischen)
  const railNav = useRef<HTMLElement>(null)
  const [railFade, setRailFade] = useState({ l: false, r: false })
  const railEdges = () => {
    const el = railNav.current
    if (!el) return
    const l = el.scrollLeft > 2, r = el.scrollLeft + el.clientWidth < el.scrollWidth - 2
    setRailFade(f => f.l === l && f.r === r ? f : { l, r })
  }
  const [railSlots, setRailSlots] = useState(99)
  const [railPx, setRailPx] = useState(99 * 48) // dasselbe in px, für Trenner (railsep)
  useEffect(() => {
    const el = railTop.current
    if (!el || mobile) return
    const measure = () => { setRailSlots(Math.max(1, Math.floor((el.clientHeight + 4) / 48))); setRailPx(el.clientHeight + 4) }
    measure()
    const ro = new ResizeObserver(measure)
    ro.observe(el)
    return () => ro.disconnect()
  }, [mobile])
  const { me, notify } = useApp()
  const [module, setModule] = useState<Module>(currentModule())
  const [theme, setTheme] = useState<ThemeChoice>(storedTheme())
  // _at: Beginn der Anfrage - Änderungen, die danach noch unterwegs sind, zählt das Seitenmenü selbst dazu (mail/pending.ts)
  const status = usePoll(() => { const at = pendingNow(); return api.get('/api/mail/status').then(r => ({ ...r, _at: at })) }, 20000, 'dmoffice:mailstatus')
  usePendingVersion()
  // Neue Mails erst zählen, wenn ihre Liste vorgeladen ist (mail/preload.ts); das Vorladen startet hier, auch ohne offenen Mailbereich
  const preloadVersion = usePreloadVersion()
  useEffect(() => { statusSeen(status) }, [status])
  useMailPush() // [feature:mailpush]
  const shownUnread = useRef<Record<string, Shown>>({})
  const chatStatus = useChatStatus()
  // DMS: Summe der Inboxen (Dateiablage und offener Tresor); DMS meldet Änderungen über das Ereignis
  useEffect(() => {
    const on = () => window.dispatchEvent(new Event('dmoffice:dmsstatus'))
    window.addEventListener(TRESOR_EVENT, on)
    return () => window.removeEventListener(TRESOR_EVENT, on)
  }, [])
  const dmsInbox = usePoll(() => !moduleOn('dms') ? Promise.resolve({ count: 0 }) : api.get<{ count: number }>('/api/dms/inbox-count'), 60000, 'dmoffice:dmsstatus')?.count ?? 0

  useEffect(() => { sessionStorage.removeItem('dmoffice.reloaded') }, [])
  useEffect(() => {
    if (!mobile) { setRailFade({ l: false, r: false }); return }
    const el = railNav.current
    if (!el) return
    railEdges()
    const ro = new ResizeObserver(railEdges)
    ro.observe(el)
    return () => ro.disconnect()
  }, [mobile]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(installDialogBehaviour, [])
  // Dateien aus dem Betriebssystem: Wo kein Bereich sie annimmt, öffnet der Browser sie sonst selbst und verlässt dmOffice.
  // Die Bereiche haben vorher (React, Wurzelelement) entschieden; hier kommt nur an, was keiner angenommen hat.
  useEffect(() => {
    const files = (e: DragEvent) => !!e.dataTransfer?.types.includes('Files')
    const native = (e: DragEvent) => e.target instanceof Element && !!e.target.closest('input, textarea, [contenteditable]:not([contenteditable="false"])')
    const over = (e: DragEvent) => {
      if (e.defaultPrevented || !files(e) || native(e)) return
      e.preventDefault()
      e.dataTransfer!.dropEffect = 'none'
    }
    const drop = (e: DragEvent) => { if (!e.defaultPrevented && files(e) && !(e.target instanceof HTMLInputElement)) e.preventDefault() }
    window.addEventListener('dragover', over)
    window.addEventListener('drop', drop)
    return () => { window.removeEventListener('dragover', over); window.removeEventListener('drop', drop) }
  }, [])
  // Audiothek ausgeschaltet (Verwaltung): nicht unsichtbar weiterspielen
  const audioQueued = useHasQueue()
  useEffect(() => { if (!me.audio && audioQueued) player.clear() }, [me.audio, audioQueued])
  // Tresor geschlossen (423): gemeinsamer Dialog „Tresor öffnen“, danach läuft die Anfrage noch einmal
  const tresor = useTresor()
  useEffect(() => { setUnlockHandler(area => area === 'tresor' ? requestTresorOpen() : Promise.resolve(false)) }, [])
  useEffect(() => {
    const onHash = (e: HashChangeEvent) => {
      const m = currentModule()
      // Den verlassenen Bereich mit seiner letzten Adresse merken - auch, wenn er sie still ersetzt hat (Galerie: Betrachter
      // geschlossen ist wieder #/gallery), sonst öffnete der Weg zurück den Betrachter erneut
      const old = e.oldURL.includes('#') ? '#' + e.oldURL.split('#').slice(1).join('#') : ''
      if (shown.current !== m && old && hashBelongs(old, shown.current)) lastHash.current[shown.current] = old
      shown.current = m
      lastHash.current[m] = location.hash; setModule(m)
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [])

  // Bereiche bleiben erhalten (nur ausgeblendet), ihre letzte Adresse auch: zurück im Bereich ist man, wo man war - gleicher Ordner,
  // gleicher Eintrag, gleiche Scrollposition
  const [visited, setVisited] = useState<Module[]>(() => [module])
  const lastHash = useRef<Partial<Record<Module, string>>>({ [module]: location.hash })
  const shown = useRef(module)
  useEffect(() => {
    setVisited(v => v.includes(module) ? v : [...v, module])
    // Wieder eingeblendet: Kalender, Karten und Raster messen sich neu
    const id = setTimeout(() => window.dispatchEvent(new Event('resize')), 0)
    return () => clearTimeout(id)
  }, [module])
  const go = (m: Module) => { const last = lastHash.current[m]; location.hash = m !== module && last && hashBelongs(last, m) ? last : '#/' + m }
  const mailUnread = status ? Object.entries<any>(status.accounts).reduce((n, [id, a]) => {
    const u = adjustInbox(id, a.unread || 0, status._at, shownUnread.current[id])
    shownUnread.current[id] = u
    return n + (shownInbox(id, u.value) ?? 0)
  }, 0) : 0
  // Benachrichtigung über neue Mail ebenso erst nach dem Vorladen; Konten ohne vorgeladenen Posteingang fehlen noch
  const notifyStatus = useMemo(() => !status?.accounts ? status : {
    ...status, accounts: Object.fromEntries(Object.entries<any>(status.accounts).flatMap(([id, a]) => {
      const u = shownInbox(id, a.unread || 0)
      return u == null ? [] : [[id, { ...a, unread: u }]]
    })),
  }, [status, preloadVersion])
  const unread = mailUnread + (chatStatus?.unread ?? 0)
  const syncing = status ? Object.values<any>(status.accounts).some(a => a.running) : false
  const reachable = useReachable()
  const version = useVersion()
  // Klick auf die Versionsnummer: Version und Build in die Zwischenablage
  const copyVersion = () => {
    const text = `v${version.release}${version.extra.length ? ' + ' + version.extra.join(', ') : ''}${version.build ? ` (${t('Build')} ${version.build})` : ''}`
    copyText(text).then(ok => ok && notify(t('Version kopiert'), text))
  }
  // Ohne Mailkonto kein Mail-Bereich (Konten legt man in den Einstellungen an); solange der Stand fehlt, bleibt er sichtbar
  const hasMail = !status || Object.keys(status.accounts ?? {}).length > 0
  useEffect(() => { if (!hasMail && module === 'mail') go('calendar') }, [hasMail, module]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    document.title = unread > 0 ? `(${unread}) dmOffice` : 'dmOffice'
    // Als App installiert (Edge/Chrome): Zahl auf dem Symbol in der Taskleiste bzw. auf dem Startbildschirm
    const nav = navigator as Navigator & { setAppBadge?: (n?: number) => Promise<void>; clearAppBadge?: () => Promise<void> }
    if (unread > 0) nav.setAppBadge?.(unread).catch(() => {})
    else nav.clearAppBadge?.().catch(() => {})
  }, [unread])

  const chooseTheme = (choice: ThemeChoice) => {
    setTheme(choice)
    storeTheme(choice)
    api.put('/api/settings/general', { theme: choice }).catch(() => {})
  }
  const logout = async () => {
    e2eForget()
    const r = await api.post<{ redirect?: string | null }>('/api/auth/logout').catch(() => null)
    // Reste des Benutzers auf einem geteilten Rechner entfernen: Einstellungen, zuletzt geoeffnete Ordner usw.
    forgetLocalData()
    // Über OIDC angemeldet: auch beim Anbieter abmelden, der leitet danach zur Anmeldeseite zurück
    if (r?.redirect) { location.href = r.redirect; return }
    location.hash = ''
    location.reload()
  }

  // Bereiche in der Leiste links
  const railModules = ordered.filter(m => ((m.key !== 'files' && m.key !== 'gallery' && m.key !== 'dms') || me.files !== false) && (m.key !== 'mail' || hasMail) && (m.key !== 'audio' || !!me.audio) && moduleOn(m.key))
  const modByKey = new Map<string, (typeof modules)[number]>(railModules.map(m => [m.key, m]))
  const navCount = (k: string) => k === 'mail' ? mailUnread : k === 'chat' ? (chatStatus?.unread ?? 0) : k === 'dms' ? dmsInbox : 0
  const sepOn = featureOn('railsep')
  const railEntries = sepOn ? railLayout(prefs.moduleOrder, railModules.map(m => m.key)) : railModules.map(m => m.key)
  // ---- Trenner in der Leiste (Kennung railsep): einfügen, löschen; gespeichert in moduleOrder
  const railOrder = () => fullOrder(prefs.moduleOrder, ordered.map(m => m.key))
  const addSep = (after: string | null) => updatePrefs({ moduleOrder: insertSep(railOrder(), after) })
  const dropSep = (id: string) => updatePrefs({ moduleOrder: removeSep(railOrder(), id) })
  const sepAfter = (k: string) => { const i = railEntries.indexOf(k); return i >= 0 && typeof railEntries[i + 1] === 'string' && isSep(railEntries[i + 1] as string) }
  const sepEl = (id: string) => (
    <div key={id} className={mergeClasses(s.sep, mobile && s.sepMobile)} draggable={!mobile} role="separator"
      style={{ boxShadow: dropAt === id && dragMod && dragMod !== id ? `0 -3px 0 ${tokens.colorBrandStroke1}` : undefined }}
      onContextMenu={ev => railMenu.open(ev, { kind: 'sep', key: id })}
      onDragStart={ev => { setDragMod(id); ev.dataTransfer.effectAllowed = 'move'; ev.dataTransfer.setData('application/x-dmoffice-module', id) }}
      onDragOver={ev => { if (dragMod) { ev.preventDefault(); setDropAt(id) } }}
      onDrop={ev => { ev.preventDefault(); if (dragMod) moveModule(dragMod, id); setDragMod(null); setDropAt(null) }}
      onDragEnd={() => { setDragMod(null); setDropAt(null) }}>
      <div className={mergeClasses(s.sepLine, mobile && s.sepLineMobile)} />
    </div>
  )

  return (
    <div className={s.root}>
      <Notifier status={notifyStatus} />
      <ChatNotifier status={chatStatus} active={module === 'chat'} />
      <SendToChatHost />
      <header className={mergeClasses(s.top, 'dm-titlebar')}>
        <div className={mergeClasses(s.brand, mobile && s.brandMobile)}>
          <img src={brandLogo(prefs.accent)} width={22} height={22} alt="" />
          <span className="dm-brand-name">dmOffice</span>
          {version.release && (
            <Tooltip content={version.build ? `${t('Build')} ${version.build} – ${t('Klick kopiert die Version')}` : t('Klick kopiert die Version')} relationship="description">
              <span className={s.release} role="button" tabIndex={0} onClick={copyVersion}
                onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') copyVersion() }}
                title={version.extra.length ? t('Zusätzlich freigeschaltet: {list}', { list: version.extra.join(', ') }) : undefined}>v{version.release}{version.extra.length ? '+' : ''}</span>
            </Tooltip>
          )}
        </div>
        <div className={s.spacer} />
        <EmergencyBadge onLogout={logout} />
        {/* Demo: das Schild mittig in der Kopfzeile läge über der Speicheranzeige */}
        {!demoMode.on && <StorageInfo mobile={mobile} />}
        <Tooltip content={!reachable ? t('Server nicht erreichbar') : syncing ? t('Wird synchronisiert …') : t('Jetzt synchronisieren')} relationship="label">
          <Button appearance="transparent" className={s.topButton}
            icon={!reachable ? <CloudOff20Regular style={{ color: tokens.colorPaletteRedForeground1 }} /> : syncing ? <Spinner size="extra-tiny" /> : <ArrowSync20Regular />}
            onClick={() => { if (reachable) api.post('/api/mail/sync'); else window.dispatchEvent(new Event(REACHABLE_RECHECK)) }}
            aria-label={!reachable ? t('Server nicht erreichbar') : syncing ? t('Wird synchronisiert …') : t('Jetzt synchronisieren')} />
        </Tooltip>
        <ActivityButton mobile={mobile} className={s.topButton} />
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Tooltip content={t('Darstellung')} relationship="label">
              <Button appearance="transparent" className={s.topButton} icon={<WeatherMoon20Regular />} />
            </Tooltip>
          </MenuTrigger>
          <MenuPopover>
            <MenuList checkedValues={{ theme: [theme] }} onCheckedValueChange={(_, d) => chooseTheme(d.checkedItems[0] as ThemeChoice)}>
              <MenuItemRadio name="theme" value="system">{t('Wie das System')}</MenuItemRadio>
              <MenuItemRadio name="theme" value="light">{t('Hell')}</MenuItemRadio>
              <MenuItemRadio name="theme" value="dark">{t('Dunkel')}</MenuItemRadio>
            </MenuList>
          </MenuPopover>
        </Menu>
        <HelpMenu className={s.topButton} release={version.release} build={version.build} extra={version.extra} />
        <Menu>
          <MenuTrigger disableButtonEnhancement>
            <Button appearance="transparent" className={s.topButton} aria-label={t('Konto')}>
              {/* Version 2: eigenes Profilbild, sonst Anfangsbuchstabe in der Akzentfarbe */}
              <UserAvatar id={me.id} name={me.displayName} login={me.login} avatar={chatStatus?.avatar} accent={prefs.accent} size={28} />
            </Button>
          </MenuTrigger>
          <MenuPopover>
            <MenuList>
              <MenuItem disabled><Text weight="semibold">{me.displayName}</Text> <Text size={200}>({me.login})</Text></MenuItem>
              <MenuDivider />
              <MenuItem icon={<Settings24Regular />} onClick={() => go('settings')}>{t('Einstellungen')}</MenuItem>
              {mobile && me.isAdmin && <MenuItem icon={<ShieldPersonRegular />} onClick={() => go('admin')}>{t('Verwaltung')}</MenuItem>}
              {tresor?.open && featureOn('vault') && <MenuItem icon={<LockClosed20Regular />} onClick={() => closeTresor().then(() => notify(t('Tresor geschlossen'))).catch(() => {})}>{t('Tresor schließen')}</MenuItem>}
              <MenuItem icon={<SignOut20Regular />} onClick={logout}>{t('Abmelden')}</MenuItem>
            </MenuList>
          </MenuPopover>
        </Menu>
      </header>
      <div className={mergeClasses(s.body, mobile && s.bodyMobile)}>
        {/* Audiothek: laufender Titel - auf dem Handy über der Leiste mit den Bereichen */}
        {mobile && me.audio && <PlayerBar mobile />}
        <nav ref={railNav} className={mergeClasses(s.rail, mobile && s.railMobile)} onScroll={mobile ? railEdges : undefined}
          // Trenner (railsep): Rechtsklick auf freie Fläche; was ein Eintrag (Bereich, Trenner, Lesezeichen) schon behandelt hat, nicht
          onContextMenu={sepOn ? (ev => { if (!ev.defaultPrevented) railMenu.open(ev, { kind: 'end' }) }) : undefined}
          style={mobile && (railFade.l || railFade.r) ? (m => ({ maskImage: m, WebkitMaskImage: m }))(
            `linear-gradient(to right, ${railFade.l ? 'transparent 0, #000 32px' : '#000 0'}, ${railFade.r ? '#000 calc(100% - 32px), transparent 100%' : '#000 100%'})`) : undefined}>
          {/* Bereiche: auf dem PC nur so viele, wie Platz haben - Verwaltung und Einstellungen bleiben unten stehen */}
          {(() => {
            const toKeys = (e: string) => [e]
            // Mit Gruppen zählen die Einträge der Leiste (ein Eintrag je Gruppe), sonst die Bereiche
            const entries = railEntries
            // Trenner (railsep) brauchen weniger Höhe als ein Bereich: nach Pixeln aufteilen
            const fit = sepOn && !mobile ? fitRail(entries as string[], railPx) : null
            const overflow = fit ? fit.rest.length > 0 : !mobile && entries.length > railSlots
            const visible = fit ? fit.visible : overflow ? entries.slice(0, railSlots - 1) : entries
            const rest = fit ? fit.rest : overflow ? entries.slice(railSlots - 1) : []
            const count = navCount
            const restKeys = rest.flatMap(toKeys)
            const restCount = restKeys.reduce((n, k) => n + count(k), 0)
            const restActive = restKeys.includes(module)
            const railBtn = (m: (typeof modules)[number]) => (
              <>
                <Button appearance="subtle" className={mergeClasses(s.railButton, mobile && s.railButtonMobile, module === m.key && s.railActive, module === m.key && mobile && s.railActiveMobile, s.tinted)} style={{ color: navTint(m.key) }}
                  icon={<m.icon />} onClick={() => go(m.key)} aria-current={module === m.key ? 'page' : undefined} aria-label={m.label} />
                {m.key === 'mail' && mailUnread > 0 && <CounterBadge className={s.badge} count={mailUnread} size="small" overflowCount={999} />}
                {m.key === 'chat' && (chatStatus?.unread ?? 0) > 0 && <CounterBadge className={s.badge} count={chatStatus!.unread} size="small" overflowCount={999} />}
                {m.key === 'dms' && dmsInbox > 0 && <CounterBadge className={s.badge} count={dmsInbox} size="small" overflowCount={999} />}
              </>
            )
            const buttons = visible.map(e => {
              if (sepOn && isSep(e)) return sepEl(e)
              const m = modByKey.get(e)!
              const wrap = null
              return (
            <Tooltip key={m.key} content={m.label} relationship="label" positioning={mobile ? 'above' : 'after'}>
              {/* Zaehler neben dem Knopf, nicht darin - sonst wird der Knopf breiter als die Leiste */}
              {wrap ?? <div style={{ position: 'relative', boxShadow: dropAt === m.key && dragMod && dragMod !== m.key ? `0 -3px 0 ${tokens.colorBrandStroke1}` : undefined }}
                    draggable={!mobile}
                    onDragStart={ev => { if (!(ev.shiftKey || ev.ctrlKey)) { ev.preventDefault(); return } setDragMod(m.key); ev.dataTransfer.effectAllowed = 'move'; ev.dataTransfer.setData('application/x-dmoffice-module', m.key) }}
                    onContextMenu={sepOn || colorsOn ? (ev => railMenu.open(ev, { kind: sepOn ? 'mod' : 'color', key: m.key })) : undefined}
                    onDragOver={ev => { if (dragMod) { ev.preventDefault(); setDropAt(m.key) } }}
                    onDrop={ev => { ev.preventDefault(); if (dragMod) moveModule(dragMod, m.key); setDragMod(null); setDropAt(null) }}
                    onDragEnd={() => { setDragMod(null); setDropAt(null) }}>{railBtn(m)}</div>}
            </Tooltip>
              )
            })
            const restItem = (k: string) => {
              const m = modByKey.get(k)!
              return <MenuItem key={k} icon={<span style={{ display: 'inline-flex', color: navTint(k) }}><m.icon /></span>} onClick={() => go(m.key)} secondaryContent={count(k) > 0 ? String(count(k)) : undefined}
                style={k === module ? { fontWeight: 600 } : undefined}>{m.label}</MenuItem>
            }
            const restEntry = restItem
            const more = rest.length > 0 && (
              <Menu positioning="after-bottom">
                <MenuTrigger disableButtonEnhancement>
                  <Tooltip content={t('Weitere Bereiche')} relationship="label" positioning="after">
                    <div style={{ position: 'relative' }}>
                      <Button appearance="subtle" className={mergeClasses(s.railButton, restActive && s.railActive, s.tinted)} icon={<MoreHorizontal24Regular />}
                        aria-label={t('Weitere Bereiche')} aria-current={restActive ? 'page' : undefined} />
                      {restCount > 0 && <CounterBadge className={s.badge} count={restCount} size="small" overflowCount={999} />}
                    </div>
                  </Tooltip>
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    {rest.map(e => typeof e === 'string' && isSep(e) ? <MenuDivider key={e} /> : restEntry(e))}
                  </MenuList>
                </MenuPopover>
              </Menu>
            )
            if (mobile) return buttons
            // Handy: Lesezeichen und Verwaltung passen nicht in die Leiste unten - Verwaltung steht im Kontomenue
            return <div ref={railTop} className={s.railTop}
              // Trenner (railsep): auf freie Fläche gezogen = ans Ende
              onDragOver={sepOn ? (ev => { if (dragMod && !ev.defaultPrevented) { ev.preventDefault(); setDropAt(null) } }) : undefined}
              onDrop={sepOn ? (ev => { if (dragMod && !ev.defaultPrevented) { ev.preventDefault(); moveModule(dragMod, null); setDragMod(null) } }) : undefined}>
              {buttons}{more}{!overflow && <Bookmarks />}</div>
          })()}
          {me.isAdmin && !mobile && (
            <Tooltip content={t('Verwaltung')} relationship="label" positioning="after">
              <Button appearance="subtle" className={mergeClasses(s.railButton, module === 'admin' && s.railActive, s.tinted)} style={{ color: navTint('admin') }}
                onContextMenu={colorsOn ? (ev => railMenu.open(ev, { kind: 'color', key: 'admin' })) : undefined} icon={<AdminIcon />} onClick={() => go('admin')} aria-label={t('Verwaltung')} aria-current={module === 'admin' ? 'page' : undefined} />
            </Tooltip>
          )}
          <Tooltip content={t('Einstellungen')} relationship="label" positioning={mobile ? 'above' : 'after'}>
            <Button appearance="subtle" className={mergeClasses(s.railButton, mobile && s.railButtonMobile, module === 'settings' && s.railActive, module === 'settings' && mobile && s.railActiveMobile, s.tinted)} style={{ color: navTint('settings') }}
              onContextMenu={colorsOn ? (ev => railMenu.open(ev, { kind: 'color', key: 'settings' })) : undefined} icon={<SettingsIcon />} onClick={() => go('settings')} aria-label={t('Einstellungen')} aria-current={module === 'settings' ? 'page' : undefined} />
          </Tooltip>
          {!mobile && <div style={{ height: 8 }} />}
        </nav>
        {/* Trenner in der Leiste (railsep) */}
        {(sepOn || colorsOn) && railMenu.ctx && <ContextMenu at={railMenu.ctx.at} onClose={railMenu.close}>
          {/* [feature:navcolors] */}
          {/* Farbe des Symbols (navcolors): „Standard“ = Akzentfarbe */}
          {colorsOn && (railMenu.ctx.data.kind === 'mod' || railMenu.ctx.data.kind === 'color') && (k => (
            <ColorMenu colors={navPalette()} current={prefs.navColors?.[k]} none noneLabel={t('Standard')}
              onPick={c => { updatePrefs({ navColors: setNavColor(prefs.navColors, k, c) }); railMenu.close() }} />
          ))(railMenu.ctx.data.key!)}
          {/* [/feature:navcolors] */}
          {railMenu.ctx.data.kind === 'sep'
            ? <MenuItem icon={<Delete20Regular />} onClick={() => dropSep(railMenu.ctx!.data.key!)}>{t('Trenner löschen')}</MenuItem>
            : railMenu.ctx.data.kind === 'mod'
              ? <MenuItem icon={<LineHorizontal120Regular />} disabled={sepAfter(railMenu.ctx.data.key!)} onClick={() => addSep(railMenu.ctx!.data.key!)}>{t('Trenner darunter einfügen')}</MenuItem>
              : railMenu.ctx.data.kind === 'end'
                ? <MenuItem icon={<LineHorizontal120Regular />} onClick={() => addSep(null)}>{t('Trenner einfügen')}</MenuItem>
                : null}
        </ContextMenu>}
        <ZoneHint />
        <main className={mergeClasses(s.main, mobile && s.mainMobile)}>
          <h1 className={s.pageName}>{module === 'settings' ? t('Einstellungen') : module === 'admin' ? t('Verwaltung') : modules.find(m => m.key === module)?.label}</h1>
          {/* Jeder geöffnete Bereich bleibt; der aktive ist sichtbar, die anderen ausgeblendet (Stand bleibt erhalten) */}
          {([
            ['dashboard', () => <DashboardView />],
            ['mail', () => <MailView status={status} />],
            ['chat', () => <ChatView />],
            ['calendar', () => <CalendarView />],
            ['contacts', () => <ContactsView />],
            ['tasks', () => moduleOn('tasks') && <TasksView />],
            ['notes', () => moduleOn('notes') && <NotesView />],
            // [feature:journal]
            ['journal', () => moduleOn('journal') && <JournalView />],
            // [/feature:journal]
            ['gallery', () => me.files === false ? <NotAvailable text={t('Die Dateiablage ist für dein Konto nicht aktiviert.')} /> : moduleOn('gallery') && <FilesView key="gallery" gallery />],
            ['files', () => me.files === false ? <NotAvailable text={t('Die Dateiablage ist für dein Konto nicht aktiviert.')} /> : <FilesView key="files" />],
            ['audio', () => me.audio ? <AudioView /> : <NotAvailable text={t('Die Audiothek ist für dein Konto nicht aktiviert.')} />],
            ['dms', () => me.files === false ? <NotAvailable text={t('Die Dateiablage ist für dein Konto nicht aktiviert.')} /> : moduleOn('dms') && <DmsView />],
            ['settings', () => <SettingsView />],
            ['admin', () => me.isAdmin ? <AdminView /> : <NotAvailable text={t('Die Verwaltung ist nur für Admins.')} />],
            ...[...pluginComponents].map(([id, View]) => [id, () => <View />]),
          ] as [Module, () => React.ReactNode][]).filter(([k]) => visited.includes(k)).map(([k, render]) => (
            <div key={k} style={{ display: module === k ? 'contents' : 'none' }}>
              <Boundary resetKey={module}>
                <Suspense fallback={<Spinner className={s.loading} />}>{render()}</Suspense>
              </Boundary>
            </div>
          ))}
        </main>
      </div>
      {!mobile && me.audio && <PlayerBar mobile={false} />}
    </div>
  )
}
