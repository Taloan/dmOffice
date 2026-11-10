// Screenshots fuer die Doku mit Edge/Chrome (puppeteer-core) - Ergebnis in docs/img/.
// Aufruf: node shots.mjs <basis-url> <passwort> <browser.exe> [de|en]
import puppeteer from 'puppeteer-core'
import { mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

const [base, password, executablePath, lang = 'de'] = process.argv.slice(2)
const out = join(dirname(fileURLToPath(import.meta.url)), '..', '..', 'docs', 'img', lang)
mkdirSync(out, { recursive: true })

const browser = await puppeteer.launch({ executablePath, headless: true, defaultViewport: { width: 1440, height: 900, deviceScaleFactor: 1 } })
const page = await browser.newPage()
// Headless meldet oft "dunkel" - die Bilder sind hell, bis auf das eigene Beispiel
await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'light' }])
// Adressen wie hinter einem Reverse-Proxy (Geraete & Sync)
await page.setExtraHTTPHeaders({ 'X-Forwarded-Host': 'office.beispiel.de', 'X-Forwarded-Proto': 'https' })
await page.goto(base, { waitUntil: 'networkidle0' })
await page.evaluate(async (password, lang) => {
  localStorage.setItem('dmoffice.theme', 'light')
  await fetch('/api/auth/login', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ login: 'alex', password }) })
  await fetch('/api/settings/general', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ theme: 'light' }) })
  const prefs = await (await fetch('/api/settings/ui')).json()
  prefs.language = lang
  await fetch('/api/settings/ui', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(prefs) })
  localStorage.setItem('dmoffice.ui', JSON.stringify(prefs))
}, password, lang)

const pause = ms => new Promise(r => setTimeout(r, ms))
async function go(hash, wait = 1200) {
  await page.goto(base + '/#/' + hash, { waitUntil: 'networkidle0' })
  await page.reload({ waitUntil: 'networkidle0' })
  await pause(wait)
}
async function shot(name) {
  await page.mouse.move(5, 895)
  await pause(300)
  await page.screenshot({ path: join(out, name + '.png') })
  console.log('  ' + name)
}
/** Erstes sichtbares Element mit genau/teilweise diesem Text anklicken. */
async function click(text, selector = '*') {
  const ok = await page.evaluate((text, selector) => {
    const els = [...document.querySelectorAll(selector)].filter(e => e.offsetParent && e.textContent?.trim() === text)
    const el = els.at(-1) ?? [...document.querySelectorAll(selector)].find(e => e.offsetParent && e.children.length === 0 && e.textContent?.includes(text))
    if (!el) return false
    el.scrollIntoView({ block: 'center' }); (el.closest('button,[role=option],[role=menuitem],[role=menuitemcheckbox],[role=tab]') ?? el).click()
    return true
  }, text, selector)
  if (!ok) console.warn('    nicht gefunden: ' + text)
  await pause(700)
}

// ---------- Mail ----------
await go('mail')
await click('Angebot Nr. 2026-0418 – Wallbox')
await shot('mail')
await click(lang === 'en' ? 'Categorize' : 'Kategorisieren')
await shot('mail-kategorien')
await page.keyboard.press('Escape')
await pause(400)
await page.click('body')
await click(lang === 'en' ? 'Snooze' : 'Wiedervorlage')
await shot('mail-wiedervorlage')
await page.keyboard.press('Escape')
await pause(400)
await click(lang === 'en' ? 'New email' : 'Neue E-Mail')
await pause(800)
await shot('mail-verfassen')

// Dunkel
await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'dark' }])
// Die App uebernimmt beim Start das Design aus den Server-Einstellungen, daher beides setzen
const setTheme = theme => page.evaluate(async theme => {
  localStorage.setItem('dmoffice.theme', theme)
  await fetch('/api/settings/general', { method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ theme }) })
}, theme)
await setTheme('dark')
await go('mail')
await click('Grillabend am Samstag?')
await shot('mail-dunkel')
await page.emulateMediaFeatures([{ name: 'prefers-color-scheme', value: 'light' }])
await setTheme('light')

// ---------- Weitere Bereiche ----------
await go('calendar', 1800)
await shot('kalender')
await go('contacts')
await click('Jana Wolff')
await shot('kontakte')
await go('tasks')
await shot('aufgaben')
await go('notes')
await click('Ideen Garten')
await shot('notizen')
await go('files')
await click('Dokumente')
await click('Öffnen')
await pause(800)
await shot('dateien')

// ---------- Einstellungen und Verwaltung ----------
for (const [hash, name] of [
  ['settings/accounts', 'einstellungen-konten'], ['settings/language', 'einstellungen-sprache'], ['settings/appearance', 'einstellungen-darstellung'],
  ['settings/categories', 'einstellungen-kategorien'], ['settings/rules', 'einstellungen-regeln'], ['settings/junk', 'einstellungen-junk'],
  ['settings/devices', 'einstellungen-geraete'], ['settings/security', 'einstellungen-sicherheit'], ['settings/notes', 'einstellungen-notizi'],
  ['settings/stats', 'einstellungen-speicher'], ['admin', 'verwaltung'],
]) {
  await go(hash)
  await shot(name)
}

// Anmeldung zuletzt (meldet ab)
await page.evaluate(() => fetch('/api/auth/logout', { method: 'POST' }))
await go('mail')
await shot('anmeldung')

await browser.close()
