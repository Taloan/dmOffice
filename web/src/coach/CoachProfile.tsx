import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, MessageBar, MessageBarBody,
  Option, Radio, RadioGroup, Spinner, Switch, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { useApp } from '../App'
import { t } from '../i18n'
import { coachApi, Diet, fmt, Profile, Targets } from './api'

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(260px, 360px)', gap: '20px', alignItems: 'start', '@media (max-width: 900px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  form: { display: 'flex', flexDirection: 'column', gap: '14px', maxWidth: '640px' },
  // Eingabefelder dürfen schmaler werden als ihre Vorgabe, sonst laufen sie über die Spalte
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(150px, 1fr))', gap: '10px', '& > *': { minWidth: 0 }, '& .fui-Input': { minWidth: 0, width: '100%' } },
  section: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold, marginTop: '8px' },
  checks: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))', gap: '2px' },
  panel: { backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusLarge, padding: '14px', display: 'flex', flexDirection: 'column', gap: '8px', position: 'sticky', top: 0 },
  line: { display: 'flex', justifyContent: 'space-between', gap: '8px', fontVariantNumeric: 'tabular-nums' },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
})

const PAL: [number, string][] = [
  [1.2, 'Fast nur sitzend oder liegend'], [1.4, 'Überwiegend sitzend, wenig Bewegung'], [1.6, 'Sitzend, zeitweise gehend oder stehend'],
  [1.8, 'Überwiegend gehend oder stehend'], [2.0, 'Körperlich schwere Arbeit oder viel Sport'],
]
const DIETS: [Diet, string][] = [
  ['dge', 'Mischkost (DGE)'], ['med', 'Mediterran'], ['vegetarian', 'Vegetarisch'], ['vegan', 'Vegan'], ['protein', 'Eiweißreich'], ['lowcarb', 'Low Carb'], ['keto', 'Keto'],
]
const ALLERGENS: [string, string][] = [
  ['gluten', 'Glutenhaltiges Getreide'], ['crustaceans', 'Krebstiere'], ['eggs', 'Eier'], ['fish', 'Fisch'], ['peanuts', 'Erdnüsse'], ['soy', 'Soja'],
  ['milk', 'Milch'], ['nuts', 'Schalenfrüchte (Nüsse)'], ['celery', 'Sellerie'], ['mustard', 'Senf'], ['sesame', 'Sesam'], ['sulphites', 'Sulfite'],
  ['lupin', 'Lupinen'], ['molluscs', 'Weichtiere'],
]
const INTOLERANCES: [string, string][] = [['lactose', 'Laktose'], ['fructose', 'Fruktose'], ['histamine', 'Histamin'], ['gluten', 'Gluten (Zöliakie)']]

const SIZES = [200, 250, 300, 330, 500, 600, 750, 1000, 1500]
const liters = (ml: number) => fmt(ml / 1000, 2) + ' l'

/**
 * Eigene Gläser und Flaschen (höchstens vier): jede wird ein „+“-Knopf der Wasser-Karte, die erste bestimmt die Symbole
 * (ab 0,5 l Flaschen). Wer nur beim Anbrechen einer 1-l-Flasche einträgt, wählt nur sie.
 */
function WaterSizes({ sizes, onChange }: { sizes: number[]; onChange: (s: number[]) => void }) {
  const [own, setOwn] = useState('')
  const add = (ml: number) => { if (!sizes.includes(ml) && sizes.length < 4) onChange([...sizes, ml]) }
  const n = Math.round(Number(own.replace(',', '.')) * (own.includes(',') || own.includes('.') || Number(own) < 10 ? 1000 : 1))
  return (
    <Field label={t('Deine Gläser und Flaschen')} hint={t('Jede wird ein „+“-Knopf beim Wasser, die erste bestimmt die Symbole (ab 0,5 l als Flaschen). Höchstens vier.')}>
      <div style={{ display: 'flex', gap: 6, flexWrap: 'wrap', alignItems: 'center' }}>
        {sizes.map((v, i) => (
          <Button key={v} size="small" appearance={i === 0 ? 'primary' : 'secondary'} disabled={sizes.length === 1}
            title={sizes.length > 1 ? t('Entfernen') : undefined} onClick={() => onChange(sizes.filter(x => x !== v))}>{liters(v)}{sizes.length > 1 ? ' ✕' : ''}</Button>
        ))}
        {sizes.length > 1 && <Button size="small" appearance="subtle" onClick={() => onChange([...sizes.slice(1), sizes[0]])} title={t('Ein anderes Gefäß für die Symbole')}>{t('Reihenfolge ändern')}</Button>}
      </div>
      {sizes.length < 4 && <div style={{ display: 'flex', gap: 4, flexWrap: 'wrap', alignItems: 'center', marginTop: 6 }}>
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Dazu:')}</Text>
        {SIZES.filter(v => !sizes.includes(v)).map(v => <Button key={v} size="small" appearance="subtle" onClick={() => add(v)}>+ {liters(v)}</Button>)}
        <Input size="small" value={own} onChange={(_, d) => setOwn(d.value)} placeholder={t('eigene, z. B. 0,4')} contentAfter="l" style={{ width: 120 }}
          onKeyDown={e => { if (e.key === 'Enter' && n >= 50 && n <= 3000) { add(n); setOwn('') } }} />
        {own && <Button size="small" disabled={!(n >= 50 && n <= 3000)} onClick={() => { add(n); setOwn('') }}>{t('Hinzufügen')}</Button>}
      </div>}
    </Field>
  )
}

