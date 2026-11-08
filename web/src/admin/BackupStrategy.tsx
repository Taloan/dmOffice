import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, InfoLabel, Input, Link, MessageBar, MessageBarBody, ProgressBar, Radio, RadioGroup, Select, Switch, Text, tokens } from '@fluentui/react-components'
import { useEffect, useMemo, useState } from 'react'
import { ChevronDown16Regular, ChevronRight16Regular, Key20Regular } from '@fluentui/react-icons'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { locale, t, tError } from '../i18n'
import { copyText, saveText } from '../shell/browser'
import { AdminCol, AdminTable } from './AdminTable'
import { AdminHeading } from './AdminHeading'
import { Target, TargetFields, TargetValues, homeOnly, valuesOf } from './TargetFields'

const DOC_URL = 'https://github.com/Taloan/dmOffice/blob/master/docs/wiederherstellen.de.md'

interface CoreStatus { running: boolean; lastRun: string | null; lastError: string | null; lastName: string | null; lastBytes: number; otherServer?: string | null }
interface FilesStatus { running: boolean; lastRun: string | null; lastError: string | null; uploaded: number; count: number; bytes: number; pending: number; completeSince: string | null; done: number; total: number }
interface TargetState { target: Target; endpoint: string | null; region: string | null; bucket: string | null; prefix: string | null; accessKey: string | null; hasSecret: boolean; webDavUrl: string | null; webDavUser: string | null; hasWebDavPassword: boolean; folder: string | null; own: boolean; configured: boolean }
interface CoreState extends TargetState { enabled: boolean; at: string; keepCount: number; everyHours: number; blocked: string | null; status: CoreStatus; overdue: boolean; holdDeletes: boolean; nextRun: string | null }
interface FilesState extends TargetState { enabled: boolean; wanted: boolean; minDays: number; days: number; effectiveDays: number | null; forever: boolean; status: FilesStatus; restore: { running: boolean; missing: number; restored: number; notThere: number; failed: number; error: string | null } | null }
interface StorageState {
  target: Target; endpoint: string | null; bucket: string | null; prefix: string | null; webDavUrl: string | null; folder: string | null; mirror: boolean; configured: boolean
  backup: CoreState; files: FilesState; profile?: { at: string | null; current: boolean }
}
interface Stand { name: string; created: string; bytes: number; files: boolean | null }

const when = (v: string) => new Date(v).toLocaleString(locale)

