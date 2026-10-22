// Zusaetzliche Beispieldaten fuer die Blog-Bilder (nach seed.mjs) - alles erfunden.
// Aufruf: node blog-seed.mjs <basis-url> <passwort>
const [base, password] = process.argv.slice(2)
let cookie = ''

async function call(method, path, body, form) {
  const r = await fetch(base + path, {
    method,
    headers: { cookie, ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: form ?? (body !== undefined ? JSON.stringify(body) : undefined),
  })
  const set = r.headers.get('set-cookie')
  if (set) cookie = set.split(';')[0]
  if (!r.ok) throw new Error(`${method} ${path}: ${r.status} ${await r.text()}`)
  const text = await r.text()
  return text ? JSON.parse(text) : null
}

await call('POST', '/api/auth/login', { login: 'alex', password })

// ---------- Zweiter Benutzer (zum Teilen, Verwaltung) ----------
await call('POST', '/api/admin/users', { login: 'sam', displayName: 'Sam Berger', password: password + '-sam', isAdmin: false })

// ---------- Einkaufsliste mit Abteilungen ----------
const list = async (name, color, type, sections) => {
  const { id } = await call('POST', '/api/pim/collections/calendars', { name, color, description: null, components: 'VTODO' })
  await call('PUT', `/api/pim/lists/${id}`, { type, sections })
  return id
}
const item = (list, qty, unit, name, section, completed = false) => call('POST', '/api/pim/tasks', {
  list, title: [qty, unit, name].filter(Boolean).join(' '), item: name, qty, unit, section, completed, dueAllDay: true, priority: 0,
})

const shop = await list('Wocheneinkauf', '#13A10E', 'shop', ['Obst & Gemüse', 'Kühlregal', 'Backwaren', 'Drogerie'])
// Abgehakte bleiben an ihrem Platz (durchgestrichen) - so ist der Haken auf dem Bild zu sehen
await call('PUT', `/api/pim/lists/${shop}`, { done: 'place' })
// Vorschlaege (wie frueher gekaufte Artikel) - erscheinen beim Tippen
await call('POST', `/api/pim/lists/${shop}/import`, {
  text: ['Kaffee', 'Kakao', 'Karotten', 'Kartoffelsalat', 'Kapern', 'Kaki', 'Bananen', 'Joghurt', 'Brezeln', 'Duschgel'].join('\n'), apply: true,
})
for (const [qty, unit, name, section, done] of [
  ['2', 'kg', 'Kartoffeln', 'Obst & Gemüse'], ['6', '', 'Äpfel', 'Obst & Gemüse'], ['1', 'Bund', 'Petersilie', 'Obst & Gemüse'],
  ['1', 'l', 'Milch', 'Kühlregal'], ['500', 'g', 'Quark', 'Kühlregal'], ['', '', 'Butter', 'Kühlregal'],
  ['', '', 'Vollkornbrot', 'Backwaren'], ['6', '', 'Brötchen', 'Backwaren', true],
  ['', '', 'Zahnpasta', 'Drogerie'], ['2', 'Pck', 'Taschentücher', 'Drogerie'],
]) await item(shop, qty, unit, name, section, !!done)

// ---------- Checkliste ----------
const pack = await list('Packliste Urlaub', '#0F6CBD', 'check', ['Dokumente', 'Kleidung', 'Kulturbeutel', 'Technik'])
for (const [name, section, done] of [
  ['Personalausweis', 'Dokumente', true], ['Bordkarten', 'Dokumente', true], ['Reiseversicherung', 'Dokumente'],
  ['Badesachen', 'Kleidung', true], ['Regenjacke', 'Kleidung'], ['Wanderschuhe', 'Kleidung'], ['Sonnenhut', 'Kleidung'],
  ['Zahnbürste', 'Kulturbeutel'], ['Sonnencreme', 'Kulturbeutel', true], ['Reiseapotheke', 'Kulturbeutel'],
  ['Ladekabel', 'Technik', true], ['Reiseadapter', 'Technik'], ['Kopfhörer', 'Technik'],
]) await item(pack, '', '', name, section, !!done)

// ---------- Noch eine Notiz mit Kaestchen (Notizi-Aufgaben) ----------
const note = async (html, color) => {
  const { id } = await call('POST', '/api/notes', { folder: null })
  await call('PUT', `/api/notes/${id}/content`, { blocks: html.map(h => ({ html: h })), plain: html.join('\n').replace(/<[^>]+>/g, '').trim() })
  await call('PUT', `/api/notes/${id}/meta`, { color, important: false })
}
await note(['<p><b>Wochenende</b></p>', '<p>☐ Fahrrad aufpumpen</p>', '<p>☑ Blumen gießen</p>', '<p>☐ Oma anrufen</p>', '<p>☐ Laub rechen</p>'], '#C7F0C2')

// ---------- Regel ----------
await call('PUT', '/api/settings/rules', [{
  id: 'blog-stadtwerke', name: 'Rechnungen der Stadtwerke', enabled: true, accountId: null, match: 'all',
  conditions: [{ field: 'from', op: 'contains', value: 'stadtwerke' }, { field: 'subject', op: 'contains', value: 'Rechnung' }],
  actions: [{ type: 'move', value: 'Rechnungen' }, { type: 'category', value: 'dm_rechnung' }, { type: 'markRead', value: '' }],
}])

// ---------- Mehr Dateien ----------
const file = async (dir, name, text) => {
  const form = new FormData()
  form.append('files', new Blob([text]), name)
  await call('POST', `/api/files/upload?path=${encodeURIComponent(dir)}`, undefined, form)
}
await call('POST', '/api/files/mkdir', { path: 'Dokumente/Versicherungen' })
await file('Dokumente', 'Nebenkosten 2025.pdf', '%PDF-1.4 Beispiel '.repeat(2500))
await file('Dokumente', 'Kfz-Versicherung.pdf', '%PDF-1.4 Beispiel '.repeat(1800))
await file('Dokumente', 'Urlaubsplanung.docx', 'Beispiel '.repeat(3000))
await file('Dokumente', 'Rezepte.txt', 'Kürbissuppe\nApfelkuchen\n')
await file('Dokumente', 'Grundriss EG.pdf', '%PDF-1.4 Beispiel '.repeat(12000))

// ---------- Kontingente: mehrere Benutzer mit unterschiedlicher Grenze und Belegung (Verwaltung -> Benutzer) ----------
// Vorgabe fuer alle ohne eigenes Kontingent: 200 MB. Alex nutzt davon einen Teil mit grossen Dateien.
await file('Dokumente', 'Urlaubsvideo.mp4', 'Beispielbild '.repeat(5_000_000))
await call('PUT', '/api/admin/settings', { defaultFileQuotaMb: 200 })
const people = [
  // login, Anzeigename, eigenes Kontingent in MB (null = Vorgabe, 0 = unbegrenzt), belegt in MB
  ['sam', 'Sam Berger', 1024, 40],
  ['lena', 'Lena Berger', 0, 20],
  ['jonas', 'Jonas Weber', 120, 96],
  ['marie', 'Marie Keller', 100, 93],
]
const admin = cookie
for (const [login, name, quota, mb] of people) {
  let users = await call('GET', '/api/admin/users')
  let u = users.find(x => x.login === login)
  if (!u) { await call('POST', '/api/admin/users', { login, displayName: name, password: password + '-' + login, isAdmin: false }); u = (await call('GET', '/api/admin/users')).find(x => x.login === login) }
  if (quota != null) await call('PUT', `/api/admin/users/${u.id}`, { fileQuotaMb: quota })
  cookie = ''
  await call('POST', '/api/auth/login', { login, password: password + '-' + login })
  const chunk = 'Beispieldaten fuer die Doku. '.repeat(1_000_000).slice(0, 20 * 1024 * 1024)
  for (let done = 0, i = 1; done < mb; i++) {
    const size = Math.min(20, mb - done)
    const form = new FormData()
    form.append('files', new Blob([chunk.slice(0, size * 1024 * 1024)]), `Archiv-${String(i).padStart(2, '0')}.zip`)
    await call('POST', '/api/files/upload?path=', undefined, form)
    done += size
  }
  cookie = admin
}
console.log('Kontingente fertig')
console.log('Blog-Daten fertig')
