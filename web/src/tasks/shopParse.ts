// Einkaufs- und Checklisten: "2 kg Kartoffeln" in Menge, Einheit und Artikel zerlegen,
// und fuer neue Artikel eine passende Abteilung raten.

export interface ShopItem { qty: string; unit: string; item: string }

const units = [
  'kg', 'g', 'mg', 'l', 'ml', 'cl', 'dl', 'Stk', 'Stück', 'St', 'Pck', 'Pkg', 'Packung', 'Packungen', 'Pack', 'Dose', 'Dosen',
  'Flasche', 'Flaschen', 'Fl', 'Glas', 'Gläser', 'Becher', 'Bund', 'Beutel', 'Tüte', 'Tüten', 'Netz', 'Schale', 'Schalen',
  'Kiste', 'Kisten', 'Kasten', 'Rolle', 'Rollen', 'Tube', 'Tuben', 'Prise', 'EL', 'TL', 'Tasse', 'Tassen', 'Scheibe', 'Scheiben',
  'x', 'pcs', 'pack', 'can', 'cans', 'bottle', 'bottles', 'jar', 'bag', 'box', 'lb', 'oz',
]
const unitRe = units.map(u => u.replace('.', '\\.')).join('|')
// "2 kg Kartoffeln", "500g Hack", "2x Milch", "1,5 l Wasser", "½ Bund Petersilie"
const re = new RegExp(`^\\s*(\\d+(?:[.,]\\d+)?|½|¼|¾)\\s*(?:(${unitRe})\\.?(?=\\s|$))?\\s+(.+)$`, 'i')

export function parseItem(text: string): ShopItem {
  const m = text.match(re)
  if (!m) return { qty: '', unit: '', item: text.trim() }
  // Einheit so schreiben, wie sie in der Liste steht ("KG" -> "kg")
  const unit = m[2] ? units.find(u => u.toLowerCase() === m[2].toLowerCase()) ?? m[2] : ''
  return { qty: m[1], unit, item: m[3].trim() }
}

export const composeItem = (x: ShopItem) => [x.qty, x.unit, x.item].filter(Boolean).join(' ')

/** Menge, Einheit und Artikel einer Aufgabe - aus den X-DM-Feldern oder aus dem Titel (in anderen Programmen geaendert). */
export function itemOf(task: { title: string; item?: string | null; qty?: string | null; unit?: string | null }): ShopItem {
  const fromFields = { qty: task.qty ?? '', unit: task.unit ?? '', item: task.item ?? '' }
  if (task.item && composeItem(fromFields) === task.title.trim()) return fromFields
  return parseItem(task.title)
}

// Abteilung raten - nur, wenn es sie in der Liste gibt. Laengster Treffer gewinnt ("Tomatenmark" vor "Tomate").
const guesses: [string, string[]][] = [
  ['Obst & Gemüse', ['apfel', 'äpfel', 'banane', 'birne', 'tomate', 'gurke', 'salat', 'kartoffel', 'zwiebel', 'knoblauch', 'paprika',
    'möhre', 'karotte', 'zitrone', 'limette', 'orange', 'mandarine', 'beeren', 'trauben', 'pilz', 'champignon', 'zucchini', 'brokkoli',
    'blumenkohl', 'lauch', 'porree', 'kräuter', 'petersilie', 'schnittlauch', 'basilikum', 'avocado', 'ingwer', 'obst', 'gemüse',
    'radieschen', 'sellerie', 'spargel', 'kohl', 'mango', 'ananas', 'kiwi', 'melone']],
  ['Brot & Backwaren', ['brot', 'brötchen', 'toast', 'croissant', 'brezel', 'baguette', 'kuchen', 'semmel']],
  ['Kühlregal', ['milch', 'joghurt', 'jogurt', 'quark', 'butter', 'sahne', 'eier', 'margarine', 'schmand', 'frischkäse', 'pudding',
    'hefe', 'skyr', 'kefir', 'buttermilch', 'tofu']],
  ['Fleisch & Wurst', ['fleisch', 'hack', 'hähnchen', 'huhn', 'pute', 'schnitzel', 'wurst', 'schinken', 'salami', 'steak', 'speck',
    'würstchen', 'aufschnitt', 'lachs', 'fisch']],
  ['Käse', ['käse', 'gouda', 'mozzarella', 'parmesan', 'feta', 'camembert', 'emmentaler']],
  ['Tiefkühl', ['tiefkühl', 'tk-', 'tk ', 'pizza', 'eis', 'pommes', 'fischstäbchen']],
  ['Konserven & Vorrat', ['nudeln', 'pasta', 'spaghetti', 'reis', 'mehl', 'zucker', 'salz', 'pfeffer', 'öl', 'essig', 'tomatenmark',
    'passierte', 'müsli', 'haferflocken', 'kaffee', 'tee', 'honig', 'marmelade', 'gewürz', 'brühe', 'linsen', 'bohnen', 'mais',
    'senf', 'ketchup', 'mayo', 'backpulver', 'konserve']],
  ['Getränke', ['wasser', 'saft', 'bier', 'wein', 'cola', 'limo', 'sprudel', 'schorle', 'sekt']],
  ['Süßes & Snacks', ['schokolade', 'schoki', 'chips', 'kekse', 'gummibärchen', 'nüsse', 'bonbons', 'riegel', 'cracker']],
  ['Drogerie', ['shampoo', 'duschgel', 'zahnpasta', 'zahnbürste', 'zahnseide', 'deo', 'seife', 'creme', 'taschentücher', 'windeln',
    'rasier', 'pflaster', 'wattestäbchen', 'sonnencreme']],
  ['Haushalt', ['spülmittel', 'waschmittel', 'weichspüler', 'müllbeutel', 'küchenrolle', 'toilettenpapier', 'klopapier', 'alufolie',
    'frischhaltefolie', 'backpapier', 'schwamm', 'batterien', 'glühbirne', 'spültabs', 'reiniger']],
]

export function guessSection(item: string, sections: string[]): string | null {
  const name = item.toLowerCase()
  let best: { section: string; len: number } | null = null
  for (const [section, words] of guesses) {
    if (!sections.includes(section)) continue
    for (const w of words) if (name.includes(w) && (!best || w.length > best.len)) best = { section, len: w.length }
  }
  return best?.section ?? null
}
