import {
  Badge, Field, Input, ProgressBar, Radio, RadioGroup, Select, Switch, Tab, TabList, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { Search20Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useState } from 'react'
import { api, featureOn, fmtSize } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { pressable } from '../shell/nav'
import { useAdminDraft } from './draft'
import { ADMIN_FORM_WIDTH } from './AdminPage'
import { SizeInput, quotaText, type AdminUser } from './AdminView'

// Kennung adminclean: Benutzer als Liste mit Detailbereich (wie Einstellungen → Catchall). Links Name, Anmeldename, Rolle und
// Kontingent, rechts der gewählte Benutzer mit Reitern. Die Aktionen stehen im Menüband der Verwaltung (AdminView).

const useStyles = makeStyles({
  md: { display: 'flex', gap: '20px', alignItems: 'flex-start', flexWrap: 'wrap' },
  left: { flex: '1 1 300px', minWidth: '260px', maxWidth: '380px', display: 'flex', flexDirection: 'column', gap: '8px' },
  right: { flex: '1 1 460px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: `${ADMIN_FORM_WIDTH}px` },
  bar: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  list: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, overflow: 'hidden' },
  li: {
    display: 'flex', flexDirection: 'column', gap: '4px', padding: '8px 10px', cursor: 'pointer', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': { borderBottom: 'none' }, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  sel: { backgroundColor: tokens.colorNeutralBackground1Selected, boxShadow: `inset 3px 0 0 ${tokens.colorBrandStroke1}` },
  line: { display: 'flex', alignItems: 'center', gap: '6px', minWidth: 0 },
  name: { fontWeight: tokens.fontWeightSemibold, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  small: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  grow: { flex: 1 },
  facts: { display: 'grid', gridTemplateColumns: 'minmax(140px, max-content) 1fr', columnGap: '20px', rowGap: '10px', alignItems: 'center' },
  label: { color: tokens.colorNeutralForeground3 },
  title: { display: 'flex', alignItems: 'baseline', gap: '10px', flexWrap: 'wrap' },
})

export interface UsersAdminProps {
  users: AdminUser[]
  meId: string
  picked: string | null
  onPick: (id: string) => void
  defaultQuotaMb: number
  aiConfigured: boolean
  onToggleAdmin: (u: AdminUser) => void
  onToggleFiles: (u: AdminUser) => void
  onToggleAi: (u: AdminUser) => void
  onMailOwn?: (u: AdminUser, v: string) => void
  /** mb: null = Vorgabe, 0 = unbegrenzt; false = nicht gespeichert */
  onQuota: (u: AdminUser, mb: number | null) => Promise<boolean>
}

type Filter = 'all' | 'admins' | 'locked' | 'no2fa'

export function UsersAdmin(p: UsersAdminProps) {
  const s = useStyles()
  const [query, setQuery] = useState('')
  const [filter, setFilter] = useState<Filter>('all')
  const q = query.trim().toLowerCase()
  const shown = useMemo(() => p.users
    .filter(u => filter === 'all' || (filter === 'admins' && u.isAdmin) || (filter === 'locked' && u.locked) || (filter === 'no2fa' && !u.twoFactor))
    .filter(u => !q || u.login.toLowerCase().includes(q) || (u.displayName ?? '').toLowerCase().includes(q))
    .sort((a, b) => (a.displayName || a.login).localeCompare(b.displayName || b.login, locale)), [p.users, filter, q])
  const user = p.users.find(u => u.id === p.picked) ?? null
  // Ohne Auswahl: der erste Benutzer der Liste
  useEffect(() => { if (!user && shown.length) p.onPick(shown[0].id) }, [user, shown]) // eslint-disable-line react-hooks/exhaustive-deps
  return <div className={s.md}>
    <div className={s.left}>
      <div className={s.bar}>
        <Input className={s.grow} contentBefore={<Search20Regular />} placeholder={t('Suchen')} value={query} onChange={(_, d) => setQuery(d.value)} aria-label={t('Benutzer suchen')} />
        <Select value={filter} onChange={(_, d) => setFilter(d.value as Filter)} aria-label={t('Filter')}>
          <option value="all">{t('Alle')}</option>
          <option value="admins">{t('Admins')}</option>
          {featureOn('userlock') && <option value="locked">{t('gesperrt')}</option>}
          <option value="no2fa">{t('ohne 2FA')}</option>
        </Select>
      </div>
      <div className={s.list} role="listbox" aria-label={t('Benutzer')}>
        {shown.map(u => <div key={u.id} {...pressable(() => p.onPick(u.id))} onClick={() => p.onPick(u.id)} role="option" aria-selected={u.id === p.picked} className={mergeClasses(s.li, u.id === p.picked && s.sel)}>
          <div className={s.line}>
            <span className={s.name}>{u.displayName || u.login}</span>
            <span className={s.grow} />
            {u.locked && <Badge size="small" color="danger" appearance="tint">{t('gesperrt')}</Badge>}
            {u.isAdmin && <Badge size="small" appearance="tint" color="brand">{t('Admin')}</Badge>}
          </div>
          <div className={s.line}>
            <span className={mergeClasses(s.small, s.grow)}>{u.login}</span>
            <span className={s.small}>{u.files != null ? fmtSize(u.files) : '–'} / {u.fileQuota > 0 ? fmtSize(u.fileQuota) : '∞'}</span>
          </div>
          {u.fileQuota > 0 && u.files != null && <QuotaBar used={u.files} quota={u.fileQuota} />}
        </div>)}
        {shown.length === 0 && <Text className={s.muted} style={{ padding: 10, display: 'block' }}>{t('Keine passenden Benutzer.')}</Text>}
      </div>
      <Text size={200} className={s.muted}>{t('{n} von {all} Benutzern', { n: shown.length, all: p.users.length })}</Text>
    </div>
    <div className={s.right}>
      {user && <UserDetail key={user.id} {...p} user={user} />}
    </div>
  </div>
}

function QuotaBar({ used, quota }: { used: number; quota: number }) {
  const share = Math.min(1, used / quota)
  return <ProgressBar value={share} thickness="medium" color={share > 0.9 ? 'error' : share > 0.75 ? 'warning' : 'brand'} />
}

type DetailTab = 'general' | 'storage' | 'mail' | 'apps'

function UserDetail(p: UsersAdminProps & { user: AdminUser }) {
  const s = useStyles()
  const u = p.user
  const [tab, setTab] = useState<DetailTab>(() => { try { return (localStorage.getItem('dmoffice.admin.usertab') as DetailTab) || 'general' } catch { return 'general' } })
  const pick = (v: DetailTab) => { setTab(v); try { localStorage.setItem('dmoffice.admin.usertab', v) } catch { /* egal */ } }
  const mail = featureOn('mailserver') || featureOn('mailownaccounts')
  const shownTab = tab === 'mail' && !mail ? 'general' : tab
  return <>
    <div className={s.title}>
      <Text as="h3" size={500} weight="semibold" style={{ margin: 0 }}>{u.displayName || u.login}</Text>
      <Text className={s.muted}>{u.login}</Text>
    </div>
    <TabList selectedValue={shownTab} onTabSelect={(_, d) => pick(d.value as DetailTab)} style={{ marginLeft: -10 }}>
      <Tab value="general">{t('Allgemein')}</Tab>
      <Tab value="storage">{t('Speicher')}</Tab>
      {mail && <Tab value="mail">{t('E-Mail')}</Tab>}
      <Tab value="apps">{t('Apps')}</Tab>
    </TabList>
    {shownTab === 'general' && <General {...p} />}
    {shownTab === 'storage' && <Storage {...p} />}
    {shownTab === 'mail' && <Mail {...p} />}
    {shownTab === 'apps' && <Apps {...p} />}
  </>
}

function General({ user: u, meId, onToggleAdmin }: UsersAdminProps & { user: AdminUser }) {
  const s = useStyles()
  const oidc = u.oidcLinked ? t('verknüpft') : u.oidcInvite ? t('eingeladen') : u.oidcExpected ? t('vorgemerkt: {name}', { name: u.oidcExpected }) : t('nicht verknüpft')
  return <div className={s.facts}>
    <span className={s.label}>{t('Name')}</span><span>{u.displayName || '–'}</span>
    <span className={s.label}>{t('Anmeldename')}</span><span>{u.login}</span>
    <span className={s.label}>{t('Rolle')}</span>
    <Switch checked={u.isAdmin} disabled={u.id === meId} label={u.isAdmin ? t('Admin') : t('Benutzer')} onChange={() => onToggleAdmin(u)} style={{ marginLeft: -8 }} />
    <span className={s.label}>{t('Passwort')}</span><span>{u.noOwnPassword ? t('kein eigenes Passwort') : t('eigenes Passwort')}</span>
    {featureOn('oidc') && <><span className={s.label}>OIDC</span><span>{oidc}</span></>}
    <span className={s.label}>{t('Zwei-Faktor-Anmeldung')}</span>
    <span>{u.twoFactor ? <Badge color="success">{t('an')}</Badge> : <Badge color="informative" appearance="outline">{t('aus')}</Badge>}</span>
    {featureOn('userlock') && <><span className={s.label}>{t('Gesperrt')}</span>
      <span>{u.locked ? <Badge color="danger" appearance="tint">{t('gesperrt')}</Badge> : t('nein')}</span></>}
    <span className={s.label}>{t('Angelegt')}</span><span>{new Date(u.created).toLocaleDateString(locale)}</span>
  </div>
}

type QuotaMode = 'default' | 'unlimited' | 'own'

function Storage({ user: u, defaultQuotaMb, onToggleFiles, onQuota }: UsersAdminProps & { user: AdminUser }) {
  const s = useStyles()
  // Kontingent als Entwurf: „Speichern“ im Menüband
  const draft = useAdminDraft<number | null>(u.fileQuotaMb, async mb => {
    if (mb != null && (Number.isNaN(mb) || mb < 0)) return false
    return onQuota(u, mb)
  })
  const mb = draft.value
  const mode: QuotaMode = mb == null ? 'default' : mb === 0 ? 'unlimited' : 'own'
  const [ownMb, setOwnMb] = useState(u.fileQuotaMb && u.fileQuotaMb > 0 ? u.fileQuotaMb : defaultQuotaMb > 0 ? defaultQuotaMb : 10 * 1024)
  const used = u.files ?? 0
  const effective = mode === 'default' ? defaultQuotaMb : mode === 'unlimited' ? 0 : ownMb
  return <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
    <Field label={t('Kontingent')} hint={t('Umfasst alles, was der Benutzer belegt.')}>
      <RadioGroup value={mode} onChange={(_, d) => draft.set(d.value === 'default' ? null : d.value === 'unlimited' ? 0 : ownMb)}>
        <Radio value="default" label={t('Vorgabe verwenden ({quota})', { quota: quotaText(defaultQuotaMb) })} />
        <Radio value="unlimited" label={t('Unbegrenzt')} />
        <Radio value="own" label={t('Eigenes Kontingent')} />
      </RadioGroup>
    </Field>
    {mode === 'own' && <SizeInput mb={ownMb} onChange={v => { setOwnMb(v); draft.set(v) }} />}
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <Text size={200}>{effective > 0 ? t('{used} von {quota} belegt', { used: fmtSize(used), quota: fmtSize(effective * 1024 * 1024) }) : t('{size} belegt, keine Grenze', { size: fmtSize(used) })}</Text>
      {effective > 0 && <QuotaBar used={used} quota={effective * 1024 * 1024} />}
    </div>
    <div className={s.facts}>
      <span className={s.label}>{t('Dateiablage')}</span>
      <Switch checked={!u.filesOff} label={u.filesOff ? t('aus – nur der notizi-Abgleich geht') : t('an')} onChange={() => onToggleFiles(u)} style={{ marginLeft: -8 }} />
      <span className={s.label}>{t('Datenbank')}</span><span>{fmtSize(u.size)}</span>
    </div>
  </div>
}

function Mail({ user: u, onMailOwn }: UsersAdminProps & { user: AdminUser }) {
  const s = useStyles()
  return <div className={s.facts}>
  </div>
}

interface PluginRights { crm?: string | null; verein?: string | null; website?: boolean | null }
let rightsCache: Promise<Record<string, PluginRights>> | null = null

/** Rechte in den Plugins CRM, Verein und Website je Benutzer (nur zum Ansehen; vergeben werden sie in der jeweiligen App). */
function loadRights(): Promise<Record<string, PluginRights>> {
  rightsCache ??= (async () => {
    const out: Record<string, PluginRights> = {}
    const put = (id: string, v: PluginRights) => { out[id] = { ...out[id], ...v } }
    let on: string[] = []
    try { on = (await api.get<{ plugins: { key: string; on: boolean; loaded: boolean }[] }>('/api/admin/plugins')).plugins.filter(x => x.on && x.loaded).map(x => x.key) } catch { return out }
    await Promise.all([
      on.includes('crm') && api.get<{ id: string; role: string | null }[]>('/api/crm/access').then(r => r.forEach(x => put(x.id, { crm: x.role ?? null }))).catch(() => {}),
      on.includes('verein') && api.get<{ id: string; role: string | null }[]>('/api/verein/access').then(r => r.forEach(x => put(x.id, { verein: x.role ?? null }))).catch(() => {}),
      on.includes('website') && api.get<{ id: string; edit: boolean }[]>('/api/website/editors').then(r => r.forEach(x => put(x.id, { website: x.edit }))).catch(() => {}),
    ])
    return out
  })()
  return rightsCache
}

const crmRole = (r: string | null | undefined) => r === 'edit' ? t('bearbeiten') : r === 'read' ? t('lesen') : t('kein Zugriff')
const vereinRole = (r: string | null | undefined) => ({ vorstand: t('Vorstand'), kasse: t('Kasse'), pruefer: t('Kassenprüfung'), schrift: t('Schriftführung'), abteilung: t('Abteilungsleitung') } as Record<string, string>)[r ?? ''] ?? t('kein Zugriff')

function Apps({ user: u, aiConfigured, onToggleAi }: UsersAdminProps & { user: AdminUser }) {
  const s = useStyles()
  const { fail } = useApp()
  const [rights, setRights] = useState<Record<string, PluginRights> | null>(null)
  useEffect(() => { rightsCache = null; loadRights().then(setRights).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const r = rights?.[u.id]
  const any = !!rights && Object.values(rights).some(x => x.crm !== undefined || x.verein !== undefined || x.website !== undefined)
  return <div style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
    <div className={s.facts}>
      <span className={s.label}>{t('KI')}</span>
      <span title={!aiConfigured ? t('Erst unter „KI“ eine KI einrichten') : undefined}>
        <Switch checked={!!u.aiAllowed && aiConfigured} disabled={!aiConfigured} label={!aiConfigured ? t('erst unter „KI“ einrichten') : u.aiAllowed ? t('darf die KI nutzen') : t('ohne KI')}
          onChange={() => onToggleAi(u)} style={{ marginLeft: -8 }} /></span>
      {r?.crm !== undefined && <><span className={s.label}>CRM</span><span>{crmRole(r.crm)}</span></>}
      {r?.verein !== undefined && <><span className={s.label}>{t('Verein')}</span><span>{vereinRole(r.verein)}</span></>}
      {r?.website !== undefined && <><span className={s.label}>{t('Website')}</span><span>{r.website ? t('bearbeiten') : t('kein Zugriff')}</span></>}
    </div>
    {any && <Text size={200} className={s.muted}>{t('Die Rechte in CRM, Verein und Website vergibst du in der jeweiligen App.')}</Text>}
  </div>
}
