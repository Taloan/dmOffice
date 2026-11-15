import { Checkbox, Switch, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, featureOn } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Hint, PageTitle, Section } from './parts'

interface Unsubscribed { at: string; account: string; sender: string; subject: string | null; result: 'done' | 'unconfirmed' | 'failed'; how: string | null }

interface SmartState {
  enabled: boolean; focus: boolean; calendar: boolean; categories: boolean; unsubscribe: boolean
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
      <Section title={t('Einschalten')} hint={t('Ordnet eingehende Mails ein. Ausgewertet wird nur auf diesem Server.')}>
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
        {featureOn('autounsub') && <>
          <Switch disabled={off} checked={st.unsubscribe} label={t('Von Newslettern automatisch abmelden')} onChange={(_, d) => save({ unsubscribe: d.checked })} />
          <Hint>{t('Wenn du eine E-Mail in den Junk-Ordner verschiebst, meldet dmOffice dich von der Verteilerliste des Absenders ab. Bietet der Absender keine direkte Abmeldung an, ruft dmOffice den Abmeldelink auf. Verlangt die Seite danach noch eine Bestätigung, bleibt die Abmeldung unvollständig.')}</Hint>
          <UnsubscribedList />
        </>}
      </Section>
    </>
  )
}

const RESULT: Record<Unsubscribed['result'], string> = { done: 'Abgemeldet', unconfirmed: 'Abmeldelink aufgerufen, Bestätigung möglich', failed: 'Abmelden fehlgeschlagen' }

/** Die letzten automatischen Abmeldungen (Mail/AutoUnsubscribe.cs). */
function UnsubscribedList() {
  const [list, setList] = useState<Unsubscribed[] | null>(null)
  useEffect(() => { api.get<Unsubscribed[]>('/api/mail/smart/unsubscribed').then(setList).catch(() => setList([])) }, [])
  if (!list) return null
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4, marginTop: 4 }}>
      <Text weight="semibold">{t('Automatisch abgemeldet')}</Text>
      {!list.length && <Hint>{t('Noch keine.')}</Hint>}
      <div style={{ maxHeight: 280, overflowY: 'auto', display: 'flex', flexDirection: 'column' }}>
        {list.map(e => (
          <div key={e.at + e.sender} style={{ display: 'flex', flexDirection: 'column', padding: '4px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` }}>
            <Text>{e.sender}</Text>
            <Text size={200} style={{ color: e.result === 'failed' ? tokens.colorPaletteRedForeground1 : tokens.colorNeutralForeground3 }}>
              {new Date(e.at).toLocaleString()} · {t(RESULT[e.result])}{e.subject ? ` · ${e.subject}` : ''}
            </Text>
          </div>
        ))}
      </div>
    </div>
  )
}
