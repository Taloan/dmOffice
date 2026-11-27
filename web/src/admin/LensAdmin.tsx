import { Tab, TabList, Text, tokens } from '@fluentui/react-components'
import { Add20Regular, ArrowDown20Regular, ArrowUp20Regular, Delete20Regular, Edit20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { askConfirm } from '../shell/context'
import { RibbonActions, RibbonButton, RibbonDivider } from '../shell/Ribbon'
import { ExplorerTable, pickCol, type ExplorerCol } from '../sdk'
import { LensProfilesTab } from '../settings/LensLibrary'
import { RuleDialog, mm, type LibRow, type Rule } from '../settings/LensSettings'

interface State { adminRules?: Rule[]; names?: Record<string, string | null>; engine: boolean }

/**
 * Verwaltung → Apps → Galerie → Objektivprofile (Kennung lenslib): Profile vom Admin (ZIP-Datei, Einzeldateien) und Regeln vom Admin.
 * Sie gelten für alle Personen; eigene Regeln einer Person gehen vor. Server: Gallery/LensProfiles.cs (/list?scope=admin, /admin/…).
 */
export function LensAdmin() {
  const { fail } = useApp()
  const [tab, setTab] = useState<'profiles' | 'rules'>('profiles')
  const [st, setSt] = useState<State | null>(null)
  const [active, setActive] = useState<string | null>(null)
  const [rule, setRule] = useState<Rule | null>(null)
  const [lib, setLib] = useState<LibRow[] | null>(null)
  const load = () => api.get<State>('/api/gallery/lens/').then(setSt).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!st) return null
  const rules = st.adminRules ?? []
  const nameOf = (id: string) => st.names?.[id] ?? t('(gelöscht)')
  const save = async (next: Rule[]) => {
    try {
      const r = await api.put<{ rules: Rule[]; names: Record<string, string | null> }>('/api/gallery/lens/admin/rules', { rules: next })
      setSt(x => x && { ...x, adminRules: r.rules, names: { ...x.names, ...r.names } })
      return true
    } catch (e) { fail(e); return false }
  }
  const edit = (r: Rule | null) => {
    if (!lib) api.get<{ items: LibRow[] }>('/api/gallery/lens/library').then(x => setLib(x.items)).catch(() => setLib([]))
    setRule(r ?? { id: '', camera: '', lens: '', focalMin: null, focalMax: null, profile: '' })
  }
  const move = (d: number) => {
    const i = rules.findIndex(r => r.id === active), j = i + d
    if (i < 0 || j < 0 || j >= rules.length) return
    const next = [...rules];[next[i], next[j]] = [next[j], next[i]]
    save(next)
  }
  const remove = async () => {
    if (!active || !await askConfirm(t('Regel löschen?'), t('Fotos, die diese Regel bisher bekommen haben, behalten ihre gespeicherte Bearbeitung.'), t('Löschen'))) return
    if (await save(rules.filter(r => r.id !== active))) setActive(null)
  }
  const focalText = (r: Rule) => r.focalMin == null && r.focalMax == null ? t('alle') : r.focalMin === r.focalMax ? mm(r.focalMin!)
    : `${r.focalMin != null ? mm(r.focalMin) : '…'} – ${r.focalMax != null ? mm(r.focalMax) : '…'}`
  const muted = { color: tokens.colorNeutralForeground3 }
  const cols: ExplorerCol<Rule>[] = [
    pickCol<Rule, string>({ id: r => r.id, selected: active, onChange: setActive }),
    { key: 'order', label: () => t('Reihenfolge'), width: 100, sort: r => rules.indexOf(r), render: r => rules.indexOf(r) + 1 },
    { key: 'camera', label: () => t('Kamera enthält'), width: 150, sort: r => r.camera ?? '', render: r => r.camera || <span style={muted}>{t('jede')}</span> },
    { key: 'lens', label: () => t('Objektiv enthält'), width: 0, sort: r => r.lens ?? '', render: r => r.lens || <span style={muted}>{t('jedes')}</span> },
    { key: 'focal', label: () => t('Brennweite'), width: 120, sort: r => r.focalMin ?? 0, render: focalText },
    { key: 'profile', label: () => t('Profil'), width: 220, sort: r => nameOf(r.profile).toLowerCase(), render: r => nameOf(r.profile) },
  ]
  return <>
    <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as typeof tab)}>
      <Tab value="profiles">{t('Profile')}</Tab>
      <Tab value="rules">{t('Regeln')}</Tab>
    </TabList>
    {tab === 'profiles' && <LensProfilesTab scope="admin" engine={st.engine} onChanged={load} />}
    {tab === 'rules' && <>
      <RibbonActions>
        <RibbonButton primary icon={<Add20Regular />} label={t('Neue Regel')} onClick={() => edit(null)} />
        <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={!active} onClick={() => edit(rules.find(r => r.id === active) ?? null)} />
        <RibbonButton icon={<ArrowUp20Regular />} label={t('Nach oben')} disabled={!active || rules[0]?.id === active} onClick={() => move(-1)} />
        <RibbonButton icon={<ArrowDown20Regular />} label={t('Nach unten')} disabled={!active || rules[rules.length - 1]?.id === active} onClick={() => move(1)} />
        <RibbonDivider />
        <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!active} onClick={remove} />
      </RibbonActions>
      <Text>{t('Regeln vom Admin gelten für alle Benutzer. Eigene Regeln eines Benutzers haben Vorrang. Es gilt jeweils die erste passende Regel.')}</Text>
      <ExplorerTable id="lens-admin-rules" cols={cols} rows={rules} rowKey={r => r.id} initialSort={{ col: 'order', desc: false }}
        onRowOpen={r => { setActive(r.id); edit(r) }} // [feature:rowdblopen]
        onRowClick={r => setActive(r.id)} active={active} />
      {rules.length === 0 && <Text style={muted}>{t('Noch keine Regeln.')}</Text>}
    </>}
    {rule && <RuleDialog rule={rule} items={[]} lib={lib} nameOf={nameOf} scope="admin" onClose={() => setRule(null)}
      onSave={async r => {
        const saved = r.id ? r : { ...r, id: Date.now().toString(36) + Math.random().toString(36).slice(2, 8) }
        const next = r.id ? rules.map(x => x.id === r.id ? saved : x) : [...rules, saved]
        if (await save(next)) { setRule(null); setActive(saved.id) }
      }} />}
  </>
}
