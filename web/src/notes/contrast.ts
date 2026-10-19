/**
 * Notizen aus notizi tragen oft feste Schriftfarben, die fuers dunkle (oder helle)
 * Design gewaehlt wurden. Zu schwach abgesetzte Farben werden nur fuer die Anzeige
 * auf die normale Schriftfarbe gesetzt (data-dm-contrast) - der Inhalt bleibt, wie er ist.
 */
function rgb(c: string): [number, number, number, number] | null {
  const m = c.match(/rgba?\(([^)]+)\)/)
  if (!m) return null
  const p = m[1].split(',').map(x => parseFloat(x))
  return [p[0], p[1], p[2], p.length > 3 ? p[3] : 1]
}

function lum([r, g, b]: number[]) {
  const f = (v: number) => { v /= 255; return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4) }
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b)
}

function background(el: HTMLElement, stop: HTMLElement): number[] {
  for (let n: HTMLElement | null = el; n; n = n.parentElement) {
    const c = rgb(getComputedStyle(n).backgroundColor)
    if (c && c[3] > 0.5) return c
    if (n === stop) break
  }
  for (let n: HTMLElement | null = stop.parentElement; n; n = n.parentElement) {
    const c = rgb(getComputedStyle(n).backgroundColor)
    if (c && c[3] > 0.5) return c
  }
  return [255, 255, 255]
}

export function fixContrast(root: HTMLElement) {
  root.querySelectorAll('[data-dm-contrast]').forEach(e => e.removeAttribute('data-dm-contrast'))
  root.querySelectorAll<HTMLElement>('[style*="color"]').forEach(el => {
    if (!el.style.color) return
    const fg = rgb(getComputedStyle(el).color)
    if (!fg) return
    const a = lum(fg), b = lum(background(el, root))
    const ratio = (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05)
    if (ratio < 3) el.setAttribute('data-dm-contrast', '')
  })
}
