import { BrandVariants, createDarkTheme, createLightTheme, Theme } from '@fluentui/react-components'
import { useEffect, useState } from 'react'

/** Die Signaturfarbe von dmOffice (Lava war als Vorgabe zu grell). */
export const SIGNATURE = '#7B1DD3'
export const LAVA = '#E2412B'

/** Akzentfarben zur Wahl: Signatur vorne, dann die anderen Kandidaten, dazu die klassischen. */
export const accents = [
  { name: 'Violett', color: SIGNATURE }, { name: 'Lava', color: LAVA }, { name: 'Petrol', color: '#0B8A96' },
  { name: 'Safran', color: '#E08A00' }, { name: 'Jade', color: '#0F9D6E' }, { name: 'Himbeere', color: '#D1245E' },
  { name: 'Mint', color: '#13B8A2' }, { name: 'Blau', color: '#0F6CBD' }, { name: 'Grau', color: '#5B5B5B' },
  { name: 'Fuchsia', color: '#D6007E' },
]

const hex = (c: string) => [1, 3, 5].map(i => parseInt(c.slice(i, i + 2), 16))
const mix = (a: number[], b: number[], t: number) =>
  '#' + a.map((x, i) => Math.round(x + (b[i] - x) * t).toString(16).padStart(2, '0')).join('')

/** Abstufung 10..160 aus einer Farbe: 80 ist die Farbe selbst, darunter dunkler, darueber heller. */
export function brandFrom(color: string): BrandVariants {
  const base = hex(color)
  const out: Record<number, string> = {}
  for (let step = 10; step <= 160; step += 10) {
    out[step] = step < 80 ? mix(base, [0, 0, 0], ((80 - step) / 70) * 0.92)
      : step === 80 ? color.toUpperCase() : mix(base, [255, 255, 255], ((step - 80) / 80) * 0.85)
  }
  return out as unknown as BrandVariants
}

/**
 * Dunkler Modus in Anthrazit statt neutralem Grau: jede graue Flaeche und Linie bekommt einen
 * leichten Blaustich (wie Proton) - die Akzentfarbe leuchtet darauf staerker.
 */
function anthracite(theme: Theme): Theme {
  const out: Record<string, string> = { ...(theme as any) }
  for (const [k, v] of Object.entries(theme)) {
    if (typeof v !== 'string' || !/^colorNeutral(Background|Stroke)|^colorSubtleBackground|^colorTransparentBackground/.test(k)) continue
    const m = /^#([0-9a-f]{2})\1\1$/i.exec(v)
    if (!m) continue
    const g = parseInt(m[1], 16)
    const t = (x: number) => Math.max(0, Math.min(255, Math.round(x))).toString(16).padStart(2, '0')
    out[k] = '#' + t(g * 0.93) + t(g * 0.95) + t(g * 1.03 + 5)
  }
  return out as unknown as Theme
}

const themes = new Map<string, { light: Theme; dark: Theme }>()
export function themesFor(color?: string): { light: Theme; dark: Theme } {
  const c = (color || SIGNATURE).toUpperCase()
  if (!themes.has(c)) {
    const b = brandFrom(c)
    themes.set(c, { light: createLightTheme(b), dark: anthracite({ ...createDarkTheme(b), colorBrandForeground1: b[110], colorBrandForeground2: b[120] }) })
  }
  return themes.get(c)!
}

/** Das dmOffice-Symbol in einer Farbe (Fläche und Haken), als Bildadresse. Gleiche Form wie public/favicon.svg und brand.js. */
export function brandLogo(color?: string): string {
  const c = /^#[0-9a-f]{6}$/i.test(color ?? '') ? color! : SIGNATURE
  return 'data:image/svg+xml,' + encodeURIComponent(
    `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 108 108"><rect width="108" height="108" rx="24" fill="${c}"/>`
    + '<path d="M27 24h30a24 24 0 0 1 24 24v12a24 24 0 0 1 -24 24H27z" fill="#fff"/>'
    + `<path d="M39 54l8 8 16-16" fill="none" stroke="${c}" stroke-width="7" stroke-linecap="round" stroke-linejoin="round"/></svg>`)
}

/**
 * Akzentfarbe nach außen: Symbol im Tab (Favicon) und theme-color - danach färben Browser wie Vivaldi ihre Tableiste und die
 * installierte App ihre Titelleiste. Beim Laden setzt das schon public/brand.js, hier bei jeder Änderung in den Einstellungen.
 */
export function applyBrand(color?: string) {
  const c = /^#[0-9a-f]{6}$/i.test(color ?? '') ? color! : SIGNATURE
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', c)
  document.querySelector('link[rel="icon"]')?.setAttribute('href', brandLogo(c))
}

export const lightTheme: Theme = themesFor(SIGNATURE).light
export const darkTheme: Theme = themesFor(SIGNATURE).dark

export type ThemeChoice = 'system' | 'light' | 'dark'

const KEY = 'dmoffice.theme'

export function storedTheme(): ThemeChoice {
  try { return (localStorage.getItem(KEY) as ThemeChoice) || 'system' } catch { return 'system' }
}

export function storeTheme(t: ThemeChoice) {
  try { localStorage.setItem(KEY, t) } catch { /* privater Modus */ }
  window.dispatchEvent(new Event('dmoffice:theme'))
}

/** Hell oder dunkel - nach Wahl, oder wie das System. */
export function useIsDark(): boolean {
  const media = typeof window !== 'undefined' ? window.matchMedia('(prefers-color-scheme: dark)') : null
  const [choice, setChoice] = useState<ThemeChoice>(storedTheme())
  const [systemDark, setSystemDark] = useState(media?.matches ?? false)
  useEffect(() => {
    const onTheme = () => setChoice(storedTheme())
    const onMedia = (e: MediaQueryListEvent) => setSystemDark(e.matches)
    window.addEventListener('dmoffice:theme', onTheme)
    media?.addEventListener('change', onMedia)
    return () => {
      window.removeEventListener('dmoffice:theme', onTheme)
      media?.removeEventListener('change', onMedia)
    }
  }, [])
  return choice === 'dark' || (choice === 'system' && systemDark)
}
