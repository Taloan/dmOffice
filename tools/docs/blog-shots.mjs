// Bilder fuer Blog-Artikel mit Edge/Chrome (puppeteer-core) - Ergebnis in docs/img/blog/.
// Aufruf: node blog-shots.mjs <basis-url> <passwort> <browser.exe> [nur,diese,bilder]
// Voraussetzung: seed.mjs und blog-seed.mjs sind gelaufen, Mails aus DocsSeed (DMOFFICE_DOCS_BLOG=1).
import puppeteer from 'puppeteer-core'
import { mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const [base, password, executablePath, only] = process.argv.slice(2)
const wanted = only ? new Set(only.split(',')) : null
const out = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'docs', 'img', 'blog')
mkdirSync(out, { recursive: true })

const VIEW = { width: 1440, height: 900, deviceScaleFactor: 1 }
const browser = await puppeteer.launch({ executablePath, headless: true, defaultViewport: VIEW })
const page = await browser.newPage()
await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'light' }])
// Adressen wie hinter einem Reverse-Proxy (Geraete & Sync, QR-Code)
await page.setExtraHTTPHeaders({ 'X-Forwarded-Host': 'office.beispiel.de', 'X-Forwarded-Proto': 'https' })
await page.goto(base, { waitUntil: 'networkidle0' })

const pause = ms => new Promise(r => setTimeout(r, ms))
const json = (method, path, body) => page.evaluate(async (method, path, body) => {
  const r = await fetch(path, { method, headers: body !== undefined ? { 'Content-Type': 'application/json' } : {}, body: body !== undefined ? JSON.stringify(body) : undefined })
  const text = await r.text()
  return text ? JSON.parse(text) : null
}, method, path, body)
/** Oberflaechen-Einstellungen aendern (Server und Kopie im Browser); null entfernt einen Wert. */
const prefs = patch => page.evaluate(async patch => {
  const p = await (await fetch('/api/settings/ui')).json()
  for (const [k, v] of Object.entries(patch)) { if (v === null) delete p[k]; else p[k] = v }
  await fetch('/api/settings/ui', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(p) })
  localStorage.setItem('dmoffice.ui', JSON.stringify(p))
}, patch)
const setTheme = theme => page.evaluate(async theme => {
  localStorage.setItem('dmoffice.theme', theme)
  await fetch('/api/settings/general', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ theme }) })
}, theme)
const local = (key, value) => page.evaluate((k, v) => localStorage.setItem(k, v), key, value)

await page.evaluate(async password => {
  await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ login: 'alex', password }) })
}, password)
await setTheme('light')
await prefs({ language: 'de', accent: null, ribbonLabels: null })

