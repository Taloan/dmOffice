import {
  Button, Dropdown, Field, Input, MessageBar, MessageBarBody, Option, Spinner, Switch, Text, ToggleButton, makeStyles, tokens,
} from '@fluentui/react-components'
import { Delete20Regular, Play20Regular, Stop20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { Ring } from './Charts'

/** Coach Stufe 2: Fasten-Tracker (docs/dmcoach-konzept.md) - /api/coach/fasting. */

type Plan = '16:8' | '14:10' | '18:6' | '5:2' | 'custom'
interface Settings { enabled: boolean; plan: Plan; customHours: number; eatFrom: string; fastDays: number[]; remind: boolean }
interface Fast { id: number; start: string; end: string | null; goal: number; hours: number; reached: boolean }
interface View {
  settings: Settings; notAllowed: string | null; goalHours: number; plannedStart: string | null
  current: { id: number; start: string; goal: number; hours: number; ends: string } | null
  history: Fast[]
  stats: { count: number; reached: number; avgHours: number | null; streak: number }
  lowDay: { fastDay: boolean; kcal: number } | null
}

const PLANS: { key: Plan; label: () => string }[] = [
  { key: '16:8', label: () => t('16:8 – 16 Stunden fasten, 8 Stunden essen') },
  { key: '14:10', label: () => t('14:10 – sanfter Einstieg') },
  { key: '18:6', label: () => t('18:6 – länger fasten') },
  { key: '5:2', label: () => t('5:2 – zwei Tage pro Woche wenig essen') },
  { key: 'custom', label: () => t('Eigener Plan') },
]

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'minmax(260px, 360px) minmax(0, 1fr)', gap: '16px', alignItems: 'start', '@media (max-width: 800px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  card: { backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusLarge, padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: '10px' },
  center: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '8px' },
  row: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  entry: { display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 0', borderTop: `1px solid ${tokens.colorNeutralStroke3}`, fontVariantNumeric: 'tabular-nums' },
})

