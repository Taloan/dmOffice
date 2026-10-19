// Verschluesselter Umschlag zwischen Browser und dmOffice (Gegenstueck zu E2e.cs), zusaetzlich zu TLS:
// ECDH P-256 zwischen einem Schluessel dieses Tabs und dem Serverschluessel, HKDF-SHA256, AES-256-GCM.
// Jede Anfrage an /api geht als Chiffrat an /api/e2e/{user}/{device}, die Antwort kommt genauso zurueck.
// Downloads ueber Links (Anhaenge, Exporte) laufen weiter direkt ueber TLS.

const VERSION = 'dmoffice-e2e-v1'
const STORE = 'dmoffice.e2e'

interface State { deviceId: string; userId: string; serverKey: string; priv: JsonWebKey; counter: number }

let state: State | null = null
let aesKey: CryptoKey | null = null
let ready: Promise<boolean> | null = null
let disabled = false   // Server ohne Umschlag oder kein WebCrypto: alles direkt

const b64u = (b: ArrayBuffer | Uint8Array) => btoa(String.fromCharCode(...new Uint8Array(b))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
const unb64u = (s: string) => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/').padEnd(s.length + ((4 - (s.length % 4)) % 4), '=')), c => c.charCodeAt(0))
const b64 = (b: ArrayBuffer | Uint8Array) => { let s = ''; const a = new Uint8Array(b); for (let i = 0; i < a.length; i += 0x8000) s += String.fromCharCode(...a.subarray(i, i + 0x8000)); return btoa(s) }
const unb64 = (s: string) => Uint8Array.from(atob(s), c => c.charCodeAt(0))
const utf8 = (s: string) => new TextEncoder().encode(s)

function load(): State | null {
  try { const s = sessionStorage.getItem(STORE); return s ? JSON.parse(s) : null } catch { return null }
}
function save() { try { if (state) sessionStorage.setItem(STORE, JSON.stringify(state)); else sessionStorage.removeItem(STORE) } catch { /* privat */ } }

async function deriveKey(priv: JsonWebKey, serverKey: string, devicePub: string): Promise<CryptoKey> {
  const privKey = await crypto.subtle.importKey('jwk', priv, { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits'])
  const pubKey = await crypto.subtle.importKey('raw', unb64u(serverKey), { name: 'ECDH', namedCurve: 'P-256' }, false, [])
  const shared = await crypto.subtle.deriveBits({ name: 'ECDH', public: pubKey }, privKey, 256)
  const ikm = await crypto.subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey'])
  const salt = new Uint8Array([...unb64u(devicePub), ...unb64u(serverKey)])
  return crypto.subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt, info: utf8(VERSION) }, ikm, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt'])
}

/** Schluessel dieses Tabs beim Server anmelden (mit dem Cookie). */
async function register(): Promise<boolean> {
  if (!crypto?.subtle) return false
  const keyRes = await fetch('/api/e2e/key', { credentials: 'same-origin' })
  if (keyRes.status === 404) return false
  if (!keyRes.ok) throw new Error('e2e key ' + keyRes.status)
  const { key: serverKey } = await keyRes.json()
  const pair = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, true, ['deriveBits'])
  const pub = b64u(await crypto.subtle.exportKey('raw', pair.publicKey))
  const priv = await crypto.subtle.exportKey('jwk', pair.privateKey)
  const reg = await fetch('/api/e2e/register', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ pubKey: pub }) })
  if (reg.status === 401) return false   // nicht angemeldet: spaeter noch einmal
  if (!reg.ok) throw new Error('e2e register ' + reg.status)
  const r = await reg.json()
  if (r.serverKey !== serverKey) throw new Error('Serverschlüssel passt nicht')
  state = { deviceId: r.deviceId, userId: r.userId, serverKey, priv, counter: 0 }
  aesKey = await deriveKey(priv, serverKey, pub)
  save()
  return true
}

