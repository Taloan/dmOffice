// Aufrufe an den Server. Fehler kommen als { error } - als Exception mit dieser Meldung weiter.
// Alles unter /api laeuft im verschluesselten Umschlag (e2e.ts), wenn der Server ihn anbietet.
import { e2eFetch as fetch } from './e2e'

export class ApiError extends Error {
  status: number
  data: any
  constructor(message: string, status: number, data: any) {
    super(message)
    this.status = status
    this.data = data
  }
}

async function handle<T>(r: Response): Promise<T> {
  const type = r.headers.get('content-type') || ''
  const data = type.includes('json') ? await r.json().catch(() => null) : null
  if (!r.ok) {
    if (r.status === 401 && !location.pathname.startsWith('/login')) window.dispatchEvent(new Event('dmoffice:unauthorized'))
    throw new ApiError(data?.error || `${r.status} ${r.statusText}`, r.status, data)
  }
  return data as T
}

export const api = {
  get: <T = any>(url: string) => fetch(url, { credentials: 'same-origin' }).then(r => handle<T>(r)),
  post: <T = any>(url: string, body?: unknown) =>
    fetch(url, {
      method: 'POST', credentials: 'same-origin',
      headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    }).then(r => handle<T>(r)),
  put: <T = any>(url: string, body?: unknown) =>
    fetch(url, { method: 'PUT', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) })
      .then(r => handle<T>(r)),
  del: <T = any>(url: string) => fetch(url, { method: 'DELETE', credentials: 'same-origin' }).then(r => handle<T>(r)),
  form: <T = any>(url: string, form: FormData) =>
    fetch(url, { method: 'POST', credentials: 'same-origin', body: form }).then(r => handle<T>(r)),
}

export const q = (params: Record<string, string | number | boolean | undefined | null>) =>
  Object.entries(params)
    .filter(([, v]) => v !== undefined && v !== null && v !== '')
    .map(([k, v]) => `${encodeURIComponent(k)}=${encodeURIComponent(String(v))}`)
    .join('&')

// ---------------- Typen ----------------

export interface Me {
  id: string; login: string; displayName: string; isAdmin: boolean; twoFactor: boolean
  /** Dateiablage an (vom Admin abschaltbar; notizi geht trotzdem) */
  files?: boolean
}

export interface Addr { name?: string | null; address: string }

export interface Account { id: string; name: string; email: string; displayName: string; signature: string; language?: 'de' | 'en'; trashDays?: number; junkDays?: number; storage?: 'full' | 'slim' }

export interface Folder {
  path: string; name: string; delimiter: string; special: string | null; selectable: boolean
  unread: number; total: number; synced: string | null
}

export interface MailItem {
  id: number; account: string; folder: string; uid: number; messageId?: string; subject: string
  from: Addr | null; to: Addr[]; cc: Addr[]; date: string; size: number
  seen: boolean; flagged: boolean; answered: boolean; forwarded: boolean; hasAttachments: boolean
  preview: string; spam: number | null; cached: boolean; keywords: string[]
}

export interface Attachment { index: number; name: string; mime: string; size: number; inline: boolean }

export interface Message {
  account: string; uid: number; folder: string; subject: string; from: Addr | null
  to: Addr[]; cc: Addr[]; bcc: Addr[]; replyTo: Addr[]; date: string
  html: string; hasRemoteImages: boolean; text: string; attachments: Attachment[]
  messageId?: string; inReplyTo?: string; references: string[]; seen: boolean; flagged: boolean
  spam: number | null; cached: boolean; imagesAllowed: boolean; imagesTrusted: boolean
  /** Newsletter: Abmeldewege aus List-Unsubscribe; oneClick erledigt der Server */
  unsubscribe?: { url?: string | null; mailto?: string | null; oneClick: boolean } | null
}

export interface Collection { id: number; uri: string; name: string; color: string | null; description: string | null; components: string[]; count: number; events?: number; tasks?: number
  /** Abonniert (webcal/ics): nur lesbar, wird regelmaessig neu geholt */
  subscription?: { url: string; interval: number; lastFetch: string | null; lastError: string | null } | null
  /** Aufgabenlisten: normale Liste, Einkaufsliste oder Checkliste */
  listType?: 'tasks' | 'shop' | 'check' | null
  /** Geteilt: mit wem (eigene Liste) bzw. wem sie gehoert (mit mir geteilt, Id negativ) */
  sharedWith?: { id: string; name: string }[] | null
  owner?: string | null }

export interface CalEvent {
  id: string; calendar: number; uri: string; etag: string; uid?: string; title: string; location?: string
  description?: string; start: string; end: string; allDay: boolean; recurring: boolean; rRule?: string
  recurrenceId?: string; status?: string; reminder?: number | null; busy: boolean; categories: string[]; url?: string
}

export interface Task {
  id: string; list: number; uri: string; etag: string; uid?: string; title: string; description?: string | null
  due?: string | null; dueAllDay: boolean; start?: string | null; completed: boolean; completedAt?: string | null
  priority: number; percent: number; status?: string | null; categories: string[]; parent?: string | null; created: string
  /** Einkaufs-/Checkliste: Artikel, Menge, Einheit, Abteilung */
  item?: string | null; qty?: string | null; unit?: string | null; section?: string | null
}

export interface TypedValue { type: string; value: string }
export interface Address { type: string; street: string; city: string; region: string; zip: string; country: string }
export interface Contact {
  id: string; book: number; uri: string; etag: string; uid?: string; displayName: string
  prefix: string; given: string; additional: string; family: string; suffix: string; nickname: string
  org: string; department: string; title: string; emails: TypedValue[]; phones: TypedValue[]
  addresses: Address[]; urls: string[]; birthday?: string | null; note: string; categories: string[]; photo?: string | null
  /** VIP: Benachrichtigungen "nur VIPs" in der App und Prio-Posteingang */
  vip?: boolean
  moveTo?: number
}

export const fmtSize = (n: number) =>
  n < 1024 ? `${n} B` : n < 1024 * 1024 ? `${(n / 1024).toFixed(0)} KB` : `${(n / 1024 / 1024).toFixed(1)} MB`

export const addrText = (a?: Addr | null) => (a ? (a.name ? `${a.name}` : a.address) : '')
export const addrFull = (a: Addr) => (a.name ? `${a.name} <${a.address}>` : a.address)
