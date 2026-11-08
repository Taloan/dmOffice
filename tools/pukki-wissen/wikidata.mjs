// Pukki (dmOffice 2): aus journali (tools/advisor-wissen/wikidata.mjs, AGPL, derselbe Autor) uebernommen. Holt zu den Familien-Themen
// (DmOffice.Server/Chat/wissen/themen.json) die Unterbegriffe aus Wikidata und legt sie als Assoziationen
// ab (DmOffice.Server/Chat/wissen/assoziationen.json): jeder Unterbegriff → die Wörter seines Oberthemas und seine direkten Oberbegriffe — „tennis“ →
// sport, training, rückschlagspiel. Wikidata-Daten stehen unter CC0. Der Chat fragt Wikidata nie; dieses Werkzeug läuft, wenn sich die
// Themen ändern, und das Ergebnis kommt ins Repo (09.10.2026).
//
//   node tools/pukki-wissen/wikidata.mjs [--max 200] [--nur key,key]
//
// Ein Thema ohne Kennung bekommt die erste Kennung der Suche nach `suche`, deren englischer Name klein beginnt — ein Allgemeinbegriff, kein
// Eigenname („winter“ die Jahreszeit, nicht der Familienname). Die Kennung und was sie ist, schreibt das Werkzeug in themen.json zurück, damit
// ein Mensch sie prüfen kann und der nächste Lauf dieselbe nimmt. Aufgenommen wird ein Unterbegriff nur mit kleinem englischem Namen (kein
// Eigenname, keine Marke) und als ein Wort; die Bekanntesten zuerst (Zahl der Wikipedia-Artikel). Trifft die Suche den falschen Begriff
// („pet“ → Positronen-Emissions-Tomographie), steht die richtige Kennung fest in themen.json; `"wikidata": false` führt ein Thema ohne
// Wikidata (Feste haben dort keine Unterklassen) — seine Wörter werden nur untereinander verknüpft.
import fs from 'node:fs'

const WISSEN = new URL('../../DmOffice.Server/Chat/wissen/', import.meta.url)
const THEMEN = new URL('themen.json', WISSEN)
const ZIEL = new URL('assoziationen.json', WISSEN)
const UA = { 'user-agent': 'dmoffice-pukki-wissen/1.0 (build tool)' }
const MAX_JE_WORT = 12

const arg = (n, v) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : v }
const max = Number(arg('max', 200))
const nur = arg('nur', '') ? new Set(arg('nur', '').split(',')) : null
const warte = ms => new Promise(r => setTimeout(r, ms))
const FUELL = new Set(['and', 'or', 'of', 'the', 'for', 'with', 'und', 'oder', 'für', 'mit', 'von', 'der', 'die', 'das'])

async function holen(url, versuche = 4) {
  for (let i = 0; ; i++) {
    const r = await fetch(url, { headers: { ...UA, accept: 'application/sparql-results+json, application/json' } }).catch(e => ({ ok: false, status: 0, e }))
    if (r.ok) return r.json()
    if (i >= versuche) throw new Error(`HTTP ${r.status} ${url.slice(0, 120)}`)
    await warte(r.status === 429 ? 30_000 : 5_000 * (i + 1))
  }
}

async function kennung(t) {
  const u = `https://www.wikidata.org/w/api.php?action=wbsearchentities&search=${encodeURIComponent(t.suche)}&language=en&type=item&limit=7&format=json`
  const treffer = (await holen(u)).search ?? []
  return treffer.find(x => /^[a-z]/.test(x.label ?? '')) ?? null
}

const sparql = q => holen('https://query.wikidata.org/sparql?query=' + encodeURIComponent(q)).then(j => j.results.bindings)

async function unterbegriffe(qid, tief) {
  const pfad = tief ? 'wdt:P279+' : 'wdt:P279/wdt:P279?/wdt:P279?'
  return sparql(`SELECT ?i ?de ?en (MAX(?links) AS ?l) (GROUP_CONCAT(DISTINCT ?pde; separator="|") AS ?pdes) (GROUP_CONCAT(DISTINCT ?pen; separator="|") AS ?pens) WHERE {
    ?i ${pfad} wd:${qid} . ?i wikibase:sitelinks ?links .
    ?i rdfs:label ?en FILTER(lang(?en) = "en") ?i rdfs:label ?de FILTER(lang(?de) = "de")
    OPTIONAL { ?i wdt:P279 ?p . ?p rdfs:label ?pde FILTER(lang(?pde) = "de") ?p rdfs:label ?pen FILTER(lang(?pen) = "en") }
  } GROUP BY ?i ?de ?en ORDER BY DESC(?l) LIMIT ${max}`)
}

