#!/usr/bin/env node
// release-strip: ignore
// Hilfe beim Nachmarkieren (docs/plugins-konzept.md 8.6): einen Commit, der auf einem Release-Zweig eine Funktion von Hand
// entfernt hat, als Marker [feature:<kennung>] auf den heutigen Stand übertragen.
//
//   node tools/strip-port.mjs <commit> <kennung> [--dry-run] [pfad …]
//
// Je Änderungsblock des Commits: die gelöschten Zeilen im heutigen Stand suchen (genau einmal) und mit Markern umschließen;
// hinzugefügte Zeilen kommen auskommentiert in den else-Teil. Ganz gelöschte Dateien gehören in features.parts.json (Ausgabe am
// Ende). Was nicht eindeutig gefunden wird, steht im Bericht und wird von Hand nachgezogen. TSX: Blöcke, die mit „<“ oder „{“
// beginnen, bekommen JSX-Kommentare – immer im Diff gegenlesen. JSON hat keine Kommentare und wird nur gemeldet.
import { execFileSync } from 'node:child_process'
import { existsSync, readFileSync, writeFileSync } from 'node:fs'
import { extname, join } from 'node:path'

const args = process.argv.slice(2)
const dry = args.includes('--dry-run')
const [commit, id, ...paths] = args.filter(a => a !== '--dry-run')
if (!commit || !/^[a-z][a-z0-9]*$/.test(id ?? '')) { console.error('Aufruf: strip-port.mjs <commit> <kennung> [--dry-run] [pfad …]'); process.exit(2) }
const repo = execFileSync('git', ['rev-parse', '--show-toplevel'], { encoding: 'utf8' }).trim()
const git = a => execFileSync('git', a, { cwd: repo, encoding: 'utf8', maxBuffer: 1 << 28 })

const deleted = git(['show', '--format=', '--diff-filter=D', '--name-only', commit, '--', ...paths]).split('\n').filter(Boolean)
const diff = git(['show', '--format=', '--diff-filter=M', '-U3', '--no-color', commit, '--', ...paths])

const style = (file, first) => {
  const ext = extname(file).toLowerCase()
  if (['.md', '.xml', '.html', '.csproj', '.props'].includes(ext)) return { o: '<!-- ', c: ' -->' }
  if (['.py', '.yml', '.yaml', '.sh', '.ps1'].includes(ext) || /Dockerfile$/.test(file)) return { o: '# ', c: '' }
  // JSX nur bei „<“ oder „{“ direkt vor einem Ausdruck; „{ “ mit Leerzeichen ist ein Objekt (Array-Eintrag) und bekommt //
  if (ext === '.tsx' && /^\s*(<|\{[^\s/]|\)\}|\}\))/.test(first ?? '')) return { o: '{/* ', c: ' */}' }
  if (['.cs', '.ts', '.tsx', '.js', '.mjs', '.kt', '.kts', '.java', '.css'].includes(ext)) return ext === '.css' ? { o: '/* ', c: ' */' } : { o: '// ', c: '' }
  return null
}

// Diff in Dateien und Änderungsblöcke zerlegen: { file, blocks: [{ before: [Kontext], minus: [], plus: [] }] }
const files = []
let cur = null, blk = null, ctx = []
for (const line of diff.split('\n')) {
  if (line.startsWith('diff --git ')) { cur = { file: line.split(' b/')[1], blocks: [] }; files.push(cur); blk = null; ctx = []; continue }
  if (!cur || line.startsWith('index ') || line.startsWith('--- ') || line.startsWith('+++ ')) continue
  if (line.startsWith('@@')) { blk = null; ctx = []; continue }
  if (line.startsWith('-') || line.startsWith('+')) {
    if (!blk) { blk = { before: ctx.slice(-3), minus: [], plus: [] }; cur.blocks.push(blk) }
    ;(line[0] === '-' ? blk.minus : blk.plus).push(line.slice(1))
  } else if (line.startsWith(' ') || line === '') { blk = null; ctx.push(line.slice(1)) }
}

