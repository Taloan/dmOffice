#!/usr/bin/env node
// Plugin-Ordner fürs Image zusammenstellen (Dockerfile, Stufe „plugins“): je Plugin nur, was zur Laufzeit gebraucht wird -
// plugin.json, LICENSE, lang/, gebauter Web-Teil (web/ ohne src/) und Server-Teil (server/bin/ aus dotnet publish) -, dann mit dem
// privaten Schlüssel siegeln, wenn einer übergeben wird. Ohne Schlüssel bleibt das Plugin ungesiegelt und der Kern lädt es nicht.
//
//   node tools/plugins/assemble.mjs <plugins-quelle> <server-bins> <ziel> [private.pem]
import { cpSync, existsSync, mkdirSync, readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { execFileSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const [src, bins, out, key] = process.argv.slice(2)
if (!src || !bins || !out) { console.error('Aufruf: assemble.mjs <plugins-quelle> <server-bins> <ziel> [private.pem]'); process.exit(2) }
mkdirSync(out, { recursive: true })
const seal = fileURLToPath(new URL('./seal.mjs', import.meta.url))
for (const name of existsSync(src) ? readdirSync(src).sort() : []) {
  const dir = join(src, name)
  if (!statSync(dir).isDirectory() || !existsSync(join(dir, 'plugin.json'))) continue
  const to = join(out, name)
  mkdirSync(to, { recursive: true })
  for (const f of ['plugin.json', 'LICENSE']) if (existsSync(join(dir, f))) cpSync(join(dir, f), join(to, f))
  if (existsSync(join(dir, 'lang'))) cpSync(join(dir, 'lang'), join(to, 'lang'), { recursive: true })
  if (existsSync(join(dir, 'web')))
    cpSync(join(dir, 'web'), join(to, 'web'), { recursive: true, filter: s => !s.replaceAll('\\', '/').includes(`/${name}/web/src`) })
  if (existsSync(join(bins, name))) cpSync(join(bins, name), join(to, 'server', 'bin'), { recursive: true })
  if (key && existsSync(key)) execFileSync(process.execPath, [seal, 'seal', to, key], { stdio: 'inherit' })
  else console.log(`Plugin ${name}: ohne Schlüssel nicht gesiegelt.`)
}
