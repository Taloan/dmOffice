// Bilder fuer Blog-Artikel mit Edge/Chrome (puppeteer-core) - Ergebnis in docs/img/blog/.
// Aufruf: node blog-shots.mjs <basis-url> <passwort> <browser.exe> [nur,diese,bilder]
// Voraussetzung: seed.mjs und blog-seed.mjs sind gelaufen, Mails aus DocsSeed (DMOFFICE_DOCS_BLOG=1).
import puppeteer from 'puppeteer-core'
import { mkdirSync, writeFileSync } from 'node:fs'
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

if (!process.env.NEW_ONLY) {
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
}
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
const need = (...names) => !wanted || names.some(n => wanted.has(n))
// NEW_ONLY=1: nur die Bilder des Essensplans/Teilens (ohne die uebrigen Mail-/Kalender-/Datei-Schritte davor)
if (!process.env.NEW_ONLY) {

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

// ---------- Speichermodus "Schlank" ----------
// Einstellung zeigen, dann mit 7 Tagen speichern: der Aufraeumer verwirft aeltere Inhalte gleich nach dem Umstellen
await go('settings/accounts')
await mouse('button[aria-label="Konto bearbeiten"]')
await pause(800)
await scrollTo('Speichermodus')
await openDrop('Speichermodus')
await mouse('[role=option]', '=Schlank')
await pause(500)
await openDrop('Inhalte behalten')
await mouse('[role=option]', '=7 Tage')
await pause(400)
await blur()
await scrollTo('Speichermodus')
await shot('m-schlank-einstellung')
await click('Speichern', 'button')
await pause(6000)
await noToasts()
await go('settings/stats', 2000)
await shot('m-schlank-speicher')

// ---------- Konto hinzufuegen: Assistent in zwei Schritten ----------
// Schritt 2 nur mit Anbietern aus der eingebauten Liste (MailDiscovery.Known): keine Abfrage im Netz.
// Nichts wird abgeschickt - kein Passwort, kein Klick auf "Einrichten" oder "Mit Microsoft anmelden".
async function wizard(email, next = true) {
  await go('settings/accounts')
  await click('Konto hinzufügen', 'button')
  await pause(600)
  await type('input[type=email]', email)
  if (next) {
    await click('Weiter', 'button')
    await pause(1500)
    const id = await page.evaluate(() => [...document.querySelectorAll('label')].find(l => l.textContent?.trim() === 'Absendername')?.htmlFor)
    if (id) await type('#' + id.replace(/:/g, '\\:'), 'Alex Berger')
    else console.warn('    nicht gefunden: Absendername')
  }
  await blur()
}
await wizard('alex.berger@example.org', false)
await shot('s-konto-assistent-1')
await wizard('alex.berger@posteo.de')
await shot('s-konto-assistent-2')
await wizard('alex.berger@gmail.com')
await shot('s-konto-assistent-app-passwort')
await wizard('alex.berger@outlook.com')
await shot('s-konto-assistent-microsoft')

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

// ---------- notizi: Einrichtung unter Einstellungen -> Notizen ----------
await go('settings/notes', 1500)
await shot('n-notizi-einstellungen')

// ======================= Dateiablage, Suche, Office, Nextcloud, notizi, Junk =======================
// Eigene Beispieldateien: Fotos und Skizzen malt der Browser (Canvas, JPEG/PNG), PDFs druckt er (page.pdf) - mit
// echtem Text, damit Vorschaubilder und Inhaltssuche etwas zu zeigen haben. Hochgeladen ueber WebDAV mit X-OC-Mtime,
// damit die Gruppen nach Aenderungsdatum (Heute, Gestern, Letzte Woche ...) gefuellt sind.

let nodeCookie = ''
async function call(method, path, body) {
  const r = await fetch(base + path, {
    method, headers: { cookie: nodeCookie, ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
    body: body !== undefined ? JSON.stringify(body) : undefined,
  })
  const set = r.headers.get('set-cookie')
  if (set) nodeCookie = set.split(';')[0]
  if (!r.ok) throw new Error(`${method} ${path}: ${r.status} ${await r.text()}`)
  const text = await r.text()
  return text ? JSON.parse(text) : null
}
await call('POST', '/api/auth/login', { login: 'alex', password })
const davAuth = 'Basic ' + Buffer.from('alex:' + (await call('POST', '/api/settings/app-passwords', { name: 'Laptop' })).password).toString('base64')
const dav = async (method, path, data, daysAgo = 0) => {
  const headers = { Authorization: davAuth }
  if (data) headers['X-OC-Mtime'] = String(Math.floor((Date.now() - daysAgo * 86400000 - 3600000) / 1000))
  const r = await fetch(base + '/remote.php/dav/files/alex/' + path.split('/').map(encodeURIComponent).join('/'), { method, headers, body: data })
  if (!r.ok && !(method === 'MKCOL' && r.status === 405)) throw new Error(`${method} ${path}: ${r.status}`)
}

/** Bild im Browser malen: einfache Landschaften und Skizzen, als JPEG oder PNG. */
const paint = (scene, type = 'image/jpeg') => page.evaluate(async (scene, type) => {
  const c = document.createElement('canvas'); c.width = 1200; c.height = 900
  const g = c.getContext('2d'), W = c.width, H = c.height
  const grad = (y0, y1, stops) => { const l = g.createLinearGradient(0, y0, 0, y1); stops.forEach(([o, col]) => l.addColorStop(o, col)); return l }
  const rect = (x, y, w, h, col) => { g.fillStyle = col; g.fillRect(x, y, w, h) }
  const circle = (x, y, r, col) => { g.fillStyle = col; g.beginPath(); g.arc(x, y, r, 0, Math.PI * 2); g.fill() }
  const poly = (pts, col) => { g.fillStyle = col; g.beginPath(); pts.forEach(([x, y], i) => i ? g.lineTo(x, y) : g.moveTo(x, y)); g.closePath(); g.fill() }
  const hills = (base, amp, col, seed) => { const pts = [[0, H]]; for (let x = 0; x <= W; x += 20) pts.push([x, base - amp * (Math.sin(x / 140 + seed) * 0.6 + Math.sin(x / 57 + seed * 2) * 0.3 + Math.sin(x / 23 + seed) * 0.1)]); pts.push([W, H]); poly(pts, col) }
  const waves = (y0, col) => { g.strokeStyle = col; g.lineWidth = 2; for (let y = y0; y < H; y += 26) { g.beginPath(); for (let x = 0; x <= W; x += 10) g.lineTo(x, y + Math.sin(x / 30 + y) * 3); g.stroke() } }
  const house = (x, w, h, col, roof) => { rect(x, H * 0.75 - h, w, h, col); poly([[x - 6, H * 0.75 - h], [x + w / 2, H * 0.75 - h - w * 0.35], [x + w + 6, H * 0.75 - h]], roof)
    for (let wy = H * 0.75 - h + 20; wy < H * 0.75 - 40; wy += 46) for (let wx = x + 12; wx < x + w - 24; wx += 34) rect(wx, wy, 16, 24, '#3b4a5a') }
  switch (scene) {
    case 'strand':
      rect(0, 0, W, H, grad(0, H * 0.55, [[0, '#4f9fe0'], [1, '#bfe3ff']])); circle(930, 170, 70, '#fff6c8')
      rect(0, H * 0.5, W, H * 0.25, grad(H * 0.5, H * 0.75, [[0, '#1f7fb8'], [1, '#49b3d6']])); waves(H * 0.52, 'rgba(255,255,255,.25)')
      poly([[0, H * 0.72], [W, H * 0.66], [W, H], [0, H]], '#ecd29a'); poly([[0, H * 0.82], [W, H * 0.78], [W, H], [0, H]], '#e2c283')
      circle(260, 690, 60, '#e85d4a'); rect(256, 690, 8, 140, '#7a5230'); break
    case 'altstadt':
      rect(0, 0, W, H, grad(0, H, [[0, '#7cc0f0'], [1, '#d9efff']]))
      ;[['#f2c14e', '#b5482f'], ['#e57a5a', '#9d3a26'], ['#f4e3c1', '#b5482f'], ['#7fb3c8', '#8a3a28'], ['#f2a65a', '#a1412c'], ['#d9d4c7', '#b5482f']]
        .forEach(([col, roof], i) => house(30 + i * 195, 170, 260 + (i % 3) * 70, col, roof))
      rect(0, H * 0.75, W, H * 0.25, '#b9b2a5'); for (let x = 0; x < W; x += 60) rect(x, H * 0.75 + ((x / 60) % 2) * 30, 58, 28, '#a9a296'); break
    case 'sonnenuntergang':
      rect(0, 0, W, H, grad(0, H * 0.6, [[0, '#2b2d6e'], [0.5, '#d9576b'], [1, '#ffb25b']])); circle(600, 520, 110, '#ffd27a')
      rect(0, H * 0.6, W, H * 0.4, grad(H * 0.6, H, [[0, '#7a3d63'], [1, '#2a1d3d']])); waves(H * 0.62, 'rgba(255,200,120,.25)')
      hills(H * 0.62, 50, '#241a33', 1.3); break
    case 'leuchtturm':
      rect(0, 0, W, H, grad(0, H * 0.6, [[0, '#5aa7e6'], [1, '#d5ecff']]))
      rect(0, H * 0.58, W, H * 0.42, grad(H * 0.58, H, [[0, '#2a7db5'], [1, '#155a87']])); waves(H * 0.6, 'rgba(255,255,255,.2)')
      poly([[600, H * 0.75], [1200, H * 0.55], [1200, H], [520, H]], '#6b6256')
      for (let i = 0; i < 6; i++) rect(820, 230 + i * 55, 90, 55, i % 2 ? '#ffffff' : '#d23a2f')
      rect(805, 200, 120, 34, '#333'); circle(865, 190, 26, '#ffe680'); break
    case 'berge':
      rect(0, 0, W, H, grad(0, H, [[0, '#8fc7f2'], [1, '#e8f4ff']])); circle(250, 160, 55, '#fffbe0')
      poly([[0, 600], [260, 230], [470, 520], [700, 180], [980, 560], [1200, 300], [1200, H], [0, H]], '#7c8ea6')
      poly([[230, 270], [260, 230], [295, 280], [270, 300]], '#fff'); poly([[660, 240], [700, 180], [745, 250], [705, 270]], '#fff')
      hills(H * 0.72, 60, '#4f8a4b', 0.4); hills(H * 0.85, 40, '#3d7140', 2.2); break
    case 'hafen':
      rect(0, 0, W, H, grad(0, H * 0.5, [[0, '#9cc9ec'], [1, '#e9f4fb']]))
      ;['#e8b04a', '#d06a4f', '#f1e1c6', '#9fbfcf', '#e48d5f'].forEach((col, i) => house(80 + i * 220, 180, 200 + (i % 2) * 80, col, '#a4462f'))
      rect(0, H * 0.75, W, H * 0.25, grad(H * 0.75, H, [[0, '#2e6f8f'], [1, '#1c4a63']])); waves(H * 0.77, 'rgba(255,255,255,.2)')
      ;[[200, '#c0392b'], [560, '#2c6fb3'], [880, '#e3a32b']].forEach(([x, col]) => { poly([[x, 740], [x + 220, 740], [x + 190, 790], [x + 30, 790]], col); rect(x + 105, 620, 6, 120, '#5a4630'); poly([[x + 111, 625], [x + 111, 730], [x + 190, 730]], '#f7f2e8') }); break
    case 'fundament':
      rect(0, 0, W, H, grad(0, H * 0.45, [[0, '#a9cfee'], [1, '#e6f1fa']])); hills(H * 0.45, 30, '#6f9a5b', 0.8)
      rect(0, H * 0.45, W, H * 0.55, '#8a6e4b'); poly([[250, 600], [950, 600], [1050, 780], [150, 780]], '#b9b6ae')
      for (let x = 260; x < 950; x += 70) rect(x, 640, 40, 110, '#9a978f'); rect(120, 380, 30, 260, '#c8a24a'); rect(80, 380, 300, 18, '#c8a24a'); break
    case 'rohbau':
    case 'gartenhaus':
      rect(0, 0, W, H, grad(0, H * 0.5, [[0, '#86c1ef'], [1, '#e3f1fc']])); hills(H * 0.5, 30, '#7fae64', 1.5)
      rect(0, H * 0.62, W, H * 0.38, '#6ea055')
      rect(360, 360, 480, 300, scene === 'rohbau' ? '#d7b98a' : '#9c6b3f')
      if (scene === 'rohbau') { g.strokeStyle = '#a8875a'; g.lineWidth = 8; for (let x = 360; x <= 840; x += 60) { g.beginPath(); g.moveTo(x, 360); g.lineTo(x, 660); g.stroke() } poly([[330, 360], [600, 220], [870, 360]], 'rgba(120,90,60,.55)') }
      else { for (let x = 360; x < 840; x += 24) rect(x, 360, 3, 300, '#865a33'); poly([[330, 360], [600, 220], [870, 360]], '#4a4f57'); rect(560, 500, 80, 160, '#5a3b22'); rect(410, 430, 90, 70, '#cfe7f7'); rect(700, 430, 90, 70, '#cfe7f7'); circle(200, 600, 60, '#4f8f45'); circle(1010, 610, 70, '#4f8f45') }
      break
    case 'skizze':
      rect(0, 0, W, H, '#fbfaf5'); g.strokeStyle = '#d8e6f3'; g.lineWidth = 1
      for (let x = 0; x < W; x += 30) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x, H); g.stroke() }
      for (let y = 0; y < H; y += 30) { g.beginPath(); g.moveTo(0, y); g.lineTo(W, y); g.stroke() }
      g.strokeStyle = '#2b3a55'; g.lineWidth = 4; g.strokeRect(300, 380, 600, 330); g.beginPath(); g.moveTo(270, 380); g.lineTo(600, 190); g.lineTo(930, 380); g.stroke()
      g.strokeRect(540, 520, 120, 190); g.strokeRect(360, 450, 120, 90); g.strokeRect(720, 450, 120, 90)
      g.lineWidth = 2; g.beginPath(); g.moveTo(300, 760); g.lineTo(900, 760); g.stroke()
      g.fillStyle = '#2b3a55'; g.font = '32px "Segoe Print", "Comic Sans MS", cursive'; g.fillText('3,00 m', 555, 800); g.fillText('Gartenhaus – Ansicht Süd', 340, 120)
      g.fillText('Lärchenholz', 930, 560); break
  }
  return c.toDataURL(type, 0.9).split(',')[1]
}, scene, type).then(b64 => Buffer.from(b64, 'base64'))

