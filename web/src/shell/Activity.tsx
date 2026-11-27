import { Button, Link, Popover, PopoverSurface, PopoverTrigger, ProgressBar, Spinner, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import { CheckmarkCircle16Regular, ErrorCircle16Regular, Pulse20Regular, Clock16Regular, Circle16Regular, PauseCircle16Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api, featureOn, fmtSize } from '../api'
import { locale, t, tError } from '../i18n'
import { usePoll } from './hooks'

export interface ActivityItem {
  id: string
  state: 'running' | 'waiting' | 'error' | 'idle'
  done: number | null
  total: number | null
  pending: number | null
  detail: string | null
  started: string | null
  finished: string | null
  error: string | null
  completed: number
  report?: string[] | null
  /** jobcontrol: angehalten oder übersprungen (bis holdUntil); control: darf steuern */
  hold?: 'paused' | 'skipped' | null
  holdUntil?: string | null
  control?: boolean
  // [feature:photoexport]
  /** Zielordner nach dem Lauf */
  link?: string | null
  // [/feature:photoexport]
}
export interface ActivityState { running: number; items: ActivityItem[] }

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column' },
  row: {
    display: 'grid', gridTemplateColumns: '20px 1fr', columnGap: '10px', padding: '8px 0',
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, ':last-child': { borderBottom: 'none' },
  },
  icon: { display: 'flex', justifyContent: 'center', paddingTop: '2px' },
  body: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  name: { fontWeight: 600, fontSize: '13px' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: '12px', overflowWrap: 'anywhere' },
  err: { color: tokens.colorPaletteRedForeground1, fontSize: '12px', overflowWrap: 'anywhere' },
  controls: { display: 'flex', flexWrap: 'wrap', gap: '4px', paddingTop: '2px' },
  surface: { width: 'min(380px, calc(100vw - 24px))', maxHeight: 'min(560px, 80vh)', overflowY: 'auto', padding: '12px 16px' },
  head: { display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', paddingBottom: '4px' },
  foot: { paddingTop: '8px', fontSize: '12px' },
  dot: {
    position: 'absolute', top: '-3px', right: '-3px', width: '8px', height: '8px', borderRadius: '50%',
    backgroundColor: tokens.colorBrandBackground, pointerEvents: 'none',
  },
})

const n = (x: number) => x.toLocaleString(locale)

/** "vor 5 Minuten" bzw. "5 minutes ago" */
function ago(iso: string): string {
  const sec = Math.round((new Date(iso).getTime() - Date.now()) / 1000)
  const rtf = new Intl.RelativeTimeFormat(locale, { numeric: 'auto' })
  const abs = Math.abs(sec)
  if (abs < 45) return t('gerade eben')
  if (abs < 3600) return rtf.format(Math.round(sec / 60), 'minute')
  if (abs < 86400) return rtf.format(Math.round(sec / 3600), 'hour')
  return rtf.format(Math.round(sec / 86400), 'day')
}

function nameOf(id: string): string {
  switch (id) {
    case 'mail-sync': return t('E-Mails abgleichen')
    case 'mail-queue': return t('Postausgang senden')
    case 'mail-slim': return t('Slim Mode aufräumen')
    case 'files-transfer': return t('Dateien übertragen')
    case 'gallery-save': return t('In Ablage sichern')
    case 'photo-export': return t('Fotos exportieren') // [feature:photoexport]
    case 'lens-apply': return t('Objektivkorrektur eintragen') // [feature:lenscorr]
    case 'lens-library': return t('Objektivprofile aus ZIP-Datei einlesen') // [feature:lenslib]
    case 'mail-trash': return t('Papierkorb und Junk leeren')
    case 'mail-classify': return t('Posteingang neu einordnen')
    case 'files-blobs': return t('Dateiinhalte auslagern')
    case 'files-hashes': return t('Prüfsummen der Dateien berechnen')
    case 'files-cleanup': return t('Dateien aufräumen')
    case 'text-index': return t('Volltextindex und Texterkennung')
    case 'thumbs': return t('Vorschaubilder erzeugen')
    case 'notes-sync': return t('Notizen abgleichen (notizi)')
    case 'subscriptions': return t('Kalender-Abos abrufen')
    case 'graph': return t('Mit Microsoft abgleichen')
    case 'compact': return t('Datenbank verdichten')
    // [feature:journal]
    case 'journal-import': return t('Tagebuch importieren')
    // [/feature:journal]
    case 'storage-sync': return t('Storage Extender abgleichen')
    case 'core-backup': return t('Core-Backup')
    case 'files-backup': return t('Files-Backup')
    case 'mount-index': return t('Suche in Server-Mounts')
    case 'files-meta': return t('Aufnahmedaten auslesen')
    case 'gallery-scan': return t('Externe Galerie-Ordner einlesen')
    case 'storage-cleanup': return t('Alte Daten am bisherigen Ziel löschen')
    case 'gallery-import': return t('Importprofile')
    case 'audio-scan': return t('Audiothek-Ordner einlesen')
    case 'gallery-proxies': return t('Große Vorschauen erzeugen')
    case 'gallery-medium': return t('Mittlere Vorschauen nachholen (nachts)') // [feature:galmedium]
    case 'gallery-relink': return t('Externe Galerie-Ordner abgleichen')
    case 'audio-relink': return t('Audiothek-Ordner abgleichen')
    case 'photo-write': return t('Bearbeitete Fotos speichern')
    case 'photo-auto': return t('Fotos automatisch verbessern')
    case 'db-health': return t('Datenbanken prüfen')
    case 'data-export': return t('Datenexport')
    case 'dms': return t('DMS: Dokumente verarbeiten')
    default: return id
  }
}

