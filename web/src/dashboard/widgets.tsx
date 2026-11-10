// Die Widgets der Übersicht: jedes holt seine Daten selbst (useWidgetData), Aktionen laufen über die Endpunkte der Bereiche.
import { Button, Checkbox, Input, Spinner, Text, Tooltip, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { ArrowRight16Regular, Checkmark16Regular, Dismiss16Regular, Send16Regular, Star16Filled } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { parseNaturalDate } from '../shell/naturalDate'
import { taskDue } from '../shell/DateHint'
import { fromDevice } from '../chat/store'
import { composeItem, parseItem } from '../tasks/shopParse'
import { usePrefs } from '../prefs'
import { KindIcon, Thumb, hasThumb, kindOf } from '../files/common'
import { gallerySource, openPhotoFromElsewhere } from '../files/GalleryFolders'
import { UserAvatar } from '../chat/UserAvatar'
import { Widget, dashboardApi, useWidgetData } from './api'

export const useWidgetStyles = makeStyles({
  head: { display: 'flex', alignItems: 'center', gap: '8px', fontSize: tokens.fontSizeBase200, fontWeight: 600, color: tokens.colorBrandForeground2, marginBottom: '8px', letterSpacing: '0.2px' },
  mut: { color: tokens.colorNeutralForeground3 },
  row: { display: 'flex', alignItems: 'center', gap: '8px', minWidth: 0, cursor: 'pointer', borderRadius: tokens.borderRadiusMedium, padding: '2px 4px', margin: '0 -4px', ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover } },
  ell: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  big: { fontSize: '40px', fontWeight: 600, lineHeight: 1 },
  bar: { width: '4px', borderRadius: '2px', alignSelf: 'stretch', flexShrink: 0 },
  col: { display: 'flex', flexDirection: 'column', gap: '6px', minHeight: 0, flex: 1 },
  scroll: { overflowY: 'auto', overflowX: 'hidden', minHeight: 0, flex: 1 },
  input: { width: '100%' },
  chip: { display: 'inline-flex', alignItems: 'center', borderRadius: '999px', padding: '2px 9px', fontSize: tokens.fontSizeBase200, backgroundColor: tokens.colorNeutralBackground3, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground3Hover } },
  chipOn: { backgroundColor: tokens.colorBrandBackground2 },
  photo: { borderRadius: tokens.borderRadiusLarge, overflow: 'hidden', backgroundColor: tokens.colorNeutralBackground3, position: 'relative', cursor: 'pointer' },
  img: { width: '100%', height: '100%', objectFit: 'cover', display: 'block' },
  tiny: { fontSize: tokens.fontSizeBase100 },
})

export const WIDGET_INFO: Record<string, { label: () => string; hint: () => string; size: 1 | 2 | 3 | 4 }> = {
  today: { label: () => t('Heute'), hint: () => t('Begrüßung, Datum, Wetter jetzt'), size: 2 },
  events: { label: () => t('Kalender'), hint: () => t('mehrmals, je mit eigenen Kalendern'), size: 3 },
  meals: { label: () => t('Essensplan'), hint: () => t('heute oder die Woche'), size: 2 },
  tasks: { label: () => t('Aufgaben'), hint: () => t('eine Liste oder alle fälligen'), size: 3 },
  shopping: { label: () => t('Einkaufsliste'), hint: () => t('zum Abhaken'), size: 1 },
  mail: { label: () => t('E-Mail'), hint: () => t('alle, nur Relevante, eine Kategorie'), size: 1 },
  chat: { label: () => t('Chat'), hint: () => t('Ungelesenes'), size: 1 },
  pukki: { label: () => t('Pukki'), hint: () => t('fragen und Überblick'), size: 2 },
  weather: { label: () => t('Wetter'), hint: () => t('heute und 7 Tage'), size: 2 },
  news: { label: () => t('Schlagzeilen'), hint: () => t('eigene Feeds'), size: 2 },
  photos: { label: () => t('Fotos'), hint: () => t('An diesem Tag'), size: 2 },
  notes: { label: () => t('Notizen'), hint: () => t('Favoriten oben'), size: 2 },
  files: { label: () => t('Dateien'), hint: () => t('Favoriten, zuletzt'), size: 2 },
  birthdays: { label: () => t('Geburtstage'), hint: () => t('aus den Kontakten'), size: 1 },
  countdown: { label: () => t('Countdown'), hint: () => t('noch X Tage bis …'), size: 1 },
  bookmarks: { label: () => t('Lesezeichen'), hint: () => t('deine Schnellzugriffe'), size: 1 },
  storage: { label: () => t('Speicher'), hint: () => t('Belegung'), size: 1 },
  system: { label: () => t('System'), hint: () => t('Sicherung, Speicher, Platz (Verwaltung)'), size: 2 },
  stack: { label: () => t('Stapel'), hint: () => t('mehrere Widgets zum Durchwischen'), size: 2 },
}

const day = (iso: string) => new Date(iso + 'T12:00:00')
const go = (hash: string) => { location.hash = hash }
const isoToday = () => new Date().toLocaleDateString('sv-SE')

function Head({ label, icon, onClick }: { label: string; icon?: React.ReactNode; onClick?: () => void }) {
  const s = useWidgetStyles()
  return <div className={s.head} style={onClick ? { cursor: 'pointer' } : undefined} onClick={onClick}>{icon}{label}{onClick && <ArrowRight16Regular style={{ marginLeft: 'auto', opacity: .6 }} />}</div>
}
function Empty({ text }: { text: string }) { const s = useWidgetStyles(); return <Text size={200} className={s.mut}>{text}</Text> }
/** Leerzustand mit Kopfzeile, damit man sieht, welches Widget das ist. */
function Blank({ label, text, to }: { label: string; text: string; to?: string }) { const s = useWidgetStyles(); return <div className={s.col}><Head label={label} onClick={to ? () => go(to) : undefined} /><Empty text={text} /></div> }
function Loading({ error }: { error: string | null }) { const s = useWidgetStyles(); return error ? <Text size={200} className={s.mut}>{error}</Text> : <Spinner size="tiny" /> }

// ---------------- Heute ----------------

