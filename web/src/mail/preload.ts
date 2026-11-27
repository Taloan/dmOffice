import { useSyncExternalStore } from 'react'
import { Account, api, featureOn, Folder, MailItem, q } from '../api'
import { currentPrefs } from '../prefs'
import { prefetch } from './messageCache'
import { hasMore } from './listPaging'

/**
 * Mails vorhalten (Kennung mailprefetch): Eine Zahl an einem Ordner (Ordnerleiste, Favoriten, „Alle Posteingänge“, Symbol im
 * Seitenmenü) steigt erst, wenn die Liste dieses Ordners samt den neuen Zeilen und dem Inhalt der neuen Mails hier im Speicher
 * liegt. Ein Klick auf einen Ordner mit Zahl zeigt die Liste dann sofort, ohne Ladekreis. Beim Öffnen der App werden die Listen
 * aller Ordner mit Zahl geholt (Posteingänge, Favoriten, „Alle Posteingänge“ zuerst); bis dahin bleibt die Zahl weg.
 *
 * Dasselbe gilt für Suchordner, Kategorien und Catchall-Ansichten (/api/mail/virtual-counts). Im Prio-Posteingang liegen beide
 * Reiter (Relevant und Sonstige) bereit. Die übrigen Ordner werden nach dem Vorladen einmal still nacheinander geholt (nur die Liste),
 * ein Ordner unter dem Mauszeiger sofort - auch ein Klick dorthin zeigt dann gleich eine Liste.
 *
 * Alles nur im Arbeitsspeicher der Seite - nichts davon kommt in localStorage oder IndexedDB.
 */

export const UNIFIED = '__unified'

export type CachedList = { items: MailItem[]; total: number; more: boolean }

/** Gemerkte Listen je Ordner und Ansicht (Konto|Ordner|Filter|Sortierung[|threads]) - auch die zuletzt in der Mailliste gesehenen */
export const lists = new Map<string, CachedList>()
const MAX_LISTS = 120

export function rememberList(key: string, entry: CachedList) {
  lists.delete(key)
  lists.set(key, entry)
  while (lists.size > MAX_LISTS) lists.delete(lists.keys().next().value!)
}

/** Suchordner, Kategorie oder Catchall-Ansicht */
const isVirtual = (path: string) => path.startsWith('sf:') || path.startsWith('cat:') || path.startsWith('ca:')

/** Schlüssel einer Liste; „Alle Posteingänge“ und virtuelle Ordner gehören keinem Konto */
export const listKeyOf = (account: string, folder: string, filter: string, sort: string, threads: boolean) =>
  `${folder === UNIFIED || isVirtual(folder) ? '*' : account}|${folder}|${filter}|${sort}` + (threads ? '|threads' : '')

export const listUrlOf = (account: string, folder: string, filter: string, sort: string, threads: boolean, page: number, size: number) =>
  `/api/mail/${account}/messages?` + q({ folder, page: page || undefined, size, filter, sort: sort === 'newest' ? undefined : sort,
    threads: threads || undefined, // [feature:mailthreads]
  })

/** Listen, die gerade geholt werden: die Mailliste wartet darauf, statt dieselbe Anfrage noch einmal zu stellen */
const inflight = new Map<string, Promise<CachedList>>()
export const listInFlight = (key: string) => inflight.get(key)

const on = () => featureOn('mailprefetch')

// ---- Zustand

type Counts = { unread: number; total: number }
let accounts: Account[] | null = null
let accountsLoad: Promise<Account[]> | null = null
/** Letzte Ordnerlisten je Konto, wie vom Server */
const folders = new Map<string, Folder[]>()
/** Freigegebene Zahlen je Konto|Ordner: so weit liegt die Liste schon hier */
const released = new Map<string, Counts>()
/** Ordner, deren Zahl erst nach dem Vorladen steigt */
let gated = new Set<string>()
/** Prio-Posteingang je Konto (Ansicht „Relevant“ statt der ganzen Liste) */
const focusOn = new Map<string, boolean>()
/** Noch vorzuladen */
const dirty = new Set<string>()
let running = false
let lastVersion: unknown = undefined

