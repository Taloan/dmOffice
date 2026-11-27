import { Button, Field, Input, MessageBar, MessageBarBody, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, server } from '../api'
import { useApp } from '../App'
import { t, tError } from '../i18n'
import { AdminHeading } from './AdminHeading'
import { useAdminDraft } from './draft' // [feature:savemode]

interface State { value: string; env: string | null; envValid: boolean; effective: string; request: string }

/** Wie der Server (Core/PublicAddress.cs): absolute http(s)-Adresse ohne Pfad, Abfrage und Zugangsdaten. */
function check(raw: string): string | null {
  const v = raw.trim()
  if (!v) return null
  let u: URL
  try { u = new URL(v) } catch { return t('Bitte eine vollständige Adresse mit https:// oder http:// angeben.') }
  if (!/^https?:$/.test(u.protocol) || !u.hostname) return t('Bitte eine vollständige Adresse mit https:// oder http:// angeben.')
  if (u.username || u.password || u.pathname !== '/' || v.includes('?') || v.includes('#')) return t('Bitte nur Protokoll, Rechnername und Port angeben, ohne Pfad.')
  return null
}

/**
 * Verwaltung → System → Öffentliche Adresse (Kennung publicurl): Adresse, unter der Empfänger dmOffice erreichen. Gilt für Links,
 * die andere öffnen (Abmeldelinks, Freigabelinks, Bestätigungsmails, Einladungen). DMOFFICE_PUBLIC_URL hat Vorrang.
 */
export function PublicUrlAdmin() {
  const { fail, notify } = useApp()
  const [st, setSt] = useState<State | null>(null)
  const [own, setOwn] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { api.get<State>('/api/admin/public-url').then(setSt).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const commit = async (value: string) => {
    const wrong = check(value)
    if (wrong) { setError(wrong); return false }
    try {
      const next = await api.put<State>('/api/admin/public-url', { value })
      setSt(next); setOwn(null); setError(null)
      server.publicUrl = next.value || null
      notify(t('Gespeichert'))
      return true
    } catch (e: any) { setError(tError(e?.message || String(e))); return false }
  }
  // [feature:savemode]
  const draft = useAdminDraft(st?.value ?? '', commit)
  const value = draft.on ? draft.value : own ?? st?.value ?? ''
  const change = (v: string) => { setError(null); if (draft.on) draft.set(v); else setOwn(v) }
  // [feature:savemode:else]
  // const draft = { on: false }
  // const value = own ?? st?.value ?? ''
  // const change = (v: string) => { setError(null); setOwn(v) }
  // [/feature:savemode]
  if (!st) return null
  const locked = st.env != null
  const shown = locked ? st.env! : value
  const wrong = check(shown)
  const plain = !wrong && /^http:\/\//i.test(shown.trim())
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 720 }}>
      <AdminHeading first>{t('Öffentliche Adresse')}</AdminHeading>
      <Text>{t('Unter dieser Adresse öffnen Empfänger Links, die dmOffice verschickt oder zum Weitergeben anzeigt: Abmeldelinks im Newsletter, Freigabelinks, Bestätigungsmails und Einladungen. Ohne Eintrag gilt die Adresse, unter der dmOffice gerade aufgerufen wird.')}</Text>
      <Field label={t('Öffentliche Adresse des Servers')} validationState={wrong || error ? 'error' : undefined} validationMessage={wrong ?? error ?? undefined}
        hint={locked ? t('Vorgegeben über die Umgebungsvariable DMOFFICE_PUBLIC_URL.') : t('Nur Protokoll, Rechnername und Port, etwa https://office.example.de')}>
        <Input value={shown} readOnly={locked} placeholder="https://office.example.de" onChange={(_, d) => change(d.value)} style={{ maxWidth: 480 }} />
      </Field>
      {locked && !st.envValid && <MessageBar intent="error"><MessageBarBody>{t('DMOFFICE_PUBLIC_URL enthält keine gültige Adresse. Bis zur Korrektur gilt die Adresse darunter.')}</MessageBarBody></MessageBar>}
      {plain && <MessageBar intent="warning"><MessageBarBody>{t('Die Adresse beginnt mit http://. Mit https:// bleiben Links und Anmeldedaten unterwegs verschlüsselt.')}</MessageBarBody></MessageBar>}
      {!draft.on && !locked && <div><Button appearance="primary" disabled={own == null || own === st.value || !!wrong} onClick={() => commit(own ?? '')}>{t('Speichern')}</Button></div>}
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Aktuell gilt: {url}', { url: st.effective })}</Text>
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Die Anmeldung über einen Identitätsanbieter (OIDC) und Rückrufe anderer Dienste verwenden die Adresse, unter der dmOffice aufgerufen wird.')}</Text>
    </div>
  )
}
