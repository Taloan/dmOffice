import { Button, Checkbox, Dropdown, Field, Input, Option, Spinner, Switch, Text, Textarea, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Hint, Section } from '../settings/parts'

interface BriefingSettings {
  news: boolean; sources: string[]; count: number; prefer: string | null; exclude: string | null
  weather: boolean; place: string | null; lat: number | null; lon: number | null
}
interface Preset { id: string; name: string }
interface PlaceHit { name: string; region: string | null; country: string | null; lat: number; lon: number }

/**
 * Einstellungen → Chat → Pukki: Wetter und Schlagzeilen im Morgenüberblick. Eigene Einstellungen auf dem Server
 * (/api/chat/pukki/briefing), damit ältere Apps sie beim Speichern der Chat-Einstellungen nicht zurücksetzen.
 */
export function PukkiBriefingSection() {
  const { fail } = useApp()
  const [s, setS] = useState<BriefingSettings | null>(null)
  const [presets, setPresets] = useState<Preset[]>([])
  const [own, setOwn] = useState('')
  const [query, setQuery] = useState('')
  const [hits, setHits] = useState<PlaceHit[] | null>(null)
  const [preview, setPreview] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    api.get<{ settings: BriefingSettings; presets: Preset[] }>('/api/chat/pukki/briefing').then(r => {
      setS(r.settings); setPresets(r.presets)
      setOwn(r.settings.sources.filter(x => !r.presets.some(p => p.id === x)).join('\n'))
    }).catch(() => setS(null))
  }, [])
  if (!s) return null
  const save = async (change: Partial<BriefingSettings>) => {
    const next = { ...s, ...change }
    setS(next); setPreview(null)
    try { setS(await api.put<BriefingSettings>('/api/chat/pukki/briefing', next)) } catch (e) { fail(e); setS(s) }
  }
  const presetIds = s.sources.filter(x => presets.some(p => p.id === x))
  const ownList = (text: string) => text.split(/\s+/).map(x => x.trim()).filter(Boolean)
  const search = async () => {
    try { setHits(await api.get<PlaceHit[]>('/api/chat/pukki/briefing/places?' + q({ q: query }))) } catch (e) { fail(e) }
  }
  return (
    <Section title={t('Wetter und Schlagzeilen im Morgenüberblick')}>
      <Switch label={t('Wetter für heute')} checked={s.weather} disabled={!s.lat}
        onChange={(_, d) => save({ weather: d.checked })} />
      <Field label={t('Ort')} hint={s.place ? t('Gewählt: {place}', { place: s.place }) : t('Erst einen Ort suchen und wählen, dann lässt sich das Wetter einschalten.')}>
        <div style={{ display: 'flex', gap: 8, maxWidth: 420 }}>
          <Input value={query} placeholder={t('z. B. Vöhringen')} onChange={(_, d) => setQuery(d.value)} onKeyDown={e => { if (e.key === 'Enter') search() }} style={{ flex: 1 }} />
          <Button onClick={search} disabled={query.trim().length < 2}>{t('Suchen')}</Button>
        </div>
      </Field>
      {hits && (hits.length === 0 ? <Text size={200}>{t('Keinen Ort gefunden.')}</Text> : (
        <div style={{ display: 'flex', flexDirection: 'column', gap: 2, maxWidth: 420 }}>
          {hits.map((h, i) => (
            <Button key={i} appearance="subtle" style={{ justifyContent: 'flex-start' }}
              onClick={() => { setHits(null); setQuery(''); save({ place: h.name, lat: h.lat, lon: h.lon, weather: true }) }}>
              {[h.name, h.region, h.country].filter(Boolean).join(', ')}
            </Button>
          ))}
        </div>
      ))}

      <Switch label={t('Die wichtigsten Schlagzeilen')} checked={s.news} onChange={(_, d) => save({ news: d.checked })} />
      {s.news && <>
        <Field label={t('Quellen')}>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: '0 12px' }}>
            {presets.map(p => (
              <Checkbox key={p.id} label={p.name} checked={presetIds.includes(p.id)}
                onChange={(_, d) => save({ sources: [...(d.checked ? [...presetIds, p.id] : presetIds.filter(x => x !== p.id)), ...ownList(own)] })} />
            ))}
          </div>
        </Field>
        <Field label={t('Eigene Feeds (RSS oder Atom, eine Adresse je Zeile)')}>
          <Textarea value={own} rows={2} onChange={(_, d) => setOwn(d.value)} onBlur={() => save({ sources: [...presetIds, ...ownList(own)] })}
            placeholder="https://www.example.de/rss" style={{ maxWidth: 520 }} />
        </Field>
        <Field label={t('Wie viele')}>
          <Dropdown value={String(s.count)} selectedOptions={[String(s.count)]} onOptionSelect={(_, d) => save({ count: Number(d.optionValue) })} style={{ maxWidth: 120 }}>
            {[3, 5, 7, 10].map(n => <Option key={n} value={String(n)}>{String(n)}</Option>)}
          </Dropdown>
        </Field>
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
          <Field label={t('Zuerst mit diesen Wörtern')} hint={t('mit Komma getrennt')}>
            <Input value={s.prefer ?? ''} onChange={(_, d) => setS({ ...s, prefer: d.value })} onBlur={() => save({ prefer: s.prefer })} placeholder={t('z. B. Vöhringen, Unraid')} />
          </Field>
          <Field label={t('Ohne diese Wörter')} hint={t('mit Komma getrennt')}>
            <Input value={s.exclude ?? ''} onChange={(_, d) => setS({ ...s, exclude: d.value })} onBlur={() => save({ exclude: s.exclude })} placeholder={t('z. B. Fußball')} />
          </Field>
        </div>
      </>}
      {(s.news || s.weather) && (
        <div>
          <Button size="small" disabled={busy} onClick={async () => {
            setBusy(true)
            try { setPreview((await api.get<{ text: string | null }>('/api/chat/pukki/briefing/preview')).text ?? t('Gerade nichts zu holen.')) } catch (e) { fail(e) } finally { setBusy(false) }
          }}>{busy ? <Spinner size="tiny" /> : t('Vorschau')}</Button>
          {preview && <pre style={{ whiteSpace: 'pre-wrap', fontFamily: 'inherit', fontSize: 13, color: tokens.colorNeutralForeground2, marginTop: 8 }}>{preview.replace(/\[([^\]]+)\]\((https?:[^)]+)\)/g, '$1')}</pre>}
        </div>
      )}
      <Hint>{t('Ohne KI: Das Wetter kommt von Open-Meteo, die Schlagzeilen kommen als Überschrift mit Link aus den Nachrichten-Feeds. Ist eines davon an, schickt Pukki den Überblick jeden Morgen, auch ohne Termine.')}</Hint>
    </Section>
  )
}
