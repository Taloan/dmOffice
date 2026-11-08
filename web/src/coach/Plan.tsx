import {
  Button, Checkbox, Dropdown, Field, Input, MessageBar, MessageBarBody, Option, Spinner, Switch, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { ArrowShuffle20Regular, CalendarAdd20Regular, Dismiss16Regular, Sparkle20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { RecipeSearchButton } from '../notes/RecipeSearchDialog'
import { locale, t } from '../i18n'

/** Coach Stufe 2: Ernährungsplan aus den eigenen Rezepten → Essensplan (docs/dmcoach-konzept.md) - /api/coach/plan. */

interface Slot { date: string; meal: string; recipe: string | null; title: string; leftover: boolean }
interface Suggestion {
  slots: Slot[]; recipes: number; usable: number; excluded: Record<string, number>
  calendars: { id: number; name: string; color: string | null }[]
}

const MEALS = [
  { key: 'Frühstück', label: () => t('Frühstück') },
  { key: 'Mittagessen', label: () => t('Mittagessen') },
  { key: 'Abendessen', label: () => t('Abendessen') },
]
const CAL_KEY = 'dmoffice.mealplan.calendar'

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'minmax(260px, 340px) minmax(0, 1fr)', gap: '16px', alignItems: 'start', '@media (max-width: 800px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  card: { backgroundColor: tokens.colorNeutralBackground2, borderRadius: tokens.borderRadiusLarge, padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: '10px' },
  row: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  day: { fontWeight: tokens.fontWeightSemibold, marginTop: '6px' },
  slot: { display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 0', borderTop: `1px solid ${tokens.colorNeutralStroke3}` },
  meal: { width: '110px', flexShrink: 0, color: tokens.colorNeutralForeground3 },
  img: { width: '44px', height: '44px', objectFit: 'cover', borderRadius: tokens.borderRadiusMedium, flexShrink: 0, backgroundColor: tokens.colorNeutralBackground3 },
  kcal: { color: tokens.colorNeutralForeground3, fontVariantNumeric: 'tabular-nums', flexShrink: 0 },
  title: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
})

const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`
/** Ab dem nächsten Montag (heute, wenn heute Montag ist). */
const nextMonday = () => { const d = new Date(); d.setDate(d.getDate() + ((8 - d.getDay()) % 7)); return iso(d) }
const reasonLabel = (r: string) => ({ 'Fleisch oder Fisch': t('Fleisch oder Fisch'), Tierisches: t('Tierisches'), Allergen: t('Allergen'), Laktose: t('Laktose'), Gluten: t('Gluten'), Abneigung: t('Abneigung'), Kohlenhydrate: t('Kohlenhydrate'), 'Kalorien fehlen': t('Kalorien fehlen') } as Record<string, string>)[r] ?? r

export function Plan() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [from, setFrom] = useState(nextMonday)
  const [days, setDays] = useState(7)
  const [meals, setMeals] = useState<string[]>(['Abendessen'])
  const [leftovers, setLeftovers] = useState(true)
  const [res, setRes] = useState<Suggestion | null>(null)
  const [slots, setSlots] = useState<Slot[]>([])
  const [busy, setBusy] = useState(false)
  const [cal, setCal] = useState<number | null>(null)
  // Bild und kcal je Rezept (aus der Rezeptliste, auf dem Server zwischengespeichert)
  const [cards, setCards] = useState<Record<string, { image?: string | null; kcal?: number | null }>>({})
  useEffect(() => {
    api.get<{ syncId: string; image?: string | null; kcal?: number | null }[]>('/api/coach/recipes')
      .then(list => setCards(Object.fromEntries(list.map(r => [r.syncId, r])))).catch(() => { })
  }, [])

  const suggest = async () => {
    if (!meals.length) return
    setBusy(true)
    try {
      const r = await api.get<Suggestion>('/api/coach/plan/suggest?' + q({ from, days, meals: meals.join(','), leftovers, seed: Math.floor(Math.random() * 1e9) }))
      setRes(r); setSlots(r.slots)
      const saved = Number((() => { try { return localStorage.getItem(CAL_KEY) } catch { return null } })())
      setCal(c => c ?? r.calendars.find(x => x.id === saved)?.id ?? r.calendars[0]?.id ?? null)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  useEffect(() => { suggest() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  // Ein Gericht tauschen: neuer Vorschlag für genau diese Mahlzeit, der noch nicht in der Woche vorkommt
  const swap = async (i: number) => {
    const old = slots[i]
    for (let tries = 0; tries < 6; tries++) {
      try {
        const r = await api.get<Suggestion>('/api/coach/plan/suggest?' + q({ from: old.date, days: 1, meals: old.meal, leftovers: false, seed: Math.floor(Math.random() * 1e9) }))
        const pick = r.slots[0]
        if (pick && pick.recipe !== old.recipe && !slots.some(x => x.recipe === pick.recipe)) {
          setSlots(list => list.map((x, j) => j === i ? pick : x)); return
        }
      } catch (e) { fail(e); return }
    }
    notify(t('Kein anderes passendes Rezept gefunden.'))
  }

  const accept = async () => {
    if (cal == null || !slots.length) return
    setBusy(true)
    try {
      const r = await api.post<{ made: number }>('/api/coach/plan/accept', { calendar: cal, slots })
      try { localStorage.setItem(CAL_KEY, String(cal)) } catch { /* egal */ }
      notify(t('{n} Mahlzeiten in den Essensplan übernommen', { n: r.made }))
      setSlots([])
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  const byDay = slots.reduce<Record<string, { slot: Slot; i: number }[]>>((acc, slot, i) => { (acc[slot.date] ??= []).push({ slot, i }); return acc }, {})
  const excluded = Object.entries(res?.excluded ?? {})

  return (
    <div className={s.grid}>
      <div className={s.card}>
        <Field label={t('Ab')}><Input type="date" value={from} onChange={(_, d) => d.value && setFrom(d.value)} /></Field>
        <Field label={t('Tage')}><Input type="number" min={1} max={14} value={String(days)} onChange={(_, d) => { const n = Number(d.value); if (n >= 1 && n <= 14) setDays(n) }} /></Field>
        <Field label={t('Mahlzeiten')}>
          <div className={s.row}>{MEALS.map(m => <Checkbox key={m.key} label={m.label()} checked={meals.includes(m.key)}
            onChange={(_, d) => setMeals(l => d.checked ? [...l, m.key] : l.filter(x => x !== m.key))} />)}</div>
        </Field>
        <Switch checked={leftovers} label={t('Reste nutzen (Abendessen am nächsten Mittag)')} onChange={(_, d) => setLeftovers(d.checked)} />
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Button appearance="primary" icon={<Sparkle20Regular />} disabled={busy || !meals.length} onClick={suggest}>{t('Vorschlagen')}</Button>
          {/* Zu wenig eigene Rezepte? Neue aus dem Internet holen - danach gleich neu vorschlagen */}
          <RecipeSearchButton label={t('Neue Rezepte suchen')} onImported={() => { if (meals.length) suggest() }} />
        </div>
        {res && <Text size={200} className={s.muted}>
          {t('{n} von {all} Rezepten passen zu deinem Profil.', { n: res.usable, all: res.recipes })}
          {excluded.length > 0 && ' ' + t('Ausgelassen:') + ' ' + excluded.map(([r, n]) => `${reasonLabel(r)} ${n}`).join(', ')}
        </Text>}
        <Text size={200} className={s.muted}>{t('Vorgeschlagen werden nur Rezepte mit der Angabe „Kalorien je Portion“.')}</Text>
        <Text size={200} className={s.muted}>{t('Allergene erkennt der Plan an Wörtern in Titel und Zutaten – ein Hinweis, keine Garantie. Bitte Zutaten selbst prüfen.')}</Text>
      </div>

      <div className={s.card}>
        {!res ? <Spinner /> : res.recipes === 0
          ? <MessageBar intent="info"><MessageBarBody>{t('Noch keine Rezepte. Rezepte sind Notizen mit der Rezeptvorlage – am schnellsten über Notizen → „Rezept aus Link“.')}</MessageBarBody></MessageBar>
          : res.calendars.length === 0
            ? <MessageBar intent="info"><MessageBarBody>{t('Es gibt noch keinen Essensplan. Lege im Kalender einen Kalender der Art „Essensplan“ an.')}</MessageBarBody></MessageBar>
            : <>
              {slots.length === 0 && <Text className={s.muted}>{t('Alle gewählten Mahlzeiten sind schon geplant – oder es passt kein Rezept.')}</Text>}
              {Object.entries(byDay).map(([date, list]) => <div key={date}>
                <div className={s.day}>{new Date(date + 'T12:00:00').toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long' })}</div>
                {list.map(({ slot, i }) => <div key={i} className={s.slot}>
                  <span className={s.meal}>{MEALS.find(m => m.key === slot.meal)?.label() ?? slot.meal}</span>
                  {slot.recipe && cards[slot.recipe]?.image
                    ? <img className={s.img} src={`/api/notes/blob/${encodeURIComponent(cards[slot.recipe].image!)}?thumb=400`} alt="" loading="lazy" />
                    : <span className={s.img} />}
                  <span className={s.title}>{slot.title}{slot.leftover ? ' · ' + t('Reste') : ''}</span>
                  {slot.recipe && cards[slot.recipe]?.kcal ? <span className={s.kcal}>{t('{n} kcal je Portion', { n: Math.round(cards[slot.recipe].kcal!) })}</span> : null}
                  {!slot.leftover && <Button appearance="subtle" size="small" icon={<ArrowShuffle20Regular />} title={t('Anderes Rezept')} onClick={() => swap(i)} />}
                  <Button appearance="subtle" size="small" icon={<Dismiss16Regular />} title={t('Weglassen')} onClick={() => setSlots(l => l.filter((_, j) => j !== i))} />
                </div>)}
              </div>)}
              {slots.length > 0 && <div className={s.row} style={{ marginTop: 8 }}>
                <Dropdown value={res.calendars.find(c => c.id === cal)?.name ?? ''} selectedOptions={cal != null ? [String(cal)] : []}
                  onOptionSelect={(_, d) => setCal(Number(d.optionValue))}>
                  {res.calendars.map(c => <Option key={c.id} value={String(c.id)}>{c.name}</Option>)}
                </Dropdown>
                <Button appearance="primary" icon={<CalendarAdd20Regular />} disabled={busy || cal == null} onClick={accept}>{t('In den Essensplan übernehmen')}</Button>
              </div>}
            </>}
      </div>
    </div>
  )
}