/** Zähler je Suchordner, Kategorie und Catchall-Ansicht, wie vom Server */
let virtual: Record<string, Counts> = {}
/** Konto, an dem die virtuellen Ordner in der Mailansicht hängen (das erste) */
const anchor = () => accounts?.[0]?.id
/** Zählt hoch, wenn eine Runde Vorladen fertig ist (oder nichts vorzuladen war): danach dürfen auch die Zahlen der Reiter nachziehen */
let settled = 0
export const settledRound = () => settled

const listeners = new Set<() => void>()
let version = 0
const emit = () => { version++; for (const l of listeners) l() }
export function usePreloadVersion() {
  return useSyncExternalStore(cb => { listeners.add(cb); return () => listeners.delete(cb) }, () => version)
}

/** Ansicht der Liste wie beim Klick in der Mailliste (ohne eigenen Filter) */
const view = () => {
  const sort = currentPrefs.listSort ?? 'newest'
  const threads = featureOn('mailthreads') && !!currentPrefs.mailThreads && (sort === 'newest' || sort === 'oldest')
  return { sort, threads }
}

/** Zeigt der Ordner eine Zahl? Wie FolderPane.counter */
const countMode = (account: string, f: Folder) =>
  currentPrefs.folderCount?.[account + '|' + f.path] ?? (f.special === 'drafts' || f.special === 'snoozed' ? 'total' : 'unread')
const showsCount = (account: string, f: Folder) =>
  countMode(account, f) === 'total' ? f.total > 0 : f.unread > 0 && f.special !== 'sent' && f.special !== 'trash'

const inUnified = (a: Account) => a.inUnified !== false || !featureOn('unifiedpick')
const inboxOf = (account: string) => folders.get(account)?.find(f => f.special === 'inbox')
const unifiedShown = () => {
  const list = (accounts ?? []).filter(a => inUnified(a) && inboxOf(a.id))
  return (currentPrefs.showUnified ?? true) && (accounts?.length ?? 0) > 1 && list.length > 1
}

/** Ordner mit Vorrang (Posteingänge, Favoriten) und die übrigen mit Zahl (höchstens 12) */
function gatedFolders(): { first: string[]; rest: string[] } {
  const first: string[] = [], rest: string[] = []
  const favs = new Set(currentPrefs.favorites ?? [])
  for (const a of accounts ?? []) for (const f of folders.get(a.id) ?? []) {
    const k = a.id + '|' + f.path
    if (f.special === 'inbox' || favs.has(k)) first.push(k)
    else if (f.selectable !== false && showsCount(a.id, f)) rest.push(k)
  }
  const at = anchor()
  if (at) for (const [path, c] of Object.entries(virtual)) {
    const total = currentPrefs.folderCount?.[path] === 'total' || currentPrefs.folderCount?.[at + '|' + path] === 'total'
    if (c.unread > 0 || (total && c.total > 0)) rest.push(at + '|' + path)
  }
  return { first, rest: rest.slice(0, 16) }
}

const countsOf = (k: string): Counts | undefined => {
  const i = k.indexOf('|')
  if (isVirtual(k.slice(i + 1))) return virtual[k.slice(i + 1)]
  const f = folders.get(k.slice(0, i))?.find(x => x.path === k.slice(i + 1))
  return f && { unread: f.unread, total: f.total }
}

/** Neue Ordnerzahlen vom Server: gesunkene gleich übernehmen, gestiegene erst nach dem Vorladen */
function observe() {
  const { first, rest } = gatedFolders()
  gated = new Set([...first, ...rest])
  for (const k of gated) {
    const now = countsOf(k)!
    const r = released.get(k)
    if (r && now.unread <= r.unread && now.total <= r.total) { released.set(k, now); continue }
    if (r) released.set(k, { unread: Math.min(r.unread, now.unread), total: Math.min(r.total, now.total) })
    dirty.add(k)
  }
  if (!dirty.size && !running) settled++
  emit()
  run()
}

