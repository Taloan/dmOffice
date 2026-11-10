import { Avatar } from '@fluentui/react-components'
import { People20Regular } from '@fluentui/react-icons'
import { useEffect, useReducer } from 'react'
import { e2eFetch } from '../e2e'
import { t } from '../i18n'
import { SIGNATURE } from '../theme'
import { ChatRoom, PUKKI } from './store'

/**
 * Profilbild (Version 2): eigenes Bild, sonst der erste Buchstabe des Benutzernamens auf der Akzentfarbe des Benutzers -
 * weiß, auf sehr hellen Farben dunkel. Pukki erscheint als die Figur, die man sich ausgesucht hat.
 */
export function UserAvatar({ id, name, login, avatar, accent, size = 32 }: {
  id: string; name: string; login?: string | null; avatar?: string | null; accent?: string | null; size?: number
}) {
  if (id === PUKKI) return <PukkiAvatar size={size} />
  if (avatar) return <Avatar name={name} size={size as any} image={{ src: `/api/chat/people/${encodeURIComponent(id)}/avatar?v=${encodeURIComponent(avatar)}` }} />
  const bg = accent && /^#[0-9a-f]{6}$/i.test(accent) ? accent : SIGNATURE
  const letter = ((login || name || '?').trim()[0] ?? '?').toUpperCase()
  return (
    <span role="img" aria-label={name} title={name} style={{
      width: size, height: size, minWidth: size, borderRadius: '50%', backgroundColor: bg, color: light(bg) ? '#1F1F1F' : '#FFFFFF',
      display: 'inline-flex', alignItems: 'center', justifyContent: 'center', fontWeight: 600, fontSize: Math.round(size * 0.45), lineHeight: 1, userSelect: 'none',
    }}>{letter}</span>
  )
}

function PukkiAvatar({ size }: { size: number }) {
  return <Avatar name="Pukki" size={size as any} image={{ src: usePukkiImage() }} />
}

/** Figuren für Pukki; die erste ist die Vorgabe. */
export const PUKKI_FIGURES = ['panda', 'walross', 'baer', 'zauberer', 'walross-zauberer'] as const
export type PukkiFigure = typeof PUKKI_FIGURES[number]
export const pukkiFigureName = (f: PukkiFigure) => ({ panda: t('Panda'), walross: t('Walross'), baer: t('Bär'), zauberer: t('Zauberer'), 'walross-zauberer': t('Zauber-Walross') })[f]
export const pukkiFigureImage = (f: PukkiFigure) => `/pukki-${f}.png`

// Pukkis Figur: einmal mit dem Chat-Status geladen, für alle Stellen gleich
let pukkiFigure: PukkiFigure | undefined
const pukkiListeners = new Set<() => void>()

/** Andere Figur gewählt - alle Stellen zeichnen neu. */
export function setPukkiFigure(figure: PukkiFigure) {
  pukkiFigure = figure
  pukkiListeners.forEach(f => f())
}

/** Die Figur, als die Pukki erscheint (bis zum Laden: der Panda). */
export function usePukkiFigure(): PukkiFigure {
  const [, redraw] = useReducer((x: number) => x + 1, 0)
  useEffect(() => {
    pukkiListeners.add(redraw)
    if (pukkiFigure === undefined) {
      pukkiFigure = 'panda'
      e2eFetch('/api/chat/status').then(r => r.json())
        .then(s => { if (PUKKI_FIGURES.includes(s.pukkiFigure)) setPukkiFigure(s.pukkiFigure) }).catch(() => {})
    }
    return () => { pukkiListeners.delete(redraw) }
  }, [])
  return pukkiFigure ?? 'panda'
}

/** Adresse von Pukkis Bild in der gewählten Figur. */
export const usePukkiImage = () => pukkiFigureImage(usePukkiFigure())

/** Pukki in einer Pose (Bögen in docs/pukki/: hallo, winken, daumen, frage, idee, herz, jubel, bitte, lesen, laptop, lupe, zeigen, checkliste, arbeitet, staunen, kaffee). */
export const pukkiPose = (f: PukkiFigure, pose: string) => `/pukki/${f}-${pose}.webp`

/** Sehr helle Farbe? (relative Helligkeit nach WCAG) - dann dunkle Schrift. */
function light(hex: string) {
  const [r, g, b] = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255).map(c => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4))
  return 0.2126 * r + 0.7152 * g + 0.0722 * b > 0.45
}

/** Bild einer Unterhaltung: zu zweit das des anderen, Gruppen ein Gruppensymbol. */
export function RoomAvatar({ room, me, size = 36 }: { room: ChatRoom; me: string; size?: number }) {
  if (room.kind === 'group') return <Avatar name={room.title} size={size as any} icon={<People20Regular />} color="brand" />
  const other = room.members.find(m => m.id !== me)
  return other ? <UserAvatar id={other.id} name={other.name} login={other.login} avatar={other.avatar} accent={other.accent} size={size} />
    : <Avatar name={room.title} size={size as any} />
}
