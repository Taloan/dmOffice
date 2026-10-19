import {
  Button, Card, CardHeader, Dropdown, Field, Input, Option, SpinButton, Switch, Text, makeStyles, tokens, Spinner, MessageBar, MessageBarBody,
  Avatar,
} from '@fluentui/react-components'
import { Add20Regular, Copy20Regular, Delete20Regular, Edit20Regular, PlugConnected20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t, tError } from '../i18n'
import { RichEditor, RichEditorHandle } from '../shell/RichEditor'

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: '12px 16px' },
  full: { gridColumn: '1 / -1' },
  section: { gridColumn: '1 / -1', fontWeight: 600, marginTop: '8px' },
  sig: { border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: tokens.borderRadiusMedium, height: '180px', display: 'flex', flexDirection: 'column' },
  cards: { display: 'flex', flexDirection: 'column', gap: '8px' },
  code: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeHero700, fontWeight: 600, letterSpacing: '2px', userSelect: 'all' },
})

interface AccountForm {
  id?: string; name: string; email: string; displayName: string
  imapHost: string; imapPort: number; imapSecurity: string; imapUser: string; imapPassword?: string; hasImapPassword?: boolean
  auth?: 'Password' | 'Microsoft'; hasOAuth?: boolean; oauthFlow?: string
  smtpHost: string; smtpPort: number; smtpSecurity: string; smtpUser: string; smtpPassword?: string; hasSmtpPassword?: boolean
  signature: string; language: 'de' | 'en'; acceptAnyCertificate: boolean; saveSentCopy: boolean; syncMonths: number; maxCachedMb: number
  trashDays: number; junkDays: number; focusedInbox: boolean
  storage: 'full' | 'slim'; slimDays: number; slimMb: number
}

export const cleanupDays = [0, 7, 14, 30, 60, 90, 180, 365]
export const cleanupLabel = (d: number) => d === 0 ? t('Nie') : d === 1 ? t('nach 1 Tag') : t('nach {n} Tagen', { n: d })

function CleanupDrop({ days, onChange }: { days: number; onChange: (d: number) => void }) {
  const list = cleanupDays.includes(days) ? cleanupDays : [...cleanupDays, days].sort((x, y) => x - y)
  return (
    <Dropdown value={cleanupLabel(days)} selectedOptions={[String(days)]} onOptionSelect={(_, d) => onChange(Number(d.optionValue))}>
      {list.map(d => <Option key={d} value={String(d)}>{cleanupLabel(d)}</Option>)}
    </Dropdown>
  )
}

const blank: AccountForm = {
  name: '', email: '', displayName: '', imapHost: '', imapPort: 993, imapSecurity: 'Ssl', imapUser: '', smtpHost: '', smtpPort: 465,
  smtpSecurity: 'Ssl', smtpUser: '', signature: '', language: 'de', acceptAnyCertificate: false, saveSentCopy: true, syncMonths: 12, maxCachedMb: 50, trashDays: 0, junkDays: 0, focusedInbox: true,
  storage: 'full', slimDays: 30, slimMb: 200,
}

// Speichermodus "Schlank": wie lange und wie viel Mail-Inhalt hier bleibt
const slimDayList = [7, 14, 30, 90, 180, 365]
const slimMbList = [50, 100, 200, 500, 1000, 2000]
const mbLabel = (mb: number) => mb >= 1000 ? `${mb / 1000} GB` : `${mb} MB`
const withValue = (list: number[], v: number) => list.includes(v) ? list : [...list, v].sort((x, y) => x - y)

