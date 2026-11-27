// Mosaikansicht der Galerie (Kennung mosaicweb): Fotos in Zeilen mit gleicher Höhe, jede Zeile füllt die Breite (Blocksatz wie Lightroom).

/** Eine Zeile: die Fotos start … start+count-1 mit ihren Breiten und der gemeinsamen Höhe; stretched = füllt die Breite. */
export interface MosaicRow { start: number; count: number; h: number; widths: number[]; stretched: boolean }

/** Seitenverhältnis (Breite durch Höhe) aus den Maßen; unbekannt: 1,5 (Video 1,78). Auf ein vernünftiges Maß begrenzt. */
export function ratioOf(width?: number | null, height?: number | null, video = false): number {
  const r = width && height && width > 0 && height > 0 ? width / height : video ? 16 / 9 : 1.5
  return Math.min(3, Math.max(0.5, r))
}

/**
 * Zeilen bilden: so lange Fotos nebeneinander, bis die Zielhöhe erreicht ist; die Zeile wird dann so skaliert, dass sie die Breite genau füllt.
 * Die letzte Zeile bleibt auf Zielhöhe (linksbündig), wenn sie gestreckt viel zu hoch würde (mehr als 1,3-fache Zielhöhe).
 */
export function mosaicRows(ratios: number[], width: number, target: number, gap: number): MosaicRow[] {
  const rows: MosaicRow[] = []
  const W = Math.max(1, width)
  let start = 0
  while (start < ratios.length) {
    let sum = 0, n = 0
    // Foto für Foto dazunehmen; reicht die Zeile schon (Höhe bei voller Breite <= Ziel), nimmt man das letzte nur mit, wenn es näher an die Zielhöhe führt
    while (start + n < ratios.length) {
      const next = sum + ratios[start + n]
      const hWith = (W - gap * n) / next
      if (n > 0) {
        const hWithout = (W - gap * (n - 1)) / sum
        if (hWith <= target) { if (Math.abs(hWith - target) < Math.abs(hWithout - target)) { sum = next; n++ } break }
      }
      sum = next; n++
      if (hWith <= target) break
    }
    const fitH = (W - gap * (n - 1)) / sum
    const last = start + n >= ratios.length
    const stretched = !(last && fitH > target * 1.3)
    const h = stretched ? fitH : target
    const widths: number[] = []
    if (stretched) {
      // Ganze Pixel, der Rest geht an das letzte Foto, damit die Zeile genau aufgeht
      let used = 0
      for (let k = 0; k < n; k++) {
        const w = k === n - 1 ? W - gap * (n - 1) - used : Math.floor(ratios[start + k] * h)
        widths.push(w); used += w
      }
    } else for (let k = 0; k < n; k++) widths.push(Math.round(ratios[start + k] * h))
    rows.push({ start, count: n, h: Math.round(h), widths, stretched })
    start += n
  }
  return rows
}

/** Höhe aller Zeilen samt Abstand dazwischen (und 4 px oben wie beim Raster). */
export const rowsHeight = (rows: MosaicRow[], gap: number) => rows.reduce((s, r) => s + r.h, 0) + Math.max(0, rows.length - 1) * gap + 4

/** Höhe für n noch nicht geladene Fotos (Platzhalter): Seitenverhältnis 1,5 angenommen. */
export function estimateHeight(n: number, width: number, target: number, gap: number, ratio = 1.5): number {
  if (n <= 0) return 0
  const perRow = Math.max(1, Math.round((width + gap) / (target * ratio + gap)))
  const rows = Math.ceil(n / perRow)
  return rows * target + Math.max(0, rows - 1) * gap + 4
}