/** PDF mit echtem Text (Chrome druckt eine kleine HTML-Seite). */
async function pdf(title, from, rows, note, color = "#2e6b30") {
  const p = await browser.newPage()
  await p.setContent(`<html><body style="font-family:Segoe UI,Arial;margin:0;color:#222">
    <div style="background:${color};color:#fff;padding:36px 48px 30px"><div style="font-size:30px;font-weight:700">${from.split('<br>')[0]}</div>
    <div style="font-size:14px;opacity:.85;margin-top:6px">${from.split('<br>').slice(1).join(' · ')}</div></div><div style="margin:0 48px">
    <h1 style="font-size:28px;margin:36px 0 6px">${title}</h1><div style="color:#666;font-size:13px">Musterstadt, ${new Date().toLocaleDateString('de-DE')}</div>
    <table style="width:100%;border-collapse:collapse;margin-top:28px;font-size:14px">
    <tr style="background:#eef3ee"><th style="text-align:left;padding:8px">Position</th><th style="text-align:right;padding:8px">Menge</th><th style="text-align:right;padding:8px">Betrag</th></tr>
    ${rows.map(([a, b, c]) => `<tr><td style="padding:8px;border-bottom:1px solid #ddd">${a}</td><td style="padding:8px;border-bottom:1px solid #ddd;text-align:right">${b}</td><td style="padding:8px;border-bottom:1px solid #ddd;text-align:right">${c}</td></tr>`).join('')}
    </table><p style="margin-top:28px;font-size:14px;line-height:1.5">${note}</p></div></body></html>`)
  const data = await p.pdf({ format: 'A4', landscape: true, printBackground: true })
  await p.close()
  return Buffer.from(data)
}

/** Warten, bis alle Vorschaubilder da sind (sie laden "lazy"; ein kleiner Bildlauf stoesst sie an). */
const thumbs = async () => {
  for (let i = 0; i < 20; i++) {
    const open = await page.evaluate(() => [...document.querySelectorAll('img')].filter(i => !i.complete || i.style.opacity === '0.01').length)
    if (!open) break
    await page.mouse.wheel({ deltaY: 1 }); await pause(500); await page.mouse.wheel({ deltaY: -1 }); await pause(500)
  }
  await pause(500)
}

