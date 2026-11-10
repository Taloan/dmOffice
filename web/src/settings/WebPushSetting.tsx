import { Button, MessageBar, MessageBarBody, Switch } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { isIos, usePwa } from '../pwa'
import { Hint, Section } from './parts'

/** Web Push geht nur mit Service Worker, Push-Manager und HTTPS (oder localhost). */
const webPushPossible = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window && window.isSecureContext

const toBytes = (b64url: string) => {
  const s = atob(b64url.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(b64url.length / 4) * 4, '='))
  return Uint8Array.from(s, c => c.charCodeAt(0))
}

/**
 * Web Push (2.0): Benachrichtigungen für Chat, neue Mail und Erinnerungen auch bei geschlossenem Fenster.
 * Das Abo gilt je Browser; der Server schickt an alle Abos des Benutzers (Inhalt Ende-zu-Ende verschlüsselt).
 */
export function WebPushSetting() {
  const { fail, notify } = useApp()
  const { installed } = usePwa()
  const [sub, setSub] = useState<PushSubscription | null | undefined>(undefined)
  const [busy, setBusy] = useState(false)
  const possible = webPushPossible()

  useEffect(() => {
    if (!possible) return
    navigator.serviceWorker.getRegistration('/').then(reg => reg?.pushManager.getSubscription() ?? null).then(setSub).catch(() => setSub(null))
  }, [possible])

  const turn = async (on: boolean) => {
    setBusy(true)
    try {
      const reg = await navigator.serviceWorker.ready
      if (on) {
        if (await Notification.requestPermission() !== 'granted') { fail(new Error(t('Der Browser hat Benachrichtigungen nicht erlaubt.'))); return }
        const { key } = await api.get<{ key: string }>('/api/push/web/key')
        const s = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: toBytes(key) })
        await api.post('/api/push/web/subscribe', s.toJSON())
        setSub(s)
      } else if (sub) {
        await api.post('/api/push/web/unsubscribe', { endpoint: sub.endpoint }).catch(() => {})
        await sub.unsubscribe()
        setSub(null)
      }
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  const test = async () => {
    try {
      const r = await api.post<{ sent: number }>('/api/push/web/test')
      notify(r.sent ? t('Probe geschickt – sie erscheint gleich als Benachrichtigung.') : t('Kein Abo erreicht – bitte aus- und wieder einschalten.'), '', r.sent ? 'success' : 'warning')
    } catch (e) { fail(e) }
  }

  return (
    <Section title={t('Auch bei geschlossenem Fenster')} hint={t('Chat, neue E-Mail und Erinnerungen kommen als Benachrichtigung, auch wenn dmOffice nicht offen ist. Gilt für diesen Browser.')} last>
      {!possible ? (
        <MessageBar intent="info"><MessageBarBody>{t('Dieser Browser kann das nicht – oder dmOffice läuft nicht über HTTPS.')}</MessageBarBody></MessageBar>
      ) : isIos() && !installed ? (
        <Hint>{t('Auf dem iPhone und iPad geht das nur in der installierten App: Teilen → „Zum Home-Bildschirm“, dann dort einschalten.')}</Hint>
      ) : (
        <>
          <Switch label={t('Benachrichtigungen bei geschlossenem Fenster')} checked={!!sub} disabled={busy || sub === undefined} onChange={(_, d) => turn(d.checked)} />
          {sub && <div><Button size="small" onClick={test}>{t('Probe')}</Button></div>}
        </>
      )}
    </Section>
  )
}
