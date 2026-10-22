import { Button, Link, Popover, PopoverSurface, PopoverTrigger, ProgressBar, Spinner, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import { CheckmarkCircle16Regular, ErrorCircle16Regular, Pulse20Regular, Clock16Regular, Circle16Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
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
  surface: { width: 'min(380px, calc(100vw - 24px))', maxHeight: 'min(560px, 80vh)', overflowY: 'auto', padding: '12px 16px' },
  head: { display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', paddingBottom: '4px' },
  foot: { paddingTop: '8px', fontSize: '12px' },
  dot: {
    position: 'absolute', top: '5px', right: '5px', width: '8px', height: '8px', borderRadius: '50%',
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
    case 'mail-slim': return t('Schlank-Modus aufräumen')
    case 'mail-trash': return t('Papierkorb und Junk leeren')
    case 'files-blobs': return t('Dateiinhalte auslagern')
    case 'files-hashes': return t('Prüfsummen der Dateien berechnen')
    case 'files-cleanup': return t('Dateien aufräumen')
    case 'text-index': return t('Volltextindex und Texterkennung')
    case 'thumbs': return t('Vorschaubilder erzeugen')
    case 'notes-sync': return t('Notizen abgleichen (notizi)')
    case 'subscriptions': return t('Kalender-Abos abrufen')
    case 'compact': return t('Datenbank verdichten')
    default: return id
  }
}

/** Ein Satz zum Zustand: was gerade geschieht, was noch ansteht, wann es zuletzt lief. */
function describe(i: ActivityItem): string {
  const parts: string[] = []
  if (i.state === 'running') parts.push(t('läuft'))
  else if (i.state === 'waiting') parts.push(t('wartet'))
  if (i.detail) parts.push(i.detail)
  if (i.total != null && i.done != null) parts.push(t('{done} von {total}', { done: n(i.done), total: n(i.total) }))
  else if (i.pending) parts.push(t('noch {n}', { n: n(i.pending) }))
  if (i.state !== 'running') {
    if (i.finished) parts.push(t('zuletzt {when}', { when: ago(i.finished) }))
    else if (i.state === 'idle') parts.push(t('noch nicht gelaufen'))
  }
  if (i.id === 'thumbs' && i.completed > 0) parts.push(t('{n} erzeugt seit dem Start', { n: n(i.completed) }))
  return parts.join(' · ')
}

function Row({ item }: { item: ActivityItem }) {
  const s = useStyles()
  const pct = item.state === 'running' && item.total ? Math.min(1, (item.done ?? 0) / item.total) : undefined
  const bar = item.state === 'waiting' && item.total && item.done != null ? Math.min(1, item.done / item.total) : pct
  return (
    <div className={s.row}>
      <span className={s.icon}>
        {item.state === 'running' ? <Spinner size="extra-tiny" />
          : item.state === 'error' ? <ErrorCircle16Regular primaryFill={tokens.colorPaletteRedForeground1} />
          : item.state === 'waiting' ? <Clock16Regular />
          : item.finished ? <CheckmarkCircle16Regular primaryFill={tokens.colorPaletteGreenForeground1} /> : <Circle16Regular />}
      </span>
      <div className={s.body}>
        <span className={s.name}>{nameOf(item.id)}</span>
        <span className={s.sub}>{describe(item)}</span>
        {bar != null && <ProgressBar value={bar} thickness="medium" />}
        {item.error && <span className={s.err}>{tError(item.error)}</span>}
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
  const data = useActivity(open)
  const now = data?.running ?? 0
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
        style={{ position: 'relative' }}
        icon={<Pulse20Regular />}
        onClick={mobile ? () => { location.hash = '#/settings/activity' } : undefined}>
        {now > 0 && <span className={s.dot} />}
      </Button>
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
        <ActivityList data={data} />
        <div className={s.foot}>
          <Link href="#/settings/activity" onClick={() => setOpen(false)}>{t('Alles anzeigen')}</Link>
        </div>
      </PopoverSurface>
    </Popover>
  )
}
