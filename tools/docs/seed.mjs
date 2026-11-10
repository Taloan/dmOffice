// Beispieldaten fuer die Doku-Screenshots ueber die normale API - erfundene Person "Alex Berger".
// Aufruf: node seed.mjs <basis-url> <passwort>   (der Benutzer alex muss schon eingerichtet sein)
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

// ---------- Termine und Aufgaben (als .ics importiert) ----------
const pad = n => String(n).padStart(2, '0')
const day = (offset, h = 0, m = 0) => {
  const d = new Date(); d.setDate(d.getDate() + offset); d.setHours(h, m, 0, 0); return d
}
const local = d => `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}T${pad(d.getHours())}${pad(d.getMinutes())}00`
const date = d => `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}`
// Montag dieser Woche als Bezug, damit die Wochenansicht gefuellt ist
const monday = (() => { const d = new Date(); return (d.getDay() + 6) % 7 })()
const w = n => n - monday

let uid = 0
const event = (title, start, end, extra = '') =>
  `BEGIN:VEVENT\r\nUID:docs-${++uid}@dmoffice\r\nDTSTAMP:20261001T000000Z\r\nSUMMARY:${title}\r\n` +
  `DTSTART;TZID=Europe/Berlin:${local(start)}\r\nDTEND;TZID=Europe/Berlin:${local(end)}\r\n${extra}END:VEVENT\r\n`
const allDay = (title, start, days = 1) =>
  `BEGIN:VEVENT\r\nUID:docs-${++uid}@dmoffice\r\nDTSTAMP:20261001T000000Z\r\nSUMMARY:${title}\r\n` +
  `DTSTART;VALUE=DATE:${date(start)}\r\nDTEND;VALUE=DATE:${date(new Date(start.getFullYear(), start.getMonth(), start.getDate() + days))}\r\nEND:VEVENT\r\n`
const todo = (title, due, done = false, prio = 0) =>
  `BEGIN:VTODO\r\nUID:docs-${++uid}@dmoffice\r\nDTSTAMP:20261001T000000Z\r\nSUMMARY:${title}\r\n` +
  (due ? `DUE;VALUE=DATE:${date(due)}\r\n` : '') + (prio ? `PRIORITY:${prio}\r\n` : '') +
  (done ? 'STATUS:COMPLETED\r\nPERCENT-COMPLETE:100\r\n' : 'STATUS:NEEDS-ACTION\r\n') + 'END:VTODO\r\n'

const ics = 'BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//dmOffice//Doku//DE\r\n' +
  event('Teambesprechung', day(w(0), 9, 30), day(w(0), 10, 30), 'LOCATION:Raum 2.14\r\nRRULE:FREQ=WEEKLY;BYDAY=MO\r\n') +
  event('Zahnarzt', day(w(1), 8, 0), day(w(1), 8, 45), 'LOCATION:Praxis Dr. Lang\r\n') +
  event('Mittagessen mit Laura', day(w(2), 12, 0), day(w(2), 13, 0)) +
  event('Projekt-Review Q4', day(w(2), 14, 0), day(w(2), 15, 30), 'LOCATION:Online\r\n') +
  event('Elektriker – Wallbox', day(w(3), 10, 0), day(w(3), 12, 0)) +
  event('Laufen im Park', day(w(3), 18, 30), day(w(3), 19, 30), 'RRULE:FREQ=WEEKLY;BYDAY=TH\r\n') +
  event('Grillabend bei Jana', day(w(5), 18, 0), day(w(5), 22, 0)) +
  allDay('Herbstferien', day(w(4)), 3) +
  todo('Angebot Wallbox prüfen', day(1), false, 1) +
  todo('Projektplan Q4 kommentieren', day(3)) +
  todo('Buch zur Bibliothek bringen', day(6)) +
  todo('Reifenwechsel buchen', null) +
  todo('Steuererklärung abgeben', day(-2), true) +
  'END:VCALENDAR\r\n'

const calendars = await call('GET', '/api/pim/collections/calendars')
const cal = calendars[0]
await call('PUT', `/api/pim/collections/${cal.id}`, { name: 'Persönlich', color: '#0F6CBD', description: null, components: 'VEVENT,VTODO' })
const work = await call('POST', '/api/pim/collections/calendars', { name: 'Arbeit', color: '#CA5010', description: null, components: 'VEVENT' })
const upload = async (id, name, text) => {
  const form = new FormData()
  form.append('files', new Blob([text], { type: 'text/plain' }), name)
  return call('POST', `/api/pim/import/${id}`, undefined, form)
}
console.log('Kalender', await upload(cal.id, 'termine.ics', ics))
const workIcs = 'BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//dmOffice//Doku//DE\r\n' +
  event('Kundentermin Hoffmann AG', day(w(1), 13, 0), day(w(1), 14, 30), 'LOCATION:Hoffmann AG\r\n') +
  event('Schulung Datenschutz', day(w(4), 9, 0), day(w(4), 11, 0)) + 'END:VCALENDAR\r\n'
