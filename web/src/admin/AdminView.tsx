import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarActions, MessageBarBody,
  Switch, Tab, TabList, Text, makeStyles, mergeClasses, tokens, Select, Radio, RadioGroup, ProgressBar, MenuItem, MenuDivider,
} from '@fluentui/react-components'
import { Checkmark20Regular, ChevronDown16Regular, ChevronRight16Regular, PersonAdd20Regular, Delete20Regular, Edit20Regular, Key20Regular, ShieldDismiss20Regular, Edit16Regular, Eye20Regular, EyeOff20Regular, LockClosed20Regular, LockOpen20Regular,
  People20Regular, Storage20Regular, AppsListDetail20Regular, Settings20Regular, ArrowSync20Regular, PersonKey20Regular } from '@fluentui/react-icons'
import { RibbonActions, RibbonButton, RibbonDivider, RibbonHeader, RibbonSlot, useSidePane } from '../shell/Ribbon'
import { pressable, useNavStyles } from '../shell/nav'
import { SidePane, useIsMobile } from '../shell/mobile'
import { Fragment, useEffect, useState, useRef } from 'react'
import { useOnce } from '../shell/hooks'
import { api, featureOn, fmtSize } from '../api'
import { useApp } from '../App'
import { SetupHints } from './SetupHints'
import { t, lang, locale, tError } from '../i18n'
import { AdminHeading } from './AdminHeading'
import { AdminCol, AdminTable, pickCol } from './AdminTable'
import { forgetOffice } from '../files/Office'
import { daysText, mbText } from '../settings/FilesSettings'
import { UpdatePanel } from './UpdatePanel'
import { ChatRightsAdmin } from '../chat/ChatSettings'
import { AiAdmin } from './AiAdmin'
import { SpamListsAdmin } from './SpamListsAdmin'
import { SpamRulesAdmin } from './SpamRulesAdmin'
import { VirusAdmin } from './VirusAdmin'
import { OidcAdmin } from './OidcAdmin'
import { OidcUserDialog } from './OidcUserDialog'
import { SecurityAdmin } from './SecurityAdmin'
import { PublicUrlAdmin } from './PublicUrlAdmin' // [feature:publicurl]
import { StorageAdmin } from './StorageAdmin'
import { BackupStrategy } from './BackupStrategy'
import { ExternalAdmin } from './ExternalAdmin'
import { LogAdmin } from './LogAdmin'
import { PlainBackupAdmin } from './PlainBackupAdmin'
import { DbHealthAdmin } from './DbHealthAdmin'
import { ModulesAdmin } from '../settings/ModuleSettings'
import { PluginsAdmin } from './PluginsAdmin'
import { GateKeeperAdmin } from '../gatekeeper/GateKeeperAdmin' // [feature:gatekeeper]
import { AdminDraftHost, AdminDraftProvider, AdminSaveButtons, useAdminDraft, useAdminSave } from './draft' // [feature:savemode]
import { ContextMenu, askConfirm, askText, useContextMenu } from '../shell/context'
// [feature:adminclean]
import { Suspense, lazy } from 'react'
import { ArrowDownload20Regular, PlugConnected20Regular, Power20Regular, Save20Regular } from '@fluentui/react-icons'
import { ADMIN_FORM_WIDTH, AdminPageHead, AdminPartCtx, useAdminPart, type AdminTabDef } from './AdminPage'
import { UsersAdmin } from './UsersAdmin'
import { useSetupOpen } from './SetupHints'
const LensAdmin = lazy(() => import('./LensAdmin').then(m => ({ default: m.LensAdmin }))) // [feature:lenslib]
// [/feature:adminclean]

const useStyles = makeStyles({
  // Links die Bereiche der Verwaltung, rechts oben die Seiten des Bereichs als Reiter
  root: { flex: 1, display: 'flex', minWidth: 0, minHeight: 0, '@media (max-width: 767px)': { flexDirection: 'column' } },
  nav: {
    width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '20px 8px', display: 'flex', flexDirection: 'column', gap: '2px', overflowY: 'auto',
    '@media (max-width: 767px)': { display: 'none' },
  },
  navGroup: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, padding: '12px 10px 4px', fontWeight: tokens.fontWeightSemibold },
  navItem: {
    textAlign: 'left', border: 'none', background: 'none', color: 'inherit', font: 'inherit', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    display: 'flex', alignItems: 'center', gap: '10px',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  navOn: { backgroundColor: tokens.colorNeutralBackground1Selected, fontWeight: tokens.fontWeightSemibold },
  mobileNav: { display: 'none', padding: '12px 12px 0', '@media (max-width: 767px)': { display: 'block' } },
  tabs: { '@media (max-width: 767px)': { display: 'none' } },
  page: { flex: 1, overflowY: 'auto', padding: '24px 32px', display: 'flex', flexDirection: 'column', gap: '16px', minWidth: 0, '@media (max-width: 767px)': { padding: '12px' } },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  // Mit Menüband (Kennung adminribbon): oben das Band, darunter Abschnittsleiste und Seite wie in den anderen Bereichen
  frame: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0 },
  split: { flex: 1, display: 'flex', minWidth: 0, minHeight: 0, position: 'relative' },
  side: { flexShrink: 0, overflowY: 'auto' },
  hint: { color: tokens.colorNeutralForeground3 },
})

interface User {
  id: string; login: string; displayName: string; isAdmin: boolean; created: string; size: number; twoFactor: boolean
  /** Anmeldung über OIDC: verknüpft, vorgemerkter Name beim Anbieter, noch ohne eigenes Passwort */
  oidcLinked?: boolean; oidcExpected?: string | null; oidcInvite?: string | null; noOwnPassword?: boolean
  /** null: Vorgabe; 0: unbegrenzt */
  fileQuotaMb: number | null; fileQuota: number; files: number | null
  /** Dateiablage abgeschaltet - notizi-Abgleich geht weiter */
  filesOff: boolean
  /** Pukki darf fuer diesen Benutzer die KI fragen */
  aiAllowed?: boolean
  /** Gesperrt (Kennung userlock): keine Anmeldung, Hintergrundarbeit ruht, Daten bleiben */
  locked?: boolean
}

export type AdminUser = User // [feature:adminclean]

type SizeUnit = 'MB' | 'GB' | 'TB'
const UNIT_MB: Record<SizeUnit, number> = { MB: 1, GB: 1024, TB: 1024 * 1024 }
/** MB als Zahl mit der groessten Einheit, in der sie glatt aufgeht (1536 MB bleiben 1536 MB). */
// Größte Einheit, in der der Wert mit höchstens drei Nachkommastellen genau ist (1536 MB = „1,5 GB“, nicht „1536 MB“ -
// sonst wird ein später getipptes „1,5“ still als MB gelesen)
const exactIn = (mb: number, u: SizeUnit) => mb >= UNIT_MB[u] && Number.isInteger(Math.round(mb * 1000 / UNIT_MB[u] * 1e6) / 1e6)
const splitMb = (mb: number): { value: number; unit: SizeUnit } =>
  mb > 0 && exactIn(mb, 'TB') ? { value: mb / UNIT_MB.TB, unit: 'TB' } : mb > 0 && exactIn(mb, 'GB') ? { value: mb / UNIT_MB.GB, unit: 'GB' } : { value: mb, unit: 'MB' }

/**
 * Zahl mit Einheit (MB, GB, TB), gespeichert wird in MB. Wechselt man die Einheit, bleibt die Zahl stehen und gilt in der
 * neuen Einheit („1“ und dann „TB“ = 1 TB) - früher rechnete nur die Anzeige um und rundete 1023 GB zu „1 TB“.
 */