export function TodayWidget({ w }: { w: Widget }) {
  const s = useWidgetStyles()
  const { data, error } = useWidgetData<{ greeting: string; date: string; weather?: { place?: string; now?: { temp: number; code: number }; today?: { text: string; max: number; min: number } } | null }>('today', w.opts, 300_000)
  if (!data) return <Loading error={error} />
  return (
    <div style={{ display: 'flex', alignItems: 'center', gap: 16, height: '100%' }}>
      <div style={{ flex: 1, minWidth: 0 }}>
        {/* Begrüßung („Guten Morgen, Anna“) und Datum in der Sprache der Oberfläche */}
        <div style={{ fontSize: 24, fontWeight: 600, lineHeight: 1.2 }}>{(([g, ...name]) => [t(g), ...name].join(', '))(data.greeting.split(', '))}</div>
        <div className={s.mut} style={{ fontSize: 14 }}>{new Date().toLocaleDateString(locale, { weekday: 'long', day: 'numeric', month: 'long' })}</div>
      </div>
      {data.weather?.now && (
        <div style={{ textAlign: 'right', cursor: 'pointer' }} onClick={() => go('#/settings/chat')}>
          <div style={{ fontSize: 34, fontWeight: 300, lineHeight: 1 }}>{Math.round(data.weather.now.temp)}°</div>
          <div className={s.mut} style={{ fontSize: 12 }}>{data.weather.today?.text}{data.weather.place ? ' · ' + data.weather.place : ''}</div>
        </div>
      )}
    </div>
  )
}

// ---------------- Kalender ----------------

interface EventsData {
  days: { day: string; label: string; weekday: string; items: { title: string; start: string | null; end: string | null; allDay: boolean; calendar: string | null; icon: string | null; now: boolean; inMinutes: number | null; uri?: string }[] }[]
  calendars: { id: number; name: string; color: string | null; shared: boolean }[]
  invites: { message: number; text: string | null; created: string; actions: { key: string; label: string }[] }[]
}
export function EventsWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  const { fail } = useApp()
  const { data, error, reload } = useWidgetData<EventsData>('events', w.opts)
  if (!data) return <Loading error={error} />
  const first = data.days[0]
  const d0 = day(first.day)
  const colorOf = (name: string | null) => data.calendars.find(c => c.name === name)?.color ?? tokens.colorBrandBackground
  const answer = async (id: number, key: string) => { try { await api.post(`/api/chat/messages/${id}/actions/${key}`); reload() } catch (e) { fail(e) } }
  const soon = (m: number | null) => m == null ? null : m < 60 ? t('in {n} Min.', { n: m }) : t('in {n} Std.', { n: Math.round(m / 60) })
  const Item = ({ e, d }: { e: EventsData['days'][0]['items'][0]; d: string }) => (
    <div className={s.row} onClick={ev => { ev.stopPropagation(); go(e.uri ? `#/calendar/event/${encodeURIComponent(e.uri)}/${d}` : '#/calendar/' + d) }}>
      <div className={s.bar} style={{ background: colorOf(e.calendar), minHeight: 28 }} />
      <div style={{ minWidth: 0, flex: 1 }}>
        <div className={s.ell} style={{ fontWeight: 600, fontSize: 13 }}>{e.icon ? e.icon + ' ' : ''}{e.title}</div>
        <div className={mergeClasses(s.mut, s.ell)} style={{ fontSize: 12 }}>
          {e.allDay ? t('ganztägig') : `${e.start}–${e.end}`}{e.calendar ? ' · ' + e.calendar : ''}
          {e.now && <span style={{ color: tokens.colorPaletteGreenForeground1 }}> · {t('jetzt')}</span>}
          {!e.now && soon(e.inMinutes) && <span style={{ color: tokens.colorBrandForeground1 }}> · {soon(e.inMinutes)}</span>}
        </div>
      </div>
    </div>
  )
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <div style={{ display: 'flex', gap: 14, alignItems: 'flex-start', cursor: 'pointer' }} onClick={() => go('#/calendar/' + first.day)}>
        <div style={{ textAlign: 'center', minWidth: 54 }}>
          <div style={{ fontSize: 12, fontWeight: 700, color: tokens.colorPaletteRedForeground1, textTransform: 'uppercase' }}>{d0.toLocaleDateString(locale, { weekday: 'short' })}</div>
          <div style={{ fontSize: 44, fontWeight: 300, lineHeight: 1 }}>{d0.getDate()}</div>
        </div>
        <div className={s.col} style={{ paddingTop: 4 }}>
          {first.items.length === 0 ? <Empty text={t('Keine Termine.')} /> : first.items.slice(0, big ? 6 : 3).map((e, i) => <Item key={i} e={e} d={first.day} />)}
        </div>
      </div>
      {data.invites.length > 0 && data.invites.slice(0, 2).map(inv => (
        <div key={inv.message} style={{ background: tokens.colorBrandBackground2, borderRadius: 10, padding: '8px 10px', display: 'flex', flexDirection: 'column', gap: 6 }}>
          <Text size={200} weight="semibold">{t('Einladung')}</Text>
          <Text size={200} style={{ whiteSpace: 'pre-line' }}>{(inv.text ?? '').split('\n').slice(0, 3).join('\n')}</Text>
          <div style={{ display: 'flex', gap: 6 }}>{inv.actions.map(a => <Button key={a.key} size="small" appearance={a.key === 'inv-accept' ? 'primary' : 'secondary'} onClick={() => answer(inv.message, a.key)}>{a.label}</Button>)}</div>
        </div>
      ))}
      {big && <div className={s.scroll}>
        {data.days.slice(1).map(dd => (
          <div key={dd.day} style={{ marginTop: 8 }}>
            <div className={s.mut} style={{ fontSize: 11, fontWeight: 600, textTransform: 'uppercase', marginBottom: 4 }}>{dd.label} · {dd.weekday}</div>
            {dd.items.length === 0 ? <Text size={200} className={s.mut}>–</Text> : dd.items.slice(0, 4).map((e, i) => <Item key={i} e={e} d={dd.day} />)}
          </div>
        ))}
      </div>}
    </div>
  )
}

// ---------------- Essensplan ----------------

