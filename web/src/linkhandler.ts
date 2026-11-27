import { api, defaultAccount, featureOn } from './api'
import { handOverCompose } from './mail/handoff'
import { handOverSubscribe } from './calendar/handoff'
import { t } from './i18n'

/**
 * dmOffice als Programm für E-Mail-Links (mailto:) und Kalender-Abos (webcal:, webcals:) - Kennung linkhandler.
 * Browser und installierte App rufen /?handle=<Link> auf (manifest.webmanifest: protocol_handlers, sonst
 * navigator.registerProtocolHandler in den Einstellungen). Der Link wird beim Laden gemerkt und aus der Adresse genommen,
 * damit er auch eine Anmeldung übersteht (auch per OIDC in diesem Tab), und nach der Anmeldung verarbeitet.
 */

const KEY = 'dmoffice.handle'
const TARGET = '/?handle=%s'

/** Beim Laden der Seite: ?handle= merken und aus der Adresse nehmen. */
export function captureHandle() {
  // Den Rest der Abfrage nehmen, nicht URLSearchParams: „+“ bleibt „+“, und ein unmaskiertes „&“ im Link schneidet nichts ab
  const m = /[?&]handle=(.*)$/s.exec(location.search)
  if (!m) return
  let link = m[1]
  try { link = decodeURIComponent(link) } catch { /* so lassen */ }
  try { sessionStorage.setItem(KEY, link) } catch { /* ohne Speicher: geht verloren */ }
  const rest = location.search.slice(0, m.index).replace(/^\?/, '')
  history.replaceState(history.state, '', location.pathname + (rest ? '?' + rest : '') + location.hash)
}

function takeHandle(): string | null {
  try {
    const v = sessionStorage.getItem(KEY)
    sessionStorage.removeItem(KEY)
    return v
  } catch { return null }
}

/** Teil eines mailto-Links dekodieren (RFC 6068: Prozentkodierung in UTF-8, „+“ ist kein Leerzeichen). */
function dec(s: string): string {
  try { return decodeURIComponent(s) } catch { return s }
}

/** Adressen aus einem Feld: durch Komma getrennt (RFC 6068 erlaubt nur das), leere weg. */
function addresses(s: string): string[] {
  return s.split(',').map(x => x.trim()).filter(Boolean)
}

export interface MailtoLink { to: string[]; cc: string[]; bcc: string[]; subject: string; body: string }

/** mailto:-Link nach RFC 6068 zerlegen: Empfänger vor „?“, dazu to, cc, bcc, subject, body (Groß-/Kleinschreibung egal). */
export function parseMailto(link: string): MailtoLink {
  const rest = link.replace(/^mailto:/i, '')
  const q = rest.indexOf('?')
  const r: MailtoLink = { to: addresses(dec(q < 0 ? rest : rest.slice(0, q))), cc: [], bcc: [], subject: '', body: '' }
  if (q < 0) return r
  for (const part of rest.slice(q + 1).split('&')) {
    if (!part) continue
    const eq = part.indexOf('=')
    const name = dec(eq < 0 ? part : part.slice(0, eq)).toLowerCase()
    const value = eq < 0 ? '' : dec(part.slice(eq + 1))
    if (name === 'to') r.to.push(...addresses(value))
    else if (name === 'cc') r.cc.push(...addresses(value))
    else if (name === 'bcc') r.bcc.push(...addresses(value))
    // Mehrfach genannt: das erste gilt
    else if (name === 'subject' && !r.subject) r.subject = value
    else if (name === 'body' && !r.body) r.body = value.replace(/\r\n?/g, '\n')
  }
  return r
}

/** webcal:// und webcals:// werden https:// (wie beim Eintragen von Hand auf dem Server) - die Adresse steht danach im Dialog „Kalender abonnieren“. */
export function webcalToHttp(link: string): string {
  return link.replace(/^webcals:(\/\/)?/i, 'https://').replace(/^webcal:(\/\/)?/i, 'https://')
}

const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;')

/** Text des Links als Inhalt des Entwurfs: Zeilen bleiben Zeilen. */
function bodyHtml(body: string): string {
  return body ? body.split('\n').map(l => `<p>${l ? esc(l) : '<br>'}</p>`).join('') : ''
}

/** Nach der Anmeldung: gemerkten Link verarbeiten. */
export async function processHandle(notify: (title: string, body?: string, intent?: 'success' | 'error' | 'info' | 'warning') => void) {
  const link = takeHandle()?.trim()
  if (!link || !featureOn('linkhandler')) return
  if (/^mailto:/i.test(link)) {
    const m = parseMailto(link)
    let accounts: { id: string; isDefault?: boolean }[] = []
    try { accounts = await api.get('/api/mail/accounts') } catch { /* wie ohne Konto */ }
    const account = defaultAccount(accounts)
    if (!account) {
      notify(t('Kein Mailkonto eingerichtet'), t('Für einen E-Mail-Link braucht dmOffice ein Mailkonto.'), 'warning')
      location.hash = '#/settings/accounts'
      return
    }
    handOverCompose({ account: account.id, to: m.to, cc: m.cc, bcc: m.bcc, subject: m.subject, html: bodyHtml(m.body) })
  } else if (/^webcals?:/i.test(link)) {
    handOverSubscribe(webcalToHttp(link))
  } else {
    notify(t('Link nicht unterstützt'), link, 'warning')
  }
}

export type LinkKind = 'mailto' | 'webcal'

/** Kann dieser Browser dmOffice als Programm für Links eintragen? */
export function linkHandlerSupport(): 'ok' | 'insecure' | 'unsupported' {
  if (typeof navigator.registerProtocolHandler !== 'function') return 'unsupported'
  return window.isSecureContext ? 'ok' : 'insecure'
}

/** Im Browser als Programm für mailto: bzw. webcal: eintragen; der Browser fragt danach selbst nach. */
export function registerLinkHandler(kind: LinkKind) {
  navigator.registerProtocolHandler(kind, location.origin + TARGET)
}
