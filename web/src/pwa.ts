/// <reference types="vite/client" />
// Installierbare App (PWA): Service Worker anmelden, Updates erkennen, Installieren anbieten.
// Der Service Worker selbst steht in sw/sw.js und wird beim Bauen zu /sw.js.
import { useSyncExternalStore } from 'react'

type InstallEvent = Event & { prompt: () => Promise<void>; userChoice: Promise<{ outcome: 'accepted' | 'dismissed' }> }

interface PwaState {
  /** Eine neue Version ist geladen und wartet auf "Neu laden". */
  update: boolean
  /** Der Browser bietet die Installation an (Chrome, Edge, Android). */
  canInstall: boolean
  /** Laeuft bereits als installierte App im eigenen Fenster. */
  installed: boolean
}

let state: PwaState = {
  update: false,
  canInstall: false,
  installed: isInstalled(),
}

/** Eigenes Fenster - je nach Browser "standalone", "window-controls-overlay" oder "minimal-ui". */
function isInstalled() {
  return ['standalone', 'window-controls-overlay', 'minimal-ui'].some(m => matchMedia(`(display-mode: ${m})`).matches)
    || (navigator as any).standalone === true
}
let installEvent: InstallEvent | null = null
let waiting: ServiceWorker | null = null
const listeners = new Set<() => void>()
const set = (p: Partial<PwaState>) => { state = { ...state, ...p }; markInstalled(); listeners.forEach(l => l()) }
// Fuer CSS: eigenes Fenster (mit oder ohne Titelleiste des Browsers)
const markInstalled = () => document.documentElement.classList.toggle('dm-app', state.installed)
markInstalled()

// Frueh mithoeren - das Ereignis kommt oft, bevor React die Einstellungen zeichnet
addEventListener('beforeinstallprompt', e => { e.preventDefault(); installEvent = e as InstallEvent; set({ canInstall: true }) })
addEventListener('appinstalled', () => { installEvent = null; set({ canInstall: false, installed: true }) })
for (const m of ['standalone', 'window-controls-overlay', 'minimal-ui'])
  matchMedia(`(display-mode: ${m})`).addEventListener('change', () => set({ installed: isInstalled() }))

// Titelleiste ausgeblendet (Window Controls Overlay): Platz fuer die Fensterknoepfe nur aus den Werten,
// die der Browser wirklich liefert. Manche Browser (Vivaldi) melden den Modus ohne brauchbare Masse -
// dann bleibt die Kopfleiste wie im normalen Fenster.
const wco = (navigator as any).windowControlsOverlay as
  | { visible: boolean; getTitlebarAreaRect(): DOMRect; addEventListener(t: string, f: () => void): void } | undefined
function applyOverlay() {
  const root = document.documentElement
  // Nur im echten Overlay-Modus - eingebettete Browser melden "visible" teils auch im normalen Tab
  const r = wco?.visible && matchMedia('(display-mode: window-controls-overlay)').matches ? wco.getTitlebarAreaRect() : null
  const ok = !!r && r.width > 100 && r.width <= innerWidth && r.height > 0
  root.classList.toggle('dm-wco', ok)
  if (ok) {
    root.style.setProperty('--tb-left', `${r!.x}px`)
    root.style.setProperty('--tb-right', `${Math.max(0, innerWidth - r!.x - r!.width)}px`)
    root.style.setProperty('--tb-height', `${r!.height}px`)
  }
}
wco?.addEventListener('geometrychange', applyOverlay)
matchMedia('(display-mode: window-controls-overlay)').addEventListener('change', applyOverlay)
addEventListener('resize', applyOverlay)
applyOverlay()

export function usePwa() {
  return useSyncExternalStore(l => { listeners.add(l); return () => listeners.delete(l) }, () => state)
}

/** Installationsdialog des Browsers zeigen. */
export async function install() {
  if (!installEvent) return false
  const e = installEvent
  installEvent = null
  set({ canInstall: false })
  await e.prompt()
  return (await e.userChoice).outcome === 'accepted'
}

/** Neue Version uebernehmen: der wartende Service Worker uebernimmt, danach laedt die Seite neu. */
export function applyUpdate() {
  if (waiting) waiting.postMessage('SKIP_WAITING')
  else location.reload()
}

/** iPhone/iPad: dort gibt es kein beforeinstallprompt, nur "Teilen -> Zum Home-Bildschirm". */
export const isIos = () => /iPad|iPhone|iPod/.test(navigator.userAgent) || (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)

/** Service Worker gibt es nur unter HTTPS oder localhost. */
export const pwaPossible = () => 'serviceWorker' in navigator && window.isSecureContext

export function registerServiceWorker() {
  if (!pwaPossible() || !import.meta.env.PROD) return
  const hadController = !!navigator.serviceWorker.controller
  let reloading = false
  navigator.serviceWorker.addEventListener('controllerchange', () => {
    // Beim allerersten Anmelden uebernimmt der Worker ohne Update - dann nicht neu laden
    if (!hadController || reloading) return
    reloading = true
    location.reload()
  })
  addEventListener('load', async () => {
    try {
      // /sw statt /sw.js: ein Proxy mit Asset-Cache haelt *.js fest, dann kaeme kein Update an
      const reg = await navigator.serviceWorker.register('/sw', { scope: '/', updateViaCache: 'none' })
      const offer = (w: ServiceWorker | null) => {
        if (!w || !navigator.serviceWorker.controller) return
        waiting = w
        set({ update: true })
      }
      offer(reg.waiting)
      reg.addEventListener('updatefound', () => {
        const w = reg.installing
        w?.addEventListener('statechange', () => { if (w.state === 'installed') offer(w) })
      })
      // Lange offene Fenster: regelmaessig und beim Zurueckholen nach einer neuen Version fragen
      const check = () => reg.update().catch(() => {})
      setInterval(check, 30 * 60000)
      document.addEventListener('visibilitychange', () => { if (!document.hidden) check() })
    } catch (e) {
      console.warn('Service Worker nicht angemeldet', e)
    }
  })
}
