import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger,
  MessageBar, MessageBarBody, SearchBox, Spinner, Switch, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, AddCircle24Regular, ArrowLeft20Regular, BarcodeScanner24Regular, Checkmark20Regular, ChevronDown20Regular, Dismiss20Regular, Flash24Regular,
  Image24Regular, TextT24Regular,
} from '@fluentui/react-icons'
import { useEffect, useMemo, useRef, useState } from 'react'
import { ApiError } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { useDebounced } from '../shell/hooks'
import { coachApi, fmt, Food, Hit, Meal, MEAL_COLOR, MEALS, mealName, Nutrients, Planned, Portion, Template, Targets } from './api'
import { Describe } from './Describe'
import { PhotoFood } from './PhotoFood'
import { OffBadges, ScoreBar } from './Scores'
import { FoodFacts, FoodWarnings } from './FoodFacts'

const useStyles = makeStyles({
  head: { display: 'flex', alignItems: 'center', gap: '2px' },
  meal: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  search: { width: '100%', maxWidth: 'none', height: '44px', '::after': { display: 'none' } },
  tiles: { display: 'flex', gap: '8px', margin: '12px 0' },
  tile: {
    flex: '1 1 0', minWidth: 0, padding: 0, border: 'none', background: 'none', cursor: 'pointer', color: tokens.colorNeutralForeground1,
    fontFamily: 'inherit', fontSize: tokens.fontSizeBase200, textAlign: 'center',
  },
  tileBox: {
    display: 'flex', alignItems: 'center', justifyContent: 'center', height: '50px', marginBottom: '4px', borderRadius: '14px',
    border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground3,
  },
  // Gleich hoch, egal welche Kachel gewählt ist: der Dialog springt nicht
  pane: { minHeight: '50vh' },
  filters: { display: 'flex', gap: '6px', flexWrap: 'wrap', marginBottom: '4px' },
  filter: {
    padding: '5px 12px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke1}`, cursor: 'pointer',
    backgroundColor: 'transparent', color: tokens.colorNeutralForeground1, fontFamily: 'inherit', fontSize: tokens.fontSizeBase300,
  },
  list: { display: 'flex', flexDirection: 'column', maxHeight: '44vh', overflowY: 'auto' },
  hit: {
    position: 'relative', overflow: 'hidden', isolation: 'isolate', flexShrink: 0,
    display: 'flex', alignItems: 'center', gap: '10px', padding: '10px 6px', cursor: 'pointer',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  // Eingetragen: ein Lichtstreif wischt vom Plus nach links über die Zeile, das Häkchen springt auf, die kcal steigen auf
  sweep: {
    position: 'absolute', top: 0, bottom: 0, left: 0, width: '100%', zIndex: -1, pointerEvents: 'none',
    backgroundImage: `linear-gradient(90deg, transparent 0%, ${tokens.colorPaletteGreenBackground2} 40%, ${tokens.colorBrandBackground2} 60%, transparent 100%)`,
    animationName: { from: { transform: 'translateX(100%)', opacity: 1 }, '70%': { opacity: 1 }, to: { transform: 'translateX(-100%)', opacity: 0 } },
    animationDuration: '750ms', animationTimingFunction: 'cubic-bezier(.3,0,.2,1)', animationFillMode: 'forwards',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none', opacity: 0 },
  },
  pop: {
    animationName: { '0%': { transform: 'scale(0.3) rotate(-30deg)' }, '60%': { transform: 'scale(1.4) rotate(8deg)' }, '100%': { transform: 'scale(1) rotate(0)' } },
    animationDuration: '450ms', animationTimingFunction: 'ease-out',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none' },
  },
  rise: {
    position: 'absolute', right: '40px', top: '50%', pointerEvents: 'none', fontWeight: tokens.fontWeightSemibold, fontSize: tokens.fontSizeBase200,
    color: tokens.colorPaletteGreenForeground1, opacity: 0, whiteSpace: 'nowrap',
    animationName: { '0%': { opacity: 0, transform: 'translateY(-50%)' }, '25%': { opacity: 1 }, '100%': { opacity: 0, transform: 'translateY(-160%)' } },
    animationDuration: '1100ms', animationTimingFunction: 'ease-out', animationFillMode: 'forwards',
    '@media (prefers-reduced-motion: reduce)': { animationName: 'none' },
  },
  hitBody: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  kcal: { fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap', fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold },
  kcalUnit: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightRegular },
  count: { width: '42px', height: '42px', borderRadius: '50%', flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', fontWeight: tokens.fontWeightSemibold },
  chips: { display: 'flex', flexWrap: 'wrap', gap: '6px', margin: '8px 0' },
  chip: {
    padding: '4px 10px', borderRadius: '999px', border: `1px solid ${tokens.colorNeutralStroke1}`, cursor: 'pointer', fontSize: tokens.fontSizeBase200,
    backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1,
  },
  chipOn: { border: `1px solid ${tokens.colorBrandStroke1}`, color: tokens.colorBrandForeground1, fontWeight: tokens.fontWeightSemibold },
  row: { display: 'flex', gap: '8px', alignItems: 'flex-end', flexWrap: 'wrap' },
  macros: { display: 'flex', gap: '14px', flexWrap: 'wrap', fontVariantNumeric: 'tabular-nums', marginTop: '6px' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(130px, 1fr))', gap: '8px', '& > *': { minWidth: 0 }, '& .fui-Input': { minWidth: 0, width: '100%' } },
  video: { width: '100%', maxHeight: '45vh', borderRadius: tokens.borderRadiusMedium, backgroundColor: '#000' },
  thumb: { width: '40px', height: '40px', objectFit: 'contain', borderRadius: tokens.borderRadiusSmall, backgroundColor: '#fff', flexShrink: 0 },
  badge: { fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: '4px', padding: '0 4px' },
})

const ask = (m: Meal) => m === 'breakfast' ? t('Was gab es zum Frühstück?') : m === 'lunch' ? t('Was gab es zum Mittagessen?') : m === 'dinner' ? t('Was gab es zum Abendessen?') : t('Was gab es zwischendurch?')
const sourceName = (s: string) => s === 'bls' ? 'BLS' : s === 'off' ? 'Open Food Facts' : s === 'own' ? t('Haushalt') : s === 'recipe' ? t('Rezept') : s === 'rw' ? t('Richtwert') : s

type Mode = 'search' | 'barcode' | 'describe' | 'quick' | 'own' | 'photo'

/**
 * Lebensmittel erfassen, Aufbau wie in der App: oben die Mahlzeit in ihrer Farbe, die Suche (eigenes, Haushalt, gemerkte Produkte, BLS),
 * darunter die Wege zum Eintragen als Kacheln, ohne Suchtext die Filter Zuletzt, Häufig, Plan und Vorlagen. Foto nur aus Galerie oder
 * vom Gerät, aufnehmen geht in der App. Eine gewählte Kachel noch einmal anklicken: zurück zur Suche.
 */
export function AddFood({ date, meal: initialMeal, onClose, onAdded }: { date: string; meal: Meal; onClose: () => void; onAdded: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [meal, setMeal] = useState<Meal>(initialMeal)
  const [mode, setMode] = useState<Mode>('search')
  const [filter, setFilter] = useState<'recent' | 'frequent' | 'plan'>('recent')
  const [frequent, setFrequent] = useState<Hit[] | null>(null)
  const [photoOk, setPhotoOk] = useState(false)
  const accent = MEAL_COLOR[meal]
  const [text, setText] = useState('')
  const query = useDebounced(text, 250)
  // Open Food Facts erst nach einer Tipp-Pause (höchstens 10 Suchen pro Minute)
  const offQuery = useDebounced(text, 800)
  const [hits, setHits] = useState<Hit[] | null>(null)
  const [offHits, setOffHits] = useState<Hit[] | null>(null)
  const [recent, setRecent] = useState<Hit[] | null>(null)
  const [food, setFood] = useState<Food | null>(null)
  const [preset, setPreset] = useState<{ amount: number | null; portion: string | null } | null>(null)
  const [unknown, setUnknown] = useState<string | null>(null)
  const [planned, setPlanned] = useState<Planned[]>([])
  // Für „≈ Minuten Gehen“: das aktuelle Gewicht aus dem Profil
  const [weight, setWeight] = useState<number | null>(null)
  const [targets, setTargets] = useState<Targets | null>(null)
  useEffect(() => { coachApi.profile().then(r => { setWeight(r.profile.weightKg ?? null); setTargets(r.targets) }).catch(() => {}) }, [])
  // Gleich lostippen: der Dialog fokussiert beim Öffnen die Mahlzeit-Auswahl - danach ins Suchfeld (auch zurück aus Detail und Scanner)
  const searchInput = useRef<HTMLInputElement>(null)
  const [templates, setTemplates] = useState<Template[]>([])

  useEffect(() => {
    if (mode !== 'search' || food) return
    const timer = setTimeout(() => searchInput.current?.focus(), 60)
    return () => clearTimeout(timer)
  }, [mode, food])
  useEffect(() => { coachApi.photoAvailable().then(r => setPhotoOk(r.available)).catch(() => setPhotoOk(false)) }, [])
  useEffect(() => { if (filter === 'frequent' && !frequent) coachApi.frequent().then(setFrequent).catch(() => setFrequent([])) }, [filter, frequent])
  useEffect(() => { coachApi.recent(meal).then(setRecent).catch(() => setRecent([])) }, [meal])
  useEffect(() => { coachApi.planned(date, meal).then(setPlanned).catch(() => setPlanned([])) }, [date, meal])
  useEffect(() => { coachApi.templates().then(setTemplates).catch(() => setTemplates([])) }, [])
  useEffect(() => {
    if (!query.trim()) { setHits(null); return }
    let stale = false
    coachApi.search(query.trim(), meal).then(r => { if (!stale) setHits(r) }).catch(e => { if (!stale) { setHits([]); fail(e) } })
    return () => { stale = true }
  }, [query, meal]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (offQuery.trim().length < 3) { setOffHits(null); return }
    let stale = false
    setOffHits(null)
    coachApi.searchOff(offQuery.trim()).then(r => { if (!stale) setOffHits(r.hits) }).catch(() => { if (!stale) setOffHits([]) })
    return () => { stale = true }
  }, [offQuery])
  const known = new Set((hits ?? []).map(h => h.id))
  const brands = (offHits ?? []).filter(h => !known.has(h.id))
  const row = (h: Hit) => (
    <div key={h.id + (h.amount ?? '')} className={s.hit} onClick={() => pick(h)} role="button" tabIndex={0} onKeyDown={e => { if (e.key === 'Enter') pick(h) }}>
      {h.image && <img src={h.image} alt="" className={s.thumb} loading="lazy" />}
      <div className={s.hitBody}>
        <Text weight="semibold" truncate wrap={false} block title={h.name}>{h.name}</Text>
        <span className={s.sub}>{[h.brand, h.portion ?? t('je 100 g')].filter(Boolean).join(' · ')}</span>
      </div>
      {h.nutri && <ScoreBar grade={h.nutri} label="Nutri-Score" small />}
      <span className={s.kcal}>{fmt(h.amount ? h.kcalPer100 * h.amount / 100 : h.kcalPer100)} <span className={s.kcalUnit}>kcal</span></span>
      <Button shape="circular" appearance="outline" style={flash?.id === h.id ? { color: '#fff', backgroundColor: accent, border: `2px solid ${accent}` } : { color: accent, border: `2px solid ${accent}` }}
        icon={flash?.id === h.id ? <Checkmark20Regular key={flash.n} className={s.pop} /> : <Add20Regular />}
        aria-label={t('Übliche Portion eintragen')} title={t('Übliche Portion eintragen')}
        onClick={e => { e.stopPropagation(); quickAdd(h) }} />
      {flash?.id === h.id && <span key={'s' + flash.n} className={s.sweep} />}
      {flash?.id === h.id && h.amount ? <span key={'r' + flash.n} className={s.rise}>+{fmt(h.kcalPer100 * h.amount / 100)} kcal</span> : null}
    </div>
  )

  const pick = async (h: Hit) => {
    try { setFood(await coachApi.food(h.id)); setPreset({ amount: h.amount, portion: h.portion }) } catch (e) { fail(e) }
  }
  const byCode = async (code: string) => {
    setMode('search')
    try { setFood(await coachApi.barcode(code)); setPreset(null) }
    catch (e) {
      if (e instanceof ApiError && e.status === 404) { setUnknown(code); setMode('own') }
      else fail(e)
    }
  }
  const fromPlan = async (p: Planned) => {
    if (!p.food) return
    try { setFood(await coachApi.food(p.food)); setPreset({ amount: null, portion: p.portion ?? '1 Portion' }) } catch (e) { fail(e) }
  }
  const apply = async (tpl: Template) => {
    try {
      const r = await coachApi.applyTemplate(tpl.id, date, meal)
      notify(t('Vorlage „{name}“ eingetragen ({n} Posten).', { name: tpl.name, n: r.added })); onAdded(); onClose()
    } catch (e) { fail(e) }
  }
  const removeTemplate = async (tpl: Template) => {
    try { await coachApi.deleteTemplate(tpl.id); setTemplates(templates.filter(x => x.id !== tpl.id)) } catch (e) { fail(e) }
  }
  const add = async (body: Parameters<typeof coachApi.add>[0]) => {
    try { await coachApi.add(body); notify(t('Eingetragen.')); onAdded(); onClose() } catch (e) { fail(e) }
  }
  // Schnell: „+“ in der Trefferliste trägt die übliche Portion ein (bei schon Gegessenem die Menge von damals); der Dialog bleibt offen
  const [quick, setQuick] = useState<{ id: string; name: string }[]>([])
  const [flash, setFlash] = useState<{ id: string; n: number } | null>(null)
  // Der Haken bleibt nur für die Dauer der Animation, danach wieder „+“ (derselbe Posten lässt sich noch einmal eintragen)
  useEffect(() => {
    if (!flash) return
    const timer = setTimeout(() => setFlash(null), 1200)
    return () => clearTimeout(timer)
  }, [flash])
  const quickAdd = async (h: Hit) => {
    // Sofort zeigen (der Eintrag dauert nur einen Augenblick); schlägt er fehl, kommt die Fehlermeldung
    setFlash(f => ({ id: h.id, n: (f?.n ?? 0) + 1 }))
    try {
      await coachApi.add({ date, meal, food: h.id, ...(h.mine && h.amount ? { amount: h.amount, portion: h.portion } : {}) })
      setQuick(l => [...l, { id: h.id, name: h.name }])
      onAdded()
    } catch (e) { fail(e) }
  }

  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>
            <div className={s.head}>
              <Button appearance="subtle" icon={food || mode !== 'search' ? <ArrowLeft20Regular /> : <Dismiss20Regular />} aria-label={food || mode !== 'search' ? t('Zurück') : t('Schließen')}
                onClick={() => { if (food) setFood(null); else if (mode !== 'search') setMode('search'); else onClose() }} />
              <Menu>
                <MenuTrigger disableButtonEnhancement>
                  <Button appearance="transparent" className={s.meal} style={{ color: accent }} icon={<ChevronDown20Regular />} iconPosition="after" title={t('Mahlzeit')}>{mealName(meal)}</Button>
                </MenuTrigger>
                <MenuPopover>
                  <MenuList>{MEALS.map(m => <MenuItem key={m} style={{ color: MEAL_COLOR[m] }} onClick={() => setMeal(m)}>{mealName(m)}</MenuItem>)}</MenuList>
                </MenuPopover>
              </Menu>
            </div>
          </DialogTitle>
          <DialogContent>
            {food ? <PortionPicker food={food} weight={weight} targets={targets} preset={preset}
              onAdd={(grams, label) => add({ date, meal, food: food.id, amount: grams, portion: label })} />
              : <>
                {mode === 'search' && <SearchBox size="large" className={s.search} input={{ ref: searchInput }} placeholder={ask(meal)} value={text} onChange={(_, d) => setText(d.value)}
                  style={{ border: `1.5px solid ${accent}`, borderRadius: 14 }} />}
                <div className={s.tiles}>
                  {([
                    ['barcode', BarcodeScanner24Regular, t('Barcode'), '#B9A6F5'],
                    ['describe', TextT24Regular, t('coach|Text'), '#6FA8EE'],
                    ['quick', Flash24Regular, t('Nur kcal'), '#F2B53C'],
                    ['own', AddCircle24Regular, t('Eigenes'), '#3CC4A0'],
                    ...(photoOk ? [['photo', Image24Regular, t('Foto'), '#E07AD6'] as const] : []),
                  ] as const).map(([id, Icon, label, tint]) => (
                    <button key={id} className={s.tile} aria-pressed={mode === id} onClick={() => setMode(mode === id ? 'search' : id)}>
                      <div className={s.tileBox} style={mode === id ? { border: `2px solid ${tint}`, backgroundColor: tint + '38', color: tint } : { color: tint }}><Icon /></div>
                      {label}
                    </button>
                  ))}
                </div>
                <div className={s.pane} style={mode === 'search' ? undefined : { minHeight: 'calc(50vh + 44px)' }}>
                {mode === 'search' && <>
                  {!text.trim() && <div className={s.filters}>
                    {([['recent', t('coach|Zuletzt')], ['frequent', t('Häufig')], ['plan', t('Plan und Vorlagen')]] as const).map(([id, label]) => (
                      <button key={id} className={s.filter} aria-pressed={filter === id} onClick={() => setFilter(id)}
                        style={filter === id ? { backgroundColor: accent, border: `1px solid ${accent}`, color: '#fff' } : undefined}>{label}</button>
                    ))}
                  </div>}
                  <div className={s.list}>
                    {!text.trim() && filter === 'plan' && planned.length > 0 && <>
                      <Text size={200} weight="semibold" style={{ margin: '6px 0 2px' }}>{t('Laut Essensplan')}</Text>
                      {planned.map((p, i) => (
                        <div key={'p' + i} className={s.hit} onClick={() => fromPlan(p)} role={p.food ? 'button' : undefined} tabIndex={p.food ? 0 : undefined}
                          onKeyDown={e => { if (e.key === 'Enter') fromPlan(p) }} style={p.food ? undefined : { cursor: 'default' }}>
                          <div className={s.hitBody}>
                            <Text>{p.title}</Text>
                            <span className={s.sub}>{p.food ? (p.guessed ? t('Zugeordnet: {name} · {portion}', { name: p.foodName ?? '', portion: p.portion ?? '' }) : t('Rezept · 1 Portion')) : p.recipe ? t('Rezept ohne erkannte Zutaten – bitte über die Suche eintragen') : t('Ohne Rezept – bitte über die Suche oder „Text“ eintragen')}</span>
                          </div>
                          {p.kcalPerServing != null && <span className={s.kcal}>{fmt(p.kcalPerServing)} <span className={s.kcalUnit}>kcal</span></span>}
                        </div>
                      ))}
                    </>}
                    {!text.trim() && filter === 'plan' && templates.length > 0 && <>
                      <Text size={200} weight="semibold" style={{ margin: '6px 0 2px' }}>{t('Vorlagen')}</Text>
                      {[...templates].sort((a, b) => Number(b.meal === meal) - Number(a.meal === meal)).map(tpl => (
                        <div key={tpl.id} className={s.hit} onClick={() => apply(tpl)} role="button" tabIndex={0} onKeyDown={e => { if (e.key === 'Enter') apply(tpl) }}>
                          <div className={s.hitBody}>
                            <Text>{tpl.name}</Text>
                            <span className={s.sub}>{tpl.items.map(i => i.name).join(', ')}</span>
                          </div>
                          <span className={s.kcal}>{fmt(tpl.kcal)} <span className={s.kcalUnit}>kcal</span></span>
                          <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} aria-label={t('Vorlage löschen')} title={t('Vorlage löschen')}
                            onClick={e => { e.stopPropagation(); removeTemplate(tpl) }} />
                        </div>
                      ))}
                    </>}
                    {!text.trim() && filter === 'plan' && planned.length === 0 && templates.length === 0 && <Text className={s.sub} style={{ padding: 8, whiteSpace: 'normal' }}>{t('Für diese Mahlzeit steht nichts im Essensplan. Vorlagen gibt es noch keine.')}</Text>}
                    {!text.trim() && filter === 'recent' && recent?.length === 0 && <Text className={s.sub} style={{ padding: 8, whiteSpace: 'normal' }}>{t('Suche nach einem Lebensmittel oder scanne den Barcode. Was du einträgst, steht beim nächsten Mal hier oben.')}</Text>}
                    {!text.trim() && filter === 'frequent' && frequent?.length === 0 && <Text className={s.sub} style={{ padding: 8, whiteSpace: 'normal' }}>{t('Hier stehen die Lebensmittel, die du am häufigsten einträgst.')}</Text>}
                    {!text.trim() && ((filter === 'recent' && !recent) || (filter === 'frequent' && !frequent)) && <Spinner size="tiny" />}
                    {(text.trim() ? hits : filter === 'recent' ? recent : filter === 'frequent' ? frequent : null)?.map(row)}
                    {text.trim() && hits === null && <Spinner size="tiny" />}
                    {text.trim() && hits?.length === 0 && brands.length === 0 && offHits !== null && <Text className={s.sub} style={{ padding: 8, whiteSpace: 'normal' }}>{t('Nichts gefunden. Lege es unter „Eigenes“ an.')}</Text>}
                    {text.trim().length >= 3 && hits !== null && <>
                      <Text size={200} weight="semibold" style={{ margin: '10px 0 2px' }}>{t('Markenprodukte (Open Food Facts)')}</Text>
                      {offHits === null ? <Spinner size="tiny" /> : brands.length === 0 ? <Text className={s.sub} style={{ padding: 8 }}>{t('Keine weiteren Markenprodukte gefunden.')}</Text> : brands.map(row)}
                    </>}
                  </div>
                </>}
                {mode === 'barcode' && <Scanner onCode={byCode} onClose={() => setMode('search')} />}
                {mode === 'describe' && <Describe date={date} meal={meal} onAdded={() => { onAdded(); onClose() }} />}
                {mode === 'quick' && <QuickEntry onAdd={(name, n) => add({ date, meal, name, quick: n })} />}
                {mode === 'own' && <OwnFood barcode={unknown} onSaved={f => { setFood(f); setPreset(null); setMode('search') }} />}
                {/* Bleibt beim Wechsel der Kachel bestehen: die Auswertung läuft weiter, das Ergebnis geht nicht verloren */}
                {photoOk && <div hidden={mode !== 'photo'}><PhotoFood date={date} meal={meal} onAdded={() => { onAdded(); onClose() }} /></div>}
                </div>
              </>}
          </DialogContent>
          {/* Mehrere nacheinander mit „+“: unten zählt mit, „Fertig“ schließt; im Hinweis stehen die Namen (Gleiches zusammengefasst, „× 2“) */}
          {!food && quick.length > 0 && <DialogActions fluid>
            <div className={s.count} style={{ color: accent, border: `2px solid ${accent}` }} title={t('Eingetragen: {names}', { names: [...quick.reduce((m, x) => m.set(x.name, (m.get(x.name) ?? 0) + 1), new Map<string, number>())].map(([n, c]) => c > 1 ? `${n} × ${c}` : n).join(', ') })}>{quick.length}</div>
            <Button appearance="primary" shape="circular" style={{ flex: 1, height: 42, backgroundColor: accent, borderColor: accent, color: '#fff' }} onClick={onClose}>{t('Fertig')}</Button>
          </DialogActions>}
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Portion wählen: Größen zum Antippen, Anzahl, Gramm frei, Vorschau der Nährwerte. */
function PortionPicker({ food, preset, weight, targets, onAdd }: {
  food: Food; preset: { amount: number | null; portion: string | null } | null; weight: number | null; targets: Targets | null
  onAdd: (grams: number, label: string) => void
}) {
  const s = useStyles()
  const unit = food.liquid ? 'ml' : 'g'
  // Vorgabe: die zuletzt gewählte Portion, sonst die erste genannte
  const initial = (preset?.portion && food.portions.find(p => preset.portion!.replace(/^.* × /, '').startsWith(p.label))) || food.portions[0]
  const [portion, setPortion] = useState<Portion | null>(initial ?? null)
  const [count, setCount] = useState(() => preset?.amount && initial ? Math.round(preset.amount / initial.grams * 2) / 2 || 1 : 1)
  const [grams, setGrams] = useState<string>(() => String(Math.round((preset?.amount ?? (initial ? initial.grams : 100)) * 10) / 10))
  const g = Number(grams.replace(',', '.'))
  const n = food.per100
  const f = Number.isFinite(g) ? g / 100 : 0
  const choose = (p: Portion) => { setPortion(p); setCount(1); setGrams(String(p.grams)) }
  const setN = (v: number) => { setCount(v); if (portion) setGrams(String(Math.round(portion.grams * v * 10) / 10)) }
  const [noImage, setNoImage] = useState(false)
  useEffect(() => setNoImage(false), [food.id])
  const label = portion && Math.abs(portion.grams * count - g) < 0.05 ? (count === 1 ? portion.label : `${fmt(count, 1)} × ${portion.label}`) : `${fmt(g, 1)} ${unit}`
  return (
    <div>
      {!noImage && <img src={`/api/coach/foods/${encodeURIComponent(food.id)}/image`} alt={food.name} onError={() => setNoImage(true)}
        style={{ display: 'block', width: 160, height: 160, objectFit: 'contain', borderRadius: tokens.borderRadiusMedium, backgroundColor: '#fff', marginBottom: 12 }} />}
      <Text weight="semibold" size={400} block>{food.name}</Text>
      <Text className={s.sub} block>{[food.brand, food.quantity, sourceName(food.source)].filter(Boolean).join(' · ')}</Text>
      <div className={s.chips}>
        {food.portions.map(p => <span key={p.label} className={mergeClasses(s.chip, portion?.label === p.label && s.chipOn)} role="button" tabIndex={0}
          onClick={() => choose(p)} onKeyDown={e => { if (e.key === 'Enter') choose(p) }}>{p.label}{/\d\s*(g|ml)\b/.test(p.label) ? '' : ` · ${fmt(p.grams)} ${unit}`}</span>)}
      </div>
      <div className={s.row}>
        <Field label={t('Anzahl')}><Input type="number" min={0.5} step={0.5} value={String(count)} onChange={(_, d) => setN(Math.max(0, Number(d.value) || 0))} style={{ width: 90 }} disabled={!portion} /></Field>
        <Field label={unit === 'ml' ? t('Milliliter') : t('Gramm')}><Input value={grams} onChange={(_, d) => setGrams(d.value)} style={{ width: 110 }} contentAfter={unit} /></Field>
      </div>
      <FoodWarnings food={food} />
      {food.info && <OffBadges info={food.info} />}
      <div className={s.macros}>
        <Text weight="semibold">{fmt(n.kcal * f)} kcal</Text>
        {/* Gehen ≈ 3,5 MET: kcal je Minute = 3,5 × 3,5 × kg / 200 */}
        {weight && n.kcal * f > 0 && <span title={t('Gehen mit 3,5 MET bei {kg} kg', { kg: fmt(weight) })}>≈ {t('{n} Min. Gehen', { n: fmt(Math.round(n.kcal * f / (3.5 * 3.5 * weight / 200))) })}</span>}
        <span style={{ color: tokens.colorPaletteRedForeground2 }}>{t('Eiweiß')} {fmt(n.protein * f, 1)} g</span>
        <span style={{ color: tokens.colorPaletteMarigoldForeground2 }}>{t('Kohlenhydrate')} {fmt(n.carbs * f, 1)} g</span>
        <span style={{ color: tokens.colorPaletteTealForeground2 }}>{t('coach|Fett')} {fmt(n.fat * f, 1)} g</span>
      </div>
      <FoodFacts food={food} grams={Number.isFinite(g) ? g : 0} targets={targets} />
      {food.info?.ingredients && <details style={{ fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground2 }}>
        <summary style={{ cursor: 'pointer' }}>{t('Zutaten')}</summary>{food.info.ingredients}</details>}
      <Button appearance="primary" disabled={!(g > 0)} style={{ width: '100%', marginTop: 16 }} onClick={() => onAdd(g, label)}>{t('Hinzufügen')}</Button>
    </div>
  )
}

function num(v: string) { const n = Number(v.replace(',', '.')); return Number.isFinite(n) ? n : NaN }

/** Schnell eintragen: Name und Energie, Makros freiwillig. */
function QuickEntry({ onAdd }: { onAdd: (name: string, n: Nutrients) => void }) {
  const s = useStyles()
  const [v, setV] = useState({ name: '', kcal: '', protein: '', fat: '', carbs: '' })
  const kcal = num(v.kcal)
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 8 }}>
      <Field label={t('Bezeichnung')}><Input value={v.name} onChange={(_, d) => setV({ ...v, name: d.value })} placeholder={t('z. B. Kantine Mittag')} /></Field>
      <div className={s.grid}>
        <Field label="kcal" required><Input value={v.kcal} onChange={(_, d) => setV({ ...v, kcal: d.value })} inputMode="decimal" /></Field>
        <Field label={t('Eiweiß (g)')}><Input value={v.protein} onChange={(_, d) => setV({ ...v, protein: d.value })} inputMode="decimal" /></Field>
        <Field label={t('Kohlenhydrate (g)')}><Input value={v.carbs} onChange={(_, d) => setV({ ...v, carbs: d.value })} inputMode="decimal" /></Field>
        <Field label={t('Fett (g)')}><Input value={v.fat} onChange={(_, d) => setV({ ...v, fat: d.value })} inputMode="decimal" /></Field>
      </div>
      <div><Button appearance="primary" disabled={!(kcal >= 0)} onClick={() => onAdd(v.name, { kcal, protein: num(v.protein) || 0, fat: num(v.fat) || 0, carbs: num(v.carbs) || 0 })}>{t('Eintragen')}</Button></div>
    </div>
  )
}

