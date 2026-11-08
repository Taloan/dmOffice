import { lang, locale } from '../i18n'

/** Cent als Text für ein Eingabefeld. */
export const moneyText = (cents: number) => (cents / 100).toLocaleString(locale, { minimumFractionDigits: 2, maximumFractionDigits: 2, useGrouping: false })

/** Eingabe wie „1.234,56“ oder „12.50“ in Cent; null, wenn es keine Zahl ist. */
export function parseMoney(text: string): number | null {
  let v = text.replace(/[\s€]/g, '')
  if (!v) return null
  const comma = v.lastIndexOf(','), dot = v.lastIndexOf('.')
  if (comma >= 0 && dot >= 0) { const dec = comma > dot ? ',' : '.'; v = v.split(dec === ',' ? '.' : ',').join('').replace(dec, '.') }
  else if (comma >= 0) v = v.replace(',', '.')
  // Nur Punkte: im Deutschen „1.234“ als Tausenderpunkt
  else if (dot >= 0 && lang !== 'en' && /^-?\d{1,3}(\.\d{3})+$/.test(v)) v = v.split('.').join('')
  if (!/^-?\d*(\.\d{0,2})?$/.test(v) || v === '-' || v === '.') return null
  return Math.round(parseFloat(v) * 100)
}
