// DMS: Typen und Aufrufe (Server: DmOffice.Server/Dms, API /api/dms). docs/dms-konzept.md
import { api, q } from '../api'
import { t } from '../i18n'

/** Meldung nach dem Übernehmen: mit richtiger Einzahl, „schon vorhanden“ nur, wenn es das gab. */
export const importedText = (n: number, dup: number) => [n === 1 ? t('1 Dokument übernommen') : t('{n} Dokumente übernommen', { n }),
  dup ? t('{d} schon vorhanden', { d: dup }) : ''].filter(Boolean).join(', ')

/** Herkunft eines Dokuments in Worten (Spalte „Quelle“). */
export const sourceLabel = (source: string) => source === 'upload' ? t('Hochgeladen') : source === 'folder' ? t('Überwachter Ordner')
  : source === 'files' ? t('Aus Dateien') : source === 'mail' ? t('Aus Mail') : source

/** Dateityp in Worten (Spalte „Dateityp“); Unbekanntes nach der Endung des Namens. */
export function typeLabel(mime: string | null, name: string) {
  const m = (mime ?? '').toLowerCase()
  if (m === 'application/pdf') return 'PDF'
  if (m === 'message/rfc822') return t('Mail')
  if (m.includes('wordprocessingml') || m === 'application/msword') return 'Word'
  if (m.includes('spreadsheetml') || m === 'application/vnd.ms-excel') return 'Excel'
  if (m.includes('presentationml') || m === 'application/vnd.ms-powerpoint') return 'PowerPoint'
  if (m.startsWith('image/')) return t('Bild')
  if (m === 'text/plain') return t('Textdatei')
  const dot = name.lastIndexOf('.')
  return dot > 0 && name.length - dot <= 6 ? name.slice(dot + 1).toUpperCase() : ''
}

export type Kind = 'correspondent' | 'doctype' | 'tag' | 'role'
export const KINDS: Kind[] = ['correspondent', 'doctype', 'tag', 'role']
export type Match = 'auto' | 'none' | 'any' | 'all' | 'exact' | 'regex' | 'sender'

export interface Entry {
  id: string; kind: Kind; parent: string | null; name: string; color: string | null; match: Match; matchText: string | null
  caseSensitive: boolean; count: number; lastUsed: string | null
}

export interface DocTag { id: string; auto: boolean }

export interface Doc {
  id: number; fileId: number; path: string | null; state: 'queued' | 'done' | 'error'; title: string; docDate: string | null; imported: string
  correspondent: string | null; doctype: string | null; tags: DocTag[]; inbox: boolean; byAi: boolean; error: string | null; originalName: string
  mime: string | null; size: number; hasOriginal: boolean; archive: boolean; pdfA: boolean; profile: string | null; source: string; sender: string | null
  autoCorrespondent: boolean; autoDoctype: boolean; manual: boolean; aiSuggestion: string | null; trashed: boolean
  /** Rolle (etwa „Testament“); roleCurrent: dieses Dokument hat sie inne, sonst frühere Version (ersetzt am roleReplaced) */
  role?: string | null; roleCurrent?: boolean; roleReplaced?: string | null
  /** Angaben aus paperless zum Vergleich (Übernahme) */
  paperless?: { correspondent: string | null; doctype: string | null; tags: string[]; differs: boolean } | null
  /** Externer Ordner als Speicherort (Kennung); path ist dann der Pfad darin */
  mount?: string | null
  /** Betrag in Cent, von Hand eingetragen */
  amount?: number | null
  /** Fixiert: nicht änderbar und nicht löschbar, bis die Fixierung aufgehoben wird */
  fixed?: boolean
}

export interface Profile {
  id: string; name: string; target: string; folder: string; folders: string; names: string; unknown: string; follow: boolean; default: boolean; language: string
  /** Ordner unter dem Zielordner für Dokumente in der Inbox (Vorgabe .INBOX) */
  inboxFolder?: string
  /** Ziel „external“: externer Ordner; Angaben bei Änderungen neu in die PDF schreiben */
  mount?: string | null; writeMeta?: boolean
}

/** Externe Ordner der Person (Quellen und Ziel). */
export interface Mount { id: string; name: string; readOnly: boolean }

export interface Source {
  id: string; name: string; mount: string; folder: string; profile: string | null; correspondent: string | null; doctype: string | null; tags: string[]
  every: number; active: boolean; lastRun: string | null; error: string | null; lastCount: number; lastDuplicates: number; total: number
}

