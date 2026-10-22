import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, MessageBar, MessageBarBody,
  ProgressBar, Select, Spinner, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { useCallback, useEffect, useRef, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { t, locale } from '../i18n'

// Migrations-Assistent: was an Datenaenderungen ansteht oder laeuft, mit Platz, Fortschritt und den Schaltern
// "Jetzt starten", "Pausieren", "Sicherung ansehen". Gleiche Daten wie GET /api/admin/update (UpdateCenter.Describe).

interface Step { version: number; title: string; heavy: boolean }
interface Plan {
  userId: string; login: string; from: number; to: number; pending: boolean
  state: 'current' | 'ready' | 'running' | 'consent' | 'space' | 'backup-space' | 'error'
  message?: string | null; steps: Step[]; dbBytes: number; migrationBytes: number; backupBytes: number; backupExists: boolean
  willBackup: boolean; contentBytes: number; needsConsent: boolean; freeBytes: number | null; missingBytes: number
  backupMissingBytes: number; etaSeconds: number
}
interface Act { state: string; done?: number | null; total?: number | null; detail?: string | null; error?: string | null }
interface BlobReport { state: 'idle' | 'ready' | 'running' | 'consent' | 'space' | 'paused'; pending: number; bytes: number; freeBytes: number | null; missingBytes: number; etaSeconds: number }
interface BackupUser { id: string; login: string; schema: number; to: number; bytes: number; created: string }
interface Backup { name: string; created: string; appVersion: string; bytes: number; path: string; users: BackupUser[] }
export interface UpdateInfo {
  version: string; schema: number; dataDir: string; blobDir: string; freeData: number | null; freeBlobs: number | null; reserve: number
  keepDays: number; paused: boolean; approved: { migration: boolean; blobs: boolean; withoutBackup: boolean }; attention: boolean
  users: { plan: Plan; activity: Act | null; backupActivity: Act | null }[]
  blobs: { userId: string; login: string; report: BlobReport | null; activity: Act | null }[]
  backups: Backup[]; backupBytes: number
}

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '14px', maxWidth: '860px' },
  card: {
    display: 'flex', flexDirection: 'column', gap: '8px', padding: '12px 14px', borderRadius: tokens.borderRadiusLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1,
  },
  head: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  nums: { display: 'flex', gap: '16px', flexWrap: 'wrap', color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200 },
  buttons: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  steps: { margin: 0, paddingLeft: '18px', fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground2 },
  tableWrap: { overflowX: 'auto' },
})

export const minutes = (sec: number) =>
  sec < 60 ? t('unter einer Minute') : sec < 3600 ? t('etwa {n} Minuten', { n: Math.round(sec / 60) }) : t('etwa {n} Stunden', { n: Math.round(sec / 360) / 10 })

const stateBadge = (state: string) => {
  switch (state) {
    case 'running': return <Badge color="brand">{t('läuft')}</Badge>
    case 'ready': return <Badge color="informative">{t('startet gleich')}</Badge>
    case 'consent': return <Badge color="warning">{t('wartet auf „Jetzt starten“')}</Badge>
    case 'space': return <Badge color="danger">{t('zu wenig Platz')}</Badge>
    case 'backup-space': return <Badge color="warning">{t('Platz für die Sicherung fehlt')}</Badge>
    case 'error': return <Badge color="danger">{t('fehlgeschlagen')}</Badge>
    case 'paused': return <Badge color="warning">{t('pausiert')}</Badge>
    default: return <Badge color="success">{t('aktuell')}</Badge>
  }
}

/** Stand laden und regelmaessig nachfragen - schneller, solange etwas laeuft oder wartet. */
export function useUpdateInfo(): [UpdateInfo | null, () => void, (i: UpdateInfo) => void] {
  const [info, setInfo] = useState<UpdateInfo | null>(null)
  const timer = useRef<number | undefined>(undefined)
  const load = useCallback(() => {
    api.get<UpdateInfo>('/api/admin/update/').then(setInfo).catch(() => {})
  }, [])
  useEffect(() => {
    load()
    return () => clearInterval(timer.current)
  }, [load])
  const busy = !!info && (info.attention || info.users.some(u => u.plan.state === 'running') || info.blobs.some(b => b.report && b.report.state !== 'idle'))
  useEffect(() => {
    clearInterval(timer.current)
    timer.current = window.setInterval(load, busy ? 3000 : 15000)
    return () => clearInterval(timer.current)
  }, [busy, load])
  return [info, load, setInfo]
}