if (need('f-kacheln', 'f-liste-gruppiert', 'f-betrachter', 'f-kontextmenue', 'f-papierkorb', 'f-versionen', 'f-freigabe', 'f-freigabe-empfaenger', 's-suche-ocr', 's-suche-treffer')) {
  const G = 'Projekt Gartenhaus', U = 'Fotos/Urlaub Portugal'
  for (const d of [G, U, 'Alte Entwürfe']) await dav('MKCOL', d)
  const holz = await pdf('Angebot Nr. 2026-118 – Gartenhaus aus Lärchenholz', 'Holzbau Kern<br>Am Sägewerk 4<br>12345 Musterstadt', [
    ['Gartenhaus 3,00 × 2,50 m, Lärchenholz, 28 mm Wandstärke', '1', '3.480,00 €'], ['Dachpappe besandet, 2 Rollen', '2', '78,00 €'],
    ['Fundament: Punktfundamente mit Betonsteinen', '9', '315,00 €'], ['Montage vor Ort', '1', '640,00 €']],
    'Das Angebot gilt 30 Tage. Lärchenholz ist unbehandelt witterungsbeständig; eine Lasur ist nicht nötig.', '#8a5a2b')
  const plan = await pdf('Bauplan Gartenhaus – Grundriss und Ansichten', 'Planungsbüro Winkel<br>Lindenweg 12<br>12345 Musterstadt', [
    ['Grundfläche 3,00 × 2,50 m (7,5 m²)', '', ''], ['Traufhöhe 2,05 m, Firsthöhe 2,60 m', '', ''], ['Tür 0,90 × 1,90 m, zwei Fenster 0,70 × 0,60 m', '', '']],
    'Wände und Boden aus Lärchenholz; Dach als Satteldach mit Dachpappe. Abstand zur Grundstücksgrenze mindestens 0,50 m.', '#1f5f8b')
  const material = await pdf('Materialliste Gartenhaus', 'Alex Berger<br>Gartenstraße 7<br>12345 Musterstadt', [
    ['Lärchenholz-Bohlen 28 × 140 mm', '64', '1.152,00 €'], ['Dachpappe besandet', '2', '78,00 €'], ['Edelstahlschrauben 5 × 60 (500 Stück)', '1', '42,90 €'], ['Betonsteine 40 × 40 cm', '9', '67,50 €']],
    'Abholung im Baumarkt am Samstag. Lärchenholz vorher zwei Wochen trocken lagern.', '#6b4c9a')
  const statik = await pdf('Nachweis Schneelast – Gartenhaus', 'Ingenieurbüro Stabil<br>Brückenstraße 9<br>12345 Musterstadt', [
    ['Schneelastzone 2, Schneelast 0,85 kN/m²', '', ''], ['Sparren 60 × 100 mm, Abstand 62,5 cm', '', 'ausreichend']], 'Der Nachweis gilt für das Satteldach mit Dachpappe auf 24-mm-Schalung.', '#b5482f')
  const files = [
    [G + '/Skizze Gartenhaus.png', await paint('skizze', 'image/png'), 24], [G + '/Fundament.jpg', await paint('fundament'), 12],
    [G + '/Rohbau.jpg', await paint('rohbau'), 5], [G + '/Ansicht Süd.jpg', await paint('gartenhaus'), 1],
    [G + '/Angebot Holzbau Kern.pdf', holz, 25], [G + '/Bauplan Gartenhaus.pdf', plan, 11], [G + '/Materialliste.pdf', material, 0],
    [G + '/Nachweis Schneelast.pdf', statik, 40], [G + '/Kosten Gartenhaus.xlsx', Buffer.from('Beispiel '.repeat(2400)), 4],
    [U + '/Strand bei Lagos.jpg', await paint('strand'), 30], [U + '/Altstadt Lissabon.jpg', await paint('altstadt'), 30],
    [U + '/Sonnenuntergang Algarve.jpg', await paint('sonnenuntergang'), 29], [U + '/Leuchtturm Cabo.jpg', await paint('leuchtturm'), 29],
    [U + '/Serra da Estrela.jpg', await paint('berge'), 28], [U + '/Hafen Porto.jpg', await paint('hafen'), 27],
    // fuer den Papierkorb
    [U + '/Unscharf.jpg', await paint('strand'), 30], [G + '/Skizze alt.png', await paint('skizze', 'image/png'), 30],
    ['Alte Entwürfe/Gartenhaus Variante A.pdf', plan, 60], ['Dokumente/Kfz-Versicherung 2024.pdf', holz, 300],
  ]
  for (const [path, data, days] of files) await dav('PUT', path, data, days)
  // Notizen mit frueheren Versionen (jedes Speichern mit anderem Inhalt legt eine an)
  const notes = ['Montag: Fundament ausheben', 'Dienstag: Betonsteine setzen', 'Donnerstag: Bodenrahmen aus Lärchenholz', 'Samstag: Wände stellen, Dachpappe besorgen']
  for (let i = 1; i <= notes.length; i++) {
    await call('POST', '/api/files/save', { path: G + '/Notizen Baustelle.txt', text: 'Baustelle Gartenhaus\n\n' + notes.slice(0, i).map(n => '– ' + n).join('\n') + '\n', create: i === 1 })
    await pause(1100)
  }
  // Papierkorb: drei Sachen geloescht
  await call('POST', '/api/files/delete', { paths: [U + '/Unscharf.jpg', G + '/Skizze alt.png'] })
  await call('POST', '/api/files/delete', { paths: ['Alte Entwürfe'] })
  await call('POST', '/api/files/delete', { paths: ['Dokumente/Kfz-Versicherung 2024.pdf'] })
  // Link fuer die Urlaubsfotos (Empfaengeransicht); die Inhaltssuche einschalten (ohne OCR - die gibt es hier nicht)
  const until = new Date(Date.now() + 14 * 86400000); until.setHours(23, 59, 59, 0)
  const link = await call('POST', '/api/files/links', { path: U, mode: 'read', expires: until.toISOString(), password: null, maxDownloads: 10 })
  await call('PUT', '/api/settings/textindex', { enabled: true, ocr: false })

  // Ohne Browser-Cache: kommt ein Vorschaubild aus dem Cache, bleibt in der Oberflaeche sonst das Symbol stehen (onLoad kommt nicht an)
  await page.setCacheEnabled(false)
  // ---------- Kacheln mit Vorschaubildern ----------
  await prefs({ fileView: 'tiles', fileTile: 'large', fileGroup: 'none', fileDetails: false, ribbonTabs: { files: 'view' } })
  await go('files/' + encodeURIComponent(G), 2500)
  await thumbs()
  await shot('f-kacheln')

  // ---------- Kontextmenue mit Mehrfachauswahl ----------
  await prefs({ fileView: 'tiles', fileGroup: 'none', fileSort: 'name', fileSortDesc: false, ribbonTabs: { files: 'start' } })
  await go('files/' + encodeURIComponent(G), 2500)
  const tileAt = p => page.evaluate(p => {
    const el = document.querySelector(`[data-path="${CSS.escape(p)}"]`)
    if (!el) return null
    const r = el.getBoundingClientRect()
    return { x: r.x + r.width / 2, y: r.y + r.height / 2 }
  }, p)
  const pick = async (p, opts = {}) => {
    const b = await tileAt(p)
    if (!b) { console.warn('    nicht gefunden: ' + p); return }
    if (opts.ctrl) await page.keyboard.down('Control')
    await page.mouse.click(b.x, b.y, { button: opts.right ? 'right' : 'left' })
    if (opts.ctrl) await page.keyboard.up('Control')
    await pause(500)
  }
  await thumbs()
  await pick(G + '/Fundament.jpg')
  await pick(G + '/Rohbau.jpg', { ctrl: true })
  await pick(G + '/Ansicht Süd.jpg', { ctrl: true })
  await pick(G + '/Ansicht Süd.jpg', { right: true })
  await pause(600)
  await shotHere('f-kontextmenue')
  await escape()

  // ---------- Liste, gruppiert nach Aenderungsdatum ----------
  await prefs({ fileView: 'details', fileGroup: 'date', fileSort: 'modified', fileSortDesc: true })
  await go('files/' + encodeURIComponent(G), 2000)
  await thumbs()
  await shot('f-liste-gruppiert')
  await prefs({ fileGroup: 'none', fileSort: 'name', fileSortDesc: false })

  // ---------- Betrachter ----------
  await go('files/' + U.split('/').map(encodeURIComponent).join('/'), 2500)
  await thumbs()
  const lagos = await tileAt(U + '/Leuchtturm Cabo.jpg')
  if (lagos) await page.mouse.click(lagos.x, lagos.y, { clickCount: 2 })
  await pause(2500)
  await shot('f-betrachter')
  await escape()

  // ---------- Fruehere Versionen im Detailbereich ----------
  await prefs({ fileView: 'details', fileDetails: true })
  await go('files/' + encodeURIComponent(G), 2000)
  await click('Notizen Baustelle.txt')
  await pause(1500)
  await shot('f-versionen')
  await prefs({ fileDetails: false })

  // ---------- Papierkorb ----------
  await go('files/:trash', 2000)
  // Einen Eintrag auswaehlen: dann sind "Wiederherstellen" und "Endgueltig loeschen" aktiv
  await page.evaluate(() => [...document.querySelectorAll('tr')].find(r => r.textContent?.includes('Alte Entwürfe'))?.querySelector('input[type=checkbox]')?.click())
  await pause(600)
  await shot('f-papierkorb')

  // ---------- Teilen-Dialog: Ablauf, Passwort, Abrufe ----------
  await prefs({ fileView: 'tiles' })
  await go('files/Fotos', 2000)
  await rclick('Urlaub Portugal')
  await click('Teilen …')
  await pause(1200)
  await page.evaluate(v => {
    const el = [...document.querySelectorAll('[role=dialog] input[type=date]')].find(e => e.offsetParent)
    Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v)
    el.dispatchEvent(new Event('input', { bubbles: true }))
  }, new Date(Date.now() + 30 * 86400000).toISOString().slice(0, 10))
  await click('Mit Passwort schützen', 'label')
  await pause(500)
  await page.keyboard.type('Sommer-2026', { delay: 20 })
  await blur()
  // Die Adresse wie hinter dem Reverse-Proxy zeigen (der Link nimmt location.origin)
  await page.evaluate(o => { for (const i of document.querySelectorAll('[role=dialog] input')) if (i.value.startsWith(o)) i.value = i.value.replace(o, 'https://office.beispiel.de') }, base)
  await shot('f-freigabe')
  await escape()

  // ---------- Was der Empfaenger sieht: eigenes, leeres Browserprofil ----------
  if (need('f-freigabe-empfaenger')) {
    const guest = await browser.createBrowserContext()
    const gp = await guest.newPage()
    await gp.setViewport({ width: 1100, height: 340, deviceScaleFactor: 1 })
    await gp.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'light' }])
    await gp.goto(base + '/s/' + link.token, { waitUntil: 'networkidle0' })
    await pause(1200)
    await gp.evaluate(() => document.querySelector('button[aria-label="Kacheln"]')?.click())
    await pause(2500)
    await gp.mouse.move(5, 335)
    await shotHere('f-freigabe-empfaenger', gp)
    await guest.close()
  }

  // ---------- Inhaltssuche: Treffer im Text eines PDFs ----------
  for (let i = 0; i < 60; i++) {
    const st = await call('GET', '/api/settings/textindex')
    if (st.pending === 0 && st.done > 0) break
    await pause(1000)
  }
  await go('files/' + encodeURIComponent(G), 2000)
  await click('Inhalte durchsuchen', 'button')
  await pause(800)
  await page.keyboard.type('Dachpappe', { delay: 30 })
  await pause(2000)
  await shot('s-suche-treffer')
  await escape()

  // ---------- Einstellungen -> Suchen ----------
  // Wie im Docker-Image (mit Tesseract): Stand vom Server im Browser vorgegeben, nichts wird umgestellt.
  // (Ohne Tesseract - etwa lokal unter Windows - ist der OCR-Schalter ausgegraut.)
  await go('settings/search', 1500)
  const fakeOcr = await page.evaluateOnNewDocument(() => {
    sessionStorage.removeItem('dmoffice.e2e')
    const real = window.fetch
    window.fetch = (input, init) => {
      const url = String(input?.url ?? input)
      if (url.endsWith('/api/e2e/key')) return Promise.resolve(new Response('', { status: 404 }))
      if (url.endsWith('/api/settings/textindex')) return Promise.resolve(new Response(JSON.stringify({
        enabled: true, ocr: true, ocrAvailable: true, ocrReason: null, ocrLanguages: 'deu+eng', done: 1284, withText: 1137, byOcr: 212,
        pending: 0, errors: 0, skipped: 0, lastRun: new Date(Date.now() - 20 * 60000).toISOString(), recentErrors: [],
      }), { status: 200, headers: { 'Content-Type': 'application/json' } }))
      return real(input, init)
    }
  })
  await page.reload({ waitUntil: 'networkidle0' })
  await pause(1500)
  await scrollTo('Inhalte von Anhängen und Dateien')
  await shot('s-suche-ocr')
  await page.removeScriptToEvaluateOnNewDocument(fakeOcr.identifier)
  await prefs({ fileView: 'details', ribbonTabs: null })
  await page.setCacheEnabled(true)
}

// ---------- Office im Browser: Verwaltung (nur eingetragen, nicht gespeichert) ----------
if (need('o-office-einstellungen')) {
  await go('admin', 1500)
  await scrollTo('Office im Browser')
  const field = label => page.evaluate(label => {
    const l = [...document.querySelectorAll('label')].find(e => e.offsetParent && e.textContent?.trim().startsWith(label))
    const id = l?.htmlFor
    return id ? '#' + CSS.escape(id) : null
  }, label)
  const urlField = await field('Adresse des Office-Servers')
  if (urlField) await type(urlField, 'https://docs.beispiel.de'); else console.warn('    nicht gefunden: Adresse des Office-Servers')
  const jwtField = await field('JWT-Geheimnis')
  if (jwtField) await type(jwtField, 'beispiel-geheimnis-1234567890')
  await blur()
  // Nicht die Adresse und den Pfad dieses Rechners zeigen
  await page.evaluate(o => {
    for (const i of document.querySelectorAll('input')) if (i.placeholder?.startsWith(o)) i.placeholder = 'https://office.beispiel.de'
    for (const el of document.querySelectorAll('span')) if (el.children.length === 0 && el.textContent?.startsWith('Datenverzeichnis:')) el.textContent = 'Datenverzeichnis: /data'
  }, base)
  // Nur der Abschnitt "Office im Browser" (oben stehen die Benutzer)
  const top = await page.evaluate(() => {
    const h = [...document.querySelectorAll('span,div,h2,h3')].find(e => e.offsetParent && e.children.length === 0 && e.textContent?.trim() === 'Office im Browser')
    h?.scrollIntoView({ block: 'start' })
    return h ? h.getBoundingClientRect().y : null
  })
  if (need('o-office-einstellungen')) {
    await page.mouse.move(5, VIEW.height - 5)
    const y = Math.max(0, (top ?? 400) - 24)
    await page.screenshot({ path: join(out, 'o-office-einstellungen.png'), clip: { x: 56, y, width: 820, height: VIEW.height - y } })
    console.log('  o-office-einstellungen')
  }
}

