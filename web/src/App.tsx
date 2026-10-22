import {
  Button, Field, FluentProvider, Input, MessageBar, MessageBarActions, MessageBarBody, Spinner, Title2, Text, makeStyles, mergeClasses, tokens,
  Toaster, useId, useToastController, Toast, ToastTitle, ToastBody,
} from '@fluentui/react-components'
import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import { chooseLanguage, lang, t, tError } from './i18n'
import { api, Me } from './api'
import { storeTheme, themesFor, ThemeChoice, useIsDark } from './theme'
import { currentPrefs, PrefsContext, usePrefsState, usePrefsVersion } from './prefs'
import { Shell } from './shell/Shell'
import { MailWindow, isMailWindow } from './mail/MailWindow'
import { applyUpdate, usePwa } from './pwa'
import { useIsMobile } from './shell/mobile'
import { UpdateGate, UpdateState } from './admin/UpdatePanel'

interface AppCtx {
  me: Me
  notify: (title: string, body?: string, intent?: 'success' | 'error' | 'info' | 'warning') => void
  fail: (e: unknown) => void
  refreshMe: () => void
}

const Ctx = createContext<AppCtx>(null as any)
export const useApp = () => useContext(Ctx)

const useStyles = makeStyles({
  center: { height: '100%', display: 'flex', alignItems: 'center', justifyContent: 'center', backgroundColor: tokens.colorNeutralBackground2 },
  card: {
    width: '360px', padding: '32px', display: 'flex', flexDirection: 'column', gap: '14px', borderRadius: tokens.borderRadiusXLarge,
    backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow16,
  },
  logo: { display: 'flex', alignItems: 'center', gap: '10px', marginBottom: '8px' },
  foot: { display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: '4px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  langs: { display: 'flex', gap: '2px' },
  attention: {
    position: 'fixed', bottom: 'calc(env(safe-area-inset-bottom, 0px) + 72px)', left: '50%', transform: 'translateX(-50%)', zIndex: 1000000,
    maxWidth: 'calc(100vw - 32px)', boxShadow: tokens.shadow16,
  },
  update: {
    // In der installierten App unter der Titelleiste - dort waere sie Ziehflaeche des Fensters und nicht klickbar
    position: 'fixed', top: 'calc(env(titlebar-area-height, 0px) + 8px)', left: '50%', transform: 'translateX(-50%)', zIndex: 1000000,
    maxWidth: 'calc(100vw - 32px)', boxShadow: tokens.shadow16,
  },
})

export function App() {
  const dark = useIsDark()
  usePrefsVersion()
  const th = themesFor(currentPrefs.accent)
  return (
    <FluentProvider theme={dark ? th.dark : th.light} style={{ height: '100%' }}>
      <Root />
    </FluentProvider>
  )
}

function Root() {
  const [state, setState] = useState<{ setupNeeded: boolean; setupCode?: boolean; loggedIn: boolean; user: Me | null } | null>(null)
  const [upd, setUpd] = useState<UpdateState | null>(null)
  // Remount der Programme, sobald die Datenaenderung des Benutzers durch ist
  const [round, setRound] = useState(0)
  const toasterId = useId('toaster')
  const { dispatchToast } = useToastController(toasterId)

  const load = useCallback(() => {
    api.get('/api/auth/state').then(setState).catch(() => setState({ setupNeeded: false, loggedIn: false, user: null }))
  }, [])
  useEffect(load, [load])
  useEffect(() => {
    const onUnauth = () => setState(s => (s ? { ...s, loggedIn: false, user: null } : s))
    window.addEventListener('dmoffice:unauthorized', onUnauth)
    return () => window.removeEventListener('dmoffice:unauthorized', onUnauth)
  }, [])

  // Datenaenderung (Update-Sicherheit): wartet die Datenbank dieses Benutzers noch, zeigt die Oberflaeche den Assistenten
  // statt der Programme und fragt oefter nach; der Verwaltung faellt zusaetzlich auf, wenn irgendwo etwas wartet
  const waiting = upd?.mine.pending ?? false
  useEffect(() => {
    if (!state?.loggedIn) { setUpd(null); return }
    let alive = true
    const poll = () => api.get<UpdateState>('/api/update/state').then(u => {
      if (!alive) return
      setUpd(prev => { if (prev?.mine.pending && !u.mine.pending) setRound(r => r + 1); return u })
    }).catch(() => { if (alive) setUpd(prev => prev ?? { isAdmin: false, mine: { pending: false, state: 'current', from: 0, to: 0, missingBytes: 0, backupMissingBytes: 0, needsConsent: false }, attention: false }) })
    poll()
    const id = window.setInterval(poll, waiting ? 4000 : 60000)
    return () => { alive = false; clearInterval(id) }
  }, [state?.loggedIn, waiting])

  // Hell/Dunkel aus den Einstellungen des Benutzers uebernehmen
  useEffect(() => {
    if (!state?.loggedIn) return
    api.get('/api/settings/').then(s => storeTheme((s.theme as ThemeChoice) || 'system')).catch(() => {})
  }, [state?.loggedIn, round])

  const notify = useCallback((title: string, body?: string, intent: 'success' | 'error' | 'info' | 'warning' = 'success') =>
    dispatchToast(<Toast><ToastTitle>{title}</ToastTitle>{body && <ToastBody>{body}</ToastBody>}</Toast>, { intent, timeout: intent === 'error' ? 8000 : 3000 }),
    [dispatchToast])
  const fail = useCallback((e: unknown) => notify(t('Fehler'), tError(e instanceof Error ? e.message : String(e)), 'error'), [notify])

  if (!state) return <Center><Spinner /></Center>
  return (
    <>
      <Toaster toasterId={toasterId} position="bottom-end" />
      <UpdateBar />
      {state.setupNeeded ? <Setup onDone={load} needCode={state.setupCode} />
        : !state.loggedIn || !state.user ? <Login onDone={load} />
        : (
          <Ctx.Provider value={{ me: state.user, notify, fail, refreshMe: load }}>
            {!upd ? <Center><Spinner /></Center> : waiting ? <UpdateGate state={upd} /> : <WithPrefs key={round}>{isMailWindow() ? <MailWindow /> : <Shell />}</WithPrefs>}
            {!waiting && upd?.attention && !isMailWindow() && <UpdateAttention />}
          </Ctx.Provider>
        )}
    </>
  )
}

function WithPrefs({ children }: { children: React.ReactNode }) {
  const value = usePrefsState()
  return <PrefsContext.Provider value={value}>{children}</PrefsContext.Provider>
}

function Center({ children }: { children: React.ReactNode }) {
  const s = useStyles()
  return <div className={s.center}>{children}</div>
}

function Logo() {
  const s = useStyles()
  return (
    <div className={s.logo}>
      <img src="/favicon.svg" width={36} height={36} alt="" />
      <Title2>dmOffice</Title2>
    </div>
  )
}

/** Fuss der Anmelde-/Einrichtungsseite: Sprache umschalten und Versionsnummer. */
function CardFooter() {
  const s = useStyles()
  const [version, setVersion] = useState('')
  useEffect(() => { fetch('/api/health').then(r => r.json()).then(r => setVersion(r.version ?? '')).catch(() => {}) }, [])
  return (
    <div className={s.foot}>
      <div className={s.langs}>
        {(['de', 'en'] as const).map(l => (
          <Button key={l} size="small" appearance={lang === l ? 'secondary' : 'subtle'} onClick={() => lang !== l && chooseLanguage(l)}>
            {l === 'de' ? 'Deutsch' : 'English'}
          </Button>
        ))}
      </div>
      {version && <span>dmOffice {version}</span>}
    </div>
  )
}

function Login({ onDone }: { onDone: () => void }) {
  const s = useStyles()
  const [login, setLogin] = useState('')
  const [password, setPassword] = useState('')
  const [code, setCode] = useState('')
  const [needCode, setNeedCode] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      const r = await api.post('/api/auth/login', { login, password, code: needCode ? code : undefined })
      if (r?.twoFactor) setNeedCode(true)
      else onDone()
    } catch (err: any) {
      setError(err.message)
      if (err.data?.twoFactor) setCode('')
    } finally {
      setBusy(false)
    }
  }

  return (
    <Center>
      <form className={s.card} onSubmit={submit}>
        <Logo />
        {!needCode ? (
          <>
            <Field label={t('Anmeldename')}><Input value={login} onChange={(_, d) => setLogin(d.value)} autoFocus autoComplete="username" /></Field>
            <Field label={t('Passwort')}><Input type="password" value={password} onChange={(_, d) => setPassword(d.value)} autoComplete="current-password" /></Field>
          </>
        ) : (
          <>
            <Text>{t('Code aus der Authenticator-App – oder ein Wiederherstellungscode.')}</Text>
            <Field label={t('Code')}>
              <Input value={code} onChange={(_, d) => setCode(d.value)} autoFocus inputMode="numeric" autoComplete="one-time-code" />
            </Field>
          </>
        )}
        {error && <MessageBar intent="error"><MessageBarBody>{tError(error)}</MessageBarBody></MessageBar>}
        <Button appearance="primary" type="submit" disabled={busy}>{busy ? <Spinner size="tiny" /> : needCode ? t('Bestätigen') : t('Anmelden')}</Button>
        {needCode && <Button appearance="subtle" onClick={() => { setNeedCode(false); setCode('') }}>{t('Zurück')}</Button>}
        <CardFooter />
      </form>
    </Center>
  )
}

