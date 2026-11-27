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
const unb64 = (s: string) => { const t = atob(s), a = new Uint8Array(t.length); for (let i = 0; i < t.length; i++) a[i] = t.charCodeAt(i); return a }
/** Inhalt als Base64 über den Browser (FileReader) statt Byte für Byte in JavaScript: läuft neben der Seite und hält sie nicht an. */
const b64Blob = (b: Blob) => new Promise<string>((ok, fail) => {
  const r = new FileReader()
  r.onload = () => { const u = String(r.result); ok(u.slice(u.indexOf(',') + 1)) }
  r.onerror = () => fail(r.error ?? new Error('Lesen gescheitert'))
  r.readAsDataURL(b)
})
const utf8 = (s: string) => new TextEncoder().encode(s)

/** Neues Fenster aus window.open (eigenes Fenster, Ordner im neuen Tab): Der Browser kopiert sessionStorage mit,
 *  dieselbe Geraete-ID mit demselben Zaehler wuerde der Server als Wiederholung ablehnen. Solche Fenster melden
 *  sich deshalb als eigenes Geraet an; ein Neuladen desselben Fensters behaelt sein Geraet. */
function copiedFromOpener(): boolean {
  try {
    if (!window.opener) return false
    const nav = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming | undefined
    return nav?.type !== 'reload'
  } catch { return false }
}

let fresh = copiedFromOpener()

function load(): State | null {
  if (fresh) { fresh = false; try { sessionStorage.removeItem(STORE) } catch { /* privat */ } return null }
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

/** Nicht im Umschlag: Anmelden, Abmelden, 2FA, Status - setzen Cookies (geht aus dem Umschlag nicht) und laufen vor dem Login. */
const outside = (url: string) => !url.startsWith('/api/') || url.startsWith('/api/e2e/') || url.startsWith('/api/auth/') || url === '/api/tresor/setup' || url === '/api/tresor/open'

/** Rumpf einer Anfrage für den Innenteil: Base64; Dateien und Formulare über den Browser (b64Blob), nie Byte für Byte. */
async function innerBody(body: BodyInit | null | undefined, headers: Record<string, string>): Promise<string | null> {
  if (body == null) return null
  if (typeof body === 'string') return b64(utf8(body))
  if (body instanceof FormData) {
    const r = new Response(body)
    headers['content-type'] = r.headers.get('content-type') || 'multipart/form-data'
    return b64Blob(await r.blob())
  }
  if (body instanceof Blob) return b64Blob(body)
  return b64Blob(await new Response(body as BodyInit).blob())
}

/** Innenteil verschlüsseln; der Zähler wird erst hier vergeben (jeder Versand einen eigenen). */
async function seal(method: string, path: string, headers: Record<string, string>, body: string | null) {
  const counter = ++state!.counter
  save()
  const inner = utf8(JSON.stringify({ method, path, headers, body }))
  const nonce = crypto.getRandomValues(new Uint8Array(12))
  const cipher = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce, additionalData: aad(counter, false) }, aesKey!, inner)
  const sealed = new Uint8Array(12 + cipher.byteLength); sealed.set(nonce); sealed.set(new Uint8Array(cipher), 12)
  return { counter, sealed }
}

/** Große Teile (Upload) im Worker verschlüsseln, damit die Seite bedienbar bleibt; null: geht nicht, dann wie bisher. */
let worker: Worker | null | undefined
let jobs = 0
const waiting = new Map<number, (r: Blob | null) => void>()
function sealWorker(): Worker | null {
  if (worker !== undefined) return worker
  try {
    worker = new Worker(new URL('./sealWorker.ts', import.meta.url), { type: 'module' })
    worker.onmessage = (e: MessageEvent<{ id: number; sealed?: Blob; error?: string }>) => {
      const done = waiting.get(e.data.id); waiting.delete(e.data.id); done?.(e.data.sealed ?? null)
    }
    // Worker lässt sich nicht laden (alter Browser, Richtlinie): ab jetzt alles in der Seite
    worker.onerror = () => { worker?.terminate(); worker = null; for (const done of waiting.values()) done(null); waiting.clear() }
  } catch { worker = null }
  return worker
}
const OFF_THREAD = 1024 * 1024

