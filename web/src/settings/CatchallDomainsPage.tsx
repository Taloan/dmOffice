import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Menu, MenuDivider, MenuItem, MenuList,
  MenuPopover, MenuTrigger, MessageBar, MessageBarBody, Option, Radio, RadioGroup, SearchBox, Spinner, Switch, Tab, TabList, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, Copy20Regular, Delete20Regular, Edit20Regular, Globe20Regular, HourglassHalf20Regular, LockClosed20Regular,
  LockOpen20Regular, PersonAdd20Regular,
} from '@fluentui/react-icons'
import { useEffect, useMemo, useState } from 'react'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { copyText } from '../shell/browser'
import { fmtDate } from '../shell/hooks'
import { ContextMenu, askConfirm, askText, useContextMenu } from '../shell/context'
import { ExplorerTable, selectCol } from '../sdk'
import { type AliasConfig, type AliasSite, type AliasState, NewAliasDialog, siteList } from '../mail/AliasDialog'
import { CATCHALL_CHANGED, adoptAlias, catchallChanged } from '../mail/CatchallFolders'
import { Hint, Section } from './parts'

const useStyles = makeStyles({
  md: { display: 'flex', gap: '16px', alignItems: 'flex-start', flexWrap: 'wrap' },
  left: { flex: '0 1 240px', minWidth: '200px', display: 'flex', flexDirection: 'column', gap: '8px' },
  right: { flex: '1 1 420px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: '8px' },
  list: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, overflow: 'hidden' },
  li: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '8px 10px', cursor: 'pointer', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`,
    ':last-child': { borderBottom: 'none' }, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  sel: { backgroundColor: tokens.colorNeutralBackground1Selected, boxShadow: `inset 3px 0 0 ${tokens.colorBrandStroke1}` },
  liText: { display: 'flex', flexDirection: 'column', minWidth: 0, flex: 1 },
  muted: { color: tokens.colorNeutralForeground3 },
  head: { display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px', flexWrap: 'wrap' },
  bar: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  detail: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, padding: '8px 12px', display: 'flex', flexDirection: 'column', gap: '2px' },
  hits: { display: 'flex', flexDirection: 'column', gap: '2px' },
})

/** Ein Alias in der Tabelle: angelegt (mit Notiz, Sperre, Entfernen nach Zeit) oder nicht angelegt (hat nur Post bekommen). */
interface Row {
  address: string; state: 'open' | 'blocked' | 'unknown'; note: string; count: number; last: string | null
  blocked: 'junk' | 'delete' | null; expireKind?: 'trash' | 'destroy' | null; expireDays?: number | null
}
type Filter = 'all' | 'open' | 'unknown' | 'blocked'
interface Sender { address: string; name: string | null; count: number }

const local = (a: string) => a.slice(0, a.lastIndexOf('@'))
const domainOf = (a: string) => a.slice(a.lastIndexOf('@') + 1)

/**
 * E-Mail → Catchall-Aliase je Domain (Kennung catchalldomains, Konzept docs/catchall-domains-konzept.md): links die Domains, rechts
 * die gewählte mit „Einstellungen“ (Postfach, annehmen, Posteingang, Antworten von) und „Aliase“ (angelegte und nicht angelegte in
 * einer Tabelle mit Filter, Suche und Mehrfachauswahl). Server: Mail/CatchallDomains.cs, Mail/CatchallAliasesApi.cs.
 */
export function CatchallDomainsPage() {
  const { notify, fail } = useApp()
  const s = useStyles()
  const [st, setSt] = useState<AliasState | null>(null)
  const [domain, setDomain] = useState<string | null>(null)
  const [tab, setTab] = useState<'settings' | 'aliases'>('settings')
  const [filter, setFilter] = useState<Filter>('all')
  const [query, setQuery] = useState('')
  const [find, setFind] = useState('')
  const [selected, setSelected] = useState<string[]>([])
  const [active, setActive] = useState<string | null>(null)
  const [senders, setSenders] = useState<Sender[] | null>(null)
  const [adding, setAdding] = useState(false)
  const [newDomain, setNewDomain] = useState<{ domain: string; account: string } | null>(null)
  const [timing, setTiming] = useState<{ addresses: string[]; kind: '' | 'trash' | 'destroy'; days: string } | null>(null)
  const menu = useContextMenu<Row>()

  const load = () => api.get<AliasState>('/api/mail/aliases').then(setSt).catch(fail)
  useEffect(() => {
    load()
    const reload = () => { load() }
    window.addEventListener(CATCHALL_CHANGED, reload)
    return () => window.removeEventListener(CATCHALL_CHANGED, reload)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const sites = st ? siteList(st) : []
  const site = sites.find(d => d.domain === domain) ?? sites[0] ?? null
  useEffect(() => { setSelected([]); setActive(null) }, [site?.domain])

  // Eine Liste statt zwei: angelegte und nicht angelegte Aliase der Domain
  const rows = useMemo<Row[]>(() => {
    if (!st || !site) return []
    const mine = (a: string) => domainOf(a) === site.domain
    return [
      ...st.items.filter(i => mine(i.address)).map(i => ({
        address: i.address, state: i.blocked ? 'blocked' as const : 'open' as const, note: i.note, count: i.count, last: i.last, blocked: i.blocked,
        expireKind: i.expireKind, expireDays: i.expireDays,
      })),
      ...st.unknown.filter(u => mine(u.address)).map(u => ({ address: u.address, state: 'unknown' as const, note: '', count: u.count, last: u.last, blocked: null })),
    ]
  }, [st, site])
  const shown = rows.filter(r => (filter === 'all' || r.state === filter)
    && (!query.trim() || r.address.includes(query.trim().toLowerCase()) || r.note.toLowerCase().includes(query.trim().toLowerCase())))
  const picked = rows.filter(r => selected.includes(r.address))

  // Häufigste Absender des gewählten Alias
  useEffect(() => {
    setSenders(null)
    const r = rows.find(x => x.address === active)
    if (!r || r.state === 'unknown') return
    api.get<{ senders: Sender[] }>('/api/mail/aliases/item?address=' + encodeURIComponent(r.address)).then(x => setSenders(x.senders)).catch(() => setSenders([]))
  }, [active]) // eslint-disable-line react-hooks/exhaustive-deps

  if (!st) return null
  const accounts = st.accounts
  const accountText = (id: string | null) => { const a = accounts.find(x => x.id === id); return a ? a.email || a.name : '' }

  const saveSites = async (next: AliasSite[]) => {
    setSt({ ...st, sites: next })
    try {
      setSt(await api.put<AliasState>('/api/mail/aliases/sites', next.map(d => ({ domain: d.domain, account: d.account, accept: d.accept, inbox: d.inbox, replyFrom: d.replyFrom }))))
      catchallChanged()
      return true
    } catch (e) { fail(e); load(); return false }
  }
  const change = (c: Partial<AliasSite>) => site && saveSites(sites.map(d => d.domain === site.domain ? { ...d, ...c } : d))
  const saveConfig = async (c: Partial<AliasConfig>) => {
    try { setSt(await api.put<AliasState>('/api/mail/aliases/config', { ...st.config, ...c })); catchallChanged() } catch (e) { fail(e) }
  }

  const addDomain = async () => {
    if (!newDomain) return
    const d = newDomain.domain.trim().replace(/^@/, '').toLowerCase()
    if (await saveSites([...sites, { domain: d, account: newDomain.account, accept: 'all', inbox: false, replyFrom: null, folder: null }])) {
      setDomain(d); setTab('settings'); setNewDomain(null)
    }
  }
  const removeDomain = async () => {
    if (!site) return
    if (!await askConfirm(t('{d} entfernen?', { d: site.domain }),
      t('Mails an {d} werden nicht mehr als Catchall-Aliase eingeordnet. Ordner und Mails bleiben im Postfach, die angelegten Aliase in der Liste.', { d: site.domain }),
      t('Entfernen'))) return
    if (await saveSites(sites.filter(d => d.domain !== site.domain))) setDomain(null)
  }

  // Aktionen auf die gewählten Aliase (Mehrfachauswahl) oder einen (Kontextmenü)
  const each = async (list: Row[], fn: (r: Row) => Promise<unknown>) => {
    try { for (const r of list) await fn(r) } catch (e) { fail(e) }
    catchallChanged(); setSelected([])
  }
  const adopt = (list: Row[]) => each(list.filter(r => r.state === 'unknown'), r => adoptAlias(r.address))
  const block = (list: Row[], as: 'junk' | 'delete' | null) => each(list.filter(r => r.state !== 'unknown'),
    r => api.put('/api/mail/aliases/item', as ? { address: r.address, block: true, blockAs: as } : { address: r.address, block: false }))
  const remove = async (list: Row[]) => {
    const known = list.filter(r => r.state !== 'unknown')
    if (!known.length) return
    if (!await askConfirm(known.length === 1 ? t('{a} löschen?', { a: known[0].address }) : t('{n} Aliase löschen?', { n: known.length }),
      t('Die Mails in den Ordnern der Aliase kommen in den Ordner der Domain, die Ordner der Aliase werden gelöscht. Neue Mails an sie gelten wieder als nicht angelegt.'),
      t('Löschen'))) return
    each(known, r => api.del('/api/mail/aliases/item?address=' + encodeURIComponent(r.address)))
  }
  const note = async (r: Row) => {
    const n = await askText(t('Notiz'), r.note, t('Notiz zu {a}:', { a: r.address }))
    if (n != null) each([r], x => api.put('/api/mail/aliases/item', { address: x.address, note: n }))
  }
  const copy = async (a: string) => { if (await copyText(a)) notify(t('{a} kopiert', { a })) }
  const saveTiming = async () => {
    if (!timing) return
    const days = Number(timing.days)
    await each(rows.filter(r => timing.addresses.includes(r.address)),
      r => api.put('/api/mail/aliases/item', { address: r.address, expireKind: timing.kind, expireDays: timing.kind ? days : null }))
    setTiming(null)
  }
  const anyKnown = picked.some(r => r.state !== 'unknown'), anyUnknown = picked.some(r => r.state === 'unknown')
  const stateText = (r: Row) => r.state === 'unknown' ? t('nicht angelegt')
    : r.blocked ? (r.blocked === 'delete' ? t('gesperrt – löschen') : t('gesperrt – Junk'))
      : r.expireKind ? (r.expireKind === 'destroy' ? t('angelegt · vernichten nach {n} T.', { n: r.expireDays ?? 0 }) : t('angelegt · Papierkorb nach {n} T.', { n: r.expireDays ?? 0 }))
        : t('angelegt')

  // Suche über alle Domains
  const all = [...st.items.map(i => i.address), ...st.unknown.map(u => u.address)]
  const hits = find.trim() ? all.filter(a => a.includes(find.trim().toLowerCase())).slice(0, 20) : []
  const counts = (d: string) => all.filter(a => domainOf(a) === d).length

  const activeRow = rows.find(r => r.address === active)
  return (
    <>
      <Hint>{t('Voraussetzung: Beim Hoster der Domain werden alle Adressen (Catch-all) an ein Postfach weitergeleitet, das dmOffice abruft. Jede Domain gehört zu genau einem Postfach; was für eine Domain gilt, gilt für alle ihre Aliase. Abweichungen für einzelne Aliase über Mailregeln.')}</Hint>
      {!accounts.length && <Hint>{t('Noch kein E-Mail-Konto eingerichtet.')}</Hint>}
      <div className={s.md}>
        <div className={s.left}>
          <RibbonActions><RibbonButton primary icon={<Add20Regular />} label={t('Neue Domain')} disabled={!accounts.length} onClick={() => setNewDomain({ domain: '', account: accounts[0]?.id ?? '' })} /></RibbonActions>
          {sites.length > 0 && <div className={s.list} role="listbox" aria-label={t('Domains')}>
            {sites.map(d => (
              <div key={d.domain} role="option" aria-selected={d.domain === site?.domain} className={mergeClasses(s.li, d.domain === site?.domain && s.sel)}
                onClick={() => { setDomain(d.domain); setFind('') }}>
                <Globe20Regular />
                <span className={s.liText}>
                  <Text weight={d.domain === site?.domain ? 'semibold' : 'regular'} truncate>{d.domain}</Text>
                  <Text size={200} className={s.muted} truncate>{accountText(d.account)} · {counts(d.domain)}</Text>
                </span>
              </div>
            ))}
          </div>}
          {sites.length > 0 && <SearchBox placeholder={t('Alias suchen (alle Domains) …')} value={find} onChange={(_, d) => setFind(d.value)} />}
          {hits.length > 0 && <div className={s.list}>
            {hits.map(a => <div key={a} className={s.li} onClick={() => { setDomain(domainOf(a)); setTab('aliases'); setFilter('all'); setQuery(local(a)); setActive(a); setFind('') }}>
              <Text size={200} truncate>{a}</Text></div>)}
          </div>}
          {find.trim() && !hits.length && <Hint>{t('Kein Alias gefunden.')}</Hint>}
        </div>

        <div className={s.right}>
          {!site ? <Hint>{t('Noch keine Domain. Mit „+ Domain“ eintragen.')}</Hint> : <>
            <div className={s.head}>
              <Text size={500} weight="semibold">{site.domain}</Text>
              <Button appearance="subtle" icon={<Delete20Regular />} onClick={removeDomain}>{t('Domain entfernen …')}</Button>
            </div>
            <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as 'settings' | 'aliases')}>
              <Tab value="settings">{t('Einstellungen')}</Tab>
              <Tab value="aliases">{t('Aliase ({n})', { n: rows.length })}</Tab>
            </TabList>

            {tab === 'settings' && <>
              <Section title={t('Postfach')}>
                <Field label={t('Postfach, das die Mails abholt')}>
                  <Dropdown value={accountText(site.account)} placeholder={t('Postfach wählen')} selectedOptions={site.account ? [site.account] : []}
                    onOptionSelect={(_, d) => d.optionValue && change({ account: d.optionValue })}>
                    {accounts.map(a => <Option key={a.id} value={a.id}>{a.email || a.name}</Option>)}
                  </Dropdown>
                </Field>
              </Section>
              <Section title={t('Welche Aliase annehmen?')}>
                <RadioGroup value={site.accept} onChange={(_, d) => change({ accept: d.value as AliasSite['accept'] })}>
                  <Radio value="all" label={t('Alle Aliase der Domain')} />
                  <Radio value="known" label={t('Nur angelegte Aliase – alle anderen gleich in Junk')} />
                </RadioGroup>
              </Section>
              <Section title={t('Anzeige')} hint={t('Die Mails bleiben im Posteingang; unter „Catchall-Aliase“ im Ordnerbereich stehen sie zusätzlich – jede Mail gibt es nur einmal. Gilt nur für Mails, die danach eintreffen; vorhandene bleiben, wo sie sind.')}>
                <Switch checked={site.inbox} label={t('Auch im Posteingang anzeigen')} onChange={(_, d) => change({ inbox: d.checked })} />
              </Section>
              <Section title={t('Antworten von')} hint={t('„Von“ lässt sich beim Schreiben jederzeit von Hand ändern. Lehnt der Mailserver den Alias als Absender ab, bietet dmOffice an, mit dem Postfach zu senden.')}>
                <Dropdown value={site.replyFrom ? accountText(site.replyFrom) : t('dem Alias, an den die Mail ging')}
                  selectedOptions={[site.replyFrom ?? '']} onOptionSelect={(_, d) => change({ replyFrom: d.optionValue || null })}>
                  <Option value="" text={t('dem Alias, an den die Mail ging')}>{t('dem Alias, an den die Mail ging')}</Option>
                  {accounts.map(a => <Option key={a.id} value={a.id} text={a.email || a.name}>{a.email || a.name}</Option>)}
                </Dropdown>
                {!site.replyFrom && <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>{t('Der SMTP-Server des Postfachs muss diesen Absender erlauben, und SPF und DKIM der Domain müssen für diesen Server eingerichtet sein.')}</MessageBarBody>
                </MessageBar>}
              </Section>
              <Section title={t('Sperren')} last hint={t('Gilt für alle Domains. Neue Mails an einen gesperrten Alias werden beim Eingang sofort an den gewählten Ort verschoben, egal welche Anzeige eingestellt ist. Mails, die schon da sind, bleiben, wo sie sind.')}>
                <RadioGroup value={st.config.blockAs} onChange={(_, d) => saveConfig({ blockAs: d.value as AliasConfig['blockAs'] })}>
                  <Radio value="junk" label={t('In Junk verschieben')} />
                  <Radio value="delete" label={t('Löschen (in den Papierkorb)')} />
                </RadioGroup>
              </Section>
            </>}

            {tab === 'aliases' && <>
              <RibbonActions>
                <RibbonButton primary icon={<Add20Regular />} label={t('Neuer Alias')} onClick={() => setAdding(true)} />
                <RibbonButton icon={<PersonAdd20Regular />} label={t('Anlegen')} disabled={!anyUnknown} onClick={() => adopt(picked)} />
                <Menu>
                  <MenuTrigger disableButtonEnhancement>
                    <RibbonButton icon={<LockClosed20Regular />} label={t('Sperren')} disabled={!anyKnown} />
                  </MenuTrigger>
                  <MenuPopover><MenuList>
                    <MenuItem icon={<LockClosed20Regular />} onClick={() => block(picked, 'junk')}>{t('Sperren – in Junk')}</MenuItem>
                    <MenuItem icon={<LockClosed20Regular />} onClick={() => block(picked, 'delete')}>{t('Sperren – löschen')}</MenuItem>
                    <MenuDivider />
                    <MenuItem icon={<LockOpen20Regular />} onClick={() => block(picked, null)}>{t('Entsperren')}</MenuItem>
                  </MenuList></MenuPopover>
                </Menu>
                <RibbonButton icon={<HourglassHalf20Regular />} label={t('Entfernen nach Zeit …')} disabled={!anyKnown}
                  onClick={() => { const k = picked.filter(r => r.state !== 'unknown'); setTiming({ addresses: k.map(r => r.address), kind: k[0]?.expireKind ?? 'trash', days: String(k[0]?.expireDays ?? 30) }) }} />
                <RibbonButton icon={<Delete20Regular />} label={t('Löschen …')} disabled={!anyKnown} onClick={() => remove(picked)} />
              </RibbonActions>
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
                <Dropdown size="small" value={{ all: t('Alle'), open: t('Angelegt'), unknown: t('Nicht angelegt'), blocked: t('Gesperrt') }[filter]} selectedOptions={[filter]}
                  onOptionSelect={(_, d) => { setFilter(d.optionValue as Filter); setSelected([]) }} style={{ minWidth: 140 }} aria-label={t('Filter')}>
                  <Option value="all">{t('Alle')}</Option>
                  <Option value="open">{t('Angelegt')}</Option>
                  <Option value="unknown">{t('Nicht angelegt')}</Option>
                  <Option value="blocked">{t('Gesperrt')}</Option>
                </Dropdown>
                <SearchBox size="small" placeholder={t('Suchen …')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ minWidth: 160 }} />
              </div>
              {menu.ctx && (() => {
                const r = menu.ctx.data
                return (
                  <ContextMenu at={menu.ctx.at} onClose={menu.close}>
                    {r.state === 'unknown'
                      ? <MenuItem icon={<PersonAdd20Regular />} onClick={() => adopt([r])}>{t('Als Alias anlegen')}</MenuItem>
                      : <>
                        <MenuItem icon={<Edit20Regular />} onClick={() => note(r)}>{t('Notiz ändern …')}</MenuItem>
                        {r.blocked
                          ? <MenuItem icon={<LockOpen20Regular />} onClick={() => block([r], null)}>{t('Entsperren')}</MenuItem>
                          : <>
                            <MenuItem icon={<LockClosed20Regular />} onClick={() => block([r], 'junk')}>{t('Sperren – in Junk')}</MenuItem>
                            <MenuItem icon={<LockClosed20Regular />} onClick={() => block([r], 'delete')}>{t('Sperren – löschen')}</MenuItem>
                          </>}
                        <MenuItem icon={<HourglassHalf20Regular />} onClick={() => setTiming({ addresses: [r.address], kind: r.expireKind ?? 'trash', days: String(r.expireDays ?? 30) })}>{t('Entfernen nach Zeit …')}</MenuItem>
                      </>}
                    <MenuItem icon={<Copy20Regular />} onClick={() => copy(r.address)}>{t('Kopieren')}</MenuItem>
                    {r.state !== 'unknown' && <><MenuDivider /><MenuItem icon={<Delete20Regular />} onClick={() => remove([r])}>{t('Löschen …')}</MenuItem></>}
                  </ContextMenu>
                )
              })()}
              {shown.length === 0 ? <Hint>{rows.length ? t('Keine Aliase für diesen Filter.') : t('Noch keine Aliase. Sobald Post an eine Adresse der Domain kommt, steht sie hier.')}</Hint>
                : <ExplorerTable<Row> id="catchall.aliases" rows={shown} rowKey={r => r.address} initialSort={{ col: 'last', desc: true }}
                  onRowClick={r => setActive(r.address === active ? null : r.address)} active={active}
                  onRowContextMenu={(e, r) => menu.open(e, r)}
                  cols={[
                    selectCol<Row, string>({ rows: shown, id: r => r.address, selected, onChange: setSelected }),
                    { key: 'alias', label: () => t('Alias'), width: 220, sort: r => r.address, render: r => <Text weight={r.state === 'unknown' ? 'regular' : 'semibold'}>{local(r.address)}@</Text> },
                    { key: 'note', label: () => t('Notiz'), width: 0, sort: r => r.note.toLowerCase(), render: r => r.note || <span className={s.muted}>–</span> },
                    { key: 'count', label: () => t('Mails'), width: 80, sort: r => r.count, render: r => r.count.toLocaleString() },
                    { key: 'last', label: () => t('Letzte'), width: 120, sort: r => r.last ?? '', render: r => r.last ? fmtDate(r.last) : '–' },
                    { key: 'state', label: () => t('Zustand'), width: 200, sort: r => stateText(r), render: r => r.blocked
                      ? <Badge color="danger" appearance="tint">{stateText(r)}</Badge> : r.state === 'unknown' ? <span className={s.muted}>{stateText(r)}</span> : stateText(r) },
                  ]} />}
              {activeRow && <div className={s.detail}>
                <Text size={200} weight="semibold">{activeRow.address} · {stateText(activeRow)}</Text>
                {activeRow.state === 'unknown' ? <Hint>{t('Häufigste Absender gibt es für angelegte Aliase.')}</Hint>
                  : <div className={s.hits}>
                    <Text size={200}>{t('Häufigste Absender')}</Text>
                    {senders == null ? <Spinner size="tiny" /> : senders.length === 0 ? <Hint>{t('Noch keine Mails.')}</Hint>
                      : senders.map(x => <Text key={x.address} size={200} className={s.muted}>{x.name ? `${x.name} <${x.address}>` : x.address} · {x.count}</Text>)}
                  </div>}
              </div>}
            </>}
          </>}
        </div>
      </div>

      {adding && <NewAliasDialog preset={site?.domain} onClose={() => setAdding(false)} />}
      {newDomain && (
        <Dialog open onOpenChange={(_, d) => !d.open && setNewDomain(null)}>
          <DialogSurface style={{ maxWidth: 440 }}>
            <DialogBody>
              <DialogTitle>{t('Domain eintragen')}</DialogTitle>
              <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
                <Field label={t('Domain')}>
                  <Input autoFocus value={newDomain.domain} placeholder="example.com" onChange={(_, d) => setNewDomain({ ...newDomain, domain: d.value })}
                    onKeyDown={e => { if (e.key === 'Enter') addDomain() }} />
                </Field>
                <Field label={t('Postfach, das die Mails abholt')}>
                  <Dropdown value={accountText(newDomain.account)} selectedOptions={[newDomain.account]} onOptionSelect={(_, d) => setNewDomain({ ...newDomain, account: d.optionValue ?? '' })}>
                    {accounts.map(a => <Option key={a.id} value={a.id}>{a.email || a.name}</Option>)}
                  </Dropdown>
                </Field>
              </DialogContent>
              <DialogActions>
                <Button onClick={() => setNewDomain(null)}>{t('Abbrechen')}</Button>
                <Button appearance="primary" disabled={!newDomain.domain.trim() || !newDomain.account} onClick={addDomain}>{t('Eintragen')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
      {timing && (
        <Dialog open onOpenChange={(_, d) => !d.open && setTiming(null)}>
          <DialogSurface style={{ maxWidth: 480 }}>
            <DialogBody>
              <DialogTitle>{t('Entfernen nach Zeit')}</DialogTitle>
              <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
                <Text>{timing.addresses.length === 1 ? timing.addresses[0] : t('{n} Aliase', { n: timing.addresses.length })}</Text>
                <div className={s.bar}>
                  <Dropdown value={timing.kind === 'trash' ? t('In den Papierkorb nach …') : timing.kind === 'destroy' ? t('Vernichten nach …') : t('Aus')}
                    selectedOptions={[timing.kind]} onOptionSelect={(_, d) => setTiming({ ...timing, kind: (d.optionValue ?? '') as '' | 'trash' | 'destroy' })} style={{ minWidth: 200 }}>
                    <Option value="">{t('Aus')}</Option>
                    <Option value="trash">{t('In den Papierkorb nach …')}</Option>
                    <Option value="destroy">{t('Vernichten nach …')}</Option>
                  </Dropdown>
                  {timing.kind && <><Input type="number" value={timing.days} style={{ width: 90 }} onChange={(_, d) => setTiming({ ...timing, days: d.value })} /><Text>{t('Tagen')}</Text></>}
                </div>
                <Hint>{timing.kind === 'destroy'
                  ? t('Gilt für neue Mails an diesen Alias, ab Eingang. Vernichten löscht sie auch auf dem Mailserver, ohne Papierkorb; Kopien anderswo erreicht dmOffice nicht. Bis dahin lässt es sich an der Mail zurücknehmen.')
                  : t('Gilt für neue Mails an diesen Alias, ab Eingang. Bis dahin lässt es sich an der Mail zurücknehmen.')}</Hint>
              </DialogContent>
              <DialogActions>
                <Button onClick={() => setTiming(null)}>{t('Abbrechen')}</Button>
                <Button appearance="primary" onClick={saveTiming}>{t('Speichern')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
    </>
  )
}
