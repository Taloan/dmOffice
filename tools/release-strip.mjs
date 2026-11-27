#!/usr/bin/env node
// release-strip: ignore
// Kern-Funktionen späterer Versionen entfernen (docs/plugins-konzept.md 8.3). Läuft auf einem Release-Zweig oder einer Kopie von
// v2 (Release-Probe, tools/release-probe.mjs), nie auf dem Arbeitsstand von v2.
//
//   node tools/release-strip.mjs <X.Y[.Z]> [--root <ordner>] [--dry-run] [--only id,id]
//
// 1. Kennungen bestimmen: alle aus DmOffice.Server/features.json mit Version über X.Y oder ohne Version („aus“).
// 2. Dateien und Ordner aus features.parts.json („files“) dieser Kennungen löschen; „with“ nennt Kennungen, die mitgehen.
//    Form: { "<kennung>": { "files": [Pfade], "names": [Typen, Pfade für die Probe], "with": [Kennungen], "allow": [Dateien ohne Befund] } }
// 3. Markierte Blöcke entfernen, else-Teile einsetzen (Form unten), einzeilige Marker: Zeile fällt weg.
// 4. Kennungen aus features.json und features.parts.json entfernen, in Features.NotIncluded eintragen (Server meldet sie
//    weiter als aus, damit die App sie ausblendet).
// 5. Bericht: was entfernt wurde. Rückgabe ≠ 0 bei Fehlern (ungeschlossene Marker, else-Zeile nicht auskommentiert …).
//
// Marker (eigene Zeile, Kommentar der jeweiligen Sprache):
//   // [feature:x]   …   // [/feature:x]                 C#, TS, Kotlin, JS
//   // [feature:x]   …   // [feature:x:else]   // ersatz   // [/feature:x]
//   {/* [feature:x] */}   …   {/* [/feature:x] */}         JSX
//   <!-- [feature:x] -->  …   <!-- [/feature:x] -->        Markdown, XML, HTML
//   # [feature:x]   …   # [/feature:x]                     YAML, Shell, Python, PowerShell
//   code();   // [feature:x]                               einzeilig: Zeile fällt weg
// else-Teile stehen auskommentiert (je Zeile), das Skript nimmt den Kommentar weg. Verschachteln ist erlaubt.
// Dateien mit „release-strip: ignore“ in den ersten fünf Zeilen bleiben unberührt (Doku mit Beispielen, dieses Skript).
import { execFileSync } from 'node:child_process'
import { existsSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'

const TEXT = /(^|\/)Dockerfile$|\.(cs|csproj|ts|tsx|js|mjs|cjs|jsx|kt|kts|gradle|java|md|xml|html|css|json|ya?ml|sh|ps1|py|txt|props|targets|slnx)$/i
const OPEN = '(?://|#|<!--|\\{/\\*|/\\*)'
const CLOSE = '(?:\\*/\\}|-->|\\*/)?'
const ID = '([a-z][a-z0-9]*)'
const START = new RegExp(`^\\s*${OPEN}\\s*\\[feature:${ID}\\]\\s*${CLOSE}\\s*$`)
const ELSE = new RegExp(`^\\s*${OPEN}\\s*\\[feature:${ID}:else\\]\\s*${CLOSE}\\s*$`)
const END = new RegExp(`^\\s*${OPEN}\\s*\\[/feature:${ID}\\]\\s*${CLOSE}\\s*$`)
const INLINE = new RegExp(`\\S.*?\\s${OPEN}\\s*\\[feature:${ID}\\]\\s*${CLOSE}\\s*$`)
const UNCOMMENT = [/^(\s*)\{\/\*\s?(.*?)\s?\*\/\}\s*$/, /^(\s*)<!--\s?(.*?)\s?-->\s*$/, /^(\s*)\/\*\s?(.*?)\s?\*\/\s*$/, /^(\s*)\/\/\s?(.*)$/, /^(\s*)#\s?(.*)$/]

const versionKey = v => v.split('.').map(Number)
/** Liegt die Zielversion `since` über dem Release (oder ist sie keine Version, also „aus“)? */
export function later(since, release) {
  if (!/^\d+(\.\d+)*$/.test(since)) return true
  const [a, b] = [versionKey(since), versionKey(release)]
  for (let i = 0; i < Math.max(a.length, b.length); i++) {
    const x = a[i] ?? 0, y = b[i] ?? 0
    if (x !== y) return x > y
  }
  return false
}

/**
 * Text einer Datei bearbeiten: Blöcke der Kennungen in `ids` entfernen. Rückgabe { text, removed (Zeilen), errors, used (Set) }.
 * Marker anderer Kennungen bleiben unverändert stehen.
 */
export function stripText(text, ids, name = '') {
  const eol = text.includes('\r\n') ? '\r\n' : '\n'
  const lines = text.split(/\r?\n/)
  const out = [], errors = [], used = new Set()
  const stack = [] // { id, strip, inElse, line }
  let removed = 0
  const emitting = () => stack.every(s => !s.strip || s.inElse)
  lines.forEach((line, i) => {
    const at = `${name}:${i + 1}`
    let m
    if ((m = line.match(START))) {
      used.add(m[1])
      stack.push({ id: m[1], strip: ids.has(m[1]), inElse: false, line: i + 1 })
      if (ids.has(m[1])) removed++; else if (emitting()) out.push(line); else removed++
      return
    }
    if ((m = line.match(ELSE))) {
      const top = stack.at(-1)
      if (!top || top.id !== m[1]) { errors.push(`${at}: else ohne passenden Anfang [feature:${m[1]}]`); return }
      if (top.inElse) { errors.push(`${at}: zweites else für ${m[1]}`); return }
      top.inElse = true
      if (top.strip) removed++; else if (emitting()) out.push(line); else removed++
      return
    }
    if ((m = line.match(END))) {
      const top = stack.at(-1)
      if (!top || top.id !== m[1]) { errors.push(`${at}: Ende [/feature:${m[1]}] ohne passenden Anfang`); return }
      stack.pop()
      if (top.strip) removed++; else if (emitting()) out.push(line); else removed++
      return
    }
    if ((m = line.match(INLINE)) && !/^\s*(\/\/|#|<!--|\{\/\*|\/\*)/.test(line)) used.add(m[1])
    const top = stack.at(-1)
    // Zeile im else-Teil einer entfernten Kennung: Kommentar wegnehmen
    if (top && top.strip && top.inElse && stack.slice(0, -1).every(s => !s.strip || s.inElse)) {
      if (!line.trim()) { out.push(line); return }
      const u = UNCOMMENT.map(r => line.match(r)).find(Boolean)
      if (!u) { errors.push(`${at}: else-Zeile für ${top.id} ist nicht auskommentiert`); return }
      out.push(u[1] + u[2])
      return
    }
    if (!emitting()) { removed++; return }
    if (m && ids.has(m[1]) && !/^\s*(\/\/|#|<!--|\{\/\*|\/\*)/.test(line)) { removed++; return }
    // In einem else-Teil einer Kennung, die bleibt, stehen die Zeilen auskommentiert - unverändert lassen
    out.push(line)
  })
  for (const s of stack) errors.push(`${name}:${s.line}: [feature:${s.id}] nicht geschlossen`)
  return { text: out.join(eol), removed, errors, used }
}

function listFiles(root) {
  try {
    return execFileSync('git', ['ls-files', '-z'], { cwd: root, encoding: 'utf8', maxBuffer: 1 << 28 }).split('\0').filter(Boolean)
  } catch { throw new Error(`${root} ist kein Git-Arbeitsbaum.`) }
}

export function run({ root, release, dry = false, only = null, log = console.log }) {
  const featuresPath = join(root, 'DmOffice.Server', 'features.json')
  const features = JSON.parse(readFileSync(featuresPath, 'utf8'))
  const partsPath = join(root, 'features.parts.json')
  const parts = existsSync(partsPath) ? JSON.parse(readFileSync(partsPath, 'utf8')) : {}
  const ids = new Set(Object.entries(features).filter(([id, v]) => later(v, release) && (!only || only.includes(id))).map(([id]) => id))
  // Kennungen, deren Code in den Dateien einer anderen liegt, fallen mit ihr weg (features.parts.json „with“)
  for (let grew = true; grew;) { grew = false; for (const id of [...ids]) for (const w of parts[id]?.with ?? []) if (!ids.has(w)) { ids.add(w); grew = true } }
  const report = { release, ids: [...ids], deleted: [], files: [], errors: [] }

  for (const id of ids)
    for (const rel of parts[id]?.files ?? []) {
      const full = join(root, rel)
      // Bei zwei Kennungen eingetragen und schon entfernt
      if (report.deleted.some(d => rel === d || (d.endsWith('/') && rel.startsWith(d)))) continue
      // Ganzer Ordner schon weg (Plugin ohne Platz in der Edition, Ordner einer anderen Kennung): nichts mehr zu tun
      if (!existsSync(full) && !existsSync(dirname(full))) continue
      if (!existsSync(full)) { report.errors.push(`features.parts.json: ${id} nennt ${rel}, das es nicht gibt`); continue }
      report.deleted.push(rel + (statSync(full).isDirectory() ? '/' : ''))
      if (!dry) rmSync(full, { recursive: true, force: true })
    }

  for (const rel of listFiles(root)) {
    if (!TEXT.test(rel)) continue
    const full = join(root, rel)
    if (!existsSync(full) || report.deleted.some(d => rel === d || (d.endsWith('/') && rel.startsWith(d)))) continue
    const text = readFileSync(full, 'utf8')
    if (!text.includes('feature:')) continue
    if (text.split('\n', 5).some(l => l.includes('release-strip: ignore'))) continue
    const r = stripText(text, ids, rel)
    report.errors.push(...r.errors)
    if (r.removed) {
      report.files.push(`${rel} (${r.removed} Zeilen)`)
      if (!dry) writeFileSync(full, r.text)
    }
  }

  if (!dry) {
    // features.json: Zeilen der Kennungen weg (Formatierung bleibt), dann gegenlesen
    const fj = readFileSync(featuresPath, 'utf8').split('\n').filter(l => { const m = l.match(/^\s*"([^"]+)"\s*:/); return !(m && ids.has(m[1])) })
    let fjText = fj.join('\n').replace(/,(\s*\n\s*})/, '$1')
    JSON.parse(fjText)
    writeFileSync(featuresPath, fjText)
    if (existsSync(partsPath)) {
      for (const id of ids) delete parts[id]
      writeFileSync(partsPath, JSON.stringify(parts, null, 2) + '\n')
    }
    // Features.NotIncluded: entfernte Kennungen anhängen (vorhandene bleiben)
    const fcs = join(root, 'DmOffice.Server', 'Core', 'Features.cs')
    if (existsSync(fcs) && ids.size) {
      const src = readFileSync(fcs, 'utf8')
      const m = src.match(/NotIncluded\s*=\s*\[([^\]]*)\]/)
      if (!m) report.errors.push('Features.cs: NotIncluded nicht gefunden')
      else {
        const have = [...m[1].matchAll(/"([^"]+)"/g)].map(x => x[1])
        const all = [...new Set([...have, ...ids])]
        writeFileSync(fcs, src.replace(m[0], `NotIncluded = [${all.map(x => JSON.stringify(x)).join(', ')}]`))
      }
    }
  }
  log(`release-strip ${release}${dry ? ' (nur Plan)' : ''}: ${ids.size} Kennungen: ${[...ids].join(', ') || 'keine'}`)
  for (const d of report.deleted) log(`  gelöscht: ${d}`)
  for (const f of report.files) log(`  bearbeitet: ${f}`)
  for (const e of report.errors) log(`  FEHLER: ${e}`)
  return report
}

if (import.meta.url === `file://${process.argv[1]}` || process.argv[1]?.endsWith('release-strip.mjs')) {
  const args = process.argv.slice(2)
  const opt = n => { const i = args.indexOf(n); return i >= 0 ? args.splice(i, 2)[1] : null }
  const root = resolve(opt('--root') ?? process.cwd())
  const only = opt('--only')?.split(',')
  const dry = args.includes('--dry-run')
  const release = args.find(a => /^\d+\.\d+(\.\d+)?$/.test(a))
  if (!release) { console.error('Aufruf: release-strip.mjs <X.Y[.Z]> [--root <ordner>] [--dry-run] [--only id,id]'); process.exit(2) }
  const r = run({ root, release, dry, only })
  process.exit(r.errors.length ? 1 : 0)
}