// ---- Öffentlich: Zahlen zur Anzeige

/** Ordner mit den Zahlen, die schon angezeigt werden dürfen */
export function shownFolder(account: string, f: Folder): Folder {
  if (!on()) return f
  const k = account + '|' + f.path
  if (!gated.has(k)) return f
  const r = released.get(k)
  const unread = Math.min(f.unread, r?.unread ?? 0), total = Math.min(f.total, r?.total ?? 0)
  return unread === f.unread && total === f.total ? f : { ...f, unread, total }
}

/** Zähler eines Suchordners, einer Kategorie oder Catchall-Ansicht, die schon angezeigt werden dürfen (account: Konto der Ansicht) */
export function shownVirtual(account: string, path: string, c: Counts | undefined): Counts | undefined {
  if (!on() || !c) return c
  const k = (anchor() ?? account) + '|' + path
  if (!gated.has(k)) return c
  const r = released.get(k)
  return { unread: Math.min(c.unread, r?.unread ?? 0), total: Math.min(c.total, r?.total ?? 0) }
}

/** Ungelesene im Posteingang eines Kontos für das Seitenmenü (Stand des Servers aus /status); null = noch nichts vorgeladen */
export function shownInbox(account: string, server: number): number | null {
  if (!on()) return server
  const inbox = inboxOf(account)
  const r = inbox && released.get(account + '|' + inbox.path)
  return r ? Math.min(server, r.unread) : null
}

/** Prio-Posteingang des Kontos an? (undefined = noch nicht bekannt) */
export const focusKnown = (account: string) => focusOn.get(account)

// ---- Laden

async function loadAccounts(): Promise<Account[]> {
  accountsLoad ??= api.get<Account[]>('/api/mail/accounts').then(l => { accounts = l; return l }).finally(() => { accountsLoad = null })
  return accountsLoad
}

let foldersLoad: { at: number; ids: string; p: Promise<[string, Folder[]][]> } | null = null
/**
 * Ordner aller Konten holen (auch für die Mailansicht). Kurz hintereinander angefragt (Seitenmenü und Mailansicht melden dieselbe
 * neue Version) geht nur eine Anfrage je Konto raus.
 */
export function fetchFolders(list: Account[]): Promise<[string, Folder[]][]> {
  const ids = list.map(a => a.id).join(',')
  if (foldersLoad && foldersLoad.ids === ids && performance.now() - foldersLoad.at < 1000) return foldersLoad.p
  const p = Promise.all(list.map(async a => [a.id, await api.get<Folder[]>(`/api/mail/${a.id}/folders`).catch(() => [] as Folder[])] as [string, Folder[]]))
    .then(entries => {
      if (on()) {
        if (!accounts || accounts.map(a => a.id).join(',') !== ids) accounts = list
        for (const [id, fs] of entries) folders.set(id, fs)
        observe()
      }
      return entries
    })
  foldersLoad = { at: performance.now(), ids, p }
  return p
}

let virtualLoad: Promise<Record<string, Counts>> | null = null
let virtualAgain = false
/**
 * Zähler der virtuellen Ordner holen. Immer nur eine Anfrage zugleich (das Zählen kann beim Server dauern; jede weitere hielte eine
 * Verbindung des Browsers fest, die dann z. B. dem Long-Poll für neue Mails fehlt); wer währenddessen fragt, bekommt danach eine
 * frische Runde. Das Ergebnis meldet usePreloadVersion.
 */
