import { askConfirm } from './shell/context'
import {
  Button, Field, FluentProvider, Input, MessageBar, MessageBarActions, MessageBarBody, Spinner, Textarea, Title2, Text, makeStyles, mergeClasses, tokens,
  Toaster, useId, useToastController, Toast, ToastTitle, ToastBody, Link as FluentLink,
  Select,
} from '@fluentui/react-components'
import { createContext, useCallback, useContext, useEffect, useState } from 'react'
import { chooseLanguage, lang, t, tError } from './i18n'
import { api, demoMode, features, fmtSize, Me, OfflineError } from './api'
import { applyBrand, brandLogo, storeTheme, themesFor, ThemeChoice, useIsDark } from './theme'
import { currentPrefs, PrefsContext, usePrefsState, usePrefsVersion } from './prefs'
import { Shell } from './shell/Shell'
import { MailWindow, isMailWindow } from './mail/MailWindow'
import { applyUpdate, usePwa } from './pwa'
import { useIsMobile } from './shell/mobile'
import { UpdateGate, UpdateState } from './admin/UpdatePanel'
import { e2eForget } from './e2e'

interface AppCtx {
  me: Me
  notify: (title: string, body?: string, intent?: 'success' | 'error' | 'info' | 'warning') => void
  fail: (e: unknown) => void
  refreshMe: () => void
  /** Meldung mit „Rückgängig“ direkt nach einer Aktion */
  undoable: (title: string, undo: () => Promise<void>) => void
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
  useEffect(() => applyBrand(currentPrefs.accent), [currentPrefs.accent])
  return (
    <FluentProvider theme={dark ? th.dark : th.light} style={{ height: '100%' }}>
      <Root />
    </FluentProvider>
  )
}

