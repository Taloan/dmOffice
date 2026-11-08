// Liste der Fremdbestandteile (Open-Source-Bibliotheken) mit Lizenz, Projektseite und vollem Lizenztext - ohne Netzwerk.
// Quellen: web/package-lock.json + web/node_modules (nur ausgelieferte Pakete, ohne devDependencies und @types),
// DmOffice.Server/obj/project.assets.json + NuGet-Paketordner (nach `dotnet restore`), die von Gradle erzeugte App-Liste
// (apps/mobile/app/src/main/assets/third-party.json, Task ossLizenzen) und tools/lizenzen/zusatz.json (Programme im
// Docker-Image, angebundene Dienste, Formate, Daten). Fehlt eine Quelle (z. B. im Docker-Bau ohne NuGet), bleibt der
// Abschnitt aus der vorhandenen Liste stehen.
// Ergebnis: web/public/third-party.json (Seite „Open-Source-Bibliotheken“) und THIRD-PARTY-NOTICES(.de).md.
//   node tools/lizenzen/erzeugen.mjs
import { createHash } from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const ROOT = path.resolve(HERE, '../..')
const WEB = path.join(ROOT, 'web')
const SERVER = path.join(ROOT, 'DmOffice.Server')
const OUT = path.join(WEB, 'public', 'third-party.json')
const APP = path.join(ROOT, 'apps', 'mobile', 'app', 'src', 'main', 'assets', 'third-party.json')

const readJson = f => JSON.parse(fs.readFileSync(f, 'utf8').replace(/^﻿/, ''))
const exists = f => { try { fs.accessSync(f); return true } catch { return false } }
const zusatz = readJson(path.join(HERE, 'zusatz.json'))
const old = exists(OUT) ? readJson(OUT) : { sections: [], texts: {} }

// Lizenztexte: je Text ein Schlüssel; Standardtexte (tools/lizenzen/texte/<SPDX>.txt) unter ihrer SPDX-Kennung,
// Texte aus den Paketen (mit Urhebervermerk) unter einem Hash - gleiche Texte nur einmal. Die Standardtexte stammen aus
// https://github.com/spdx/license-list-data (text/), ohne -only/-or-later im Namen, weil der Text derselbe ist.
const texts = {}
const norm = s => s.replace(/^﻿/, '').replace(/\r\n?/g, '\n').replace(/[ \t]+$/gm, '').trim()
function ownText(s) {
  const t = norm(s)
  const key = 'h' + createHash('sha256').update(t).digest('hex').slice(0, 12)
  texts[key] = t
  return key
}
function spdxTexts(expr) {
  const keys = []
  for (const id of (expr || '').replace(/[()]/g, ' ').split(/\s+(?:OR|AND|WITH)\s+|\s+/).filter(Boolean)) {
    const base = id.replace(/(-only|-or-later|\+)$/, '')
    const f = path.join(HERE, 'texte', base + '.txt')
    if (exists(f)) { texts[base] ??= norm(fs.readFileSync(f, 'utf8')); keys.push(base) }
  }
  return keys
}
// LICENSE, COPYING, NOTICE (Apache-2.0 verlangt dessen Weitergabe) und THIRD-PARTY-NOTICES (SkiaSharp, .NET)
const LICENSE_FILE = /^((licen[sc]e|copying|notice)([.-].*)?|third-party-notices\.txt)$/i
/** Lizenzdateien eines Pakets; eine Zeile nur mit dem Paketnamen (Fluent UI) entfällt, damit gleiche Texte zusammenfallen. */
function fileTexts(dir, name) {
  if (!exists(dir)) return []
  return fs.readdirSync(dir).filter(n => LICENSE_FILE.test(n) && !/\.(js|ts|json|html|png)$/i.test(n)).sort()
    .map(n => path.join(dir, n)).filter(f => fs.statSync(f).isFile())
    .map(f => ownText(fs.readFileSync(f, 'utf8').split(/\r?\n/).filter(l => !name || l.trim() !== name).join('\n')))
}

