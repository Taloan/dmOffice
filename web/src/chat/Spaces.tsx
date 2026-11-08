import { Button, makeStyles, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, tokens } from '@fluentui/react-components'
import { CalendarLtr20Regular, Folder20Regular, Note20Regular, TaskListLtr20Regular, AddSquare20Regular, Dismiss20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { t } from '../i18n'
import { ChatRoom } from './store'
import { askConfirm } from '../shell/context'

/** Gruppenraum (Stufe 4), wie der Server ihn fuer mich zeigt. target: Sprungziel (Sammlung, Pfad oder Notizordner). */
export interface ChatSpace { kind: SpaceKind; name: string; owner: string; ownerName: string; mine: boolean; target: string | null }
export type SpaceKind = 'calendar' | 'list' | 'folder' | 'notes'

export const spaceKinds: SpaceKind[] = ['calendar', 'list', 'folder', 'notes']

export const spaceLabel = (k: SpaceKind) =>
  k === 'calendar' ? t('Kalender') : k === 'list' ? t('Liste') : k === 'folder' ? t('Ordner') : t('Notizordner')

const spaceIcon = (k: SpaceKind) =>
  k === 'calendar' ? <CalendarLtr20Regular /> : k === 'list' ? <TaskListLtr20Regular /> : k === 'folder' ? <Folder20Regular /> : <Note20Regular />

/** Dorthin, wo der Raum in dmOffice liegt. */
export function openSpace(s: ChatSpace) {
  if (s.kind === 'calendar') location.hash = '#/calendar'
  else if (s.kind === 'list') location.hash = s.target ? `#/tasks/${s.target}` : '#/tasks'
  else if (s.kind === 'folder') location.hash = '#/files/' + (s.target ?? '').split('/').map(encodeURIComponent).join('/')
  else location.hash = '#/notes'
}

/** Raeume einer Gruppe; neu geladen, wenn sich in der Unterhaltung etwas tut (seq). */
export function useSpaces(room: ChatRoom) {
  const [spaces, setSpaces] = useState<ChatSpace[]>([])
  const load = () => {
    if (room.kind !== 'group') { setSpaces([]); return }
    api.get<ChatSpace[]>(`/api/chat/rooms/${room.id}/spaces`).then(setSpaces).catch(() => setSpaces([]))
  }
  useEffect(load, [room.id, room.kind, room.seq]) // eslint-disable-line react-hooks/exhaustive-deps
  return { spaces, setSpaces, reload: load }
}

const useStyles = makeStyles({
  bar: {
    display: 'flex', gap: '4px', flexWrap: 'wrap', padding: '4px 12px',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground2,
  },
})

/** Leiste unter dem Kopf der Gruppe: „Kalender · Liste · Ordner · Notizordner“ zum Hinspringen. */
export function SpacesBar({ spaces }: { spaces: ChatSpace[] }) {
  const s = useStyles()
  if (!spaces.length) return null
  return (
    <div className={s.bar}>
      {spaces.map(x => (
        <Button key={x.kind} size="small" appearance="subtle" icon={spaceIcon(x.kind)} onClick={() => openSpace(x)}
          title={x.mine ? t('{kind} „{name}“ – bei dir', { kind: spaceLabel(x.kind), name: x.name }) : t('{kind} „{name}“ – von {owner}', { kind: spaceLabel(x.kind), name: x.name, owner: x.ownerName })}>
          {spaceLabel(x.kind)}
        </Button>
      ))}
    </div>
  )
}

/** Menüeinträge: Gruppenraum einrichten (wer die Gruppe verwaltet) und eigene Räume herausnehmen. */
export function SpacesMenu({ room, spaces, onChanged, fail }: { room: ChatRoom; spaces: ChatSpace[]; onChanged: (s: ChatSpace[]) => void; fail: (e: unknown) => void }) {
  if (room.kind !== 'group') return null
  const free = spaceKinds.filter(k => !spaces.some(x => x.kind === k))
  const mine = spaces.filter(x => x.mine)
  const create = async (kind: SpaceKind) => {
    if (!await askConfirm('', t('{kind} „{name}“ für die Gruppe einrichten? Er liegt bei dir (dein Speicherplatz) und ist mit allen Mitgliedern geteilt – wer dazukommt, bekommt Zugriff, wer geht, verliert ihn.', { kind: spaceLabel(kind), name: room.title }))) return
    try { onChanged(await api.post<ChatSpace[]>(`/api/chat/rooms/${room.id}/spaces`, { kind })) } catch (e) { fail(e) }
  }
  const remove = async (x: ChatSpace) => {
    if (!await askConfirm('', t('{kind} „{name}“ aus der Gruppe nehmen? Die anderen sehen ihn dann nicht mehr; bei dir bleibt er.', { kind: spaceLabel(x.kind), name: x.name }))) return
    try { await api.del(`/api/chat/rooms/${room.id}/spaces/${x.kind}`); onChanged(spaces.filter(s => s !== x)) } catch (e) { fail(e) }
  }
  return (
    <>
      {room.admin && free.length > 0 && (
        <Menu>
          <MenuTrigger disableButtonEnhancement><MenuItem icon={<AddSquare20Regular />}>{t('Gruppenraum einrichten')}</MenuItem></MenuTrigger>
          <MenuPopover><MenuList>{free.map(k => <MenuItem key={k} icon={spaceIcon(k)} onClick={() => create(k)}>{spaceLabel(k)}</MenuItem>)}</MenuList></MenuPopover>
        </Menu>
      )}
      {mine.length > 0 && (
        <Menu>
          <MenuTrigger disableButtonEnhancement><MenuItem icon={<Dismiss20Regular />}>{t('Raum aus der Gruppe nehmen')}</MenuItem></MenuTrigger>
          <MenuPopover><MenuList>{mine.map(x => <MenuItem key={x.kind} icon={spaceIcon(x.kind)} onClick={() => remove(x)}>{spaceLabel(x.kind)}</MenuItem>)}</MenuList></MenuPopover>
        </Menu>
      )}
    </>
  )
}
