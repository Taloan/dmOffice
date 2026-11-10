import { Switch } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { AdminHeading } from '../admin/AdminHeading'
import { Hint } from './parts'

/** Hauptschalter eines Moduls (Server: Features.Modules). released: in dieser Version freigeschaltet. */
export interface ModuleState { id: string; on: boolean; released: boolean }

const names: Record<string, () => string> = { audio: () => t('Audiothek'), dms: () => 'DMS' }

/** Eingebaute Photo-Engine. external: läuft auf einem anderen Rechner (PHOTO_ENGINE_URL), der Schalter wirkt dann nicht. */
interface EngineState { builtIn: boolean; external: boolean; on: boolean; running: boolean }

/** Verwaltung → Module: Hauptschalter aller freigeschalteten Module, serverweit an/aus. */
export function ModulesAdmin() {
  const { fail, refreshMe } = useApp()
  const [list, setList] = useState<ModuleState[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [engine, setEngine] = useState<EngineState | null>(null)
  useEffect(() => {
    api.get<ModuleState[]>('/api/admin/modules').then(setList).catch(fail)
    api.get<EngineState>('/api/admin/photo-engine').then(setEngine).catch(fail)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const toggle = async (id: string, next: boolean) => {
    setBusy(true)
    try {
      setList(await api.put<ModuleState[]>('/api/admin/modules', { id, on: next }))
      // Neuer Stand für Leiste und Einstellungen (featuresOff) ohne Neuladen
      refreshMe()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <>
      <AdminHeading>{t('Module')}</AdminHeading>
      {list?.filter(m => m.released).map(m => (
        <Switch key={m.id} checked={m.on} disabled={busy} label={t('{name} eingeschaltet', { name: names[m.id]?.() ?? m.id })} onChange={(_, d) => toggle(m.id, d.checked)} />
      ))}
      <Hint>{t('Gilt für alle Benutzer. Ausgeschaltet fehlt das Modul in Web und App, seine Hintergrundarbeit ruht; gespeicherte Inhalte bleiben erhalten.')}</Hint>
      {engine && (engine.builtIn || engine.external) && <>
        <AdminHeading>{t('Photo-Engine')}</AdminHeading>
        {engine.external
          ? <Hint>{t('dmOffice verwendet eine Photo-Engine auf einem anderen Rechner (PHOTO_ENGINE_URL).')}</Hint>
          : <>
            <Switch checked={engine.on} disabled={busy} label={t('Photo-Engine eingeschaltet')}
              onChange={(_, d) => { setBusy(true); api.put<EngineState>('/api/admin/photo-engine', { on: d.checked }).then(setEngine).catch(fail).finally(() => setBusy(false)) }} />
            <Hint>{t('Rechnet auf diesem Server die RAW-Entwicklung und Bearbeitungsvorschläge. Ausgeschaltet fehlen diese Funktionen. Fotos ohne RAW lassen sich weiter bearbeiten.')}</Hint>
          </>}
      </>}
    </>
  )
}