// Bekannte Anbieter: Server aus der Adresse vorschlagen
const providers: Record<string, Partial<AccountForm>> = {
  'gmail.com': { imapHost: 'imap.gmail.com', smtpHost: 'smtp.gmail.com', saveSentCopy: false },
  'googlemail.com': { imapHost: 'imap.gmail.com', smtpHost: 'smtp.gmail.com', saveSentCopy: false },
  'outlook.com': { imapHost: 'outlook.office365.com', smtpHost: 'smtp.office365.com', smtpPort: 587, smtpSecurity: 'StartTls' },
  'hotmail.com': { imapHost: 'outlook.office365.com', smtpHost: 'smtp.office365.com', smtpPort: 587, smtpSecurity: 'StartTls' },
  'live.de': { imapHost: 'outlook.office365.com', smtpHost: 'smtp.office365.com', smtpPort: 587, smtpSecurity: 'StartTls' },
  'live.com': { imapHost: 'outlook.office365.com', smtpHost: 'smtp.office365.com', smtpPort: 587, smtpSecurity: 'StartTls' },
  'gmx.de': { imapHost: 'imap.gmx.net', smtpHost: 'mail.gmx.net', smtpPort: 587, smtpSecurity: 'StartTls' },
  'gmx.net': { imapHost: 'imap.gmx.net', smtpHost: 'mail.gmx.net', smtpPort: 587, smtpSecurity: 'StartTls' },
  'web.de': { imapHost: 'imap.web.de', smtpHost: 'smtp.web.de', smtpPort: 587, smtpSecurity: 'StartTls' },
  't-online.de': { imapHost: 'secureimap.t-online.de', smtpHost: 'securesmtp.t-online.de' },
  'posteo.de': { imapHost: 'posteo.de', smtpHost: 'posteo.de', smtpPort: 587, smtpSecurity: 'StartTls' },
  'mailbox.org': { imapHost: 'imap.mailbox.org', smtpHost: 'smtp.mailbox.org' },
  'icloud.com': { imapHost: 'imap.mail.me.com', smtpHost: 'smtp.mail.me.com', smtpPort: 587, smtpSecurity: 'StartTls' },
  'yahoo.com': { imapHost: 'imap.mail.yahoo.com', smtpHost: 'smtp.mail.yahoo.com' },
  'ionos.de': { imapHost: 'imap.ionos.de', smtpHost: 'smtp.ionos.de' },
  'strato.de': { imapHost: 'imap.strato.de', smtpHost: 'smtp.strato.de' },
}

// Zeitraum der Synchronisierung: 0 = alles, ab 12 Monaten in Jahren
const period = (m: number) => m === 0 ? t('Alles') : m === 12 ? t('1 Jahr') : m > 12 ? t('{n} Jahre', { n: m / 12 }) : m === 1 ? t('1 Monat') : t('{n} Monate', { n: m })

const security = [['Ssl', 'SSL/TLS'], ['StartTls', 'STARTTLS'], ['None', t('Keine')]]

export function AccountsSettings() {
  const s = useStyles()
  const { fail } = useApp()
  const [accounts, setAccounts] = useState<AccountForm[] | null>(null)
  const [edit, setEdit] = useState<AccountForm | null>(null)
  const [wizard, setWizard] = useState(false)
  // Domains privater Microsoft-Konten - null: auf diesem Server keine Microsoft-Anmeldung (DMOFFICE_MS_CLIENT_ID fehlt)
  const [msDomains, setMsDomains] = useState<string[] | null>(null)
  const load = () => api.get('/api/settings/').then(r => { setAccounts(r.accounts); setMsDomains(r.microsoftDomains ?? null) }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const remove = async (a: AccountForm) => {
    if (!confirm(t('Konto „{name}“ entfernen? Die hier gespeicherten Mails werden gelöscht – auf dem Server bleibt alles.', { name: a.name }))) return
    try { await api.del(`/api/settings/accounts/${a.id}`); load() } catch (e) { fail(e) }
  }

  if (edit) return <AccountEditor initial={edit} microsoft={!!msDomains} onDone={() => { setEdit(null); load() }} />
  if (wizard) return <AccountWizard onManual={f => { setWizard(false); setEdit(f) }} onDone={() => { setWizard(false); load() }} />
  return (
    <>
      <Text size={600} weight="semibold">{t('E-Mail-Konten')}</Text>
      <div className={s.cards}>
        {accounts?.map(a => (
          <Card key={a.id} size="small">
            <CardHeader image={<Avatar name={a.name} color="colorful" />} header={<Text weight="semibold">{a.name}</Text>}
              description={`${a.email} · ${a.syncMonths ? t('{period} synchronisiert', { period: period(a.syncMonths) }) : t('alles synchronisiert')}${a.storage === 'slim' ? ' · ' + t('Schlank') : ''}`}
              action={<div>
                <Button appearance="subtle" icon={<Edit20Regular />} onClick={() => setEdit(a)} aria-label={t('Konto bearbeiten')} title={t('Konto bearbeiten')} />
                <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => remove(a)} aria-label={t('Konto entfernen')} title={t('Konto entfernen')} />
              </div>} />
          </Card>
        ))}
      </div>
      <div><Button appearance="primary" icon={<Add20Regular />} onClick={() => setWizard(true)}>{t('Konto hinzufügen')}</Button></div>
    </>
  )
}

