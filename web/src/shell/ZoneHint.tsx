import { Button, Text, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { usePrefs } from '../prefs'

const useStyles = makeStyles({
  box: {
    position: 'fixed', right: '16px', bottom: '16px', zIndex: 1000, maxWidth: '440px', padding: '12px 14px', display: 'flex', flexDirection: 'column', gap: '10px',
    borderRadius: tokens.borderRadiusLarge, backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke1}`, boxShadow: tokens.shadow16,
    '@media (max-width: 767px)': { left: '8px', right: '8px', bottom: '72px', maxWidth: 'none' },
  },
  buttons: { display: 'flex', gap: '8px', flexWrap: 'wrap', justifyContent: 'flex-end' },
})

/** Abstand einer Zeitzone zu UTC gerade jetzt; Berlin und Wien zählen damit als gleich. */
const offsetOf = (zone: string) => new Intl.DateTimeFormat('en-US', { timeZone: zone, timeZoneName: 'longOffset' }).formatToParts(new Date()).find(p => p.type === 'timeZoneName')?.value

const place = (zone: string) => zone.split('/').pop()!.replace(/_/g, ' ')

/** Gerät und Konto stehen auf verschiedenen Zeitzonen (etwa auf Reisen): einmal ansprechen, je Paar von Zeitzonen. */
export function ZoneHint() {
  const s = useStyles()
  const { fail } = useApp()
  const { prefs, update } = usePrefs()
  const device = Intl.DateTimeFormat().resolvedOptions().timeZone
  const [account, setAccount] = useState<string | null>(null)
  useEffect(() => { api.get<{ timeZone?: string }>('/api/settings/').then(x => setAccount(x.timeZone ?? null)).catch(() => {}) }, [])
  if (!account || !device) return null
  const key = `${device}|${account}`
  let same = true
  try { same = offsetOf(device) === offsetOf(account) } catch { /* unbekannte Zone: nicht fragen */ }
  if (same || prefs.zoneHintDone === key) return null
  const done = () => update({ zoneHintDone: key })
  const adopt = async () => {
    try { await api.put('/api/settings/general', { timeZone: device }); setAccount(device); done() } catch (e) { fail(e) }
  }
  return (
    <div className={s.box} role="status">
      <Text>{t('Dieses Gerät steht auf der Zeitzone {device}, dein Konto auf {account}. Kalender und Pukki rechnen hier nach dem Gerät, Tagesüberblick und Erinnerungen von Pukki nach dem Konto.', { device: place(device), account: place(account) })}</Text>
      <div className={s.buttons}>
        <Button size="small" onClick={done}>{t('Konto so lassen')}</Button>
        <Button size="small" appearance="primary" onClick={adopt}>{t('Konto auf {device} umstellen', { device: place(device) })}</Button>
      </div>
    </div>
  )
}
