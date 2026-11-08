import '@fontsource/caveat-brush/400.css'
import { useState } from 'react'
import { makeStyles } from '@fluentui/react-components'
import { t } from '../i18n'
import type { RecipeNutrition } from './api'

// Rezeptkarte wie gedruckt: helles Papier, Olivgrün, Pinselstrich-Überschriften (auch im dunklen Design hell - es ist eine Karte)
const ACCENT = '#5F7A34'
const PAPER = '#F6F1E6'
const INK = '#2A2620'
const MUTED = '#5C5548'
// Pinselstrich als Fläche hinter Überschriften
const BRUSH = 'M14 22 C80 8 200 16 300 10 C400 5 500 12 588 4 L596 30 L584 48 L598 70 L586 94 L594 120 C500 132 380 124 280 130 C180 136 90 128 10 134 L18 112 L4 90 L16 66 L2 44 Z'
const HAND = "'Caveat Brush', 'Segoe Print', cursive"

const useStyles = makeStyles({
  card: { background: PAPER, color: INK, borderRadius: '12px', overflow: 'hidden', position: 'relative' },
  hero: { position: 'relative', height: '300px', background: '#DCD3BF' },
  // Ohne Foto: nur Platz für den Titel
  heroBare: { height: '150px' },
  heroImg: { width: '100%', height: '100%', objectFit: 'cover', display: 'block' },
  wave: { position: 'absolute', left: 0, right: 0, bottom: '-1px', width: '100%', height: '48px' },
  title: { position: 'absolute', left: '20px', top: '20px', maxWidth: 'calc(100% - 40px)', padding: '14px 36px 18px', color: '#fff',
    fontFamily: HAND, fontSize: '52px', lineHeight: 1, margin: 0, fontWeight: 400 },
  brush: { position: 'absolute', inset: 0, width: '100%', height: '100%', zIndex: 0 },
  brushText: { position: 'relative', zIndex: 1 },
  tiles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(118px, 1fr))', gap: '10px', margin: '-34px 20px 0', position: 'relative' },
  tile: { background: '#EEF1DF', border: '1px solid #D3DBB5', borderRadius: '14px', padding: '12px 14px', boxShadow: '0 6px 18px rgba(60,50,30,.14)',
    display: 'flex', flexDirection: 'column', gap: '4px' },
  tileLabel: { fontSize: '12px', color: MUTED, lineHeight: 1.25 },
  tileValue: { fontSize: '19px', fontWeight: 800, lineHeight: 1.1 },
  body: { display: 'grid', gridTemplateColumns: 'minmax(0, 2fr) 1px minmax(0, 3fr)', gap: '28px', padding: '28px 28px 20px',
    '@media (max-width: 640px)': { gridTemplateColumns: '1fr', gap: '20px' } },
  rule: { background: '#CFC6B2', '@media (max-width: 640px)': { display: 'none' } },
  head: { position: 'relative', display: 'inline-block', padding: '6px 26px 10px', color: '#fff', fontFamily: HAND, fontSize: '32px', lineHeight: 1, margin: '0 0 14px', fontWeight: 400 },
  list: { margin: 0, padding: 0, listStyle: 'none', display: 'flex', flexDirection: 'column', gap: '10px', fontSize: '15px' },
  steps: { margin: 0, padding: 0, listStyle: 'none', display: 'flex', flexDirection: 'column', gap: '16px', fontSize: '15px', lineHeight: 1.5 },
  num: { width: '30px', height: '30px', flexShrink: 0, borderRadius: '50%', background: ACCENT, color: '#fff', display: 'flex', alignItems: 'center',
    justifyContent: 'center', fontWeight: 800, fontSize: '15px' },
  servings: { display: 'flex', alignItems: 'center', gap: '10px', margin: '-4px 0 14px', fontSize: '14px', color: MUTED },
  step: { width: '28px', height: '28px', borderRadius: '50%', border: `1px solid ${ACCENT}`, background: 'transparent', color: ACCENT, fontSize: '17px',
    lineHeight: 1, cursor: 'pointer', ':disabled': { opacity: 0.35, cursor: 'default' } },
  times: { margin: '-4px 0 14px', fontSize: '14px', color: MUTED },
  foot: { padding: '0 28px 22px', fontSize: '12px', color: MUTED, display: 'flex', flexDirection: 'column', gap: '4px' },
})

function Brush({ className }: { className: string }) {
  return <svg aria-hidden="true" className={className} viewBox="0 0 600 140" preserveAspectRatio="none"><path d={BRUSH} fill={ACCENT} /></svg>
}

/** Symbole der Kacheln (Strichzeichnungen) */
const ICONS: Record<string, string> = {
  kcal: 'M7 2v7a2 2 0 0 0 2 2h0a2 2 0 0 0 2-2V2M9 11v11M18 2c-2 1.5-3 4-3 7h3v13',
  protein: 'M6.5 6.5v11M3.5 9v6M17.5 6.5v11M20.5 9v6M6.5 12h11',
  carbs: 'M12 22V8M12 8c0-3 2-5 4-6-.3 3-1.7 5-4 6zM12 8c0-3-2-5-4-6 .3 3 1.7 5 4 6zM12 14c2.6 0 4.5-1.5 5.5-4-2.6 0-4.5 1.5-5.5 4zM12 14c-2.6 0-4.5-1.5-5.5-4 2.6 0 4.5 1.5 5.5 4z',
  fat: 'M12 2.5C9 7 6 10.2 6 14a6 6 0 0 0 12 0c0-3.8-3-7-6-11.5z',
  time: 'M12 2.5a9.5 9.5 0 1 0 0 19a9.5 9.5 0 1 0 0-19M12 6.5V12l3.5 2.5',
}

