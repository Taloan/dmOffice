// Lange Listen mit unterschiedlich hohen Zeilen: nur der sichtbare Ausschnitt (plus Rand) steht im DOM. Gemessene Höhen werden
// gemerkt; noch nicht gemessene Zeilen zählen mit dem Mittel der gemessenen ihrer Art.

/** Anfang jeder Zeile (offsets[i]) und am Ende die Gesamthöhe (offsets[n]). */
export function offsetsOf(n: number, height: (i: number) => number): number[] {
  const o = new Array<number>(n + 1)
  o[0] = 0
  for (let i = 0; i < n; i++) o[i + 1] = o[i] + height(i)
  return o
}

/** Erste Zeile, deren Ende unter y liegt (binäre Suche). */
export function indexAt(offsets: number[], y: number): number {
  let lo = 0, hi = offsets.length - 2
  if (hi < 0) return 0
  while (lo < hi) {
    const mid = (lo + hi) >> 1
    if (offsets[mid + 1] <= y) lo = mid + 1
    else hi = mid
  }
  return lo
}

/** Zeilen [first, last) für den Ausschnitt top..top+height mit overscan Pixeln Rand oben und unten. */
export function rangeOf(offsets: number[], top: number, height: number, overscan: number): [number, number] {
  const n = offsets.length - 1
  if (n <= 0) return [0, 0]
  const first = indexAt(offsets, Math.max(0, top - overscan))
  const last = Math.min(n, indexAt(offsets, top + height + overscan) + 1)
  return [first, last]
}

/** Scrollposition, damit Zeile i ganz sichtbar ist (wie scrollIntoView „nearest“); null: ist schon sichtbar. */
export function scrollToShow(offsets: number[], i: number, top: number, height: number): number | null {
  const a = offsets[i], b = offsets[i + 1]
  if (a < top) return a
  if (b > top + height) return Math.max(0, b - height)
  return null
}
