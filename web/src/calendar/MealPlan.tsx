import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { Add16Regular, ChevronLeft20Regular, ChevronRight20Regular, Book16Regular, Delete16Regular, Cart20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, CalEvent, Collection, q } from '../api'
import { composeItem, guessSection, parseItem } from '../tasks/shopParse'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import type { PlanProps } from './kinds'

/** Mahlzeiten: der Schluessel steht als Kategorie im Termin (sprachunabhaengig), die Uhrzeit nur fuer andere Kalenderprogramme. */
const MEALS = [
  { key: 'Frühstück', label: () => t('Frühstück'), hour: 8 },
  { key: 'Mittagessen', label: () => t('Mittagessen'), hour: 12 },
  { key: 'Abendessen', label: () => t('Abendessen'), hour: 18 },
]
const CAL_KEY = 'dmoffice.mealplan.calendar'

interface Recipe { id: number; syncId: string; title: string; folder?: string | null; db?: string | null }
type Slot = { day: Date; meal: string; ev?: CalEvent }

const useStyles = makeStyles({
  root: { flex: 1, minHeight: 0, overflow: 'auto', padding: '8px 12px 16px', display: 'flex', flexDirection: 'column', gap: '8px' },
  bar: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  grid: { display: 'grid', gridTemplateColumns: 'minmax(90px, 120px) repeat(3, minmax(0, 1fr))', gap: '1px', backgroundColor: tokens.colorNeutralStroke2, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: '6px', overflow: 'hidden', '@media (max-width: 767px)': { gridTemplateColumns: 'minmax(56px, 70px) repeat(3, minmax(0, 1fr))' } },
  head: { backgroundColor: tokens.colorNeutralBackground2, padding: '6px 8px', fontWeight: 600 },
  day: { backgroundColor: tokens.colorNeutralBackground2, padding: '6px 8px' },
  today: { backgroundColor: tokens.colorBrandBackground2 },
  over: { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: '-2px' },
  cell: { backgroundColor: tokens.colorNeutralBackground1, padding: '4px', minHeight: '56px', display: 'flex', flexDirection: 'column', gap: '3px' },
  entry: { display: 'flex', alignItems: 'center', gap: '4px', textAlign: 'left', border: 'none', borderRadius: '4px', padding: '3px 6px', cursor: 'pointer', color: 'white', fontSize: '13px', minWidth: 0 },
  name: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  add: { marginTop: 'auto', alignSelf: 'flex-start', opacity: 0.5, ':hover': { opacity: 1 } },
  hint: { color: tokens.colorNeutralForeground3 },
})

const mondayOf = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate() - ((d.getDay() + 6) % 7))
const addDays = (d: Date, n: number) => new Date(d.getFullYear(), d.getMonth(), d.getDate() + n)
const sameDay = (a: Date, b: Date) => a.toDateString() === b.toDateString()
const mealOf = (e: CalEvent) => MEALS.find(m => e.categories.includes(m.key))
const calName = (c: Collection) => c.owner ? t('{name} (von {owner})', { name: c.name, owner: c.owner }) : c.name

/**
 * Essensplan: Woche mit Fruehstueck, Mittag und Abend aus allen eingeschalteten Kalendern der Art Essensplan, jeder Eintrag in der Farbe
 * seines Kalenders. Eintraege sind gewoehnliche Termine (auch in geteilten Kalendern) – Freitext oder Rezept aus den Notizen.
 * calendars: die Essensplan-Kalender, die gerade eingeschaltet sind; total: wie viele es ueberhaupt gibt.
 */
