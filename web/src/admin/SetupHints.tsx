import { Button, MessageBar, MessageBarActions, MessageBarBody, MessageBarTitle } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { usePrefs } from '../prefs'

/** Was ein Admin nach der Einrichtung tun sollte: verschwindet, sobald es erledigt ist oder er es abhakt. */
// [feature:adminclean]
// Kennung adminclean: Hinweise stehen auf ihrer Seite (Backup, Sicherheit); only wählt sie aus, der Punkt in der Leiste kommt aus useSetupOpen
export function SetupHints({ onBackup, only }: { onBackup?: () => void; only?: string[] }) {
// [feature:adminclean:else]
// export function SetupHints({ onBackup }: { onBackup: () => void }) {
// [/feature:adminclean]
  const { me } = useApp()
  const { prefs, update } = usePrefs()
  const [backup, setBackup] = useState<boolean | null>(null)
  useEffect(() => {
    api.get<{ backup?: { enabled?: boolean } }>('/api/admin/storage').then(s => setBackup(!!s.backup?.enabled)).catch(() => setBackup(null))
  }, [])
  const done = prefs.adminHintsDone ?? []
  const check = (key: string) => update({ adminHintsDone: [...done, key] })
  // [feature:adminclean]
  const open = (key: string) => !done.includes(key) && (!only || only.includes(key))
  // [feature:adminclean:else]
  // const open = (key: string) => !done.includes(key)
  // [/feature:adminclean]
  const hints: { key: string; title: string; text: string; action?: [string, () => void] }[] = []
  if (backup === false && open('backup')) hints.push({
    key: 'backup', title: t('Es läuft noch kein Backup'),
    text: '',
    // [feature:adminclean]
    action: onBackup && [t('Backup einrichten'), onBackup],
    // [feature:adminclean:else]
    // action: [t('Backup einrichten'), onBackup],
    // [/feature:adminclean]
  })
  if (open('masterkey')) hints.push({
    key: 'masterkey', title: t('Hauptschlüssel aufbewahren'),
    text: t('Ohne den Hauptschlüssel lässt sich kein Backup öffnen. Er steht als DMOFFICE_MASTER_KEY in der Container-Vorlage oder als Datei master.key im Datenverzeichnis.'),
  })
  if (me && !me.twoFactor && open('twofactor')) hints.push({
    key: 'twofactor', title: t('Zwei-Faktor einschalten'),
    text: '',
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

// [feature:adminclean]
/** Offene Einrichtungshinweise (backup, masterkey, twofactor) für den Punkt an der Seite in der Leiste. */
export function useSetupOpen(): string[] {
  const { me } = useApp()
  const { prefs } = usePrefs()
  const [backup, setBackup] = useState<boolean | null>(null)
  useEffect(() => {
    api.get<{ backup?: { enabled?: boolean } }>('/api/admin/storage').then(s => setBackup(!!s.backup?.enabled)).catch(() => setBackup(null))
  }, [])
  const done = prefs.adminHintsDone ?? []
  return [...(backup === false ? ['backup'] : []), 'masterkey', ...(me && !me.twoFactor ? ['twofactor'] : [])].filter(k => !done.includes(k))
}
// [/feature:adminclean]
