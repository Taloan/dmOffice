import {
  Badge, Button, Dialog, Spinner, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody, SearchBox, SpinButton,
  Switch, Text, makeStyles, tokens,
} from '@fluentui/react-components'
// [feature:adminclean]
// [feature:adminclean:else]
// import { Checkbox } from '@fluentui/react-components'
// [/feature:adminclean]
import { Add20Regular, ArrowDownload20Regular, ChevronDown16Regular, ChevronRight16Regular, Delete20Regular, Edit20Regular } from '@fluentui/react-icons'
import { useContext, useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { askConfirm } from '../shell/context'
import { RibbonActions, RibbonButton, RibbonSlot } from '../shell/Ribbon'
import { AdminHeading } from '../admin/AdminHeading'
import { AdminTable, pickCol, type AdminCol } from '../admin/AdminTable'
import { useAdminDraft } from '../admin/draft' // [feature:savemode]
import { useAdminPart, useShows } from '../admin/AdminPage' // [feature:adminclean]
import { GkCountsTable } from './GkCounts'
import { type GkAdmin, type GkAdminStats, type GkList, fmtNum, fmtUnix, listStatusText } from './model'

// Verwaltung → Dienste → GateKeeper (Kennung gatekeeper, 2.2, docs/gatekeeper-konzept.md): Filterlisten des Servers und
// Einstellungen (Ladeabstand, Adressen im eigenen Netz). Server: /api/admin/gatekeeper in DmOffice.Server/GateKeeper/GateKeeperApi.cs.

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, overflowWrap: 'anywhere' },
  bar: { display: 'flex', gap: '8px', alignItems: 'flex-end', flexWrap: 'wrap' },
  fallback: { display: 'flex', gap: '2px', flexWrap: 'wrap' },
})

// Bekannte, frei nutzbare Listen, die auf Ebene der Namen wirken (reine Browser-Listen wie EasyList greifen hier nicht).
// Der Server lädt sie selbst; dmOffice liefert keinen Inhalt mit. Gruppen wie in Werbeblockern: Haken = eingetragen und an.
const H = 'https://raw.githubusercontent.com/hagezi/dns-blocklists/main/adblock/'
interface Known { name: string; url: string; what?: string; recommended?: boolean }
const CATALOG: { group: string; hint?: string; lists: Known[] }[] = [
  { group: 'Werbung und Tracking', hint: 'Eine dieser Listen genügt; mehrere sperren großteils dieselben Adressen.', lists: [
    { name: 'HaGeZi Light', url: H + 'light.txt', what: 'zurückhaltend, kaum Fehlsperren' },
    { name: 'HaGeZi Multi Normal', url: H + 'multi.txt', what: 'ausgewogen', recommended: true },
    { name: 'HaGeZi Pro', url: H + 'pro.txt', what: 'gründlich, selten Fehlsperren' },
    { name: 'StevenBlack Unified Hosts', url: 'https://raw.githubusercontent.com/StevenBlack/hosts/master/hosts', what: 'Werbung und Schadseiten' },
    { name: 'AdGuard DNS Filter', url: 'https://adguardteam.github.io/AdGuardSDNSFilter/Filters/filter.txt' },
  ] },
  { group: 'Schutz vor Schadsoftware, Sicherheit', lists: [
    { name: 'HaGeZi Threat Intelligence', url: H + 'tif.medium.txt', what: 'Schadseiten und Phishing', recommended: true },
    { name: 'HaGeZi Fake', url: H + 'fake.txt', what: 'Fake-Shops und Betrugsseiten' },
  ] },
  { group: 'Belästigungen', lists: [
    { name: 'HaGeZi Pop-up-Werbung', url: H + 'popupads.txt' },
    { name: 'HaGeZi Glücksspiel', url: H + 'gambling.txt' },
  ] },
  { group: 'Tracking der Hersteller', hint: 'Sperrt Datensammlung der Geräte und Apps eines Herstellers; einzelne Dienste des Herstellers können danach fehlen.', lists: [
    { name: 'Amazon', url: H + 'native.amazon.txt' },
    { name: 'Apple', url: H + 'native.apple.txt' },
    { name: 'Huawei', url: H + 'native.huawei.txt' },
    { name: 'LG webOS', url: H + 'native.lgwebos.txt' },
    { name: 'Oppo/Realme', url: H + 'native.oppo-realme.txt' },
    { name: 'Samsung', url: H + 'native.samsung.txt' },
    { name: 'TikTok', url: H + 'native.tiktok.txt' },
    { name: 'Windows/Office', url: H + 'native.winoffice.txt' },
    { name: 'Xiaomi', url: H + 'native.xiaomi.txt' },
  ] },
]

