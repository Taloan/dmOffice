import assert from 'node:assert/strict'
import { test } from 'node:test'
import { parseNaturalDate } from '../src/shell/naturalDate.ts'

// Montag, 23.11.2026, 12:00
const now = new Date(2026, 10, 23, 12, 0)
const ymd = (d: Date) => `${d.getFullYear()}-${d.getMonth() + 1}-${d.getDate()}`

test('Uhrzeit ohne Tag gilt am gewählten Tag', () => {
  const nd = parseNaturalDate('Besprechung 16 Uhr', now, new Date(2026, 10, 27, 10, 0))!
  assert.equal(ymd(nd.date), '2026-11-27')
  assert.equal(nd.date.getHours(), 16)
  assert.equal(nd.title, 'Besprechung')
  // Auch eine Uhrzeit, die heute schon vorbei wäre
  assert.equal(ymd(parseNaturalDate('Arzt 9:30', now, new Date(2026, 10, 26))!.date), '2026-11-26')
  // Ohne gewählten Tag wie bisher: heute bzw. morgen
  assert.equal(ymd(parseNaturalDate('Arzt 9:30', now)!.date), '2026-11-24')
  // Tagesangabe im Titel geht vor
  assert.equal(ymd(parseNaturalDate('Arzt morgen 9:30', now, new Date(2026, 10, 27))!.date), '2026-11-24')
})

test('unmögliche Daten werden nicht erkannt', () => {
  for (const x of ['am 31.02. Unsinn', 'Steuer 00.00.0000', 'Altes 01.01.0001', 'Frist 32.12.', 'Frist 2026-13-01', 'am 31. November'])
    assert.equal(parseNaturalDate(x, now), null, x)
  assert.equal(ymd(parseNaturalDate('am 28.02. Steuer', now)!.date), '2027-2-28')
  assert.equal(ymd(parseNaturalDate('Frist 29.02.2028', now)!.date), '2028-2-29')
  assert.equal(ymd(parseNaturalDate('Frist 1.12.26', now)!.date), '2026-12-1')
})