export interface State {
  ocr: boolean; ocrReason: string | null; pdfa: boolean; aiAllowed: boolean; aiService: string | null; ai: boolean; inbox: number; queued: number
  /** Warteschlange angehalten; Wartendes im Tresor */
  paused: boolean; tresorQueued: number
  /** Tresor eingerichtet, offen auf diesem Gerät, Inbox darin */
  tresor: boolean; tresorOpen: boolean; tresorInbox: number
  /** Anzahl unter „Alle Dokumente“ (Dateiablage bzw. offener Tresor) */
  all: number; tresorAll: number
  /** Bei geschlossenem Tresor abgelegt, wartet aufs Öffnen */
  tresorWaiting: number
}

export interface Node { level: string; key: string | null; name: string; count: number; children: Node[] }

export type Level = 'correspondent' | 'doctype' | 'tag' | 'role' | 'doc_year' | 'doc_month' | 'import_year' | 'import_month' | 'profile'
export const LEVELS: Level[] = ['correspondent', 'doctype', 'tag', 'role', 'doc_year', 'doc_month', 'import_year', 'import_month', 'profile']

export const levelLabel = (l: Level | string): string => ({
  correspondent: t('Korrespondent'), doctype: t('Dokumenttyp'), tag: t('Tag'), role: t('Rolle'), doc_year: t('Dokumentjahr'), doc_month: t('Dokumentmonat'),
  import_year: t('Importjahr'), import_month: t('Importmonat'), profile: t('Ablageprofil'),
} as Record<string, string>)[l] ?? l

export const kindLabel = (k: Kind, plural = false): string => plural
  ? { correspondent: t('Korrespondenten'), doctype: t('Dokumenttypen'), tag: t('Tags'), role: t('Rollen') }[k]
  : { correspondent: t('Korrespondent'), doctype: t('Dokumenttyp'), tag: t('Tag'), role: t('Rolle') }[k]

export const matchLabel = (m: Match): string => ({
  auto: t('Automatisch'), none: t('Keine'), any: t('Ein Wort'), all: t('Alle Wörter'), exact: t('Genau'), regex: t('Regulärer Ausdruck'), sender: t('Absender'),
})[m]

/** Gespeicherte Bäume (Vorgaben, solange keine eigenen gespeichert sind). */
export interface Tree { name: string; levels: Level[] }
export const defaultTrees = (): Tree[] => [
  { name: t('Korrespondent'), levels: ['correspondent', 'doc_year'] },
  { name: t('Dokumenttyp'), levels: ['doctype', 'doc_year'] },
  { name: t('Tags'), levels: ['tag'] },
  { name: t('Rolle'), levels: ['role'] },
  { name: t('Dokumentdatum'), levels: ['doc_year', 'doc_month'] },
  { name: t('Importdatum'), levels: ['import_year', 'import_month'] },
]

/** Filter der Dokumentliste (wie /api/dms/docs). */
export interface Filter {
  q?: string; correspondent?: string; doctype?: string; tags?: string[]; docFrom?: string; docTo?: string; importedFrom?: string; importedTo?: string
  inbox?: boolean; profile?: string; noCorrespondent?: boolean; noDoctype?: boolean; noTags?: boolean; paperlessDiffers?: boolean
  /** Rolle: das geltende Dokument und die früheren Versionen */
  role?: string; noRole?: boolean
  fixed?: boolean
  /** Warteschlange: noch nicht verarbeitet */
  queued?: boolean
  /** „tresor“: Dokumente im Tresor */
  area?: 'tresor'
  /** Sortierung (Spaltenkopf): date, title, correspondent, doctype, tags, role, imported, size, name, type, source */
  sort?: string; desc?: boolean
}

export const loadDocs = (f: Filter, offset = 0, limit = 200) =>
  api.get<{ items: Doc[]; total: number }>('/api/dms/docs?' + q({
    q: f.q || undefined, correspondent: f.correspondent, doctype: f.doctype, tags: f.tags?.length ? f.tags.join(',') : undefined,
    docFrom: f.docFrom, docTo: f.docTo, importedFrom: f.importedFrom, importedTo: f.importedTo, inbox: f.inbox, profile: f.profile,
    noCorrespondent: f.noCorrespondent, noDoctype: f.noDoctype, noTags: f.noTags, paperlessDiffers: f.paperlessDiffers, queued: f.queued, area: f.area, role: f.role, noRole: f.noRole, sort: f.sort, desc: f.sort ? f.desc : undefined, offset, limit,
  }))