async function go(hash, wait = 1200) {
  await page.goto(base + '/#/' + hash, { waitUntil: 'networkidle0' })
  await page.reload({ waitUntil: 'networkidle0' })
  await pause(wait)
}
async function shot(name) {
  if (wanted && !wanted.has(name)) return
  await page.mouse.move(5, page.viewport().height - 5)
  await pause(400)
  await page.screenshot({ path: join(out, name + '.png') })
  console.log('  ' + name)
}
/** Erstes sichtbares Element mit genau/teilweise diesem Text anklicken (wie shots.mjs). */
async function click(text, selector = '*') {
  const ok = await page.evaluate((text, selector) => {
    const els = [...document.querySelectorAll(selector)].filter(e => e.offsetParent && e.textContent?.trim() === text)
    const el = els.at(-1) ?? [...document.querySelectorAll(selector)].find(e => e.offsetParent && e.children.length === 0 && e.textContent?.includes(text))
    if (!el) return false
    el.scrollIntoView({ block: 'center' }); (el.closest('button,[role=option],[role=menuitem],[role=menuitemcheckbox],[role=tab],[role=checkbox],label') ?? el).click()
    return true
  }, text, selector)
  if (!ok) console.warn('    nicht gefunden: ' + text)
  await pause(700)
  return ok
}
/** Mit der echten Maus auf die Mitte eines Elements klicken (fuer Menues und Auswahllisten). */
async function mouse(selector, text) {
  const box = await page.evaluate((selector, text) => {
    // text "=Wort": genau dieser Text, sonst enthalten
    const hit = e => !text || (text.startsWith('=') ? e.textContent?.trim() === text.slice(1) : e.textContent?.trim().includes(text))
    const el = [...document.querySelectorAll(selector)].find(e => e.offsetParent !== null && hit(e))
    if (!el) return null
    el.scrollIntoView({ block: 'center' })
    const r = el.getBoundingClientRect()
    return { x: r.x + r.width / 2, y: r.y + r.height / 2 }
  }, selector, text)
  if (!box) { console.warn('    nicht gefunden: ' + selector + ' ' + (text ?? '')); return false }
  await page.mouse.click(box.x, box.y)
  await pause(700)
  return true
}
async function type(selector, text) {
  await page.click(selector)
  await page.keyboard.type(text, { delay: 25 })
  await pause(600)
}
const escape = async () => { await page.keyboard.press('Escape'); await pause(400); await blur() }
// Fokusrahmen (z. B. am Menueband-Knopf nach einem Menue) nicht mit aufs Bild
const blur = () => page.evaluate(() => document.activeElement?.blur?.())
// Hinweise unten rechts ("Im Kalender eingetragen") ausblenden lassen
const noToasts = () => pause(6000)

// ======================= Mail =======================
await go('mail')
await click('Ihr Kundenkonto: Bitte Daten bis morgen bestätigen')
await pause(800)
await shot('m-junk-unsicher')

// Der Newsletter liegt im Prio-Posteingang unter "Sonstige"
await mouse('[role=tab]', 'Sonstige')
await click('Gartenbrief Oktober: Zwiebeln setzen, Laub nutzen')
await pause(800)
await mouse('button', 'Bilder anzeigen')
await shot('m-bilder-blockiert')
await escape()
await mouse('[role=tab]', 'Relevant')

await click('Grillabend am Samstag?')
await mouse('button', 'Nachverfolgen')
await shot('m-nachverfolgen-menu')
await escape()

// Erst die Karten zeigen, dann alle drei in den Kalender (Hinweise abwarten)
await click('Ihre Bestellung GL-20931 ist unterwegs')
await pause(800)
await shot('m-smart-paket')
await click('Buchungsbestätigung K7QZ2M – Frankfurt → Lissabon')
await pause(800)
await shot('m-smart-flug')
await click('In Kalender')
await click('Ihre Bestellung GL-20931 ist unterwegs')
await pause(800)
await click('In Kalender')
await click('Ihre Tischreservierung T-5521')
await pause(800)
await click('In Kalender')
await noToasts()

await click('Einladung: Workshop Roadmap 2027')
await pause(1200)
await shot('m-einladung')

// Suchordner anlegen: Knopf "+" neben "Suchordner" im Ordnerbereich
await page.evaluate(() => {
  const head = [...document.querySelectorAll('span,div')].find(e => e.offsetParent && e.children.length === 0 && e.textContent?.trim() === 'Suchordner')
  head?.parentElement?.querySelector('button')?.click()
})
await pause(800)
await page.keyboard.type('Rechnungen', { delay: 25 })
await type('input[placeholder="z. B. rechnung von:telekom"]', 'rechnung')
await mouse('button[aria-label="#CA5010"]')
await mouse('[role=combobox]', 'Beliebig')
await mouse('[role=option],[role=menuitemcheckbox]', '=Rechnung')
await mouse('h2', 'Neuer Suchordner')
await pause(800)
await shot('m-suchordner-dialog')
await click('Speichern', 'button')
await pause(1500)
// Den neuen Suchordner im Ordnerbereich oeffnen (der Mailordner "Rechnungen" steht weiter oben)
await click('Rechnungen')
await pause(1200)
await click('Rechnung Nr. 4711 – Heckenschnitt')
await pause(800)
await shot('m-suchordner')