/** Profil und Ziele: Körperdaten, Ziel, Ernährungsform, Allergien; rechts der Rechenweg mit Hinweisen und Quellen. */
export function CoachProfileView() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [p, setP] = useState<Profile | null>(null)
  const [targets, setTargets] = useState<Targets | null>(null)
  const [sources, setSources] = useState<string[]>([])
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  const [confirm, setConfirm] = useState(false)
  useEffect(() => { coachApi.profile().then(r => { setP(r.profile); setTargets(r.targets); setSources(r.sources) }).catch(fail) }, [fail])
  if (!p) return <Spinner />
  const up = (patch: Partial<Profile>) => { setP({ ...p, ...patch }); setDirty(true) }
  const num = (v: string) => { const n = Number(v.replace(',', '.')); return v.trim() && Number.isFinite(n) ? n : null }
  const toggle = (list: string[], k: string, on: boolean) => on ? [...list.filter(x => x !== k), k] : list.filter(x => x !== k)
  const save = async () => {
    setBusy(true)
    try { const r = await coachApi.saveProfile(p); setP(r.profile); setTargets(r.targets); setDirty(false); notify(t('Gespeichert.')) }
    catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <div className={s.grid}>
      <div className={s.form}>
        <Text className={s.section}>{t('Über dich')}</Text>
        <div className={s.row}>
          <Field label={t('Geburtsdatum')}><Input type="date" value={p.birth ?? ''} onChange={(_, d) => up({ birth: d.value || null })} /></Field>
          <Field label={t('coach|Größe')}><Input value={p.heightCm?.toString() ?? ''} onChange={(_, d) => up({ heightCm: num(d.value) })} inputMode="decimal" contentAfter="cm" /></Field>
          <Field label={t('Gewicht')} hint={t('Danach zählt das zuletzt im Tagebuch eingetragene')}><Input value={p.weightKg?.toString() ?? ''} onChange={(_, d) => up({ weightKg: num(d.value) })} inputMode="decimal" contentAfter="kg" /></Field>
        </div>
        <Field label={t('Geschlecht')}>
          <RadioGroup layout="horizontal" value={p.sex ?? ''} onChange={(_, d) => up({ sex: d.value as Profile['sex'] })}>
            <Radio value="f" label={t('weiblich')} /><Radio value="m" label={t('männlich')} /><Radio value="d" label={t('divers')} />
          </RadioGroup>
        </Field>
        <Field label={t('Alltag')}>
          <Dropdown value={t(PAL.find(x => x[0] === p.pal)?.[1] ?? PAL[1][1])} selectedOptions={[String(p.pal)]} onOptionSelect={(_, d) => up({ pal: Number(d.optionValue) })}>
            {PAL.map(([v, l]) => <Option key={v} value={String(v)} text={t(l)}>{t(l)}</Option>)}
          </Dropdown>
        </Field>
        {p.sex !== 'm' && <Switch checked={p.pregnant} onChange={(_, d) => up({ pregnant: d.checked })} label={t('Schwanger oder stillend')} />}

        <Text className={s.section}>{t('coach|Ziel')}</Text>
        <RadioGroup layout="horizontal" value={p.goal} onChange={(_, d) => up({ goal: d.value as Profile['goal'] })}>
          <Radio value="keep" label={t('Gewicht halten')} /><Radio value="lose" label={t('Abnehmen')} /><Radio value="gain" label={t('Zunehmen')} /><Radio value="muscle" label={t('Muskeln aufbauen')} />
        </RadioGroup>
        {(p.goal === 'lose' || p.goal === 'gain') && <Field label={t('Tempo')}>
          <RadioGroup layout="horizontal" value={String(p.pace)} onChange={(_, d) => up({ pace: Number(d.value) })}>
            <Radio value="0.25" label={t('gemächlich (0,25 kg/Woche)')} /><Radio value="0.5" label={t('normal (0,5 kg/Woche)')} /><Radio value="0.75" label={t('zügig (0,75 kg/Woche)')} />
          </RadioGroup>
        </Field>}

        <Text className={s.section}>{t('Ernährung')}</Text>
        <Field label={t('Ernährungsform')}>
          <Dropdown value={t(DIETS.find(x => x[0] === p.diet)?.[1] ?? '')} selectedOptions={[p.diet]} onOptionSelect={(_, d) => up({ diet: d.optionValue as Diet })}>
            {DIETS.map(([v, l]) => <Option key={v} value={v} text={t(l)}>{t(l)}</Option>)}
          </Dropdown>
        </Field>
        <Field label={t('Allergien')} hint={t('Werden nie vorgeschlagen (Rezepte und Pläne kommen in der nächsten Stufe).')}>
          <div className={s.checks}>{ALLERGENS.map(([k, l]) => <Checkbox key={k} label={t(l)} checked={p.allergens.includes(k)} onChange={(_, d) => up({ allergens: toggle(p.allergens, k, !!d.checked) })} />)}</div>
        </Field>
        <Field label={t('Unverträglichkeiten')}>
          <div className={s.checks}>{INTOLERANCES.map(([k, l]) => <Checkbox key={k} label={t(l)} checked={p.intolerances.includes(k)} onChange={(_, d) => up({ intolerances: toggle(p.intolerances, k, !!d.checked) })} />)}</div>
        </Field>
        <Field label={t('Mag ich nicht')} hint={t('Durch Kommas getrennt, z. B. Rosenkohl, Leber')}>
          <Input value={p.dislikes.join(', ')} onChange={(_, d) => up({ dislikes: d.value.split(',').map(x => x.trimStart()) })} onBlur={() => up({ dislikes: p.dislikes.map(x => x.trim()).filter(Boolean) })} />
        </Field>

        <Text className={s.section}>{t('Bewegung und Trinken')}</Text>
        <Field label={t('Schrittziel')}>
          <RadioGroup value={p.stepMode} onChange={(_, d) => up({ stepMode: d.value as Profile['stepMode'] })}>
            <Radio value="fixed" label={t('Fest')} />
            <Radio value="dynamic" label={t('Dynamisch: beginnt bei 10.000 und passt sich jeden Tag um höchstens 10 % an den Durchschnitt der letzten drei Tage an')} />
          </RadioGroup>
        </Field>
        {p.stepMode === 'fixed' && <Field label={t('Schritte am Tag')} hint={t('Studien zeigen den größten Nutzen bis etwa 7.000 Schritte.')}>
          <Input value={String(p.stepGoal)} onChange={(_, d) => up({ stepGoal: Number(d.value.replace(/\D/g, '')) || 0 })} inputMode="numeric" style={{ maxWidth: 160 }} />
        </Field>}
        <Field label={t('Trinkziel')} hint={t('Leer lassen: wird aus dem Gewicht berechnet, mindestens 1,5 l')}>
          <Input value={p.waterMl?.toString() ?? ''} onChange={(_, d) => up({ waterMl: num(d.value) })} inputMode="numeric" contentAfter="ml" style={{ maxWidth: 160 }} />
        </Field>
        <WaterSizes sizes={p.waterSizes?.length ? p.waterSizes : [250, 500]} onChange={waterSizes => up({ waterSizes })} />
        <Switch checked={p.hideNumbers} onChange={(_, d) => up({ hideNumbers: d.checked })} label={t('Kalorienzahlen ausblenden (nur Anteile zeigen)')} />
        <Field hint={t('Pukki schreibt dir im Chat – nie in deiner Ruhezeit, ohne Druck. Bewegung: nur mit frischen Schritten von Uhr oder Handy, mit Wetter, Sonnenuntergang und deinen Terminen.')}>
          <Switch checked={p.nudges} onChange={(_, d) => up({ nudges: d.checked })} label={t('Pukki als Coach: an Trinken und Bewegung erinnern')} />
        </Field>
        {p.nudges && <div style={{ display: 'flex', flexDirection: 'column', marginLeft: 24 }}>
          <Switch checked={p.nudgeWater} onChange={(_, d) => up({ nudgeWater: d.checked })} label={t('Ans Trinken, wenn ich hinter dem Tagesverlauf liege')} />
          <Switch checked={p.nudgeMove} onChange={(_, d) => up({ nudgeMove: d.checked })} label={t('An Bewegung, wenn mir am Nachmittag noch viele Schritte fehlen')} />
          <Switch checked={p.nudgeWeek} onChange={(_, d) => up({ nudgeWeek: d.checked })} label={t('Sonntags um 18 Uhr ein Wochenrückblick')} />
        </div>}
        <Field hint={t('Pukki beantwortet Fragen zu Ernährung und Gesundheit mit dem Wissen des Coach. Ist bei dir eine KI eingerichtet, bekommt sie dieses Wissen mit – deine eigenen Werte (Ziele, heute Gegessenes) aber nur, wenn du das hier erlaubst.')}>
          <Switch checked={p.aiData} onChange={(_, d) => up({ aiData: d.checked })} label={t('Pukkis KI darf meine Coach-Werte verwenden')} />
        </Field>

        <div style={{ display: 'flex', gap: 8, marginTop: 8 }}>
          <Button appearance="primary" disabled={!dirty || busy} onClick={save}>{t('Speichern')}</Button>
          <Button appearance="subtle" onClick={() => setConfirm(true)}>{t('Alle Coach-Daten löschen …')}</Button>
        </div>
      </div>

      {targets && <div className={s.panel}>
        <Text weight="semibold" size={400}>{t('Deine Tagesziele')}</Text>
        {targets.complete ? <>
          <div className={s.line}><span>{t('Grundumsatz')}</span><span>{fmt(targets.bmr)} kcal</span></div>
          <div className={s.line}><span>{t('Gesamtbedarf')}</span><span>{fmt(targets.tdee)} kcal</span></div>
          <div className={s.line}><b>{t('coach|Ziel')}</b><b>{fmt(targets.kcal)} kcal</b></div>
          <div className={s.line}><span>{t('Eiweiß')}</span><span>{fmt(targets.protein)} g</span></div>
          <div className={s.line}><span>{t('Kohlenhydrate')}</span><span>{fmt(targets.carbs)} g</span></div>
          <div className={s.line}><span>{t('coach|Fett')}</span><span>{fmt(targets.fat)} g</span></div>
          <div className={s.line}><span>{t('Ballaststoffe')}</span><span>{t('mindestens {n} g', { n: targets.fiber })}</span></div>
        </> : null}
        <div className={s.line}><span>{t('Trinken')}</span><span>{fmt(targets.waterMl / 1000, 2)} l</span></div>
        {targets.bmi != null && <div className={s.line}><span>BMI</span><span>{fmt(targets.bmi, 1)}</span></div>}
        {targets.notes.map(n => <MessageBar key={n} intent="info"><MessageBarBody>{n}</MessageBarBody></MessageBar>)}
        {dirty && <Text className={s.muted}>{t('Nach dem Speichern neu berechnet.')}</Text>}
        <Text className={s.muted}>
          {t('Berechnet nach Mifflin-St Jeor mit dem Aktivitätsfaktor der DGE. Das Ziel liegt nie unter deinem Grundumsatz, abnehmen höchstens 1 % des Gewichts pro Woche. Coach ersetzt keine ärztliche oder Ernährungsberatung – bei Erkrankungen, Essstörungen, in Schwangerschaft und Stillzeit bitte fachlich beraten lassen.')}
        </Text>
        <Text className={s.muted}>{t('Quellen')}: {sources.join(' · ')}</Text>
      </div>}

      {confirm && <Dialog open onOpenChange={(_, d) => { if (!d.open) setConfirm(false) }}>
        <DialogSurface style={{ maxWidth: 440 }}>
          <DialogBody>
            <DialogTitle>{t('Alle Coach-Daten löschen?')}</DialogTitle>
            <DialogContent>{t('Profil, Tagebuch, Wasser, Gewicht, Schritte und die Garmin-Verbindung werden endgültig gelöscht. Eigene Lebensmittel des Haushalts bleiben.')}</DialogContent>
            <DialogActions>
              <Button appearance="primary" onClick={async () => {
                setConfirm(false)
                try { await coachApi.deleteAll(); const r = await coachApi.profile(); setP(r.profile); setTargets(r.targets); setDirty(false); notify(t('Gelöscht.')) } catch (e) { fail(e) }
              }}>{t('Löschen')}</Button>
              <Button onClick={() => setConfirm(false)}>{t('Abbrechen')}</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
    </div>
  )
}
