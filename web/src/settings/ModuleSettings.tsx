import { Switch } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { AdminHeading } from '../admin/AdminHeading'
import { Hint } from './parts'
import { useAdminDraft } from '../admin/draft' // [feature:savemode]
import { askConfirm } from '../shell/context' // [feature:savemode]
import { ModulePlugins } from './ModulePlugins' // [feature:modplugins]
import { featureOn } from '../api' // [feature:modplugins]

/** Hauptschalter eines Moduls (Server: Features.Modules). released: in dieser Version freigeschaltet. */
export interface ModuleState { id: string; on: boolean; released: boolean }

const names: Record<string, () => string> = { audio: () => t('Audiothek'), dms: () => 'DMS',
  journal: () => t('Tagebuch'), // [feature:journal]
}

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
  // [feature:savemode]
  // Kennung savemode (Verwaltung): Schalter sammeln; was ausgeschaltet wird, fragt beim Speichern nach
  const label = (id: string) => names[id]?.() ?? id
  const draft = useAdminDraft<{ mods: Record<string, boolean>; engine: boolean | null }>(
    { mods: Object.fromEntries((list ?? []).map(m => [m.id, m.on])), engine: engine?.on ?? null },
    async v => {
      try {
        let next = list
        for (const m of list ?? []) if (v.mods[m.id] !== undefined && v.mods[m.id] !== m.on) next = await api.put<ModuleState[]>('/api/admin/modules', { id: m.id, on: v.mods[m.id] })
        if (next !== list) { setList(next); refreshMe() }
        if (engine && v.engine !== null && v.engine !== engine.on) setEngine(await api.put<EngineState>('/api/admin/photo-engine', { on: v.engine }))
        return true
      } catch (e) { fail(e); return false }
    },
    async (from, to) => {
      const off = (list ?? []).filter(m => from.mods[m.id] && to.mods[m.id] === false).map(m => label(m.id))
      if (from.engine && to.engine === false) off.push(t('Photo-Engine'))
      return off.length === 0 || askConfirm(t('Ausschalten?'), t('Ausgeschaltet wird: {list}. Was ausgeschaltet ist, fehlt in Web und App für alle Benutzer, seine Hintergrundarbeit ruht. Gespeicherte Inhalte bleiben erhalten.', { list: off.join(', ') }), t('Ausschalten'))
    })
  const modOn = (m: ModuleState) => draft.on ? draft.value.mods[m.id] ?? m.on : m.on
  const flip = (id: string, next: boolean) => draft.on ? draft.set({ ...draft.value, mods: { ...draft.value.mods, [id]: next } }) : toggle(id, next)
  const engineOn = (e: EngineState) => draft.on ? draft.value.engine ?? e.on : e.on
  // [feature:savemode:else]
  // const modOn = (m: ModuleState) => m.on
  // const flip = toggle
  // const engineOn = (e: EngineState) => e.on
  // [/feature:savemode]
  return (
    <>
      <AdminHeading>{t('Module')}</AdminHeading>
      {list?.filter(m => m.released).map(m => (
        <Switch key={m.id} checked={modOn(m)} disabled={busy} label={t('{name} aktiviert', { name: names[m.id]?.() ?? m.id })} onChange={(_, d) => flip(m.id, d.checked)} />
      ))}
      <Hint>{t('Gilt für alle Benutzer. Ist ein Modul deaktiviert, fehlt es in Web und App und seine Hintergrundarbeit ruht. Gespeicherte Inhalte bleiben erhalten.')}</Hint>
      {engine && (engine.builtIn || engine.external) && <>
        {/* [feature:modplugins] */}
        <AdminHeading>{featureOn('modplugins') ? t('Server-Erweiterungen') : t('Photo-Engine')}</AdminHeading>
        {/* [feature:modplugins:else] */}
        {/* <AdminHeading>{t('Photo-Engine')}</AdminHeading> */}
        {/* [/feature:modplugins] */}
        {engine.external
          ? <Hint>{t('dmOffice verwendet eine Photo-Engine auf einem anderen Rechner (PHOTO_ENGINE_URL).')}</Hint>
          : <>
            <Switch checked={engineOn(engine)} disabled={busy} label={t('Photo-Engine aktiviert')}
              onChange={(_, d) => {
                if (draft.on) { draft.set({ ...draft.value, engine: d.checked }); return } // [feature:savemode]
                setBusy(true); api.put<EngineState>('/api/admin/photo-engine', { on: d.checked }).then(setEngine).catch(fail).finally(() => setBusy(false)) }} />
            <Hint>{t('Rechnet auf diesem Server die RAW-Entwicklung und Bearbeitungsvorschläge. Ist die Photo-Engine deaktiviert, fehlen diese Funktionen. Fotos ohne RAW lassen sich weiter bearbeiten.')}</Hint>
          </>}
      </>}
      {featureOn('modplugins') && featureOn('plugins') && <ModulePlugins />} {/* [feature:modplugins] */}
    </>
  )
}
