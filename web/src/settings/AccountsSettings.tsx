import {
  Button, Card, CardHeader, Dropdown, Field, Input, Option, SpinButton, Switch, Text, makeStyles, tokens, Spinner, MessageBar, MessageBarBody,
  Avatar,
} from '@fluentui/react-components'
import { Add20Regular, Delete20Regular, Edit20Regular, PlugConnected20Regular } from '@fluentui/react-icons'
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
})

interface AccountForm {
  id?: string; name: string; email: string; displayName: string
  imapHost: string; imapPort: number; imapSecurity: string; imapUser: string; imapPassword?: string; hasImapPassword?: boolean
  smtpHost: string; smtpPort: number; smtpSecurity: string; smtpUser: string; smtpPassword?: string; hasSmtpPassword?: boolean
  signature: string; language: 'de' | 'en'; acceptAnyCertificate: boolean; saveSentCopy: boolean; syncMonths: number; maxCachedMb: number
  trashDays: number; junkDays: number; focusedInbox: boolean
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
}

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
  const load = () => api.get('/api/settings/').then(r => setAccounts(r.accounts)).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const remove = async (a: AccountForm) => {
    if (!confirm(t('Konto „{name}“ entfernen? Die hier gespeicherten Mails werden gelöscht – auf dem Server bleibt alles.', { name: a.name }))) return
    try { await api.del(`/api/settings/accounts/${a.id}`); load() } catch (e) { fail(e) }
  }

  if (edit) return <AccountEditor initial={edit} onDone={() => { setEdit(null); load() }} />
  return (
    <>
      <Text size={600} weight="semibold">{t('E-Mail-Konten')}</Text>
      <div className={s.cards}>
        {accounts?.map(a => (
          <Card key={a.id} size="small">
            <CardHeader image={<Avatar name={a.name} color="colorful" />} header={<Text weight="semibold">{a.name}</Text>}
              description={`${a.email} · ${a.syncMonths ? t('{period} synchronisiert', { period: period(a.syncMonths) }) : t('alles synchronisiert')}`}
              action={<div>
                <Button appearance="subtle" icon={<Edit20Regular />} onClick={() => setEdit(a)} aria-label={t('Konto bearbeiten')} title={t('Konto bearbeiten')} />
                <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => remove(a)} aria-label={t('Konto entfernen')} title={t('Konto entfernen')} />
              </div>} />
          </Card>
        ))}
      </div>
      <div><Button appearance="primary" icon={<Add20Regular />} onClick={() => setEdit({ ...blank })}>{t('Konto hinzufügen')}</Button></div>
    </>
  )
}

function AccountEditor({ initial, onDone }: { initial: AccountForm; onDone: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [a, setA] = useState<AccountForm>({ ...initial, imapPassword: '', smtpPassword: '' })
  const [testing, setTesting] = useState(false)
  const [test, setTest] = useState<Record<string, string> | null>(null)
  const sig = useRef<RichEditorHandle>(null)
  const set = (p: Partial<AccountForm>) => setA(x => ({ ...x, ...p }))

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
        <Field label={t('Passwort')} hint={a.hasImapPassword ? t('leer lassen: bleibt') : t('bei Gmail/Outlook: App-Passwort des Anbieters')}>
          <Input type="password" value={a.imapPassword} onChange={(_, d) => set({ imapPassword: d.value })} autoComplete="new-password" />
        </Field>

        <Text className={s.section}>{t('Postausgang (SMTP)')}</Text>
        <Field label={t('Server')}><Input value={a.smtpHost} onChange={(_, d) => set({ smtpHost: d.value })} /></Field>
        <div style={{ display: 'flex', gap: 8 }}>
          <Field label={t('Port')} style={{ width: 100 }}><Input type="number" value={String(a.smtpPort)} onChange={(_, d) => set({ smtpPort: Number(d.value) })} /></Field>
          <Field label={t('Verschlüsselung')} style={{ flex: 1 }}>{secDrop(a.smtpSecurity, v => set({ smtpSecurity: v, smtpPort: v === 'Ssl' ? 465 : 587 }))}</Field>
        </div>
        <Field label={t('Benutzer')} hint={t('leer: wie IMAP')}><Input value={a.smtpUser} onChange={(_, d) => set({ smtpUser: d.value })} /></Field>
        <Field label={t('Passwort')} hint={t('leer: wie IMAP')}><Input type="password" value={a.smtpPassword} onChange={(_, d) => set({ smtpPassword: d.value })} autoComplete="new-password" /></Field>

        <Text className={s.section}>{t('Synchronisieren')}</Text>
        <Field label={t('Zeitraum')} hint={t('wie in Outlook: was hier offline liegt und durchsucht wird')}>
          <Dropdown value={period(a.syncMonths)}
            selectedOptions={[String(a.syncMonths)]} onOptionSelect={(_, d) => set({ syncMonths: Number(d.optionValue) })}>
            {[1, 3, 6, 12, 24, 60, 0].map(m => <Option key={m} value={String(m)}>{period(m)}</Option>)}
          </Dropdown>
        </Field>
        <Field label={t('Mails bis zu dieser Größe vollständig speichern (MB)')} hint={t('samt Anhängen; größere werden beim Öffnen geholt')}>
          <SpinButton value={a.maxCachedMb} min={1} max={500} onChange={(_, d) => set({ maxCachedMb: d.value ?? Number(d.displayValue) ?? 50 })} />
        </Field>
        <Switch label={t('Prio-Posteingang: Posteingang in „Relevant“ und „Sonstige“ aufteilen')} checked={a.focusedInbox ?? true} onChange={(_, d) => set({ focusedInbox: d.checked })} />
        <Field label={t('Gelöschte Elemente automatisch leeren')} hint={t('nach Datum der Mail, endgültig')}>
          <CleanupDrop days={a.trashDays ?? 0} onChange={v => set({ trashDays: v })} />
        </Field>
        <Field label={t('Junk-E-Mail automatisch leeren')} hint={t('nach Datum der Mail, endgültig')}>
          <CleanupDrop days={a.junkDays ?? 0} onChange={v => set({ junkDays: v })} />
        </Field>
        <Switch label={t('Gesendetes im Ordner „Gesendet“ ablegen (bei Gmail aus)')} checked={a.saveSentCopy} onChange={(_, d) => set({ saveSentCopy: d.checked })} />
        <Switch label={t('Selbstsignierte Zertifikate annehmen (eigener Server im Netz)')} checked={a.acceptAnyCertificate} onChange={(_, d) => set({ acceptAnyCertificate: d.checked })} />

        <Field label={t('Sprache der Mails')} hint={t('Antworten und Weiterleitungen: AW/Re, WG/Fwd, Zitatkopf und Rechtschreibprüfung')}>
          <Dropdown value={a.language === 'en' ? 'English' : 'Deutsch'} selectedOptions={[a.language ?? 'de']} onOptionSelect={(_, d) => set({ language: d.optionValue as 'de' | 'en' })}>
            <Option value="de">Deutsch</Option>
            <Option value="en">English</Option>
          </Dropdown>
        </Field>

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