/** Neues Lebensmittel für den ganzen Haushalt (z. B. unbekannter Barcode): Nährwerte je 100 g von der Packung. */
function OwnFood({ barcode, onSaved }: { barcode: string | null; onSaved: (f: Food) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [v, setV] = useState({ name: '', brand: '', barcode: barcode ?? '', kcal: '', protein: '', fat: '', carbs: '', fiber: '', sugar: '', salt: '', portion: '', grams: '' })
  const [liquid, setLiquid] = useState(false)
  const [busy, setBusy] = useState(false)
  const [image, setImage] = useState<File | null>(null)
  const preview = useMemo(() => image ? URL.createObjectURL(image) : null, [image])
  useEffect(() => () => { if (preview) URL.revokeObjectURL(preview) }, [preview])
  const set = (k: keyof typeof v) => (_: unknown, d: { value: string }) => setV({ ...v, [k]: d.value })
  const file = useRef<HTMLInputElement>(null)
  const per100: Nutrients = { kcal: num(v.kcal), protein: num(v.protein) || 0, fat: num(v.fat) || 0, carbs: num(v.carbs) || 0,
    fiber: v.fiber ? num(v.fiber) : null, sugar: v.sugar ? num(v.sugar) : null, salt: v.salt ? num(v.salt) : null }
  // Plausibel? Energie ≈ 4 × Eiweiß + 4 × Kohlenhydrate + 9 × Fett (+ 2 × Ballaststoffe)
  const calc = 4 * per100.protein + 4 * per100.carbs + 9 * per100.fat + 2 * (per100.fiber ?? 0)
  const odd = v.kcal && (v.protein || v.carbs || v.fat) && Math.abs(calc - per100.kcal) > Math.max(25, per100.kcal * 0.2)
  const save = async () => {
    setBusy(true)
    try {
      const portions = v.portion.trim() && num(v.grams) > 0 ? [{ label: v.portion.trim(), grams: num(v.grams) }] : []
      const food = await coachApi.saveFood({ name: v.name, brand: v.brand || null, barcode: v.barcode || null, per100, portions, liquid })
      if (image) await coachApi.foodImage(food.id, image).catch(fail)
      onSaved(food)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 8 }}>
      {barcode && <MessageBar intent="info"><MessageBarBody>{t('Dieses Produkt ist noch nicht bekannt. Trage die Werte von der Packung ein – danach kennt es dein ganzer Haushalt.')}</MessageBarBody></MessageBar>}
      <div className={s.grid}>
        <Field label={t('Name')} required style={{ gridColumn: '1 / -1' }}><Input value={v.name} onChange={set('name')} /></Field>
        <Field label={t('Marke')}><Input value={v.brand} onChange={set('brand')} /></Field>
        <Field label={t('Barcode')}><Input value={v.barcode} onChange={set('barcode')} inputMode="numeric" /></Field>
      </div>
      <Text weight="semibold">{liquid ? t('Nährwerte je 100 ml') : t('Nährwerte je 100 g')}</Text>
      <div className={s.grid}>
        <Field label="kcal" required><Input value={v.kcal} onChange={set('kcal')} inputMode="decimal" /></Field>
        <Field label={t('Eiweiß (g)')}><Input value={v.protein} onChange={set('protein')} inputMode="decimal" /></Field>
        <Field label={t('Kohlenhydrate (g)')}><Input value={v.carbs} onChange={set('carbs')} inputMode="decimal" /></Field>
        <Field label={t('davon Zucker (g)')}><Input value={v.sugar} onChange={set('sugar')} inputMode="decimal" /></Field>
        <Field label={t('Fett (g)')}><Input value={v.fat} onChange={set('fat')} inputMode="decimal" /></Field>
        <Field label={t('Ballaststoffe (g)')}><Input value={v.fiber} onChange={set('fiber')} inputMode="decimal" /></Field>
        <Field label={t('Salz (g)')}><Input value={v.salt} onChange={set('salt')} inputMode="decimal" /></Field>
      </div>
      {odd && <Text size={200} style={{ color: tokens.colorPaletteMarigoldForeground2 }}>{t('Die Energie passt nicht ganz zu Eiweiß, Kohlenhydraten und Fett (berechnet etwa {kcal} kcal) – bitte noch einmal prüfen.', { kcal: fmt(calc) })}</Text>}
      <div className={s.grid}>
        <Field label={t('Portion (freiwillig)')}><Input value={v.portion} onChange={set('portion')} placeholder={t('z. B. 1 Riegel')} /></Field>
        <Field label={liquid ? t('Milliliter') : t('Gramm')}><Input value={v.grams} onChange={set('grams')} inputMode="decimal" /></Field>
      </div>
      <Switch checked={liquid} onChange={(_, d) => setLiquid(d.checked)} label={t('Getränk (Angaben je 100 ml)')} />
      <div className={s.row} style={{ alignItems: 'center' }}>
        {preview && <img src={preview} alt="" className={s.thumb} />}
        <Button size="small" onClick={() => file.current?.click()}>{image ? t('Anderes Bild …') : t('Bild hinzufügen …')}</Button>
        {image && <Button size="small" appearance="subtle" onClick={() => setImage(null)}>{t('Bild entfernen')}</Button>}
        <input ref={file} type="file" accept="image/*" hidden onChange={e => { setImage(e.target.files?.[0] ?? null); e.target.value = '' }} />
      </div>
      <div><Button appearance="primary" disabled={busy || !v.name.trim() || !(num(v.kcal) >= 0)} onClick={save}>{t('Speichern')}</Button></div>
    </div>
  )
}

