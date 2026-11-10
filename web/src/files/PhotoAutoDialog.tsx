import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Radio, RadioGroup, Text } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Item } from './common'
import { RAW, photoCaps } from './PhotoEditor'

/**
 * Markierte Fotos automatisch verbessern: Vorschlag der Photo-Engine oder der KI je Foto, gemerkt als Entwicklungseinstellungen -
 * die Originale bleiben. Läuft auf dem Server im Hintergrund (Fortschritt in der Aktivitätsanzeige).
 */
export function PhotoAutoDialog({ items, onClose, onStarted }: { items: Item[]; onClose: () => void; onStarted: () => void }) {
  const { fail, notify } = useApp()
  const [caps, setCaps] = useState<{ available: boolean; engine?: boolean; raw?: boolean } | null>(null)
  const [source, setSource] = useState<'engine' | 'ai'>('engine')
  const [busy, setBusy] = useState(false)
  useEffect(() => { photoCaps().then(c => { setCaps(c); if (!c.engine && c.available) setSource('ai') }) }, [])
  // Bearbeitbar: JPG, RAW nur mit der Engine
  const usable = items.filter(i => !i.isDir && (/\.jpe?g$/i.test(i.name) || (caps?.raw && RAW.test(i.name))))
  const start = async () => {
    setBusy(true)
    try {
      const r = await api.post<{ started: number }>('/api/files/photo-auto', { paths: usable.map(i => i.path), source })
      notify(t('{n} Fotos werden im Hintergrund verbessert - Fortschritt in der Aktivitätsanzeige.', { n: r.started }))
      onStarted(); onClose()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <DialogBody>
          <DialogTitle>{t('Automatisch verbessern')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>{t('{n} von {all} markierten Fotos lassen sich bearbeiten.', { n: usable.length, all: items.length })}</Text>
            {caps && !caps.engine && !caps.available && <Text>{t('Weder die Photo-Engine noch eine KI ist eingerichtet.')}</Text>}
            <Field label={t('Vorschlag')}>
              <RadioGroup value={source} onChange={(_, d) => setSource(d.value as 'engine' | 'ai')}>
                <Radio value="engine" label={t('Photo-Engine')} disabled={!caps?.engine} />
                <Radio value="ai" label={t('KI')} disabled={!caps?.available} />
              </RadioGroup>
            </Field>
            <Text size={200}>{t('Die Vorschläge werden als Bearbeitung gemerkt, die Originale bleiben unverändert. Im Editor lässt sich jede Bearbeitung anpassen oder entfernen.')}</Text>
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={busy || usable.length === 0 || !caps || (source === 'engine' ? !caps.engine : !caps.available)} onClick={start}>{t('Starten')}</Button>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