await go('settings/rules')
await shot('m-regel-dialog')

await go('tasks')
await click('E-Mail-Nachverfolgung')
await pause(800)
await shot('m-nachverfolgung-liste')

await local('dmoffice.calendar.view', 'timeGridWeek')
await go('calendar', 1800)
await shot('m-ankuendigungen-kalender')

// ======================= Kalender =======================
await local('dmoffice.calendar.view', 'dayGridMonth')
await go('calendar', 1800)
await shot('k-monat')

await local('dmoffice.calendar.view', 'timeGridWeek')
await go('calendar', 1800)
await click('Neuer Termin')
await pause(800)
await page.keyboard.type('Elternabend nächsten Mittwoch 19:30', { delay: 25 })
await pause(800)
await shot('k-termin-text')
await escape()

await go('calendar', 1500)
await click('Kalender abonnieren')
await pause(800)
await page.keyboard.type('https://termine.blau-weiss.example/vereinskalender.ics', { delay: 15 })
await page.keyboard.press('Tab')
await page.keyboard.type('Sportverein Blau-Weiß', { delay: 15 })
await pause(600)
await shot('k-abonnieren-dialog')
await click('Abbrechen', 'button')

await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'dark' }])
await setTheme('dark')
await go('calendar', 1800)
await shot('k-dunkel')
await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'light' }])
await setTheme('light')
await local('dmoffice.calendar.view', 'timeGridWorkWeek')

// ======================= Aufgaben =======================
await go('tasks')
await click('Wocheneinkauf')
await pause(800)
await shot('a-einkaufsliste')
await type('input[placeholder^="Artikel hinzufügen"]', 'Ka')
await pause(600)
await shot('a-einkauf-vorschlaege')
await page.keyboard.press('Escape')
await page.mouse.click(1000, 850)
await pause(400)

// Menue "..." an der Liste -> Teilen
await page.evaluate(() => {
  const name = [...document.querySelectorAll('span')].find(e => e.offsetParent && e.textContent?.trim() === 'Wocheneinkauf')
  name?.parentElement?.querySelector('button[aria-label="Mehr"]')?.click()
})
await pause(700)
await click('Teilen …')
await pause(800)
await click('Sam Berger (sam)', 'label')
await shot('a-teilen')
await click('Abbrechen', 'button')

await click('Packliste Urlaub')
await pause(800)
await shot('a-checkliste')

await click('Notizi-Aufgaben')
await pause(800)
await shot('a-notizi-aufgaben')

await click('Alle')
await pause(600)
await type('input[placeholder^="Aufgabe hinzufügen"]', 'Zahnarzt morgen Nachmittag')
await shot('a-aufgabe-text')

// ======================= Sicherheit =======================
await go('settings/devices')
await click('QR-Code erzeugen')
await pause(1500)
await shot('s-qr-dialog')
await click('Erledigt', 'button')

await go('settings/security')
await click('Einrichten')
await pause(1200)
await shot('s-2fa-einrichten')
await click('Abbrechen', 'button')

// ======================= Bedienung =======================
await prefs({ ribbonLabels: 'icons' })
await go('mail')
await click('Grillabend am Samstag?')
await pause(600)
await shot('b-menueband-symbole')
await prefs({ ribbonLabels: null })

await page.setViewport({ width: 430, height: 900, deviceScaleFactor: 1 })
await go('mail', 1500)
await shot('b-schmal-mail')
await click('Zurück zur Liste')
await pause(800)
await shot('b-schmal')
await page.setViewport(VIEW)

await prefs({ accent: '#0B8A96' })
await go('settings/appearance')
await shot('b-akzent-petrol')
await prefs({ accent: null })

await go('settings/install')
await shot('b-als-app')

// ======================= Dateien / Betrieb =======================
await go('files')
await click('Dokumente')
await click('Öffnen')
await pause(800)
await shot('d-dateien-hochladen')

