import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarActions, MessageBarBody,
  Switch, Text, makeStyles, mergeClasses, tokens, SpinButton, Select, Radio, RadioGroup, ProgressBar,
} from '@fluentui/react-components'
import { ChevronDown16Regular, ChevronRight16Regular, PersonAdd20Regular, Delete20Regular, Key20Regular, ShieldDismiss20Regular, Edit16Regular, Eye20Regular, EyeOff20Regular } from '@fluentui/react-icons'
import { useEffect, useState, useRef } from 'react'
import { api, featureOn, fmtSize } from '../api'
import { useApp } from '../App'
import { SetupHints } from './SetupHints'
import { t, lang, locale, tError } from '../i18n'
import { AdminHeading } from './AdminHeading'
import { AdminCol, AdminTable } from './AdminTable'
import { forgetOffice } from '../files/Office'
import { daysText, mbText } from '../settings/FilesSettings'
import { UpdatePanel } from './UpdatePanel'
import { ChatRightsAdmin } from '../chat/ChatSettings'
import { AiAdmin } from './AiAdmin'
import { SpamListsAdmin } from './SpamListsAdmin'
import { SpamRulesAdmin } from './SpamRulesAdmin'
import { SecurityAdmin } from './SecurityAdmin'
import { StorageAdmin } from './StorageAdmin'
import { BackupStrategy } from './BackupStrategy'
import { ExternalAdmin } from './ExternalAdmin'
import { LogAdmin } from './LogAdmin'
import { PlainBackupAdmin } from './PlainBackupAdmin'
import { DbHealthAdmin } from './DbHealthAdmin'
import { ModulesAdmin } from '../settings/ModuleSettings'
import { askConfirm } from '../shell/context'

const useStyles = makeStyles({
  // Links die Seiten der Verwaltung (wie die Einstellungen), rechts die gewählte Seite
  root: { flex: 1, display: 'flex', minWidth: 0, minHeight: 0, '@media (max-width: 767px)': { flexDirection: 'column' } },
  nav: {
    width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '20px 8px', display: 'flex', flexDirection: 'column', gap: '2px', overflowY: 'auto',
    '@media (max-width: 767px)': { display: 'none' },
  },
  navGroup: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, padding: '12px 10px 4px', fontWeight: tokens.fontWeightSemibold },
  navItem: {
    textAlign: 'left', border: 'none', background: 'none', color: 'inherit', font: 'inherit', padding: '7px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  navOn: { backgroundColor: tokens.colorNeutralBackground1Selected, fontWeight: tokens.fontWeightSemibold },
  mobileNav: { display: 'none', padding: '12px 12px 0', '@media (max-width: 767px)': { display: 'block' } },
  page: { flex: 1, overflowY: 'auto', padding: '24px 32px', display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0, '@media (max-width: 767px)': { padding: '12px' } },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
})

interface User {
  id: string; login: string; displayName: string; isAdmin: boolean; created: string; size: number; twoFactor: boolean
  /** null: Vorgabe; 0: unbegrenzt */
  fileQuotaMb: number | null; fileQuota: number; files: number | null
  /** Dateiablage abgeschaltet - notizi-Abgleich geht weiter */
  filesOff: boolean
  /** Pukki darf fuer diesen Benutzer die KI fragen */
  aiAllowed?: boolean
}

type SizeUnit = 'MB' | 'GB' | 'TB'
const UNIT_MB: Record<SizeUnit, number> = { MB: 1, GB: 1024, TB: 1024 * 1024 }
/** MB als Zahl mit der groessten Einheit, in der sie glatt aufgeht (1536 MB bleiben 1536 MB). */
const splitMb = (mb: number): { value: number; unit: SizeUnit } =>
  mb > 0 && mb % UNIT_MB.TB === 0 ? { value: mb / UNIT_MB.TB, unit: 'TB' } : mb > 0 && mb % UNIT_MB.GB === 0 ? { value: mb / UNIT_MB.GB, unit: 'GB' } : { value: mb, unit: 'MB' }

/**
 * Zahl mit Einheit (MB, GB, TB), gespeichert wird in MB. Wechselt man die Einheit, bleibt die Zahl stehen und gilt in der
 * neuen Einheit („1“ und dann „TB“ = 1 TB) - früher rechnete nur die Anzeige um und rundete 1023 GB zu „1 TB“.
 */