const report = []
for (const f of files) {
  const full = join(repo, f.file)
  if (!existsSync(full)) { report.push(`${f.file}: gibt es nicht mehr`); continue }
  if (/\.json$/i.test(f.file)) { report.push(`${f.file}: JSON, von Hand (${f.blocks.length} Blöcke)`); continue }
  let text = readFileSync(full, 'utf8')
  const eol = text.includes('\r\n') ? '\r\n' : '\n'
  const lines = text.split(/\r?\n/)
  const norm = s => s.replace(/\s+$/, '')
  // Von hinten nach vorn einfügen, damit Zeilennummern stimmen: erst alle Stellen finden
  const edits = []
  for (const b of f.blocks) {
    const st = style(f.file, b.minus[0] ?? b.plus[0])
    if (!st) { report.push(`${f.file}: Dateiart ohne Marker`); break }
    let at = -1
    if (b.minus.length) {
      const hits = []
      for (let i = 0; i + b.minus.length <= lines.length; i++)
        if (b.minus.every((m, k) => norm(lines[i + k]) === norm(m))) hits.push(i)
      if (hits.length !== 1) { report.push(`${f.file}: ${hits.length}x gefunden: ${b.minus[0].trim().slice(0, 90)}`); continue }
      at = hits[0]
    } else {
      // reine Ergänzung im Release: hinter den Kontext
      const c = b.before.filter(x => x.trim())
      if (!c.length) { report.push(`${f.file}: Ergänzung ohne Kontext`); continue }
      const hits = []
      for (let i = 0; i + c.length <= lines.length; i++) if (c.every((m, k) => norm(lines[i + k]) === norm(m))) hits.push(i + c.length)
      if (hits.length !== 1) { report.push(`${f.file}: Ergänzung, Kontext ${hits.length}x: ${c.at(-1).trim().slice(0, 80)}`); continue }
      at = hits[0]
    }
    if (edits.some(e => at < e.at + e.len && e.at < at + Math.max(b.minus.length, 1))) { report.push(`${f.file}: überlappt bei Zeile ${at + 1}`); continue }
    edits.push({ at, len: b.minus.length, plus: b.plus, st })
  }
  edits.sort((a, b) => b.at - a.at)
  for (const e of edits) {
    const ind = (lines[e.at] ?? e.plus[0] ?? '').match(/^\s*/)[0]
    const m = s => `${ind}${e.st.o}${s}${e.st.c}`
    const block = [m(`[feature:${id}]`), ...lines.slice(e.at, e.at + e.len)]
    if (e.plus.length) {
      block.push(m(`[feature:${id}:else]`))
      // Ein Kommentar lässt sich nicht in einen Kommentar derselben Art legen (XML: <!-- in <!-- … -->): stattdessen melden
      if (e.st.c && e.plus.some(p => p.includes(e.st.o.trim()) || p.includes(e.st.c.trim()))) { report.push(`${f.file}:${e.at + 1}: Ersatz ist selbst ein Kommentar – von Hand`); continue }
      for (const p of e.plus) { const pi = p.match(/^\s*/)[0]; block.push(p.trim() ? `${pi}${e.st.o}${p.trim()}${e.st.c}` : '') }
    }
    block.push(m(`[/feature:${id}]`))
    lines.splice(e.at, e.len, ...block)
  }
  if (edits.length) {
    if (!dry) writeFileSync(full, lines.join(eol))
    console.log(`${f.file}: ${edits.length} Blöcke markiert`)
  }
}
if (deleted.length) console.log(`\nGanz gelöscht (features.parts.json, „files“ von ${id}):\n` + deleted.map(d => '  ' + d).join('\n'))
if (report.length) console.log('\nVon Hand:\n' + report.map(r => '  ' + r).join('\n'))
