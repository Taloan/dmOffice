// Tresor (docs/tresor-konzept.md): Stand, Öffnen-Dialog für jede Antwort 423 { locked: "tresor" } (api.ts wiederholt danach die
// Anfrage), gemerktes Gerät („nie schließen“) und Schließen. /api/tresor/setup und /open laufen direkt (nicht im Umschlag), damit
// das Cookie des Tresors ankommt.
import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, FluentProvider, Input, Link,
  MessageBar, MessageBarBody, Text, tokens,
} from '@fluentui/react-components'
import { LockClosed20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { api } from '../api'
import { t } from '../i18n'
import { darkTheme, lightTheme, storedTheme } from '../theme'

export interface TresorDevice { id: string; name: string; at: string; used: string; app: boolean; mine: boolean }
export interface TresorState {
  exists: boolean; user: string; open?: boolean; openAnywhere?: boolean
  settings?: { autoClose: '1h' | '8h' | '1d' | 'never'; closeOnAdminReset: boolean; closeOnScreenLock: boolean; dav?: boolean }
  codesLeft?: number; size?: number; pending?: number; devices?: TresorDevice[]
  /** Noch nicht als Cryptomator-Tresor angelegt: wird beim nächsten Öffnen mit dem Tresor-Passwort umgestellt */
  oldFormat?: boolean
  /** Umgestellt ohne Passwort: masterkey.cryptomator fehlt noch */
  needsPassword?: boolean
  remembered?: { id: string; kind: 'remember' | 'biometric'; name: string | null; created: string }[]
  passkeys?: { id: string; name: string | null; created: string; credentialId: string; salt: string }[]
}

/** Nach Öffnen, Schließen oder Einrichten: Seitenleiste, Dateien und Einstellungen laden neu. */
export const TRESOR_EVENT = 'dmoffice:tresor'
export const tresorChanged = () => window.dispatchEvent(new Event(TRESOR_EVENT))

export const tresorState = () => api.get<TresorState>('/api/tresor')

/** Name dieses Browsers für die Liste der Geräte („Firefox unter Windows“). */
export function deviceName() {
  const ua = navigator.userAgent
  const browser = /Edg\//.test(ua) ? 'Edge' : /Firefox\//.test(ua) ? 'Firefox' : /Chrome\//.test(ua) ? 'Chrome' : /Safari\//.test(ua) ? 'Safari' : t('Browser')
  const os = /Windows/.test(ua) ? 'Windows' : /Android/.test(ua) ? 'Android' : /iPhone|iPad/.test(ua) ? 'iOS' : /Mac OS/.test(ua) ? 'macOS' : /Linux/.test(ua) ? 'Linux' : ''
  return os ? t('{browser} unter {os}', { browser, os }) : browser
}

// ---------------- Gemerktes Gerät ----------------

// Das Geheimnis des Geräts liegt nur verschlüsselt im Browser-Speicher. Der Schlüssel dazu ist ein nicht exportierbarer
// WebCrypto-Schlüssel in IndexedDB: Skripte dieser Seite können ihn benutzen, aber nicht auslesen - eine Kopie des
// Browser-Speichers allein öffnet den Tresor nicht. Ohne sichere Verbindung (http) gibt es WebCrypto nicht: dann kein Merken.

const rememberKey = (user: string) => 'dm.tresor.device.' + user
interface Remembered { id: string; secret: string }
interface Sealed { id: string; iv: string; data: string }

/** Merken geht nur über https (oder localhost) mit WebCrypto und IndexedDB. */
export const canRemember = () => window.isSecureContext && !!globalThis.crypto?.subtle && 'indexedDB' in window

function keyStore<T>(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    const open = indexedDB.open('dm-tresor', 1)
    open.onupgradeneeded = () => open.result.createObjectStore('keys')
    open.onerror = () => reject(open.error)
    open.onsuccess = () => {
      const db = open.result
      const req = run(db.transaction('keys', mode).objectStore('keys'))
      req.onsuccess = () => { resolve(req.result); db.close() }
      req.onerror = () => { reject(req.error); db.close() }
    }
  })
}

