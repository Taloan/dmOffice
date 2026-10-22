import { Button, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { ArrowLeft20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { t } from '../i18n'

// Handybreite: die Oberflaeche arbeitet einspaltig wie eine App - Navigation als ausklappbare Leiste,
// Inhalt in voller Breite, Einzelansichten im Vollbild mit Zurueck. Ab 768 px bleibt alles wie am Desktop.
const QUERY = '(max-width: 767px)'

/** true auf schmalen Bildschirmen (Handy). Folgt Drehen und Groessenaenderung. */
export function useIsMobile(): boolean {
  const [on, setOn] = useState(() => window.matchMedia(QUERY).matches)
  useEffect(() => {
    const mq = window.matchMedia(QUERY)
    const change = () => setOn(mq.matches)
    change()
    mq.addEventListener('change', change)
    return () => mq.removeEventListener('change', change)
  }, [])
  return on
}

const useStyles = makeStyles({
  // Schwebt ueber dem Inhalt; der Bereich darunter (position: relative) begrenzt sie
  drawer: {
    position: 'absolute', top: 0, bottom: 0, left: 0, zIndex: 20, width: 'min(86vw, 320px)', maxWidth: 'none',
    backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow28, overflowY: 'auto', borderRight: 'none',
  },
  backdrop: { position: 'absolute', inset: 0, zIndex: 19, backgroundColor: 'rgba(0,0,0,.25)' },
  back: {
    display: 'flex', alignItems: 'center', gap: '4px', padding: '4px 4px 4px 0', minHeight: '44px', flexShrink: 0,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, backgroundColor: tokens.colorNeutralBackground1,
  },
  backTitle: { flex: 1, minWidth: 0, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  // Einzelansicht im Vollbild: deckt Liste und Navigation des Bereichs ab
  full: {
    position: 'absolute', inset: 0, zIndex: 15, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0,
    backgroundColor: tokens.colorNeutralBackground1,
  },
})

/**
 * Navigationsspalte eines Bereichs. Am Desktop wie bisher links daneben (ein-/ausblendbar),
 * auf dem Handy als Leiste, die ueber dem Inhalt aufklappt; ein Tipp daneben schliesst sie.
 */
export function SidePane({ open, onClose, className, children, as = 'div', style }: {
  open: boolean; onClose: () => void; className?: string; children: React.ReactNode; as?: 'div' | 'aside'; style?: React.CSSProperties
}) {
  const s = useStyles()
  const mobile = useIsMobile()
  const Tag = as
  if (!mobile) return <Tag className={className} style={open ? style : { ...style, display: 'none' }}>{children}</Tag>
  // Tipp auf einen Eintrag (Ordner, Liste ...) schliesst die Leiste; Knoepfe, Felder und Haekchen nicht
  const pick = (e: React.MouseEvent) => {
    const el = e.target as HTMLElement
    // Klicks aus Menues/Dialogen (Portale) kommen auch hier an - die zaehlen nicht
    if (!(e.currentTarget as HTMLElement).contains(el)) return
    if (!el.closest('button, input, textarea, select, label, a, [role=menuitem], [role=checkbox], [contenteditable=true]')) onClose()
  }
  // Zu: nur verstecken, nicht entfernen - Inhalte wie versteckte Datei-Felder bleiben erreichbar
  return (
    <>
      {open && <div className={s.backdrop} onClick={onClose} />}
      <Tag className={mergeClasses(className, s.drawer)} style={open ? style : { ...style, display: 'none' }} onClick={pick}>{children}</Tag>
    </>
  )
}

/** Kopf einer Einzelansicht im Vollbild: Zurueck-Knopf, Titel, rechts eigene Knoepfe. */
export function BackBar({ onBack, title, children, label }: { onBack: () => void; title?: React.ReactNode; children?: React.ReactNode; label?: string }) {
  const s = useStyles()
  return (
    <div className={s.back}>
      <Button appearance="subtle" icon={<ArrowLeft20Regular />} onClick={onBack} aria-label={label ?? t('Zurück')}>{title ? undefined : label ?? t('Zurück')}</Button>
      {title && <div className={s.backTitle}>{title}</div>}
      {!title && <div style={{ flex: 1 }} />}
      {children}
    </div>
  )
}

/** Klasse fuer die Vollbild-Ebene (s.full) - auf dem Handy an die Einzelansicht haengen. */
export const useMobileStyles = useStyles

/**
 * Handy: eine offene Einzelansicht schliesst auch mit der Zurueck-Taste des Geraets bzw. Browsers.
 * Beim Oeffnen kommt ein Eintrag in den Verlauf; Zurueck nimmt ihn wieder heraus.
 */
export function useBackClose(open: boolean, close: () => void) {
  const mobile = useIsMobile()
  const closeRef = useRef(close)
  closeRef.current = close
  useEffect(() => {
    if (!mobile || !open) return
    // Tiefe mitzaehlen, damit Zurueck nur die oberste Ansicht schliesst
    const depth = (Number(history.state?.dmBack) || 0) + 1
    history.pushState({ dmBack: depth }, '')
    let popped = false
    const onPop = () => {
      if ((Number(history.state?.dmBack) || 0) >= depth) return
      popped = true
      closeRef.current()
    }
    window.addEventListener('popstate', onPop)
    return () => {
      window.removeEventListener('popstate', onPop)
      // Mit dem Knopf geschlossen: den eigenen Verlaufseintrag wieder abbauen (nicht nach einem Bereichswechsel)
      if (!popped && history.state?.dmBack === depth) history.back()
    }
  }, [mobile, open])
}