interface Discovered {
  source: string; imapHost: string; imapPort: number; imapSecurity: string
  smtpHost: string; smtpPort: number; smtpSecurity: string; userName: string; saveSentCopy: boolean
}

const secLabel = (v: string) => security.find(x => x[0] === v)?.[1] ?? v

/**
 * Anmeldung bei Microsoft per Geraetecode: Code anzeigen, der Benutzer bestaetigt auf microsoft.com/devicelogin,
 * hier wird gewartet. onDone bekommt die Kennung der Anmeldung - das Token dazu bleibt auf dem Server.
 */
function MicrosoftSignIn({ onDone, onCancel }: { onDone: (flow: string) => void; onCancel: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [start, setStart] = useState<{ flow: string; userCode: string; verificationUri: string; interval: number } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [round, setRound] = useState(0)
  const done = useRef(onDone)
  done.current = onDone

  useEffect(() => {
    let stop = false
    let timer: number | undefined
    setStart(null); setError(null)
    api.post('/api/settings/accounts/microsoft/start').then(r => {
      if (stop) return
      setStart(r)
      const poll = async () => {
        if (stop) return
        try {
          const { status } = await api.post('/api/settings/accounts/microsoft/poll', { flow: r.flow })
          if (stop) return
          if (status === 'ok') { done.current(r.flow); return }
          if (status === 'pending') { timer = window.setTimeout(poll, r.interval * 1000); return }
          setError(status === 'expired' ? t('Der Code ist abgelaufen.') : status === 'denied' ? t('Die Anmeldung wurde abgelehnt.') : tError(status))
        } catch {
          // Kurzer Netzfehler: weiter warten
          if (!stop) timer = window.setTimeout(poll, r.interval * 2000)
        }
      }
      timer = window.setTimeout(poll, r.interval * 1000)
    }).catch(e => { if (!stop) { fail(e); onCancel() } })
    return () => { stop = true; window.clearTimeout(timer) }
  }, [round]) // eslint-disable-line react-hooks/exhaustive-deps

  const copy = () => { if (start) navigator.clipboard?.writeText(start.userCode).catch(() => { }) }
  return (
    <MessageBar className={s.full} intent={error ? 'warning' : 'info'} layout="multiline">
      <MessageBarBody>
        {error ? (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8, alignItems: 'flex-start' }}>
            <Text>{error}</Text>
            <div style={{ display: 'flex', gap: 8 }}>
              <Button size="small" onClick={() => setRound(x => x + 1)}>{t('Neuen Code holen')}</Button>
              <Button size="small" onClick={onCancel}>{t('Abbrechen')}</Button>
            </div>
          </div>
        ) : !start ? (
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}><Spinner size="tiny" /><Text>{t('Verbinde mit Microsoft …')}</Text></div>
        ) : (
          <div style={{ display: 'flex', flexDirection: 'column', gap: 8, alignItems: 'flex-start' }}>
            <Text>{t('Öffne {url} und gib diesen Code ein. Danach mit deinem Microsoft-Konto anmelden und den Zugriff erlauben.', { url: start.verificationUri })}</Text>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
              <span className={s.code}>{start.userCode}</span>
              <Button appearance="subtle" icon={<Copy20Regular />} title={t('Code kopieren')} aria-label={t('Code kopieren')} onClick={copy} />
            </div>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <Button appearance="primary" size="small" as="a" href={start.verificationUri} target="_blank" rel="noopener noreferrer" onClick={copy}>
                {t('Microsoft-Anmeldung öffnen')}
              </Button>
              <Button size="small" onClick={onCancel}>{t('Abbrechen')}</Button>
              <Spinner size="tiny" /><Text size={200}>{t('Warte auf die Bestätigung …')}</Text>
            </div>
          </div>
        )}
      </MessageBarBody>
    </MessageBar>
  )
}

