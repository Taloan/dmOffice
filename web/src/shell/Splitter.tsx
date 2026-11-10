import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { useRef, useState } from 'react'
import { t } from '../i18n'
import { usePrefs } from '../prefs'
import { useIsMobile } from './mobile'

const useStyles = makeStyles({
  // Schmale Griffleiste zwischen zwei Bereichen; nimmt keinen Platz weg (negativer Rand), zeigt beim Zeigen eine Linie
  split: {
    width: '7px', margin: '0 -3px', flexShrink: 0, alignSelf: 'stretch', cursor: 'col-resize', zIndex: 5, position: 'relative',
    touchAction: 'none',
    ':hover': { backgroundImage: `linear-gradient(to right, transparent 3px, ${tokens.colorBrandStroke1} 3px, ${tokens.colorBrandStroke1} 4px, transparent 4px)` },
  },
  inside: { position: 'absolute', top: 0, bottom: 0, margin: 0 },
  active: { backgroundImage: `linear-gradient(to right, transparent 3px, ${tokens.colorBrandStroke1} 3px, ${tokens.colorBrandStroke1} 4px, transparent 4px)` },
})

/**
 * Breite eines Bereichs (Ordnerleiste, Liste, Detailbereich …), mit der Maus verstellbar und in den Einstellungen gemerkt
 * (prefs.paneWidths[key]). Doppelklick auf die Griffleiste: zurück zur Vorgabe. Auf dem Handy gibt es keine Griffleiste.
 * edge: auf welcher Seite des Bereichs die Leiste sitzt - "right" für Bereiche links, "left" für Bereiche rechts.
 */
export function usePaneWidth(key: string, def: number, min = 160, max = 720) {
  const { prefs, update } = usePrefs()
  const mobile = useIsMobile()
  const [live, setLive] = useState<number | null>(null)
  const clamp = (w: number) => Math.max(min, Math.min(max, w))
  const width = live ?? clamp(prefs.paneWidths?.[key] ?? def)
  const save = (w: number | null) => {
    const next = { ...(prefs.paneWidths ?? {}) }
    if (w == null) delete next[key]; else next[key] = Math.round(w)
    update({ paneWidths: next })
  }
  // inside: im Bereich selbst am Rand (der Bereich braucht position: relative und darf selbst nicht scrollen)
  const handle = (edge: 'right' | 'left', inside = false) => mobile ? null : (
    <Splitter edge={edge} inside={inside} width={width} clamp={clamp} onLive={setLive} onDone={w => { setLive(null); save(w) }} onReset={() => { setLive(null); save(null) }} />
  )
  return { width: mobile ? undefined : width, handle }
}

function Splitter({ edge, inside, width, clamp, onLive, onDone, onReset }: {
  edge: 'right' | 'left'; inside: boolean; width: number; clamp: (w: number) => number
  onLive: (w: number) => void; onDone: (w: number) => void; onReset: () => void
}) {
  const s = useStyles()
  const drag = useRef<{ x: number; w: number; now: number } | null>(null)
  const [on, setOn] = useState(false)
  return (
    <div className={mergeClasses(s.split, inside && s.inside, on && s.active)} style={inside ? (edge === 'right' ? { right: -3 } : { left: -3 }) : undefined}
      role="separator" aria-orientation="vertical"
      title={t('Breite ziehen · Doppelklick: Vorgabe')}
      onPointerDown={e => {
        if (e.button !== 0) return
        e.preventDefault()
        e.currentTarget.setPointerCapture(e.pointerId)
        drag.current = { x: e.clientX, w: width, now: width }
        setOn(true)
        document.body.style.userSelect = 'none'
      }}
      onPointerMove={e => {
        const d = drag.current
        if (!d) return
        d.now = clamp(d.w + (edge === 'right' ? 1 : -1) * (e.clientX - d.x))
        onLive(d.now)
      }}
      onPointerUp={() => {
        const d = drag.current
        drag.current = null
        setOn(false)
        document.body.style.userSelect = ''
        if (d && d.now !== d.w) onDone(d.now)
      }}
      onDoubleClick={onReset} />
  )
}