async function sealOff(method: string, path: string, headers: Record<string, string>, body: Blob) {
  const w = sealWorker()
  if (!w) return null
  const counter = ++state!.counter
  save()
  const id = ++jobs
  const sealed = await new Promise<Blob | null>(done => {
    waiting.set(id, done)
    w.postMessage({ id, key: aesKey, aad: aad(counter, false), method, path, headers, body })
  })
  return sealed ? { counter, sealed } : null
}

interface Outer { status: number; headers: Record<string, string>; body: string | null; zip?: string | null }

async function unseal(counter: number, buf: ArrayBuffer): Promise<Outer> {
  const data = new Uint8Array(buf)
  const plain = await crypto.subtle.decrypt({ name: 'AES-GCM', iv: data.subarray(0, 12), additionalData: aad(counter, true) }, aesKey!, data.subarray(12))
  return JSON.parse(new TextDecoder().decode(plain)) as Outer
}

const envelope = () => `/api/e2e/${state!.userId}/${state!.deviceId}`

/** 404: Gerät unbekannt (abgemeldet, Passwort geändert, anderer Benutzer). 409: Zähler schon verbraucht - nach einem
 *  Neustart des Servers gilt nur, was über dem gespeicherten Stand liegt; ein zweiter Reiter mit derselben Anmeldung zählt
 *  getrennt. 401: das Gerät gilt nur mit gültiger Anmeldung derselben Person. Alles drei: neu aushandeln und einmal wiederholen. */
const renegotiate = (status: number) => status === 404 || status === 409 || status === 401

/** Ersatz fuer fetch: alles unter /api im Umschlag, sonst direkt. */
const canGunzip = typeof DecompressionStream === 'function'

export async function e2eFetch(input: string, init: RequestInit = {}, again = false): Promise<Response> {
  const url = typeof input === 'string' ? input : String(input)
  if (outside(url) || !(await ensure())) return fetch(url, { credentials: 'same-origin', ...init })
  const method = (init.method || 'GET').toUpperCase()
  const headers: Record<string, string> = {}
  new Headers(init.headers || {}).forEach((v, k) => { headers[k] = v })
  const body = await innerBody(init.body, headers)
  const { counter, sealed } = await seal(method, url, headers, body)
  const res = await fetch(envelope(), {
    method: 'POST', credentials: 'same-origin', body: sealed, keepalive: init.keepalive,
    // Gepackt antworten, wenn der Browser entpacken kann (große Listen: wenige MB statt dutzende)
    headers: { 'Content-Type': 'application/octet-stream', 'X-DM-Counter': String(counter), ...(canGunzip ? { 'X-DM-Zip': 'gzip' } : {}) },
  })
  // (sonst blieb der Reiter bei 409 bis zum Neuladen bei „Fehler 409“); ohne Anmeldung antwortet dann der normale Weg
  if (renegotiate(res.status) && !again) {
    state = null; aesKey = null; ready = null; save()
    if (await ensure()) return e2eFetch(input, init, true)
    return fetch(url, { credentials: 'same-origin', ...init })
  }
  // Umschlag gebremst (zu viele unechte Umschläge von dieser Adresse): ohne Umschlag weiter, angemeldet ist der Browser ja
  if (res.status === 429) return fetch(url, { credentials: 'same-origin', ...init })
  if (!res.ok) return res
  const outer = await unseal(counter, await res.arrayBuffer())
  const h = new Headers()
  for (const [k, v] of Object.entries(outer.headers || {})) if (!/^content-length$/i.test(k)) h.set(k, v)
  let bytes: Uint8Array<ArrayBuffer> | null = outer.body ? unb64(outer.body) : null
  if (bytes && outer.zip === 'gzip') bytes = new Uint8Array(await new Response(new Blob([bytes]).stream().pipeThrough(new DecompressionStream('gzip'))).arrayBuffer())
  return new Response(outer.status === 204 || outer.status === 304 ? null : bytes, { status: outer.status, headers: h })
}

export interface XhrOptions {
  /** Gesendete Bytes des Inhalts (nicht des Umschlags) */
  onProgress?: (loaded: number) => void
  /** Die laufende Anfrage (zum Abbrechen über abort) */
  onXhr?: (x: XMLHttpRequest | null) => void
  signal?: AbortSignal
}
export interface XhrResult { status: number; text: string }

