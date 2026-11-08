import { Switch, Text } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { PageTitle, Section } from './parts'

/** Galerie: Angaben (Sterne, Titel, Beschreibung, Stichwörter, Markierung, Farbe) in die Fotos schreiben oder nur in dmOffice. */
export function GallerySettings() {
  const { fail } = useApp()
  const [write, setWrite] = useState<boolean | null>(null)
  useEffect(() => { api.get<{ writeXmp: boolean }>('/api/gallery/settings').then(r => setWrite(r.writeXmp)).catch(fail) }, [fail])
  const change = async (on: boolean) => {
    setWrite(on)
    try { await api.put('/api/gallery/settings', { writeXmp: on }) } catch (e) { fail(e); setWrite(!on) }
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
      <PageTitle>{t('Angaben in den Fotos')}</PageTitle>
      <Section title={t('XMP')}>
        <Switch checked={!!write} disabled={write == null} label={t('Angaben in die Fotos schreiben')} onChange={(_, d) => change(d.checked)} />
        <Text size={200}><b>{t('Aktiviert:')}</b> {t('Sterne, Titel, Beschreibung, Stichwörter und Farbe werden direkt in den Fotos gespeichert: bei JPEG in den eingebetteten XMP-Daten, bei RAW in einer separaten Sidecar-Datei (.xmp).')}</Text>
        <Text size={200}><b>{t('Deaktiviert:')}</b> {t('Die Angaben werden nur in dmOffice gespeichert. Fotos und Sidecar-Dateien bleiben unverändert.')}</Text>
        <Text size={200}>{t('Vorhandene Metadaten aus den Fotos werden weiterhin gelesen. Sind Angaben auch in dmOffice gespeichert, haben diese Vorrang.')}</Text>
        <Text size={200}>{t('Die Markierung (P, X, U) bleibt wie bei Lightroom immer nur in dmOffice.')}</Text>
        <Text size={200}>{t('Ausnahme: Der beim Verorten festgelegte Standort wird immer in die Fotodaten geschrieben, unabhängig von dieser Einstellung.')}</Text>
      </Section>
    </div>
  )
}
