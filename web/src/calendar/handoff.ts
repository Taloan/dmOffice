/**
 * Kalender-Abo aus einem Link (webcal:, webcals:, Kennung linkhandler): Die Adresse wird hier abgelegt, der Kalender öffnet
 * beim nächsten Anzeigen den Dialog „Kalender abonnieren“ mit ihr.
 */
let pending: string | null = null

export const SUBSCRIBE_EVENT = 'dmoffice:subscribe'

export function handOverSubscribe(url: string) {
  pending = url
  // Schon im Kalender: kein hashchange - der Kalender hört auf das Ereignis
  if (location.hash.startsWith('#/calendar')) window.dispatchEvent(new Event(SUBSCRIBE_EVENT))
  else location.hash = '#/calendar'
}

export function takeSubscribe(): string | null {
  const p = pending
  pending = null
  return p
}