export function UpdatePanel({ gate = false }: { gate?: boolean }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [info, , setInfo] = useUpdateInfo()
  const [viewing, setViewing] = useState<Backup | null>(null)
  const [confirmNoBackup, setConfirmNoBackup] = useState(false)

  if (!info) return <Spinner />

  const call = async (fn: () => Promise<UpdateInfo>, done?: string) => {
    try { setInfo(await fn()); if (done) notify(done) } catch (e) { fail(e) }
  }
  const start = (withoutBackup = false) => call(() => api.post('/api/admin/update/start', { withoutBackup, confirmWithoutBackup: withoutBackup }), t('Gestartet'))
  const pause = (paused: boolean) => call(() => api.post('/api/admin/update/pause', { paused }))
  const recheck = () => call(() => api.post('/api/admin/update/recheck'), t('Wird geprüft'))
  const setKeep = (keepDays: number) => call(() => api.put('/api/admin/update/settings', { keepDays }), t('Gespeichert'))
  const removeBackup = async (b: Backup) => {
    if (!confirm(t('Sicherung „{name}“ ({size}) endgültig löschen?', { name: b.name, size: fmtSize(b.bytes) }))) return
    await call(() => api.del(`/api/admin/update/backups/${encodeURIComponent(b.name)}`))
  }

  const pendingUsers = info.users.filter(u => u.plan.pending)
  const blobsOpen = info.blobs.filter(b => b.report && b.report.state !== 'idle')
  const anyNoBackup = pendingUsers.some(u => u.plan.state === 'backup-space')
  const anyConsent = pendingUsers.some(u => u.plan.state === 'consent') || blobsOpen.some(b => b.report!.state === 'consent')
  const anyBlobRunning = blobsOpen.some(b => b.report!.state === 'running' || b.activity?.state === 'running')
  const nothing = pendingUsers.length === 0 && blobsOpen.length === 0

  return (
    <div className={s.root}>
      {!gate && <Text size={500} weight="semibold">{t('Updates und Datenänderungen')}</Text>}
      <Text>
        {t('Bei einer neuen Version bringt dmOffice die Daten selbst auf den neuen Stand. Vorher prüft es den freien Platz und sichert die Datenbanken; reicht der Platz nicht, wird nichts verändert, und hier steht, was fehlt. Große Umbauten starten erst nach „Jetzt starten“.')}
      </Text>
      <div className={s.nums}>
        <span>{t('Version {v}, Datenstand {n}', { v: info.version, n: info.schema })}</span>
        <span>{t('Frei im Datenordner: {size}', { size: info.freeData != null ? fmtSize(info.freeData) : '?' })}</span>
        {info.blobDir !== info.dataDir && info.freeBlobs != null && <span>{t('Frei bei den Dateiinhalten: {size}', { size: fmtSize(info.freeBlobs) })}</span>}
        <span>{t('Reserve: {size}', { size: fmtSize(info.reserve) })}</span>
      </div>

      {nothing && <MessageBar intent="success"><MessageBarBody>{t('Alle Daten sind auf dem neuesten Stand, es steht nichts an.')}</MessageBarBody></MessageBar>}

      {pendingUsers.map(({ plan, activity, backupActivity }) => (
        <div key={plan.userId} className={s.card}>
          <div className={s.head}>
            <Text weight="semibold">{plan.login}</Text>
            {stateBadge(plan.state)}
            <span className={s.muted}>{t('Datenstand {from} → {to}', { from: plan.from, to: plan.to })}</span>
          </div>
          <ul className={s.steps}>
            {plan.steps.map(st => (
              <li key={st.version}>{t(st.title)}{st.heavy ? ' – ' + t('schreibt viele Daten um') : ''}</li>
            ))}
          </ul>
          <div className={s.nums}>
            <span>{t('Datenbank: {size}', { size: fmtSize(plan.dbBytes) })}</span>
            <span>{t('Platz für den Umbau: {size}', { size: fmtSize(plan.migrationBytes + info.reserve) })}</span>
            <span>{plan.backupExists ? t('Sicherung vorhanden') : plan.willBackup ? t('Sicherung: {size}', { size: fmtSize(plan.backupBytes) }) : t('ohne Sicherung')}</span>
            {plan.freeBytes != null && <span>{t('Frei: {size}', { size: fmtSize(plan.freeBytes) })}</span>}
            <span>{t('Dauer: {eta}', { eta: minutes(plan.etaSeconds) })}</span>
          </div>
          {plan.state === 'space' && (
            <MessageBar intent="error"><MessageBarBody>
              {t('Es fehlen etwa {missing} freier Platz. Bis genug da ist, wird nichts verändert – die Daten bleiben unberührt, dieser Benutzer kommt nur noch nicht an seine Daten. Platz schaffen (Dateien löschen, größeren Datenträger einbinden) und „Erneut prüfen“.', { missing: fmtSize(plan.missingBytes) })}
            </MessageBarBody></MessageBar>
          )}
          {plan.state === 'backup-space' && (
            <MessageBar intent="warning"><MessageBarBody>
              {t('Für die Sicherung vor dem Umbau fehlen etwa {missing}. Entweder Platz schaffen und „Erneut prüfen“ – oder ausdrücklich ohne Sicherung fortfahren (dann hilft bei einem Fehler nur deine eigene Sicherung des Datenordners).', { missing: fmtSize(plan.backupMissingBytes) })}
            </MessageBarBody></MessageBar>
          )}
          {plan.state === 'consent' && (
            <MessageBar intent="warning"><MessageBarBody>
              {t('Dieser Umbau ist groß ({eta}, {size} Platz) und startet nicht von selbst. Der Server läuft währenddessen weiter, dieser Benutzer wartet. Am besten vorher den ganzen Datenordner kopieren.', { eta: minutes(plan.etaSeconds), size: fmtSize(plan.migrationBytes) })}
            </MessageBarBody></MessageBar>
          )}
          {plan.state === 'error' && (
            <MessageBar intent="error"><MessageBarBody>
              {t('Die Datenänderung ist gescheitert; die Daten sind unverändert (jeder Schritt gilt ganz oder gar nicht).')}{plan.message ? ' ' + plan.message : ''}
            </MessageBarBody></MessageBar>
          )}
          {plan.state === 'running' && activity && (
            <>
              <ProgressBar value={activity.total ? (activity.done ?? 0) / activity.total : undefined} />
              <Text size={200}>{activity.detail ?? ''}</Text>
            </>
          )}
          {backupActivity?.state === 'running' && <Text size={200}>{t('Sicherung wird angelegt …')}</Text>}
        </div>
      ))}

      {blobsOpen.map(({ userId, login, report, activity }) => report && (
        <div key={userId} className={s.card}>
          <div className={s.head}>
            <Text weight="semibold">{login}</Text>
            {stateBadge(activity?.state === 'running' ? 'running' : report.state)}
            <span className={s.muted}>{t('Dateiinhalte aus der Datenbank in eigene Dateien auslagern')}</span>
          </div>
          <div className={s.nums}>
            <span>{t('{n} Einträge, {size}', { n: report.pending, size: fmtSize(report.bytes) })}</span>
            {report.freeBytes != null && <span>{t('Frei: {size}', { size: fmtSize(report.freeBytes) })}</span>}
            <span>{t('Dauer: {eta}', { eta: minutes(report.etaSeconds) })}</span>
          </div>
          {report.state === 'space' && (
            <MessageBar intent="error"><MessageBarBody>
              {t('Es fehlen etwa {missing} Platz für die Dateiinhalte. Nichts wird angefasst, bis genug frei ist; alles bleibt wie bisher in der Datenbank und funktioniert weiter.', { missing: fmtSize(report.missingBytes) })}
            </MessageBarBody></MessageBar>
          )}
          {report.state === 'consent' && (
            <MessageBar intent="warning"><MessageBarBody>
              {t('Das Auslagern ist groß und startet nicht von selbst. Es löscht nichts: Die alten Inhalte bleiben in der Datenbank, bis du unter Einstellungen → Statistik verdichtest. Dafür braucht es dann noch einmal so viel freien Platz.')}
            </MessageBarBody></MessageBar>
          )}
          {activity?.state === 'running' && <ProgressBar value={activity.total ? (activity.done ?? 0) / activity.total : undefined} />}
        </div>
      ))}

      {(!nothing || info.paused) && (
        <div className={s.buttons}>
          {(anyConsent || pendingUsers.some(u => u.plan.state === 'ready')) && <Button appearance="primary" onClick={() => start()}>{t('Jetzt starten')}</Button>}
          {anyNoBackup && <Button onClick={() => setConfirmNoBackup(true)}>{t('Ohne Sicherung fortfahren')}</Button>}
          {(anyBlobRunning || info.paused) && (
            <Button onClick={() => pause(!info.paused)}>{info.paused ? t('Fortsetzen') : t('Pausieren')}</Button>
          )}
          <Button appearance="subtle" onClick={recheck}>{t('Erneut prüfen')}</Button>
        </div>
      )}
      {info.paused && <MessageBar intent="info"><MessageBarBody>{t('Pausiert: Die Auslagerung hält nach der laufenden Portion an und macht mit „Fortsetzen“ weiter.')}</MessageBarBody></MessageBar>}

      {!gate && (
        <>
          <Text size={400} weight="semibold">{t('Sicherungen vor Datenänderungen')}</Text>
          <Text size={200} className={s.muted}>
            {t('Vor jedem Umbau legt dmOffice von jeder Benutzerdatenbank eine verschlüsselte Kopie an (samt Benutzerliste, Einstellungen, Freigaben und Schlüsseln) – aber nur, wenn der Platz reicht. Die Dateiinhalte (Blob-Dateien) werden nicht mitgesichert: Das Auslagern löscht nichts, die alten Inhalte bleiben in der Datenbank – und damit in dieser Sicherung –, bis du ausdrücklich verdichtest. Die Sicherungen zählen zum belegten Platz: {size}.', { size: fmtSize(info.backupBytes) })}
          </Text>
          <div className={s.head}>
            <Text>{t('Aufbewahren')}</Text>
            <Select value={String(info.keepDays)} onChange={(_, d) => setKeep(Number(d.value))}>
              {[...new Set([3, 7, 14, 30, 60, 90, info.keepDays])].sort((a, b) => a - b).map(v => <option key={v} value={String(v)}>{t('{n} Tage', { n: v })}</option>)}
              <option value="0">{t('nie löschen')}</option>
            </Select>
            <Text size={200} className={s.muted}>{t('Danach räumt dmOffice sie selbst weg.')}</Text>
          </div>
          {info.backups.length === 0 ? <Text className={s.muted}>{t('Keine Sicherungen vorhanden.')}</Text> : (
            <div className={s.tableWrap}>
              <Table size="small">
                <TableHeader><TableRow>
                  <TableHeaderCell>{t('Sicherung')}</TableHeaderCell>
                  <TableHeaderCell>{t('Angelegt')}</TableHeaderCell>
                  <TableHeaderCell>{t('Benutzer')}</TableHeaderCell>
                  <TableHeaderCell>{t('Größe')}</TableHeaderCell>
                  <TableHeaderCell />
                </TableRow></TableHeader>
                <TableBody>
                  {info.backups.map(b => (
                    <TableRow key={b.name}>
                      <TableCell>{b.name}</TableCell>
                      <TableCell>{new Date(b.created).toLocaleString(locale)}</TableCell>
                      <TableCell>{b.users.map(u => u.login).join(', ')}</TableCell>
                      <TableCell>{fmtSize(b.bytes)}</TableCell>
                      <TableCell>
                        <Button size="small" onClick={() => setViewing(b)}>{t('Sicherung ansehen')}</Button>{' '}
                        <Button size="small" appearance="subtle" onClick={() => removeBackup(b)}>{t('Löschen')}</Button>
                      </TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            </div>
          )}
        </>
      )}

      {viewing && (
        <Dialog open onOpenChange={(_, d) => !d.open && setViewing(null)}>
          <DialogSurface style={{ maxWidth: 640 }}>
            <DialogBody>
              <DialogTitle>{t('Sicherung {name}', { name: viewing.name })}</DialogTitle>
              <DialogContent>
                <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                  <Text>{t('Ordner: {path}', { path: viewing.path })}</Text>
                  <Text>{t('Angelegt mit Version {v} am {date}, {size}.', { v: viewing.appVersion || '?', date: new Date(viewing.created).toLocaleString(locale), size: fmtSize(viewing.bytes) })}</Text>
                  <ul className={s.steps}>
                    {viewing.users.map(u => <li key={u.id}>{t('{login}: Datenbank im Stand {schema} ({size})', { login: u.login, schema: u.schema, size: fmtSize(u.bytes) })}</li>)}
                    <li>{t('users.json, server.json, shares.json, Hauptschlüssel und keys/ (soweit vorhanden)')}</li>
                  </ul>
                  <Text weight="semibold">{t('Zurückgehen')}</Text>
                  <ol className={s.steps}>
                    <li>{t('dmOffice (den Container) stoppen und die Image-Version festnageln, die zu dieser Sicherung passt, z. B. ghcr.io/taloan/dmoffice:{v}.', { v: viewing.appVersion || '1.1.0' })}</li>
                    <li>{t('Aus dem Sicherungsordner users/*.db nach {dir}/users/ kopieren; die gleichnamigen Dateien mit -wal und -shm dort vorher löschen.', { dir: info.dataDir })}</li>
                    <li>{t('users.json, server.json, shares.json (und master.key, keys/) aus dem Sicherungsordner in {dir} zurückkopieren.', { dir: info.dataDir })}</li>
                    <li>{t('Container starten. Änderungen seit der Sicherung gehen dabei verloren; Blob-Dateien müssen nicht zurück (überzählige räumt dmOffice selbst weg).')}</li>
                  </ol>
                </div>
              </DialogContent>
              <DialogActions><Button appearance="primary" onClick={() => setViewing(null)}>{t('Schließen')}</Button></DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}

      {confirmNoBackup && (
        <Dialog open onOpenChange={(_, d) => !d.open && setConfirmNoBackup(false)}>
          <DialogSurface>
            <DialogBody>
              <DialogTitle>{t('Ohne Sicherung fortfahren?')}</DialogTitle>
              <DialogContent>
                {t('Für die Sicherung reicht der Platz nicht. dmOffice baut die Datenbank trotzdem um – jeder Schritt ist für sich sicher (ganz oder gar nicht), aber wenn etwas Unerwartetes schiefgeht, gibt es keine Kopie von vorher. Hast du den Datenordner selbst gesichert?')}
              </DialogContent>
              <DialogActions>
                <Button onClick={() => setConfirmNoBackup(false)}>{t('Abbrechen')}</Button>
                <Button appearance="primary" onClick={() => { setConfirmNoBackup(false); start(true) }}>{t('Ohne Sicherung fortfahren')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
    </div>
  )
}

/** Wartet die Datenbank des angemeldeten Benutzers? Dann zeigt die Oberflaeche statt der Programme den Assistenten. */
export interface UpdateState {
  isAdmin: boolean
  mine: { pending: boolean; state: string; from: number; to: number; missingBytes: number; backupMissingBytes: number; needsConsent: boolean }
  attention: boolean
}

export function UpdateGate({ state }: { state: UpdateState }) {
  const { me } = useApp()
  const s = useStyles()
  return (
    <div style={{ padding: '24px 32px', overflowY: 'auto', height: '100%', boxSizing: 'border-box' }}>
      <div className={s.root}>
        <Text size={700} weight="semibold">{t('Die Daten werden auf den neuen Stand gebracht')}</Text>
        {!me.isAdmin ? (
          <MessageBar intent="info"><MessageBarBody>
            {t('dmOffice wurde aktualisiert und bringt deine Daten gerade auf den neuen Stand. Das dauert oder wartet auf die Verwaltung (Platz, Freigabe). Nichts geht verloren – bitte später wieder öffnen.')}
          </MessageBarBody></MessageBar>
        ) : <UpdatePanel gate />}
      </div>
    </div>
  )
}
