import { makeStyles, Spinner, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { locale, t } from '../i18n'
import { Snippet } from '../shell/Snippet'
import { fmtWhen, KindIcon } from './common'

export interface ExternalHit { mount: string; mountName: string; path: string; name: string; isDir: boolean; size: number; modified: string; snippet: string | null }

/** Wie aktuell der Index eines Server-Mounts ist: ohne das wäre „nichts gefunden“ keine verlässliche Aussage. */
export interface ExternalStand { mount: string; name: string; level: string; lastScan: string | null; checking: boolean; open: number; limited: boolean }

const standText = (s: ExternalStand) => {
  const parts = [s.lastScan ? t('Dateien geprüft: {when}', { when: fmtWhen(s.lastScan, locale) }) : t('Noch nicht vollständig geprüft')]
  if (s.checking) parts.push(t('Prüfung läuft'))
  if (s.level === 'names') parts.push(t('Inhalte werden hier nicht durchsucht'))
  else if (s.open > 0) parts.push(t('{n} Dateien noch nicht ausgewertet', { n: s.open.toLocaleString(locale) }))
  if (s.level !== 'names' && s.limited) parts.push(t('Inhalte nur aus gewählten Ordnern'))
  return parts.join(' · ')
}

const useStyles = makeStyles({
  head: { display: 'block', margin: '16px 0 4px', fontWeight: tokens.fontWeightSemibold },
  row: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  body: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  meta: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums' },
  stand: { display: 'block', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, marginTop: '4px' },
  snip: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, overflow: 'hidden', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical' },
})

const parentOf = (p: string) => (p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '')

/**
 * Treffer in Ordnern auf dem Server (Namen und Inhalte, wenn der Admin die Suche dort eingeschaltet hat). Ohne mount: in allen,
 * die man sehen darf (unter den Treffern der eigenen Dateien); mit mount: nur in diesem Ordner, auf Wunsch unterhalb von path.
 * Ein Klick öffnet den Ordner (bei einer Datei den, in dem sie liegt).
 */
export function ExternalHits({ term, mount, path, heading, onOpen }: {
  term: string; mount?: string; path?: string; heading?: boolean; onOpen: (mount: string, path: string, file: string | null) => void
}) {
  const s = useStyles()
  const [hits, setHits] = useState<ExternalHit[] | null>(null)
  const [stand, setStand] = useState<ExternalStand[]>([])
  useEffect(() => {
    if (!term) { setHits(null); return }
    let stale = false
    setHits(null)
    api.get<{ items: ExternalHit[]; stand: ExternalStand[] }>('/api/files/external/search?' + q({ q: term, mount, path: path || undefined }))
      .then(r => { if (!stale) { setHits(r.items); setStand(r.stand ?? []) } }).catch(() => { if (!stale) setHits([]) })
    return () => { stale = true }
  }, [term, mount, path])
  if (hits == null) return mount ? <Spinner size="tiny" style={{ marginTop: 16 }} /> : null
  const standLines = stand.map(x => <span key={x.mount} className={s.stand}>{mount ? standText(x) : `${x.name}: ${standText(x)}`}</span>)
  if (hits.length === 0) {
    if (stand.length === 0 && !mount) return null
    return (
      <div style={mount ? { padding: 16 } : undefined}>
        {heading && <Text className={s.head}>{t('In Ordnern auf dem Server')}</Text>}
        <Text className={s.sub} style={{ display: 'block' }}>{t('Nichts gefunden.')}</Text>
        {standLines}
      </div>
    )
  }
  return (
    <div>
      {heading && <Text className={s.head}>{t('In Ordnern auf dem Server')}</Text>}
      {hits.map(h => {
        const where = [h.mountName, ...parentOf(h.path).split('/').filter(Boolean)].join(' / ')
        const open = () => onOpen(h.mount, h.isDir ? h.path : parentOf(h.path), h.isDir ? null : h.name)
        return (
          <div key={h.mount + '|' + h.path} className={s.row} role="button" tabIndex={0} onClick={open} onKeyDown={e => { if (e.key === 'Enter') open() }}>
            <KindIcon item={{ name: h.name, isDir: h.isDir }} />
            <div className={s.body}>
              <Text truncate wrap={false}>{h.name}</Text>
              <span className={s.sub} title={where}>{where}</span>
              {h.snippet && <span className={s.snip} title={t('Im Inhalt gefunden')}><Snippet text={h.snippet} /></span>}
            </div>
            <span className={s.meta}>{h.isDir ? '' : fmtSize(h.size) + ' · '}{fmtWhen(h.modified, locale)}</span>
          </div>
        )
      })}
      <div style={{ padding: '6px 8px' }}>{standLines}</div>
    </div>
  )
}
