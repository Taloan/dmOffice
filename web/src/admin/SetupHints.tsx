import { Button, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { usePrefs } from '../prefs'

/** Was ein Admin nach der Einrichtung tun sollte: verschwindet, sobald es erledigt ist oder er es abhakt. */
export function SetupHints({ onBackup }: { onBackup: () => void }) {
  const { me } = useApp()
  const { prefs, update } = usePrefs()
  const [backup, setBackup] = useState<boolean | null>(null)
  useEffect(() => {
    api.get<{ backup?: { enabled?: boolean } }>('/api/admin/storage').then(s => setBackup(!!s.backup?.enabled)).catch(() => setBackup(null))
  }, [])
  const done = prefs.adminHintsDone ?? []
  const check = (key: string) => update({ adminHintsDone: [...done, key] })
  const open = (key: string) => !done.includes(key)
  const hints: { key: string; title: string; text: string; action?: [string, () => void] }[] = []
  if (backup === false && open('backup')) hints.push({
    key: 'backup', title: t('Es läuft noch keine Sicherung'),
    text: t('Ohne Sicherung sind die Daten bei einem Plattenschaden verloren.'),
    action: [t('Sicherung einrichten'), onBackup],
  })
  if (open('masterkey')) hints.push({
    key: 'masterkey', title: t('Hauptschlüssel aufbewahren'),
    text: t('Ohne den Hauptschlüssel lässt sich keine Sicherung öffnen. Bewahre ihn getrennt vom Server auf. Er steht als DMOFFICE_MASTER_KEY in der Container-Vorlage oder als Datei master.key im Datenverzeichnis.'),
  })
  if (me && !me.twoFactor && open('twofactor')) hints.push({
    key: 'twofactor', title: t('Zwei-Faktor einschalten'),
    text: t('Dein Konto verwaltet den ganzen Server. Ein zweiter Faktor schützt es, falls dein Passwort in falsche Hände gerät.'),
    action: [t('Zur Sicherheit'), () => { location.hash = '#/settings/security' }],
  })
  if (hints.length === 0) return null
  return <>{hints.map(h => (
    <MessageBar key={h.key} intent="info" layout="multiline" style={{ flexShrink: 0 }}>
      <MessageBarBody><MessageBarTitle>{h.title}</MessageBarTitle>{h.text}</MessageBarBody>
      <MessageBarActions>
        {h.action && <Button onClick={h.action[1]}>{h.action[0]}</Button>}
        <Button onClick={() => check(h.key)}>{t('Erledigt')}</Button>
      </MessageBarActions>
    </MessageBar>
  ))}</>
}
