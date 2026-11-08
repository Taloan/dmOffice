// Hochladen in Stuecken (Version 2): Teil fuer Teil (8 MB), fortsetzbar und ohne Groessengrenze je Datei.
// Wie das Hochladen bisher am Umschlag vorbei (Cookie, XMLHttpRequest wegen des Fortschritts).
// Bricht die Verbindung ab, fragt der Browser den Stand beim Server ab und macht dort weiter.

export class UploadError extends Error {
  status: number
  data: any
  constructor(message: string, status: number, data: any) { super(message); this.status = status; this.data = data }
}

function call(method: string, url: string, body?: Blob | string, onProgress?: (loaded: number) => void, onXhr?: (x: XMLHttpRequest | null) => void): Promise<any> {
  return new Promise((resolve, reject) => {
    const r = new XMLHttpRequest()
    onXhr?.(r)
    r.open(method, url)
    r.withCredentials = true
    if (typeof body === 'string') r.setRequestHeader('Content-Type', 'application/json')
    if (onProgress) r.upload.onprogress = e => onProgress(e.loaded)
    r.onload = () => {
      let data: any = null
      try { data = r.responseText ? JSON.parse(r.responseText) : null } catch { /* kein JSON */ }
      if (r.status >= 200 && r.status < 300) resolve(data)
      else reject(new UploadError(data?.error || r.statusText || String(r.status), r.status, data))
    }
    r.onerror = () => reject(new UploadError('Verbindung unterbrochen', 0, null))
    r.onabort = () => reject(new Error('abort'))
    r.send(body ?? null)
  })
}

const sleep = (ms: number) => new Promise(ok => setTimeout(ok, ms))

/**
 * Datei in Stuecken hochladen; zurueck kommt die Kennung der Sitzung - abschliessen danach ueber
 * /api/files/uploads/{id}/finish (Dateiablage) oder /api/chat/uploads/{id}/send (Chat).
 * name darf Unterordner enthalten („a/b/c.txt“, Ordner hochladen). base: über einen Upload-Link /api/public/{token}/uploads.
 */
export async function uploadChunked(file: File, name: string, onBytes?: (loaded: number) => void, onXhr?: (x: XMLHttpRequest | null) => void,
  base = '/api/files/uploads'): Promise<string> {
  const start = await call('POST', base, JSON.stringify({ name, size: file.size, mime: file.type || null }), undefined, onXhr)
  const id: string = start.id
  const size: number = start.chunkSize || 8 * 1024 * 1024
  let offset = 0
  let tries = 0
  while (offset < file.size) {
    const from = offset
    try {
      const r = await call('PUT', `${base}/${id}?offset=${from}`, file.slice(from, Math.min(file.size, from + size)), loaded => onBytes?.(from + loaded), onXhr)
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
      try { offset = (await call('GET', `${base}/${id}`)).received } catch { /* naechster Versuch */ }
    }
  }
  onXhr?.(null)
  return id
}

/** Abschliessen in die Dateiablage - Antwort wie /api/files/upload. */
export const finishToFiles = (id: string, path: string, conflict: string, onXhr?: (x: XMLHttpRequest | null) => void) =>
  call('POST', `/api/files/uploads/${id}/finish?path=${encodeURIComponent(path)}&conflict=${encodeURIComponent(conflict)}`, undefined, undefined, onXhr)

/** Abschliessen als Chat-Anhang - Antwort: die neue Nachricht. */
export const finishToChat = (id: string, room: number, params: Record<string, string>) =>
  call('POST', `/api/chat/uploads/${id}/send?room=${room}` + Object.entries(params).map(([k, v]) => `&${k}=${encodeURIComponent(v)}`).join(''))

/** Abschliessen über einen Upload-Link - Antwort: { files: [Name] }. */
export const finishToLink = (base: string, id: string, path: string) => call('POST', `${base}/${id}/finish?path=${encodeURIComponent(path)}`)

/** Abgebrochen: die Teile auf dem Server wegraeumen. */
export const dropUpload = (id: string, base = '/api/files/uploads') => call('DELETE', `${base}/${id}`).catch(() => {})