export function fetchVirtualCounts(): void {
  if (virtualLoad) { virtualAgain = true; return }
  virtualLoad = api.get<Record<string, Counts>>('/api/mail/virtual-counts')
  virtualLoad.then(c => {
    virtual = c
    if (on() && accounts && folders.size) observe(); else emit()
  }, () => {}).finally(() => {
    virtualLoad = null
    if (virtualAgain) { virtualAgain = false; fetchVirtualCounts() }
  })
}
/** Letzter Stand der Zähler virtueller Ordner (vom Server; zur Anzeige durch shownVirtual) */
export const virtualCounts = () => virtual

/** Seitenmenü: neuer Stand von /api/mail/status - bei neuer Version Ordner holen (und damit vorladen) */
export function statusSeen(status: any) {
  if (!on() || !status?.accounts || !Object.keys(status.accounts).length) return
  if (status.version === lastVersion && accounts) return
  lastVersion = status.version
  ;(accounts ? Promise.resolve(accounts) : loadAccounts()).then(fetchFolders).catch(() => {})
}

if (typeof window !== 'undefined') {
  // Postfächer angelegt, geändert oder entfernt
  window.addEventListener('dmoffice:mailaccounts', () => { if (on()) loadAccounts().then(fetchFolders).catch(() => {}) })
  // Favoriten, Sortierung oder Zählweise geändert: andere Ordner vorladen
  window.addEventListener('dmoffice:prefs', () => { if (on() && accounts && folders.size) observe() })
}

/** Liste holen und merken; neue ungelesene Mails gegenüber der gemerkten Liste samt Inhalt vorladen */
async function loadList(account: string, folder: string, contents: boolean): Promise<void> {
  let filters = ['']
  if (folder !== UNIFIED && inboxOf(account)?.path === folder) {
    if (!focusOn.has(account)) {
      const r = await api.get<{ enabled: boolean }>(`/api/mail/${account}/focus?` + q({ folder })).catch(() => null)
      if (r) focusOn.set(account, r.enabled)
    }
    // Prio-Posteingang: beide Reiter
    if (focusOn.get(account)) filters = ['focused', 'other']
  }
  await Promise.all(filters.map(f => loadOne(account, folder, f, contents)))
}

async function loadOne(account: string, folder: string, filter: string, contents: boolean): Promise<void> {
  const { sort, threads } = view()
  const key = listKeyOf(account, folder, filter, sort, threads)
  const before = lists.get(key)
  let p = inflight.get(key)
  if (!p) {
    p = api.get<{ items: MailItem[]; total: number }>(listUrlOf(account, folder, filter, sort, threads, 0, 100))
      .then(r => ({ items: r.items, total: r.total, more: hasMore(0, 100, r.items.length, r.total) }))
    inflight.set(key, p)
    p.then(l => rememberList(key, l), () => {}).finally(() => inflight.delete(key))
  }
  const l = await p
  if (!contents) return
  // Neu dazugekommen (gegenüber der gemerkten Liste) oder beim ersten Mal: die obersten ungelesenen
  const had = before && new Set(before.items.map(i => i.id))
  const fresh = l.items.filter(i => !i.seen && (!had || !had.has(i.id))).slice(0, 3)
  // Höchstens 8 s auf die Inhalte warten - eine Zahl soll nicht an einer hängenden Mail hängenbleiben
  if (fresh.length) await Promise.race([prefetch(fresh), new Promise(r => setTimeout(r, 8000))])
}

