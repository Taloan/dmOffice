import { Text, tokens } from '@fluentui/react-components'
import { Checkmark16Regular } from '@fluentui/react-icons'
import { type ReactNode, useEffect, useRef, useState } from 'react'
import { featureOn } from '../api'
import { t } from '../i18n'

// Kennung savemode: In den Einstellungen wird sofort gespeichert. Schalter speichern beim Umlegen, Text- und
// Zahlenfelder beim Verlassen oder mit Enter. Rückmeldung „Gespeichert“ an festem Platz unten rechts im
// Arbeitsbereich; nichts verschiebt den Inhalt. Ein gemeinsamer Baustein für alle Seiten der Einstellungen.

/** Ereignis nach jedem erfolgreichen Schreiben (api.ts sendet es bei PUT/PATCH, useAutoSave nach dem Speichern). */
export const SAVED_EVENT = 'dmoffice:saved'

const typing = (el: Element | null): el is HTMLInputElement | HTMLTextAreaElement =>
  el instanceof HTMLTextAreaElement
  || (el instanceof HTMLInputElement && !['checkbox', 'radio', 'range', 'button', 'submit', 'reset', 'file'].includes(el.type))

/**
 * Speichert von selbst, sobald dirty ist: steht der Fokus in einem Text- oder Zahlenfeld, erst beim Verlassen
 * des Felds oder mit Enter, sonst sofort. Liefert save false (oder wirft), bleibt der Stand stehen und wird
 * erst nach der nächsten Änderung wieder versucht. Verlässt man die Seite mit offenen Änderungen, wird gespeichert.
 * key: Stand der Änderung (für „nicht noch einmal denselben Fehlversuch“). Liefert, ob sofort gespeichert wird.
 */
export function useAutoSave(key: unknown, dirty: boolean, save: () => Promise<unknown>, on = featureOn('savemode')): boolean {
  const ref = useRef({ key, dirty, save, on })
  ref.current = { key, dirty, save, on }
  const busy = useRef(false)
  const failed = useRef<string | null>(null)
  const run = useRef(async () => {
    const c = ref.current
    if (!c.on || busy.current || !c.dirty) return
    const k = JSON.stringify(c.key) ?? ''
    if (failed.current === k) return
    busy.current = true
    try {
      const ok = await c.save().then(r => r !== false, () => false)
      failed.current = ok ? null : k
      if (ok) window.dispatchEvent(new Event(SAVED_EVENT))
    } finally { busy.current = false }
  }).current
  useEffect(() => {
    if (!on || !dirty) return
    const el = document.activeElement
    if (!typing(el)) { void run(); return }
    const go = () => void run()
    const enter = (e: Event) => { if ((e as KeyboardEvent).key === 'Enter' && el instanceof HTMLInputElement) go() }
    el.addEventListener('blur', go)
    el.addEventListener('keydown', enter)
    return () => { el.removeEventListener('blur', go); el.removeEventListener('keydown', enter) }
  })
  // Seite verlassen, während ein Feld noch offen ist: trotzdem speichern
  useEffect(() => () => { void run() }, [run])
  return on
}

/** Rückmeldung „Gespeichert“ unten rechts, über dem Inhalt schwebend (verschiebt nichts); etwa zwei Sekunden lang. */
function SavedNote() {
  const [shown, setShown] = useState(false)
  useEffect(() => {
    let timer = 0
    const show = () => { setShown(true); clearTimeout(timer); timer = window.setTimeout(() => setShown(false), 2000) }
    window.addEventListener(SAVED_EVENT, show)
    return () => { window.removeEventListener(SAVED_EVENT, show); clearTimeout(timer) }
  }, [])
  return (
    <div role="status" aria-live="polite" style={{
      position: 'absolute', right: 24, bottom: 12, pointerEvents: 'none', display: 'flex', alignItems: 'center', gap: 4,
      padding: '4px 10px', borderRadius: tokens.borderRadiusMedium, background: tokens.colorNeutralBackground1,
      boxShadow: tokens.shadow4, color: tokens.colorPaletteGreenForeground1,
      opacity: shown ? 1 : 0, transition: 'opacity 200ms',
    }}>
      {shown && <><Checkmark16Regular /><Text size={200} style={{ color: 'inherit' }}>{t('Gespeichert')}</Text></>}
    </div>
  )
}

/** Rahmen um den Arbeitsbereich der Einstellungen: fester Platz für „Gespeichert“. Ohne Kennung nur der Inhalt. */
export function SavedFrame({ children }: { children: ReactNode }) {
  if (!featureOn('savemode')) return <>{children}</>
  return (
    <div style={{ flex: 1, minWidth: 0, minHeight: 0, position: 'relative', display: 'flex', flexDirection: 'column' }}>
      {children}
      <SavedNote />
    </div>
  )
}