export function MealsWidget({ w }: { w: Widget }) {
  const s = useWidgetStyles()
  const { fail, notify } = useApp()
  const { data, error } = useWidgetData<{ days: { day: string; label: string; weekday: string; items: { title: string; meal: string | null; calendar: number; uri: string; recipe: string | null }[] }[]; plans: { id: number; name: string }[] }>('meals', w.opts, 300_000)
  if (!data) return <Loading error={error} />
  if (data.plans.length === 0) return <Blank label={w.title || t('Essensplan')} text={t('Kein Essensplan. Lege im Kalender einen Kalender der Art „Essensplan“ an.')} to="#/calendar" />
  const toShop = async (recipe: string) => {
    try {
      const r = await api.post<{ syncId: string; title: string; items: string[] }[]>('/api/notes/recipes/ingredients', { ids: [recipe] })
      const lists = await api.get<any[]>('/api/pim/collections/calendars')
      const shop = lists.find(l => l.listType === 'shop') ?? lists.find(l => (l.components ?? '').includes('VTODO'))
      if (!shop) throw new Error(t('Keine Einkaufsliste.'))
      let n = 0
      for (const it of r.flatMap(x => x.items)) { const p = parseItem(it); await api.post('/api/pim/tasks', { list: shop.id, title: composeItem(p), ...p, dueAllDay: true }); n++ }
      notify(t('{n} Zutaten auf der Einkaufsliste', { n }))
    } catch (e) { fail(e) }
  }
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <Head label={t('Essensplan')} onClick={() => go('#/calendar/' + data.days[0].day)} />
      <div style={{ display: 'grid', gridTemplateColumns: `repeat(${Math.min(data.days.length, 4)}, minmax(0, 1fr))`, gap: 8, flex: 1 }}>
        {data.days.slice(0, 8).map(d => (
          <div key={d.day} style={{ minWidth: 0 }}>
            <div className={s.mut} style={{ fontSize: 11, fontWeight: 600 }}>{d.weekday} <span style={{ fontWeight: 400 }}>{day(d.day).getDate()}.</span></div>
            {d.items.length === 0 ? <Text size={200} className={s.mut} style={{ cursor: 'pointer' }} onClick={() => go('#/calendar/' + d.day)}>{t('Noch nichts geplant')}</Text>
              : d.items.map((m, i) => (
                <div key={i} style={{ fontSize: 13, marginTop: 2 }}>
                  <div className={s.ell} style={{ cursor: 'pointer' }} title={m.title} onClick={() => go('#/calendar/' + d.day)}>{m.meal ? <span className={s.mut}>{m.meal.slice(0, 1)}: </span> : null}{m.title}</div>
                  {m.recipe && <div style={{ display: 'flex', gap: 6 }}>
                    <Button size="small" appearance="transparent" style={{ padding: 0, minWidth: 0, fontSize: 11 }} onClick={() => go('#/notes/' + m.recipe)}>{t('Rezept öffnen')}</Button>
                    <Button size="small" appearance="transparent" style={{ padding: 0, minWidth: 0, fontSize: 11 }} onClick={() => toShop(m.recipe!)}>{t('Zutaten auf die Einkaufsliste')}</Button>
                  </div>}
                </div>
              ))}
          </div>
        ))}
      </div>
    </div>
  )
}

// ---------------- Aufgaben ----------------

interface TasksData {
  items: { id: string; list: number; listName: string; color: string | null; uri: string; title: string; due: string; time: string | null; overdue: boolean; when: string; priority: number; assignee: string | null }[]
  followUps: { account: string; folder: string; uid: number; messageId: string | null; subject: string; from: string | null; due: string; overdue: boolean; when: string }[]
  lists: { id: number; name: string; color: string | null; shared: boolean }[]
  defaultList: number | null
}
export function TasksWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  const { fail } = useApp()
  const { data, error, reload } = useWidgetData<TasksData>('tasks', w.opts)
  const [text, setText] = useState('')
  const [undo, setUndo] = useState<{ list: number; uri: string; title: string } | null>(null)
  if (!data) return <Loading error={error} />
  const done = async (x: TasksData['items'][0]) => {
    try { await dashboardApi.taskDone(x.list, x.uri, true); setUndo({ list: x.list, uri: x.uri, title: x.title }); reload(); setTimeout(() => setUndo(u => u?.uri === x.uri ? null : u), 8000) } catch (e) { fail(e) }
  }
  const undoIt = async () => { if (!undo) return; try { await dashboardApi.taskDone(undo.list, undo.uri, false); setUndo(null); reload() } catch (e) { fail(e) } }
  const add = async () => {
    const raw = text.trim(); if (!raw || data.defaultList == null) return
    const nd = parseNaturalDate(raw, new Date())
    const when = nd ? taskDue(nd) : { due: isoToday(), dueAllDay: true }
    try { await api.post('/api/pim/tasks', { list: w.opts?.lists && (w.opts.lists as number[])[0] != null ? (w.opts.lists as number[])[0] : data.defaultList, title: nd?.title || raw, ...when, priority: 0, completed: false }); setText(''); reload() } catch (e) { fail(e) }
  }
  const all = [...data.items.map(x => ({ k: 't' as const, x })), ...data.followUps.map(x => ({ k: 'm' as const, x }))]
  const count = data.items.filter(x => x.due <= isoToday()).length + data.followUps.filter(x => x.due <= isoToday()).length
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <div style={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', cursor: 'pointer' }} onClick={() => go('#/tasks')}>
        <div style={{ width: 30, height: 30, borderRadius: '50%', background: tokens.colorBrandBackground, display: 'flex', alignItems: 'center', justifyContent: 'center' }}><Checkmark16Regular style={{ color: '#fff' }} /></div>
        <span style={{ fontSize: 30, fontWeight: 600, lineHeight: 1 }}>{count}</span>
      </div>
      <div style={{ fontSize: 14, fontWeight: 600, color: tokens.colorBrandForeground1 }}>{t('Heute')}</div>
      <div className={s.scroll}>
        {all.length === 0 && <Empty text={t('Nichts fällig.')} />}
        {all.slice(0, big ? 12 : 4).map(e => e.k === 't' ? (
          <label key={e.x.id} style={{ display: 'flex', gap: 8, alignItems: 'center', fontSize: 13, padding: '3px 0' }}>
            <Checkbox checked={false} onChange={() => done(e.x)} aria-label={e.x.title} />
            <span className={s.ell} style={{ flex: 1, cursor: 'pointer' }} onClick={ev => { ev.preventDefault(); go('#/tasks/' + e.x.list) }}>{e.x.title}</span>
            <span className={s.mut} style={{ fontSize: 11, flexShrink: 0, color: e.x.overdue ? tokens.colorPaletteRedForeground1 : undefined }}>{e.x.overdue ? t('überfällig') : e.x.time ?? (e.x.due === isoToday() ? '' : e.x.when)}</span>
          </label>
        ) : (
          <div key={e.x.account + e.x.uid} className={s.row} style={{ fontSize: 13 }} onClick={() => go(e.x.messageId ? `#/mail/open/${encodeURIComponent(e.x.account)}/${encodeURIComponent(e.x.messageId)}` : '#/mail')}>
            <span className={s.mut} style={{ fontSize: 11 }}>✉</span>
            <span className={s.ell} style={{ flex: 1 }}>{e.x.subject}<span className={s.mut}> · {e.x.from}</span></span>
            <span className={s.mut} style={{ fontSize: 11, flexShrink: 0, color: e.x.overdue ? tokens.colorPaletteRedForeground1 : undefined }}>{t('Wiedervorlage')}</span>
          </div>
        ))}
      </div>
      {undo && <div style={{ display: 'flex', alignItems: 'center', gap: 8, fontSize: 12 }}><span className={s.ell}>{t('Erledigt: {title}', { title: undo.title })}</span><Button size="small" appearance="subtle" onClick={undoIt}>{t('Rückgängig')}</Button></div>}
      {data.defaultList != null && <Input size="small" className={s.input} value={text} onChange={(_, d) => setText(d.value)} placeholder={t('Neue Aufgabe, z. B. „morgen Zahnarzt anrufen“')} onKeyDown={e => { if (e.key === 'Enter') add() }} />}
    </div>
  )
}

