import { saveUrl } from '../shell/browser'
import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, InfoLabel, Input, MessageBar, MessageBarBody, Select, Switch, Text, makeStyles, tokens } from '@fluentui/react-components'
import { ArrowClockwise20Regular, ArrowDownload20Regular, Checkmark20Regular, Delete20Regular, LockClosed20Regular, LockOpen20Regular, PersonAvailable20Regular, Search20Regular, ShieldCheckmark20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, featureOn, q } from '../api'
import { RibbonActions, RibbonButton, RibbonDivider } from '../shell/Ribbon'
import { useApp } from '../App'
import { t, locale } from '../i18n'
import { askConfirm } from '../shell/context'
import { AdminHeading } from './AdminHeading'
import { AdminCol, AdminTable, pickCol } from './AdminTable'
import { useAdminSave } from './draft' // [feature:savemode]
import { useAdminPart, useShows } from './AdminPage' // [feature:adminclean]
import { useRef } from 'react' // [feature:savemode]
import { SecurityProxies } from './SecurityProxies' // [feature:proxyconfirm]

interface Settings { traps: boolean; firstDays: number; seenDays: number; keepDays: number; abuseCheck: boolean; abuseScore: number; abuseDays: number; abuseReport: boolean }
interface Block { key: string; reason: string; since: string; until: string | null; level: number; hits: number; last: string; by: string | null; note: string | null }
interface Allow { cidr: string; note: string | null; added: string }
type AllowRow = { cidr: string; note: string | null; added: string | null; fixed: boolean }
interface Ev { id: number; at: string; key: string; kind: string; method: string; path: string; agent: string | null; detail: string | null }
interface State {
  off: boolean; settings: Settings; day: Record<string, number>; week: Record<string, number>
  top: { key: string; count: number; last: string }[]; blocks: Block[]; allow: Allow[]; builtIn: string[]
  me: { address: string; allowed: boolean } | null; abuseKey: boolean
}

const useStyles = makeStyles({
  page: { display: 'flex', flexDirection: 'column', gap: '10px' },
  muted: { color: tokens.colorNeutralForeground3 },
  row: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'flex-end' },
  tiles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: '8px' },
  // Einstellungen untereinander: Beschriftung links, Feld rechts - nichts springt, wenn ein Feld einen Hinweis hat
  form: { display: 'grid', gridTemplateColumns: 'minmax(220px, max-content) minmax(0, 560px)', gap: '10px 16px', alignItems: 'center', '@media (max-width: 767px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  tile: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}` },
})

/** Arten der Ereignisse - so heißen sie in der Oberfläche */
/** Vermerke des Servers (auf Deutsch gespeichert) in der Sprache der Oberfläche; fremde Teile (Pfad, Name) bleiben. */
function noteText(note: string | null | undefined) {
  if (!note) return note
  let m = /^Falle: (.*)$/.exec(note)
  if (m) return t('Falle: {request}', { request: m[1] })
  if (note === 'an AbuseIPDB gemeldet') return t('an AbuseIPDB gemeldet')
  m = /^von ([^:]*)(?:: (.*))?$/.exec(note)
  if (m) return t('von {name}', { name: m[1] }) + (m[2] ? ': ' + m[2] : '')
  return note
}

const KINDS: [string, () => string, () => string][] = [
  ['falle', () => t('Abtasten'), () => t('Unbekannter Pfad ohne Anmeldung')],
  ['scanner', () => t('Scanner'), () => t('Werkzeug zum Abtasten im User-Agent')],
  ['anmeldung', () => t('Anmeldung'), () => t('Falsches Passwort oder falscher Zweitfaktor')],
  ['app-passwort', () => t('App-Passwort'), () => t('App-Passwort, das es nie gab')],
  ['link', () => t('Freigabelink'), () => t('Unbekannter Freigabelink')],
  ['gesperrt', () => t('Abgewiesen'), () => t('Anfrage einer gesperrten Adresse')],
  ['altes-geraet', () => t('Altes Gerät'), () => t('Gelöschtes App-Passwort, kein Raten')],
  ['ruf', () => t('AbuseIPDB'), () => t('Bei AbuseIPDB als Angreifer bekannt')],
]
/** Handlungen in der Verwaltung - in der Liste der Ereignisse, nicht in der Übersicht */
const ADMIN_KINDS: [string, () => string][] = [['von-hand', () => t('Von Hand gesperrt')], ['freigegeben', () => t('Freigegeben')], ['gemeldet', () => t('An AbuseIPDB gemeldet')]]
const kindName = (k: string) => KINDS.find(x => x[0] === k)?.[1]() ?? ADMIN_KINDS.find(x => x[0] === k)?.[1]() ?? k
const when = (iso: string) => new Date(iso).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' })
const ms = (iso: string | null) => iso ? Date.parse(iso) : Number.MAX_SAFE_INTEGER

