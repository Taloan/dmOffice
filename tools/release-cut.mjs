#!/usr/bin/env node
// Release-Zweig schneiden (docs/plugins-konzept.md 7.1, 6.1): Zweig release-X.Y.Z von v2 in einem eigenen Arbeitsbaum anlegen,
// Plugin-Ordner auf die Liste der Community Edition in releases.json bringen (nicht gelistete löschen, gelistete auf das Tag
// plugin/<key>/<version> setzen), requires und core prüfen, release.txt setzen und committen. Kein Push.
//
//   node tools/release-cut.mjs <X.Y.Z> <arbeitsbaum> [--from v2] [--dry-run]
//
// Danach entfernt tools/release-strip.mjs die Kern-Funktionen späterer Versionen (Marker, features.parts.json) als eigener Commit.
// Bei Fehlern im Schnitt bleibt der Arbeitsbaum zum Nachsehen stehen, nichts ist gepusht.
import { execFileSync } from 'node:child_process'
import { existsSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs'
import { join, resolve } from 'node:path'
import { run as strip } from './release-strip.mjs'

const args = process.argv.slice(2)
const flag = name => { const i = args.indexOf(name); return i >= 0 ? args.splice(i, 2)[1] : null }
const dry = args.includes('--dry-run') && args.splice(args.indexOf('--dry-run'), 1)
const from = flag('--from') ?? 'v2'
const [version, tree] = args
if (!/^\d+\.\d+\.\d+$/.test(version ?? '') || !tree) {
  console.error('Aufruf: release-cut.mjs <X.Y.Z> <arbeitsbaum> [--from v2] [--dry-run]')
  process.exit(2)
}
const release = version.split('.').slice(0, 2).join('.')
const branch = `release-${version}`
const git = (cwd, ...a) => execFileSync('git', a, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim()
const fail = msg => { console.error('Abbruch: ' + msg); process.exit(1) }
const repo = git(process.cwd(), 'rev-parse', '--show-toplevel')

// Kernbereiche wie in DmOffice.Server/Plugins/PluginHost.cs (CoreAreas)
const CORE_AREAS = ['mail', 'chat', 'pukki', 'calendar', 'contacts', 'tasks', 'notes', 'files', 'gallery', 'audio', 'dms', 'vault', 'overview']
const newer = (a, b) => { const [x, y] = [a, b].map(v => v.split('.').map(Number)); for (let i = 0; i < 2; i++) if (x[i] !== y[i]) return x[i] > y[i]; return false }

const show = path => { try { return git(repo, 'show', `${from}:${path}`) } catch { return null } }
const releases = JSON.parse(show('releases.json') ?? fail(`releases.json fehlt auf ${from}.`))
const entry = releases[release] ?? fail(`releases.json hat keinen Eintrag „${release}“.`)
const community = entry.community ?? {}
const pro = entry.pro ?? entry.enterprise ?? {}
const features = JSON.parse(show('DmOffice.Server/features.json') ?? '{}')
const later = Object.entries(features).filter(([, v]) => !/^\d+\.\d+/.test(v) || newer(v, release)).map(([k, v]) => `${k} (${v})`)

console.log(`Release ${version} von ${from}: Zweig ${branch} in ${resolve(tree)}`)
console.log(`Plugins (Community): ${Object.entries(community).map(([k, v]) => `${k} ${v}`).join(', ') || 'keine'}`)
if (Object.keys(pro).length) console.log(`Plugins (Pro, nicht im Zweig, kommen beim Pro-Bau dazu): ${Object.entries(pro).map(([k, v]) => `${k} ${v}`).join(', ')}`)
for (const [key, v] of Object.entries(community)) {
  try { git(repo, 'rev-parse', '--verify', '--quiet', `refs/tags/plugin/${key}/${v}`) } catch { fail(`Tag plugin/${key}/${v} fehlt.`) }
}
if (dry) {
  console.log(`Kennungen über ${release} (entfernt release-strip): ${later.join(', ') || 'keine'}`)
  process.exit(0)
}

if (existsSync(tree)) fail(`${tree} gibt es schon.`)
try { git(repo, 'rev-parse', '--verify', '--quiet', `refs/heads/${branch}`); fail(`Zweig ${branch} gibt es schon.`) } catch (e) { if (e.status !== 1) throw e }
git(repo, 'worktree', 'add', '-b', branch, tree, from)

const plugins = join(tree, 'plugins')
const present = existsSync(plugins) ? readdirSync(plugins).filter(n => statSync(join(plugins, n)).isDirectory()) : []
for (const name of present) {
  if (name in community) continue
  rmSync(join(plugins, name), { recursive: true, force: true })
  console.log(`Plugin ${name}: entfernt (nicht für ${release} gelistet${name in pro ? ', Pro' : ''}).`)
}
for (const [key, v] of Object.entries(community)) {
  rmSync(join(plugins, key), { recursive: true, force: true })
  git(tree, 'checkout', `plugin/${key}/${v}`, '--', `plugins/${key}`)
  const m = JSON.parse(readFileSync(join(plugins, key, 'plugin.json'), 'utf8'))
  if (m.version !== v) fail(`plugins/${key}/plugin.json nennt ${m.version}, gelistet ist ${v}.`)
  for (const r of m.requires ?? [])
    if (!CORE_AREAS.includes(r) && !(r in community) && !(r in features && !later.some(l => l.startsWith(r + ' '))))
      fail(`Plugin ${key} braucht „${r}“, das ${release} nicht enthält.`)
  console.log(`Plugin ${key}: Stand von plugin/${key}/${v}.`)
}

writeFileSync(join(tree, 'DmOffice.Server', 'release.txt'), version)
git(tree, 'add', '-A')
git(tree, 'commit', '-q', '-m', `Release ${version}: Plugins nach releases.json, release.txt ${version} [skip ci]`)
// Kern-Funktionen späterer Versionen entfernen (docs/plugins-konzept.md 8)
const r = strip({ root: resolve(tree), release })
if (r.errors.length) fail(`release-strip meldet ${r.errors.length} Fehler (Arbeitsbaum ${tree} bleibt zum Nachsehen).`)
git(tree, 'add', '-A')
git(tree, 'commit', '-q', '-m', `Release ${version}: Funktionen über ${release} entfernt (release-strip: ${r.ids.join(', ') || 'keine'}) [skip ci]`)
console.log(`Committet auf ${branch} (nicht gepusht). Noch prüfen: node tools/release-probe.mjs ist auf v2 gelaufen; im Arbeitsbaum Bau, Tests,`)
console.log(`\x60cd web && npm run lizenzen\x60 (Lizenzlisten ohne entfernte Funktionen) und die Prüfung aus docs/plugins-konzept.md 9.`)