// ---------- Nextcloud-Client: Anmeldung im Browser (Login Flow v2) ----------
if (need('nc-einrichtung', 'nc-geraete')) {
  await go('settings/devices', 1500)
  await shot('nc-geraete')
  const r = await fetch(base + '/index.php/login/v2', { method: 'POST', headers: { 'User-Agent': 'Mozilla/5.0 (Windows) mirall/3.16.0 (Nextcloud, windows-10.0.26100 ClientArchitecture: x86_64 OsArchitecture: x86_64)' } })
  const flow = await r.json()
  await page.setViewport({ width: 760, height: 420, deviceScaleFactor: 1 })
  await page.goto(base + new URL(flow.login).pathname, { waitUntil: 'networkidle0' })
  await pause(600)
  await shot('nc-einrichtung')
  await page.setViewport(VIEW)
}

// ---------- notizi verbinden: eigener Zugang nur fuer den Notizen-Ordner ----------
if (need('n-notizi-verbinden')) {
  await go('settings/notes', 1500)
  await click('notizi verbinden', 'button')
  await pause(1500)
  await scrollTo('2. In notizi mit dmOffice verbinden')
  await shot('n-notizi-verbinden')
}

// ---------- Mail: Junk gesperrt, Kontextmenue mit Mehrfachauswahl ----------
if (need('m-junk-blockiert', 'k-kontextmenue-mail')) {
  // Der Posteingang wurde oben fuers leere Bild ins Archiv geraeumt - zurueckholen
  await page.evaluate(async () => {
    const acc = (await (await fetch('/api/mail/accounts')).json())[0].id
    const r = await (await fetch(`/api/mail/${acc}/messages?folder=Archive&size=500`)).json()
    if (r.items.length) await fetch(`/api/mail/${acc}/move`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ folder: 'Archive', uids: r.items.map(i => i.uid), target: 'INBOX' }) })
  })
  await go('mail', 1500)
  await click('Junk-E-Mail')
  await pause(1200)
  await click('Herzlichen Glückwunsch! Ihr Gutschein über 500 € wartet')
  await pause(1500)
  await shot('m-junk-blockiert')

  await click('Posteingang')
  await pause(1200)
  await mouse('[role=tab]', 'Relevant')
  // Drei sichtbare Mails nacheinander (ohne zu scrollen), Rechtsklick am rechten Rand der Liste: das Menue liegt dann ueber dem Lesebereich
  const rows = await page.evaluate(() => [...document.querySelectorAll('[role=option]')].filter(e => e.offsetParent).slice(0, 4).map(e => {
    const r = e.getBoundingClientRect(); return { x: r.x + 60, y: r.y + r.height / 2, right: r.x + r.width - 30 }
  }))
  if (rows.length < 4) console.warn('    zu wenige Mails im Posteingang')
  else {
    await page.mouse.click(rows[1].x, rows[1].y); await pause(400)
    await page.keyboard.down('Control')
    await page.mouse.click(rows[2].x, rows[2].y); await pause(300)
    await page.mouse.click(rows[3].x, rows[3].y); await pause(300)
    await page.keyboard.up('Control')
    await page.mouse.click(rows[1].right, rows[1].y, { button: 'right' })
  }
  await pause(600)
  await shotHere('k-kontextmenue-mail')
  await escape(); await escape()
}

}