/**
 * Verwaltung → Sicherheit: das Security Center. Ohne Anmeldung gibt es nur feste Wege - wer etwas anderes abfragt, wird
 * sofort gesperrt. Übersicht, Sperren, Erlaubt-Liste, jedes einzelne Ereignis, Einstellungen.
 */
export function SecurityAdmin() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [data, setData] = useState<State | null>(null)
  const [events, setEvents] = useState<Ev[] | null>(null)
  const [query, setQuery] = useState('')
  const [block, setBlock] = useState({ address: '', days: '0', note: '' })
  const [allow, setAllow] = useState({ cidr: '', note: '' })
  const [form, setForm] = useState<Settings | null>(null)
  const [abuseKey, setAbuseKey] = useState('')
  // Ereignisse: erst die letzten 10, der Rest auf Wunsch
  const [allEvents, setAllEvents] = useState(false)
  // [feature:adminclean]
  // Kennung adminclean: Reiter Übersicht, Sperren und Erlaubt-Liste, Reverse-Proxys, Ereignisse, AbuseIPDB, Einstellungen
  const shows = useShows()
  const tabbed = useAdminPart().part != null
  // [feature:adminclean:else]
  // const shows = (..._: string[]) => true
  // const tabbed = false
  // [/feature:adminclean]
  // Mit Menüband: Auswahl in den Tabellen, Sperren und Erlauben als Dialog
  const ribbon = featureOn('adminribbon')
  const [selBlock, setSelBlock] = useState<string | null>(null)
  const [selAllow, setSelAllow] = useState<string | null>(null)
  const [dialog, setDialog] = useState<'block' | 'allow' | null>(null)
  // [feature:savemode]
  // Kennung savemode: offene Änderungen der Einstellungen bleiben stehen, wenn eine Aktion (Sperren, Freigeben …) neu lädt
  const formDirty = useRef(false)
  const load = () => api.get<State>('/api/admin/security').then(d => { setData(d); if (!formDirty.current) setForm(d.settings ?? null) }).catch(fail)
  // [feature:savemode:else]
  // const load = () => api.get<State>('/api/admin/security').then(d => { setData(d); setForm(d.settings ?? null) }).catch(fail)
  // [/feature:savemode]
  const loadEvents = (more = false) => {
    const before = more && events?.length ? events[events.length - 1].id : undefined
    api.get<Ev[]>('/api/admin/security/events?' + q({ q: query.trim() || undefined, limit: 500, before }))
      .then(r => setEvents(e => more && e ? [...e, ...r] : r)).catch(fail)
  }
  useEffect(() => { load(); loadEvents() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { const h = setTimeout(() => loadEvents(), 300); return () => clearTimeout(h) }, [query]) // eslint-disable-line react-hooks/exhaustive-deps
  const run = async (f: () => Promise<unknown>, done?: string) => { try { await f(); if (done) notify(done); load(); loadEvents() } catch (e) { fail(e) } }

  const unblock = async (key: string) => {
    if (await askConfirm('', t('Sperre für {key} aufheben?', { key }))) run(() => api.del('/api/admin/security/block?' + q({ key })), t('Freigegeben'))
  }
  const disallow = (cidr: string) => run(() => api.del('/api/admin/security/allow?' + q({ cidr })))
  const doBlock = () => run(async () => {
    await api.post('/api/admin/security/block', { address: block.address.trim(), days: Number(block.days) || null, note: block.note.trim() || null })
    setBlock({ address: '', days: '0', note: '' }); setDialog(null)
  }, t('Gesperrt'))
  const doAllow = () => run(async () => {
    await api.post('/api/admin/security/allow', { cidr: allow.cidr.trim(), note: allow.note.trim() || null })
    setAllow({ cidr: '', note: '' }); setDialog(null)
  }, t('Erlaubt'))
  const allowMine = () => data?.me && run(() => api.post('/api/admin/security/allow', { cidr: data.me!.address, note: t('Eigene Adresse') }), t('Erlaubt'))
  const exportEvents = () => saveUrl('/api/admin/security/events.csv?' + q({ q: query.trim() || undefined }))

  // [feature:savemode]
  // Speichern im Menüband; Lockern (Abtasten nicht mehr sperren, AbuseIPDB nicht mehr prüfen) fragt vorher nach
  const dirtyForm = !!form && !!data?.settings && JSON.stringify(form) !== JSON.stringify(data.settings)
  formDirty.current = dirtyForm
  const drafting = useAdminSave({
    dirty: dirtyForm || !!abuseKey.trim(),
    save: async () => {
      if (!data || !form) return true
      const old = data.settings
      const loosen = [old.traps && !form.traps && t('Abtasten (unbekannter Pfad ohne Anmeldung, Scanner) wird nicht mehr sofort gesperrt.'),
        old.abuseCheck && !form.abuseCheck && t('Unbekannte Adressen werden nicht mehr bei AbuseIPDB geprüft.')].filter(Boolean)
      if (loosen.length && !(await askConfirm(t('Schutz lockern?'), loosen.join(' '), t('Speichern')))) return false
      try {
        if (dirtyForm) await api.put('/api/admin/security/settings', form)
        if (abuseKey.trim()) { await api.put('/api/admin/security/abuseipdb/key', { key: abuseKey.trim() }); setAbuseKey('') }
        formDirty.current = false
        notify(t('Gespeichert')); load(); return true
      } catch (e) { fail(e); return false }
    },
    discard: () => { formDirty.current = false; setForm(data?.settings ?? null); setAbuseKey('') },
  })
  // [feature:savemode:else]
  // const drafting = false
  // [/feature:savemode]
  if (!data) return null
  if (data.off) return <>
    {!tabbed && <AdminHeading first>{t('Security Center')}</AdminHeading>}
    <MessageBar intent="warning"><MessageBarBody>{t('Abgeschaltet (DMOFFICE_SECURITY=off): Es wird nichts erkannt und nichts gesperrt.')}</MessageBarBody></MessageBar>
    {featureOn('proxyconfirm') && <SecurityProxies ribbon={ribbon} />} {/* [feature:proxyconfirm] */}
  </>

  const allowRows: AllowRow[] = [...data.builtIn.map(c => ({ cidr: c, note: null, added: null, fixed: true })), ...data.allow.map(a => ({ ...a, fixed: false }))]
  const pickedBlock = data.blocks.some(b => b.key === selBlock) ? selBlock : null
  const pickedAllow = allowRows.some(a => a.cidr === selAllow && !a.fixed) ? selAllow : null
  const blockFields = <>
    <Field label={ribbon ? t('Adresse') : t('Adresse von Hand sperren')}><Input value={block.address} onChange={(_, d) => setBlock({ ...block, address: d.value })} placeholder="203.0.113.7" /></Field>
    <Field label={t('Dauer')}>
      <Select value={block.days} onChange={(_, d) => setBlock({ ...block, days: d.value })}>
        {[1, 7, 30, 90, 365].map(n => <option key={n} value={n}>{n === 1 ? t('1 Tag') : t('{n} Tage', { n })}</option>)}
        <option value="0">{t('dauerhaft')}</option>
      </Select>
    </Field>
    <Field label={t('Hinweis')}><Input value={block.note} onChange={(_, d) => setBlock({ ...block, note: d.value })} /></Field>
  </>
  const allowFields = <>
    <Field label={t('Adresse oder Netz')}><Input value={allow.cidr} onChange={(_, d) => setAllow({ ...allow, cidr: d.value })} placeholder="203.0.113.0/24" /></Field>
    <Field label={t('Hinweis')}><Input value={allow.note} onChange={(_, d) => setAllow({ ...allow, note: d.value })} /></Field>
  </>
  const blockCols: AdminCol<Block>[] = [
    ...(ribbon ? [pickCol<Block, string>({ id: b => b.key, selected: selBlock, onChange: setSelBlock })] : []),
    { key: 'key', label: () => t('Adresse'), width: 170, sort: b => b.key, render: b => b.key },
    { key: 'reason', label: () => t('Grund'), width: 0, sort: b => b.reason, render: b => <>{noteText(b.reason)}{b.note ? <span className={s.muted}> · {b.note}</span> : null}{b.by ? <span className={s.muted}> · {t('von {who}', { who: b.by })}</span> : null}</> },
    { key: 'since', label: () => t('Seit'), width: 130, sort: b => ms(b.since), render: b => when(b.since) },
    { key: 'until', label: () => t('Bis'), width: 130, sort: b => ms(b.until), render: b => b.until ? when(b.until) : t('dauerhaft') },
    { key: 'level', label: () => t('Stufe'), width: 70, sort: b => b.level, render: b => b.level },
    { key: 'hits', label: () => t('Abgewiesen'), width: 100, sort: b => b.hits, render: b => b.hits },
    { key: 'last', label: () => t('Zuletzt'), width: 130, sort: b => ms(b.last), render: b => when(b.last) },
    ...(ribbon ? [] : [{ key: 'act', label: () => '', width: 110, fixed: true, render: (b: Block) => <Button size="small" onClick={() => unblock(b.key)}>{t('Freigeben')}</Button> }]),
  ]
  const allowCols: AdminCol<AllowRow>[] = [
    ...(ribbon ? [pickCol<AllowRow, string>({ id: a => a.cidr, selected: selAllow, onChange: c => setSelAllow(c != null && allowRows.find(a => a.cidr === c)?.fixed ? null : c) })] : []),
    { key: 'cidr', label: () => t('Netz'), width: 200, sort: a => a.cidr, render: a => a.cidr },
    { key: 'note', label: () => t('Hinweis'), width: 0, sort: a => a.note ?? '', render: a => a.fixed ? <span className={s.muted}>{t('fest eingebaut (eigener Rechner, private Netze, Tailscale)')}</span> : a.note },
    { key: 'added', label: () => t('Eingetragen'), width: 130, sort: a => ms(a.added), render: a => a.added ? when(a.added) : '' },
    ...(ribbon ? [] : [{ key: 'act', label: () => '', width: 110, fixed: true, render: (a: AllowRow) => a.fixed ? null : <Button size="small" onClick={() => disallow(a.cidr)}>{t('Entfernen')}</Button> }]),
  ]
  const eventCols: AdminCol<Ev>[] = [
    { key: 'at', label: () => t('Zeit'), width: 140, sort: e => e.id, render: e => new Date(e.at).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'medium' }) },
    { key: 'key', label: () => t('Adresse'), width: 160, sort: e => e.key, render: e => e.key },
    { key: 'kind', label: () => t('Art'), width: 120, sort: e => e.kind, render: e => kindName(e.kind) },
    { key: 'path', label: () => t('Anfrage'), width: 0, sort: e => e.path, render: e => e.method === '-' ? '' : `${e.method} ${e.path}` },
    { key: 'detail', label: () => t('Hinweis'), width: 220, sort: e => e.detail ?? '', render: e => noteText(e.detail) },
    { key: 'agent', label: () => t('User-Agent'), width: 220, sort: e => e.agent ?? '', render: e => <span className={s.muted} title={e.agent ?? undefined} style={{ display: 'block', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{e.agent}</span> },
  ]

  return (
    <div className={s.page}>
      {ribbon && <RibbonActions>
        {shows('blocks') && <>
        <RibbonButton icon={<LockClosed20Regular />} label={t('Adresse sperren …')} onClick={() => setDialog('block')} />
        <RibbonButton icon={<LockOpen20Regular />} label={t('Sperre aufheben')} disabled={!pickedBlock} onClick={() => pickedBlock && unblock(pickedBlock)} />
        <RibbonDivider />
        <RibbonButton icon={<ShieldCheckmark20Regular />} label={t('Netz erlauben …')} onClick={() => setDialog('allow')} />
        <RibbonButton icon={<PersonAvailable20Regular />} label={t('Meine Adresse erlauben')} disabled={!data.me || data.me.allowed} onClick={allowMine} />
        <RibbonButton icon={<Delete20Regular />} label={t('Aus der Erlaubt-Liste entfernen')} disabled={!pickedAllow} onClick={() => pickedAllow && disallow(pickedAllow)} />
        </>}
        {!tabbed && <RibbonDivider />}
        {shows('events') && <RibbonButton icon={<ArrowDownload20Regular />} label={t('Ereignisse exportieren')} onClick={exportEvents} />}
        {/* [feature:adminclean] */}
        {tabbed && shows('events') && events && events.length % 500 === 0 && events.length > 0 && <RibbonButton icon={<ArrowClockwise20Regular />} label={t('Ältere laden')} onClick={() => loadEvents(true)} />}
        {tabbed && shows('abuse') && <>
          <RibbonButton icon={<Checkmark20Regular />} label={t('Schlüssel prüfen')} disabled={!abuseKey.trim() && !data.abuseKey} onClick={() => run(() => api.post('/api/admin/security/abuseipdb/verify', { key: abuseKey.trim() || null }), t('Der Schlüssel funktioniert.'))} />
          {data.abuseKey && <RibbonButton icon={<Delete20Regular />} label={t('Schlüssel entfernen')} onClick={() => run(() => api.put('/api/admin/security/abuseipdb/key', { key: null }), t('Entfernt'))} />}
        </>}
        {/* [/feature:adminclean] */}
      </RibbonActions>}
      {dialog && <Dialog open onOpenChange={(_, d) => { if (!d.open) setDialog(null) }}>
        <DialogSurface style={{ maxWidth: 480 }}><DialogBody>
          <DialogTitle>{dialog === 'block' ? t('Adresse sperren') : t('Netz erlauben')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>{dialog === 'block' ? blockFields : allowFields}</DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={() => setDialog(null)}>{t('Abbrechen')}</Button>
            {dialog === 'block'
              ? <Button appearance="primary" icon={<LockClosed20Regular />} disabled={!block.address.trim()} onClick={doBlock}>{t('Sperren')}</Button>
              : <Button appearance="primary" icon={<Checkmark20Regular />} disabled={!allow.cidr.trim()} onClick={doAllow}>{t('Erlauben')}</Button>}
          </DialogActions>
        </DialogBody></DialogSurface>
      </Dialog>}
      {shows('overview') && <>
      <AdminHeading first>{t('Security Center')}</AdminHeading>
      <Text>{t('Ohne Anmeldung gibt es nur feste Wege: die Anmeldung, das Koppeln der App, Freigabelinks und die Zugänge für Kalender, Kontakte und Dateien mit eigener Anmeldung. Wer ohne jedes Anmeldemerkmal etwas anderes abfragt, tastet ab und wird sofort gesperrt. Nie gesperrt werden die Netze der Erlaubt-Liste und Adressen, von denen sich in letzter Zeit jemand gültig angemeldet hat.')}</Text>

      {KINDS.every(([k]) => !data.week[k]) && <Text weight="semibold">{t('Alles ruhig')}</Text>}
      <div className={s.tiles}>
        {KINDS.map(([k, name, hint]) => (
          <div key={k} className={s.tile} title={hint()}>
            <Text size={200} className={s.muted}>{name()}</Text>
            <Text weight="semibold" size={400}>{data.day[k] ?? 0} <Text size={200} weight="regular" className={s.muted}>{t('24 h')}</Text></Text>
            <Text size={200}>{t('{n} in 7 Tagen', { n: data.week[k] ?? 0 })}</Text>
          </div>
        ))}
        <div className={s.tile}>
          <Text size={200} className={s.muted}>{t('Gesperrte Adressen')}</Text>
          <Text weight="semibold" size={400}>{data.blocks.length}</Text>
        </div>
      </div>
      {data.top.length > 0 && <Text size={200}>{t('Häufigste Adressen in 7 Tagen:')} {data.top.map(x => `${x.key} (${x.count})`).join(' · ')}</Text>}

      {data.me && <Text size={200} className={s.muted}>
        {t('Deine Adresse, wie der Server sie sieht: {address}', { address: data.me.address })}{data.me.allowed ? ' · ' + t('steht in der Erlaubt-Liste') : ''}
        {!ribbon && !data.me.allowed && <Button size="small" appearance="transparent" onClick={() => run(() => api.post('/api/admin/security/allow', { cidr: data.me!.address, note: t('Eigene Adresse') }), t('Erlaubt'))}>{t('Meine Adresse erlauben')}</Button>}
      </Text>}
      </>}
      {shows('blocks') && <>
      <AdminHeading>{t('Gesperrte Adressen')}</AdminHeading>
      {data.blocks.length === 0 ? <Text className={s.muted}>{t('Keine.')}</Text>
        : <AdminTable id="security.blocks" cols={blockCols} rows={data.blocks} rowKey={b => b.key} initialSort={{ col: 'last', desc: true }}
          onRowClick={ribbon ? b => setSelBlock(b.key) : undefined} active={ribbon ? pickedBlock : undefined} />}
      {!ribbon && <div className={s.row}>
        {blockFields}
        <Button disabled={!block.address.trim()} onClick={doBlock}>{t('Sperren')}</Button>
      </div>}

      <AdminHeading>{t('Erlaubt-Liste')}</AdminHeading>
      <Text size={200} className={s.muted}>{t('Diese Netze werden nie gesperrt.')}</Text>
      <AdminTable id="security.allow" cols={allowCols} rows={allowRows} rowKey={a => a.cidr}
        onRowClick={ribbon ? a => setSelAllow(a.fixed ? null : a.cidr) : undefined} active={ribbon ? pickedAllow : undefined} />
      {!ribbon && <div className={s.row}>
        {allowFields}
        <Button disabled={!allow.cidr.trim()} onClick={doAllow}>{t('Erlauben')}</Button>
      </div>}
      </>}
      {shows('proxies') && featureOn('proxyconfirm') && <SecurityProxies ribbon={ribbon} />} {/* [feature:proxyconfirm] */}

      {shows('events') && <>
      <AdminHeading first={tabbed}>{t('Ereignisse')}</AdminHeading>
      <Text size={200} className={s.muted}>{t('Jeder einzelne Versuch, aufbewahrt {n} Tage. Bei gesperrten Adressen stehen die ersten 20 abgewiesenen Anfragen einzeln hier, danach werden sie nur noch gezählt.', { n: data.settings.keepDays })}</Text>
      <div className={s.row}>
        <Input contentBefore={<Search20Regular />} placeholder={t('Adresse, Pfad, Art durchsuchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ minWidth: 260 }} />
        {!ribbon && <Button icon={<ArrowClockwise20Regular />} onClick={() => { load(); loadEvents() }}>{t('Aktualisieren')}</Button>}
        {!ribbon && <Button icon={<ArrowDownload20Regular />} onClick={exportEvents}>{t('Exportieren')}</Button>}
      </div>
      {events && (events.length === 0 ? <Text className={s.muted}>{t('Keine Ereignisse.')}</Text> : <>
        <AdminTable id="security.events" cols={eventCols} rows={allEvents || tabbed ? events : events.slice(0, 10)} rowKey={e => e.id} initialSort={{ col: 'at', desc: true }} max={5000} />
        {!tabbed && <div className={s.row}>
          {events.length > 10 && <Button size="small" onClick={() => setAllEvents(a => !a)}>
            {allEvents ? t('Nur die letzten 10 zeigen') : t('Alle {n} zeigen', { n: events.length })}</Button>}
          {allEvents && events.length % 500 === 0 && <Button size="small" onClick={() => loadEvents(true)}>{t('Ältere laden')}</Button>}
        </div>}
      </>)}
      </>}

      {form && <>
      {shows('settings') && <>
      <AdminHeading first={tabbed}>{t('Einstellungen')}</AdminHeading>
        <Switch checked={form.traps} onChange={(_, d) => setForm({ ...form, traps: d.checked })} label={t('Abtasten sofort sperren (unbekannter Pfad ohne Anmeldung, Scanner)')} />
        <div className={s.form}>
          <InfoLabel info={t('Wiederholt innerhalb von 90 Tagen: 30 Tage, danach dauerhaft.')}>{t('Erste Sperre (Tage)')}</InfoLabel>
          <Input type="number" value={String(form.firstDays)} onChange={(_, d) => setForm({ ...form, firstDays: Number(d.value) })} style={{ width: 120 }} />
          <Text>{t('Gültige Anmeldung schützt eine Adresse (Tage)')}</Text>
          <Input type="number" value={String(form.seenDays)} onChange={(_, d) => setForm({ ...form, seenDays: Number(d.value) })} style={{ width: 120 }} />
          <Text>{t('Ereignisse aufbewahren (Tage)')}</Text>
          <Input type="number" value={String(form.keepDays)} onChange={(_, d) => setForm({ ...form, keepDays: Number(d.value) })} style={{ width: 120 }} />
        </div>
      </>}

      {shows('abuse') && <>
        <AdminHeading first={tabbed}>{t('AbuseIPDB')}</AdminHeading>
        <Switch checked={form.abuseCheck} onChange={(_, d) => setForm({ ...form, abuseCheck: d.checked })}
          label={<InfoLabel info={t('Fragt die Adresse jedes nicht angemeldeten Besuchers beim ersten Kontakt bei AbuseIPDB nach, auch von Empfängern eines Freigabelinks (das Ergebnis gilt einen Tag), und sperrt sie ab der Schwelle sofort. Nicht gefragt wird für die Erlaubt-Liste, Angemeldete und Adressen mit gültiger Anmeldung. Die Adresse geht dabei an AbuseIPDB (Marathon Studios, Inc., USA).')}>{t('Unbekannte Adressen bei AbuseIPDB prüfen')}</InfoLabel>} />
        <Switch checked={form.abuseReport} onChange={(_, d) => setForm({ ...form, abuseReport: d.checked })}
          label={<InfoLabel info={t('Meldet Adressen, die in eine Falle gelaufen sind, an AbuseIPDB: Adresse, Kategorie und Anfrage, auf Englisch mit dem Namen dmOffice (dmoffice.de).')}>{t('Angriffe an AbuseIPDB melden')}</InfoLabel>} />
        <div className={s.form}>
          <InfoLabel info={t('Der Schlüssel wird verschlüsselt gespeichert und nicht wieder angezeigt.')}>{t('API-Schlüssel')}</InfoLabel>
          <div className={s.row} style={{ alignItems: 'center', flexWrap: 'nowrap' }}>
            <Input type="password" value={abuseKey} onChange={(_, d) => setAbuseKey(d.value)} placeholder={data.abuseKey ? t('gespeichert') : ''} style={{ flex: 1, minWidth: 160 }} />
            {!drafting && <Button disabled={!abuseKey.trim()} onClick={() => run(async () => { await api.put('/api/admin/security/abuseipdb/key', { key: abuseKey.trim() }); setAbuseKey('') }, t('Gespeichert'))}>{t('Speichern')}</Button>}
            {!tabbed && <Button disabled={!abuseKey.trim() && !data.abuseKey} onClick={() => run(() => api.post('/api/admin/security/abuseipdb/verify', { key: abuseKey.trim() || null }), t('Der Schlüssel funktioniert.'))}>{t('Prüfen')}</Button>}
            {!tabbed && data.abuseKey && <Button appearance="subtle" onClick={() => run(() => api.put('/api/admin/security/abuseipdb/key', { key: null }), t('Entfernt'))}>{t('Entfernen')}</Button>}
          </div>
          <InfoLabel info={t('Ab diesem Wert (0–100: wie sicher AbuseIPDB ist, dass von der Adresse Angriffe kommen) wird gesperrt. Empfohlen: 90.')}>{t('Sperren ab (%)')}</InfoLabel>
          <Input type="number" value={String(form.abuseScore)} onChange={(_, d) => setForm({ ...form, abuseScore: Number(d.value) })} style={{ width: 120 }} />
          <Text>{t('Sperrdauer (Tage)')}</Text>
          <Input type="number" value={String(form.abuseDays)} onChange={(_, d) => setForm({ ...form, abuseDays: Number(d.value) })} style={{ width: 120 }} />
        </div>
      </>}

        {shows('settings') && !drafting && <div><Button appearance="primary" onClick={() => run(() => api.put('/api/admin/security/settings', form), t('Gespeichert'))}>{t('Einstellungen speichern')}</Button></div>}
        {shows('settings') && <Text size={200} className={s.muted}>{t('Notbremse, falls eine Sperre die eigenen Zugänge trifft: im Container DMOFFICE_SECURITY=off setzen und neu starten.')}</Text>}
      </>}
    </div>
  )
}