// ---------------- Einkaufsliste ----------------

export function ShoppingWidget({ w }: { w: Widget }) {
  const s = useWidgetStyles()
  const { fail } = useApp()
  const { data, error, reload } = useWidgetData<{ list: number | null; name?: string; items: { id: string; uri: string; text: string; section: string | null }[]; lists: { id: number; name: string }[] }>('shopping', w.opts)
  const [text, setText] = useState('')
  if (!data) return <Loading error={error} />
  if (data.list == null) return <Blank label={w.title || t('Einkaufsliste')} text={t('Keine Einkaufsliste.')} to="#/tasks" />
  const check = async (uri: string) => { try { await dashboardApi.taskDone(data.list!, uri, true); reload() } catch (e) { fail(e) } }
  const add = async () => {
    const raw = text.trim(); if (!raw) return
    const p = parseItem(raw)
    try { await api.post('/api/pim/tasks', { list: data.list, title: composeItem(p), ...p, dueAllDay: true, priority: 0, completed: false }); setText(''); reload() } catch (e) { fail(e) }
  }
  const groups: { section: string | null; items: typeof data.items }[] = []
  for (const it of data.items) { const g = groups.find(x => x.section === it.section); if (g) g.items.push(it); else groups.push({ section: it.section, items: [it] }) }
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <Head label={data.name ?? t('Einkaufsliste')} onClick={() => go('#/tasks/' + data.list)} />
      <div className={s.scroll}>
        {data.items.length === 0 && <Empty text={t('Alles eingekauft.')} />}
        {groups.map(g => (
          <div key={g.section ?? ''}>
            {g.section && groups.length > 1 && <div className={s.mut} style={{ fontSize: 11, fontWeight: 600, marginTop: 4 }}>{g.section}</div>}
            {g.items.map(it => <label key={it.id} style={{ display: 'flex', gap: 8, alignItems: 'center', fontSize: 13, padding: '2px 0' }}><Checkbox checked={false} onChange={() => check(it.uri)} aria-label={it.text} /><span className={s.ell}>{it.text}</span></label>)}
          </div>
        ))}
      </div>
      <Input size="small" className={s.input} value={text} onChange={(_, d) => setText(d.value)} placeholder={t('Hinzufügen, z. B. „2 kg Äpfel“')} onKeyDown={e => { if (e.key === 'Enter') add() }} />
    </div>
  )
}

// ---------------- E-Mail ----------------

export function MailWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  const { fail } = useApp()
  const { data, error, reload } = useWidgetData<{ unread: number; items: { account: string; folder: string; uid: number; messageId: string | null; subject: string; from: string | null; date: string; preview: string }[]; accounts: { id: string; name: string }[] }>('mail', w.opts)
  if (!data) return <Loading error={error} />
  if (data.accounts.length === 0) return <Blank label={w.title || t('E-Mail')} text={t('Kein Mailkonto eingerichtet.')} to="#/settings/mail" />
  const mode = (w.opts?.mode as string) ?? 'all'
  const read = async (m: typeof data.items[0]) => { try { await api.post(`/api/mail/${encodeURIComponent(m.account)}/flags`, { folder: m.folder, uids: [m.uid], seen: true }); reload() } catch (e) { fail(e) } }
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <Head label={w.title || (mode === 'focused' ? t('E-Mail') + ' · ' + t('Nur Relevante') : t('E-Mail'))} onClick={() => go('#/mail')} />
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 8 }}><span className={s.big}>{data.unread}</span><span className={s.mut} style={{ fontSize: 12 }}>{mode === 'focused' ? t('relevant ungelesen') : t('ungelesen')}</span></div>
      <div className={s.scroll}>
        {data.items.length === 0 && <Empty text={t('Nichts Ungelesenes.')} />}
        {data.items.slice(0, big ? 8 : 3).map(m => (
          <div key={m.account + m.uid} className={s.row} style={{ alignItems: 'flex-start' }} onClick={() => go(m.messageId ? `#/mail/open/${encodeURIComponent(m.account)}/${encodeURIComponent(m.messageId)}` : '#/mail')}>
            <div style={{ flex: 1, minWidth: 0 }}>
              <div className={s.ell} style={{ fontSize: 13, fontWeight: 600 }}>{m.from}</div>
              <div className={mergeClasses(s.ell)} style={{ fontSize: 12 }}>{m.subject}</div>
              {big && <div className={mergeClasses(s.mut, s.ell)} style={{ fontSize: 11 }}>{m.preview}</div>}
            </div>
            <Tooltip content={t('Als gelesen markieren')} relationship="label"><Button size="small" appearance="subtle" icon={<Checkmark16Regular />} onClick={e => { e.stopPropagation(); read(m) }} /></Tooltip>
          </div>
        ))}
      </div>
    </div>
  )
}

// ---------------- Chat ----------------

export function ChatWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  const { fail, me } = useApp()
  const { prefs } = usePrefs()
  const { data, error, reload } = useWidgetData<{ unread: number; rooms: { id: number; title: string; kind: string; unread: number; mentioned: number; other: string | null; last: { created: string; from: string; text: string | null } | null }[]; pukki: number | null }>('chat', w.opts, 30_000)
  const [reply, setReply] = useState<{ room: number; text: string } | null>(null)
  if (!data) return <Loading error={error} />
  const send = async () => {
    if (!reply?.text.trim()) return
    try { await api.post(`/api/chat/rooms/${reply.room}/messages`, { text: reply.text.trim() }); setReply(null); reload() } catch (e) { fail(e) }
  }
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <Head label={t('Chat')} onClick={() => go('#/chat')} />
      <div className={s.scroll}>
        {data.rooms.length === 0 && <Empty text={t('Keine Unterhaltungen.')} />}
        {data.rooms.slice(0, big ? 8 : 4).map(r => (
          <div key={r.id}>
            <div className={s.row} onClick={() => go('#/chat/' + r.id)}>
              <UserAvatar id={r.other ?? 'g' + r.id} name={r.title} accent={prefs.accent} size={26} />
              <div style={{ flex: 1, minWidth: 0 }}>
                <div className={s.ell} style={{ fontSize: 13, fontWeight: r.unread ? 600 : 400 }}>{r.title}{r.mentioned > 0 && <span style={{ color: tokens.colorBrandForeground1 }}> @</span>}</div>
                {r.last && <div className={mergeClasses(s.mut, s.ell)} style={{ fontSize: 11 }}>{r.last.from}: {r.last.text}</div>}
              </div>
              {r.unread > 0 && <span style={{ background: tokens.colorBrandBackground, color: '#fff', borderRadius: 999, padding: '0 7px', fontSize: 11 }}>{r.unread}</span>}
              {big && r.unread > 0 && <Button size="small" appearance="subtle" icon={<Send16Regular />} aria-label={t('Antwort schreiben …')} onClick={e => { e.stopPropagation(); setReply(reply?.room === r.id ? null : { room: r.id, text: '' }) }} />}
            </div>
            {reply?.room === r.id && <Input size="small" className={s.input} autoFocus value={reply.text} onChange={(_, d) => setReply({ room: r.id, text: d.value })} placeholder={t('Antwort schreiben …')}
              onKeyDown={e => { if (e.key === 'Enter') send(); if (e.key === 'Escape') setReply(null) }} contentAfter={<Button size="small" appearance="transparent" icon={<Send16Regular />} onClick={send} aria-label={t('Senden')} />} />}
          </div>
        ))}
      </div>
      {me && null}
    </div>
  )
}

