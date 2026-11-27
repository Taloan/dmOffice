import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import { fitRail, fullOrder, insertSep, moveEntry, railLayout, removeSep, SEP_PX, SLOT_PX } from '../src/shell/railsep.ts'

describe('railsep', () => {
  it('ohne gespeicherte Reihenfolge: nur die Bereiche', () => {
    assert.deepEqual(railLayout(undefined, ['mail', 'chat']), ['mail', 'chat'])
  })
  it('Trenner zwischen sichtbaren Bereichen, fehlende Bereiche hinten', () => {
    assert.deepEqual(railLayout(['mail', 'sep:a', 'chat'], ['mail', 'chat', 'notes']), ['mail', 'sep:a', 'chat', 'notes'])
  })
  it('Trenner vorn und doppelte (ausgeblendeter Bereich dazwischen) nur einmal', () => {
    assert.deepEqual(railLayout(['sep:x', 'mail', 'sep:a', 'audio', 'sep:b', 'chat'], ['mail', 'chat']), ['mail', 'sep:a', 'chat'])
  })
  it('Trenner am Ende bleibt', () => {
    assert.deepEqual(railLayout(['mail', 'chat', 'sep:e'], ['mail', 'chat']), ['mail', 'chat', 'sep:e'])
  })
  it('einfügen nach Bereich und am Ende', () => {
    const o = fullOrder(['chat'], ['mail', 'chat'])
    assert.deepEqual(o, ['chat', 'mail'])
    assert.deepEqual(insertSep(o, 'chat', 'sep:1'), ['chat', 'sep:1', 'mail'])
    assert.deepEqual(insertSep(o, null, 'sep:2'), ['chat', 'mail', 'sep:2'])
  })
  it('löschen und verschieben', () => {
    assert.deepEqual(removeSep(['mail', 'sep:1', 'chat'], 'sep:1'), ['mail', 'chat'])
    assert.deepEqual(moveEntry(['mail', 'sep:1', 'chat'], 'sep:1', 'mail'), ['sep:1', 'mail', 'chat'])
    assert.deepEqual(moveEntry(['mail', 'sep:1', 'chat'], 'mail', null), ['sep:1', 'chat', 'mail'])
  })
  it('Überlauf nach Pixeln, ohne Trenner am Rand', () => {
    const e = ['a', 'sep:1', 'b', 'c', 'sep:2', 'd']
    assert.deepEqual(fitRail(e, 4 * SLOT_PX + 2 * SEP_PX), { visible: e, rest: [] })
    // Platz für „…“ und zwei Bereiche samt Trenner
    assert.deepEqual(fitRail(e, 3 * SLOT_PX + SEP_PX), { visible: ['a', 'sep:1', 'b'], rest: ['c', 'sep:2', 'd'] })
    assert.deepEqual(fitRail(e, 4 * SLOT_PX + SEP_PX), { visible: ['a', 'sep:1', 'b', 'c'], rest: ['d'] })
  })
})
