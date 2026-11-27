import assert from 'node:assert/strict'
import { describe, it } from 'node:test'
import { hasMore, LIST_MAX, listPlan, listRows, mergeRows } from '../src/mail/listPaging.ts'

describe('listPaging', () => {
  it('Zeilen wachsen bis 500 um 100, danach um 500', () => {
    assert.deepEqual([0, 1, 4, 5, 6, 10].map(listRows), [100, 200, 500, 1000, 1500, 3500])
  })
  it('bis 500 eine Anfrage, darüber Seiten zu je 500', () => {
    assert.deepEqual(listPlan(300), [{ page: 0, size: 300 }])
    assert.deepEqual(listPlan(500), [{ page: 0, size: 500 }])
    assert.deepEqual(listPlan(1500), [0, 1, 2].map(page => ({ page, size: LIST_MAX })))
  })
  it('erste Seite: mehr, solange der Server mehr zählt als geliefert', () => {
    assert.equal(hasMore(0, 100, 1, 1), false)
    assert.equal(hasMore(0, 100, 0, 0), false)
    assert.equal(hasMore(0, 100, 100, 180), true)
    assert.equal(hasMore(0, 500, 500, 3160), true)
  })
  it('Folgeseiten: nach der Lage beim Server, leere Seite heißt Ende', () => {
    assert.equal(hasMore(1, 500, 500, 3160), true)
    assert.equal(hasMore(5, 500, 500, 3160), true)
    assert.equal(hasMore(6, 500, 160, 3160), false)
    assert.equal(hasMore(1, 500, 0, 3160), false)
  })
  it('anhängen ohne Doppelte', () => {
    assert.deepEqual(mergeRows([{ id: 1 }, { id: 2 }], [{ id: 2 }, { id: 3 }]).map(i => i.id), [1, 2, 3])
  })
})
