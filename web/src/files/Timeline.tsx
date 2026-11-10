import { makeStyles, tokens } from '@fluentui/react-components'
import { useMemo, useRef, useState, useEffect } from 'react'
import { locale, t } from '../i18n'

const useStyles = makeStyles({
  bar: { position: 'absolute', right: '2px', bottom: '8px', width: '46px', zIndex: 3, cursor: 'ns-resize', touchAction: 'none', userSelect: 'none' },
  year: {
    position: 'absolute', right: '10px', transform: 'translateY(-50%)', fontSize: '11px', fontWeight: 600, color: tokens.colorNeutralForeground2,
    pointerEvents: 'none', whiteSpace: 'nowrap',
  },
  dot: { position: 'absolute', right: '3px', width: '3px', height: '3px', borderRadius: '50%', backgroundColor: tokens.colorNeutralForeground3, pointerEvents: 'none' },
  // Griff: wo man gerade ist - gut sichtbar und groß genug zum Anfassen (die ganze Leiste zieht)
  handle: {
    position: 'absolute', right: '0px', width: '22px', height: '14px', marginTop: '-7px', borderRadius: '7px', pointerEvents: 'none',
    backgroundColor: tokens.colorBrandBackground, boxShadow: `0 0 0 2px ${tokens.colorNeutralBackground1}, ${tokens.shadow4}`,
  },
  bubble: {
    position: 'absolute', right: '50px', transform: 'translateY(-50%)', padding: '4px 10px', borderRadius: '12px', fontSize: '13px', fontWeight: 600,
    backgroundColor: tokens.colorBrandBackground, color: tokens.colorNeutralForegroundOnBrand, whiteSpace: 'nowrap', pointerEvents: 'none', boxShadow: tokens.shadow8,
  },
})

/**
 * Zeitleiste am rechten Rand der Galerie wie bei Immich: Jahre in ihrem Anteil an allen Fotos, Punkte je Monat. Zeigen
 * nennt den Monat, Klicken oder Ziehen springt dorthin. months: Monate in Anzeigereihenfolge mit ihrem ersten Index.
 * Nach Quelle gruppiert ist der Schlüssel „s<Quelle>|yyyy-MM“ und group der Name der Quelle: Jahre dann je Quelle, die
 * Blase nennt Quelle und Monat.
 */
export function Timeline({ months, total, top, onJump, scroller }: {
  months: { key: string; first: number; group?: string }[]; total: number; top: number; onJump: (index: number) => void
  /** Scrollbereich der Galerie: der Griff zeigt, wo man darin gerade ist */
  scroller?: React.RefObject<HTMLDivElement | null>
}) {
  const s = useStyles()
  const ref = useRef<HTMLDivElement>(null)
  const [hover, setHover] = useState<{ y: number; m: { key: string; group?: string } } | null>(null)
  const down = useRef(false)
  const [pos, setPos] = useState(0)
  useEffect(() => {
    const el = scroller?.current
    if (!el) return
    const on = () => setPos(el.scrollHeight > el.clientHeight ? el.scrollTop / (el.scrollHeight - el.clientHeight) : 0)
    on()
    el.addEventListener('scroll', on, { passive: true })
    return () => el.removeEventListener('scroll', on)
  }, [scroller, total])
  // Jahre: erster Monat jedes Jahres; zu dicht beieinander wird nur jedes zweite … beschriftet
  const years = useMemo(() => {
    const out: { id: string; year: string; at: number }[] = []
    for (const m of months) {
      const y = dateKey(m.key).slice(0, 4), id = (m.group ?? '') + '|' + y
      if (!out.length || out[out.length - 1].id !== id) out.push({ id, year: y, at: m.first / total })
    }
    const minGap = 0.035
    let last = -1
    return out.filter(y => { if (last >= 0 && y.at - last < minGap) return false; last = y.at; return true })
  }, [months, total])
  if (months.length < 3 || total < 40) return null
  const monthAt = (frac: number) => { const idx = Math.floor(frac * total); let m = months[0]; for (const x of months) { if (x.first <= idx) m = x; else break } return m }
  const label = (m: { key: string; group?: string }) => {
    const k = dateKey(m.key)
    const month = new Date(+k.slice(0, 4), +k.slice(5) - 1, 1).toLocaleDateString(locale, { month: 'long', year: 'numeric' })
    return m.group ? t(m.group) + ' · ' + month : month
  }
  const at = (clientY: number) => { const r = ref.current!.getBoundingClientRect(); return { y: clientY - r.top, frac: Math.min(1, Math.max(0, (clientY - r.top) / r.height)) } }
  const move = (clientY: number, jump: boolean) => {
    const p = at(clientY)
    const m = monthAt(p.frac)
    setHover({ y: p.y, m })
    if (jump) onJump(m.first)
  }
  return (
    <div ref={ref} className={s.bar} style={{ top }}
      onPointerMove={e => move(e.clientY, down.current)}
      onPointerLeave={() => { if (!down.current) setHover(null) }}
      onPointerDown={e => { down.current = true; e.currentTarget.setPointerCapture(e.pointerId); move(e.clientY, true) }}
      onPointerUp={() => { down.current = false; setHover(null) }}>
      {months.map(m => <span key={m.key} className={s.dot} style={{ top: `${(m.first / total) * 100}%` }} />)}
      {years.map(y => <span key={y.id} className={s.year} style={{ top: `${y.at * 100}%` }}>{y.year}</span>)}
      <span className={s.handle} style={{ top: `${pos * 100}%` }} />
      {hover && <span className={s.bubble} style={{ top: hover.y }}>{label(hover.m)}</span>}
    </div>
  )
}

/** Monat „yyyy-MM“ aus dem Schlüssel (nach Quelle: „s<Quelle>|yyyy-MM“). */
const dateKey = (key: string) => key.slice(key.lastIndexOf('|') + 1)
