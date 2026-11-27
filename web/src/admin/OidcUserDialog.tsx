import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Text, tokens } from '@fluentui/react-components'
import { Copy20Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { copyText } from '../shell/browser'

export interface OidcUser { id: string; login: string; isAdmin?: boolean; oidcLinked?: boolean; oidcExpected?: string | null; oidcInvite?: string | null; noOwnPassword?: boolean }

/**
 * Anmeldung über OIDC für ein Konto aus der Verwaltung: per Einladungslink (einmalig, 7 Tage) oder über eine E-Mail-Adresse, die
 * der Anbieter als bestätigt meldet, vormerken - oder eine bestehende Verknüpfung lösen. Ein Anmeldename beim Anbieter genügt
 * nicht (den kann man dort je nach Einrichtung selbst wählen). Administratorkonten verknüpft die Person selbst.
 */
export function OidcUserDialog({ user, onClose }: { user: OidcUser; onClose: (changed: boolean) => void }) {
  const { fail, notify } = useApp()
  const [expected, setExpected] = useState(user.oidcExpected ?? '')
  const [pw, setPw] = useState('')
  const [invite, setInvite] = useState<{ url: string; until: string } | null>(null)
  const muted = { color: tokens.colorNeutralForeground3 }
  const link = async (value: string) => {
    try {
      await api.post(`/api/admin/oidc/users/${user.id}/link`, { expected: value })
      notify(value ? t('Verknüpfung vorgemerkt') : t('Vormerkung entfernt')); onClose(true)
    } catch (e) { fail(e) }
  }
  const makeInvite = async () => {
    try { setInvite(await api.post<{ url: string; until: string }>(`/api/admin/oidc/users/${user.id}/link`, { invite: true })) } catch (e) { fail(e) }
  }
  const unlink = async () => {
    try { await api.post(`/api/admin/oidc/users/${user.id}/unlink`, { password: pw }); notify(t('Verknüpfung gelöst')); onClose(true) } catch (e) { fail(e) }
  }
  const validMail = /^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(expected.trim())
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(!!invite) }}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{t('Anmeldung über OIDC für {login}', { login: user.login })}</DialogTitle>
          {user.oidcLinked ? <>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text>{t('Das Konto ist mit einem Konto beim Anbieter verknüpft.')}</Text>
              <Text size={200} style={muted}>{t('Nach dem Lösen kann sich {login} nur noch mit Anmeldename und Passwort anmelden. Alle Sitzungen im Browser werden beendet.', { login: user.login })}</Text>
              {user.noOwnPassword && <Field label={t('Neues Passwort')} hint={t('Das Konto wurde über den Anbieter angelegt und hat noch kein eigenes Passwort. Mindestens 8 Zeichen.')} required>
                <Input type="password" value={pw} onChange={(_, d) => setPw(d.value)} autoComplete="new-password" />
              </Field>}
            </DialogContent>
            <DialogActions>
              <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
              <Button appearance="primary" disabled={user.noOwnPassword && pw.length < 8} onClick={unlink}>{t('Verknüpfung lösen')}</Button>
            </DialogActions>
          </> : user.isAdmin ? <>
            <DialogContent>
              <Text>{t('Ein Administratorkonto kann nur der Benutzer selbst verknüpfen, unter Einstellungen → Sicherheit. Dafür muss er sein Passwort und gegebenenfalls einen Code eingeben.')}</Text>
            </DialogContent>
            <DialogActions><Button onClick={() => onClose(false)}>{t('Schließen')}</Button></DialogActions>
          </> : <>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text weight="semibold">{t('Einladungslink')}</Text>
              <Text size={200} style={muted}>{t('Wer den Link öffnet und sich beim Anbieter anmeldet, wird mit diesem Konto verknüpft. Der Link gilt 7 Tage und nur einmal; ein neuer Link ersetzt den alten.')}</Text>
              {user.oidcInvite && !invite && <Text size={200}>{t('Ein Einladungslink ist offen bis {date}.', { date: new Date(user.oidcInvite).toLocaleString() })}</Text>}
              {invite ? <div style={{ display: 'flex', gap: 6 }}>
                <Input readOnly value={invite.url} style={{ flex: 1 }} />
                <Button icon={<Copy20Regular />} aria-label={t('Kopieren')} onClick={() => { copyText(invite.url); notify(t('Kopiert')) }} />
              </div> : <div><Button onClick={makeInvite}>{user.oidcInvite ? t('Neuen Einladungslink erstellen') : t('Einladungslink erstellen')}</Button></div>}
              <Text weight="semibold" style={{ marginTop: 8 }}>{t('Oder über die E-Mail-Adresse')}</Text>
              <Field label={t('E-Mail-Adresse beim Anbieter')}
                hint={t('Meldet sich der Benutzer zum ersten Mal über OIDC an und bestätigt der Anbieter genau diese Adresse, wird das Konto verknüpft. Unbestätigte Adressen zählen nicht.')}>
                <Input type="email" value={expected} onChange={(_, d) => setExpected(d.value)} />
              </Field>
              <Text size={200} style={muted}>{t('Solange sich der Benutzer noch mit Passwort anmelden kann, kann er sein Konto auch selbst verknüpfen, unter Einstellungen → Sicherheit.')}</Text>
            </DialogContent>
            <DialogActions>
              {(user.oidcExpected || user.oidcInvite) && <Button onClick={() => link('')}>{t('Vormerkung entfernen')}</Button>}
              <Button onClick={() => onClose(!!invite)}>{invite ? t('Schließen') : t('Abbrechen')}</Button>
              <Button appearance="primary" disabled={!validMail || expected.trim() === user.oidcExpected} onClick={() => link(expected.trim())}>{t('Vormerken')}</Button>
            </DialogActions>
          </>}
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
