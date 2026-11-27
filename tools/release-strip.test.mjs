// release-strip: ignore
// node --test tools/release-strip.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { later, stripText } from './release-strip.mjs'

const S = (...ids) => new Set(ids)

test('Versionen: über dem Release und „aus“ werden entfernt', () => {
  assert.equal(later('2.2', '2.1'), true)
  assert.equal(later('2.1', '2.1.0'), false)
  assert.equal(later('2.0.2', '2.1'), false)
  assert.equal(later('aus', '2.1'), true)
  assert.equal(later('2.10', '2.9'), true)
})

test('Block mit Kennung über dem Release fällt weg, andere Marker bleiben', () => {
  const src = 'a\n// [feature:x]\nb\n// [/feature:x]\n// [feature:y]\nc\n// [/feature:y]\nd'
  const r = stripText(src, S('x'))
  assert.equal(r.text, 'a\n// [feature:y]\nc\n// [/feature:y]\nd')
  assert.equal(r.removed, 3)
  assert.deepEqual(r.errors, [])
})

test('else-Teil wird eingesetzt, Einrückung bleibt', () => {
  const src = '  // [feature:x]\n  neu();\n  // [feature:x:else]\n  // alt();\n\n  //alt2();\n  // [/feature:x]'
  assert.equal(stripText(src, S('x')).text, '  alt();\n\n  alt2();')
  assert.equal(stripText(src, S()).text, src)
})

test('JSX, Markdown, YAML', () => {
  const jsx = '<A>\n  {/* [feature:x] */}\n  <B />\n  {/* [feature:x:else] */}\n  {/* <C /> */}\n  {/* [/feature:x] */}\n</A>'
  assert.equal(stripText(jsx, S('x')).text, '<A>\n  <C />\n</A>')
  assert.equal(stripText('t\n<!-- [feature:x] -->\nweg\n<!-- [/feature:x] -->\n', S('x')).text, 't\n')
  assert.equal(stripText('a: 1\n# [feature:x]\nb: 2\n# [/feature:x]', S('x')).text, 'a: 1')
})

test('einzeiliger Marker: Zeile fällt weg', () => {
  const src = 'A = "a",\nB = "b", // [feature:x]\n{/* nur ein Kommentar */}\nC = "c"'
  assert.equal(stripText(src, S('x')).text, 'A = "a",\n{/* nur ein Kommentar */}\nC = "c"')
  assert.equal(stripText(src, S()).text, src)
})

test('verschachtelt: innerer Block einer bleibenden Kennung im entfernten fällt mit weg', () => {
  const src = '// [feature:x]\n// [feature:y]\ni\n// [/feature:y]\n// [/feature:x]\n// [feature:y]\nj // [feature:x]\nk\n// [/feature:y]'
  assert.equal(stripText(src, S('x')).text, '// [feature:y]\nk\n// [/feature:y]')
  assert.equal(stripText(src, S('y')).text, '// [feature:x]\n// [/feature:x]')
})

test('Fehler: nicht geschlossen, falsches Ende, else nicht auskommentiert', () => {
  assert.match(stripText('// [feature:x]\na', S('x'), 'f').errors[0], /f:1: \[feature:x\] nicht geschlossen/)
  assert.match(stripText('// [feature:x]\n// [/feature:y]\n// [/feature:x]', S(), 'f').errors[0], /ohne passenden Anfang/)
  assert.match(stripText('// [feature:x]\n// [feature:x:else]\nalt();\n// [/feature:x]', S('x'), 'f').errors[0], /nicht auskommentiert/)
})

test('Zeilenende CRLF bleibt erhalten', () => {
  assert.equal(stripText('a\r\n// [feature:x]\r\nb\r\n// [/feature:x]\r\nc', S('x')).text, 'a\r\nc')
})