const MAX_SERVINGS = 6

const fmt = (n: number, d = 0) => n.toLocaleString(undefined, { maximumFractionDigits: d })

/** Zutatenzeile: Menge fett, dann der Name („<b>½</b> rote Paprikaschote (80 g)“). */
function Line({ text, name }: { text: string; name: string }) {
  const at = text.indexOf(name)
  if (at <= 0) return <>{text}</>
  return <><strong>{text.slice(0, at).trim()}</strong> {text.slice(at)}</>
}

/** Basisrezept als Karte: Foto (oder Fläche), Titel, Nährwerte je Portion, Zutaten für 1 bis 6 Portionen, Zubereitung. */
export default function RecipeCard({ r }: { r: RecipeNutrition }) {
  const s = useStyles()
  const [servings, setServings] = useState(1)
  const b = r.base!
  const n = r.perServing
  const tiles: [string, string, string][] = [
    ['kcal', t('1 Portion ({g} g)', { g: fmt(r.weight) }), `${fmt(n.kcal)} kcal`],
    ['protein', t('Eiweiß'), `${fmt(n.protein, 1)} g`],
    ['carbs', t('Kohlenhydrate'), `${fmt(n.carbs, 1)} g`],
    ['fat', t('Fett'), `${fmt(n.fat, 1)} g`],
    ['time', t('Zubereitung'), t('ca. {n} Min.', { n: b.minutes })],
  ]
  return (
    <div className={s.card}>
      <div className={b.image ? s.hero : `${s.hero} ${s.heroBare}`}>
        {b.image && <img className={s.heroImg} src={b.image} alt={r.title} />}
        <svg aria-hidden="true" className={s.wave} viewBox="0 0 1080 90" preserveAspectRatio="none"><path d="M0 40 C180 10 360 62 560 38 C760 14 900 52 1080 26 L1080 90 L0 90 Z" fill={PAPER} /></svg>
        <h2 className={s.title}><Brush className={s.brush} /><span className={s.brushText}>{r.title}</span></h2>
      </div>
      <div className={s.tiles}>
        {tiles.map(([k, label, value]) => (
          <div key={k} className={s.tile}>
            <svg aria-hidden="true" width="26" height="26" viewBox="0 0 24 24" fill="none" stroke={ACCENT} strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round"><path d={ICONS[k]} /></svg>
            <span className={s.tileLabel}>{label}</span>
            <span className={s.tileValue}>{value}</span>
          </div>
        ))}
      </div>
      <div className={s.body}>
        <div>
          <h3 className={s.head}><Brush className={s.brush} /><span className={s.brushText}>{t('Zutaten')}</span></h3>
          <div className={s.servings}>
            <button type="button" className={s.step} aria-label={t('Eine Portion weniger')} disabled={servings <= 1} onClick={() => setServings(servings - 1)}>−</button>
            <span>{servings === 1 ? t('1 Portion') : t('{n} Portionen', { n: servings })}</span>
            <button type="button" className={s.step} aria-label={t('Eine Portion mehr')} disabled={servings >= MAX_SERVINGS} onClick={() => setServings(servings + 1)}>+</button>
          </div>
          <ul className={s.list}>
            {b.ingredients.map((i, k) => <li key={k}><Line text={i.texts[servings - 1] ?? i.name} name={i.name} /></li>)}
          </ul>
        </div>
        <div className={s.rule} />
        <div>
          <h3 className={s.head}><Brush className={s.brush} /><span className={s.brushText}>{t('Zubereitung')}</span></h3>
          <div className={s.times}>{b.cook > 0 ? t('Vorbereitung {p} Min. · Garzeit {c} Min.', { p: b.prep, c: b.cook }) : t('Vorbereitung {p} Min.', { p: b.prep })}</div>
          <ol className={s.steps}>
            {b.steps.map((x, k) => <li key={k} style={{ display: 'flex', gap: 14, alignItems: 'flex-start' }}><span className={s.num}>{k + 1}</span><span>{x}</span></li>)}
          </ol>
        </div>
      </div>
      <div className={s.foot}>
        {b.tip && <span>{t('Tipp: {t}', { t: b.tip })}</span>}
        <span>{t('Je Portion außerdem: Ballaststoffe {f} g · Zucker {s} g · Salz {salt} g',
          { f: fmt(n.fiber ?? 0, 1), s: fmt(n.sugar ?? 0, 1), salt: fmt(n.salt ?? 0, 1) })}</span>
        <span>{b.allergens.length ? t('Allergene: {a}', { a: b.allergens.map(a => t(a)).join(', ') }) : t('Allergene: keine der 14 Hauptallergene')}</span>
        {b.hint && <span>{b.hint}</span>}
        <span>{t('Nährwerte berechnet nach dem Bundeslebensmittelschlüssel (BLS 4.0); Zutaten und Allergene vor dem Verzehr prüfen.')}</span>
      </div>
    </div>
  )
}
