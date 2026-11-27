import { Checkbox, Spinner, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { AlertOn20Regular, CheckboxChecked20Regular, CheckboxUnchecked20Regular, Location16Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, CalEvent, Collection, featureOn, q } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { usePrefs } from '../prefs'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { artOf } from './arts'
import { BulkRemindDialog } from './BulkRemindDialog'

const useStyles = makeStyles({
  root: { flex: 1, minHeight: 0, overflowY: 'auto', padding: '8px 16px 24px' },
  head: { display: 'block', fontWeight: 600, color: tokens.colorNeutralForeground3, margin: '16px 0 4px', fontSize: '12px', textTransform: 'uppercase', letterSpacing: '.04em' },
  pick: { gridTemplateColumns: '32px 4px 170px minmax(0, 1fr)', '@media (max-width: 600px)': { gridTemplateColumns: '32px 4px minmax(0, 1fr)' } },
  pickCol: { '@media (max-width: 600px)': { gridColumn: '3' } },
  row: {
    display: 'grid', gridTemplateColumns: '4px 170px minmax(0, 1fr)', gap: '12px', alignItems: 'center', padding: '8px', borderRadius: '4px',
    cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    '@media (max-width: 600px)': { gridTemplateColumns: '4px minmax(0, 1fr)' },
  },
  bar: { alignSelf: 'stretch', borderRadius: '2px' },
  when: { color: tokens.colorNeutralForeground2, fontSize: '13px', '@media (max-width: 600px)': { gridColumn: '2' } },
  what: { minWidth: 0, '@media (max-width: 600px)': { gridColumn: '2' } },
  title: { display: 'block', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sub: { display: 'flex', gap: '8px', alignItems: 'center', color: tokens.colorNeutralForeground3, fontSize: '12px', overflow: 'hidden', whiteSpace: 'nowrap', minWidth: 0 },
  cut: { minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' },
})

/**
 * Trefferliste der Terminsuche: alle Wörter in Titel, Ort, Beschreibung, Kategorien oder Teilnehmern, ohne Zeitgrenze.
 * Kommende aufsteigend, Vergangene mit den neuesten zuerst; Serien als ein Treffer mit dem nächsten Vorkommen.
 */
export function CalendarSearch({ query, calendars, onOpen, taskLists = [], reload = 0 }: {
  query: string; calendars: Collection[]; onOpen: (e: CalEvent) => void
  /** Aufgabenlisten für „Aufgabe anlegen“ in der Mehrfachauswahl */
  taskLists?: Collection[]
  /** Hochzählen lädt die Treffer neu */
  reload?: number
}) {
  const s = useStyles()
  const { fail, notify } = useApp()
  // Mehrfachauswahl (calbulkremind): Kästchen je Zeile, Aktionen im Menüband
  const bulk = featureOn('calbulkremind')
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const [remindOpen, setRemindOpen] = useState(false)
  const [again, setAgain] = useState(0)
  const { prefs } = usePrefs()
  const [hits, setHits] = useState<CalEvent[] | null>(null)
  const ids = calendars.map(c => c.id).join(',')

  useEffect(() => {
    const needle = query.trim()
    if (!needle || !ids) { setHits([]); return }
    setHits(null)
    let stale = false
    const timer = setTimeout(() => {
      api.get<CalEvent[]>('/api/pim/events/search?' + q({ q: needle, calendars: ids }))
        .then(list => { if (!stale) setHits(list) }).catch(e => { if (!stale) { setHits([]); fail(e) } })
    }, 300)
    return () => { stale = true; clearTimeout(timer) }
  }, [query, ids, reload, again])
  useEffect(() => { setPicked(new Set()) }, [query])

  const cal = (id: number) => calendars.find(c => c.id === id)
  const now = Date.now()
  const when = (e: CalEvent) => {
    const start = new Date(e.allDay ? e.start + 'T00:00:00' : e.start)
    const day = start.toLocaleDateString(locale, { weekday: 'short', day: 'numeric', month: 'short', year: 'numeric' })
    if (e.allDay) return day + ' · ' + t('ganztägig')
    return day + ' · ' + start.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit', hour12: prefs.timeFormat === '12' })
  }
  const ended = (e: CalEvent) => new Date(e.allDay ? e.end + 'T00:00:00' : e.end).getTime() <= now

  // Nur Termine, deren Erinnerungen sich ändern lassen: eigene, abonnierte, mit Bearbeitungsrecht geteilte
  const pickable = (e: CalEvent) => { const c = cal(e.calendar); return !!c && (c.canWrite !== false || (!!c.subscription && c.id > 0)) }
  const all = (hits ?? []).filter(pickable)
  const chosen = all.filter(e => picked.has(e.id))
  const toggle = (e: CalEvent) => setPicked(p => { const n = new Set(p); if (n.has(e.id)) n.delete(e.id); else n.add(e.id); return n })
  const actions = bulk && <RibbonActions>
    <RibbonButton icon={chosen.length === all.length && all.length ? <CheckboxChecked20Regular /> : <CheckboxUnchecked20Regular />}
      label={chosen.length === all.length && all.length ? t('Markierung aufheben') : t('Alle markieren')} disabled={!all.length}
      onClick={() => setPicked(chosen.length === all.length ? new Set() : new Set(all.map(e => e.id)))} />
    <RibbonButton icon={<AlertOn20Regular />} label={t('Erinnerung setzen …')} disabled={!chosen.length} onClick={() => setRemindOpen(true)} />
  </RibbonActions>
  const dialog = remindOpen && <BulkRemindDialog events={chosen} calendars={calendars} taskLists={taskLists} onClose={r => {
    setRemindOpen(false)
    if (!r) return
    notify(r.changed === 1 ? t('Erinnerung bei 1 Termin gesetzt') : t('Erinnerung bei {n} Terminen gesetzt', { n: r.changed }),
      r.skipped ? t('{n} nicht geändert (nur lesbar oder nicht mehr vorhanden)', { n: r.skipped }) : undefined, r.skipped ? 'warning' : undefined)
    setPicked(new Set()); setAgain(n => n + 1)
  }} />

  if (hits === null) return <div className={s.root}>{actions}<Spinner size="small" /></div>
  if (hits.length === 0) return <div className={s.root}>{actions}<Text>{t('Keine Termine gefunden.')}</Text></div>
  const upcoming = hits.filter(e => !ended(e)), past = hits.filter(ended)

  return (
    <div className={s.root}>
      {actions}{dialog}
      {bulk && chosen.length > 0 && <Text size={200} block style={{ color: tokens.colorNeutralForeground3, marginTop: 8 }}>{t('{n} markiert', { n: chosen.length })}</Text>}
      {([[t('Kommende'), upcoming], [t('Vergangene'), past]] as const).map(([label, list]) => list.length > 0 && (
        <div key={label}>
          <Text className={s.head}>{label}</Text>
          {list.map(e => {
            const c = cal(e.calendar)
            return (
              <div key={e.id} className={bulk ? mergeClasses(s.row, s.pick) : s.row} role="button" tabIndex={0} onClick={() => onOpen(e)}
                onKeyDown={k => { if (k.key === 'Enter') onOpen(e); if (bulk && k.key === ' ' && pickable(e)) { k.preventDefault(); toggle(e) } }}>
                {bulk && <span onClick={x => x.stopPropagation()}>
                  <Checkbox checked={picked.has(e.id)} disabled={!pickable(e)} onChange={() => toggle(e)} aria-label={t('Auswählen')} />
                </span>}
                <span className={s.bar} style={{ backgroundColor: c?.color || tokens.colorBrandBackground }} />
                <span className={mergeClasses(s.when, bulk && s.pickCol)}>{when(e)}</span>
                <span className={mergeClasses(s.what, bulk && s.pickCol)}>
                  <Text className={s.title} weight="semibold" dir="auto" title={e.title || undefined}>{(artOf(e.categories) ? artOf(e.categories)!.icon + ' ' : '') + (e.title || t('(Ohne Titel)'))}{e.recurring ? ' ↻' : ''}</Text>
                  <span className={s.sub}>
                    {e.location && <span style={{ display: 'flex', alignItems: 'center', gap: 2, minWidth: 0, flexShrink: 1 }} title={e.location}><Location16Regular style={{ flexShrink: 0 }} /><span className={s.cut}>{e.location}</span></span>}
                    {c && <span className={s.cut} title={c.name}>{c.name}</span>}
                  </span>
                </span>
              </div>
            )
          })}
        </div>
      ))}
      {hits.length >= 200 && <Text size={200} style={{ display: 'block', marginTop: 12, color: tokens.colorNeutralForeground3 }}>{t('Es werden die ersten 200 Treffer angezeigt. Ein genauerer Suchbegriff grenzt die Liste ein.')}</Text>}
    </div>
  )
}