function Root() {
  const [state, setState] = useState<{ setupNeeded: boolean; setupCode?: boolean; loggedIn: boolean; user: Me | null; demo?: { login: string; password: string } | null; ended?: boolean } | null>(null)
  const [upd, setUpd] = useState<UpdateState | null>(null)
  // Remount der Programme, sobald die Datenaenderung des Benutzers durch ist
  const [round, setRound] = useState(0)
  const toasterId = useId('toaster')
  const { dispatchToast, dismissToast } = useToastController(toasterId)

  const load = useCallback(() => {
    api.get<{ setupNeeded: boolean; loggedIn: boolean; user: Me | null; demo?: { login: string; password: string } | null; featuresOff?: string[] }>('/api/auth/state')
      .then(s => { demoMode.on = !!s.demo; features.off = new Set(s.featuresOff ?? []); setState(s) }).catch(() => setState({ setupNeeded: false, loggedIn: false, user: null }))
  }, [])
  useEffect(load, [load])
  useEffect(() => {
    // Mitten in der Arbeit abgemeldet (nicht beim ersten Laden): die Anmeldeseite nennt den Grund
    const onUnauth = () => setState(s => (s ? { ...s, ended: s.ended || s.loggedIn, loggedIn: false, user: null } : s))
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
  const undoable = useCallback((title: string, undo: () => Promise<void>) => {
    const id = String(Date.now()) + Math.random()
    dispatchToast(<Toast><ToastTitle action={<FluentLink onClick={() => { dismissToast(id); undo().catch(e => notify(t('Fehler'), tError(e instanceof Error ? e.message : String(e)), 'error')) }}>{t('Rückgängig')}</FluentLink>}>{title}</ToastTitle></Toast>,
      { toastId: id, intent: 'success', timeout: 8000 })
  }, [dispatchToast, dismissToast, notify])
  const fail = useCallback((e: unknown) => {
    // Server nicht erreichbar: im Hintergrund (Fenster verdeckt, Rechner schläft) nicht melden - beim Zurückkommen lädt alles neu
    if (e instanceof OfflineError && document.hidden) return
    notify(t('Fehler'), tError(e instanceof Error ? e.message : String(e)), 'error')
  }, [notify])

  if (!state) return <Center><Spinner /></Center>
  return (
    <>
      <Toaster toasterId={toasterId} position="bottom-end" />
      <UpdateBar />
      {/* Schild mittig in der Titelleiste - ein eigener Streifen machte die Seite höher als das Fenster */}
      {state.demo && state.loggedIn && <div style={{ position: 'fixed', top: 6, left: '50%', transform: 'translateX(-50%)', zIndex: 1000, pointerEvents: 'none', maxWidth: '60vw',
        whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', fontSize: tokens.fontSizeBase100, padding: '1px 10px', borderRadius: 999,
        background: tokens.colorPaletteMarigoldBackground2, color: tokens.colorNeutralForeground1 }}>
        {t('Demo – alle Inhalte sind erfunden und werden jede Nacht zurückgesetzt.')}
      </div>}
      {state.setupNeeded ? <Setup onDone={load} needCode={state.setupCode} />
        : !state.loggedIn || !state.user ? <Login onDone={load} demo={state.demo} ended={state.ended} />
        : (
          <Ctx.Provider value={{ me: state.user, notify, fail, refreshMe: load, undoable }}>
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
      <img src={brandLogo(currentPrefs.accent)} width={36} height={36} alt="" />
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

function Login({ onDone, demo, ended }: { onDone: () => void; demo?: { login: string; password: string } | null; ended?: boolean }) {
  const s = useStyles()
  // Demo-Server: Zugangsdaten schon eingetragen
  const [login, setLogin] = useState(demo?.login ?? '')
  const [password, setPassword] = useState(demo?.password ?? '')
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
      else { e2eForget(); onDone() }   // Geraeteschluessel gehoert zum vorigen Benutzer dieses Browsers
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
        {ended && <MessageBar intent="warning" layout="multiline"><MessageBarBody>{t('Du wurdest abgemeldet. Das geschieht, wenn die Sitzung abläuft oder der Admin Passwort oder Rolle deines Kontos ändert. Bitte melde dich neu an.')}</MessageBarBody></MessageBar>}
        {demo && <MessageBar intent="info"><MessageBarBody>{t('Demo-Zugang: Anmeldename „{login}“, Passwort „{password}“. Alles ist erfunden und wird jede Nacht zurückgesetzt.', { login: demo.login, password: demo.password })}</MessageBarBody></MessageBar>}
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
        {error && <MessageBar intent="error" layout="multiline"><MessageBarBody>{tError(error)}{!needCode && ' ' + t('Passwort vergessen? Der Admin deines dmOffice kann dir ein neues setzen.')}</MessageBarBody></MessageBar>}
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
  const [restore, setRestore] = useState(false)

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

  if (restore) return <RestoreFromS3 needCode={needCode} onBack={() => setRestore(false)} />
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
        <Button appearance="subtle" onClick={() => setRestore(true)}>{t('Stattdessen aus dem Core-Backup wiederherstellen …')}</Button>
        <CardFooter />
      </form>
    </Center>
  )
}

/**
 * Ersteinrichtung: alles aus einer S3-Sicherung zurueckholen. Braucht denselben Hauptschluessel (DMOFFICE_MASTER_KEY)
 * wie das alte System. Danach startet der Server neu und holt die Dateiinhalte im Hintergrund.
 */
function RestoreFromS3({ needCode, onBack }: { needCode?: boolean; onBack: () => void }) {
  const s = useStyles()
  const [f, setF] = useState({ code: '', target: 's3', endpoint: '', region: '', bucket: '', prefix: 'dmoffice', accessKey: '', secretKey: '', webDavUrl: '', webDavUser: '', webDavPassword: '', folder: '' })
  const [stands, setStands] = useState<{ name: string; created: string; bytes: number }[] | null>(null)
  const [pick, setPick] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState<{ users: string[] } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const set = (k: keyof typeof f) => (_: unknown, d: { value: string }) => setF(x => ({ ...x, [k]: d.value }))
  // Zugangsdaten für den Notfall (Verwaltung → Storage): eingefügt oder als Datei - der Server öffnet sie mit seinem Hauptschlüssel
  const [profile, setProfile] = useState('')
  const [filled, setFilled] = useState(false)
  // Zugangsdaten von Hand: erst auf Wunsch aufgeklappt (mit Wiederherstellungsschlüssel braucht man sie nicht)
  const [manual, setManual] = useState(false)
  const readProfile = async (text: string) => {
    setBusy(true); setError(null)
    try {
      // Gebraucht wird der Ort des Core-Backups (eigenes Ziel, sonst der Storage Extender) - die Dateien holt der Server danach selbst
      const p = (await api.post<{ core: Record<string, string | null> }>('/api/auth/restore/profile', { code: f.code, text })).core
      setF(x => ({ ...x, target: p.target ?? 's3', endpoint: p.endpoint ?? '', region: p.region ?? '', bucket: p.bucket ?? '', prefix: p.prefix ?? '', accessKey: p.accessKey ?? '',
        secretKey: p.secretKey ?? '', webDavUrl: p.webDavUrl ?? '', webDavUser: p.webDavUser ?? '', webDavPassword: p.webDavPassword ?? '', folder: p.folder ?? '' }))
      setFilled(true)
    } catch (e: any) { setError(e.message); setFilled(false) } finally { setBusy(false) }
  }
  const list = async () => {
    setBusy(true); setError(null)
    try { const r = await api.post<{ name: string; created: string; bytes: number }[]>('/api/auth/restore/list', f); setStands(r); setPick(r[0]?.name ?? '') }
    catch (e: any) { setError(e.message) } finally { setBusy(false) }
  }
  const run = async () => {
    setBusy(true); setError(null)
    try {
      // Vorab nennen, wie viele Dateien des Stands am Ziel fehlen - entscheiden muss der Admin
      const c = await api.post<{ checkedFiles: boolean; total: number; missing: number }>('/api/auth/restore/check', { ...f, name: pick }).catch(() => null)
      if (c?.checkedFiles && c.missing > 0 && !await askConfirm('', t('{missing} von {total} Dateien dieses Stands liegen nicht am Ziel. Trotzdem wiederherstellen?', { missing: c.missing, total: c.total }))) { setBusy(false); return }
      setDone(await api.post<{ users: string[] }>('/api/auth/restore/run', { ...f, name: pick }))
      // Der Server startet neu - warten, bis er wieder antwortet
      for (let i = 0; i < 90; i++) {
        await new Promise(r => setTimeout(r, 2000))
        try { const st = await api.get<{ setupNeeded?: boolean }>('/api/auth/state'); if (st.setupNeeded === false) { location.reload(); return } } catch { /* startet noch */ }
      }
    } catch (e: any) { setError(e.message); setBusy(false) }
  }
  if (done) return (
    <Center><div className={s.card}><Logo />
      <Text weight="semibold">{t('Wiederhergestellt')}</Text>
      <Text>{t('Benutzer: {list}. Der Server startet jetzt neu und holt danach die Dateiinhalte vom Ziel – das kann je nach Menge eine Weile dauern. Anmelden kannst du dich gleich mit deinem bisherigen Passwort.', { list: done.users.join(', ') })}</Text>
      <Spinner size="small" label={t('Warte auf den Neustart …')} />
    </div></Center>
  )
  return (
    <Center>
      <div className={s.card}>
        <Logo />
        <Text weight="semibold">{t('Aus dem Core-Backup wiederherstellen')}</Text>
        <Text size={200}>{t('Nur mit demselben DMOFFICE_MASTER_KEY wie auf dem alten Server. Die Dateien holt dmOffice danach selbst.')}</Text>
        {needCode && <Field label={t('Einrichtungscode')} hint={t('Steht im Server-Protokoll (docker logs).')}><Input value={f.code} onChange={set('code')} autoFocus /></Field>}
        <Field label={t('Wiederherstellungsschlüssel (optional)')}>
          <Textarea value={profile} onChange={(_, d) => { setProfile(d.value); setFilled(false) }} rows={2} style={{ fontFamily: 'Consolas, monospace' }} />
        </Field>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Button appearance="primary" disabled={busy || !profile.trim() || (needCode && !f.code.trim())} onClick={() => readProfile(profile)}>{t('Einlesen')}</Button>
          <Button disabled={busy || (needCode && !f.code.trim())} onClick={() => document.getElementById('restore-profile-file')?.click()}>{t('Datei wählen …')}</Button>
          <input id="restore-profile-file" type="file" accept=".txt,text/plain" hidden onChange={async e => { const file = e.target.files?.[0]; e.target.value = ''; if (file) { const text = await file.text(); setProfile(text); readProfile(text) } }} />
        </div>
        {filled && <MessageBar intent="success"><MessageBarBody>
          {t('Passt zum Hauptschlüssel.')} {f.target === 's3' ? `${f.endpoint.replace(/^https?:\/\//, '')} · ${f.bucket}${f.prefix ? '/' + f.prefix : ''}` : f.target === 'webdav' ? f.webDavUrl : f.folder}
        </MessageBarBody></MessageBar>}
        {!filled && !manual && <Button appearance="transparent" size="small" style={{ alignSelf: 'flex-start', padding: 0 }} onClick={() => setManual(true)}>{t('Ohne Wiederherstellungsschlüssel: Zugangsdaten eingeben …')}</Button>}
        {manual && !filled && <>
        <Field label={t('Wo liegt das Core-Backup?')}>
          <Select value={f.target} onChange={(_, d) => setF(x => ({ ...x, target: d.value }))}>
            <option value="s3">S3</option><option value="webdav">WebDAV</option><option value="folder">{t('Ordner auf dem Server')}</option>
          </Select>
        </Field>
        {f.target === 'webdav' && <>
          <Field label={t('Adresse')}><Input value={f.webDavUrl} onChange={set('webDavUrl')} placeholder="https://u123456.your-storagebox.de/dmoffice/" /></Field>
          <Field label={t('Benutzername')}><Input value={f.webDavUser} onChange={set('webDavUser')} autoComplete="off" /></Field>
          <Field label={t('Passwort')}><Input type="password" autoComplete="new-password" value={f.webDavPassword} onChange={set('webDavPassword')} /></Field>
        </>}
        {f.target === 'folder' && <Field label={t('Ordner')}><Input value={f.folder} onChange={set('folder')} placeholder="/backup" /></Field>}
        {f.target === 's3' && <>
        <Field label={t('Adresse')}><Input value={f.endpoint} onChange={set('endpoint')} placeholder="https://…" /></Field>
        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
          <Field label={t('Region')} style={{ flex: '1 1 90px', minWidth: 0 }}><Input style={{ width: '100%', minWidth: 0 }} value={f.region} onChange={set('region')} /></Field>
          <Field label={t('Bucket')} style={{ flex: '1 1 90px', minWidth: 0 }}><Input style={{ width: '100%', minWidth: 0 }} value={f.bucket} onChange={set('bucket')} /></Field>
        </div>
        {/* Eigene Zeile - in drei Spalten bricht der Name um */}
        <Field label={t('Ordner im Bucket')}><Input value={f.prefix} onChange={set('prefix')} placeholder="dmoffice" /></Field>
        <Field label={t('Zugangsschlüssel (Key ID)')}><Input value={f.accessKey} onChange={set('accessKey')} autoComplete="off" /></Field>
        <Field label={t('Geheimer Schlüssel')}><Input type="password" autoComplete="new-password" value={f.secretKey} onChange={set('secretKey')} /></Field>
        </>}
        </>}
        <Button onClick={list} disabled={busy || (f.target === 's3' ? !f.endpoint || !f.bucket || !f.accessKey || !f.secretKey : f.target === 'webdav' ? !f.webDavUrl : !f.folder)}>{t('Core-Backups suchen')}</Button>
        {stands && (stands.length === 0 ? <Text>{t('Dort liegt kein Core-Backup.')}</Text> : (
          <Field label={t('Stand')}>
            <Select value={pick} onChange={(_, d) => setPick(d.value)}>
              {stands.map(x => <option key={x.name} value={x.name}>{new Date(x.created).toLocaleString()} · {fmtSize(x.bytes)}</option>)}
            </Select>
          </Field>
        ))}
        {error && <MessageBar intent="error"><MessageBarBody>{tError(error)}</MessageBarBody></MessageBar>}
        {!!stands?.length && <Button appearance="primary" onClick={run} disabled={busy || !pick}>{busy ? t('Stelle wieder her …') : t('Diesen Stand wiederherstellen')}</Button>}
        <Button appearance="subtle" onClick={onBack} disabled={busy}>{t('Zurück zur Neueinrichtung')}</Button>
        <CardFooter />
      </div>
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