function Setup({ onDone, needCode }: { onDone: () => void; needCode?: boolean }) {
  const s = useStyles()
  const [login, setLogin] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [password, setPassword] = useState('')
  const [repeat, setRepeat] = useState('')
  const [code, setCode] = useState('')
  const [error, setError] = useState<string | null>(null)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (password !== repeat) { setError(t('Die Passwörter sind verschieden.')); return }
    try {
      await api.post('/api/auth/setup', { login, displayName, password, code })
      onDone()
    } catch (err: any) {
      setError(err.message)
    }
  }

  return (
    <Center>
      <form className={s.card} onSubmit={submit}>
        <Logo />
        <Text>{t('Erster Start: Lege das Verwaltungskonto an. Weitere Benutzer kommen danach unter Verwaltung dazu.')}</Text>
        {needCode && (
          <Field label={t('Einrichtungscode')} hint={t('Steht im Server-Protokoll (docker logs).')}>
            <Input value={code} onChange={(_, d) => setCode(d.value)} autoFocus />
          </Field>
        )}
        <Field label={t('Anmeldename')} hint={t('a–z, 0–9, Punkt, Bindestrich')}>
          <Input value={login} onChange={(_, d) => setLogin(d.value)} autoFocus={!needCode} />
        </Field>
        <Field label={t('Anzeigename')}><Input value={displayName} onChange={(_, d) => setDisplayName(d.value)} /></Field>
        <Field label={t('Passwort')} hint={t('mindestens 8 Zeichen')}><Input type="password" value={password} onChange={(_, d) => setPassword(d.value)} /></Field>
        <Field label={t('Passwort wiederholen')}><Input type="password" value={repeat} onChange={(_, d) => setRepeat(d.value)} /></Field>
        {error && <MessageBar intent="error"><MessageBarBody>{tError(error)}</MessageBarBody></MessageBar>}
        <Button appearance="primary" type="submit">{t('Einrichten')}</Button>
        <CardFooter />
      </form>
    </Center>
  )
}