export function SizeInput({ mb, onChange }: { mb: number; onChange: (mb: number) => void }) {
  const [unit, setUnit] = useState<SizeUnit>(splitMb(mb).unit)
  const shown = (v: number, u: SizeUnit) => String(Math.round((v / UNIT_MB[u]) * 1000) / 1000).replace('.', ',')
  const [text, setText] = useState(() => shown(mb, splitMb(mb).unit))
  // Selbst gemeldete Werte: Einheit und Text nur anpassen, wenn der Wert von außen kommt (nach dem Laden)
  const sent = useRef<number | null>(null)
  useEffect(() => {
    if (Object.is(mb, sent.current) || Number.isNaN(mb)) return
    const u = splitMb(mb).unit
    setUnit(u); setText(shown(mb, u))
  }, [mb])
  // Komma oder Punkt als Dezimaltrenner; leer, negativ oder keine Zahl: ungültig (NaN), nie stillschweigend „unbegrenzt“
  const parse = (s: string) => { const v = s.trim() === '' ? NaN : Number(s.trim().replace(',', '.')); return Number.isFinite(v) && v >= 0 ? v : NaN }
  const send = (s: string, u: SizeUnit) => { const v = parse(s); const next = Number.isNaN(v) ? NaN : Math.round(v * UNIT_MB[u]); sent.current = next; onChange(next) }
  const bad = Number.isNaN(parse(text))
  return (
    <Field validationState={bad ? 'error' : 'none'} validationMessage={bad ? t('Bitte eine Zahl ab 0 eingeben, z. B. 1,5.') : undefined}>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
        <Input value={text} inputMode="decimal" style={{ width: 120 }} onChange={(_, d) => { setText(d.value); send(d.value, unit) }} />
        <Select value={unit} style={{ minWidth: 72 }} onChange={(_, d) => { const u = d.value as SizeUnit; setUnit(u); send(text, u) }}>
          <option value="MB">MB</option><option value="GB">GB</option><option value="TB">TB</option>
        </Select>
      </div>
    </Field>
  )
}

export const quotaText = (mb: number) => (mb > 0 ? (({ value, unit }) => `${value} ${unit}`)(splitMb(mb)) : t('unbegrenzt'))

