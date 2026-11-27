// Eine Mail im eigenen Fenster (Doppelklick in der Liste, wie in Outlook): Lesen, Antworten, Weiterleiten, Loeschen.
// Adresse: #/popup/mail/<konto>/<message-id>
import { Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, Spinner, Text, tokens } from '@fluentui/react-components'
import {
  ArrowForward20Regular, ArrowReply20Regular, ArrowReplyAll20Regular, Code20Regular, Delete20Regular, MoreHorizontal20Regular, Print20Regular,
  ShieldCheckmark20Regular, ShieldError20Regular, WeatherMoon20Regular,
} from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { Account, api, featureOn, MailItem, Message, q } from '../api'
import { Ribbon, RibbonButton, RibbonDivider, RibbonFrame, RibbonTabs } from '../shell/Ribbon'
import { useApp } from '../App'
import { t } from '../i18n'
import { Compose, ComposeHandle, ComposeInit, forwardOf, replyOf } from './Compose'
import { ReadingPane, saveMailSource } from './ReadingPane'
import { ExpiryMenuItems } from './ExpiryMenu'
import { useIsDark } from '../theme'
import { usePrefs } from '../prefs'

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
  // null: noch nicht geladen
  const [accounts, setAccounts] = useState<Account[] | null>(null)
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
  // Eigenes Fenster: Menüband mit dem Reiter „Nachricht“ wie im Hauptfenster
  const [ribbonSlot, setRibbonSlot] = useState<HTMLDivElement | null>(null)
  const messageRibbon = featureOn('mailribbon') && window.innerWidth >= 768
  useEffect(() => { api.get<Account[]>('/api/mail/accounts').then(setAccounts).catch(() => setAccounts([])) }, [])
  useEffect(() => { document.title = init?.subject || t('Neue E-Mail') }, [init])
  if (!init) return <div style={{ padding: 40, textAlign: 'center', color: tokens.colorNeutralForeground3 }}><Text>{t('Diese Mail wurde nicht gefunden.')}</Text></div>
  if (!accounts) return <Spinner style={{ marginTop: 60 }} />
  if (!accounts.length) return <div style={{ padding: 40, textAlign: 'center', color: tokens.colorNeutralForeground3 }}><Text>{t('Kein Mailkonto eingerichtet.')}</Text></div>
  return (
    <div style={{ height: '100vh', display: 'flex', flexDirection: 'column', background: tokens.colorNeutralBackground1 }}>
      {messageRibbon && <RibbonFrame area="mailcompose" tabs={<RibbonTabs tabs={[['message', t('Nachricht')]]} value="message" onChange={() => {}} />}>
        <Ribbon><div ref={setRibbonSlot} style={{ display: 'contents' }} /></Ribbon>
      </RibbonFrame>}
      <Compose ref={ref} init={init} accounts={accounts} onSaved={tellOpener} ribbonSlot={messageRibbon ? ribbonSlot : null} onClose={(_, queued) => {
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
  const dark = useIsDark()
  const [invert, setInvert] = useState(true)
  const { prefs } = usePrefs()

  useEffect(() => {
    const m = location.hash.match(/^#\/popup\/mail\/([^/]+)\/(.+)$/)
    if (!m) { setError(t('Diese Mail wurde nicht gefunden.')); return }
    api.get<MailItem>(`/api/mail/${m[1]}/find?` + q({ messageId: decodeURIComponent(m[2]) }))
      .then(setItem).catch(e => setError(e instanceof Error ? e.message : String(e)))
    api.get<Account[]>('/api/mail/accounts').then(setAccounts).catch(() => {})
  }, [])
  useEffect(() => { document.title = message?.subject || item?.subject || 'dmOffice' }, [message, item])

  const account = accounts.find(a => a.id === item?.account)
  const junkLock = featureOn('junklock') && !!message?.junk // [feature:junklock]
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
      {!compose && (
        // Menüband wie im Hauptfenster: alle Befehle zur Mail stehen hier, nicht im Lesebereich
        <RibbonFrame area="mailwindow" tabs={<RibbonTabs tabs={[['start', t('ribbon|Start')]]} value="start" onChange={() => {}} />}>
          <Ribbon>
            {/* Standardantwort (Einstellungen -> Verfassen) steht vorne */}
            {(prefs.replyAll ? [true, false] : [false, true]).map(all => (
              <RibbonButton key={String(all)} icon={all ? <ArrowReplyAll20Regular /> : <ArrowReply20Regular />} label={all ? t('Allen antworten') : t('Antworten')}
                disabled={!message || !account || junkLock} onClick={() => message && account && setCompose(replyOf(message, account, all))} />
            ))}
            <RibbonButton icon={<ArrowForward20Regular />} label={t('Weiterleiten')} disabled={!message || !account || junkLock}
              onClick={() => message && account && setCompose(forwardOf(message, account))} />
            <RibbonDivider />
            <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} onClick={() => act('delete')} />
            {message?.junk
              ? <RibbonButton icon={<ShieldCheckmark20Regular />} label={t('Kein Junk')} onClick={() => act('notjunk')} />
              : <RibbonButton icon={<ShieldError20Regular />} label={t('Junk')} onClick={() => act('junk')} />}
            <RibbonDivider />
            <RibbonButton icon={<Print20Regular />} label={t('Drucken')} disabled={!message} onClick={() => window.dispatchEvent(new Event('dm-mail-print'))} />
            {dark && <RibbonButton icon={<WeatherMoon20Regular />} label={t('Dunkel darstellen')} pressed={invert} onClick={() => setInvert(v => !v)} />}
            <Menu>
              <MenuTrigger disableButtonEnhancement><RibbonButton icon={<MoreHorizontal20Regular />} label={t('Mehr')} /></MenuTrigger>
              <MenuPopover>
                <MenuList>
                  <MenuItem icon={<Code20Regular />} disabled={junkLock} onClick={() => saveMailSource(item)}>{t('Quelltext (.eml)')}</MenuItem>
                  {featureOn('mailexpiry') && <MenuDivider />}
                  <ExpiryMenuItems targets={[item]} onError={fail}
                    onDone={w => { tellOpener(); notify(w === 'set' ? t('Entfernen nach Zeit eingeplant') : t('Entfernen nach Zeit zurückgenommen')) }} />
                </MenuList>
              </MenuPopover>
            </Menu>
          </Ribbon>
        </RibbonFrame>
      )}
      {compose && account ? (
        <Compose init={compose} accounts={accounts} onClose={sent => { setCompose(null); if (sent) { tellOpener(); window.close() } }} />
      ) : (
        <ReadingPane item={item}
          onLoaded={m => { setMessage(m); if (!item.seen) tellOpener() }}
          onReply={all => message && account && !junkLock && setCompose(replyOf(message, account, all))}
          onAct={act}
          invert={invert}
          onRemoveCategory={async key => {
            setItem(i => i && { ...i, keywords: (i.keywords ?? []).filter(k => k !== key) })
            try { await api.post(`/api/mail/${item.account}/category`, { folder: item.folder, uids: [item.uid], key, on: false }); tellOpener() }
            catch (e) { fail(e) }
          }} />
      )}
    </div>
  )
}
