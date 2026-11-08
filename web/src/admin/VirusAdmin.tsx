import { Button, Field, Input, MessageBar, MessageBarBody, Switch, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { AdminHeading } from './AdminHeading'

interface State { clamAv: boolean; host: string; port: number; maxMb: number; required: boolean; findings: { at: string; user: string; where: string; virus: string }[] }
interface TestResult { ok: boolean; version?: string; eicar?: string; error?: string }

/**
 * Virenprüfung mit ClamAV (clamd über TCP): Dateien beim Speichern (Web, WebDAV, App, Chat, Tagebuch, Office), Bilder und Anhänge
 * von Notizen, neue Mails mit Anhang. Aus, bis der Admin sie einschaltet; der Scanner läuft als eigener Container.
 */
export function VirusAdmin() {
  const { fail, notify } = useApp()
  const [st, setSt] = useState<State | null>(null)
  const [draft, setDraft] = useState<State | null>(null)
  const [test, setTest] = useState<TestResult | null>(null)
  const [testing, setTesting] = useState(false)
  useEffect(() => { api.get<State>('/api/admin/virus').then(r => { setSt(r); setDraft(r) }).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!st || !draft) return null
  const set = (p: Partial<State>) => setDraft(d => d && { ...d, ...p })
  const changed = JSON.stringify({ ...draft, findings: [] }) !== JSON.stringify({ ...st, findings: [] })
  const save = async () => {
    try { const r = await api.put<State>('/api/admin/virus', draft); setSt(r); setDraft(r); notify(t('Gespeichert')) } catch (e) { fail(e) }
  }
  const runTest = async () => {
    setTesting(true); setTest(null)
    try { setTest(await api.post<TestResult>('/api/admin/virus/test', { host: draft.host, port: draft.port })) } catch (e) { fail(e) } finally { setTesting(false) }
  }
  const muted = { color: tokens.colorNeutralForeground3 }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 680 }}>
      <AdminHeading first>{t('Virenprüfung')}</AdminHeading>
      <Text>{t('dmOffice prüft Hochgeladenes (Dateien, Chat, Tagebuch, Notizen) und neue Mails mit Anhang mit ClamAV. Funde werden nicht gespeichert; bei Mails sperrt dmOffice die Anhänge und zeigt einen Warnhinweis.')}</Text>
      <Text size={200} style={muted}>{t('ClamAV läuft als eigener Container (Image clamav/clamav, Port 3310) und braucht rund 1 bis 1,5 GB Arbeitsspeicher; die Signaturen lädt er selbst nach. Größere Dateien als die Grenze werden nicht geprüft.')}</Text>
      <Switch checked={draft.clamAv} label={t('Virenprüfung einschalten')} onChange={(_, d) => set({ clamAv: d.checked })} />
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <Field label={t('Rechner')} style={{ flex: 1, minWidth: 200 }}><Input value={draft.host} placeholder="clamav" onChange={(_, d) => set({ host: d.value })} /></Field>
        <Field label={t('Port')} style={{ width: 110 }}><Input type="number" value={String(draft.port)} onChange={(_, d) => set({ port: Number(d.value) || 3310 })} /></Field>
        <Field label={t('Grenze (MB)')} style={{ width: 120 }}><Input type="number" value={String(draft.maxMb)} onChange={(_, d) => set({ maxMb: Number(d.value) || 100 })} /></Field>
      </div>
      <Switch checked={draft.required} label={t('Ohne erreichbaren Scanner nichts annehmen')} onChange={(_, d) => set({ required: d.checked })} />
      <Text size={200} style={muted}>{t('Aus: Ist der Scanner nicht erreichbar, speichert dmOffice trotzdem und vermerkt es im Protokoll.')}</Text>
      <div style={{ display: 'flex', gap: 8 }}>
        <Button appearance="primary" disabled={!changed} onClick={save}>{t('Speichern')}</Button>
        <Button disabled={testing || !draft.host.trim()} onClick={runTest}>{testing ? t('Prüft …') : t('Verbindung testen')}</Button>
      </div>
      {test && <MessageBar intent={test.ok ? 'success' : 'error'}><MessageBarBody>
        {test.ok ? t('Scanner erreichbar ({version}); die EICAR-Testdatei wurde erkannt ({name}).', { version: test.version ?? '', name: test.eicar ?? '' }) : test.error}
      </MessageBarBody></MessageBar>}

      <AdminHeading>{t('Letzte Funde')}</AdminHeading>
      {st.findings.length === 0 ? <Text style={muted}>{t('Keine.')}</Text> : st.findings.slice(0, 30).map((f, i) =>
        <Text key={i} size={200}>{new Date(f.at).toLocaleString(locale)} · {f.virus} · {f.where}</Text>)}
    </div>
  )
}