/** Barcode mit der Kamera (BarcodeDetector, wo der Browser ihn kann), sonst Eingabe von Hand. */
function Scanner({ onCode, onClose }: { onCode: (code: string) => void; onClose: () => void }) {
  const s = useStyles()
  const video = useRef<HTMLVideoElement>(null)
  const [manual, setManual] = useState('')
  const [camera, setCamera] = useState<'starting' | 'on' | 'off'>('starting')
  useEffect(() => {
    const Detector = (window as any).BarcodeDetector
    if (!Detector || !navigator.mediaDevices?.getUserMedia) { setCamera('off'); return }
    let stream: MediaStream | null = null
    let stop = false
    const detector = new Detector({ formats: ['ean_13', 'ean_8', 'upc_a', 'upc_e'] })
    navigator.mediaDevices.getUserMedia({ video: { facingMode: 'environment' } }).then(async st => {
      stream = st
      if (stop || !video.current) return
      video.current.srcObject = st
      await video.current.play().catch(() => {})
      setCamera('on')
      while (!stop) {
        try {
          const found = await detector.detect(video.current)
          if (found?.[0]?.rawValue) { stop = true; onCode(found[0].rawValue); break }
        } catch { /* nächstes Bild */ }
        await new Promise(r => setTimeout(r, 250))
      }
    }).catch(() => setCamera('off'))
    return () => { stop = true; stream?.getTracks().forEach(t => t.stop()) }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 10 }}>
      {camera !== 'off' && <video ref={video} className={s.video} muted playsInline />}
      {camera === 'off' && <Text size={200} className={s.sub}>{t('Dieser Browser kann keine Barcodes lesen – tippe die Ziffern unter dem Strichcode ein (in der Handy-App geht es mit der Kamera).')}</Text>}
      <div className={s.row}>
        <Input value={manual} onChange={(_, d) => setManual(d.value.replace(/\D/g, ''))} placeholder={t('Barcode, z. B. 4006040…')} inputMode="numeric" style={{ flex: 1 }}
          onKeyDown={e => { if (e.key === 'Enter' && manual.length >= 8) onCode(manual) }} />
        <Button disabled={manual.length < 8} onClick={() => onCode(manual)}>{t('Suchen')}</Button>
        <Button appearance="subtle" onClick={onClose}>{t('Abbrechen')}</Button>
      </div>
    </div>
  )
}
