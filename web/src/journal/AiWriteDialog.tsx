import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Radio, RadioGroup, Spinner, Text, tokens,
} from '@fluentui/react-components'
import { useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'

export interface AiSettings { length: 'short' | 'medium' | 'long'; fitness: boolean; events: boolean; place: boolean; weather: boolean; news: boolean }
export interface AiState { allowed: boolean; search: boolean; feeds: boolean; settings: AiSettings }

/**
 * Eintrag von der KI ausformulieren lassen: Länge und was mitgeht wählen (die Wahl ist beim nächsten Mal vorgegeben),
 * Ergebnis ansehen, dann übernehmen oder neu schreiben lassen.
 */
export function AiWriteDialog({ state, journal, date, mood, title, notes, place, weather, onClose }: {
  state: AiState; journal: number; date: string; mood: number | null; title: string; notes: string; place: string | null; weather: string | null
  onClose: (html: string | null) => void
}) {
  const { fail } = useApp()
  const [o, setO] = useState<AiSettings>(state.settings)
  const [busy, setBusy] = useState(false)
  const [html, setHtml] = useState<string | null>(null)
  const set = (p: Partial<AiSettings>) => setO(x => ({ ...x, ...p }))
  const write = async () => {
    setBusy(true)
    try {
      const r = await api.post<{ html: string }>('/api/journal/ai/write', {
        date, ...o, placeName: place, weatherText: weather, mood, title, notes, lang, journal,
      })
      setHtml(r.html)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const muted = { color: tokens.colorNeutralForeground3 }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(null) }}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{t('Mit KI schreiben')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <RadioGroup layout="horizontal" value={o.length} onChange={(_, d) => set({ length: d.value as AiSettings['length'] })}>
              <Radio value="short" label={t('Kurz')} /><Radio value="medium" label={t('Mittel')} /><Radio value="long" label={t('Lang')} />
            </RadioGroup>
            <Text weight="semibold" size={200}>{t('Mitschicken')}</Text>
            <div style={{ display: 'flex', flexDirection: 'column' }}>
              <Checkbox checked={o.fitness} onChange={(_, d) => set({ fitness: !!d.checked })} label={t('Bewegung (Schritte, Strecke, Schlaf)')} />
              <Checkbox checked={o.events} onChange={(_, d) => set({ events: !!d.checked })} label={t('Termine des Tages')} />
              <Checkbox checked={o.place} disabled={!place} onChange={(_, d) => set({ place: !!d.checked })} label={place ? t('Ort: {place}', { place }) : t('Ort (im Eintrag nicht angegeben)')} />
              <Checkbox checked={o.weather} disabled={!weather} onChange={(_, d) => set({ weather: !!d.checked })} label={weather ? t('Wetter: {weather}', { weather }) : t('Wetter (im Eintrag nicht angegeben)')} />
              <Checkbox checked={o.news} disabled={!state.feeds && !state.search} onChange={(_, d) => set({ news: !!d.checked })}
                label={state.search && place ? t('Drei Schlagzeilen des Tages und eine aus der Nähe') : t('Drei Schlagzeilen des Tages')} />
            </div>
            <Text size={200} style={muted}>
              {t('Datum, Stimmung, Titel und dein bisheriger Text gehen immer mit, dazu das Angehakte – an die KI, die in der Verwaltung eingerichtet ist.')}
              {o.news && !state.feeds ? ' ' + t('Schlagzeilen sucht die KI im Internet.') : ''}
            </Text>
            {busy && <Spinner size="small" label={t('Schreibt …')} />}
            {html && !busy && <div style={{ border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: 6, padding: '6px 12px', maxHeight: '40vh', overflowY: 'auto' }}
              dangerouslySetInnerHTML={{ __html: html }} />}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            {html ? <>
              <Button disabled={busy} onClick={write}>{t('Neu schreiben')}</Button>
              <Button appearance="primary" disabled={busy} onClick={() => onClose(html)}>{notes.trim() ? t('Anhängen') : t('Übernehmen')}</Button>
            </> : <Button appearance="primary" disabled={busy} onClick={write}>{t('Schreiben')}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
