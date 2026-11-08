import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Menu, MenuItem, MenuList, MenuPopover,
  MenuTrigger, MessageBar, MessageBarBody, Spinner, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowClockwise20Regular, ArrowLeft20Regular, ArrowRight20Regular, BookOpen20Regular, CalendarToday20Regular, ChartMultiple20Regular,
  Delete20Regular, Food20Regular, FoodApple20Regular, Lightbulb20Regular, MoreHorizontal20Regular, PersonSettings20Regular,
  PlugConnected20Regular, TaskListLtr20Regular, Timer20Regular,
} from '@fluentui/react-icons'
import { api } from '../api'
import { RibbonButton, RibbonDivider, RibbonHeader } from '../shell/Ribbon'
import { useCallback, useEffect, useState } from 'react'
import { useApp } from '../App'
import { t } from '../i18n'
import { AddFood } from './AddFood'
import { addDays, coachApi, Day, DayStat, Entry, fmt, isoDay, Meal, MEAL_COLOR, MEALS, mealName, mealNow, STEPS_EXTRA } from './api'
import { ActivityView } from './ActivityView'
import { Ring, RING_EATEN, RING_MOVE, StepsBar, TripleRing } from './Charts'
import { CoachStats } from './CoachStats'
import { Fasting } from './Fasting'
import { Insights } from './Insights'
import { Plan } from './Plan'
import { Recipes } from './Recipes'
import { Hints, Metrics, useStats, Week } from './TodayTiles'
import { KcalCurve } from './KcalCurve'
import { Balance } from './Balance'

