import { useEffect, useRef, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import { beep, desktopPossible } from '../shell/Notifier'
import { t } from '../i18n'
import { ChatRoom } from './store'

export interface ChatStatus {
  seq: number; unread: number
  /** Ruhezeit: null = alles melden, 'mentions' = nur Erwaehnungen, 'all' = nichts */
  quiet?: 'all' | 'mentions' | null
  /** eigenes Profilbild (Version) - für den Kreis oben rechts */
  avatar?: string | null
}

/**
 * Stand des Chats fuer die ganze Oberflaeche (Zahl am Symbol, Hinweise): wartet per Long-Poll auf Aenderungen
 * und holt dann den Stand. Auf Zuruf (dmoffice:chatstatus, etwa nach dem Lesen) sofort.
 */
export function useChatStatus(): ChatStatus | null {
  const [status, setStatus] = useState<ChatStatus | null>(null)
  useEffect(() => {
    let alive = true
    let seq = 0
    const load = () => api.get<ChatStatus>('/api/chat/status').then(s => { if (alive) { seq = Math.max(seq, s.seq); setStatus(s) } }).catch(() => {})
    const run = async () => {
      await load()
      while (alive) {
        try {
          const r = await api.get<{ seq: number }>('/api/chat/wait?' + q({ since: seq, timeout: 25 }))
          if (alive && r.seq > seq) await load()
        } catch { await new Promise(res => setTimeout(res, 10000)) }
      }
    }
    run()
    window.addEventListener('dmoffice:chatstatus', load)
    return () => { alive = false; window.removeEventListener('dmoffice:chatstatus', load) }
  }, [])
  return status
}

/** Für Meldung und Desktop-Hinweis nur der Anfang: erste nicht leere Zeile, höchstens 100 Zeichen (lange Nachrichten wie der Morgenbericht füllten sonst den ganzen Hinweis). */
export function excerpt(text: string | null | undefined): string | undefined {
  const line = text?.split('\n').map(l => l.trim()).find(l => l)
  if (!line) return undefined
  const more = line.length > 100 || text!.trim().length > line.length
  return more ? line.slice(0, 100).trimEnd() + ' …' : line
}

/** Neue Nachricht, waehrend der Chat nicht offen (oder das Fenster verdeckt) ist: Meldung, Ton, Desktop-Hinweis. */
export function ChatNotifier({ status, active }: { status: ChatStatus | null; active: boolean }) {
  const { notify } = useApp()
  const { prefs } = usePrefs()
  const last = useRef<number | null>(null)
  const prefsRef = useRef(prefs)
  prefsRef.current = prefs
  const activeRef = useRef(active)
  activeRef.current = active

  useEffect(() => {
    if (!status) return
    const before = last.current
    last.current = status.unread
    if (before == null || status.unread <= before) return
    if (activeRef.current && !document.hidden) return
    if (status.quiet === 'all') return
    api.get<ChatRoom[]>('/api/chat/rooms').then(rooms => {
      // Stumm oder Ruhezeit: nur, wo ich erwaehnt bin
      const r = rooms.filter(x => x.unread > 0 && x.last?.kind === 'text' && (x.mentioned || (!x.muted && status.quiet !== 'mentions')))
        .sort((a, b) => (b.last!.created > a.last!.created ? 1 : -1))[0]
      if (!r || !r.last) return
      const title = r.kind === 'group' ? `${r.last.senderName} · ${r.title}` : r.title
      const body = excerpt(r.last.text) ?? r.last.refs.map(x => x.type === 'location' ? '📍 ' + t('Standort') : x.name).join(', ') ?? t('Neue Nachricht')
      const p = prefsRef.current
      notify(title, body, 'info')
      if (p.notifySound ?? true) beep()
      if ((p.notifyDesktop ?? true) && desktopPossible() && Notification.permission === 'granted' && document.hidden) {
        const n = new Notification(title, { body, icon: '/icon-128.png', tag: 'chat-' + r.id })
        n.onclick = () => { window.focus(); location.hash = `#/chat/${r.id}`; n.close() }
      }
    }).catch(() => {})
  }, [status]) // eslint-disable-line react-hooks/exhaustive-deps

  return null
}
