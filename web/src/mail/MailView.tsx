import {
  Button, Menu, MenuDivider, MenuItem, MenuItemCheckbox, MenuItemRadio, MenuList, MenuPopover, MenuTrigger, SearchBox, Spinner, Text,
  Tooltip, makeStyles, mergeClasses, tokens, Checkbox, Tab, TabList,
} from '@fluentui/react-components'
import {
  MailAdd20Regular, Delete20Regular, Archive20Regular, ShieldError20Regular, ShieldCheckmark20Regular, FolderArrowRight20Regular,
  Tag20Regular, MailRead20Regular, MailUnread20Regular, Flag20Regular, Flag20Filled, ArrowReply20Regular, ArrowReplyAll20Regular, ArrowForward20Regular,
  Filter20Regular, ArrowSort20Regular, ArrowSwap20Regular, MoreHorizontal20Regular, ArrowLeft20Regular, ArrowUndo20Regular, Clock20Regular, CalendarAdd20Regular, Attach20Regular, Note20Regular,
  TextLineSpacing20Regular, TextFontSize20Regular, PanelLeft20Regular, PanelRight20Regular, PanelRightContract20Regular, TextDescription20Regular,
  PersonCircle20Regular, Settings20Regular, Navigation20Regular, CalendarLtr20Regular,
  Open20Regular,
} from '@fluentui/react-icons'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { Account, api, Folder, MailItem, Message, q } from '../api'
import { prefetch } from './messageCache'
import { useApp } from '../App'
import { MobileRibbonRow, Ribbon, RibbonButton, RibbonDisplayMenu, RibbonDivider, RibbonTabs } from '../shell/Ribbon'
import { useBackClose, useIsMobile } from '../shell/mobile'
import { TodoBar } from './TodoBar'
import { CategoryMenuList, CategoryDot, useCategories } from './Categories'
import { SnoozeMenu, snoozeOptions } from './SnoozeMenu'
import { fmtDate } from '../shell/hooks'
import { usePrefs } from '../prefs'
import { useDebounced } from '../shell/hooks'
import { FolderPane, folderLabel } from './FolderPane'
import { FollowUpItems, FollowUpRibbon } from './FollowUpMenu'
import { SEARCH_FOLDERS_CHANGED, useVirtualName } from './VirtualFolders'
import { MessageList } from './MessageList'
import { ReadingPane } from './ReadingPane'
import { Compose, ComposeHandle, ComposeInit, QueuedSend, forwardAsAttachmentOf, forwardOf, replyOf } from './Compose'
import { OutboxView } from './Outbox'
import { openComposeWindow, openMailWindow } from './MailWindow'
import { EventDialog, EventDraft, guessStart } from '../calendar/EventDialog'
import type { Collection } from '../api'
import { locale, t } from '../i18n'

