// Hochladen in Stuecken (Version 2): Teil fuer Teil (8 MB), fortsetzbar und ohne Groessengrenze je Datei.
// Jedes Teil geht wie jede andere Anfrage im verschluesselten Umschlag (e2e.ts, XMLHttpRequest wegen des Fortschritts);
// ueber Upload-Links ohne Anmeldung direkt (Cookie-los, nur TLS).
// Bricht die Verbindung ab, fragt der Browser den Stand beim Server ab und macht dort weiter.
import { e2eXhr } from '../e2e'

export class UploadError extends Error {
  status: number
  data: any
  constructor(message: string, status: number, data: any) { super(message); this.status = status; this.data = data }
}

async function call(method: string, url: string, body?: Blob | FormData | string, onProgress?: (loaded: number) => void,
  onXhr?: (x: XMLHttpRequest | null) => void, signal?: AbortSignal): Promise<any> {
  const r = await e2eXhr(method, url, body ?? null, { onProgress, onXhr, signal }, url.startsWith('/api/public/'))
  let data: any = null
  try { data = r.text ? JSON.parse(r.text) : null } catch { /* kein JSON */ }
  if (r.status >= 200 && r.status < 300) return data
  if (r.status === 0) throw new UploadError('Verbindung unterbrochen', 0, null)
  throw new UploadError(data?.error || String(r.status), r.status, data)
}

const sleep = (ms: number) => new Promise(ok => setTimeout(ok, ms))

/** Laufende Sitzungen dieses Reiters: beim Verlassen der Seite (Neuladen, Schliessen) auf dem Server wegraeumen - sonst
 *  blieben sie einen Tag liegen und zaehlten gegen die offenen Uploads je Benutzer. */
const open = new Map<string, string>()
let watching = false
function track(id: string, base: string) {
  open.set(id, base)
  if (watching) return
  watching = true
  // keepalive: die Anfrage überlebt das Verlassen der Seite; ohne Inhalt, daher direkt (Cookie) statt im Umschlag
  window.addEventListener('pagehide', () => { for (const [id, base] of open) try { fetch(`${base}/${id}`, { method: 'DELETE', credentials: 'same-origin', keepalive: true }) } catch { /* egal */ } })
}

/**
 * Datei in Stuecken hochladen; zurueck kommt die Kennung der Sitzung - abschliessen danach ueber
 * /api/files/uploads/{id}/finish (Dateiablage), /api/chat/uploads/{id}/send (Chat) oder ?upload={id} an einem
 * Endpunkt, der sonst ein Formular nimmt (sendFiles). Die Sitzung gilt bis zum Abschliessen als offen (dropUpload).
 * name darf Unterordner enthalten („a/b/c.txt“, Ordner hochladen). base: über einen Upload-Link /api/public/{token}/uploads.
 * signal: abbrechen; die angefangene Sitzung wird dann gleich weggeräumt.
 */
export async function uploadChunked(file: File, name: string, onBytes?: (loaded: number) => void, onXhr?: (x: XMLHttpRequest | null) => void,
  base = '/api/files/uploads', signal?: AbortSignal): Promise<string> {
  const start = await call('POST', base, JSON.stringify({ name, size: file.size, mime: file.type || null }), undefined, onXhr, signal)
  const id: string = start.id
  track(id, base)
  const size: number = start.chunkSize || 8 * 1024 * 1024
  let offset = 0
  let tries = 0
  try {
    while (offset < file.size) {
      const from = offset
      try {
        const r = await call('PUT', `${base}/${id}?offset=${from}`, file.slice(from, Math.min(file.size, from + size)), loaded => onBytes?.(from + loaded), onXhr, signal)
        offset = r.received
        tries = 0
      } catch (e) {
        if (!(e instanceof UploadError)) throw e   // abgebrochen
        // Der Server steht woanders (etwa nach einem Abbruch): dort weitermachen
        if (e.status === 409 && typeof e.data?.received === 'number') { offset = e.data.received; continue }
        // Fehler des Servers oder der Anfrage: nicht endlos wiederholen
        if (e.status !== 0 && e.status < 500) throw e
        if (++tries > 6) throw new UploadError('Verbindung unterbrochen', 0, null)
        await sleep(1500 * tries)
        if (signal?.aborted) throw new Error('abort')
        try { offset = (await call('GET', `${base}/${id}`)).received } catch { /* naechster Versuch */ }
      }
    }
  } catch (e) {
    if (signal?.aborted) dropUpload(id, base)
    throw e
  }
  onXhr?.(null)
  return id
}

