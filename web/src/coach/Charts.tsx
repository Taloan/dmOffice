import { tokens } from '@fluentui/react-components'
import { fmt, STEPS_DONE, STEPS_EXTRA } from './api'

/** Kleine SVG-Diagramme für den Coach: Ring, Balken mit Ziel-Linie, Linie mit Trend. Farben aus dem Theme. */

/** Ring „übrig“: Anteil gefüllt, Text in der Mitte. */
export function Ring({ value, max, label, sub, size = 132, color = tokens.colorBrandForeground1 }: {
  value: number; max: number; label: string; sub: string; size?: number; color?: string
}) {
  const r = size / 2 - 10, c = 2 * Math.PI * r
  const part = max > 0 ? Math.min(1, value / max) : 0
  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img" aria-label={label + ' ' + sub}>
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={tokens.colorNeutralStroke2} strokeWidth={10} />
      <circle cx={size / 2} cy={size / 2} r={r} fill="none" stroke={color} strokeWidth={10} strokeLinecap="round"
        strokeDasharray={c} strokeDashoffset={c * (1 - part)} transform={`rotate(-90 ${size / 2} ${size / 2})`} />
      <text x={size / 2} y={size / 2 + 2} textAnchor="middle" fontSize={22} fontWeight={600} fill={tokens.colorNeutralForeground1}>{label}</text>
      <text x={size / 2} y={size / 2 + 20} textAnchor="middle" fontSize={11} fill={tokens.colorNeutralForeground3}>{sub}</text>
    </svg>
  )
}

/**
 * Drei Ringe, von außen nach innen zu lesen: gegessen (pink) minus Bewegung (grün, vom Ende des Gegessenen zurück) ergibt
 * netto (orange, dick, direkt um die Zahl). Voller Kreis = Grundbedarf ohne Bewegung; netto darüber wird rot. In der Mitte, was
 * heute noch geht (mit „+“ in Rot, wenn drüber), bei verborgenen Zahlen der Anteil am Tagesziel.
 */
export function TripleRing({ eaten, bonus, budget, hide, size = 132 }: { eaten: number; bonus: number; budget: number; hide?: boolean; size?: number }) {
  const base = Math.max(1, budget - bonus)
  const move = Math.min(bonus, eaten)
  const fe = eaten / base, fm = move / base, fn = (eaten - move) / base
  const left = budget - eaten
  const k = size / 132
  const thin = 7 * k, thick = 13 * k, gap = 3 * k, c = size / 2
  const r0 = c - thin / 2, r1 = r0 - thin - gap, r2 = r1 - thin / 2 - gap - thick / 2
  const arc = (r: number, w: number, from: number, to: number, color: string) => {
    const a = Math.max(0, Math.min(1, from)), b = Math.max(0, Math.min(1, to)), len = 2 * Math.PI * r
    return b > a ? <circle cx={c} cy={c} r={r} fill="none" stroke={color} strokeWidth={w} strokeLinecap="round"
      strokeDasharray={`${(b - a) * len} ${len}`} strokeDashoffset={-a * len} transform={`rotate(-90 ${c} ${c})`} /> : null
  }
  const track = (r: number, w: number) => <circle cx={c} cy={c} r={r} fill="none" stroke={tokens.colorNeutralStroke2} strokeWidth={w} />
  const label = hide ? fmt(Math.round(eaten / budget * 100)) + ' %' : (left < 0 ? '+' : '') + fmt(Math.abs(Math.round(left)))
  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} role="img"
      aria-label={`${fmt(Math.round(eaten))} kcal − ${fmt(Math.round(move))} kcal = ${fmt(Math.round(eaten - move))} kcal; ${label}`}>
      {track(r0, thin)}{track(r1, thin)}{track(r2, thick)}
      {arc(r0, thin, 0, fe, RING_EATEN)}
      {arc(r1, thin, fe - fm, fe, RING_MOVE)}
      {arc(r2, thick, 0, fn, fn > 1 ? RING_OVER : RING_NET)}
      <text x={c} y={c + 4 * k} textAnchor="middle" fontSize={20 * k} fontWeight={600} fill={left < 0 && !hide ? RING_OVER : tokens.colorNeutralForeground1}>{label}</text>
      {!hide && <text x={c} y={c + 19 * k} textAnchor="middle" fontSize={10 * k} fill={tokens.colorNeutralForeground3}>kcal</text>}
    </svg>
  )
}
export const RING_EATEN = '#C239B3', RING_MOVE = '#5DA84E', RING_NET = '#E8833A', RING_OVER = '#D13438'

/** Schritte zum Ziel; darüber steht der Balken für alle Schritte - das Ziel dunkelgrün, was darüber ist, hellgrün. */
export function StepsBar({ steps, goal, height = 6, color }: { steps: number; goal: number; height?: number; color: string }) {
  const box = { height, borderRadius: height / 2, background: tokens.colorNeutralStroke2, overflow: 'hidden', display: 'flex' } as const
  if (goal > 0 && steps > goal) return <div style={box}>
    <div style={{ flex: goal, background: STEPS_DONE }} /><div style={{ flex: steps - goal, background: STEPS_EXTRA }} />
  </div>
  return <div style={box}><div style={{ width: `${goal > 0 ? Math.min(100, steps / goal * 100) : 0}%`, background: goal > 0 && steps >= goal ? STEPS_DONE : color }} /></div>
}