/** Vorladen, nacheinander in Runden: erst Posteingänge, Favoriten und „Alle Posteingänge“, dann die übrigen Ordner mit Zahl */
async function run() {
  if (running || !dirty.size || !accounts) return
  running = true
  try {
    while (dirty.size) {
      const { first } = gatedFolders()
      const prio = new Set(first)
      let batch = [...dirty].filter(k => prio.has(k))
      if (!batch.length) batch = [...dirty]
      for (const k of batch) dirty.delete(k)
      // Stand, den die Listen mindestens enthalten: vor dem Holen festgehalten
      const target = new Map(batch.filter(k => gated.has(k)).map(k => [k, countsOf(k)!]))
      const startup = batch.some(k => !released.has(k))
      const jobs: [string[], () => Promise<void>][] = []
      for (const k of target.keys()) {
        const i = k.indexOf('|')
        // Beim ersten Laden nur die Liste; danach auch den Inhalt der neuen Mails
        jobs.push([[k], () => loadList(k.slice(0, i), k.slice(i + 1), released.has(k))])
      }
      // Ein Posteingang ändert sich: „Alle Posteingänge“ mit auffrischen, bevor seine Zahl steigt
      const inboxes = [...target.keys()].filter(k => { const i = k.indexOf('|'); return inboxOf(k.slice(0, i))?.path === k.slice(i + 1) })
      if (inboxes.length && unifiedShown()) {
        const acc = accounts.find(a => inUnified(a))?.id ?? accounts[0].id
        jobs.push([inboxes, () => loadList(acc, UNIFIED, !startup)])
      }
      // Höchstens drei Anfragen zugleich
      const failed = new Set<string>()
      let next = 0
      await Promise.all(Array.from({ length: Math.min(3, jobs.length) }, async () => {
        while (next < jobs.length) {
          const [keys, job] = jobs[next++]
          try { await job() } catch { for (const k of keys) failed.add(k) }
        }
      }))
      for (const [k, c] of target) {
        if (failed.has(k)) continue
        // Inzwischen gesunken (gelesen, verschoben): der niedrigere Stand gilt; inzwischen gestiegen: die nächste Runde
        const now = countsOf(k) ?? c
        released.set(k, { unread: Math.min(c.unread, now.unread), total: Math.min(c.total, now.total) })
      }
      emit()
      // Gescheitert (Server nicht erreichbar): später noch einmal
      if (failed.size) setTimeout(() => { for (const k of failed) dirty.add(k); run() }, 15000)
    }
  } finally { running = false }
  settled++
  emit()
  quiet()
}

/** Gerade unter dem Mauszeiger (Ordnerbereich): Liste holen, wenn sie noch nicht hier liegt - der Klick danach zeigt sie gleich */
export function warm(account: string, folder: string) {
  if (!on() || !accounts || folder === '__outbox' || folder === '__nudge') return
  if (folder !== UNIFIED && !isVirtual(folder) && !folders.get(account)?.some(f => f.path === folder && f.selectable !== false)) return
  const { sort, threads } = view()
  const inbox = folder !== UNIFIED && inboxOf(account)?.path === folder
  // Prio-Posteingang noch unbekannt: loadList fragt nach
  const keys = inbox && focusOn.get(account) !== false ? (focusOn.has(account) ? ['focused', 'other'] : ['focused', 'other', '']) : ['']
  if (keys.some(f => lists.has(listKeyOf(account, folder, f, sort, threads)) || inflight.has(listKeyOf(account, folder, f, sort, threads)))) return
  loadList(account, folder, false).catch(() => {})
}

/** Einmal nach dem ersten Vorladen: die übrigen Ordner still nacheinander holen (nur die Liste, höchstens 30) */
let quietDone = false
function quiet() {
  if (quietDone || !accounts) return
  quietDone = true
  const todo: [string, string][] = []
  for (const a of accounts) for (const f of folders.get(a.id) ?? [])
    if (f.selectable !== false && f.total > 0 && !gated.has(a.id + '|' + f.path)) todo.push([a.id, f.path])
  ;(async () => {
    for (const [acc, path] of todo.slice(0, 30)) {
      await new Promise(r => setTimeout(r, 250))
      // Nur, wenn die Seite sichtbar ist und gerade nichts anderes vorgeladen wird
      while (running || document.hidden) await new Promise(r => setTimeout(r, 1000))
      const { sort, threads } = view()
      const key = listKeyOf(acc, path, '', sort, threads)
      if (lists.has(key) || inflight.has(key)) continue
      await loadOne(acc, path, '', false).catch(() => {})
    }
  })()
}