/** Wo ein Ziel liegt, ohne Zugangsdaten. */
function placeOf(x: { target: Target; endpoint: string | null; bucket: string | null; prefix: string | null; webDavUrl: string | null; folder: string | null }) {
  if (x.target === 'folder') return t('Ordner') + ': ' + (x.folder ?? '')
  if (x.target === 'webdav') return 'WebDAV: ' + (x.webDavUrl ?? '')
  return 'S3: ' + (x.endpoint ?? '').replace(/^https?:\/\//, '') + (x.bucket ? ' / ' + x.bucket : '') + (x.prefix ? '/' + x.prefix : '')
}

/**
 * Verwaltung → Backup-Strategie: Übersicht, Core-Backup und Files-Backup. Das Storage Extender bleibt davon getrennt.
 * Beide Backups gehen wahlweise an das Ziel aus dem Storage Extender oder an ein anderes. Erklärungen im InfoLabel,
 * die Schritte zum Wiederherstellen in docs/wiederherstellen.de.md.
 */
export function BackupStrategy() {
  const { fail, notify } = useApp()
  const [st, setSt] = useState<StorageState | null>(null)
  const [stands, setStands] = useState<Stand[] | null>(null)
  const [at, setAt] = useState('03:00')
  const [count, setCount] = useState('10')
  const [days, setDays] = useState('1')
  const [coreOwn, setCoreOwn] = useState(false)
  const [coreTarget, setCoreTarget] = useState<Target>('s3')
  const [coreValues, setCoreValues] = useState<TargetValues>(valuesOf({}))
  const [coreTested, setCoreTested] = useState(false)
  const [filesOwn, setFilesOwn] = useState(false)
  const [filesTarget, setFilesTarget] = useState<Target>('s3')
  const [filesValues, setFilesValues] = useState<TargetValues>(valuesOf({}))
  const [filesTested, setFilesTested] = useState(false)
  const [busy, setBusy] = useState(false)
  const [restoreOpen, setRestoreOpen] = useState(false)
  const [profile, setProfile] = useState<string | null>(null)

  const take = (s: StorageState) => {
    setSt(s)
    setAt(s.backup.at); setCount(String(s.backup.keepCount)); setDays(String(s.files.effectiveDays ?? s.files.minDays))
    setCoreOwn(s.backup.own); setCoreTarget(s.backup.target ?? 's3'); setCoreValues(valuesOf(s.backup))
    setFilesOwn(s.files.own); setFilesTarget(s.files.target ?? 's3'); setFilesValues(valuesOf(s.files))
  }
  const loadStands = () => api.get<Stand[]>('/api/admin/storage/backups').then(setStands).catch(() => setStands(null))
  useEffect(() => { api.get<StorageState>('/api/admin/storage').then(take).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { if (st?.backup.configured) loadStands() }, [st?.backup.configured, st?.backup.own, st?.backup.status.lastRun, st?.files.status.completeSince, st?.files.effectiveDays]) // eslint-disable-line react-hooks/exhaustive-deps
  // Stand nachladen, solange etwas läuft - Eingaben dabei nicht überschreiben
  const running = !!st && (st.backup.status.running || st.files.status.running || !!st.files.restore?.running)
  useEffect(() => {
    if (!running) return
    const h = setInterval(() => api.get<StorageState>('/api/admin/storage').then(s => setSt(o => (o ? { ...o, backup: { ...o.backup, status: s.backup.status, overdue: s.backup.overdue, holdDeletes: s.backup.holdDeletes, nextRun: s.backup.nextRun }, files: { ...o.files, status: s.files.status, restore: s.files.restore } } : s))).catch(() => {}), 5000)
    return () => clearInterval(h)
  }, [running])

  const cols = useMemo<AdminCol<Stand>[]>(() => [
    { key: 'created', label: () => t('Zeitpunkt'), width: 200, sort: r => r.created, render: r => when(r.created) },
    { key: 'bytes', label: () => t('Größe'), width: 100, sort: r => r.bytes, render: r => fmtSize(r.bytes) },
    { key: 'files', label: () => t('Dateien'), width: 0, sort: r => (r.files == null ? 2 : r.files ? 0 : 1), render: r => (r.files == null ? '–' : r.files ? t('Vollständig') : t('Unvollständig')) },
  ], [])

  if (!st) return null
  const core = st.backup
  const files = st.files
  const muted = { color: tokens.colorNeutralForeground3 }
  const storagePlace = st.configured ? placeOf(st) : null

  const saveCore = async (patch: Partial<{ enabled: boolean; at: string; keepCount: number; everyHours: number }>) => {
    try { take(await api.put<StorageState>('/api/admin/storage/backup', { enabled: core.enabled, at, keepCount: Number(count) || core.keepCount, everyHours: core.everyHours, ...patch })); notify(t('Gespeichert')) } catch (e) { fail(e) }
  }
  const saveCoreTarget = async (own: boolean, check: boolean) => {
    setBusy(true)
    try {
      take(await api.put<StorageState>('/api/admin/storage/backup/target', { own, target: coreTarget, ...coreValues, secretKey: coreValues.secretKey || null, webDavPassword: coreValues.webDavPassword || null }))
      if (check) { await api.post('/api/admin/storage/backup/test'); setCoreTested(true) } else notify(t('Gespeichert'))
    } catch (e) { setCoreTested(false); fail(e) } finally { setBusy(false) }
  }
  const saveFiles = async (patch: Partial<{ enabled: boolean; own: boolean; days: number; forever: boolean }>, check = false) => {
    setBusy(true)
    try {
      take(await api.put<StorageState>('/api/admin/storage/files-backup', { enabled: files.wanted, own: filesOwn, days: Number(days) || 0, forever: files.forever, target: filesTarget, ...filesValues, secretKey: filesValues.secretKey || null, webDavPassword: filesValues.webDavPassword || null, ...patch }))
      if (check) { await api.post('/api/admin/storage/files-backup/test'); setFilesTested(true) } else notify(t('Gespeichert'))
    } catch (e) { setFilesTested(false); fail(e); api.get<StorageState>('/api/admin/storage').then(take).catch(() => {}) } finally { setBusy(false) }
  }
  const forgetOther = async () => {
    try { await api.post('/api/admin/storage/backup/forget-other', {}); notify(t('Vermerk entfernt')); take(await api.get<StorageState>('/api/admin/storage')) } catch (e) { fail(e) }
  }
  const makeProfile = async () => {
    try { setProfile((await api.post<{ text: string }>('/api/admin/storage/profile')).text); take(await api.get<StorageState>('/api/admin/storage')) } catch (e) { fail(e) }
  }
  const profileFile = () => [
    t('dmOffice – Wiederherstellungsschlüssel ({host})', { host: location.host }),
    t('Erstellt {time}', { time: new Date().toLocaleString(locale) }),
    t('Nur zusammen mit dem Hauptschlüssel (DMOFFICE_MASTER_KEY) nutzbar. Beim Wiederherstellen auf einem neuen Server unter „Aus dem Core-Backup wiederherstellen“ einlesen.'),
    '', profile ?? '', ''].join('\n')

  // Übersicht
  const coreState = !core.enabled ? t('Aus') : core.status.running ? t('Läuft') : core.status.lastError ? t('Gescheitert') : !core.status.lastRun ? t('Noch kein Lauf') : core.overdue ? t('Überfällig') : t('Aktuell')
  const coreBad = core.enabled && !core.status.running && (!!core.status.lastError || core.overdue)
  const filesState = !files.enabled ? t('Aus') : files.status.running ? t('Läuft') : files.status.lastError ? t('Gescheitert') : !files.status.completeSince ? (files.status.lastRun ? t('Unvollständig') : t('Noch kein Lauf')) : t('Aktuell')
  const filesBad = files.enabled && !files.status.running && !!files.status.lastError
  const oldest = stands?.filter(x => x.files).map(x => x.created).sort()[0]
  const tile = (label: string, value: string, bad: boolean, lines: (string | null)[]) => (
    <div style={{ background: tokens.colorNeutralBackground3, borderRadius: 6, padding: '10px 12px', display: 'flex', flexDirection: 'column', gap: 2 }}>
      <Text size={200} style={muted}>{label}</Text>
      <Text weight="semibold" style={{ color: bad ? tokens.colorPaletteRedForeground1 : undefined }}>{value}</Text>
      {lines.filter(Boolean).map(l => <Text key={l} size={200} style={muted}>{l}</Text>)}
    </div>
  )
  const row = (from: React.ReactNode, to: string, off?: boolean) => (
    <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0,1fr) 20px minmax(0,1fr)', gap: 8, alignItems: 'center' }}>
      <Text size={200} style={off ? muted : undefined}>{from}</Text><Text size={200} style={muted}>{off ? '–' : '→'}</Text><Text size={200} style={off ? muted : undefined}>{to}</Text>
    </div>
  )
  const coreWhere = core.own ? placeOf(core) : storagePlace
  const filesWhere = files.own ? placeOf(files) : storagePlace && storagePlace + ' · files-backup'
  const target = (own: boolean, setOwn: (v: boolean) => void, onSame: () => void, label: string) => (
    <Field label={label}>
      <RadioGroup value={own || !st.configured ? 'own' : 'same'} onChange={(_, d) => { if (d.value === 'same') { setOwn(false); onSame() } else setOwn(true) }}>
        <Radio value="same" disabled={!st.configured} label={<span>{t('Ziel aus Storage Extender übernehmen')}<br /><Text size={200} style={muted}>{storagePlace ?? t('Im Storage Extender ist kein Ziel eingerichtet.')}</Text></span>} />
        <Radio value="own" label={t('Anderes Ziel')} />
      </RadioGroup>
    </Field>
  )

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 720 }}>
      <AdminHeading first>{t('Übersicht')}</AdminHeading>
      {core.enabled && core.status.lastError && <MessageBar intent="warning" layout="multiline"><MessageBarBody>{t('Core-Backup gescheitert: {error}', { error: core.status.lastError })}</MessageBarBody></MessageBar>}
      {files.enabled && files.status.lastError && <MessageBar intent="warning" layout="multiline"><MessageBarBody>{t('Files-Backup gescheitert: {error}', { error: tError(files.status.lastError) })}</MessageBarBody></MessageBar>}
      {core.status.otherServer && <MessageBar intent="warning" layout="multiline"><MessageBarBody>{t('Am Ziel des Core-Backups speichert noch ein anderer dmOffice-Server (zuletzt {when}). Solange löscht dieser Server dort keine alten Stände, die eingestellte Zahl gilt also nicht.', { when: new Date(core.status.otherServer).toLocaleString(locale) })}<div style={{ marginTop: 6 }}><Button size="small" onClick={forgetOther}>{t('Das war dieser Server vor einem Umzug')}</Button></div></MessageBarBody></MessageBar>}
      {core.holdDeletes && core.status.lastRun && <Text size={200}>{t('Das Löschen am Ziel ist pausiert, bis das Core-Backup wieder durchläuft.')}</Text>}
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(190px, 1fr))', gap: 10 }}>
        {tile(t('Core-Backup'), coreState, coreBad, [
          core.status.lastRun ? t('Zuletzt: {when}', { when: when(core.status.lastRun) }) : null,
          core.enabled && core.nextRun ? t('Nächster Lauf: {when}', { when: when(core.nextRun) }) : null])}
        {tile(t('Files-Backup'), filesState, filesBad, [
          files.enabled && files.status.lastRun ? t('Zuletzt: {when}', { when: when(files.status.lastRun) }) : null,
          files.enabled && files.status.count > 0 ? t('{size} am Ziel', { size: fmtSize(files.status.bytes) }) : null])}
        {tile(t('Vollständig zurück bis'), oldest ? new Date(oldest).toLocaleDateString(locale) : '–', false, [oldest ? t('Älteste Version mit allen Dateien') : null])}
      </div>
      {files.status.running && files.status.total > 0 && <>
        <Text size={200}>{t('Files-Backup: {done} von {total} geprüft · {n} übertragen', { done: files.status.done.toLocaleString(locale), total: files.status.total.toLocaleString(locale), n: files.status.uploaded.toLocaleString(locale) })}</Text>
        <ProgressBar value={Math.min(1, files.status.done / files.status.total)} thickness="medium" />
      </>}
      {files.restore?.running && <Text size={200}>{t('Holt Dateien aus dem Files-Backup: {restored} von {missing}', { restored: files.restore.restored, missing: files.restore.missing })}</Text>}
      {files.restore && !files.restore.running && files.restore.notThere + files.restore.failed > 0 && <MessageBar intent="warning"><MessageBarBody>{t('Nach der Wiederherstellung fehlen {n} Dateien auch im Files-Backup.', { n: files.restore.notThere + files.restore.failed })}</MessageBarBody></MessageBar>}
      {files.restore?.error && <MessageBar intent="warning"><MessageBarBody>{files.restore.error}</MessageBarBody></MessageBar>}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
        {row(t('Datenbanken, Benutzer, Einstellungen'), core.enabled && coreWhere ? t('Core-Backup') + ' · ' + coreWhere : stands?.length ? t('Nur von Hand gesichert: {n} Versionen', { n: stands.length }) : t('Kein Backup'), !core.enabled)}
        {row(t('Dateien'), files.enabled && filesWhere ? t('Files-Backup') + ' · ' + filesWhere : t('Kein Backup'), !files.enabled)}
        {row(t('Hauptschlüssel'), t('Beim Administrator'), true)}
        {row(t('Externe Ordner'), t('Nicht enthalten'), true)}
      </div>
      <Text size={200}>{t('Schreib dir den DMOFFICE_MASTER_KEY aus der Container-Vorlage auf, am besten in den Passwortmanager. Ohne ihn lässt sich kein Backup öffnen.')}</Text>

      <AdminHeading><InfoLabel size="large" info={t('Sichert alles außer den Dateien selbst: Mails, Kalender, Kontakte, Aufgaben, Notizen, Tagebuch, Chat, die Namen und Ordner der Dateien, Benutzer und Einstellungen. Jede Version ist vollständig.')}>{t('Core-Backup')}</InfoLabel></AdminHeading>
      {core.blocked && <Text size={200} style={muted}>{tError(core.blocked)}</Text>}
      <Switch checked={core.enabled} disabled={!!core.blocked} onChange={(_, d) => saveCore({ enabled: d.checked })} label={core.enabled ? t('Core-Backup ist eingeschaltet') : t('Core-Backup einschalten')} />
      {target(coreOwn, setCoreOwn, () => { setCoreTested(false); if (core.own) saveCoreTarget(false, false) }, t('Ziel'))}
      {(coreOwn || !st.configured) && <>
        <TargetFields target={coreTarget} onTarget={x => { setCoreTested(false); setCoreTarget(x) }} v={coreValues} onChange={(k, v) => { setCoreTested(false); setCoreValues(x => ({ ...x, [k]: v })) }}
          hasSecret={core.own && core.hasSecret} hasWebDavPassword={core.own && core.hasWebDavPassword} />
        <div><Button appearance="primary" disabled={busy} onClick={() => saveCoreTarget(true, true)}>{busy ? t('Prüfe …') : t('Speichern und testen')}</Button></div>
        {coreTested && <MessageBar intent="success"><MessageBarBody>{t('Klappt – dmOffice kann dort schreiben, lesen und löschen.')}</MessageBarBody></MessageBar>}
      </>}
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'flex-end' }}>
        <Field label={t('Wie oft')}>
          <Select value={String(core.everyHours)} onChange={(_, d) => saveCore({ everyHours: Number(d.value) })}>
            {[1, 3, 6, 12, 24, 168, 720].map(h => <option key={h} value={h}>{h === 1 ? t('stündlich') : h === 24 ? t('täglich') : h === 168 ? t('wöchentlich') : h === 720 ? t('monatlich') : t('alle {n} Stunden', { n: h })}</option>)}
          </Select>
        </Field>
        <Field label={core.everyHours < 24 ? t('ab Uhrzeit') : t('Uhrzeit')}><Input type="time" value={at} onChange={(_, d) => setAt(d.value)} onBlur={() => at !== core.at && saveCore({ at })} style={{ width: 120 }} /></Field>
        <Field label={t('Versionen aufbewahren')}><Input type="number" min={1} value={count} onChange={(_, d) => setCount(d.value)} onBlur={() => { const n = Math.max(1, Math.round(Number(count)) || core.keepCount); setCount(String(n)); if (n !== core.keepCount) saveCore({ keepCount: n }) }} style={{ width: 100 }} /></Field>
        <Button disabled={!!core.blocked || core.status.running}
          onClick={() => api.post('/api/admin/storage/backup/run').then(() => { notify(t('Sicherung gestartet')); setSt(o => (o ? { ...o, backup: { ...o.backup, status: { ...o.backup.status, running: true } } } : o)) }).catch(fail)}>
          {core.status.running ? t('Sichert …') : t('Jetzt sichern')}</Button>
      </div>
      {stands && stands.length > 0 && <>
        <Text weight="semibold" size={300} style={{ marginTop: 6 }}>{t('Versionen')}</Text>
        <AdminTable id="backup-stands" cols={cols} rows={stands} rowKey={r => r.name} initialSort={{ col: 'created', desc: true }} />
      </>}
      {core.configured && (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 6, marginTop: 4 }}>
          <Button appearance="transparent" size="small" icon={restoreOpen ? <ChevronDown16Regular /> : <ChevronRight16Regular />} style={{ alignSelf: 'flex-start', padding: 0, fontWeight: 600 }} onClick={() => setRestoreOpen(x => !x)}>{t('Wiederherstellung')}</Button>
          {restoreOpen && <>
            <Text size={200}>{t('Nur auf ein leeres dmOffice, mit dem Hauptschlüssel. Einzelne Dateien holt das Werkzeug restore-tool.')} <Link href={DOC_URL} target="_blank" rel="noreferrer">{t('Anleitung')}</Link></Text>
            {!st.profile?.at ? <Text size={200} style={{ color: tokens.colorPaletteMarigoldForeground1 }}>{t('Noch kein Wiederherstellungsschlüssel erstellt.')}</Text>
              : st.profile.current ? <Text size={200} style={muted}>{t('Wiederherstellungsschlüssel erstellt am {when}.', { when: when(st.profile.at) })}</Text>
              : <Text size={200} style={{ color: tokens.colorPaletteMarigoldForeground1 }}>{t('Die Zugangsdaten zum Sicherungsort haben sich seit dem {when} geändert – Wiederherstellungsschlüssel neu erstellen.', { when: when(st.profile.at) })}</Text>}
            {homeOnly(core.own ? core.target : st.target, (core.own ? core.target : st.target) === 'webdav' ? (core.own ? core.webDavUrl : st.webDavUrl) : (core.own ? core.endpoint : st.endpoint)) && <Text size={200} style={{ color: tokens.colorPaletteMarigoldForeground1 }}>{t('Der Sicherungsort liegt im Heimnetz – ein Server außerhalb erreicht ihn nicht.')}</Text>}
            <div><Button icon={<Key20Regular />} onClick={makeProfile}>{t('Wiederherstellungsschlüssel erstellen')}</Button></div>
          </>}
        </div>
      )}

      <AdminHeading><InfoLabel size="large" info={t('Legt eine eigene Kopie der Dateien an und überträgt vor jedem Core-Backup, was darin fehlt. Gelöschte Dateien bleiben für die Haltedauer in der Kopie. Jede Version des Core-Backups, die jünger ist als die Haltedauer, lässt sich vollständig wiederherstellen, auch wenn sie weggesichert wurde. Mit dem Ziel aus dem Storage Extender liegt die Kopie dort im eigenen Ordner files-backup.')}>{t('Files-Backup')}</InfoLabel></AdminHeading>
      {!core.enabled && <Text size={200} style={muted}>{t('Setzt das Core-Backup voraus.')}</Text>}
      <Switch checked={files.wanted && core.enabled} disabled={!core.enabled || busy} onChange={(_, d) => saveFiles({ enabled: d.checked })} label={files.enabled ? t('Files-Backup ist eingeschaltet') : t('Files-Backup einschalten')} />
      {target(filesOwn, setFilesOwn, () => { setFilesTested(false); if (files.own) saveFiles({ own: false }) }, t('Ziel'))}
      {!filesOwn && st.configured && !st.mirror && <Text size={200} style={muted}>{t('Dafür muss das Ziel im Storage Extender eingeschaltet sein.')}</Text>}
      {(filesOwn || !st.configured) && <>
        <TargetFields target={filesTarget} onTarget={x => { setFilesTested(false); setFilesTarget(x) }} v={filesValues} onChange={(k, v) => { setFilesTested(false); setFilesValues(x => ({ ...x, [k]: v })) }}
          hasSecret={files.own && files.hasSecret} hasWebDavPassword={files.own && files.hasWebDavPassword} />
        <div><Button appearance="primary" disabled={busy} onClick={() => saveFiles({ own: true }, true)}>{busy ? t('Prüfe …') : t('Speichern und testen')}</Button></div>
        {filesTested && <MessageBar intent="success"><MessageBarBody>{t('Klappt – dmOffice kann dort schreiben, lesen und löschen.')}</MessageBarBody></MessageBar>}
      </>}
      <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap', alignItems: 'flex-end' }}>
        <Field label={<InfoLabel info={t('Mindestens (Anzahl Versionen + 1) × Abstand. Für weggesicherte Versionen lässt sich mehr einstellen.')}>{t('Haltedauer in Tagen')}</InfoLabel>} hint={t('Mindestens {n} Tage', { n: files.minDays })}>
          <Input type="number" min={files.minDays} disabled={files.forever} value={files.forever ? '' : days} onChange={(_, d) => setDays(d.value)}
            onBlur={() => { const n = Math.max(files.minDays, Math.round(Number(days)) || 0); setDays(String(n)); if (n !== files.effectiveDays) saveFiles({ days: n }) }} style={{ width: 120 }} />
        </Field>
        <Switch checked={files.forever} onChange={(_, d) => saveFiles({ forever: d.checked })} label={t('Unbegrenzt')} />
      </div>
      {files.enabled && files.status.lastRun && <Text size={200} style={muted}>{t('Zuletzt {n} Dateien neu übertragen', { n: files.status.uploaded.toLocaleString(locale) })}</Text>}

      {profile && (
        <Dialog open onOpenChange={(_, d) => !d.open && setProfile(null)}>
          <DialogSurface style={{ maxWidth: 480 }}>
            <DialogBody>
              <DialogTitle>{t('Wiederherstellungsschlüssel')}</DialogTitle>
              <DialogContent>
                <Text block style={{ marginBottom: 12 }}>{t('Im Passwortmanager neben dem DMOFFICE_MASTER_KEY ablegen oder als Datei speichern. Nur zusammen mit dem DMOFFICE_MASTER_KEY verwendbar.')}</Text>
                <div style={{ fontFamily: 'Consolas, monospace', fontSize: 12, wordBreak: 'break-all', padding: 12, background: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusMedium, userSelect: 'all' }}>{profile}</div>
              </DialogContent>
              <DialogActions>
                <Button onClick={() => saveText(t('dmOffice-Wiederherstellungsschluessel-{host}.txt', { host: location.hostname }), profileFile())}>{t('Als Datei speichern')}</Button>
                <Button onClick={async () => (await copyText(profile))
                  ? notify(t('Kopiert'))
                  : notify(t('Kopieren ging nicht'), t('Bitte markieren und mit Strg+C kopieren.'), 'warning')}>{t('Kopieren')}</Button>
                <Button appearance="primary" onClick={() => setProfile(null)}>{t('Erledigt')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
    </div>
  )
}
