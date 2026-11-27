import { Badge, Spinner, Switch, Text, tokens } from '@fluentui/react-components'
import { useAdminPart } from './AdminPage' // [feature:adminclean]
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { AdminHeading } from './AdminHeading'
import { useAdminDraft } from './draft' // [feature:savemode]

interface PluginRow {
  key: string; version: string | null; api: number | null; label: Record<string, string> | null; description: Record<string, string> | null
  core: string | null; requires: string[]; server: boolean; web: boolean; signer: string | null; loaded: boolean; problem: string | null; on: boolean
}

/** Verwaltung → System → Plugins (Kennung plugins): gefundene Plugins mit Version, Siegel und Grund, wenn eines nicht läuft. */
export function PluginsAdmin() {
  const { fail, notify } = useApp()
  const [data, setData] = useState<{ root: string; level: number; plugins: PluginRow[] } | null>(null)
  const load = () => api.get<typeof data>('/api/admin/plugins').then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // [feature:adminclean]
  // Kennung adminclean: überall derselbe Name „Mailserver-Verwaltung“
  const name = (p: PluginRow) => p.key === 'mailserver' ? t('Mailserver-Verwaltung') : p.label?.[lang] ?? p.label?.de ?? p.key
  const tabbed = useAdminPart().part != null
  // [feature:adminclean:else]
  // const name = (p: PluginRow) => p.label?.[lang] ?? p.label?.de ?? p.key
  // const tabbed = false
  // [/feature:adminclean]
  const toggle = async (p: PluginRow, on: boolean) => {
    try {
      await api.put(`/api/admin/plugins/${encodeURIComponent(p.key)}`, { on })
      notify(on ? t('{name} eingeschaltet', { name: name(p) }) : t('{name} ausgeschaltet', { name: name(p) }), t('Die Leiste ändert sich nach dem Neuladen der Seite.'))
      load()
    } catch (e) { fail(e) }
  }
  // [feature:savemode]
  // Kennung savemode: Schalter sammeln, mit „Speichern“ im Menüband gelten sie
  const draft = useAdminDraft<Record<string, boolean>>(Object.fromEntries((data?.plugins ?? []).map(p => [p.key, p.on])), async v => {
    // Voraussetzungen zuerst einschalten (Verein braucht CRM: erst CRM, dann Verein)
    const changed = (data?.plugins ?? []).filter(p => v[p.key] !== p.on)
      .sort((a, b) => a.requires.includes(b.key) ? 1 : b.requires.includes(a.key) ? -1 : 0)
    try {
      for (const p of changed) await api.put(`/api/admin/plugins/${encodeURIComponent(p.key)}`, { on: v[p.key] })
      if (changed.length) notify(t('Gespeichert'), t('Die Leiste ändert sich nach dem Neuladen der Seite.'))
      await load(); return true
    } catch (e) { fail(e); load(); return false }
  })
  const isOn = (p: PluginRow) => draft.on ? draft.value[p.key] ?? p.on : p.on
  const flip = (p: PluginRow, on: boolean) => draft.on ? draft.set({ ...draft.value, [p.key]: on }) : toggle(p, on)
  // [feature:savemode:else]
  // const isOn = (p: PluginRow) => p.on
  // const flip = toggle
  // [/feature:savemode]
  if (!data) return <Spinner />
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: tabbed ? undefined : 760 }}>
      <AdminHeading first>{t('Plugins')}</AdminHeading>
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
        {t('Plugins liegen als Ordner unter {dir}. Geladen wird nur, was gesiegelt ist und zur Kern-Schnittstelle (Stufe {level}) passt. Die Daten ausgeschalteter Plugins bleiben erhalten.', { dir: data.root, level: data.level })}
      </Text>
      {data.plugins.length === 0 && <Text>{t('Keine Plugins vorhanden.')}</Text>}
      {data.plugins.map(p => (
        <div key={p.key} style={{ border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusLarge, padding: '10px 12px', display: 'flex', flexDirection: 'column', gap: 4 }}>
          {/* [feature:adminclean] */}
          {/* Drei feste Spalten: Name mit Version, Siegel, Schalter - in jeder Karte an derselben Stelle */}
          {tabbed && <div style={{ display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) 150px 90px', alignItems: 'center', gap: 8 }}>
            <span style={{ minWidth: 0 }}><Text weight="semibold">{name(p)}</Text>{p.version && <Text size={200} style={{ color: tokens.colorNeutralForeground3, marginLeft: 8 }}>{p.version}</Text>}</span>
            <span>{p.loaded
              ? <Badge appearance="outline" color="success">{p.signer === 'dmOffice' ? t('Siegel: dmOffice') : t('Siegel: Betreiber')}</Badge>
              : <Badge appearance="outline" color="danger">{t('nicht geladen')}</Badge>}</span>
            <Switch checked={isOn(p)} disabled={!p.loaded} label={isOn(p) ? t('an') : t('aus')} onChange={(_, d) => flip(p, d.checked)} />
          </div>}
          {/* [/feature:adminclean] */}
          {!tabbed && <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <Text weight="semibold">{name(p)}</Text>
            {p.version && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{p.version}</Text>}
            {p.loaded
              ? <Badge appearance="outline" color="success">{p.signer === 'dmOffice' ? t('Siegel: dmOffice') : t('Siegel: Betreiber')}</Badge>
              : <Badge appearance="outline" color="danger">{t('nicht geladen')}</Badge>}
            <span style={{ flex: 1 }} />
            <Switch checked={isOn(p)} disabled={!p.loaded} label={isOn(p) ? t('an') : t('aus')} onChange={(_, d) => flip(p, d.checked)} />
          </div>}
          {p.description?.[lang] ?? p.description?.de ? <Text size={200}>{p.description?.[lang] ?? p.description?.de}</Text> : null}
          {p.problem && <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{t(p.problem)}</Text>}
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            {[p.key, p.server && t('Server-Teil'), p.web && t('Web-Teil'), p.requires.length > 0 && t('braucht {list}', { list: p.requires.join(', ') }), p.api != null && t('API-Stufe {n}', { n: p.api })].filter(Boolean).join(' · ')}
          </Text>
        </div>
      ))}
    </div>
  )
}
