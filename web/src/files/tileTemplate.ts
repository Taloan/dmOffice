// Kopfzeile der Galerie-Kachel frei belegbar (Kennung tilehead, docs/kachel-infozeilen-konzept.md): Vorlagen mit Platzhaltern wie
// im CameraImporter. Schreibweise {feld} oder {"Präfix"feld"Suffix"} – Präfix und Suffix erscheinen nur, wenn das Feld einen Wert
// hat. Unbekannte Felder bleiben als Text stehen. Dieses Modul hat keine Abhängigkeiten (Test: web/tests/tileTemplate.test.ts).

/** Felder in der Reihenfolge der „Wolke“; Namen wie im CameraImporter, soweit dmOffice das Feld hat. */
export const TILE_FIELDS = [
  'titel', 'beschreibung', 'tags', 'aufn-datum', 'aufn-zeit', 'datum-zeit', 'aufn-yy', 'aufn-mm', 'aufn-dd',
  'kamera', 'objektiv', 'blende', 'bel-zeit', 'iso', 'brennweite', 'filename', 'ext', 'groesse', 'abmessung', 'dauer',
  'sterne', 'farbe', 'ordner',
] as const
export type TileField = typeof TILE_FIELDS[number]

/** Werte eines Fotos je Feld; leer oder fehlend = kein Wert (Präfix und Suffix entfallen). */
export type TileValues = Partial<Record<TileField, string | null | undefined>>

/** Vier Zeilen, je links und rechts: [Z1 links, Z1 rechts, Z2 links, …]. Vorgabe = der feste Kopf vor dieser Kennung. */
export const DEFAULT_TILE_HEAD: readonly string[] = [
  '{titel}', '{aufn-datum}',
  '{beschreibung}', '{aufn-zeit}',
  '{"f"blende} {bel-zeit"s"} {brennweite"mm"}', '{kamera}',
  '{objektiv}', '{ext}',
]

const PLACEHOLDER = /\{(?:"([^"]*)")?([a-z][a-z0-9-]*)(?:"([^"]*)")?\}/g

/** Eine Vorlage mit den Werten eines Fotos füllen. Leerraum durch leere Felder fällt weg (doppelt und am Rand). */
export function renderTile(template: string, values: TileValues): string {
  const out = template.replace(PLACEHOLDER, (whole, pre: string | undefined, field: string, suf: string | undefined) => {
    if (!(TILE_FIELDS as readonly string[]).includes(field)) return whole
    const v = values[field as TileField]
    return v ? (pre ?? '') + v + (suf ?? '') : ''
  })
  return out.replace(/\s{2,}/g, ' ').trim()
}

/** Platzhalter, die es nicht gibt (etwa {datum}): stehen sonst wörtlich in der Kachel. */
export function unknownTileFields(templates: readonly string[]): string[] {
  const out = new Set<string>()
  for (const tpl of templates)
    for (const m of tpl.matchAll(PLACEHOLDER)) if (!(TILE_FIELDS as readonly string[]).includes(m[2])) out.add(m[2])
  return [...out]
}

/** Gespeicherte Belegung auf genau acht Vorlagen bringen (fehlende aus der Vorgabe). */
export function tileHeadOf(saved: readonly string[] | null | undefined): string[] {
  return DEFAULT_TILE_HEAD.map((d, i) => typeof saved?.[i] === 'string' ? saved[i] : d)
}

/** Text an der Cursorposition einfügen; Rückgabe: neuer Text und neue Cursorposition. */
export function insertAt(text: string, start: number, end: number, insert: string): { text: string; cursor: number } {
  const s = Math.max(0, Math.min(start, text.length)), e = Math.max(s, Math.min(end, text.length))
  return { text: text.slice(0, s) + insert + text.slice(e), cursor: s + insert.length }
}