/** Bekannte Filterlisten nach Gruppen zum Abhaken - mit Suche und einklappbaren Gruppen. */
function Catalog({ lists, busy, onAdd, onToggle }: { lists: GkList[]; busy: boolean; onAdd: (k: Known) => Promise<unknown>; onToggle: (l: GkList, on: boolean) => Promise<unknown> }) {
  const [q, setQ] = useState('')
  // Gerade geänderte Liste: bis der Server sie geladen hat, steht „lädt …“ daneben
  const [pending, setPending] = useState<string | null>(null)
  const change = (url: string, p: Promise<unknown>) => { setPending(url); p.finally(() => setPending(null)) }
  const [closed, setClosed] = useState<Set<string>>(() => new Set(['Belästigungen', 'Tracking der Hersteller']))
  const query = q.trim().toLowerCase()
  const byUrl = new Map(lists.map(l => [l.url, l]))
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <SearchBox placeholder={t('Bekannte Filterlisten suchen')} value={q} onChange={(_, d) => setQ(d.value)} style={{ maxWidth: 360, marginBottom: 4 }} />
      {CATALOG.map(g => {
        const items = g.lists.filter(k => !query || k.name.toLowerCase().includes(query) || t(k.what ?? '').toLowerCase().includes(query))
        if (!items.length) return null
        const on = g.lists.filter(k => byUrl.get(k.url)?.enabled).length
        const open = !!query || !closed.has(g.group)
        return (
          <div key={g.group}>
            <Button appearance="transparent" icon={open ? <ChevronDown16Regular /> : <ChevronRight16Regular />} iconPosition="after" style={{ padding: '4px 0', minWidth: 0 }}
              onClick={() => setClosed(c => { const n = new Set(c); if (n.has(g.group)) n.delete(g.group); else n.add(g.group); return n })}>
              <Text weight="semibold" size={400}>{t(g.group)}</Text><Text size={200} style={{ marginLeft: 6, color: tokens.colorNeutralForeground3 }}>{on}/{g.lists.length}</Text>
            </Button>
            {open && g.hint && <Text as="p" size={200} style={{ margin: '0 0 2px', color: tokens.colorNeutralForeground3 }}>{t(g.hint)}</Text>}
            {open && items.map(k => {
              const l = byUrl.get(k.url)
              return (
                <div key={k.url} style={{ display: 'flex', alignItems: 'center', gap: 6, flexWrap: 'wrap' }}>
                  {/* [feature:adminclean] */}
                  <Switch checked={!!l?.enabled} disabled={busy} label={t(k.name)} onChange={(_, d) => change(k.url, l ? onToggle(l, d.checked) : onAdd(k))} />
                  {/* [feature:adminclean:else] */}
                  {/* <Checkbox checked={!!l?.enabled} disabled={busy} label={t(k.name)} onChange={(_, d) => change(k.url, l ? onToggle(l, !!d.checked) : onAdd(k))} /> */}
                  {/* [/feature:adminclean] */}
                  {pending === k.url && <Spinner size="extra-tiny" label={t('Lädt …')} />}
                  {k.what && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t(k.what)}</Text>}
                  {k.recommended && <Badge appearance="tint" color="brand" size="small">{t('empfohlen')}</Badge>}
                  {l?.enabled && l.status === 'ok' && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('{n} Einträge', { n: fmtNum(l.entries) })}</Text>}
                  {l?.enabled && l.status && l.status !== 'ok' && <Badge appearance="tint" color="danger" size="small">{listStatusText(l.status)}</Badge>}
                </div>
              )
            })}
          </div>
        )
      })}
    </div>
  )
}

