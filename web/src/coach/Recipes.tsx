import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Input, MessageBar, MessageBarBody, Option,
  SearchBox, Spinner, Switch, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { useCallback, useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { RecipeSearchButton } from '../notes/RecipeSearchDialog'
import { t } from '../i18n'
import RecipeCard from './RecipeCard'
import { useDebounced } from '../shell/hooks'
import { Search20Regular } from '@fluentui/react-icons'
import { coachApi, fmt, Hit, isoDay, mealName, mealNow, RecipeNutrition } from './api'

const useStyles = makeStyles({
  // Karten: Bild, Titel, kcal je Portion, Ordner
  list: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(200px, 1fr))', gap: '12px' },
  card: {
    display: 'flex', flexDirection: 'column', borderRadius: tokens.borderRadiusLarge, overflow: 'hidden', cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground2, ':hover': { boxShadow: tokens.shadow8 }, ':focus-visible': { outline: `2px solid ${tokens.colorBrandStroke1}` },
  },
  cardImg: { width: '100%', aspectRatio: '4 / 3', objectFit: 'cover', display: 'block', backgroundColor: tokens.colorNeutralBackground3 },
  cardNoImg: { width: '100%', aspectRatio: '4 / 3', display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '40px', backgroundColor: tokens.colorNeutralBackground3 },
  cardBody: { padding: '8px 10px 10px', display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  kcal: { fontVariantNumeric: 'tabular-nums', fontWeight: tokens.fontWeightSemibold, color: tokens.colorBrandForeground1 },
  item: { display: 'flex', alignItems: 'center', gap: '10px', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  grow: { flex: 1, minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  line: { display: 'grid', gridTemplateColumns: 'minmax(0, 1.2fr) minmax(0, 1.4fr) auto 64px 72px', gap: '8px', alignItems: 'center', padding: '5px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` },
  num: { fontVariantNumeric: 'tabular-nums', textAlign: 'right' },
  found: { display: 'flex', flexDirection: 'column', gap: '4px', padding: '6px 0 8px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` },
  pickImg: { width: '28px', height: '28px', objectFit: 'cover', borderRadius: tokens.borderRadiusSmall, flexShrink: 0 },
  pick: { display: 'flex', gap: '8px', alignItems: 'center', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  groupHead: { fontWeight: tokens.fontWeightSemibold, margin: '16px 0 8px', display: 'flex', gap: '8px', alignItems: 'baseline' },
  badges: { display: 'flex', gap: '4px', flexWrap: 'wrap', marginTop: '2px' },
  badge: { fontSize: tokens.fontSizeBase100, padding: '0 6px', borderRadius: '999px', border: `1px solid ${tokens.colorNeutralStroke2}`, color: tokens.colorNeutralForeground2 },
  sums: { display: 'flex', gap: '16px', flexWrap: 'wrap', fontVariantNumeric: 'tabular-nums', margin: '10px 0' },
})

interface RecipeEntry {
  syncId: string; title: string; folder: string | null; image?: string | null; kcal?: number | null; servings?: number | null
  protein?: number | null; diets?: string[]; excluded?: string | null; base?: boolean; meal?: string | null
  /** kcal stammt aus der Angabe „Kalorien je Portion“ im Rezept (sonst aus den Zutaten gerechnet) */
  kcalGiven?: boolean
}
type Source = 'all' | 'own' | 'base'

// Ernährungsformen wie im Profil: vegan, vegetarisch, Low Carb und eiweißreich erkennt der Server an den Zutaten (Low Carb gilt auch
// für Keto); Mischkost, Mediterran und Keto tragen nur die Basisrezepte
const DIETS = ['dge', 'med', 'vegetarian', 'vegan', 'highprotein', 'lowcarb', 'keto'] as const
const dietName = (d: string) => ({ dge: t('Mischkost (DGE)'), med: t('Mediterran'), vegan: t('Vegan'), vegetarian: t('Vegetarisch'), lowcarb: t('Low Carb'), keto: t('Keto'),
  highprotein: t('Eiweißreich') } as Record<string, string>)[d] ?? d
type Group = 'none' | 'diet' | 'folder'
// Passt das Rezept zur Ernährungsform? Eigene Rezepte ohne vegetarisch und Low Carb zählen als Mischkost
const fitsDiet = (r: RecipeEntry, d: string) => !!r.diets?.includes(d)
  || (d === 'dge' && !r.base && !r.diets?.some(x => x === 'vegetarian' || x === 'lowcarb'))

/** Rezepte (Rezept-Notizen): Nährwerte je Portion aus den Zutaten, Zuordnung korrigierbar, als Mahlzeit eintragen. */
export function Recipes() {
  const s = useStyles()
  const { fail } = useApp()
  const [list, setList] = useState<RecipeEntry[] | null>(null)
  const [q, setQ] = useState('')
  const [open, setOpen] = useState<string | null>(null)
  // Filter: Ernährungsform, nur Passendes zum Profil, höchstens so viele kcal je Portion; Gruppieren nach Ernährungsform oder Ordner
  const [diet, setDiet] = useState<string>('')
  const [fits, setFits] = useState(false)
  const [maxKcal, setMaxKcal] = useState('')
  // Herkunft: eigene Rezept-Notizen oder Basisrezepte aus der Auslieferung
  const [source, setSource] = useState<Source>(() => { try { return (localStorage.getItem('dmoffice.coach.recipeSource') as Source) || 'all' } catch { return 'all' } })
  useEffect(() => { try { localStorage.setItem('dmoffice.coach.recipeSource', source) } catch { /* egal */ } }, [source])
  const [group, setGroup] = useState<Group>(() => { try { return (localStorage.getItem('dmoffice.coach.recipeGroup') as Group) || 'none' } catch { return 'none' } })
  useEffect(() => { try { localStorage.setItem('dmoffice.coach.recipeGroup', group) } catch { /* egal */ } }, [group])
  const loadList = useCallback(() => { api.get<RecipeEntry[]>('/api/coach/recipes').then(setList).catch(e => { setList([]); fail(e) }) }, [fail])
  useEffect(loadList, [loadList])
  const limit = Number(maxKcal) || 0
  const shown = (list ?? []).filter(r => (!q.trim() || r.title.toLowerCase().includes(q.trim().toLowerCase()))
    && (source === 'all' || (source === 'base') === !!r.base)
    && (!diet || fitsDiet(r, diet)) && (!fits || !r.excluded) && (!limit || (r.kcal != null && r.kcal <= limit)))
  // Gruppen: bei Ernährungsform kann ein Rezept in mehreren stehen (vegan ist auch vegetarisch)
  const groups: [string, RecipeEntry[]][] = group === 'diet'
    ? DIETS.map(d => [dietName(d), shown.filter(r => fitsDiet(r, d))] as [string, RecipeEntry[]]).filter(([, l]) => l.length > 0)
    : group === 'folder'
      ? [...new Set(shown.map(r => r.folder ?? ''))].sort((a, b) => a.localeCompare(b)).map(f => [f || t('Ohne Ordner'), shown.filter(r => (r.folder ?? '') === f)] as [string, RecipeEntry[]])
      : [['', shown]]
  const card = (r: RecipeEntry) => <div key={r.syncId} className={s.card} role="button" tabIndex={0} onClick={() => setOpen(r.syncId)} onKeyDown={e => { if (e.key === 'Enter') setOpen(r.syncId) }}>
    {r.image ? <img src={r.image.startsWith('/api/') ? r.image : `/api/notes/blob/${encodeURIComponent(r.image)}?thumb=400`} alt="" className={s.cardImg} loading="lazy" />
      : <div className={s.cardNoImg} aria-hidden>🍲</div>}
    <div className={s.cardBody}>
      <Text weight="semibold" truncate wrap={false} title={r.title}>{r.title}</Text>
      {r.kcal != null && <span className={s.kcal}>{t('{n} kcal je Portion', { n: fmt(r.kcal) })}</span>}
      {r.folder && group !== 'folder' && <span className={s.muted} style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{r.folder}</span>}
      {(r.diets?.length ?? 0) > 0 && <div className={s.badges}>{r.diets!.filter(d => !(d === 'vegetarian' && r.diets!.includes('vegan')) && !(d === 'lowcarb' && r.diets!.includes('keto'))).map(d => <span key={d} className={s.badge}>{dietName(d)}</span>)}</div>}
      {r.excluded && <span className={s.muted} title={t('Passt nicht zu deinem Profil')}>⚠ {t('Profil: {why}', { why: r.excluded })}</span>}
      {!r.base && !r.kcalGiven && <span className={s.muted} title={t('Der Essensplan schlägt nur Rezepte mit dieser Angabe vor.')}>⚠ {t('Ohne Kalorien je Portion')}</span>}
    </div>
  </div>
  return (
    <div>
      <Text block className={s.muted} style={{ maxWidth: 760, marginBottom: 8 }}>
        {t('Rezepte sind deine Rezept-Notizen (auch per Link aus dem Internet übernommen). Coach rechnet die Nährwerte aus den Zutaten; stimmt eine Zuordnung nicht, wähle eine andere – das merkt er sich.')}
        {' '}{t('Dazu kommen 210 Basisrezepte: je Ernährungsform zehn für Frühstück, Mittag- und Abendessen, Nährwerte nach dem BLS berechnet.')}
      </Text>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center', marginBottom: 8 }}>
        <SearchBox value={q} onChange={(_, d) => setQ(d.value)} placeholder={t('Rezept suchen')} style={{ maxWidth: 360 }} />
        <RecipeSearchButton onImported={loadList} />
      </div>
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'center', marginBottom: 8 }}>
        <Dropdown value={diet ? dietName(diet) : t('Alle Ernährungsformen')} selectedOptions={[diet]} onOptionSelect={(_, d) => setDiet(d.optionValue ?? '')} aria-label={t('Ernährungsform')} style={{ minWidth: 190 }}>
          <Option value="">{t('Alle Ernährungsformen')}</Option>
          {DIETS.map(d => <Option key={d} value={d}>{dietName(d)}</Option>)}
        </Dropdown>
        <Dropdown value={({ all: t('Alle Rezepte'), own: t('Eigene Rezepte'), base: t('Basisrezepte') } as Record<Source, string>)[source]} selectedOptions={[source]}
          onOptionSelect={(_, d) => setSource((d.optionValue as Source) ?? 'all')} aria-label={t('Herkunft')} style={{ minWidth: 160 }}>
          <Option value="all">{t('Alle Rezepte')}</Option>
          <Option value="own">{t('Eigene Rezepte')}</Option>
          <Option value="base">{t('Basisrezepte')}</Option>
        </Dropdown>
        <Input type="number" min={0} value={maxKcal} onChange={(_, d) => setMaxKcal(d.value)} placeholder={t('bis kcal')} aria-label={t('Höchstens kcal je Portion')} style={{ width: 110 }} />
        <Switch checked={fits} onChange={(_, d) => setFits(d.checked)} label={t('Passt zu meinem Profil')} />
        <Dropdown value={({ none: t('Nicht gruppieren'), diet: t('Nach Ernährungsform'), folder: t('Nach Ordner') } as Record<Group, string>)[group]} selectedOptions={[group]}
          onOptionSelect={(_, d) => setGroup((d.optionValue as Group) ?? 'none')} aria-label={t('Gruppieren')} style={{ minWidth: 180 }}>
          <Option value="none">{t('Nicht gruppieren')}</Option>
          <Option value="diet">{t('Nach Ernährungsform')}</Option>
          <Option value="folder">{t('Nach Ordner')}</Option>
        </Dropdown>
        {(diet || fits || maxKcal) && <Button appearance="subtle" size="small" onClick={() => { setDiet(''); setFits(false); setMaxKcal('') }}>{t('Filter zurücksetzen')}</Button>}
        {list && <Text className={s.muted}>{t('{n} von {all}', { n: shown.length, all: list.length })}</Text>}
      </div>
      {!list ? <Spinner /> : shown.length === 0
        ? <Text className={s.muted}>{list.length ? t('Kein Rezept passt zu den Filtern.') : t('Keine Rezepte gefunden. Lege in den Notizen ein Rezept an oder übernimm eins per Link.')}</Text>
        : groups.map(([name, items]) => <div key={name || 'alle'}>
          {name && <div className={s.groupHead}><span>{name}</span><span className={s.muted}>{items.length}</span></div>}
          <div className={s.list}>{items.map(card)}</div>
        </div>)}
      {open && <RecipeDialog syncId={open} onClose={() => setOpen(null)} onAdopted={loadList} />}
    </div>
  )
}

function RecipeDialog({ syncId, onClose, onAdopted }: { syncId: string; onClose: () => void; onAdopted: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [r, setR] = useState<RecipeNutrition | null>(null)
  const [servings, setServings] = useState('')
  const [search, setSearch] = useState<{ line: number; text: string; hits: Hit[] } | null>(null)
  // Markenprodukte von Open Food Facts dazu (verzögert, der Dienst erlaubt nur wenige Suchen) - der BLS kennt z. B. kaum Gewürze
  const [offHits, setOffHits] = useState<Hit[]>([])
  const offText = useDebounced(search?.text.trim() ?? '', 700)
  useEffect(() => {
    setOffHits([])
    if (offText.length < 3) return
    let stale = false
    coachApi.searchOff(offText).then(r => { if (!stale) setOffHits(r.hits) }).catch(() => { })
    return () => { stale = true }
  }, [offText])
  const load = () => coachApi.recipe(syncId).then(x => { setR(x); setServings(String(x.servings)) }).catch(e => { fail(e); onClose() })
  useEffect(() => { load() }, [syncId]) // eslint-disable-line react-hooks/exhaustive-deps
  const map = async (name: string, food: string) => { try { await coachApi.foodmap(name, food); await load() } catch (e) { fail(e) } }
  // Portionen speichern, sobald eine Weile nichts mehr getippt wird
  const typed = useDebounced(servings, 600)
  useEffect(() => {
    const n = Number(typed)
    if (!r || n === r.servings || !(n >= 1 && n <= 50)) return
    coachApi.recipeServings(syncId, n).then(setR).catch(fail)
  }, [typed]) // eslint-disable-line react-hooks/exhaustive-deps
  const eat = async () => {
    if (!r) return
    try {
      await coachApi.add({ date: isoDay(new Date()), meal: mealNow(), food: 'recipe:' + syncId, amount: Math.round(r.weight / r.servings * 10) / 10, portion: '1 Portion' })
      notify(t('„{title}“ bei „{meal}“ eingetragen.', { title: r.title, meal: mealName(mealNow()) }))
    } catch (e) { fail(e) }
  }
  const adopt = async () => {
    try { await coachApi.adoptRecipe(syncId); notify(t('„{title}“ steht jetzt in den Rezept-Notizen.', { title: r?.title ?? '' })); onAdopted(); onClose() } catch (e) { fail(e) }
  }
  const find = async (line: number, text: string) => {
    setSearch({ line, text, hits: [] })
    if (text.trim().length >= 2) try { setSearch({ line, text, hits: (await coachApi.search(text.trim(), 'lunch')).slice(0, 12) }) } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: r?.base ? 900 : 760 }}>
        <DialogBody>
          {/* Basisrezept: die Karte trägt den Titel selbst */}
          {!r?.base && <DialogTitle>{r?.title ?? t('Rezept')}</DialogTitle>}
          <DialogContent>
            {!r ? <Spinner /> : r.base ? <RecipeCard r={r} /> : <>
              <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
                <Text>{t('Portionen')}</Text>
                <Input value={servings} onChange={(_, d) => setServings(d.value.replace(/\D/g, ''))} style={{ width: 70 }} aria-label={t('Portionen')} />
                {r.servingsGuessed && <Text className={s.muted}>{t('geschätzt – im Rezept steht keine Angabe')}</Text>}
              </div>
              <div className={s.sums}>
                {r.kcalGiven != null && <Text weight="semibold">{t('Kalorien je Portion: {kcal} kcal', { kcal: fmt(r.kcalGiven) })}</Text>}
                <Text weight={r.kcalGiven != null ? undefined : 'semibold'}>{t(r.kcalGiven != null ? 'Aus den Zutaten: {kcal} kcal' : 'Je Portion: {kcal} kcal', { kcal: fmt(r.perServing.kcal) })}</Text>
                <span>{t('Eiweiß')} {fmt(r.perServing.protein, 1)} g</span>
                <span>{t('Kohlenhydrate')} {fmt(r.perServing.carbs, 1)} g</span>
                <span>{t('Fett')} {fmt(r.perServing.fat, 1)} g</span>
                <span className={s.muted}>{t('Gesamt {kcal} kcal, {g} g', { kcal: fmt(r.total.kcal), g: fmt(r.weight) })}</span>
              </div>
              {r.kcalGiven == null && <MessageBar intent="info"><MessageBarBody>{t('Im Rezept fehlt die Angabe „Kalorien je Portion“. Der Essensplan schlägt nur Rezepte mit dieser Angabe vor.')}</MessageBarBody></MessageBar>}
              {r.unmatched > 0 && <MessageBar intent="warning"><MessageBarBody>{t('{n} Zutaten ohne Nährwerte – bitte unten zuordnen, sonst fehlen sie in der Summe.', { n: r.unmatched })}</MessageBarBody></MessageBar>}
              {r.lines.map((l, i) => {
                const options = [...(l.food ? [l.food] : []), ...l.alternatives.filter(a => a.id !== l.food?.id)]
                return (
                  <div key={i}>
                    <div className={s.line}>
                      <Text size={200}>{l.text}</Text>
                      {l.name && options.length > 0 ? <Dropdown value={l.food?.name ?? t('nicht zugeordnet')} selectedOptions={l.food ? [l.food.id] : []} style={{ minWidth: 0, width: '100%' }}
                        onOptionSelect={(_, d) => d.optionValue && map(l.name!, d.optionValue)}>
                        {options.map(o => <Option key={o.id} value={o.id} text={o.name}>{o.name}{o.brand ? ' · ' + o.brand : ''}</Option>)}
                      </Dropdown> : <Text size={200} className={s.muted}>{l.vague ? t('ohne Menge, zählt nicht') : t('nicht zugeordnet')}</Text>}
                      {l.name ? <Button size="small" appearance="subtle" icon={<Search20Regular />} aria-label={t('Anderes Lebensmittel suchen')} title={t('Anderes Lebensmittel suchen')}
                        onClick={() => search?.line === i ? setSearch(null) : find(i, l.name!)} /> : <span />}
                      <Text size={200} className={s.num}>{l.grams != null ? fmt(l.grams) + ' g' : '–'}</Text>
                      <Text size={200} className={s.num}>{l.kcal ? fmt(l.kcal) + ' kcal' : ''}</Text>
                    </div>
                    {search?.line === i && <div className={s.found}>
                      <Input size="small" autoFocus value={search.text} onChange={(_, d) => find(i, d.value)} placeholder={t('Lebensmittel suchen')} />
                      {[...search.hits, ...offHits.filter(o => !search.hits.some(h => h.id === o.id))].map(h => <div key={h.id} className={s.pick} role="button" tabIndex={0}
                        onClick={() => { setSearch(null); map(l.name!, h.id) }} onKeyDown={e => { if (e.key === 'Enter') { setSearch(null); map(l.name!, h.id) } }}>
                        {h.image ? <img src={h.image} alt="" className={s.pickImg} loading="lazy" /> : <span className={s.pickImg} />}
                        <Text size={200} style={{ flex: 1, minWidth: 0 }}>{h.name}{h.brand ? ' · ' + h.brand : ''}</Text>
                        <Text size={200} className={s.num}>{fmt(h.kcalPer100)} kcal/100</Text>
                      </div>)}
                    </div>}
                  </div>
                )
              })}
            </>}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={!r || r.weight <= 0} onClick={eat}>{t('1 Portion bei „{meal}“ eintragen', { meal: mealName(mealNow()) })}</Button>
            {r?.base && <Button onClick={adopt}>{t('In meine Rezepte übernehmen')}</Button>}
            <Button onClick={onClose}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

