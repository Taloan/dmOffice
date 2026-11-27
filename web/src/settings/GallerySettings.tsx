import { Switch, Text } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, featureOn } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Section } from './parts'

/** Galerie: Angaben (Sterne, Titel, Beschreibung, Tags, Markierung, Farbe) in die Fotos schreiben oder nur in dmOffice. */
export function GallerySettings() {
  const { fail } = useApp()
  const [write, setWrite] = useState<boolean | null>(null)
  // Demo: nie in Dateien schreiben - der Schalter bleibt aus
  const [demo, setDemo] = useState(false)
  useEffect(() => { api.get<{ writeXmp: boolean; demo?: boolean }>('/api/gallery/settings').then(r => { setWrite(r.writeXmp); setDemo(!!r.demo) }).catch(fail) }, [fail])
  const change = async (on: boolean) => {
    setWrite(on)
    try { await api.put('/api/gallery/settings', { writeXmp: on }) } catch (e) { fail(e); setWrite(!on) }
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
      <Section title={t('XMP')}>
        <Switch checked={!!write} disabled={write == null || demo} label={t('Angaben in die Fotos schreiben')} onChange={(_, d) => change(d.checked)} />
        {demo && <Text size={200}>{t('In der Demo werden Angaben und Standort nur in dmOffice gespeichert, nie in die Fotos.')}</Text>}
        <Text size={200}><b>{t('Aktiviert:')}</b> {featureOn('pickxmp') ? t('Sterne, Markierung (P, X), Titel, Beschreibung, Tags und Farbe werden direkt in den Fotos gespeichert: bei JPEG in den eingebetteten XMP-Daten, bei RAW in einer separaten Sidecar-Datei (.xmp).') : t('Sterne, Titel, Beschreibung, Tags und Farbe werden direkt in den Fotos gespeichert: bei JPEG in den eingebetteten XMP-Daten, bei RAW in einer separaten Sidecar-Datei (.xmp).')}</Text>
        <Text size={200}><b>{t('Deaktiviert:')}</b> {t('Die Angaben werden nur in dmOffice gespeichert. Fotos und Sidecar-Dateien bleiben unverändert.')}</Text>
        <Text size={200}>{t('Vorhandene Metadaten aus den Fotos werden weiterhin gelesen. Sind Angaben auch in dmOffice gespeichert, haben diese Vorrang.')}</Text>
        {!featureOn('pickxmp') && <Text size={200}>{t('Die Markierung (P, X, U) bleibt immer nur in dmOffice.')}</Text>}
        <Text size={200}>{t('Ausnahme: Der beim Verorten festgelegte Standort wird immer in die Fotodaten geschrieben, unabhängig von dieser Einstellung.')}</Text>
      </Section>
    </div>
  )
}
