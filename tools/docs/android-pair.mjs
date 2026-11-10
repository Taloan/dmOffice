// Legt fuer alex ein App-Passwort an und schreibt den Kopplungs-Link (dmoffice://pair?...) nach .data-serve/pair.txt
// Aufruf: node android-pair.mjs <basis-url> <passwort>
import { writeFileSync } from 'node:fs'
const [base, password] = process.argv.slice(2)
let cookie = ''
async function call(method, path, body) {
  const r = await fetch(base + path, { method, headers: { cookie, ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined })
  const set = r.headers.get('set-cookie'); if (set) cookie = set.split(';')[0]
  if (!r.ok) throw new Error(`${method} ${path}: ${r.status} ${await r.text()}`)
  const t = await r.text(); return t ? JSON.parse(t) : null
}
await call('POST', '/api/auth/login', { login: 'alex', password })
const dav = await call('GET', '/api/settings/dav')
const { password: token } = await call('POST', '/api/settings/app-passwords', { name: 'Handy-App (Android-Bilder)' })
const q = new URLSearchParams({ v: '1', url: base, user: dav.login, token, name: 'Alex Berger' })
if (dav.serverKey) q.set('spk', dav.serverKey)
writeFileSync(new URL('./.data-serve/pair.txt', import.meta.url), 'dmoffice://pair?' + q.toString())
console.log('Link in .data-serve/pair.txt')
