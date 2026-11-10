import {
  Avatar, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Radio, RadioGroup, Spinner, Text,
  makeStyles, tokens,
} from '@fluentui/react-components'
import { ArrowUp20Regular, Folder20Regular, Note20Regular, Search20Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { hasThumb, Item, KindIcon, ownSource, parentOf, Thumb } from '../files/common'
import { ChatMessage, ChatMode, ChatRoom, chatSettings, Person, refBlobUrl, refUrl, safeNoteHtml } from './store'
import { askConfirm } from '../shell/context'

const useStyles = makeStyles({
  surface: { maxWidth: '520px', width: '100%' },
  // Liste nutzt die Hoehe des Fensters (Titel, Pfad, Art und Knoepfe abgezogen)
  list: { display: 'flex', flexDirection: 'column', maxHeight: 'calc(100vh - 340px)', minHeight: '160px', overflowY: 'auto', marginTop: '8px', gap: '2px' },
  row: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  rowName: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  path: { display: 'flex', alignItems: 'center', gap: '4px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  note: { maxHeight: '60vh', overflowY: 'auto', lineHeight: 1.5, '& img': { maxWidth: '100%', borderRadius: tokens.borderRadiusMedium } },
  empty: { padding: '16px', textAlign: 'center', color: tokens.colorNeutralForeground3 },
  thumb: {
    width: '36px', height: '36px', flexShrink: 0, position: 'relative', display: 'flex', alignItems: 'center', justifyContent: 'center',
    borderRadius: tokens.borderRadiusMedium, overflow: 'hidden',
  },
  thumbImg: { width: '100%', height: '100%' },
})

/** Mini-Vorschau fuer Bilder (auch PDF, Video), sonst das Symbol der Dateiart. */
function MiniThumb({ item }: { item: Item }) {
  const s = useStyles()
  return <span className={s.thumb}>{hasThumb(item) ? <Thumb item={item} source={ownSource} size={96} className={s.thumbImg} /> : <KindIcon item={item} />}</span>
}

export function usePeople(): Person[] | null {
  const [people, setPeople] = useState<Person[] | null>(null)
  useEffect(() => { api.get<Person[]>('/api/chat/people').then(setPeople).catch(() => setPeople([])) }, [])
  return people
}

function PeopleChooser({ people, chosen, toggle, exclude = [] }: { people: Person[] | null; chosen: string[]; toggle: (id: string) => void; exclude?: string[] }) {
  const s = useStyles()
  const [filter, setFilter] = useState('')
  const shown = (people ?? []).filter(p => !exclude.includes(p.id))
    .filter(p => !filter || (p.displayName + ' ' + p.login).toLowerCase().includes(filter.toLowerCase()))
  return (
    <>
      <Input contentBefore={<Search20Regular />} placeholder={t('Personen suchen')} value={filter} onChange={(_, d) => setFilter(d.value)} style={{ width: '100%' }} />
      <div className={s.list}>
        {people == null && <Spinner size="small" />}
        {people != null && shown.length === 0 && <div className={s.empty}>{t('Keine weiteren Personen auf diesem Server.')}</div>}
        {shown.map(p => (
          <label key={p.id} className={s.row}>
            <Checkbox checked={chosen.includes(p.id)} onChange={() => toggle(p.id)} />
            <Avatar name={p.displayName} size={28} />
            <span className={s.rowName}>{p.displayName} <span className={s.sub}>{p.login}</span></span>
          </label>
        ))}
      </div>
    </>
  )
}

/** Neue Unterhaltung: eine Person = zu zweit, mehrere = Gruppe mit Namen. */
export function NewChatDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (room: ChatRoom) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const people = usePeople()
  const [chosen, setChosen] = useState<string[]>([])
  const [title, setTitle] = useState('')
  const [busy, setBusy] = useState(false)
  const group = chosen.length > 1
  const toggle = (id: string) => setChosen(c => (c.includes(id) ? c.filter(x => x !== id) : [...c, id]))
  const create = async () => {
    setBusy(true)
    try { onCreated(await api.post<ChatRoom>('/api/chat/rooms', { members: chosen, title: group ? title : null })) }
    catch (e) { fail(e) }
    finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{t('Neue Unterhaltung')}</DialogTitle>
          <DialogContent>
            <PeopleChooser people={people} chosen={chosen} toggle={toggle} />
            {group && (
              <Input style={{ width: '100%', marginTop: 12 }} placeholder={t('Name der Gruppe')} value={title} onChange={(_, d) => setTitle(d.value)} />
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || chosen.length === 0 || (group && !title.trim())} onClick={create}>
              {group ? t('Gruppe erstellen') : t('Schreiben')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Mitglieder ansehen; wer verwaltet, fuegt hinzu, entfernt und gibt das Verwalten weiter. */
export function MembersDialog({ room, me, onClose, onChanged }: { room: ChatRoom; me: string; onClose: () => void; onChanged: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const people = usePeople()
  const [adding, setAdding] = useState<string[] | null>(null)
  const run = async (f: () => Promise<unknown>) => { try { await f(); onChanged() } catch (e) { fail(e) } }
  if (adding) return (
    <Dialog open onOpenChange={(_, d) => !d.open && setAdding(null)}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{t('Personen hinzufügen')}</DialogTitle>
          <DialogContent>
            <PeopleChooser people={people} chosen={adding} exclude={room.members.map(m => m.id)}
              toggle={id => setAdding(a => (a!.includes(id) ? a!.filter(x => x !== id) : [...a!, id]))} />
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setAdding(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={adding.length === 0}
              onClick={() => run(() => api.post(`/api/chat/rooms/${room.id}/members`, { users: adding })).then(() => setAdding(null))}>
              {t('Hinzufügen')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{t('Mitglieder')} ({room.members.length})</DialogTitle>
          <DialogContent>
            <div className={s.list}>
              {room.members.map(m => (
                <div key={m.id} className={s.row} style={{ cursor: 'default' }}>
                  <Avatar name={m.name} size={28} />
                  <span className={s.rowName}>{m.name}{m.id === me && ` (${t('du')})`} {m.admin && <span className={s.sub}>· {t('verwaltet')}</span>}</span>
                  {room.admin && m.id !== me && (
                    <>
                      <Button size="small" appearance="subtle" onClick={() => run(() => api.put(`/api/chat/rooms/${room.id}/admins/${m.id}`, { admin: !m.admin }))}>
                        {m.admin ? t('Verwalten abnehmen') : t('Verwalten lassen')}
                      </Button>
                      <Button size="small" appearance="subtle"
                        onClick={async () => { if (await askConfirm('', t('{name} aus der Gruppe entfernen?', { name: m.name }), t('Entfernen'))) run(() => api.del(`/api/chat/rooms/${room.id}/members/${m.id}`)) }}>
                        {t('Entfernen')}
                      </Button>
                    </>
                  )}
                </div>
              ))}
            </div>
          </DialogContent>
          <DialogActions>
            {room.admin && <Button onClick={() => setAdding([])}>{t('Personen hinzufügen')}</Button>}
            <Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Datei aus der eigenen Ablage waehlen - gesendet wird nur der Verweis. */
export function FilePickerDialog({ onClose, onPick }: { onClose: () => void; onPick: (items: Item[], mode: ChatMode) => void }) {
  const s = useStyles()
  const [mode, setMode] = useDefaultMode('files')
  const [path, setPath] = useState('')
  const [items, setItems] = useState<Item[] | null>(null)
  const [chosen, setChosen] = useState<Item[]>([])
  useEffect(() => {
    setItems(null)
    // "Mit mir geteilt" und andere fremde Ordner gehen nicht: gesendet werden nur eigene Dateien
    api.get<{ items: Item[] }>('/api/files/?' + q({ path })).then(d => setItems(d.items.filter(i => !i.shared && !i.owner))).catch(() => setItems([]))
  }, [path])
  const toggle = (i: Item) => setChosen(c => (c.some(x => x.path === i.path) ? c.filter(x => x.path !== i.path) : [...c, i]))
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{t('Dateiablage')}</DialogTitle>
          <DialogContent>
            <div className={s.path}>
              <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} disabled={!path} onClick={() => setPath(parentOf(path))} aria-label={t('Nach oben')} />
              <Folder20Regular /> <span>{path || t('Dateien')}</span>
            </div>
            <div className={s.list}>
              {items == null && <Spinner size="small" />}
              {items?.length === 0 && <div className={s.empty}>{t('Dieser Ordner ist leer.')}</div>}
              {items?.map(i => i.isDir ? (
                <div key={i.path} className={s.row} onClick={() => setPath(i.path)}>
                  <KindIcon item={i} /><span className={s.rowName}>{i.name}</span>
                </div>
              ) : (
                <label key={i.path} className={s.row}>
                  <Checkbox checked={chosen.some(x => x.path === i.path)} onChange={() => toggle(i)} />
                  <MiniThumb item={i} /><span className={s.rowName}>{i.name}</span>
                  <span className={s.sub}>{fmtSize(i.size)}</span>
                </label>
              ))}
            </div>
            <div style={{ marginTop: 8 }}><ModeSwitch value={mode} onChange={setMode} /></div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={chosen.length === 0} onClick={() => onPick(chosen, mode)}>
              {chosen.length > 1 ? t('{n} Dateien senden', { n: chosen.length }) : t('Senden')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

interface NoteFolder { syncId: string; parent: string | null; name: string }

/** Notizordner als eingerueckte Liste (Eltern vor Kindern). */
function folderTree(list: NoteFolder[]): { f: NoteFolder; depth: number }[] {
  const out: { f: NoteFolder; depth: number }[] = []
  const walk = (parent: string | null, depth: number) => {
    for (const f of list.filter(x => x.parent === parent)) { out.push({ f, depth }); walk(f.syncId, depth + 1) }
  }
  walk(null, 0)
  return out
}

/**
 * Bei mir speichern - mit Ziel: Datei in einen Ordner der eigenen Ablage (Vorgabe "Chat"), Notiz in einen Notizordner
 * (Vorgabe: Ordner „Chat“). Zurueck an onSave geht das gewaehlte Ziel, gespeichert wird dort.
 */
/** Platzhalter für den Notizordner „Chat“ - der Server nimmt ihn, wenn kein Ordner mitkommt. */
const CHAT_FOLDER = '#chat'

export function SaveRefDialog({ kind, name, onClose, onSave }: { kind: 'file' | 'note'; name: string; onClose: () => void; onSave: (folder: string | null) => void }) {
  const s = useStyles()
  const [path, setPath] = useState('Chat')
  // Vorgabe-Ziele aus Einstellungen -> Chat
  useEffect(() => { chatSettings().then(r => setPath(r.settings.saveFolder)).catch(() => {}) }, [])
  const [dirs, setDirs] = useState<Item[] | null>(null)
  const [folders, setFolders] = useState<NoteFolder[] | null>(null)
  // Notizen: Vorgabe ist der Ordner „Chat“ (CHAT_FOLDER - legt der Server bei Bedarf an), "" = ohne Ordner
  const [noteFolder, setNoteFolder] = useState<string>(CHAT_FOLDER)
  useEffect(() => {
    if (kind !== 'file') return
    setDirs(null)
    // "Chat" gibt es erst nach dem ersten Hochladen - dann eben leer, angelegt wird er beim Speichern
    api.get<{ items: Item[] }>('/api/files/?' + q({ path })).then(d => setDirs(d.items.filter(i => i.isDir && !i.shared && !i.owner))).catch(() => setDirs([]))
  }, [kind, path])
  useEffect(() => {
    if (kind === 'note') api.get<NoteFolder[]>('/api/notes/folders').then(setFolders).catch(() => setFolders([]))
  }, [kind])
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ bei mir speichern', { name })}</DialogTitle>
          <DialogContent>
            {kind === 'file' ? (
              <>
                <div className={s.path}>
                  <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} disabled={!path} onClick={() => setPath(parentOf(path))} aria-label={t('Nach oben')} />
                  <Folder20Regular /> <span>{path || t('Dateien')}</span>
                </div>
                <div className={s.list}>
                  {dirs == null && <Spinner size="small" />}
                  {dirs?.length === 0 && <div className={s.empty}>{t('Keine Unterordner.')}</div>}
                  {dirs?.map(i => (
                    <div key={i.path} className={s.row} onClick={() => setPath(i.path)}>
                      <KindIcon item={i} /><span className={s.rowName}>{i.name}</span>
                    </div>
                  ))}
                </div>
              </>
            ) : (
              <div className={s.list}>
                {folders == null && <Spinner size="small" />}
                {[{ f: { syncId: CHAT_FOLDER, parent: null, name: t('Ordner „Chat“ (wird bei Bedarf angelegt)') }, depth: 0 }, { f: { syncId: '', parent: null, name: t('Ohne Ordner') }, depth: 0 },
                  ...folderTree((folders ?? []).filter(x => !(x.parent == null && x.name === 'Chat')))].map(({ f, depth }) => (
                  <label key={f.syncId || '-'} className={s.row} style={{ paddingLeft: 8 + depth * 18 }}>
                    <input type="radio" name="notefolder" checked={noteFolder === f.syncId} onChange={() => setNoteFolder(f.syncId)} />
                    <Folder20Regular /><span className={s.rowName}>{f.name}</span>
                  </label>
                ))}
              </div>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={() => onSave(kind === 'file' ? path : noteFolder === CHAT_FOLDER ? null : noteFolder)}>
              {kind === 'file' ? t('In „{folder}“ speichern', { folder: path || t('Dateien') }) : t('Speichern')}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

interface NoteItem { id: number; title: string; snippet: string; owner?: string | null; color?: string }

/** Eigene Notiz waehlen - gesendet wird nur der Verweis. */
export function NotePickerDialog({ onClose, onPick }: { onClose: () => void; onPick: (note: NoteItem, mode: ChatMode) => void }) {
  const s = useStyles()
  const [mode, setMode] = useDefaultMode('items')
  const [query, setQuery] = useState('')
  const [notes, setNotes] = useState<NoteItem[] | null>(null)
  useEffect(() => {
    const timer = setTimeout(() => {
      api.get<{ items: NoteItem[] }>('/api/notes?' + q(query.trim() ? { q: query.trim() } : { view: 'all' }))
        // Nur eigene: Notizen aus mit mir geteilten Ordnern gehoeren jemand anderem
        .then(d => setNotes(d.items.filter(n => n.id > 0 && !n.owner))).catch(() => setNotes([]))
    }, 250)
    return () => clearTimeout(timer)
  }, [query])
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{t('Notiz senden')}</DialogTitle>
          <DialogContent>
            <Input contentBefore={<Search20Regular />} placeholder={t('Notizen durchsuchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ width: '100%' }} />
            <div style={{ marginTop: 8 }}><ModeSwitch value={mode} onChange={setMode} /></div>
            <div className={s.list}>
              {notes == null && <Spinner size="small" />}
              {notes?.length === 0 && <div className={s.empty}>{t('Keine Notizen gefunden.')}</div>}
              {notes?.map(n => (
                <div key={n.id} className={s.row} onClick={() => onPick(n, mode)}>
                  <Note20Regular />
                  <span className={s.rowName}>{n.title}<br /><span className={s.sub}>{n.snippet}</span></span>
                </div>
              ))}
            </div>
          </DialogContent>
          <DialogActions><Button onClick={onClose}>{t('Abbrechen')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

interface NoteView { title: string; html: string; ownerName: string; modified: string; attachments: { name: string; size: number; blob: string }[] }

/** Notiz aus einer Nachricht lesen (aktueller Stand beim Besitzer) und bei Bedarf als eigene speichern. */
export function NotePreviewDialog({ message, index, onClose, onSave }: { message: number; index: number; onClose: () => void; onSave: () => void }) {
  const s = useStyles()
  const [note, setNote] = useState<NoteView | null | 'gone'>(null)
  useEffect(() => { api.get<NoteView>(refUrl(message, index, 'note')).then(setNote).catch(() => setNote('gone')) }, [message, index])
  const html = useMemo(() => (note && note !== 'gone' ? safeNoteHtml(note.html, name => refBlobUrl(message, index, name)) : ''), [note, message, index])
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface} style={{ maxWidth: 720 }}>
        <DialogBody>
          <DialogTitle>{note && note !== 'gone' ? note.title : t('Notiz')}</DialogTitle>
          <DialogContent>
            {note == null && <Spinner size="small" />}
            {note === 'gone' && <Text>{t('Diese Notiz ist nicht mehr verfügbar.')}</Text>}
            {note && note !== 'gone' && (
              <>
                <Text className={s.sub} block>{t('Von {name}', { name: note.ownerName })}</Text>
                <div className={s.note} dangerouslySetInnerHTML={{ __html: html }} />
                {note.attachments.map(a => (
                  <div key={a.blob}><a href={refBlobUrl(message, index, a.blob) + '?' + q({ download: true, file: a.name })}>{a.name}</a> <span className={s.sub}>{fmtSize(a.size)}</span></div>
                ))}
              </>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Schließen')}</Button>
            {note && note !== 'gone' && <Button appearance="primary" onClick={onSave}>{t('Bei mir speichern')}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Zeigen oder Senden waehlen; vorbelegt mit der Vorgabe aus Einstellungen -> Chat. */
export function ModeSwitch({ value, onChange }: { value: ChatMode; onChange: (m: ChatMode) => void }) {
  return (
    <RadioGroup layout="horizontal" value={value} onChange={(_, d) => onChange(d.value as ChatMode)}>
      <Radio value="show" label={t('Zeigen – nur ansehen')} />
      <Radio value="send" label={t('Senden – darf speichern')} />
    </RadioGroup>
  )
}

/** Vorgabe-Art je Herkunft laden (files: Dateiablage, items: Notizen usw.). */
export function useDefaultMode(kind: 'files' | 'items'): [ChatMode, (m: ChatMode) => void] {
  const [mode, setMode] = useState<ChatMode>('show')
  useEffect(() => { chatSettings().then(r => setMode(kind === 'files' ? r.settings.filesMode : r.settings.itemsMode)).catch(() => {}) }, [kind])
  return [mode, setMode]
}

/**
 * Weiterleiten: eine Nachricht in andere Unterhaltungen. Gesendetes geht als Zeigen oder Senden; Gezeigtes nur als
 * Zeigen und nur, wenn der Absender es erlaubt (prueft der Server).
 */
export function ForwardDialog({ message, rooms, onClose, onDone }: { message: ChatMessage; rooms: ChatRoom[]; onClose: () => void; onDone: (list: ChatMessage[]) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const onlyShow = message.refs.some(r => r.mode === 'show')
  const [chosen, setChosen] = useState<number[]>([])
  // Personen ohne Unterhaltung zu zweit: beim Weiterleiten eine neue anfangen
  const [people, setPeople] = useState<{ id: string; displayName: string }[]>([])
  const [chosenPeople, setChosenPeople] = useState<string[]>([])
  useEffect(() => {
    Promise.all([api.get<ChatRoom[]>('/api/chat/rooms'), api.get<{ id: string; displayName: string }[]>('/api/chat/people')]).then(([all, list]) => {
      const withDirect = new Set(all.filter(r => r.kind === 'direct').flatMap(r => r.members.map(m => m.id)))
      setPeople(list.filter(p => !withDirect.has(p.id)))
    }).catch(() => {})
  }, [])
  const [mode, setMode] = useState<ChatMode>(onlyShow ? 'show' : 'send')
  const [withText, setWithText] = useState(true)
  const [filter, setFilter] = useState('')
  const [busy, setBusy] = useState(false)
  const go = async () => {
    setBusy(true)
    try {
      const fresh: number[] = []
      for (const p of chosenPeople) fresh.push((await api.post<ChatRoom>('/api/chat/rooms', { members: [p] })).id)
      onDone(await api.post<ChatMessage[]>(`/api/chat/messages/${message.id}/forward`, { rooms: [...chosen, ...fresh], mode: message.refs.length ? mode : undefined, withText }))
    }
    catch (e) { fail(e) }
    finally { setBusy(false) }
  }
  const shown = rooms.filter(r => !filter || r.title.toLowerCase().includes(filter.toLowerCase()))
  const shownPeople = people.filter(p => !filter || p.displayName.toLowerCase().includes(filter.toLowerCase()))
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{t('Weiterleiten')}</DialogTitle>
          <DialogContent>
            <Input contentBefore={<Search20Regular />} placeholder={t('Unterhaltungen durchsuchen')} value={filter} onChange={(_, d) => setFilter(d.value)} style={{ width: '100%' }} />
            <div className={s.list}>
              {shown.length === 0 && shownPeople.length === 0 && <div className={s.empty}>{t('Keine weiteren Unterhaltungen.')}</div>}
              {shown.map(r => (
                <label key={r.id} className={s.row}>
                  <Checkbox checked={chosen.includes(r.id)} onChange={() => setChosen(c => (c.includes(r.id) ? c.filter(x => x !== r.id) : [...c, r.id]))} />
                  <Avatar name={r.title} size={28} />
                  <span className={s.rowName}>{r.title}</span>
                </label>
              ))}
              {shownPeople.map(p => (
                <label key={'p' + p.id} className={s.row}>
                  <Checkbox checked={chosenPeople.includes(p.id)} onChange={() => setChosenPeople(c => (c.includes(p.id) ? c.filter(x => x !== p.id) : [...c, p.id]))} />
                  <Avatar name={p.displayName} size={28} />
                  <span className={s.rowName}>{p.displayName}</span>
                  <Text className={s.sub}>{t('neue Unterhaltung')}</Text>
                </label>
              ))}
            </div>
            {message.refs.length > 0 && (onlyShow
              ? <Text className={s.sub} block style={{ marginTop: 8 }}>{t('Enthält Gezeigtes – lässt sich nur weiterzeigen, und nur wenn der Absender es erlaubt.')}</Text>
              : <div style={{ marginTop: 8 }}><ModeSwitch value={mode} onChange={setMode} /></div>)}
            {message.text && message.refs.length > 0 && <Checkbox label={t('Text mitschicken')} checked={withText} onChange={(_, d) => setWithText(!!d.checked)} />}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || chosen.length + chosenPeople.length === 0} onClick={go}>{t('Weiterleiten')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

interface Coll { id: number; name: string; color?: string | null; components?: string[]; owner?: string | null; subscription?: unknown }

/** Bei mir speichern fuer Termin/Aufgabe/Kontakt: in welchen eigenen Kalender, welche Liste, welches Adressbuch. */
export function CollectionPickDialog({ type, name, onClose, onSave }: { type: 'event' | 'task' | 'contact'; name: string; onClose: () => void; onSave: (id: string) => void }) {
  const s = useStyles()
  const [list, setList] = useState<Coll[] | null>(null)
  const [chosen, setChosen] = useState<number | null>(null)
  useEffect(() => {
    const want = type === 'event' ? 'VEVENT' : type === 'task' ? 'VTODO' : null
    api.get<Coll[]>('/api/pim/collections/' + (type === 'contact' ? 'addressbooks' : 'calendars'))
      .then(all => {
        // Nur eigene, keine Abos und keine mit mir geteilten
        const own = all.filter(c => !c.owner && !c.subscription && c.id > 0 && (!want || (c.components ?? []).includes(want)))
        setList(own)
        setChosen(own[0]?.id ?? null)
      }).catch(() => setList([]))
  }, [type])
  const label = type === 'event' ? t('Kalender') : type === 'task' ? t('Liste') : t('Adressbuch')
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ bei mir speichern', { name })}</DialogTitle>
          <DialogContent>
            <Text className={s.sub} block>{label}</Text>
            <div className={s.list}>
              {list == null && <Spinner size="small" />}
              {list?.length === 0 && <div className={s.empty}>{t('Keine passende Sammlung.')}</div>}
              {list?.map(c => (
                <label key={c.id} className={s.row}>
                  <input type="radio" name="coll" checked={chosen === c.id} onChange={() => setChosen(c.id)} />
                  <span style={{ width: 12, height: 12, borderRadius: 6, background: c.color ?? tokens.colorBrandBackground, flexShrink: 0 }} />
                  <span className={s.rowName}>{c.name}</span>
                </label>
              ))}
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={chosen == null} onClick={() => onSave(String(chosen))}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Termin, Aufgabe oder Kontakt aus einer Nachricht ansehen (aktueller Stand beim Besitzer). */
export function ItemDialog({ message, index, canSave, onClose, onSave }: { message: number; index: number; canSave: boolean; onClose: () => void; onSave: () => void }) {
  const s = useStyles()
  const [item, setItem] = useState<any | null | 'gone'>(null)
  useEffect(() => { api.get(refUrl(message, index, 'item')).then(setItem).catch(() => setItem('gone')) }, [message, index])
  const when = (iso?: string | null, allDay?: boolean) => {
    if (!iso) return ''
    const d = new Date(iso)
    return allDay
      ? d.toLocaleDateString(locale, { weekday: 'short', day: 'numeric', month: 'long', year: 'numeric' })
      : d.toLocaleString(locale, { weekday: 'short', day: 'numeric', month: 'long', year: 'numeric', hour: '2-digit', minute: '2-digit' })
  }
  const ok = item && item !== 'gone'
  const c = ok ? item.contact : null
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{ok ? (c ? c.displayName : item.title) : t('Ansehen')}</DialogTitle>
          <DialogContent>
            {item == null && <Spinner size="small" />}
            {item === 'gone' && <Text>{t('Nicht mehr verfügbar.')}</Text>}
            {ok && (
              <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
                <Text className={s.sub}>{t('Von {name}', { name: item.ownerName })}</Text>
                {item.type === 'event' && <>
                  <Text>{when(item.start, item.allDay)}{item.end && !item.allDay ? ' – ' + new Date(item.end).toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' }) : ''}</Text>
                  {item.recurring && <Text className={s.sub}>{t('Wiederholt sich')}</Text>}
                  {item.location && <Text>{item.location}</Text>}
                </>}
                {item.type === 'task' && <>
                  {item.due && <Text>{t('Fällig: {date}', { date: when(item.due, item.dueAllDay) })}</Text>}
                  <Text className={s.sub}>{item.done ? t('Erledigt') : t('Offen')}</Text>
                </>}
                {(item.type === 'event' || item.type === 'task') && item.description && <Text style={{ whiteSpace: 'pre-wrap' }}>{item.description}</Text>}
                {c && <>
                  {c.organization && <Text>{c.organization}</Text>}
                  {(c.emails ?? []).map((e: any) => <Text key={e.value}>{e.value}</Text>)}
                  {(c.phones ?? []).map((p: any) => <Text key={p.value}>{p.value}</Text>)}
                </>}
              </div>
            )}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Schließen')}</Button>
            {canSave && ok && <Button appearance="primary" onClick={onSave}>{t('Bei mir speichern')}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Gezeigtes Album: Fotos als Raster, antippen öffnet das Bild im Browser (nur ansehen, nicht herunterladen). */
export function AlbumRefDialog({ message, index, name, onClose }: { message: number; index: number; name: string; onClose: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [items, setItems] = useState<{ key: string; name: string; mime: string | null; etag: string; ownerName?: string | null }[] | null>(null)
  useEffect(() => {
    api.get<{ items: { key: string; name: string; mime: string | null; etag: string; ownerName?: string | null }[] }>(refUrl(message, index, 'album'))
      .then(r => setItems(r.items)).catch(e => { fail(e); setItems([]) })
  }, [message, index, fail])
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface} style={{ maxWidth: 820 }}>
        <DialogBody>
          <DialogTitle>{name}</DialogTitle>
          <DialogContent>
            {items == null && <Spinner size="small" />}
            {items?.length === 0 && <div className={s.empty}>{t('Dieses Album ist leer.')}</div>}
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(120px, 1fr))', gap: 6, maxHeight: '60vh', overflowY: 'auto' }}>
              {items?.map(f => (
                <a key={f.key} href={refUrl(message, index, 'album/file', { key: f.key })} target="_blank" rel="noopener noreferrer" title={[f.name, f.ownerName].filter(Boolean).join(' · ')}
                  style={{ aspectRatio: '1', overflow: 'hidden', borderRadius: 6, background: 'var(--colorNeutralBackground3)', display: 'block' }}>
                  <img src={refUrl(message, index, 'album/thumb', { key: f.key, size: 256, v: f.etag })} alt={f.name} loading="lazy" style={{ width: '100%', height: '100%', objectFit: 'cover' }} />
                </a>
              ))}
            </div>
          </DialogContent>
          <DialogActions><Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

interface FolderEntry { path: string; name: string; isDir: boolean; size: number; mime: string; etag?: string }

/** Ordner aus einer Nachricht: blaettern, Dateien ansehen, bei Senden einzelne Dateien bei mir speichern. */
export function FolderRefDialog({ message, index, name, canSave, onClose }: { message: number; index: number; name: string; canSave: boolean; onClose: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [path, setPath] = useState('')
  const [items, setItems] = useState<FolderEntry[] | null>(null)
  useEffect(() => {
    setItems(null)
    api.get<FolderEntry[]>(refUrl(message, index, 'folder', path ? { path } : {})).then(setItems).catch(e => { fail(e); setItems([]) })
  }, [message, index, path, fail])
  const save = async (f: FolderEntry) => {
    try {
      const r = await api.post<{ path: string }>(`/api/chat/messages/${message}/refs/${index}/folder/save`, { path: f.path })
      notify(t('In der Dateiablage gespeichert'), r.path)
    } catch (e) { fail(e) }
  }
  const thumbable = (f: FolderEntry) => /^(image|video)\//.test(f.mime) || f.mime === 'application/pdf'
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface} style={{ maxWidth: 720 }}>
        <DialogBody>
          <DialogTitle>{name}</DialogTitle>
          <DialogContent>
            <div className={s.path}>
              <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} disabled={!path} onClick={() => setPath(parentOf(path))} aria-label={t('Nach oben')} />
              <Folder20Regular /> <span>{path ? name + ' / ' + path : name}</span>
            </div>
            <div className={s.list}>
              {items == null && <Spinner size="small" />}
              {items?.length === 0 && <div className={s.empty}>{t('Dieser Ordner ist leer.')}</div>}
              {items?.map(f => f.isDir ? (
                <div key={f.path} className={s.row} onClick={() => setPath(f.path)}>
                  <Folder20Regular /><span className={s.rowName}>{f.name}</span>
                </div>
              ) : (
                <div key={f.path} className={s.row}>
                  <span className={s.thumb}>
                    {thumbable(f)
                      ? <img src={refUrl(message, index, 'folder/thumb', { path: f.path, size: 96, v: f.etag })} alt="" className={s.thumbImg} style={{ objectFit: 'cover' }} loading="lazy" />
                      : <KindIcon item={{ name: f.name, isDir: false, mime: f.mime }} />}
                  </span>
                  <a className={s.rowName} href={refUrl(message, index, 'folder/file', { path: f.path, inline: true })} target="_blank" rel="noopener noreferrer" style={{ color: 'inherit' }}>{f.name}</a>
                  <span className={s.sub}>{fmtSize(f.size)}</span>
                  {canSave && <Button size="small" appearance="subtle" onClick={() => save(f)}>{t('Bei mir speichern')}</Button>}
                </div>
              ))}
            </div>
          </DialogContent>
          <DialogActions><Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