/** Filter für einen Ordner eines virtuellen Baums (samt der Ordner darüber). */
export function filterOf(path: { level: string; key: string | null }[]): Filter {
  const f: Filter = {}
  for (const { level, key } of path) {
    if (level === 'correspondent') { if (key) f.correspondent = key; else f.noCorrespondent = true }
    else if (level === 'doctype') { if (key) f.doctype = key; else f.noDoctype = true }
    else if (level === 'tag') { if (key) f.tags = [...(f.tags ?? []), key]; else f.noTags = true }
    else if (level === 'role') { if (key) f.role = key; else f.noRole = true }
    else if (level === 'profile' && key) f.profile = key
    else if (key && (level === 'doc_year' || level === 'doc_month')) {
      const [from, to] = range(key)
      f.docFrom = from; f.docTo = to
    } else if (key && (level === 'import_year' || level === 'import_month')) {
      const [from, to] = range(key)
      f.importedFrom = from; f.importedTo = to
    }
  }
  return f
}

/** „2025“ oder „2025-03“ als erster und letzter Tag. */
function range(key: string): [string, string] {
  if (key.length === 4) return [`${key}-01-01`, `${key}-12-31`]
  const [y, m] = key.split('-').map(Number)
  const last = new Date(y, m, 0).getDate()
  return [`${key}-01`, `${key}-${String(last).padStart(2, '0')}`]
}

/** Name mit Oberbegriffen, etwa „20_Bürokratie › 21_Finanzamt“. */
export function fullName(all: Entry[], id: string | null): string {
  if (!id) return ''
  const byId = new Map(all.map(e => [e.id, e]))
  const parts: string[] = []
  for (let e = byId.get(id); e && parts.length < 20; e = e.parent ? byId.get(e.parent) : undefined) parts.unshift(e.name)
  return parts.join(' › ')
}

/** Einträge einer Liste in Baumreihenfolge mit Tiefe (für Auswahllisten); cmp ordnet Geschwister (sonst nach Name). */
export function ordered(all: Entry[], kind: Kind, cmp?: (a: Entry, b: Entry) => number): { entry: Entry; depth: number }[] {
  const list = all.filter(e => e.kind === kind)
  const out: { entry: Entry; depth: number }[] = []
  const byName = (a: Entry, b: Entry) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
  const walk = (parent: string | null, depth: number) => {
    for (const e of list.filter(x => x.parent === parent).sort((a, b) => (cmp?.(a, b) || 0) || byName(a, b))) {
      out.push({ entry: e, depth })
      if (depth < 20) walk(e.id, depth + 1)
    }
  }
  walk(null, 0)
  return out
}

/** Zusatz für Aufrufe im Tresor: „?area=tresor“ bzw. „&area=tresor“. */
export const areaQ = (tresor: boolean, more = false) => tresor ? (more ? '&' : '?') + 'area=tresor' : ''

/** Inhalt eines Dokuments: Datei der Ablage bzw. Datei im Tresor. */
export const contentUrl = (d: Doc, tresor: boolean, inline: boolean) => d.mount
  ? `/api/dms/docs/${d.id}/content`
  : tresor
  ? `/api/tresor/files/${d.fileId}/content` + (inline ? '?inline=true' : '')
  : d.path ? '/api/files/download?' + q({ path: d.path, inline: inline || undefined }) : null

/** Herunterladen unter dem Titel, mehrere als ZIP - über ein Formular, der Browser speichert die Datei selbst. */
export function downloadDocs(ids: number[], tresor: boolean) {
  const form = document.createElement('form')
  form.method = 'POST'; form.action = '/api/dms/docs/download'; form.style.display = 'none'
  const add = (k: string, v: string) => { const i = document.createElement('input'); i.type = 'hidden'; i.name = k; i.value = v; form.appendChild(i) }
  ids.forEach(id => add('ids', String(id)))
  if (tresor) add('area', 'tresor')
  document.body.appendChild(form); form.submit(); form.remove()
}

/** Monatsname zu „2025-03“. */
export function monthName(key: string, locale: string) {
  const [y, m] = key.split('-').map(Number)
  return new Date(y, m - 1, 1).toLocaleDateString(locale, { month: 'long', year: 'numeric' })
}
