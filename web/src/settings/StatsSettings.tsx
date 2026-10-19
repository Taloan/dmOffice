import {
  Button, Card, MessageBar, MessageBarBody, Spinner, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Tooltip, makeStyles, tokens,
} from '@fluentui/react-components'
import { useEffect, useRef, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { folderLabel } from '../mail/FolderPane'
import { t, locale } from '../i18n'

const useStyles = makeStyles({
  tiles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(170px, 1fr))', gap: '10px' },
  tile: { padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: '2px' },
  value: { fontSize: '22px', fontWeight: 600, fontVariantNumeric: 'tabular-nums' },
  label: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' },
  barCell: { width: '160px' },
  bar: { height: '8px', borderRadius: '4px', backgroundColor: tokens.colorNeutralBackground5, overflow: 'hidden' },
  fill: { height: '100%', backgroundColor: tokens.colorBrandBackground, borderRadius: '4px' },
  muted: { color: tokens.colorNeutralForeground3 },
})

interface FolderStat {
  path: string; name: string; special: string | null; serverTotal: number; unread: number; local: number
  size: number; stored: number; cached: number; withAttachments: number; oldest: string | null; newest: string | null
}
interface Stats {
  file: number; wal: number; free: number
  database: number; mails: number; mailBytes: number; events: number; tasks: number; contacts: number
  notes: number; noteBlobs: number; files: number; fileBytes: number; appPasswords: number
  accounts: {
    id: string; name: string; email: string; syncMonths: number; folders: FolderStat[]
    storage?: 'full' | 'slim'; slimDays?: number; slimMb?: number
    /** Schlank: Groesse der Mails, deren Inhalt nur beim Anbieter liegt */
    serverOnly?: number
  }[]
}

const n = (x: number) => x.toLocaleString(locale)
const day = (iso: string | null) => (iso ? new Date(iso).toLocaleDateString(locale) : '–')

