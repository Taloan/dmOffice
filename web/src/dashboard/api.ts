// Übersicht (Dashboard): Zusammenstellung und Daten der Widgets vom Server (DmOffice.Server/Dashboard).
import { useCallback, useEffect, useRef, useState } from 'react'
import { api, q } from '../api'

export type WidgetType = 'today' | 'events' | 'meals' | 'tasks' | 'shopping' | 'mail' | 'chat' | 'pukki' | 'weather' | 'news' | 'photos' | 'notes' | 'files'
  | 'birthdays' | 'countdown' | 'bookmarks' | 'storage' | 'system' | 'stack'
export interface Widget { id: string; type: WidgetType; size: 1 | 2 | 3 | 4; x?: number | null; y?: number | null; title?: string | null; opts?: Record<string, unknown> | null; stack?: Widget[] | null; smart?: boolean }
export interface Background { kind: 'none' | 'gallery' | 'upload' | 'album'; path?: string | null; album?: number | null; dim: number }
export interface Layout { widgets: Widget[]; cols?: number | null; background: Background; saved: boolean }

/** Geräteart: eigene Zusammenstellung für breite Bildschirme und fürs Handy (App und schmales Web). */
export type Device = 'desktop' | 'phone'

export const dashboardApi = {
  layout: (device: Device) => api.get<Layout>('/api/dashboard/?device=' + device),
  save: (l: Layout, device: Device) => api.put<Layout>('/api/dashboard/?device=' + device, l),
  reset: (device: Device) => api.del<Layout>('/api/dashboard/?device=' + device),
  data: <T = any>(type: WidgetType, opts?: Record<string, unknown> | null) => api.get<T>('/api/dashboard/w/' + type + (opts && Object.keys(opts).length ? '?' + q({ o: JSON.stringify(opts) }) : '')),
  taskDone: (list: number, uri: string, done: boolean) => api.post('/api/dashboard/task', { list, uri, done }),
  uploadBackground: async (file: File, device: Device) => {
    const r = await fetch('/api/dashboard/background?device=' + device, { method: 'PUT', body: file, credentials: 'same-origin' })
    if (!r.ok) throw new Error((await r.json().catch(() => null))?.error || r.statusText)
    return (await r.json()) as Layout
  },
  removeBackground: (device: Device) => api.del<Layout>('/api/dashboard/background?device=' + device),
}

export const newId = () => Math.random().toString(36).slice(2, 10)

/**
 * Zuletzt geladener Stand je Widget und der Zusammenstellung - nur im Speicher dieser Seite (nicht im Browser gespeichert,
 * es stehen etwa Mail-Betreffs darin). Zurück in der Übersicht steht sofort alles da und wird im Hintergrund aufgefrischt.
 */
export const dashCache = { layout: {} as Partial<Record<Device, Layout>>, bg: {} as Partial<Record<Device, string | null>>, data: new Map<string, unknown>() }

/**
 * Daten eines Widgets: beim Öffnen, bei Rückkehr ins Fenster und jede Minute neu; `reload` holt sofort (nach einer Aktion).
 * Fehler bleiben beim Widget (es zeigt sie selbst), die anderen laufen weiter.
 */
export function useWidgetData<T>(type: WidgetType, opts: Record<string, unknown> | null | undefined, every = 60_000) {
  const key = JSON.stringify(opts ?? {})
  const [data, setData] = useState<T | null>(() => (dashCache.data.get(type + key) as T | undefined) ?? null)
  const [error, setError] = useState<string | null>(null)
  const seq = useRef(0)
  const load = useCallback(async () => {
    const n = ++seq.current
    try { const d = await dashboardApi.data<T>(type, opts); dashCache.data.set(type + key, d); if (n === seq.current) { setData(d); setError(null) } }
    catch (e: any) {
      if (n !== seq.current) return
      setError(e?.message || String(e))
    }
  }, [type, key]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    load()
    const iv = setInterval(() => { if (!document.hidden) load() }, every)
    const on = () => { if (!document.hidden) load() }
    window.addEventListener('focus', on)
    return () => { clearInterval(iv); window.removeEventListener('focus', on) }
  }, [load, every])
  return { data, error, reload: load }
}