export default function AdminView() {
  const s = useStyles()
  const { me, fail, notify } = useApp()
  const [users, setUsers] = useState<User[]>([])
  const [status, setStatus] = useState<any>(null)
  const [adding, setAdding] = useState(false)
  // Seite gemerkt fuer den naechsten Besuch; fruehere Reiter-Namen gelten weiter
  const clean = featureOn('adminclean') && featureOn('adminribbon') // [feature:adminclean]
  const [tab, setTab] = useState<string>(() => { try { const v = localStorage.getItem('dmoffice.admin.tab') ?? 'users'; return v === 'files' ? 'quota' : v } catch { return 'users' } })
  // Vier Bereiche links; ihre Seiten oben als Reiter (früher alle Seiten untereinander - das Menü wurde zu lang)
  const groups: { key: string; label: string; icon: React.ReactNode; pages: [string, string][] }[] = [
    { key: 'people', label: t('Benutzer'), icon: <People20Regular />, pages: [['users', t('Benutzer')], ...(featureOn('oidc') ? [['sso', t('Anmeldung (OIDC)')] as [string, string]] : [])] },
    { key: 'space', label: t('Speicher'), icon: <Storage20Regular />, pages: [['quota', t('Kontingente und Grenzen')], ['storage', t('Storage Extender')], ['backup', t('Backup-Strategie')],
      ['serverfolders', t('Server-Mounts')], ['external', t('Benutzer-Mounts')], ['plainbackup', t('Benutzer-Datenexport')]] },
    // Office, Coach und Bankzugang nur mit eingeschaltetem Modul (Hauptschalter: System → Module)
    { key: 'services', label: t('Dienste'), icon: <AppsListDetail20Regular />, pages: [...(featureOn('office') ? [['office', t('Office im Browser')] as [string, string]] : []), ['chat', t('Chat')], ['mail', t('E-Mail')], ['ai', t('KI')],
      ...(featureOn('gatekeeper') ? [['gatekeeper', 'GateKeeper'] as [string, string]] : []), // [feature:gatekeeper]
      ] },
    // Plugins vor Module: welche Module es zum Einschalten gibt, hängt von den Plugins ab
    { key: 'system', label: t('System'), icon: <Settings20Regular />, pages: [...(featureOn('plugins') ? [['plugins', t('Plugins')] as [string, string]] : []), ['modules', t('Module')],
      ...(featureOn('publicurl') ? [['address', t('Öffentliche Adresse')] as [string, string]] : []), // [feature:publicurl]
      ['security', t('Sicherheit')], ['log', t('Protokoll')], ['updates', t('Updates')], ['db', t('Datenbanken')]] },
  ]
  const allPages = groups.flatMap(g => g.pages)
  const group = groups.find(g => g.pages.some(p => p[0] === tab)) ?? groups[0]
  // Je Bereich die zuletzt gewählte Seite: ein Klick auf den Bereich führt dorthin zurück
  const lastOf = (g: typeof groups[number]) => { try { const v = localStorage.getItem('dmoffice.admin.last.' + g.key); if (v && g.pages.some(p => p[0] === v)) return v } catch { /* egal */ } return g.pages[0][0] }
  // Gemerkte Seite gibt es nicht mehr (frühere Seite „Audiothek“, Modul ausgeschaltet): Benutzer
  // [feature:adminclean]
  useEffect(() => { if (!clean && !allPages.some(p => p[0] === tab)) setTab('users') }, [tab, allPages.length]) // eslint-disable-line react-hooks/exhaustive-deps
  // [feature:adminclean:else]
  // useEffect(() => { if (!allPages.some(p => p[0] === tab)) setTab('users') }, [tab, allPages.length]) // eslint-disable-line react-hooks/exhaustive-deps
  // [/feature:adminclean]
  // [feature:savemode]
  // Kennung savemode (mit Menüband): Änderungen der Seiten sammeln, Speichern/Verwerfen im Band, Rückfrage beim Verlassen
  const [drafts] = useState(() => featureOn('savemode') && featureOn('adminribbon') ? new AdminDraftHost() : null)
  const pick = (v: string) => {
    if (drafts?.dirty && v !== tab) { void drafts.leave().then(ok => { if (ok) show(v) }); return }
    show(v)
  }
  // Verwaltung verlassen (anderer Bereich): erst zurück, dann fragen und danach weiter
  useEffect(() => {
    if (!drafts) return
    let here = /^#\/?admin/.test(location.hash) ? location.hash : '#/admin', pass = false
    const onHash = () => {
      if (/^#\/?admin/.test(location.hash)) { here = location.hash; return }
      if (pass || !drafts.dirty) { pass = false; return }
      const target = location.hash
      location.hash = here
      void drafts.leave().then(ok => { if (ok) { pass = true; location.hash = target } })
    }
    window.addEventListener('hashchange', onHash)
    return () => window.removeEventListener('hashchange', onHash)
  }, [drafts])
  const show = (v: string) => {
  // [feature:savemode:else]
  // const pick = (v: string) => {
  // [/feature:savemode]
    setTab(v)
    const g = groups.find(x => x.pages.some(p => p[0] === v))
    try { localStorage.setItem('dmoffice.admin.tab', v); if (g) localStorage.setItem('dmoffice.admin.last.' + g.key, v) } catch { /* egal */ }
  }
  // Seiten mit Formularen zum Speichern bleiben beim Wechsel erhalten (nur ausgeblendet), halb Ausgefülltes geht nicht verloren
  const [kept, setKept] = useState<string[]>([])
  useEffect(() => { if (['backup', 'office', 'ai'].includes(tab)) setKept(k => k.includes(tab) ? k : [...k, tab]) }, [tab])
  const keptPage = (key: string, page: React.ReactNode) => (tab === key || kept.includes(key)) && <div style={{ display: tab === key ? 'contents' : 'none' }}>{page}</div>

  const load = () => api.get<User[]>('/api/admin/users').then(setUsers).catch(fail)
  const [defaultQuota, setDefaultQuota] = useState(0)
  useEffect(() => { if (status) setDefaultQuota(status.defaultFileQuotaMb ?? 0) }, [status])
  const saveDefaultQuota = async () => {
    if (Number.isNaN(defaultQuota)) { fail(new Error(t('Bitte eine Zahl ab 0 eingeben, z. B. 1,5.'))); return false }
    try {
      await api.put('/api/admin/settings', { defaultFileQuotaMb: defaultQuota })
      setStatus({ ...status, defaultFileQuotaMb: defaultQuota }); load(); notify(t('Vorgabe gespeichert')); window.dispatchEvent(new Event('dmoffice:storage'))
      return true // [feature:savemode]
    } catch (e) { fail(e) }
    return false // [feature:savemode]
  }
  // [feature:savemode]
  const quotaDraft = useAdminSave({ dirty: !!status && defaultQuota !== status.defaultFileQuotaMb, save: saveDefaultQuota, discard: () => setDefaultQuota(status?.defaultFileQuotaMb ?? 0) }, drafts)
  // [/feature:savemode]
  const [quotaUser, setQuotaUser] = useState<User | null>(null)
  const [oidcUser, setOidcUser] = useState<User | null>(null)
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
    if (!await askConfirm(t('Datei master.key löschen'), t('Hast du die Datei master.key heruntergeladen? Ohne die Datei master.key lassen sich Backups von vor dem Umstieg nicht mehr zurückspielen.'), t('Löschen'))) return
    try { await api.del('/api/admin/old-master-key'); setStatus({ ...status, oldKeyFile: false }); notify(t('Datei gelöscht')) } catch (e) { fail(e) }
  }

  const resetPassword = async (u: User) => {
    const password = await askText(t('Passwort setzen'), '', t('Neues Passwort für {login} (mindestens 8 Zeichen):', { login: u.login }))
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
  const [removeUser, setRemoveUser] = useState<User | null>(null)
  // Rechtsklick auf einen Benutzer (Kennung ctxall): dieselben Aktionen wie Schalter und Symbole der Zeile
  const userMenu = useContextMenu<User>()
  const remove = async (u: User) => {
    try { await api.del(`/api/admin/users/${u.id}`); setRemoveUser(null); load() } catch (e) { fail(e) }
  }
  // Sperren als Vorstufe zum Löschen (Kennung userlock)
  const lockOn = featureOn('userlock')
  const lock = async (u: User, asked = false) => {
    if (!asked && !await askConfirm(t('Benutzer {login} sperren', { login: u.login }), t('{login} kann sich danach nicht mehr anmelden. Alle Sitzungen enden, App-Passwörter werden gelöscht, Mailabruf und Abgleich ruhen. Die Daten bleiben; Entsperren ist jederzeit möglich.', { login: u.login }), t('Sperren'))) return
    try { await api.post(`/api/admin/users/${u.id}/lock`); setRemoveUser(null); load(); notify(t('Benutzer gesperrt')) } catch (e) { fail(e) }
  }
  const unlock = async (u: User) => {
    try { await api.post(`/api/admin/users/${u.id}/unlock`); load(); notify(t('Benutzer entsperrt'), t('{login} bekommt eine Nachricht.', { login: u.login })) } catch (e) { fail(e) }
  }
  // Menüband (Kennung adminribbon): ausgewählter Benutzer für die Knöpfe im Band, Abschnittsleiste links
  const ribbon = featureOn('adminribbon')
  const [pickedId, setPickedId] = useState<string | null>(null)
  const picked = users.find(u => u.id === pickedId) ?? null
  const [slot, setSlot] = useState<HTMLDivElement | null>(null)
  // Aktualisieren im Menüband lädt die gezeigte Seite neu (neu einhängen)
  const [rev, setRev] = useState(0)
  const nv = useNavStyles()
  const mobile = useIsMobile()
  const [drawer, toggleSide, closeSide] = useSidePane('admin')
  const userCols: AdminCol<User>[] = [
    ...(ribbon ? [pickCol<User, string>({ id: u => u.id, selected: pickedId, onChange: setPickedId })] : []),
    { key: 'login', label: () => t('Anmeldename'), width: 130, sort: u => u.login, render: u => lockOn && u.locked
      ? <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}><span style={{ color: tokens.colorNeutralForeground3 }}>{u.login}</span><Badge color="danger" appearance="tint">{t('gesperrt')}</Badge></span>
      : u.login },
    { key: 'name', label: () => t('Name'), width: 150, sort: u => u.displayName, render: u => u.displayName },
    { key: 'role', label: () => t('Rolle'), width: 150, sort: u => (u.isAdmin ? 0 : 1), render: u => <Switch checked={u.isAdmin} disabled={u.id === me.id} label={{ children: u.isAdmin ? t('Admin') : t('Benutzer'), style: { whiteSpace: 'nowrap' } }} onChange={() => toggleAdmin(u)} /> },
    { key: 'twoFactor', label: () => '2FA', width: 70, sort: u => (u.twoFactor ? 0 : 1), render: u => (u.twoFactor ? <Badge color="success">{t('an')}</Badge> : <Badge color="informative" appearance="outline">{t('aus')}</Badge>) },
    // Mit Menüband nur Anzeige: Ändern über „Anmeldung über OIDC“ bzw. „Kontingent festlegen“ im Menüband
    ...(featureOn('oidc') ? [{ key: 'oidc', label: () => 'OIDC', width: 150, sort: (u: User) => (u.oidcLinked ? 0 : u.oidcExpected || u.oidcInvite ? 1 : 2), render: (u: User) => {
      const v = u.oidcLinked ? t('verknüpft') : u.oidcInvite ? t('eingeladen') : u.oidcExpected ? t('vorgemerkt: {name}', { name: u.oidcExpected }) : '–'
      return ribbon ? v : <Button appearance="subtle" size="small" icon={<Edit16Regular />} onClick={() => setOidcUser(u)} title={t('Anmeldung über OIDC')} style={{ justifyContent: 'flex-start' }}>{v}</Button>
    } } as AdminCol<User>] : []),
    { key: 'db', label: () => t('Datenbank'), width: 100, sort: u => u.size, render: u => fmtSize(u.size) },
    { key: 'files', label: () => t('Dateiablage'), width: 100, sort: u => (u.filesOff ? 1 : 0), render: u => <Switch checked={!u.filesOff} onChange={() => toggleFiles(u)} aria-label={t('Dateiablage')}
      title={u.filesOff ? t('Dateiablage aus – nur der notizi-Abgleich geht') : t('Dateiablage an')} /> },
    // Erst mit eingerichteter KI (Verwaltung → KI); der Grund steht am Umschlag, ein gesperrter Schalter zeigt selbst keinen Tooltip
    { key: 'ai', label: () => t('KI'), width: 70, sort: u => (u.aiAllowed ? 0 : 1), render: u => <span title={!status?.aiConfigured ? t('Erst unter „KI“ eine KI einrichten') : u.aiAllowed ? t('Darf die KI nutzen') : t('Ohne KI')}>
      <Switch checked={!!u.aiAllowed && !!status?.aiConfigured} disabled={!status?.aiConfigured} onChange={() => toggleAi(u)} aria-label={t('KI')} /></span> },
    { key: 'quota', label: () => t('Kontingent'), width: 190, sort: u => u.files ?? 0, render: u => (
      <div style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 150 }}>
        {ribbon
          ? <Text size={200} style={{ paddingLeft: 8 }}>{u.files != null ? fmtSize(u.files) : '–'} / {u.fileQuota > 0 ? fmtSize(u.fileQuota) : '∞'}</Text>
          : <Button appearance="subtle" size="small" icon={<Edit16Regular />} onClick={() => setQuotaUser(u)} title={t('Kontingent festlegen')} style={{ justifyContent: 'flex-start' }}>
            {u.files != null ? fmtSize(u.files) : '–'} / {u.fileQuota > 0 ? fmtSize(u.fileQuota) : '∞'}
          </Button>}
        <Text size={100} style={{ color: tokens.colorNeutralForeground3, paddingLeft: 8 }}>{u.fileQuotaMb == null ? t('Vorgabe') : t('eigenes Kontingent')}</Text>
        {u.fileQuota > 0 && u.files != null && <ProgressBar value={Math.min(1, u.files / u.fileQuota)} thickness="medium" style={{ marginLeft: 8 }} color={u.files / u.fileQuota > 0.9 ? 'error' : u.files / u.fileQuota > 0.75 ? 'warning' : 'brand'} />}
      </div>) },
    { key: 'created', label: () => t('Angelegt'), width: 110, sort: u => u.created, render: u => new Date(u.created).toLocaleDateString(locale) },
    ...(ribbon ? [] : [{ key: 'actions', label: () => '', width: lockOn ? 170 : 130, fixed: true, render: (u: User) => <>
      <Button appearance="subtle" icon={<Key20Regular />} onClick={() => resetPassword(u)} title={t('Passwort setzen')} aria-label={t('Passwort setzen')} />
      {u.twoFactor && <Button appearance="subtle" icon={<ShieldDismiss20Regular />} onClick={() => reset2fa(u)} title={t('2FA zurücksetzen')} aria-label={t('2FA zurücksetzen')} />}
      {lockOn && u.id !== me.id && (u.locked
        ? <Button appearance="subtle" icon={<LockOpen20Regular />} onClick={() => unlock(u)} title={t('Entsperren')} aria-label={t('Entsperren')} />
        : <Button appearance="subtle" icon={<LockClosed20Regular />} onClick={() => lock(u)} title={t('Sperren')} aria-label={t('Sperren')} />)}
      {u.id !== me.id && <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => setRemoveUser(u)} title={t('Löschen')} aria-label={t('Löschen')} />}
    </> } as AdminCol<User>]),
  ]

  const dialogs = <>
      {oidcUser && <OidcUserDialog user={oidcUser} onClose={c => { setOidcUser(null); if (c) load() }} />}
      {removeUser && <DeleteUserDialog user={removeUser} onDelete={() => remove(removeUser)} onClose={() => setRemoveUser(null)}
        onLock={lockOn && !removeUser.locked ? () => lock(removeUser, true) : undefined} />}
      {quotaUser && <QuotaDialog user={quotaUser} defaultMb={status?.defaultFileQuotaMb ?? 0} onSave={mb => saveQuota(quotaUser, mb)} onClose={() => setQuotaUser(null)} />}
  </>
  const content = <>
      {/* Schlüssel als Datei ist für eine Installation zu Hause in Ordnung - nur ein ruhiger Tipp bei der Sicherung */}
      {status?.keyInDataDir && tab === 'backup' && (
        <MessageBar intent="info" layout="multiline" style={{ flexShrink: 0 }}>
          <MessageBarBody>
            {t('Der Hauptschlüssel liegt als Datei master.key im Datenverzeichnis. Das Core-Backup enthält ihn nicht.')}{' '}
            <a href={lang === 'en' ? 'https://github.com/Taloan/dmOffice/blob/master/README.md#data-and-keys' : 'https://github.com/Taloan/dmOffice/blob/master/README.de.md#daten-und-schl%C3%BCssel'} target="_blank" rel="noopener">{t('So geht’s')}</a>
          </MessageBarBody>
        </MessageBar>
      )}
      {status?.oldKeyFile && tab === 'backup' && (
        <MessageBar intent="info" layout="multiline" style={{ flexShrink: 0 }}>
          <MessageBarBody>
            {t('Der Umstieg auf DMOFFICE_MASTER_KEY ist erledigt. Die Datei master.key im Datenverzeichnis wird nur noch gebraucht, um Backups von vor dem Umstieg zurückzuspielen.')}
          </MessageBarBody>
          <MessageBarActions>
            <Button as="a" href="/api/admin/old-master-key" download="master.key">{t('Herunterladen')}</Button>
            <Button onClick={deleteOldKey}>{t('Löschen')}</Button>
          </MessageBarActions>
        </MessageBar>
      )}
      {tab === 'users' && <>
      {ribbon ? <>
        <RibbonActions>
          <RibbonButton primary icon={<PersonAdd20Regular />} label={t('Neuer Benutzer')} onClick={() => setAdding(true)} />
          <RibbonDivider />
          <RibbonButton icon={<Key20Regular />} label={t('Passwort setzen')} disabled={!picked} onClick={() => picked && resetPassword(picked)} />
          <RibbonButton icon={<Edit20Regular />} label={t('Kontingent festlegen')} disabled={!picked} onClick={() => picked && setQuotaUser(picked)} />
          {featureOn('oidc') && <RibbonButton icon={<PersonKey20Regular />} label={t('Anmeldung über OIDC')} disabled={!picked} onClick={() => picked && setOidcUser(picked)} />}
          <RibbonButton icon={<ShieldDismiss20Regular />} label={t('2FA zurücksetzen')} disabled={!picked?.twoFactor} onClick={() => picked && reset2fa(picked)} />
          <RibbonDivider />
          {lockOn && (picked?.locked
            ? <RibbonButton icon={<LockOpen20Regular />} label={t('Entsperren')} disabled={!picked || picked.id === me.id} onClick={() => picked && unlock(picked)} />
            : <RibbonButton icon={<LockClosed20Regular />} label={t('Sperren')} disabled={!picked || picked.id === me.id} onClick={() => picked && lock(picked)} />)}
          <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!picked || picked.id === me.id} onClick={() => picked && setRemoveUser(picked)} />
        </RibbonActions>
        <Text size={200} className={s.hint}>{t('Das Kontingent umfasst alles, was ein Benutzer belegt. Die Spalte „Datenbank“ zeigt nur die Größe seiner Datenbank.')}</Text>
      </> : <div style={{ display: 'flex', gap: 16, alignItems: 'center', flexWrap: 'wrap' }}>
        <Button appearance="primary" icon={<PersonAdd20Regular />} onClick={() => setAdding(true)}>{t('Benutzer anlegen')}</Button>
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Das Kontingent umfasst alles, was ein Benutzer belegt. Die Spalte „Datenbank“ zeigt nur die Größe seiner Datenbank.')}</Text>
      </div>}
      {/* Volle Höhe: die Seite rollt als Ganzes, die Tabelle schrumpft nicht auf wenige Zeilen */}
      <div style={{ flexShrink: 0 }}><AdminTable id="users" cols={userCols} rows={users} rowKey={u => u.id} initialSort={{ col: 'login', desc: false }}
        onRowContextMenu={featureOn('ctxall') ? (e, u) => { if (ribbon) setPickedId(u.id); userMenu.open(e, u) } : undefined}
        onRowClick={ribbon ? u => setPickedId(u.id) : undefined} active={ribbon ? pickedId : undefined} /></div>
      {userMenu.ctx && (() => {
        const u = userMenu.ctx.data
        const check = (on: boolean) => on ? <Checkmark20Regular /> : <span style={{ width: 20 }} />
        return (
          <ContextMenu at={userMenu.ctx.at} onClose={userMenu.close}>
            <MenuItem icon={<Key20Regular />} onClick={() => resetPassword(u)}>{t('Passwort setzen')}</MenuItem>
            <MenuItem icon={<Edit20Regular />} onClick={() => setQuotaUser(u)}>{t('Kontingent festlegen')}</MenuItem>
            {featureOn('oidc') && <MenuItem icon={<Edit20Regular />} onClick={() => setOidcUser(u)}>{t('Anmeldung über OIDC')}</MenuItem>}
            <MenuDivider />
            <MenuItem icon={check(u.isAdmin)} disabled={u.id === me.id} onClick={() => toggleAdmin(u)}>{t('Admin')}</MenuItem>
            <MenuItem icon={check(!u.filesOff)} onClick={() => toggleFiles(u)}>{t('Dateiablage')}</MenuItem>
            <MenuItem icon={check(!!u.aiAllowed && !!status?.aiConfigured)} disabled={!status?.aiConfigured} onClick={() => toggleAi(u)}>{t('KI')}</MenuItem>
            {u.twoFactor && <MenuItem icon={<ShieldDismiss20Regular />} onClick={() => reset2fa(u)}>{t('2FA zurücksetzen')}</MenuItem>}
            {u.id !== me.id && <>
              <MenuDivider />
              <MenuItem icon={<Delete20Regular />} onClick={() => setRemoveUser(u)}>{t('Löschen')}</MenuItem>
            </>}
          </ContextMenu>
        )
      })()}
      {/* Einrichtungshinweise unter der Tabelle, damit sie die Benutzerliste nicht nach unten schieben */}
      <SetupHints onBackup={() => pick('backup')} />
      </>}
      {tab === 'quota' && status && <>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, maxWidth: 640 }}>
          <AdminHeading first>{t('Kontingent (Vorgabe)')}</AdminHeading>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Das Kontingent gilt für alle Benutzer ohne eigenes Kontingent und umfasst den gesamten Speicherplatz, den ein Benutzer belegt. 0 = unbegrenzt.')}</Text>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <SizeInput mb={defaultQuota} onChange={setDefaultQuota} />
            {/* [feature:savemode] */}
            {!quotaDraft && <Button disabled={defaultQuota === status.defaultFileQuotaMb || Number.isNaN(defaultQuota)} onClick={saveDefaultQuota}>{t('Übernehmen')}</Button>}
            {/* [feature:savemode:else] */}
            {/* <Button disabled={defaultQuota === status.defaultFileQuotaMb} onClick={saveDefaultQuota}>{t('Übernehmen')}</Button> */}
            {/* [/feature:savemode] */}
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
      {tab === 'plugins' && featureOn('plugins') && <PluginsAdmin />}
      {tab === 'log' && <LogAdmin />}
      {featureOn('office') && keptPage('office', <OfficeSettings />)}
      {tab === 'updates' && <UpdatePanel />}
      {tab === 'chat' && <ChatRightsAdmin />}
      {tab === 'mail' && <><SpamListsAdmin /><SpamRulesAdmin /></>}
      {keptPage('ai', <AiAdmin />)}
      {tab === 'address' && featureOn('publicurl') && <PublicUrlAdmin />} {/* [feature:publicurl] */}
      {tab === 'security' && <SecurityAdmin />}
      {tab === 'security' && featureOn('clamav') && <VirusAdmin />}
      {tab === 'gatekeeper' && featureOn('gatekeeper') && <GateKeeperAdmin />} {/* [feature:gatekeeper] */}
      {tab === 'sso' && featureOn('oidc') && <OidcAdmin />}
      {status && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Datenverzeichnis: {dir}', { dir: status.dataDir })}</Text>}
      {adding && <NewUser onClose={c => { setAdding(false); if (c) load() }} />}
  </>
  // [feature:adminclean]
  // Kennung adminclean: 14 Seiten in vier Bereichen; je Seite oben Name und Reiter, alle Aktionen im Menüband
  // (Entwurf: sessions/admin-umbau/ENTWURF.md). Frühere Seiten-Kennungen führen auf die neue Seite und ihren Reiter.
  const setupOpen = useSetupOpen()
  const [subs, setSubs] = useState<Record<string, string>>({})
  if (clean) {
    const tabsOf = (page: string): AdminTabDef[] => {
      const x = (key: string, label: string, on = true) => (on ? [{ key, label }] : [])
      switch (page) {
        case 'quota': return [...x('quota', t('Kontingente')), ...x('keep', t('Aufbewahrung'))]
        case 'storage': return [...x('overview', t('Übersicht')), ...x('target', t('Ziel')), ...x('usage', t('Belegung'))]
        case 'backup': return [...x('overview', t('Übersicht')), ...x('core', t('Core-Backup')), ...x('files', t('Files-Backup')), ...x('export', t('Benutzer-Datenexport'))]
        case 'mounts': return [...x('server', t('Server-Mounts')), ...x('users', t('Benutzer-Mounts'))]
        case 'mail': return [
          ...x('spam', t('Spamlisten')), ...x('rules', t('Erkennungsregeln')),
        ]
        case 'apps': return [
          ...x('office', t('Office im Browser'), featureOn('office')),
          ...x('gallery', t('Galerie'), featureOn('lenslib')), // [feature:lenslib]
        ]
        case 'gatekeeper': return [...x('lists', t('Filterlisten')), ...x('settings', t('Einstellungen'))]
        case 'ext': return [...x('modules', t('Module')), ...x('plugins', t('Plugins'), featureOn('plugins'))]
        case 'security': return [...x('overview', t('Übersicht')), ...x('blocks', t('Sperren und Erlaubt-Liste')), ...x('proxies', t('Reverse-Proxys')), ...x('events', t('Ereignisse')),
          ...x('virus', t('Virenprüfung'), featureOn('clamav')), ...x('abuse', 'AbuseIPDB'), ...x('settings', t('Einstellungen'))]
        case 'server': return [
          ...x('address', t('Öffentliche Adresse'), featureOn('publicurl')), // [feature:publicurl]
          ...x('updates', t('Updates')), ...x('db', t('Datenbanken')), ...x('log', t('Protokoll'))]
        default: return []
      }
    }
    const apps = tabsOf('apps')
    const areas: { key: string; label: string; pages: { key: string; label: string; dot?: string }[] }[] = [
      { key: 'people', label: t('Benutzer'), pages: [{ key: 'users', label: t('Benutzer') }, ...(featureOn('oidc') ? [{ key: 'sso', label: t('Anmeldung (OIDC)') }] : [])] },
      { key: 'space', label: t('Speicher'), pages: [{ key: 'quota', label: t('Kontingente') }, { key: 'storage', label: t('Storage Extender') },
        { key: 'backup', label: t('Backup'), dot: setupOpen.some(k => k === 'backup' || k === 'masterkey') || status?.oldKeyFile ? t('Einrichtung offen') : undefined }, { key: 'mounts', label: t('Mounts') }] },
      { key: 'services', label: t('Dienste'), pages: [{ key: 'mail', label: t('E-Mail') }, { key: 'chat', label: t('Chat') }, { key: 'ai', label: t('KI') },
        ...(apps.length ? [{ key: 'apps', label: t('Apps') }] : []),
        ...(featureOn('gatekeeper') ? [{ key: 'gatekeeper', label: 'GateKeeper' }] : []), // [feature:gatekeeper]
      ] },
      { key: 'system', label: t('System'), pages: [{ key: 'ext', label: t('Erweiterungen') },
        { key: 'security', label: t('Sicherheit'), dot: setupOpen.includes('twofactor') ? t('Einrichtung offen') : undefined }, { key: 'server', label: t('Server') }] },
    ]
    const FORMER: Record<string, [string, string?]> = {
      files: ['quota', 'quota'], plainbackup: ['backup', 'export'], serverfolders: ['mounts', 'server'], external: ['mounts', 'users'],
      office: ['apps', 'office'], coach: ['apps', 'coach'], banking: ['apps', 'finance'], plugins: ['ext', 'plugins'], modules: ['ext', 'modules'],
      address: ['server', 'address'], log: ['server', 'log'], updates: ['server', 'updates'], db: ['server', 'db'],
    }
    const former = FORMER[tab]
    const [page, forced] = former ?? [areas.some(a => a.pages.some(p => p.key === tab)) ? tab : 'users']
    const area = areas.find(a => a.pages.some(p => p.key === page))
    const title = area?.pages.find(p => p.key === page)?.label ?? t('Benutzer')
    const tabs = tabsOf(page)
    const stored = () => { try { return localStorage.getItem('dmoffice.admin.sub.' + page) } catch { return null } }
    const wanted = forced ?? subs[page] ?? stored()
    const sub = tabs.find(x => x.key === wanted)?.key ?? tabs[0]?.key ?? ''
    const pickSub = (v: string) => {
      const go = () => {
        setSubs(o => ({ ...o, [page]: v }))
        try { localStorage.setItem('dmoffice.admin.sub.' + page, v) } catch { /* egal */ }
        if (tab !== page) show(page)
      }
      if (drafts?.dirty && v !== sub) { void drafts.leave().then(ok => { if (ok) go() }); return }
      go()
    }
    const goPage = (key: string) => {
      if (key === page) return
      if (drafts?.dirty) { void drafts.leave().then(ok => { if (ok) show(key) }); return }
      show(key)
    }
    // Reiter mit nur einem Abschnitt: dessen erste Zwischenüberschrift entfällt; Tabellen nutzen die volle Breite
    const several = ['quota:keep', 'mail:spam', 'apps:finance', 'apps:helpdesk', 'apps:gallery', 'backup:core', 'security:blocks'].includes(page + ':' + sub)
    const wide = page === 'users' || page === 'mounts' || ['apps:gallery', 'security:blocks', 'security:proxies', 'security:events', 'server:db', 'server:log', 'server:updates', 'gatekeeper:lists',
      'mail:own', 'mail:rules', 'backup:export'].includes(page + ':' + sub) || sub.startsWith('p:')
    const muted = { color: tokens.colorNeutralForeground3 }
    const body = <>
      {page === 'users' && <>
        <RibbonActions>
          <RibbonButton primary icon={<PersonAdd20Regular />} label={t('Neuer Benutzer')} onClick={() => setAdding(true)} />
          <RibbonDivider />
          <RibbonButton icon={<Key20Regular />} label={t('Passwort setzen')} disabled={!picked} onClick={() => picked && resetPassword(picked)} />
          {featureOn('oidc') && <RibbonButton icon={<PersonKey20Regular />} label={t('Anmeldung über OIDC')} disabled={!picked} onClick={() => picked && setOidcUser(picked)} />}
          <RibbonButton icon={<ShieldDismiss20Regular />} label={t('2FA zurücksetzen')} disabled={!picked?.twoFactor} onClick={() => picked && reset2fa(picked)} />
          <RibbonDivider />
          {lockOn && (picked?.locked
            ? <RibbonButton icon={<LockOpen20Regular />} label={t('Entsperren')} disabled={!picked || picked.id === me.id} onClick={() => picked && unlock(picked)} />
            : <RibbonButton icon={<LockClosed20Regular />} label={t('Sperren')} disabled={!picked || picked.id === me.id} onClick={() => picked && lock(picked)} />)}
          <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!picked || picked.id === me.id} onClick={() => picked && setRemoveUser(picked)} />
        </RibbonActions>
        <UsersAdmin users={users} meId={me.id} picked={pickedId} onPick={setPickedId} defaultQuotaMb={status?.defaultFileQuotaMb ?? 0} aiConfigured={!!status?.aiConfigured}
          onToggleAdmin={toggleAdmin} onToggleFiles={toggleFiles} onToggleAi={toggleAi}
          onQuota={async (u, mb) => { await saveQuota(u, mb); return true }} />
      </>}
      {page === 'sso' && featureOn('oidc') && <OidcAdmin />}
      {page === 'quota' && sub === 'quota' && status && <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
        <Text size={200} style={muted}>{t('Das Kontingent gilt für alle Benutzer ohne eigenes Kontingent und umfasst den gesamten Speicherplatz, den ein Benutzer belegt. 0 = unbegrenzt.')}</Text>
        <Field label={t('Kontingent (Vorgabe)')}><SizeInput mb={defaultQuota} onChange={setDefaultQuota} /></Field>
        {!quotaDraft && <RibbonActions><RibbonButton primary icon={<Checkmark20Regular />} label={t('Übernehmen')} disabled={defaultQuota === status.defaultFileQuotaMb || Number.isNaN(defaultQuota)} onClick={saveDefaultQuota} /></RibbonActions>}
        <Text size={200} style={muted}>{t('Ein eigenes Kontingent je Benutzer steht unter „Benutzer“ im Reiter „Speicher“.')}</Text>
      </div>}
      {page === 'quota' && sub === 'keep' && status && <FileLimitsSettings initial={status.fileLimits} />}
      {page === 'storage' && <StorageAdmin onOpen={k => goPage(k)} />}
      {page === 'backup' && sub === 'overview' && <>
        <SetupHints only={['backup', 'masterkey']} />
        {status?.keyInDataDir && <MessageBar intent="info" layout="multiline" style={{ flexShrink: 0 }}><MessageBarBody>
          {t('Der Hauptschlüssel liegt als Datei master.key im Datenverzeichnis. Das Core-Backup enthält ihn nicht.')}{' '}
          <a href={lang === 'en' ? 'https://github.com/Taloan/dmOffice/blob/master/README.md#data-and-keys' : 'https://github.com/Taloan/dmOffice/blob/master/README.de.md#daten-und-schl%C3%BCssel'} target="_blank" rel="noopener">{t('So gehst du damit um')}</a>
        </MessageBarBody></MessageBar>}
        {status?.oldKeyFile && <>
          <MessageBar intent="info" layout="multiline" style={{ flexShrink: 0 }}><MessageBarBody>
            {t('Der Umstieg auf DMOFFICE_MASTER_KEY ist erledigt. Die Datei master.key im Datenverzeichnis wird nur noch gebraucht, um Backups von vor dem Umstieg zurückzuspielen.')}
          </MessageBarBody></MessageBar>
          <RibbonActions>
            <RibbonButton icon={<ArrowDownload20Regular />} label={t('master.key herunterladen')} onClick={() => { const a = document.createElement('a'); a.href = '/api/admin/old-master-key'; a.download = 'master.key'; a.click() }} />
            <RibbonButton icon={<Delete20Regular />} label={t('master.key löschen')} onClick={deleteOldKey} />
          </RibbonActions>
        </>}
      </>}
      {page === 'backup' && sub !== 'export' && <BackupStrategy />}
      {page === 'backup' && sub === 'export' && <PlainBackupAdmin />}
      {page === 'mounts' && <ExternalAdmin key={sub} part={sub === 'users' ? 'users' : 'server'} />}
      {page === 'mail' && sub === 'spam' && <SpamListsAdmin />}
      {page === 'mail' && sub === 'rules' && <SpamRulesAdmin />}
      {page === 'chat' && <ChatRightsAdmin />}
      {page === 'ai' && <AiAdmin />}
      {page === 'apps' && sub === 'office' && featureOn('office') && <OfficeSettings />}
      {/* [feature:lenslib] */}
      {page === 'apps' && sub === 'gallery' && featureOn('lenslib') && <Suspense fallback={null}>
        <AdminHeading first>{t('Objektivprofile')}</AdminHeading>
        <LensAdmin />
      </Suspense>}
      {/* [/feature:lenslib] */}
      {page === 'gatekeeper' && featureOn('gatekeeper') && <GateKeeperAdmin />} {/* [feature:gatekeeper] */}
      {page === 'ext' && sub === 'modules' && <ModulesAdmin />}
      {page === 'ext' && sub === 'plugins' && featureOn('plugins') && <PluginsAdmin />}
      {page === 'security' && sub === 'overview' && <SetupHints only={['twofactor']} />}
      {page === 'security' && sub !== 'virus' && <SecurityAdmin />}
      {page === 'security' && sub === 'virus' && featureOn('clamav') && <VirusAdmin />}
      {page === 'server' && sub === 'address' && featureOn('publicurl') && <PublicUrlAdmin />} {/* [feature:publicurl] */}
      {page === 'server' && sub === 'updates' && <UpdatePanel />}
      {page === 'server' && sub === 'db' && <>
        <DbHealthAdmin />
        {status && <Text size={200} style={muted}>{t('Datenverzeichnis: {dir}', { dir: status.dataDir })}</Text>}
      </>}
      {page === 'server' && sub === 'log' && <LogAdmin />}
      {adding && <NewUser onClose={c => { setAdding(false); if (c) load() }} />}
    </>
    return (
      <div className={s.frame}>
        <RibbonHeader area="admin" onMenu={toggleSide}
          start={<>
            {drafts && <AdminSaveButtons host={drafts} />}
            <div ref={setSlot} style={{ display: 'contents' }} />
            <RibbonButton icon={<ArrowSync20Regular />} label={t('Aktualisieren')} onClick={async () => { if (drafts && !(await drafts.leave())) return; setRev(r => r + 1); load(); api.get('/api/admin/status').then(setStatus).catch(() => {}) }} />
          </>}
          view={<></>} />
        <div className={s.split}>
          <SidePane as="aside" className={mergeClasses(s.side, nv.pane)} open={mobile ? drawer : true} onClose={closeSide} width={{ key: 'admin.side', def: 240 }}>
            <Text size={500} weight="semibold" style={{ padding: '4px 10px 4px' }}>{t('Verwaltung')}</Text>
            {areas.map(a => <Fragment key={a.key}>
              <div className={nv.section}>{a.label}</div>
              {a.pages.map(p => <div key={p.key} className={mergeClasses(nv.row, page === p.key && nv.active)} {...pressable(() => goPage(p.key))}
                onClick={() => { goPage(p.key); if (mobile) closeSide() }}>
                <span className={nv.name}>{p.label}</span>
                {p.dot && <span title={p.dot} aria-label={p.dot} style={{ width: 8, height: 8, borderRadius: 4, flexShrink: 0, background: tokens.colorPaletteMarigoldBorderActive }} />}
              </div>)}
            </Fragment>)}
          </SidePane>
          <RibbonSlot.Provider value={slot}><AdminDraftProvider value={drafts}>
            <div key={rev} className={s.page}>
              <AdminPageHead title={title} tabs={tabs} value={sub} onChange={pickSub} />
              <AdminPartCtx.Provider value={{ part: sub || page, solo: !several }}>
                <div key={page + ':' + sub} style={{ display: 'flex', flexDirection: 'column', gap: 12, minWidth: 0, ...(wide ? {} : { maxWidth: ADMIN_FORM_WIDTH, width: '100%' }) }}>{body}</div>
              </AdminPartCtx.Provider>
            </div>
          </AdminDraftProvider></RibbonSlot.Provider>
        </div>
        {dialogs}
      </div>
    )
  }
  // [/feature:adminclean]
  if (ribbon) return (
    <div className={s.frame}>
      <RibbonHeader area="admin" onMenu={toggleSide}
        start={<>
          {/* Vorn die Knöpfe der Seite, dahinter Aktualisieren; die Seiten stehen in der Leiste links */}
          {drafts && <AdminSaveButtons host={drafts} />} {/* [feature:savemode] */}
          <div ref={setSlot} style={{ display: 'contents' }} />
          {/* [feature:savemode] */}
          <RibbonButton icon={<ArrowSync20Regular />} label={t('Aktualisieren')} onClick={async () => { if (drafts && !(await drafts.leave())) return; setRev(r => r + 1); load(); api.get('/api/admin/status').then(setStatus).catch(() => {}) }} />
          {/* [feature:savemode:else] */}
          {/* <RibbonButton icon={<ArrowSync20Regular />} label={t('Aktualisieren')} onClick={() => { setRev(r => r + 1); load(); api.get('/api/admin/status').then(setStatus).catch(() => {}) }} /> */}
          {/* [/feature:savemode] */}
        </>}
        view={<></>} />
      <div className={s.split}>
        <SidePane as="aside" className={mergeClasses(s.side, nv.pane)} open={mobile ? drawer : true} onClose={closeSide} width={{ key: 'admin.side', def: 240 }}>
          <Text size={500} weight="semibold" style={{ padding: '4px 10px 4px' }}>{t('Verwaltung')}</Text>
          {groups.map((g) => <Fragment key={g.key}>
            <div className={nv.section}>{g.label}</div>
            {g.pages.map(([k, label]) => <div key={k} className={mergeClasses(nv.row, tab === k && nv.active)} {...pressable(() => pick(k))}
              onClick={() => { pick(k); if (mobile) closeSide() }}><span className={nv.name}>{label}</span></div>)}
          </Fragment>)}
        </SidePane>
        {/* [feature:savemode] */}
        <RibbonSlot.Provider value={slot}><AdminDraftProvider value={drafts}><div key={rev} className={s.page}>{content}</div></AdminDraftProvider></RibbonSlot.Provider>
        {/* [feature:savemode:else] */}
        {/* <RibbonSlot.Provider value={slot}><div key={rev} className={s.page}>{content}</div></RibbonSlot.Provider> */}
        {/* [/feature:savemode] */}
      </div>
      {dialogs}
    </div>
  )
  return (
    <div className={s.root}>
      <nav className={s.nav} aria-label={t('Verwaltung')}>
        <Text size={500} weight="semibold" style={{ padding: '0 10px 4px' }}>{t('Verwaltung')}</Text>
        <div style={{ height: 8 }} />
        {groups.map(g => <button key={g.key} className={mergeClasses(s.navItem, group.key === g.key && s.navOn)} aria-current={group.key === g.key ? 'page' : undefined}
          onClick={() => pick(lastOf(g))}>{g.icon}{g.label}</button>)}
      </nav>
      <div className={s.mobileNav}>
        <Select value={tab} onChange={(_, d) => pick(d.value)} aria-label={t('Seite der Verwaltung')}>
          {groups.map(g => <optgroup key={g.label} label={g.label}>{g.pages.map(([k, label]) => <option key={k} value={k}>{label}</option>)}</optgroup>)}
        </Select>
      </div>
      <div className={s.page}>
      <Text size={600} weight="semibold">{group.label}</Text>
      {/* Seiten des Bereichs als Reiter (am Rechner; auf dem Handy wählt die Liste oben die Seite) */}
      {group.pages.length > 1 && <TabList className={s.tabs} selectedValue={tab} onTabSelect={(_, d) => pick(d.value as string)} style={{ marginTop: -8, flexWrap: 'wrap' }}>
        {group.pages.map(([k, label]) => <Tab key={k} value={k}>{label}</Tab>)}
      </TabList>}
      {content}
      </div>
      {dialogs}
    </div>
  )
}

