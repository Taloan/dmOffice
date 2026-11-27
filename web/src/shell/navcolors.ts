// Farben der Symbole in der Leiste links (Kennung navcolors, ab 2.2): je Bereich (auch Gruppe, Verwaltung, Einstellungen) eine
// Farbe aus der Palette der Akzentfarben. Sie bleibt, wenn die Akzentfarbe wechselt; ohne Eintrag gilt die Akzentfarbe („Standard“).
import { featureOn } from '../api'
import { accents, limeAccent, themesFor } from '../theme'

/** Dieselben Farben wie unter Einstellungen → Darstellung → Designs */
export const navPalette = () => (featureOn('limeaccent') ? [...accents, limeAccent] : accents).map(a => a.color)

const shades = new Map<string, { light: string; dark: string }>()
/** Der Ton, den die Farbe als Akzentfarbe für Symbole hätte - im Dunkelmodus heller, im Hellmodus dunkler, damit sie lesbar bleibt */
export function navShade(color: string, dark: boolean): string {
  let c = shades.get(color)
  if (!c) { const th = themesFor(color); c = { light: th.light.colorBrandForeground1, dark: th.dark.colorBrandForeground1 }; shades.set(color, c) }
  return dark ? c.dark : c.light
}

/** Farbe setzen; null = zurück auf Standard (Akzentfarbe) */
export function setNavColor(map: Record<string, string> | undefined, key: string, color: string | null): Record<string, string> {
  const out = { ...(map ?? {}) }
  if (color) out[key] = color; else delete out[key]
  return out
}
