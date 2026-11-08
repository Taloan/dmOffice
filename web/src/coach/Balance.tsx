import { tokens } from '@fluentui/react-components'
import { t } from '../i18n'
import { fmt } from './api'

/**
 * Apothekerwaage: links gegessen, rechts der Bedarf (Gesamtumsatz des Tages plus Bewegung). Der Balken neigt sich zur
 * schwereren Seite - höchstens 14°, schon bei kleinen Unterschieden sichtbar. Die Schalen hängen immer waagerecht.
 * Die Farbe richtet sich nach dem Tagesziel (beim Abnehmen mit Defizit): nah dran grün, darüber orange/rot; deutlich
 * darunter erst bei abgeschlossenen Tagen gelb/orange - im Lauf des Tages ist man natürlich noch darunter.
 */
export function Balance({ eaten, burned, goal, past, hide }: { eaten: number; burned: number | null; goal?: number | null; past?: boolean; hide?: boolean }) {
  const W = 220, H = 150, cx = 110, pivotY = 34, arm = 78, chain = 52
  const diff = burned ? (eaten - burned) / Math.max(burned, 1) : 0
  // Neigung: links schwerer → linke Seite sinkt (positiver Winkel im SVG dreht im Uhrzeigersinn → rechts hoch, links runter: also negativ)
  const angle = Math.max(-14, Math.min(14, diff * 40))
  const rad = (-angle * Math.PI) / 180
  const lx = cx - arm * Math.cos(rad), ly = pivotY - arm * Math.sin(rad)
  const rx = cx + arm * Math.cos(rad), ry = pivotY + arm * Math.sin(rad)
  // Farbe nach dem Verhältnis: deutlich mehr verbraucht grün, etwas mehr gelb, ausgeglichen grau, etwas mehr gegessen orange, deutlich mehr rot
  const target = goal ?? burned
  const off = target ? (eaten - target) / Math.max(target, 1) : null
  const ink = off == null ? tokens.colorNeutralForeground2
    : off > 0.25 ? tokens.colorPaletteRedForeground1 : off > 0.08 ? tokens.colorPaletteDarkOrangeForeground1
      : off >= -0.08 ? tokens.colorPaletteGreenForeground1
        : !past ? tokens.colorNeutralForeground2 : off >= -0.25 ? tokens.colorPaletteYellowForeground1 : tokens.colorPaletteDarkOrangeForeground1
  const soft = ink
  const eatC = tokens.colorPaletteBerryForeground2, burnC = tokens.colorPaletteMarigoldForeground2
  const pan = (x: number, y: number, color: string, value: string, label: string) => (
    <g>
      <line x1={x} y1={y} x2={x - 22} y2={y + chain} stroke={soft} strokeWidth={1} />
      <line x1={x} y1={y} x2={x + 22} y2={y + chain} stroke={soft} strokeWidth={1} />
      <path d={`M${x - 30},${y + chain} Q${x},${y + chain + 16} ${x + 30},${y + chain} Z`} fill={color} opacity={0.85} />
      <text x={x} y={y + chain + 30} textAnchor="middle" fontSize={15} fontWeight={600} fill={tokens.colorNeutralForeground1}>{value}</text>
      <text x={x} y={y + chain + 43} textAnchor="middle" fontSize={10} fill={tokens.colorNeutralForeground3}>{label}</text>
    </g>
  )
  return (
    <svg viewBox={`0 0 ${W} ${H + 14}`} style={{ width: '100%', maxWidth: 220, height: 'auto', display: 'block', margin: '0 auto' }} role="img"
      aria-label={t('Gegessen {a} kcal, Bedarf {b} kcal', { a: fmt(eaten), b: burned != null ? fmt(burned) : '–' })}>
      {/* Ständer */}
      <line x1={cx} y1={pivotY} x2={cx} y2={H - 6} stroke={ink} strokeWidth={3} strokeLinecap="round" />
      <path d={`M${cx - 26},${H + 2} L${cx + 26},${H + 2} L${cx + 14},${H - 8} L${cx - 14},${H - 8} Z`} fill={ink} />
      <circle cx={cx} cy={pivotY - 8} r={4} fill={ink} />
      {/* Balken */}
      <line x1={lx} y1={ly} x2={rx} y2={ry} stroke={ink} strokeWidth={3} strokeLinecap="round" />
      <circle cx={cx} cy={pivotY} r={4} fill={tokens.colorNeutralBackground1} stroke={ink} strokeWidth={2} />
      {pan(lx, ly, eatC, hide ? '–' : fmt(eaten), t('Gegessen'))}
      {pan(rx, ry, burnC, burned != null ? fmt(burned) : '–', t('Bedarf'))}
    </svg>
  )
}
