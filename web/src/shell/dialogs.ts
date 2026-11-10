// Verhalten aller Dialoge an einer Stelle, statt in jedem der vielen Dialoge einzeln:
// - Zurück im Browser oder auf dem Gerät schließt den obersten Dialog (wie Esc) und lässt die Seite dahinter stehen.
// - Der Fokus geht nach dem Schließen zurück auf das Element, das den Dialog geöffnet hat.
// Dazu die englischen Vorgaben von Fluent an Auswahlfeldern, die Hilfsprogramme vorlesen, in der Sprache der Oberfläche.
import { t } from '../i18n'

const SURFACE = '.fui-DialogSurface'

const defaultLabels: [string, () => string][] = [
  ['.fui-Dropdown__clearButton[aria-label="Clear selection"], .fui-Combobox__clearIcon[aria-label="Clear selection"]', () => t('Auswahl leeren')],
  ['.fui-Dropdown__expandIcon[aria-label="Open"], .fui-Combobox__expandIcon[aria-label="Open"]', () => t('Liste öffnen')],
  ['.fui-Dropdown__expandIcon[aria-label="Close"], .fui-Combobox__expandIcon[aria-label="Close"]', () => t('Liste schließen')],
]
const nameControls = () => {
  for (const [selector, text] of defaultLabels) document.querySelectorAll(selector).forEach(el => el.setAttribute('aria-label', text()))
}

const topDialog = () => { const all = document.querySelectorAll<HTMLElement>(SURFACE); return all.length ? all[all.length - 1] : null }
const typingIn = (el: Element | null) => !!el && (['INPUT', 'TEXTAREA', 'SELECT'].includes(el.tagName) || (el as HTMLElement).isContentEditable)

let installed = false

export function installDialogBehaviour() {
  if (installed) return
  installed = true

  // ---------------- Fokus ----------------
  const openers = new Map<HTMLElement, HTMLElement>()
  let lastFocus: HTMLElement | null = null
  document.addEventListener('focusin', e => {
    const target = e.target as HTMLElement
    const surface = target.closest?.<HTMLElement>(SURFACE)
    if (surface && !openers.has(surface)) {
      const from = (e.relatedTarget as HTMLElement | null) ?? lastFocus
      if (from && !surface.contains(from)) openers.set(surface, from)
    }
    lastFocus = target
  }, true)
  const returnFocus = () => {
    for (const [surface, opener] of [...openers]) {
      if (surface.isConnected) continue
      openers.delete(surface)
      // Nach dem eigenen Rückweg von Fluent, der sonst beim letzten Popover-Auslöser der Seite landet
      window.setTimeout(() => {
        const top = topDialog()
        if (!opener.isConnected || !opener.getClientRects().length || (top && !top.contains(opener)) || typingIn(document.activeElement)) return
        opener.focus()
      }, 60)
    }
  }

  // ---------------- Zurück ----------------
  // Ein Verlaufseintrag, solange mindestens ein Dialog offen ist. Abgebaut wird verzögert: folgt gleich der nächste Dialog
  // (Rückfrage, dann Eingabe), bleibt der Eintrag stehen, sonst träfe das Zurück den neuen Dialog.
  let mine: number | null = null
  let mineAt = ''
  let leaving: number | undefined
  const sync = () => {
    returnFocus()
    nameControls()
    if (topDialog()) {
      window.clearTimeout(leaving); leaving = undefined
      if (mine == null) {
        mine = (Number(history.state?.dmBack) || 0) + 1
        mineAt = location.href
        history.pushState({ dmBack: mine }, '')
      }
    } else if (mine != null && leaving == null) {
      leaving = window.setTimeout(() => {
        leaving = undefined
        if (topDialog() || mine == null) return
        const depth = mine
        mine = null
        if (history.state?.dmBack === depth) history.back()
      }, 80)
    }
  }
  let queued = false
  new MutationObserver(() => {
    if (queued) return
    queued = true
    queueMicrotask(() => { queued = false; sync() })
  }).observe(document.body, { childList: true, subtree: true })

  window.addEventListener('popstate', () => {
    if (mine == null || (Number(history.state?.dmBack) || 0) >= mine) return
    mine = null
    // Eine neue Adresse aus dem Dialog heraus (Link darin) ist kein Zurück: der Eintrag darunter hat dieselbe Adresse
    if (location.href !== mineAt) return
    const top = topDialog()
    if (!top) return
    // Wie Esc: Rückfragen des Dialogs gelten weiter; bleibt er offen, kommt der Eintrag zurück
    top.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', code: 'Escape', bubbles: true, cancelable: true }))
    window.setTimeout(sync, 120)
  })
}