async function remembered(user: string): Promise<Remembered | null> {
  let raw: (Partial<Remembered> & Partial<Sealed>) | null
  try { raw = JSON.parse(localStorage.getItem(rememberKey(user)) ?? 'null') } catch { return null }
  if (!raw?.id) return null
  // Früher im Klartext gemerkt: verschlüsselt neu ablegen (ohne WebCrypto: vergessen)
  if (raw.secret) { const d = { id: raw.id, secret: raw.secret }; await remember(user, canRemember() ? d : null); return canRemember() ? d : null }
  if (!raw.iv || !raw.data || !canRemember()) return null
  try {
    const key = await keyStore<CryptoKey | undefined>('readonly', st => st.get(user))
    if (!key) return null
    const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: fromB64(raw.iv) }, key, fromB64(raw.data))
    return { id: raw.id, secret: new TextDecoder().decode(plain) }
  } catch { return null }
}

async function remember(user: string, d: Remembered | null) {
  try {
    if (!d) {
      localStorage.removeItem(rememberKey(user))
      if (canRemember()) await keyStore('readwrite', st => st.delete(user)).catch(() => {})
      return
    }
    if (!canRemember()) return
    const key = await crypto.subtle.generateKey({ name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt'])
    const iv = crypto.getRandomValues(new Uint8Array(12))
    const data = await crypto.subtle.encrypt({ name: 'AES-GCM', iv }, key, new TextEncoder().encode(d.secret))
    await keyStore('readwrite', st => st.put(key, user))
    const sealed: Sealed = { id: d.id, iv: b64(iv), data: b64(data) }
    localStorage.setItem(rememberKey(user), JSON.stringify(sealed))
  } catch { /* privates Fenster, kein Speicher: nicht gemerkt */ }
}

/** Öffnen (direkt, nicht im Umschlag - das Cookie muss ankommen). Fehler: Meldung des Servers. */
async function openRaw(body: object): Promise<{ codesLeft: number; usedCode: boolean; device: Remembered | null }> {
  const r = await fetch('/api/tresor/open', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ name: deviceName(), ...body }) })
  const data = await r.json().catch(() => ({}))
  if (!r.ok) throw Object.assign(new Error(data.error ?? t('Der Tresor ließ sich nicht öffnen.')), { forgotten: !!data.forgotten })
  return data
}

/** Von Hand geschlossen (in diesem Fenster): das gemerkte Gerät öffnet dann nicht still wieder - erst, wenn man selbst öffnet. */
const closedKey = 'dm.tresor.closed'
const closedByHand = () => { try { return sessionStorage.getItem(closedKey) === '1' } catch { return false } }
const setClosedByHand = (v: boolean) => { try { if (v) sessionStorage.setItem(closedKey, '1'); else sessionStorage.removeItem(closedKey) } catch { /* ohne Speicher */ } }

/** Gemerktes Gerät: ohne Eingabe öffnen (nach einem Neustart des Servers). true: offen. */
export async function openRemembered(st: TresorState): Promise<boolean> {
  if (st.settings?.autoClose !== 'never' || closedByHand()) return false
  const d = await remembered(st.user)
  if (!d) return false
  try { await openRaw({ deviceId: d.id, secret: d.secret }); tresorChanged(); return true }
  catch (e) { if ((e as { forgotten?: boolean }).forgotten) await remember(st.user, null); return false }
}

/** Tresor schließen auf diesem Gerät. Gemerkt bleibt das Gerät trotzdem (das öffnet erst nach einem Neustart des Servers wieder). */
export async function closeTresor() {
  setClosedByHand(true)
  await api.post('/api/tresor/close')
  tresorChanged()
}

/** Merken auf diesem Gerät vergessen (beim Ändern von „Automatisch schließen“ oder Entfernen in der Liste). */
export const forgetThisDevice = (user: string) => { void remember(user, null) }

// ---------------- Passkey (WebAuthn mit PRF) ----------------

const b64 = (b: ArrayBuffer | Uint8Array) => btoa(String.fromCharCode(...new Uint8Array(b instanceof Uint8Array ? b : new Uint8Array(b))))
const b64u = (b: ArrayBuffer | Uint8Array) => b64(b).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
const fromB64 = (s: string) => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/') + '==='.slice((s.length + 3) % 4)), c => c.charCodeAt(0))