/** Wie der gefundene Anbieter anmeldet - liefert /accounts/discover mit. */
type DiscoverAuth = 'password' | 'app-password' | 'microsoft'

// Anbieter mit App-Passwort, fuer den Hinweis am Passwortfeld
const appPasswordProvider = (imapHost: string) =>
  imapHost.endsWith('gmail.com') ? 'Google' : imapHost.endsWith('me.com') ? 'Apple (iCloud)' : imapHost.endsWith('yahoo.com') ? 'Yahoo' : t('Der Anbieter')

/**
 * Konto hinzufuegen wie in Thunderbird, in zwei Schritten: erst nur die Adresse, dann je nach Anbieter
 * Passwort, App-Passwort oder Anmeldung bei Microsoft. Nichts gefunden: gleich ins Formular.
 */
function AccountWizard({ onManual, onDone }: { onManual: (f: AccountForm) => void; onDone: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [step, setStep] = useState<'idle' | 'search' | 'microsoft' | 'test' | 'save'>('idle')
  // Ergebnis der Suche - solange null, ist Schritt 1 (Adresse) zu sehen
  const [found, setFound] = useState<{ settings: Discovered; auth: DiscoverAuth } | null>(null)
  const [test, setTest] = useState<Record<string, string> | null>(null)
  const microsoft = found?.auth === 'microsoft'

  const form = (d: Discovered | null, flow?: string): AccountForm => ({
    ...blank, email: email.trim(), name: email.trim(), displayName, imapUser: d?.userName ?? email.trim(),
    ...(microsoft ? { auth: 'Microsoft' as const, oauthFlow: flow } : { imapPassword: password }),
    ...(d ? {
      imapHost: d.imapHost, imapPort: d.imapPort, imapSecurity: d.imapSecurity,
      smtpHost: d.smtpHost, smtpPort: d.smtpPort, smtpSecurity: d.smtpSecurity, saveSentCopy: d.saveSentCopy,
    } : {}),
  })

  // Schritt 1 -> 2
  const next = async () => {
    setTest(null)
    try {
      setStep('search')
      const r = await api.post('/api/settings/accounts/discover', { email: email.trim() })
      setStep('idle')
      if (!r.found) {
        notify(t('Keine Einstellungen gefunden'), t('Für diese Adresse ließen sich keine Einstellungen finden. Bitte manuell eingeben – die Daten stehen meist in der Hilfe des Anbieters.'))
        onManual(form(null))
        return
      }
      setFound({ settings: r.settings, auth: r.auth ?? 'password' })
    } catch (e) { fail(e); setStep('idle') }
  }

  const finish = async (flow?: string) => {
    if (!found) return
    try {
      setStep('test')
      const res = await api.post('/api/settings/accounts/test', form(found.settings, flow))
      setTest(res)
      if (res.imap !== 'ok' || res.smtp !== 'ok') { setStep('idle'); return }
      setStep('save')
      await api.post('/api/settings/accounts', form(found.settings, flow))
      notify(t('Konto eingerichtet'), t('Der erste Sync läuft – je nach Postfach einige Minuten.'))
      onDone()
    } catch (e) { fail(e); setStep('idle') }
  }

  const busy = step !== 'idle'
  const validEmail = /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(email.trim())
  const source = (x: string) => ({ known: t('bekannter Anbieter'), ispdb: t('Thunderbird-Datenbank'), autoconfig: t('Angaben des Anbieters'), mx: t('Mailserver der Domain'), guess: t('ausprobiert') } as Record<string, string>)[x] ?? x
  const progress = busy && step !== 'microsoft' && (
    <div className={s.full} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
      <Spinner size="tiny" />
      <Text>{step === 'search' ? t('Suche Einstellungen …') : step === 'test' ? t('Prüfe die Anmeldung …') : t('Richte das Konto ein …')}</Text>
    </div>
  )

  if (!found) return (
    <>
      <Text size={600} weight="semibold">{t('Konto hinzufügen')}</Text>
      <Text>{t('Gib deine E-Mail-Adresse ein – Server, Ports und die Art der Anmeldung sucht dmOffice selbst heraus.')}</Text>
      <form className={s.grid} onSubmit={e => { e.preventDefault(); if (validEmail && !busy) next() }}>
        <Field label={t('E-Mail-Adresse')} className={s.full}>
          <Input type="email" value={email} onChange={(_, d) => setEmail(d.value)} autoFocus autoComplete="email" />
        </Field>
        {progress}
        <div className={s.full} style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Button appearance="primary" type="submit" disabled={!validEmail || busy}>{t('Weiter')}</Button>
          <Button disabled={busy} onClick={() => onManual(form(null))}>{t('Manuell einrichten')}</Button>
          <Button disabled={busy} onClick={onDone}>{t('Abbrechen')}</Button>
        </div>
      </form>
    </>
  )

  const d = found.settings
  return (
    <>
      <Text size={600} weight="semibold">{t('Konto hinzufügen')}</Text>
      <Text weight="semibold">{email.trim()}</Text>
      <MessageBar intent="info">
        <MessageBarBody>
          {t('Gefunden ({source})', { source: source(d.source) })}: IMAP {d.imapHost}:{d.imapPort} ({secLabel(d.imapSecurity)}) · SMTP {d.smtpHost}:{d.smtpPort} ({secLabel(d.smtpSecurity)})
        </MessageBarBody>
      </MessageBar>
      <form className={s.grid} onSubmit={e => {
        e.preventDefault()
        if (busy) return
        if (microsoft) setStep('microsoft')   // weiter in finish, sobald Microsoft bestaetigt
        else if (password) finish()
      }}>
        {microsoft ? (
          <Text className={s.full}>{t('Outlook.com, Hotmail und Live erlauben keine Anmeldung mit Passwort mehr – dmOffice meldet sich dafür bei Microsoft an.')}</Text>
        ) : (
          <Field label={found.auth === 'app-password' ? t('App-Passwort des Anbieters') : t('Passwort')}
            hint={found.auth === 'app-password' ? t('{provider} verlangt hier ein App-Passwort, nicht das normale Passwort – zu erstellen in den Sicherheitseinstellungen des Kontos.', { provider: appPasswordProvider(d.imapHost) }) : undefined}>
            <Input type="password" value={password} onChange={(_, v) => { setPassword(v.value); setTest(null) }} autoFocus autoComplete="new-password" />
          </Field>
        )}
        <Field label={t('Absendername')} hint={t('so sehen Empfänger dich, z. B. „Max Muster“')}>
          <Input value={displayName} onChange={(_, v) => setDisplayName(v.value)} />
        </Field>

        {step === 'microsoft' && <MicrosoftSignIn onDone={flow => finish(flow)} onCancel={() => setStep('idle')} />}
        {progress}
        {test && (test.imap !== 'ok' || test.smtp !== 'ok') && !busy && (
          <MessageBar className={s.full} intent="error">
            <MessageBarBody>
              IMAP: {test.imap === 'ok' ? t('verbunden') : tError(test.imap)} · SMTP: {test.smtp === 'ok' ? t('verbunden') : tError(test.smtp)}
              <br />{microsoft ? t('Bei Microsoft neu anmelden oder die Einstellungen manuell anpassen.') : t('Passwort prüfen oder die Einstellungen manuell anpassen.')}
            </MessageBarBody>
          </MessageBar>
        )}

        <div className={s.full} style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Button appearance="primary" type="submit" disabled={busy || (!microsoft && !password)}>{microsoft ? t('Mit Microsoft anmelden') : t('Einrichten')}</Button>
          <Button disabled={busy} onClick={() => { setFound(null); setTest(null); setPassword('') }}>{t('Zurück')}</Button>
          <Button disabled={busy} onClick={() => onManual(form(d))}>{t('Manuell einrichten')}</Button>
          <Button disabled={busy} onClick={onDone}>{t('Abbrechen')}</Button>
        </div>
      </form>
    </>
  )
}

function AccountEditor({ initial, microsoft, onDone }: { initial: AccountForm; microsoft: boolean; onDone: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [a, setA] = useState<AccountForm>(initial.id ? { ...initial, imapPassword: '', smtpPassword: '' } : { ...initial })
  const [testing, setTesting] = useState(false)
  const [test, setTest] = useState<Record<string, string> | null>(null)
  const [signIn, setSignIn] = useState(false)
  const sig = useRef<RichEditorHandle>(null)
  const isMs = a.auth === 'Microsoft'
  const set =(p: Partial<AccountForm>) => setA(x => ({ ...x, ...p }))

  const onEmail = (email: string) => {
    const domain = email.split('@')[1]?.toLowerCase()
    const p = domain ? providers[domain] : undefined
    set({
      email, imapUser: a.imapUser && a.imapUser !== a.email ? a.imapUser : email,
      ...(p && !initial.id ? { imapPort: 993, imapSecurity: 'Ssl', smtpPort: 465, smtpSecurity: 'Ssl', ...p } : {}),
      ...(!initial.id && !p && domain && !a.imapHost ? { imapHost: `imap.${domain}`, smtpHost: `smtp.${domain}` } : {}),
    })
  }

  const body = () => ({ ...a, signature: sig.current?.html() ?? a.signature })

  const save = async () => {
    try {
      if (a.id) await api.put(`/api/settings/accounts/${a.id}`, body())
      else await api.post('/api/settings/accounts', body())
      notify(t('Gespeichert'), a.id ? undefined : t('Der erste Sync läuft – je nach Postfach einige Minuten.'))
      onDone()
    } catch (e) { fail(e) }
  }
  const check = async () => {
    setTesting(true)
    setTest(null)
    try { setTest(await api.post('/api/settings/accounts/test', body())) } catch (e) { fail(e) } finally { setTesting(false) }
  }

  const secDrop = (value: string, onChange: (v: string) => void) => (
    <Dropdown value={security.find(x => x[0] === value)?.[1]} selectedOptions={[value]} onOptionSelect={(_, d) => onChange(d.optionValue!)}>
      {security.map(([k, l]) => <Option key={k} value={k}>{l}</Option>)}
    </Dropdown>
  )

  return (
    <>
      <Text size={600} weight="semibold">{a.id ? t('Konto bearbeiten') : t('Konto hinzufügen')}</Text>
      <form className={s.grid} onSubmit={e => { e.preventDefault(); save() }}>
        <Field label={t('E-Mail-Adresse')}><Input type="email" value={a.email} onChange={(_, d) => onEmail(d.value)} autoFocus /></Field>
        <Field label={t('Absendername')}><Input value={a.displayName} onChange={(_, d) => set({ displayName: d.value })} /></Field>
        <Field label={t('Kontoname (in der Ordnerliste)')} className={s.full}><Input value={a.name} placeholder={a.email} onChange={(_, d) => set({ name: d.value })} /></Field>

        <Text className={s.section}>{t('Posteingang (IMAP)')}</Text>
        <Field label={t('Server')}><Input value={a.imapHost} onChange={(_, d) => set({ imapHost: d.value })} /></Field>
        <div style={{ display: 'flex', gap: 8 }}>
          <Field label={t('Port')} style={{ width: 100 }}><Input type="number" value={String(a.imapPort)} onChange={(_, d) => set({ imapPort: Number(d.value) })} /></Field>
          <Field label={t('Verschlüsselung')} style={{ flex: 1 }}>{secDrop(a.imapSecurity, v => set({ imapSecurity: v, imapPort: v === 'Ssl' ? 993 : 143 }))}</Field>
        </div>
        <Field label={t('Benutzer')}><Input value={a.imapUser} onChange={(_, d) => set({ imapUser: d.value })} /></Field>
        {(microsoft || isMs) && (
          <Field label={t('Anmeldung')}>
            <Dropdown value={isMs ? t('Microsoft-Konto (OAuth2)') : t('Passwort')} selectedOptions={[a.auth ?? 'Password']}
              onOptionSelect={(_, d) => { set({ auth: d.optionValue as AccountForm['auth'] }); setSignIn(false); setTest(null) }}>
              <Option value="Password">{t('Passwort')}</Option>
              <Option value="Microsoft">{t('Microsoft-Konto (OAuth2)')}</Option>
            </Dropdown>
          </Field>
        )}
        {isMs ? (
          <div className={s.full} style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
            <Text>{a.oauthFlow ? t('Neu bei Microsoft angemeldet – zum Übernehmen speichern.') : a.hasOAuth ? t('Bei Microsoft angemeldet.') : t('Noch nicht bei Microsoft angemeldet.')}</Text>
            {microsoft && !signIn && <Button size="small" onClick={() => setSignIn(true)}>{a.hasOAuth || a.oauthFlow ? t('Neu anmelden') : t('Mit Microsoft anmelden')}</Button>}
            {signIn && <MicrosoftSignIn onDone={flow => { set({ oauthFlow: flow }); setSignIn(false); setTest(null) }} onCancel={() => setSignIn(false)} />}
          </div>
        ) : (
          <Field label={t('Passwort')} hint={a.hasImapPassword ? t('leer lassen: bleibt') : t('bei Gmail/Outlook: App-Passwort des Anbieters')}>
            <Input type="password" value={a.imapPassword} onChange={(_, d) => set({ imapPassword: d.value })} autoComplete="new-password" />
          </Field>
        )}

        <Text className={s.section}>{t('Postausgang (SMTP)')}</Text>
        <Field label={t('Server')}><Input value={a.smtpHost} onChange={(_, d) => set({ smtpHost: d.value })} /></Field>
        <div style={{ display: 'flex', gap: 8 }}>
          <Field label={t('Port')} style={{ width: 100 }}><Input type="number" value={String(a.smtpPort)} onChange={(_, d) => set({ smtpPort: Number(d.value) })} /></Field>
          <Field label={t('Verschlüsselung')} style={{ flex: 1 }}>{secDrop(a.smtpSecurity, v => set({ smtpSecurity: v, smtpPort: v === 'Ssl' ? 465 : 587 }))}</Field>
        </div>
        <Field label={t('Benutzer')} hint={t('leer: wie IMAP')}><Input value={a.smtpUser} onChange={(_, d) => set({ smtpUser: d.value })} /></Field>
        {!isMs && <Field label={t('Passwort')} hint={t('leer: wie IMAP')}><Input type="password" value={a.smtpPassword} onChange={(_, d) => set({ smtpPassword: d.value })} autoComplete="new-password" /></Field>}

        <Text className={s.section}>{t('Synchronisieren')}</Text>
        {/* Feste Zeilen: was je nach Speichermodus erscheint, hat eine eigene Zelle - so rutscht nichts nach */}
        <Field label={t('Zeitraum')} hint={t('wie in Outlook: was hier offline liegt und durchsucht wird')}>
          <Dropdown value={period(a.syncMonths)}
            selectedOptions={[String(a.syncMonths)]} onOptionSelect={(_, d) => set({ syncMonths: Number(d.optionValue) })}>
            {[1, 3, 6, 12, 24, 60, 0].map(m => <Option key={m} value={String(m)}>{period(m)}</Option>)}
          </Dropdown>
        </Field>
        <Field label={t('Mails bis zu dieser Größe vollständig speichern (MB)')} hint={t('samt Anhängen; größere werden beim Öffnen geholt')}>
          <SpinButton value={a.maxCachedMb} min={1} max={500} onChange={(_, d) => set({ maxCachedMb: d.value ?? Number(d.displayValue) ?? 50 })} />
        </Field>
        <Field label={t('Speichermodus')} hint={a.storage === 'slim'
          ? t('Kopfdaten, Vorschau und die jüngsten Mails liegen hier, ältere holt dmOffice beim Öffnen vom Anbieter. Ältere Mails öffnen sich dadurch etwas langsamer, dafür wird viel Platz gespart. Suche, Regeln, Spamfilter und Prio-Posteingang arbeiten weiter.')
          : t('Alle Mails im Zeitraum liegen samt Anhängen hier und öffnen sich sofort – braucht am meisten Platz.')}>
          <Dropdown value={a.storage === 'slim' ? t('Schlank') : t('Vollständig')} selectedOptions={[a.storage ?? 'full']}
            onOptionSelect={(_, d) => set({ storage: d.optionValue as 'full' | 'slim' })}>
            <Option value="full">{t('Vollständig')}</Option>
            <Option value="slim">{t('Schlank')}</Option>
          </Dropdown>
        </Field>
        {a.storage === 'slim' ? (
          <div style={{ display: 'flex', gap: 8 }}>
            <Field label={t('Inhalte behalten')} hint={t('neue und zuletzt geöffnete Mails')} style={{ flex: 1 }}>
              <Dropdown value={t('{n} Tage', { n: a.slimDays })} selectedOptions={[String(a.slimDays)]} onOptionSelect={(_, d) => set({ slimDays: Number(d.optionValue) })}>
                {withValue(slimDayList, a.slimDays).map(d => <Option key={d} value={String(d)} text={t('{n} Tage', { n: d })}>{t('{n} Tage', { n: d })}</Option>)}
              </Dropdown>
            </Field>
            <Field label={t('höchstens')} hint={t('älteres wird verworfen')} style={{ flex: 1 }}>
              <Dropdown value={mbLabel(a.slimMb)} selectedOptions={[String(a.slimMb)]} onOptionSelect={(_, d) => set({ slimMb: Number(d.optionValue) })}>
                {withValue(slimMbList, a.slimMb).map(m => <Option key={m} value={String(m)} text={mbLabel(m)}>{mbLabel(m)}</Option>)}
              </Dropdown>
            </Field>
          </div>
        ) : <div />}

        <Field label={t('Gelöschte Elemente automatisch leeren')} hint={t('nach Datum der Mail, endgültig')}>
          <CleanupDrop days={a.trashDays ?? 0} onChange={v => set({ trashDays: v })} />
        </Field>
        <Field label={t('Junk-E-Mail automatisch leeren')} hint={t('nach Datum der Mail, endgültig')}>
          <CleanupDrop days={a.junkDays ?? 0} onChange={v => set({ junkDays: v })} />
        </Field>
        <Field label={t('Sprache der Mails')} hint={t('Antworten und Weiterleitungen: AW/Re, WG/Fwd, Zitatkopf und Rechtschreibprüfung')}>
          <Dropdown value={a.language === 'en' ? 'English' : 'Deutsch'} selectedOptions={[a.language ?? 'de']} onOptionSelect={(_, d) => set({ language: d.optionValue as 'de' | 'en' })}>
            <Option value="de">Deutsch</Option>
            <Option value="en">English</Option>
          </Dropdown>
        </Field>
        <div />
        <Switch className={s.full} label={t('Prio-Posteingang: Posteingang in „Relevant“ und „Sonstige“ aufteilen')} checked={a.focusedInbox ?? true} onChange={(_, d) => set({ focusedInbox: d.checked })} />
        <Switch className={s.full} label={t('Gesendetes im Ordner „Gesendet“ ablegen (bei Gmail aus)')} checked={a.saveSentCopy} onChange={(_, d) => set({ saveSentCopy: d.checked })} />
        <Switch className={s.full} label={t('Selbstsignierte Zertifikate annehmen (eigener Server im Netz)')} checked={a.acceptAnyCertificate} onChange={(_, d) => set({ acceptAnyCertificate: d.checked })} />

        <Text className={s.section}>{t('Signatur')}</Text>
        <div className={`${s.full} ${s.sig}`}><RichEditor ref={sig} initial={a.signature} minimal /></div>

        {test && (
          <MessageBar intent={test.imap === 'ok' && test.smtp === 'ok' ? 'success' : 'error'} className={s.full}>
            <MessageBarBody>IMAP: {test.imap === 'ok' ? t('verbunden') : tError(test.imap)} · SMTP: {test.smtp === 'ok' ? t('verbunden') : tError(test.smtp)}</MessageBarBody>
          </MessageBar>
        )}
        <div className={s.full} style={{ display: 'flex', gap: 8 }}>
          <Button appearance="primary" type="submit">{t('Speichern')}</Button>
          <Button icon={testing ? <Spinner size="tiny" /> : <PlugConnected20Regular />} onClick={check} disabled={testing}>{t('Verbindung testen')}</Button>
          <Button onClick={onDone}>{t('Abbrechen')}</Button>
        </div>
      </form>
    </>
  )
}