function rawXhr(method: string, url: string, body: XMLHttpRequestBodyInit | null, headers: Record<string, string>, opt: XhrOptions,
  binary: boolean, scale = 1): Promise<{ status: number; response: any }> {
  return new Promise((resolve, reject) => {
    if (opt.signal?.aborted) { reject(new Error('abort')); return }
    const r = new XMLHttpRequest()
    opt.onXhr?.(r)
    r.open(method, url)
    r.withCredentials = true
    if (binary) r.responseType = 'arraybuffer'
    for (const [k, v] of Object.entries(headers)) r.setRequestHeader(k, v)
    if (opt.onProgress) r.upload.onprogress = e => opt.onProgress!(Math.round(e.loaded * scale))
    const stop = () => r.abort()
    opt.signal?.addEventListener('abort', stop)
    const done = () => opt.signal?.removeEventListener('abort', stop)
    r.onload = () => { done(); resolve({ status: r.status, response: binary ? r.response : r.responseText }) }
    r.onerror = () => { done(); resolve({ status: 0, response: null }) }
    r.onabort = () => { done(); reject(new Error('abort')) }
    r.send(body)
  })
}

/**
 * Anfrage mit Fortschritt (XMLHttpRequest) - für das Hochladen. Unter /api im Umschlag wie e2eFetch: jedes Teil eines
 * Uploads ist so genauso verschlüsselt wie jede andere Anfrage; Base64 und Verschlüsselung erledigt der Browser neben der Seite.
 * direct: am Umschlag vorbei (Upload-Links ohne Anmeldung). Status 0: keine Verbindung. Abbruch: Error('abort').
 */
export async function e2eXhr(method: string, url: string, body: Blob | FormData | string | null, opt: XhrOptions = {}, direct = false, again = false): Promise<XhrResult> {
  const headers: Record<string, string> = {}
  if (typeof body === 'string') headers['content-type'] = 'application/json'
  if (direct || outside(url) || !(await ensure())) {
    const r = await rawXhr(method, url, body, headers, opt, false)
    return { status: r.status, text: r.response ?? '' }
  }
  const size = body == null ? 0 : typeof body === 'string' ? body.length : body instanceof Blob ? body.size : 0
  if (body instanceof Blob && body.type) headers['content-type'] = body.type
  let blob: Blob | null = body instanceof Blob ? body : null
  if (body instanceof FormData) {
    const r = new Response(body)
    blob = await r.blob()
    headers['content-type'] = r.headers.get('content-type') || 'multipart/form-data'
  }
  // Große Teile im Worker (Base64, JSON und Verschlüsselung eines 8-MB-Teils hielten die Seite sonst an), kleine wie bisher
  let off: { counter: number; sealed: Blob | Uint8Array<ArrayBuffer> } | null = blob && blob.size >= OFF_THREAD ? await sealOff(method.toUpperCase(), url, headers, blob) : null
  if (opt.signal?.aborted) throw new Error('abort')
  if (!off) {
    const inner = await innerBody(blob ?? body, headers)
    if (opt.signal?.aborted) throw new Error('abort')
    off = await seal(method.toUpperCase(), url, headers, inner)
  }
  const { counter, sealed } = off
  const sealedSize = sealed instanceof Blob ? sealed.size : sealed.byteLength
  const res = await rawXhr('POST', envelope(), sealed, { 'Content-Type': 'application/octet-stream', 'X-DM-Counter': String(counter) }, opt, true,
    size > 0 ? size / sealedSize : 0)
  if (renegotiate(res.status) && !again) {
    state = null; aesKey = null; ready = null; save()
    return e2eXhr(method, url, body, opt, !(await ensure()), true)
  }
  if (res.status === 429) return e2eXhr(method, url, body, opt, true, true)
  if (res.status < 200 || res.status >= 300) return { status: res.status, text: '' }
  const outer = await unseal(counter, res.response as ArrayBuffer)
  return { status: outer.status, text: outer.body ? new TextDecoder().decode(unb64(outer.body)) : '' }
}

/** Beim Abmelden: Geraeteschluessel beim Server loeschen (sonst laeuft er nach 30 Tagen ab). */
export function e2eForget() {
  const s = state ?? load()
  state = null; aesKey = null; ready = null; save()
  if (s) try { navigator.sendBeacon(`/api/e2e/unregister/${s.deviceId}`) } catch { /* egal */ }
}

export function e2eActive(): boolean { return !!aesKey && !!state }