export const passkeysPossible = () => typeof window.PublicKeyCredential === 'function' && window.isSecureContext

/** Ergebnis der PRF-Erweiterung (in den Typen des Browsers noch nicht überall beschrieben). */
type Prf = { enabled?: boolean; results?: { first?: ArrayBuffer } }
const prfOf = (c: PublicKeyCredential) => (c.getClientExtensionResults() as { prf?: Prf }).prf

/**
 * Passkey für den Tresor hinterlegen (Tresor offen): der Passkey liefert über die PRF-Erweiterung zu einem zufälligen Salz ein
 * Geheimnis - nur er kann es nach Windows Hello, Fingerabdruck oder PIN wieder erzeugen. Kann er das nicht (ältere Passkeys,
 * manche Sicherheitsschlüssel), gibt es eine verständliche Meldung statt eines Passkeys, der den Tresor nicht öffnet.
 */
export async function addTresorPasskey(user: string, login: string) {
  const salt = crypto.getRandomValues(new Uint8Array(32))
  const cred = await navigator.credentials.create({
    publicKey: {
      challenge: crypto.getRandomValues(new Uint8Array(32)), rp: { id: location.hostname, name: 'dmOffice' },
      // Eigene Kennung für den Tresor - ein Passkey der Bereichssperre auf demselben Gerät bleibt daneben bestehen
      user: { id: new TextEncoder().encode(user + ':tresor'), name: login, displayName: t('{login} – Tresor', { login }) },
      pubKeyCredParams: [{ type: 'public-key', alg: -7 }, { type: 'public-key', alg: -257 }],
      authenticatorSelection: { userVerification: 'required', residentKey: 'preferred' },
      attestation: 'none', timeout: 60_000,
      extensions: { prf: { eval: { first: salt } } } as AuthenticationExtensionsClientInputs,
    },
  }) as PublicKeyCredential | null
  if (!cred) return false
  let first = prfOf(cred)?.results?.first
  if (!first) {
    if (prfOf(cred)?.enabled === false || !prfOf(cred)) throw new Error(t('Dieser Passkey kann den Tresor nicht öffnen: er liefert keinen Schlüssel (WebAuthn-Erweiterung PRF). Windows Hello in einer aktuellen Version, Passkeys von Google oder Apple und viele Sicherheitsschlüssel können das.'))
    // Manche Passkeys liefern den Schlüssel erst beim ersten Anmelden
    const again = await navigator.credentials.get({
      publicKey: {
        challenge: crypto.getRandomValues(new Uint8Array(32)), rpId: location.hostname, userVerification: 'required', timeout: 60_000,
        allowCredentials: [{ type: 'public-key', id: cred.rawId }],
        extensions: { prf: { eval: { first: salt } } } as AuthenticationExtensionsClientInputs,
      },
    }) as PublicKeyCredential | null
    first = again ? prfOf(again)?.results?.first : undefined
    if (!first) throw new Error(t('Der Passkey hat keinen Schlüssel geliefert. Bitte einen anderen Passkey verwenden.'))
  }
  await api.post('/api/tresor/passkey', { credentialId: b64u(cred.rawId), salt: b64(salt), secret: b64(first), name: deviceName() })
  tresorChanged()
  return true
}

/** Mit einem hinterlegten Passkey öffnen. true: offen. */
export async function openWithPasskey(st: TresorState): Promise<boolean> {
  const keys = st.passkeys ?? []
  if (!keys.length) return false
  const evalBy: Record<string, { first: Uint8Array }> = {}
  for (const k of keys) evalBy[k.credentialId] = { first: fromB64(k.salt) }
  const cred = await navigator.credentials.get({
    publicKey: {
      challenge: crypto.getRandomValues(new Uint8Array(32)), rpId: location.hostname, userVerification: 'required', timeout: 60_000,
      allowCredentials: keys.map(k => ({ type: 'public-key' as const, id: fromB64(k.credentialId) })),
      extensions: { prf: { evalByCredential: evalBy } } as AuthenticationExtensionsClientInputs,
    },
  }) as PublicKeyCredential | null
  if (!cred) return false
  const key = keys.find(k => k.credentialId === b64u(cred.rawId))
  const first = prfOf(cred)?.results?.first
  if (!key || !first) throw new Error(t('Der Passkey hat keinen Schlüssel geliefert.'))
  await openRaw({ deviceId: key.id, secret: b64(first) })
  setClosedByHand(false)
  tresorChanged()
  return true
}