const hm = (h: number) => { const m = Math.round(h * 60); return `${Math.floor(m / 60)}:${String(m % 60).padStart(2, '0')}` }
const time = (iso: string) => new Date(iso).toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })
const dayTime = (iso: string) => new Date(iso).toLocaleString(locale, { weekday: 'short', day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' })
/** Uhrzeit (HH:mm) heute bzw. gestern als ISO - zum Nachtragen von Beginn und Ende. */
const at = (hhmm: string) => {
  const [h, m] = hhmm.split(':').map(Number)
  const d = new Date(); d.setHours(h, m, 0, 0)
  if (d.getTime() > Date.now()) d.setDate(d.getDate() - 1)
  return d.toISOString()
}

export function Fasting() {
  const s = useStyles()
  const { fail } = useApp()
  const [v, setV] = useState<View | null>(null)
  const [later, setLater] = useState('')
  const [, tick] = useState(0)
  const load = useCallback(() => api.get<View>('/api/coach/fasting').then(setV).catch(fail), [fail])
  useEffect(() => { load() }, [load])
  // Läuft ein Fasten: jede Minute neu zeichnen (die Zeit läuft im Browser weiter)
  useEffect(() => { if (!v?.current) return; const id = window.setInterval(() => tick(x => x + 1), 30_000); return () => window.clearInterval(id) }, [v?.current])
  if (!v) return <Spinner />

  const save = (patch: Partial<Settings>) => api.put<View>('/api/coach/fasting', { ...v.settings, ...patch }).then(setV).catch(fail)
  const call = (path: string, body: object = {}) => api.post<View>('/api/coach/fasting/' + path, body).then(r => { setV(r); setLater('') }).catch(fail)
  const remove = (id: number) => api.del<View>(`/api/coach/fasting/${id}`).then(setV).catch(fail)
  const st = v.settings
  const timer = st.plan !== '5:2'
  const cur = v.current
  const hours = cur ? (Date.now() - new Date(cur.start).getTime()) / 3_600_000 : 0
  const days = Array.from({ length: 7 }, (_, i) => (i + 1) % 7)   // Montag zuerst

  return (
    <div className={s.grid}>
      <div className={s.card}>
        <Switch checked={st.enabled} disabled={!!v.notAllowed && !st.enabled} label={t('Fasten einschalten')} onChange={(_, d) => save({ enabled: d.checked })} />
        {v.notAllowed && <MessageBar intent="info"><MessageBarBody>{t(v.notAllowed)}</MessageBarBody></MessageBar>}
        {st.enabled && <>
          <Field label={t('Plan')}>
            <Dropdown value={PLANS.find(p => p.key === st.plan)?.label()} selectedOptions={[st.plan]} onOptionSelect={(_, d) => save({ plan: d.optionValue as Plan })}>
              {PLANS.map(p => <Option key={p.key} value={p.key}>{p.label()}</Option>)}
            </Dropdown>
          </Field>
          {st.plan === 'custom' && <Field label={t('Fastenstunden (12 bis 23)')}>
            <Input type="number" min={12} max={23} value={String(st.customHours)} onChange={(_, d) => { const n = Number(d.value); if (n >= 12 && n <= 23) save({ customHours: n }) }} />
          </Field>}
          {timer && <Field label={t('Essensfenster beginnt um')} hint={t('Das Fasten beginnt nach Plan um {at}.', { at: v.plannedStart ? time(v.plannedStart) : '–' })}>
            <Input type="time" value={st.eatFrom} onChange={(_, d) => { if (/^\d\d:\d\d$/.test(d.value)) save({ eatFrom: d.value }) }} />
          </Field>}
          {st.plan === '5:2' && <Field label={t('Fastentage')}>
            <div className={s.row}>
              {days.map(d => <ToggleButton key={d} size="small" checked={st.fastDays.includes(d)}
                onClick={() => save({ fastDays: st.fastDays.includes(d) ? st.fastDays.filter(x => x !== d) : [...st.fastDays, d] })}>
                {new Date(2024, 0, 7 + d).toLocaleDateString(locale, { weekday: 'short' })}
              </ToggleButton>)}
            </div>
          </Field>}
          <Switch checked={st.remind} label={t('Pukki erinnert an Beginn und Ziel')} onChange={(_, d) => save({ remind: d.checked })} />
          <Text size={200} className={s.muted}>{t('Fasten ist nicht für Schwangere, Stillende, unter 18-Jährige und bei Untergewicht gedacht. Bei Diabetes, Essstörungen oder anderen Erkrankungen vorher mit einer Ärztin oder einem Arzt sprechen.')}</Text>
        </>}
      </div>

      {st.enabled && !v.notAllowed && <div style={{ display: 'flex', flexDirection: 'column', gap: 16, minWidth: 0 }}>
        {timer ? <div className={s.card}>
          <div className={s.center}>
            <Ring size={180} value={cur ? hours : 0} max={cur?.goal ?? v.goalHours}
              label={cur ? hm(hours) : hm(0)} sub={cur ? t('von {h} Stunden', { h: cur.goal }) : t('nicht gestartet')} />
            {cur ? <>
              <Text>{t('Seit {at}', { at: dayTime(cur.start) })} · {hours >= cur.goal ? t('Ziel erreicht') : t('Ziel um {at}', { at: time(cur.ends) })}</Text>
              <Button appearance="primary" icon={<Stop20Regular />} onClick={() => call('stop', later ? { at: at(later) } : {})}>{t('Fasten beenden')}</Button>
            </> : <Button appearance="primary" icon={<Play20Regular />} onClick={() => call('start', later ? { at: at(later) } : {})}>{t('Fasten beginnen')}</Button>}
            <div className={s.row}>
              <Text size={200} className={s.muted}>{cur ? t('Ende nachtragen:') : t('Beginn nachtragen:')}</Text>
              <Input size="small" type="time" value={later} onChange={(_, d) => setLater(d.value)} style={{ width: 110 }} />
            </div>
          </div>
        </div> : v.lowDay && <div className={s.card}>
          <Text weight="semibold">{v.lowDay.fastDay ? t('Heute ist Fastentag') : t('Heute ist kein Fastentag')}</Text>
          <Text>{v.lowDay.fastDay ? t('Ziel heute: höchstens {kcal} kcal, viel trinken.', { kcal: v.lowDay.kcal }) : t('Normal essen nach deinem Kalorienziel.')}</Text>
        </div>}

        {timer && <div className={s.card}>
          <div className={s.row}>
            <Text weight="semibold" style={{ flex: 1 }}>{t('Letzte 30 Tage')}</Text>
            <Text className={s.muted}>{t('{n} gefastet, {r} mit Ziel', { n: v.stats.count, r: v.stats.reached })}{v.stats.avgHours != null ? ' · ' + t('Ø {h} Std.', { h: v.stats.avgHours }) : ''}{v.stats.streak > 1 ? ' · ' + t('{n} Tage in Folge', { n: v.stats.streak }) : ''}</Text>
          </div>
          {v.history.length === 0 && <Text className={s.muted}>{t('Noch kein Fasten beendet.')}</Text>}
          {v.history.map(f => <div key={f.id} className={s.entry}>
            <span style={{ flex: 1 }}>{dayTime(f.start)} – {f.end ? time(f.end) : '…'}</span>
            <span>{hm(f.hours)}</span>
            <span style={{ width: 22 }}>{f.reached ? '✓' : ''}</span>
            <Button appearance="subtle" size="small" icon={<Delete20Regular />} aria-label={t('Löschen')} onClick={() => remove(f.id)} />
          </div>)}
        </div>}
      </div>}
    </div>
  )
}
