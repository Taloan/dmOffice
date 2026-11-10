// Zeitleiste der Galerie vom Server (GalleryTimeline.cs): erst nur die Monate mit Anzahl, dann die Fotos der Monate im Bild.
// Wie bei Immich - beim Öffnen kommt kein Abruf über ein paar KB, egal wie viele Fotos es sind.
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { ApiError, q } from '../api'
import { e2eFetch } from '../e2e'
import { Item } from './common'
import { Meta } from './PhotoFilter'

/** group: „source“ = nach Quelle (eigene Ablage, je Galerie-Ordner, geteilt), der Server liefert dann je Abschnitt den Namen (l). */
export interface TLQuery { sort: string; desc: boolean; stack: boolean; ext: string; filter: string | null; group?: string }
/** Ein Abschnitt: Monat („2026-10“) oder bei Sortierung nach Name/Größe eine Seite („p0“), Anzahl, Fingerabdruck */
export interface TLBucket { k: string; n: number; h: string; l?: string }
/** Neuer Stand eines Fotos aus dem Abgleich beim Ansehen */
export interface TLPatch { path: string; meta: Meta; etag: string; size: number; modified: string }
interface TLState { key: string; buckets: TLBucket[]; total: number; shown: number; types: Record<string, number> }

/** Eine Zeile vom Server - kurze Feldnamen, leere fehlen */
interface Row {
  i: string; s: number; m: number; t?: string; e: string; f?: number
  k?: number; w?: number; h?: number; d?: number; r?: number; c?: string; l?: string; fo?: number; a?: number; x?: number; o?: number
  ti?: string; kw?: string; de?: string; la?: number; lo?: number; fv?: 1; ln?: 1; pp?: 1; ow?: string; cw?: boolean; so?: 1; st?: Row[]; lb?: string; pk?: number
}

function decode(r: Row, meta: Map<string, Meta>): Item {
  const item: Item = {
    path: r.i, name: r.i.slice(r.i.lastIndexOf('/') + 1), isDir: false, size: r.s, mime: r.t ?? null, modified: new Date(r.m).toISOString(), etag: r.e,
    fileId: r.f, favorite: !!r.fv, shared: !!r.ln, sharedWith: !!r.pp, owner: r.ow ?? null, canWrite: r.cw ?? null, showOnly: r.so ? true : null,
  }
  if (r.st?.length) item.stack = r.st.map(x => decode(x, meta))
  if (r.k != null || r.c != null || r.w != null || r.ti != null || r.kw != null || r.r != null || r.de != null || r.la != null || r.d != null || r.lb != null || r.pk != null) {
    meta.set(r.i, {
      key: r.i, taken: r.k, width: r.w, height: r.h, duration: r.d, rating: r.r, camera: r.c, lens: r.l, focal: r.fo, aperture: r.a, exposure: r.x, iso: r.o,
      title: r.ti, keywords: r.kw, description: r.de, lat: r.la, lon: r.lo, label: r.lb, pick: r.pk,
    })
  }
  return item
}

/** Zeilen wie in einem Abschnitt (auch Personen-Ansicht) zu Einträgen */
export const decodeRows = (rows: unknown[]): Item[] => { const meta = new Map<string, Meta>(); return (rows as Row[]).map(r => decode(r, meta)) }

/** JSON mit ETag; unverändert: status 304 ohne Inhalt. */
async function getJson(url: string, etag?: string | null): Promise<{ status: number; etag: string | null; data: any }> {
  const r = await e2eFetch(url, { credentials: 'same-origin', headers: etag ? { 'If-None-Match': etag } : {} })
  if (r.status === 304) return { status: 304, etag: etag ?? null, data: null }
  const data = await r.json().catch(() => null)
  if (!r.ok) throw new ApiError(data?.error || `${r.status} ${r.statusText}`, r.status, data)
  return { status: r.status, etag: r.headers.get('ETag'), data }
}

const queryOf = (x: TLQuery) => q({ sort: x.sort, desc: x.desc, stack: x.stack, ext: x.ext || undefined, filter: x.filter || undefined, group: x.group || undefined })

/** So viele Monate höchstens im Speicher - weiter entfernte fallen wieder heraus (zurückgescrollt: neu geladen). */
const KEEP = 24
/** So viele Monate gleichzeitig laden. */
const PARALLEL = 3

/**
 * Zeitleiste der Galerie: buckets (Monate mit Anzahl), items (die geladenen Fotos in Anzeigereihenfolge), loaded(k) (die Fotos
 * eines Monats, falls da), metaOf (Bildangaben), want (welche Monate gerade gebraucht werden - je Zweck, etwa Bild und Betrachter),
 * refresh (Stand vom Server, unverändert nur 304; geänderte Monate werden neu geholt, sobald sie gebraucht werden).
 */