// ======================= Essensplan, geteilter Kalender, Teilnehmer, Notiz-Termin, Vorschaubild =======================
// Alle Beispieldaten dieser Bilder entstehen hier (nach den anderen Bildern), damit deren Listen unveraendert bleiben.
const ALL_PLAN = ['e-plan-woche', 'e-plan-eintrag', 'e-plan-zutaten', 'e-plan-zutaten-dialog', 'e-rezept-notiz', 'k-geteilt', 'k-teilnehmer', 'k-einladung-mail', 'n-notiz-termin', 'n-notiz-termin-dialog', 'n-vorschaubild', 'n-teilen-dialog', 'n-geteilt-empfaenger', 'n-verschieben']
if (need(...ALL_PLAN)) {
  const zlib = await import('node:zlib')
  const crcTable = Array.from({ length: 256 }, (_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320 ^ (c >>> 1) : c >>> 1; return c >>> 0 })
  const crc = buf => { let c = 0xFFFFFFFF; for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8); return (c ^ 0xFFFFFFFF) >>> 0 }
  const chunk = (type, data) => {
    const len = Buffer.alloc(4); len.writeUInt32BE(data.length)
    const body = Buffer.concat([Buffer.from(type), data])
    const c = Buffer.alloc(4); c.writeUInt32BE(crc(body))
    return Buffer.concat([len, body, c])
  }
  /** Erfundenes "Foto": Himmelsverlauf, Sonne und zwei Huegelketten (PNG, ohne Bibliothek). */
  const scenery = (w, h, sky1, sky2, hill1, hill2, sun) => {
    const raw = Buffer.alloc((w * 3 + 1) * h)
    const mix = (a, b, t) => a.map((x, i) => Math.round(x + (b[i] - x) * t))
    for (let y = 0; y < h; y++) {
      raw[y * (w * 3 + 1)] = 0
      for (let x = 0; x < w; x++) {
        let px = mix(sky1, sky2, y / h)
        const d = Math.hypot(x - w * 0.72, y - h * 0.28)
        if (d < h * 0.12) px = mix(px, sun, 0.95)
        else if (d < h * 0.2) px = mix(px, sun, 0.35 * (1 - (d - h * 0.12) / (h * 0.08)))
        if (y > h * (0.55 + 0.07 * Math.sin(x / 38) + 0.04 * Math.sin(x / 15))) px = mix(hill2, hill1, 0.4 + 0.5 * Math.sin(x / 90))
        if (y > h * (0.72 + 0.05 * Math.sin(x / 55 + 2))) px = hill1.map(v => Math.round(v * 0.8))
        px.forEach((v, i) => { raw[y * (w * 3 + 1) + 1 + x * 3 + i] = v })
      }
    }
    const ihdr = Buffer.alloc(13); ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4); ihdr[8] = 8; ihdr[9] = 2
    return Buffer.concat([Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw)), chunk('IEND', Buffer.alloc(0))]).toString('base64')
  }
  const uploadImage = (b64, name) => page.evaluate(async (b64, name) => {
    const form = new FormData()
    form.append('file', new Blob([Uint8Array.from(atob(b64), c => c.charCodeAt(0))], { type: 'image/png' }), name)
    return (await (await fetch('/api/notes/images', { method: 'POST', body: form })).json()).name
  }, b64, name)

  const plainOf = blocks => blocks.map(h => h.replace(/<br\s*\/?>/g, '\n')).join('\n').replace(/<\/(li|p|h\d)>/g, '\n').replace(/<[^>]+>/g, '').replace(/\n+/g, '\n').trim()
  const makeNote = async (blocks, color, important = false) => {
    const { id } = await json('POST', '/api/notes', { folder: null })
    await json('PUT', `/api/notes/${id}/content`, { blocks: blocks.map(html => ({ html })), plain: plainOf(blocks) })
    await json('PUT', `/api/notes/${id}/meta`, { color, important })
    return id
  }
  /** Rezept in der Gestalt der notizi-Vorlage: Eckdaten, Zutaten mit Kaestchen, Zubereitung. */
  const recipe = (title, time, portions, ingredients, steps, color) => makeNote([
    `<p><b>${title}</b></p>`,
    `<p>Arbeitszeit ${time[0]} Min. · Koch-/Backzeit ${time[1]} Min. · Portionen ${portions} · Sammlung Familie</p>`,
    '<p><b>Zutaten</b></p>',
    ...ingredients.map(i => `<p>☐ ${i}</p>`),
    '<p><b>Zubereitung</b></p>',
    ...steps.map((s, n) => `<p>${n + 1}. ${s}</p>`),
    '<p>Rezept von Oma Berger</p>',
  ], color)

  // ---------- Rezepte ----------
  const recipeIds = {}
  recipeIds['Spaghetti Bolognese'] = await recipe('Spaghetti Bolognese', [15, 30], 4,
    ['500 g Spaghetti', '400 g Hackfleisch', '1 Dose passierte Tomaten', '2 Zwiebeln', '2 Möhren', '1 Bund Petersilie'],
    ['Zwiebeln und Möhren fein würfeln und im Öl anbraten.', 'Hackfleisch zugeben, krümelig braten, mit Tomaten aufgießen und 25 Minuten köcheln.', 'Nudeln kochen, mit der Soße servieren.'], '#FFD6E7')
  recipeIds['Gemüsesuppe'] = await recipe('Gemüsesuppe', [20, 25], 4,
    ['1 kg Kartoffeln', '3 Möhren', '1 Stange Lauch', '1 l Gemüsebrühe', '1 Pck Suppenkräuter', '1 Vollkornbrot'],
    ['Gemüse schälen und würfeln.', 'In der Brühe 25 Minuten garen, mit Kräutern abschmecken.'], '#C7F0C2')
  recipeIds['Pfannkuchen mit Apfelmus'] = await recipe('Pfannkuchen mit Apfelmus', [10, 20], 4,
    ['300 g Mehl', '500 ml Milch', '4 Eier', '1 Glas Apfelmus', '1 Pck Vanillezucker', '50 g Butter'],
    ['Mehl, Milch und Eier zu einem glatten Teig rühren, 15 Minuten quellen lassen.', 'In der Pfanne in Butter goldbraun ausbacken.'], '#FFF9A8')
  recipeIds['Kartoffelauflauf'] = await recipe('Kartoffelauflauf', [20, 45], 4,
    ['1 kg Kartoffeln', '200 g Gouda', '200 ml Sahne', '2 Zwiebeln', '1 Pck Schinkenwürfel'],
    ['Kartoffeln in Scheiben schneiden und in die Form schichten.', 'Sahne darübergießen, Käse darüberstreuen und 45 Minuten bei 180 Grad backen.'], '#FFF9A8')
  recipeIds['Linsencurry'] = await recipe('Linsencurry', [10, 30], 4,
    ['250 g rote Linsen', '1 Dose Kokosmilch', '2 EL Currypaste', '1 Zwiebel', '300 g Reis'],
    ['Zwiebel anbraten, Currypaste zugeben, Linsen und Kokosmilch zufügen.', '20 Minuten köcheln, mit Reis servieren.'], '#BDE3FF')
  recipeIds['Tomatensuppe'] = await recipe('Tomatensuppe', [10, 20], 3,
    ['1 Dose Tomaten', '1 Zwiebel', '200 ml Sahne', '1 Bund Basilikum', '4 Scheiben Toast'],
    ['Zwiebel anschwitzen, Tomaten zugeben, 15 Minuten kochen und pürieren.', 'Mit Sahne verfeinern.'], '#FFD6E7')
  const found = await json('GET', '/api/notes/recipes')
  const syncId = title => found.find(r => r.title === title)?.syncId
  if (found.length < 6) console.warn('    Rezepte nicht erkannt: ' + found.length)

  // ---------- Kalender "Essensplan", mit Sam (bearbeiten) und Mia (lesen) geteilt ----------
  await json('POST', '/api/admin/users', { login: 'mia', displayName: 'Mia Berger', password: 'Doku-mia-' + Date.now(), isAdmin: false })
  const planCal = (await json('POST', '/api/pim/collections/calendars', { name: 'Essensplan', color: '#2E7D4F', description: null, components: 'VEVENT' })).id
  const people = await json('GET', '/api/pim/people')
  const sam = people.find(p => p.login === 'sam'), miaP = people.find(p => p.login === 'mia')
  await json('PUT', `/api/pim/shares/${planCal}`, { members: [sam.id, miaP.id], readOnly: [miaP.id] })

  const monday = (() => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), d.getDate() - ((d.getDay() + 6) % 7)) })()
  const MEAL = { f: ['Frühstück', 8], m: ['Mittagessen', 12], a: ['Abendessen', 18] }
  const at = (dayIdx, h) => new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + dayIdx, h).toISOString()
  const meal = (dayIdx, kind, title, rec) => json('POST', '/api/pim/events', {
    calendar: planCal, title, start: at(dayIdx, MEAL[kind][1]), end: at(dayIdx, MEAL[kind][1] + 1), allDay: false, busy: false,
    categories: [MEAL[kind][0]], reminder: null, recipe: rec ? syncId(title) : '', scope: 'series',
  })
  for (const [d, k, title, rec] of [
    [0, 'f', 'Müsli mit Obst'], [1, 'f', 'Haferbrei mit Apfel'], [2, 'f', 'Müsli mit Obst'], [3, 'f', 'Haferbrei mit Apfel'], [4, 'f', 'Müsli mit Obst'],
    [5, 'f', 'Brötchen und Rührei'], [6, 'f', 'Brötchen und Rührei'],
    [0, 'm', 'Gemüsesuppe', 1], [1, 'm', 'Nudelsalat'], [2, 'm', 'Pfannkuchen mit Apfelmus', 1], [3, 'm', 'Kartoffelauflauf', 1], [4, 'm', 'Fischstäbchen mit Püree'],
    [5, 'm', 'Spaghetti Bolognese', 1], [6, 'm', 'Sonntagsbraten bei Oma'],
    [0, 'a', 'Abendbrot mit Käse'], [1, 'a', 'Tomatensuppe', 1], [2, 'a', 'Belegte Brote'], [3, 'a', 'Linsencurry', 1], [5, 'a', 'Pizza vom Blech'],
  ]) await meal(d, k, title, rec)

  // ---------- Einkaufsliste mit Abteilungen fuer die Zutaten ----------
  const famList = (await json('POST', '/api/pim/collections/calendars', { name: 'Einkauf Familie', color: '#C239B3', description: null, components: 'VTODO' })).id
  await json('PUT', `/api/pim/lists/${famList}`, { type: 'shop', sections: ['Obst & Gemüse', 'Kühlregal', 'Käse', 'Fleisch & Wurst', 'Konserven & Vorrat', 'Brot & Backwaren'] })
  await json('POST', '/api/pim/tasks', { list: famList, title: '1 l Milch', item: 'Milch', qty: '1', unit: 'l', section: 'Kühlregal', dueAllDay: true, priority: 0 })
  await json('POST', '/api/pim/tasks', { list: famList, title: '6 Äpfel', item: 'Äpfel', qty: '6', unit: '', section: 'Obst & Gemüse', dueAllDay: true, priority: 0 })

  // ---------- Notizen: Bildvorschau, Termin aus der Notiz ----------
  const pics = [
    scenery(480, 320, [120, 170, 230], [250, 215, 170], [60, 120, 70], [110, 160, 90], [255, 240, 180]),
    scenery(480, 320, [250, 170, 120], [255, 220, 150], [80, 90, 140], [140, 110, 150], [255, 250, 220]),
    scenery(480, 320, [90, 140, 200], [190, 225, 245], [50, 100, 110], [90, 150, 140], [255, 255, 240]),
  ]
  const img = async (b64, n) => `<p><img src="blob:${await uploadImage(b64, `doku-${n}.png`)}" /></p>`
  const inDays = n => { const d = new Date(); d.setDate(d.getDate() + n); return `${d.getDate()}.${d.getMonth() + 1}.${d.getFullYear()}` }
  await makeNote(['<p><b>Ideen Geburtstag</b></p>', '<p>Mottoparty im Garten, Lampions, Schokokuchen</p>'], '#FFFFFF')
  await makeNote(['<p><b>Ausflug Sonntag</b></p>', await img(pics[2], 3), '<p>Picknick mit Jana und Tom, Decke und Federball einpacken.</p>'], '#FFF9A8')
  await makeNote(['<p><b>Ferienhaus am See</b></p>', await img(pics[1], 2), '<p>Drei Zimmer, Steg, Boot inklusive. Frühbucherpreis bis Ende November.</p>'], '#FFD6E7')
  await makeNote(['<p><b>Wanderung Harzblick</b></p>', await img(pics[0], 1), '<p>Start am Parkplatz Mühlental, ca. 14 km, gutes Schuhwerk.</p>'], '#C7F0C2')
  await makeNote(['<p><b>Elternabend Klasse 3b</b></p>', `<p>Am ${inDays(8)} um 19:30 Uhr in der Aula.</p>`, '<p>Themen: Klassenfahrt, Elterncafé, Lesenacht</p>', '<p>Hefter und Kuchenspende mitbringen.</p>'], '#BDE3FF')

  // ---------- Termin mit Teilnehmern (Antworten wie nach Einladungen per Mail) ----------
  const cals = await json('GET', '/api/pim/collections/calendars')
  const privat = cals.find(c => c.name === 'Persönlich').id
  const pad2 = n => String(n).padStart(2, '0')
  const thu = new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + 3)
  const stamp = h => `${thu.getFullYear()}${pad2(thu.getMonth() + 1)}${pad2(thu.getDate())}T${pad2(h)}0000`
  const att = (name, mail, st) => `ATTENDEE;CN=${name};ROLE=REQ-PARTICIPANT;PARTSTAT=${st}${st === 'NEEDS-ACTION' ? ';RSVP=TRUE' : ''}:mailto:${mail}\r\n`
  const ics = 'BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//dmOffice//Doku//DE\r\nBEGIN:VEVENT\r\nUID:doku-fest@dmoffice\r\nDTSTAMP:20261001T000000Z\r\n' +
    'SUMMARY:Planung Nachbarschaftsfest\r\nLOCATION:Gemeindehaus\r\n' +
    `DTSTART;TZID=Europe/Berlin:${stamp(19)}\r\nDTEND;TZID=Europe/Berlin:${stamp(20)}\r\n` +
    'ORGANIZER;CN=Alex Berger:mailto:alex@beispiel.de\r\n' +
    att('Jana Wolff', 'jana.wolff@example.org', 'ACCEPTED') + att('Tom Becker', 'tom.becker@example.net', 'TENTATIVE') +
    att('Sophie Krüger', 'sophie.krueger@example.org', 'DECLINED') + att('Paul Hoffmann', 'p.hoffmann@hoffmann-ag.example', 'NEEDS-ACTION') +
    'END:VEVENT\r\nEND:VCALENDAR\r\n'
  await page.evaluate(async (id, ics) => {
    const form = new FormData()
    form.append('files', new Blob([ics], { type: 'text/calendar' }), 'fest.ics')
    await fetch(`/api/pim/import/${id}`, { method: 'POST', body: form })
  }, privat, ics)
  // Eine neue Einladung wie aus dem Termindialog: landet im Postausgang
  const accountId = (await json('GET', '/api/mail/accounts'))[0].id
  const invStart = new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + 9, 18)
  const sent = await json('POST', '/api/pim/events', {
    calendar: privat, title: 'Grillabend im Garten', location: 'Bei uns im Garten', description: 'Bitte Salate und Getränke mitbringen.',
    start: invStart.toISOString(), end: new Date(invStart.getTime() + 4 * 3600000).toISOString(), allDay: false, busy: true, reminder: 60, scope: 'series',
    attendees: [{ email: 'jana.wolff@example.org', name: 'Jana Wolff' }, { email: 'tom.becker@example.net', name: 'Tom Becker' }], account: accountId, notify: true,
  })
  console.log('    Einladung:', JSON.stringify(sent))

  // ---------- Bilder ----------
  await local('dmoffice.calendar.meal', '1')
  await local('dmoffice.mealplan.calendar', String(planCal))
  await go('calendar', 2000)
  await shot('e-plan-woche')

  // Eintrag bearbeiten: Freitag Abend (leer), Rezepte zur Auswahl
  const cellBox = (d, m) => page.evaluate((d, m) => {
    const grid = [...document.querySelectorAll('div')].find(e => getComputedStyle(e).display === 'grid' && e.children.length === 32)
    const b = grid?.children[4 + d * 4 + 1 + m]?.querySelector('button[aria-label="Eintragen"]')
    if (!b) return null
    const r = b.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 }
  }, d, m)
  const addBtn = await cellBox(4, 2)
  if (addBtn) { await page.mouse.click(addBtn.x, addBtn.y); await pause(900) } else console.warn('    Eintragen-Knopf nicht gefunden')
  await shotHere('e-plan-eintrag')
  await escape()

  // Zutaten der Woche
  await mouse('button', 'Zutaten auf die Einkaufsliste')
  await pause(1500)
  // Die Liste "Einkauf Familie" waehlen (es gibt zwei Einkaufslisten)
  await mouse('[role=dialog] [role=combobox]')
  await mouse('[role=option]', 'Einkauf Familie')
  await pause(500)
  await shotHere('e-plan-zutaten-dialog')
  await mouse('button', '=Hinzufügen')
  await noToasts()
  await go(`tasks/${famList}`, 1800)
  // Die Liste ist lang: bis zur ersten Abteilung nach Obst & Gemüse scrollen, damit mehrere Abteilungen zu sehen sind
  await page.evaluate(() => { [...document.querySelectorAll('div,span')].find(e => e.offsetParent && e.children.length === 0 && /^k[üu]hlregal$/i.test(e.textContent?.trim() ?? ''))?.scrollIntoView({ block: 'start' }) })
  await pause(500)
  await shot('e-plan-zutaten')

  // Rezept in den Notizen
  await go(`notes/${recipeIds['Spaghetti Bolognese']}`, 2000)
  await shot('e-rezept-notiz')

  // Kalender teilen
  await local('dmoffice.calendar.meal', '0')
  await go('calendar', 1800)
  const side = await page.evaluate(() => {
    const els = [...document.querySelectorAll('span')].filter(e => e.offsetParent && e.children.length === 0 && e.textContent?.trim() === 'Essensplan')
    const r = els.at(-1)?.getBoundingClientRect(); return r ? { x: r.x + 10, y: r.y + r.height / 2 } : null
  })
  if (side) await page.mouse.click(side.x, side.y, { button: 'right' }); else console.warn('    Essensplan nicht in der Seitenleiste')
  await pause(700)
  await click('Teilen …')
  await pause(1000)
  await shotHere('k-geteilt')
  await escape()

  // Termin mit Teilnehmern
  await go('calendar', 1800)
  await click('Planung Nachbarschaftsfest')
  await pause(1200)
  await shotHere('k-teilnehmer')
  await escape()

  // Einladung im Postausgang
  await go('mail', 1500)
  await mouse('button', 'Postausgang')
  await pause(1500)
  await shot('k-einladung-mail')

  // Notizliste mit Bildvorschau, Termin aus einer Notiz
  await go('notes', 2000)
  await shot('n-vorschaubild')
  await rclick('Elternabend Klasse 3b')
  await pause(500)
  await shotHere('n-notiz-termin')
  await click('Termin erstellen')
  await pause(1200)
  await shotHere('n-notiz-termin-dialog')
  await escape()
  // ---------- Notizordner teilen (Besitzer alex, Empfaenger sam) ----------
  const rf = (await json('POST', '/api/notes/folders', { name: 'Rezepte Familie', parent: null })).id
  const bf = (await json('POST', '/api/notes/folders', { name: 'Backen', parent: rf })).id
  for (const id of Object.values(recipeIds)) await json('PUT', `/api/notes/${id}/meta`, { folder: rf })
  await makeNote(['<p><b>Apfelkuchen</b></p>', '<p>Arbeitszeit 20 Min. · Koch-/Backzeit 45 Min.</p>', '<p><b>Zutaten</b></p>', '<p>☐ 500 g Äpfel</p>', '<p>☐ 250 g Mehl</p>', '<p>☐ 3 Eier</p>', '<p><b>Zubereitung</b></p>', '<p>1. Teig rühren, Äpfel darauf legen, 45 Minuten backen.</p>'], '#FFF9A8')
    .then(id => json('PUT', `/api/notes/${id}/meta`, { folder: bf }))
  await go('notes', 2000)
  await rclick('Rezepte Familie')
  await click('Teilen …')
  await pause(1000)
  const samDrop = await page.evaluate(() => {
    const row = [...document.querySelectorAll('[role=dialog] div')].find(d => d.querySelector('[role=combobox]') && d.textContent?.includes('Sam Berger') && !d.textContent.includes('Mia Berger'))
    const r = row?.querySelector('[role=combobox]')?.getBoundingClientRect(); return r ? { x: r.x + r.width / 2, y: r.y + r.height / 2 } : null
  })
  if (samDrop) { await page.mouse.click(samDrop.x, samDrop.y); await pause(700) } else console.warn('    Dropdown von Sam nicht gefunden')
  await mouse('[role=option]', '=Bearbeiten')
  await pause(400)
  await shotHere('n-teilen-dialog')
  await click('Speichern', 'button')
  await pause(1200)

  // Ansicht von Sam: eigene Notiz, geteilter Ordner
  await page.evaluate(async pw => {
    await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ login: 'sam', password: pw + '-sam' }) })
  }, password)
  // Der verschluesselte Umschlag des Tabs gehoert noch zu alex: fuer sam neu aufbauen
  await page.evaluate(() => sessionStorage.removeItem('dmoffice.e2e'))
  await setTheme('light')
  await prefs({ language: 'de', accent: null, ribbonLabels: null })
  await makeNote(['<p><b>Kürbissuppe</b></p>', '<p>Arbeitszeit 15 Min. · Koch-/Backzeit 30 Min.</p>', '<p><b>Zutaten</b></p>', '<p>☐ 1 Hokkaido-Kürbis</p>', '<p>☐ 1 l Gemüsebrühe</p>', '<p><b>Zubereitung</b></p>', '<p>1. Kürbis würfeln und weich kochen, pürieren.</p>'], '#FFD6E7')
  await go('notes', 2000)
  await click('Rezepte Familie')
  await pause(1000)
  await click('Spaghetti Bolognese')
  await pause(1500)
  await shot('n-geteilt-empfaenger')

  // Eigene Notiz in den geteilten Ordner verschieben
  await go('notes', 2000)
  const k = await page.evaluate(() => {
    const el = [...document.querySelectorAll('*')].find(e => e.offsetParent && e.children.length === 0 && e.textContent?.trim() === 'Kürbissuppe')
    const r = el?.getBoundingClientRect(); return r ? { x: r.x + 20, y: r.y + r.height / 2 } : null
  })
  if (k) await page.mouse.click(k.x, k.y, { button: 'right' }); else console.warn('    Kürbissuppe nicht gefunden')
  await pause(700)
  await hover('[role=menuitem]', 'Verschieben')
  await shotHere('n-verschieben')
  await escape(); await escape()
  await local('dmoffice.calendar.meal', '0')
}