// ---------------- Pukki ----------------

export function PukkiWidget({ w }: { w: Widget }) {
  const s = useWidgetStyles()
  const { fail } = useApp()
  const { prefs } = usePrefs()
  const { data, error } = useWidgetData<{ text: string; room: number | null; tomorrow: boolean }>('pukki', w.opts, 300_000)
  const [q, setQ] = useState('')
  const [answer, setAnswer] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  if (!data) return <Loading error={error} />
  const ask = async () => {
    const text = q.trim(); if (!text || data.room == null) return
    setBusy(true); setAnswer(null)
    try {
      const sent = await api.post<{ id: number }>(`/api/chat/rooms/${data.room}/messages`, { text, ...fromDevice() })
      // Pukki antwortet sofort im selben Raum: die nächste Nachricht von ihm nach meiner
      for (let i = 0; i < 20; i++) {
        await new Promise(r => setTimeout(r, 400))
        const msgs = await api.get<{ id: number; sender: string; text: string | null }[]>(`/api/chat/rooms/${data.room}/messages?limit=5`)
        const a = msgs.find(m => m.id > sent.id && m.sender === 'pukki')
        if (a) { setAnswer(a.text ?? ''); break }
      }
      setQ('')
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <div style={{ display: 'flex', gap: 10, alignItems: 'center' }}>
        <UserAvatar id="pukki" name="Pukki" accent={prefs.accent} size={36} />
        <div style={{ flex: 1, minWidth: 0 }}><div style={{ fontWeight: 600, fontSize: 13 }}>Pukki</div>{data.tomorrow && <div className={s.mut} style={{ fontSize: 11 }}>{t('Für morgen')}</div>}</div>
        {data.room != null && <Button size="small" appearance="subtle" onClick={() => go('#/chat/' + data.room)}>{t('Im Chat öffnen')}</Button>}
      </div>
      <div className={s.scroll} style={{ fontSize: 13, whiteSpace: 'pre-line', lineHeight: 1.45 }}>{busy ? <Spinner size="tiny" label={t('Pukki denkt nach …')} /> : answer ?? data.text}</div>
      <Input size="small" className={s.input} value={q} onChange={(_, d) => setQ(d.value)} placeholder={t('Frag Pukki …')} disabled={busy || data.room == null} onKeyDown={e => { if (e.key === 'Enter') ask() }}
        contentAfter={<Button size="small" appearance="transparent" icon={<Send16Regular />} onClick={ask} aria-label={t('Senden')} />} />
    </div>
  )
}

// ---------------- Wetter ----------------

const weatherIcon = (code: number) => code === 0 ? '☀️' : code <= 2 ? '🌤️' : code === 3 ? '☁️' : code <= 48 ? '🌫️' : code <= 57 ? '🌦️' : code <= 67 ? '🌧️' : code <= 77 ? '🌨️' : code <= 82 ? '🌦️' : code <= 86 ? '🌨️' : '⛈️'
export function WeatherWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  const { data, error } = useWidgetData<{ configured: boolean; place?: string; error?: string; now?: { time: string; temp: number; code: number; rain: number | null } | null; days?: { date: string; code: number; text: string; max: number; min: number; rain: number | null }[]; hours?: { time: string; temp: number; code: number; rain: number | null }[]; hints?: { title: string; start: string; rain: number }[] }>('weather', w.opts, 600_000)
  if (!data) return <Loading error={error} />
  if (!data.configured) return <Blank label={w.title || t('Wetter')} text={t('Wetter nicht eingerichtet – Ort unter Einstellungen → Chat → Pukki wählen.')} to="#/settings/chat" />
  if (!data.days) return <Blank label={w.title || t('Wetter')} text={data.error ?? t('Keine Wetterdaten bekommen.')} />
  const today = data.days[0]
  const hours = (data.hours ?? []).filter(h => h.time >= (data.now?.time ?? '00:00')).filter((_, i) => i % 3 === 0).slice(0, 6)
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <Head label={`${t('Wetter')}${data.place ? ' · ' + data.place : ''}`} />
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <span style={{ fontSize: 46, fontWeight: 300, lineHeight: 1 }}>{Math.round(data.now?.temp ?? today.max)}°</span>
        <div><div style={{ fontSize: 14 }}>{weatherIcon(data.now?.code ?? today.code)} {today.text}</div><div className={s.mut} style={{ fontSize: 12 }}>{t('H: {max}° T: {min}°', { max: Math.round(today.max), min: Math.round(today.min) })}{today.rain != null ? ` · 🌧 ${today.rain} %` : ''}</div></div>
      </div>
      {w.size === 3 && hours.length > 0 && <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 11 }}>
        {hours.map(h => <div key={h.time} style={{ textAlign: 'center' }}><div className={s.mut}>{h.time}</div><div>{weatherIcon(h.code)}</div><div>{Math.round(h.temp)}°</div>{h.rain != null && h.rain >= 30 && <div className={s.mut}>{h.rain} %</div>}</div>)}
      </div>}
      {(data.hints ?? []).slice(0, big ? 3 : 1).map((h, i) => <Text key={i} size={200} className={s.ell} style={{ color: tokens.colorPaletteMarigoldForeground1 }}>☔ {t('Regen ab {time}: {title}', { time: h.start, title: h.title })}</Text>)}
      <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: 12, marginTop: 'auto', lineHeight: 1.25 }}>
        {data.days.slice(1, w.size === 3 ? 7 : 5).map(d => <div key={d.date} style={{ textAlign: 'center' }}><div className={s.mut}>{weatherIcon(d.code)} {day(d.date).toLocaleDateString(locale, { weekday: 'short' })}</div><div>{Math.round(d.max)}° <span className={s.mut}>{Math.round(d.min)}°</span></div></div>)}
      </div>
    </div>
  )
}

// ---------------- Schlagzeilen ----------------