const cleanUrl = u => {
  if (!u) return undefined
  u = (typeof u === 'string' ? u : u.url || '').trim()
  u = u.replace(/^git\+/, '').replace(/^git:\/\//, 'https://').replace(/^ssh:\/\/git@/, 'https://').replace(/^git@github\.com:/, 'https://github.com/')
  u = u.replace(/\.git$/, '').replace(/^github:/, 'https://github.com/').replace(/#.*$/, '')
  if (/^[\w.-]+\/[\w.-]+$/.test(u)) u = 'https://github.com/' + u
  return /^https?:\/\//.test(u) ? u : undefined
}

/** Pakete eines Projekts (gleiche Quelle und Lizenz) zu einem Eintrag zusammenfassen. */
function group(pkgs, direct) {
  const by = new Map()
  for (const p of pkgs) {
    const key = (p.url || p.name).toLowerCase().replace(/\/(tree|blob)\/.*$/, '') + '|' + p.license
    by.has(key) ? by.get(key).push(p) : by.set(key, [p])
  }
  const out = []
  for (const list of by.values()) {
    list.sort((a, b) => a.name.localeCompare(b.name))
    const first = list.find(p => direct.has(p.name)) || list[0]
    const scope = list.every(p => p.name.startsWith('@') && p.name.split('/')[0] === first.name.split('/')[0]) ? first.name.split('/')[0] : null
    const name = list.length === 1 ? first.name : scope && !direct.has(first.name) ? scope + '/*' : first.name
    const versions = [...new Set(list.map(p => p.version))]
    out.push({
      name, version: first.version + (versions.length > 1 ? ' …' : ''), license: first.license, url: first.url,
      ...(first.author ? { author: first.author } : {}),
      ...(list.length > 1 ? { packages: list.map(p => p.name + ' ' + p.version) } : {}),
      texts: [...new Set(list.flatMap(p => p.texts))],
    })
  }
  return out.sort((a, b) => a.name.replace(/^@/, '').localeCompare(b.name.replace(/^@/, ''), 'en', { sensitivity: 'base' }))
}

// --- Web (npm) ---
function npmSection() {
  const lockFile = path.join(WEB, 'package-lock.json')
  if (!exists(lockFile) || !exists(path.join(WEB, 'node_modules'))) return null
  const lock = readJson(lockFile)
  const direct = new Set(Object.keys(readJson(path.join(WEB, 'package.json')).dependencies || {}))
  // Nur, was von den Abhängigkeiten aus erreichbar ist (ohne @types: Typen werden nicht ausgeliefert), aufgelöst wie Node
  const reach = new Set()
  const find = (from, dep) => {
    for (let base = from; ; base = base.slice(0, Math.max(0, base.lastIndexOf('node_modules/') - 1))) {
      const key = (base ? base + '/' : '') + 'node_modules/' + dep
      if (lock.packages[key]) return key
      if (!base) return null
    }
  }
  const walk = (from, dep) => {
    if (dep.startsWith('@types/')) return
    const key = find(from, dep)
    if (!key || reach.has(key)) return
    reach.add(key)
    const v = lock.packages[key]
    for (const d of Object.keys({ ...v.dependencies, ...v.optionalDependencies, ...v.peerDependencies })) walk(key, d)
  }
  for (const d of direct) walk('', d)
  const pkgs = []
  for (const key of [...reach].sort()) {
    const v = lock.packages[key]
    const name = key.slice(key.lastIndexOf('node_modules/') + 'node_modules/'.length)
    const dir = path.join(WEB, key)
    const meta = exists(path.join(dir, 'package.json')) ? readJson(path.join(dir, 'package.json')) : {}
    if (!exists(dir)) { if (v.optional) continue; throw new Error(`${name}: fehlt in node_modules (npm ci)`) }
    const license = v.license || meta.license || 'UNKNOWN'
    const own = fileTexts(dir, name)
    const author = typeof meta.author === 'string' ? meta.author.replace(/\s*[<(].*$/, '') : meta.author?.name
    pkgs.push({ name, version: v.version, license, url: cleanUrl(meta.repository) || cleanUrl(meta.homepage), author, texts: own.length ? own : spdxTexts(license) })
  }
  return group(pkgs, direct)
}

// --- Server (NuGet) ---
function nuspecField(xml, tag) {
  const m = xml.match(new RegExp(`<${tag}(\\s[^>]*)?>([\\s\\S]*?)</${tag}>`))
  return m ? { attrs: m[1] || '', value: m[2].trim() } : null
}
function nugetSection() {
  const assetsFile = path.join(SERVER, 'obj', 'project.assets.json')
  if (!exists(assetsFile)) return null
  const assets = readJson(assetsFile)
  const folders = Object.keys(assets.packageFolders || {}).concat(path.join(os.homedir(), '.nuget', 'packages'))
  const used = new Set(Object.values(assets.targets).flatMap(t => Object.entries(t).filter(([, v]) => v.type === 'package').map(([k]) => k)))
  const direct = new Set(assets.project?.frameworks ? Object.values(assets.project.frameworks).flatMap(f => Object.keys(f.dependencies || {})) : [])
  const pkgs = []
  for (const [key, lib] of Object.entries(assets.libraries)) {
    if (lib.type !== 'package' || !used.has(key)) continue
    const [name, version] = key.split('/')
    const dir = folders.map(f => path.join(f, lib.path)).find(exists)
    if (!dir) throw new Error(`${key}: nicht im NuGet-Paketordner (dotnet restore)`)
    const spec = fs.readdirSync(dir).find(n => n.endsWith('.nuspec'))
    const xml = spec ? fs.readFileSync(path.join(dir, spec), 'utf8') : ''
    const lic = nuspecField(xml, 'license')
    const licUrl = nuspecField(xml, 'licenseUrl')?.value
    let license = 'UNKNOWN', own = []
    if (lic && /type="expression"/.test(lic.attrs)) license = lic.value
    else if (lic && /type="file"/.test(lic.attrs)) { license = 'SEE-LICENSE-FILE'; const f = path.join(dir, lic.value); if (exists(f)) own = [ownText(fs.readFileSync(f, 'utf8'))] }
    else if (licUrl) {
      const m = licUrl.match(/licenses\.nuget\.org\/(.+)$/)
      license = m ? decodeURIComponent(m[1]) : /dotnet|aspnet|microsoft/i.test(licUrl) && /mit/i.test(licUrl) ? 'MIT' : 'SEE ' + licUrl
    }
    const fix = zusatz.overrides?.[name]
    if (fix) { license = fix.license; if (fix.text === false) own = [] }
    if (!own.length) own = fileTexts(dir).filter(k => texts[k].length > 40)
    const repo = xml.match(/<repository\s[^>]*url="([^"]+)"/)?.[1]
    const url = cleanUrl(repo) || cleanUrl(nuspecField(xml, 'projectUrl')?.value)
    const author = nuspecField(xml, 'authors')?.value
    pkgs.push({ name, version, license, url, author, texts: own.length ? own : spdxTexts(license) })
  }
  return group(pkgs, direct)
}

// --- App (Gradle) und Zusatz ---
function appSection() {
  if (!exists(APP)) return null
  const app = readJson(APP)
  for (const [k, v] of Object.entries(app.texts || {})) texts[k] = v
  return app.sections.find(s => s.id === 'app')?.entries || []
}
function extra(id) {
  return (zusatz[id] || []).map(e => ({ ...e, texts: e.texts || spdxTexts(e.license) }))
}

function keep(id) {
  const s = old.sections.find(x => x.id === id)
  if (!s) return []
  for (const e of s.entries) for (const k of e.texts || []) if (old.texts[k]) texts[k] = old.texts[k]
  console.warn(`${id}: Quelle fehlt, Abschnitt aus der vorhandenen Liste übernommen`)
  const added = new Set((zusatz[id] || []).map(e => e.name))
  return s.entries.filter(e => !added.has(e.name))
}

const sections = [
  { id: 'web', entries: [...(npmSection() ?? keep('web')), ...extra('web')] },
  { id: 'server', entries: [...(nugetSection() ?? keep('server')), ...extra('server')] },
  { id: 'app', entries: appSection() ?? keep('app') },
  { id: 'image', entries: extra('image') },
  { id: 'services', entries: extra('services') },
  { id: 'specs', entries: extra('specs') },
  { id: 'own', entries: extra('own') },
]
const usedTexts = Object.fromEntries([...new Set(sections.flatMap(s => s.entries.flatMap(e => e.texts || [])))].sort().map(k => [k, texts[k]]))
const result = { sections, texts: usedTexts }
const json = JSON.stringify(result, null, 1) + '\n'
if (!exists(OUT) || fs.readFileSync(OUT, 'utf8') !== json) fs.writeFileSync(OUT, json)

// --- THIRD-PARTY-NOTICES.md (nur im Repository, nicht im Docker-Bau der Oberfläche) ---
if (exists(path.join(ROOT, 'LICENSE'))) {
  const L = zusatz.markdown
  for (const lang of ['en', 'de']) {
    const m = L[lang]
    const esc = s => String(s ?? '').replace(/\|/g, '\\|')
    let md = `# ${m.title}\n\n${lang === 'en' ? '[Deutsch](THIRD-PARTY-NOTICES.de.md) · **English**' : '**Deutsch** · [English](THIRD-PARTY-NOTICES.md)'}\n\n${m.intro}\n`
    for (const s of sections) {
      if (!s.entries.length) continue
      md += `\n## ${m.sections[s.id]}\n\n`
      if (m.notes[s.id]) md += m.notes[s.id] + '\n\n'
      md += `| ${m.cols.join(' | ')} |\n|---|---|---|---|\n`
      for (const e of s.entries) {
        const note = e.note?.[lang] ? ` – ${e.note[lang]}` : ''
        const count = e.packages ? ` (${e.packages.length} ${m.packages})` : ''
        md += `| ${esc(e.name)}${count}${esc(note)} | ${esc(e.version || '–')} | ${esc(e.license)} | ${e.url || '–'} |\n`
      }
    }
    md += `\n## ${m.compatTitle}\n\n${m.compat}\n`
    const file = path.join(ROOT, lang === 'en' ? 'THIRD-PARTY-NOTICES.md' : 'THIRD-PARTY-NOTICES.de.md')
    if (!exists(file) || fs.readFileSync(file, 'utf8') !== md) fs.writeFileSync(file, md)
  }
}

const unknown = sections.flatMap(s => s.entries.filter(e => e.license !== '–' && (/UNKNOWN|SEE/.test(e.license) || !(e.texts || []).length)).map(e => `${s.id}: ${e.name} (${e.license})`))
console.log(`third-party.json: ${sections.map(s => `${s.id} ${s.entries.length}`).join(', ')}, ${Object.keys(usedTexts).length} Lizenztexte`)
if (unknown.length) console.warn('Ohne eindeutige Lizenz bzw. ohne Lizenztext:\n  ' + unknown.join('\n  '))