await go('admin')
// Nicht den Pfad dieses Rechners zeigen - im Container ist es /data
await page.evaluate(() => {
  for (const el of document.querySelectorAll('span')) if (el.children.length === 0 && el.textContent?.startsWith('Datenverzeichnis:')) el.textContent = 'Datenverzeichnis: /data'
})
await shot('d-verwaltung-benutzer')

// ======================= Mail: Prio, Abmelden, VIP, Postausgang, Fenster, Suche, Aufraeumen =======================
// Rueckfragen (Verwerfen? Loeschen?) bestaetigen
page.on('dialog', d => d.accept().catch(() => {}))

/** Rechtsklick auf das sichtbare Element mit diesem Text (Kontextmenue). */
async function rclick(text) {
  const box = await page.evaluate(text => {
    const el = [...document.querySelectorAll('*')].find(e => e.offsetParent && e.children.length === 0 && e.textContent?.trim() === text)
    if (!el) return null
    el.scrollIntoView({ block: 'center' })
    const r = el.getBoundingClientRect()
    return { x: r.x + r.width / 2, y: r.y + r.height / 2 }
  }, text)
  if (!box) { console.warn('    nicht gefunden: ' + text); return false }
  await page.mouse.click(box.x, box.y, { button: 'right' })
  await pause(700)
  return true
}
/** Maus auf einen Menuepunkt (oeffnet Untermenues). */
async function hover(selector, text) {
  const box = await page.evaluate((selector, text) => {
    const el = [...document.querySelectorAll(selector)].find(e => e.offsetParent !== null && e.textContent?.trim().includes(text))
    if (!el) return null
    const r = el.getBoundingClientRect()
    return { x: r.x + r.width / 2, y: r.y + r.height / 2 }
  }, selector, text)
  if (!box) { console.warn('    nicht gefunden: ' + selector + ' ' + text); return false }
  await page.mouse.move(box.x, box.y)
  await pause(900)
  return true
}
/** Ohne die Maus nach unten links zu schieben (Untermenues bleiben offen). */
async function shotHere(name, target = page) {
  if (wanted && !wanted.has(name)) return
  await pause(400)
  await target.screenshot({ path: join(out, name + '.png') })
  console.log('  ' + name)
}
/** Element mit diesem Text in die Mitte scrollen. */
const scrollTo = text => page.evaluate(text => {
  const el = [...document.querySelectorAll('label,span,div')].find(e => e.offsetParent && e.children.length === 0 && e.textContent?.trim().startsWith(text))
  el?.scrollIntoView({ block: 'center' })
}, text)
/** Auswahlliste unter einer Feldbeschriftung oeffnen. */
async function openDrop(label) {
  const box = await page.evaluate(label => {
    const l = [...document.querySelectorAll('label')].find(e => e.offsetParent && e.textContent?.trim() === label)
    const dd = l?.parentElement?.querySelector('[role=combobox],button')
    if (!dd) return null
    dd.scrollIntoView({ block: 'center' })
    const r = dd.getBoundingClientRect()
    return { x: r.x + r.width / 2, y: r.y + r.height / 2 }
  }, label)
  if (!box) { console.warn('    nicht gefunden: Feld ' + label); return false }
  await page.mouse.click(box.x, box.y)
  await pause(700)
  return true
}
/** Neue Mail: Empfaenger aus den Kontakten, Betreff, Text vor der Signatur. */
async function newMail(to, subject, body) {
  await click('Neue E-Mail')
  await pause(900)
  await page.keyboard.type(to, { delay: 30 })
  await pause(900)
  await page.keyboard.press('Enter')
  await pause(300)
  await page.click('input[placeholder="Betreff"]')
  await page.keyboard.type(subject, { delay: 15 })
  await page.evaluate(() => {
    const el = document.querySelector('.dm-editor')
    el.focus()
    const r = document.createRange(); r.setStart(el, 0); r.collapse(true)
    const s = getSelection(); s.removeAllRanges(); s.addRange(r)
  })
  for (const line of body.split('\n')) { await page.keyboard.type(line, { delay: 8 }); await page.keyboard.press('Enter') }
  await pause(500)
}
const mailStatus = () => page.evaluate(() => window.dispatchEvent(new Event('dmoffice:mailstatus')))