export function NewsWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  const { data, error } = useWidgetData<{ configured: boolean; items: { title: string; link: string; at: string | null; source: string }[] }>('news', w.opts, 600_000)
  if (!data) return <Loading error={error} />
  if (!data.configured) return <Blank label={w.title || t('Schlagzeilen')} text={t('Keine Feeds eingerichtet – unter Einstellungen → Chat → Pukki wählen.')} to="#/settings/chat" />
  // Schriftgröße der Überschriften (Einstellung des Widgets), die Quelle bleibt klein
  const size = String(w.opts?.text ?? 'm')
  const font = size === 's' ? { fontSize: 13, fontWeight: 400 } : size === 'l' ? { fontSize: 19, fontWeight: 600 } : { fontSize: 16, fontWeight: 600 }
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <Head label={t('Schlagzeilen')} />
      <div className={s.scroll} style={{ display: 'flex', flexDirection: 'column', gap: size === 's' ? 6 : 10 }}>
        {data.items.slice(0, big ? 12 : 5).map((h, i) => <a key={i} href={h.link} target="_blank" rel="noopener noreferrer" style={{ color: 'inherit', textDecoration: 'none', lineHeight: 1.3, ...font }}>{h.title} <span className={s.mut} style={{ fontSize: 11, fontWeight: 400 }}>· {h.source}</span></a>)}
      </div>
    </div>
  )
}

// ---------------- Fotos ----------------

/** Wo die Diashow je Widget stand - beim nächsten Öffnen der Übersicht geht es mit dem folgenden Foto weiter, nicht wieder mit dem ersten. */
const photoAt = new Map<string, number>()

export function PhotosWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  type Photo = { path: string; name: string; etag: string; mime: string | null; taken: number }
  const { data, error } = useWidgetData<{ years: { year: number; yearsAgo: number; total: number; items: Photo[] }[]; total: number; recent: Photo[] }>('photos', w.opts, 600_000)
  const [n, setN] = useState(() => photoAt.has(w.id) ? photoAt.get(w.id)! + 1 : Math.floor(Math.random() * 1000))
  useEffect(() => { photoAt.set(w.id, n) }, [w.id, n])
  // Nichts von diesem Tag: dann die neuesten Fotos, damit das Widget nie leer bleibt
  const onDay = (data?.years ?? []).flatMap(y => y.items.map(i => ({ ...i, yearsAgo: y.yearsAgo })))
  const all = onDay.length ? onDay : (data?.recent ?? []).map(i => ({ ...i, yearsAgo: -1 }))
  // Wechseltakt aus den Einstellungen (Sekunden, 0 = gar nicht)
  const every = Number(w.opts?.every ?? 7)
  useEffect(() => { if (all.length < 2 || !every) return; const h = setInterval(() => setN(x => (x + 1) % all.length), every * 1000); return () => clearInterval(h) }, [all.length, every])
  if (!data) return <Loading error={error} />
  if (all.length === 0) return <div className={s.col}><Head label={t('Fotos') + ' · ' + t('An diesem Tag')} onClick={() => go('#/gallery')} /><Empty text={t('Keine Fotos von diesem Tag in früheren Jahren.')} /></div>
  const cur = all[n % all.length]
  const item = { path: cur.path, name: cur.name, isDir: false, size: 0, mime: cur.mime, modified: new Date(cur.taken).toISOString(), etag: cur.etag }
  const label = (y: number) => y === 1 ? t('vor 1 Jahr') : t('vor {n} Jahren', { n: y })
  return (
    <div className={s.photo} style={{ margin: -14, height: 'calc(100% + 28px)', borderRadius: 0 }} onClick={() => openPhotoFromElsewhere(cur.path)}>
      <img className={s.img} src={gallerySource.thumb(item, big ? 1600 : 512)} alt="" style={{ transition: 'opacity .4s' }} />
      <div style={{ position: 'absolute', left: 0, right: 0, bottom: 0, padding: '28px 14px 12px', background: 'linear-gradient(transparent, rgba(0,0,0,.65))', color: '#fff' }}>
        <div style={{ fontSize: 13, fontWeight: 600 }}>{cur.yearsAgo < 0 ? t('Zuletzt aufgenommen') : `${t('An diesem Tag')} · ${label(cur.yearsAgo)}`}</div>
        <div style={{ fontSize: 11, opacity: .85 }}>{new Date(cur.taken).toLocaleDateString(locale, { day: 'numeric', month: 'long', year: 'numeric' })}{cur.yearsAgo < 0 ? '' : ` · ${data.total} ${t('Fotos')}`}</div>
      </div>
      {big && <div style={{ position: 'absolute', right: 10, top: 10, display: 'flex', gap: 4 }}>{all.slice(0, 8).map((_, i) => <span key={i} style={{ width: 6, height: 6, borderRadius: 3, background: i === n % all.length ? '#fff' : 'rgba(255,255,255,.45)' }} />)}</div>}
    </div>
  )
}

// ---------------- Notizen ----------------

export function NotesWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  const { fail, notify } = useApp()
  const { data, error, reload } = useWidgetData<{ items: { id: number; title: string; snippet: string; modified: string; important: boolean; color: string | null }[]; favorites: number }>('notes', w.opts)
  const [text, setText] = useState('')
  if (!data) return <Loading error={error} />
  const add = async () => {
    const raw = text.trim(); if (!raw) return
    try {
      const r = await api.post<{ id: number }>('/api/notes', {})
      await api.put(`/api/notes/${r.id}/content`, { blocks: [{ html: `<p>${raw.replace(/&/g, '&amp;').replace(/</g, '&lt;')}</p>` }], plain: raw })
      setText(''); notify(t('Notiz angelegt.')); reload()
    } catch (e) { fail(e) }
  }
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <Head label={t('Notizen') + (data.favorites ? ' · ' + t('Favoriten oben') : '')} onClick={() => go('#/notes')} />
      <div className={s.scroll} style={{ display: 'grid', gridTemplateColumns: w.size === 3 ? 'repeat(2, minmax(0, 1fr))' : '1fr', gap: 6, alignContent: 'start' }}>
        {data.items.length === 0 && <Empty text={t('Keine Notizen.')} />}
        {data.items.slice(0, big ? 8 : 4).map(n => (
          <div key={n.id} style={{ background: n.important ? tokens.colorPaletteMarigoldBackground2 : tokens.colorNeutralBackground3, borderRadius: 10, padding: '8px 10px', cursor: 'pointer', minWidth: 0 }} onClick={() => go('#/notes/' + n.id)}>
            <div className={s.ell} style={{ fontSize: 13, fontWeight: 600 }}>{n.important && <Star16Filled style={{ color: '#E8A33D', verticalAlign: -2, marginRight: 4 }} />}{n.title}</div>
            <div className={mergeClasses(s.mut, s.ell)} style={{ fontSize: 11 }}>{n.snippet || new Date(n.modified).toLocaleDateString(locale)}</div>
          </div>
        ))}
      </div>
      <Input size="small" className={s.input} value={text} onChange={(_, d) => setText(d.value)} placeholder={t('Schnellnotiz …')} onKeyDown={e => { if (e.key === 'Enter') add() }} />
    </div>
  )
}

