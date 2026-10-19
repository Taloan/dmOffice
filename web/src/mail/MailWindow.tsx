// Eine Mail im eigenen Fenster (Doppelklick in der Liste, wie in Outlook): Lesen, Antworten, Weiterleiten, Loeschen.
// Adresse: #/popup/mail/<konto>/<message-id>
import { Spinner, Text, tokens } from '@fluentui/react-components'
import { useEffect, useRef, useState } from 'react'
import { Account, api, MailItem, Message, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Compose, ComposeHandle, ComposeInit, forwardOf, replyOf } from './Compose'
import { ReadingPane } from './ReadingPane'

export const isMailWindow = () => location.hash.startsWith('#/popup/mail/') || location.hash.startsWith('#/popup/compose/')

/** Angefangene Mail in ein eigenes Fenster: der Stand (auch angehaengte Dateien) geht direkt hinueber. */
export function openComposeWindow(state: ComposeInit): boolean {
  const key = Math.random().toString(36).slice(2)
  const handoff = ((window as any).__dmCompose ??= {}) as Record<string, ComposeInit>
  handoff[key] = state
  const w = Math.min(1000, screen.availWidth - 80), h = Math.min(860, screen.availHeight - 80)
  const win = window.open(`${location.pathname}#/popup/compose/${key}`, 'dm-compose-' + key, `popup,width=${w},height=${h}`)
  if (!win) delete handoff[key]
  return !!win
}

/** Verfassen im eigenen Fenster; schliesst sich nach dem Senden oder Verwerfen. */
function ComposeWindow() {
  const [accounts, setAccounts] = useState<Account[]>([])
  const [init] = useState<ComposeInit | null>(() => {
    const key = location.hash.replace('#/popup/compose/', '')
    try {
      const handoff = (window.opener as any)?.__dmCompose as Record<string, ComposeInit> | undefined
      const s = handoff?.[key]
      if (handoff) delete handoff[key]
      return s ?? null
    } catch { return null }
  })
  const ref = useRef<ComposeHandle>(null)
  useEffect(() => { api.get<Account[]>('/api/mail/accounts').then(setAccounts).catch(() => {}) }, [])
  useEffect(() => { document.title = init?.subject || t('Neue E-Mail') }, [init])
  if (!init) return <div style={{ padding: 40, textAlign: 'center', color: tokens.colorNeutralForeground3 }}><Text>{t('Diese Mail wurde nicht gefunden.')}</Text></div>
  if (!accounts.length) return <Spinner style={{ marginTop: 60 }} />
  return (
    <div style={{ height: '100vh', display: 'flex', flexDirection: 'column', background: tokens.colorNeutralBackground1 }}>
      <Compose ref={ref} init={init} accounts={accounts} onSaved={tellOpener} onClose={(_, queued) => {
        // Zurueckrufen geht im Hauptfenster weiter, dieses Fenster schliesst sich
        try { if (queued) (window.opener as any)?.__dmUndo?.(queued) } catch { /* Hauptfenster zu */ }
        tellOpener(); window.close()
      }} />
    </div>
  )
}

/** Mail in einem eigenen Fenster oeffnen; ohne Message-ID geht das nicht (dann false). */
export function openMailWindow(item: MailItem): boolean {
  if (!item.messageId) return false
  const url = `${location.pathname}#/popup/mail/${encodeURIComponent(item.account)}/${encodeURIComponent(item.messageId)}`
  const w = Math.min(1100, screen.availWidth - 80), h = Math.min(900, screen.availHeight - 80)
  return !!window.open(url, 'dm-mail-' + item.id, `popup,width=${w},height=${h}`)
}

/** Dem Hauptfenster Bescheid geben, dass sich etwas geaendert hat (Liste und Zaehler neu laden). */
const tellOpener = () => { try { const o = window.opener as (Window & typeof globalThis) | null; o?.dispatchEvent(new o.Event('dmoffice:mailstatus')) } catch { /* anderes Fenster */ } }

export function MailWindow() {
  if (location.hash.startsWith('#/popup/compose/')) return <ComposeWindow />
  return <ReadWindow />
}

function ReadWindow() {
  const { fail, notify } = useApp()
  const [item, setItem] = useState<MailItem | null>(null)
  const [message, setMessage] = useState<Message | null>(null)
  const [accounts, setAccounts] = useState<Account[]>([])
  const [compose, setCompose] = useState<ComposeInit | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    const m = location.hash.match(/^#\/popup\/mail\/([^/]+)\/(.+)$/)
    if (!m) { setError(t('Diese Mail wurde nicht gefunden.')); return }
    api.get<MailItem>(`/api/mail/${m[1]}/find?` + q({ messageId: decodeURIComponent(m[2]) }))
      .then(setItem).catch(e => setError(e instanceof Error ? e.message : String(e)))
    api.get<Account[]>('/api/mail/accounts').then(setAccounts).catch(() => {})
  }, [])
  useEffect(() => { document.title = message?.subject || item?.subject || 'dmOffice' }, [message, item])

  const account = accounts.find(a => a.id === item?.account)
  const act = async (what: 'delete' | 'junk' | 'notjunk') => {
    if (!item) return
    try {
      await api.post(`/api/mail/${item.account}/${what}`, { folder: item.folder, uids: [item.uid] })
      tellOpener()
      if (what === 'notjunk') notify(t('Als „Kein Junk“ gelernt'))
      else window.close()
    } catch (e) { fail(e) }
  }

  if (error) return <div style={{ padding: 40, textAlign: 'center', color: tokens.colorNeutralForeground3 }}><Text>{error}</Text></div>
  if (!item) return <Spinner style={{ marginTop: 60 }} />
  return (
    <div style={{ height: '100vh', display: 'flex', flexDirection: 'column', background: tokens.colorNeutralBackground1 }}>
      {compose && account ? (
        <Compose init={compose} accounts={accounts} onClose={sent => { setCompose(null); if (sent) { tellOpener(); window.close() } }} />
      ) : (
        <ReadingPane item={item}
          onLoaded={m => { setMessage(m); if (!item.seen) tellOpener() }}
          onReply={all => message && account && setCompose(replyOf(message, account, all))}
          onForward={() => message && account && setCompose(forwardOf(message, account))}
          onAct={act} />
      )}
    </div>
  )
}
