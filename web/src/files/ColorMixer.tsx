import { Tooltip } from '@fluentui/react-components'
import { ArrowReset20Regular, Target20Regular } from '@fluentui/react-icons'
import { t } from '../i18n'

/** Farbbänder des Farbmischers mit ihrer Mitte (Farbton in Grad) - wie im Server (PhotoAdjust.Bands) und in CameraImporter. */
export const BANDS: [string, string, number][] = [['red', 'Rot', 0], ['orange', 'Orange', 30], ['yellow', 'Gelb', 60], ['green', 'Grün', 120],
  ['aqua', 'Aqua', 180], ['blue', 'Blau', 240], ['purple', 'Violett', 285], ['magenta', 'Magenta', 320]]
export type MixPart = 'h' | 's' | 'l'
export const mixKey = (p: MixPart, band: string) => `hsl_${p}_${band}`

/** Anteil jedes Bandes an einem Farbton (weich, 40° breit) - so verteilt das Zielwerkzeug das Ziehen auf die Bänder. */
export function bandWeights(hue: number): Record<string, number> {
  const w: Record<string, number> = {}
  for (const [b, , c] of BANDS) {
    const d = Math.abs(((hue - c + 180) % 360 + 360) % 360 - 180)
    const x = Math.max(0, 1 - d / 40)
    w[b] = x * x * (3 - 2 * x)
  }
  return w
}

/** Farbton (Grad) und Sättigung (0..1) einer Farbe. */
export function hueOf(r: number, g: number, b: number): [number, number] {
  const max = Math.max(r, g, b), min = Math.min(r, g, b), c = max - min
  if (c === 0 || max === 0) return [0, 0]
  let h = max === r ? ((g - b) / c) % 6 : max === g ? (b - r) / c + 2 : (r - g) / c + 4
  h *= 60
  return [h < 0 ? h + 360 : h, c / max]
}

const hsl = (deg: number, l = 52) => `hsl(${((deg % 360) + 360) % 360}, 75%, ${l}%)`
/** Spur des Reglers: Farbton zeigt die Richtung, Sättigung von Grau zur Farbe, Luminanz von Dunkel über die Farbe zu Hell. */
const track = (p: MixPart, c: number) => p === 'h' ? `linear-gradient(90deg, ${[-50, -25, 0, 25, 50].map(o => hsl(c + o)).join(', ')})`
  : p === 's' ? `linear-gradient(90deg, #808080, ${hsl(c)})` : `linear-gradient(90deg, #1a1a1a, ${hsl(c)}, #f0f0f0)`

const CSS = `.dm-mix{-webkit-appearance:none;appearance:none;width:100%;height:6px;border-radius:3px;outline:none;margin:0;cursor:pointer}
.dm-mix::-webkit-slider-thumb{-webkit-appearance:none;width:12px;height:14px;border-radius:3px;background:#eee;border:1px solid #101010}
.dm-mix::-moz-range-thumb{width:12px;height:14px;border-radius:3px;background:#eee;border:1px solid #101010}`

/**
 * Farbmischer wie in Lightroom: Reiter Farbton, Sättigung, Luminanz, je Farbband ein Regler (−100 … +100). Das Zielwerkzeug: im Bild
 * auf eine Farbe drücken und hoch oder runter ziehen - die Bänder dieser Farbe ändern sich anteilig (der Editor ruft dazu onTarget).
 */
export function ColorMixer({ values, part, onPart, onChange, target, onTarget }: {
  values: Record<string, number>; part: MixPart; onPart: (p: MixPart) => void
  onChange: (key: string, v: number) => void; target: boolean; onTarget: (on: boolean) => void
}) {
  const tab = (p: MixPart, label: string) => <button key={p} onClick={() => onPart(p)} style={{
    background: 'rgba(255,255,255,.08)', color: 'inherit', border: `1px solid ${part === p ? '#fff' : 'rgba(255,255,255,.3)'}`, borderRadius: 4,
    fontSize: 11, fontWeight: part === p ? 700 : 400, padding: '2px 8px', cursor: 'pointer',
  }}>{label}</button>
  const tool = { background: 'rgba(255,255,255,.08)', color: 'inherit', borderRadius: 4, width: 24, height: 22, padding: 0, cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center' }
  return <>
    <style>{CSS}</style>
    <div style={{ display: 'flex', gap: 4, alignItems: 'center' }}>
      {tab('h', t('Farbton'))}{tab('s', t('Sättigung'))}{tab('l', t('Luminanz'))}
      <span style={{ flex: 1 }} />
      <Tooltip content={t('Zielwerkzeug: im Bild auf eine Farbe drücken und hoch oder runter ziehen')} relationship="label">
        <button onClick={() => onTarget(!target)} style={{ ...tool, border: `1px solid ${target ? '#fff' : 'rgba(255,255,255,.3)'}`, background: target ? 'rgba(255,255,255,.25)' : tool.background }}>
          <Target20Regular style={{ width: 16, height: 16 }} /></button>
      </Tooltip>
      <Tooltip content={t('Diesen Reiter zurücksetzen')} relationship="label">
        <button onClick={() => BANDS.forEach(([b]) => onChange(mixKey(part, b), 0))} style={{ ...tool, border: '1px solid rgba(255,255,255,.3)' }}>
          <ArrowReset20Regular style={{ width: 16, height: 16 }} /></button>
      </Tooltip>
    </div>
    {BANDS.map(([b, name, c]) => {
      const k = mixKey(part, b), v = values[k] ?? 0
      return <div key={k} onDoubleClick={() => onChange(k, 0)} title={t(name) + ' · ' + t('Doppelklick: auf 0')}
        style={{ display: 'grid', gridTemplateColumns: '14px minmax(0, 1fr) 34px', alignItems: 'center', gap: 6, fontSize: 12, padding: '3px 0' }}>
        <span style={{ width: 12, height: 12, borderRadius: 6, background: hsl(c), border: '1px solid rgba(255,255,255,.3)' }} />
        <input type="range" className="dm-mix" min={-100} max={100} step={1} value={v} aria-label={t(name)}
          style={{ background: track(part, c) }} onChange={e => onChange(k, +e.target.value)} />
        <span style={{ textAlign: 'right', opacity: 0.8, fontVariantNumeric: 'tabular-nums' }}>{v > 0 ? '+' + v : v}</span>
      </div>
    })}
  </>
}
