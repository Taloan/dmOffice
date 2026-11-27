// Eigene Textbausteine beim Schreiben von Mails (Kennung mailsnippets, 2.2): Titel, optionales Kürzel für „::“ und
// formatierter Text, ohne Platzhalter - die gibt es nur bei den Textbausteinen des Helpdesks.
import { useEffect, useState } from 'react'
import { api } from '../api'

export interface MailSnippet {
  id: string; title: string; code?: string | null; html: string; changed: string
  /** aus dem Helpdesk (nur lesen; body mit Platzhaltern, html ungefüllt für Suche und Vorschau) */
  hd?: boolean; body?: string; lang?: string | null
}

/** Angaben zum Füllen von Platzhaltern (nur Helpdesk-Bausteine): erster Empfänger, eigener Name, Sprache des Kontos */
export interface SnippetContext { to?: string; me?: string; lang?: string }

let cache: MailSnippet[] | null = null
let loading: Promise<MailSnippet[]> | null = null
const listeners = new Set<(l: MailSnippet[]) => void>()

/** Liste laden (einmal, danach aus dem Zwischenspeicher; force lädt neu). */
export function loadSnippets(force = false): Promise<MailSnippet[]> {
  if (cache && !force) return Promise.resolve(cache)
  if (!loading || force) {
    loading = api.get<MailSnippet[]>('/api/mail/snippets').then(l => { cache = l; listeners.forEach(f => f(l)); return l }).finally(() => { loading = null })
  }
  return loading
}

/** Aktuelle Liste; null, solange sie lädt. */
export function useSnippets(enabled = true): MailSnippet[] | null {
  const [list, setList] = useState<MailSnippet[] | null>(cache)
  useEffect(() => {
    if (!enabled) return
    listeners.add(setList)
    loadSnippets().catch(() => setList([]))
    return () => { listeners.delete(setList) }
  }, [enabled])
  return list
}

export async function saveSnippet(id: string | null, body: { title: string; code?: string; html: string }): Promise<MailSnippet> {
  const r = id ? await api.put<MailSnippet>(`/api/mail/snippets/${id}`, body) : await api.post<MailSnippet>('/api/mail/snippets', body)
  await loadSnippets(true)
  return r
}

export async function deleteSnippet(id: string) {
  await api.del(`/api/mail/snippets/${id}`)
  await loadSnippets(true)
}

/** Reiner Text eines Bausteins (Suche, Vorschau). */
export function snippetText(html: string): string {
  const d = document.createElement('div')
  // Zeilenumbrüche und Absätze als Leerzeichen, sonst klebt „Grüße<br>Max“ zusammen
  d.innerHTML = html.replace(/<br\s*\/?>|<\/(div|p|li|h\d|tr)>/gi, m => ' ' + m)
  return (d.textContent ?? '').replace(/\s+/g, ' ').trim()
}

/** Rang für die Suche wie im Helpdesk: 0 Kürzel genau, 1 Kürzelanfang, 2 Wortanfang im Titel, 3 Treffer im Text; -1 kein Treffer. */
export function snippetRank(x: MailSnippet, query: string): number {
  const q = query.trim().toLowerCase()
  if (!q) return 3
  if (x.code === q) return 0
  if (x.code?.startsWith(q)) return 1
  // Kurze Eingaben (Kürzel) nur an Wortanfängen im Titel - sonst trifft „rv“ alles mit „Server“
  if (x.title.toLowerCase().split(/[^\p{L}\p{N}]+/u).some(w => w.startsWith(q))) return 2
  if (q.length < 4) return -1
  if (x.title.toLowerCase().includes(q)) return 2
  const all = (x.title + ' ' + snippetText(x.html) + ' ' + (x.code ?? '')).toLowerCase()
  return q.split(/\s+/).every(w => all.includes(w)) ? 3 : -1
}

/** Treffer sortiert: Rang, dann Titel. */
export function matchSnippets(list: MailSnippet[], query: string, max = Infinity): MailSnippet[] {
  return list.map(x => [x, snippetRank(x, query)] as const).filter(([, r]) => r >= 0)
    .sort((a, b) => a[1] - b[1] || a[0].title.localeCompare(b[0].title)).slice(0, max).map(([x]) => x)
}
