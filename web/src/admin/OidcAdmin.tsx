import { Button, Field, Input, MessageBar, MessageBarBody, Spinner, Text, tokens } from '@fluentui/react-components'
import { useAdminPart } from './AdminPage' // [feature:adminclean]
import { PlugConnected20Regular } from '@fluentui/react-icons' // [feature:adminclean]
import { RibbonActions, RibbonButton } from '../shell/Ribbon' // [feature:adminclean]
import { Copy20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { copyText } from '../shell/browser'
import { AdminHeading } from './AdminHeading'
import { useAdminSave } from './draft' // [feature:savemode]
import { askConfirm } from '../shell/context' // [feature:savemode]
// [feature:adminclean]
// Kennung adminclean: ein/aus immer als Schalter
import { Switch as Checkbox } from '@fluentui/react-components'
// [feature:adminclean:else]
// import { Checkbox } from '@fluentui/react-components'
// [/feature:adminclean]

interface State { enabled: boolean; name: string | null; issuer: string | null; clientId: string | null; hasSecret: boolean; autoCreate: boolean; group: string | null; allowUnlink: boolean; logoutAtProvider: boolean; onlyOidc: boolean; redirectUri: string; logoutUri: string }

/**
 * Anmeldung über einen Identitätsanbieter (OpenID Connect): Keycloak, Authentik, Pocket ID u. a. als zusätzliche Anmeldemethode im Web.
 * Das Client-Geheimnis wird nur geschrieben, nie angezeigt.
 */
export function OidcAdmin() {
  const { fail, notify } = useApp()
  const [st, setSt] = useState<State | null>(null)
  const [v, setV] = useState({ enabled: false, name: '', issuer: '', clientId: '', clientSecret: '', autoCreate: false, group: '', allowUnlink: false, logoutAtProvider: true, onlyOidc: false })
  const [test, setTest] = useState<{ ok: boolean; issuer?: string; keys?: number; error?: string } | 'busy' | null>(null)
  const formOf = (s: State) => ({ enabled: s.enabled, name: s.name ?? '', issuer: s.issuer ?? '', clientId: s.clientId ?? '', clientSecret: '', autoCreate: s.autoCreate, group: s.group ?? '', allowUnlink: !!s.allowUnlink, logoutAtProvider: s.logoutAtProvider !== false, onlyOidc: !!s.onlyOidc })
  const load = () => api.get<State>('/api/admin/oidc').then(s => {
    setSt(s)
    setV(formOf(s))
  }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // [feature:adminclean]
  const tabbed = useAdminPart().part != null
  // [feature:adminclean:else]
  // const tabbed = false
  // [/feature:adminclean]
  // [feature:savemode]
  // Kennung savemode: Speichern im Menüband; Abschalten der Passwort-Anmeldung und Abschalten von OIDC fragen nach
  const drafting = useAdminSave({
    dirty: !!st && JSON.stringify(v) !== JSON.stringify(formOf(st)),
    save: async () => {
      if (!st) return true
      if (v.onlyOidc && !st.onlyOidc && !(await askConfirm(t('Anmeldung nur über OIDC?'),
        t('Benutzer ohne Administratorrechte können sich danach nicht mehr mit Anmeldename und Passwort anmelden.'), t('Umstellen')))) return false
      if (!v.enabled && st.enabled && !(await askConfirm(t('Anmeldung über den Anbieter abschalten?'),
        t('Wer sich bisher nur über den Anbieter anmeldet, kann sich danach nicht mehr anmelden.'), t('Abschalten')))) return false
      return save()
    },
    discard: () => { if (st) setV(formOf(st)) },
  })
  // [feature:savemode:else]
  // const drafting = false
  // [/feature:savemode]
  if (!st) return <Spinner />
  const muted = { color: tokens.colorNeutralForeground3 }
  const save = async () => {
    // [feature:savemode]
    try { await api.put('/api/admin/oidc', v); notify(t('Gespeichert')); await load(); return true } catch (e) { fail(e); return false }
    // [feature:savemode:else]
    // try { await api.put('/api/admin/oidc', v); notify(t('Gespeichert')); load() } catch (e) { fail(e) }
    // [/feature:savemode]
  }
  const check = async () => {
    setTest('busy')
    try { setTest(await api.post('/api/admin/oidc/test', v)) } catch (e) { setTest(null); fail(e) }
  }
  return <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: tabbed ? undefined : 640 }}>
    <AdminHeading first>{t('Anmeldung über Identitätsanbieter (SSO)')}</AdminHeading>
    <Text>{t('Benutzer können sich im Browser zusätzlich über einen Identitätsanbieter mit OpenID Connect anmelden. Gekoppelte Apps und App-Passwörter sind davon nicht betroffen.')}</Text>
    <Checkbox checked={v.enabled} onChange={(_, d) => setV({ ...v, enabled: !!d.checked })} label={t('Anmeldung über den Anbieter anbieten')} />
    <Field label={t('Aussteller-Adresse (Issuer)')} hint={t('Etwa https://sso.example.org/realms/dmoffice (Keycloak) oder https://auth.example.org/application/o/dmoffice/ (Authentik).')}>
      <Input value={v.issuer} onChange={(_, d) => setV({ ...v, issuer: d.value })} />
    </Field>
    <div style={{ display: 'flex', gap: 10, flexWrap: 'wrap' }}>
      <Field label={t('Client-ID')} style={{ flex: 1, minWidth: 200 }}><Input value={v.clientId} onChange={(_, d) => setV({ ...v, clientId: d.value })} /></Field>
      <Field label={t('Client-Geheimnis')} style={{ flex: 1, minWidth: 200 }} hint={st.hasSecret ? t('Gespeichert. Leer lassen, um es zu behalten.') : t('Leer bei einem öffentlichen Client.')}>
        <Input type="password" value={v.clientSecret} onChange={(_, d) => setV({ ...v, clientSecret: d.value })} autoComplete="new-password" />
      </Field>
    </div>
    <Field label={t('Weiterleitungsadresse')} hint={t('Diese Adresse muss beim Anbieter als erlaubte Weiterleitungsadresse (Redirect URI) eingetragen sein.')}>
      <div style={{ display: 'flex', gap: 6 }}>
        <Input readOnly value={st.redirectUri} style={{ flex: 1 }} />
        <Button icon={<Copy20Regular />} onClick={() => { copyText(st.redirectUri); notify(t('Kopiert')) }} aria-label={t('Kopieren')} />
      </div>
    </Field>
    <Checkbox checked={v.autoCreate} onChange={(_, d) => setV({ ...v, autoCreate: !!d.checked })}
      label={t('Neue Konten beim ersten Anmelden anlegen')} />
    <Text size={200} style={muted}>{t('Ist die Einstellung deaktiviert, können sich über den Anbieter nur Benutzer anmelden, deren Konto verknüpft ist. Benutzer können ihr Konto unter Einstellungen › Sicherheit selbst verknüpfen. Administratoren können unter Benutzer in der Spalte OIDC einen Einladungslink erstellen oder eine E-Mail-Adresse vormerken. Ist die Einstellung aktiviert, erhält jeder mit einem Konto beim Anbieter bei der ersten Anmeldung ein dmOffice-Konto ohne Administratorrechte.')}</Text>
    <Field label={t('Nur Mitglieder dieser Gruppe')} hint={t('Name der Gruppe beim Anbieter (Claim „groups“). Gilt bei jeder Anmeldung über OIDC: Wer beim Anbieter aus der Gruppe entfernt wird, kann sich nicht mehr über OIDC anmelden. Bereits angemeldete Sitzungen und App-Passwörter bleiben bestehen.')}>
      <Input value={v.group} onChange={(_, d) => setV({ ...v, group: d.value })} />
    </Field>
    {v.autoCreate && !v.group.trim() && <Text size={200} style={{ color: tokens.colorPaletteMarigoldForeground1 }}>{t('Ohne Gruppe erhält jeder ein Konto, der sich beim Anbieter anmelden kann. Ist dort die Registrierung offen, kann sich jeder selbst ein dmOffice-Konto anlegen.')}</Text>}
    <Checkbox checked={v.logoutAtProvider} onChange={(_, d) => setV({ ...v, logoutAtProvider: !!d.checked })} label={t('Beim Abmelden auch beim Anbieter abmelden')} />
    <Text size={200} style={muted}>{t('Ist die Einstellung deaktiviert, bleibt die Sitzung beim Anbieter bestehen. Wer danach auf „Anmelden mit OIDC“ klickt, wird ohne Passwortabfrage als der zuletzt angemeldete Benutzer angemeldet. Damit die Einstellung wirkt, muss beim Anbieter diese Adresse als erlaubte Weiterleitung nach dem Abmelden eingetragen sein: {uri}', { uri: st.logoutUri })}</Text>
    <Checkbox checked={v.onlyOidc} onChange={(_, d) => setV({ ...v, onlyOidc: !!d.checked })} label={t('Anmeldung nur über OIDC')} />
    <Text size={200} style={muted}>{t('Benutzer können sich nur noch über den Anbieter anmelden. Administratoren können sich weiterhin mit Anmeldename und Passwort anmelden, damit dmOffice auch bei einem Ausfall des Anbieters erreichbar bleibt. Dazu müssen sie auf der Anmeldeseite unten rechts auf das Schlüsselsymbol klicken. Gekoppelte Apps und App-Passwörter können weiter verwendet werden.')}</Text>
    {!v.onlyOidc && <><Checkbox checked={v.allowUnlink} onChange={(_, d) => setV({ ...v, allowUnlink: !!d.checked })} label={t('Benutzer dürfen die Verknüpfung selbst lösen')} />
    <Text size={200} style={muted}>{t('Wer die Verknüpfung löst, bestätigt das und legt dabei ein eigenes Passwort für dmOffice fest.')}</Text></>}
    <Text size={200} style={muted}>{t('Hat ein Konto in dmOffice die Zwei-Faktor-Anmeldung eingeschaltet, fragt dmOffice nach der Anmeldung über den Anbieter zusätzlich den Code ab – außer der Anbieter meldet, dass dort schon ein zweiter Faktor verwendet wurde.')}</Text>
    <Text size={200} style={muted}>{t('Wird ein Konto beim Anbieter gesperrt oder gelöscht, sind neue Anmeldungen über OIDC nicht mehr möglich. Bereits laufende Sitzungen in dmOffice und App-Passwörter bleiben bis zu ihrem Ablauf bestehen.')}</Text>
    {test && test !== 'busy' && <MessageBar intent={test.ok ? 'success' : 'error'} layout="multiline"><MessageBarBody>
      {test.ok ? t('Anbieter erreicht: {issuer}, {n} Schlüssel.', { issuer: test.issuer ?? '', n: test.keys ?? 0 }) : t('Anbieter nicht erreichbar: {error}', { error: test.error ?? '' })}
    </MessageBarBody></MessageBar>}
    {/* [feature:adminclean] */}
    {tabbed && <RibbonActions>
      <RibbonButton icon={<PlugConnected20Regular />} disabled={!v.issuer.trim() || test === 'busy'} label={test === 'busy' ? t('Prüfe …') : t('Verbindung prüfen')} onClick={check} />
    </RibbonActions>}
    {/* [/feature:adminclean] */}
    {!tabbed && <div style={{ display: 'flex', gap: 8 }}>
      {!drafting && <Button appearance="primary" onClick={save}>{t('Speichern')}</Button>}
      <Button disabled={!v.issuer.trim() || test === 'busy'} icon={test === 'busy' ? <Spinner size="tiny" /> : undefined} onClick={check}>{t('Verbindung prüfen')}</Button>
    </div>}
  </div>
}