interface Limits { versionsDays: number; versionsMb: number; trashDays: number; trashMb: number; linkMaxDownloads: number; linkDays: number; zipMb: number
  linkUploadMb: number // [feature:uploadlimit]
}

/** Hoechstwerte, ueber die Benutzer unter Einstellungen -> Dateien nicht hinauskommen (0 = keine Vorgabe). */
function FileLimitsSettings({ initial }: { initial?: Limits }) {
  const { fail, notify } = useApp()
  const [l, setL] = useState<Limits>(initial ?? {
    versionsDays: 0, versionsMb: 0, trashDays: 0, trashMb: 0, linkMaxDownloads: 0, linkDays: 0, zipMb: 5120,
    linkUploadMb: 0, // [feature:uploadlimit]
  })
  const save = async (p: Partial<Limits>) => {
    const next = { ...l, ...p }
    setL(next)
    try { setL(await api.put<Limits>('/api/admin/file-limits', next)); notify(t('Vorgabe gespeichert')) } catch (e) { fail(e) }
  }
  // [feature:savemode]
  const draft = useAdminDraft(l, async v => { try { setL(await api.put<Limits>('/api/admin/file-limits', v)); notify(t('Vorgabe gespeichert')); return true } catch (e) { fail(e); return false } })
  const shown = draft.on ? draft.value : l
  const change = (p: Partial<Limits>) => draft.on ? draft.set({ ...draft.value, ...p }) : save(p)
  // [feature:savemode:else]
  // const shown = l
  // const change = save
  // [/feature:savemode]
  const none = t('keine Vorgabe')
  const pickField = (label: string, key: keyof Limits, steps: number[], text: (v: number) => string) => (
    <Field label={label}>
      <Select value={String(shown[key])} onChange={(_, d) => change({ [key]: Number(d.value) } as Partial<Limits>)}>
        {[0, ...steps].concat(steps.includes(shown[key]) || shown[key] === 0 ? [] : [shown[key]]).map(v => <option key={v} value={String(v)}>{v === 0 ? none : text(v)}</option>)}
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
        {/* [feature:uploadlimit] */}
        {featureOn('uploadlimit') && pickField(t('Hochladen höchstens je Link'), 'linkUploadMb', [100, 500, 1024, 5120, 10240, 51200], mbText)}
        {/* [/feature:uploadlimit] */}
      </div>
      {/* [feature:uploadlimit] */}
      {featureOn('uploadlimit') && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('„Hochladen höchstens je Link“ gilt für alles, was über einen einzelnen Hochlade-Link zusammen hochgeladen wird, sofort für alle Links. Das Kontingent des Besitzers gilt zusätzlich.')}</Text>}
      {/* [/feature:uploadlimit] */}
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
  // [feature:adminclean]
  const tabbed = useAdminPart().part != null
  // [feature:adminclean:else]
  // const tabbed = false
  // [/feature:adminclean]
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
      <AdminHeading first={tabbed}>{t('Office im Browser')}</AdminHeading>
      <Text size={200}>
        {t('Word-, Excel- und PowerPoint-Dateien (auch ODF) in der Dateiablage bearbeiten, mit einem eigenen Office-Server: Collabora Online/CODE oder ONLYOFFICE Docs.')}{' '}
        <a href="https://github.com/Taloan/dmOffice/blob/master/docs/office.md" target="_blank" rel="noreferrer">{t('Anleitung (Unraid, Docker)')}</a>
      </Text>
      <Field label={t('Adresse des Office-Servers')} hint={t('Muss für den Browser und für dmOffice erreichbar sein. Leer = aus.')}>
        <Input value={url} placeholder="https://office.example.org" onChange={(_, d) => setUrl(d.value)} />
      </Field>
      {mixed && (
        <MessageBar intent="warning"><MessageBarBody>
          {t('dmOffice läuft über https, der Office-Server über http. Der Browser blockiert den Editor dann als gemischten Inhalt.')}
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
      <Field label={t('Adresse von dmOffice für den Office-Server (optional)')} hint={t('Nur nötig, wenn der Office-Server dmOffice unter einer anderen Adresse erreicht als der Browser. Bei Collabora muss sie in der aliasgroup1 stehen.')}>
        <Input value={wopiBase} placeholder={location.origin} onChange={(_, d) => setWopiBase(d.value)} />
      </Field>
      </>}
{/* [feature:adminclean] */}
      {tabbed && <RibbonActions>
        <RibbonButton primary icon={<PlugConnected20Regular />} disabled={busy || !url.trim()} label={t('Prüfen und speichern')} onClick={() => save()} />
        {error && url.trim() && <RibbonButton icon={<Save20Regular />} disabled={busy} label={t('Trotzdem speichern')} onClick={() => save(true)} />}
        {st?.jwt && <RibbonButton icon={<Delete20Regular />} disabled={busy} label={t('Geheimnis löschen')} onClick={() => save(true, false, true)} />}
        {st?.url && <RibbonButton icon={<Power20Regular />} disabled={busy} label={t('Abschalten')} onClick={() => save(false, true)} />}
      </RibbonActions>}
      {/* [/feature:adminclean] */}
      {!tabbed && <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <Button appearance="primary" disabled={busy || !url.trim()} onClick={() => save()}>{t('Prüfen und speichern')}</Button>
        {error && url.trim() && <Button disabled={busy} onClick={() => save(true)}>{t('Trotzdem speichern')}</Button>}
        {st?.jwt && <Button disabled={busy} onClick={() => save(true, false, true)}>{t('Geheimnis löschen')}</Button>}
        {st?.url && <Button disabled={busy} onClick={() => save(false, true)}>{t('Abschalten')}</Button>}
      </div>}
      {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
      {!error && st?.url && (st.ok
        ? <MessageBar intent="success"><MessageBarBody>
            {t('Verbunden mit {product}. Bearbeiten: {types}.', { product, types: (st.edit ?? []).join(', ') || '–' })}{' '}
            {st.native ? (st.jwt ? t('Anfragen werden mit dem JWT-Geheimnis signiert und geprüft.') : t('Ohne JWT-Geheimnis – jeder, der den Document Server erreicht, kann ihn nutzen.'))
              : st.proof ? t('Anfragen werden mit Proof-Keys geprüft.') : t('Der Office-Server veröffentlicht keine Proof-Keys – geprüft wird nur das Token.')}
          </MessageBarBody></MessageBar>
        : <MessageBar intent="warning"><MessageBarBody>{st.error ? tError(st.error) : t('Der Office-Server ist gerade nicht erreichbar.')}</MessageBarBody></MessageBar>)}
      {/* [feature:officejwthint] */}
      {/* ONLYOFFICE ohne gemeinsames Geheimnis: fester Platz unter den Einstellungen, bleibt stehen, bis eines gespeichert ist */}
      {featureOn('officejwthint') && st?.url && st.ok && st.native && !st.jwt && (
        <MessageBar intent="warning" layout="multiline" style={{ flexShrink: 0 }}><MessageBarBody>
          {t('Mit einem JWT-Geheimnis nimmt ONLYOFFICE nur Anfragen von dmOffice an. Trag dafür unter „Fortgeschritten“ das JWT_SECRET des Document Servers ein.')}
          </MessageBarBody>
          {!advanced && <MessageBarActions><Button size="small" onClick={() => setAdvanced(true)}>{t('JWT-Geheimnis eintragen')}</Button></MessageBarActions>}
        </MessageBar>
      )}
      {/* [/feature:officejwthint] */}
    </div>
  )
}

/** Kontingent fuer die Dateien eines Benutzers: Vorgabe der Verwaltung, unbegrenzt oder eine eigene Grenze. */
/** Benutzer löschen: erst nach Eintippen des Anmeldenamens. */
function DeleteUserDialog({ user, onDelete, onClose, onLock }: { user: User; onDelete: () => Promise<void>; onClose: () => void
  /** Kennung userlock: statt zu löschen sperren */
  onLock?: () => Promise<void> }) {
  const [typed, setTyped] = useState('')
  const [busy, setBusy] = useState(false)
  const ok = typed.trim() === user.login
  const go = async () => { if (!ok || busy) return; setBusy(true); try { await onDelete() } finally { setBusy(false) } }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <DialogBody>
          <DialogTitle>{t('Benutzer {login} löschen', { login: user.login })}</DialogTitle>
          <DialogContent>
            <Field label={t('Alles von {login} wird gelöscht. Zum Bestätigen den Anmeldenamen eingeben:', { login: user.login })}>
              <Input value={typed} autoFocus autoComplete="off" onChange={(_, d) => setTyped(d.value)} onKeyDown={e => { if (e.key === 'Enter') go() }} />
            </Field>
            {onLock && <Text size={200} block style={{ marginTop: 12, color: tokens.colorNeutralForeground3 }}>
              {t('Stattdessen sperren: {login} kann sich nicht mehr anmelden, die Daten bleiben und das Konto lässt sich später entsperren.', { login: user.login })}
            </Text>}
          </DialogContent>
          <DialogActions>
            {onLock && <Button icon={<LockClosed20Regular />} disabled={busy} style={{ marginRight: 'auto' }}
              onClick={async () => { setBusy(true); try { await onLock() } finally { setBusy(false) } }}>{t('Stattdessen sperren')}</Button>}
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!ok || busy} onClick={go}>{t('Endgültig löschen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

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
                <MessageBar intent="warning"><MessageBarBody>{t('{login} belegt schon mehr. Vorhandene Daten bleiben erhalten. Neue Mails werden erst wieder abgerufen und neue Dateien erst wieder gespeichert, wenn Platz frei ist. Frühere Versionen und der Papierkorb werden bei Bedarf zuerst gelöscht.', { login: user.login })}</MessageBarBody></MessageBar>
              )}
              {effectiveMb > 0 && <ProgressBar value={share} thickness="medium" color={share > 0.9 ? 'error' : share > 0.75 ? 'warning' : 'brand'} />}
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={mode === 'own' && !(mb > 0)} onClick={() => onSave(mode === 'default' ? null : mode === 'unlimited' ? 0 : mb)}>{t('Speichern')}</Button>
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
  const [busy, once] = useOnce()
  const save = () => once(async () => {
    try { await api.post('/api/admin/users', { login, displayName, password, isAdmin }); onClose(true) } catch (e) { fail(e) }
  })
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
            <Button appearance="primary" disabled={busy} onClick={save}>{t('Anlegen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