/** Abschliessen in die Dateiablage - Antwort wie /api/files/upload. */
export const finishToFiles = (id: string, path: string, conflict: string, onXhr?: (x: XMLHttpRequest | null) => void, mtime?: number) =>
  done(id, call('POST', `/api/files/uploads/${id}/finish?path=${encodeURIComponent(path)}&conflict=${encodeURIComponent(conflict)}`
    + (mtime && mtime > 0 ? `&mtime=${Math.round(mtime)}` : ''), undefined, undefined, onXhr))

/** Abschliessen als Chat-Anhang - Antwort: die neue Nachricht. */
export const finishToChat = (id: string, room: number, params: Record<string, string>) =>
  done(id, call('POST', `/api/chat/uploads/${id}/send?room=${room}` + Object.entries(params).map(([k, v]) => `&${k}=${encodeURIComponent(v)}`).join('')))

/** Abschliessen über einen Upload-Link - Antwort: { files: [Name] }. */
export const finishToLink = (base: string, id: string, path: string) => done(id, call('POST', `${base}/${id}/finish?path=${encodeURIComponent(path)}`))

/** Abgeschlossen: der Server hat die Sitzung verbraucht (bei einem Fehler bleibt sie für einen zweiten Versuch). */
function done<T>(id: string, p: Promise<T>): Promise<T> { return p.then(r => { open.delete(id); return r }) }

/** Vom Server verbraucht (etwa als Mail-Anhang über ?upload/Feld „upload“): nicht mehr beim Verlassen der Seite wegräumen. */
export const uploadUsed = (id: string) => { open.delete(id) }

/** Abgebrochen: die Teile auf dem Server wegraeumen. */
export const dropUpload = (id: string, base = '/api/files/uploads') => { open.delete(id); return call('DELETE', `${base}/${id}`).catch(() => {}) }

/** Bis zu dieser Größe gehen Dateien gesammelt als ein Formular (eine Anfrage für viele kleine), darüber in Stücken. */
const SMALL = 4 * 1024 * 1024, FORM_BYTES = 8 * 1024 * 1024, FORM_FILES = 20

export interface SendProgress { bytes: number; total: number; done: number; count: number; name: string }

/**
 * Dateien an einen Endpunkt, der ein Formular mit Dateien nimmt (Feld „file“) - oder statt des Formulars ?upload={id}
 * einer fertigen Sitzung in Stücken (Server: MultipartUpload.Files mit sessions). Kleine Dateien gesammelt als Formular
 * (höchstens 20 Dateien bzw. 8 MB je Anfrage), größere in Teilen von 8 MB: nie eine große Datei als ein Rumpf.
 * Alles im Umschlag. Gibt die Antworten der Reihe nach zurück; abbrechen über signal (Error('abort')).
 */
export async function sendFiles<T = any>(url: string, files: { file: File; name?: string }[], onProgress?: (p: SendProgress) => void, signal?: AbortSignal): Promise<T[]> {
  const total = files.reduce((n, f) => n + f.file.size, 0)
  const join = url.includes('?') ? '&' : '?'
  const out: T[] = []
  let base = 0, finished = 0
  const tell = (bytes: number, name: string) => onProgress?.({ bytes: Math.min(total, bytes), total, done: finished, count: files.length, name })
  for (let i = 0; i < files.length;) {
    const f = files[i]
    const name = f.name ?? f.file.name
    if (f.file.size > SMALL) {
      tell(base, name)
      const id = await uploadChunked(f.file, name, loaded => tell(base + loaded, name), undefined, undefined, signal)
      if (signal?.aborted) { dropUpload(id); throw new Error('abort') }
      out.push(await done(id, call('POST', `${url}${join}upload=${id}`, undefined, undefined, undefined, signal)))
      base += f.file.size; finished++; i++
      continue
    }
    // Kleine sammeln
    const form = new FormData()
    let bytes = 0, n = 0
    while (i < files.length && files[i].file.size <= SMALL && n < FORM_FILES && (n === 0 || bytes + files[i].file.size <= FORM_BYTES)) {
      form.append('file', files[i].file, files[i].name ?? files[i].file.name)
      bytes += files[i].file.size; n++; i++
    }
    tell(base, name)
    out.push(await call('POST', url, form, loaded => tell(base + Math.min(loaded, bytes), name), undefined, signal))
    base += bytes; finished += n
  }
  tell(total, '')
  return out
}
