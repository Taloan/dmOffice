import { Button, Field, Input, Switch, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { AdminHeading } from './AdminHeading'

/** Spamlisten: Spamhaus-Zugangsschlüssel für den ganzen Server (kommt nie zurück in die Oberfläche) und gemeinsames Junk-Wissen. */
export function SpamListsAdmin() {
  const { fail, notify } = useApp()
  const [hasKey, setHasKey] = useState<boolean | null>(null)
  const [key, setKey] = useState('')
  const [shared, setShared] = useState(false)
  useEffect(() => { api.get<{ hasKey: boolean; sharedJunk: boolean }>('/api/admin/spamlists').then(r => { setHasKey(r.hasKey); setShared(r.sharedJunk) }).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (hasKey === null) return null
  const save = async (value: string) => {
    try {
      const r = await api.put<{ hasKey: boolean }>('/api/admin/spamlists', { key: value })
      setHasKey(r.hasKey); setKey('')
      notify(t('Gespeichert'))
    } catch (e) { fail(e) }
  }
  const share = async (on: boolean) => {
    try { const r = await api.put<{ sharedJunk: boolean }>('/api/admin/spamlists/shared', { on }); setShared(r.sharedJunk); notify(t('Gespeichert')) } catch (e) { fail(e) }
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 640 }}>
      <AdminHeading>{t('Spamlisten')}</AdminHeading>
      <Text>{t('Spamhaus beantwortet Anfragen über öffentliche DNS-Server nicht. Mit einem Zugangsschlüssel (Data Query Service) funktionieren Spamhaus ZEN und DBL zuverlässig – für private Nutzung kostenlos nach Registrierung bei Spamhaus.')}</Text>
      <Field label={t('Spamhaus-Zugangsschlüssel')} hint={hasKey ? t('Ein Schlüssel ist hinterlegt.') : t('Kein Schlüssel hinterlegt.')}>
        <Input type="password" value={key} onChange={(_, d) => setKey(d.value)} placeholder={hasKey ? '••••••••' : ''} autoComplete="off" />
      </Field>
      <div style={{ display: 'flex', gap: 8 }}>
        <Button appearance="primary" disabled={!key.trim()} onClick={() => save(key.trim())}>{t('Speichern')}</Button>
        {hasKey && <Button onClick={() => save('')}>{t('Schlüssel entfernen')}</Button>}
      </div>
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Welche Listen gefragt werden, stellt jeder Benutzer unter Einstellungen → E-Mail → Junk-E-Mail ein.')}</Text>
      <AdminHeading>{t('Gemeinsames Junk-Wissen')}</AdminHeading>
      <Text>{t('Klickt jemand auf diesem Server „Junk“, merkt sich dmOffice unlesbare Prüfsummen von Betreff, Absenderdomain, Links und Textabschnitten – keine Inhalte. Kommt dieselbe Mail (auch leicht verändert) bei anderen an, gilt sie dort ab einer Meldung als unsicher, ab zwei als Junk. Gute Mails werden nie geteilt. Meldungen gelten 30 Tage; jeder kann in seinen Junk-Einstellungen aussteigen.')}</Text>
      <Switch label={t('Gemeinsames Junk-Wissen einschalten')} checked={shared} onChange={(_, d) => share(d.checked)} />
    </div>
  )
}