function SizeInput({ mb, onChange }: { mb: number; onChange: (mb: number) => void }) {
  const [unit, setUnit] = useState<SizeUnit>(splitMb(mb).unit)
  // Selbst gemeldete Werte: die Einheit nur anpassen, wenn der Wert von außen kommt (nach dem Laden)
  const sent = useRef<number | null>(null)
  useEffect(() => { if (mb !== sent.current) setUnit(splitMb(mb).unit) }, [mb])
  const value = Math.round((mb / UNIT_MB[unit]) * 1000) / 1000
  const send = (v: number, u: SizeUnit) => { const next = Math.max(0, Math.round(v * UNIT_MB[u])); sent.current = next; onChange(next) }
  return (
    <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
      <SpinButton value={value} min={0} step={unit === 'MB' ? 512 : 1} style={{ width: 120 }}
        onChange={(_, d) => send(d.value ?? (Number(d.displayValue) || 0), unit)} />
      <Select value={unit} style={{ minWidth: 72 }} onChange={(_, d) => { const u = d.value as SizeUnit; setUnit(u); send(value, u) }}>
        <option value="MB">MB</option><option value="GB">GB</option><option value="TB">TB</option>
      </Select>
    </div>
  )
}

const quotaText = (mb: number) => (mb > 0 ? (({ value, unit }) => `${value} ${unit}`)(splitMb(mb)) : t('unbegrenzt'))