export function useGalleryTimeline(enabled: boolean, query: TLQuery, onError: (e: unknown) => void) {
  const key = queryOf(query)
  const [state, setState] = useState<TLState | null>(null)
  const [version, setVersion] = useState(0)
  const loaded = useRef(new Map<string, { h: string; items: Item[] }>())
  const meta = useRef(new Map<string, Meta>())
  const tag = useRef<string | null>(null)
  const wants = useRef(new Map<string, string[]>())
  const inflight = useRef(new Set<string>())
  const keyRef = useRef(key); keyRef.current = key
  const stateRef = useRef(state); stateRef.current = state
  const errRef = useRef(onError); errRef.current = onError

  const forget = (items: Item[]) => { for (const i of items) { meta.current.delete(i.path); for (const s of i.stack ?? []) meta.current.delete(s.path) } }

  // Was weit weg ist, wieder vergessen: behalten werden die gewünschten und die ihnen nächsten Monate
  const evict = () => {
    const st = stateRef.current
    if (!st || loaded.current.size <= KEEP) return
    const at = new Map(st.buckets.map((b, n) => [b.k, n]))
    const wanted = [...new Set([...wants.current.values()].flat())].map(k => at.get(k)).filter((n): n is number => n != null)
    const center = wanted.length ? wanted.reduce((a, b) => a + b, 0) / wanted.length : 0
    const far = [...loaded.current.keys()].filter(k => !wanted.includes(at.get(k) ?? -1))
      .sort((a, b) => Math.abs((at.get(b) ?? 0) - center) - Math.abs((at.get(a) ?? 0) - center))
    for (const k of far.slice(0, loaded.current.size - KEEP)) { forget(loaded.current.get(k)!.items); loaded.current.delete(k) }
  }

  const pump = useCallback(() => {
    const st = stateRef.current
    if (!st) return
    const hOf = new Map(st.buckets.map(b => [b.k, b.h]))
    const wanted = [...new Set([...wants.current.values()].flat())]
    for (const k of wanted) {
      if (inflight.current.size >= PARALLEL) break
      const h = hOf.get(k)
      if (h == null || inflight.current.has(k) || loaded.current.get(k)?.h === h) continue
      void load(k, h)
    }
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const load = async (k: string, h: string) => {
    const qk = keyRef.current
    inflight.current.add(k)
    try {
      const old = loaded.current.get(k)
      const r = await getJson('/api/gallery/timeline/bucket?' + q({ k }) + '&' + qk, old ? `"${old.h}"` : null)
      if (keyRef.current !== qk) return
      if (r.status === 304 && old) { loaded.current.set(k, { ...old, h }); return }
      if (old) forget(old.items)
      const items = (r.data.items as Row[]).map(x => decode(x, meta.current))
      loaded.current.set(k, { h: r.etag?.replace(/"/g, '') ?? h, items })
      evict()
      setVersion(v => v + 1)
    } catch (e) { errRef.current(e) } finally { inflight.current.delete(k); pump() }
  }

  const refresh = useCallback(async () => {
    const k = keyRef.current
    try {
      const r = await getJson('/api/gallery/timeline/buckets?' + k, stateRef.current?.key === k ? tag.current : null)
      if (keyRef.current !== k || r.status === 304) return
      tag.current = r.etag
      // Monate, die es nicht mehr gibt, vergessen; geänderte bleiben sichtbar, bis der neue Stand da ist
      const keys = new Set((r.data.buckets as TLBucket[]).map(b => b.k))
      for (const [bk, l] of loaded.current) if (!keys.has(bk)) { forget(l.items); loaded.current.delete(bk) }
      setState({ key: k, buckets: r.data.buckets, total: r.data.total, shown: r.data.shown, types: r.data.types ?? {} })
      setVersion(v => v + 1)
      setTimeout(pump, 0)
    } catch (e) { errRef.current(e) }
  }, [pump])

  // Andere Abfrage (Sortierung, Filter, Stapel, Format): von vorn
  useEffect(() => {
    for (const l of loaded.current.values()) forget(l.items)
    loaded.current = new Map(); tag.current = null; inflight.current.clear()
    setState(null); setVersion(v => v + 1)
    if (enabled) void refresh()
  }, [key, enabled]) // eslint-disable-line react-hooks/exhaustive-deps

  /** Diese Monate werden gebraucht (je Zweck: „view“ für den Bildausschnitt, „viewer“ für den Betrachter). */
  const want = useCallback((keys: string[], slot = 'view') => {
    const before = wants.current.get(slot)
    if (before && before.length === keys.length && before.every((k, n) => k === keys[n])) { pump(); return }
    wants.current.set(slot, keys)
    pump()
  }, [pump])

  /** Abgleich einzelner Fotos gleich einarbeiten, ohne Monate neu zu laden; einsortiert wird beim nächsten Stand vom Server. */
  const patch = useCallback((list: TLPatch[]) => {
    if (!list.length) return
    const by = new Map(list.map(p => [p.path, p]))
    const fresh = <T extends Item>(i: T): T => { const p = by.get(i.path); return p ? { ...i, etag: p.etag, size: p.size, modified: p.modified } : i }
    for (const p of list) meta.current.set(p.path, p.meta)
    for (const [k, l] of loaded.current) {
      if (!l.items.some(i => by.has(i.path) || i.stack?.some(x => by.has(x.path)))) continue
      loaded.current.set(k, { ...l, items: l.items.map(i => fresh(i.stack?.some(x => by.has(x.path)) ? { ...i, stack: i.stack.map(fresh) } : i)) })
    }
    setVersion(v => v + 1)
  }, [])

  const items = useMemo(() => state ? state.buckets.flatMap(b => loaded.current.get(b.k)?.items ?? []) : [], [state, version]) // eslint-disable-line react-hooks/exhaustive-deps
  const bucketOf = useMemo(() => {
    const m = new Map<string, string>()
    if (state) for (const b of state.buckets) for (const i of loaded.current.get(b.k)?.items ?? []) m.set(i.path, b.k)
    return m
  }, [state, version]) // eslint-disable-line react-hooks/exhaustive-deps
  const loadedOf = useCallback((k: string) => loaded.current.get(k)?.items, [version]) // eslint-disable-line react-hooks/exhaustive-deps
  const metaOf = useCallback((path: string) => meta.current.get(path), [version]) // eslint-disable-line react-hooks/exhaustive-deps
  return { state, items, version, loaded: loadedOf, metaOf, bucketOf, want, refresh, patch, stamp: tag.current }
}

/** Werte der Filterleiste vom Server: je Spalte alle Werte mit Anzahl (jede Spalte nach den linken gefiltert). */
export type Facets = { total: number; values: [string, number][] }[]
export function useFacets(enabled: boolean, cols: string[], ext: string, filter: string | null, stamp: string | null, onError: (e: unknown) => void) {
  const [facets, setFacets] = useState<Facets | null>(null)
  const url = '/api/gallery/timeline/facets?' + q({ cols: cols.join(','), ext: ext || undefined, filter: filter || undefined })
  useEffect(() => {
    if (!enabled) { setFacets(null); return }
    let gone = false
    getJson(url).then(r => { if (!gone && r.data) setFacets(r.data.cols) }).catch(e => { if (!gone) onError(e) })
    return () => { gone = true }
  }, [enabled, url, stamp]) // eslint-disable-line react-hooks/exhaustive-deps
  return facets
}

/** Suche in der Galerie (eigene Fotos und Galerie-Ordner, auch Titel und Stichwörter): Einträge samt Bildangaben. */
export async function searchGallery(term: string, query: TLQuery): Promise<{ items: Item[]; meta: Record<string, Meta> }> {
  const r = await getJson('/api/gallery/timeline/search?' + q({ q: term, stack: query.stack, ext: query.ext || undefined }))
  const meta = new Map<string, Meta>()
  const items = (r.data.items as Row[]).map(x => decode(x, meta))
  return { items, meta: Object.fromEntries(meta) }
}

/** Für den Atlas: nur Fotos mit Aufnahmeort, als Einträge mit Bildangaben. */
export async function loadGeo(query: TLQuery): Promise<{ items: Item[]; meta: Map<string, Meta> }> {
  const r = await getJson('/api/gallery/timeline/geo?' + q({ stack: query.stack, ext: query.ext || undefined, filter: query.filter || undefined }))
  const meta = new Map<string, Meta>()
  const items = (r.data.items as [string, number, number, string, string | null][]).map(([path, lat, lon, etag, mime]) => {
    meta.set(path, { key: path, lat, lon })
    return { path, name: path.slice(path.lastIndexOf('/') + 1), isDir: false, size: 0, mime, modified: new Date(0).toISOString(), etag } as Item
  })
  return { items, meta }
}

/** Für Auswahldialoge: die Monate (neueste zuerst) und die Fotos eines Monats, ohne den Speicher der großen Zeitleiste. */
const pickQuery = q({ sort: 'taken', desc: true, stack: false })
export async function galleryMonths(): Promise<TLBucket[]> {
  return (await getJson('/api/gallery/timeline/buckets?' + pickQuery)).data.buckets as TLBucket[]
}
/** In welchem Monat ein Foto liegt (für die eigene Adresse je Foto); null, wenn es das Foto nicht gibt. */
export async function galleryLocate(id: string): Promise<string | null> {
  try { return (await getJson('/api/gallery/timeline/locate?' + q({ id }) + '&' + pickQuery)).data.k as string } catch { return null }
}
export async function galleryMonth(k: string): Promise<{ items: Item[]; meta: Record<string, Meta> }> {
  const meta = new Map<string, Meta>()
  const items = ((await getJson('/api/gallery/timeline/bucket?' + q({ k }) + '&' + pickQuery)).data.items as Row[]).map(x => decode(x, meta))
  return { items, meta: Object.fromEntries(meta) }
}

/** Alle Fotos einer Abfrage, Monat für Monat (etwa alle abgelehnten zum Löschen). Nicht für die Anzeige - die lädt nur, was im Bild ist. */
export async function galleryAll(query: TLQuery): Promise<Item[]> {
  const qs = queryOf(query)
  const buckets = (await getJson('/api/gallery/timeline/buckets?' + qs)).data.buckets as TLBucket[]
  const meta = new Map<string, Meta>()
  const out: Item[] = []
  for (const b of buckets) out.push(...((await getJson('/api/gallery/timeline/bucket?' + q({ k: b.k }) + '&' + qs)).data.items as Row[]).map(x => decode(x, meta)))
  return out
}
