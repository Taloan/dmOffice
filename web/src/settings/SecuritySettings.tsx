import {
  Button, Field, Input, MessageBar, MessageBarBody, Text, makeStyles, tokens,
  Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle,
} from '@fluentui/react-components'
import QRCode from 'qrcode'
import { useEffect, useState } from 'react'
import { api, featureOn } from '../api'
import { useApp } from '../App'
import { askConfirm, askPassword } from '../shell/context'
import { copyText, saveText } from '../shell/browser'
import { t, tError, locale } from '../i18n'
import { PersonDelete20Regular, SignOut20Regular } from '@fluentui/react-icons'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { Section, usePageTab } from './parts'

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '420px' },
  codes: { display: 'grid', gridTemplateColumns: 'repeat(2, 1fr)', gap: '4px 16px', fontFamily: 'Consolas, monospace', fontSize: '15px', padding: '12px', backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusMedium },
})

export function SecuritySettings() {
  const s = useStyles()
  const { me, fail, notify, refreshMe } = useApp()
  const [old, setOld] = useState('')
  const [pw, setPw] = useState('')
  const [pw2, setPw2] = useState('')
  const [setup, setSetup] = useState<{ uri: string; secret: string; qr: string } | null>(null)
  const [code, setCode] = useState('')
  const [codes, setCodes] = useState<string[] | null>(null)
  const [disable, setDisable] = useState(false)
  // Anmeldung über einen Identitätsanbieter (OIDC): verknüpfen oder lösen
  type Sso = { available: boolean; name: string | null; linked: boolean; allowUnlink: boolean; confirm?: 'password' | 'password+code' | 'code' | 'provider'; confirmed?: boolean; hasPassword?: boolean; passwordLogin?: boolean }
  const [sso, setSso] = useState<Sso | null>(null)
  const loadSso = () => { if (featureOn('oidc')) api.get<Sso>('/api/auth/oidc/me').then(x => { setSso(x); if (x.confirmed) setUnlinking(true) }).catch(() => setSso(null)) }
  useEffect(loadSso, [])
  // Lösen: dabei das Passwort festlegen, das ab jetzt gilt
  const [unlinking, setUnlinking] = useState(false)
  const [newPw, setNewPw] = useState({ a: '', b: '' })
  // Bestätigung beim Lösen: bisheriges Passwort und/oder Code (ohne eigenes Passwort und ohne 2FA: neue Anmeldung beim Anbieter)
  const [unOld, setUnOld] = useState('')
  const [unCode, setUnCode] = useState('')
  const needOld = sso?.confirm === 'password' || sso?.confirm === 'password+code'
  const needCode = sso?.confirm === 'password+code' || sso?.confirm === 'code'
  const needProvider = sso?.confirm === 'provider' && !sso.confirmed
  const unlinkSso = async () => {
    if (newPw.a !== newPw.b) { notify(t('Die Passwörter sind verschieden'), undefined, 'warning'); return }
    try {
      await api.post('/api/auth/oidc/unlink', { password: newPw.a, old: unOld || undefined, code: unCode || undefined })
      setUnlinking(false); setNewPw({ a: '', b: '' }); setUnOld(''); setUnCode(''); notify(t('Verknüpfung gelöst')); refreshMe(); loadSso()
    } catch (e) { fail(e); loadSso() }
  }

  const change = async () => {
    if (pw !== pw2) { notify(t('Die Passwörter sind verschieden'), undefined, 'warning'); return }
    try { await api.post('/api/auth/password', { old, new: pw, code: pwCode || undefined }); setOld(''); setPw(''); setPw2(''); setPwCode(''); notify(t('Passwort geändert')); loadSso() } catch (e) { fail(e); loadSso() }
  }
  // Ohne eigenes Passwort (über OIDC angelegt): statt des bisherigen Passworts der Code bzw. eine Bestätigung beim Anbieter
  const [pwCode, setPwCode] = useState('')
  const noOwn = sso?.linked && sso.hasPassword === false
  // Verknüpfen: zuerst mit Passwort (und Code) bestätigen, dann frisch beim Anbieter anmelden
  const linkSso = async () => {
    const c = await askPassword(t('Verknüpfen bestätigen'), !!me.twoFactor)
    if (!c) return
    try {
      const r = await api.post<{ url: string }>('/api/auth/oidc/link', { password: c.password, code: c.code })
      window.location.href = r.url
    } catch (e) { fail(e) }
  }
  const begin = async () => {
    const c = await askPassword(t('Zwei-Faktor-Anmeldung einrichten'), false)
    if (!c) return
    try {
      const r = await api.post('/api/auth/2fa/begin', { password: c.password })
      setSetup({ ...r, qr: await QRCode.toDataURL(r.uri, { width: 220, margin: 1 }) })
    } catch (e) { fail(e) }
  }
  const confirm2fa = async () => {
    try {
      const r = await api.post('/api/auth/2fa/confirm', { code })
      setCodes(r.recoveryCodes); setSetup(null); setCode(''); refreshMe()
    } catch (e) { fail(e) }
  }
  const newCodes = async () => {
    const c = await askPassword(t('Neue Wiederherstellungscodes'), true)
    if (!c) return
    try { setCodes((await api.post('/api/auth/2fa/recovery', { login: me.login, password: c.password, code: c.code })).recoveryCodes) } catch (e) { fail(e) }
  }
  const logoutAll = async () => {
    if (!await askConfirm(t('Überall abmelden'), t('Alle anderen Browser und Sitzungen werden abgemeldet. App-Passwörter bleiben gültig; sie stehen unter Geräte & Sync.'), t('Abmelden'))) return
    try { await api.post('/api/auth/logout-all'); notify(t('Überall abgemeldet')); loadSessions() } catch (e) { fail(e) }
  }
  // Angemeldete Browser (Sitzungsliste)
  const [sessions, setSessions] = useState<{ id: string; created: string; seen: string; browser: string; address: string; current: boolean }[] | null>(null)
  const loadSessions = () => { api.get('/api/auth/sessions').then(setSessions).catch(() => setSessions([])) }
  useEffect(loadSessions, [])
  const removeAccount = async () => {
    try {
      const info = await api.get<{ twoFactor: boolean; lastAdmin: boolean; shares: number; members: string[] }>('/api/auth/delete-account/impact')
      if (info.lastAdmin) { notify(t('Du bist der einzige Admin. Lege zuerst einen weiteren Admin an.'), undefined, 'warning'); return }
      const parts = [
        t('Dein Konto und deine Daten in dmOffice werden sofort gelöscht, dazu deine Kopien im Storage Extender und im Files-Backup. Das lässt sich nicht rückgängig machen.'),
        t('Von dir im Chat gesendete Dateien sind danach nicht mehr verfügbar. Mails bei deinem Anbieter bleiben dort.'),
        t('Vorhandene Core-Backups bleiben bestehen, bis neuere sie ersetzen. Bis dahin enthalten sie deine Daten verschlüsselt.'),
      ]
      if (info.shares > 0) parts.push(t('Du hast {n} Freigaben für {names}. Diese Benutzer verlieren den Zugriff, auch auf alles, was sie dort abgelegt haben.', { n: info.shares, names: info.members.join(', ') }))
      if (!await askConfirm(t('Konto löschen'), parts.join(' '), t('Weiter'))) return
      const r = await askPassword(t('Zum Löschen bestätigen'), info.twoFactor, t('Konto endgültig löschen'))
      if (!r) return
      await api.post('/api/auth/delete-account', { password: r.password, code: r.code })
      location.hash = ''
      location.reload()
    } catch (e) { fail(e) }
  }

  const [tab, tabBar] = usePageTab('security', [['login', t('Anmeldung')], ['sessions', t('Sitzungen')], ['log', t('Sicherheitsprotokoll')], ['delete', t('Konto löschen')]])

  const endSession = async (id: string) => {
    try { await api.del(`/api/auth/sessions/${id}`); notify(t('Abgemeldet')); loadSessions() } catch (e) { fail(e) }
  }

  return (
    <>
      {tabBar}
      {tab === 'login' && <>
      {/* Auch mit OIDC verknüpft: Das Passwort gilt zum Bestätigen und - wenn erlaubt - für die Anmeldung mit Anmeldename und Passwort */}
      <Section title={noOwn ? t('Passwort festlegen') : t('Passwort ändern')}>
      {noOwn && <Text>{t('Dein Konto wurde über {name} angelegt und hat noch kein eigenes Passwort.', { name: sso?.name ?? 'OIDC' })}</Text>}
      {noOwn && sso?.confirm === 'provider' && !sso.confirmed
        ? <div><Button onClick={() => { window.location.href = '/api/auth/oidc/start?reauth=1&back=' + encodeURIComponent('#/settings/security') }}>{t('Bei {name} bestätigen', { name: sso?.name ?? 'OIDC' })}</Button></div>
        : <form className={s.form} onSubmit={e => { e.preventDefault(); change() }}>
          {!noOwn && <Field label={t('Bisheriges Passwort')}><Input type="password" value={old} onChange={(_, d) => setOld(d.value)} autoComplete="current-password" /></Field>}
          {noOwn && sso?.confirm === 'code' && <Field label={t('Code aus der Authenticator-App')}><Input value={pwCode} onChange={(_, d) => setPwCode(d.value)} autoComplete="one-time-code" inputMode="numeric" /></Field>}
          <Field label={t('Neues Passwort')} hint={t('mindestens 8 Zeichen')}><Input type="password" value={pw} onChange={(_, d) => setPw(d.value)} autoComplete="new-password" /></Field>
          <Field label={t('password|Wiederholen')}><Input type="password" value={pw2} onChange={(_, d) => setPw2(d.value)} autoComplete="new-password" /></Field>
          <div><Button type="submit" appearance="primary" disabled={(!noOwn && !old) || (noOwn && sso?.confirm === 'code' && !pwCode) || !pw}>{noOwn ? t('Festlegen') : t('Ändern')}</Button></div>
        </form>}
      </Section>
      </>}

      {tab === 'sessions' && <Section title={t('Sitzungen')} last>
      <RibbonActions><RibbonButton icon={<SignOut20Regular />} label={t('Überall abmelden')} onClick={logoutAll} /></RibbonActions>
      <Text>{t('Browser, in denen du angemeldet bist. Einzelne lassen sich abmelden. Programme mit App-Passwort stehen unter Geräte & Sync.')}</Text>
      {sessions && sessions.length > 0 && (
        <div style={{ display: 'grid', gridTemplateColumns: '1fr auto auto', gap: '6px 16px', alignItems: 'center', maxWidth: 640 }}>
          {sessions.map(x => [
            <Text key={x.id + 'a'} weight={x.current ? 'semibold' : 'regular'}>{browserText(x.browser)}{x.current ? ' – ' + t('dieser Browser') : ''}</Text>,
            <Text key={x.id + 'b'} size={200}>{t('zuletzt aktiv {time}', { time: new Date(x.seen).toLocaleString(locale) })}{x.address ? ' · ' + x.address : ''}</Text>,
            x.current ? <span key={x.id + 'c'} /> : <Button key={x.id + 'c'} size="small" onClick={() => endSession(x.id)}>{t('Abmelden')}</Button>,
          ])}
        </div>
      )}
      </Section>}

      {tab === 'login' && sso?.available && <Section title={t('Anmeldung über {name}', { name: sso.name ?? 'SSO' })}>
        <Text>{sso.linked
          ? (sso.passwordLogin
            ? t('Dein Konto ist mit {name} verknüpft: Du meldest dich mit „Anmelden mit {name}“ an. Anmeldename und Passwort gelten weiterhin.', { name: sso.name ?? 'SSO' })
            : t('Dein Konto ist mit {name} verknüpft: Du meldest dich mit „Anmelden mit {name}“ an.', { name: sso.name ?? 'SSO' }))
          : t('Verknüpfe dein Konto, um dich künftig über {name} anzumelden. Du bestätigst mit deinem Passwort und meldest dich danach beim Anbieter an.', { name: sso.name ?? 'SSO' })}</Text>
        {sso.linked && !sso.allowUnlink && <Text size={200}>{t('Die Verknüpfung kann nur die Verwaltung lösen.')}</Text>}
        {sso.linked && sso.allowUnlink && !unlinking && <div><Button onClick={() => setUnlinking(true)}>{t('Verknüpfung lösen')}</Button></div>}
        {sso.linked && unlinking && needProvider && <div className={s.form}>
          <Text>{t('Bestätige zuerst mit einer neuen Anmeldung bei {name}. Danach legst du hier dein Passwort fest.', { name: sso.name ?? 'SSO' })}</Text>
          <div style={{ display: 'flex', gap: 8 }}>
            <Button appearance="primary" onClick={() => { window.location.href = '/api/auth/oidc/start?reauth=1&back=' + encodeURIComponent('#/settings/security') }}>{t('Bei {name} bestätigen', { name: sso.name ?? 'SSO' })}</Button>
            <Button onClick={() => setUnlinking(false)}>{t('Abbrechen')}</Button>
          </div>
        </div>}
        {sso.linked && unlinking && !needProvider && <form className={s.form} onSubmit={e => { e.preventDefault(); unlinkSso() }}>
          <Text>{t('Ab jetzt meldest du dich mit Anmeldename und diesem Passwort an.')}</Text>
          {needOld && <Field label={t('Bisheriges Passwort')}><Input type="password" value={unOld} onChange={(_, d) => setUnOld(d.value)} autoComplete="current-password" /></Field>}
          {needCode && <Field label={t('Code aus der Authenticator-App')}><Input value={unCode} onChange={(_, d) => setUnCode(d.value)} autoComplete="one-time-code" inputMode="numeric" /></Field>}
          <Field label={t('Neues Passwort')} hint={t('mindestens 8 Zeichen')}><Input type="password" value={newPw.a} onChange={(_, d) => setNewPw(x => ({ ...x, a: d.value }))} autoComplete="new-password" /></Field>
          <Field label={t('password|Wiederholen')}><Input type="password" value={newPw.b} onChange={(_, d) => setNewPw(x => ({ ...x, b: d.value }))} autoComplete="new-password" /></Field>
          <div style={{ display: 'flex', gap: 8 }}>
            <Button type="submit" appearance="primary" disabled={newPw.a.length < 8 || !newPw.b || (needOld && !unOld) || (needCode && !unCode)}>{t('Verknüpfung lösen')}</Button>
            <Button onClick={() => { setUnlinking(false); setNewPw({ a: '', b: '' }); setUnOld(''); setUnCode('') }}>{t('Abbrechen')}</Button>
          </div>
        </form>}
        {!sso.linked && <div><Button appearance="primary" onClick={linkSso}>{t('Verknüpfen')}</Button></div>}
      </Section>}

      {tab === 'login' && <Section title={t('Zwei-Faktor-Anmeldung')} last>
      <Text>
        {t('Beim Anmelden im Browser zusätzlich ein Code aus einer Authenticator-App. Programme mit App-Passwort (Handy-Kalender, notizi) sind davon nicht betroffen.')}
      </Text>
      {me.twoFactor ? (
        <div style={{ display: 'flex', gap: 8 }}>
          <MessageBar intent="success" style={{ flex: 1 }}><MessageBarBody>{t('Eingeschaltet.')}</MessageBarBody></MessageBar>
          <Button onClick={newCodes}>{t('Neue Wiederherstellungscodes')}</Button>
          <Button onClick={() => setDisable(true)}>{t('Ausschalten')}</Button>
        </div>
      ) : !setup ? (
        <div><Button appearance="primary" onClick={begin}>{t('Einrichten')}</Button></div>
      ) : (
        <div style={{ display: 'flex', gap: 24, alignItems: 'flex-start' }}>
          <img src={setup.qr} width={220} height={220} alt={t('QR-Code')} style={{ background: '#fff', borderRadius: 8 }} />
          <form className={s.form} onSubmit={e => { e.preventDefault(); confirm2fa() }}>
            <Text>{t('1. Den QR-Code mit der Authenticator-App scannen – oder den Schlüssel eintippen:')}</Text>
            <Text font="monospace" style={{ wordBreak: 'break-all' }}>{setup.secret.match(/.{1,4}/g)?.join(' ')}</Text>
            <Field label={t('2. Den angezeigten Code eingeben')}><Input value={code} onChange={(_, d) => setCode(d.value)} inputMode="numeric" autoFocus /></Field>
            <div style={{ display: 'flex', gap: 8 }}><Button appearance="primary" type="submit">{t('Bestätigen')}</Button><Button onClick={() => setSetup(null)}>{t('Abbrechen')}</Button></div>
          </form>
        </div>
      )}
      </Section>}

      {codes && (
        <Dialog open onOpenChange={(_, d) => !d.open && setCodes(null)}>
          <DialogSurface style={{ maxWidth: 440 }}>
            <DialogBody>
              <DialogTitle>{t('Wiederherstellungscodes')}</DialogTitle>
              <DialogContent>
                <Text block style={{ marginBottom: 12 }}>{t('Jeder Code gilt einmal, falls das Handy fehlt. Die Codes werden nicht noch einmal angezeigt.')}</Text>
                <div className={s.codes}>{codes.map(c => <span key={c}>{c}</span>)}</div>
              </DialogContent>
              <DialogActions>
                <Button onClick={() => saveText(t('dmOffice-Wiederherstellungscodes-{login}.txt', { login: me.login }),
                  t('dmOffice – Wiederherstellungscodes für {login}', { login: me.login }) + '\n' + t('Erstellt {time}', { time: new Date().toLocaleString(locale) }) + '\n' + t('Jeder Code gilt einmal.') + `\n\n${codes.join('\n')}\n`)}>
                  {t('Als Datei speichern')}
                </Button>
                <Button onClick={async () => (await copyText(codes.join('\n')))
                  ? notify(t('Kopiert'))
                  : notify(t('Kopieren ging nicht'), t('Bitte markieren und mit Strg+C kopieren.'), 'warning')}>{t('Kopieren')}</Button>
                <Button appearance="primary" onClick={() => setCodes(null)}>{t('Erledigt')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
      {disable && <DisableDialog onClose={done => { setDisable(false); if (done) refreshMe() }} />}
      {tab === 'log' && <SecurityLog />}

      {tab === 'delete' && <Section title={t('Konto löschen')} last>
        <RibbonActions><RibbonButton icon={<PersonDelete20Regular />} label={t('Konto löschen …')} onClick={removeAccount} /></RibbonActions>
        <Text>{t('Löscht dein Konto und deine Daten in dmOffice endgültig.')}</Text>
      </Section>}
    </>
  )
}

function DisableDialog({ onClose }: { onClose: (done: boolean) => void }) {
  const { me, fail } = useApp()
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const go = async () => {
    try { await api.post('/api/auth/2fa/disable', { login: me.login, password, code }); onClose(true) } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 380 }}>
        <DialogBody>
          <DialogTitle>{t('Zwei-Faktor-Anmeldung ausschalten')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Passwort')}><Input type="password" value={password} onChange={(_, d) => setPassword(d.value)} /></Field>
            <Field label={t('Code')}><Input value={code} onChange={(_, d) => setCode(d.value)} /></Field>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={go}>{t('Ausschalten')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Name des Browsers, wie ihn der Server meldet („Edge unter Windows“). */
function browserText(name: string) {
  const m = /^(.+) unter (.+)$/.exec(name)
  return m ? t('{browser} unter {os}', { browser: m[1], os: m[2] }) : name
}

/** Eintrag des Protokolls: feste Texte direkt, solche mit Namen oder Adresse über ihr Muster. */
function logText(what: string) {
  let m = /^neuer Browser \((.+), Adresse (.*)\)$/.exec(what)
  if (m) return t('neuer Browser ({browser}, Adresse {address})', { browser: browserText(m[1]), address: m[2] })
  m = /^neues App-Passwort „(.*)“$/.exec(what)
  if (m) return t('neues App-Passwort „{name}“', { name: m[1] })
  m = /^neuer Zugang für „(.*)“ \(Dateien, Kalender, Kontakte\)$/.exec(what)
  if (m) return t('neuer Zugang für „{name}“ (Dateien, Kalender, Kontakte)', { name: m[1] })
  m = /^Mail-Weiterleitung an (.*)$/.exec(what)
  if (m) return t('Mail-Weiterleitung an {to}', { to: m[1] })
  m = /^Anmeldung über OIDC vorgemerkt für „(.*)“$/.exec(what)
  if (m) return t('Anmeldung über OIDC vorgemerkt für „{name}“', { name: m[1] })
  m = /^Anmeldung über (.+) verknüpft$/.exec(what)
  if (m) return t('Anmeldung über {name} verknüpft', { name: m[1] })
  m = /^angemeldet über (.+)$/.exec(what)
  if (m) return t('angemeldet über {name}', { name: m[1] })
  // Feste Texte, auch mit wechselndem Teil nach „: “ („Für WebDAV und Nextcloud ausgeblendet: /Pfad“)
  return tError(what)
}

/** Sicherheitsprotokoll (Tresor Stufe 1): Änderungen durch einen Administrator (mit Name) und eigene. */
function SecurityLog() {
  const [log, setLog] = useState<{ at: string; what: string; by: string | null }[] | null>(null)
  useEffect(() => { api.get('/api/settings/security-log').then(setLog).catch(() => setLog([])) }, [])
  if (!log) return null
  return (
    <>
      <Section title={t('Sicherheitsprotokoll')} last>
      <Text size={200}>{t('Wichtige Änderungen an deinem Konto und neue Anmeldungen meldet dir Pukki sofort, zusätzlich per Mail an dich selbst. Das gilt auch für Änderungen durch einen Administrator.')}</Text>
      {log.length === 0 ? <Text size={200}>{t('Noch keine Einträge.')}</Text> : (
        <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '4px 16px', maxWidth: 640 }}>
          {log.slice(0, 30).map((e, i) => [
            <Text key={i + 'a'} size={200}>{new Date(e.at).toLocaleString(locale)}</Text>,
            <Text key={i + 'b'} size={200} weight={e.by ? 'semibold' : 'regular'}>{logText(e.what)}{e.by ? ' – ' + t('durch den Administrator {name}', { name: e.by }) : ''}</Text>,
          ])}
        </div>
      )}
      </Section>
    </>
  )
}