export default function AdminView() {
  const s = useStyles()
  const { me, fail, notify } = useApp()
  const [users, setUsers] = useState<User[]>([])
  const [status, setStatus] = useState<any>(null)
  const [adding, setAdding] = useState(false)
  // Seite gemerkt fuer den naechsten Besuch; fruehere Reiter-Namen gelten weiter
  const [tab, setTab] = useState<string>(() => { try { const v = localStorage.getItem('dmoffice.admin.tab') ?? 'users'; return v === 'files' ? 'quota' : v } catch { return 'users' } })
  const groups: { label: string; pages: [string, string][] }[] = [
    { label: t('Benutzer'), pages: [['users', t('Benutzer')]] },
    { label: t('Server'), pages: [['quota', t('Kontingente und Grenzen')], ['storage', t('Storage Extender')], ['backup', t('Backup-Strategie')]] },
    { label: t('Server-Mounts'), pages: [['serverfolders', t('Lokale Server-Mounts')]] },
    { label: t('Für Benutzer freigeben'), pages: [['external', t('Benutzer-Mounts')], ['plainbackup', t('Benutzer-Datenexport')]] },
    // Office nur mit eingeschaltetem Modul (Hauptschalter: System → Module)
    { label: t('Dienste'), pages: [...(featureOn('office') ? [['office', t('Office im Browser')] as [string, string]] : []), ['chat', t('Chat')], ['mail', t('E-Mail')], ['ai', t('KI')]] },
    { label: t('System'), pages: [['modules', t('Module')], ['security', t('Sicherheit')], ['log', t('Protokoll')], ['updates', t('Updates')], ['db', t('Datenbanken')]] },
  ]
  const allPages = groups.flatMap(g => g.pages)
  const pageLabel = allPages.find(p => p[0] === tab)?.[1] ?? allPages[0][1]
  // Gemerkte Seite gibt es nicht mehr (frühere Seite „Audiothek“, Modul ausgeschaltet): Benutzer
  useEffect(() => { if (!allPages.some(p => p[0] === tab)) setTab('users') }, [tab, allPages.length]) // eslint-disable-line react-hooks/exhaustive-deps
  const pick = (v: string) => { setTab(v); try { localStorage.setItem('dmoffice.admin.tab', v) } catch { /* egal */ } }
  // Seiten mit Formularen zum Speichern bleiben beim Wechsel erhalten (nur ausgeblendet), halb Ausgefülltes geht nicht verloren
  const [kept, setKept] = useState<string[]>([])
  useEffect(() => { if (['backup', 'office', 'ai'].includes(tab)) setKept(k => k.includes(tab) ? k : [...k, tab]) }, [tab])
  const keptPage = (key: string, page: React.ReactNode) => (tab === key || kept.includes(key)) && <div style={{ display: tab === key ? 'contents' : 'none' }}>{page}</div>

  const load = () => api.get<User[]>('/api/admin/users').then(setUsers).catch(fail)
  const [defaultQuota, setDefaultQuota] = useState(0)
  useEffect(() => { if (status) setDefaultQuota(status.defaultFileQuotaMb ?? 0) }, [status])
  const saveDefaultQuota = async () => {
    try {
      await api.put('/api/admin/settings', { defaultFileQuotaMb: defaultQuota })
      setStatus({ ...status, defaultFileQuotaMb: defaultQuota }); load(); notify(t('Vorgabe gespeichert')); window.dispatchEvent(new Event('dmoffice:storage'))
    } catch (e) { fail(e) }
  }
  const [quotaUser, setQuotaUser] = useState<User | null>(null)
  /** mb: null = Vorgabe, 0 = unbegrenzt, sonst eigene Grenze in MB */
  const saveQuota = async (u: User, mb: number | null) => {
    try {
      await api.put(`/api/admin/users/${u.id}`, mb == null ? { defaultFileQuota: true } : { fileQuotaMb: mb })
      setQuotaUser(null); load(); notify(t('Kontingent gespeichert')); window.dispatchEvent(new Event('dmoffice:storage'))
    } catch (e) { fail(e) }
  }
  useEffect(() => { load(); api.get('/api/admin/status').then(setStatus).catch(() => {}) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  /** Alte master.key nach dem Umstieg auf DMOFFICE_MASTER_KEY - erst nach Rückfrage. */
  async function deleteOldKey() {
    if (!await askConfirm(t('Datei master.key löschen'), t('Hast du die Datei master.key heruntergeladen? Ohne die Datei master.key lassen sich Sicherungen von vor dem Umstieg nicht mehr zurückspielen.'), t('Löschen'))) return
    try { await api.del('/api/admin/old-master-key'); setStatus({ ...status, oldKeyFile: false }); notify(t('Datei gelöscht')) } catch (e) { fail(e) }
  }

  const resetPassword = async (u: User) => {
    const password = prompt(t('Neues Passwort für {login} (mindestens 8 Zeichen):', { login: u.login }))
    if (!password) return
    try { await api.put(`/api/admin/users/${u.id}`, { password }); notify(t('Passwort gesetzt'), t('{login} bekommt eine Nachricht.', { login: u.login })) } catch (e) { fail(e) }
  }
  const reset2fa = async (u: User) => {
    if (!await askConfirm('', t('Zwei-Faktor-Anmeldung für {login} abschalten?', { login: u.login }))) return
    try { await api.post(`/api/admin/users/${u.id}/reset-2fa`); load() } catch (e) { fail(e) }
  }
  const toggleAdmin = async (u: User) => {
    try { await api.put(`/api/admin/users/${u.id}`, { isAdmin: !u.isAdmin }); load() } catch (e) { fail(e) }
  }
  const toggleFiles = async (u: User) => {
    try { await api.put(`/api/admin/users/${u.id}`, { filesOff: !u.filesOff }); load() } catch (e) { fail(e) }
  }
  const toggleAi = async (u: User) => {
    try { await api.put(`/api/admin/users/${u.id}`, { aiAllowed: !u.aiAllowed }); load() } catch (e) { fail(e) }
  }
  const remove = async (u: User) => {
    if (prompt(t('Alles von {login} wird gelöscht. Zum Bestätigen den Anmeldenamen eingeben:', { login: u.login })) !== u.login) return
    try { await api.del(`/api/admin/users/${u.id}`); load() } catch (e) { fail(e) }
  }
  const userCols: AdminCol<User>[] = [
    { key: 'login', label: () => t('Anmeldename'), width: 130, sort: u => u.login, render: u => u.login },
    { key: 'name', label: () => t('Name'), width: 150, sort: u => u.displayName, render: u => u.displayName },
    { key: 'role', label: () => t('Rolle'), width: 130, sort: u => (u.isAdmin ? 0 : 1), render: u => <Switch checked={u.isAdmin} disabled={u.id === me.id} label={u.isAdmin ? t('Admin') : t('Benutzer')} onChange={() => toggleAdmin(u)} /> },
    { key: 'twoFactor', label: () => '2FA', width: 70, sort: u => (u.twoFactor ? 0 : 1), render: u => (u.twoFactor ? <Badge color="success">{t('an')}</Badge> : <Badge color="informative" appearance="outline">{t('aus')}</Badge>) },
    { key: 'db', label: () => t('Datenbank'), width: 100, sort: u => u.size, render: u => fmtSize(u.size) },
    { key: 'files', label: () => t('Dateiablage'), width: 100, sort: u => (u.filesOff ? 1 : 0), render: u => <Switch checked={!u.filesOff} onChange={() => toggleFiles(u)} aria-label={t('Dateiablage')}
      title={u.filesOff ? t('Dateiablage aus – nur der notizi-Abgleich geht') : t('Dateiablage an')} /> },
    // Erst mit eingerichteter KI (Verwaltung → KI); der Grund steht am Umschlag, ein gesperrter Schalter zeigt selbst keinen Tooltip
    { key: 'ai', label: () => t('KI'), width: 70, sort: u => (u.aiAllowed ? 0 : 1), render: u => <span title={!status?.aiConfigured ? t('Erst unter „KI“ eine KI einrichten') : u.aiAllowed ? t('Darf die KI nutzen') : t('Ohne KI')}>
      <Switch checked={!!u.aiAllowed && !!status?.aiConfigured} disabled={!status?.aiConfigured} onChange={() => toggleAi(u)} aria-label={t('KI')} /></span> },
    { key: 'quota', label: () => t('Kontingent'), width: 190, sort: u => u.files ?? 0, render: u => (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 150 }}>
        <Button appearance="subtle" size="small" icon={<Edit16Regular />} onClick={() => setQuotaUser(u)} title={t('Kontingent festlegen')} style={{ justifyContent: 'flex-start' }}>
          {u.files != null ? fmtSize(u.files) : '–'} / {u.fileQuota > 0 ? fmtSize(u.fileQuota) : '∞'}
        </Button>
        <Text size={100} style={{ color: tokens.colorNeutralForeground3, paddingLeft: 8 }}>{u.fileQuotaMb == null ? t('Vorgabe') : t('eigenes Kontingent')}</Text>
        {u.fileQuota > 0 && u.files != null && <ProgressBar value={Math.min(1, u.files / u.fileQuota)} thickness="medium" style={{ marginLeft: 8 }} color={u.files / u.fileQuota > 0.9 ? 'error' : u.files / u.fileQuota > 0.75 ? 'warning' : 'brand'} />}
      </div>) },
    { key: 'created', label: () => t('Angelegt'), width: 110, sort: u => u.created, render: u => new Date(u.created).toLocaleDateString(locale) },
    { key: 'actions', label: () => '', width: 130, fixed: true, render: u => <>
      <Button appearance="subtle" icon={<Key20Regular />} onClick={() => resetPassword(u)} title={t('Passwort setzen')} aria-label={t('Passwort setzen')} />
      {u.twoFactor && <Button appearance="subtle" icon={<ShieldDismiss20Regular />} onClick={() => reset2fa(u)} title={t('2FA zurücksetzen')} aria-label={t('2FA zurücksetzen')} />}
      {u.id !== me.id && <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => remove(u)} title={t('Löschen')} aria-label={t('Löschen')} />}
    </> },
  ]

  return (
    <div className={s.root}>
      <nav className={s.nav} aria-label={t('Verwaltung')}>
        <Text size={500} weight="semibold" style={{ padding: '0 10px 4px' }}>{t('Verwaltung')}</Text>
        {groups.map(g => <div key={g.label} style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
          <span className={s.navGroup}>{g.label}</span>
          {g.pages.map(([k, label]) => <button key={k} className={mergeClasses(s.navItem, tab === k && s.navOn)} aria-current={tab === k ? 'page' : undefined} onClick={() => pick(k)}>{label}</button>)}
        </div>)}
      </nav>
      <div className={s.mobileNav}>
        <Select value={tab} onChange={(_, d) => pick(d.value)} aria-label={t('Seite der Verwaltung')}>
          {groups.map(g => <optgroup key={g.label} label={g.label}>{g.pages.map(([k, label]) => <option key={k} value={k}>{label}</option>)}</optgroup>)}
        </Select>
      </div>
      <div className={s.page}>
      <Text size={600} weight="semibold">{pageLabel}</Text>
      {tab === 'users' && <SetupHints onBackup={() => pick('backup')} />}
      {/* Schlüssel als Datei ist für eine Installation zu Hause in Ordnung - nur ein ruhiger Tipp bei der Sicherung */}
      {status?.keyInDataDir && tab === 'backup' && (
        <MessageBar intent="info" layout="multiline" style={{ flexShrink: 0 }}>
          <MessageBarBody>
            {t('Der Hauptschlüssel liegt als Datei master.key im Datenverzeichnis und wird mit ihm gesichert. Für eine Installation zu Hause ist das in Ordnung. Wer Sicherungen außer Haus aufbewahrt, legt ihn besser getrennt ab.')}{' '}
            <a href={lang === 'en' ? 'https://github.com/Taloan/dmOffice/blob/master/README.md#data-and-keys' : 'https://github.com/Taloan/dmOffice/blob/master/README.de.md#daten-und-schl%C3%BCssel'} target="_blank" rel="noopener">{t('So geht’s')}</a>
          </MessageBarBody>
        </MessageBar>
      )}
      {status?.oldKeyFile && tab === 'backup' && (
        <MessageBar intent="info" layout="multiline" style={{ flexShrink: 0 }}>
          <MessageBarBody>
            {t('Der Umstieg auf DMOFFICE_MASTER_KEY ist erledigt. Die Datei master.key liegt noch im Datenverzeichnis. Die Datei master.key wird nur gebraucht, um eine Sicherung von vor dem Umstieg zurückzuspielen. Lade die Datei master.key herunter und bewahre sie bei diesen Sicherungen auf. Danach kannst du die Datei master.key hier löschen.')}
          </MessageBarBody>
          <MessageBarActions>
            <Button as="a" href="/api/admin/old-master-key" download="master.key">{t('Herunterladen')}</Button>
            <Button onClick={deleteOldKey}>{t('Löschen')}</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {tab === 'users' && <>
      <div style={{ display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap' }}>
        <Button appearance="primary" icon={<PersonAdd20Regular />} onClick={() => setAdding(true)}>{t('Benutzer anlegen')}</Button>
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Das Kontingent umfasst alles, was ein Benutzer belegt. Du stellst es je Benutzer in der Spalte „Kontingent“ ein; die Vorgabe steht unter „Kontingent und Grenzen“. Die Spalte „Datenbank“ zeigt davon nur Mails, Kalender, Kontakte und Notizen, ohne die Dateien.')}</Text>
      </div>
      <AdminTable id="users" cols={userCols} rows={users} rowKey={u => u.id} initialSort={{ col: 'login', desc: false }} />
      </>}
      {tab === 'quota' && status && <>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, maxWidth: 640 }}>
          <AdminHeading first>{t('Kontingent (Vorgabe)')}</AdminHeading>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Das Kontingent gilt für alle Benutzer, denen kein individuelles Kontingent zugewiesen wurde. Es umfasst den gesamten Speicherplatz, den ein Benutzer belegt. 0 = unbegrenzt. Abweichende Kontingente kannst du unter „Benutzer“ in der Spalte „Kontingent“ für jeden Benutzer einzeln festlegen.')}</Text>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <SizeInput mb={defaultQuota} onChange={setDefaultQuota} />
            <Button disabled={defaultQuota === status.defaultFileQuotaMb} onClick={saveDefaultQuota}>{t('Übernehmen')}</Button>
          </div>
        </div>
        <FileLimitsSettings initial={status.fileLimits} />
      </>}
      {tab === 'serverfolders' && <ExternalAdmin part="server" />}
      {tab === 'external' && <ExternalAdmin part="users" />}
      {tab === 'storage' && <StorageAdmin onOpen={pick} />}
      {keptPage('backup', <BackupStrategy />)}
      {tab === 'plainbackup' && <PlainBackupAdmin />}
      {tab === 'db' && <DbHealthAdmin />}
      {tab === 'modules' && <ModulesAdmin />}
      {tab === 'log' && <LogAdmin />}
      {featureOn('office') && keptPage('office', <OfficeSettings />)}
      {tab === 'updates' && <UpdatePanel />}
      {tab === 'chat' && <ChatRightsAdmin />}
      {tab === 'mail' && <><SpamListsAdmin /><SpamRulesAdmin /></>}
      {keptPage('ai', <AiAdmin />)}
      {tab === 'security' && <SecurityAdmin />}
      {status && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Datenverzeichnis: {dir}', { dir: status.dataDir })}</Text>}
      {adding && <NewUser onClose={c => { setAdding(false); if (c) load() }} />}
      </div>
      {quotaUser && <QuotaDialog user={quotaUser} defaultMb={status?.defaultFileQuotaMb ?? 0} onSave={mb => saveQuota(quotaUser, mb)} onClose={() => setQuotaUser(null)} />}
    </div>
  )
}

interface Limits { versionsDays: number; versionsMb: number; trashDays: number; trashMb: number; linkMaxDownloads: number; linkDays: number; zipMb: number }

/** Hoechstwerte, ueber die Benutzer unter Einstellungen -> Dateien nicht hinauskommen (0 = keine Vorgabe). */
function FileLimitsSettings({ initial }: { initial?: Limits }) {
  const { fail, notify } = useApp()
  const [l, setL] = useState<Limits>(initial ?? { versionsDays: 0, versionsMb: 0, trashDays: 0, trashMb: 0, linkMaxDownloads: 0, linkDays: 0, zipMb: 5120 })
  const save = async (p: Partial<Limits>) => {
    const next = { ...l, ...p }
    setL(next)
    try { setL(await api.put<Limits>('/api/admin/file-limits', next)); notify(t('Vorgabe gespeichert')) } catch (e) { fail(e) }
  }
  const none = t('keine Vorgabe')
  const pickField = (label: string, key: keyof Limits, steps: number[], text: (v: number) => string) => (
    <Field label={label}>
      <Select value={String(l[key])} onChange={(_, d) => save({ [key]: Number(d.value) } as Partial<Limits>)}>
        {[0, ...steps].concat(steps.includes(l[key]) || l[key] === 0 ? [] : [l[key]]).map(v => <option key={v} value={String(v)}>{v === 0 ? none : text(v)}</option>)}
      </Select>
    </Field>
  )
  const grid = { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(260px, 1fr))', gap: 12, maxWidth: 760 } as const
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 16 }}>
      <Text>{t('Höchstwerte für alle Benutzer. In ihren Einstellungen unter „Dateien“ können sie nur gleich viel oder weniger wählen; „unbegrenzt“ entfällt dann.')}</Text>
      <AdminHeading>{t('Frühere Versionen')}</AdminHeading>
      <div style={grid}>
        {pickField(t('Höchstens aufbewahren'), 'versionsDays', [7, 30, 90, 180, 365, 730], d => daysText(d, ''))}
        {pickField(t('Speichergrenze je Benutzer'), 'versionsMb', [10, 100, 500, 1024], mbText)}
      </div>
      <AdminHeading>{t('Papierkorb')}</AdminHeading>
      <div style={grid}>
        {pickField(t('Spätestens leeren nach'), 'trashDays', [7, 14, 30, 60, 90, 180, 365], d => daysText(d, ''))}
        {pickField(t('Speichergrenze je Benutzer'), 'trashMb', [10, 100, 500, 1024], mbText)}
      </div>
      <AdminHeading>{t('Freigabelinks')}</AdminHeading>
      <div style={grid}>
        {pickField(t('Höchstens Abrufe je Link'), 'linkMaxDownloads', [1, 3, 5, 10, 25, 50, 100], n => String(n))}
        {pickField(t('Läuft spätestens ab nach'), 'linkDays', [1, 7, 14, 30, 90, 365], d => daysText(d, ''))}
      </div>
      <AdminHeading>{t('Herunterladen als ZIP')}</AdminHeading>
      <div style={grid}>
        {pickField(t('Höchstens (Downloadkorb, „Tresor herunterladen“)'), 'zipMb', [1024, 2048, 5120, 10240, 20480], mbText)}
      </div>
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Gilt sofort, auch für bestehende Einstellungen; neue und geänderte Links halten sich an die Vorgaben.')}</Text>
    </div>
  )
}

