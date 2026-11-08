import { api, q } from '../api'
import { uploadChunked } from '../files/chunked'

/** Tagebuch (Version 2): Typen und Aufrufe für /api/journal. */
export interface Member { id: string; name: string; readOnly: boolean; level?: string }
export interface Journal {
  id: number; name: string; color: string | null; count: number
  /** Mit mir geteilt: wem es gehört; eigene: null */
  owner: string | null; canWrite: boolean; showOnly: boolean; components: string[]; sharedWith: Member[] | null
  /** Liegt im Tresor (nur in der Liste, solange er auf diesem Gerät offen ist) */
  tresor?: boolean
}

/** Tagebücher im Tresor haben Kennungen ab 2^40 (Server: TresorJournals.Base). */
export const inTresor = (id: number | null | undefined) => id != null && id >= TRESOR_ANY
/** „Ein Tagebuch im Tresor“ ohne bestimmtes: das zuletzt beschriebene (für neue Einträge bei geschlossenem Tresor) */
export const TRESOR_ANY = 2 ** 40
export interface Place { name: string | null; lat: number | null; lon: number | null }
export interface Media { path: string; mime: string; size?: number | null; width?: number | null; height?: number | null; taken?: string | null }
export interface Ref { type: 'note' | 'file' | 'event'; id: string; name: string }

export interface Preview {
  id: string; journal: number; uri: string; date: string; title: string | null; snippet: string; mood: number | null
  tags: string[]; place: Place | null; private: boolean; media: Media[]; refs: number; weather: string | null; owner: string | null; canWrite: boolean
}

export interface Entry {
  id: string; journal: number; uri: string; etag: string; date: string; title: string | null; html: string; text: string
  mood: number | null; tags: string[]; place: Place | null; private: boolean; media: Media[]; refs: Ref[]; showEvents: boolean
  created: string | null; modified: string | null; weather: string | null; owner: string | null; canWrite: boolean
}

export interface EntryInput {
  journal: number; uri?: string; etag?: string; date: string; title?: string | null; html: string; mood?: number | null
  tags: string[]; place?: Place | null; private: boolean; showEvents: boolean; media: Media[]; refs: Ref[]; weather?: string | null
}

export interface Found { media: Media; lat: number | null; lon: number | null }
export interface OnThisDay { label: string; day: string; entries: Preview[] }
export interface ImportStatus { running: boolean; file: string | null; total: number; done: number; created: number; skipped: number; media: number; error: string | null; finished: string | null }
export interface Settings { imageSize: number; sizes: number[]; lookup: boolean; ask: string | null }
/** meal: aus einem Essensplan-Kalender die Mahlzeit (Frühstück, Mittagessen, Abendessen, sonst „Mahlzeit“) */
export interface DayEvent { title: string; time: string | null; allDay: boolean; location: string | null; meal?: string | null }
/** Foto oder Video eines Tages für den Vorschlag: aus der Ablage (Pfad) oder einem Galerie-Ordner („ext:…“); taken: Unix-ms */
export interface DayPhoto { kind: 'file' | 'ext'; path: string; name: string; mime: string | null; etag: string | null; taken: number | null }
export interface PlaceHit { id: string; journal: number; uri: string; date: string; title: string | null; place: Place; photo: Media | null }

/** Stimmung 1–5 */
export const MOODS = [
  { value: 1, emoji: '😢' },
  { value: 2, emoji: '🙁' },
  { value: 3, emoji: '😐' },
  { value: 4, emoji: '🙂' },
  { value: 5, emoji: '😄' },
]
export const moodEmoji = (m: number | null | undefined) => MOODS.find(x => x.value === Math.round(m ?? 0))?.emoji ?? ''

/** Tag (yyyy-MM-dd) in Ortszeit */
export const dayOf = (iso: string) => new Date(iso).toLocaleDateString('sv-SE')

