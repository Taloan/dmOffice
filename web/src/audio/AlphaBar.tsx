import { makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useMemo, useRef, useState } from 'react'
import { norm } from './library'

/** Breite der Leiste - so viel Platz lassen die Listen rechts frei */
export const BAR_WIDTH = 28

const useStyles = makeStyles({
  bar: { position: 'absolute', right: '2px', top: '6px', bottom: '8px', width: `${BAR_WIDTH - 2}px`, zIndex: 3, cursor: 'ns-resize', touchAction: 'none', userSelect: 'none' },
  letter: {
    position: 'absolute', left: 0, right: '6px', textAlign: 'center', transform: 'translateY(-50%)', fontSize: '11px', fontWeight: 600,
    color: tokens.colorNeutralForeground2, pointerEvents: 'none',
  },
  // Griff: wo man gerade ist - schmal am Rand, damit er keinen Buchstaben verdeckt (die ganze Leiste zieht)
  handle: {
    position: 'absolute', right: '0px', width: '4px', height: '24px', marginTop: '-12px', borderRadius: '2px', pointerEvents: 'none',
    backgroundColor: tokens.colorBrandBackground,
  },
  bubble: {
    position: 'absolute', right: `${BAR_WIDTH + 6}px`, transform: 'translateY(-50%)', minWidth: '24px', textAlign: 'center', padding: '4px 10px', borderRadius: '12px',
    fontSize: '15px', fontWeight: 600, backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand, whiteSpace: 'nowrap',
    pointerEvents: 'none', boxShadow: tokens.shadow8,
  },
})

/** Ein Anfangsbuchstabe und sein erster Eintrag in der Anzeigereihenfolge */
export interface Letter { key: string; first: number }

/** Anfangsbuchstabe: A-Z (Umlaute und Akzente beim Grundbuchstaben), sonst „#“ */
const initial = (s: string) => { const c = norm(s.trim()).charAt(0).toUpperCase(); return c >= 'A' && c <= 'Z' ? c : '#' }

/** Buchstaben der Leiste aus den Namen in Anzeigereihenfolge; null, wenn sich die Leiste nicht lohnt (kurze Liste, kaum Buchstaben). */
export function useLetters(names: string[] | null): Letter[] | null {
  return useMemo(() => {
    if (!names || names.length < 40) return null
    const out: Letter[] = []
    names.forEach((n, i) => { const k = initial(n); if (!out.length || out[out.length - 1].key !== k) out.push({ key: k, first: i }) })
    return out.length >= 3 ? out : null
  }, [names])
}

/**
 * A-Z-Leiste am rechten Rand einer Liste der Audiothek, gleichartig zur Zeitleiste der Galerie: Buchstaben in ihrem Anteil
 * an der Liste, Zeigen nennt den Buchstaben, Klicken oder Ziehen springt dorthin. Der Elternteil braucht position: relative.
 */
export function AlphaBar({ letters, total, onJump, scroller }: {
  letters: Letter[]; total: number; onJump: (index: number) => void
  /** Scrollbereich der Liste: der Griff zeigt, wo man darin gerade ist */
  scroller: React.RefObject<HTMLDivElement | null>
}) {
  const s = useStyles()
  const ref = useRef<HTMLDivElement>(null)
  const [hover, setHover] = useState<{ y: number; key: string } | null>(null)
  const down = useRef(false)
  const [pos, setPos] = useState(0)
  useEffect(() => {
    const el = scroller.current
    if (!el) return
    const on = () => setPos(el.scrollHeight > el.clientHeight ? el.scrollTop / (el.scrollHeight - el.clientHeight) : 0)
    on()
    el.addEventListener('scroll', on, { passive: true })
    return () => el.removeEventListener('scroll', on)
  }, [scroller, total])
  // Zu dicht beieinander wird nur jeder zweite … beschriftet
  const shown = useMemo(() => {
    let last = -1
    return letters.filter(l => { const at = l.first / total; if (last >= 0 && at - last < 0.03) return false; last = at; return true })
  }, [letters, total])
  const letterAt = (frac: number) => { const idx = Math.floor(frac * total); let m = letters[0]; for (const x of letters) { if (x.first <= idx) m = x; else break } return m }
  const move = (clientY: number, jump: boolean) => {
    const r = ref.current!.getBoundingClientRect()
    const l = letterAt(Math.min(1, Math.max(0, (clientY - r.top) / r.height)))
    setHover({ y: clientY - r.top, key: l.key })
    if (jump) onJump(l.first)
  }
  return (
    <div ref={ref} className={s.bar}
      onPointerMove={e => move(e.clientY, down.current)}
      onPointerLeave={() => { if (!down.current) setHover(null) }}
      onPointerDown={e => { down.current = true; e.currentTarget.setPointerCapture(e.pointerId); move(e.clientY, true) }}
      onPointerUp={() => { down.current = false; setHover(null) }}>
      {shown.map(l => <span key={l.key + l.first} className={s.letter} style={{ top: `${(l.first / total) * 100}%` }}>{l.key}</span>)}
      <span className={s.handle} style={{ top: `${pos * 100}%` }} />
      {hover && <span className={s.bubble} style={{ top: hover.y }}>{hover.key}</span>}
    </div>
  )
}
