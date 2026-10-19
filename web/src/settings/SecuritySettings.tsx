import {
  Button, Field, Input, MessageBar, MessageBarBody, Text, makeStyles, tokens,
  Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle,
} from '@fluentui/react-components'
import QRCode from 'qrcode'
import { useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { copyText, saveText } from '../shell/browser'
import { t, locale } from '../i18n'

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

  const change = async () => {
    if (pw !== pw2) { notify(t('Die Passwörter sind verschieden'), undefined, 'warning'); return }
    try { await api.post('/api/auth/password', { old, new: pw }); setOld(''); setPw(''); setPw2(''); notify(t('Passwort geändert')) } catch (e) { fail(e) }
  }
  const begin = async () => {
    try {
      const r = await api.post('/api/auth/2fa/begin')
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
    const password = prompt(t('Zur Bestätigung dein Passwort:'))
    if (!password) return
    try { setCodes((await api.post('/api/auth/2fa/recovery', { login: me.login, password })).recoveryCodes) } catch (e) { fail(e) }
  }

  return (
    <>
      <Text size={600} weight="semibold">{t('Sicherheit')}</Text>
      <Text weight="semibold">{t('Passwort ändern')}</Text>
      <form className={s.form} onSubmit={e => { e.preventDefault(); change() }}>
        <Field label={t('Bisheriges Passwort')}><Input type="password" value={old} onChange={(_, d) => setOld(d.value)} autoComplete="current-password" /></Field>
        <Field label={t('Neues Passwort')} hint={t('mindestens 8 Zeichen')}><Input type="password" value={pw} onChange={(_, d) => setPw(d.value)} autoComplete="new-password" /></Field>
        <Field label={t('Wiederholen')}><Input type="password" value={pw2} onChange={(_, d) => setPw2(d.value)} autoComplete="new-password" /></Field>
        <div><Button type="submit" appearance="primary" disabled={!old || !pw}>{t('Ändern')}</Button></div>
      </form>

      <Text weight="semibold" style={{ marginTop: 16 }}>{t('Zwei-Faktor-Anmeldung')}</Text>
      <Text>
        {t('Beim Anmelden im Browser zusätzlich ein Code aus einer Authenticator-App (Microsoft/Google Authenticator, Aegis, 1Password …). Programme mit App-Passwort (Handy-Kalender, notizi) sind davon nicht betroffen.')}
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

      {codes && (
        <Dialog open onOpenChange={(_, d) => !d.open && setCodes(null)}>
          <DialogSurface style={{ maxWidth: 440 }}>
            <DialogBody>
              <DialogTitle>{t('Wiederherstellungscodes')}</DialogTitle>
              <DialogContent>
                <Text block style={{ marginBottom: 12 }}>{t('Jeder Code gilt einmal, falls das Handy fehlt. Jetzt ausdrucken oder im Passwortmanager ablegen – sie werden nicht noch einmal gezeigt.')}</Text>
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