export const journalApi = {
  journals: () => api.get<Journal[]>('/api/journal/journals'),
  createJournal: (name: string, color?: string | null, tresor?: boolean) => api.post<{ id: number }>('/api/journal/journals', { name, color, tresor }),
  /** In den Tresor (to: tresor) bzw. heraus (open); unshare: Freigaben entfernen (nach dem Hinweis) */
  moveJournal: (id: number, to: 'tresor' | 'open', unshare?: boolean) => api.post<{ id: number }>(`/api/journal/journals/${id}/move`, { to, unshare }),
  renameJournal: (id: number, name: string, color?: string | null) => api.put(`/api/journal/journals/${id}`, { name, color }),
  deleteJournal: (id: number) => api.del(`/api/journal/journals/${id}`),
  leaveJournal: (id: number) => api.post(`/api/pim/shares/${id}/leave`),
  entries: (p: { journal?: number; from?: string; to?: string; q?: string; tag?: string; mood?: number; before?: string; limit?: number }) =>
    api.get<Preview[]>('/api/journal/entries?' + q(p)),
  entry: (journal: number, uri: string) => api.get<Entry>(`/api/journal/entries/${journal}/${encodeURIComponent(uri)}`),
  save: (e: EntryInput) => api.post<Entry>('/api/journal/entries', e),
  /** Einträge (ids „Tagebuch/Name“) in ein anderes eigenes Tagebuch, auch in den Tresor und heraus */
  moveEntries: (ids: string[], to: number) => api.post<Entry[]>('/api/journal/entries/move', { ids, to }),
  remove: (journal: number, uri: string) => api.del(`/api/journal/entries/${journal}/${encodeURIComponent(uri)}`),
  forChat: (journal: number, uri: string) => api.get<{ text: string; refs: { type: 'file'; fileId: number }[] }>(`/api/journal/entries/${journal}/${encodeURIComponent(uri)}/chat`),
  tags: (journal?: number) => api.get<{ tag: string; count: number }[]>('/api/journal/tags?' + q({ journal })),
  onThisDay: () => api.get<OnThisDay[]>('/api/journal/onthisday'),
  moods: (from: string, journal?: number) => api.get<{ day: string; mood: number; count: number }[]>('/api/journal/moods?' + q({ from, journal })),
  media: (journal?: number, tag?: string | null, mood?: number | null) =>
    api.get<{ entry: string; journal: number; date: string; media: Media }[]>('/api/journal/media?' + q({ journal, tag, mood })),
  places: (journal?: number) => api.get<PlaceHit[]>('/api/journal/places?' + q({ journal })),
  settings: () => api.get<Settings>('/api/journal/settings'),
  saveSettings: (s: { imageSize?: number; lookup?: boolean; ask?: string }) => api.put<Settings>('/api/journal/settings', s),
  lookup: (lat: number, lon: number, date: string) => api.get<{ weather: string | null; place: string | null }>('/api/journal/lookup?' + q({ lat, lon, date })),
  events: (day: string) => api.get<DayEvent[]>('/api/journal/events?' + q({ date: day })),
  suggest: (day: string) => api.get<{ html: string; events: number; tasks: number }>('/api/journal/suggest?' + q({ date: day })),
  fromFiles: (paths: string[], journal?: number) => api.post<Found[]>('/api/journal/media/files', { paths, journal }),
  fromExternal: (ids: string[], journal?: number) => api.post<Found[]>('/api/journal/media/external', { ids, journal }),
  suggestDay: (day: string) => api.get<{ events: DayEvent[]; tasks: string[]; photos: DayPhoto[] }>('/api/journal/suggest/day?' + q({ date: day })),
  importDiarium: (path: string, journal?: number) => api.post<ImportStatus>('/api/journal/import/diarium', { path, journal }),
  importStatus: () => api.get<ImportStatus>('/api/journal/import'),
}

/** Datei hochladen (in Stücken) und ins Tagebuch übernehmen: Bild kleingerechnet, Datum und Ort aus dem Bild. */
export async function uploadMedia(file: File, journal?: number, onBytes?: (n: number) => void): Promise<Found> {
  const id = await uploadChunked(file, file.name, onBytes)
  return api.post<Found>(`/api/journal/uploads/${id}/attach` + (journal != null ? '?' + q({ journal }) : ''))
}

/** Vorschaubild bzw. Inhalt einer Datei des Tagebuchs - auch in geteilten Tagebüchern (über das Tagebuch, nicht die Dateiablage). */
export const thumbUrl = (journal: number, m: Media, size = 512) => '/api/journal/file?' + q({ journal, path: m.path, size })
export const fileUrl = (journal: number, m: Media, download = false) => '/api/journal/file?' + q({ journal, path: m.path, download: download || undefined })
export const isImage = (m: Media) => m.mime.startsWith('image/')
export const isVideo = (m: Media) => m.mime.startsWith('video/')
export const isAudio = (m: Media) => m.mime.startsWith('audio/')