/** Wie viel wo liegt: Ordner je Konto, nach Groesse, und die Datenbank insgesamt. */
export function StatsSettings() {
  const s = useStyles()
  const { fail } = useApp()
  const [stats, setStats] = useState<Stats | null>(null)
  useEffect(() => { api.get<Stats>('/api/settings/stats').then(setStats).catch(fail) }, [fail])
  if (!stats) return <Spinner />
  const slim = stats.accounts.filter(a => a.storage === 'slim')
  const saved = slim.reduce((sum, a) => sum + (a.serverOnly ?? 0), 0)

  const tile = (value: string, label: string, hint?: string) => (
    <Card className={s.tile}>
      <span className={s.value}>{value}</span>
      <Tooltip content={hint ?? label} relationship="description"><span className={s.label}>{label}</span></Tooltip>
    </Card>
  )

  return (
    <>
      <Text size={600} weight="semibold">{t('Speicher & Statistik')}</Text>
      <div className={s.tiles}>
        {tile(fmtSize(stats.database), t('Datenbank (verschlüsselt)'), t('Die ganze Datei auf dem Server, mit allem'))}
        {tile(n(stats.mails), t('Mails hier'), t('davon vollständig gespeichert: {size} (komprimiert)', { size: fmtSize(stats.mailBytes) }))}
        {slim.length > 0 && tile(fmtSize(saved), t('Gespart durch „Schlank“'),
          t('So viel Mail (Größe beim Anbieter) liegt nur dort und wird beim Öffnen geholt.'))}
        {tile(n(stats.events), t('Termine'))}
        {tile(n(stats.tasks), t('Aufgaben'))}
        {tile(n(stats.contacts), t('Kontakte'))}
        {tile(n(stats.notes), t('Notizen'), t('Bilder und Anhänge: {size}', { size: fmtSize(stats.noteBlobs) }))}
        {tile(`${n(stats.files)} · ${fmtSize(stats.fileBytes)}`, t('Dateien'))}
        {tile(n(stats.appPasswords), t('App-Passwörter'))}
      </div>

      <Compact stats={stats} onDone={() => api.get<Stats>('/api/settings/stats').then(setStats).catch(fail)} />

      {stats.accounts.map(a => {
        const max = Math.max(1, ...a.folders.map(f => f.size))
        const sum = a.folders.reduce((acc, f) => ({ local: acc.local + f.local, size: acc.size + f.size, stored: acc.stored + f.stored, server: acc.server + f.serverTotal }),
          { local: 0, size: 0, stored: 0, server: 0 })
        return (
          <div key={a.id} style={{ marginTop: 12 }}>
            <Text weight="semibold" size={500} block>{a.name}</Text>
            <Text className={s.muted} block style={{ marginBottom: 6 }}>
              {a.email} · {a.syncMonths === 1 ? t('letzter Monat synchronisiert') : a.syncMonths ? t('letzte {n} Monate synchronisiert', { n: a.syncMonths }) : t('alles synchronisiert')} ·
              {' '}{sum.server === 1 ? t('{local} von 1 Mail hier', { local: n(sum.local) }) : t('{local} von {server} Mails hier', { local: n(sum.local), server: n(sum.server) })}
              {' · '}{t('{size} auf dem Server, {stored} gespeichert', { size: fmtSize(sum.size), stored: fmtSize(sum.stored) })}
              {a.storage === 'slim' && <>{' · '}{t('Schlank: Inhalte {days} Tage, höchstens {max}; {saved} nur beim Anbieter', {
                days: a.slimDays ?? 30, max: fmtSize((a.slimMb ?? 200) * 1024 * 1024), saved: fmtSize(a.serverOnly ?? 0) })}</>}
            </Text>
            <Table size="small">
              <TableHeader>
                <TableRow>
                  <TableHeaderCell>{t('Ordner')}</TableHeaderCell>
                  <TableHeaderCell className={s.barCell} />
                  <TableHeaderCell className={s.num}>{t('Größe')}</TableHeaderCell>
                  <TableHeaderCell className={s.num}>{t('Hier')}</TableHeaderCell>
                  <TableHeaderCell className={s.num}>{t('Server')}</TableHeaderCell>
                  <TableHeaderCell className={s.num}>{t('Ungelesen')}</TableHeaderCell>
                  <TableHeaderCell className={s.num}>{t('Mit Anhang')}</TableHeaderCell>
                  <TableHeaderCell className={s.num}>{t('Gespeichert')}</TableHeaderCell>
                  <TableHeaderCell className={s.num}>{t('Zeitraum')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {a.folders.map(f => (
                  <TableRow key={f.path}>
                    <TableCell title={f.path}>{folderLabel({ ...f, delimiter: '/', selectable: true, total: f.serverTotal, synced: null })}</TableCell>
                    <TableCell className={s.barCell}><div className={s.bar}><div className={s.fill} style={{ width: `${(f.size / max) * 100}%` }} /></div></TableCell>
                    <TableCell className={s.num}>{fmtSize(f.size)}</TableCell>
                    <TableCell className={s.num}>{n(f.local)}</TableCell>
                    <TableCell className={s.num}>{n(f.serverTotal)}</TableCell>
                    <TableCell className={s.num}>{f.unread ? n(f.unread) : ''}</TableCell>
                    <TableCell className={s.num}>{f.withAttachments ? n(f.withAttachments) : ''}</TableCell>
                    <TableCell className={s.num}>
                      <Tooltip content={f.local === 1 ? t('{cached} von 1 Mail vollständig hier, komprimiert {size}', { cached: n(f.cached), size: fmtSize(f.stored) }) : t('{cached} von {local} Mails vollständig hier, komprimiert {size}', { cached: n(f.cached), local: n(f.local), size: fmtSize(f.stored) })} relationship="description">
                        <span>{f.local ? `${Math.round((f.cached / f.local) * 100)} %` : ''}</span>
                      </Tooltip>
                    </TableCell>
                    <TableCell className={s.num}>{f.local ? `${day(f.oldest)} – ${day(f.newest)}` : ''}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        )
      })}
    </>
  )
}

/** Freier Platz in der Datei und im Journal - verdichten gibt ihn dem Server zurueck. */
function Compact({ stats, onDone }: { stats: Stats; onDone: () => void }) {
  const { fail, notify } = useApp()
  const [busy, setBusy] = useState(false)
  const reclaim = stats.free + Math.max(0, stats.wal - 64 * 1024 * 1024)
  // Laeuft im Hintergrund; alle drei Sekunden nachfragen
  // Intervall merken, damit es beim Aushaengen endet
  const timer = useRef<number | undefined>(undefined)
  const stop = () => { clearInterval(timer.current); timer.current = undefined }
  useEffect(() => stop, [])
  const watch = () => {
    stop()
    const id = window.setInterval(async () => {
      const j = await api.get('/api/settings/compact').catch(() => null)
      if (timer.current !== id || !j || j.running) return
      stop()
      setBusy(false)
      if (j.error) fail(new Error(j.error))
      else notify(t('Datenbank verdichtet'), `${fmtSize(j.before)} → ${fmtSize(j.after)}`)
      onDone()
    }, 3000)
    timer.current = id
  }
  useEffect(() => { api.get('/api/settings/compact').then(j => { if (j.running) { setBusy(true); watch() } }).catch(() => {}) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const run = async () => {
    setBusy(true)
    try { await api.post('/api/settings/compact'); watch() } catch (e) { fail(e); setBusy(false) }
  }
  return (
    <MessageBar intent={reclaim > 200 * 1024 * 1024 ? 'warning' : 'info'}>
      <MessageBarBody>
        {t('Datei {file}, Journal {wal}, davon frei {free}.', { file: fmtSize(stats.file), wal: fmtSize(stats.wal), free: fmtSize(stats.free) })}
        {reclaim > 50 * 1024 * 1024
          ? ' ' + t('Verdichten gibt etwa {size} frei. Dauert je GB etwa eine Minute; der Mail-Sync wartet so lange.', { size: fmtSize(reclaim) })
          : ' ' + t('Kaum freier Platz – verdichten lohnt sich nicht.')}
      </MessageBarBody>
      {reclaim > 50 * 1024 * 1024 && (
        <Button size="small" onClick={run} disabled={busy} style={{ marginLeft: 12 }}>
          {busy ? <><Spinner size="extra-tiny" /> {t('verdichte …')}</> : t('Datenbank verdichten')}
        </Button>
      )}
    </MessageBar>
  )
}
