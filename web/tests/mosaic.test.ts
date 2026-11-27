import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import { estimateHeight, mosaicRows, ratioOf, rowsHeight } from '../src/files/mosaic.ts'

describe('mosaic', () => {
  it('Ratio mit Vorgabe und Grenzen', () => {
    assert.ok(Math.abs(ratioOf(3000, 2000) - 1.5) < 1e-9)
    assert.equal(ratioOf(null, null), 1.5)
    assert.ok(Math.abs(ratioOf(undefined, undefined, true) - 16 / 9) < 1e-9)
    assert.equal(ratioOf(10000, 1000), 3)
    assert.equal(ratioOf(1000, 10000), 0.5)
  })
  it('jede gestreckte Zeile füllt die Breite genau', () => {
    const ratios = [1.5, 0.75, 1.5, 1.78, 0.67, 1.5, 1.33, 1.5, 0.8, 1.78, 1.5, 1, 1.33, 0.75, 1.5]
    for (const width of [320, 700, 1000, 1366]) {
      const rows = mosaicRows(ratios, width, 150, 4)
      assert.equal(rows.reduce((n, r) => n + r.count, 0), ratios.length)
      for (const r of rows.filter(x => x.stretched)) assert.equal(r.widths.reduce((a, b) => a + b, 0) + 4 * (r.count - 1), width)
    }
  })
  it('Höhe nahe der Zielhöhe', () => {
    const rows = mosaicRows(Array(40).fill(1.5), 1000, 150, 4)
    for (const r of rows.slice(0, -1)) assert.ok(Math.abs(r.h - 150) < 45)
  })
  it('kurze letzte Zeile bleibt auf Zielhöhe und linksbündig', () => {
    const rows = mosaicRows([1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5, 1.5], 1000, 150, 4)
    const last = rows[rows.length - 1]
    assert.equal(last.stretched, false)
    assert.equal(last.h, 150)
    assert.ok(last.widths.reduce((a, b) => a + b, 0) < 1000)
  })
  it('ein einzelnes Foto, leere Liste', () => {
    assert.deepEqual(mosaicRows([], 800, 150, 4), [])
    const one = mosaicRows([1.5], 800, 150, 4)
    assert.equal(one.length, 1)
    assert.equal(one[0].h, 150)
  })
  it('Höhen: Zeilen und Schätzung', () => {
    const rows = mosaicRows(Array(30).fill(1.5), 1000, 150, 4)
    assert.equal(rowsHeight(rows, 4), rows.reduce((s, r) => s + r.h, 0) + (rows.length - 1) * 4 + 4)
    const est = estimateHeight(30, 1000, 150, 4)
    const real = rowsHeight(rows, 4)
    assert.ok(Math.abs(est - real) / real < 0.25)
    assert.equal(estimateHeight(0, 1000, 150, 4), 0)
  })
})
