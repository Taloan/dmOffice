// Trenner in der Leiste links (Kennung railsep, ab 2.2). Reine Logik ohne Oberfläche: Trenner stehen als eigene Einträge
// „sep:<id>“ in prefs.moduleOrder zwischen den Bereichen. Ohne Schalter liest die Leiste dort nur die Bereiche und übergeht sie.

export const SEP = 'sep:'
export const isSep = (k: string) => k.startsWith(SEP)
export const newSep = () => SEP + Math.random().toString(36).slice(2, 8)

/**
 * Vollständige Reihenfolge: die gespeicherte (mit Trennern und noch nicht geladenen Plugin-Bereichen), dahinter alle Bereiche,
 * die dort noch fehlen, in ihrer Reihenfolge. Grundlage zum Ändern, damit nichts Gespeichertes verloren geht.
 */
export function fullOrder(stored: string[] | undefined, keys: string[]): string[] {
  const base = stored ?? []
  const seen = new Set(base)
  return [...base, ...keys.filter(k => !seen.has(k))]
}

/**
 * Was die Leiste zeigt: die sichtbaren Bereiche (schon sortiert) mit den Trennern dazwischen. Ein Trenner ganz vorn und
 * mehrere direkt hintereinander (etwa weil ein Bereich dazwischen ausgeblendet ist) erscheinen nicht bzw. nur einmal.
 */
export function railLayout(stored: string[] | undefined, visible: string[]): string[] {
  const show = new Set(visible)
  const out: string[] = []
  for (const k of fullOrder(stored, visible)) {
    if (isSep(k)) { if (out.length > 0 && !isSep(out[out.length - 1])) out.push(k) }
    else if (show.has(k)) out.push(k)
  }
  return out
}

/** Trenner direkt nach diesem Eintrag einfügen; ohne Eintrag ans Ende. */
export function insertSep(order: string[], after: string | null, id = newSep()): string[] {
  const out = [...order]
  const i = after == null ? -1 : out.indexOf(after)
  out.splice(i < 0 ? out.length : i + 1, 0, id)
  return out
}

export const removeSep = (order: string[], id: string): string[] => order.filter(k => k !== id)

/** Eintrag (Bereich oder Trenner) vor einen anderen setzen; ohne Ziel ans Ende. */
export function moveEntry(order: string[], from: string, before: string | null): string[] {
  if (from === before) return order
  const out = order.filter(k => k !== from)
  const i = before == null ? -1 : out.indexOf(before)
  out.splice(i < 0 ? out.length : i, 0, from)
  return out
}

/** Höhen in der Leiste (px, samt Abstand 4 px): Bereichsknopf und Trenner */
export const SLOT_PX = 48, SEP_PX = 11

/**
 * Überlauf: wie viele Einträge der Leiste passen bei dieser Höhe, wenn darunter ggf. noch „…“ Platz braucht.
 * Ergebnis: sichtbare Einträge (ohne Trenner am Ende) und der Rest fürs Menü „…“ (ohne Trenner am Anfang).
 */
export function fitRail(entries: string[], px: number): { visible: string[]; rest: string[] } {
  const cost = (k: string) => isSep(k) ? SEP_PX : SLOT_PX
  const total = entries.reduce((n, k) => n + cost(k), 0)
  if (total <= px) return { visible: entries, rest: [] }
  const room = Math.max(0, px - SLOT_PX)
  let used = 0, n = 0
  while (n < entries.length && used + cost(entries[n]) <= room) used += cost(entries[n++])
  // mindestens ein Bereich bleibt sichtbar (wie bisher)
  if (n === 0) n = 1
  const visible = entries.slice(0, n)
  while (visible.length && isSep(visible[visible.length - 1])) visible.pop()
  const rest = entries.slice(n)
  while (rest.length && isSep(rest[0])) rest.shift()
  return { visible, rest }
}