const useStyles = makeStyles({
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
  const { fail, notify } = useApp()
  const [accounts, setAccounts] = useState<Account[] | null>(null)
  const [folders, setFolders] = useState<Record<string, Folder[]>>({})
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
        setSearch('')
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
  const [page, setPage] = useState(0)
  const [filter, setFilter] = useState<string>('')
  // Prio-Posteingang: Reiter Relevant/Sonstige im Posteingang (ohne eigenen Filter)
  const [focusTab, setFocusTab] = useState<'focused' | 'other'>('focused')
  const [focusInfo, setFocusInfo] = useState<{ enabled: boolean; focused: number; other: number } | null>(null)
  const [search, setSearch] = useState('')
  const searchSort = prefs.searchSort ?? (prefs.searchRelevance ? 'relevance' : 'newest')
  // Reihenfolge der Mailliste eines Ordners (der Server sortiert, damit auch lange Ordner stimmen)
  const listSort = prefs.listSort ?? 'newest'
  const [allAccounts, setAllAccounts] = useState(false)
  const [selected, setSelected] = useState<number[]>([])
  const [message, setMessage] = useState<Message | null>(null)
  const [compose, setComposeState] = useState<{ id: number; init: ComposeInit } | null>(null)
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
  const setCompose = (init: ComposeInit | null) => {
    if (init && compose && composeDirty.current && !confirm(t('Aktuellen Entwurf verwerfen?'))) return
    composeDirty.current = false
    setComposeState(init ? { id: ++composeSeq.current, init } : null)
  }
  const query = useDebounced(search, 350)
  const version = status?.version
  // Wiedervorlage: wann welche Mail zurueckkommt
  const [snoozes, setSnoozes] = useState<{ account: string; uid: number; messageId?: string; until: string }[]>([])
  useEffect(() => { api.get('/api/mail/snoozes').then(setSnoozes).catch(() => {}) }, [version])
  const listRef = useRef(items)
  listRef.current = items

  // Konten und Ordner
  const loadFolders = useCallback(async (list: Account[]) => {
    const entries = await Promise.all(list.map(async a => [a.id, await api.get<Folder[]>(`/api/mail/${a.id}/folders`).catch(() => [])] as const))
    setFolders(Object.fromEntries(entries))
  }, [])

  useEffect(() => {
    api.get<Account[]>('/api/mail/accounts').then(list => {
      setAccounts(list)
      loadFolders(list)
    }).catch(fail)
  }, [fail, loadFolders])

  useEffect(() => { if (accounts) loadFolders(accounts) }, [version, accounts, loadFolders])
  // Gelesen/ungelesen: Zahl am Ordner sofort anpassen, Zaehler oben (Leiste, Taskleiste) gleich neu holen
  const bumpUnread = (changes: { account: string; folder: string; delta: number }[]) => {
    if (changes.length === 0) return
    setFolders(all => {
      const next = { ...all }
      for (const c of changes)
        next[c.account] = (next[c.account] ?? []).map(f => f.path === c.folder ? { ...f, unread: Math.max(0, f.unread + c.delta) } : f)
      return next
    })
    setTimeout(() => window.dispatchEvent(new Event('dmoffice:mailstatus')), 300)
  }

  // Aus den Kontakten: "E-Mail an ..."
  useEffect(() => {
    const to = sessionStorage.getItem('dmoffice.compose.to')
    if (!to || !accounts?.length) return
    sessionStorage.removeItem('dmoffice.compose.to')
    setCompose({ account: accounts[0].id, to: to.split('\n').filter(Boolean), cc: [], bcc: [], subject: '', html: '' })
  }, [accounts])

  // Ohne Auswahl: Posteingang des ersten Kontos
  useEffect(() => {
    if (sel || !accounts?.length) return
    const inbox = folders[accounts[0].id]?.find(f => f.special === 'inbox')
    if (inbox) { autoSelect.current = true; setSel({ account: accounts[0].id, folder: inbox.path }) }
  }, [accounts, folders, sel])

  // Beim Oeffnen eines Ordners eine Mail waehlen (Einstellungen -> Allgemein)
  const autoSelect = useRef(true)
  const [openOnFolder, setOpenOnFolder] = useState<'none' | 'first' | 'unread' | 'last'>('last')
  useEffect(() => { api.get('/api/settings/').then(r => r.openOnFolder && setOpenOnFolder(r.openOnFolder)).catch(() => {}) }, [])
  const lastKey = (s: Selection) => `dmoffice.mail.last.${s.account}|${s.folder}`
  // Zusammengesetzte Ordner (Alle Posteingaenge, Suchordner, Kategorien): Mails aus mehreren Konten - ueber die id merken
  const mixedSel = !!sel && (sel.folder.startsWith('sf:') || sel.folder.startsWith('cat:') || sel.folder === '__unified')
  useEffect(() => {
    if (!autoSelect.current || !sel || query || loading) return
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
    if (!sel || query || selected.length !== 1) return
    const item = items.find(i => i.id === selected[0])
    if (item && mixedSel) { try { localStorage.setItem(lastKey(sel), String(item.id)) } catch { /* egal */ } }
    else if (item && item.account === sel.account && item.folder === sel.folder) {
      try { localStorage.setItem(lastKey(sel), String(item.uid)) } catch { /* egal */ }
    }
  }, [selected]) // eslint-disable-line react-hooks/exhaustive-deps

  // Liste - nur die Antwort der neuesten Anfrage uebernehmen
  const listReq = useRef(0)
  // Zuletzt gesehene Listen je Ordner: beim Wechsel sofort zeigen, im Hintergrund auffrischen
  const listCache = useRef(new Map<string, { items: MailItem[]; total: number }>())
  const inboxSel = !!sel && folders[sel.account]?.find(f => f.path === sel.folder)?.special === 'inbox'
  const focusOn = inboxSel && !!focusInfo?.enabled
  const listFilter = filter || (focusOn ? focusTab : '')
  const listKey = sel ? `${sel.account}|${sel.folder}|${listFilter}` : ''
  const itemsKey = useRef('')
  const remember = (key: string, items: MailItem[], total: number) => {
    const c = listCache.current
    c.delete(key)
    c.set(key, { items, total })
    if (c.size > 30) c.delete(c.keys().next().value!)
  }
  // Aenderungen in der Liste (gelesen, Fahne, verschoben) gleich mitmerken
  useEffect(() => { if (!query.trim() && itemsKey.current && itemsKey.current === listKey) remember(listKey, items, total) }, [items, total]) // eslint-disable-line react-hooks/exhaustive-deps

  const loadList = useCallback(async (keepPage = false) => {
    if (!sel || sel.folder === '__outbox') return
    const req = ++listReq.current
    const key = `${sel.account}|${sel.folder}|${listFilter}|${listSort}`
    const cached = !query.trim() && !keepPage ? listCache.current.get(key) : undefined
    if (cached) {
      itemsKey.current = key
      setItems(cached.items)
      setTotal(cached.total)
      setPage(0)
    } else itemsKey.current = ''
    setLoading(!cached)
    try {
      if (query.trim()) {
        // Suchbereich wie in Outlook: aus dem Posteingang alle Ordner, sonst nach Einstellung
        const scope = prefs.searchScope ?? 'inboxAll'
        const inInbox = folders[sel.account]?.find(f => f.path === sel.folder)?.special === 'inbox'
        const inVirtual = sel.folder.startsWith('sf:') || sel.folder.startsWith('cat:') || sel.folder === '__unified'
        const folder = inVirtual ? undefined : scope === 'current' || (scope === 'inboxAll' && !inInbox) ? sel.folder : undefined
        const params = { q: query, acc: allAccounts ? undefined : sel.account, folder: allAccounts ? undefined : folder, deleted: prefs.searchDeleted ?? true }
        const r = await api.get<MailItem[]>('/api/mail/search?' + q({ ...params, relevance: searchSort === 'relevance' }))
        if (req !== listReq.current) return
        setItems(r)
        setTotal(r.length)
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
        const size = 100 * ((keepPage ? page : 0) + 1)
        const r = await api.get(`/api/mail/${sel.account}/messages?` + q({ folder: sel.folder, size, filter: listFilter, sort: listSort === 'newest' ? undefined : listSort }))
        if (req !== listReq.current) return
        itemsKey.current = key
        setItems(r.items)
        setTotal(r.total)
        if (!keepPage) setPage(0)
        if (size <= 100) remember(key, r.items, r.total)
      }
    } catch (e) { if (req === listReq.current) fail(e) } finally { if (req === listReq.current) setLoading(false) }
  }, [sel, query, allAccounts, listFilter, listSort, page, fail, accounts])
  // Ungelesene je Reiter; beim Wechsel des Posteingangs und nach Aenderungen neu
  const loadFocus = useCallback(() => {
    if (!sel || !inboxSel) { setFocusInfo(null); return }
    api.get(`/api/mail/${sel.account}/focus?` + q({ folder: sel.folder })).then(setFocusInfo).catch(() => setFocusInfo(null))
  }, [sel, inboxSel])
  useEffect(() => { loadFocus() }, [loadFocus, version])
  // Nach Relevant/Sonstige verschieben; always: Absender immer dorthin
  const moveFocus = async (focused: boolean, always = false) => {
    if (!selectedItems.length) return
    const ids = selectedItems.map(i => i.id)
    try {
      for (const g of groups()) await api.post(`/api/mail/${g.account}/focus`, { folder: g.folder, uids: g.uids, focused, always })
      if (focusOn) afterRemove(ids)
      notify(focused ? t('Nach Relevant verschoben') : t('Nach Sonstige verschoben'), always ? t('Künftige Mails dieses Absenders landen auch dort.') : undefined)
      loadFocus()
    } catch (e) { fail(e) }
  }

  useEffect(() => { loadList(false) }, [sel, query, allAccounts, listFilter, listSort, searchSort === 'relevance']) // eslint-disable-line react-hooks/exhaustive-deps
  // Suchordner geaendert: gemerkte Trefferlisten verwerfen und die offene neu laden
  const selRef = useRef(sel)
  selRef.current = sel
  const loadListRef = useRef(loadList)
  loadListRef.current = loadList
  useEffect(() => {
    const on = () => {
      for (const k of [...listCache.current.keys()]) if (k.split('|')[1]?.startsWith('sf:')) listCache.current.delete(k)
      if (selRef.current?.folder.startsWith('sf:')) loadListRef.current(false)
    }
    window.addEventListener(SEARCH_FOLDERS_CHANGED, on)
    return () => window.removeEventListener(SEARCH_FOLDERS_CHANGED, on)
  }, [])
  // Suchtreffer sortieren: Datum und Relevanz liefert der Server, Absender und Betreff sortieren wir hier
  const listItems = useMemo(() => {
    if (!query || searchSort === 'newest' || searchSort === 'relevance') return items
    const key = (i: MailItem) => searchSort === 'from' ? (i.from?.name || i.from?.address || '') : i.subject.replace(/^((re|aw|fwd?|wg)\s*:\s*)+/i, '')
    return [...items].sort(searchSort === 'oldest' ? (a, b) => a.date.localeCompare(b.date)
      : (a, b) => key(a).localeCompare(key(b), locale, { sensitivity: 'base' }) || b.date.localeCompare(a.date))
  }, [items, query, searchSort])
  useEffect(() => { if (version !== undefined) loadList(true) }, [version]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (page > 0) loadList(true) }, [page]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    if (!opening || loading || sel?.account !== opening.account || sel?.folder !== opening.openIn) return
    const { openIn: _, ...row } = opening
    if (!items.some(i => i.id === opening.id)) setItems(list => [row, ...list])
    setSelected([opening.id])
    setOpening(null)
  }, [opening, loading, items, sel])

  const selectedItems = useMemo(() => items.filter(i => selected.includes(i.id)), [items, selected])
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
  useEffect(() => { setMessage(null) }, [current?.id])
  const folderOf = (acc: string, path: string) => folders[acc]?.find(f => f.path === path)
  const currentFolder = sel ? folderOf(sel.account, sel.folder) : undefined
  // Suchordner ("sf:"), Kategorien ("cat:") und Alle Posteingaenge: Mails aus allen Konten
  const virtual = !!sel && (sel.folder.startsWith('sf:') || sel.folder.startsWith('cat:') || sel.folder === '__unified')
  const virtualName = useVirtualName(virtual ? sel!.folder : undefined)
  // Verschieben geht in einen Ordner des Kontos - im virtuellen Ordner nur, wenn alle Mails aus einem Konto sind
  const moveAccount = !virtual ? sel?.account : new Set(selectedItems.map(i => i.account)).size === 1 ? selectedItems[0].account : undefined

  // Gruppiert nach Konto und Ordner - in der Suche koennen es mehrere sein
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
    const idx = items.findIndex(i => ids.has(i.id))
    const rest = items.filter(i => !ids.has(i.id))
    setItems(rest)
    if (selected.some(id => ids.has(id))) {
      const next = rest[Math.min(idx, rest.length - 1)]
      setSelected(next ? [next.id] : [])
    }
  }
  const idsOf = (g: { account: string; folder: string; uids: number[] }) =>
    selectedItems.filter(i => i.account === g.account && i.folder === g.folder && g.uids.includes(i.uid)).map(i => i.id)

  const act = async (what: 'delete' | 'junk' | 'notjunk' | 'archive' | 'read' | 'unread' | 'flag' | 'unflag', target?: string) => {
    if (!selectedItems.length) return
    const removes = ['delete', 'junk', 'archive'].includes(what) || (what === 'notjunk' && currentFolder?.special === 'junk')
      || (what === 'unflag' && sel?.folder === '__flagged')
    // Sofort anzeigen, der Mailserver braucht manchmal Sekunden; bei Fehler zurueck auf den Stand des Servers
    const all = selectedItems.map(i => i.id)
    if (removes && (what !== 'archive' || selectedItems.every(i => folders[i.account]?.some(f => f.special === 'archive')))) afterRemove(all)
    else if (!removes) setItems(list => list.map(i => !all.includes(i.id) ? i :
      { ...i, seen: what === 'read' ? true : what === 'unread' ? false : i.seen, flagged: what === 'flag' ? true : what === 'unflag' ? false : i.flagged }))
    // Bereits erledigte Gruppen
    const done: number[] = []
    try {
      for (const g of groups()) {
        const body = { folder: g.folder, uids: g.uids }
        if (what === 'delete') await api.post(`/api/mail/${g.account}/delete`, body)
        else if (what === 'junk') await api.post(`/api/mail/${g.account}/junk`, body)
        else if (what === 'notjunk') await api.post(`/api/mail/${g.account}/notjunk`, body)
        else if (what === 'archive') {
          const archive = folders[g.account]?.find(f => f.special === 'archive')
          // Konto ohne Archiv ueberspringen, die anderen trotzdem erledigen
          if (!archive) { notify(t('Kein Archiv-Ordner'), t('Lege beim Mailanbieter einen Ordner „Archiv“ an.'), 'warning'); continue }
          await api.post(`/api/mail/${g.account}/move`, { ...body, target: archive.path })
        }
        else if (what === 'read' || what === 'unread') {
          await api.post(`/api/mail/${g.account}/flags`, { ...body, seen: what === 'read' })
          bumpUnread(items.filter(i => idsOf(g).includes(i.id) && i.seen !== (what === 'read'))
            .map(i => ({ account: i.account, folder: i.folder, delta: what === 'read' ? -1 : 1 })))
        }
        else if (what === 'flag' || what === 'unflag') await api.post(`/api/mail/${g.account}/flags`, { ...body, flagged: what === 'flag' })
        done.push(...idsOf(g))
      }
      if (removes && done.length < all.length) afterRemove(done)
      if ((what === 'flag' || what === 'unflag') && accounts) loadFolders(accounts)
      if (what === 'notjunk') setItems(list => list.map(i => selected.includes(i.id) ? { ...i, spam: 0 } : i))
      if (what === 'junk') notify(t('Als Junk gelernt'))
      if (what === 'notjunk') notify(t('Als „Kein Junk“ gelernt'))
      if (target) { /* nur fuer move */ }
    } catch (e) {
      fail(e)
      // Nicht oder nur teilweise erledigt: Liste auf den Stand des Servers bringen
      loadList(true)
    }
  }

  // Kategorie an/aus fuer alle gewaehlten Mails - auf dem Server als IMAP-Schlagwort
  const categorize = async (key: string, on: boolean) => {
    try {
      for (const g of groups()) await api.post(`/api/mail/${g.account}/category`, { folder: g.folder, uids: g.uids, key, on })
      setItems(list => list.map(i => !selected.includes(i.id) ? i :
        { ...i, keywords: on ? [...new Set([...(i.keywords ?? []), key])] : (i.keywords ?? []).filter(k => k !== key) }))
    } catch (e) { fail(e) }
  }
  const clearCategories = async () => {
    const keys = [...new Set(selectedItems.flatMap(i => i.keywords ?? []).filter(k => k.startsWith('dm_')))]
    for (const k of keys) await categorize(k, false)
  }

  // Nachverfolgen: kennzeichnen (IMAP) mit Faelligkeit - erscheint unter Aufgaben in "E-Mail-Nachverfolgung"
  const followUp = async (due: string | null, done = false) => {
    try {
      for (const g of groups()) await api.post(`/api/mail/${g.account}/followup`, { folder: g.folder, uids: g.uids, due, done })
      setItems(list => list.map(i => selected.includes(i.id) ? { ...i, flagged: !done } : i))
      if (accounts) loadFolders(accounts)
      notify(done ? t('Nachverfolgung erledigt') : t('Zur Nachverfolgung markiert'))
    } catch (e) { fail(e) }
  }

  const move = async (account: string, target: string, ids = selected) => {
    if (target === '__flagged') {
      const flagging = items.filter(i => ids.includes(i.id) && i.account === account)
      try {
        for (const [folder, uids] of Object.entries(flagging.reduce<Record<string, number[]>>((m, i) => ({ ...m, [i.folder]: [...(m[i.folder] ?? []), i.uid] }), {})))
          await api.post(`/api/mail/${account}/flags`, { folder, uids, flagged: true })
        setItems(list => list.map(i => ids.includes(i.id) ? { ...i, flagged: true } : i))
        notify(t('Markiert'))
        loadFolders(accounts!)
      } catch (e) { fail(e) }
      return
    }
    const moving = items.filter(i => ids.includes(i.id) && i.account === account)
    if (!moving.length) return
    const removed: number[] = []
    try {
      const byFolder = new Map<string, number[]>()
      moving.forEach(i => byFolder.set(i.folder, [...(byFolder.get(i.folder) || []), i.uid]))
      for (const [folder, uids] of byFolder) {
        if (folder === target) continue
        await api.post(`/api/mail/${account}/move`, { folder, uids, target })
        removed.push(...moving.filter(i => i.folder === folder).map(i => i.id))
      }
      // Nur die tatsaechlich verschobenen Mails entfernen
      afterRemove(removed)
      notify(t('Verschoben nach „{folder}“', { folder: folderLabel(folderOf(account, target)) }))
    } catch (e) {
      fail(e)
      if (removed.length) { afterRemove(removed); loadList(true) }
    }
  }

  const reply = (all: boolean) => shown && accounts && setCompose(replyOf(shown, accounts.find(a => a.id === shown.account)!, all))
  const forward = () => shown && accounts && setCompose(forwardOf(shown, accounts.find(a => a.id === shown.account)!))

  // Rechtsklick auf Mails in der Liste
  const [ctx, setCtx] = useState<{ item: MailItem; x: number; y: number } | null>(null)
  /** Die angeklickte Mail laden, falls im Lesebereich noch eine andere steht. */
  const messageFor = async (i: MailItem): Promise<Message> =>
    message && message.account === i.account && message.folder === i.folder && message.uid === i.uid
      ? message : api.get<Message>(`/api/mail/${i.account}/message?` + q({ folder: i.folder, uid: i.uid, markSeen: false }))
  const replyTo = async (i: MailItem, all: boolean) => {
    if (!accounts) return
    try { const m = await messageFor(i); setCompose(replyOf(m, accounts.find(a => a.id === m.account)!, all)) } catch (e) { fail(e) }
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
    try { const m = await messageFor(i); setCompose(forwardAsAttachmentOf(m, accounts.find(a => a.id === m.account)!)) } catch (e) { fail(e) }
  }
  const [eventDraft, setEventDraft] = useState<{ draft: EventDraft; calendars: Collection[] } | null>(null)
  /** Termin aus einer Mail: Betreff als Titel, Text als Beschreibung, ein Datum im Text als Vorschlag. */
  const eventFrom = async (i: MailItem) => {
    try {
      const [m, cals] = await Promise.all([messageFor(i), api.get<Collection[]>('/api/pim/collections/calendars')])
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
    try { const m = await messageFor(i); setCompose(forwardOf(m, accounts.find(a => a.id === m.account)!)) } catch (e) { fail(e) }
  }
  const newMail = () => accounts?.length && setCompose({ account: sel?.account ?? accounts[0].id, to: [], cc: [], bcc: [], subject: '', html: '' })

  // Handy: Zurueck-Taste schliesst die offene Mail
  useBackClose(mobile && !!current && !compose, () => setSelected([]))

  // Tastatur wie in Outlook
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
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
      else if (e.key === 'Enter' && selectedItems.length === 1 && currentFolder?.special !== 'drafts') { e.preventDefault(); openMailWindow(selectedItems[0]) }
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

  // Entwurf oeffnen: im Editor statt im Lesebereich
  const openDraft = currentFolder?.special === 'drafts'

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
    try {
      for (const g of groups()) await api.post(`/api/mail/${g.account}/snooze`, { folder: g.folder, uids: g.uids, until: until.toISOString() })
      afterRemove(selectedItems.map(i => i.id))
      notify(t('Wiedervorlage'), t('Kommt {when} zurück in den Posteingang.', { when: fmtDate(until.toISOString()) }))
      api.get('/api/mail/snoozes').then(setSnoozes).catch(() => {})
      if (accounts) setTimeout(() => loadFolders(accounts), 1500)
    } catch (e) { fail(e) }
  }
  const unsnooze = async () => {
    try {
      for (const g of groups()) await api.post(`/api/mail/${g.account}/unsnooze`, { folder: g.folder, uids: g.uids })
      afterRemove(selectedItems.map(i => i.id))
      notify(t('Zurück im Posteingang'))
    } catch (e) { fail(e) }
  }
  const snoozeOf = (i: MailItem) =>
    snoozes.find(x => x.account === i.account && ((x.messageId && x.messageId === i.messageId) || (x.uid && x.uid === i.uid && snoozedFolder)))?.until

  /** Aktionen fuer eine Mail - Rechtsklick in der Liste und auf dem Handy das Menue "Mehr". */
  const actionList = (item: MailItem, close: () => void) => {
    const many = selectedItems.length > 1
    const allFlagged = selectedItems.length > 0 && selectedItems.every(i => i.flagged)
    const allRead = selectedItems.length > 0 && selectedItems.every(i => i.seen)
    const accountFolders = (folders[item.account] || []).filter(f => f.selectable && f.special !== 'flagged')
    return (
      <MenuList>
        {many && <MenuItem disabled>{t('{n} Mails ausgewählt', { n: selectedItems.length })}</MenuItem>}
        {!many && currentFolder?.special !== 'drafts' && <MenuItem icon={<Open20Regular />} onClick={() => { openMailWindow(item); close() }}>{t('In neuem Fenster öffnen')}</MenuItem>}
        {!many && <MenuDivider />}
        {!many && <MenuItem icon={<ArrowReply20Regular />} onClick={() => { replyTo(item, false); close() }}>{t('Antworten')}</MenuItem>}
        {!many && <MenuItem icon={<ArrowReplyAll20Regular />} onClick={() => { replyTo(item, true); close() }}>{t('Allen antworten')}</MenuItem>}
        {!many && <MenuItem icon={<ArrowForward20Regular />} onClick={() => { forwardItem(item); close() }}>{t('Weiterleiten')}</MenuItem>}
        {!many && <MenuItem icon={<Attach20Regular />} onClick={() => { forwardAsAttachment(item); close() }}>{t('Als Anlage weiterleiten')}</MenuItem>}
        {!many && <MenuItem icon={<CalendarAdd20Regular />} onClick={() => { eventFrom(item); close() }}>{t('Termin aus E-Mail')}</MenuItem>}
        {!many && <MenuItem icon={<Note20Regular />} onClick={() => { toNote(item); close() }}>{t('Als Notiz speichern')}</MenuItem>}
        <MenuDivider />
        <MenuItem icon={<Delete20Regular />} onClick={() => { act('delete'); close() }}>{t('Löschen')}</MenuItem>
        <MenuItem icon={<Archive20Regular />} onClick={() => { act('archive'); close() }}>{t('Archivieren')}</MenuItem>
        {inboxSel && focusInfo?.enabled && (
          <Menu>
            <MenuTrigger disableButtonEnhancement><MenuItem icon={<ArrowSwap20Regular />}>{focusTab === 'other' ? t('Nach Relevant verschieben') : t('Nach Sonstige verschieben')}</MenuItem></MenuTrigger>
            <MenuPopover>
              <MenuList>
                <MenuItem onClick={() => { moveFocus(focusTab === 'other'); close() }}>{t('Nur diese Mail')}</MenuItem>
                <MenuItem onClick={() => { moveFocus(focusTab === 'other', true); close() }}>{t('Immer für diesen Absender')}</MenuItem>
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        <Menu>
          <MenuTrigger disableButtonEnhancement><MenuItem icon={<FolderArrowRight20Regular />}>{t('Verschieben')}</MenuItem></MenuTrigger>
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
        <MenuDivider />
        <Menu>
          <MenuTrigger disableButtonEnhancement><MenuItem icon={<Tag20Regular />}>{t('Kategorisieren')}</MenuItem></MenuTrigger>
          <MenuPopover>
            <CategoryMenuList selectedKeywords={selectedItems.map(i => i.keywords ?? [])}
              onToggle={(k, on) => { categorize(k, on); close() }} onClear={() => { clearCategories(); close() }} />
          </MenuPopover>
        </Menu>
        <Menu>
          <MenuTrigger disableButtonEnhancement><MenuItem icon={allFlagged ? <Flag20Filled /> : <Flag20Regular />}>{t('Nachverfolgen')}</MenuItem></MenuTrigger>
          <MenuPopover>
            <FollowUpItems flagged={allFlagged} onPick={due => { followUp(due); close() }} onDone={() => { followUp(null, true); close() }} />
          </MenuPopover>
        </Menu>
        <MenuItem icon={allRead ? <MailUnread20Regular /> : <MailRead20Regular />} onClick={() => { act(allRead ? 'unread' : 'read'); close() }}>
          {allRead ? t('Als ungelesen markieren') : t('Als gelesen markieren')}
        </MenuItem>
        {snoozedFolder ? (
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
        )}
        <MenuDivider />
        {junkFolder
          ? <MenuItem icon={<ShieldCheckmark20Regular />} onClick={() => { act('notjunk'); close() }}>{t('Kein Junk')}</MenuItem>
          : <MenuItem icon={<ShieldError20Regular />} onClick={() => { act('junk'); close() }}>{t('Als Junk melden')}</MenuItem>}
      </MenuList>
    )
  }

  const ribbonTab = prefs.ribbonTab ?? 'start'
  const todoButton = todoFloating && (prefs.todoBar ?? []).length > 0 && (
    <Tooltip content={t('Aufgabenleiste')} relationship="label">
      <Button appearance={todoOpen ? 'primary' : 'subtle'} icon={<CalendarLtr20Regular />} onClick={() => setTodoOpen(o => !o)} style={{ marginRight: mobile ? 0 : 8 }} />
    </Tooltip>
  )
  const viewItems = <>
    <RibbonButton icon={<TextLineSpacing20Regular />} label={t('Engere Abstände')} pressed={prefs.density === 'compact'}
      onClick={() => update({ density: prefs.density === 'compact' ? 'normal' : 'compact' })} />
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<TextFontSize20Regular />} label={t('Textgröße')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ size: [prefs.textSize ?? 'medium'] }} onCheckedValueChange={(_, d) => update({ textSize: d.checkedItems[0] as any })}>
          <MenuItemRadio name="size" value="small">{t('Klein')}</MenuItemRadio>
          <MenuItemRadio name="size" value="medium">{t('Mittel')}</MenuItemRadio>
          <MenuItemRadio name="size" value="large">{t('Groß')}</MenuItemRadio>
        </MenuList>
      </MenuPopover>
    </Menu>
    <RibbonDivider />
    <Menu>
      <MenuTrigger disableButtonEnhancement><RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} /></MenuTrigger>
      <MenuPopover>
        <MenuList checkedValues={{ fp: [(prefs.folderPane ?? true) ? 'on' : 'off'], fav: (prefs.showFavorites ?? true) ? ['fav'] : [] }}
          onCheckedValueChange={(_, d) => d.name === 'fp' ? update({ folderPane: d.checkedItems[0] === 'on' }) : update({ showFavorites: d.checkedItems.includes('fav') })}>
          <MenuItemRadio name="fp" value="on">{t('Anzeigen')}</MenuItemRadio>
          <MenuItemRadio name="fp" value="off">{t('Ausblenden')}</MenuItemRadio>
          <MenuDivider />
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
    <RibbonButton icon={<TextDescription20Regular />} label={t("Vorschau\u00ADtext")} pressed={prefs.showPreview ?? true}
      onClick={() => update({ showPreview: !(prefs.showPreview ?? true) })} />
    <RibbonButton icon={<PersonCircle20Regular />} label={t("Absender\u00ADbilder")} pressed={prefs.showAvatars ?? true}
      onClick={() => update({ showAvatars: !(prefs.showAvatars ?? true) })} />
    <RibbonDivider />
    <RibbonButton icon={<Settings20Regular />} label={t("Ansicht\u00ADeinstellungen")} onClick={() => { location.hash = '#/settings/list' }} />
    <RibbonDivider />
    <RibbonDisplayMenu />
  </>
  const startItems = <>
    <RibbonButton primary icon={<MailAdd20Regular />} label={t('Neue E-Mail')} onClick={newMail} />
    <RibbonDivider />
    <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!selected.length} onClick={() => act('delete')} />
    <RibbonButton icon={<Archive20Regular />} label={t('Archivieren')} disabled={!selected.length} onClick={() => act('archive')} />
    {junkFolder
      ? <RibbonButton icon={<ShieldCheckmark20Regular />} label={t('Kein Junk')} disabled={!selected.length} onClick={() => act('notjunk')} />
      : <RibbonButton icon={<ShieldError20Regular />} label={t('Junk')} disabled={!selected.length} onClick={() => act('junk')} />}
    <MoveMenu folders={moveAccount ? (folders[moveAccount] || []).filter(f => f.special !== 'flagged') : []} disabled={!selected.length || allAccounts || !moveAccount} onMove={t => moveAccount && move(moveAccount, t)} />
    {snoozedFolder
      ? <RibbonButton icon={<ArrowUndo20Regular />} label={t('Wiedervorlage aufheben')} disabled={!selected.length} onClick={unsnooze} />
      : <SnoozeMenu disabled={!selected.length} onSnooze={snooze} />}
    <RibbonDivider />
    <RibbonButton icon={<ArrowReply20Regular />} label={t('Antworten')} disabled={!shown} onClick={() => reply(false)} />
    <RibbonButton icon={<ArrowReplyAll20Regular />} label={t('Allen antworten')} disabled={!shown} onClick={() => reply(true)} />
    <RibbonButton icon={<ArrowForward20Regular />} label={t('Weiterleiten')} disabled={!shown} onClick={forward} />
    <RibbonButton icon={<CalendarAdd20Regular />} label={t('Termin')} disabled={!current} onClick={() => current && eventFrom(current)} />
    <RibbonButton icon={<Note20Regular />} label={t('Als Notiz')} disabled={!current} onClick={() => current && toNote(current)} />
    <RibbonDivider />
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <RibbonButton icon={<Tag20Regular />} label={t('Kategorisieren')} disabled={!selected.length} />
      </MenuTrigger>
      <MenuPopover>
        <CategoryMenuList selectedKeywords={selectedItems.map(i => i.keywords ?? [])} onToggle={categorize} onClear={clearCategories} />
      </MenuPopover>
    </Menu>
    {(() => {
      const allFlagged = selectedItems.length > 0 && selectedItems.every(i => i.flagged)
      return <FollowUpRibbon disabled={!selected.length} flagged={allFlagged} onPick={due => followUp(due)} onDone={() => followUp(null, true)} />
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
          <MenuItem icon={<Attach20Regular />} disabled={!current} onClick={() => current && forwardAsAttachment(current)}>{t('Als Anlage weiterleiten')}</MenuItem>
          <MenuItem icon={<CalendarAdd20Regular />} disabled={!current} onClick={() => current && eventFrom(current)}>{t('Termin aus E-Mail')}</MenuItem>
          <MenuItem icon={<Note20Regular />} disabled={!current} onClick={() => current && toNote(current)}>{t('Als Notiz speichern')}</MenuItem>
          <MenuDivider />
          <MenuItem icon={<MailRead20Regular />} onClick={() => act('read')}>{t('Als gelesen markieren (Strg+Q)')}</MenuItem>
          <MenuItem icon={<MailUnread20Regular />} onClick={() => act('unread')}>{t('Als ungelesen markieren (Strg+U)')}</MenuItem>
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
    {current && <>
      <RibbonDivider />
      <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} onClick={() => act('delete')} />
      <RibbonButton icon={<Archive20Regular />} label={t('Archivieren')} onClick={() => act('archive')} />
      <RibbonButton icon={<ArrowReply20Regular />} label={t('Antworten')} disabled={!shown} onClick={() => reply(false)} />
      <RibbonButton icon={<ArrowForward20Regular />} label={t('Weiterleiten')} disabled={!shown} onClick={forward} />
      <Menu>
        <MenuTrigger disableButtonEnhancement><RibbonButton icon={<MoreHorizontal20Regular />} label={t('Mehr')} /></MenuTrigger>
        <MenuPopover style={{ maxHeight: '70vh', overflowY: 'auto' }}>{actionList(current, () => {})}</MenuPopover>
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
      ) : <>
      <div className={s.tabsRow}>
        {foldersCollapsed && (
          <Tooltip content={t('Ordnerbereich')} relationship="label">
            <Button appearance="subtle" icon={<Navigation20Regular />} onClick={() => setFoldersOpen(o => !o)} style={{ marginLeft: 6 }} />
          </Tooltip>
        )}
        <div style={{ flex: 1, minWidth: 0 }}>
          <RibbonTabs tabs={[['start', t('Start')], ['view', t('Ansicht')]]} value={prefs.ribbonTab ?? 'start'} onChange={v => update({ ribbonTab: v as any })} />
        </div>
        {todoButton}
      </div>
      <Ribbon>{ribbonTab === 'view' ? viewItems : startItems}</Ribbon>
      </>}
    <div className={s.root}>
      {foldersCollapsed && foldersOpen && <div className={s.backdrop} onClick={() => setFoldersOpen(false)} />}
      <div className={mergeClasses(s.folders, foldersCollapsed && s.flyout)}
        style={foldersCollapsed ? (foldersOpen ? { left: 0, width: mobile ? 'min(86vw, 320px)' : 280 } : { display: 'none' }) : (prefs.folderPane ?? true) ? undefined : { display: 'none' }}>
        {(status?.outbox ?? 0) > 0 && (
          <Button appearance={sel?.folder === '__outbox' ? 'secondary' : 'subtle'} icon={<Clock20Regular />}
            style={{ justifyContent: 'flex-start', margin: '6px 8px 0' }}
            onClick={() => { if (compose) leaveCompose(); if (accounts?.length) setSel({ account: accounts[0].id, folder: '__outbox' }); setSelected([]); setMessage(null); setSearch(''); setFoldersOpen(false) }}>
            {t('Postausgang')} ({status.outbox})
          </Button>
        )}
        <FolderPane accounts={accounts} folders={folders} selection={sel} status={status}
          onSelect={x => { if (compose) leaveCompose(); autoSelect.current = true; setSel(x); setSelected([]); setMessage(null); setSearch(''); setFoldersOpen(false) }}
          onDrop={(account, target, ids) => move(account, target, ids)}
          onChanged={() => loadFolders(accounts)} />
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
        <div style={{ flex: 1, display: sel?.folder === '__outbox' ? 'none' : 'flex', minHeight: 0, minWidth: 0, position: 'relative' }}>
        <div className={mergeClasses(s.panes, pane === 'bottom' && s.panesBottom)}>
          <div className={mergeClasses(s.list, pane === 'bottom' && s.listBottom)} style={pane === 'off' && (current || compose) ? { display: 'none' } : pane === 'off' ? { width: '100%', borderRight: 'none' } : undefined}>
            <div className={s.listHead}>
              <SearchBox placeholder={t('Suchen (von:, an:, betreff:)')} value={search} onChange={(_, d) => setSearch(d.value)} size="medium" />
              <div className={s.listTitle}>
                <Text weight="semibold" size={400} truncate>
                  {query ? (total === 1 ? t('Suche: 1 Treffer') : t('Suche: {n} Treffer', { n: total })) : virtualName ?? folderLabel(currentFolder)}
                  {query && remoteSearch && <Text size={200} weight="regular" style={{ marginLeft: 6, color: tokens.colorNeutralForeground3 }}>{t('sucht auch beim Anbieter …')}</Text>}
                </Text>
                {query ? (
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
                ) : (
                  <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                  <Menu checkedValues={{ sort: [listSort] }} onCheckedValueChange={(_, d) => update({ listSort: (d.checkedItems[0] ?? 'newest') as any })}>
                    <MenuTrigger disableButtonEnhancement>
                      <Tooltip content={t('Sortieren')} relationship="label">
                        <Button appearance={listSort === 'newest' ? 'subtle' : 'primary'} size="small" icon={<ArrowSort20Regular />}>
                          {listSort === 'newest' ? undefined : ({ oldest: t('Älteste'), from: t('Absender'), subject: t('Betreff'), size: t('Größe'), attachment: t('Anhang'), unread: t('Ungelesen'), flagged: t('Gekennzeichnet') } as Record<string, string>)[listSort]}
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
                      </MenuList>
                    </MenuPopover>
                  </Menu>
                  <Menu>
                    <MenuTrigger disableButtonEnhancement>
                      <Tooltip content={t('Filter')} relationship="label">
                        <Button appearance={filter ? 'primary' : 'subtle'} size="small" icon={<Filter20Regular />}>
                          {filter === 'unread' ? t('Ungelesen') : filter === 'flagged' ? t('Gekennzeichnet') : filter === 'attachments' ? t('Mit Anhang')
                            : filter.startsWith('cat:') ? (categories.find(c => 'cat:' + c.key === filter)?.name ?? t('Kategorie')) : t('Alle')}
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
            {focusOn && !query && !filter && (
              <TabList size="small" selectedValue={focusTab} onTabSelect={(_, d) => setFocusTab(d.value as 'focused' | 'other')} style={{ padding: '0 8px' }}>
                <Tab value="focused">{t('Relevant')}{focusInfo!.focused > 0 ? ` (${focusInfo!.focused})` : ''}</Tab>
                <Tab value="other">{t('Sonstige')}{focusInfo!.other > 0 ? ` (${focusInfo!.other})` : ''}</Tab>
              </TabList>
            )}
            <MessageList items={listItems} grouped={query ? searchSort === 'newest' || searchSort === 'oldest' : listSort === 'newest' || listSort === 'oldest'} selected={selected}
              onSelect={ids => { if (compose) leaveCompose().then(() => setSelected(ids)); else setSelected(ids) }} loading={loading}
              onContextMenu={(item, x, y) => setCtx({ item, x, y })}
              showFolder={!!query || sel?.folder === '__flagged' || virtual} folders={folders} spamCutoffs={null}
              more={!query && items.length < total} onMore={() => setPage(p => p + 1)}
              snoozeOf={snoozeOf}
              onOpen={item => { if (currentFolder?.special !== 'drafts') openMailWindow(item) }}
              empty={query ? 'search' : filter ? 'filter' : sel && folders[sel.account]?.find(f => f.path === sel.folder)?.special === 'inbox' ? 'inbox' : sel && !virtual && !sel.folder.startsWith('__') ? 'folder' : undefined}
              onToggleFlag={async i => {
                // Sofort anzeigen, der Mailserver braucht manchmal Sekunden; bei Fehler zurueck auf den Stand des Servers
                if (i.flagged && sel?.folder === '__flagged') afterRemove([i.id])
                else setItems(list => list.map(x => x.id === i.id ? { ...x, flagged: !i.flagged } : x))
                try {
                  await api.post(`/api/mail/${i.account}/flags`, { folder: i.folder, uids: [i.uid], flagged: !i.flagged })
                  if (accounts) loadFolders(accounts)
                } catch (e) { fail(e); loadList(true) }
              }} />
          </div>
          <div className={s.reading} style={pane === 'off' && !current && !compose ? { display: 'none' } : undefined}>
            {pane === 'off' && current && !compose && (
              <Button className={s.back} appearance="subtle" icon={<ArrowLeft20Regular />} onClick={() => setSelected([])}>{t('Zurück zur Liste')}</Button>
            )}
            {compose ? (
              <Compose key={compose.id} ref={composeRef} init={compose.init} accounts={accounts} onPopOut={popOutCompose}
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
            ) : current ? (
              <ReadingPane item={current} onLoaded={m => {
                setMessage(m)
                if (m.seen && !current.seen) {
                  setItems(list => list.map(x => x.id === current.id ? { ...x, seen: true } : x))
                  bumpUnread([{ account: current.account, folder: current.folder, delta: -1 }])
                }
              }} onReply={reply} onForward={forward} onAct={act} />
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
              <div className={todoFloating ? s.flyout : undefined} style={todoFloating ? { right: 0 } : { display: 'flex' }}>
                <TodoBar onWrite={(address, name) => accounts?.length && setCompose({ account: sel?.account ?? accounts[0].id, to: [name ? `"${name}" <${address}>` : address], cc: [], bcc: [], subject: '', html: '' })} />
              </div>
            </>
          )}
        </div>
      </div>
      </div>
      {ctx && (
        <Menu open onOpenChange={(_, d) => !d.open && setCtx(null)}
          positioning={{ target: { getBoundingClientRect: () => ({ x: ctx.x, y: ctx.y, top: ctx.y, left: ctx.x, bottom: ctx.y, right: ctx.x, width: 0, height: 0 }) } as any }}>
          <MenuPopover>{actionList(ctx.item, () => setCtx(null))}</MenuPopover>
        </Menu>
      )}
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
        <RibbonButton icon={<FolderArrowRight20Regular />} label={t('Verschieben')} disabled={disabled} />
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
      draftUid: item.uid, inReplyTo: m.inReplyTo, references: m.references,
    })).catch(e => { setFailed(true); fail(e) })
  }, [item.id]) // eslint-disable-line react-hooks/exhaustive-deps
  if (failed) return <Text style={{ margin: 'auto', color: tokens.colorNeutralForeground3 }}>{t('Der Entwurf konnte nicht geöffnet werden.')}</Text>
  return <Spinner style={{ margin: 'auto' }} />
}
