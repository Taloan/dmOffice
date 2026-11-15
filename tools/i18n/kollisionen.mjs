// Prüft die englischen Texte (web/src/i18n/en/*.ts): Alle Bereiche werden in en/index.ts per Object.assign
// zusammengeführt, ein Schlüssel in mehreren Dateien mit unterschiedlicher Übersetzung gilt dann app-weit nur
// mit der letzten. Solche Schlüssel meldet das Skript. Bereichseigene Bedeutungen bekommen einen Kontext
// („dms|Sicherheit“, siehe t() in web/src/i18n/index.ts).
//   node tools/i18n/kollisionen.mjs        (Exit-Code 1 bei Kollisionen)
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

const HERE = path.dirname(fileURLToPath(import.meta.url))
const EN = path.resolve(HERE, '../../web/src/i18n/en')
process.removeAllListeners('warning')

const files = fs.readdirSync(EN).filter(f => f.endsWith('.ts') && f !== 'index.ts')
const seen = new Map() // Schlüssel -> [{ file, value }]
for (const f of files) {
  const mod = await import(pathToFileURL(path.join(EN, f)).href)
  for (const dict of Object.values(mod)) {
    if (!dict || typeof dict !== 'object') continue
    for (const [k, v] of Object.entries(dict)) {
      if (!seen.has(k)) seen.set(k, [])
      seen.get(k).push({ file: f, value: v })
    }
  }
}

let n = 0
for (const [k, list] of [...seen].sort((a, b) => a[0].localeCompare(b[0]))) {
  if (new Set(list.map(e => e.value)).size < 2) continue
  n++
  console.log(`'${k}'`)
  for (const e of list) console.log(`    ${e.file.padEnd(16)} ${JSON.stringify(e.value)}`)
}
console.log(n ? `${n} Schlüssel mit unterschiedlicher Übersetzung` : 'Keine Kollisionen')
process.exit(n ? 1 : 0)