export function MealPlan({ calendars, total, onCreate }: PlanProps) {
  const s = useStyles()
  const { fail } = useApp()
  const cals = calendars
  const ids = cals.map(c => c.id).join(',')
  const writable = cals.filter(c => c.canWrite !== false)
  const canWrite = writable.length > 0
  const calOf = (id: number) => cals.find(c => c.id === id)
  const [week, setWeek] = useState(() => mondayOf(new Date()))
  const [events, setEvents] = useState<CalEvent[]>([])
  const [recipes, setRecipes] = useState<Recipe[]>([])
  const [slot, setSlot] = useState<Slot | null>(null)
  const [shopping, setShopping] = useState(false)
  // Eintrag auf einen anderen Tag oder eine andere Mahlzeit ziehen
  const [drag, setDrag] = useState<CalEvent | null>(null)
  const [over, setOver] = useState<string | null>(null)

  useEffect(() => { api.get<Recipe[]>('/api/notes/recipes').then(setRecipes).catch(() => setRecipes([])) }, [])
  const load = useCallback(() => {
    if (!ids) { setEvents([]); return }
    api.get<CalEvent[]>('/api/pim/events?' + q({ from: week.toISOString(), to: addDays(week, 7).toISOString(), calendars: ids }))
      .then(list => setEvents(list.filter(e => mealOf(e)))).catch(fail)
  }, [ids, week]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { load() }, [load])
  // Aenderungen anderer Familienmitglieder: beim Zurueckkehren ins Fenster neu holen
  useEffect(() => {
    const on = () => document.visibilityState === 'visible' && load()
    document.addEventListener('visibilitychange', on)
    return () => document.removeEventListener('visibilitychange', on)
  }, [load])

  const days = useMemo(() => Array.from({ length: 7 }, (_, i) => addDays(week, i)), [week])
  // Rezepte aus einer anderen Datenbank haben hier keine Notiz zum Oeffnen (id 0)
  const noteId = (e: CalEvent) => { const r = recipes.find(x => x.syncId === e.recipe); return r && r.id > 0 ? r.id : undefined }
  const colorOf = (id: number) => calOf(id)?.color || '#0F6CBD'
  const movable = (e: CalEvent) => calOf(e.calendar)?.canWrite !== false
  const moveTo = async (e: CalEvent, day: Date, meal: (typeof MEALS)[number]) => {
    if (sameDay(new Date(e.start), day) && mealOf(e)?.key === meal.key) return
    const at = (h: number) => new Date(day.getFullYear(), day.getMonth(), day.getDate(), h).toISOString()
    try {
      await api.post('/api/pim/events', {
        calendar: e.calendar, uri: e.uri, etag: e.etag, title: e.title, start: at(meal.hour), end: at(meal.hour + 1), allDay: false, busy: false,
        categories: [meal.key], reminder: null, recipe: e.recipe ?? '', scope: 'series',
      })
      load()
    } catch (err) { fail(err); load() }
  }

  return (
    <div className={s.root}>
      <div className={s.bar}>
        <Button appearance="subtle" icon={<ChevronLeft20Regular />} aria-label={t('Vorherige Woche')} onClick={() => setWeek(addDays(week, -7))} />
        <Button appearance="subtle" icon={<ChevronRight20Regular />} aria-label={t('Nächste Woche')} onClick={() => setWeek(addDays(week, 7))} />
        <Button onClick={() => setWeek(mondayOf(new Date()))}>{t('Diese Woche')}</Button>
        <Text weight="semibold" style={{ marginRight: 'auto' }}>
          {week.toLocaleDateString(locale, { day: 'numeric', month: 'short' })} – {addDays(week, 6).toLocaleDateString(locale, { day: 'numeric', month: 'short', year: 'numeric' })}
        </Text>
        <Button icon={<Cart20Regular />} disabled={!events.some(e => e.recipe)} onClick={() => setShopping(true)}>{t('Zutaten auf die Einkaufsliste')}</Button>
      </div>
      {total === 0 ? (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, alignItems: 'flex-start' }}>
          <Text className={s.hint}>{t('Es gibt noch keinen Kalender der Art „Essensplan“. Lege einen an und teile ihn mit deiner Familie – oder stelle bei einem vorhandenen Kalender die Art auf „Essensplan“.')}</Text>
          <Button appearance="primary" icon={<Add16Regular />} onClick={onCreate}>{t('Essensplan-Kalender anlegen')}</Button>
        </div>
      ) : cals.length === 0 ? <Text className={s.hint}>{t('Schalte links einen Essensplan-Kalender ein.')}</Text> : (
        <>
          <div className={s.grid}>
            <div className={s.head} />
            {MEALS.map(m => <div key={m.key} className={s.head}>{m.label()}</div>)}
            {days.map(d => [
              <div key={'d' + d.toISOString()} className={`${s.day} ${sameDay(d, new Date()) ? s.today : ''}`}>
                <div style={{ fontWeight: 600 }}>{d.toLocaleDateString(locale, { weekday: 'short' })}</div>
                <div className={s.hint}>{d.toLocaleDateString(locale, { day: 'numeric', month: 'numeric' })}</div>
              </div>,
              ...MEALS.map(m => {
                const here = events.filter(e => mealOf(e)?.key === m.key && sameDay(new Date(e.start), d))
                return (
                  <div key={d.toISOString() + m.key} className={mergeClasses(s.cell, over === d.toISOString() + m.key && s.over)}
                    onDragOver={ev => { if (drag) { ev.preventDefault(); setOver(d.toISOString() + m.key) } }}
                    onDragLeave={() => setOver(o => (o === d.toISOString() + m.key ? null : o))}
                    onDrop={ev => { ev.preventDefault(); const e = drag; setDrag(null); setOver(null); if (e) moveTo(e, d, m) }}>
                    {here.map(e => (
                      <button key={e.id} className={s.entry} draggable={movable(e)} style={{ backgroundColor: colorOf(e.calendar) }}
                        onDragStart={ev => { ev.dataTransfer.effectAllowed = 'move'; ev.dataTransfer.setData('text/plain', e.title); setDrag(e) }}
                        onDragEnd={() => { setDrag(null); setOver(null) }} title={cals.length > 1 ? `${e.title} · ${calOf(e.calendar)?.name ?? ''}` : e.title} onClick={() => setSlot({ day: d, meal: m.key, ev: e })}>
                        {e.recipe && <Book16Regular style={{ flexShrink: 0 }} />}
                        <span className={s.name}>{e.title}</span>
                      </button>
                    ))}
                    {canWrite && <Button className={s.add} size="small" appearance="transparent" icon={<Add16Regular />} aria-label={t('Eintragen')} onClick={() => setSlot({ day: d, meal: m.key })} />}
                  </div>
                )
              }),
            ])}
          </div>
          {!canWrite && <Text className={s.hint}>{cals.length > 1 ? t('Die Besitzer haben diese Kalender nur zum Lesen mit dir geteilt.') : t('Der Besitzer hat diesen Kalender nur zum Lesen mit dir geteilt.')}</Text>}
          {recipes.length === 0 && <Text className={s.hint}>{t('Rezepte kommen aus deinen Notizen mit der Rezeptvorlage von notizi. Es wurde noch keine gefunden.')}</Text>}
        </>
      )}
      {shopping && <ShoppingDialog events={events.filter(e => e.recipe)} onClose={() => setShopping(false)} />}
      {slot && <MealDialog slot={slot} targets={writable} cal={slot.ev ? calOf(slot.ev.calendar) : undefined} recipes={recipes} noteId={slot.ev ? noteId(slot.ev) : undefined} onClose={changed => { setSlot(null); if (changed) load() }} />}
    </div>
  )
}

