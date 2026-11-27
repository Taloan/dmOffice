import { DmsMailMenuItem, DmsMailRibbon } from '../dms/MailToDms'
import {
  Button, Menu, MenuDivider, MenuGroup, MenuGroupHeader, MenuItem, MenuItemCheckbox, MenuItemRadio, MenuList, MenuPopover, MenuSplitGroup, MenuTrigger, SearchBox, Spinner, Text,
  Tooltip, makeStyles, mergeClasses, tokens, Checkbox, Tab, TabList,
} from '@fluentui/react-components'
import {
  MailAdd20Regular, Delete20Regular, Archive20Regular, ShieldError20Regular, ShieldCheckmark20Regular, FolderArrowRight20Regular,
  Tag20Regular, MailRead20Regular, MailUnread20Regular, Flag20Regular, Flag20Filled, ArrowReply20Regular, ArrowReplyAll20Regular, ArrowForward20Regular,
  Filter20Regular, ArrowSort20Regular, ArrowSwap20Regular, ArrowSync20Regular,
  MoreHorizontal20Regular, ArrowLeft20Regular, ArrowUndo20Regular, Clock20Regular, CalendarAdd20Regular, Attach20Regular, Note20Regular, Print20Regular, Code20Regular, WeatherMoon20Regular, FolderAdd20Regular,
  TextLineSpacing20Regular, PanelLeft20Regular, ChevronRight20Regular, ChevronLeft20Regular, Pin20Regular, FolderSearch20Regular, DocumentSearch20Regular, ArrowExportLtr20Regular, PanelRight20Regular, PanelRightContract20Regular, TextDescription20Regular,
  PersonCircle20Regular, PersonMail20Regular, Settings20Regular, Navigation20Regular, CalendarLtr20Regular,
  Open20Regular, Filter20Filled,
  History16Regular, Dismiss12Regular, Delete16Regular,
  Checkmark20Regular, // [feature:mailtocolumn]
  Chat20Regular, // [feature:mailthreads]
} from '@fluentui/react-icons'
import { ruleFromMail } from './ruleFromMail'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Account, api, composeAccount, defaultAccount, featureOn, Folder, MailItem, Message, q } from '../api'
import { prefetch } from './messageCache'
import { fetchFolders, focusKnown, listInFlight, listKeyOf, listUrlOf, lists as listCache, rememberList, settledRound, shownFolder, usePreloadVersion } from './preload'
import { useApp } from '../App'
import { MobileRibbonRow, Ribbon, RibbonButton, RibbonDisplayMenu, RibbonDivider, RibbonFrame, RibbonTabs } from '../shell/Ribbon'
import { useBackClose, useIsMobile } from '../shell/mobile'
import { TodoBar } from './TodoBar'
import { CategoryMenuList, CategoryDot, useCategories } from './Categories'
import { SnoozeMenu, snoozeOptions } from './SnoozeMenu'
import { ExpiryMenuItems, expiryOf, useExpiries } from './ExpiryMenu'
import { fmtDate } from '../shell/hooks'
import { usePrefs } from '../prefs'
import { useDebounced } from '../shell/hooks'
import { createMailFolder, FolderPane, folderLabel } from './FolderPane'
import { FollowUpItems, FollowUpRibbon } from './FollowUpMenu'
import { SEARCH_FOLDERS_CHANGED, useVirtualName } from './VirtualFolders'
import { MessageList } from './MessageList'
import { checkTrust, TrustCheck, TrustOffer } from './TrustOffer'
import { markReadDelay, ReadingPane, saveMailSource } from './ReadingPane'
import { useIsDark } from '../theme'
import { ConversationPane } from './Conversation' // [feature:mailthreads]
import { AiReplyMenu, aiHtml, useAiReply } from './AiReply' // [feature:mailaireply]
import { Compose, ComposeHandle, ComposeInit, QueuedSend, forwardAsAttachmentOf, forwardOf, replyOf,
} from './Compose'
import { OutboxView } from './Outbox'
import { NewAliasDialog } from './AliasDialog'
import { AdoptAliasDialog, CATCHALL_CHANGED, catchallTitle, setAliasBlocked, useCatchall } from './CatchallFolders'
import { openComposeWindow, openMailWindow } from './MailWindow'
import { COMPOSE_EVENT, takeCompose } from './handoff'
import { EventDialog, EventDraft, guessStart } from '../calendar/EventDialog'
import type { Collection } from '../api'
import { locale, t, tError } from '../i18n'
import { adjustCount, applyPatch, begin, type CountChange, isPending, mailKey, type MailPatch, overlay, pendingNow, type PendingOp, type Shown, usePendingVersion } from './pending'
import { usePaneHeight, usePaneWidth } from '../shell/Splitter'
import { hasMore, LIST_MAX, listPlan, listRows, mergeRows } from './listPaging'
import { askConfirm } from '../shell/context'
import { ContextMenu } from '../shell/context' // [feature:mailtocolumn]
import { undoAllOn } from '../shell/undo'

