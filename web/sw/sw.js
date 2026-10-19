// Service Worker fuer die installierbare App (PWA).
// Vorlage: vite.config.ts setzt beim Bauen VERSION und die Liste der Dateien ein und
// schreibt das Ergebnis als /sw.js in den wwwroot. Der Server liefert sw.js mit no-cache aus.
//
// - Statische Dateien (assets/*, Symbole, Manifest) werden beim Installieren vorab geladen
//   und danach aus dem Zwischenspeicher bedient - die Namen tragen einen Hash.
// - Seitenaufrufe gehen immer zuerst ans Netz; ohne Verbindung erscheint eine Offline-Seite.
// - /api und /dav werden nie angefasst: Post, Termine und Dateien kommen immer frisch vom Server.
// - Eine neue Version wartet, bis die App "Neu laden" sagt (Nachricht SKIP_WAITING).

const VERSION = '__VERSION__'
const PRECACHE = '__PRECACHE__'
const CACHE = 'dmoffice-' + VERSION

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(PRECACHE)))
})

self.addEventListener('activate', event => {
  event.waitUntil((async () => {
    for (const key of await caches.keys())
      if (key.startsWith('dmoffice-') && key !== CACHE) await caches.delete(key)
    await self.clients.claim()
  })())
})

self.addEventListener('message', event => {
  if (event.data === 'SKIP_WAITING') self.skipWaiting()
})

self.addEventListener('fetch', event => {
  const req = event.request
  if (req.method !== 'GET') return
  const url = new URL(req.url)
  if (url.origin !== self.location.origin) return
  const path = url.pathname
  if (path.startsWith('/api/') || path === '/api' || path.startsWith('/dav/') || path === '/dav' || path.startsWith('/.well-known/')) return

  if (req.mode === 'navigate') {
    event.respondWith(fetch(req).catch(() => offline()))
    return
  }
  if (PRECACHE.includes(path)) {
    event.respondWith(caches.match(req, { cacheName: CACHE, ignoreSearch: true }).then(hit => hit || fetch(req)))
  }
})

function offline() {
  const html = `<!doctype html>
<html lang="de"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<meta name="color-scheme" content="light dark"><meta name="theme-color" content="#7B1DD3"><title>dmOffice – offline</title>
<style>
  body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center; text-align: center;
    font-family: 'Segoe UI', -apple-system, BlinkMacSystemFont, Roboto, 'Helvetica Neue', sans-serif; background: #fff; color: #242424; }
  @media (prefers-color-scheme: dark) { body { background: #1c1d22; color: #f0f0f0; } p.en { color: #adadad; } }
  main { padding: 24px; max-width: 420px; }
  img { width: 72px; height: 72px; }
  h1 { font-size: 22px; margin: 16px 0 8px; font-weight: 600; }
  p { margin: 4px 0; line-height: 1.4; }
  p.en { color: #616161; }
  button { margin-top: 20px; font: inherit; font-size: 14px; padding: 6px 20px; border-radius: 4px; border: none; background: #7B1DD3; color: #fff; cursor: pointer; }
  button:hover { background: #115EA3; }
</style></head>
<body><main>
  <img src="/icon-192.png" alt="">
  <h1>Keine Verbindung zu dmOffice</h1>
  <p>Der Server ist gerade nicht erreichbar. Sobald die Verbindung zurück ist, lädt die Seite von selbst neu.</p>
  <p class="en" lang="en">No connection to dmOffice. The page reloads by itself once the server is reachable again.</p>
  <button onclick="location.reload()">Erneut versuchen · Retry</button>
</main>
<script>
  addEventListener('online', () => location.reload())
  setInterval(() => fetch('/api/health', { cache: 'no-store' }).then(r => { if (r.ok) location.reload() }).catch(() => {}), 15000)
</script>
</body></html>`
  return new Response(html, { status: 503, headers: { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' } })
}

// Web-Push (vorbereitet): der Server schickt JSON { title, body, url, tag }.
// Noch sendet dmOffice nichts - siehe docs/web-push.md.
self.addEventListener('push', event => {
  let data = {}
  try { data = event.data ? event.data.json() : {} } catch { data = { body: event.data && event.data.text() } }
  event.waitUntil(self.registration.showNotification(data.title || 'dmOffice', {
    body: data.body || '',
    icon: '/icon-192.png',
    badge: '/icon-maskable-192.png',
    tag: data.tag,
    data: { url: data.url || '/' },
  }))
})

self.addEventListener('notificationclick', event => {
  event.notification.close()
  const target = new URL((event.notification.data && event.notification.data.url) || '/', self.location.origin)
  event.waitUntil((async () => {
    const wins = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
    for (const w of wins) {
      if (new URL(w.url).origin !== target.origin) continue
      await w.focus()
      if (target.hash) w.navigate(target.href).catch(() => {})
      return
    }
    await self.clients.openWindow(target.href)
  })())
})