interface OfficeStatus {
  url: string; wopiBase: string; ok: boolean; error?: string; product?: string; native?: boolean; proof?: boolean; edit?: string[]
  jwt?: boolean; jwtHeader?: string
}

/** Office-Server (Collabora/CODE, ONLYOFFICE) fuer das Bearbeiten im Browser: Adresse eintragen, pruefen, speichern. */
function OfficeSettings() {
  const { fail, notify } = useApp()
  const [st, setSt] = useState<OfficeStatus | null>(null)
  const [url, setUrl] = useState('')
  const [wopiBase, setWopiBase] = useState('')
  // Geheimnis: leer lassen = unveraendert (es kommt nie zurueck in die Oberflaeche)
  const [secret, setSecret] = useState('')
  const [header, setHeader] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [advanced, setAdvanced] = useState(false)
  const load = () => api.get<OfficeStatus>('/api/admin/office').then(x => {
    setSt(x); setUrl(x.url); setWopiBase(x.wopiBase); setHeader(x.jwtHeader ?? ''); setSecret('')
  }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const save = async (force = false, off = false, clearSecret = false) => {
    setBusy(true); setError(null)
    try {
      await api.put('/api/admin/office', {
        url: off ? '' : url, wopiBase: off ? '' : wopiBase, force,
        jwtSecret: clearSecret ? '' : secret.trim() ? secret.trim() : undefined, jwtHeader: header.trim(),
      })
      forgetOffice()
      notify(off ? t('Office im Browser abgeschaltet') : t('Office-Server gespeichert'))
      await load()
    } catch (e: any) { setError(tError(e?.message || String(e))) }
    setBusy(false)
  }
  // dmOffice ueber https, Office-Server ueber http: der Browser blockiert den Editor (gemischte Inhalte)
  const mixed = location.protocol === 'https:' && /^http:\/\//i.test(url.trim())
  const product = st?.product === 'collabora' ? 'Collabora Online' : st?.product === 'onlyoffice' ? 'ONLYOFFICE (WOPI)'
    : st?.product === 'onlyoffice-docs' ? 'ONLYOFFICE Docs' : t('WOPI-Server')
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, maxWidth: 720 }}>
      <AdminHeading>{t('Office im Browser')}</AdminHeading>
      <Text size={200}>
        {t('Word-, Excel- und PowerPoint-Dateien (auch ODF) direkt in der Dateiablage bearbeiten – mit einem eigenen Office-Server: Collabora Online/CODE (empfohlen, auch auf dem Handy) oder ONLYOFFICE Docs.')}{' '}
        <a href="https://github.com/Taloan/dmOffice/blob/master/docs/office.md" target="_blank" rel="noreferrer">{t('Anleitung (Unraid, Docker)')}</a>
      </Text>
      <Field label={t('Adresse des Office-Servers')} hint={t('Mit https, erreichbar für den Browser und für dmOffice, etwa https://office.example.org. Leer = aus.')}>
        <Input value={url} placeholder="https://office.example.org" onChange={(_, d) => setUrl(d.value)} />
      </Field>
      {mixed && (
        <MessageBar intent="warning"><MessageBarBody>
          {t('dmOffice läuft über https, der Office-Server über http – der Browser blockiert den Editor dann (gemischte Inhalte), und unterwegs ist eine Adresse im Heimnetz nicht erreichbar. Besser: eigene Subdomain mit Zertifikat am Reverse Proxy.')}
        </MessageBarBody></MessageBar>
      )}
      <Button appearance="transparent" size="small" icon={advanced ? <ChevronDown16Regular /> : <ChevronRight16Regular />} aria-expanded={advanced} style={{ alignSelf: 'flex-start', padding: 0, fontWeight: 600 }} onClick={() => setAdvanced(x => !x)}>{t('Fortgeschritten')}</Button>
      {advanced && <>
      <Field label={t('JWT-Geheimnis (ONLYOFFICE)')} hint={st?.jwt ? t('Gespeichert. Leer lassen = unverändert.') : t('Das JWT_SECRET des Document Servers; Collabora braucht keins.')}>
        <Input type="password" value={secret} autoComplete="new-password" placeholder={st?.jwt ? '••••••••' : ''} onChange={(_, d) => setSecret(d.value)} />
      </Field>
      <Field label={t('JWT-Kopfzeile (optional)')} hint={t('Nur wenn am Document Server JWT_HEADER geändert ist. Vorgabe: Authorization.')}>
        <Input value={header} placeholder="Authorization" onChange={(_, d) => setHeader(d.value)} />
      </Field>
      <Field label={t('Adresse von dmOffice für den Office-Server (optional)')} hint={t('Nur nötig, wenn der Office-Server dmOffice unter einer anderen Adresse erreicht als der Browser (kein Hairpin-NAT). Bei Collabora muss sie in der aliasgroup1 stehen.')}>
        <Input value={wopiBase} placeholder={location.origin} onChange={(_, d) => setWopiBase(d.value)} />
      </Field>
      </>}
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <Button appearance="primary" disabled={busy || !url.trim()} onClick={() => save()}>{t('Prüfen und speichern')}</Button>
        {error && url.trim() && <Button disabled={busy} onClick={() => save(true)}>{t('Trotzdem speichern')}</Button>}
        {st?.jwt && <Button disabled={busy} onClick={() => save(true, false, true)}>{t('Geheimnis löschen')}</Button>}
        {st?.url && <Button disabled={busy} onClick={() => save(false, true)}>{t('Abschalten')}</Button>}
      </div>
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {!error && st?.url && (st.ok
        ? <MessageBar intent="success"><MessageBarBody>
            {t('Verbunden mit {product}. Bearbeiten: {types}.', { product, types: (st.edit ?? []).join(', ') || '–' })}{' '}
            {st.native ? (st.jwt ? t('Anfragen werden mit dem JWT-Geheimnis signiert und geprüft.') : t('Ohne JWT-Geheimnis – jeder, der den Document Server erreicht, kann ihn nutzen.'))
              : st.proof ? t('Anfragen werden mit Proof-Keys geprüft.') : t('Der Office-Server veröffentlicht keine Proof-Keys – geprüft wird nur das Token.')}
          </MessageBarBody></MessageBar>
        : <MessageBar intent="warning"><MessageBarBody>{st.error ? tError(st.error) : t('Der Office-Server ist gerade nicht erreichbar.')}</MessageBarBody></MessageBar>)}
    </div>
  )
}

