import type { ComposeInit } from './Compose'

/**
 * Entwurf aus einem anderen Bereich (Belege aus Finanzen mit PDF und Anhängen): wird hier abgelegt, der Mailbereich öffnet ihn beim
 * nächsten Anzeigen im Fenster „Neue E-Mail“. Dateien gehen nicht über sessionStorage, daher im Speicher dieser Seite.
 */
let pending: ComposeInit | null = null

export function handOverCompose(init: ComposeInit) {
  pending = init
  location.hash = '#/mail'
}

export function takeCompose(): ComposeInit | null {
  const p = pending
  pending = null
  return p
}