/** Ein Satz zum Zustand: was gerade geschieht, was noch ansteht, wann es zuletzt lief. */
function describe(i: ActivityItem): string {
  const parts: string[] = []
  if (i.hold === 'paused') parts.push(i.state === 'running' ? t('hält an') : t('angehalten'))
  else if (i.hold === 'skipped') parts.push(t('übersprungen bis {when}', { when: new Date(i.holdUntil!).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' }) }))
  else if (i.state === 'running') parts.push(t('läuft'))
  else if (i.state === 'waiting') parts.push(t('wartet'))
  if (i.detail) parts.push(i.detail)
  // Übertragen zählt Bytes - als Größe zeigen (190,0 MB von 3,1 GB), sonst Anzahl
  const amount = (x: number) => (i.id === 'files-transfer' || i.id === 'gallery-save' ? fmtSize(x) : n(x))
  if (i.total != null && i.done != null) parts.push(t('{done} von {total}', { done: amount(i.done), total: amount(i.total) }))
  else if (i.pending) parts.push(t('noch {n}', { n: n(i.pending) }))
  if (i.state !== 'running') {
    if (i.finished) parts.push(t('zuletzt {when}', { when: ago(i.finished) }))
    else if (i.state === 'idle') parts.push(t('noch nicht gelaufen'))
  }
  if (i.id === 'thumbs' && i.completed > 0) parts.push(t('{n} erzeugt seit dem Start', { n: n(i.completed) }))
  return parts.join(' · ')
}

/** Anhalten, fortsetzen, überspringen, abbrechen (jobcontrol) - nur für steuerbare Jobs. */
function Controls({ item, onDone }: { item: ActivityItem; onDone: () => void }) {
  const s = useStyles()
  const [busy, setBusy] = useState(false)
  if (!item.control || !featureOn('jobcontrol')) return null
  const act = async (action: 'pause' | 'resume' | 'skip' | 'cancel') => {
    setBusy(true)
    try { await api.post('/api/activity/control', { kind: item.id, action }); onDone() }
    catch (e) { alert(tError(e instanceof Error ? e.message : String(e))) }
    finally { setBusy(false) }
  }
  // [feature:photoexport]
  let oneOff = item.id === 'files-transfer' || item.id === 'photo-export'
  // [feature:photoexport:else]
  // let oneOff = item.id === 'files-transfer'
  // [/feature:photoexport]
  if (item.id === 'lens-apply') oneOff = true // [feature:lenscorr]
  if (item.id === 'lens-library') oneOff = true // [feature:lenslib]
  const btn = (label: string, action: 'pause' | 'resume' | 'skip' | 'cancel', tip: string) =>
    <Tooltip content={tip} relationship="description"><Button size="small" disabled={busy} onClick={() => act(action)}>{label}</Button></Tooltip>
  return <div className={s.controls}>
    {item.hold ? btn(t('Fortsetzen'), 'resume', item.hold === 'skipped' ? t('Gleich wieder wie geplant laufen lassen') : t('Dort weitermachen, wo angehalten wurde'))
      : <>
        {(item.state === 'running' || item.state === 'waiting' || !oneOff) && btn(t('Anhalten'), 'pause', oneOff ? t('Wartet an der aktuellen Datei, bis „Fortsetzen“') : t('Hält an und beginnt keinen neuen Lauf, bis „Fortsetzen“ – auch nach einem Neustart'))}
        {!oneOff && btn(t('Überspringen'), 'skip', t('Diesen Lauf beenden und bis zum nächsten Termin ruhen'))}
      </>}
    {item.state === 'running' && btn(t('Abbrechen'), 'cancel', oneOff ? t('Übertragung beenden; Übertragenes bleibt im Ziel') : t('Nur diesen Lauf beenden; der nächste beginnt wie geplant'))}
  </div>
}

function Row({ item, onChanged }: { item: ActivityItem; onChanged?: () => void }) {
  const s = useStyles()
  const pct = item.state === 'running' && item.total ? Math.min(1, (item.done ?? 0) / item.total) : undefined
  const bar = item.state === 'waiting' && item.total && item.done != null ? Math.min(1, item.done / item.total) : pct
  return (
    <div className={s.row}>
      <span className={s.icon}>
        {item.hold ? <PauseCircle16Regular />
          : item.state === 'running' ? <Spinner size="extra-tiny" />
          : item.state === 'error' ? <ErrorCircle16Regular primaryFill={tokens.colorPaletteRedForeground1} />
          : item.state === 'waiting' ? <Clock16Regular />
          : item.finished ? <CheckmarkCircle16Regular primaryFill={tokens.colorPaletteGreenForeground1} /> : <Circle16Regular />}
      </span>
      <div className={s.body}>
        <span className={s.name}>{nameOf(item.id)}</span>
        <span className={s.sub}>{describe(item)}</span>
        {bar != null && <ProgressBar value={bar} thickness="medium" />}
        {item.error && <span className={s.err}>{tError(item.error)}</span>}
        <Controls item={item} onDone={() => { onChanged?.(); window.dispatchEvent(new Event('dmoffice:activity')) }} />
        {/* [feature:photoexport] */}
        {item.link != null && item.state !== 'running' && <Link href={'#/files/' + item.link.split('/').filter(Boolean).map(encodeURIComponent).join('/')}>{t('Zielordner öffnen')}</Link>}
        {/* [/feature:photoexport] */}
        {!!item.report?.length && <details className={s.sub}>
          {/* [feature:photoexport] */}
          <summary>{item.id === 'photo-export' ? t('Fehler ({n})', { n: n(item.report.length) }) : t('Entfernte Dateien ({n})', { n: n(item.report.length) })}</summary>
          {/* [feature:photoexport:else] */}
          {/* <summary>{t('Entfernte Dateien ({n})', { n: n(item.report.length) })}</summary> */}
          {/* [/feature:photoexport] */}
          {item.report.map(p => <div key={p}>{p}</div>)}
        </details>}
      </div>
    </div>
  )
}

const order = { running: 0, error: 1, waiting: 2, idle: 3 }

/** Die Liste aller Hintergrundaufgaben - im Fenster der Kopfzeile und auf der Einstellungsseite. */
export function ActivityList({ data }: { data: ActivityState | null }) {
  const s = useStyles()
  if (!data) return <Spinner size="small" />
  const items = [...data.items].sort((a, b) => order[a.state] - order[b.state])
  return <div className={s.list}>{items.map(i => <Row key={i.id} item={i} />)}</div>
}

/** Stand der Hintergrundarbeit; schnell, solange jemand hinsieht, sonst selten. */
export function useActivity(watching: boolean): ActivityState | null {
  return usePoll(() => api.get<ActivityState>('/api/activity'), watching ? 3000 : 15000, 'dmoffice:activity')
}

/** Symbol in der Kopfzeile. Auf dem Handy oeffnet es die Seite unter Einstellungen, am Desktop ein Fenster. */
export function ActivityButton({ mobile, className }: { mobile: boolean; className?: string }) {
  const s = useStyles()
  const [open, setOpen] = useState(false)
  // Solange etwas läuft, öfter fragen; endet etwas (Import, Einlesen …), dürfen offene Ansichten wie die Galerie gleich nachladen
  const [busy, setBusy] = useState(false)
  const data = useActivity(open || busy)
  const now = data?.running ?? 0
  const before = useRef(0)
  useEffect(() => {
    if (!data) return
    if (data.running < before.current) window.dispatchEvent(new Event('dmoffice:files-changed'))
    before.current = data.running
    setBusy(data.running > 0)
  }, [data])
  // Beim Oeffnen geht der Fokus auf den Link unten und scrollt die Liste ans Ende - zurueck an den Anfang
  const surface = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!open) return
    const top = () => { if (surface.current) surface.current.scrollTop = 0 }
    const timers = [0, 60, 200, 600].map(ms => setTimeout(top, ms))
    return () => timers.forEach(clearTimeout)
  }, [open])
  const label = now > 0 ? t('Aktivität – im Hintergrund läuft etwas') : t('Aktivität')
  const button = (
    <Tooltip content={label} relationship="label">
      <Button appearance="transparent" className={className} aria-label={label}
        // Punkt im Symbol statt als Inhalt: sonst wird der Knopf breiter und die Leiste links davon verrutscht
        icon={<span style={{ position: 'relative', display: 'inline-flex' }}><Pulse20Regular />{now > 0 && <span className={s.dot} />}</span>}
        onClick={mobile ? () => { location.hash = '#/settings/activity' } : undefined} />
    </Tooltip>
  )
  if (mobile) return button
  return (
    <Popover open={open} onOpenChange={(_, d) => setOpen(d.open)} positioning="below-end">
      <PopoverTrigger disableButtonEnhancement>{button}</PopoverTrigger>
      <PopoverSurface className={s.surface} ref={surface}>
        <div className={s.head}>
          <Text weight="semibold">{t('Aktivität')}</Text>
          <Text size={200}>{now > 0 ? t('{n} laufen', { n: now }) : t('Gerade ruhig')}</Text>
        </div>
        {/* Im Fenster nur, was gerade ansteht; alle Dienste stehen hinter „Alles anzeigen“ */}
        <ActivityList data={data && { ...data, items: data.items.filter(i => i.state !== 'idle' || !!i.hold || (!!i.report?.length && !!i.finished && Date.now() - new Date(i.finished).getTime() < 86_400_000)) }} />
        <div className={s.foot}>
          <Link href="#/settings/activity" onClick={() => setOpen(false)}>{t('Alles anzeigen')}</Link>
        </div>
      </PopoverSurface>
    </Popover>
  )
}
