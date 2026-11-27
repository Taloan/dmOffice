import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import { indexAt, offsetsOf, rangeOf, scrollToShow } from '../src/mail/virtualList.ts'

describe('virtualList', () => {
  // Kopf 30, dann Zeilen 70
  const o = offsetsOf(101, i => i === 0 ? 30 : 70)
  it('Anfänge und Gesamthöhe', () => {
    assert.deepEqual(o.slice(0, 4), [0, 30, 100, 170])
    assert.equal(o[101], 30 + 100 * 70)
  })
  it('Zeile an einer Höhe', () => {
    assert.equal(indexAt(o, 0), 0)
    assert.equal(indexAt(o, 29), 0)
    assert.equal(indexAt(o, 30), 1)
    assert.equal(indexAt(o, 99999), 100)
    assert.equal(indexAt([0], 10), 0)
  })
  it('Ausschnitt mit Rand, am Anfang und Ende begrenzt', () => {
    assert.deepEqual(rangeOf(o, 0, 700, 0), [0, 11])
    assert.deepEqual(rangeOf(o, 3530, 700, 140), [49, 64])
    assert.deepEqual(rangeOf(o, 99999, 700, 140), [100, 101])
    assert.deepEqual(rangeOf([0], 0, 700, 100), [0, 0])
  })
  it('Mitscrollen nur, wenn die Zeile nicht ganz sichtbar ist', () => {
    assert.equal(scrollToShow(o, 2, 0, 700), null)
    assert.equal(scrollToShow(o, 20, 0, 700), o[21] - 700)
    assert.equal(scrollToShow(o, 1, 500, 700), 30)
  })
})
