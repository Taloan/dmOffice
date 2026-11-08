import { Text, tokens } from '@fluentui/react-components'
import { ErrorCircle20Filled, Warning20Filled } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { t } from '../i18n'
import { fmt, Food, Targets } from './api'

interface Warning { kind: 'allergen' | 'intolerance'; key: string; label: string; found: string[]; known: boolean }

/**
 * Passt es zu mir? Allergien (rot) und Unverträglichkeiten (orange) aus dem Profil, mit den gefundenen Zutaten.
 * „Laut Hersteller“: von Open Food Facts als Allergen angegeben; sonst Wörter in Name und Zutaten - ein Hinweis, keine Garantie.
 */
export function FoodWarnings({ food }: { food: Food }) {
  const [list, setList] = useState<Warning[]>([])
  useEffect(() => { api.get<{ warnings: Warning[] }>(`/api/coach/foods/${encodeURIComponent(food.id)}/check`).then(r => setList(r.warnings)).catch(() => setList([])) }, [food.id])
  if (!list.length) return null
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6, margin: '8px 0' }}>
      {list.map(w => {
        const red = w.kind === 'allergen'
        const bg = red ? tokens.colorPaletteRedBackground2 : tokens.colorPaletteDarkOrangeBackground2
        const fg = red ? tokens.colorPaletteRedForeground2 : tokens.colorPaletteDarkOrangeForeground2
        return (
          <div key={w.kind + w.key} style={{ display: 'flex', gap: 10, alignItems: 'flex-start', padding: '8px 10px', borderRadius: 8, background: bg, color: fg }}>
            {red ? <ErrorCircle20Filled /> : <Warning20Filled />}
            <div style={{ minWidth: 0 }}>
              <Text weight="semibold" block style={{ color: fg }}>
                {red ? t('Allergie: {name}', { name: w.label }) : t('Unverträglichkeit: {name}', { name: w.label })}
                {w.known && <span style={{ fontWeight: 400 }}> · {t('laut Hersteller')}</span>}
              </Text>
              {w.found.length > 0 && <div style={{ display: 'flex', flexWrap: 'wrap', gap: 4, marginTop: 4 }}>
                {w.found.map(f => <span key={f} style={{ fontSize: tokens.fontSizeBase200, padding: '0 6px', borderRadius: 6, border: `1px solid ${fg}` }}>{f}</span>)}
              </div>}
            </div>
          </div>
        )
      })}
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Erkannt an Angaben des Herstellers und an Wörtern in Name und Zutaten – bitte die Packung prüfen.')}</Text>
    </div>
  )
}

// Ampel je 100 g bzw. 100 ml nach den Grenzwerten der britischen Lebensmittelbehörde (FSA, Front-of-pack)
const LIMITS = {
  food: { fat: [3, 17.5], satFat: [1.5, 5], sugar: [5, 22.5], salt: [0.3, 1.5] },
  drink: { fat: [1.5, 8.75], satFat: [0.75, 2.5], sugar: [2.5, 11.25], salt: [0.3, 0.75] },
}

/** Nährwertampel je 100 g (Fett, gesättigte Fettsäuren, Zucker, Salz) und die gewählte Menge mit Anteil am Tagesziel. */
export function FoodFacts({ food, grams, targets }: { food: Food; grams: number; targets: Targets | null }) {
  const n = food.per100
  const lim = food.liquid ? LIMITS.drink : LIMITS.food
  const level = (v: number, [low, high]: number[]) => v <= low ? 0 : v > high ? 2 : 1
  const colors = [tokens.colorPaletteGreenForeground1, tokens.colorPaletteMarigoldForeground1, tokens.colorPaletteRedForeground1]
  const words = [t('niedrig'), t('mittel'), t('hoch')]
  const unit = food.liquid ? '100 ml' : '100 g'
  const rows: [string, number | null | undefined, number[]][] = [
    [t('coach|Fett'), n.fat, lim.fat], [t('davon gesättigt'), n.satFat, lim.satFat], [t('Zucker'), n.sugar, lim.sugar], [t('Salz'), n.salt, lim.salt],
  ]
  const f = grams > 0 ? grams / 100 : 0
  // Tagesanteil: eigene Ziele; Zucker höchstens 10 % der Energie (WHO), Salz 6 g (DGE)
  const share: [string, number, number | null, string][] = [
    ['kcal', n.kcal * f, targets?.kcal ?? null, ''],
    [t('Eiweiß'), n.protein * f, targets?.protein ?? null, 'g'],
    [t('coach|Fett'), n.fat * f, targets?.fat ?? null, 'g'],
    [t('Kohlenhydrate'), n.carbs * f, targets?.carbs ?? null, 'g'],
    ...(n.fiber != null ? [[t('Ballaststoffe'), n.fiber * f, targets?.fiber ?? null, 'g'] as [string, number, number | null, string]] : []),
    ...(n.sugar != null ? [[t('Zucker'), n.sugar * f, targets?.kcal ? targets.kcal * 0.1 / 4 : null, 'g'] as [string, number, number | null, string]] : []),
    ...(n.salt != null ? [[t('Salz'), n.salt * f, 6, 'g'] as [string, number, number | null, string]] : []),
  ]
  const shown = rows.filter(r => r[1] != null)
  return (
    <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(220px, 1fr))', gap: 14, margin: '10px 0' }}>
      {shown.length > 0 && <div>
        <Text weight="semibold" size={200} block>{t('Je {unit}', { unit })}</Text>
        {shown.map(([label, v, l]) => {
          const lv = level(v!, l)
          return (
            <div key={label} style={{ marginTop: 6 }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', fontSize: tokens.fontSizeBase200 }}>
                <span>{label}</span><span style={{ color: colors[lv] }}>{fmt(v!, 1)} g · {words[lv]}</span>
              </div>
              <div style={{ height: 5, borderRadius: 3, background: tokens.colorNeutralStroke2, overflow: 'hidden' }}>
                <div style={{ height: '100%', width: `${Math.min(100, v! / (l[1] * 1.3) * 100)}%`, background: colors[lv] }} />
              </div>
            </div>
          )
        })}
      </div>}
      <div>
        <Text weight="semibold" size={200} block>{t('Diese Menge vom Tagesziel')}</Text>
        {share.map(([label, v, goal, u]) => (
          <div key={label} style={{ display: 'grid', gridTemplateColumns: '1fr auto 52px', gap: 8, fontSize: tokens.fontSizeBase200, padding: '3px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` }}>
            <span>{label}</span>
            <span style={{ fontVariantNumeric: 'tabular-nums' }}>{fmt(v, u ? 1 : 0)}{u ? ' ' + u : ''}</span>
            <span style={{ textAlign: 'right', color: tokens.colorNeutralForeground3, fontVariantNumeric: 'tabular-nums' }}>{goal ? fmt(Math.round(v / goal * 100)) + ' %' : '–'}</span>
          </div>
        ))}
      </div>
    </div>
  )
}
