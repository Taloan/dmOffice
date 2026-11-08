import { Spinner, Tab, TabList, Text, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { useApp } from '../App'
import { t } from '../i18n'
import { coachApi, DayStat, fmt, Targets } from './api'
import { Bars, WeightLine } from './Charts'

const useStyles = makeStyles({
  kpis: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(140px, 1fr))', gap: '10px', margin: '12px 0' },
  kpi: { backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusLarge, padding: '10px 12px' },
  value: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums' },
  label: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  charts: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(300px, 1fr))', gap: '14px' },
  chart: { backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusLarge, padding: '10px 12px', minWidth: 0 },
  macro: { display: 'flex', height: '22px', borderRadius: '4px', overflow: 'hidden', margin: '8px 0' },
})

/** Statistik: Kennzahlen und Verläufe für 7, 30, 90 oder 365 Tage. Tage ohne Einträge zählen bei Durchschnitten nicht mit. */
export function CoachStats() {
  const s = useStyles()
  const { fail } = useApp()
  const [days, setDays] = useState(30)
  const [data, setData] = useState<{ days: DayStat[]; targets: Targets } | null>(null)
  useEffect(() => { setData(null); coachApi.stats(days).then(setData).catch(fail) }, [days, fail])
  const range = (
    <TabList selectedValue={String(days)} onTabSelect={(_, d) => setDays(Number(d.value))} size="small">
      <Tab value="7">{t('Woche')}</Tab><Tab value="30">{t('Monat')}</Tab><Tab value="90">{t('3 Monate')}</Tab><Tab value="365">{t('Jahr')}</Tab>
    </TabList>
  )
  if (!data) return <>{range}<Spinner /></>
  const list = data.days
  const logged = list.filter(d => d.entries > 0)
  const avg = (xs: number[]) => xs.length ? xs.reduce((a, b) => a + b, 0) / xs.length : null
  const weights = list.filter(d => d.weight != null)
  const change = weights.length >= 2 ? weights[weights.length - 1].weight! - weights[0].weight! : null
  const stepsDays = list.filter(d => d.steps != null)
  const reached = stepsDays.filter(d => d.stepGoal && d.steps! >= d.stepGoal).length
  const waterDays = list.filter(d => d.water > 0)
  const macroSum = logged.reduce((a, d) => ({ p: a.p + d.protein * 4, c: a.c + d.carbs * 4, f: a.f + d.fat * 9 }), { p: 0, c: 0, f: 0 })
  const total = macroSum.p + macroSum.c + macroSum.f
  const labels: [string, string] = [new Date(list[0].day + 'T12:00:00').toLocaleDateString(undefined, { day: 'numeric', month: 'short' }), t('heute')]
  const sleep = list.filter(d => d.sleepMin != null)
  return (
    <div>
      {range}
      <div className={s.kpis}>
        <Kpi value={change == null ? '–' : (change > 0 ? '+' : '') + fmt(change, 1) + ' kg'} label={t('Gewicht im Zeitraum')} />
        <Kpi value={fmt(avg(logged.map(d => d.kcal)))} label={t('Ø kcal an Tagen mit Einträgen')} />
        <Kpi value={fmt(avg(stepsDays.map(d => d.steps!)))} label={t('Ø Schritte')} />
        <Kpi value={stepsDays.length ? `${reached} / ${stepsDays.length}` : '–'} label={t('Tage mit Schrittziel')} />
        <Kpi value={waterDays.length ? fmt(avg(waterDays.map(d => d.water))! / 1000, 2) + ' l' : '–'} label={t('Ø getrunken')} />
        <Kpi value={sleep.length ? fmtHours(avg(sleep.map(d => d.sleepMin!))!) : '–'} label={t('Ø Schlaf')} />
      </div>
      <div className={s.charts}>
        <div className={s.chart}>
          <Text weight="semibold">{t('Gewicht und Trend')}</Text>
          {weights.length ? <WeightLine values={list.map(d => d.weight)} labels={labels} /> : <Empty />}
        </div>
        <div className={s.chart}>
          <Text weight="semibold">{t('Kalorien je Tag')}</Text>
          {logged.length ? <Bars values={list.map(d => d.entries ? d.kcal : null)} goals={list.map(() => data.targets.kcal)} color={tokens.colorBrandForeground1} unit="kcal" labels={labels} /> : <Empty />}
        </div>
        <div className={s.chart}>
          <Text weight="semibold">{t('Schritte und Ziel')}</Text>
          {stepsDays.length ? <Bars values={list.map(d => d.steps)} goals={list.map(d => d.stepGoal)} color={tokens.colorPaletteGreenForeground2} labels={labels} /> : <Empty />}
        </div>
        <div className={s.chart}>
          <Text weight="semibold">{t('Wasser')}</Text>
          {waterDays.length ? <Bars values={list.map(d => d.water || null)} goals={list.map(() => data.targets.waterMl)} color={tokens.colorPaletteBlueBorderActive} unit="ml" labels={labels} /> : <Empty />}
        </div>
        <div className={s.chart}>
          <Text weight="semibold">{t('Makros im Durchschnitt')}</Text>
          {total > 0 ? <>
            <div className={s.macro}>
              <div style={{ width: `${macroSum.p / total * 100}%`, background: tokens.colorPaletteRedForeground2 }} />
              <div style={{ width: `${macroSum.c / total * 100}%`, background: tokens.colorPaletteMarigoldForeground2 }} />
              <div style={{ width: `${macroSum.f / total * 100}%`, background: tokens.colorPaletteTealForeground2 }} />
            </div>
            <Text size={200} block>{t('Eiweiß {p} % · Kohlenhydrate {c} % · Fett {f} %', { p: fmt(macroSum.p / total * 100), c: fmt(macroSum.c / total * 100), f: fmt(macroSum.f / total * 100) })}</Text>
            {data.targets.kcal && <Text size={200} block className={s.label}>{t('Ziel: Eiweiß {p} % · Kohlenhydrate {c} % · Fett {f} %', {
              p: fmt(data.targets.protein! * 4 / data.targets.kcal * 100), c: fmt(data.targets.carbs! * 4 / data.targets.kcal * 100), f: fmt(data.targets.fat! * 9 / data.targets.kcal * 100) })}</Text>}
          </> : <Empty />}
        </div>
        <div className={s.chart}>
          <Text weight="semibold">{t('Schlaf')}</Text>
          {sleep.length ? <Bars values={list.map(d => d.sleepMin != null ? Math.round(d.sleepMin / 6) / 10 : null)} color={tokens.colorPaletteLavenderForeground2} unit="h" labels={labels} /> : <Empty text={t('Kommt von der Uhr über die Handy-App (Health Connect).')} />}
        </div>
      </div>
    </div>
  )
  function Kpi({ value, label }: { value: string; label: string }) {
    return <div className={s.kpi}><div className={s.value}>{value}</div><div className={s.label}>{label}</div></div>
  }
  function Empty({ text }: { text?: string }) { return <Text block size={200} className={s.label} style={{ padding: '20px 0' }}>{text ?? t('Noch keine Daten in diesem Zeitraum.')}</Text> }
}

const fmtHours = (min: number) => `${Math.floor(min / 60)} h ${String(Math.round(min % 60)).padStart(2, '0')}`