// ---------------- Öffnen-Dialog ----------------

let pending: Promise<boolean> | null = null

/** Öffnen-Dialog (einer zugleich). Versucht zuerst ein gemerktes Gerät. true: offen. */
export function requestTresorOpen(): Promise<boolean> {
  if (pending) return pending
  pending = (async () => {
    const st = await tresorState().catch(() => null)
    if (!st?.exists) return false
    if (st.open) return true
    if (await openRemembered(st)) return true
    return new Promise<boolean>(resolve => {
      const host = document.createElement('div')
      document.body.appendChild(host)
      const root = createRoot(host)
      const done = (r: boolean) => { root.unmount(); host.remove(); if (r) tresorChanged(); resolve(r) }
      const dark = storedTheme() === 'dark' || (storedTheme() === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
      root.render(<FluentProvider theme={dark ? darkTheme : lightTheme}><OpenDialog st={st} done={done} /></FluentProvider>)
    })
  })().finally(() => { pending = null })
  return pending
}

function OpenDialog({ st, done }: { st: TresorState; done: (ok: boolean) => void }) {
  const [password, setPassword] = useState('')
  const [useCode, setUseCode] = useState(false)
  const [keep, setKeep] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  // Nach einem Wiederherstellungscode: gleich ein neues Tresor-Passwort anbieten
  const [afterCode, setAfterCode] = useState<number | null>(null)
  const never = st.settings?.autoClose === 'never'
  const hasPasskey = passkeysPossible() && (st.passkeys ?? []).length > 0
  const passkey = async () => {
    setBusy(true); setError(null)
    try { if (await openWithPasskey(st)) done(true) }
    catch (e) { setError(e instanceof Error && e.name !== 'NotAllowedError' ? e.message : t('Abgebrochen.')) }
    finally { setBusy(false) }
  }

  const go = async () => {
    setBusy(true); setError(null)
    try {
      const r = await openRaw(useCode ? { code: password } : { password, remember: never && keep && canRemember() })
      setClosedByHand(false)
      if (r.device) await remember(st.user, r.device)
      if (r.usedCode) setAfterCode(r.codesLeft); else done(true)
    } catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(false) }
  }
  if (afterCode != null) return <NewPasswordDialog codesLeft={afterCode} done={() => done(true)} />
  return (
    <Dialog open onOpenChange={(_, x) => !x.open && done(false)}>
      <DialogSurface style={{ maxWidth: 420 }}>
        <form onSubmit={e => { e.preventDefault(); if (password) go() }}>
          <DialogBody>
            <DialogTitle action={null}><span style={{ display: 'inline-flex', gap: 8, alignItems: 'center' }}><LockClosed20Regular />{t('Tresor öffnen')}</span></DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
              {hasPasskey && <Button appearance="primary" onClick={passkey} disabled={busy}>{t('Mit Passkey oder Windows Hello öffnen')}</Button>}
              <Field label={useCode ? t('Wiederherstellungscode oder Wiederherstellungsschlüssel') : t('Tresor-Passwort')}>
                <Input type={useCode ? 'text' : 'password'} value={password} onChange={(_, d) => setPassword(d.value)} autoFocus={!hasPasskey} autoComplete="off" />
              </Field>
              {!useCode && never && canRemember() && <Checkbox checked={keep} onChange={(_, d) => setKeep(!!d.checked)}
                label={t('Auf diesem Gerät merken – nach einem Neustart des Servers öffnet sich der Tresor hier von selbst')} />}
              <Link onClick={() => { setUseCode(!useCode); setPassword('') }}>{useCode ? t('Tresor-Passwort eingeben') : t('Passwort vergessen? Wiederherstellungscode verwenden')}</Link>
            </DialogContent>
            <DialogActions>
              <Button onClick={() => done(false)}>{t('Abbrechen')}</Button>
              <Button appearance={hasPasskey ? 'secondary' : 'primary'} type="submit" disabled={!password || busy}>{t('Öffnen')}</Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  )
}

