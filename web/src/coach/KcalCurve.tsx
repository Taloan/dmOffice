import { tokens } from '@fluentui/react-components'
import { t } from '../i18n'
import { Entry, fmt, Meal } from './api'

/** Übliche Uhrzeit einer Mahlzeit - für Einträge, die an einem anderen Tag nachgetragen wurden. */
const MEAL_HOUR: Record<Meal, number> = { breakfast: 8, lunch: 12.5, dinner: 19, snack: 15.5 }

/**
 * Kalorienaufnahme über den Tag: Treppe der aufsummierten Kalorien (x: Uhrzeit, y: kcal), gestrichelt das Tagesziel.
 * Zeitpunkt eines Eintrags: wann er eingetragen wurde; nachgetragene (anderer Tag) stehen bei der üblichen Uhrzeit der Mahlzeit.
 */
export function KcalCurve({ entries, date, goal }: { entries: Entry[]; date: string; goal: number | null }) {
  const W = 300, H = 120, L = 34, B = 18, T = 8, R = 6
  const dayStart = new Date(date + 'T00:00:00').getTime()
  const hourOf = (e: Entry) => {
    const h = (e.at * 1000 - dayStart) / 3_600_000
    return h >= 0 && h < 24 ? h : MEAL_HOUR[e.meal]
  }
  const points = entries.map(e => ({ h: hourOf(e), kcal: e.kcal })).sort((a, b) => a.h - b.h)
  const today = new Date().toISOString().slice(0, 10) === date
  const end = today ? Math.min(24, (Date.now() - dayStart) / 3_600_000) : 24
  const total = points.reduce((s, p) => s + p.kcal, 0)
  const max = Math.max(goal ?? 0, total, 100) * 1.08
  const x = (h: number) => L + (h / 24) * (W - L - R)
  const y = (k: number) => T + (1 - k / max) * (H - T - B)
  // Treppe: waagerecht bis zum Eintrag, dann hoch
  let sum = 0
  let d = `M${x(0)},${y(0)}`
  for (const p of points) { d += ` H${x(p.h)}`; sum += p.kcal; d += ` V${y(sum)}` }
  d += ` H${x(Math.max(end, points[points.length - 1]?.h ?? 0))}`
  const grid = tokens.colorNeutralStroke2, label = tokens.colorNeutralForeground3
  return (
    <svg viewBox={`0 0 ${W} ${H}`} style={{ width: '100%', height: 'auto', maxHeight: 160 }} role="img" aria-label={t('Kalorien im Tagesverlauf')}>
      {[0, 6, 12, 18, 24].map(h => <g key={h}>
        <line x1={x(h)} x2={x(h)} y1={T} y2={H - B} stroke={grid} strokeWidth={0.5} />
        <text x={x(h)} y={H - 4} fontSize={9} textAnchor="middle" fill={label}>{h}</text>
      </g>)}
      <line x1={L} x2={W - R} y1={y(0)} y2={y(0)} stroke={grid} />
      <text x={L - 4} y={y(0) + 3} fontSize={9} textAnchor="end" fill={label}>0</text>
      {goal != null && <>
        <line x1={L} x2={W - R} y1={y(goal)} y2={y(goal)} stroke={label} strokeDasharray="4 3" strokeWidth={1} />
        <text x={L - 4} y={y(goal) + 3} fontSize={9} textAnchor="end" fill={label}>{fmt(goal)}</text>
      </>}
      <path d={d} fill="none" stroke={tokens.colorPaletteBerryForeground2} strokeWidth={2} strokeLinejoin="round" />
      {points.length === 0 && <text x={(L + W) / 2} y={H / 2} fontSize={10} textAnchor="middle" fill={label}>{t('Noch nichts eingetragen')}</text>}
    </svg>
  )
}
