// Sprache der Oberflaeche je Benutzer (Einstellungen -> Allgemein -> Sprache und Zeit).
// Deutsch ist die Quelle: t('Löschen') gibt auf Englisch den Eintrag aus en/* zurueck,
// fehlt er, bleibt der deutsche Text stehen. Platzhalter: t('{n} Mails', { n: 3 }).
// Ein Sprachwechsel laedt die Seite neu - so muss nicht jede Stelle neu zeichnen.
import { currentPrefs } from '../prefs'
import { en } from './en'

export type Lang = 'de' | 'en'

function initial(): Lang {
  if (currentPrefs.language) return currentPrefs.language
  // Vor der Anmeldung: auf der Anmeldeseite gewaehlt, sonst Sprache des Browsers
  try { const l = localStorage.getItem('dmoffice.lang'); if (l === 'de' || l === 'en') return l } catch { /* egal */ }
  return navigator.language?.toLowerCase().startsWith('de') ? 'de' : 'en'
}

export const lang: Lang = initial()
document.documentElement.lang = lang

/** Fuer Intl/toLocaleString: "de-DE" oder "en-GB". */
export const locale = lang === 'en' ? 'en-GB' : 'de-DE'

/** Sprache vor der Anmeldung umschalten (Anmelde- und Einrichtungsseite); laedt neu. */
export function chooseLanguage(l: Lang) {
  try { localStorage.setItem('dmoffice.lang', l) } catch { /* egal */ }
  location.reload()
}

/**
 * Text in der eingestellten Sprache. Mehrdeutiges bekommt einen Kontext vor „|“ (etwa „coach|Größe“ = Körpergröße, nicht
 * Dateigröße): Deutsch zeigt nur den Text dahinter, Englisch nimmt die Übersetzung mit Kontext, sonst die ohne.
 */
export function t(de: string, vars?: Record<string, string | number>): string {
  const bar = /^[a-z]+\|/.exec(de)
  const plain = bar ? de.slice(bar[0].length) : de
  let s = lang === 'en' ? en[de] ?? en[plain] ?? plain : plain
  if (vars) for (const [k, v] of Object.entries(vars)) s = s.split(`{${k}}`).join(String(v))
  return s
}

/** Fehlermeldungen vom Server sind deutsch - bekannte uebersetzen, Teile in „…“ bleiben. */
export function tError(message: string): string {
  if (lang === 'de') return message
  if (en[message]) return en[message]
  // Meldungen mit wechselndem Teil am Ende: "Adresse in An ungültig: xyz"
  const i = message.indexOf(': ')
  if (i > 0 && en[message.slice(0, i)]) return en[message.slice(0, i)] + message.slice(i)
  // Wechselnder Teil vorn: "Einkauf: Fassung aus dmOffice verworfen, …" (Titel, Name)
  const j = message.lastIndexOf(': ')
  if (j > 0 && en[message.slice(j + 2)]) return tError(message.slice(0, j)) + ': ' + en[message.slice(j + 2)]
  return message
}