function MealDialog({ slot, cal, targets, recipes, noteId, onClose }: {
  slot: Slot; cal?: Collection; targets: Collection[]; recipes: Recipe[]; noteId?: number; onClose: (changed: boolean) => void
}) {
  const { fail } = useApp()
  const meal = MEALS.find(m => m.key === slot.meal)!
  const [title, setTitle] = useState(slot.ev?.title ?? '')
  const [recipe, setRecipe] = useState<string>(slot.ev?.recipe ?? '')
  const [busy, setBusy] = useState(false)
  const hits = useMemo(() => {
    const w = title.trim().toLowerCase()
    return recipes.filter(r => !w || r.title.toLowerCase().includes(w)).slice(0, 8)
  }, [recipes, title])
  // Neuer Eintrag: Kalender waehlen (Vorgabe: zuletzt benutzt, sonst der erste beschreibbare); bestehender bleibt in seinem
  const [target, setTarget] = useState<Collection | undefined>(() =>
    cal ?? targets.find(c => c.id === Number(localStorage.getItem(CAL_KEY))) ?? targets[0])
  const ro = !target || target.canWrite === false
  const save = async () => {
    if (!title.trim()) return
    setBusy(true)
    try {
      const at = (h: number) => new Date(slot.day.getFullYear(), slot.day.getMonth(), slot.day.getDate(), h).toISOString()
      await api.post('/api/pim/events', {
        calendar: target!.id, uri: slot.ev?.uri, etag: slot.ev?.etag, title: title.trim(),
        start: slot.ev ? slot.ev.start : at(meal.hour), end: slot.ev ? slot.ev.end : at(meal.hour + 1), allDay: false, busy: false,
        categories: [meal.key], reminder: null, recipe, scope: 'series',
      })
      if (!slot.ev) try { localStorage.setItem(CAL_KEY, String(target!.id)) } catch { /* egal */ }
      onClose(true)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const remove = async () => {
    if (!slot.ev) return
    try { await api.del(`/api/pim/events/${slot.ev.calendar}/${encodeURIComponent(slot.ev.uri)}`); onClose(true) } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>{meal.label()} · {slot.day.toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long' })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Field label={t('Was gibt es? (Text oder Rezept wählen)')}>
              <Input autoFocus value={title} disabled={ro} onChange={(_, v) => { setTitle(v.value); setRecipe('') }}
                onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); save() } }} />
            </Field>
            {!slot.ev && targets.length > 1 && (
              <Field label={t('Eintragen in Kalender')}>
                <Dropdown value={target ? calName(target) : ''} selectedOptions={[String(target?.id)]} onOptionSelect={(_, d) => setTarget(targets.find(c => c.id === Number(d.optionValue)))}>
                  {targets.map(c => <Option key={c.id} value={String(c.id)} text={calName(c)}>{calName(c)}</Option>)}
                </Dropdown>
              </Field>
            )}
            {!ro && hits.length > 0 && (
              <div style={{ display: 'flex', flexDirection: 'column', maxHeight: 220, overflow: 'auto' }}>
                {hits.map(r => (
                  <Button key={r.syncId} appearance={recipe === r.syncId ? 'primary' : 'subtle'} icon={<Book16Regular />} style={{ justifyContent: 'flex-start' }}
                    onClick={() => { setTitle(r.title); setRecipe(r.syncId) }}>
                    <span style={{ textAlign: 'left' }}>{r.title}{r.folder ? <span style={{ opacity: 0.6 }}> · {r.folder.split(' / ').slice(-1)[0]}</span> : null}{r.db ? <span style={{ opacity: 0.6 }}> · {r.db}</span> : null}</span>
                  </Button>
                ))}
              </div>
            )}
            {noteId != null && <Button appearance="transparent" icon={<Book16Regular />} style={{ alignSelf: 'flex-start' }} onClick={() => { location.hash = `#/notes/${noteId}` }}>{t('Rezept öffnen')}</Button>}
          </DialogContent>
          <DialogActions>
            {slot.ev && !ro && <Button icon={<Delete16Regular />} onClick={remove} style={{ marginRight: 'auto' }}>{t('Löschen')}</Button>}
            <Button onClick={() => onClose(false)}>{ro ? t('Schließen') : t('Abbrechen')}</Button>
            {!ro && <Button appearance="primary" disabled={busy || !title.trim()} onClick={save}>{t('Speichern')}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

interface Ingredients { syncId: string; title: string; items: string[] }

/** Zutaten der Rezepte dieser Woche, zum Abhaken, auf eine Einkaufsliste. */
function ShoppingDialog({ events, onClose }: { events: CalEvent[]; onClose: () => void }) {
  const { fail, notify } = useApp()
  const [lists, setLists] = useState<Collection[] | null>(null)
  const [listId, setListId] = useState<number | null>(null)
  const [groups, setGroups] = useState<{ title: string; items: { text: string; on: boolean }[] }[] | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    api.get<Collection[]>('/api/pim/collections/calendars').then(all => {
      const shop = all.filter(c => c.listType === 'shop' && c.canWrite !== false)
      setLists(shop)
      setListId(shop[0]?.id ?? null)
    }).catch(fail)
    const ids = [...new Set(events.map(e => e.recipe!))]
    api.post<Ingredients[]>('/api/notes/recipes/ingredients', { ids }).then(found => {
      // Ein Rezept, das mehrmals in der Woche auf dem Plan steht, kommt mehrmals auf die Liste
      setGroups(events.flatMap(e => {
        const r = found.find(x => x.syncId === e.recipe)
        return r ? [{ title: r.title, items: r.items.map(text => ({ text, on: true })) }] : []
      }))
    }).catch(fail)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const toggle = (g: number, i: number, on: boolean) => setGroups(gs => gs!.map((x, a) => a !== g ? x : { ...x, items: x.items.map((y, b) => b === i ? { ...y, on } : y) }))
  const chosen = (groups ?? []).flatMap(g => g.items.filter(x => x.on).map(x => x.text))
  const add = async () => {
    if (listId == null) return
    setBusy(true)
    try {
      const meta = await api.get<{ sections: string[] }>(`/api/pim/lists/${listId}`).catch(() => ({ sections: [] as string[] }))
      for (const text of chosen) {
        const x = parseItem(text)
        await api.post('/api/pim/tasks', { list: listId, title: composeItem(x), ...x, section: guessSection(x.item, meta.sections), dueAllDay: true })
      }
      notify(chosen.length === 1 ? t('1 Eintrag auf die Liste gesetzt') : t('{n} Einträge auf die Liste gesetzt', { n: chosen.length }))
      onClose()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{t('Zutaten auf die Einkaufsliste')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 6, maxHeight: '60vh', overflow: 'auto' }}>
            {lists && lists.length === 0 && <Text>{t('Lege zuerst unter „Aufgaben“ eine Einkaufsliste an.')}</Text>}
            {lists && lists.length === 1 && <Text>{t('Auf die Liste „{name}“', { name: lists[0].owner ? t('{name} (von {owner})', { name: lists[0].name, owner: lists[0].owner }) : lists[0].name })}</Text>}
            {lists && lists.length > 1 && (
              <Field label={t('Einkaufsliste')}>
                <Dropdown value={lists.find(l => l.id === listId)?.name ?? ''} selectedOptions={[String(listId)]} onOptionSelect={(_, d) => setListId(Number(d.optionValue))}>
                  {lists.map(l => <Option key={l.id} value={String(l.id)} text={l.name}>{l.owner ? t('{name} (von {owner})', { name: l.name, owner: l.owner }) : l.name}</Option>)}
                </Dropdown>
              </Field>
            )}
            {groups && groups.length === 0 && <Text>{t('In den Rezepten dieser Woche stehen keine Zutaten mit Kästchen.')}</Text>}
            {groups?.map((g, a) => (
              <div key={a}>
                <Text weight="semibold">{g.title}</Text>
                {g.items.length === 0 && <div><Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('In diesem Rezept stehen keine Zutaten.')}</Text></div>}
                {g.items.map((x, b) => <div key={b}><Checkbox checked={x.on} label={x.text} onChange={(_, d) => toggle(a, b, !!d.checked)} /></div>)}
              </div>
            ))}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || listId == null || chosen.length === 0} onClick={add}>{t('Hinzufügen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