/** Fuer die Verwaltung: irgendwo wartet eine Datenaenderung auf Platz oder Freigabe - unten einblenden, ein Klick fuehrt zum Assistenten. */
function UpdateAttention() {
  const s = useStyles()
  const narrow = useIsMobile()
  const open = () => {
    try { localStorage.setItem('dmoffice.admin.tab', 'updates') } catch { /* egal */ }
    if (location.hash.replace(/^#\/?/, '') === 'admin') location.reload()
    else location.hash = '#admin'
  }
  return (
    <MessageBar intent="warning" className={mergeClasses(s.attention, 'dm-nodrag')} layout={narrow ? 'multiline' : 'singleline'}>
      <MessageBarBody>{t('Eine Datenänderung wartet auf Platz oder deine Freigabe.')}</MessageBarBody>
      <MessageBarActions><Button size="small" appearance="primary" onClick={open}>{t('Öffnen')}</Button></MessageBarActions>
    </MessageBar>
  )
}

/** Neue Version auf dem Server: oben einblenden, ein Klick laedt neu. */
function UpdateBar() {
  const s = useStyles()
  const { update } = usePwa()
  // Handy: Text und Knopf untereinander, sonst ragt der Knopf aus dem Bild
  const narrow = useIsMobile()
  if (!update) return null
  return (
    <MessageBar intent="info" className={mergeClasses(s.update, 'dm-nodrag')} layout={narrow ? 'multiline' : 'singleline'}>
      <MessageBarBody>{t('Neue Version von dmOffice verfügbar.')}</MessageBarBody>
      <MessageBarActions><Button size="small" appearance="primary" onClick={applyUpdate}>{t('Neu laden')}</Button></MessageBarActions>
    </MessageBar>
  )
}
