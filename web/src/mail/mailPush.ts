// Neue Mails gleich zeigen (Kennung mailpush): Der gemeinsame Long-Poll (chat/wait.ts) bringt auch die Version des Mail-Bereichs mit.
// Ändert sie sich, gibt es dasselbe Ereignis wie nach eigenen Änderungen (dmoffice:mailstatus) - die Shell holt /api/mail/status
// neu, Ordnerleiste, Liste und Zähler folgen wie beim Takt alle 20 Sekunden.
import { useEffect } from 'react'
import { featureOn } from '../api'
import { onChatWait } from '../chat/wait'

export const MAIL_STATUS_EVENT = 'dmoffice:mailstatus'

export function useMailPush() {
  useEffect(() => {
    if (!featureOn('mailpush')) return
    let seen: number | undefined
    let timer: ReturnType<typeof setTimeout> | undefined
    const off = onChatWait(w => {
      if (w.mail === undefined) return
      // Erster Stand nur merken; danach jede Änderung melden (auch kleiner: Neustart des Servers)
      if (seen !== undefined && w.mail !== seen && !timer)
        timer = setTimeout(() => { timer = undefined; window.dispatchEvent(new Event(MAIL_STATUS_EVENT)) }, 150)
      seen = w.mail
    })
    return () => { off(); if (timer) clearTimeout(timer) }
  }, [])
}