/** Balken je Tag, optional Ziel je Tag (Linie bzw. Strich), Beschriftung erste/letzte. */
export function Bars({ values, goals, color, unit, height = 150, labels }: {
  values: (number | null)[]; goals?: (number | null)[]; color: string; unit?: string; height?: number; labels: [string, string]
}) {
  const W = 320, H = height, x0 = 40, x1 = W - 6, y0 = 8, y1 = H - 22
  const max = Math.max(1, ...values.map(v => v ?? 0), ...(goals ?? []).map(g => g ?? 0)) * 1.08
  const ticks = niceTicks(max)
  const bw = (x1 - x0) / Math.max(1, values.length)
  const Y = (v: number) => y1 - (y1 - y0) * v / max
  return (
    <svg viewBox={`0 0 ${W} ${H}`} width="100%" role="img" style={{ display: 'block' }}>
      {ticks.map(v => <g key={v}>
        <line x1={x0} x2={x1} y1={Y(v)} y2={Y(v)} stroke={tokens.colorNeutralStroke2} />
        <text x={x0 - 4} y={Y(v) + 4} textAnchor="end" fontSize={10} fill={tokens.colorNeutralForeground3}>{fmt(v)}</text>
      </g>)}
      {values.map((v, i) => v == null ? null : <rect key={i} x={x0 + i * bw + bw * 0.15} y={Y(v)} width={bw * 0.7} height={Math.max(0, y1 - Y(v))} rx={2} fill={color}
        opacity={goals?.[i] != null && v < goals[i]! ? 0.6 : 0.95}><title>{fmt(v)}{unit ? ' ' + unit : ''}</title></rect>)}
      {goals && goals.map((g, i) => g == null ? null : <line key={'g' + i} x1={x0 + i * bw} x2={x0 + (i + 1) * bw} y1={Y(g)} y2={Y(g)} stroke={tokens.colorNeutralForeground1} strokeWidth={1.5} strokeDasharray="3 2" />)}
      <text x={x0} y={H - 6} fontSize={10} fill={tokens.colorNeutralForeground3}>{labels[0]}</text>
      <text x={x1} y={H - 6} textAnchor="end" fontSize={10} fill={tokens.colorNeutralForeground3}>{labels[1]}</text>
    </svg>
  )
}

/** Gewicht: Punkte je Messung, geglätteter Trend (gleitend, neuere zählen mehr), optional Ziel. */
export function WeightLine({ values, labels, height = 150 }: { values: (number | null)[]; labels: [string, string]; height?: number }) {
  const W = 320, H = height, x0 = 40, x1 = W - 6, y0 = 8, y1 = H - 22
  const known = values.filter((v): v is number => v != null)
  if (known.length === 0) return null
  const lo = Math.floor(Math.min(...known) - 0.5), hi = Math.ceil(Math.max(...known) + 0.5)
  const X = (i: number) => x0 + (x1 - x0) * i / Math.max(1, values.length - 1)
  const Y = (v: number) => y1 - (y1 - y0) * (v - lo) / Math.max(0.1, hi - lo)
  let t: number | null = null
  const trend = values.map(v => v == null ? t : (t = t == null ? v : t + 0.25 * (v - t)))
  const pts = trend.map((v, i) => v == null ? null : `${X(i)},${Y(v)}`).filter(Boolean).join(' ')
  const ticks = [lo, Math.round((lo + hi) / 2), hi]
  return (
    <svg viewBox={`0 0 ${W} ${H}`} width="100%" role="img" style={{ display: 'block' }}>
      {ticks.map(v => <g key={v}>
        <line x1={x0} x2={x1} y1={Y(v)} y2={Y(v)} stroke={tokens.colorNeutralStroke2} />
        <text x={x0 - 4} y={Y(v) + 4} textAnchor="end" fontSize={10} fill={tokens.colorNeutralForeground3}>{v}</text>
      </g>)}
      {values.map((v, i) => v == null ? null : <circle key={i} cx={X(i)} cy={Y(v)} r={2.5} fill={tokens.colorNeutralForeground3}><title>{fmt(v, 1)} kg</title></circle>)}
      <polyline points={pts} fill="none" stroke={tokens.colorBrandForeground1} strokeWidth={2.5} />
      <text x={x0} y={H - 6} fontSize={10} fill={tokens.colorNeutralForeground3}>{labels[0]}</text>
      <text x={x1} y={H - 6} textAnchor="end" fontSize={10} fill={tokens.colorNeutralForeground3}>{labels[1]}</text>
    </svg>
  )
}

/** Schöne Rasterwerte (0, 500, 1000 ...) bis max. */
function niceTicks(max: number): number[] {
  const raw = max / 3
  const pow = Math.pow(10, Math.floor(Math.log10(raw)))
  const step = [1, 2, 2.5, 5, 10].map(f => f * pow).find(s => s >= raw) ?? raw
  const out: number[] = []
  for (let v = step; v < max; v += step) out.push(v)
  return out
}