const useStyles = makeStyles({
  smartBar: { display: 'flex', flexWrap: 'nowrap', gap: '4px', padding: '2px 8px 4px', overflowX: 'auto', flexShrink: 0, scrollbarWidth: 'none', '& > *': { flexShrink: 0, whiteSpace: 'nowrap' } },
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0, position: 'relative' },
  folders: { width: '250px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', minHeight: 0 },
  content: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  toolbar: { borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, padding: '4px 8px', flexWrap: 'wrap', gap: '2px' },
  panes: { flex: 1, display: 'flex', minHeight: 0, minWidth: 0 },
  panesBottom: { flexDirection: 'column' },
  listBottom: { width: 'auto', height: '42%', borderRight: 'none', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  back: { alignSelf: 'flex-start', margin: '6px 12px 0' },
  list: { width: '380px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', minHeight: 0 },
  listHead: { padding: '8px 12px 4px', display: 'flex', flexDirection: 'column', gap: '6px' },
  history: {
    position: 'absolute', top: 'calc(100% + 4px)', left: 0, right: 0, zIndex: 20, padding: '4px 0', borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke1}`, boxShadow: tokens.shadow16, maxHeight: '360px', overflowY: 'auto',
  },
  historyHead: { display: 'block', padding: '4px 12px', color: tokens.colorNeutralForeground3 },
  historyRow: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 8px 4px 12px', cursor: 'pointer', minHeight: '28px',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  listTitle: { display: 'flex', alignItems: 'center', justifyContent: 'space-between' },
  reading: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  empty: { margin: 'auto', color: tokens.colorNeutralForeground3, textAlign: 'center' },
  // Schmale Fenster wie in Outlook: Ordner und Aufgabenleiste schweben ueber dem Inhalt
  flyout: {
    position: 'absolute', top: 0, bottom: 0, zIndex: 20, display: 'flex', flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow28,
  },
  backdrop: { position: 'absolute', inset: 0, zIndex: 19, backgroundColor: 'rgba(0,0,0,.12)' },
  tabsRow: { display: 'flex', alignItems: 'center', minWidth: 0 },
  newButton: { marginRight: '8px' },
})

export interface Selection { account: string; folder: string }

/** Ordner „Markiert“ (alle gekennzeichneten Mails eines Kontos) */
const FLAGGED = '__flagged'

/** Breite eines Elements, laufend - fuer das Layout je nach Platz. */
function useWidth(): [number, (el: HTMLElement | null) => void] {
  const [el, setEl] = useState<HTMLElement | null>(null)
  const [w, setW] = useState(() => window.innerWidth)
  useEffect(() => {
    if (!el) return
    const ro = new ResizeObserver(e => setW(e[0].contentRect.width))
    ro.observe(el)
    return () => ro.disconnect()
  }, [el])
  return [w, setEl]
}

export default function MailView({ status }: { status: any }) {
  const s = useStyles()
  // Verstellbare Breiten: Ordnerleiste, Liste (Lesebereich rechts), Aufgabenleiste
  const folderW = usePaneWidth('mail.folders', 250)
  const listW = usePaneWidth('mail.list', 380, 260, 900)
  // Lesebereich unten: Höhe der Liste ziehbar und gemerkt (Kennung mailpanesize)
  const listH = usePaneHeight('mail.listHeight')
  const paneSize = featureOn('mailpanesize')
  const todoW = usePaneWidth('mail.todo', 290)
  // Stufen wie Outlook: erst schwebt die Aufgabenleiste, dann zeigt die offene Mail
  // die ganze Breite, zuletzt klappt der Ordnerbereich in ein Menue
  const [width, rootRef] = useWidth()
  // Handy: einspaltig - Liste in voller Breite, die Mail im Vollbild, Aktionen im Menue "Mehr"
  const mobile = useIsMobile()
  const foldersCollapsed = width < 900 || mobile
  const stacked = width < 1100 || mobile
  const todoFloating = width < 1500
  const [foldersOpen, setFoldersOpen] = useState(false)
  const [todoOpen, setTodoOpen] = useState(false)
  const { prefs, update } = usePrefs()
  const pane = stacked ? 'off' : prefs.readingPane ?? 'right'
  const { fail, notify, undoable } = useApp()
  const [accounts, setAccounts] = useState<Account[] | null>(null)
  // Ordner wie vom Server (at = Beginn der Anfrage); angezeigt mit den Änderungen, die noch unterwegs sind (pending.ts)
  const [serverFolders, setServerFolders] = useState<{ at: number; list: Record<string, Folder[]> }>({ at: 0, list: {} })
  const pendingVersion = usePendingVersion()
  const shownCounts = useRef(new Map<string, Shown>())
  const folders = useMemo(() => {
    const out: Record<string, Folder[]> = {}
    for (const [acc, fs] of Object.entries(serverFolders.list)) out[acc] = fs.map(f => {
      const m = (c: CountChange) => c.account === acc && c.folder === f.path && !c.tab
      const k = acc + '|' + f.path
      const u = adjustCount(m, 'unread', f.unread, serverFolders.at, shownCounts.current.get(k + '|u'))
      const n = adjustCount(m, 'total', f.total, serverFolders.at, shownCounts.current.get(k + '|t'))
      shownCounts.current.set(k + '|u', u)
      shownCounts.current.set(k + '|t', n)
      return u.value === f.unread && n.value === f.total ? f : { ...f, unread: u.value, total: n.value }
    })
    return out
  }, [serverFolders, pendingVersion])
  // Ordnerleiste: eine Zahl steigt erst, wenn die Liste des Ordners samt neuer Mails vorgeladen ist (preload.ts)
  const preloadVersion = usePreloadVersion()
  const paneFolders = useMemo(() => Object.fromEntries(Object.entries(folders).map(([acc, fs]) => [acc, fs.map(f => shownFolder(acc, f))])),
    [folders, preloadVersion])
  const [sel, setSel] = useState<Selection | null>(() => {
    const parts = location.hash.replace(/^#\/?/, '').split('/')
    if (parts[0] === 'mail' && parts.length >= 3 && parts[1] !== 'open' && parts[1] !== 'flagged')
      return { account: decodeURIComponent(parts[1]), folder: decodeURIComponent(parts.slice(2).join('/')) }
    // Zurueck aus einem anderen Bereich (Aufgaben, Kalender ...): der zuletzt offene Ordner
    try { const last = JSON.parse(sessionStorage.getItem('dmoffice.mail.sel') || 'null'); if (last?.account && last?.folder) return last } catch { /* egal */ }
    return null
  })
  useEffect(() => { if (sel) try { sessionStorage.setItem('dmoffice.mail.sel', JSON.stringify(sel)) } catch { /* egal */ } }, [sel])
  const [items, setItems] = useState<MailItem[]>([])
  // Speichermodus "Schlank": Suche laeuft noch beim Anbieter
  const [remoteSearch, setRemoteSearch] = useState(false)
  // Ruecklink aus einer Notiz: #/mail/open/<konto>/<message-id>
  // Aus der E-Mail-Nachverfolgung: #/mail/flagged/<konto>/<message-id> - im Ordner "Markiert" oeffnen
  const [opening, setOpening] = useState<(MailItem & { openIn: string }) | null>(null)
  useEffect(() => {
    const check = async () => {
      const m = location.hash.match(/^#\/mail\/(open|flagged)\/([^/]+)\/(.+)$/)
      if (!m) return
      history.replaceState(null, '', '#/mail')
      try {
        const row = await api.get<MailItem>(`/api/mail/${m[2]}/find?` + q({ messageId: decodeURIComponent(m[3]) }))
        const openIn = m[1] === 'flagged' ? '__flagged' : row.folder
        autoSelect.current = false
        setSearch(''); setSimilarTo(null)
        setSel({ account: row.account, folder: openIn })
        setOpening({ ...row, openIn })
      } catch (e) { fail(e) }
    }
    check()
    window.addEventListener('hashchange', check)
    return () => window.removeEventListener('hashchange', check)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  // Gibt es beim Server noch mehr als geliefert (nach seiner Antwort, nicht nach den angezeigten Zeilen) und läuft gerade das Nachladen?
  const [moreRows, setMoreRows] = useState(false)
  const [loadingMore, setLoadingMore] = useState(false)
  const [page, setPage] = useState(0)
  const [filter, setFilter] = useState<string>('')
  // Prio-Posteingang: Reiter Relevant/Sonstige im Posteingang (ohne eigenen Filter)
  const [focusTab, setFocusTab] = useState<'focused' | 'other'>('focused')
  // Zahlen der Reiter wie vom Server; angezeigt mit den Änderungen, die noch unterwegs sind
  type FocusInfo = { enabled: boolean; focused: number; other: number; focusedTotal?: number; otherTotal?: number }
  const [serverFocus, setServerFocus] = useState<{ at: number; account: string; folder: string; info: FocusInfo } | null>(null)
  const shownFocus = useRef(new Map<string, Shown>())
  const focusInfo = useMemo((): FocusInfo | null => {
    if (!serverFocus) return null
    const { at, account, folder, info } = serverFocus
    const adj = <V extends number | undefined>(tab: 'focused' | 'other', field: 'unread' | 'total', v: V): V => {
      if (v == null) return v
      const k = `${account}|${folder}|${tab}|${field}`
      const r = adjustCount(c => c.account === account && c.folder === folder && c.tab === tab, field, v, at, shownFocus.current.get(k))
      shownFocus.current.set(k, r)
      return r.value as V
    }
    return { ...info, focused: adj('focused', 'unread', info.focused), other: adj('other', 'unread', info.other),
      focusedTotal: adj('focused', 'total', info.focusedTotal), otherTotal: adj('other', 'total', info.otherTotal) }
  }, [serverFocus, pendingVersion])
  const [search, setSearch] = useState('')
  const searchSort = prefs.searchSort ?? (prefs.searchRelevance ? 'relevance' : 'newest')
  // Reihenfolge der Mailliste eines Ordners (der Server sortiert, damit auch lange Ordner stimmen)
  const listSort = prefs.listSort ?? 'newest'
  // [feature:mailthreads]
  // Als Unterhaltungen: eine Zeile je Unterhaltung (nur bei Sortierung nach Datum); aufgeklappte Unterhaltungen mit ihren Mails
  const threadsOn = featureOn('mailthreads') && !!prefs.mailThreads && (listSort === 'newest' || listSort === 'oldest')
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set())
  const [members, setMembers] = useState<Record<string, MailItem[]>>({})
  // [feature:mailthreads:else]
  // const threadsOn = false
  // [/feature:mailthreads]
  // [feature:mailtocolumn]
  // Spalte „An“: Empfänger, wenn er von der Kontoadresse abweicht - der Catchall-Alias zuerst
  const toColumn = featureOn('mailtocolumn') && (prefs.mailToColumn ?? false)
  const [toMenu, setToMenu] = useState<{ x: number; y: number } | null>(null)
  const toOf = (i: MailItem): string | undefined => {
    if (i.alias) return i.alias
    const own = accounts?.find(a => a.id === i.account)?.email?.toLowerCase()
    const to = i.to ?? []
    if (!to.length || (own && to.some(a => a.address?.toLowerCase() === own))) return undefined
    return to[0].address
  }
  // [/feature:mailtocolumn]
  const [allAccounts, setAllAccounts] = useState(false)
  const [selected, setSelected] = useState<number[]>([])
  const [message, setMessage] = useState<Message | null>(null)
  const [compose, setComposeState] = useState<{ id: number; init: ComposeInit } | null>(null)
  // Reiter „Nachricht“ beim Schreiben (Menüband); Hooks vor jeder frühen Rückgabe
  const [composeTab, setComposeTab] = useState<'start' | 'message' | 'view'>('message')
  useEffect(() => { setComposeTab('message') }, [compose?.id])
  const [ribbonSlot, setRibbonSlot] = useState<HTMLDivElement | null>(null)
  // Dunkelmodus: Mail umgefärbt oder in Original-Farben (Menüband, Ansicht)
  const dark = useIsDark()
  const [invert, setInvert] = useState(true)
  // Fortlaufende ID je Verfassen-Auftrag, damit Compose neu startet; dirty meldet Compose selbst
  const composeSeq = useRef(0)
  const composeDirty = useRef(false)
  const composeRef = useRef<ComposeHandle>(null)
  // Wie in Outlook: woanders hinklicken sichert die angefangene Mail als Entwurf und schliesst sie
  const leaveCompose = async () => {
    if (!composeRef.current) return
    const saved = await composeRef.current.flush()
    composeDirty.current = false
    setComposeState(null)
    if (saved) notify(t('Als Entwurf gespeichert'))
  }
  // Gerade gesendet: noch ein paar Sekunden zurueckrufbar (auch aus einem eigenen Verfassen-Fenster)
  const [undo, setUndo] = useState<QueuedSend | null>(null)
  // Nach „Kein Junk“ einer einzelnen Mail: Angebot „Diesem Absender immer vertrauen?“ (vorher vom Server geprüft)
  const [trustOffer, setTrustOffer] = useState<{ check: TrustCheck; lead?: string; onTrusted?: () => void } | null>(null)
  // Gehört zur Mail im gewählten Ordner: beim Wechsel des Ordners schließen
  useEffect(() => setTrustOffer(null), [sel?.account, sel?.folder])
  const [, tick] = useState(0)
  useEffect(() => { (window as any).__dmUndo = (q: QueuedSend) => setUndo(q); return () => { delete (window as any).__dmUndo } }, [])
  useEffect(() => {
    if (!undo) return
    const left = new Date(undo.sendAt).getTime() - Date.now()
    if (left <= 0) { setUndo(null); return }
    const timer = setTimeout(() => tick(n => n + 1), Math.min(1000, left))
    return () => clearTimeout(timer)
  })
  const recall = async () => {
    const q = undo
    if (!q) return
    setUndo(null)
    try {
      await api.post(`/api/mail/queue/${q.id}/cancel`)
      if (compose && composeRef.current) await leaveCompose()
      setComposeState({ id: ++composeSeq.current, init: q.snapshot })
      composeDirty.current = true
      notify(t('Zurückgerufen'))
    } catch (e) { fail(e) }
  }
  const popOutCompose = () => {
    const snap = composeRef.current?.snapshot()
    if (!snap) return
    if (!openComposeWindow(snap)) { notify(t('Fenster blockiert'), t('Erlaube Popups für diese Seite.'), 'warning'); return }
    composeDirty.current = false
    setComposeState(null)
  }
  const setCompose = async (init: ComposeInit | null) => {
    if (init && compose && composeDirty.current && !await askConfirm('', t('Aktuellen Entwurf verwerfen?'))) return
    composeDirty.current = false
    setComposeState(init ? { id: ++composeSeq.current, init } : null)
  }
  const query = useDebounced(search, 350)
  // „Ähnliche Mails“ (Kennung similarmail): Trefferliste zu dieser Mail statt einer Suche nach Wörtern
  const [similarTo, setSimilarTo] = useState<MailItem | null>(null)
  const searching = !!query || !!similarTo
  const findSimilar = (item: MailItem) => { setSearch(''); setSimilarTo(item) }
  const similarLabel = similarTo ? t('Ähnlich wie: {subject}', { subject: similarTo.subject || t('(ohne Betreff)') }) : ''
  const similarLevel = (n?: number | null) => n === 1 ? t('Sehr ähnlich') : n === 2 ? t('Ähnlich') : t('Entfernt ähnlich')
  // Suchverlauf (Kennung mailsearchhist): ein Begriff, der 2 Sekunden stehen bleibt, kommt in den Verlauf (höchstens 15)
  const histOn = featureOn('mailsearchhist') && !prefs.mailSearchHistoryOff
  const searchHist = histOn ? prefs.mailSearchHistory ?? [] : []
  const [histOpen, setHistOpen] = useState(false)
  useEffect(() => {
    const q = query.trim()
    if (!histOn || q.length < 2) return
    const h = setTimeout(() => {
      const list = prefs.mailSearchHistory ?? []
      if (list[0] === q) return
      update({ mailSearchHistory: [q, ...list.filter(x => x !== q)].slice(0, 15) })
    }, 2000)
    return () => clearTimeout(h)
  }, [query, histOn]) // eslint-disable-line react-hooks/exhaustive-deps
  const version = status?.version
  // Wiedervorlage: wann welche Mail zurueckkommt
  const [snoozes, setSnoozes] = useState<{ account: string; uid: number; messageId?: string; until: string }[]>([])
  useEffect(() => { api.get('/api/mail/snoozes').then(setSnoozes).catch(() => {}) }, [version])
  // Entfernen nach Zeit: wann welche Mail in den Papierkorb kommt oder vernichtet wird
  const expiries = useExpiries(version)
  const listRef = useRef(items)
  listRef.current = items

  // Konten und Ordner
  const loadFolders = useCallback(async (list: Account[]) => {
    const at = pendingNow()
    // Über das Vorladen (Kennung mailprefetch): gleichzeitige Anfragen des Seitenmenüs teilen sich die Antwort
    const entries = await fetchFolders(list)
    // Eine ältere Antwort, die nach einer neueren ankommt, nicht übernehmen
    setServerFolders(prev => prev.at > at ? prev : { at, list: Object.fromEntries(entries) })
  }, [])

  useEffect(() => {
    const load = () => api.get<Account[]>('/api/mail/accounts').then(list => {
      setAccounts(list)
      loadFolders(list)
    }).catch(fail)
    load()
    // In den Einstellungen angelegte, geänderte oder entfernte Postfächer ohne Neuladen der Seite
    window.addEventListener('dmoffice:mailaccounts', load)
    return () => window.removeEventListener('dmoffice:mailaccounts', load)
  }, [fail, loadFolders])

  useEffect(() => { if (accounts) loadFolders(accounts) }, [version, accounts, loadFolders])
  // Einstellung „Als gelesen markieren“ schon holen: die erste Mail soll nicht darauf warten
  useEffect(() => { markReadDelay() }, [])

  // Aus einem anderen Bereich übergebener Entwurf (Belege aus Finanzen)
  // Auch, wenn der Mailbereich schon offen war: beim Wechsel dorthin nachsehen
  useEffect(() => {
    if (!accounts?.length) return
    const check = () => {
      const handed = takeCompose()
      if (handed) setCompose({ ...handed, account: accounts.some(a => a.id === handed.account) ? handed.account : defaultAccount(accounts)!.id })
    }
    check()
    window.addEventListener('hashchange', check)
    window.addEventListener(COMPOSE_EVENT, check)
    return () => { window.removeEventListener('hashchange', check); window.removeEventListener(COMPOSE_EVENT, check) }
  }, [accounts])

  // Aus den Kontakten: "E-Mail an ..."
  useEffect(() => {
    const to = sessionStorage.getItem('dmoffice.compose.to')
    if (!to || !accounts?.length) return
    sessionStorage.removeItem('dmoffice.compose.to')
    setCompose({ account: defaultAccount(accounts)!.id, to: to.split('\n').filter(Boolean), cc: [], bcc: [], subject: '', html: '' })
  }, [accounts])

  // Ohne Auswahl: Posteingang des Standard-Mailkontos
  useEffect(() => {
    if (sel || !accounts?.length) return
    const start = defaultAccount(accounts)!.id
    const inbox = folders[start]?.find(f => f.special === 'inbox')
    if (inbox) { autoSelect.current = true; setSel({ account: start, folder: inbox.path }) }
  }, [accounts, folders, sel])

  // [feature:mailaccountoff]
  // Konto deaktiviert (oder entfernt), während seine Ordner offen sind: zurück zum Posteingang des Standard-Mailkontos
  useEffect(() => {
    if (!accounts?.length || !sel || accounts.some(a => a.id === sel.account)) return
    setSel(null); setSelected([]); setMessage(null)
  }, [accounts]) // eslint-disable-line react-hooks/exhaustive-deps
  // [/feature:mailaccountoff]

  // Beim Oeffnen eines Ordners eine Mail waehlen (Einstellungen -> Allgemein)
  const autoSelect = useRef(true)
  const [openOnFolder, setOpenOnFolder] = useState<'none' | 'first' | 'unread' | 'last'>('last')
  useEffect(() => { api.get('/api/settings/').then(r => r.openOnFolder && setOpenOnFolder(r.openOnFolder)).catch(() => {}) }, [])
  const lastKey = (s: Selection) => `dmoffice.mail.last.${s.account}|${s.folder}`
  // Zusammengesetzte Ordner (Alle Posteingaenge, Suchordner, Kategorien): Mails aus mehreren Konten - ueber die id merken
  const mixedSel = !!sel && (sel.folder.startsWith('sf:') || sel.folder.startsWith('cat:') || sel.folder === '__unified'
    || sel.folder.startsWith('ca:') // [feature:catchalldomains]
  )
  useEffect(() => {
    if (!autoSelect.current || !sel || searching || loading) return
    // Liste noch leer (zusammengesetzte Ordner laden erst nach dem Wechsel): auf die Mails warten, nicht verbrauchen
    if (!items.length) return
    // Noch die Liste des vorigen Ordners: warten
    if (!mixedSel && items[0] && (items[0].account !== sel.account || items[0].folder !== sel.folder)) return
    autoSelect.current = false
    // Handy: erst die Liste zeigen, nicht gleich eine Mail im Vollbild
    if (openOnFolder === 'none' || mobile) return
    let pick: MailItem | undefined
    if (openOnFolder === 'last') {
      try {
        const last = Number(localStorage.getItem(lastKey(sel))) || null
        pick = last == null ? undefined : items.find(i => (mixedSel ? i.id : i.uid) === last)
      } catch { /* egal */ }
    }
    if (openOnFolder === 'unread') pick = items.find(i => !i.seen)
    setSelected([(pick ?? items[0]).id])
  }, [items, loading]) // eslint-disable-line react-hooks/exhaustive-deps
  // Zuletzt gewaehlte Mail je Ordner merken
  useEffect(() => {
    if (!sel || searching || selected.length !== 1) return
    const item = items.find(i => i.id === selected[0])
    if (item && mixedSel) { try { localStorage.setItem(lastKey(sel), String(item.id)) } catch { /* egal */ } }
    else if (item && item.account === sel.account && item.folder === sel.folder) {
      try { localStorage.setItem(lastKey(sel), String(item.uid)) } catch { /* egal */ }
    }
  }, [selected]) // eslint-disable-line react-hooks/exhaustive-deps

  // Liste - nur die Antwort der neuesten Anfrage uebernehmen
  const listReq = useRef(0)
  // Zuletzt angehängte Seite (über 500) mit der Gesamtzahl, die der Server dabei gemeldet hat
  const pageTotal = useRef<{ key: string; page: number; total: number } | null>(null)
  // Zuletzt gesehene Listen je Ordner: beim Wechsel sofort zeigen, im Hintergrund auffrischen
  // (preload.ts, auch für die vorgeladenen Listen der Ordner mit Zahl)
  const inboxSel = !!sel && folders[sel.account]?.find(f => f.path === sel.folder)?.special === 'inbox'
  // Prio-Posteingang: Stand dieses Posteingangs, solange er noch nicht geholt ist der vom Vorladen bekannte
  const focusHere = !!serverFocus && !!sel && serverFocus.account === sel.account && serverFocus.folder === sel.folder
  const focusOn = inboxSel && (focusHere ? !!focusInfo?.enabled : !!focusKnown(sel!.account) || !!focusInfo?.enabled)
  // Zahlen der Reiter nur aus diesem Posteingang (bis er geholt ist: keine Zahl)
  const tabFocus = focusHere ? focusInfo : null
  // Zähler der Reiter und Kategorien wie beim Ordner eingestellt (Ordneranzahl: ungelesene oder alle)
  const totalMode = !!sel && prefs.folderCount?.[sel.account + '|' + sel.folder] === 'total'
  const countOf = (unread: number, total?: number) => totalMode && total != null ? total : unread
  const listFilter = filter || (focusOn ? focusTab : '')
  const listKey = sel ? listKeyOf(sel.account, sel.folder, listFilter, listSort, threadsOn) : ''
  const itemsKey = useRef('')
  const remember = (key: string, items: MailItem[], total: number, more: boolean) => rememberList(key, { items, total, more })
  // Aenderungen in der Liste (gelesen, Fahne, verschoben) gleich mitmerken
  useEffect(() => { if (!query.trim() && itemsKey.current && itemsKey.current === listKey) remember(listKey, items, total, moreRows) }, [items, total, moreRows]) // eslint-disable-line react-hooks/exhaustive-deps

  // Sofort reagieren: Mails, die eine Aktion aus dem Ordner nimmt, verschwinden gleich; der Auftrag an den Mailserver läuft in einer
  // Warteschlange nacheinander im Hintergrund. Solange er läuft (und kurz danach, bis der Abgleich ihn kennt), bleiben sie auch beim
  // Neuladen der Liste ausgeblendet. Schlägt er fehl, erscheinen sie mit dem Stand des Servers wieder.
  const pendingGone = useRef(new Map<string, number>())
  const goneKey = (i: { account: string; folder: string; uid: number }) => i.account + '|' + i.folder + '|' + i.uid
  // Gelesen, Fahne und Kategorien, die noch unterwegs sind, gelten auch für eine Liste, die der Server vorher gelesen hat (at = Beginn
  // der Anfrage, 0 = gemerkte Liste)
  const visibleOf = (list: MailItem[], at = 0) => {
    const l = overlay(list, at)
    return pendingGone.current.size ? l.filter(i => !pendingGone.current.has(goneKey(i))) : l
  }
  const mailQueue = useRef<Promise<void>>(Promise.resolve())
  const enqueue = (job: () => Promise<void>) => { const p = mailQueue.current.then(job); mailQueue.current = p.catch(() => {}); return p }
  /** Ausblenden; Zahlen an Ordnern, Reitern und im Seitenmenü gleich mit (Vorgabe: die Mails verlassen ihren Ordner) */
  const hideGone = (list: MailItem[], counts = countsFor(list, { remove: true }), patch?: MailPatch): PendingOp => {
    for (const i of list) pendingGone.current.set(goneKey(i), (pendingGone.current.get(goneKey(i)) ?? 0) + 1)
    const op = begin(patch ? list.map(i => ({ account: i.account, folder: i.folder, uid: i.uid, patch })) : [], counts)
    afterRemove(list.map(i => i.id))
    return op
  }
  /** Auftrag erledigt: nach kurzer Zeit wieder freigeben. Fehlgeschlagen: sofort freigeben und mit dem Stand des Servers neu laden. */
  const settleGone = (list: MailItem[], ok: boolean, op?: PendingOp) => {
    const release = () => { for (const i of list) { const k = goneKey(i), n = (pendingGone.current.get(k) ?? 1) - 1; if (n > 0) pendingGone.current.set(k, n); else pendingGone.current.delete(k) } }
    if (ok) { op?.done(); refreshSoon(); setTimeout(release, 15000) }
    else { op?.failed(); release(); reloadRef.current() }
  }

  /** more: Nachladen über „Mehr laden“ oder Blättern ans Ende - nur dann dreht der Kreis unter der Liste */
  const loadList = useCallback(async (keepPage = false, more = false) => {
    if (!sel || sel.folder === '__outbox' || sel.folder === '__nudge') { setLoadingMore(false); return }
    const req = ++listReq.current, at = pendingNow()
    const key = listKeyOf(sel.account, sel.folder, listFilter, listSort, threadsOn)
    const plain = !query.trim() && !similarTo && !keepPage
    let cached = plain ? listCache.get(key) : undefined
    // Wird die Liste gerade vorgeladen: auf diese Antwort warten statt eine zweite Anfrage zu stellen
    const coming = plain && !cached ? listInFlight(key) : undefined
    // Anderer Ordner ohne gemerkte Liste: sofort umschalten (leere Liste), nie die Mails des vorigen Ordners stehen lassen
    if (plain && !cached && itemsKey.current !== key) {
      itemsKey.current = ''
      setItems([])
      setTotal(0)
      setMoreRows(false)
    }
    if (coming) {
      setLoading(true)
      cached = await coming.catch(() => undefined)
      if (req !== listReq.current) return
    }
    if (cached) {
      itemsKey.current = key
      setItems(visibleOf(cached.items))
      setTotal(cached.total)
      setMoreRows(cached.more)
      setPage(0)
    } else itemsKey.current = ''
    // Gerade vorgeladen: schon der neueste Stand
    if (coming && cached) { pageTotal.current = null; setLoading(false); return }
    setLoading(!cached)
    if (more) setLoadingMore(true)
    try {
      if (similarTo) {
        const r = await api.get<MailItem[]>('/api/mail/similar?' + q({ acc: similarTo.account, folder: similarTo.folder, uid: similarTo.uid, deleted: prefs.searchDeleted ?? true }))
        if (req !== listReq.current) return
        setItems(visibleOf(r, at))
        setTotal(r.length)
        setMoreRows(false)
        setRemoteSearch(false)
      } else if (query.trim()) {
        // Suchbereich wie in Outlook: aus dem Posteingang alle Ordner, sonst nach Einstellung
        const scope = prefs.searchScope ?? 'inboxAll'
        const inInbox = folders[sel.account]?.find(f => f.path === sel.folder)?.special === 'inbox'
        const inVirtual = sel.folder.startsWith('sf:') || sel.folder.startsWith('cat:') || sel.folder === '__unified'
          || sel.folder.startsWith('ca:') // [feature:catchalldomains]
        const folder = inVirtual ? undefined : scope === 'current' || (scope === 'inboxAll' && !inInbox) ? sel.folder : undefined
        const params = { q: query, acc: allAccounts ? undefined : sel.account, folder: allAccounts ? undefined : folder, deleted: prefs.searchDeleted ?? true }
        const r = await api.get<MailItem[]>('/api/mail/search?' + q({ ...params, relevance: searchSort === 'relevance' }))
        if (req !== listReq.current) return
        setItems(visibleOf(r, at))
        setTotal(r.length)
        setMoreRows(false)
        // Schlank: den Text der Mails, deren Inhalt nur beim Anbieter liegt, dort durchsuchen - die Treffer kommen danach dazu
        if (accounts?.some(a => a.storage === 'slim' && (allAccounts || a.id === sel.account))) {
          setRemoteSearch(true)
          api.get<MailItem[]>('/api/mail/search/remote?' + q(params)).then(more => {
            if (req !== listReq.current) return
            const known = new Set(r.map(i => i.id))
            const add = more.filter(m => !known.has(m.id))
            if (!add.length) return
            setItems(prev => {
              const have = new Set(prev.map(i => i.id))
              const all = [...prev, ...add.filter(m => !have.has(m.id))]
              return searchSort === 'relevance' ? all : all.sort((a, b) => b.date.localeCompare(a.date))
            })
            setTotal(n => n + add.length)
          }).catch(() => { /* Anbieter nicht erreichbar: bleibt bei den eigenen Treffern */ })
            .finally(() => { if (req === listReq.current) setRemoteSearch(false) })
        } else setRemoteSearch(false)
      } else {
        const n = listRows(keepPage ? page : 0), plan = listPlan(n)
        const urlOf = (p: number, size: number) => listUrlOf(sel.account, sel.folder, listFilter, listSort, threadsOn, p, size)
        const url = urlOf(0, plan[0].size)
        // Über 500: beim Nachladen nur die nächste Seite anhängen, solange sich beim Server seit der vorigen Seite nichts verschoben hat
        // (gleiche Gesamtzahl); sonst und beim Neuladen im Hintergrund alle geladenen Seiten von vorn, damit nichts fehlt oder doppelt ist
        const next = plan[plan.length - 1], prev = pageTotal.current
        if (more && next.page > 1 && prev?.key === key && prev.page === next.page - 1) {
          const r = await api.get(urlOf(next.page, LIST_MAX))
          if (req !== listReq.current) return
          if (r.total === prev.total) {
            pageTotal.current = { key, page: next.page, total: r.total }
            setItems(list => mergeRows(list, visibleOf(r.items, at)))
            setMoreRows(hasMore(next.page, LIST_MAX, r.items.length, r.total))
            return
          }
        }
        let rows: MailItem[] = [], head: { items: MailItem[]; total: number } | undefined, last = { page: 0, size: 0, delivered: 0, total: 0 }
        for (const s of plan) {
          const r = await api.get(s.page ? urlOf(s.page, s.size) : url)
          if (req !== listReq.current) return
          head ??= r
          rows = mergeRows(rows, r.items)
          last = { ...s, delivered: r.items.length, total: r.total }
          if (s.page && !r.items.length) break
        }
        pageTotal.current = last.page ? { key, page: last.page, total: last.total } : null
        // [feature:mailprefetch]
        if (featureOn('mailprefetch')) {
          prefetchArrived(listCache.get(key)?.items, head!.items)
        }
        // [/feature:mailprefetch]
        itemsKey.current = key
        setItems(visibleOf(rows, at))
        setTotal(head!.total)
        const further = hasMore(last.page, last.size, last.delivered, last.total)
        setMoreRows(further)
        if (!keepPage) setPage(0)
        if (n <= 100) remember(key, rows, head!.total, further)
      }
    } catch (e) { if (req === listReq.current) fail(e) } finally { if (req === listReq.current) { setLoading(false); setLoadingMore(false) } }
  }, [sel, query, similarTo, allAccounts, listFilter, listSort, page, fail, accounts,
    threadsOn, // [feature:mailthreads]
  ])
  // [feature:mailprefetch]
  // Neu angekommene Mails der offenen Liste vorhalten: Inhalt der neuen ungelesenen gleich mitholen (die übrigen Ordner mit
  // Zahl hält preload.ts vor)
  const prefetchArrived = (before: MailItem[] | undefined, after: MailItem[]) => {
    if (!before) return
    const had = new Set(before.map(i => i.id))
    const arrived = after.filter(i => !had.has(i.id) && !i.seen).slice(0, 3)
    if (arrived.length) prefetch(arrived)
  }
  // [/feature:mailprefetch]
  // Ungelesene je Reiter; beim Wechsel des Posteingangs und nach Aenderungen neu
  const loadFocus = useCallback(() => {
    if (!sel || !inboxSel) { setServerFocus(null); return }
    const at = pendingNow(), { account, folder } = sel
    api.get<FocusInfo>(`/api/mail/${account}/focus?` + q({ folder }))
      .then(info => setServerFocus(prev => prev && prev.account === account && prev.folder === folder && prev.at > at ? prev : { at, account, folder, info }))
      .catch(() => setServerFocus(null))
  }, [sel, inboxSel])
  // Zahlen der Reiter: mit Vorladen erst, wenn die Listen beider Reiter hier liegen (preload.ts, auch nach eigenen Änderungen)
  const focusRound = featureOn('mailprefetch') ? 'p' + settledRound() : 'v' + version
  useEffect(() => { loadFocus() }, [loadFocus, focusRound])
  // Nach Relevant/Sonstige verschieben; always: Absender immer dorthin. Verlassen die Mails den offenen Reiter, verschwinden sie gleich
  // und die Zahlen der Reiter wandern mit.
  const focusMove = async (list: MailItem[], focused: boolean, always = false) => {
    if (!list.length) return
    const target = focused ? 'focused' : 'other'
    const leaves = focusOn && !filter && focusTab !== target
    const counts: CountChange[] = !leaves ? [] : list.filter(i => i.account === sel!.account && i.folder === sel!.folder).flatMap(i => [
      { account: i.account, folder: i.folder, tab: focusTab, unread: i.seen ? 0 : -1, total: -1 },
      { account: i.account, folder: i.folder, tab: target, unread: i.seen ? 0 : 1, total: 1 },
    ])
    const op = leaves ? hideGone(list, counts) : null
    const jobs = groupMails(list)
    await enqueue(async () => { op?.sent(); try {
      for (const g of jobs) await api.post(`/api/mail/${g.account}/focus`, { folder: g.folder, uids: g.uids, focused, always })
      if (op) settleGone(list, true, op); else loadFocus()
      notify(focused ? t('Nach Relevant verschoben') : t('Nach Sonstige verschoben'), always ? t('Künftige Mails dieses Absenders landen auch dort.') : undefined)
    } catch (e) { if (op) { failBack(e); settleGone(list, false, op) } else fail(e) } })
  }
  const moveFocus = (focused: boolean, always = false) => focusMove(selectedItems, focused, always)

  useEffect(() => { loadList(false) }, [sel, query, similarTo, allAccounts, listFilter, listSort, searchSort === 'relevance',
    threadsOn, // [feature:mailthreads]
  ]) // eslint-disable-line react-hooks/exhaustive-deps
  // Suchordner geaendert: gemerkte Trefferlisten verwerfen und die offene neu laden
  const selRef = useRef(sel)
  selRef.current = sel
  const loadListRef = useRef(loadList)
  // Alias gesperrt oder übernommen: Liste neu (Zustand, Ordner)
  useEffect(() => {
    const reload = () => { loadListRef.current(true) }
    window.addEventListener(CATCHALL_CHANGED, reload)
    return () => window.removeEventListener(CATCHALL_CHANGED, reload)
  }, [])
  loadListRef.current = loadList
  useEffect(() => {
    const on = () => {
      for (const k of [...listCache.keys()]) if (k.split('|')[1]?.startsWith('sf:')) listCache.delete(k)
      if (selRef.current?.folder.startsWith('sf:')) loadListRef.current(false)
    }
    window.addEventListener(SEARCH_FOLDERS_CHANGED, on)
    return () => window.removeEventListener(SEARCH_FOLDERS_CHANGED, on)
  }, [])
  // Suchtreffer sortieren: Datum und Relevanz liefert der Server, Absender und Betreff sortieren wir hier
  const sortedItems = useMemo(() => {
    if (!query || similarTo || searchSort === 'newest' || searchSort === 'relevance') return items
    const key = (i: MailItem) => searchSort === 'from' ? (i.from?.name || i.from?.address || '') : i.subject.replace(/^((re|aw|fwd?|wg)\s*:\s*)+/i, '')
    return [...items].sort(searchSort === 'oldest' ? (a, b) => a.date.localeCompare(b.date)
      : (a, b) => key(a).localeCompare(key(b), locale, { sensitivity: 'base' }) || b.date.localeCompare(a.date))
  }, [items, query, similarTo, searchSort])
  // [feature:mailthreads]
  // Unterhaltungen: unter der Zeile einer aufgeklappten Unterhaltung ihre übrigen Mails (auch aus „Gesendet“)
  const threadList = threadsOn && !query.trim() && !similarTo
  const listItems = useMemo(() => !threadList || !expanded.size ? sortedItems
    : sortedItems.flatMap(i => i.threadKey && expanded.has(i.threadKey) && members[i.threadKey]
      ? [i, ...members[i.threadKey].filter(m => m.id !== i.id).map(m => ({ ...m, member: i.threadKey }))] : [i]),
  [sortedItems, threadList, expanded, members])
  /** Mails einer Unterhaltung (neueste zuerst) vom Server holen */
  const loadThread = async (i: MailItem) => {
    if (!i.threadKey) return
    try {
      const at = pendingNow()
      const all = await api.get<MailItem[]>(`/api/mail/${i.account}/conversation?` + q({ key: i.threadKey, folder: i.folder }))
      setMembers(m => ({ ...m, [i.threadKey!]: overlay(all, at) }))
    } catch (e) { fail(e) }
  }
  const toggleThread = (i: MailItem) => {
    const key = i.threadKey
    if (!key) return
    const open = !expanded.has(key)
    setExpanded(x => { const n = new Set(x); if (open) n.add(key); else n.delete(key); return n })
    if (open) loadThread(i)
  }
  /** Aktion aus einer Karte der Unterhaltung, die nicht die gewählte Zeile ist */
  const actOne = async (m: MailItem, what: 'delete' | 'junk' | 'notjunk') => {
    const fs = folders[m.account]
    const trash = fs?.find(f => f.special === 'trash')
    if (what === 'delete' && fs && (!trash || trash.path === m.folder)
      && !await askConfirm('', t('1 Mail endgültig löschen? Das lässt sich nicht rückgängig machen.'), t('Endgültig löschen'))) return
    try {
      await api.post(`/api/mail/${m.account}/${what}`, { folder: m.folder, uids: [m.uid] })
      if (what === 'junk') notify(t('Als Junk gelernt'))
      if (what === 'notjunk') notify(t('Als „Kein Junk“ gelernt'))
      afterRemove([m.id])
      setMembers(all => Object.fromEntries(Object.entries(all).map(([k, v]) => [k, v.filter(x => x.id !== m.id)])))
      loadList(true)
    } catch (e) { fail(e) }
  }
  // Gewählte Unterhaltung: ihre Mails für den Lesebereich holen
  useEffect(() => {
    const c = selected.length === 1 ? listItems.find(i => i.id === selected[0]) : undefined
    if (threadList && c && !c.member && (c.threadCount ?? 0) > 1 && c.threadKey && !members[c.threadKey]) loadThread(c)
  }, [selected, threadList]) // eslint-disable-line react-hooks/exhaustive-deps
  // Anderer Ordner, Filter oder Ansicht: alles zugeklappt
  useEffect(() => { setExpanded(new Set()); setMembers({}) }, [sel, listFilter, threadsOn, query, similarTo])
  // [feature:mailthreads:else]
  // const listItems = sortedItems
  // [/feature:mailthreads]
  useEffect(() => { if (version !== undefined) loadList(true) }, [version]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (page > 0) loadList(true, true) }, [page]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!opening || loading || sel?.account !== opening.account || sel?.folder !== opening.openIn) return
    const { openIn: _, ...row } = opening
    if (!items.some(i => i.id === opening.id)) setItems(list => [row, ...list])
    setSelected([opening.id])
    setOpening(null)
  }, [opening, loading, items, sel])

  const selectedItems = useMemo(() => listItems.filter(i => selected.includes(i.id)), [listItems, selected])
  const current = selectedItems.length === 1 ? selectedItems[0] : null
  // Die naechsten Mails schon laden: nach Loeschen/Archivieren springt die Auswahl dorthin und soll nicht warten
  useEffect(() => {
    if (listItems.length === 0) return
    const at = current ? listItems.findIndex(x => x.id === current.id) : -1
    prefetch(at >= 0 ? [...listItems.slice(at + 1, at + 4), ...listItems.slice(Math.max(0, at - 1), at)] : listItems.slice(0, 3))
  }, [current?.id, listItems]) // eslint-disable-line react-hooks/exhaustive-deps
  // Geladene Mail nur verwenden, wenn sie zur aktuellen Auswahl passt
  const shown = message && current && message.account === current.account && message.folder === current.folder && message.uid === current.uid
    ? message : null
  // Kein Zurücksetzen beim Wechsel der Auswahl: Ist die Mail vorgeladen, meldet der Lesebereich sie sofort (vor diesem Effekt) -
  // ein setMessage(null) hier löschte sie wieder, und Antworten/Weiterleiten blieben gesperrt. „shown“ prüft ohnehin, ob sie passt.
  const folderOf = (acc: string, path: string) => folders[acc]?.find(f => f.path === path)
  const currentFolder = sel ? folderOf(sel.account, sel.folder) : undefined
  // [feature:junklock]
  // Junk (Ordner oder Einstufung der angezeigten Mail): Antworten, Weiterleiten, Übernehmen und Ablegen gesperrt - bis „Kein Junk“
  const junkLock = featureOn('junklock') && (currentFolder?.special === 'junk' || !!shown?.junk)
  const junkStop = (m?: { junk?: boolean } | null) => {
    if (!featureOn('junklock') || !m?.junk) return false
    notify(t('Bei Junk-Mails ist diese Aktion gesperrt. Mit „Kein Junk“ wird die Mail freigegeben.'), undefined, 'warning')
    return true
  }
  // [/feature:junklock]
  // Entwurf nach dem Ordner der Mail selbst, nicht nach dem gewählten Ordner (Suche findet auch Mails anderer Ordner)
  const isDraft = (i: MailItem) => folderOf(i.account, i.folder)?.special === 'drafts'
  // Suchordner ("sf:"), Kategorien ("cat:") und Alle Posteingaenge: Mails aus allen Konten
  const virtual = !!sel && (sel.folder.startsWith('sf:') || sel.folder.startsWith('cat:') || sel.folder === '__unified')
  const virtualName = useVirtualName(virtual ? sel!.folder : undefined)
  // Catchall-Ordner: oben die Adresse des Alias (mit Notiz) bzw. die Domain statt des Ordnernamens
  const catchall = useCatchall()
  const catchallName = sel && !virtual ? catchallTitle(catchall, sel.account, sel.folder) : null
  // Verschieben geht in einen Ordner des Kontos - im virtuellen Ordner nur, wenn alle Mails aus einem Konto sind
  const moveAccount = !virtual ? sel?.account : new Set(selectedItems.map(i => i.account)).size === 1 ? selectedItems[0].account : undefined

  // Gruppiert nach Konto und Ordner - in der Suche koennen es mehrere sein
  // Neu einordnen (nur im Posteingang): Junk-Erkennung, Relevant/Sonstige und Kategorien für Mails, die schon da waren
  const inInbox = selectedItems.length > 0 && selectedItems.every(i => folders[i.account]?.find(f => f.path === i.folder)?.special === 'inbox')
  const reclassify = async () => {
    try {
      let n = 0
      for (const g of groups()) n += (await api.post<{ count: number }>(`/api/mail/${g.account}/reclassify`, { folder: g.folder, uids: g.uids })).count
      notify(t('Wird neu eingeordnet'), t('{n} Mails – Junk-Erkennung, Relevant/Sonstige und Kategorien. Den Fortschritt siehst du unter „Aktivität“.', { n }))
    } catch (e) { fail(e) }
  }
  const groups = () => {
    const g = new Map<string, { account: string; folder: string; uids: number[] }>()
    for (const i of selectedItems) {
      const k = i.account + '|' + i.folder
      if (!g.has(k)) g.set(k, { account: i.account, folder: i.folder, uids: [] })
      g.get(k)!.uids.push(i.uid)
    }
    return [...g.values()]
  }

  const categories = useCategories()

  /** Entfernte Mails aus der Liste nehmen; war die Auswahl betroffen, die naechste waehlen. */
  const afterRemove = (removed: number[]) => {
    const ids = new Set(removed)
    if (!ids.size) return
    setMembers(m => Object.fromEntries(Object.entries(m).map(([k, v]) => [k, v.filter(i => !ids.has(i.id))]))) // [feature:mailthreads]
    const now = listRef.current
    const idx = now.findIndex(i => ids.has(i.id))
    if (idx < 0) return
    const rest = now.filter(i => !ids.has(i.id))
    listRef.current = rest
    setItems(rest)
    if (selected.some(id => ids.has(id))) {
      const next = rest[Math.min(idx, rest.length - 1)]
      setSelected(next ? [next.id] : [])
    }
  }
  const idsOf = (g: { account: string; folder: string; uids: number[] }) =>
    selectedItems.filter(i => i.account === g.account && i.folder === g.folder && g.uids.includes(i.uid)).map(i => i.id)

  // ---------------- Sofort sichtbar: erst hier ändern, dann im Hintergrund an den Server (pending.ts) ----------------
  /** Nach Konto und Ordner gruppiert, jede Mail einmal */
  const groupMails = (list: MailItem[]) => {
    const g = new Map<string, { account: string; folder: string; uids: number[] }>()
    for (const i of list) {
      const k = i.account + '|' + i.folder
      if (!g.has(k)) g.set(k, { account: i.account, folder: i.folder, uids: [] })
      if (!g.get(k)!.uids.includes(i.uid)) g.get(k)!.uids.push(i.uid)
    }
    return [...g.values()]
  }
  const uniqueMails = (list: MailItem[]) => [...new Map(list.map(i => [mailKey(i), i])).values()]
  /** Zahlen, die sich mit der Änderung verschieben: Ordner der Mail (im Posteingang auch das Seitenmenü), „Markiert“ und der offene
   *  Reiter Relevant/Sonstige. remove: die Mails verlassen ihren Ordner. */
  const countsFor = (list: MailItem[], ch: { seen?: boolean; flagged?: boolean; remove?: boolean }): CountChange[] => {
    const out: CountChange[] = []
    const tab = focusOn && !filter && !query.trim() && !similarTo && sel ? focusTab : undefined
    for (const i of uniqueMails(list)) {
      const inbox = folders[i.account]?.find(f => f.path === i.folder)?.special === 'inbox'
      const seen = ch.seen ?? i.seen, flagged = ch.flagged ?? i.flagged
      const unread = ch.remove ? (i.seen ? 0 : -1) : seen === i.seen ? 0 : seen ? -1 : 1
      out.push({ account: i.account, folder: i.folder, inbox, unread, total: ch.remove ? -1 : 0 })
      if (!ch.remove) out.push({ account: i.account, folder: FLAGGED, unread: (flagged && !seen ? 1 : 0) - (i.flagged && !i.seen ? 1 : 0), total: (flagged ? 1 : 0) - (i.flagged ? 1 : 0) })
      if (tab && inbox && i.account === sel!.account && i.folder === sel!.folder) out.push({ account: i.account, folder: i.folder, tab, unread, total: ch.remove ? -1 : 0 })
    }
    return out
  }
  /** Mails in der Liste und in aufgeklappten Unterhaltungen ändern */
  const patchLocal = (keys: Set<string>, f: (i: MailItem) => MailItem) => {
    const g = (i: MailItem) => keys.has(mailKey(i)) ? f(i) : i
    listRef.current = listRef.current.map(g)
    setItems(l => l.map(g))
    setMembers(m => Object.fromEntries(Object.entries(m).map(([k, v]) => [k, v.map(g)]))) // [feature:mailthreads]
  }
  /** Danach die Zahlen beim Server nachfragen (Ordner, Reiter, Seitenmenü) - einmal für mehrere schnelle Änderungen */
  const refreshTimer = useRef<ReturnType<typeof setTimeout>>()
  const refreshRef = useRef<() => void>(() => {})
  refreshRef.current = () => { if (accounts) loadFolders(accounts); loadFocus(); window.dispatchEvent(new Event('dmoffice:mailstatus')) }
  const refreshSoon = () => { clearTimeout(refreshTimer.current); refreshTimer.current = setTimeout(() => refreshRef.current(), 300) }
  /** Der Server hat eine Änderung abgelehnt, die schon zu sehen war */
  const failBack = (e: unknown) => notify(t('Änderung zurückgenommen'), tError(e instanceof Error ? e.message : String(e)), 'error')
  /**
   * Gelesen, Fahne, Kategorie: gleich anzeigen (Liste, Unterhaltung, Zahlen), dann in der Warteschlange an den Server. Abgelehnt: die
   * Felder, die noch auf dem neuen Stand sind, zurück, Hinweis, Stand des Servers neu laden.
   */
  const changeMails = (targets: MailItem[], patch: MailPatch, send: (g: { account: string; folder: string; uids: number[] }) => Promise<unknown>,
    opts: { after?: () => void; counts?: boolean } = {}) => {
    const all = uniqueMails(targets)
    if (!all.length) return Promise.resolve()
    const has = (i: MailItem, k: string) => (i.keywords ?? []).includes(k)
    const differs = (i: MailItem) => (patch.seen !== undefined && i.seen !== patch.seen) || (patch.flagged !== undefined && i.flagged !== patch.flagged)
      || Object.entries(patch.keywords ?? {}).some(([k, on]) => has(i, k) !== on)
    const changing = all.filter(differs)
    const before = new Map(changing.map(i => [mailKey(i), i]))
    const op = begin(changing.map(i => ({ account: i.account, folder: i.folder, uid: i.uid, patch })),
      opts.counts === false ? [] : countsFor(changing, { seen: patch.seen, flagged: patch.flagged }))
    if (changing.length) patchLocal(new Set(before.keys()), i => applyPatch(i, patch))
    const jobs = groupMails(all)
    return enqueue(async () => {
      op.sent()
      try {
        for (const g of jobs) await send(g)
        op.done()
        refreshSoon()
        opts.after?.()
      } catch (e) {
        op.failed()
        patchLocal(new Set(before.keys()), i => {
          const old = before.get(mailKey(i))!
          return applyPatch(i, {
            seen: patch.seen !== undefined && i.seen === patch.seen ? old.seen : undefined,
            flagged: patch.flagged !== undefined && i.flagged === patch.flagged ? old.flagged : undefined,
            keywords: patch.keywords && Object.fromEntries(Object.entries(patch.keywords).filter(([k, on]) => has(i, k) === on && has(old, k) !== on).map(([k, on]) => [k, !on])),
          })
        })
        failBack(e)
        reloadRef.current()
      }
    })
  }
  const flagsOf = (p: { seen?: boolean; flagged?: boolean }) => (g: { account: string; folder: string; uids: number[] }) =>
    api.post(`/api/mail/${g.account}/flags`, { folder: g.folder, uids: g.uids, ...p })
  /** Als gelesen markieren aus dem Lesebereich (nach der eingestellten Wartezeit) - mit dem aktuellen Stand der Mail */
  const markSeen = (i: MailItem) => {
    const now = listRef.current.find(x => mailKey(x) === mailKey(i)) ?? i
    if (!now.seen) changeMails([now], { seen: true }, flagsOf({ seen: true }))
  }
  /** Ordner „Alle als gelesen markieren“: Zahlen und sichtbare Mails gleich, der Server folgt */
  const markFolderRead = (account: string, f: Folder) => {
    const here = listRef.current.filter(i => i.account === account && i.folder === f.path && !i.seen)
    const counts: CountChange[] = [{ account, folder: f.path, inbox: f.special === 'inbox', unread: -f.unread }]
    if (focusInfo?.enabled && serverFocus?.account === account && serverFocus.folder === f.path)
      counts.push({ account, folder: f.path, tab: 'focused', unread: -focusInfo.focused }, { account, folder: f.path, tab: 'other', unread: -focusInfo.other })
    const op = begin(here.map(i => ({ account, folder: f.path, uid: i.uid, patch: { seen: true } })), counts)
    const keys = new Set(here.map(mailKey))
    patchLocal(keys, i => ({ ...i, seen: true }))
    return enqueue(async () => {
      op.sent()
      try { await api.post(`/api/mail/${account}/folders/read`, { path: f.path }); op.done(); refreshSoon() }
      catch (e) { op.failed(); patchLocal(keys, i => ({ ...i, seen: false })); failBack(e); reloadRef.current() }
    })
  }
  /** Der Server kennt die Mail schon als gelesen (anderes Gerät), die Liste noch nicht: nur die Zeile angleichen, die Zahlen hat er schon */
  const seenOnServer = (i: MailItem) => {
    const now = listRef.current.find(x => mailKey(x) === mailKey(i)) ?? i
    if (!now.seen && !isPending(now, 'seen')) patchLocal(new Set([mailKey(now)]), x => ({ ...x, seen: true }))
  }

  // ---------------- Einheitliches Rückgängig (Kennung undoall) ----------------
  // Verschobene Mails (auch gelöscht = in den Papierkorb, archiviert, Junk) holt der Server über ihre Message-ID zurück;
  // danach die Liste und die Ordner mit dem dann aktuellen Stand neu laden
  const reloadRef = useRef<() => void>(() => {})
  reloadRef.current = () => { loadList(true); if (accounts) loadFolders(accounts) }
  type Moved = { account: string; from: string; to: string; items: MailItem[] }
  /** true: „Rückgängig“ angeboten (die bisherige Meldung entfällt dann) */
  const offerMoveBack = (moved: Moved[]): boolean => {
    if (!undoAllOn()) return false
    const back = moved.filter(m => m.from !== m.to).map(m => ({ ...m, ids: m.items.map(i => i.messageId).filter((x): x is string => !!x) })).filter(m => m.ids.length)
    const n = back.reduce((a, m) => a + m.ids.length, 0)
    if (!n) return false
    const where = new Set(back.map(m => m.account + '|' + m.to)).size === 1 ? folderLabel(folderOf(back[0].account, back[0].to)) : ''
    const label = !where ? t('{n} Mails verschoben', { n }) : n === 1 ? t('Mail nach „{folder}“ verschoben', { folder: where }) : t('{n} Mails nach „{folder}“ verschoben', { n, folder: where })
    undoable(label, async () => {
      try { for (const m of back) await api.post(`/api/mail/${m.account}/move-back`, { folder: m.to, target: m.from, messageIds: m.ids }) }
      finally { reloadRef.current() }
    })
    return true
  }
  /** Gelesen/ungelesen zurück: nur die Mails, deren Stand sich geändert hat */
  const offerSeenBack = (changed: { account: string; folder: string; uids: number[] }[], seen: boolean) => {
    if (!undoAllOn() || !changed.length) return
    const n = changed.reduce((a, g) => a + g.uids.length, 0)
    undoable(seen ? (n === 1 ? t('Als gelesen markiert') : t('{n} Mails als gelesen markiert', { n })) : (n === 1 ? t('Als ungelesen markiert') : t('{n} Mails als ungelesen markiert', { n })), async () => {
      try { for (const g of changed) await api.post(`/api/mail/${g.account}/flags`, { folder: g.folder, uids: g.uids, seen: !seen }) }
      finally { reloadRef.current() }
    })
  }

  const act = async (what: 'delete' | 'junk' | 'notjunk' | 'archive' | 'read' | 'unread' | 'flag' | 'unflag', target?: string) => {
    if (!selectedItems.length) return
    const before = selectedItems
    // Gelesen, ungelesen, Fahne: gleich sichtbar, der Server folgt im Hintergrund
    if (what === 'read' || what === 'unread') {
      const seen = what === 'read'
      const changed = groupMails(before.filter(i => i.seen !== seen))
      return changeMails(before, { seen }, flagsOf({ seen }), { after: () => offerSeenBack(changed, seen) })
    }
    if (what === 'flag' || (what === 'unflag' && sel?.folder !== FLAGGED)) return changeMails(before, { flagged: what === 'flag' }, flagsOf({ flagged: what === 'flag' }))
    // Im Papierkorb (oder Konto ohne Papierkorb) ist Löschen endgültig: vorher fragen
    if (what === 'delete') {
      const final = selectedItems.filter(i => { const fs = folders[i.account]; const trash = fs?.find(f => f.special === 'trash'); return !!fs && (!trash || trash.path === i.folder) }).length
      if (final && !await askConfirm('', final === 1 ? t('1 Mail endgültig löschen? Das lässt sich nicht rückgängig machen.') : t('{n} Mails endgültig löschen? Das lässt sich nicht rückgängig machen.', { n: final }), t('Endgültig löschen'))) return
    }
    const removes = ['delete', 'junk', 'archive'].includes(what) || (what === 'notjunk' && currentFolder?.special === 'junk')
      || what === 'unflag'
    // Sofort anzeigen, der Mailserver braucht manchmal Sekunden; bei Fehler zurueck auf den Stand des Servers
    const all = selectedItems.map(i => i.id)
    const hiddenNow = removes && (what !== 'archive' || selectedItems.every(i => folders[i.account]?.some(f => f.special === 'archive')))
    // Fahne weg in „Markiert“: die Mails bleiben in ihrem Ordner, nur „Markiert“ zählt sie nicht mehr
    const op = !hiddenNow ? null : what === 'unflag' ? hideGone(before, countsFor(before, { flagged: false }), { flagged: false }) : hideGone(before)
    if (what === 'notjunk' && !removes) setItems(list => list.map(i => !all.includes(i.id) ? i : { ...i, spam: 0 }))
    // Vor dem Verschieben prüfen lassen, ob sich der Absender als sicher eintragen ließe
    const trust = what === 'notjunk' && selectedItems.length === 1
      ? checkTrust(selectedItems[0].account, selectedItems[0].folder, selectedItems[0].uid).catch(() => null) : null
    // Bereits erledigte Gruppen
    const done: number[] = []
    // Für „Rückgängig“ (undoall): wohin verschoben wurde
    const moved: Moved[] = []
    const special = (acc: string, kind: string) => folders[acc]?.find(f => f.special === kind)?.path
    const itemsOf = (g: { account: string; folder: string; uids: number[] }) => before.filter(i => i.account === g.account && i.folder === g.folder && g.uids.includes(i.uid))
    const jobs = groups()
    await enqueue(async () => { op?.sent(); try {
      for (const g of jobs) {
        const body = { folder: g.folder, uids: g.uids }
        if (what === 'delete') {
          const r = await api.post<{ moved?: boolean }>(`/api/mail/${g.account}/delete`, body)
          const trash = special(g.account, 'trash')
          if (r?.moved && trash) moved.push({ account: g.account, from: g.folder, to: trash, items: itemsOf(g) })
        }
        else if (what === 'junk') {
          await api.post(`/api/mail/${g.account}/junk`, body)
          const junk = special(g.account, 'junk')
          if (junk && junk !== g.folder) moved.push({ account: g.account, from: g.folder, to: junk, items: itemsOf(g) })
        }
        else if (what === 'notjunk') {
          await trust; await api.post(`/api/mail/${g.account}/notjunk`, body)
          const inbox = special(g.account, 'inbox')
          if (inbox && special(g.account, 'junk') === g.folder) moved.push({ account: g.account, from: g.folder, to: inbox, items: itemsOf(g) })
        }
        else if (what === 'archive') {
          const archive = folders[g.account]?.find(f => f.special === 'archive')
          // Konto ohne Archiv ueberspringen, die anderen trotzdem erledigen
          if (!archive) { notify(t('Kein Archiv-Ordner'), t('Lege in den Konto-Einstellungen unter „Ordner“ einen Archiv-Ordner fest oder an.'), 'warning'); continue }
          await api.post(`/api/mail/${g.account}/move`, { ...body, target: archive.path })
          moved.push({ account: g.account, from: g.folder, to: archive.path, items: itemsOf(g) })
        }
        else if (what === 'unflag') await api.post(`/api/mail/${g.account}/flags`, { ...body, flagged: false })
        done.push(...idsOf(g))
      }
      if (removes && done.length < all.length) afterRemove(done)
      if (moved.length) offerMoveBack(moved)
      if (what === 'junk') notify(t('Als Junk gelernt'))
      if (what === 'notjunk') { const tc = await trust; if (tc) setTrustOffer({ check: tc }); else notify(t('Als „Kein Junk“ gelernt')) }
      if (op) settleGone(before, true, op)
    } catch (e) {
      // Nicht oder nur teilweise erledigt: Liste auf den Stand des Servers bringen (ausgeblendete Mails erscheinen wieder)
      if (op) { failBack(e); settleGone(before, false, op) } else { fail(e); loadList(true) }
    } })
  }
  // Kategorie an/aus - auf dem Server als IMAP-Tag. Gleich sichtbar; im Ordner dieser Kategorie verschwinden Mails ohne sie gleich.
  const categoryOf = (key: string, on: boolean) => (g: { account: string; folder: string; uids: number[] }) =>
    api.post(`/api/mail/${g.account}/category`, { folder: g.folder, uids: g.uids, key, on })
  const setCategory = (list: MailItem[], key: string, on: boolean, after?: () => void) => {
    if (!on && sel?.folder === 'cat:' + key) {
      const gone = uniqueMails(list.filter(x => x.keywords?.includes(key)))
      if (!gone.length) return Promise.resolve()
      const op = hideGone(gone, [], { keywords: { [key]: false } })
      const jobs = groupMails(gone)
      return enqueue(async () => { op.sent(); try {
        for (const g of jobs) await categoryOf(key, false)(g)
        settleGone(gone, true, op)
        after?.()
      } catch (e) { failBack(e); settleGone(gone, false, op) } })
    }
    return changeMails(list, { keywords: { [key]: on } }, categoryOf(key, on), { after })
  }
  // Für alle gewählten Mails
  const categorize = (key: string, on: boolean) => setCategory(selectedItems, key, on)
  // Klick auf einen Badge: Kategorie nur von dieser Mail nehmen (bzw. von den gewählten, wenn sie dazugehört)
  const uncategorize = (i: MailItem, key: string) =>
    setCategory(selected.includes(i.id) ? selectedItems.filter(x => x.keywords?.includes(key)) : [i], key, false)
  const dropIds = (e: React.DragEvent): number[] | null => {
    e.preventDefault()
    try { const d = JSON.parse(e.dataTransfer.getData('application/x-dmoffice-mail') || '{}'); return d.ids?.length ? d.ids : null } catch { return null }
  }
  const dragOk = (e: React.DragEvent) => { if (e.dataTransfer.types.includes('application/x-dmoffice-mail')) { e.preventDefault(); e.dataTransfer.dropEffect = 'move' } }
  // Auf „Relevant“ bzw. „Sonstige“ gezogen: dorthin, der Filter lernt daraus
  const focusDropped = (ids: number[], focused: boolean) => focusMove(items.filter(x => ids.includes(x.id)), focused)
  // Mails auf eine Kategorie im Ordnerbereich gezogen: zuordnen (eine lernende Kategorie lernt daraus)
  const categorizeDropped = (key: string, ids: number[]) => {
    const list = items.filter(i => ids.includes(i.id))
    const name = categories.find(c => c.key === key)?.name ?? key
    return setCategory(list, key, true, () =>
      notify(list.length === 1 ? t('Kategorie „{name}“ zugeordnet', { name }) : t('{n} Mails der Kategorie „{name}“ zugeordnet', { n: list.length, name })))
  }
  const clearCategories = async () => {
    const keys = [...new Set(selectedItems.flatMap(i => i.keywords ?? []).filter(k => k.startsWith('dm_')))]
    for (const k of keys) await categorize(k, false)
  }

  // Nachverfolgen: kennzeichnen (IMAP) mit Faelligkeit - erscheint unter Aufgaben in "E-Mail-Nachverfolgung"
  const followUp = (due: string | null, done = false) =>
    changeMails(selectedItems, { flagged: !done }, g => api.post(`/api/mail/${g.account}/followup`, { folder: g.folder, uids: g.uids, due, done }),
      { after: () => notify(done ? t('Nachverfolgung erledigt') : t('Zur Nachverfolgung markiert')) })

  const move = async (account: string, target: string, ids = selected) => {
    if (target === FLAGGED) {
      // Markieren geht in jedem Konto: je Konto und Ordner
      return changeMails(items.filter(i => ids.includes(i.id)), { flagged: true }, flagsOf({ flagged: true }), { after: () => notify(t('Markiert')) })
    }
    const moving = items.filter(i => ids.includes(i.id) && i.account === account)
    if (!moving.length) return
    // Gemischte Auswahl (alle Posteingänge, Kategorien, Suchordner): das Ziel gehört zu einem Konto
    const others = items.filter(i => ids.includes(i.id) && i.account !== account).length
    const removed: number[] = []
    const movedBack: Moved[] = []
    const gone = moving.filter(i => i.folder !== target)
    const op = hideGone(gone)
    await enqueue(async () => { op.sent(); try {
      const byFolder = new Map<string, number[]>()
      moving.forEach(i => byFolder.set(i.folder, [...(byFolder.get(i.folder) || []), i.uid]))
      for (const [folder, uids] of byFolder) {
        if (folder === target) continue
        await api.post(`/api/mail/${account}/move`, { folder, uids, target })
        removed.push(...moving.filter(i => i.folder === folder).map(i => i.id))
        movedBack.push({ account, from: folder, to: target, items: moving.filter(i => i.folder === folder) })
      }
      settleGone(gone, true, op)
      const where = folderLabel(folderOf(account, target))
      // Mit „Rückgängig“ (undoall) nennt die Leiste das Ziel - der Hinweis bei gemischter Auswahl bleibt
      const undo = offerMoveBack(movedBack)
      if (others) notify(t('{n} von {all} Mails nach „{folder}“ verschoben. Mails aus anderen Konten bleiben in ihrem Ordner.', { n: moving.length, all: moving.length + others, folder: where }), undefined, 'warning')
      else if (!undo) notify(t('Verschoben nach „{folder}“', { folder: where }))
    } catch (e) {
      failBack(e)
      settleGone(gone, false, op)
    } })
  }

  /** Kopieren (Strg beim Ziehen) und Verschieben in ein anderes Postfach (Schalter mailmove). Zwischen Postfächern lädt der Server
   *  jede Mail erst ins Ziel hoch und entfernt sie erst danach im Quellpostfach. */
  const transfer = async (from: string, toAccount: string, target: string, ids: number[], copy: boolean) => {
    const moving = items.filter(i => ids.includes(i.id) && i.account === from)
    if (!moving.length) return
    const others = items.filter(i => ids.includes(i.id) && i.account !== from).length
    const accName = (id: string) => { const a = accounts?.find(x => x.id === id); return a?.name || a?.email || '' }
    const where = folderLabel(folderOf(toAccount, target)) + (from !== toAccount ? ' (' + accName(toAccount) + ')' : '')
    const removed: number[] = []
    const movedBack: Moved[] = []
    let done = 0, failed = 0, error: string | null = null
    const gone = copy ? [] : moving.filter(i => !(from === toAccount && i.folder === target))
    const op = gone.length ? hideGone(gone) : undefined
    await enqueue(async () => { op?.sent(); try {
      const byFolder = new Map<string, MailItem[]>()
      moving.forEach(i => byFolder.set(i.folder, [...(byFolder.get(i.folder) || []), i]))
      for (const [folder, list] of byFolder) {
        if (from === toAccount && folder === target) continue
        const r = await api.post<{ done: number; failed: number; error?: string | null; moved?: number[] }>(`/api/mail/${from}/transfer`,
          { folder, uids: list.map(i => i.uid), targetAccount: toAccount, target, copy })
        done += r.done; failed += r.failed; error = r.error ?? error
        if (!copy) {
          const gone = from === toAccount ? list.map(i => i.uid) : (r.moved ?? [])
          removed.push(...list.filter(i => gone.includes(i.uid)).map(i => i.id))
          // Rückgängig (undoall) nur im selben Postfach - in ein anderes ist die Mail dort neu hochgeladen
          if (from === toAccount) movedBack.push({ account: from, from: folder, to: target, items: list })
        }
        if (r.failed) break
      }
      // Nicht übertragene (Abbruch, Fehler) erscheinen wieder
      if (gone.length) settleGone(gone, !failed && removed.length === gone.length, op)
      const undo = !failed && offerMoveBack(movedBack)
      if (failed) fail(new Error(t('{done} von {all} Mails übertragen, dann abgebrochen: {error}', { done, all: moving.length, error: error ?? '' })))
      else if (others) notify(copy
        ? t('{n} von {all} Mails nach „{folder}“ kopiert. Mails aus anderen Konten wurden nicht kopiert.', { n: moving.length, all: moving.length + others, folder: where })
        : t('{n} von {all} Mails nach „{folder}“ verschoben. Mails aus anderen Konten bleiben in ihrem Ordner.', { n: moving.length, all: moving.length + others, folder: where }), undefined, 'warning')
      else if (!undo) notify(copy ? t('Kopiert nach „{folder}“', { folder: where }) : t('Verschoben nach „{folder}“', { folder: where }))
      if (accounts) loadFolders(accounts)
    } catch (e) {
      if (gone.length) failBack(e); else fail(e)
      if (gone.length) settleGone(gone, false, op)
    } })
  }

  const reply = (all: boolean) => shown && accounts && !junkStop(shown) && setCompose(replyOf(shown, accounts.find(a => a.id === shown.account)!, all))
  const forward = () => shown && accounts && !junkStop(shown) && setCompose(forwardOf(shown, accounts.find(a => a.id === shown.account)!))
  // [feature:mailaireply]
  // Antwort mit KI: Entwurf vor Signatur und Zitat - nie automatisch senden
  const aiOn = useAiReply()
  const aiDraft = (text: string) => {
    if (!shown || !accounts || junkStop(shown)) return
    const init = replyOf(shown, accounts.find(a => a.id === shown.account)!, false)
    setCompose({ ...init, html: aiHtml(text) + init.html.replace(/^<div><br><\/div>/, '') })
  }
  // [/feature:mailaireply]

  // Rechtsklick auf Mails in der Liste
  const [ctx, setCtx] = useState<{ item: MailItem; x: number; y: number } | null>(null)
  /** Die angeklickte Mail laden, falls im Lesebereich noch eine andere steht. */
  const messageFor = async (i: MailItem): Promise<Message> =>
    message && message.account === i.account && message.folder === i.folder && message.uid === i.uid
      ? message : api.get<Message>(`/api/mail/${i.account}/message?` + q({ folder: i.folder, uid: i.uid, markSeen: false }))
  const replyTo = async (i: MailItem, all: boolean) => {
    if (!accounts) return
    try { const m = await messageFor(i); if (junkStop(m)) return; setCompose(replyOf(m, accounts.find(a => a.id === m.account)!, all)) } catch (e) { fail(e) }
  }
  // ---------------- An notizi senden, Termin aus E-Mail, als Anlage weiterleiten ----------------
  const toNote = async (i: MailItem) => {
    try {
      const r = await api.post(`/api/mail/${i.account}/to-note`, { folder: i.folder, uid: i.uid, attachments: true })
      notify(t('Als Notiz gespeichert'), !r.attached ? undefined : r.attached === 1 ? t('mit 1 Anhang') : t('mit {n} Anhängen', { n: r.attached }))
    } catch (e) { fail(e) }
  }
  const forwardAsAttachment = async (i: MailItem) => {
    if (!accounts) return
    try { const m = await messageFor(i); if (junkStop(m)) return; setCompose(forwardAsAttachmentOf(m, accounts.find(a => a.id === m.account)!)) } catch (e) { fail(e) }
  }
  const [eventDraft, setEventDraft] = useState<{ draft: EventDraft; calendars: Collection[] } | null>(null)
  const [newAlias, setNewAlias] = useState(false)
  const [adoptFor, setAdoptFor] = useState<MailItem | null>(null)
  /** Termin aus einer Mail: Betreff als Titel, Text als Beschreibung, ein Datum im Text als Vorschlag. */
  const eventFrom = async (i: MailItem) => {
    try {
      const [m, cals] = await Promise.all([messageFor(i), api.get<Collection[]>('/api/pim/collections/calendars')])
      if (junkStop(m)) return // [feature:junklock]
      const calendars = cals.filter(c => c.components.includes('VEVENT'))
      const start = guessStart(`${m.subject}\n${m.text}`)
      const desc = `${t('Von')}: ${m.from ? (m.from.name ? `${m.from.name} <${m.from.address}>` : m.from.address) : ''}\n${t('Betreff')}: ${m.subject}\n\n${m.text}`.slice(0, 8000)
      setEventDraft({
        calendars,
        draft: { calendar: calendars[0]?.id, title: m.subject, description: desc, start, end: new Date(start.getTime() + 3600000), allDay: false, rRule: '', reminder: 15, busy: true },
      })
    } catch (e) { fail(e) }
  }

  const forwardItem = async (i: MailItem) => {
    if (!accounts) return
    try { const m = await messageFor(i); if (junkStop(m)) return; setCompose(forwardOf(m, accounts.find(a => a.id === m.account)!)) } catch (e) { fail(e) }
  }
  const newMail = () => accounts?.length && setCompose({ account: composeAccount(sel, accounts), to: [], cc: [], bcc: [], subject: '', html: '' })

  // Handy: Zurueck-Taste schliesst die offene Mail
  useBackClose(mobile && !!current && !compose, () => setSelected([]))

  // Tastatur wie in Outlook
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      // Ausgeblendet (anderer Bereich aktiv): nicht reagieren
      if (!location.hash.startsWith('#/mail')) return
      const t = e.target as HTMLElement
      if (compose || t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName)) return
      // Nicht hinter offenen Dialogen und Menues
      if (eventDraft || ctx || t.closest?.('[role=dialog],[role=alertdialog],[role=menu],[role=listbox]')) return
      if (e.key === 'Delete') { e.preventDefault(); act('delete') }
      else if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'r') { e.preventDefault(); reply(true) }
      else if (e.ctrlKey && e.key.toLowerCase() === 'r') { e.preventDefault(); reply(false) }
      else if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'f') { e.preventDefault(); forward() }
      else if (e.ctrlKey && e.key.toLowerCase() === 'n') { e.preventDefault(); newMail() }
      else if (e.ctrlKey && e.key.toLowerCase() === 'q') { e.preventDefault(); act('read') }
      else if (e.ctrlKey && e.key.toLowerCase() === 'u') { e.preventDefault(); act('unread') }
      else if (e.key === 'Insert') { e.preventDefault(); act(selectedItems.every(i => i.flagged) ? 'unflag' : 'flag') }
      else if (e.ctrlKey && e.key.toLowerCase() === 'a') { e.preventDefault(); setSelected(listRef.current.map(x => x.id)) }
      else if (e.key === 'Escape' && selected.length > 1) { e.preventDefault(); setSelected(selected.slice(-1)) }
      else if (e.key === 'Enter' && selectedItems.length === 1 && !isDraft(selectedItems[0])) { e.preventDefault(); openMailWindow(selectedItems[0]) }
      else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
        e.preventDefault()
        const list = listRef.current
        const i = list.findIndex(x => x.id === selected[selected.length - 1])
        const n = list[Math.max(0, Math.min(list.length - 1, i + (e.key === 'ArrowDown' ? 1 : -1)))]
        // Umschalt: Auswahl erweitern bzw. verkleinern (wie in Outlook)
        if (n && e.shiftKey) setSelected(selected.includes(n.id) ? selected.filter(x => x !== selected[selected.length - 1]) : [...selected, n.id])
        else if (n) setSelected([n.id])
      }
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  })

  // Entwurf oeffnen: im Editor statt im Lesebereich - nur wenn die Mail selbst im Entwürfe-Ordner liegt
  const openDraft = !!current && isDraft(current)

  if (!accounts) return <Spinner style={{ margin: 'auto' }} />
  if (accounts.length === 0)
    return (
      <div className={s.empty}>
        <Text size={500} block>{t('Noch kein Mailkonto eingerichtet.')}</Text>
        <Button appearance="primary" style={{ marginTop: 12 }} onClick={() => { location.hash = '#/settings/accounts' }}>{t('Konto hinzufügen')}</Button>
      </div>
    )

  const junkFolder = currentFolder?.special === 'junk'
  const snoozedFolder = currentFolder?.special === 'snoozed'

  const snooze = async (until: Date) => {
    const jobs = groups(), gone = selectedItems
    const op = hideGone(gone)
    await enqueue(async () => { op.sent(); try {
      for (const g of jobs) await api.post(`/api/mail/${g.account}/snooze`, { folder: g.folder, uids: g.uids, until: until.toISOString() })
      settleGone(gone, true, op)
      notify(t('Wiedervorlage'), t('Kommt {when} zurück in den Posteingang.', { when: fmtDate(until.toISOString()) }))
      api.get('/api/mail/snoozes').then(setSnoozes).catch(() => {})
      if (accounts) setTimeout(() => loadFolders(accounts), 1500)
    } catch (e) { failBack(e); settleGone(gone, false, op) } })
  }
  const unsnooze = async () => {
    const jobs = groups(), gone = selectedItems
    const op = hideGone(gone)
    await enqueue(async () => { op.sent(); try {
      for (const g of jobs) await api.post(`/api/mail/${g.account}/unsnooze`, { folder: g.folder, uids: g.uids })
      settleGone(gone, true, op)
      notify(t('Zurück im Posteingang'))
    } catch (e) { failBack(e); settleGone(gone, false, op) } })
  }
  const snoozeOf = (i: MailItem) =>
    snoozes.find(x => x.account === i.account && ((x.messageId && x.messageId === i.messageId) || (x.uid && x.uid === i.uid && snoozedFolder)))?.until

  /** Aktionen fuer eine Mail - Rechtsklick in der Liste und auf dem Handy das Menue "Mehr". */
  const actionList = (item: MailItem, close: () => void, extra?: React.ReactNode) => {
    const many = selectedItems.length > 1
    const allFlagged = selectedItems.length > 0 && selectedItems.every(i => i.flagged)
    const allRead = selectedItems.length > 0 && selectedItems.every(i => i.seen)
    const accountFolders = (folders[item.account] || []).filter(f => f.selectable && f.special !== 'flagged')
    const one = !many
    const lock = featureOn('junklock') && (currentFolder?.special === 'junk' || (!!shown?.junk && shown.uid === item.uid)) // [feature:junklock]
    const openEl = one && !isDraft(item) && <MenuItem icon={<Open20Regular />} onClick={() => { openMailWindow(item); close() }}>{t('In neuem Fenster öffnen')}</MenuItem>
    const replyEl = one && !lock && <MenuItem icon={<ArrowReply20Regular />} onClick={() => { replyTo(item, false); close() }}>{t('Antworten')}</MenuItem>
    const replyAllEl = one && !lock && <MenuItem icon={<ArrowReplyAll20Regular />} onClick={() => { replyTo(item, true); close() }}>{t('Allen antworten')}</MenuItem>
    const forwardEl = one && !lock && <MenuItem icon={<ArrowForward20Regular />} onClick={() => { forwardItem(item); close() }}>{t('Weiterleiten')}</MenuItem>
    const attachEl = one && !lock && <MenuItem icon={<Attach20Regular />} onClick={() => { forwardAsAttachment(item); close() }}>{t('Als Anlage weiterleiten')}</MenuItem>
    const aliasEl = one && item.alias && (item.aliasState === 'unknown'
      ? <MenuItem icon={<PersonMail20Regular />} onClick={() => { setAdoptFor(item); close() }}>{t('Als Alias anlegen …')}</MenuItem>
      : item.aliasState && <MenuItem icon={<PersonMail20Regular />} onClick={() => { setAliasBlocked(item.alias!, item.aliasState !== 'blocked').catch(fail); close() }}>
        {item.aliasState === 'blocked' ? t('Alias {a} entsperren', { a: item.alias }) : t('Alias {a} sperren', { a: item.alias })}</MenuItem>)
    const eventEl = one && !lock && <MenuItem icon={<CalendarAdd20Regular />} onClick={() => { eventFrom(item); close() }}>{t('Termin aus E-Mail')}</MenuItem>
    const noteEl = one && !lock && <MenuItem icon={<Note20Regular />} onClick={() => { toNote(item); close() }}>{t('Als Notiz speichern')}</MenuItem>
    const ruleEl = one && <MenuItem icon={<Filter20Filled />} onClick={() => { ruleFromMail(item, shown).catch(fail); close() }}>{t('Regel erstellen …')}</MenuItem>
    const similarEl = one && featureOn('similarmail') && <MenuItem icon={<DocumentSearch20Regular />} onClick={() => { findSimilar(item); close() }}>{t('Ähnliche Mails suchen')}</MenuItem>
    const dmsEl = featureOn('ctxall') && !lock && <><DmsMailMenuItem items={selectedItems.length ? selectedItems : [item]} onDone={close} />
    </>
    const archiveEl = <MenuItem icon={<Archive20Regular />} onClick={() => { act('archive'); close() }}>{t('Archivieren')}</MenuItem>
    const focusEl = inboxSel && focusInfo?.enabled && (
      <Menu>
        <MenuTrigger disableButtonEnhancement><MenuItem icon={<ArrowSwap20Regular />}>{focusTab === 'other' ? t('Nach Relevant verschieben') : t('Nach Sonstige verschieben')}</MenuItem></MenuTrigger>
        <MenuPopover>
          <MenuList>
            <MenuItem onClick={() => { moveFocus(focusTab === 'other'); close() }}>{t('Nur diese Mail')}</MenuItem>
            <MenuItem onClick={() => { moveFocus(focusTab === 'other', true); close() }}>{t('Immer für diesen Absender')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
    )
    const moveEl = (
      <Menu>
        <MenuTrigger disableButtonEnhancement><MenuItem icon={<FolderArrowRight20Regular />}>{t('mail|Verschieben')}</MenuItem></MenuTrigger>
        <MenuPopover style={{ maxHeight: 420, overflowY: 'auto' }}>
          <MenuList>
            {accountFolders.sort((a, b) => a.path.localeCompare(b.path)).map(f => (
              <MenuItem key={f.path} onClick={() => { move(item.account, f.path); close() }}>
                {'\u00A0'.repeat(4 * (f.path.split(f.delimiter).length - 1))}{folderLabel(f)}
              </MenuItem>
            ))}
          </MenuList>
        </MenuPopover>
      </Menu>
    )
    const categoryEl = (
      <Menu>
        <MenuTrigger disableButtonEnhancement><MenuItem icon={<Tag20Regular />}>{t('Kategorisieren')}</MenuItem></MenuTrigger>
        <MenuPopover>
          <CategoryMenuList selectedKeywords={selectedItems.map(i => i.keywords ?? [])}
            onToggle={(k, on) => { categorize(k, on); close() }} onClear={() => { clearCategories(); close() }} />
        </MenuPopover>
      </Menu>
    )
    const followEl = (
      <Menu>
        <MenuTrigger disableButtonEnhancement><MenuItem icon={allFlagged ? <Flag20Filled /> : <Flag20Regular />}>{t('Nachverfolgen')}</MenuItem></MenuTrigger>
        <MenuPopover>
          <FollowUpItems flagged={allFlagged} onPick={due => { followUp(due); close() }} onDone={() => { followUp(null, true); close() }} />
        </MenuPopover>
      </Menu>
    )
    const readEl = (
      <MenuItem icon={allRead ? <MailUnread20Regular /> : <MailRead20Regular />} onClick={() => { act(allRead ? 'unread' : 'read'); close() }}>
        {allRead ? t('Als ungelesen markieren') : t('Als gelesen markieren')}
      </MenuItem>
    )
    const snoozeEl = snoozedFolder ? (
      <MenuItem icon={<ArrowUndo20Regular />} onClick={() => { unsnooze(); close() }}>{t('Wiedervorlage aufheben')}</MenuItem>
    ) : (
      <Menu>
        <MenuTrigger disableButtonEnhancement><MenuItem icon={<Clock20Regular />}>{t('Wiedervorlage')}</MenuItem></MenuTrigger>
        <MenuPopover>
          <MenuList>
            {snoozeOptions().map(o => (
              <MenuItem key={o.label} secondaryContent={o.hint} onClick={() => { snooze(o.when); close() }}>{o.label}</MenuItem>
            ))}
          </MenuList>
        </MenuPopover>
      </Menu>
    )
    const expiryEl = <ExpiryMenuItems targets={selectedItems} onClose={close} onError={fail}
      onDone={w => notify(w === 'set' ? t('Entfernen nach Zeit eingeplant') : t('Entfernen nach Zeit zurückgenommen'))} />
    const junkEl = junkFolder
      ? <MenuItem icon={<ShieldCheckmark20Regular />} onClick={() => { act('notjunk'); close() }}>{t('Kein Junk')}</MenuItem>
      : <MenuItem icon={<ShieldError20Regular />} onClick={() => { act('junk'); close() }}>{t('Als Junk melden')}</MenuItem>
    const reclassifyEl = inInbox && <MenuItem icon={<ArrowSync20Regular />} onClick={() => { reclassify(); close() }}>{t('Neu einordnen')}</MenuItem>
    const deleteEl = <MenuItem icon={<Delete20Regular />} onClick={() => { act('delete'); close() }}>{t('Löschen')}</MenuItem>
    const sub = (icon: JSX.Element, label: string, children: React.ReactNode) => (
      <Menu>
        <MenuTrigger disableButtonEnhancement><MenuItem icon={icon}>{label}</MenuItem></MenuTrigger>
        <MenuPopover><MenuList>{children}</MenuList></MenuPopover>
      </Menu>
    )

    // Gruppiert (Kennung ctxgroups): Antworten, Ablegen, Kennzeichnen, Zeitsteuerung, Aktionen, Junk, Löschen
    if (featureOn('ctxgroups')) return (
      <MenuList>
        {many && <MenuItem disabled>{t('{n} Mails ausgewählt', { n: selectedItems.length })}</MenuItem>}
        {openEl}
        {one && <MenuDivider />}
        {replyEl}
        {replyAllEl}
        {one && !lock && (
          <Menu>
            <MenuSplitGroup>
              {forwardEl}
              <MenuTrigger disableButtonEnhancement><MenuItem aria-label={t('Weitere Arten weiterzuleiten')} /></MenuTrigger>
            </MenuSplitGroup>
            <MenuPopover><MenuList>{attachEl}</MenuList></MenuPopover>
          </Menu>
        )}
        {one && <MenuDivider />}
        {archiveEl}
        {!lock && moveEl}
        {focusEl}
        <MenuDivider />
        {readEl}
        {!lock && categoryEl}
        {!lock && followEl}
        {sub(<Clock20Regular />, t('Zeitsteuerung'), <>
          {!lock && snoozeEl}
          {expiryEl}
        </>)}
        <MenuDivider />
        {!lock && (one || dmsEl) && sub(<ArrowExportLtr20Regular />, t('Aktionen'), <>{eventEl}{noteEl}{dmsEl}</>)}
        {similarEl}
        {ruleEl}
        {aliasEl}
        {extra && <><MenuDivider />{extra}</>}
        <MenuDivider />
        {junkEl}
        {reclassifyEl}
        <MenuDivider />
        {deleteEl}
      </MenuList>
    )
    return (
      <MenuList>
        {many && <MenuItem disabled>{t('{n} Mails ausgewählt', { n: selectedItems.length })}</MenuItem>}
        {openEl}
        {one && <MenuDivider />}
        {replyEl}
        {replyAllEl}
        {forwardEl}
        {aliasEl}
        {attachEl}
        {eventEl}
        {noteEl}
        {ruleEl}
        {similarEl}
        {dmsEl}
        <MenuDivider />
        {archiveEl}
        {focusEl}
        {!lock && moveEl}
        <MenuDivider />
        {!lock && categoryEl}
        {!lock && followEl}
        {readEl}
        {!lock && snoozeEl}
        {expiryEl}
        {extra && <><MenuDivider />{extra}</>}
        <MenuDivider />
        {junkEl}
        {reclassifyEl}
        {/* Löschen zuletzt, mit Strich davor (Reihenfolge der Kontextmenüs) */}
        <MenuDivider />
        {deleteEl}
      </MenuList>
    )
  }

  // Beim Schreiben wechselt das Menüband auf den Reiter „Nachricht“ (wie in Outlook); Start und Ansicht bleiben anwählbar
  const messageRibbon = featureOn('mailribbon') && !mobile && !!compose
  const ribbonTab = messageRibbon ? composeTab : prefs.ribbonTab ?? 'start'
  const todoButton = todoFloating && (prefs.todoBar ?? []).length > 0 && (
    <Tooltip content={t('Aufgabenleiste')} relationship="label">
      <Button appearance={todoOpen ? 'primary' : 'subtle'} icon={<CalendarLtr20Regular />} onClick={() => setTodoOpen(o => !o)} style={{ marginRight: mobile ? 0 : 8 }} />
    </Tooltip>
  )
  // Ordnerbereich: normal, minimiert (Favoriten senkrecht) oder aus; früher nur an/aus (folderPane)
  const folderMode: 'normal' | 'min' | 'off' = prefs.folderPaneMode ?? ((prefs.folderPane ?? true) ? 'normal' : 'off')
  const minItems: { key: string; label: string; unread: number; sel: { account: string; folder: string } }[] = (() => {
    // Mehrere Postfächer: „Alle Posteingänge“ steht oben, wie in der Ordnerliste
    const inboxes = (accounts ?? []).map(x => (folders[x.id] ?? []).find(f => f.special === 'inbox')).filter(Boolean)
    const all = (accounts?.length ?? 0) > 1 ? [{ key: '__unified', label: t('Alle Posteingänge'), unread: inboxes.reduce((n, f) => n + (f!.unread || 0), 0), sel: { account: accounts![0].id, folder: '__unified' } }] : []
    const favs = (prefs.favorites ?? []).map(k => {
      const [account, ...rest] = k.split('|')
      const f = folders[account]?.find(x => x.path === rest.join('|'))
      return f ? { key: k, label: folderLabel(f), unread: f.unread || 0, sel: { account, folder: f.path } } : null
    }).filter(Boolean) as { key: string; label: string; unread: number; sel: { account: string; folder: string } }[]
    if (favs.length) return [...all, ...favs]
    // Ohne Favoriten: die Posteingänge
    return [...all, ...(accounts ?? []).map(a => (folders[a.id] ?? []).find(f => f.special === 'inbox') && { a, f: (folders[a.id] ?? []).find(f => f.special === 'inbox')! })
      .filter(Boolean).map(x => ({ key: x!.a.id, label: (accounts?.length ?? 0) > 1 ? x!.a.name || x!.a.email : folderLabel(x!.f), unread: x!.f.unread || 0, sel: { account: x!.a.id, folder: x!.f.path } }))]
  })()
  const viewItems = <>
    {/* Abstände: je drei Stufen für Ordnerliste und Nachrichtenliste, wie in den Einstellungen */}
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<TextLineSpacing20Regular />} label={t('Abstände')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ folder: [prefs.folderDensity ?? prefs.density ?? 'normal'], list: [prefs.listDensity ?? prefs.density ?? 'normal'] }}
          onCheckedValueChange={(_, d) => update(d.name === 'folder' ? { folderDensity: d.checkedItems[0] as any } : { listDensity: d.checkedItems[0] as any })}>
          <MenuGroup><MenuGroupHeader>{t('Ordnerliste')}</MenuGroupHeader>
            <MenuItemRadio name="folder" value="compact">{t('Kompakt')}</MenuItemRadio>
            <MenuItemRadio name="folder" value="normal">{t('Normal')}</MenuItemRadio>
            <MenuItemRadio name="folder" value="roomy">{t('Geräumig')}</MenuItemRadio>
          </MenuGroup>
          <MenuDivider />
          <MenuGroup><MenuGroupHeader>{t('Nachrichtenliste')}</MenuGroupHeader>
            <MenuItemRadio name="list" value="compact">{t('Kompakt')}</MenuItemRadio>
            <MenuItemRadio name="list" value="normal">{t('Normal')}</MenuItemRadio>
            <MenuItemRadio name="list" value="roomy">{t('Geräumig')}</MenuItemRadio>
          </MenuGroup>
        </MenuList>
      </MenuPopover>
    </Menu>
    <RibbonDivider />
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ fp: [folderMode], fav: [...((prefs.showUnified ?? true) ? ['uni'] : []), ...((prefs.showFavorites ?? true) ? ['fav'] : [])] }}
          onCheckedValueChange={(_, d) => d.name === 'fp' ? update({ folderPaneMode: d.checkedItems[0] as 'normal' | 'min' | 'off', folderPane: d.checkedItems[0] !== 'off' })
            : update({ showFavorites: d.checkedItems.includes('fav'), showUnified: d.checkedItems.includes('uni') })}>
          <MenuItemRadio name="fp" value="normal">{t('Normal')}</MenuItemRadio>
          <MenuItemRadio name="fp" value="min">{t('Minimiert')}</MenuItemRadio>
          <MenuItemRadio name="fp" value="off">{t('Aus')}</MenuItemRadio>
          <MenuDivider />
          <MenuItemCheckbox name="fav" value="uni">{t('„Alle Posteingänge“ anzeigen')}</MenuItemCheckbox>
          <MenuItemCheckbox name="fav" value="fav">{t('Favoriten anzeigen')}</MenuItemCheckbox>
        </MenuList>
      </MenuPopover>
    </Menu>
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<PanelRight20Regular />} label={t('Lesebereich')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ rp: [pane] }} onCheckedValueChange={(_, d) => update({ readingPane: d.checkedItems[0] as any })}>
          <MenuItemRadio name="rp" value="right">{t('Rechts anzeigen')}</MenuItemRadio>
          <MenuItemRadio name="rp" value="bottom">{t('Unten anzeigen')}</MenuItemRadio>
          <MenuItemRadio name="rp" value="off">{t('Ausblenden')}</MenuItemRadio>
        </MenuList>
      </MenuPopover>
    </Menu>
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<PanelRightContract20Regular />} label={t("Aufgaben\u00ADleiste")} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ tb: prefs.todoBar ?? [] }} onCheckedValueChange={(_, d) => update({ todoBar: d.checkedItems as any })}>
          <MenuItemCheckbox name="tb" value="calendar">{t('Kalender')}</MenuItemCheckbox>
          <MenuItemCheckbox name="tb" value="people">{t('Personen')}</MenuItemCheckbox>
          <MenuItemCheckbox name="tb" value="tasks">{t('Aufgaben')}</MenuItemCheckbox>
          <MenuDivider />
          <MenuItem onClick={() => update({ todoBar: [] })}>{t('Aus')}</MenuItem>
        </MenuList>
      </MenuPopover>
    </Menu>
    <RibbonDivider />
    <RibbonButton icon={<TextDescription20Regular />} label={t("Vorschau\u00ADtext")} pressed={(prefs.previewLines ?? ((prefs.showPreview ?? true) ? 1 : 0)) > 0}
      onClick={() => { const on = (prefs.previewLines ?? ((prefs.showPreview ?? true) ? 1 : 0)) > 0; update({ showPreview: !on, previewLines: on ? 0 : 1 }) }} />
    <RibbonButton icon={<PersonCircle20Regular />} label={t("Absender\u00ADbilder")} pressed={prefs.showAvatars ?? true}
      onClick={() => update({ showAvatars: !(prefs.showAvatars ?? true) })} />
    {/* [feature:mailtocolumn] */}
    {featureOn('mailtocolumn') && <RibbonButton icon={<PersonMail20Regular />} label={t('Spalte „An“')} pressed={toColumn}
      onClick={() => update({ mailToColumn: !toColumn })} />}
    {/* [/feature:mailtocolumn] */}
    {/* [feature:mailthreads] */}
    {featureOn('mailthreads') && <RibbonButton icon={<Chat20Regular />} label={t('Als Unterhaltungen anzeigen')} pressed={!!prefs.mailThreads}
      disabled={listSort !== 'newest' && listSort !== 'oldest'} onClick={() => update({ mailThreads: !prefs.mailThreads })} />}
    {/* [/feature:mailthreads] */}
    {/* Kategorien als Ordner im Ordnerbereich */}
    <RibbonButton icon={<FolderSearch20Regular />} label={t('Suchordner')} pressed={prefs.searchFolders ?? true}
      onClick={() => update({ searchFolders: !(prefs.searchFolders ?? true) })} />
    <RibbonButton icon={<Tag20Regular />} label={t("Kategorie­ordner")} pressed={prefs.categoryFolders ?? true}
      onClick={() => update({ categoryFolders: !(prefs.categoryFolders ?? true) })} />
    {dark && <>
      <RibbonDivider />
      <RibbonButton icon={<WeatherMoon20Regular />} label={t('Dunkel darstellen')} pressed={invert} onClick={() => setInvert(v => !v)} />
    </>}
    <RibbonDivider />
    <RibbonButton icon={<Settings20Regular />} label={t("Ansicht\u00ADeinstellungen")} onClick={() => { location.hash = '#/settings/list' }} />
    <RibbonDivider />
    <RibbonDisplayMenu />
  </>
  // Neuer Mailordner auf oberster Ebene; mehrere Konten: erst das Konto wählen
  const newFolderIn = (account: string) => createMailFolder(account, '', () => accounts && loadFolders(accounts)).catch(fail)
  const newFolderButton = (accounts?.length ?? 0) > 1 ? (
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<FolderAdd20Regular />} label={t('Neuer Ordner')} /></MenuTrigger>
      <MenuPopover>
        <MenuList>
          {accounts!.map(a => <MenuItem key={a.id} onClick={() => newFolderIn(a.id)}>{a.name || a.email}</MenuItem>)}
        </MenuList>
      </MenuPopover>
    </Menu>
  ) : <RibbonButton icon={<FolderAdd20Regular />} label={t('Neuer Ordner')} disabled={!accounts?.length} onClick={() => accounts?.[0] && newFolderIn(accounts[0].id)} />
  const startItems = <>
    <RibbonButton primary icon={<MailAdd20Regular />} label={t('Neue E-Mail')} onClick={newMail} />
    {featureOn('catchall') && <RibbonButton icon={<PersonMail20Regular />} label={t('Catchall-Alias')} onClick={() => setNewAlias(true)} />}
    {newFolderButton}
    <RibbonDivider />
    <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!selected.length} onClick={() => act('delete')} />
    <RibbonButton icon={<Archive20Regular />} label={t('Archivieren')} disabled={!selected.length} onClick={() => act('archive')} />
    {junkFolder
      ? <RibbonButton icon={<ShieldCheckmark20Regular />} label={t('Kein Junk')} disabled={!selected.length} onClick={() => act('notjunk')} />
      : <RibbonButton icon={<ShieldError20Regular />} label={t('Junk')} disabled={!selected.length} onClick={() => act('junk')} />}
    <MoveMenu folders={moveAccount ? (folders[moveAccount] || []).filter(f => f.special !== 'flagged') : []} disabled={!selected.length || allAccounts || !moveAccount || junkLock} onMove={t => moveAccount && move(moveAccount, t)} />
    {snoozedFolder
      ? <RibbonButton icon={<ArrowUndo20Regular />} label={t('Wiedervorlage aufheben')} disabled={!selected.length} onClick={unsnooze} />
      : <SnoozeMenu disabled={!selected.length || junkLock} onSnooze={snooze} />}
    <RibbonDivider />
    <RibbonButton icon={<ArrowReply20Regular />} label={t('Antworten')} disabled={!shown || junkLock} onClick={() => reply(false)} />
    <RibbonButton icon={<ArrowReplyAll20Regular />} label={t('Allen antworten')} disabled={!shown || junkLock} onClick={() => reply(true)} />
    <RibbonButton icon={<ArrowForward20Regular />} label={t('Weiterleiten')} disabled={!shown || junkLock} onClick={forward} />
    {aiOn && <AiReplyMenu source={shown} disabled={!shown || junkLock} onDraft={aiDraft} />} {/* [feature:mailaireply] */}
    <RibbonButton icon={<CalendarAdd20Regular />} label={t('Termin')} disabled={!current || junkLock} onClick={() => current && eventFrom(current)} />
    <RibbonButton icon={<Note20Regular />} label={t('Als Notiz')} disabled={!current || junkLock} onClick={() => current && toNote(current)} />
    <DmsMailRibbon items={selectedItems} disabled={!selected.length || junkLock} />
    <RibbonDivider />
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <RibbonButton icon={<Tag20Regular />} label={t('Kategorisieren')} disabled={!selected.length || junkLock} />
      </MenuTrigger>
      <MenuPopover>
        <CategoryMenuList selectedKeywords={selectedItems.map(i => i.keywords ?? [])} onToggle={categorize} onClear={clearCategories} />
      </MenuPopover>
    </Menu>
    {(() => {
      const allFlagged = selectedItems.length > 0 && selectedItems.every(i => i.flagged)
      return <FollowUpRibbon disabled={!selected.length || junkLock} flagged={allFlagged} onPick={due => followUp(due)} onDone={() => followUp(null, true)} />
    })()}
    {(() => {
      const allRead = selectedItems.length > 0 && selectedItems.every(i => i.seen)
      return <RibbonButton icon={allRead ? <MailUnread20Regular /> : <MailRead20Regular />}
        label={allRead ? t('Als ungelesen markieren') : t('Als gelesen markieren')} disabled={!selected.length} onClick={() => act(allRead ? 'unread' : 'read')} />
    })()}
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <RibbonButton icon={<MoreHorizontal20Regular />} label={t('Mehr')} disabled={!selected.length} />
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          <MenuItem icon={<Attach20Regular />} disabled={!current || junkLock} onClick={() => current && forwardAsAttachment(current)}>{t('Als Anlage weiterleiten')}</MenuItem>
          {/* Termin, Notiz, gelesen/ungelesen stehen als eigene Knöpfe im Menüband */}
          <MenuItem icon={<Filter20Filled />} disabled={!current} onClick={() => current && ruleFromMail(current, shown).catch(fail)}>{t('Regel erstellen …')}</MenuItem>
          {featureOn('similarmail') && <MenuItem icon={<DocumentSearch20Regular />} disabled={!current} onClick={() => current && findSimilar(current)}>{t('Ähnliche Mails suchen')}</MenuItem>}
          <MenuItem icon={<Print20Regular />} disabled={!current} onClick={() => window.dispatchEvent(new Event('dm-mail-print'))}>{t('Drucken')}</MenuItem>
          <MenuItem icon={<Code20Regular />} disabled={!current || junkLock} onClick={() => current && saveMailSource(current)}>{t('Quelltext (.eml)')}</MenuItem>
          {featureOn('mailexpiry') && <MenuDivider />}
          <ExpiryMenuItems targets={selectedItems} onError={fail}
            onDone={w => notify(w === 'set' ? t('Entfernen nach Zeit eingeplant') : t('Entfernen nach Zeit zurückgenommen'))} />
          <MenuDivider />
          <MenuItem icon={<Flag20Regular />} onClick={() => act('flag')}>{t('Markieren (Einfg)')}</MenuItem>
          <MenuItem onClick={() => act('unflag')}>{t('Markierung entfernen')}</MenuItem>
          {!junkFolder && <MenuItem icon={<ShieldCheckmark20Regular />} onClick={() => act('notjunk')}>{t('Kein Junk (als gut lernen)')}</MenuItem>}
        </MenuList>
      </MenuPopover>
    </Menu>
  </>
  // Handy: nur das Noetigste als Symbol, der Rest im Menue "Mehr" (wie beim Rechtsklick)
  const mobileStart = <>
    <RibbonButton primary icon={<MailAdd20Regular />} label={t('Neue E-Mail')} onClick={newMail} />
    {!current && featureOn('catchall') && <RibbonButton icon={<PersonMail20Regular />} label={t('Catchall-Alias')} onClick={() => setNewAlias(true)} />}
    {!current && newFolderButton}
    {current && <>
      <RibbonDivider />
      <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} onClick={() => act('delete')} />
      <RibbonButton icon={<Archive20Regular />} label={t('Archivieren')} onClick={() => act('archive')} />
      <RibbonButton icon={<ArrowReply20Regular />} label={t('Antworten')} disabled={!shown || junkLock} onClick={() => reply(false)} />
      <RibbonButton icon={<ArrowForward20Regular />} label={t('Weiterleiten')} disabled={!shown || junkLock} onClick={forward} />
      {aiOn && <AiReplyMenu source={shown} disabled={!shown || junkLock} onDraft={aiDraft} />} {/* [feature:mailaireply] */}
      <Menu>
        <MenuTrigger disableButtonEnhancement><RibbonButton icon={<MoreHorizontal20Regular />} label={t('Mehr')} /></MenuTrigger>
        <MenuPopover style={{ maxHeight: '70vh', overflowY: 'auto' }}>{actionList(current, () => {}, <>
          <MenuItem icon={<Print20Regular />} onClick={() => window.dispatchEvent(new Event('dm-mail-print'))}>{t('Drucken')}</MenuItem>
          <MenuItem icon={<Code20Regular />} disabled={junkLock} onClick={() => saveMailSource(current)}>{t('Quelltext (.eml)')}</MenuItem>
        </>)}</MenuPopover>
      </Menu>
    </>}
  </>

  return (
    <div className={s.outer} ref={rootRef}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Ordnerbereich */}
      {mobile ? (
        <MobileRibbonRow onMenu={() => setFoldersOpen(o => !o)} view={ribbonTab === 'view'}
          onView={() => update({ ribbonTab: ribbonTab === 'view' ? 'start' : 'view' })} after={todoButton}>
          {ribbonTab === 'view' ? viewItems : mobileStart}
        </MobileRibbonRow>
      ) : <RibbonFrame area="mail" tabs={
      <div className={s.tabsRow}>
        {foldersCollapsed && (
          <Tooltip content={t('Ordnerbereich')} relationship="label">
            <Button appearance="subtle" icon={<Navigation20Regular />} onClick={() => setFoldersOpen(o => !o)} style={{ marginLeft: 6 }} />
          </Tooltip>
        )}
        <div style={{ flex: 1, minWidth: 0 }}>
          {messageRibbon
            ? <RibbonTabs tabs={[['start', t('ribbon|Start')], ['message', t('Nachricht')], ['view', t('Ansicht')]]} value={composeTab} onChange={v => setComposeTab(v as any)} />
            : <RibbonTabs tabs={[['start', t('ribbon|Start')], ['view', t('Ansicht')]]} value={prefs.ribbonTab ?? 'start'} onChange={v => update({ ribbonTab: v as any })} />}
        </div>
        {todoButton}
      </div>}>
      <Ribbon>{ribbonTab === 'message' ? <div ref={setRibbonSlot} style={{ display: 'contents' }} /> : ribbonTab === 'view' ? viewItems : startItems}</Ribbon>
      </RibbonFrame>}
    <div className={s.root}>
      {/* Minimiert (wie Outlook): aufgeklappt schweben die Ordner über dem Inhalt und klappen nach der Auswahl wieder zu */}
      {(foldersCollapsed || folderMode === 'min') && foldersOpen && <div className={s.backdrop} onClick={() => setFoldersOpen(false)} />}
      {/* Ordnerbereich minimiert (wie Outlook): schmale Leiste mit den Favoriten senkrecht, oben der Knopf zum Aufklappen */}
      {!foldersCollapsed && folderMode === 'min' && <div style={{ width: 34, flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column',
        alignItems: 'center', gap: 2, paddingTop: 6, overflow: 'hidden' }}>
        <Button appearance="subtle" size="small" icon={<ChevronRight20Regular />} aria-label={t('Ordnerbereich aufklappen')} title={t('Ordnerbereich aufklappen')}
          onClick={() => setFoldersOpen(true)} />
        {minItems.map(x => <button key={x.key} title={x.label} onClick={() => { if (compose) leaveCompose(); autoSelect.current = true; setSel(x.sel); setSelected([]); setMessage(null); setSearch(''); setSimilarTo(null) }}
          style={{ writingMode: 'vertical-rl', transform: 'rotate(180deg)', border: 'none', cursor: 'pointer', padding: '8px 4px', borderRadius: 4, whiteSpace: 'nowrap', fontSize: 13,
            background: sel?.folder === x.sel.folder && (x.sel.folder === '__unified' || sel?.account === x.sel.account) ? tokens.colorBrandBackground2 : 'transparent',
            color: tokens.colorNeutralForeground1, fontWeight: x.unread ? 600 : 400, maxHeight: 200, overflow: 'hidden', textOverflow: 'ellipsis' }}>
          {x.label}{x.unread ? <span style={{ color: tokens.colorBrandForeground1 }}> [{x.unread}]</span> : null}
        </button>)}
      </div>}
      <div className={mergeClasses(s.folders, (foldersCollapsed || folderMode === 'min') && s.flyout)}
        style={foldersCollapsed ? (foldersOpen ? { left: 0, width: mobile ? 'min(86vw, 320px)' : 280 } : { display: 'none' })
          : folderMode === 'min' ? (foldersOpen ? { left: 34, width: folderW.width } : { display: 'none' })
          : folderMode === 'normal' ? { width: folderW.width, position: 'relative' } : { display: 'none' }}>
        {!foldersCollapsed && folderMode === 'normal' && folderW.handle('right', true)}
        {/* Angeheftet: Pfeil zum Minimieren; aufgeklappt: Stecknadel zum Anheften - rechts in der obersten Zeile der Ordnerliste */}
        <FolderPane corner={!foldersCollapsed && (folderMode === 'normal' || folderMode === 'min') && (folderMode === 'normal'
            ? <Button appearance="subtle" size="small" icon={<ChevronLeft20Regular />} aria-label={t('Ordnerbereich minimieren')} title={t('Ordnerbereich minimieren')}
              onClick={() => { setFoldersOpen(false); update({ folderPaneMode: 'min', folderPane: true }) }} />
            : <Button appearance="subtle" size="small" icon={<Pin20Regular />} aria-label={t('Ordnerbereich anheften')} title={t('Ordnerbereich anheften')}
              onClick={() => { setFoldersOpen(false); update({ folderPaneMode: 'normal', folderPane: true }) }} />)}
          accounts={accounts} folders={paneFolders} selection={sel} status={status}
          onSelect={x => { if (compose) leaveCompose(); autoSelect.current = true; setSel(x); setSelected([]); setMessage(null); setSearch(''); setSimilarTo(null); setFoldersOpen(false) }}
          onDrop={(account, target, ids, from, copy) => from === account && !copy ? move(account, target, ids) : transfer(from, account, target, ids, copy)} onCategoryDrop={categorizeDropped}
          onChanged={() => loadFolders(accounts)} onMarkRead={markFolderRead} />
        {/* Unter den Ordnern, immer da: oben eingeblendet verschöbe er die ganze Ordnerleiste; Linie trennt ihn von den Ordnern */}
        {accounts && accounts.length > 0 && <div style={{ borderTop: `1px solid ${tokens.colorNeutralStroke2}`, margin: '0 8px', paddingTop: (prefs.folderDensity ?? prefs.density) === 'compact' ? 3 : 6, flexShrink: 0 }} />}
        {accounts && accounts.length > 0 && (
          <Button appearance={sel?.folder === '__outbox' ? 'secondary' : 'subtle'} icon={<Clock20Regular />}
            size={(prefs.folderDensity ?? prefs.density) === 'compact' ? 'small' : 'medium'}
            style={{ justifyContent: 'flex-start', margin: (prefs.folderDensity ?? prefs.density) === 'compact' ? '0 6px 2px' : (prefs.folderDensity ?? prefs.density) === 'roomy' ? '0 8px 6px' : '0 8px 2px', flexShrink: 0, fontWeight: sel?.folder === '__outbox' ? tokens.fontWeightSemibold : tokens.fontWeightRegular }}
            onClick={() => { if (compose) leaveCompose(); setSel({ account: accounts[0].id, folder: '__outbox' }); setSelected([]); setMessage(null); setSearch(''); setSimilarTo(null); setFoldersOpen(false) }}>
            {t('Postausgang')}{(status?.outbox ?? 0) > 0 ? ` (${status!.outbox})` : ''}
          </Button>
        )}
      </div>
      <div className={s.content}>
        {sel?.folder === '__outbox' && accounts && <OutboxView accounts={accounts} version={version} onChanged={() => window.dispatchEvent(new Event('dmoffice:mailstatus'))} />}
        {undo && (
          <div style={{ position: 'absolute', left: '50%', bottom: 16, transform: 'translateX(-50%)', zIndex: 20, display: 'flex', alignItems: 'center', gap: 12,
            padding: '8px 8px 8px 16px', borderRadius: 8, background: tokens.colorNeutralBackgroundInverted, color: tokens.colorNeutralForegroundInverted, boxShadow: tokens.shadow16 }}>
            <Text style={{ color: 'inherit' }}>{t('Wird in {n} s gesendet', { n: Math.max(0, Math.ceil((new Date(undo.sendAt).getTime() - Date.now()) / 1000)) })}</Text>
            <Button size="small" onClick={recall}>{t('Zurückrufen')}</Button>
          </div>
        )}
        <div style={{ flex: 1, display: sel?.folder === '__outbox' || sel?.folder === '__nudge' ? 'none' : 'flex', minHeight: 0, minWidth: 0, position: 'relative' }}>
        <div className={mergeClasses(s.panes, pane === 'bottom' && s.panesBottom)}>
          <div className={mergeClasses(s.list, pane === 'bottom' && s.listBottom)} style={pane === 'off' && (current || compose) ? { display: 'none' } : pane === 'off' ? { width: '100%', borderRight: 'none' } : pane === 'right' ? { width: listW.width, position: 'relative' }
            : pane === 'bottom' && paneSize && listH.height != null ? { height: listH.height } : undefined}>
            {pane === 'right' && listW.handle('right', true)}
            <div className={s.listHead}>
              <div style={{ position: 'relative' }}>
                <SearchBox placeholder={t('Suchen (von:, an:, betreff:)')} value={similarTo ? similarLabel : search}
                  onChange={(_, d) => {
                    // „Ähnlich wie …“ steht im Suchfeld: Leeren oder Weitertippen beendet es und beginnt eine normale Suche
                    if (similarTo) { setSimilarTo(null); setSearch(d.value.startsWith(similarLabel) ? d.value.slice(similarLabel.length).trimStart() : '') }
                    else setSearch(d.value)
                  }} size="medium" style={{ width: '100%', maxWidth: 'none' }}
                  // Gefiltert (etwa per Klick aufs Absenderbild): das × zum Aufheben bleibt sichtbar, nicht nur mit Fokus
                  contentAfter={featureOn('mailavatarfilter') && search ? { style: { height: 'auto', width: 'auto', overflow: 'visible', paddingLeft: 8, paddingRight: 8 } } : undefined} // [feature:mailavatarfilter]
                  onFocus={() => setHistOpen(true)} onBlur={() => setTimeout(() => setHistOpen(false), 150)}
                  onKeyDown={e => { if (e.key === 'Escape') setHistOpen(false) }} />
                {histOpen && !search && !similarTo && searchHist.length > 0 && (
                  <div className={s.history} role="listbox" aria-label={t('Zuletzt gesucht')}>
                    <Text size={200} className={s.historyHead}>{t('Zuletzt gesucht')}</Text>
                    {searchHist.map(h => (
                      <div key={h} role="option" aria-selected={false} className={s.historyRow} onMouseDown={e => e.preventDefault()} onClick={() => { setSearch(h); setHistOpen(false) }}>
                        <History16Regular />
                        <span style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{h}</span>
                        <Button size="small" appearance="transparent" icon={<Dismiss12Regular />} aria-label={t('Aus dem Verlauf entfernen')} title={t('Aus dem Verlauf entfernen')}
                          onClick={e => { e.stopPropagation(); update({ mailSearchHistory: searchHist.filter(x => x !== h) }) }} />
                      </div>
                    ))}
                    <div className={s.historyRow} onMouseDown={e => e.preventDefault()} onClick={() => { update({ mailSearchHistory: [] }); setHistOpen(false) }}>
                      <Delete16Regular /><span>{t('Verlauf löschen')}</span>
                    </div>
                  </div>
                )}
              </div>
              <div className={s.listTitle}
                // [feature:mailtocolumn]
                onContextMenu={featureOn('mailtocolumn') ? e => { e.preventDefault(); setToMenu({ x: e.clientX, y: e.clientY }) } : undefined}
                // [/feature:mailtocolumn]
              >
                {/* [feature:mailtocolumn] */}
                <ContextMenu at={toMenu} onClose={() => setToMenu(null)}>
                  <MenuItem icon={toColumn ? <Checkmark20Regular /> : <span style={{ width: 20 }} />} onClick={() => { update({ mailToColumn: !toColumn }); setToMenu(null) }}>{t('Spalte „An“')}</MenuItem>
                </ContextMenu>
                {/* [/feature:mailtocolumn] */}
                <Text weight="semibold" size={400} truncate>
                  {similarTo ? (total === 1 ? t('Ähnliche Mails: 1 Treffer') : t('Ähnliche Mails: {n} Treffer', { n: total }))
                    : query ? (total === 1 ? t('Suche: 1 Treffer') : t('Suche: {n} Treffer', { n: total })) : virtualName ?? catchallName ?? folderLabel(currentFolder)}
                  {query && remoteSearch && <Text size={200} weight="regular" style={{ marginLeft: 6, color: tokens.colorNeutralForeground3 }}>{t('sucht auch beim Anbieter …')}</Text>}
                </Text>
                {query && !similarTo ? (
                  <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                    <Menu checkedValues={{ sort: [searchSort] }} onCheckedValueChange={(_, d) => update({ searchSort: d.checkedItems[0] as any })}>
                      <MenuTrigger disableButtonEnhancement>
                        <Tooltip content={t('Sortieren')} relationship="label">
                          <Button appearance="subtle" size="small" icon={<ArrowSort20Regular />}>
                            {({ newest: t('Neueste'), oldest: t('Älteste'), relevance: t('Relevanz'), from: t('Absender'), subject: t('Betreff') } as Record<string, string>)[searchSort]}
                          </Button>
                        </Tooltip>
                      </MenuTrigger>
                      <MenuPopover>
                        <MenuList>
                          <MenuItemRadio name="sort" value="newest">{t('Neueste zuerst')}</MenuItemRadio>
                          <MenuItemRadio name="sort" value="oldest">{t('Älteste zuerst')}</MenuItemRadio>
                          <MenuItemRadio name="sort" value="relevance">{t('Relevanz')}</MenuItemRadio>
                          <MenuItemRadio name="sort" value="from">{t('Absender')}</MenuItemRadio>
                          <MenuItemRadio name="sort" value="subject">{t('Betreff')}</MenuItemRadio>
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                    <Checkbox label={t('Alle Konten')} checked={allAccounts} onChange={(_, d) => setAllAccounts(!!d.checked)} />
                  </div>
                ) : similarTo ? null : (
                  <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                  <Menu checkedValues={{ sort: [listSort] }} onCheckedValueChange={(_, d) => update({ listSort: (d.checkedItems[0] ?? 'newest') as any })}>
                    <MenuTrigger disableButtonEnhancement>
                      <Tooltip content={t('Sortieren')} relationship="label">
                        <Button appearance={listSort === 'newest' ? 'subtle' : 'primary'} size="small" icon={<ArrowSort20Regular />}>
                          {listSort === 'newest' ? undefined : ({ oldest: t('Älteste'), from: t('Absender'), subject: t('Betreff'), size: t('Größe'), attachment: t('Anhang'), unread: t('Ungelesen'), flagged: t('Gekennzeichnet'), to: t('An') } as Record<string, string>)[listSort]}
                        </Button>
                      </Tooltip>
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList>
                        <MenuItemRadio name="sort" value="newest">{t('Neueste zuerst')}</MenuItemRadio>
                        <MenuItemRadio name="sort" value="oldest">{t('Älteste zuerst')}</MenuItemRadio>
                        <MenuItemRadio name="sort" value="from">{t('Absender')}</MenuItemRadio>
                        <MenuItemRadio name="sort" value="subject">{t('Betreff')}</MenuItemRadio>
                        <MenuItemRadio name="sort" value="size">{t('Größte zuerst')}</MenuItemRadio>
                        <MenuItemRadio name="sort" value="attachment">{t('Mit Anhang zuerst')}</MenuItemRadio>
                        <MenuItemRadio name="sort" value="unread">{t('Ungelesene zuerst')}</MenuItemRadio>
                        <MenuItemRadio name="sort" value="flagged">{t('Gekennzeichnete zuerst')}</MenuItemRadio>
                        {featureOn('mailtocolumn') && <MenuItemRadio name="sort" value="to">{t('An')}</MenuItemRadio>} {/* [feature:mailtocolumn] */}
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      <Tooltip content={t('Filter')} relationship="label">
                        <Button appearance={filter ? 'primary' : 'subtle'} size="small" icon={<Filter20Regular />}>
                          {filter === 'unread' ? t('Ungelesen') : filter === 'flagged' ? t('Gekennzeichnet') : filter === 'attachments' ? t('Mit Anhang')
                            : filter.startsWith('cat:') ? (categories.find(c => 'cat:' + c.key === filter)?.name ?? t('Kategorie'))
                            : filter.startsWith('to:') ? t('An: {who}', { who: filter.slice(3) }) // [feature:mailtocolumn]
                            : filter.startsWith('from:') ? t('Von: {who}', { who: filter.slice(5) }) // [feature:mailavatarfilter]
                            : t('Alle')}
                        </Button>
                      </Tooltip>
                    </MenuTrigger>
                    <MenuPopover>
                      <MenuList checkedValues={{ f: [filter] }} onCheckedValueChange={(_, d) => setFilter(d.checkedItems[0] || '')}>
                        <MenuItem onClick={() => setFilter('')}>{t('Alle')}</MenuItem>
                        <MenuItem onClick={() => setFilter('unread')}>{t('Ungelesen')}</MenuItem>
                        <MenuItem onClick={() => setFilter('flagged')}>{t('Gekennzeichnet')}</MenuItem>
                        <MenuItem onClick={() => setFilter('attachments')}>{t('Mit Anhang')}</MenuItem>
                        {categories.length > 0 && <MenuDivider />}
                        {categories.map(c => (
                          <MenuItem key={c.key} icon={<CategoryDot color={c.color} />} onClick={() => setFilter('cat:' + c.key)}>{c.name}</MenuItem>
                        ))}
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                  </div>
                )}
              </div>
            </div>
            {focusOn && !searching && !filter && (
              <TabList size="small" selectedValue={focusTab} onTabSelect={(_, d) => setFocusTab(d.value as 'focused' | 'other')} style={{ padding: '0 8px' }}>
                <Tab value="focused" onDragOver={dragOk} onDrop={e => { const ids = dropIds(e); if (ids) focusDropped(ids, true) }}>{t('Relevant')}{countOf((tabFocus?.focused ?? 0), tabFocus?.focusedTotal) > 0 ? ` (${countOf((tabFocus?.focused ?? 0), tabFocus?.focusedTotal)})` : ''}</Tab>
                <Tab value="other" onDragOver={dragOk} onDrop={e => { const ids = dropIds(e); if (ids) focusDropped(ids, false) }}>{t('Sonstige')}{countOf((tabFocus?.other ?? 0), tabFocus?.otherTotal) > 0 ? ` (${countOf((tabFocus?.other ?? 0), tabFocus?.otherTotal)})` : ''}</Tab>
              </TabList>
            )}
            {trustOffer && <TrustOffer check={trustOffer.check} lead={trustOffer.lead} onTrusted={trustOffer.onTrusted} onClose={() => setTrustOffer(null)} />}
            <MessageList items={listItems} grouped={similarTo ? true : query ? searchSort === 'newest' || searchSort === 'oldest' : listSort === 'newest' || listSort === 'oldest'} selected={selected}
              onSelect={ids => { if (compose) leaveCompose().then(() => setSelected(ids)); else setSelected(ids) }} loading={loading}
              onContextMenu={(item, x, y) => setCtx({ item, x, y })}
              onRemoveCategory={uncategorize}
              groupOf={similarTo ? (i, date) => `${similarLevel(i.similar)} · ${date}` : undefined}
              // [feature:mailtocolumn]
              toOf={toColumn ? toOf : undefined}
              // [/feature:mailtocolumn]
              onSenderFilter={featureOn('mailavatarfilter') ? a => { setSimilarTo(null); setFilter('from:' + a.toLowerCase()) } : undefined} // [feature:mailavatarfilter]
              expanded={threadList ? expanded : undefined} onToggleThread={threadList ? toggleThread : undefined} // [feature:mailthreads]
              showFolder={searching || sel?.folder === '__flagged' || virtual || !!sel?.folder.startsWith('ca:')} folders={folders} spamCutoffs={null}
              more={!searching && moreRows} loadingMore={loadingMore} onMore={() => setPage(p => p + 1)}
              snoozeOf={snoozeOf}
              expiryOf={i => expiryOf(expiries, i)}
              onOpen={item => { if (!isDraft(item)) openMailWindow(item) }}
              empty={searching ? 'search' : filter ? 'filter' : sel && folders[sel.account]?.find(f => f.path === sel.folder)?.special === 'inbox' ? 'inbox' : sel && !virtual && !sel.folder.startsWith('__') ? 'folder' : undefined}
              onToggleFlag={i => {
                // Sofort anzeigen, der Server folgt im Hintergrund; in „Markiert“ verschwindet die Mail ohne Fahne
                const flagged = !i.flagged
                if (!flagged && sel?.folder === FLAGGED) {
                  const op = hideGone([i], countsFor([i], { flagged }), { flagged })
                  return enqueue(async () => { op.sent(); try { await flagsOf({ flagged })({ account: i.account, folder: i.folder, uids: [i.uid] }); settleGone([i], true, op) }
                    catch (e) { failBack(e); settleGone([i], false, op) } })
                }
                return changeMails([i], { flagged }, flagsOf({ flagged }))
              }} />
          </div>
          {pane === 'bottom' && paneSize && listH.handle()}
          <div className={s.reading} style={pane === 'off' && !current && !compose ? { display: 'none' } : undefined}>
            {pane === 'off' && current && !compose && (
              <Button className={s.back} appearance="subtle" icon={<ArrowLeft20Regular />} onClick={() => setSelected([])}>{t('Zurück zur Liste')}</Button>
            )}
            {compose ? (
              <Compose key={compose.id} ref={composeRef} init={compose.init} accounts={accounts} onPopOut={popOutCompose}
                ribbonSlot={messageRibbon && ribbonTab === 'message' ? ribbonSlot : null}
                onDirty={d => { composeDirty.current = d }}
                onSaved={() => loadList(true)}
                onClose={(sent, queued) => {
                  setCompose(null)
                  if (queued) setUndo(queued)
                  // Geschlossenen Entwurf nicht sofort wieder oeffnen
                  if (openDraft) setSelected([])
                  if (sent || openDraft) loadList(true)
                }} />
            ) : current && openDraft ? (
              <DraftOpener key={current.id} item={current} onOpen={setCompose} />
            // [feature:mailthreads]
            ) : current && threadList && !current.member && (current.threadCount ?? 0) > 1 && current.threadKey && members[current.threadKey] ? (
              <ConversationPane key={current.threadKey} head={current} folders={folders}
                items={members[current.threadKey].map(m => m.id === current.id ? current : m)}
                render={m => <ReadingPane embedded item={m} onLoaded={msg => {
                  if (m.id === current.id) setMessage(msg)
                  if (msg.seen) seenOnServer(m)
                }} onMarkSeen={markSeen} onReply={all => replyTo(m, all)} invert={invert}
                  onAct={what => m.id === current.id ? act(what) : actOne(m, what)}
                  onTrustOffer={(check, onTrusted) => setTrustOffer({ check, lead: t('Diese Mail ist kein Betrug.'), onTrusted })} />} />
            // [/feature:mailthreads]
            ) : current ? (
              <ReadingPane item={current} onLoaded={m => {
                setMessage(m)
                if (m.seen) seenOnServer(current)
              }} onMarkSeen={markSeen} onReply={reply} onAct={act} invert={invert} onTrustOffer={(check, onTrusted) => setTrustOffer({ check, lead: t('Diese Mail ist kein Betrug.'), onTrusted })} onRemoveCategory={k => uncategorize(current, k)}
              />
            ) : (
              <div className={s.empty}>
                {selected.length > 1 ? <Text>{t('{n} Mails ausgewählt', { n: selected.length })}</Text> : <Text>{t('Wähle eine Mail aus.')}</Text>}
              </div>
            )}
          </div>
        </div>
          {(prefs.todoBar ?? []).length > 0 && (!todoFloating || todoOpen) && (
            <>
              {todoFloating && <div className={s.backdrop} onClick={() => setTodoOpen(false)} />}
              {!todoFloating && todoW.handle('left')}
              <div className={todoFloating ? s.flyout : undefined} style={todoFloating ? { right: 0 } : { display: 'flex' }}>
                <TodoBar width={todoFloating ? undefined : todoW.width} onWrite={(address, name) => accounts?.length && setCompose({ account: composeAccount(sel, accounts), to: [name ? `"${name}" <${address}>` : address], cc: [], bcc: [], subject: '', html: '' })} />
              </div>
            </>
          )}
        </div>
      </div>
      </div>
      {ctx && (
        <Menu open onOpenChange={(_, d) => !d.open && setCtx(null)}
          positioning={{ autoSize: 'height', target: { getBoundingClientRect: () => ({ x: ctx.x, y: ctx.y, top: ctx.y, left: ctx.x, bottom: ctx.y, right: ctx.x, width: 0, height: 0 }) } as any }}>
          <MenuPopover>{actionList(ctx.item, () => setCtx(null))}</MenuPopover>
        </Menu>
      )}
      {newAlias && <NewAliasDialog onClose={() => setNewAlias(false)} />}
      {adoptFor?.alias && <AdoptAliasDialog address={adoptFor.alias} sender={adoptFor.from?.name} onClose={() => setAdoptFor(null)} />}
      {eventDraft && (
        <EventDialog draft={eventDraft.draft} calendars={eventDraft.calendars}
          onClose={saved => { setEventDraft(null); if (saved) notify(t('Termin angelegt')) }} />
      )}
    </div>
  )
}

function MoveMenu({ folders, disabled, onMove }: { folders: Folder[]; disabled: boolean; onMove: (path: string) => void }) {
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <RibbonButton icon={<FolderArrowRight20Regular />} label={t('mail|Verschieben')} disabled={disabled} />
      </MenuTrigger>
      <MenuPopover style={{ maxHeight: 420, overflowY: 'auto' }}>
        <MenuList>
          {folders.filter(f => f.selectable).sort((a, b) => a.path.localeCompare(b.path)).map(f => (
            <MenuItem key={f.path} onClick={() => onMove(f.path)}>
              {' '.repeat(4 * (f.path.split(f.delimiter).length - 1))}{folderLabel(f)}
            </MenuItem>
          ))}
        </MenuList>
      </MenuPopover>
    </Menu>
  )
}

/** Ein Entwurf geht im Editor auf. */
function DraftOpener({ item, onOpen }: { item: MailItem; onOpen: (c: ComposeInit) => void }) {
  const { fail } = useApp()
  const [failed, setFailed] = useState(false)
  useEffect(() => {
    api.get<Message>(`/api/mail/${item.account}/message?` + q({ folder: item.folder, uid: item.uid, images: true })).then(m => onOpen({
      account: item.account, to: m.to.map(a => a.name ? `"${a.name}" <${a.address}>` : a.address),
      cc: m.cc.map(a => a.address), bcc: m.bcc.map(a => a.address), subject: m.subject, html: m.html,
      draftUid: item.uid, draftId: item.messageId, inReplyTo: m.inReplyTo, references: m.references,
    })).catch(e => { setFailed(true); fail(e) })
  }, [item.id]) // eslint-disable-line react-hooks/exhaustive-deps
  if (failed) return <Text style={{ margin: 'auto', color: tokens.colorNeutralForeground3 }}>{t('Der Entwurf konnte nicht geöffnet werden.')}</Text>
  return <Spinner style={{ margin: 'auto' }} />
}
