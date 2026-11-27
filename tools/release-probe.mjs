#!/usr/bin/env node
// release-strip: ignore
// Release-Probe auf v2 (docs/plugins-konzept.md 8.4): den aktuellen Stand (mit nicht committeten und neuen Dateien) in einen
// Wegwerf-Arbeitsbaum legen, dort release-strip für die nächste Version laufen lassen und prüfen, was übrig bleibt:
//   - Verweise auf entfernte Kennungen außerhalb von Markern (Features.On, featureOn, Konstanten, Namen aus features.parts.json);
//     nicht in apps/ (die App enthält alle Module, docs/plugins-konzept.md 4.4) und tools/, Ausnahmen je Kennung unter „allow“
//   - mit --build: Server bauen (dotnet build), Web prüfen (tsc --noEmit)
// Ein fehlender Marker fällt so beim Entwickeln auf, nicht erst beim Schnitt.
//
//   node tools/release-probe.mjs [X.Y] [--build] [--keep] [--only id,id] [--dir <ordner>]
//
// X.Y: Version des nächsten Schnitts, sonst die höchste aus releases.json. Arbeitsbaum unter /mnt/user, damit dotnet im
// Container ihn sieht (Vorgabe: ../.release-probe neben dem Repo).
import { execFileSync, spawnSync } from 'node:child_process'
import { existsSync, mkdtempSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs'
import { dirname, join, resolve } from 'node:path'
import { run as strip } from './release-strip.mjs'

const args = process.argv.slice(2)
const opt = n => { const i = args.indexOf(n); return i >= 0 ? args.splice(i, 2)[1] : null }
const has = n => { const i = args.indexOf(n); return i >= 0 && !!args.splice(i, 1) }
const only = opt('--only')?.split(',')
const dirArg = opt('--dir')
const build = has('--build'), keep = has('--keep')
const git = (cwd, a, env) => execFileSync('git', a, { cwd, encoding: 'utf8', env: { ...process.env, ...env }, maxBuffer: 1 << 28, stdio: ['ignore', 'pipe', 'pipe'] }).trim()
const repo = git(process.cwd(), ['rev-parse', '--show-toplevel'])
const releases = JSON.parse(readFileSync(join(repo, 'releases.json'), 'utf8'))
const release = args.find(a => /^\d+\.\d+$/.test(a)) ?? Object.keys(releases).sort((a, b) => a.localeCompare(b, 'en', { numeric: true })).at(-1)

// Stand mit allen Änderungen als Commit, ohne den echten Index anzufassen
const base = join(dirArg ? resolve(dirArg) : join(dirname(repo), '.release-probe'))
const tmpIndex = resolve(repo, git(repo, ['rev-parse', '--git-path', 'release-probe-index'])) // auch in einem git worktree (.git ist dort eine Datei)
git(repo, ['read-tree', 'HEAD'], { GIT_INDEX_FILE: tmpIndex })
git(repo, ['add', '-A'], { GIT_INDEX_FILE: tmpIndex })
const tree = git(repo, ['write-tree'], { GIT_INDEX_FILE: tmpIndex })
rmSync(tmpIndex, { force: true })
const commit = git(repo, ['commit-tree', tree, '-p', 'HEAD', '-m', 'release-probe'], { GIT_AUTHOR_NAME: 'probe', GIT_AUTHOR_EMAIL: 'probe@local', GIT_COMMITTER_NAME: 'probe', GIT_COMMITTER_EMAIL: 'probe@local' })
if (existsSync(base)) { try { git(repo, ['worktree', 'remove', '--force', base]) } catch { rmSync(base, { recursive: true, force: true }) } }
git(repo, ['worktree', 'prune'])
git(repo, ['worktree', 'add', '--detach', base, commit])

let problems = 0
const log = s => console.log(s)
try {
  // Erst den unveränderten Stand bauen: Marker in else-Teilen können v2 selbst brechen (etwa ein Kommentar im Kommentar),
  // was der geschnittene Stand nicht zeigt
  if (build) {
    log('Bau des unveränderten Stands …')
    const d0 = spawnSync('dotnet', ['build', 'DmOffice.Tests/DmOffice.Tests.csproj', '-c', 'Debug', '-nologo', '-v', 'q'], { cwd: base, encoding: 'utf8', maxBuffer: 1 << 28 })
    const e0 = (d0.stdout + d0.stderr).split('\n').filter(l => / error /.test(l)).map(l => l.replace(base + '/', ''))
    log(e0.length ? [...new Set(e0)].slice(0, 20).map(l => '  ' + l).join('\n') : '  baut.')
    problems += e0.length
  }
  const r = strip({ root: base, release, only, log })
  problems += r.errors.length
  const ids = new Set(r.ids)

  // Übrig gebliebene Verweise
  const fcs = readFileSync(join(base, 'DmOffice.Server', 'Core', 'Features.cs'), 'utf8')
  const consts = Object.fromEntries([...fcs.matchAll(/\b([A-Z][A-Za-z0-9]*)\s*=\s*"([a-z0-9]+)"/g)].map(m => [m[2], m[1]]))
  const parts = existsSync(join(repo, 'features.parts.json')) ? JSON.parse(readFileSync(join(repo, 'features.parts.json'), 'utf8')) : {}
  const patterns = []
  for (const id of ids) {
    patterns.push([id, `featureOn\\(['"]${id}['"]`], [id, `Features\\.On\\("${id}"`], [id, `["']${id}["']\\s*(in|\\)|,)`])
    if (consts[id]) patterns.push([id, `Features\\.${consts[id]}\\b`])
    for (const n of parts[id]?.names ?? []) patterns.push([id, n.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')])
  }
  const found = new Map()
  if (patterns.length) {
    const rx = patterns.map(p => p[1]).join('|')
    const out = spawnSync('git', ['grep', '-n', '-I', '-E', rx, '--', '.', ':!tools/', ':!apps/', ':!docs/', ':!*.md', ':!KOORDINATION.md', ':!features.parts.json', ':!releases.json', ':!DmOffice.Server/features.json'],
      { cwd: base, encoding: 'utf8', maxBuffer: 1 << 28 }).stdout
    for (const line of out.split('\n').filter(Boolean)) {
      const [id] = patterns.find(p => new RegExp(p[1]).test(line)) ?? ['?']
      if (/NotIncluded\s*=/.test(line)) continue
      // Plugins, die für die Version nicht gelistet sind, entfernt release-cut ganz: ihre Verweise sind keine Befunde
      const plugin = /^plugins\/([^/]+)\//.exec(line)?.[1]
      if (plugin && !(plugin in (releases[release]?.community ?? {}))) continue
      // Ausnahmen je Kennung (features.parts.json „allow“): Dateien, in denen das Wort etwas anderes meint
      if ((parts[id]?.allow ?? []).some(a => line.startsWith(a + ':'))) continue
      if (!found.has(id)) found.set(id, [])
      found.get(id).push(line.length > 220 ? line.slice(0, 220) + '…' : line)
    }
  }
  if (found.size) {
    log(`\nÜbrig gebliebene Verweise auf entfernte Kennungen (Marker fehlen) – ${[...found.values()].flat().length} Stellen:`)
    for (const [id, lines] of [...found].sort()) {
      log(`  ${id} (${lines.length}):`)
      for (const l of lines.slice(0, 12)) log(`    ${l}`)
      if (lines.length > 12) log(`    … ${lines.length - 12} weitere`)
    }
    problems += found.size
  } else log('\nKeine übrig gebliebenen Verweise.')

  if (build) {
    log('\nBau von Server und Tests …')
    const d = spawnSync('dotnet', ['build', 'DmOffice.Tests/DmOffice.Tests.csproj', '-c', 'Debug', '-nologo', '-v', 'q', '-p:TreatWarningsAsErrors=false'], { cwd: base, encoding: 'utf8', maxBuffer: 1 << 28 })
    const errs = (d.stdout + d.stderr).split('\n').filter(l => / error /.test(l)).map(l => l.replace(base + '/', ''))
    log(errs.length ? [...new Set(errs)].slice(0, 40).map(l => '  ' + l).join('\n') : '  Server und Tests bauen.')
    problems += errs.length
    log('Typprüfung Web …')
    if (!existsSync(join(base, 'web', 'node_modules'))) symlinkSync(join(repo, 'web', 'node_modules'), join(base, 'web', 'node_modules'))
    const t = spawnSync('npx', ['tsc', '--noEmit'], { cwd: join(base, 'web'), encoding: 'utf8', maxBuffer: 1 << 28 })
    const terrs = t.stdout.split('\n').filter(l => /error TS/.test(l))
    log(terrs.length ? terrs.slice(0, 40).map(l => '  ' + l).join('\n') : '  Web ohne Typfehler.')
    problems += terrs.length
  }
} finally {
  if (!keep) { try { git(repo, ['worktree', 'remove', '--force', base]) } catch { /* egal */ } }
  else log(`\nArbeitsbaum bleibt: ${base}`)
}
log(problems ? `\nRelease-Probe ${release}: ${problems} Befunde.` : `\nRelease-Probe ${release}: sauber.`)
process.exit(problems ? 1 : 0)