// ---------- Prio-Posteingang ----------
await go('mail')
await mouse('[role=tab]', 'Relevant')
await click('Grillabend am Samstag?')
await pause(800)
await shot('m-prio-relevant')

await mouse('[role=tab]', 'Sonstige')
await pause(800)
await click('Kinoprogramm ab Donnerstag: Herbstfilmtage')
await pause(1000)
await shot('m-prio-sonstige')

// Kontextmenue: zurueck nach Relevant, nur diese Mail oder immer fuer den Absender
await rclick('Wochenrezepte: Kürbis in allen Varianten')
await hover('[role=menuitem]', 'Nach Relevant verschieben')
await shotHere('m-prio-verschieben')
await escape(); await escape()

// ---------- Newsletter abmelden ----------
await click('Herbstputz fürs Fahrrad: 5 Tipps und 15 % auf Pflegemittel')
await pause(1200)
await shot('m-abmelden')
// Kein echter Aufruf beim Anbieter: die Antwort des Servers ("One-Click erledigt") wird im Browser vorgegeben.
// Dafuer diese Seite ohne verschluesselten Umschlag laden (/api/e2e/key "gibt es nicht"), sonst sieht fetch nur Chiffrat;
// Puppeteer-Abfangen greift nicht, der Service Worker der PWA steht dazwischen.
const fake = await page.evaluateOnNewDocument(() => {
  sessionStorage.removeItem('dmoffice.e2e')
  const real = window.fetch
  window.fetch = (input, init) => {
    const url = String(input?.url ?? input)
    if (url.endsWith('/api/e2e/key')) return Promise.resolve(new Response('', { status: 404 }))
    if (url.includes('/unsubscribe')) return Promise.resolve(new Response(JSON.stringify({ done: true, how: 'oneclick' }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
    return real(input, init)
  }
})
await page.reload({ waitUntil: 'networkidle0' })
await pause(1200)
await mouse('[role=tab]', 'Sonstige')
await click('Herbstputz fürs Fahrrad: 5 Tipps und 15 % auf Pflegemittel')
await pause(1200)
await mouse('button', 'Vom Newsletter abmelden')
await pause(600)
await shot('m-abmelden-erledigt')
await page.removeScriptToEvaluateOnNewDocument(fake.identifier)
await noToasts()

// ---------- Konto: Prio-Posteingang, Papierkorb/Junk automatisch leeren ----------
await go('settings/accounts')
await mouse('button[aria-label="Konto bearbeiten"]')
await pause(800)
await scrollTo('Prio-Posteingang')
await shot('m-prio-einstellung')
await openDrop('Gelöschte Elemente automatisch leeren')
await mouse('[role=option]', '=nach 30 Tagen')
await openDrop('Junk-E-Mail automatisch leeren')
await shotHere('m-autoleeren')
await escape()
await click('Abbrechen', 'button')

// Dasselbe im Kontextmenue des Ordners (nur zeigen, nicht einstellen - ohne Mailserver)
await go('mail')
await rclick('Gelöschte Elemente')
await hover('[role=menuitem]', 'Automatisch leeren')
await shotHere('m-autoleeren-ordner')
await escape(); await escape()

// ---------- VIP-Kontakte ----------
await go('contacts')
await click('Laura Neumann')
await click('VIP', 'button')
await pause(1200)
await click('Jana Wolff')
await click('VIP', 'button')
await pause(1200)
await noToasts()
await shot('m-vip')
await click('Bearbeiten', 'button')
await pause(800)
await scrollTo('VIP – Benachrichtigungen')
await shot('m-vip-bearbeiten')
await click('Abbrechen', 'button')

// ---------- Eigenes Fenster, Senden mit Zurueckrufen, spaeter senden, Postausgang ----------
const popup = async open => {
  // Nur Seiten, nicht den Service Worker der PWA
  const target = new Promise(res => {
    const on = t => { if (t.type() === 'page') { browser.off('targetcreated', on); res(t.page()) } }
    browser.on('targetcreated', on)
  })
  await open()
  const p = await target
  await p.setViewport(VIEW)
  await p.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'light' }])
  await pause(2500)
  return p
}
// Verfassen in ein eigenes Fenster auskoppeln (Knopf links neben "Schliessen")
await go('mail')
await newMail('Jana', 'Salat für Samstag', 'Hallo Jana,\n\nich bringe Nudelsalat und frisches Brot mit. Bis Samstag!')
let win = await popup(() => page.evaluate(() => {
  document.querySelector('button[aria-label="Schließen"]')?.previousElementSibling?.click()
}))
await shotHere('m-eigenes-fenster', win)
// Das eigene Fenster verwerfen und im Hauptfenster senden: unten erscheint die Leiste zum Zurueckrufen
await win.close()
await pause(800)
await go('mail')
await newMail('Jana', 'Salat für Samstag', 'Hallo Jana,\n\nich bringe Nudelsalat und frisches Brot mit. Bis Samstag!')
await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent?.trim() === 'Senden')?.click())
await pause(1500)
await shot('m-zurueckrufen')
// Zurueckrufen: die Mail kommt wieder ins Verfassen - und geht dann geplant raus
await mouse('button', 'Zurückrufen')
await pause(1200)
await noToasts()
await mouse('button[aria-label="Später senden"]')
await pause(600)
await shotHere('m-spaeter-senden')
await click('Morgen')
await pause(1500)
await noToasts()

