import { Button, Input, Text, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { t } from '../i18n'
import { coachApi, Day, DayStat, fmt } from './api'

const useStyles = makeStyles({
  tiles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(128px, 1fr))', gap: '10px' },
  tile: { backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusLarge, padding: '10px 12px', display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  label: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  value: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums', lineHeight: tokens.lineHeightBase500 },
  sub: { fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3 },
  card: { backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusLarge, padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: '6px' },
  track: { height: '5px', borderRadius: '3px', backgroundColor: tokens.colorNeutralStroke2, overflow: 'hidden', marginTop: '4px' },
  row: { display: 'flex', alignItems: 'center', gap: '8px', padding: '5px 0', borderTop: `1px solid ${tokens.colorNeutralStroke3}`, fontSize: tokens.fontSizeBase300, cursor: 'pointer' },
  week: { display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', gap: '8px', alignItems: 'end', height: '80px' },
  pair: { display: 'flex', gap: '3px', alignItems: 'flex-end', height: '100%' },
  bar: { flex: 1, borderRadius: '3px 3px 0 0', minHeight: '2px' },
})

const hm = (min: number) => `${Math.floor(min / 60)}:${String(min % 60).padStart(2, '0')}`

function Tile({ label, value, sub, children, onClick }: { label: string; value: string; sub?: string; children?: React.ReactNode; onClick?: () => void }) {
  const s = useStyles()
  return (
    <div className={s.tile} onClick={onClick} style={onClick ? { cursor: 'pointer' } : undefined}>
      <span className={s.label}>{label}</span>
      <span className={s.value}>{value}</span>
      {children}
      {sub && <span className={s.sub}>{sub}</span>}
    </div>
  )
}

/** Samstag oder Sonntag (Tag als „2026-10-31“) */
const isWeekend = (day: string) => { const d = new Date(day + 'T12:00:00').getDay(); return d === 0 || d === 6 }

/** Hellerer Streifen hinter den Tagen des Wochenendes; step = Abstand der Tage, shift = Versatz der Tagesmitte. */
function Stripes({ weekend, step, shift }: { weekend: boolean[]; step: number; shift: number }) {
  return <>{weekend.map((we, i) => {
    if (!we) return null
    const l = Math.max(0, i * step + shift - step / 2), r = Math.min(100, i * step + shift + step / 2)
    return <rect key={'w' + i} x={l} width={r - l} y={0} height={20} fill={tokens.colorNeutralForeground1} opacity={0.07} />
  })}</>
}

/** Balken je Tag (Schlaf) - der letzte (heute) heller; fehlende Tage leer. */
function Bars({ values, color, weekend = [] }: { values: (number | null)[]; color: string; weekend?: boolean[] }) {
  const max = Math.max(1, ...values.map(v => v ?? 0))
  const w = 100 / values.length
  return <svg viewBox="0 0 100 20" preserveAspectRatio="none" style={{ width: '100%', height: 20 }} aria-hidden>
    <Stripes weekend={weekend} step={w} shift={w / 2} />
    {values.map((v, i) => v == null ? null : <rect key={i} x={i * w + w * 0.15} width={w * 0.7} y={20 - v / max * 19} height={v / max * 19} rx={0.6}
      fill={color} opacity={i === values.length - 1 ? 1 : 0.55} />)}
  </svg>
}

/** Bereich von–bis auf einer Skala 0–100 (Body Battery) */
function Range({ low, high }: { low: number; high: number }) {
  return <div style={{ position: 'relative', height: 8, borderRadius: 4, margin: '6px 0 2px', background: 'linear-gradient(90deg, #D13438, #E8A33A, #5DA84E)' }}>
    <div style={{ position: 'absolute', left: `${low}%`, width: `${Math.max(2, high - low)}%`, top: -3, bottom: -3, border: '2px solid ' + tokens.colorNeutralForeground1, borderRadius: 6 }} />
  </div>
}

/** Kleine Linie (Gewicht, Ruhepuls, HRV); fehlende Tage (null) werden übersprungen, die übrigen bleiben an ihrem Tag. */
function Spark({ values, color, weekend = [] }: { values: (number | null)[]; color: string; weekend?: boolean[] }) {
  const step = 100 / Math.max(1, values.length - 1)
  const known = values.flatMap((v, i) => v == null ? [] : [{ i, v }])
  if (known.length < 2 && !weekend.some(Boolean)) return null
  const min = Math.min(...known.map(p => p.v)), max = Math.max(...known.map(p => p.v)), span = max - min || 1
  const pts = known.map(p => `${p.i * step},${18 - ((p.v - min) / span) * 16}`).join(' ')
  return <svg viewBox="0 0 100 20" preserveAspectRatio="none" style={{ width: '100%', height: 18 }} aria-hidden>
    <Stripes weekend={weekend} step={step} shift={0} />
    {known.length >= 2 && <polyline points={pts} fill="none" stroke={color} strokeWidth="2" vectorEffect="non-scaling-stroke" />}
  </svg>
}

/** Kacheln unter den großen Karten: Gewicht, Stress, Ruhepuls, Body Battery, Schlaf, HRV, Fasten. */
export function Metrics({ day, stats, onWeight, onFasting, noWeight }: { day: Day; stats: DayStat[] | null; onWeight: (w: number) => void; onFasting: () => void; noWeight?: boolean }) {
  const s = useStyles()
  const [edit, setEdit] = useState(false)
  const [v, setV] = useState('')
  const [now, setNow] = useState(Date.now())
  useEffect(() => { const i = setInterval(() => setNow(Date.now()), 60_000); return () => clearInterval(i) }, [])
  const a = day.activity, x = day.extra, avg = day.avg7
  const trend = day.trend ?? []
  const weekend = trend.map(d => isWeekend(d.day))
  // Gewicht: Verlauf der letzten 30 Tage, Änderung je Woche aus erstem und letztem Wert
  const weights = (stats ?? []).filter(d => d.weight != null && d.day <= day.date).map(d => ({ day: d.day, w: d.weight! }))
  let perWeek: number | null = null
  if (weights.length >= 2) {
    const days = (new Date(weights[weights.length - 1].day).getTime() - new Date(weights[0].day).getTime()) / 86_400_000
    if (days >= 6) perWeek = (weights[weights.length - 1].w - weights[0].w) / days * 7
  }
  const n = Number(v.replace(',', '.'))
  const f = day.fasting
  const fastH = f?.running && f.start ? (now - new Date(f.start).getTime()) / 3_600_000 : 0
  return (
    <div className={s.tiles}>
      {!noWeight && <Tile label={t('Gewicht')} value={day.weight ? fmt(day.weight, 1) : '–'}
        sub={perWeek != null ? t('{n} kg / Woche', { n: (perWeek > 0 ? '+' : '') + fmt(perWeek, 1) }) : undefined}
        onClick={edit ? undefined : () => { setV(''); setEdit(true) }}>
        {edit ? <div style={{ display: 'flex', gap: 4, marginTop: 2 }} onClick={e => e.stopPropagation()}>
          <Input size="small" value={v} onChange={(_, d) => setV(d.value)} inputMode="decimal" placeholder="kg" style={{ width: 70 }} autoFocus aria-label={t('Gewicht')}
            onKeyDown={e => { if (e.key === 'Enter' && n >= 20 && n <= 400) { onWeight(n); setEdit(false) } if (e.key === 'Escape') setEdit(false) }} />
          <Button size="small" disabled={!(n >= 20 && n <= 400)} onClick={() => { onWeight(n); setEdit(false) }}>{t('OK')}</Button>
        </div> : <Spark values={weights.map(w => w.w)} color={tokens.colorPaletteTealForeground2} />}
      </Tile>}
      {/* Körperwerte mit Verlauf der letzten 14 Tage, das Wochenende heller hinterlegt */}
      {x.stress != null && <Tile label={t('Stress')} value={String(Math.round(x.stress))}
        sub={x.stress < 26 ? t('entspannt') : x.stress < 51 ? t('niedrig') : x.stress < 76 ? t('mittel') : t('hoch')}>
        <Bars values={trend.map(d => d.stress)} color="#378ADD" weekend={weekend} />
      </Tile>}
      <Tile label={t('Ruhepuls')} value={a?.restingHr ? String(a.restingHr) : '–'} sub={avg.restingHr ? t('Ø {v}', { v: avg.restingHr }) : undefined}>
        <Spark values={trend.map(d => d.restingHr)} color="#E0675B" weekend={weekend} />
      </Tile>
      {(x.bodyBatteryHigh != null || x.bodyBatteryLow != null) &&
        <Tile label="Body Battery" value={`${x.bodyBatteryLow ?? '–'}–${x.bodyBatteryHigh ?? '–'}`} sub={t('niedrigster und höchster Wert')}>
          <Range low={x.bodyBatteryLow ?? 0} high={x.bodyBatteryHigh ?? 100} />
        </Tile>}
      <Tile label={t('Schlaf')} value={a?.sleepMin ? hm(a.sleepMin) : '–'} sub={avg.sleepMin ? t('Ø {v}', { v: hm(avg.sleepMin) }) : undefined}>
        {trend.some(d => d.sleepMin) && <Bars values={trend.map(d => d.sleepMin)} color="#7F77DD" weekend={weekend} />}
      </Tile>
      {x.hrv != null && <Tile label="HRV" value={String(Math.round(x.hrv)) + ' ms'} sub={avg.hrv ? t('Ø {v}', { v: avg.hrv }) : undefined}>
        <Spark values={trend.map(d => d.hrv)} color="#1D9E75" weekend={weekend} />
      </Tile>}
      {f && <Tile label={t('Fasten')} value={f.running ? hm(Math.round(fastH * 60)) : t('pausiert')} onClick={onFasting}
        sub={f.running ? t('Ziel {h} h', { h: f.goal }) : t('Plan {plan}', { plan: f.plan })}>
        {f.running && f.goal > 0 && <div className={s.track}><div style={{ height: '100%', width: `${Math.min(100, fastH / f.goal * 100)}%`, backgroundColor: tokens.colorPaletteBerryForeground2 }} /></div>}
      </Tile>}
    </div>
  )
}

interface Insight { key: string; kind: string; title: string }
interface Check { key: string; title: string; state: string }

/** Hinweise: fällige Vorsorge und Impfungen, dazu die ersten Hinweise aus dem Tagebuch und Profil. */
export function Hints({ onOpen }: { onOpen: () => void }) {
  const s = useStyles()
  const [list, setList] = useState<{ key: string; text: string }[] | null>(null)
  useEffect(() => {
    Promise.all([
      api.get<{ items: Check[] }>('/api/coach/checks').then(r => r.items.filter(i => i.state === 'due')).catch(() => [] as Check[]),
      api.get<Insight[]>('/api/coach/insights').then(r => r.filter(i => i.kind === 'diary' || i.kind === 'profile')).catch(() => [] as Insight[]),
    ]).then(([due, ins]) => setList([
      ...due.slice(0, 2).map(c => ({ key: 'c' + c.key, text: t('Fällig: {title}', { title: c.title }) })),
      ...ins.slice(0, 3).map(i => ({ key: 'i' + i.key, text: i.title })),
    ]))
  }, [])
  return (
    <div className={s.card}>
      <Text weight="semibold">{t('Hinweise')}</Text>
      {list == null ? null : list.length === 0
        ? <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Gerade nichts Besonderes.')}</Text>
        : list.map(h => <div key={h.key} className={s.row} onClick={onOpen}>{h.text}</div>)}
    </div>
  )
}

/** Diese Woche: je Tag Kalorien zum Ziel und Schritte zum Ziel. */
export function Week({ stats, kcalGoal, stepGoal, date }: { stats: DayStat[] | null; kcalGoal: number | null; stepGoal: number; date: string }) {
  const s = useStyles()
  if (!stats) return null
  const days = stats.filter(d => d.day <= date).slice(-7)
  if (days.length === 0) return null
  const pct = (v: number | null | undefined, goal: number | null | undefined) => v && goal ? Math.min(100, v / goal * 100) : 0
  return (
    <div className={s.card}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
        <Text weight="semibold" style={{ flex: 1 }}>{t('Diese Woche')}</Text>
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
          <span style={{ color: tokens.colorPaletteBerryForeground2 }}>■</span> {t('kcal zum Ziel')} &nbsp;
          <span style={{ color: tokens.colorPaletteBlueForeground2 }}>■</span> {t('Schritte zum Ziel')}
        </Text>
      </div>
      <div className={s.week}>
        {days.map(d => (
          <div key={d.day} className={s.pair} title={`${fmt(d.kcal)} kcal · ${fmt(d.steps ?? 0)} ${t('Schritte')}`}>
            <div className={s.bar} style={{ height: `${pct(d.kcal, kcalGoal)}%`, backgroundColor: d.kcal > (kcalGoal ?? Infinity) * 1.1 ? tokens.colorPaletteMarigoldForeground2 : tokens.colorPaletteBerryForeground2 }} />
            <div className={s.bar} style={{ height: `${pct(d.steps, d.stepGoal ?? stepGoal)}%`, backgroundColor: tokens.colorPaletteBlueForeground2 }} />
          </div>
        ))}
      </div>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', gap: 8, textAlign: 'center' }}>
        {days.map(d => <Text key={d.day} size={100} style={{ color: tokens.colorNeutralForeground3 }}>{new Date(d.day + 'T12:00:00').toLocaleDateString(undefined, { weekday: 'short' })}</Text>)}
      </div>
    </div>
  )
}

/** Statistik der letzten 30 Tage für Kacheln und Woche. */
export function useStats(date: string, version: number) {
  const [stats, setStats] = useState<DayStat[] | null>(null)
  useEffect(() => { coachApi.stats(30).then(r => setStats(r.days)).catch(() => setStats([])) }, [date, version])
  return stats
}
