import {
  Button, Card, Link, MessageBar, MessageBarBody, Spinner, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, Tooltip, makeStyles, tokens,
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
  // Kopf der Zahlenspalten: Fluent legt den Text in ein Flex-Element, textAlign allein greift dort nicht
  numHead: { '& .fui-TableHeaderCell__button': { justifyContent: 'flex-end', textAlign: 'right' } },
  barCell: { width: '120px' },
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
  /** Dateiinhalte, die noch in der Datenbank liegen (Auslagern in den BlobStore laeuft im Hintergrund) */
  fileContentsPending?: number
  /** Freier Platz, den das Verdichten voruebergehend braucht, und was frei ist */
  compactNeed: number; diskFree?: number | null
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
/** Verdichten schreibt die belegten Seiten neu: etwa eine Minute je GB, das Freie kostet kaum etwas. */
function compactDuration(used: number): string {
  const min = Math.max(1, Math.ceil(used / 1024 / 1024 / 1024))
  return min === 1 ? t('etwa eine Minute') : min < 60 ? t('etwa {n} Minuten', { n: min }) : t('etwa {h} Std. {m} Min.', { h: Math.floor(min / 60), m: min % 60 })
}

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
      <Text className={s.muted}>
        {t('Woran der Server gerade im Hintergrund arbeitet (Auslagern, Index, Vorschaubilder, Aufräumen), steht unter')}{' '}
        <Link href="#/settings/activity">{t('Aktivität')}</Link>.
      </Text>
      <div className={s.tiles}>
        {tile(fmtSize(stats.database), t('Datenbank (verschlüsselt)'), t('Die ganze Datei auf dem Server, mit allem'))}
        {tile(n(stats.mails), t('Mails hier'), t('davon vollständig gespeichert: {size} (komprimiert)', { size: fmtSize(stats.mailBytes) }))}
        {slim.length > 0 && tile(fmtSize(saved), t('Gespart durch „Schlank“'),
          t('So viel Mail (Größe beim Anbieter) liegt nur dort und wird beim Öffnen geholt.'))}
        {tile(n(stats.events), t('Termine'))}
        {tile(n(stats.tasks), t('Aufgaben'))}
        {tile(n(stats.contacts), t('Kontakte'))}
        {tile(n(stats.notes), t('Notizen'), t('Bilder und Anhänge: {size}', { size: fmtSize(stats.noteBlobs) }))}
        {tile(`${n(stats.files)} · ${fmtSize(stats.fileBytes)}`, t('Dateiablage (Anzahl · Größe)'))}
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
              {a.storage === 'slim' && <>{' · '}{t('Schlank: vollständig hier sind die Mails der letzten {days} Tage (neue und zuletzt geöffnete zuerst, zusammen höchstens {max}), von älteren Kopfdaten und Vorschau; {saved} liegen nur beim Anbieter', {
                days: a.slimDays ?? 30, max: fmtSize((a.slimMb ?? 200) * 1024 * 1024), saved: fmtSize(a.serverOnly ?? 0) })}</>}
            </Text>
            <div style={{ overflowX: 'auto' }}>
            <Table size="small" style={{ minWidth: 960 }}>
              <TableHeader>
                <TableRow>
                  <TableHeaderCell style={{ width: 230 }}>{t('Ordner')}</TableHeaderCell>
                  <TableHeaderCell className={s.barCell} />
                  <TableHeaderCell className={s.numHead}>{t('Größe')}</TableHeaderCell>
                  <TableHeaderCell className={s.numHead}>{t('Hier')}</TableHeaderCell>
                  <TableHeaderCell className={s.numHead}>{t('Server')}</TableHeaderCell>
                  <TableHeaderCell className={s.numHead}>{t('Ungelesen')}</TableHeaderCell>
                  <TableHeaderCell className={s.numHead}>{t('Mit Anhang')}</TableHeaderCell>
                  <TableHeaderCell className={s.numHead}>{t('Gespeichert')}</TableHeaderCell>
                  <TableHeaderCell className={s.numHead}>{t('Zeitraum')}</TableHeaderCell>
                </TableRow>
              </TableHeader>
              <TableBody>
                {a.folders.map(f => (
                  <TableRow key={f.path}>
                    <TableCell title={f.path} style={{ minWidth: 0, overflow: 'hidden', whiteSpace: 'nowrap', textOverflow: 'ellipsis' }}>{folderLabel({ ...f, delimiter: '/', selectable: true, total: f.serverTotal, synced: null })}</TableCell>
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
    try { await api.post('/api/settings/compact'); watch() } catch (e: any) {
      setBusy(false)
      // 409: der Platz reicht nicht - nichts ist passiert
      if (e?.status === 409 && e.data) fail(new Error(t('Zu wenig freier Platz zum Verdichten: es fehlen etwa {missing} (gebraucht {need}, frei {free}). Es wurde nichts verändert.', { missing: fmtSize(e.data.missing), need: fmtSize(e.data.need), free: fmtSize(e.data.free ?? 0) })))
      else fail(e)
    }
  }
  const lacking = stats.diskFree != null && stats.diskFree < stats.compactNeed
  return (
    <MessageBar intent={reclaim > 200 * 1024 * 1024 ? 'warning' : 'info'}>
      <MessageBarBody>
        {t('Die Datenbank ({total}) besteht aus der Datenbankdatei ({file}) und dem Schreibprotokoll ({wal}, neue Änderungen, die noch nicht in die Datei übernommen sind). Davon ist {free} leer.', { total: fmtSize(stats.file + stats.wal), file: fmtSize(stats.file), wal: fmtSize(stats.wal), free: fmtSize(stats.free) })}
        {reclaim > 50 * 1024 * 1024
          ? ' ' + t('Verdichten gibt etwa {size} frei und dauert voraussichtlich {duration}; der Mail-Sync wartet so lange.', { size: fmtSize(reclaim), duration: compactDuration(stats.file - stats.free) })
          : ' ' + t('Kaum freier Platz – verdichten lohnt sich nicht.')}
        {' ' + t('Das Verdichten schreibt die Datenbank vorübergehend noch einmal und braucht dafür etwa {need} freien Platz (frei: {free}).', { need: fmtSize(stats.compactNeed), free: stats.diskFree != null ? fmtSize(stats.diskFree) : '?' })}
        {lacking && ' ' + t('Das reicht nicht – erst Platz schaffen.')}
        {!!stats.fileContentsPending && ' ' + t('Dateiinhalte werden gerade aus der Datenbank ausgelagert, noch {n} – danach hier verdichten.', { n: n(stats.fileContentsPending) })}
      </MessageBarBody>
      {reclaim > 50 * 1024 * 1024 && (
        <Button size="small" onClick={run} disabled={busy || lacking} style={{ marginLeft: 12 }}>
          {busy ? <><Spinner size="extra-tiny" /> {t('verdichte …')}</> : t('Datenbank verdichten')}
        </Button>
      )}
    </MessageBar>
  )
}