/** Nach dem Öffnen mit einem Code: neues Tresor-Passwort festlegen (das alte braucht es dafür nicht, der Tresor ist offen). */
function NewPasswordDialog({ codesLeft, done }: { codesLeft: number; done: () => void }) {
  const [pw, setPw] = useState('')
  const [pw2, setPw2] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const save = async () => {
    if (pw !== pw2) { setError(t('Die beiden Passwörter stimmen nicht überein.')); return }
    setBusy(true); setError(null)
    try { await api.post('/api/tresor/password', { current: '', password: pw }); done() }
    catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, x) => !x.open && done()}>
      <DialogSurface style={{ maxWidth: 420 }}>
        <form onSubmit={e => { e.preventDefault(); if (pw) save() }}>
          <DialogBody>
            <DialogTitle>{t('Neues Tresor-Passwort festlegen?')}</DialogTitle>
            <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <Text>{t('Der Tresor ist offen. Der Code ist damit verbraucht, es sind noch {n} Codes übrig.', { n: codesLeft })}</Text>
              {error && <MessageBar intent="error"><MessageBarBody>{error}</MessageBarBody></MessageBar>}
              <Field label={t('Neues Tresor-Passwort')} hint={t('Mindestens 10 Zeichen, nicht dasselbe wie zum Anmelden.')}>
                <Input type="password" value={pw} onChange={(_, d) => setPw(d.value)} autoFocus autoComplete="new-password" />
              </Field>
              <Field label={t('Neues Tresor-Passwort wiederholen')}>
                <Input type="password" value={pw2} onChange={(_, d) => setPw2(d.value)} autoComplete="new-password" />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={done}>{t('Später')}</Button>
              <Button appearance="primary" type="submit" disabled={!pw || busy}>{t('Speichern')}</Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  )
}

/** Stand des Tresors für Seitenleiste und Kontomenü; lädt bei jeder Änderung neu. */
export function useTresor() {
  const [st, setSt] = useState<TresorState | null>(null)
  useEffect(() => {
    let alive = true
    const load = () => tresorState().then(x => { if (alive) setSt(x) }).catch(() => {})
    load()
    window.addEventListener(TRESOR_EVENT, load)
    return () => { alive = false; window.removeEventListener(TRESOR_EVENT, load) }
  }, [])
  return st
}

/** Farbe für „offen“ - überall gleich (Seitenleiste von Dateien und Notizen, Web und App). */
export const OPEN_COLOR = tokens.colorPaletteGreenForeground1

/** Der Tresor in der Dateiansicht: Pfad „tresor:“, darunter „tresor:/Ordner/Datei“ (docs/tresor-cryptomator-konzept.md, Stufe 3a). */
export const TRESOR = 'tresor:'
export const isTresor = (p?: string | null) => !!p && p.startsWith(TRESOR)
/** Eigener Cryptomator-Tresor, entsperrt: „cmv:/Ordner/Tresor/…“ (Stufe 3b). */
export const CMV = 'cmv:'
export const isCmv = (p?: string | null) => !!p && p.startsWith(CMV)
/** Verschlüsselt abgelegt (System-Tresor oder eigener Cryptomator-Tresor): kein Teilen, keine Favoriten, keine Suche, kein Papierkorb. */
export const isEncrypted = (p?: string | null) => isTresor(p) || isCmv(p)
/** Entsperrte eigene Tresore dieser Sitzung (Wurzelpfade) - um bei „gesperrt“ (423) zum Ordner zurückzufinden. */
export const cmvRoots = {
  get: (): string[] => { try { return JSON.parse(sessionStorage.getItem('dm.cmvRoots') ?? '[]') } catch { return [] } },
  add: (root: string) => { try { sessionStorage.setItem('dm.cmvRoots', JSON.stringify([...new Set([...cmvRoots.get(), root])])) } catch { /* ohne Speicher */ } },
}
