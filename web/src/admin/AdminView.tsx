import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody,
  Switch, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text, makeStyles, tokens, SpinButton,
} from '@fluentui/react-components'
import { PersonAdd20Regular, Delete20Regular, Key20Regular, ShieldDismiss20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { t, locale } from '../i18n'

const useStyles = makeStyles({
  root: { flex: 1, overflowY: 'auto', padding: '24px 32px', display: 'flex', flexDirection: 'column', gap: '16px' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
})

interface User {
  id: string; login: string; displayName: string; isAdmin: boolean; created: string; size: number; twoFactor: boolean
  /** null: Vorgabe; 0: unbegrenzt */
  fileQuotaMb: number | null; fileQuota: number; files: number | null
  /** Dateiablage abgeschaltet - notizi-Abgleich geht weiter */
  filesOff: boolean
}

const quotaText = (mb: number) => (mb > 0 ? (mb >= 1024 && mb % 1024 === 0 ? `${mb / 1024} GB` : `${mb} MB`) : t('unbegrenzt'))

export default function AdminView() {
  const s = useStyles()
  const { me, fail, notify } = useApp()
  const [users, setUsers] = useState<User[]>([])
  const [status, setStatus] = useState<any>(null)
  const [adding, setAdding] = useState(false)

  const load = () => api.get<User[]>('/api/admin/users').then(setUsers).catch(fail)
  const [defaultQuota, setDefaultQuota] = useState(0)
  useEffect(() => { if (status) setDefaultQuota(status.defaultFileQuotaMb ?? 0) }, [status])
  const saveDefaultQuota = async () => {
    try {
      await api.put('/api/admin/settings', { defaultFileQuotaMb: defaultQuota })
      setStatus({ ...status, defaultFileQuotaMb: defaultQuota }); load(); notify(t('Vorgabe gespeichert'))
    } catch (e) { fail(e) }
  }
  const editQuota = async (u: User) => {
    const current = u.fileQuotaMb == null ? '' : String(u.fileQuotaMb)
    const answer = prompt(t('Speicher für Dateien von {login} in MB (0 = unbegrenzt, leer = Vorgabe: {quota})', { login: u.login, quota: quotaText(status?.defaultFileQuotaMb ?? 0) }), current)
    if (answer == null) return
    const value = answer.trim()
    if (value !== '' && !/^\d+$/.test(value)) { notify(t('Bitte eine Zahl eingeben'), undefined, 'warning'); return }
    try {
      await api.put(`/api/admin/users/${u.id}`, value === '' ? { defaultFileQuota: true } : { fileQuotaMb: Number(value) })
      load()
    } catch (e) { fail(e) }
  }
  useEffect(() => { load(); api.get('/api/admin/status').then(setStatus).catch(() => {}) }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const resetPassword = async (u: User) => {
    const password = prompt(t('Neues Passwort für {login} (mindestens 8 Zeichen):', { login: u.login }))
    if (!password) return
    try { await api.put(`/api/admin/users/${u.id}`, { password }); notify(t('Passwort gesetzt')) } catch (e) { fail(e) }
  }
  const reset2fa = async (u: User) => {
    if (!confirm(t('Zwei-Faktor-Anmeldung für {login} abschalten?', { login: u.login }))) return
    try { await api.post(`/api/admin/users/${u.id}/reset-2fa`); load() } catch (e) { fail(e) }
  }
  const toggleAdmin = async (u: User) => {
    try { await api.put(`/api/admin/users/${u.id}`, { isAdmin: !u.isAdmin }); load() } catch (e) { fail(e) }
  }
  const toggleFiles = async (u: User) => {
    try { await api.put(`/api/admin/users/${u.id}`, { filesOff: !u.filesOff }); load() } catch (e) { fail(e) }
  }
  const remove = async (u: User) => {
    if (prompt(t('Alles von {login} wird gelöscht – Mails, Kalender, Kontakte, Dateien, Notizen. Zum Bestätigen den Anmeldenamen eingeben:', { login: u.login })) !== u.login) return
    try { await api.del(`/api/admin/users/${u.id}`); load() } catch (e) { fail(e) }
  }

  return (
    <div className={s.root}>
      <Text size={700} weight="semibold">{t('Verwaltung')}</Text>
      {status?.keyInDataDir && (
        <MessageBar intent="warning">
          <MessageBarBody>
            {t('Der Hauptschlüssel liegt als Datei im Datenverzeichnis. Wer das Verzeichnis kopiert, kann die Datenbanken öffnen.')}{' '}
            {t('Besser: in der Unraid-Vorlage die Variable DMOFFICE_MASTER_KEY setzen (eine lange Zufallsfolge) und den Container neu starten – dmOffice packt dann alle Schlüssel um und löscht die Datei.')}
          </MessageBarBody>
        </MessageBar>
      )}
      <div style={{ display: 'flex', gap: 16, alignItems: 'end', flexWrap: 'wrap' }}>
        <Button appearance="primary" icon={<PersonAdd20Regular />} onClick={() => setAdding(true)}>{t('Benutzer anlegen')}</Button>
        {status && (
          <Field label={t('Speicher für Dateien je Benutzer (Vorgabe)')} hint={t('WebDAV, Dateien und notizi-Abgleich; 0 = unbegrenzt. Mit * markiert: eigener Wert.')}>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
              <SpinButton value={defaultQuota} min={0} step={512} onChange={(_, d) => setDefaultQuota(d.value ?? (Number(d.displayValue) || 0))} style={{ width: 120 }} />
              <Text>MB</Text>
              <Button disabled={defaultQuota === status.defaultFileQuotaMb} onClick={saveDefaultQuota}>{t('Übernehmen')}</Button>
            </div>
          </Field>
        )}
      </div>
      <Table>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{t('Anmeldename')}</TableHeaderCell>
            <TableHeaderCell>{t('Name')}</TableHeaderCell>
            <TableHeaderCell>{t('Rolle')}</TableHeaderCell>
            <TableHeaderCell>2FA</TableHeaderCell>
            <TableHeaderCell>{t('Daten')}</TableHeaderCell>
            <TableHeaderCell>{t('Dateien')}</TableHeaderCell>
            <TableHeaderCell>{t('Angelegt')}</TableHeaderCell>
            <TableHeaderCell />
          </TableRow>
        </TableHeader>
        <TableBody>
          {users.map(u => (
            <TableRow key={u.id}>
              <TableCell>{u.login}</TableCell>
              <TableCell>{u.displayName}</TableCell>
              <TableCell>
                <Switch checked={u.isAdmin} disabled={u.id === me.id} label={u.isAdmin ? t('Admin') : t('Benutzer')} onChange={() => toggleAdmin(u)} />
              </TableCell>
              <TableCell>{u.twoFactor ? <Badge color="success">{t('an')}</Badge> : <Badge color="informative" appearance="outline">{t('aus')}</Badge>}</TableCell>
              <TableCell>{fmtSize(u.size)}</TableCell>
              <TableCell>
                <div style={{ display: 'flex', alignItems: 'center', gap: 4 }}>
                  <Switch checked={!u.filesOff} onChange={() => toggleFiles(u)} aria-label={t('Dateiablage')}
                    title={u.filesOff ? t('Dateiablage aus – nur der notizi-Abgleich geht') : t('Dateiablage an')} />
                  <Button appearance="transparent" size="small" onClick={() => editQuota(u)} title={t('Speicher für Dateien festlegen')}>
                    {u.files != null ? fmtSize(u.files) : '–'} / {u.fileQuota > 0 ? fmtSize(u.fileQuota) : '∞'}
                    {u.fileQuotaMb == null ? '' : ' *'}
                  </Button>
                </div>
              </TableCell>
              <TableCell>{new Date(u.created).toLocaleDateString(locale)}</TableCell>
              <TableCell>
                <Button appearance="subtle" icon={<Key20Regular />} onClick={() => resetPassword(u)} title={t('Passwort setzen')} />
                {u.twoFactor && <Button appearance="subtle" icon={<ShieldDismiss20Regular />} onClick={() => reset2fa(u)} title={t('2FA zurücksetzen')} />}
                {u.id !== me.id && <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => remove(u)} title={t('Löschen')} />}
              </TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
      {status && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Datenverzeichnis: {dir}', { dir: status.dataDir })}</Text>}
      {adding && <NewUser onClose={c => { setAdding(false); if (c) load() }} />}
    </div>
  )
}

function NewUser({ onClose }: { onClose: (created: boolean) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [login, setLogin] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
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
              <Field label={t('Erstes Passwort')} hint={t('mindestens 8 Zeichen; ändert der Benutzer unter Einstellungen → Sicherheit')}><Input type="password" value={password} onChange={(_, d) => setPassword(d.value)} /></Field>
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
