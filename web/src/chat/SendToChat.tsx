import {
  Avatar, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Spinner, Text, Textarea,
  makeStyles, tokens,
} from '@fluentui/react-components'
import { People20Regular, Search20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { ChatMode, ChatRoom, chatSettings, chatStatusChanged, Person } from './store'
import { ModeSwitch } from './Dialogs'

/** Verweis, den ein Bereich in den Chat schicken will (eigene Datei oder Notiz). */
export type ChatRefInput =
  | { type: 'file' | 'folder'; fileId: number }
  | { type: 'album'; fileId: number }
  | { type: 'note'; noteId: number }
  | { type: 'event' | 'task' | 'contact'; collection: number; item: string }

const EVENT = 'dmoffice:sendtochat'

/** "Im Chat …" aus einem Bereich (Dateiablage, Notizen): oeffnet den Dialog in der Shell. */
export function sendToChat(refs: ChatRefInput[], text?: string) {
  // text: vorausgefüllte Nachricht (etwa ein Text) - dann auch ohne Verweise
  if (refs.length || text) window.dispatchEvent(new CustomEvent<{ refs: ChatRefInput[]; text?: string }>(EVENT, { detail: { refs, text } }))
}

const useStyles = makeStyles({
  surface: { maxWidth: '560px', width: '100%' },
  list: { display: 'flex', flexDirection: 'column', maxHeight: 'calc(100vh - 420px)', minHeight: '140px', overflowY: 'auto', margin: '8px 0', gap: '2px' },
  row: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  name: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
})

/** Ziel: eine bestehende Unterhaltung oder eine Person, mit der es noch keine gibt (dann wird sie angelegt). */
type Target = { key: string; title: string; group: boolean; room?: number; person?: string }

/** In der Shell eingehaengt: wartet auf sendToChat() und zeigt den Dialog. */
export function SendToChatHost() {
  const [refs, setRefs] = useState<{ refs: ChatRefInput[]; text?: string } | null>(null)
  useEffect(() => {
    const on = (e: Event) => setRefs((e as CustomEvent<{ refs: ChatRefInput[]; text?: string }>).detail)
    window.addEventListener(EVENT, on)
    return () => window.removeEventListener(EVENT, on)
  }, [])
  return refs ? <SendToChatDialog refs={refs.refs} initialText={refs.text} onClose={() => setRefs(null)} /> : null
}

function SendToChatDialog({ refs, initialText, onClose }: { refs: ChatRefInput[]; initialText?: string; onClose: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [targets, setTargets] = useState<Target[] | null>(null)
  const [chosen, setChosen] = useState<string[]>([])
  const [filter, setFilter] = useState('')
  const [mode, setMode] = useState<ChatMode>('show')
  const [text, setText] = useState(initialText ?? '')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    // Vorgabe-Art: Dateien aus der Ablage bzw. Notizen (Einstellungen -> Chat)
    chatSettings().then(r => setMode(refs.every(x => x.type === 'file' || x.type === 'folder') ? r.settings.filesMode : r.settings.itemsMode)).catch(() => {})
    Promise.all([api.get<ChatRoom[]>('/api/chat/rooms'), api.get<Person[]>('/api/chat/people')]).then(([rooms, people]) => {
      const withDirect = new Set(rooms.filter(r => r.kind === 'direct').flatMap(r => r.members.map(m => m.id)))
      setTargets([
        ...rooms.map(r => ({ key: 'r' + r.id, title: r.title, group: r.kind === 'group', room: r.id })),
        ...people.filter(p => !withDirect.has(p.id)).map(p => ({ key: 'p' + p.id, title: p.displayName, group: false, person: p.id })),
      ])
    }).catch(e => { fail(e); setTargets([]) })
  }, [refs, fail])

  const send = async () => {
    setBusy(true)
    try {
      for (const key of chosen) {
        const tg = targets!.find(x => x.key === key)!
        const room = tg.room ?? (await api.post<ChatRoom>('/api/chat/rooms', { members: [tg.person] })).id
        await api.post(`/api/chat/rooms/${room}/messages`, { text: text.trim() || null, refs: refs.map(r => ({ ...r, mode })) })
      }
      chatStatusChanged()
      notify(chosen.length > 1 ? t('In {n} Unterhaltungen verschickt', { n: chosen.length }) : t('Im Chat verschickt'))
      onClose()
    } catch (e) { fail(e) }
    finally { setBusy(false) }
  }

  const shown = (targets ?? []).filter(x => !filter || x.title.toLowerCase().includes(filter.toLowerCase()))
  const kinds: Record<ChatRefInput['type'], string> = { file: t('Datei'), folder: t('Ordner'), note: t('Notiz'), event: t('Termin'), task: t('Aufgabe'), contact: t('Kontakt'), album: t('Album') }
  const what = refs.length > 1 ? t('{n} Elemente', { n: refs.length }) : kinds[refs[0].type]
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{refs.every(r => r.type === 'album') ? t('Im Chat zeigen: {what}', { what }) : t('Im Chat verschicken: {what}', { what })}</DialogTitle>
          <DialogContent>
            <Input contentBefore={<Search20Regular />} placeholder={t('Unterhaltung oder Person suchen')} value={filter} onChange={(_, d) => setFilter(d.value)} style={{ width: '100%' }} />
            <div className={s.list}>
              {targets == null && <Spinner size="small" />}
              {targets?.length === 0 && <Text className={s.sub}>{t('Keine weiteren Personen auf diesem Server.')}</Text>}
              {shown.map(x => (
                <label key={x.key} className={s.row}>
                  <Checkbox checked={chosen.includes(x.key)} onChange={() => setChosen(c => (c.includes(x.key) ? c.filter(k => k !== x.key) : [...c, x.key]))} />
                  <Avatar name={x.title} size={28} icon={x.group ? <People20Regular /> : undefined} color={x.group ? 'brand' : 'colorful'} />
                  <span className={s.name}>{x.title}</span>
                  {x.person && <span className={s.sub}>{t('neue Unterhaltung')}</span>}
                </label>
              ))}
            </div>
            {/* Alben gehen nur als Zeigen */}
            {!refs.every(r => r.type === 'album') && <ModeSwitch value={mode} onChange={setMode} />}
            <Textarea style={{ width: '100%', marginTop: 8 }} resize="vertical" placeholder={t('Nachricht dazu (optional)')} value={text} onChange={(_, d) => setText(d.value)} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || chosen.length === 0} onClick={send}>{t('Verschicken')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