// ---------------- Dateien ----------------

export function FilesWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  const { fail, notify } = useApp()
  const { data, error, reload } = useWidgetData<{ items: { path: string; name: string; isDir: boolean; size: number; mime: string | null; modified: string; etag: string; favorite: boolean; used: string | null }[]; favorites: number }>('files', w.opts)
  const [over, setOver] = useState(false)
  if (!data) return <Loading error={error} />
  const folder = (w.opts?.folder as string) ?? ''
  const drop = async (e: React.DragEvent) => {
    e.preventDefault(); setOver(false)
    const files = Array.from(e.dataTransfer.files); if (!files.length) return
    try {
      for (const f of files) { const fd = new FormData(); fd.append('file', f, f.name); await api.form('/api/files/upload?' + q({ path: folder, conflict: 'rename', mtime: f.lastModified }), fd) }
      notify(t('Hochgeladen: {name}', { name: files.map(f => f.name).join(', ') })); reload()
    } catch (err) { fail(err) }
  }
  return (
    <div className={s.col} style={{ height: '100%', outline: over ? `2px dashed ${tokens.colorBrandStroke1}` : undefined, outlineOffset: -4, borderRadius: 12 }}
      onDragOver={e => { if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); setOver(true) } }} onDragLeave={() => setOver(false)} onDrop={drop}>
      <Head label={t('Dateien') + (data.favorites ? ' · ' + t('Favoriten oben') : '')} onClick={() => go('#/files')} />
      <div className={s.scroll}>
        {data.items.length === 0 && <Empty text={t('Keine Dateien.')} />}
        {data.items.slice(0, big ? 10 : 4).map(f => (
          <div key={f.path} className={s.row} onClick={() => go('#/files/' + (f.isDir ? f.path : f.path.includes('/') ? f.path.slice(0, f.path.lastIndexOf('/')) : '').split('/').map(encodeURIComponent).join('/'))}>
            <span style={{ width: 20, display: 'inline-flex', justifyContent: 'center' }}>{f.favorite ? <Star16Filled style={{ color: '#E8A33D' }} /> : <KindIcon item={f} />}</span>
            <span className={s.ell} style={{ flex: 1, fontSize: 13 }}>{f.name}</span>
            <span className={s.mut} style={{ fontSize: 11, flexShrink: 0 }}>{f.isDir ? '' : fmtSize(f.size)}</span>
          </div>
        ))}
      </div>
      <Text size={100} className={s.mut}>{over ? t('Datei hierher ziehen zum Hochladen nach „{folder}“', { folder: folder || t('Dateien') }) : ''}</Text>
    </div>
  )
}

// ---------------- Geburtstage ----------------

