import { Switch, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { AdminHeading } from '../admin/AdminHeading'
import { useAdminDraft } from '../admin/draft'
import { Hint } from './parts'

// Kennung modplugins (ab 2.2): Verwaltung → Module zeigt auch die Plugins mit Schalter (Einzelheiten unter Plugins).
interface PluginRow { key: string; label: Record<string, string> | null; requires: string[]; loaded: boolean; on: boolean }

export function ModulePlugins() {
  const { fail, notify } = useApp()
  const [list, setList] = useState<PluginRow[] | null>(null)
  const load = () => api.get<{ plugins: PluginRow[] }>('/api/admin/plugins').then(r => setList(r.plugins)).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const name = (p: PluginRow) => p.key === 'mailserver' ? t('Mailserver-Verwaltung') : p.label?.[lang] ?? p.label?.de ?? p.key
  const draft = useAdminDraft<Record<string, boolean>>(Object.fromEntries((list ?? []).map(p => [p.key, p.on])), async v => {
    // Voraussetzungen zuerst einschalten (wie unter Plugins)
    const changed = (list ?? []).filter(p => v[p.key] !== p.on)
      .sort((a, b) => a.requires.includes(b.key) ? 1 : b.requires.includes(a.key) ? -1 : 0)
    try {
      for (const p of changed) await api.put(`/api/admin/plugins/${encodeURIComponent(p.key)}`, { on: v[p.key] })
      if (changed.length) notify(t('Gespeichert'), t('Die Leiste ändert sich nach dem Neuladen der Seite.'))
      await load(); return true
    } catch (e) { fail(e); load(); return false }
  })
  const isOn = (p: PluginRow) => draft.on ? draft.value[p.key] ?? p.on : p.on
  const flip = async (p: PluginRow, on: boolean) => {
    if (draft.on) { draft.set({ ...draft.value, [p.key]: on }); return }
    try { await api.put(`/api/admin/plugins/${encodeURIComponent(p.key)}`, { on }); load() } catch (e) { fail(e) }
  }
  if (!list || list.length === 0) return null
  return <>
    <AdminHeading>{t('Plugins')}</AdminHeading>
    {list.map(p => <div key={p.key} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
      <Switch checked={isOn(p)} disabled={!p.loaded} label={t('{name} aktiviert', { name: name(p) })} onChange={(_, d) => flip(p, d.checked)} />
      {!p.loaded && <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{t('nicht geladen')}</Text>}
    </div>)}
    <Hint>{t('Gilt für alle Benutzer. Version, Siegel und Voraussetzungen stehen unter Plugins.')}</Hint>
  </>
}
