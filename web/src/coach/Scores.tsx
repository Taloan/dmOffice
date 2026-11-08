import { tokens } from '@fluentui/react-components'
import { t } from '../i18n'
import { OffInfo } from './api'

// Farben wie auf den Packungen: dunkelgrün, hellgrün, gelb, orange, rot
const NUTRI = ['#038141', '#85bb2f', '#fecb02', '#ee8100', '#e63e11']
const LETTERS = ['a', 'b', 'c', 'd', 'e']

/** Nutri-Score bzw. Eco-Score als Buchstabenreihe, die eigene Note hervorgehoben. */
export function ScoreBar({ grade, label, small }: { grade: string; label: string; small?: boolean }) {
  const h = small ? 14 : 22, w = small ? 10 : 18
  return (
    <span title={`${label}: ${grade.toUpperCase()}`} aria-label={`${label}: ${grade.toUpperCase()}`} style={{ display: 'inline-flex', flexDirection: 'column', gap: 2 }}>
      {!small && <span style={{ fontSize: 9, letterSpacing: 0.5, color: tokens.colorNeutralForeground3, fontWeight: 600 }}>{label.toUpperCase()}</span>}
      <span style={{ display: 'inline-flex', alignItems: 'center' }}>
        {LETTERS.map((l, i) => {
          const on = l === grade
          return <span key={l} style={{
            width: on ? w * 1.4 : w, height: on ? h * 1.25 : h, background: NUTRI[i], color: 'white', fontWeight: 700, fontSize: on ? (small ? 11 : 15) : (small ? 8 : 11),
            display: 'inline-flex', alignItems: 'center', justifyContent: 'center', borderRadius: on ? 4 : 0, opacity: on ? 1 : 0.55,
            boxShadow: on ? '0 0 0 2px white' : undefined, zIndex: on ? 1 : 0,
          }}>{l.toUpperCase()}</span>
        })}
      </span>
    </span>
  )
}

const NOVA = ['', '#038141', '#fecb02', '#ee8100', '#e63e11']

/** Nutri-Score, Eco-Score, NOVA und Merkmale eines Produkts (Open Food Facts). */
export function OffBadges({ info }: { info: OffInfo }) {
  const flag = (f: string) => ({ 'palm-oil': t('Enthält Palmöl'), 'palm-oil-free': t('Ohne Palmöl'), vegan: t('Vegan'), vegetarian: t('Vegetarisch'),
    'gluten-free': t('Glutenfrei'), 'lactose-free': t('Laktosefrei'), organic: t('Bio') } as Record<string, string>)[f] ?? f
  return (
    <div style={{ display: 'flex', flexWrap: 'wrap', gap: 14, alignItems: 'flex-end', margin: '8px 0' }}>
      {info.nutri && <ScoreBar grade={info.nutri} label="Nutri-Score" />}
      {info.eco && <ScoreBar grade={info.eco} label="Eco-Score" />}
      {info.nova && <span title={t('NOVA {n}: Verarbeitungsgrad (1 unverarbeitet … 4 stark verarbeitet)', { n: info.nova })}
        style={{ display: 'inline-flex', flexDirection: 'column', gap: 2 }}>
        <span style={{ fontSize: 9, letterSpacing: 0.5, color: tokens.colorNeutralForeground3, fontWeight: 600 }}>NOVA</span>
        <span style={{ width: 24, height: 26, borderRadius: 4, background: NOVA[info.nova], color: 'white', fontWeight: 700, display: 'inline-flex', alignItems: 'center', justifyContent: 'center' }}>{info.nova}</span>
      </span>}
      {info.flags.length > 0 && <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
        {info.flags.map(f => <span key={f} style={{ fontSize: tokens.fontSizeBase200, padding: '2px 8px', borderRadius: 999,
          background: f === 'palm-oil' ? tokens.colorPaletteRedBackground2 : tokens.colorPaletteGreenBackground2,
          color: f === 'palm-oil' ? tokens.colorPaletteRedForeground2 : tokens.colorPaletteGreenForeground2 }}>{flag(f)}</span>)}
      </div>}
    </div>
  )
}