export function BirthdaysWidget({ w, big }: { w: Widget; big: boolean }) {
  const s = useWidgetStyles()
  const { fail, notify } = useApp()
  const { data, error } = useWidgetData<{ items: { name: string; day: string; inDays: number; age: number | null; contact: string | null; email: string | null; chatWith: string | null }[] }>('birthdays', w.opts, 600_000)
  if (!data) return <Loading error={error} />
  const days = (w.opts?.days as number) ?? 30
  const when = (n: number) => n === 0 ? t('heute') : n === 1 ? t('morgen') : t('in {n} Tagen', { n })
  const chat = async (id: string) => { try { const r = await api.post<{ id: number }>('/api/chat/rooms', { members: [id] }); go('#/chat/' + r.id) } catch (e) { fail(e) } }
  const mail = (addr: string) => { sessionStorage.setItem('dmoffice.compose.to', addr); go('#/mail') }
  const gift = async (name: string, d: string) => {
    try {
      const lists = await api.get<any[]>('/api/pim/collections/calendars')
      const list = lists.find(l => (l.components ?? '').includes('VTODO') && l.listType !== 'shop') ?? lists.find(l => (l.components ?? '').includes('VTODO'))
      if (!list) return
      const due = new Date(d + 'T12:00:00'); due.setDate(due.getDate() - 3)
      await api.post('/api/pim/tasks', { list: list.id, title: t('Geschenk für {name}', { name }), due: due.toLocaleDateString('sv-SE'), dueAllDay: true, priority: 0, completed: false })
      notify(t('Aufgabe angelegt.'))
    } catch (e) { fail(e) }
  }
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <Head label={t('Geburtstage')} onClick={() => go('#/contacts')} />
      <div className={s.scroll}>
        {data.items.length === 0 && <Empty text={t('Keine Geburtstage in den nächsten {n} Tagen.', { n: days })} />}
        {data.items.slice(0, big ? 8 : 3).map((b, i) => (
          <div key={i} style={{ padding: '3px 0' }}>
            <div style={{ fontSize: 13, fontWeight: 600 }} className={s.ell}>🎂 {b.name}</div>
            <div className={s.mut} style={{ fontSize: 11, display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <span>{when(b.inDays)}{b.age ? ' · ' + t('wird {n}', { n: b.age }) : ''}</span>
              {b.chatWith && <a href="#" onClick={e => { e.preventDefault(); chat(b.chatWith!) }}>{t('Glückwunsch im Chat')}</a>}
              {!b.chatWith && b.email && <a href="#" onClick={e => { e.preventDefault(); mail(b.email!) }}>{t('Glückwunsch per Mail')}</a>}
              {b.inDays > 0 && <a href="#" onClick={e => { e.preventDefault(); gift(b.name, b.day) }}>{t('Geschenk besorgen')}</a>}
            </div>
          </div>
        ))}
      </div>
    </div>
  )
}

// ---------------- Countdown ----------------

export function CountdownWidget({ w }: { w: Widget }) {
  const s = useWidgetStyles()
  const { data, error } = useWidgetData<{ found: boolean; word: string; title?: string; day?: string; days?: number; calendar?: string | null }>('countdown', w.opts, 600_000)
  if (!data) return <Loading error={error} />
  if (!data.found) return <div className={s.col}><Head label={t('Countdown')} /><Empty text={t('Kein passender Termin.')} /></div>
  return (
    <div className={s.col} style={{ height: '100%', cursor: 'pointer', justifyContent: 'center', textAlign: 'center' }} onClick={() => go('#/calendar/' + data.day)}>
      <div style={{ fontSize: 44, fontWeight: 600, lineHeight: 1 }}>{data.days}</div>
      <div style={{ fontSize: 13, fontWeight: 600 }}>{data.days === 1 ? t('Morgen!') : t('Noch {n} Tage', { n: data.days! })}</div>
      <div className={mergeClasses(s.mut, s.ell)} style={{ fontSize: 12 }}>{data.title} · {day(data.day!).toLocaleDateString(locale, { day: 'numeric', month: 'short' })}</div>
    </div>
  )
}

// ---------------- Lesezeichen ----------------

export function BookmarksWidget() {
  const s = useWidgetStyles()
  const { prefs } = usePrefs()
  const list = prefs.bookmarks ?? []
  return (
    <div className={s.col} style={{ height: '100%' }}>
      <Head label={t('Lesezeichen')} />
      {list.length === 0 && <Empty text={t('Keine Lesezeichen – oben links in der Leiste anlegen.')} />}
      {list.map((b, i) => <a key={i} href={b.url} target={b.url.startsWith('#') ? undefined : '_blank'} rel="noopener noreferrer" style={{ fontSize: 13, color: 'inherit' }} className={s.ell}>↗ {b.name}</a>)}
    </div>
  )
}

// ---------------- Speicher ----------------

export function StorageWidget({ w }: { w: Widget }) {
  const s = useWidgetStyles()
  const { data, error } = useWidgetData<{ used: number; database: number; files: number; trash: number; versions: number; quota: number }>('storage', w.opts, 600_000)
  if (!data) return <Loading error={error} />
  const pct = data.quota > 0 ? Math.min(100, Math.round(data.used / data.quota * 100)) : null
  return (
    <div className={s.col} style={{ height: '100%', cursor: 'pointer' }} onClick={() => go('#/settings/stats')}>
      <Head label={t('Speicher')} />
      <div style={{ fontSize: 22, fontWeight: 600 }}>{fmtSize(data.used)} <span className={s.mut} style={{ fontSize: 12, fontWeight: 400 }}>{data.quota > 0 ? t('von {quota}', { quota: fmtSize(data.quota) }) : t('ohne Grenze')}</span></div>
      {pct != null && <div style={{ height: 8, borderRadius: 4, background: tokens.colorNeutralBackground3, overflow: 'hidden' }}><div style={{ width: pct + '%', height: '100%', background: pct > 90 ? tokens.colorPaletteRedBackground3 : tokens.colorBrandBackground }} /></div>}
      <div className={s.mut} style={{ fontSize: 11, display: 'flex', gap: 10, flexWrap: 'wrap' }}><span>{t('Dateien')} {fmtSize(data.files)}</span><span>{t('Datenbank')} {fmtSize(data.database)}</span><span>{t('Papierkorb')} {fmtSize(data.trash)}</span><span>{t('Versionen')} {fmtSize(data.versions)}</span></div>
    </div>
  )
}

// ---------------- System (Verwaltung) ----------------

export function SystemWidget({ w }: { w: Widget }) {
  const s = useWidgetStyles()
  const { data, error } = useWidgetData<{ version: string; storage: { on: boolean; mode: string; target: string; running: boolean; lastRun: string | null; lastError: string | null; pending: number; notHome: number }; backup: { enabled: boolean; lastRun: string | null; lastError: string | null; blocked: string | null; own: boolean }; volume: { total: number; free: number } | null; profile: { at: string | null; current: boolean } }>('system', w.opts, 300_000)
  if (!data) return <Loading error={error} />
  const ago = (iso: string | null) => iso ? new Date(iso).toLocaleString(locale, { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' }) : t('nie')
  const red = tokens.colorPaletteRedForeground1, yellow = tokens.colorPaletteMarigoldForeground1, green = tokens.colorPaletteGreenForeground1
  const old = (iso: string | null, h: number) => !iso || Date.now() - new Date(iso).getTime() > h * 3600_000
  const Row = ({ color, label, value }: { color: string; label: string; value: string }) => <div style={{ display: 'flex', gap: 8, alignItems: 'center', fontSize: 13 }}><span style={{ width: 10, height: 10, borderRadius: 5, background: color, flexShrink: 0 }} /><span style={{ flex: 1 }}>{label}</span><span className={s.mut} style={{ fontSize: 12 }}>{value}</span></div>
  const lowDisk = data.volume && data.volume.free < data.volume.total * 0.1
  return (
    <div className={s.col} style={{ height: '100%', cursor: 'pointer' }} onClick={() => go('#/admin')}>
      <Head label={t('System') + ' · ' + t('Version {v}', { v: data.version })} />
      <Row color={!data.storage.on ? yellow : data.storage.lastError ? red : green} label={t('Storage Extender')} value={!data.storage.on ? t('aus') : ago(data.storage.lastRun)} />
      <Row color={!data.backup.enabled || data.backup.blocked ? red : data.backup.lastError || old(data.backup.lastRun, 48) ? yellow : green} label={t('Core-Backup')} value={!data.backup.enabled ? t('aus') : ago(data.backup.lastRun)} />
      <Row color={!data.profile.at ? red : data.profile.current ? green : yellow} label={t('Zugangsdaten für den Notfall')} value={!data.profile.at ? t('nie gesichert') : data.profile.current ? t('gesichert') : t('veraltet')} />
      {data.volume && <Row color={lowDisk ? red : green} label={t('Platte')} value={fmtSize(data.volume.free) + ' ' + t('frei')} />}
      {(data.storage.lastError || data.backup.lastError || data.backup.blocked) && <Text size={100} style={{ color: red }} className={s.ell}>{data.backup.blocked || data.backup.lastError || data.storage.lastError}</Text>}
    </div>
  )
}

// ---------------- Verteiler ----------------

export function WidgetBody({ w }: { w: Widget }) {
  // Groß (2×2) und hoch (1×2): mehr Zeilen
  const big = w.size >= 3
  switch (w.type) {
    case 'today': return <TodayWidget w={w} />
    case 'events': return <EventsWidget w={w} big={big} />
    case 'meals': return <MealsWidget w={w} />
    case 'tasks': return <TasksWidget w={w} big={big} />
    case 'shopping': return <ShoppingWidget w={w} />
    case 'mail': return <MailWidget w={w} big={big} />
    case 'chat': return <ChatWidget w={w} big={big} />
    case 'pukki': return <PukkiWidget w={w} />
    case 'weather': return <WeatherWidget w={w} big={big} />
    case 'news': return <NewsWidget w={w} big={big} />
    case 'photos': return <PhotosWidget w={w} big={big} />
    case 'notes': return <NotesWidget w={w} big={big} />
    case 'files': return <FilesWidget w={w} big={big} />
    case 'birthdays': return <BirthdaysWidget w={w} big={big} />
    case 'countdown': return <CountdownWidget w={w} />
    case 'bookmarks': return <BookmarksWidget />
    case 'storage': return <StorageWidget w={w} />
    case 'system': return <SystemWidget w={w} />
    default: return <Empty text={t('Nichts geladen.')} />
  }
}

export { hasThumb, kindOf, Thumb, Dismiss16Regular }