async function ensure(): Promise<boolean> {
  if (disabled) return false
  if (aesKey && state) return true
  if (!ready) ready = (async () => {
    try {
      const s = load()
      if (s) {
        state = s
        const pub = b64u(await crypto.subtle.exportKey('raw', await crypto.subtle.importKey('jwk', { ...s.priv, d: undefined, key_ops: [] }, { name: 'ECDH', namedCurve: 'P-256' }, true, [])))
        aesKey = await deriveKey(s.priv, s.serverKey, pub)
        return true
      }
      const ok = await register()
      if (!ok) { ready = null }   // beim naechsten Aufruf erneut versuchen (z. B. nach dem Anmelden)
      return ok
    } catch (e) {
      console.warn('E2E-Umschlag nicht verfuegbar:', e)
      disabled = true
      return false
    }
  })()
  return ready
}

function aad(counter: number, response: boolean) { return utf8(`${VERSION}|${state!.deviceId}|${counter}${response ? '|resp' : ''}`) }

/** Ersatz fuer fetch: alles unter /api im Umschlag, sonst direkt. */
export async function e2eFetch(input: string, init: RequestInit = {}): Promise<Response> {
  const url = typeof input === 'string' ? input : String(input)
  // /api/auth: Anmelden, Abmelden, 2FA, Status - setzen Cookies (geht aus dem Umschlag nicht) und laufen vor dem Login
  if (!url.startsWith('/api/') || url.startsWith('/api/e2e/') || url.startsWith('/api/auth/') || !(await ensure())) return fetch(url, { credentials: 'same-origin', ...init })
  const method = (init.method || 'GET').toUpperCase()
  const headers: Record<string, string> = {}
  new Headers(init.headers || {}).forEach((v, k) => { headers[k] = v })
  let body: string | null = null
  if (init.body != null) {
    if (init.body instanceof FormData) {
      const r = new Response(init.body)
      headers['Content-Type'] = r.headers.get('content-type') || 'multipart/form-data'
      body = b64(await r.arrayBuffer())
    } else if (typeof init.body === 'string') body = b64(utf8(init.body))
    else body = b64(await new Response(init.body as BodyInit).arrayBuffer())
  }
  const counter = ++state!.counter
  save()
  const inner = utf8(JSON.stringify({ method, path: url, headers, body }))
  const nonce = crypto.getRandomValues(new Uint8Array(12))
  const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad(counter, false) }, aesKey!, inner)
  const sealed = new Uint8Array(12 + cipher.byteLength); sealed.set(nonce); sealed.set(new Uint8Array(cipher), 12)
  const res = await fetch(`/api/e2e/${state!.userId}/${state!.deviceId}`, {
    method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/octet-stream', 'X-DM-Counter': String(counter) }, body: sealed,
  })
  if (res.status === 404) {
    // Geraet unbekannt (abgemeldet, Passwort geaendert, anderer Benutzer): neu anmelden und einmal wiederholen
    state = null; aesKey = null; ready = null; save()
    if (await ensure()) return e2eFetch(input, init)
    return fetch(url, { credentials: 'same-origin', ...init })
  }
  if (!res.ok) return res
  const data = new Uint8Array(await res.arrayBuffer())
  const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: data.subarray(0, 12), additionalData: aad(counter, true) }, aesKey!, data.subarray(12))
  const outer = JSON.parse(new TextDecoder().decode(plain)) as { status: number; headers: Record<string, string>; body: string | null }
  const h = new Headers()
  for (const [k, v] of Object.entries(outer.headers || {})) if (!/^content-length$/i.test(k)) h.set(k, v)
  const bytes = outer.body ? unb64(outer.body) : null
  return new Response(outer.status === 204 || outer.status === 304 ? null : bytes, { status: outer.status, headers: h })
}

/** Beim Abmelden: Geraeteschluessel beim Server loeschen (sonst laeuft er nach 30 Tagen ab). */
export function e2eForget() {
  const s = state ?? load()
  state = null; aesKey = null; ready = null; save()
  if (s) try { navigator.sendBeacon(`/api/e2e/unregister/${s.deviceId}`) } catch { /* egal */ }
}

export function e2eActive(): boolean { return !!aesKey && !!state }

