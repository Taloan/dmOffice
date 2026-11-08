import { Text, makeStyles, tokens } from '@fluentui/react-components'
import { CalendarLtr20Regular, Document20Regular, Folder20Regular, Note20Regular, People20Regular, Person20Regular, TaskListSquareLtr20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { t } from '../i18n'
import { fmtDate } from '../shell/hooks'
import { ChatMessage, ChatRef, ChatRoom, refUrl } from './store'
import { ChatSpace, openSpace, spaceLabel } from './Spaces'
import { UserAvatar } from './UserAvatar'

// Infobereich rechts neben dem Verlauf (Version 2, grosse Bildschirme): Mitglieder, Gruppenraeume, zuletzt geteilte
// Fotos und Anhaenge der offenen Unterhaltung - ein- und ausblendbar ueber Ansicht -> Infobereich.

const useStyles = makeStyles({
  pane: {
    width: '300px', flexShrink: 0, overflowY: 'auto', borderLeft: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1, padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: '4px',
  },
  head: { fontWeight: 600, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, textTransform: 'uppercase', letterSpacing: '0.04em', margin: '12px 0 4px' },
  row: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, minWidth: 0,
  },
  click: { cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  name: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  photos: { display: 'grid', gridTemplateColumns: 'repeat(3, 1fr)', gap: '4px' },
  photo: { width: '100%', aspectRatio: '1', objectFit: 'cover', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', backgroundColor: tokens.colorNeutralBackground3 },
  empty: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, padding: '2px 6px' },
})

/** Breit genug für Verlauf und Infobereich nebeneinander? */
export function useWide(min = 1100) {
  const [wide, setWide] = useState(() => window.innerWidth >= min)
  useEffect(() => {
    const on = () => setWide(window.innerWidth >= min)
    window.addEventListener('resize', on)
    return () => window.removeEventListener('resize', on)
  }, [min])
  return wide
}

const isPhoto = (r: ChatRef) => r.type === 'file' && (r.mime ?? '').startsWith('image/') && r.mime !== 'image/svg+xml' && r.state === 'ok'

const kindIcon = (r: ChatRef) => r.type === 'note' ? <Note20Regular /> : r.type === 'folder' ? <Folder20Regular /> : r.type === 'event' ? <CalendarLtr20Regular />
  : r.type === 'task' ? <TaskListSquareLtr20Regular /> : r.type === 'contact' ? <Person20Regular /> : <Document20Regular />

export function ChatInfo({ room, me, messages, spaces, onPreview, onPhotos, width }: {
  room: ChatRoom; me: string; messages: ChatMessage[]; spaces: ChatSpace[]; width?: number
  /** Anhang öffnen wie im Verlauf (Notiz, Ordner, Termin …) */
  onPreview: (message: number, index: number) => void
  /** Fotos im Bildbetrachter: alle Fotos der Unterhaltung, beginnend beim angeklickten */
  onPhotos?: (list: { m: ChatMessage; r: ChatRef; i: number }[], at: { m: ChatMessage; i: number }) => void
}) {
  const s = useStyles()
  // Neueste zuerst: Fotos als Kacheln, alles andere als Liste
  const refs = messages.filter(m => !m.deleted).flatMap(m => m.refs.map((r, i) => ({ m, r, i }))).reverse()
  const photos = refs.filter(x => isPhoto(x.r)).slice(0, 12)
  const others = refs.filter(x => !isPhoto(x.r) && x.r.type !== 'location' && x.r.state === 'ok').slice(0, 15)
  const open = (x: { m: ChatMessage; r: ChatRef; i: number }) =>
    isPhoto(x.r) && onPhotos ? onPhotos(refs.filter(y => isPhoto(y.r)), x)
    : x.r.type === 'file' ? window.open(refUrl(x.m.id, x.i, 'file', { inline: true }), '_blank', 'noopener') : onPreview(x.m.id, x.i)

  return (
    <aside className={s.pane} style={width ? { width } : undefined} aria-label={t('Infobereich')}>
      <div className={s.head}>{room.kind === 'group' ? t('Mitglieder ({n})', { n: room.members.length }) : t('Unterhaltung mit')}</div>
      {room.members.filter(m => room.kind === 'group' || m.id !== me).map(m => (
        <div key={m.id} className={s.row}>
          <UserAvatar id={m.id} name={m.name} login={m.login} avatar={m.avatar} accent={m.accent} size={28} />
          <span className={s.name}>{m.id === me ? t('Du') : m.name}</span>
          {m.admin && room.kind === 'group' && <span className={s.sub}>{t('verwaltet')}</span>}
        </div>
      ))}

      {spaces.length > 0 && (
        <>
          <div className={s.head}>{t('Gruppenräume')}</div>
          {spaces.map(x => (
            <div key={x.kind} className={`${s.row} ${s.click}`} onClick={() => openSpace(x)}>
              <People20Regular />
              <span className={s.name}>{spaceLabel(x.kind)} „{x.name}“</span>
            </div>
          ))}
        </>
      )}

      <div className={s.head}>{t('Fotos')}</div>
      {photos.length === 0 ? <Text className={s.empty}>{t('Noch keine Fotos.')}</Text> : (
        <div className={s.photos}>
          {photos.map(x => (
            <img key={`${x.m.id}-${x.i}`} className={s.photo} src={refUrl(x.m.id, x.i, 'thumb', { size: 160 })} alt={x.r.name} title={x.r.name} loading="lazy" onClick={() => open(x)} />
          ))}
        </div>
      )}

      <div className={s.head}>{t('Dateien, Notizen und mehr')}</div>
      {others.length === 0 ? <Text className={s.empty}>{t('Noch nichts geteilt.')}</Text> : others.map(x => (
        <div key={`${x.m.id}-${x.i}`} className={`${s.row} ${s.click}`} onClick={() => open(x)} title={x.r.name}>
          {kindIcon(x.r)}
          <span className={s.name}>{x.r.name}</span>
          <span className={s.sub}>{fmtDate(x.m.created)}</span>
        </div>
      ))}
    </aside>
  )
}
