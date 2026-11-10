import { api, q } from '../api'
import { t } from '../i18n'

/**
 * Arten von Terminen (Version 2, Pukki Schritt 2): gespeichert als Kategorie mit dem deutschen Namen (wie auf dem Server,
 * Chat/PukkiArts.cs) - so kommen sie auch in anderen Kalendern an. Pukki ordnet neue Termine selbst ein.
 */
export interface Art { key: string; de: string; en: string; icon: string }

export const arts: Art[] = [
  { key: 'gesundheit', de: 'Gesundheit', en: 'Health', icon: '🩺' },
  { key: 'schule', de: 'Schule & Kita', en: 'School & daycare', icon: '🎒' },
  { key: 'abholen', de: 'Abholen & Bringen', en: 'Pick-up & drop-off', icon: '🚸' },
  { key: 'sport', de: 'Sport', en: 'Sports', icon: '⚽' },
  { key: 'kurse', de: 'Musik & Kurse', en: 'Music & classes', icon: '🎵' },
  { key: 'familie', de: 'Familie & Feiern', en: 'Family & celebrations', icon: '🎉' },
  { key: 'freizeit', de: 'Freizeit & Kultur', en: 'Leisure & culture', icon: '🎭' },
  { key: 'reisen', de: 'Reisen', en: 'Travel', icon: '✈️' },
  { key: 'arbeit', de: 'Arbeit', en: 'Work', icon: '💼' },
  { key: 'behoerden', de: 'Behörden & Finanzen', en: 'Authorities & finance', icon: '📄' },
  { key: 'haushalt', de: 'Haushalt & Handwerker', en: 'Household & repairs', icon: '🔧' },
  { key: 'auto', de: 'Auto', en: 'Car', icon: '🚗' },
  { key: 'tiere', de: 'Tiere', en: 'Pets', icon: '🐾' },
]

const same = (a: string, b: string) => a.trim().toLowerCase() === b.trim().toLowerCase()

/** Die Art aus den Kategorien eines Termins. */
export const artOf = (categories?: string[] | null): Art | undefined =>
  (categories ?? []).map(c => arts.find(a => same(a.de, c) || same(a.en, c))).find(Boolean)

/** Kategorien mit dieser Art (andere Kategorien bleiben, eine alte Art fällt weg); key leer: ohne Art. */
export const withArt = (categories: string[] | undefined | null, key: string | null): string[] => {
  const rest = (categories ?? []).filter(c => !arts.some(a => same(a.de, c) || same(a.en, c)))
  const a = arts.find(x => x.key === key)
  return a ? [...rest, a.de] : rest
}

/** Name in der Sprache der Oberfläche (die Liste kennt Deutsch und Englisch). */
export const artName = (a: Art) => t(a.de)

/** Vorschlag des Servers zu einem Titel. */
export const suggestArt = (title: string) =>
  api.get<{ key: string | null }>('/api/chat/arts/suggest?' + q({ title })).then(r => r.key).catch(() => null)