await newMail('Laura', 'Projektplan Q4 – meine Anmerkungen', 'Hallo Laura,\n\nich habe mir Abschnitt 3 angesehen. Die Meilensteine passen, nur der Test braucht eine Woche mehr.\n\nViele Grüße\nAlex')
await mouse('button[aria-label="Später senden"]')
await click('Morgen Nachmittag')
await pause(1500)
await noToasts()
await mailStatus()
await pause(1500)
await mouse('button', 'Postausgang')
await pause(1200)
await shot('m-postausgang')

// Mail per Doppelklick im eigenen Fenster
await go('mail')
await click('Einladung zur Jahreshauptversammlung')
await pause(1200)
win = await popup(() => page.evaluate(() => {
  const el = [...document.querySelectorAll('[role=option]')].find(e => e.textContent?.includes('Einladung zur Jahreshauptversammlung'))
  el?.dispatchEvent(new MouseEvent('dblclick', { bubbles: true }))
}))
await shotHere('m-mail-fenster', win)
await win.close()

// ---------- Suchtreffer sortieren ----------
await go('mail')
await type('input[placeholder="Suchen (von:, an:, betreff:)"]', 'rechnung')
await pause(1500)
await mouse('button', 'Neueste')
await shotHere('m-suche-sortiert')
await escape()

// ---------- Apps installieren ----------
await go('settings/install')
await scrollTo('Android-App (APK)')
await shot('b-apps-installieren')

// ---------- Leerer Posteingang (zuletzt: alle Mails ins Archiv) ----------
await go('mail')
await page.evaluate(async () => {
  const acc = (await (await fetch('/api/mail/accounts')).json())[0].id
  const r = await (await fetch(`/api/mail/${acc}/messages?folder=INBOX&size=500`)).json()
  await fetch(`/api/mail/${acc}/move`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ folder: 'INBOX', uids: r.items.map(i => i.uid), target: 'Archive' }) })
})
await go('mail')
// Immer derselbe Spruch: der erste
await page.evaluate(() => { Math.random = () => 0 })
await click('Verein')
await pause(800)
await click('Posteingang')
await pause(1200)
await shot('m-leer')

await browser.close()
