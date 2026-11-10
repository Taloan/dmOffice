import { Checkbox, Switch } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Hint, PageTitle, Section } from './parts'

interface SmartState {
  enabled: boolean; focus: boolean; calendar: boolean; categories: boolean
  accounts: { id: string; name: string; email: string; on: boolean }[]
}

/** Smart Inbox: Hauptschalter, für welche Postfächer, und die Funktionen darin (Mail/SmartInbox.cs). */
export function SmartInboxSettings() {
  const { fail } = useApp()
  const [st, setSt] = useState<SmartState | null>(null)
  useEffect(() => { api.get<SmartState>('/api/mail/smart/settings').then(setSt).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const save = async (change: Partial<SmartState>) => {
    if (!st) return
    setSt({ ...st, ...change })
    try { setSt(await api.put<SmartState>('/api/mail/smart/settings', change)) } catch (e) { fail(e) }
  }
  if (!st) return <PageTitle>{t('Smart Inbox')}</PageTitle>
  const off = !st.enabled
  return (
    <>
      <PageTitle>{t('Smart Inbox')}</PageTitle>
      <Section title={t('Einschalten')} hint={t('Ordnet eingehende Mails für dich ein. Ausgewertet wird nur auf deinem Server.')}>
        <Switch checked={st.enabled} label={st.enabled ? t('Eingeschaltet') : t('Ausgeschaltet')} onChange={(_, d) => save({ enabled: d.checked })} />
      </Section>
      <Section title={t('Gilt für')}>
        {st.accounts.map(a => (
          <Checkbox key={a.id} disabled={off} checked={a.on} label={a.email || a.name}
            onChange={(_, d) => save({ accounts: st.accounts.map(x => x.id === a.id ? { ...x, on: !!d.checked } : x) })} />
        ))}
        {!st.accounts.length && <Hint>{t('Noch kein E-Mail-Konto eingerichtet.')}</Hint>}
      </Section>
      <Section title={t('Funktionen')} last>
        <Switch disabled={off} checked={st.focus} label={t('Posteingang in „Relevant“ und „Sonstige“ aufteilen')} onChange={(_, d) => save({ focus: d.checked })} />
        <Switch disabled={off} checked={st.calendar} label={t('Termine aus E-Mails in den Kalender „E-Mail-Ankündigungen“ eintragen')} onChange={(_, d) => save({ calendar: d.checked })} />
        <Switch disabled={off} checked={st.categories} label={t('Neue Mails automatisch kategorisieren')} onChange={(_, d) => save({ categories: d.checked })} />
        <Hint>{t('Gilt für Kategorien mit „Lernt“ (E-Mail → Kategorien).')}</Hint>
      </Section>
    </>
  )
}
