import {
  Button, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, SearchBox, Spinner, Text, Toolbar, Tooltip,
  makeStyles, mergeClasses, tokens, MessageBar, MessageBarBody, MessageBarActions,
} from '@fluentui/react-components'
import {
  Note20Regular, Star20Regular, Star20Filled, Archive20Regular, Delete20Regular, Folder20Regular, ArrowSync20Regular,
  ArrowUndo20Regular, Color20Regular, Attach20Regular, Dismiss16Regular, FolderArrowRight20Regular, FolderAdd20Regular,
  TextBold20Regular, TextItalic20Regular, TextUnderline20Regular, TextStrikethrough20Regular, TextBulletListLtr20Regular,
  TextNumberListLtr20Regular, TextColor20Regular, Highlight20Regular, LockClosed16Regular, CheckmarkCircle16Regular, Circle16Filled,
  TextAlignLeft20Regular, TextAlignCenter20Regular, TextAlignRight20Regular, TextIndentIncreaseLtr20Regular, TextIndentDecreaseLtr20Regular,
  CheckboxChecked20Regular, Code20Regular,
} from '@fluentui/react-icons'
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { e2eFetch } from '../e2e'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { useDebounced, fmtDate } from '../shell/hooks'
import { useIsDark } from '../theme'
import { cleanPaste, plainToHtml } from './cleanPaste'
import { fixContrast } from './contrast'
import { t } from '../i18n'
import { MenuItemRadio, MenuDivider } from '@fluentui/react-components'
import { NoteAdd20Regular, Database20Regular, PanelLeft20Regular, TextDescription20Regular, Settings20Regular } from '@fluentui/react-icons'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { usePrefs } from '../prefs'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0 },
  column: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  panes: { flex: 1, display: 'flex', minHeight: 0, minWidth: 0 },
  side: { width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '12px 8px', display: 'flex', flexDirection: 'column', gap: '2px', overflowY: 'auto' },
  nav: { display: 'flex', alignItems: 'center', gap: '10px', padding: '7px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  active: { backgroundColor: tokens.colorBrandBackground2, fontWeight: 600 },
  count: { marginLeft: 'auto', fontSize: '12px', color: tokens.colorNeutralForeground3 },
  list: { width: '300px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', minHeight: 0 },
  cards: { flex: 1, overflowY: 'auto', padding: '4px 10px 10px', display: 'flex', flexDirection: 'column', gap: '8px' },
  // Ruhig wie die Mailliste: neutrale Karte, schmaler Balken in der Farbe der Notiz, alle gleich hoch
  card: {
    padding: '8px 12px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', flexShrink: 0, boxSizing: 'border-box',
    display: 'flex', flexDirection: 'column', height: '92px', overflow: 'hidden',
    color: tokens.colorNeutralForeground1, backgroundColor: tokens.colorNeutralBackground2,
    borderTop: '3px solid #FFF9A8',
    ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  cardShort: { height: '58px' },
  cardSel: { outline: `2px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorNeutralBackground2Selected },
  cardTitle: { fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', flexShrink: 0 },
  cardText: { fontSize: '12px', color: tokens.colorNeutralForeground3, display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden', marginTop: '2px', lineHeight: '16px' },
  cardDate: { fontSize: '11px', color: tokens.colorNeutralForeground4, marginTop: 'auto' },
  editor: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  head: { display: 'flex', alignItems: 'center', gap: '6px', padding: '8px 12px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  titleInput: { flex: 1 },
  bar: { borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, padding: '2px 8px', flexWrap: 'wrap' },
  // Neutral wie die App, die Notizfarbe nur als Streifen oben
  paper: { flex: 1, overflowY: 'auto', padding: '20px 28px', fontSize: '15px', color: tokens.colorNeutralForeground1, backgroundColor: tokens.colorNeutralBackground1 },
  stripe: { height: '6px', flexShrink: 0 },
  atts: { display: 'flex', flexWrap: 'wrap', gap: '6px', padding: '8px 12px', borderTop: `1px solid ${tokens.colorNeutralStroke2}` },
  att: { display: 'flex', alignItems: 'center', gap: '6px', padding: '2px 4px 2px 10px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, fontSize: '13px' },
  swatches: { display: 'grid', gridTemplateColumns: 'repeat(4, 28px)', gap: '6px', padding: '8px' },
  swatch: { width: '28px', height: '28px', borderRadius: '6px', border: `1px solid ${tokens.colorNeutralStroke1}`, cursor: 'pointer', padding: 0 },
  empty: { margin: 'auto', textAlign: 'center', color: tokens.colorNeutralForeground3, maxWidth: '420px' },
})

// Notizfarben wie die Kurznotizen
const noteColors = ['#FFF9A8', '#D9F2C4', '#FFD9E8', '#E6D9FF', '#CFE8FF', '#FFE2C2', '#EDEDED', '#3B3B3B']
const textColors = ['#000000', '#C00000', '#E8590C', '#2E7D32', '#0F6CBD', '#7B1FA2']
const marks = ['#FFF176', '#A5D6A7', '#90CAF9', '#F48FB1', 'transparent']


interface NoteListItem { id: number; syncId: string; folder: string | null; title: string; snippet: string; color: string; modified: string; important: boolean; attachments: number; archived: string | null; deleted: string | null }
interface NoteFolder { syncId: string; parent: string | null; name: string }
interface NoteDetail {
  id: number; folder: string | null; customTitle: string | null; title: string; color: string; important: boolean
  archived: string | null; deleted: string | null; blocks: string[]; protected: boolean[]; readOnly: boolean
  attachments: { name: string; mime: string | null; size: number; blob: string }[]; pending: boolean; modified: string
  /** Fingerabdruck des Inhalts - beim Speichern zurueck, sonst 409 */
  version: string
}

type View = { kind: 'folder'; id: string | null } | { kind: 'all' | 'important' | 'archive' | 'trash' }

export default function NotesView() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [status, setStatus] = useState<any>(null)
  const [config, setConfig] = useState<any>(null)
  const [folders, setFolders] = useState<NoteFolder[]>([])
  const [view, setView] = useState<View>({ kind: 'all' })
  const [notes, setNotes] = useState<{ items: NoteListItem[]; archived: number; trashed: number } | null>(null)
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<number | null>(null)
  // Datenbanken unter der Sync-Wurzel (wie die Auswahl "Datenbank" in notizi)
  const [dbs, setDbs] = useState<{ current: string; list: { folder: string; title: string; passwordFits: boolean }[] | null } | null>(null)
  const [switching, setSwitching] = useState(false)
  const loadDbs = () => api.get('/api/notes/databases').then(setDbs).catch(() => {})
  useEffect(() => { loadDbs() }, [])
  // Direkt eine Notiz oeffnen: #/notes/<id> (aus "Notizi-Aufgaben")
  useEffect(() => {
    const open = () => {
      const m = location.hash.match(/^#\/notes\/(\d+)$/)
      if (!m) return
      history.replaceState(null, '', '#/notes')
      setSearch('')
      setView({ kind: 'all' })
      setSelected(Number(m[1]))
    }
    open()
    window.addEventListener('hashchange', open)
    return () => window.removeEventListener('hashchange', open)
  }, [])
  const switchDb = async (folder: string) => {
    if (folder === '__new') {
      const name = prompt(t('Name der neuen Datenbank (wird im Abgleichordner angelegt):'))
      if (!name?.trim()) return
      folder = name.trim()
    }
    setSwitching(true)
    setSelected(null)
    try {
      setStatus(await api.post('/api/notes/database', { folder }))
      await Promise.all([loadDbs(), loadNotes(), loadFolders()])
    } catch (e) { fail(e) } finally { setSwitching(false) }
  }
  const query = useDebounced(search, 300)
  const { prefs, update } = usePrefs()
  const preview = prefs.notePreview ?? true
  const [sidePane, toggleSide] = useSidePane('notes')

  const loadFolders = () => api.get<NoteFolder[]>('/api/notes/folders').then(setFolders).catch(() => {})
  const loadNotes = useCallback(() => {
    const params = query ? { q: query } : view.kind === 'folder' ? { folder: view.id ?? '' } : { view: view.kind }
    return api.get('/api/notes?' + q(params as any)).then(setNotes).catch(fail)
  }, [view, query, fail])

  useEffect(() => {
    api.get('/api/settings/notizi').then(setConfig).catch(() => setConfig({}))
    api.get('/api/notes/status').then(setStatus).catch(() => {})
    loadFolders()
  }, [])
  useEffect(() => { loadNotes() }, [loadNotes])
  // Stabil fuer den Editor
  const onChanged = useCallback(() => { loadNotes() }, [loadNotes])
  const onGone = useCallback(() => { setSelected(null); loadNotes() }, [loadNotes])
  const onOpen = useCallback(async (nid: number) => { await loadNotes(); setSelected(nid) }, [loadNotes])
  // Was von anderen Geraeten kommt, alle 30 Sekunden nachladen
  useEffect(() => {
    const iv = setInterval(() => { if (!document.hidden) { loadNotes(); api.get('/api/notes/status').then(setStatus).catch(() => {}) } }, 30000)
    return () => clearInterval(iv)
  }, [loadNotes])

  const sync = async () => {
    try { setStatus(await api.post('/api/notes/sync')); loadNotes(); loadFolders() } catch (e) { fail(e) }
  }

  const create = async () => {
    try {
      const r = await api.post('/api/notes', { folder: view.kind === 'folder' ? view.id : null })
      await loadNotes()
      setSelected(r.id)
    } catch (e) { fail(e) }
  }

  const newFolder = async () => {
    const name = prompt(t('Name des Ordners:'))
    if (!name) return
    try { await api.post('/api/notes/folders', { name, parent: null }); loadFolders() } catch (e) { fail(e) }
  }

  const tree = (parent: string | null, depth: number): JSX.Element[] =>
    folders.filter(f => f.parent === parent).flatMap(f => [
      <div key={f.syncId} className={mergeClasses(s.nav, view.kind === 'folder' && view.id === f.syncId && s.active)} style={{ paddingLeft: 10 + depth * 14 }}
        onClick={() => setView({ kind: 'folder', id: f.syncId })}
        onContextMenu={async e => {
          e.preventDefault()
          const name = prompt(t('Ordner umbenennen (leer lassen und OK: löschen, wenn leer):'), f.name)
          if (name === null) return
          try {
            if (name === '') await api.del(`/api/notes/folders/${f.syncId}`)
            else await api.put(`/api/notes/folders/${f.syncId}`, { name })
            loadFolders()
          } catch (err) { fail(err) }
        }}>
        <Folder20Regular />{f.name}
      </div>,
      ...tree(f.syncId, depth + 1),
    ])


  const nav = (v: View, icon: JSX.Element, label: string, n?: number) => (
    <div className={mergeClasses(s.nav, view.kind === v.kind && (v.kind !== 'folder' || (view as any).id === (v as any).id) && s.active)} onClick={() => { setView(v); setSearch('') }}>
      {icon}{label}<span className={s.count}>{n || ''}</span>
    </div>
  )

  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      <RibbonHeader area="notes"
        start={<>
          <RibbonButton primary icon={<NoteAdd20Regular />} label={t('Neue Notiz')} onClick={create} disabled={switching} />
          <RibbonDivider />
          <RibbonButton icon={<FolderAdd20Regular />} label={t('Neuer Ordner')} onClick={newFolder} />
          <RibbonDivider />
          {dbs?.list && (
            <Menu>
              <MenuTrigger disableButtonEnhancement><RibbonButton icon={<Database20Regular />} label={t('Datenbank')} disabled={switching} /></MenuTrigger>
              <MenuPopover>
                <MenuList checkedValues={{ db: [dbs.current] }} onCheckedValueChange={(_, d) => d.checkedItems[0] && switchDb(d.checkedItems[0])}>
                  {dbs.list.map(d => (
                    <MenuItemRadio key={d.folder} name="db" value={d.folder} disabled={!d.passwordFits}>
                      {d.title}{!d.passwordFits && ' ' + t('(anderes Passwort)')}
                    </MenuItemRadio>
                  ))}
                  <MenuDivider />
                  <MenuItem onClick={() => switchDb('__new')}>{t('Neue Datenbank …')}</MenuItem>
                </MenuList>
              </MenuPopover>
            </Menu>
          )}
          <RibbonButton icon={<ArrowSync20Regular />} label={t('Abgleichen')} onClick={sync} disabled={status?.running} />
        </>}
        view={<>
          <RibbonButton icon={<TextDescription20Regular />} label={t('Vorschau­text')} pressed={preview} onClick={() => update({ notePreview: !preview })} />
          <RibbonDivider />
          <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />
          <RibbonDivider />
          <RibbonButton icon={<Settings20Regular />} label={t('Notiz­einstellungen')} onClick={() => { location.hash = '#/settings/notes' }} />
        </>} />
      <div className={s.root}>
      <aside className={s.side} style={sidePane ? undefined : { display: 'none' }}>
        {dbs?.list && dbs.list.length > 1 && (
          <Text size={200} style={{ padding: '0 10px 8px', color: tokens.colorNeutralForeground3 }} truncate wrap={false}>
            {switching ? t('Wechsle …') : t('Datenbank: {name}', { name: dbs.list.find(d => d.folder === dbs.current)?.title ?? dbs.current ?? '—' })}
          </Text>
        )}
        {nav({ kind: 'all' }, <Note20Regular />, t('Alle Notizen'))}
        {nav({ kind: 'folder', id: null }, <Note20Regular />, t('Ohne Ordner'))}
        {nav({ kind: 'important' }, <Star20Regular />, t('Wichtig'))}
        <div style={{ display: 'flex', alignItems: 'center', marginTop: 12 }}>
          <Text size={200} weight="semibold" style={{ flex: 1, paddingLeft: 10 }}>{t('ORDNER')}</Text>
          <Tooltip content={t('Neuer Ordner')} relationship="label"><Button appearance="subtle" size="small" icon={<FolderAdd20Regular />} onClick={newFolder} /></Tooltip>
        </div>
        {tree(null, 0)}
        <div style={{ flex: 1 }} />
        {nav({ kind: 'archive' }, <Archive20Regular />, t('Archiv'), notes?.archived)}
        {nav({ kind: 'trash' }, <Delete20Regular />, t('Papierkorb'), notes?.trashed)}
        <div className={s.nav} onClick={sync} title={status?.error ?? status?.report ?? ''}>
          <ArrowSync20Regular />
          <span style={{ fontSize: 12 }}>
            {status?.running ? t('Gleicht ab …') : status?.error ? <span style={{ color: tokens.colorPaletteRedForeground1 }}>{t('Abgleich: Fehler')}</span>
              : status?.lastRun ? t('Abgeglichen {date}', { date: fmtDate(status.lastRun) }) : t('Abgleichen')}
          </span>
        </div>
      </aside>
      <div className={s.column}>
      <div className={s.panes}>
      <div className={s.list}>
        {config && !config.enabled && (
          <MessageBar intent="info" style={{ margin: '10px 10px 0' }}>
            <MessageBarBody>
              {t('Tipp: Mit der App notizi hast du deine Notizen auch auf dem Windows-Desktop und dem Handy – mit Erinnerungen, Texterkennung und Abgleich.')}{' '}
              <a href="#/settings/notes">{t('Abgleich einrichten')}</a>
            </MessageBarBody>
          </MessageBar>
        )}
        <div style={{ padding: 10 }}><SearchBox placeholder={t('Notizen durchsuchen')} value={search} onChange={(_, d) => setSearch(d.value)} style={{ width: '100%' }} /></div>
        <div className={s.cards}>
          {!notes ? <Spinner /> : notes.items.length === 0 ? <Text style={{ padding: 8, color: tokens.colorNeutralForeground3 }}>{t('Keine Notizen.')}</Text>
            : notes.items.map(n => (
              <div key={n.id} className={mergeClasses(s.card, !preview && s.cardShort, selected === n.id && s.cardSel)} onClick={() => setSelected(n.id)}
                style={{ borderTopColor: n.color || '#FFF9A8' }}>
                <div className={s.cardTitle}>{n.important && '★ '}{n.title}</div>
                {preview && n.snippet && <div className={s.cardText}>{n.snippet}</div>}
                <div className={s.cardDate}>{fmtDate(n.modified)}{n.attachments > 0 && ` · 📎 ${n.attachments}`}</div>
              </div>
            ))}
        </div>
      </div>
      {selected ? (
        <NoteEditor key={selected} id={selected} folders={folders}
          onChanged={onChanged} onGone={onGone} onOpen={onOpen} notify={notify} fail={fail} />
      ) : (
        <div className={s.empty}><Text>{t('Wähle eine Notiz aus oder lege eine neue an.')}</Text></div>
      )}
      </div>
      </div>
      </div>
    </div>
  )
}

function NoteEditor({ id, folders, onChanged, onGone, onOpen, notify, fail }: {
  id: number; folders: NoteFolder[]; onChanged: () => void; onGone: () => void; onOpen: (id: number) => void
  notify: (t: string, b?: string, i?: any) => void; fail: (e: unknown) => void
}) {
  const s = useStyles()
  const [note, setNote] = useState<NoteDetail | null>(null)
  const [title, setTitle] = useState('')
  const [saving, setSaving] = useState<'idle' | 'dirty' | 'saving' | 'saved'>('idle')
  const root = useRef<HTMLDivElement | null>(null)
  // Das Element ueber das Aushaengen hinaus festhalten - beim Notizwechsel ist root.current schon null
  const lastEl = useRef<HTMLDivElement | null>(null)
  const setRoot = useCallback((el: HTMLDivElement | null) => { root.current = el; if (el) lastEl.current = el }, [])
  const ser0 = useRef<string[]>([])
  const timer = useRef<number | undefined>(undefined)
  const fileInput = useRef<HTMLInputElement>(null)
  const isDark = useIsDark()
  const [rev, setRev] = useState(0)
  const [rejected, setRejected] = useState<string | null>(null)
  const rejectedRef = useRef(false)
  rejectedRef.current = !!rejected
  // Fassung, auf die sich die Blocknummern im Editor beziehen, und welche Bloecke gesperrt sind
  const version = useRef<string | undefined>(undefined)
  const prot = useRef<boolean[]>([])
  // Ungespeicherte Aenderungen im Editor
  const dirty = useRef(false)
  // Speichern nacheinander, sonst schickt der zweite Aufruf eine veraltete Fassung
  const chain = useRef<Promise<void>>(Promise.resolve())

  /** Laedt die Notiz. Den Editor nur neu aufbauen, wenn sich der Inhalt geaendert hat und nichts offen ist. */
  const load = useCallback(async (force = false) => {
    try {
      const n = await api.get<NoteDetail>(`/api/notes/${id}`)
      setNote(n)
      setTitle(n.customTitle ?? '')
      if (force || version.current === undefined || (n.version !== version.current && !dirty.current)) {
        version.current = n.version
        dirty.current = false
        setRev(r => r + 1)
      }
    } catch (e) { fail(e) }
  }, [id, fail])
  useEffect(() => { load() }, [load])

  // Bloecke in den Editor - jeder mit seiner Nummer, gesperrte nicht bearbeitbar
  useEffect(() => {
    const el = root.current
    if (!el || !note) return
    prot.current = note.protected
    el.innerHTML = ''
    note.blocks.forEach((html, i) => {
      const tpl = document.createElement('template')
      tpl.innerHTML = html.trim().startsWith('<') ? html : `<p>${html}</p>`
      const nodes = Array.from(tpl.content.children) as HTMLElement[]
      const node = nodes[0] ?? document.createElement('p')
      node.setAttribute('data-i', String(i))
      if (note.protected[i] || note.readOnly) node.setAttribute('contenteditable', 'false')
      node.querySelectorAll('img').forEach(img => {
        const src = img.getAttribute('src') || ''
        if (src.startsWith('blob:')) { img.setAttribute('data-blob', src.slice(5)); img.setAttribute('src', `/api/notes/blob/${src.slice(5)}`) }
      })
      el.appendChild(node)
      nodes.slice(1).forEach(n => el.appendChild(n))
    })
    if (!el.children.length) el.appendChild(document.createElement('p'))
    fixContrast(el)
    ser0.current = Array.from(el.children).map(c => ser(c as HTMLElement))
  }, [note?.id, rev]) // eslint-disable-line react-hooks/exhaustive-deps

  // Hell/Dunkel gewechselt: Kontrast neu pruefen (Inhalt bleibt)
  useEffect(() => { if (root.current) fixContrast(root.current) }, [isDark])

  const ser = (node: HTMLElement): string => {
    const clone = node.cloneNode(true) as HTMLElement
    clone.removeAttribute('data-i')
    clone.removeAttribute('contenteditable')
    clone.removeAttribute('data-dm-contrast')
    clone.querySelectorAll('[data-dm-contrast]').forEach(e => e.removeAttribute('data-dm-contrast'))
    clone.querySelectorAll('img[data-blob]').forEach(img => {
      img.setAttribute('src', 'blob:' + img.getAttribute('data-blob'))
      img.removeAttribute('data-blob')
    })
    return clone.outerHTML
  }

  const textBlock = (txt: string) => `<p>${txt.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</p>`

  const doSave = async (plain?: string) => {
    const el = root.current ?? lastEl.current
    if (!el || !note || note.readOnly) return
    const used = new Set<number>()
    const blocks: ({ i: number } | { html: string })[] = []
    // Je Block der Knoten und sein Stand beim Absenden - danach die Nummern nachfuehren
    const nodes: (HTMLElement | null)[] = []
    const snap: string[] = []
    el.childNodes.forEach(n => {
      if (n.nodeType === 3) { if (n.textContent?.trim()) { blocks.push({ html: textBlock(n.textContent) }); nodes.push(null); snap.push('') } return }
      if (n.nodeType !== 1) return
      const node = n as HTMLElement
      const now = ser(node)
      const i = node.hasAttribute('data-i') ? Number(node.getAttribute('data-i')) : -1
      if (i >= 0 && !used.has(i) && (prot.current[i] || now === ser0.current[i])) { used.add(i); blocks.push({ i }) }
      else { if (i >= 0) used.add(i); blocks.push({ html: node.tagName === 'DIV' && !node.hasAttribute('data-i') ? `<p>${node.innerHTML}</p>` : now }) }
      nodes.push(node); snap.push(now)
    })
    dirty.current = false
    setSaving('saving')
    try {
      const r = await e2eFetch(`/api/notes/${id}/content`, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, credentials: 'same-origin',
        body: JSON.stringify({ blocks, plain: plain ?? el.innerText, version: version.current }),
      })
      const data = await r.json().catch(() => null)
      if (!r.ok) throw new Error(data?.error || r.statusText)
      version.current = data?.version
      renumber(el, blocks, nodes, snap, data?.protected ?? null)
      setSaving(dirty.current ? 'dirty' : 'saved')
      setRejected(null)
      onChanged()
    } catch (e) {
      dirty.current = true
      setSaving('dirty')
      setRejected(e instanceof Error ? e.message : String(e))
    }
  }

  /** Nach dem Speichern: Nummern im Editor auf die neue Fassung umstellen. */
  const renumber = (el: HTMLElement, blocks: ({ i: number } | { html: string })[], nodes: (HTMLElement | null)[], snap: string[], flags: boolean[] | null) => {
    el.querySelectorAll(':scope > [data-i]').forEach(n => n.removeAttribute('data-i'))
    ser0.current = []
    if (!flags) { prot.current = []; return }
    prot.current = flags
    if (flags.length === blocks.length) {
      // Ein Block je Knoten: Nummer gleich Position, Stand vom Absenden als Vergleich
      nodes.forEach((node, k) => { if (node) { node.setAttribute('data-i', String(k)); ser0.current[k] = snap[k] } })
      return
    }
    // Sonst nur die gesperrten Bloecke zuordnen (Reihenfolge bleibt), der Rest geht beim naechsten Mal als HTML
    const target = flags.map((f, k) => (f ? k : -1)).filter(k => k >= 0)
    const sent = nodes.filter((node, k) => node && 'i' in blocks[k] && node.getAttribute('contenteditable') === 'false')
    if (sent.length === target.length) sent.forEach((node, k) => node!.setAttribute('data-i', String(target[k])))
  }

  // Immer die aktuelle Fassung aufrufen (keine veralteten Closures), nacheinander
  const saveRef = useRef(doSave)
  saveRef.current = doSave
  const save = useCallback((plain?: string) => (chain.current = chain.current.then(() => saveRef.current(plain))), [])

  const changed = () => {
    dirty.current = true
    setSaving('dirty')
    clearTimeout(timer.current)
    timer.current = undefined
    // Nach abgelehntem Speichern nicht dauernd neu versuchen
    if (rejectedRef.current) return
    timer.current = window.setTimeout(() => { timer.current = undefined; save() }, 1200)
  }
  /** Ausstehende Aenderungen sofort speichern (vor Meta-Aktionen und beim Wechsel). */
  const flush = async () => {
    if (timer.current !== undefined) { clearTimeout(timer.current); timer.current = undefined; await save() }
    else await chain.current
  }
  // Beim Wechseln der Notiz sofort sichern - als Layout-Cleanup, da steht der Editor noch im DOM
  useLayoutEffect(() => () => {
    // Text jetzt lesen - ausgehaengt liefert innerText keine Zeilenumbrueche mehr
    if (timer.current !== undefined) { clearTimeout(timer.current); timer.current = undefined; save(root.current?.innerText) }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const meta = async (body: any) => {
    try { await flush(); await api.put(`/api/notes/${id}/meta`, body); await load(); onChanged() } catch (e) { fail(e) }
  }
  const action = async (a: 'archive' | 'restore' | 'trash') => {
    try { await flush(); await api.post(`/api/notes/${id}/${a}`); a === 'restore' ? (await load(), onChanged()) : onGone() } catch (e) { fail(e) }
  }
  /** Aenderungen verwerfen und den Stand vom Server laden. */
  const discard = () => {
    clearTimeout(timer.current); timer.current = undefined
    dirty.current = false
    setRejected(null); setSaving('saved')
    load(true)
  }
  /** Bei Konflikt: den Editorinhalt als neue Notiz sichern, diese bleibt unveraendert. */
  const saveAsCopy = async () => {
    const el = root.current
    if (!el || !note) return
    const blocks: { html: string }[] = []
    el.childNodes.forEach(n => {
      if (n.nodeType === 3) { if (n.textContent?.trim()) blocks.push({ html: textBlock(n.textContent) }); return }
      if (n.nodeType !== 1) return
      const node = n as HTMLElement
      blocks.push({ html: node.tagName === 'DIV' && !node.hasAttribute('data-i') ? `<p>${node.innerHTML}</p>` : ser(node) })
    })
    try {
      const r = await api.post<{ id: number }>('/api/notes', { folder: note.folder })
      await api.put(`/api/notes/${r.id}/content`, { blocks, plain: el.innerText })
      clearTimeout(timer.current); timer.current = undefined
      dirty.current = false
      setRejected(null)
      notify(t('Als Kopie gespeichert'))
      onOpen(r.id)
    } catch (e) { fail(e) }
  }
  const destroy = async () => {
    if (!confirm(t('Endgültig löschen? Auch auf allen anderen Geräten.'))) return
    try { await api.del(`/api/notes/${id}`); onGone() } catch (e) { fail(e) }
  }
  const exec = (cmd: string, value?: string) => { document.execCommand('styleWithCSS', false, 'true'); document.execCommand(cmd, false, value); changed() }

  /** Der Absatz bzw. Listenpunkt, in dem die Schreibmarke steht (innerhalb des Editors). */
  const currentBlock = (): HTMLElement | null => {
    const sel = window.getSelection()
    let n: Node | null = sel?.anchorNode ?? null
    while (n && n !== root.current) {
      if (n.nodeType === 1 && ['P', 'LI', 'DIV', 'H1', 'H2', 'H3'].includes((n as HTMLElement).tagName)) return n as HTMLElement
      n = n.parentNode
    }
    return null
  }

  /** Codeblock wie in notizi: feste Schrittweite auf durchscheinendem Grau (CodeBlockTools). */
  const toggleCode = () => {
    const b = currentBlock()
    if (!b || b.tagName === 'LI') return
    if (/consolas|courier/i.test(b.style.fontFamily)) {
      b.style.removeProperty('font-family'); b.style.removeProperty('background-color')
      b.style.removeProperty('padding'); b.style.removeProperty('margin')
    } else {
      b.style.fontFamily = 'Consolas, "Courier New"'
      b.style.backgroundColor = '#80808022'
      b.style.padding = '6px 8px'
      b.style.margin = '4px 0px 8px'
      // Zeichenweise Schrift und Markierung heraus, sonst schlaegt sie die des Absatzes (wie in notizi)
      b.querySelectorAll<HTMLElement>('[style]').forEach(el => { el.style.removeProperty('font-family'); el.style.removeProperty('background-color') })
    }
    if (!b.getAttribute('style')) b.removeAttribute('style')
    changed()
  }

  /** Kaestchen wie in notizi: ☐ am Zeilenanfang; noch einmal entfernt es. */
  const toggleCheckbox = () => {
    const b = currentBlock()
    if (!b) return
    const first = b.firstChild
    if (first && first.nodeType === 3 && /^[\u2610\u2611] ?/.test(first.textContent ?? '')) {
      first.textContent = (first.textContent ?? '').replace(/^[\u2610\u2611] ?/, '')
    } else {
      b.insertBefore(document.createTextNode('\u2610 '), b.firstChild)
    }
    changed()
  }

  /** Klick auf ☐/☑ hakt ab bzw. oeffnet wieder. */
  const onEditorClick = () => {
    const sel = window.getSelection()
    const node = sel?.anchorNode
    if (!node || node.nodeType !== 3 || !sel?.isCollapsed) return
    const text = node.textContent ?? ''
    const o = sel.anchorOffset
    for (const i of [o, o - 1]) {
      const c = text[i]
      if (c === '\u2610' || c === '\u2611') {
        node.textContent = text.slice(0, i) + (c === '\u2610' ? '\u2611' : '\u2610') + text.slice(i + 1)
        const r = document.createRange()
        r.setStart(node, Math.min(i + 1, node.textContent.length))
        sel.removeAllRanges(); sel.addRange(r)
        changed()
        return
      }
    }
  }

  /** Bilder kommen an die Schreibmarke (wie in notizi), alles andere wird Anhang. */
  const addFiles = async (files: File[]) => {
    const images = files.filter(f => f.type.startsWith('image/'))
    const others = files.filter(f => !f.type.startsWith('image/'))
    if (images.length && !note?.readOnly) {
      // Schreibmarke merken - das Hochladen dauert
      const sel = window.getSelection()
      const range = sel && sel.rangeCount && root.current?.contains(sel.anchorNode) ? sel.getRangeAt(0).cloneRange() : null
      try {
        let html = ''
        for (const f of images) {
          const form = new FormData()
          form.append('file', f, f.name || 'bild.png')
          const { name } = await api.form<{ name: string }>('/api/notes/images', form)
          html += `<p><img src="/api/notes/blob/${name}" data-blob="${name}" style="max-width:100%" /></p>`
        }
        root.current?.focus()
        if (range) { const s2 = window.getSelection(); s2?.removeAllRanges(); s2?.addRange(range) }
        document.execCommand('insertHTML', false, html)
        changed()
      } catch (e) { fail(e) }
    }
    if (others.length) {
      const dt = new DataTransfer()
      others.forEach(f => dt.items.add(f))
      upload(dt.files)
    }
  }

  const upload = async (files: FileList | null) => {
    if (!files?.length) return
    const f = new FormData()
    Array.from(files).forEach(x => f.append('files', x, x.name))
    try { await flush(); await api.form(`/api/notes/${id}/attachments`, f); await load(); notify(t('Angehängt')) } catch (e) { fail(e) }
  }

  if (!note) return <Spinner style={{ margin: 'auto' }} />
  const bg = note.color || '#FFF9A8'
  const inTrash = !!note.deleted

  const tb = (label: string, icon: JSX.Element, cmd: string, value?: string) => (
    <Tooltip content={label} relationship="label">
      <Button appearance="subtle" size="small" icon={icon} onMouseDown={e => e.preventDefault()} onClick={() => exec(cmd, value)} disabled={note.readOnly || inTrash} />
    </Tooltip>
  )
  const picker = (label: string, icon: JSX.Element, values: string[], cmd: string) => (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content={label} relationship="label"><Button appearance="subtle" size="small" icon={icon} onMouseDown={e => e.preventDefault()} disabled={note.readOnly || inTrash} /></Tooltip>
      </MenuTrigger>
      <MenuPopover>
        <div className={s.swatches}>
          {values.map(c => <button key={c} className={s.swatch} style={{ background: c === 'transparent' ? '#fff' : c }} onMouseDown={e => e.preventDefault()} onClick={() => exec(cmd, c)} />)}
        </div>
      </MenuPopover>
    </Menu>
  )

  return (
    <div className={s.editor}>
      <div className={s.head}>
        <Input className={s.titleInput} appearance="underline" placeholder={note.title} value={title}
          onChange={(_, d) => setTitle(d.value)} onBlur={() => title !== (note.customTitle ?? '') && meta(title ? { customTitle: title } : { clearCustomTitle: true })} />
        <Tooltip content={saving === 'saving' ? t('Speichert …') : saving === 'dirty' ? t('Ungespeichert') : note.pending ? t('Gespeichert, noch nicht abgeglichen') : t('Gespeichert und abgeglichen')} relationship="label">
          <span style={{ display: 'inline-flex', color: tokens.colorNeutralForeground3 }}>
            {saving === 'saving' ? <Spinner size="extra-tiny" /> : saving === 'dirty' || note.pending ? <Circle16Filled color={tokens.colorPaletteYellowForeground1} /> : <CheckmarkCircle16Regular />}
          </span>
        </Tooltip>
        <Tooltip content={t('Wichtig')} relationship="label">
          <Button appearance="subtle" icon={note.important ? <Star20Filled color={tokens.colorPaletteYellowForeground1} /> : <Star20Regular />} onClick={() => meta({ important: !note.important })} />
        </Tooltip>
        <Menu>
          <MenuTrigger disableButtonEnhancement><Tooltip content={t('Farbe der Notiz')} relationship="label"><Button appearance="subtle" icon={<Color20Regular />} /></Tooltip></MenuTrigger>
          <MenuPopover>
            <div className={s.swatches}>{noteColors.map(c => <button key={c} className={s.swatch} style={{ background: c }} onClick={() => meta({ color: c })} />)}</div>
          </MenuPopover>
        </Menu>
        <Menu>
          <MenuTrigger disableButtonEnhancement><Tooltip content={t('In Ordner verschieben')} relationship="label"><Button appearance="subtle" icon={<FolderArrowRight20Regular />} /></Tooltip></MenuTrigger>
          <MenuPopover>
            <MenuList>
              <MenuItem onClick={() => meta({ folder: '' })}>{t('Ohne Ordner')}</MenuItem>
              {folders.map(f => <MenuItem key={f.syncId} onClick={() => meta({ folder: f.syncId })}>{f.name}</MenuItem>)}
            </MenuList>
          </MenuPopover>
        </Menu>
        <Tooltip content={t('Datei anhängen')} relationship="label"><Button appearance="subtle" icon={<Attach20Regular />} onClick={() => fileInput.current?.click()} /></Tooltip>
        {inTrash || note.archived
          ? <Tooltip content={t('Wiederherstellen')} relationship="label"><Button appearance="subtle" icon={<ArrowUndo20Regular />} onClick={() => action('restore')} /></Tooltip>
          : <Tooltip content={t('Archivieren')} relationship="label"><Button appearance="subtle" icon={<Archive20Regular />} onClick={() => action('archive')} /></Tooltip>}
        {inTrash
          ? <Button appearance="subtle" icon={<Delete20Regular />} onClick={destroy}>{t('Endgültig löschen')}</Button>
          : <Tooltip content={t('In den Papierkorb')} relationship="label"><Button appearance="subtle" icon={<Delete20Regular />} onClick={() => action('trash')} /></Tooltip>}
        <input ref={fileInput} type="file" multiple hidden onChange={e => { upload(e.target.files); e.target.value = '' }} />
      </div>
      <Toolbar size="small" className={s.bar}>
        {tb(t('Fett'), <TextBold20Regular />, 'bold')}
        {tb(t('Kursiv'), <TextItalic20Regular />, 'italic')}
        {tb(t('Unterstrichen'), <TextUnderline20Regular />, 'underline')}
        {tb(t('Durchgestrichen'), <TextStrikethrough20Regular />, 'strikeThrough')}
        {picker(t('Textfarbe'), <TextColor20Regular />, textColors, 'foreColor')}
        {picker(t('Hervorheben'), <Highlight20Regular />, marks, 'hiliteColor')}
        {tb(t('Aufzählung'), <TextBulletListLtr20Regular />, 'insertUnorderedList')}
        {tb(t('Nummerierung'), <TextNumberListLtr20Regular />, 'insertOrderedList')}
        <Tooltip content={t('Kästchen (Aufgabe) – anklicken hakt ab')} relationship="label">
          <Button appearance="subtle" size="small" icon={<CheckboxChecked20Regular />} onMouseDown={e => e.preventDefault()} onClick={toggleCheckbox} disabled={note.readOnly || inTrash} />
        </Tooltip>
        {tb(t('Linksbündig'), <TextAlignLeft20Regular />, 'justifyLeft')}
        {tb(t('Zentriert'), <TextAlignCenter20Regular />, 'justifyCenter')}
        {tb(t('Rechtsbündig'), <TextAlignRight20Regular />, 'justifyRight')}
        {tb(t('Einzug verkleinern'), <TextIndentDecreaseLtr20Regular />, 'outdent')}
        {tb(t('Einzug vergrößern'), <TextIndentIncreaseLtr20Regular />, 'indent')}
        <Tooltip content={t('Codeblock (Strg+Umschalt+C)')} relationship="label">
          <Button appearance="subtle" size="small" icon={<Code20Regular />} onMouseDown={e => e.preventDefault()} onClick={toggleCode} disabled={note.readOnly || inTrash} />
        </Tooltip>
        <span style={{ flex: 1 }} />
        {note.protected.some(Boolean) && (
          <Tooltip content={t('Tabellen und Bilder bearbeitet notizi am PC – hier bleiben sie unverändert.')} relationship="description">
            <Text size={200} style={{ color: tokens.colorNeutralForeground3, display: 'inline-flex', gap: 4, alignItems: 'center' }}><LockClosed16Regular /> {t('gesperrte Blöcke')}</Text>
          </Tooltip>
        )}
      </Toolbar>
      {rejected && (
        <MessageBar intent="error">
          <MessageBarBody>{rejected}</MessageBarBody>
          <MessageBarActions>
            <Button size="small" onClick={saveAsCopy}>{t('Als Kopie speichern')}</Button>
            <Button size="small" onClick={discard}>{t('Änderungen verwerfen')}</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {note.readOnly && <MessageBar intent="warning"><MessageBarBody>{t('Diese Notiz lässt sich hier nur lesen.')}</MessageBarBody></MessageBar>}
      {inTrash && (
        <MessageBar intent="info"><MessageBarBody>{t('Im Papierkorb.')}</MessageBarBody><MessageBarActions><Button size="small" onClick={() => action('restore')}>{t('Wiederherstellen')}</Button></MessageBarActions></MessageBar>
      )}
      <div className={s.stripe} style={{ backgroundColor: bg }} />
      <div className={`${s.paper} ${isDark ? 'dm-paper-dark' : 'dm-paper-light'}`}>
        <div ref={setRoot} className="dm-editor" contentEditable={!note.readOnly && !inTrash} suppressContentEditableWarning onInput={changed} spellCheck
          onClick={e => {
            // Links: der Ruecklink zur Mail mit einfachem Klick, andere mit Strg+Klick (wie in Word)
            const link = (e.target as HTMLElement).closest('a')
            if (link && (e.ctrlKey || e.metaKey || link.href.includes('#/mail/open/'))) {
              e.preventDefault()
              if (link.href.startsWith(location.origin)) location.href = link.href
              else window.open(link.href, '_blank', 'noopener')
              return
            }
            onEditorClick()
          }}
          onPaste={e => {
            const files = Array.from(e.clipboardData.files)
            if (files.length) { e.preventDefault(); addFiles(files); return }
            const html = e.clipboardData.getData('text/html')
            const text = e.clipboardData.getData('text/plain')
            e.preventDefault()
            if (html) document.execCommand('insertHTML', false, cleanPaste(html) || plainToHtml(text))
            else if (text) document.execCommand(/[\r\n]/.test(text) ? 'insertHTML' : 'insertText', false, /[\r\n]/.test(text) ? plainToHtml(text) : text)
            changed()
          }}
          onDrop={e => {
            // Nichts ungefiltert hineinziehen - Dateien werden Anhaenge, HTML wird bereinigt
            e.preventDefault()
            if (e.dataTransfer.files.length) {
              const r = (document as any).caretRangeFromPoint?.(e.clientX, e.clientY) as Range | undefined
              if (r) { const sel = window.getSelection(); sel?.removeAllRanges(); sel?.addRange(r) }
              addFiles(Array.from(e.dataTransfer.files)); return
            }
            const html = e.dataTransfer.getData('text/html'), text = e.dataTransfer.getData('text/plain')
            if (!html && !text) return
            const r = (document as any).caretRangeFromPoint?.(e.clientX, e.clientY) as Range | undefined
            if (r) { const sel = window.getSelection(); sel?.removeAllRanges(); sel?.addRange(r) }
            document.execCommand('insertHTML', false, html ? cleanPaste(html) || plainToHtml(text) : plainToHtml(text))
            changed()
          }}
          onKeyDown={e => {
            if (e.ctrlKey && e.key === 's') { e.preventDefault(); clearTimeout(timer.current); timer.current = undefined; save() }
            else if (e.ctrlKey && e.shiftKey && e.key.toLowerCase() === 'c') { e.preventDefault(); toggleCode() }
          }} />
      </div>
      {note.attachments.length > 0 && (
        <div className={s.atts}>
          {note.attachments.map(a => (
            <span key={a.name} className={s.att}>
              <a href={`/api/notes/blob/${a.blob}?` + q({ download: true, file: a.name })} style={{ color: 'inherit' }}>{a.name}</a>
              <Text size={200}>({fmtSize(a.size)})</Text>
              <Button size="small" appearance="transparent" icon={<Dismiss16Regular />} aria-label={t('Entfernen')}
                onClick={async () => { if (!confirm(t('„{name}“ entfernen?', { name: a.name }))) return; await api.del(`/api/notes/${id}/attachments/${encodeURIComponent(a.name)}`).catch(fail); load() }} />
            </span>
          ))}
        </div>
      )}
    </div>
  )
}