const useStyles = makeStyles({
  root: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, overflow: 'hidden' },
  // Leiste unter dem Menüband wie im Tagebuch: was gerade zu sehen ist
  bar: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap', padding: '10px 16px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  scroll: { flex: 1, overflowY: 'auto', padding: '16px' },
  grid: { display: 'grid', gridTemplateColumns: 'minmax(260px, 340px) minmax(0, 1fr)', gap: '16px', alignItems: 'start', '@media (max-width: 800px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  col: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  // Tagesansicht als Übersicht: oben Energie, Bewegung, Wasser; darunter Kacheln, Mahlzeiten mit Hinweisen, die Woche
  dash: {
    display: 'grid', gridTemplateColumns: 'minmax(0, 1.25fr) minmax(0, 1fr)', gap: '12px', alignItems: 'start', maxWidth: '1400px',
    '@media (max-width: 1100px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
    // Breit: die Hinweise bekommen eine eigene dritte Spalte
    '@media (min-width: 1600px)': { gridTemplateColumns: 'minmax(0, 1.25fr) minmax(0, 1fr) minmax(260px, 0.55fr)', maxWidth: '1900px' },
  },
  hintsSide: { '@media (min-width: 1600px)': { display: 'none' } },
  hintsWide: { display: 'none', minWidth: 0, '@media (min-width: 1600px)': { display: 'block' } },
  side: { display: 'flex', flexDirection: 'column', gap: '12px', minWidth: 0 },
  full: { gridColumn: '1 / -1', minWidth: 0 },
  // Drei gleich breite Spalten wie die Nährstoff-Balken darunter, jede Grafik mittig in ihrer Spalte
  summary: { display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', alignItems: 'center', justifyItems: 'center', gap: '12px', '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  sumStats: { width: '100%', maxWidth: '220px' },
  sumChart: { minWidth: 0, width: '100%' },
  sumSide: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '2px' },
  sumNum: { fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold, fontVariantNumeric: 'tabular-nums' },
  mealRow: {
    display: 'flex', alignItems: 'center', gap: '12px', padding: '10px 14px', cursor: 'pointer', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': { borderBottom: 'none' }, ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  mealIcon: { width: '44px', height: '44px', position: 'relative', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '20px', flexShrink: 0 },
  signs: { display: 'flex', justifyContent: 'center', gap: '28px', fontVariantNumeric: 'tabular-nums' },
  status: { display: 'flex', alignItems: 'center', gap: '8px', borderRadius: '12px', padding: '7px 12px', cursor: 'pointer', fontSize: tokens.fontSizeBase300 },
  pair: { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: '12px', '@media (max-width: 560px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  top: { display: 'grid', gridTemplateColumns: 'minmax(0, 1.3fr) minmax(0, 1fr) minmax(0, 1fr)', gap: '12px', alignItems: 'start', '@media (max-width: 1000px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  lower: { display: 'grid', gridTemplateColumns: 'minmax(0, 2fr) minmax(220px, 1fr)', gap: '12px', alignItems: 'start', '@media (max-width: 800px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  card: { backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusLarge, padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: '8px' },
  cardHead: { display: 'flex', alignItems: 'center', gap: '8px' },
  grow: { flex: 1, minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  num: { fontVariantNumeric: 'tabular-nums' },
  track: { height: '6px', borderRadius: '3px', backgroundColor: tokens.colorNeutralStroke2, overflow: 'hidden' },
  fill: { height: '100%', borderRadius: '3px' },
  macro: { display: 'flex', flexDirection: 'column', gap: '3px', fontSize: tokens.fontSizeBase200 },
  entry: { display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 0', borderTop: `1px solid ${tokens.colorNeutralStroke3}` },
  glasses: { display: 'flex', gap: '6px', flexWrap: 'wrap', alignItems: 'center' },
  dayNav: { display: 'flex', alignItems: 'center', gap: '2px' },
})

/** Coach (docs/dmcoach-konzept.md): Heute (Tagebuch), Statistik, Profil. Stufe 2 (Planen, Rezepte, Fasten) kommt dazu. */
type Tab = 'today' | 'stats' | 'fasting' | 'plan' | 'recipes' | 'insights'
const TABS: Tab[] = ['stats', 'fasting', 'plan', 'recipes', 'insights']
/** Profil und Ziele liegen in den Einstellungen. */
export const openCoachProfile = () => { location.hash = '#/settings/coach-profile' }
/** Reiter aus der Adresse (#/coach/plan …) - beim Öffnen und bei Links, während der Coach schon offen ist. */
const tabOfHash = (): Tab => TABS.find(x => location.hash.includes('/' + x)) ?? 'today'

export default function CoachView() {
  const s = useStyles()
  const [tab, setTab] = useState<Tab>(tabOfHash)
  // Alte Links auf #/coach/profile führen in die Einstellungen
  useEffect(() => { if (location.hash.startsWith('#/coach/profile')) openCoachProfile() }, [])
  const [date, setDate] = useState(() => isoDay(new Date()))
  const today = isoDay(new Date())
  useEffect(() => { history.replaceState(history.state, '', '#/coach' + (tab === 'today' ? '' : '/' + tab)) }, [tab])
  // Link auf #/coach/profile bzw. /stats, während der Coach schon offen ist
  useEffect(() => {
    const on = () => { if (location.hash.startsWith('#/coach')) setTab(tabOfHash()) }
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [])
  const dayLabel = date === today ? t('Heute') : date === addDays(today, -1) ? t('Gestern') : new Date(date + 'T12:00:00').toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long' })
  const { fail, notify } = useApp()
  // Aus dem Menüband in die Tagesansicht: Essen eintragen (Dialog für die Mahlzeit jetzt) und neu laden
  const [addReq, setAddReq] = useState<{ meal: Meal; n: number } | null>(null)
  const [refresh, setRefresh] = useState(0)
  // Uhr verbunden (Garmin): „Abgleichen“ holt die Werte sofort statt beim nächsten Durchgang
  const [garmin, setGarmin] = useState(false)
  const [syncing, setSyncing] = useState(false)
  useEffect(() => { api.get<{ connected: boolean }>('/api/coach/garmin').then(g => setGarmin(g.connected)).catch(() => {}) }, [])
  const showToday = (f: () => void) => { setTab('today'); f() }
  const sync = async () => {
    setSyncing(true)
    try { const r = await api.post<{ days: number }>('/api/coach/garmin/sync'); notify(t('Von der Uhr abgeglichen ({n} Tage).', { n: r.days })); setRefresh(n => n + 1) }
    catch (e) { fail(e) } finally { setSyncing(false) }
  }
  const viewName = ({ today: t('Tagebuch'), plan: t('Planen'), recipes: t('Rezepte'), fasting: t('Fasten'), insights: t('Hinweise'), stats: t('Statistik') } as Record<Tab, string>)[tab]
  const views: [Tab, JSX.Element, string][] = [
    ['today', <BookOpen20Regular />, t('Tagebuch')], ['plan', <TaskListLtr20Regular />, t('Planen')], ['recipes', <FoodApple20Regular />, t('Rezepte')],
    ['fasting', <Timer20Regular />, t('Fasten')], ['insights', <Lightbulb20Regular />, t('Hinweise')], ['stats', <ChartMultiple20Regular />, t('Statistik')],
  ]
  return (
    <div className={s.root}>
      {/* Menüband wie in den anderen Bereichen: Start (Eintragen, Tag, Abgleich, Einstellungen) und Ansicht (die Bereiche des Coach) */}
      <RibbonHeader area="coach"
        start={<>
          <RibbonButton primary icon={<Food20Regular />} label={t('Essen eintragen')} onClick={() => showToday(() => setAddReq(r => ({ meal: mealNow(), n: (r?.n ?? 0) + 1 })))} />
          <RibbonDivider />
          <RibbonButton icon={<ArrowLeft20Regular />} label={t('Tag davor')} onClick={() => showToday(() => setDate(addDays(date, -1)))} />
          <RibbonButton icon={<CalendarToday20Regular />} label={t('Heute')} pressed={tab === 'today' && date === today} onClick={() => showToday(() => setDate(today))} />
          <RibbonButton icon={<ArrowRight20Regular />} label={t('Tag danach')} disabled={date >= today} onClick={() => showToday(() => setDate(addDays(date, 1)))} />
          {garmin && <>
            <RibbonDivider />
            <RibbonButton icon={<ArrowClockwise20Regular />} label={syncing ? t('Gleicht ab …') : t('Abgleichen')} title={t('Schritte, Schlaf und Gewicht jetzt von der Uhr holen')}
              disabled={syncing} onClick={sync} />
          </>}
          <RibbonDivider />
          <RibbonButton icon={<PersonSettings20Regular />} label={t('Profil & Ziele')} onClick={openCoachProfile} />
          <RibbonButton icon={<PlugConnected20Regular />} label={t('Verbindungen')} onClick={() => { location.hash = '#/settings/coach-connections' }} />
          {/* Wie bei Office: das Wichtigste aus „Ansicht“ auch hier - Tagebuch, Planen, Rezepte, Fasten, Statistik */}
          <RibbonDivider />
          {views.filter(([v]) => v !== 'insights').map(([v, icon, label]) => <RibbonButton key={v} icon={icon} label={label} pressed={tab === v} onClick={() => setTab(v)} />)}
        </>}
        view={<>{views.map(([v, icon, label]) => <RibbonButton key={v} icon={icon} label={label} pressed={tab === v} onClick={() => setTab(v)} />)}</>} />
      <div className={s.bar}>
        <Text weight="semibold">{viewName}</Text>
        {tab === 'today' && <Text>{dayLabel}</Text>}
      </div>
      <div className={s.scroll}>
        {tab === 'today' && <Today date={date} onProfile={openCoachProfile} onTab={setTab} addReq={addReq} refresh={refresh} />}
        {tab === 'plan' && <Plan />}
        {tab === 'recipes' && <Recipes />}
        {tab === 'fasting' && <Fasting />}
        {tab === 'insights' && <Insights />}
        {tab === 'stats' && <CoachStats />}
      </div>
    </div>
  )
}

function Today({ date, onProfile, onTab, addReq, refresh }: { date: string; onProfile: () => void; onTab: (t: Tab) => void; addReq: { meal: Meal; n: number } | null; refresh: number }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [day, setDay] = useState<Day | null>(null)
  const [adding, setAdding] = useState<Meal | null>(null)
  const [openMeal, setOpenMeal] = useState<Meal | null>(null)
  // Aktivität mit Karte und Fotos
  const [activity, setActivity] = useState<number | null>(null)
  const closeActivity = useCallback(() => setActivity(null), [])
  const [template, setTemplate] = useState<{ meal: Meal; name: string } | null>(null)
  const [version, setVersion] = useState(0)
  const load = useCallback(() => coachApi.day(date).then(d => { setDay(d); setVersion(v => v + 1) }).catch(fail), [date, fail])
  useEffect(() => { setDay(null); load() }, [load])
  // Aus dem Menüband: neu laden (Wasser, Abgleich) bzw. Eintragen öffnen
  useEffect(() => { if (refresh) load() }, [refresh]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (addReq) setAdding(addReq.meal) }, [addReq])
  const stats = useStats(date, version)
  if (!day) return <Spinner />
  const budget = day.budget
  const hide = day.hideNumbers
  const run = async (f: () => Promise<unknown>) => { try { await f(); await load() } catch (e) { fail(e) } }
  const ring = budget != null
    ? <TripleRing eaten={day.eaten.kcal} bonus={day.movement ?? day.activityBonus} budget={budget} hide={hide} />
    : <Ring value={0} max={1} label={fmt(day.eaten.kcal)} sub={t('kcal gegessen')} />
  return (
    <div className={s.dash}>
      <div className={s.side}>
        {/* Übersicht: Ring, links gegessen, rechts verbrannt, darunter die Nährstoffe */}
        <div className={s.card}>
          {/* Links die Waage, in der Mitte der Ring, rechts die Kalorien im Tagesverlauf */}
          <div className={s.summary}>
            {/* Waage: gegessen gegen den Verbrauch des Tages (Gesamtbedarf plus Bewegung) */}
            <div className={s.sumStats}><Balance eaten={day.eaten.kcal} burned={day.targets.tdee != null ? Math.round(day.targets.tdee + day.activityBonus) : null} goal={budget} past={date < isoDay(new Date())} hide={hide} /></div>
            {ring}
            {!hide && <div className={s.sumChart}><KcalCurve entries={MEALS.flatMap(m => day.meals[m] ?? [])} date={date} goal={budget} /></div>}
          </div>
          {day.targets.complete ? <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 12 }}>
            <Macro label={t('Eiweiß')} value={day.eaten.protein} goal={day.targets.protein} color={tokens.colorPaletteRedForeground2} />
            <Macro label={t('coach|Fett')} value={day.eaten.fat} goal={day.targets.fat} color={tokens.colorPaletteTealForeground2} />
            <Macro label={t('Kohlenhydrate')} value={day.eaten.carbs} goal={day.targets.carbs} color={tokens.colorPaletteMarigoldForeground2} />
          </div> : <MessageBar intent="info" layout="multiline"><MessageBarBody>
            {t('Für Kalorien- und Nährstoffziele fehlen noch Angaben.')} <Button appearance="transparent" size="small" onClick={onProfile}>{t('Profil ausfüllen')}</Button>
          </MessageBarBody></MessageBar>}
          {!hide && budget != null && <div className={s.signs}>
            <span><Text weight="semibold" size={500} style={{ color: RING_EATEN }}>+{fmt(day.eaten.kcal)}</Text> <Text size={200} className={s.muted}>{t('gegessen')}</Text></span>
            {(day.movement ?? day.activityBonus) > 0 && <span><Text weight="semibold" size={500} style={{ color: RING_MOVE }}>−{fmt(day.movement ?? day.activityBonus)}</Text> <Text size={200} className={s.muted}>{t('Bewegung')}</Text></span>}
          </div>}
          {date === isoDay(new Date()) && <StatusLine fasting={day.fasting} onFasting={() => onTab('fasting')} />}
        </div>
        {/* Ernährung: je Mahlzeit eine Zeile - Klick zeigt die Einträge, + trägt ein */}
        <div className={s.card} style={{ padding: '4px 0' }}>
          {MEALS.map(m => {
            const entries = day.meals[m] ?? []
            const kcal = entries.reduce((x, e) => x + e.kcal, 0)
            const goal = budget != null && MEAL_SHARE[m] ? Math.round(budget * MEAL_SHARE[m]) : null
            return (
              <div key={m} className={s.mealRow} style={{ borderLeft: `4px solid ${MEAL_COLOR[m]}` }} onClick={() => setOpenMeal(m)} role="button" tabIndex={0} onKeyDown={e => { if (e.key === 'Enter') setOpenMeal(m) }}>
                <span className={s.mealIcon} aria-hidden>
                  <svg width="44" height="44" viewBox="0 0 44 44" style={{ position: 'absolute', inset: 0 }}>
                    <circle cx="22" cy="22" r="19.5" fill={MEAL_COLOR[m] + '26'} stroke={MEAL_COLOR[m] + '40'} strokeWidth="3" />
                    {goal && kcal > 0 && <circle cx="22" cy="22" r="19.5" fill="none" stroke={MEAL_COLOR[m]} strokeWidth="3" strokeLinecap="round"
                      strokeDasharray={`${Math.min(1, kcal / goal) * 122.5} 122.5`} transform="rotate(-90 22 22)" />}
                  </svg>
                  <span style={{ position: 'relative' }}>{MEAL_ICON[m]}</span>
                </span>
                <div className={s.grow}>
                  <Text weight="semibold" block>{mealName(m)}</Text>
                  <Text size={200} className={s.muted} block truncate wrap={false}>
                    {hide ? (entries.length ? (entries.length === 1 ? t('1 Eintrag') : t('{n} Einträge', { n: entries.length })) : '–') : `${fmt(kcal)}${goal ? ' / ' + fmt(goal) : ''} kcal`}
                    {entries.length > 0 && !hide ? ' · ' + (entries.length === 1 ? t('1 Eintrag') : t('{n} Einträge', { n: entries.length })) : ''}
                  </Text>
                </div>
                <Button shape="circular" appearance="primary" style={{ backgroundColor: MEAL_COLOR[m], borderColor: MEAL_COLOR[m] }} icon={<Add20Regular />}
                  aria-label={t('{meal} hinzufügen', { meal: mealName(m) })} onClick={e => { e.stopPropagation(); setAdding(m) }} />
              </div>
            )
          })}
        </div>
      </div>
      <div className={s.side}>
        <div className={s.pair}>
          <WaterCard day={day} onWater={ml => run(() => coachApi.water(date, ml))} onUndo={id => run(() => coachApi.removeWater(id))} />
          <WeightCard day={day} stats={stats} onWeight={w => run(() => coachApi.weight(date, w))} />
        </div>
        <StepsCard day={day} onSteps={n => run(() => coachApi.steps(date, n))} />
        {(day.activities?.length ?? 0) > 0 && <div className={s.card} style={tint('#F2B53C')}>
          <div className={s.cardHead}><Text weight="semibold" className={s.grow} style={{ color: '#F2B53C' }}>🏃 {t('Aktivitäten')}</Text><Text size={200} className={s.muted}>{t('von der Uhr')}</Text></div>
          {day.activities!.map(a => {
            const min = Math.round((a.durationS ?? 0) / 60)
            return <div key={a.id} style={{ display: 'flex', alignItems: 'center', gap: 8, cursor: 'pointer' }} role="button" tabIndex={0}
              onClick={() => setActivity(a.id)} onKeyDown={e => { if (e.key === 'Enter') setActivity(a.id) }}>
              <span aria-hidden>{/cycl|bik/.test(a.type) ? '🚴' : /run/.test(a.type) ? '🏃' : '🚶'}</span>
              <div className={s.grow}>
                <Text block truncate wrap={false}>{a.name || a.type}</Text>
                <Text size={200} className={s.muted}>{[a.start.slice(11, 16), min ? `${Math.floor(min / 60)}:${String(min % 60).padStart(2, '0')} h` : null, a.distanceM ? fmt(a.distanceM / 1000, 2) + ' km' : null].filter(Boolean).join(' · ')}</Text>
              </div>
              {a.kcal != null && <Text weight="semibold" className={s.num}>{fmt(a.kcal)} kcal</Text>}
            </div>
          })}
        </div>}
        <Metrics day={day} stats={stats} onWeight={w => run(() => coachApi.weight(date, w))} onFasting={() => onTab('fasting')} noWeight />
        <div className={s.hintsSide}><Hints onOpen={() => onTab('insights')} /></div>
      </div>
      <div className={s.hintsWide}><Hints onOpen={() => onTab('insights')} /></div>
      <div className={s.full}><Week stats={stats} kcalGoal={day.budget ?? day.targets.kcal} stepGoal={day.steps.goal} date={date} /></div>
      {openMeal && <Dialog open onOpenChange={(_, d) => { if (!d.open) setOpenMeal(null) }}>
        <DialogSurface style={{ maxWidth: 600 }}>
          <DialogBody>
            <DialogContent>
              <MealDetail meal={openMeal} day={day} onClose={() => setOpenMeal(null)} onAdd={() => { setAdding(openMeal); setOpenMeal(null) }}
                onRemove={id => run(() => coachApi.remove(id))} onMove={(id, to) => run(() => coachApi.change(id, { meal: to }))}
                onAmount={(id, g) => run(() => coachApi.change(id, { amount: g }))}
                onYesterday={ids => { const m = openMeal; run(async () => {
                  const r = await coachApi.copy(addDays(date, -1), date, m, m, ids)
                  notify(t('{n} Posten von gestern übernommen.', { n: r.added }))
                }) }}
                onTemplate={() => { setTemplate({ meal: openMeal, name: mealName(openMeal) }); setOpenMeal(null) }} />
            </DialogContent>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
      {activity != null && <ActivityView id={activity} onClose={closeActivity} />}
      {adding && <AddFood date={date} meal={adding} onClose={() => setAdding(null)} onAdded={load} />}
      {template && <Dialog open onOpenChange={(_, d) => { if (!d.open) setTemplate(null) }}>
        <DialogSurface style={{ maxWidth: 420 }}>
          <DialogBody>
            <DialogTitle>{t('Als Vorlage speichern')}</DialogTitle>
            <DialogContent>
              <Text block className={s.muted} style={{ marginBottom: 8 }}>{t('Die Vorlage steht beim Hinzufügen unter „Plan und Vorlagen“ und trägt alles auf einmal ein.')}</Text>
              <Field label={t('Name')}><Input autoFocus value={template.name} onChange={(_, d) => setTemplate({ ...template, name: d.value })} /></Field>
            </DialogContent>
            <DialogActions>
              <Button appearance="primary" disabled={!template.name.trim()} onClick={() => { const tp = template; setTemplate(null)
                run(async () => { await coachApi.saveTemplate(tp.name.trim(), date, tp.meal); notify(t('Vorlage „{name}“ gespeichert.', { name: tp.name.trim() })) }) }}>{t('Speichern')}</Button>
              <Button onClick={() => setTemplate(null)}>{t('Abbrechen')}</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
    </div>
  )
}

function Macro({ label, value, goal, color }: { label: string; value: number; goal: number | null; color: string }) {
  const s = useStyles()
  return (
    <div className={s.macro}>
      <span>{label}</span>
      <span className={s.num}>{fmt(value)}{goal ? ' / ' + fmt(goal) : ''} g</span>
      <div className={s.track}><div className={s.fill} style={{ width: `${goal ? Math.min(100, value / goal * 100) : 0}%`, backgroundColor: color }} /></div>
    </div>
  )
}

/** Anteil einer Mahlzeit am Tagesziel (wie bei den bekannten Apps; Snacks ohne eigenes Ziel). */
const MEAL_SHARE: Record<Meal, number> = { breakfast: 0.35, lunch: 0.35, dinner: 0.3, snack: 0 }
const MEAL_ICON: Record<Meal, string> = { breakfast: '☕', lunch: '🍝', dinner: '🥗', snack: '🍎' }
const WATER = '#4F8FE0', WEIGHT = '#B57BD0'
/** Getönte Karte in der Farbe ihrer Sache. */
const tint = (c: string): React.CSSProperties => ({ backgroundColor: c + '17', border: `1px solid ${c}59` })

/** Fasten an: Fastenzeit bzw. Essenszeit mit Restdauer und Endzeit; sonst die Mahlzeit, die gerade dran ist. */
function StatusLine({ fasting: f, onFasting }: { fasting: Day['fasting']; onFasting: () => void }) {
  const s = useStyles()
  const [now, setNow] = useState(() => new Date())
  useEffect(() => { const i = setInterval(() => setNow(new Date()), 60_000); return () => clearInterval(i) }, [])
  const hm = (d: Date) => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
  const dur = (ms: number) => { const m = Math.max(0, Math.round(ms / 60_000)); return `${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')} h` }
  let color = '#2E9E5B', icon = '🍽', text = t('Jetzt: {meal}', { meal: mealName(mealNow()) })
  if (f) {
    const fast = '#7C5CD6'
    if (f.running && f.start) {
      const ends = new Date(new Date(f.start).getTime() + f.goal * 3_600_000)
      color = fast; icon = '🌙'
      text = ends <= now ? t('Fastenziel erreicht') : t('Fasten – noch {d}, bis {t}', { d: dur(ends.getTime() - now.getTime()), t: hm(ends) })
    } else {
      const [h, m] = (f.eatFrom ?? '12:00').split(':').map(Number)
      const start = new Date(now); start.setHours(h || 0, m || 0, 0, 0)
      const end = new Date(start.getTime() + (24 - f.goal) * 3_600_000)
      if (now < start) { color = fast; icon = '🌙'; text = t('Fasten – noch {d}, bis {t}', { d: dur(start.getTime() - now.getTime()), t: hm(start) }) }
      else if (now < end) text = t('Essenszeit – noch {d}, bis {t}', { d: dur(end.getTime() - now.getTime()), t: hm(end) })
      else { color = fast; icon = '🌙'; text = t('Fasten ab {t}', { t: hm(end) }) }
    }
  }
  return <div className={s.status} style={{ backgroundColor: color + '38' }} onClick={f ? onFasting : undefined} role={f ? 'button' : undefined}>
    <span aria-hidden>{icon}</span><span>{text}</span>
  </div>
}

/** Gewichtsverlauf als Linie über die Tage from…to (fehlende Tage überbrückt), letzter Wert als Punkt; so hoch wie die Flaschen beim Wasser. */
function WeightLine({ points, from, to, color }: { points: { day: string; w: number }[]; from: string; to: string; color: string }) {
  if (points.length === 0) return <div style={{ height: 44 }} />
  const t0 = new Date(from).getTime(), span = Math.max(1, new Date(to).getTime() - t0)
  const ws = points.map(p => p.w)
  const min = Math.min(...ws), max = Math.max(...ws), range = max - min || 1
  const xy = points.map(p => [((new Date(p.day).getTime() - t0) / span) * 96 + 2, 40 - ((p.w - min) / range) * 32] as const)
  const line = xy.map(([x, y]) => `${x},${y}`).join(' ')
  const [lx, ly] = xy[xy.length - 1]
  // Der Punkt außerhalb des gestreckten SVG - sonst würde er zur Ellipse
  return (
    <div style={{ position: 'relative', height: 44 }} role="img" aria-label={t('Gewicht der letzten 14 Tage')}>
      <svg viewBox="0 0 100 44" preserveAspectRatio="none" style={{ width: '100%', height: 44, display: 'block' }} aria-hidden>
        {xy.length > 1 && <polygon points={`${xy[0][0]},44 ${line} ${lx},44`} fill={color} opacity={0.15} />}
        {xy.length > 1 && <polyline points={line} fill="none" stroke={color} strokeWidth="2" vectorEffect="non-scaling-stroke" strokeLinejoin="round" />}
      </svg>
      <span style={{ position: 'absolute', left: `${lx}%`, top: `${ly / 44 * 100}%`, width: 7, height: 7, marginLeft: -3.5, marginTop: -3.5, borderRadius: '50%', background: color }} />
    </div>
  )
}

/** Gewicht zum Erfassen, neben dem Wasser: Wert oben rechts, Verlauf 14 Tage, Änderung je Woche, Eintragen unten. */
function WeightCard({ day, stats, onWeight }: { day: Day; stats: DayStat[] | null; onWeight: (w: number) => void }) {
  const s = useStyles()
  const [edit, setEdit] = useState(false)
  const [v, setV] = useState('')
  const weights = (stats ?? []).filter(d => d.weight != null && d.day <= day.date).map(d => ({ day: d.day, w: d.weight! }))
  let perWeek: number | null = null
  if (weights.length >= 2) {
    const days = (new Date(weights[weights.length - 1].day).getTime() - new Date(weights[0].day).getTime()) / 86_400_000
    if (days >= 6) perWeek = (weights[weights.length - 1].w - weights[0].w) / days * 7
  }
  const n = Number(v.replace(',', '.'))
  const ok = n >= 20 && n <= 400
  // Verlauf der letzten 14 Tage (bis zum gezeigten Tag) - statt der Flaschen beim Wasser
  const from = addDays(day.date, -13)
  const recent = weights.filter(x => x.day >= from)
  return (
    <div className={s.card} style={tint(WEIGHT)}>
      <div className={s.cardHead}>
        <Text weight="semibold" className={s.grow} style={{ color: WEIGHT }}>⚖ {t('Gewicht')}</Text>
        <Text weight="semibold" className={s.num} style={{ color: tokens.colorNeutralForeground1 }}>{day.weight ? fmt(day.weight, 1) + ' kg' : '–'}</Text>
      </div>
      <WeightLine points={recent} from={from} to={day.date} color={WEIGHT} />
      {perWeek != null && <Text size={200} style={{ color: perWeek <= 0 ? '#2E9E5B' : tokens.colorNeutralForeground3 }}>{t('{n} kg / Woche', { n: (perWeek > 0 ? '+' : '') + fmt(perWeek, 1) })}</Text>}
      <div className={s.grow} />
      {edit ? <div style={{ display: 'flex', gap: 4 }}>
        <Input size="small" value={v} onChange={(_, d) => setV(d.value)} inputMode="decimal" placeholder="kg" style={{ width: 80 }} autoFocus aria-label={t('Gewicht')}
          onKeyDown={e => { if (e.key === 'Enter' && ok) { onWeight(n); setEdit(false) } if (e.key === 'Escape') setEdit(false) }} />
        <Button size="small" disabled={!ok} onClick={() => { onWeight(n); setEdit(false) }}>{t('OK')}</Button>
      </div> : <Button size="small" style={{ borderColor: WEIGHT, color: WEIGHT }} onClick={() => { setV(''); setEdit(true) }}>{t('Eintragen')}</Button>}
    </div>
  )
}

/**
 * Eine Mahlzeit im Einzelnen, in ihrer Farbe: Ring mit Summe und Rest zum Mahlzeitenziel, Nährstoffe zum Anteil am Tagesziel,
 * die Einträge (Menge ändern, verschieben, löschen), unten „Wie gestern“ und „Mehr hinzufügen“. „Wie gestern“ zeigt erst die
 * Einträge dieser Mahlzeit von gestern, alle ausgewählt; übernommen werden nur die ausgewählten.
 */
function MealDetail({ meal, day, onClose, onAdd, onRemove, onMove, onAmount, onYesterday, onTemplate }: {
  meal: Meal; day: Day; onClose: () => void; onAdd: () => void; onRemove: (id: number) => void; onMove: (id: number, to: Meal) => void
  onAmount: (id: number, grams: number) => void; onYesterday: (ids: number[]) => void; onTemplate: () => void
}) {
  const s = useStyles()
  const c = MEAL_COLOR[meal]
  const entries = day.meals[meal] ?? []
  const kcal = entries.reduce((a, e) => a + e.kcal, 0)
  const share = MEAL_SHARE[meal]
  const goal = day.budget != null && share ? day.budget * share : null
  const hide = day.hideNumbers
  const [edit, setEdit] = useState<{ id: number; v: string } | null>(null)
  const { fail } = useApp()
  const [yesterday, setYesterday] = useState<{ entries: Entry[]; picked: Set<number> } | null>(null)
  const showYesterday = () => coachApi.day(addDays(day.date, -1))
    .then(d => { const list = d.meals[meal] ?? []; setYesterday({ entries: list, picked: new Set(list.map(e => e.id)) }) }).catch(fail)
  const toggle = (id: number, on: boolean) => setYesterday(y => {
    if (!y) return y
    const picked = new Set(y.picked)
    if (on) picked.add(id); else picked.delete(id)
    return { ...y, picked }
  })
  const detail = (e: Entry) => [e.brand, e.portion, e.amount && !/\d\s*(g|ml)\b/.test(e.portion ?? '') ? fmt(e.amount) + ' g' : null].filter(Boolean).join(' · ')
  const sum = (k: 'protein' | 'fat' | 'carbs') => entries.reduce((a, e) => a + e[k], 0)
  const len = 2 * Math.PI * 38
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
        <Text size={600} weight="semibold" style={{ color: c, flex: 1 }}>{MEAL_ICON[meal]} {mealName(meal)}</Text>
        <Menu>
          <MenuTrigger disableButtonEnhancement><Button appearance="subtle" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr zu „{meal}“', { meal: mealName(meal) })} /></MenuTrigger>
          <MenuPopover><MenuList>
            {entries.length > 0 && <MenuItem onClick={onTemplate}>{t('Als Vorlage speichern …')}</MenuItem>}
            <MenuItem onClick={showYesterday}>{t('Wie gestern')}</MenuItem>
          </MenuList></MenuPopover>
        </Menu>
        <Button appearance="subtle" onClick={onClose}>{t('Schließen')}</Button>
      </div>
      <div style={{ ...tint(c), borderRadius: 18, padding: 14, display: 'flex', alignItems: 'center', gap: 16 }}>
        <svg width="88" height="88" viewBox="0 0 88 88" aria-hidden>
          <circle cx="44" cy="44" r="38" fill={c + '22'} stroke={c + '44'} strokeWidth="7" />
          {goal && kcal > 0 && <circle cx="44" cy="44" r="38" fill="none" stroke={c} strokeWidth="7" strokeLinecap="round"
            strokeDasharray={`${Math.min(1, kcal / goal) * len} ${len}`} transform="rotate(-90 44 44)" />}
          <text x="44" y="51" textAnchor="middle" fontSize="24">{MEAL_ICON[meal]}</text>
        </svg>
        {/* Untereinander: Summe (zum Ziel), was noch geht, Zahl der Einträge */}
        <div style={{ display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: 4 }}>
          <div><Text size={700} weight="semibold">{hide ? '–' : fmt(kcal)}</Text>
            <Text className={s.muted}>{goal && !hide ? ` / ${fmt(goal)} kcal` : ' kcal'}</Text></div>
          {goal && !hide && <Text size={200} className={s.muted}>{kcal <= goal
            ? t('noch {n} kcal für diese Mahlzeit', { n: fmt(goal - kcal) }) : t('{n} kcal über dem Ziel dieser Mahlzeit', { n: fmt(kcal - goal) })}</Text>}
          {entries.length > 0 && <span style={{ background: c, color: '#fff', borderRadius: 999, padding: '2px 10px', fontSize: 12 }}>
            {entries.length === 1 ? t('1 Eintrag') : t('{n} Einträge', { n: entries.length })}</span>}
        </div>
      </div>
      {day.targets.complete && !hide && <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 8 }}>
        {([['protein', t('Eiweiß'), '#E0675B', day.targets.protein], ['fat', t('coach|Fett'), '#4FB3A0', day.targets.fat],
          ['carbs', t('Kohlenhydrate'), '#E0B14E', day.targets.carbs]] as const).map(([k, label, col, g]) => {
          const v = sum(k), goalM = g ? g * share : null
          return <div key={k} style={{ ...tint(col), borderRadius: 12, padding: '8px 10px' }}>
            <Text size={200} style={{ color: col }}>{label}</Text>
            <Text block weight="semibold">{fmt(v)}{goalM ? ' / ' + fmt(goalM) : ''} g</Text>
            <div className={s.track}><div className={s.fill} style={{ width: `${goalM ? Math.min(100, v / goalM * 100) : 0}%`, backgroundColor: col }} /></div>
          </div>
        })}
      </div>}
      {entries.length > 0 && <div className={s.card} style={{ padding: '2px 12px' }}>
        {entries.map(e => <div key={e.id} className={s.entry} style={{ cursor: 'pointer' }} onClick={() => setEdit({ id: e.id, v: e.amount ? String(Math.round(e.amount)) : '' })}>
          <span style={{ width: 36, height: 36, borderRadius: 10, background: c + '2e', display: 'inline-flex', alignItems: 'center', justifyContent: 'center', flexShrink: 0 }} aria-hidden>{MEAL_ICON[meal]}</span>
          <div className={s.grow}>
            <Text block weight="semibold" truncate wrap={false}>{e.name}</Text>
            <Text size={200} className={s.muted}>{detail(e)}</Text>
          </div>
          {!hide && <Text weight="semibold" className={s.num}>{fmt(e.kcal)} kcal</Text>}
          <span onClick={ev => ev.stopPropagation()}>
            <Menu>
              <MenuTrigger disableButtonEnhancement><Button appearance="subtle" size="small" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} /></MenuTrigger>
              <MenuPopover><MenuList>
                {MEALS.filter(m => m !== meal).map(m => <MenuItem key={m} onClick={() => onMove(e.id, m)}>{t('Nach „{meal}“', { meal: mealName(m) })}</MenuItem>)}
                <MenuItem icon={<Delete20Regular />} onClick={() => onRemove(e.id)}>{t('Löschen')}</MenuItem>
              </MenuList></MenuPopover>
            </Menu>
          </span>
        </div>)}
      </div>}
      {edit && <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
        <Text>{t('Menge')}</Text>
        <Input autoFocus value={edit.v} onFocus={ev => ev.target.select()} onChange={(_, d) => setEdit({ ...edit, v: d.value })} inputMode="decimal" contentAfter="g" style={{ width: 120 }}
          onKeyDown={ev => { const n = Number(edit.v.replace(',', '.')); if (ev.key === 'Enter' && n > 0) { onAmount(edit.id, n); setEdit(null) } if (ev.key === 'Escape') setEdit(null) }} />
        <Button appearance="primary" disabled={!(Number(edit.v.replace(',', '.')) > 0)} onClick={() => { onAmount(edit.id, Number(edit.v.replace(',', '.'))); setEdit(null) }}>{t('OK')}</Button>
        <Button appearance="subtle" onClick={() => setEdit(null)}>{t('Abbrechen')}</Button>
      </div>}
      {entries.length > 0 && <Text size={200} className={s.muted}>{t('Eintrag anklicken: Menge ändern · …: verschieben, löschen')}</Text>}
      {yesterday && <div className={s.card} style={{ padding: '8px 12px' }}>
        {yesterday.entries.length === 0 ? <Text>{t('Gestern steht bei „{meal}“ nichts.', { meal: mealName(meal) })}</Text> : <>
          <Text weight="semibold">{t('Gestern bei „{meal}“', { meal: mealName(meal) })}</Text>
          <div>
            {yesterday.entries.map(e => <div key={e.id} className={s.entry}>
              <Checkbox checked={yesterday.picked.has(e.id)} onChange={(_, d) => toggle(e.id, !!d.checked)} aria-label={e.name} />
              <div className={s.grow} style={{ cursor: 'pointer' }} onClick={() => toggle(e.id, !yesterday.picked.has(e.id))}>
                <Text block weight="semibold" truncate wrap={false}>{e.name}</Text>
                <Text size={200} className={s.muted}>{detail(e)}</Text>
              </div>
              {!hide && <Text className={s.num}>{fmt(e.kcal)} kcal</Text>}
            </div>)}
          </div>
        </>}
        <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end' }}>
          <Button appearance="subtle" onClick={() => setYesterday(null)}>{yesterday.entries.length ? t('Abbrechen') : t('Schließen')}</Button>
          {yesterday.entries.length > 0 && <Button appearance="primary" disabled={yesterday.picked.size === 0} style={{ backgroundColor: c, borderColor: c }}
            onClick={() => { onYesterday([...yesterday.picked]); setYesterday(null) }}>{t('Übernehmen')}</Button>}
        </div>
      </div>}
      <div style={{ display: 'flex', gap: 8 }}>
        <Button shape="circular" style={{ flex: 1, borderColor: c, color: c }} onClick={showYesterday}>{t('Wie gestern')}</Button>
        <Button shape="circular" appearance="primary" icon={<Add20Regular />} style={{ flex: 1.4, backgroundColor: c, borderColor: c }} onClick={onAdd}>{t('Mehr hinzufügen')}</Button>
      </div>
    </div>
  )
}

/** Wasser: Gläser zum Antippen (250 ml), Flasche (500 ml), letztes zurücknehmen. */
function WaterCard({ day, onWater, onUndo }: { day: Day; onWater: (ml: number) => void; onUndo: (id: number) => void }) {
  const s = useStyles()
  const { ml, goal, entries } = day.water
  // Eigene Gläser und Flaschen (Profil) werden Knöpfe; angezeigt wird in 1-l-Flaschen, die sich füllen - über dem Ziel grün
  const sizes = day.water.sizes?.length ? day.water.sizes : [250, 500]
  const count = Math.min(12, Math.max(1, Math.ceil(goal / 1000), Math.ceil(ml / 1000)))
  const blue = tokens.colorPaletteBlueBorderActive
  const green = tokens.colorPaletteGreenBorderActive
  const pct = (v: number) => Math.round(Math.max(0, Math.min(1, v)) * 100)
  return (
    <div className={s.card} style={tint(WATER)}>
      <div className={s.cardHead}>
        <Text weight="semibold" className={s.grow} style={{ color: WATER }}>💧 {t('Wasser')}</Text>
        <Text weight="semibold" className={s.num} style={{ color: tokens.colorNeutralForeground1 }}>{fmt(ml / 1000, 2)} / {fmt(goal / 1000, 2)} l</Text>
      </div>
      <div className={s.glasses}>
        {Array.from({ length: count }, (_, i) => {
          // Füllstand dieser Flasche; bis zum Ziel blau, darüber grün
          const fill = pct((ml - i * 1000) / 1000)
          const upTo = Math.min(fill, pct((goal - i * 1000) / 1000))
          const edge = i * 1000 < goal ? blue : green
          const level = `linear-gradient(to top, ${blue} 0 ${upTo}%, ${green} ${upTo}% ${fill}%, transparent ${fill}%)`
          return (
            <span key={i} role="img" aria-label={t('Flasche {n}', { n: i + 1 })} style={{ width: 24, height: 44, display: 'inline-flex', flexDirection: 'column', alignItems: 'center' }}>
              <span style={{ width: 9, height: 7, border: `1.5px solid ${edge}`, borderBottom: 'none', borderRadius: '2px 2px 0 0' }} />
              <span style={{ width: 22, height: 35, border: `1.5px solid ${edge}`, borderRadius: '7px 7px 4px 4px', background: level }} />
            </span>
          )
        })}
      </div>
      {!!day.water.watch && <Text size={200} className={s.muted}>{t('davon {l} l von der Uhr', { l: fmt(day.water.watch / 1000, 2) })}</Text>}
      {/* Knöpfe an der Unterkante, wie beim Gewicht */}
      <div className={s.grow} />
      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap' }}>
        {sizes.map(v => <Button key={v} size="small" onClick={() => onWater(v)}>+ {fmt(v / 1000, 2)} l</Button>)}
        {entries.length > 0 && <Button size="small" appearance="subtle" onClick={() => onUndo(entries[entries.length - 1].id)}>{t('Letztes zurücknehmen')}</Button>}
      </div>
    </div>
  )
}

function StepsCard({ day, onSteps }: { day: Day; onSteps: (n: number) => void }) {
  const s = useStyles()
  const [edit, setEdit] = useState(false)
  const [v, setV] = useState('')
  const steps = day.steps.steps ?? 0
  // Ziel erreicht: Balken dunkelgrün, dazu der Überschuss
  const reached = day.steps.goal > 0 && steps >= day.steps.goal
  return (
    <div className={s.card} style={tint(RING_MOVE)}>
      <div className={s.cardHead}>
        <Text weight="semibold" className={s.grow} style={{ color: RING_MOVE }}>👟 {t('Schritte')}</Text>
        {reached && <Text weight="semibold" style={{ color: STEPS_EXTRA, marginRight: 8 }}>✓ +{fmt(steps - day.steps.goal)}</Text>}
        <Text className={s.num}>{fmt(steps)} / {fmt(day.steps.goal)}</Text>
      </div>
      <StepsBar steps={steps} goal={day.steps.goal} height={8} color={tokens.colorPaletteGreenForeground2} />
      <Text size={200} className={s.muted}>
        {day.steps.mode === 'dynamic' ? t('Dynamisches Ziel') : t('Festes Ziel')}
        {day.activity?.source && day.activity.source !== 'manual' ? ' · ' + t('von der Uhr') : ''}
        {day.activity?.distance ? ' · ' + fmt(day.activity.distance / 1000, 1) + ' km' : ''}
        {day.activity?.activeKcal ? ' · ' + t('{n} kcal aktiv', { n: fmt(day.activity.activeKcal) }) : ''}
      </Text>
      {edit ? <div style={{ display: 'flex', gap: 6 }}>
        <Input value={v} onChange={(_, d) => setV(d.value.replace(/\D/g, ''))} inputMode="numeric" style={{ width: 120 }} aria-label={t('Schritte')} />
        <Button size="small" appearance="primary" disabled={!v} onClick={() => { onSteps(Number(v)); setEdit(false) }}>{t('Speichern')}</Button>
        <Button size="small" onClick={() => setEdit(false)}>{t('Abbrechen')}</Button>
      </div> : !day.activity?.source || day.activity.source === 'manual'
        ? <div><Button size="small" appearance="subtle" onClick={() => { setV(String(day.steps.steps ?? '')); setEdit(true) }}>{t('Von Hand eintragen')}</Button>
            <Text size={200} className={s.muted}> {t('oder in der Handy-App mit Health Connect verbinden')}</Text></div>
        : null}
    </div>
  )
}

