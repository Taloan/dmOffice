import { useCallback, useEffect, useRef, useState } from 'react'
import { currentPrefs } from '../prefs'
import { locale } from '../i18n'

/** Laedt sofort und dann im Takt; pausiert, solange der Tab verdeckt ist. */
export function usePoll<T>(load: () => Promise<T>, everyMs: number, refreshEvent?: string): T | null {
  const [data, setData] = useState<T | null>(null)
  const loadRef = useRef(load)
  loadRef.current = load
  useEffect(() => {
    let alive = true
    const tick = () => {
      if (document.hidden) return
      loadRef.current().then(d => alive && setData(d)).catch(() => {})
    }
    tick()
    const t = setInterval(tick, everyMs)
    const onVisible = () => !document.hidden && tick()
    document.addEventListener('visibilitychange', onVisible)
    // Sofort neu laden, wenn jemand Bescheid gibt (z.B. Mail gelesen) - nicht erst beim naechsten Takt
    if (refreshEvent) window.addEventListener(refreshEvent, tick)
    return () => {
      alive = false
      clearInterval(t)
      document.removeEventListener('visibilitychange', onVisible)
      if (refreshEvent) window.removeEventListener(refreshEvent, tick)
    }
  }, [everyMs, refreshEvent])
  return data
}

/** Name der Version ("1.5") und Build ("2026.10.22-a643818") des Servers aus /api/health (ohne Anmeldung); leer, solange sie nicht da sind. */
export function useVersion(): { release: string; build: string } {
  const [v, setV] = useState({ release: '', build: '' })
  useEffect(() => {
    fetch('/api/health', { cache: 'no-store' }).then(r => r.json()).then(d => setV({ release: String(d.release ?? ''), build: String(d.version ?? '') })).catch(() => {})
  }, [])
  return v
}

/** Ist der Server erreichbar? Prueft regelmaessig /api/health; false bei Netzfehler, Zeitueberschreitung oder 5xx vom Proxy. */
export const REACHABLE_RECHECK = 'dmoffice:reachable'
export function useReachable(): boolean {
  const [ok, setOk] = useState(true)
  useEffect(() => {
    let alive = true
    const check = async () => {
      if (document.hidden) return
      const ctl = new AbortController()
      const timer = setTimeout(() => ctl.abort(), 8000)
      try {
        const r = await fetch('/api/health', { cache: 'no-store', signal: ctl.signal })
        if (alive) setOk(r.ok)
      } catch { if (alive) setOk(false) } finally { clearTimeout(timer) }
    }
    check()
    const t = setInterval(check, 15000)
    const on = () => { check() }
    window.addEventListener('online', on); window.addEventListener('offline', () => alive && setOk(false))
    window.addEventListener(REACHABLE_RECHECK, on); document.addEventListener('visibilitychange', on)
    return () => {
      alive = false; clearInterval(t)
      window.removeEventListener('online', on); window.removeEventListener(REACHABLE_RECHECK, on); document.removeEventListener('visibilitychange', on)
    }
  }, [])
  return ok
}

/** Daten laden mit Neuladen auf Zuruf. */
export function useLoad<T>(load: () => Promise<T>, deps: unknown[]): [T | null, () => void, boolean] {
  const [data, setData] = useState<T | null>(null)
  const [loading, setLoading] = useState(false)
  const [n, setN] = useState(0)
  useEffect(() => {
    let alive = true
    setLoading(true)
    load().then(d => alive && setData(d)).catch(() => alive && setData(null)).finally(() => alive && setLoading(false))
    return () => { alive = false }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [...deps, n])
  const reload = useCallback(() => setN(x => x + 1), [])
  return [data, reload, loading]
}

/** Wert erst nach einer Pause uebernehmen - fuer Suchfelder. */
export function useDebounced<T>(value: T, ms = 300): T {
  const [v, setV] = useState(value)
  useEffect(() => {
    const t = setTimeout(() => setV(value), ms)
    return () => clearTimeout(t)
  }, [value, ms])
  return v
}

// Datum und Uhrzeit nach Einstellungen -> Allgemein -> Sprache und Zeit
const p2 = (n: number) => String(n).padStart(2, '0')

export function fmtTime(d: Date): string {
  if (currentPrefs.timeFormat === '12') {
    const h = d.getHours() % 12 || 12
    return `${h}:${p2(d.getMinutes())} ${d.getHours() < 12 ? 'AM' : 'PM'}`
  }
  return `${p2(d.getHours())}:${p2(d.getMinutes())}`
}

export function fmtDay(d: Date, withYear = true): string {
  const y = d.getFullYear(), m = d.getMonth() + 1, day = d.getDate()
  switch (currentPrefs.dateFormat) {
    case 'd.M.yyyy': return withYear ? `${day}.${m}.${y}` : `${day}.${m}.`
    case 'dd.MM.yy': return withYear ? `${p2(day)}.${p2(m)}.${String(y).slice(2)}` : `${p2(day)}.${p2(m)}.`
    case 'yyyy-MM-dd': return withYear ? `${y}-${p2(m)}-${p2(day)}` : `${p2(m)}-${p2(day)}`
    default: return withYear ? `${p2(day)}.${p2(m)}.${y}` : `${p2(day)}.${p2(m)}.`
  }
}

export function fmtDate(iso: string, withTime = true): string {
  const d = new Date(iso)
  const now = new Date()
  const sameDay = d.toDateString() === now.toDateString()
  if (sameDay && withTime) return fmtTime(d)
  const diff = (now.getTime() - d.getTime()) / 86400000
  if (diff < 6 && diff > 0) return d.toLocaleDateString(locale, { weekday: 'short' }) + (withTime ? ' ' + fmtTime(d) : '')
  return fmtDay(d, d.getFullYear() !== now.getFullYear())
}

export function fmtLong(iso: string): string {
  const d = new Date(iso)
  return `${d.toLocaleDateString(locale, { weekday: 'short' })} ${fmtDay(d)} ${fmtTime(d)}`
}