const ein = s => /^[\p{Ll}][\p{L}-]*$/u.test(s)                                   // ein Wort, klein
// die Wörter eines Oberbegriffs: im Deutschen nur das Grundwort am Ende („olympische Sportart“ → sportart), im Englischen alle („racket sport“)
const woerterVon = (s, l) => {
  const ws = s.toLowerCase().split(/[\s,/()]+/).map(w => w.replace(/^-+|-+$/g, '')).filter(w => w.length >= 3 && !FUELL.has(w) && /^[\p{L}-]+$/u.test(w))
  return l === 'de' ? ws.slice(-1) : ws
}

const themen = JSON.parse(fs.readFileSync(THEMEN, 'utf8'))
let alt = {}
try { alt = JSON.parse(fs.readFileSync(ZIEL, 'utf8')) } catch { }
const aus = { de: {}, en: {} }
const dazu = (l, w, liste) => { if (!ein(w)) return; aus[l][w] = [...new Set([...(aus[l][w] ?? []), ...liste.filter(x => x !== w)])].slice(0, MAX_JE_WORT) }

for (const t of themen) {
  if (nur && !nur.has(t.key)) continue
  // das Thema selbst: seine Wörter untereinander — auch ohne Kennung (Feste haben bei Wikidata keine Unterklassen)
  for (const w of t.de) dazu('de', w, t.de)
  for (const w of t.en) dazu('en', w, t.en)
  if (t.wikidata === false) { console.log(`${t.key}: ohne Wikidata`); continue }
  try {
    if (!t.wikidata) {
      const k = await kennung(t)
      if (!k) { console.log(`${t.key}: keine Kennung zu „${t.suche}“`); continue }
      t.wikidata = k.id; t.gefunden = `${k.label} — ${k.description ?? ''}`.trim()
      await warte(500)
    }
    let zeilen
    try { zeilen = await unterbegriffe(t.wikidata, true) } catch { zeilen = await unterbegriffe(t.wikidata, false) }   // zu breit: drei Ebenen
    let n = 0
    for (const z of zeilen) {
      const en = z.en.value, de = z.de.value
      if (!/^[a-z]/.test(en)) continue                                              // Eigenname oder Marke
      const pde = (z.pdes?.value ?? '').split('|').flatMap(s => woerterVon(s, 'de')), pen = (z.pens?.value ?? '').split('|').flatMap(s => woerterVon(s, 'en'))
      const d = de.toLowerCase(), e = en.toLowerCase()
      if (ein(d)) { dazu('de', d, [...t.de, ...pde]); n++ }
      if (ein(e)) { dazu('en', e, [...t.en, ...pen]); n++ }
    }
    console.log(`${t.key} ${t.wikidata} (${t.gefunden ?? ''}): ${zeilen.length} Unterbegriffe, ${n} aufgenommen`)
  } catch (e) { console.log(`${t.key}: ${e.message}`) }
  await warte(1_000)
}

// was ein Lauf mit --nur nicht berührt, bleibt
if (nur) for (const l of Object.keys(alt)) for (const [w, liste] of Object.entries(alt[l])) if (!aus[l]?.[w]) (aus[l] ??= {})[w] = liste

fs.writeFileSync(THEMEN, JSON.stringify(themen, null, 2) + '\n')
const zeilen = ['{']
const sprachen = Object.keys(aus).sort()
sprachen.forEach((l, i) => {
  const ws = Object.keys(aus[l]).sort((a, b) => a.localeCompare(b, l))
  zeilen.push(`  ${JSON.stringify(l)}: {`)
  ws.forEach((w, k) => zeilen.push(`    ${JSON.stringify(w)}: ${JSON.stringify(aus[l][w])}${k < ws.length - 1 ? ',' : ''}`))
  zeilen.push(`  }${i < sprachen.length - 1 ? ',' : ''}`)
})
zeilen.push('}')
fs.writeFileSync(ZIEL, zeilen.join('\n') + '\n')
for (const l of sprachen) console.log(l, Object.keys(aus[l]).length, 'Stichwörter,', Object.values(aus[l]).flat().length, 'Assoziationen')
