// GateKeeper (Kennung gatekeeper, 2.2, docs/gatekeeper-konzept.md): Typen und Hilfen für Einstellungen und Verwaltung.
// Server: DmOffice.Server/GateKeeper/GateKeeperApi.cs.
import { t } from '../i18n'
import { fmtDate } from '../shell/hooks'

export interface GkCounts { connections: number; dns: number; blocked: number; up?: number; down?: number; traffic?: number; median?: number; saved?: number }
export interface GkRule { domain: string; action: 'block' | 'allow'; created: number }
export interface GkState {
  rules: GkRule[]
  today: GkCounts
  month: GkCounts
  top?: GkHit[]
  filter: { lists: number; entries: number }
}
export interface GkDay extends GkCounts { day: string; people?: number }
export interface GkHit { domain: string; hits: number; last: number }
export interface GkStats { days: GkDay[]; totals: GkCounts; hits: GkHit[] }
export interface GkCheck { domain: string; blocked: boolean; source: 'none' | 'user-allow' | 'user-block' | 'list-allow' | 'list' }

export interface GkList { id: number; url: string; name: string; enabled: boolean; fetched: number | null; status: string | null; entries: number }
export interface GkAdminStats { days: GkDay[]; today: GkCounts; totals: GkCounts; peopleToday: number; people: number }
export interface GkAdmin { lists: GkList[]; refreshHours: number; allowPrivate: boolean; entries: number; dns: boolean }

/** Zeitpunkt in Unix-Sekunden wie in den übrigen Listen (heute nur Uhrzeit, sonst Tag). */
export const fmtUnix = (s: number | null | undefined) => (s ? fmtDate(new Date(s * 1000).toISOString()) : '–')

/** Zahl mit Tausendertrennung in der eingestellten Sprache. */
export const fmtNum = (n: number) => n.toLocaleString()

export const actionText = (a: GkRule['action']) => (a === 'allow' ? t('gk|erlaubt') : t('gk|gesperrt'))

/** Ergebnis von „Adresse prüfen“: gesperrt oder erlaubt, und wodurch. */
export const checkText = (c: GkCheck) => ({
  'user-allow': t('Erlaubt durch eine eigene Regel.'),
  'user-block': t('Gesperrt durch eine eigene Regel.'),
  'list-allow': t('Erlaubt durch eine Filterliste.'),
  list: t('Gesperrt durch eine Filterliste.'),
  none: t('Erlaubt: Für diese Adresse gilt keine Sperre.'),
} as Record<string, string>)[c.source] ?? (c.blocked ? t('gk|gesperrt') : t('gk|erlaubt'))

/** Stand einer Filterliste vom Server („ok“ oder kurzer Fehlertext). */
export const listStatusText = (s: string | null) => !s ? t('noch nicht geladen') : s === 'ok' ? t('geladen') : t(s)
