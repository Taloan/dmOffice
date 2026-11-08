import type { Collection } from '../api'
import { locale, t, tError } from '../i18n'

/** Microsoft-Konten, mit denen Sammlungen verbunden sind - je Konto ein Abschnitt in der Seitenleiste. */
export function graphAccounts(collections: Collection[]): { account: string; email: string; name: string }[] {
  const seen = new Map<string, { email: string; name: string }>()
  for (const c of collections)
    if (c.graph && !seen.has(c.graph.account)) seen.set(c.graph.account, { email: c.graph.email, name: c.graph.name || c.graph.email })
  return [...seen].map(([account, v]) => ({ account, ...v }))
}

/** Hinweis an der Zeile: Stand des Abgleichs mit Microsoft. */
export function graphTitle(c: Collection): string {
  const g = c.graph
  if (!g) return c.name
  if (g.lastError) return t('Microsoft-Abgleich fehlgeschlagen: {error}', { error: tError(g.lastError) })
  const when = g.lastSync ? new Date(g.lastSync).toLocaleString(locale) : '–'
  return t('Mit Microsoft abgeglichen: {when}', { when }) + (g.pending > 0 ? ' · ' + t('{n} Änderungen warten', { n: g.pending }) : '')
}