// ======================= Aktivitaet (Hintergrundaufgaben) =======================
if (need('a-aktivitaet-popover', 'a-aktivitaet-seite', 'a-aktivitaet-mobil')) {
  await page.evaluate(async pw => {
    await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ login: 'alex', password: pw }) })
  }, password)
  await page.evaluate(() => sessionStorage.removeItem('dmoffice.e2e'))
  await setTheme('light')
  await prefs({ language: 'de', accent: null, ribbonLabels: null })
  // Ein zweites Konto, dessen Server es nicht gibt: der Abgleich meldet einen echten Fehler
  await json('POST', '/api/settings/accounts', {
    name: 'Familie', email: 'familie@beispiel.de', displayName: 'Familie Berger',
    imapHost: 'mail.beispiel.invalid', imapPort: 993, imapSecurity: 'Ssl', imapUser: 'familie@beispiel.de', imapPassword: 'x',
    smtpHost: 'mail.beispiel.invalid', smtpPort: 465, smtpSecurity: 'Ssl', smtpUser: 'familie@beispiel.de', smtpPassword: 'x',
  })
  // Viele kleine Textdateien, dann den Volltextindex einschalten: der Server arbeitet sie nacheinander ab
  await json('POST', '/api/files/mkdir', { path: 'Archiv' })
  await go('files', 1500)
  const upload = async (from, to) => page.evaluate(async (from, to) => {
    const fd = new FormData()
    for (let i = from; i <= to; i++) fd.append('files', new Blob([`Rechnung ${i} vom Gartenbau Beispiel, Hochbeet, Regentonne, Kraeuterspirale. `.repeat(300)]), `Rechnung ${String(i).padStart(3, '0')}.txt`)
    await fetch('/api/files/upload?path=Archiv', { method: 'POST', body: fd })
  }, from, to)
  for (let i = 1; i <= 1200; i += 100) await upload(i, i + 99)
  await json('PUT', '/api/settings/textindex', { enabled: true, ocr: false })
  await json('POST', '/api/settings/compact')
  // Moeglichst mitten in der Arbeit aufnehmen
  for (let i = 0; i < 40; i++) {
    const a = await json('GET', '/api/activity')
    if (a.items.some(x => x.id === 'text-index' && x.total)) break
    await pause(300)
  }
  // Index neu aufbauen lassen und warten, bis die Anzeige mitten im Lauf steht ("läuft · 312 von 1.200")
  const midRun = async () => {
    await json('POST', '/api/settings/textindex/rebuild')
    for (let i = 0; i < 80; i++) {
      const txt = await page.evaluate(() => document.body.innerText)
      if (/läuft · [1-9][\d.]* von/.test(txt)) return true
      await pause(250)
    }
    console.warn('    kein laufender Index gesehen'); return false
  }
  await go('settings/stats', 1000)
  await go('mail', 1500)
  await page.click('button[aria-label^="Aktivität"]')
  await pause(3500)
  await midRun()
  await shot('a-aktivitaet-popover')
  await escape()
  await go('settings/activity', 3500)
  await midRun()
  await shot('a-aktivitaet-seite')
  await page.setViewport({ width: 430, height: 900, deviceScaleFactor: 1 })
  await go('settings/activity', 3500)
  await midRun()
  await shot('a-aktivitaet-mobil')
  await page.setViewport(VIEW)
}