/** Filterliste anlegen (ohne list) oder bearbeiten. */
function ListDialog({ list, onSave, onClose }: { list?: GkList; onSave: (url: string, name: string) => Promise<boolean>; onClose: () => void }) {
  const [url, setUrl] = useState(list?.url ?? '')
  const [name, setName] = useState(list?.name ?? '')
  const [busy, setBusy] = useState(false)
  const ok = /^https?:\/\/\S+$/i.test(url.trim())
  const save = async () => {
    if (!ok || busy) return
    setBusy(true)
    try { if (await onSave(url.trim(), name.trim())) onClose() } finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open && !busy) onClose() }}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{list ? t('Filterliste bearbeiten') : t('Filterliste hinzufügen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Adresse')} hint={t('Unterstützt werden Hosts-Dateien, reine Adresslisten und Regeln im AdBlock-Format.')}>
              <Input value={url} autoFocus type="url" placeholder="https://" onChange={(_, d) => setUrl(d.value)} onKeyDown={e => { if (e.key === 'Enter') save() }} />
            </Field>
            <Field label={t('Name')} hint={list ? undefined : t('Bleibt das Feld leer, übernimmt dmOffice den Namen aus der Adresse.')}>
              <Input value={name} onChange={(_, d) => setName(d.value)} onKeyDown={e => { if (e.key === 'Enter') save() }} />
            </Field>
            {!list && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Der Server lädt die Filterliste gleich nach dem Hinzufügen.')}</Text>}
          </DialogContent>
          <DialogActions>
            <Button disabled={busy} onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!ok || busy} onClick={save}>{busy ? t('Lädt …') : list ? t('Speichern') : t('Hinzufügen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

export function GateKeeperAdmin() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [st, setSt] = useState<GkAdmin | null>(null)
  // [feature:adminclean]
  // Kennung adminclean: Reiter Filterlisten (bekannte und eingetragene) und Einstellungen (mit Lage und Statistik)
  const shows = useShows()
  const tabbed = useAdminPart().part != null
  // [feature:adminclean:else]
  // const shows = (..._: string[]) => true
  // const tabbed = false
  // [/feature:adminclean]
  const [picked, setPicked] = useState<number | null>(null)
  const [query, setQuery] = useState('')
  const [dialog, setDialog] = useState<'add' | 'edit' | null>(null)
  const [busy, setBusy] = useState(false)
  const inRibbon = useContext(RibbonSlot) != null
  const [stats, setStats] = useState<GkAdminStats | null>(null)
  const load = () => {
    api.get<GkAdminStats>('/api/admin/gatekeeper/stats').then(setStats).catch(fail)
    return api.get<GkAdmin>('/api/admin/gatekeeper').then(setSt).catch(fail)
  }
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  // Einstellungen des Servers: Ladeabstand und Adressen im eigenen Netz
  const saveSettings = async (v: { refreshHours: number; allowPrivate: boolean }) => {
    try { setSt(await api.put<GkAdmin>('/api/admin/gatekeeper', v)); notify(t('Gespeichert')); return true } catch (e) { fail(e); return false }
  }
  const saved = { refreshHours: st?.refreshHours ?? 24, allowPrivate: st?.allowPrivate ?? false }
  // Ohne Menüband der Verwaltung: Änderungen hier sammeln, „Übernehmen“ speichert
  const [local, setLocal] = useState<typeof saved | null>(null)
  // [feature:savemode]
  // Kennung savemode: Speichern/Verwerfen im Menüband der Verwaltung
  const draft = useAdminDraft(saved, saveSettings)
  const set = (v: typeof saved) => draft.on ? draft.set(v) : setLocal(v)
  const shownSettings = draft.on ? draft.value : local ?? saved
  const ownSave = !draft.on
  // [feature:savemode:else]
  // const set = setLocal
  // const shownSettings = local ?? saved
  // const ownSave = true
  // [/feature:savemode]

  if (!st) return null
  const list = st.lists.find(l => l.id === picked) ?? null
  const q = query.trim().toLowerCase()
  const shown = st.lists.filter(l => !q || l.name.toLowerCase().includes(q) || l.url.toLowerCase().includes(q))

  const run = async (fn: () => Promise<GkAdmin>, done?: string) => {
    setBusy(true)
    try { setSt(await fn()); if (done) notify(done); return true } catch (e) { fail(e); return false } finally { setBusy(false) }
  }
  const add = (url: string, name: string) => run(() => api.post<GkAdmin>('/api/admin/gatekeeper/lists', { url, name: name || null }), t('Filterliste hinzugefügt'))
  const edit = (url: string, name: string) => list ? run(() => api.put<GkAdmin>(`/api/admin/gatekeeper/lists/${list.id}`, { url, name: name || null }), t('Gespeichert')) : Promise.resolve(false)
  const toggle = (l: GkList, enabled: boolean) => run(() => api.put<GkAdmin>(`/api/admin/gatekeeper/lists/${l.id}`, { enabled }))
  const remove = async () => {
    if (!list || !await askConfirm(t('Filterliste „{name}“ entfernen?', { name: list.name }), t('GateKeeper wendet die Einträge dieser Filterliste danach nicht mehr an.'), t('Entfernen'))) return
    if (await run(() => api.del<GkAdmin>(`/api/admin/gatekeeper/lists/${list.id}`), t('Filterliste entfernt'))) setPicked(null)
  }
  const refresh = (id?: number) => run(() => api.post<GkAdmin>('/api/admin/gatekeeper/refresh', id != null ? { id } : {}), t('Filterlisten geladen'))

  const cols: AdminCol<GkList>[] = [
    pickCol<GkList, number>({ id: l => l.id, selected: picked, onChange: setPicked }),
    { key: 'name', label: () => t('Name'), width: 180, sort: l => l.name, render: l => l.name },
    { key: 'url', label: () => t('Adresse'), width: 0, sort: l => l.url, render: l => <span className={s.mono}>{l.url}</span> },
    { key: 'enabled', label: () => t('gk|An'), width: 70, sort: l => (l.enabled ? 0 : 1), render: l => <Switch checked={l.enabled} disabled={busy} aria-label={t('gk|An')}
      onClick={e => e.stopPropagation()} onChange={(_, d) => toggle(l, d.checked)} /> },
    { key: 'fetched', label: () => t('Zuletzt geladen'), width: 130, sort: l => l.fetched ?? 0, render: l => fmtUnix(l.fetched) },
    { key: 'status', label: () => t('Status'), width: 150, sort: l => l.status ?? '', render: l => l.status === 'ok'
      ? <Badge appearance="tint" color="success">{listStatusText(l.status)}</Badge>
      : l.status ? <Badge appearance="tint" color="danger">{listStatusText(l.status)}</Badge> : <span className={s.muted}>{listStatusText(l.status)}</span> },
    { key: 'entries', label: () => t('Einträge'), width: 100, sort: l => l.entries, render: l => <span style={{ fontVariantNumeric: 'tabular-nums' }}>{fmtNum(l.entries)}</span> },
  ]

  const actions = <>
    <RibbonButton icon={<Add20Regular />} label={t('Filterliste hinzufügen')} disabled={busy} onClick={() => setDialog('add')} />
    <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={!list || busy} onClick={() => setDialog('edit')} />
    <RibbonButton icon={<Delete20Regular />} label={t('Entfernen')} disabled={!list || busy} onClick={remove} />
    <RibbonButton icon={<ArrowDownload20Regular />} label={list ? t('Gewählte jetzt laden') : t('Alle jetzt laden')} disabled={busy || !st.lists.length}
      title={list ? t('Lädt die gewählte Filterliste neu.') : t('Lädt alle eingeschalteten Filterlisten neu.')} onClick={() => refresh(list?.id)} />
  </>

  const dirtySettings = shownSettings.refreshHours !== saved.refreshHours || shownSettings.allowPrivate !== saved.allowPrivate
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: tabbed ? undefined : 1000 }}>
      {shows('lists') && (inRibbon ? <RibbonActions>{actions}</RibbonActions> : <div className={s.fallback}>{actions}</div>)}
      {shows('settings') && <>
      {!tabbed && <AdminHeading first>GateKeeper</AdminHeading>}
      <Text>{t('GateKeeper hält Werbung und unerwünschten Datenverkehr von Handy-Apps fern. Jeder Benutzer kann in der Android-App von dmOffice selbst auswählen, welche Apps GateKeeper filtern soll, und in seinen Einstellungen eigene Regeln anlegen, die nur für ihn gelten. Die Filterlisten auf dieser Seite gelten für alle Benutzer gemeinsam.')}</Text>
      {st.dns
        ? <Text size={200} className={s.muted}>{t('dmOffice hat einen DNS-Server gefunden. Er beantwortet die DNS-Anfragen der Apps; Anfragen nach gesperrten Adressen beantwortet GateKeeper selbst.')}</Text>
        : <MessageBar intent="warning"><MessageBarBody>{t('dmOffice hat keinen DNS-Server gefunden. Die DNS-Anfragen der Apps erhalten deshalb eine Fehlerantwort. Damit die Apps Adressen auflösen können, muss in der Umgebungsvariable DMOFFICE_GATEKEEPER_DNS die Adresse eines DNS-Servers eingetragen sein.')}</MessageBarBody></MessageBar>}

      {stats && <>
        <AdminHeading>{t('Statistik')}</AdminHeading>
        <Text size={200} className={s.muted}>{t('Die Statistik fasst alle Benutzer zusammen. Welche Adressen im Einzelnen gesperrt wurden, kann jeder Benutzer nur für sich selbst in seinen Einstellungen einsehen.')}</Text>
        <GkCountsTable today={stats.today} month={stats.totals} people={[stats.peopleToday, stats.people]} />
      </>}
      </>}

      {shows('lists') && <>
      <AdminHeading>{t('Bekannte Filterlisten')}</AdminHeading>
      <Catalog lists={st.lists} busy={busy} onToggle={toggle}
        onAdd={k => run(() => api.post<GkAdmin>('/api/admin/gatekeeper/lists', { url: k.url, name: k.name }), t('Filterliste hinzugefügt'))} />

      <AdminHeading>{t('Eingetragene Filterlisten')}</AdminHeading>
      <Text size={200} className={s.muted}>{t('Zusammen {n} Einträge aus den eingeschalteten Filterlisten. Der Server lädt die Filterlisten beim Start, im eingestellten Abstand und auf Knopfdruck.', { n: fmtNum(st.entries) })}</Text>
      {st.lists.length === 0
        ? <Text className={s.muted}>{t('Es ist noch keine Filterliste eingetragen. Eine bekannte Filterliste lässt sich oben auswählen, eine eigene Adresse über „Filterliste hinzufügen“ im Menüband.')}</Text>
        : <>
          <SearchBox placeholder={t('Filterlisten suchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ maxWidth: 360 }} />
          <AdminTable id="gatekeeper.lists" cols={cols} rows={shown} rowKey={l => l.id} initialSort={{ col: 'name', desc: false }}
            onRowOpen={l => { if (busy) return; setPicked(l.id); setDialog('edit') }} // [feature:rowdblopen]
            onRowClick={l => setPicked(p => p === l.id ? p : l.id)} active={picked} />
          {shown.length === 0 && <Text className={s.muted}>{t('Keine passenden Filterlisten.')}</Text>}
        </>}
      </>}

      {shows('settings') && <>
      <AdminHeading>{t('Einstellungen')}</AdminHeading>
      <div className={s.bar}>
        <Field label={t('Filterlisten laden alle')} hint={t('1 bis 168 Stunden')}>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <SpinButton value={shownSettings.refreshHours} min={1} max={168} style={{ width: 100 }}
              onChange={(_, d) => { const v = d.value ?? Number(d.displayValue); if (Number.isFinite(v)) set({ ...shownSettings, refreshHours: Math.min(168, Math.max(1, Math.round(v!))) }) }} />
            <Text>{t('Stunden')}</Text>
          </div>
        </Field>
      </div>
      <Switch checked={shownSettings.allowPrivate} onChange={(_, d) => set({ ...shownSettings, allowPrivate: d.checked })}
        label={t('Adressen im eigenen Netz des Servers erlauben')} />
      <Text size={200} className={s.muted}>{t('Ist die Einstellung aktiviert, können die Apps über GateKeeper auch private Adressen wie 192.168.x.x erreichen, also Geräte im Netz des Servers. Standardmäßig ist die Einstellung deaktiviert.')}</Text>
      {ownSave && <div><Button appearance="primary" disabled={!dirtySettings} onClick={async () => { if (await saveSettings(shownSettings)) setLocal(null) }}>{t('Übernehmen')}</Button></div>}
      </>}

      {dialog === 'add' && <ListDialog onSave={add} onClose={() => setDialog(null)} />}
      {dialog === 'edit' && list && <ListDialog list={list} onSave={edit} onClose={() => setDialog(null)} />}
    </div>
  )
}
