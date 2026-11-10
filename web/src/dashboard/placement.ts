// Freie Platzierung der Widgets in der breiten Ansicht: jedes Widget hat eine Stelle im Raster (Spalte x, Reihe y ab 0).
// Ohne Stelle (neu, ältere Zusammenstellung) kommt es an die erste freie. Verschoben oder vergrößert rutscht nur, was im Weg
// ist, nach unten - der Rest bleibt, wo er ist; Lücken bleiben Lücken.
import type { Widget } from './api'

export interface Box { x: number; y: number; w: number; h: number }

/** Klein 1×1, mittel 2×1, groß 2×2, hoch 1×2 (Spalten × Reihen). */
export const dims = (size: number) => size === 1 ? { w: 1, h: 1 } : size === 2 ? { w: 2, h: 1 } : size === 4 ? { w: 1, h: 2 } : { w: 2, h: 2 }

const hit = (a: Box, b: Box) => a.x < b.x + b.w && b.x < a.x + a.w && a.y < b.y + b.h && b.y < a.y + a.h

/** Erste freie Stelle (Reihe für Reihe von links) für w×h. */
export function firstFree(taken: Box[], w: number, h: number, cols: number): { x: number; y: number } {
  for (let y = 0; y < 400; y++)
    for (let x = 0; x + w <= cols; x++)
      if (!taken.some(b => hit(b, { x, y, w, h }))) return { x, y }
  return { x: 0, y: 400 }
}

/**
 * Stellen aller Widgets bei dieser Spaltenzahl: gespeicherte Stellen, solange sie passen und frei sind (in der Reihenfolge der
 * Liste), dann die übrigen an die erste freie Stelle. Ein zu breit gewordenes Widget (schmales Fenster) rückt nach links.
 */
export function place(widgets: Widget[], cols: number): Map<string, Box> {
  const out = new Map<string, Box>()
  const taken: Box[] = []
  const rest: Widget[] = []
  for (const wd of widgets) {
    const { w, h } = dims(wd.size)
    const ww = Math.min(w, cols)
    if (wd.x == null || wd.y == null) { rest.push(wd); continue }
    const b = { x: Math.max(0, Math.min(wd.x, cols - ww)), y: wd.y, w: ww, h }
    if (taken.some(t => hit(t, b))) { rest.push(wd); continue }
    out.set(wd.id, b); taken.push(b)
  }
  for (const wd of rest) {
    const { w, h } = dims(wd.size)
    const ww = Math.min(w, cols)
    const p = firstFree(taken, ww, h, cols)
    const b = { ...p, w: ww, h }
    out.set(wd.id, b); taken.push(b)
  }
  return out
}

/**
 * Ein Widget fest an eine Stelle (verschoben oder in der Größe geändert): wer im Weg ist, rutscht so weit nach unten, bis er
 * frei ist - von oben nach unten, damit Nachrutschende weiter schieben. Gibt die neuen Stellen aller Widgets zurück.
 */
export function settle(boxes: Map<string, Box>, id: string, at: Box): Map<string, Box> {
  const out = new Map<string, Box>([[id, at]])
  const fixed: Box[] = [at]
  const others = [...boxes.entries()].filter(([k]) => k !== id).sort((a, b) => a[1].y - b[1].y || a[1].x - b[1].x)
  for (const [k, b0] of others) {
    const b = { ...b0 }
    while (fixed.some(f => hit(f, b))) b.y++
    out.set(k, b); fixed.push(b)
  }
  return out
}

/** Stellen in die Widgets schreiben (zum Speichern). */
export const withBoxes = (widgets: Widget[], boxes: Map<string, Box>): Widget[] =>
  widgets.map(w => { const b = boxes.get(w.id); return b ? { ...w, x: b.x, y: b.y } : w })