// ======================= Ordner intern teilen, Kalenderarten, Suchordner, Notizen (Blog 21.10.) =======================
// Eigene Beispieldaten (erfunden); laufen auch mit NEW_ONLY=1 und ohne die Schritte davor.
const FOLDER_SCENES = ['f-teilen-personen', 'f-mit-mir-geteilt', 'f-von-mir-geteilt']
const KIND_SCENES = ['k-kalenderarten', 'k-kalender-art-dialog', 'e-plan-mehrere']
if (need(...FOLDER_SCENES, ...KIND_SCENES, 's-suchordner-liste', 'n-mehrfach-archivieren')) {
  const loginAs = async (login, pw) => {
    await page.evaluate(async (login, pw) => {
      await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ login, password: pw }) })
    }, login, pw)
    // Der verschluesselte Umschlag des Tabs gehoert zum vorigen Benutzer: neu aufbauen
    await page.evaluate(() => sessionStorage.removeItem('dmoffice.e2e'))
    await setTheme('light')
    await prefs({ language: 'de', accent: null, ribbonLabels: null })
  }
  const samPw = password + '-sam'
  await loginAs('alex', password)
  await local('dmoffice.calendar.meal', '0')

  /** Kleines erfundenes "Foto" (Himmel, Huegel, Sonne) im Browser malen. */
  const paintPhoto = (hue, seed) => page.evaluate(async (hue, seed) => {
    const c = document.createElement('canvas'); c.width = 640; c.height = 420
    const g = c.getContext('2d')
    const sky = g.createLinearGradient(0, 0, 0, 420); sky.addColorStop(0, `hsl(${hue},70%,62%)`); sky.addColorStop(1, `hsl(${hue + 30},80%,88%)`)
    g.fillStyle = sky; g.fillRect(0, 0, 640, 420)
    g.fillStyle = '#fff6c8'; g.beginPath(); g.arc(120 + seed * 90, 110, 38, 0, 7); g.fill()
    for (const [y, l] of [[250, 38], [300, 30], [350, 22]]) {
      g.fillStyle = `hsl(${hue + 100},45%,${l}%)`; g.beginPath(); g.moveTo(0, 420)
      for (let x = 0; x <= 640; x += 8) g.lineTo(x, y + Math.sin(x / 60 + seed * 2 + y) * 26)
      g.lineTo(640, 420); g.fill()
    }
    const blob = await new Promise(r => c.toBlob(r, 'image/jpeg', 0.85))
    return Array.from(new Uint8Array(await blob.arrayBuffer()))
  }, hue, seed)
  const putFile = (dir, name, bytes, type) => page.evaluate(async (dir, name, bytes, type) => {
    const fd = new FormData(); fd.append('files', new Blob([new Uint8Array(bytes)], { type }), name)
    const r = await fetch('/api/files/upload?path=' + encodeURIComponent(dir), { method: 'POST', body: fd })
    if (!r.ok) console.warn('upload', name, r.status)
  }, dir, name, bytes, type)
  const text = s => Array.from(Buffer.from(s, 'utf8'))

  if (need(...FOLDER_SCENES)) {
    // ---------- Beispielordner ----------
    for (const d of ['Familienfotos', 'Familienfotos/Sommer 2026', 'Familienfotos/Geburtstag Mia', 'Reisen 2026', 'Verein Blau-Weiß']) await json('POST', '/api/files/mkdir', { path: d })
    const pics = [['Sommer 2026', 'Am See.jpg', 200, 0], ['Sommer 2026', 'Eis am Hafen.jpg', 20, 1], ['Sommer 2026', 'Radtour.jpg', 140, 2],
      ['Geburtstag Mia', 'Kuchen.jpg', 330, 1], ['Geburtstag Mia', 'Gruppenbild.jpg', 280, 2]]
    for (const [d, n, h, sd] of pics) await putFile('Familienfotos/' + d, n, await paintPhoto(h, sd), 'image/jpeg')
    await putFile('Familienfotos', 'Fotoliste.txt', text('Fotos für das Familienalbum 2026\nSommer: See, Hafen, Radtour\nGeburtstag Mia: Kuchen, Gruppenbild\n'), 'text/plain')
    await putFile('Reisen 2026', 'Packliste Lissabon.txt', text('Reisepass\nLadekabel\nSonnencreme\nRegenjacke\n'), 'text/plain')
    await putFile('Reisen 2026', 'Hotel Lissabon.jpg', await paintPhoto(190, 0), 'image/jpeg')
    await putFile('Verein Blau-Weiß', 'Protokoll Jahreshauptversammlung.txt', text('Protokoll der Jahreshauptversammlung\nTOP 1 Begrüßung\nTOP 2 Kassenbericht\n'), 'text/plain')
    let people = await json('GET', '/api/pim/people')
    if (!people.some(p => p.login === 'mia')) {
      await json('POST', '/api/admin/users', { login: 'mia', displayName: 'Mia Berger', password: 'Doku-mia-' + Date.now(), isAdmin: false })
      people = await json('GET', '/api/pim/people')
    }
    const sam = people.find(p => p.login === 'sam'), mia = people.find(p => p.login === 'mia')
    // Familienfotos: Sam darf bearbeiten, Mia nur lesen; Reisen 2026: Sam liest; Vereinsordner nur per Link
    await json('PUT', '/api/files/shares', { path: 'Familienfotos', members: [sam.id, mia.id], readOnly: [mia.id] })
    await json('PUT', '/api/files/shares', { path: 'Reisen 2026', members: [sam.id], readOnly: [sam.id] })
    const inDays = n => new Date(Date.now() + n * 86400000).toISOString()
    const links = await json('GET', '/api/files/links?path=' + encodeURIComponent('Familienfotos'))
    for (const l of links.items.filter(l => !l.hasPassword)) await json('DELETE', '/api/files/links/' + l.id)
    if (!links.items.some(l => l.hasPassword)) await json('POST', '/api/files/links', { path: 'Familienfotos', mode: 'read', expires: inDays(30), password: 'Familie-2026', maxDownloads: 10 })
    const links2 = await json('GET', '/api/files/links?path=' + encodeURIComponent('Verein Blau-Weiß'))
    if (!links2.items.length) await json('POST', '/api/files/links', { path: 'Verein Blau-Weiß', mode: 'upload', expires: inDays(14), password: 'Verein-2026', maxDownloads: 0 })
    await prefs({ fileView: 'tiles', fileGroup: 'none', fileSort: 'name', fileSortDesc: false, fileDetails: false })

    // ---------- Teilen-Dialog des Ordners: Personen oben, Link darunter (etwas hoeher, damit der ganze Dialog passt) ----------
    await page.setViewport({ width: 1440, height: 1000, deviceScaleFactor: 1 })
    await go('files', 2000)
    await rclick('Familienfotos')
    await click('Teilen …')
    await pause(1500)
    await page.evaluate(o => { for (const i of document.querySelectorAll('[role=dialog] input')) if (i.value.startsWith(o)) i.value = i.value.replace(o, 'https://office.beispiel.de') }, base)
    await shotHere('f-teilen-personen')
    await escape()
    await page.setViewport(VIEW)

    // ---------- Von mir geteilt (Besitzer) ----------
    await prefs({ fileView: 'details', fileDetails: false })
    await go('files/:shared', 2000)
    await shot('f-von-mir-geteilt')

    // ---------- Mit mir geteilt (Empfaenger Sam): eigenes Browserprofil, damit nichts von alex im Tab haengt ----------
    {
      const ctx = await browser.createBrowserContext()
      const sp = await ctx.newPage()
      await sp.setViewport(VIEW)
      await sp.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'light' }])
      await sp.setExtraHTTPHeaders({ 'X-Forwarded-Host': 'office.beispiel.de', 'X-Forwarded-Proto': 'https' })
      await sp.goto(base, { waitUntil: 'networkidle0' })
      await sp.evaluate(async pw => {
        await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ login: 'sam', password: pw }) })
        await fetch('/api/settings/general', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ theme: 'light' }) })
        const p = await (await fetch('/api/settings/ui')).json()
        Object.assign(p, { language: 'de', fileView: 'tiles' })
        await fetch('/api/settings/ui', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(p) })
        localStorage.setItem('dmoffice.ui', JSON.stringify(p)); localStorage.setItem('dmoffice.theme', 'light')
      }, samPw)
      if (!(await sp.evaluate(async () => (await (await fetch('/api/files?path=')).json()).items.some(i => i.name === 'Einkaufsideen.txt')))) await sp.evaluate(async () => {
        const fd = new FormData(); fd.append('files', new Blob(['Geschenk für Mia: Buch, Malstifte'], { type: 'text/plain' }), 'Einkaufsideen.txt')
        await fetch('/api/files/upload?path=', { method: 'POST', body: fd })
      })
      await sp.goto(base + '/#/files/' + encodeURIComponent('Mit mir geteilt'), { waitUntil: 'networkidle0' })
      await sp.reload({ waitUntil: 'networkidle0' })
      await pause(2200)
      await shotHere('f-mit-mir-geteilt', sp)
      await ctx.close()
    }
  }

  if (need(...KIND_SCENES)) {
    // ---------- Kalenderarten: zwei Essensplan-Kalender (Name frei), dazu die normalen ----------
    const cals = await json('GET', '/api/pim/collections/calendars')
    const mk = async (name, color, kind) => cals.find(c => c.name === name)?.id
      ?? (await json('POST', '/api/pim/collections/calendars', { name, color, description: null, components: 'VEVENT', calendarKind: kind })).id
    const fresh = !cals.some(c => c.name === 'Wochenmenü Familie')
    const family = await mk('Wochenmenü Familie', '#2E7D4F', 'meal')
    const school = await mk('Mittagstisch Schule', '#E08A00', 'meal')
    if (fresh) {
      const monday = (() => { const d = new Date(); return new Date(d.getFullYear(), d.getMonth(), d.getDate() - ((d.getDay() + 6) % 7)) })()
      const MEAL = { f: ['Frühstück', 8], m: ['Mittagessen', 12], a: ['Abendessen', 18] }
      const at = (dayIdx, h) => new Date(monday.getFullYear(), monday.getMonth(), monday.getDate() + dayIdx, h).toISOString()
      const meal = (cal, dayIdx, kind, title) => json('POST', '/api/pim/events', {
        calendar: cal, title, start: at(dayIdx, MEAL[kind][1]), end: at(dayIdx, MEAL[kind][1] + 1), allDay: false, busy: false,
        categories: [MEAL[kind][0]], reminder: null, recipe: '', scope: 'series',
      })
      for (const [d, k, title] of [
        [0, 'f', 'Müsli mit Obst'], [1, 'f', 'Haferbrei mit Apfel'], [2, 'f', 'Müsli mit Obst'], [3, 'f', 'Haferbrei mit Apfel'], [4, 'f', 'Müsli mit Obst'],
        [5, 'f', 'Brötchen und Rührei'], [6, 'f', 'Brötchen und Rührei'],
        [5, 'm', 'Spaghetti Bolognese'], [6, 'm', 'Sonntagsbraten bei Oma'],
        [0, 'a', 'Abendbrot mit Käse'], [1, 'a', 'Tomatensuppe'], [2, 'a', 'Belegte Brote'], [3, 'a', 'Linsencurry'], [4, 'a', 'Pizza vom Blech'], [5, 'a', 'Grillabend'],
      ]) await meal(family, d, k, title)
      for (const [d, title] of [[0, 'Gemüselasagne'], [1, 'Nudeln mit Tomatensoße'], [2, 'Kartoffelsuppe'], [3, 'Fischstäbchen mit Püree'], [4, 'Reispfanne mit Gemüse']]) await meal(school, d, 'm', title)
    }

    // ---------- Kalenderliste nach Art gruppiert ----------
    await local('dmoffice.calendar.meal', '0')
    await prefs({ calendarView: 'month' })
    await go('calendar', 2200)
    await shot('k-kalenderarten')

    // ---------- Dialog: wo die Art gewaehlt wird ----------
    await mouse('button[aria-label="Kalender hinzufügen"]')
    await pause(800)
    await page.click('[role=dialog] input')
    await page.keyboard.type('Wochenmenü Oma', { delay: 25 })
    await page.evaluate(() => document.querySelectorAll('[role=dialog] button[aria-label^="#"]')[1]?.click())
    await mouse('[role=dialog] label', 'Essensplan')
    await pause(500)
    await shotHere('k-kalender-art-dialog')
    await escape()

    // ---------- Essensplan mit Eintraegen aus zwei Kalendern ----------
    // Zwei Rezepte in der Gestalt der notizi-Vorlage, damit unter dem Plan kein Hinweis "keine Rezepte" steht
    if (!(await json('GET', '/api/notes/recipes')).length) for (const [title, ing] of [['Spaghetti Bolognese', ['500 g Spaghetti', '400 g Hackfleisch', '1 Dose Tomaten']], ['Linsencurry', ['250 g rote Linsen', '1 Dose Kokosmilch', '2 EL Currypaste']]]) {
      const blocks = [`<p><b>${title}</b></p>`, '<p>Arbeitszeit 15 Min. · Koch-/Backzeit 30 Min. · Portionen 4 · Sammlung Familie</p>', '<p><b>Zutaten</b></p>',
        ...ing.map(i => `<p>☐ ${i}</p>`), '<p><b>Zubereitung</b></p>', '<p>1. Alles vorbereiten und garen.</p>', '<p>Rezept von Oma Berger</p>']
      const { id } = await json('POST', '/api/notes', { folder: null })
      await json('PUT', `/api/notes/${id}/content`, { blocks: blocks.map(html => ({ html })), plain: blocks.map(h => h.replace(/<[^>]+>/g, '')).join(String.fromCharCode(10)) })
      await json('PUT', `/api/notes/${id}/meta`, { color: '#FFF9A8', important: false })
    }
    await local('dmoffice.calendar.meal', '1')
    await go('calendar', 2500)
    await shot('e-plan-mehrere')
    await local('dmoffice.calendar.meal', '0')
  }

  // ---------- Notizen: mehrere markieren und archivieren ----------
  if (need('n-mehrfach-archivieren')) {
    await go('notes', 2000)
    const centerOf = title => page.evaluate(t => {
      const el = [...document.querySelectorAll('*')].find(e => e.offsetParent && e.children.length === 0 && e.textContent?.trim() === t)
      const r = el?.getBoundingClientRect(); return r ? { x: r.x + Math.min(r.width / 2, 40), y: r.y + r.height / 2 } : null
    }, title)
    const titles = ['Wochenende', 'Geschenkideen', 'Einkaufen']
    for (const [i, t] of titles.entries()) {
      const c = await centerOf(t)
      if (!c) { console.warn('    Notiz nicht gefunden: ' + t); continue }
      if (i > 0) await page.keyboard.down('Control')
      await page.mouse.click(c.x, c.y)
      if (i > 0) await page.keyboard.up('Control')
      await pause(500)
    }
    await pause(600)
    await shot('n-mehrfach-archivieren')
    await page.evaluate(() => [...document.querySelectorAll('button')].find(b => b.textContent?.trim() === 'Auswahl aufheben')?.click())
  }

  // ---------- Suchordner: Ordnerauswahl als Liste je Konto ----------
  if (need('s-suchordner-liste')) {
    let accs = await json('GET', '/api/mail/accounts')
    if (!accs.some(a => a.name === 'Verein')) {
      await json('POST', '/api/settings/accounts', {
        name: 'Verein', email: 'vorstand@blau-weiss.example', displayName: 'Alex Berger (Verein)',
        imapHost: 'mail.beispiel.invalid', imapPort: 993, imapSecurity: 'Ssl', imapUser: 'vorstand@blau-weiss.example', imapPassword: 'x',
        smtpHost: 'mail.beispiel.invalid', smtpPort: 465, smtpSecurity: 'Ssl', smtpUser: 'vorstand@blau-weiss.example', smtpPassword: 'x',
      })
      accs = await json('GET', '/api/mail/accounts')
    }
    const second = accs.find(a => a.name === 'Verein').id
    const folders = [['INBOX', 'INBOX', 'inbox'], ['Sent', 'Sent', 'sent'], ['Drafts', 'Drafts', 'drafts'], ['Trash', 'Trash', 'trash'], ['Archive', 'Archive', 'archive'],
      ['Vorstand', 'Vorstand', null], ['Vorstand/Protokolle', 'Protokolle', null], ['Vorstand/Termine', 'Termine', null], ['Mitglieder', 'Mitglieder', null]]
      .map(([path, name, special]) => ({ path, name, delimiter: '/', special, selectable: true, unread: 0, total: 0, synced: null }))
    // Die Ordner des zweiten Kontos kommen sonst von einem IMAP-Server: hier im Browser vorgeben
    const fake = await page.evaluateOnNewDocument((id, list) => {
      sessionStorage.removeItem('dmoffice.e2e')
      const real = window.fetch
      window.fetch = (input, init) => {
        const url = String(input?.url ?? input)
        if (url.endsWith('/api/e2e/key')) return Promise.resolve(new Response('', { status: 404 }))
        if (url.endsWith(`/api/mail/${id}/folders`)) return Promise.resolve(new Response(JSON.stringify(list), { status: 200, headers: { 'Content-Type': 'application/json' } }))
        return real(input, init)
      }
    }, second, folders)
    await go('mail', 2500)
    await mouse('button[aria-label="Neuer Suchordner"]')
    await pause(800)
    await page.click('[role=dialog] input')
    await page.keyboard.type('Rechnungen und Belege', { delay: 25 })
    await mouse('[role=dialog] button', 'Alle Ordner')
    await pause(700)
    // Nur das zweite Konto aufklappen (das erste bleibt eingeklappt), darin zwei Ordner ankreuzen
    await page.evaluate(() => document.querySelectorAll('button[aria-label="Aufklappen"]')[1]?.click())
    await pause(600)
    // (Klick auf das Kaestchen selbst: Der Text daneben zeigt wegen doppelter IDs im Fluent-Feld auf das Suchfeld, siehe Bericht)
    for (const l of ['Protokolle', 'Termine']) {
      const bx = await page.evaluate(l => {
        const lab = [...document.querySelectorAll('label')].find(e => e.offsetParent && e.textContent?.trim() === l)
        const r = lab?.parentElement?.querySelector('input')?.getBoundingClientRect(); return r ? { x: r.x + r.width / 2, y: r.y + r.height / 2 } : null
      }, l)
      if (!bx) { console.warn('    Kontrollkaestchen nicht gefunden: ' + l); continue }
      await page.mouse.click(bx.x, bx.y)
      await pause(500)
    }
    await pause(300)
    await shotHere('s-suchordner-liste')
    await escape(); await escape()
    await page.removeScriptToEvaluateOnNewDocument(fake.identifier)
  }
}

