// node --experimental-strip-types --test tests/   (npm test)
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { DEFAULT_TILE_HEAD, insertAt, renderTile, tileHeadOf } from '../src/files/tileTemplate.ts'

test('Präfix und Suffix nur mit Wert', () => {
  assert.equal(renderTile('{"f"blende} {bel-zeit"s"} {brennweite"mm"}', { blende: '2,8', 'bel-zeit': '1/250', brennweite: '35' }), 'f2,8 1/250s 35mm')
  assert.equal(renderTile('{"f"blende} {bel-zeit"s"} {brennweite"mm"}', { brennweite: '35' }), '35mm')
  assert.equal(renderTile('{"ISO "iso}', {}), '')
})

test('Leerraum durch leere Felder fällt weg, Text bleibt', () => {
  assert.equal(renderTile('  {titel}   –   {kamera}  ', { kamera: 'X-T5' }), '– X-T5')
  assert.equal(renderTile('Foto: {titel}', { titel: 'Strand' }), 'Foto: Strand')
})

test('Unbekannte Felder bleiben als Text stehen', () => {
  assert.equal(renderTile('{stadt} {titel}', { titel: 'A' }), '{stadt} A')
  assert.equal(renderTile('{ titel }', { titel: 'A' }), '{ titel }')
})

test('Vorgabe entspricht dem festen Kopf', () => {
  const v = { titel: 'T', 'aufn-datum': '01.02.2026', beschreibung: 'B', 'aufn-zeit': '09:05', blende: '2,8', 'bel-zeit': '1/250', brennweite: '35', kamera: 'K', objektiv: 'O', ext: 'JPG' }
  assert.deepEqual(DEFAULT_TILE_HEAD.map(x => renderTile(x, v)), ['T', '01.02.2026', 'B', '09:05', 'f2,8 1/250s 35mm', 'K', 'O', 'JPG'])
})

test('Gespeicherte Belegung wird auf acht Vorlagen ergänzt', () => {
  assert.deepEqual(tileHeadOf(['{ext}']), ['{ext}', ...DEFAULT_TILE_HEAD.slice(1)])
  assert.deepEqual(tileHeadOf(null), [...DEFAULT_TILE_HEAD])
  assert.equal(tileHeadOf(['', 'x'])[0], '')
})

test('Einfügen an der Cursorposition, Auswahl wird ersetzt', () => {
  assert.deepEqual(insertAt('ab', 1, 1, '{x}'), { text: 'a{x}b', cursor: 4 })
  assert.deepEqual(insertAt('abcd', 1, 3, '{x}'), { text: 'a{x}d', cursor: 4 })
  assert.deepEqual(insertAt('ab', 99, 99, '{x}'), { text: 'ab{x}', cursor: 5 })
})
