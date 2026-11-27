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

const useRowStyles = makeStyles({
  // Waagerechte Griffleiste unter einem Bereich (Liste über dem Lesebereich); wie split, nur quer
  split: {
    height: '7px', margin: '-3px 0', flexShrink: 0, alignSelf: 'stretch', cursor: 'row-resize', zIndex: 5, position: 'relative',
    touchAction: 'none',
    ':hover': { backgroundImage: `linear-gradient(to bottom, transparent 3px, ${tokens.colorBrandStroke1} 3px, ${tokens.colorBrandStroke1} 4px, transparent 4px)` },
  },
  active: { backgroundImage: `linear-gradient(to bottom, transparent 3px, ${tokens.colorBrandStroke1} 3px, ${tokens.colorBrandStroke1} 4px, transparent 4px)` },
})

/**
 * Höhe eines Bereichs (Liste über dem Lesebereich), mit der Maus verstellbar und gemerkt wie die Breite (prefs.paneWidths[key]).
 * Ohne gemerkten Wert: height undefined - es gilt die Vorgabe aus dem CSS (etwa 42 %). Doppelklick: zurück zur Vorgabe.
 * Die Leiste (handle) gehört direkt unter den Bereich; sie misst beim Ziehen dessen aktuelle Höhe.
 */
export function usePaneHeight(key: string, min = 120) {
  const { prefs, update } = usePrefs()
  const mobile = useIsMobile()
  const [live, setLive] = useState<number | null>(null)
  // Oben und unten bleibt Platz für den anderen Bereich
  const clamp = (h: number) => Math.max(min, Math.min(window.innerHeight - 200, h))
  const saved = prefs.paneWidths?.[key]
  const height = live ?? (saved != null ? clamp(saved) : undefined)
  const save = (h: number | null) => {
    const next = { ...(prefs.paneWidths ?? {}) }
    if (h == null) delete next[key]; else next[key] = Math.round(h)
    update({ paneWidths: next })
  }
  const handle = () => mobile ? null : (
    <RowSplitter clamp={clamp} onLive={setLive} onDone={h => { setLive(null); save(h) }} onReset={() => { setLive(null); save(null) }} />
  )
  return { height: mobile ? undefined : height, handle }
}

function RowSplitter({ clamp, onLive, onDone, onReset }: {
  clamp: (h: number) => number; onLive: (h: number) => void; onDone: (h: number) => void; onReset: () => void
}) {
  const s = useRowStyles()
  const drag = useRef<{ y: number; h: number; now: number } | null>(null)
  const [on, setOn] = useState(false)
  return (
    <div className={mergeClasses(s.split, on && s.active)} role="separator" aria-orientation="horizontal"
      title={t('Höhe ziehen · Doppelklick: Vorgabe')}
      onPointerDown={e => {
        if (e.button !== 0) return
        // Bereich darüber: die Leiste folgt ihm direkt
        const above = e.currentTarget.previousElementSibling as HTMLElement | null
        if (!above) return
        e.preventDefault()
        e.currentTarget.setPointerCapture(e.pointerId)
        const h = above.getBoundingClientRect().height
        drag.current = { y: e.clientY, h, now: h }
        setOn(true)
        document.body.style.userSelect = 'none'
      }}
      onPointerMove={e => {
        const d = drag.current
        if (!d) return
        d.now = clamp(d.h + e.clientY - d.y)
        onLive(d.now)
      }}
      onPointerUp={() => {
        const d = drag.current
        drag.current = null
        setOn(false)
        document.body.style.userSelect = ''
        if (d && d.now !== d.h) onDone(d.now)
      }}
      onDoubleClick={onReset} />
  )
}