/** Kontingent fuer die Dateien eines Benutzers: Vorgabe der Verwaltung, unbegrenzt oder eine eigene Grenze. */
function QuotaDialog({ user, defaultMb, onSave, onClose }: { user: User; defaultMb: number; onSave: (mb: number | null) => void; onClose: () => void }) {
  type Mode = 'default' | 'unlimited' | 'own'
  const [mode, setMode] = useState<Mode>(user.fileQuotaMb == null ? 'default' : user.fileQuotaMb === 0 ? 'unlimited' : 'own')
  const [mb, setMb] = useState(user.fileQuotaMb && user.fileQuotaMb > 0 ? user.fileQuotaMb : defaultMb > 0 ? defaultMb : 10 * 1024)
  const used = user.files ?? 0
  const effectiveMb = mode === 'default' ? defaultMb : mode === 'unlimited' ? 0 : mb
  const share = effectiveMb > 0 ? Math.min(1, used / (effectiveMb * 1024 * 1024)) : 0
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <DialogBody>
          <DialogTitle>{t('Kontingent für {login}', { login: user.login })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Umfasst alles, was der Benutzer belegt.')}</Text>
            <RadioGroup value={mode} onChange={(_, d) => setMode(d.value as Mode)}>
              <Radio value="default" label={t('Vorgabe verwenden ({quota})', { quota: quotaText(defaultMb) })} />
              <Radio value="unlimited" label={t('Unbegrenzt')} />
              <Radio value="own" label={t('Eigenes Kontingent')} />
            </RadioGroup>
            {mode === 'own' && <SizeInput mb={mb} onChange={setMb} />}
            <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
              <Text size={200}>{effectiveMb > 0 ? t('{used} von {quota} belegt', { used: fmtSize(used), quota: fmtSize(effectiveMb * 1024 * 1024) }) : t('{size} belegt, keine Grenze', { size: fmtSize(used) })}</Text>
              {effectiveMb > 0 && used > effectiveMb * 1024 * 1024 && (
                <MessageBar intent="warning"><MessageBarBody>{t('{login} belegt schon mehr. Alles Vorhandene bleibt, aber es werden keine neuen Mails mehr abgerufen und keine Dateien mehr gespeichert, bis wieder Platz frei ist (Mails löschen, Aufbewahrung, Papierkorb, frühere Versionen). Frühere Versionen und der Papierkorb werden bei Bedarf zuerst gelöscht.', { login: user.login })}</MessageBarBody></MessageBar>
              )}
              {effectiveMb > 0 && <ProgressBar value={share} thickness="medium" color={share > 0.9 ? 'error' : share > 0.75 ? 'warning' : 'brand'} />}
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={() => onSave(mode === 'default' ? null : mode === 'unlimited' ? 0 : mb)}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

function NewUser({ onClose }: { onClose: (created: boolean) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [login, setLogin] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  // Erstes Passwort sichtbar machen: ohne Wiederholungsfeld faellt ein Tippfehler sonst erst bei der Anmeldung auf
  const [showPw, setShowPw] = useState(false)
  const [isAdmin, setIsAdmin] = useState(false)
  const save = async () => {
    try { await api.post('/api/admin/users', { login, displayName, password, isAdmin }); onClose(true) } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 420 }}>
        <DialogBody>
          <DialogTitle>{t('Benutzer anlegen')}</DialogTitle>
          <DialogContent>
            <form className={s.form} onSubmit={e => { e.preventDefault(); save() }}>
              <Field label={t('Anmeldename')} hint={t('a–z, 0–9, Punkt, Bindestrich – erscheint auch in den DAV-Adressen')}><Input value={login} onChange={(_, d) => setLogin(d.value)} autoFocus /></Field>
              <Field label={t('Anzeigename')}><Input value={displayName} onChange={(_, d) => setDisplayName(d.value)} /></Field>
              <Field label={t('Erstes Passwort')} hint={t('mindestens 8 Zeichen; ändert der Benutzer unter Einstellungen → Sicherheit')}><Input type={showPw ? 'text' : 'password'} value={password} autoComplete="new-password" onChange={(_, d) => setPassword(d.value)}
                contentAfter={<Button appearance="transparent" size="small" icon={showPw ? <EyeOff20Regular /> : <Eye20Regular />} onClick={() => setShowPw(v => !v)}
                  aria-label={showPw ? t('Passwort verbergen') : t('Passwort anzeigen')} title={showPw ? t('Passwort verbergen') : t('Passwort anzeigen')} />} /></Field>
              <Switch label={t('Verwaltung erlaubt')} checked={isAdmin} onChange={(_, d) => setIsAdmin(d.checked)} />
              <button type="submit" hidden />
            </form>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={save}>{t('Anlegen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