// ======================= 1.5: Kontingent, Kopfzeile, Hilfe, Sortieren, Updates, Fingerabdruck =======================
/** Verwaltung fuers Bild aufraeumen: Hinweis zum Hauptschluessel (Testumgebung) und Pfad des Datenordners ausblenden. */
const tidyAdmin = () => page.evaluate(() => {
  for (const el of document.querySelectorAll('[role=group],[class*=MessageBar]')) if (el.textContent?.includes('Hauptschlüssel')) el.style.display = 'none'
  for (const el of document.querySelectorAll('span,div,p')) if (el.children.length === 0 && el.textContent?.startsWith('Datenverzeichnis')) el.style.display = 'none'
})
const Q_SCENES = ['q-kontingent-dialog', 'q-kontingent-spalte', 'q-kopfzeile', 'q-hilfe-menue', 'm-sortieren-menue', 'm-sortiert-groesse', 'u-update-seite', 's-fingerabdruck']
if (need(...Q_SCENES)) {
  await page.evaluate(async pw => {
    await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ login: 'alex', password: pw }) })
  }, password)
  await page.evaluate(() => sessionStorage.removeItem('dmoffice.e2e'))
  await setTheme('light')
  await prefs({ language: 'de', accent: null, ribbonLabels: null, listSort: null })
  await page.setViewport(VIEW)

  // ---------- Verwaltung: Benutzer mit Kontingent-Spalte und Dialog ----------
  if (need('q-kontingent-dialog', 'q-kontingent-spalte')) {
    await local('dmoffice.admin.tab', 'users')
    await go('admin', 2500)
    await tidyAdmin()
    await shot('q-kontingent-spalte')
    const sam = await page.evaluate(() => {
      const row = [...document.querySelectorAll('tr')].find(r => r.offsetParent && r.cells?.[0]?.textContent?.trim() === 'sam')
      const b = row?.querySelector('button[title="Kontingent festlegen"]'); const r = b?.getBoundingClientRect()
      return r ? { x: r.x + r.width / 2, y: r.y + r.height / 2 } : null
    })
    if (sam) { await page.mouse.click(sam.x, sam.y); await pause(900) } else console.warn('    Zeile sam nicht gefunden')
    await mouse('[role=dialog] [role=radio], [role=dialog] label', 'Eigenes Kontingent')
    await pause(600)
    await tidyAdmin()
    await shotHere('q-kontingent-dialog')
    await escape()
  }

  // ---------- Kopfzeile und Hilfe-Menue ----------
  if (need('q-kopfzeile', 'q-hilfe-menue')) {
    await go('mail', 2500)
    await shot('q-kopfzeile')
    if (need('q-hilfe-menue')) {
      await mouse('button[aria-label="Hilfe"]')
      await pause(700)
      await shotHere('q-hilfe-menue')
      await escape()
    }
  }

  // ---------- Mailliste sortieren ----------
  if (need('m-sortieren-menue', 'm-sortiert-groesse')) {
    await go('mail', 2500)
    await mouse('button[aria-label="Sortieren"]')
    await pause(700)
    await shotHere('m-sortieren-menue')
    await mouse('[role=menuitemradio]', 'Größte zuerst')
    await pause(1200)
    await escape()
    await pause(600)
    await page.evaluate(() => { document.activeElement?.blur?.(); document.querySelector('button[aria-label="Sortieren"]')?.blur() })
    await page.mouse.click(1000, 700)
    await pause(400)
    await shot('m-sortiert-groesse')
    await prefs({ listSort: null })
  }

  // ---------- Verwaltung -> Updates: Sicherungen vor Datenaenderungen ----------
  if (need('u-update-seite')) {
    // Der Testserver hat nichts zu migrieren; die Sicherungen entstehen sonst beim Umbau - hier als Dateien im Datenordner nachgestellt
    const users = await json('GET', '/api/admin/users')
    const dataDir = join(dirname(fileURLToPath(import.meta.url)), '.data')
    const day = n => new Date(Date.now() - n * 86400000)
    for (const [n, ver, logins, mb] of [[3, '1.4.2', ['alex', 'sam', 'lena'], 38], [19, '1.3.0', ['alex', 'sam'], 31]]) {
      const created = day(n), name = created.toISOString().slice(0, 10) + '-' + ver
      const dir = join(dataDir, 'pre-update', name)
      mkdirSync(join(dir, 'users'), { recursive: true })
      const entries = logins.map(l => ({ Id: users.find(u => u.login === l).id, Login: l, Schema: 12, To: 14, Bytes: Math.round(mb / logins.length * 1048576), Created: created.toISOString() }))
      for (const e of entries) writeFileSync(join(dir, 'users', e.Id + '.db'), Buffer.alloc(e.Bytes, 1))
      writeFileSync(join(dir, 'manifest.json'), JSON.stringify({ Name: name, Created: created.toISOString(), AppVersion: ver, Users: entries }, null, 2))
    }
    await local('dmoffice.admin.tab', 'updates')
    await go('admin', 2500)
    await tidyAdmin()
    await shot('u-update-seite')
    await local('dmoffice.admin.tab', 'users')
  }

  // ---------- Einstellungen -> Geraete & Sync: Fingerabdruck des Serverschluessels ----------
  if (need('s-fingerabdruck')) {
    await go('settings/devices', 3000)
    await page.evaluate(() => [...document.querySelectorAll('h1,h2,h3,h4,span,div')].filter(e => e.children.length === 0 && e.textContent?.trim() === 'Handy-App koppeln' && e.offsetParent).at(-1)?.scrollIntoView({ block: 'start' }))
    await pause(500)
    await shot('s-fingerabdruck')
  }
}

await browser.close()