console.log('Arbeit', await upload(work.id ?? (await call('GET', '/api/pim/collections/calendars')).find(c => c.name === 'Arbeit').id, 'arbeit.ics', workIcs))

// ---------- Kontakte (als .vcf importiert) ----------
const card = (first, last, email, tel, org = '', city = '') =>
  `BEGIN:VCARD\r\nVERSION:3.0\r\nUID:docs-card-${++uid}\r\nN:${last};${first};;;\r\nFN:${first} ${last}\r\n` +
  (org ? `ORG:${org}\r\n` : '') + `EMAIL;TYPE=HOME:${email}\r\nTEL;TYPE=CELL:${tel}\r\n` +
  (city ? `ADR;TYPE=HOME:;;Hauptstraße 1;${city};;12345;Deutschland\r\n` : '') + 'END:VCARD\r\n'
const vcf =
  card('Jana', 'Wolff', 'jana.wolff@example.org', '+49 151 0000001', '', 'Musterstadt') +
  card('Tom', 'Becker', 'tom.becker@example.net', '+49 151 0000002') +
  card('Laura', 'Neumann', 'laura.neumann@firma.example', '+49 30 0000003', 'Firma Beispiel GmbH') +
  card('Paul', 'Hoffmann', 'p.hoffmann@hoffmann-ag.example', '+49 40 0000004', 'Hoffmann AG') +
  card('Sophie', 'Krüger', 'sophie.krueger@example.org', '+49 160 0000005', '', 'Beispieldorf') +
  card('Elektro', 'Sommer', 'info@elektro-sommer.example', '+49 89 0000006', 'Elektro Sommer GmbH')
const books = await call('GET', '/api/pim/collections/addressbooks')
console.log('Kontakte', await upload(books[0].id, 'kontakte.vcf', vcf))

// ---------- Notizen ----------
const note = async (html, color, important = false) => {
  const { id } = await call('POST', '/api/notes', { folder: null })
  await call('PUT', `/api/notes/${id}/content`, { blocks: html.map(h => ({ html: h })), plain: html.join('\n').replace(/<\/(li|p)>/g, '\n').replace(/<[^>]+>/g, '').replace(/\n+/g, '\n').trim() })
  await call('PUT', `/api/notes/${id}/meta`, { color, important })
}
await note(['<p><b>Einkaufen</b></p>', '<p>☐ Milch</p>', '<p>☑ Brot</p>', '<p>☐ Tomaten</p>', '<p>☐ Kaffee</p>'], '#FFF9A8')
await note(['<p><b>Ideen Garten</b></p>', '<ul><li>Hochbeet an der Südseite</li><li>Regentonne an das Fallrohr</li><li>Kräuterspirale</li></ul>'], '#C7F0C2', true)
await note(['<p><b>WLAN Gäste</b></p>', '<p>Netz: Gaeste-Berger</p>', '<p style="font-family:Consolas">Passwort steht auf dem Router</p>'], '#BDE3FF')
await note(['<p><b>Geschenkideen</b></p>', '<p>Jana: Kochbuch</p>', '<p>Tom: Kletterkurs</p>'], '#FFD6E7')

// ---------- Dateien ----------
await call('POST', '/api/files/mkdir', { path: 'Dokumente' })
await call('POST', '/api/files/mkdir', { path: 'Fotos' })
const file = async (dir, name, text) => {
  const form = new FormData()
  form.append('files', new Blob([text]), name)
  await call('POST', `/api/files/upload?path=${encodeURIComponent(dir)}`, undefined, form)
}
await file('Dokumente', 'Mietvertrag.pdf', '%PDF-1.4 Beispiel '.repeat(4000))
await file('Dokumente', 'Haushaltsbuch.xlsx', 'Beispiel '.repeat(9000))
await file('', 'Packliste Urlaub.txt', 'Zahnbürste\nLadekabel\nSonnencreme\n')

// ---------- Oberflaeche: hell, Aufgabenleiste mit Kalender ----------
await call('PUT', '/api/settings/ui', { todoBar: ['calendar', 'tasks'], favorites: [] })
console.log('fertig')
