import { Button, Field, Input, MessageBar, MessageBarBody, Radio, RadioGroup, Select, Switch, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

interface AiState { provider: 'anthropic' | 'openai'; endpoint: string | null; model: string | null; webSearch: boolean; location: string | null; hasKey: boolean; configured: boolean; claudeModels: string[] }

/**
 * Pukki mit KI: Anbieter, Schluessel und Modell fuer den ganzen Server. Wer KI nutzen darf, steht je Benutzer im Reiter
 * „Benutzer“. Der Schluessel kommt nie zurueck in die Oberflaeche.
 */
export function AiAdmin() {
  const { fail, notify } = useApp()
  const [st, setSt] = useState<AiState | null>(null)
  const [provider, setProvider] = useState<'anthropic' | 'openai'>('anthropic')
  const [endpoint, setEndpoint] = useState('')
  const [model, setModel] = useState('')
  const [key, setKey] = useState('')
  const [webSearch, setWebSearch] = useState(false)
  const [location, setLocation] = useState('')
  const [busy, setBusy] = useState(false)
  const take = (s: AiState) => { setSt(s); setProvider(s.provider); setEndpoint(s.endpoint ?? ''); setModel(s.model ?? ''); setWebSearch(s.webSearch); setLocation(s.location ?? ''); setKey('') }
  useEffect(() => { api.get<AiState>('/api/admin/ai').then(take).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!st) return null
  const save = async (apiKey?: string | null) => {
    try {
      take(await api.put<AiState>('/api/admin/ai', { provider, endpoint, model: provider === 'anthropic' ? model || st.claudeModels[0] : model, apiKey: apiKey ?? (key ? key : null), webSearch: provider === 'anthropic' && webSearch, location }))
      notify(t('Gespeichert'))
    } catch (e) { fail(e) }
  }
  const test = async () => {
    setBusy(true)
    try { const r = await api.post<{ answer: string }>('/api/admin/ai/test'); notify(t('Die KI antwortet'), r.answer) } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const muted = { color: tokens.colorNeutralForeground3 }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 640 }}>
      <Text>{t('dmOffice nutzt die KI nur für Benutzer, denen du das im Reiter „Benutzer“ (Spalte „KI“) erlaubst: Pukki beantwortet damit, was er selbst nicht kann; in den Notizen sucht „Rezepte suchen“ auf Wunsch zusätzlich mit der KI; das DMS ordnet damit auf Wunsch beim Import ein (Korrespondent, Dokumenttyp, Tags).')}</Text>
      <MessageBar intent="warning" layout="multiline">
        <MessageBarBody>
          <div>{t('An die KI gehen:')}</div>
          <ul style={{ margin: '4px 0', paddingLeft: 18 }}>
            <li>{t('Pukki: die Frage, die letzten Nachrichten der Unterhaltung, Datum und Uhrzeit.')}</li>
            <li>{t('Rezeptsuche: nur der Suchbegriff.')}</li>
          </ul>
        </MessageBarBody>
      </MessageBar>
      <Field label={t('Anbieter')}>
        <RadioGroup value={provider} onChange={(_, d) => { setProvider(d.value as any); setModel('') }}>
          <Radio value="anthropic" label={t('Claude (Anthropic)')} />
          <Radio value="openai" label={t('OpenAI-kompatibel (OpenAI, Ollama, LM Studio …)')} />
        </RadioGroup>
      </Field>
      {provider === 'openai' && (
        <Field label={t('Adresse')} hint={t('OpenAI: https://api.openai.com/v1 · Ollama: http://<rechner>:11434/v1')}>
          <Input value={endpoint} onChange={(_, d) => setEndpoint(d.value)} placeholder="https://api.openai.com/v1" />
        </Field>
      )}
      <Field label={t('Modell')} hint={provider === 'anthropic' ? t('Opus antwortet am besten, Haiku am günstigsten.') : t('Name wie beim Anbieter, etwa gpt-5-mini oder llama3.2')}>
        {provider === 'anthropic'
          ? <Select value={model || st.claudeModels[0]} onChange={(_, d) => setModel(d.value)}>{st.claudeModels.map(m => <option key={m} value={m}>{m}</option>)}</Select>
          : <Input value={model} onChange={(_, d) => setModel(d.value)} />}
      </Field>
      <Field label={t('API-Schlüssel')} hint={st.hasKey ? t('Ein Schlüssel ist gespeichert – leer lassen, um ihn zu behalten.') : provider === 'openai' ? t('Für Ollama und LM Studio nicht nötig.') : t('Aus der Claude Console (console.anthropic.com).')}>
        <Input type="password" autoComplete="new-password" value={key} onChange={(_, d) => setKey(d.value)} placeholder={st.hasKey ? '••••••••' : ''} />
      </Field>
      {provider === 'anthropic' && (
        <>
          <Field hint={t('Für Wetter, Nachrichten, Öffnungszeiten … Die Suchbegriffe gehen an die Websuche von Anthropic; jede Suche kostet extra.')}>
            <Switch checked={webSearch} onChange={(_, d) => setWebSearch(d.checked)} label={t('Im Internet suchen')} />
          </Field>
          {webSearch && (
            <Field label={t('Ort')} hint={t('Für Fragen ohne Ortsangabe, etwa „Wie wird das Wetter morgen?“')}>
              <Input value={location} onChange={(_, d) => setLocation(d.value)} placeholder={t('z. B. München')} />
            </Field>
          )}
        </>
      )}
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <Button appearance="primary" onClick={() => save()}>{t('Speichern')}</Button>
        <Button disabled={!st.configured || busy} onClick={test}>{busy ? t('Frage …') : t('Testen')}</Button>
        {st.hasKey && <Button appearance="subtle" onClick={() => save('')}>{t('Schlüssel löschen')}</Button>}
      </div>
      <Text size={200} style={muted}>{st.configured ? t('Eingerichtet.') : t('Noch nicht eingerichtet – Pukki antwortet wie bisher ohne KI.')}</Text>
    </div>
  )
}
