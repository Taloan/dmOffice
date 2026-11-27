import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Radio, RadioGroup, SearchBox, Select, Text,
  makeStyles, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowReset20Regular, ArrowSync20Regular, CheckmarkCircle20Regular, Delete20Regular, Prohibited20Regular,
} from '@fluentui/react-icons'
import { Fragment, useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { askConfirm } from '../shell/context'
import { RibbonActions, RibbonButton, RibbonDivider } from '../shell/Ribbon'
import { ExplorerTable, selectCol, type ExplorerCol } from '../sdk'
import { Hint, Section, SectionTabs } from '../settings/parts'
import { type GkCheck, type GkHit, type GkRule, type GkState, type GkStats, actionText, checkText, fmtNum, fmtUnix } from './model'
import { GkCountsTable } from './GkCounts'

// Einstellungen → GateKeeper (Kennung gatekeeper, 2.2, docs/gatekeeper-konzept.md): drei Seiten – Übersicht, Eigene Regeln,
// Statistik. Ein/Aus und die Auswahl der Apps gibt es nur in der Android-App; sie bleiben auf dem Handy. Wie in den übrigen Einstellungen wird sofort gespeichert; Aktionen stehen im Menüband über dem Inhalt.

const useStyles = makeStyles({
  counts: {
    display: 'grid', gridTemplateColumns: 'minmax(140px, max-content) repeat(2, minmax(90px, max-content))', gap: '6px 24px', alignItems: 'baseline',
    fontVariantNumeric: 'tabular-nums',
  },
  head: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  num: { textAlign: 'right' },
  bar: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200 },
  muted: { color: tokens.colorNeutralForeground3 },
})

/** Stand der Einstellungen laden; reload holt ihn neu. */
function useGkState() {
  const { fail } = useApp()
  const [st, setSt] = useState<GkState | null>(null)
  const reload = () => api.get<GkState>('/api/gatekeeper').then(setSt).catch(fail)
  useEffect(() => { reload() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  return { st, reload }
}

/** Menüband einer GateKeeper-Seite: Aktionen der Seite, am Ende Aktualisieren. */
function GkRibbon({ children, onReload }: { children?: React.ReactNode; onReload: () => void }) {
  return (
    <RibbonActions>
      {children}
      {children && <RibbonDivider />}
      <RibbonButton icon={<ArrowSync20Regular />} label={t('Aktualisieren')} onClick={onReload} />
    </RibbonActions>
  )
}

// ------------------------------------------------------------ Übersicht

export function GateKeeperOverview() {
  const s = useStyles()
  const { st, reload } = useGkState()
  if (!st) return null
  return (
    <>
      <SectionTabs page="gatekeeper">
      <GkRibbon onReload={reload} />
      <Section title="GateKeeper" hint={t('GateKeeper filtert Werbung und unerwünschten Verkehr aus den Handy-Apps, die du in der Android-App von dmOffice wählst. Ihr Verkehr läuft über deinen dmOffice-Server; alle übrigen Apps gehen wie gewohnt direkt ins Netz.')}>
        <Text>{t('Eigene Regeln: {r}', { r: st.rules.length })}</Text>
      </Section>
      <Section title={t('Zähler')}>
        <GkCountsTable today={st.today} month={st.month} />
      </Section>
      {!!st.top?.length && <Section title={t('Am häufigsten gesperrt')}>
        <div className={s.counts}>
          {st.top.map(h => <Fragment key={h.domain}>
            <Text className={s.mono}>{h.domain}</Text>
            <Text className={s.num}>{fmtNum(h.hits)}</Text>
            <Text className={s.muted}>{fmtUnix(h.last)}</Text>
          </Fragment>)}
        </div>
      </Section>}
      <Section title={t('Filterlisten')}>
        <Text>{st.filter.lists > 0
          ? t('{n} Filterlisten mit zusammen {m} Einträgen', { n: st.filter.lists, m: fmtNum(st.filter.entries) })
          : t('Filterlisten kann ein Admin in der Verwaltung unter GateKeeper eintragen. Bis dahin wendet GateKeeper nur deine eigenen Regeln an.')}</Text>
      </Section>
      <Section title={t('Auf dem Handy')} last>
        <Text>{t('Die Verbindung zum Server stellt die Android-App von dmOffice her. Du kannst sie unter Allgemein → Apps installieren von deinem Server herunterladen. In der App kannst du GateKeeper aktivieren und die Apps auswählen, die gefiltert werden sollen. Die Auswahl wird ausschließlich auf dem Handy gespeichert.')}</Text>
      </Section>
      </SectionTabs>
    </>
  )
}

// ------------------------------------------------------------ Eigene Regeln

/** Neue eigene Regel: Adresse und Aktion. */
function RuleDialog({ initial, onClose }: { initial: { domain: string; action: GkRule['action'] }; onClose: (saved: boolean) => void }) {
  const { fail } = useApp()
  const [domain, setDomain] = useState(initial.domain)
  const [action, setAction] = useState(initial.action)
  const [busy, setBusy] = useState(false)
  const save = async () => {
    if (!domain.trim()) return
    setBusy(true)
    try { await api.post('/api/gatekeeper/rules', { domain: domain.trim(), action }); onClose(true) } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(false) }}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>{t('Neue eigene Regel')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Adresse')} hint={t('Gilt auch für alle Adressen darunter, bei werbung.beispiel.de also auch für ads.werbung.beispiel.de.')}>
              <Input value={domain} autoFocus placeholder={t('gk|werbung.beispiel.de')} onChange={(_, d) => setDomain(d.value)} onKeyDown={e => { if (e.key === 'Enter') save() }} />
            </Field>
            <RadioGroup value={action} onChange={(_, d) => setAction(d.value as GkRule['action'])}>
              <Radio value="block" label={t('gk|Sperren')} />
              <Radio value="allow" label={t('Erlauben')} />
            </RadioGroup>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!domain.trim() || busy} onClick={save}>{t('Hinzufügen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

export function GateKeeperRules() {
  const s = useStyles()
  const { fail } = useApp()
  const [rules, setRules] = useState<GkRule[] | null>(null)
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<string[]>([])
  const [adding, setAdding] = useState(false)
  const [name, setName] = useState('')
  const [check, setCheck] = useState<GkCheck | null>(null)
  const load = () => api.get<GkRule[]>('/api/gatekeeper/rules').then(r => { setRules(r); setSelected(x => x.filter(d => r.some(y => y.domain === d))) }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!rules) return null
  const q = query.trim().toLowerCase()
  const shown = rules.filter(r => !q || r.domain.includes(q))
  const picked = rules.filter(r => selected.includes(r.domain))

  const setAction = async (list: GkRule[], action: GkRule['action']) => {
    try { for (const r of list) if (r.action !== action) await api.post('/api/gatekeeper/rules', { domain: r.domain, action }) } catch (e) { fail(e) }
    load()
  }
  const remove = async (list: GkRule[]) => {
    if (!list.length) return
    if (!await askConfirm(list.length === 1 ? t('Regel für {d} entfernen?', { d: list[0].domain }) : t('{n} eigene Regeln entfernen?', { n: list.length }),
      t('Danach gelten für diese Adressen wieder die Filterlisten.'), t('Entfernen'))) return
    try { for (const r of list) await api.del('/api/gatekeeper/rules?domain=' + encodeURIComponent(r.domain)) } catch (e) { fail(e) }
    setSelected([]); load()
  }
  const runCheck = async () => {
    if (!name.trim()) return
    try { setCheck(await api.get<GkCheck>('/api/gatekeeper/check?domain=' + encodeURIComponent(name.trim()))) } catch (e) { setCheck(null); fail(e) }
  }

  const cols: ExplorerCol<GkRule>[] = [
    selectCol<GkRule, string>({ rows: shown, id: r => r.domain, selected, onChange: setSelected }),
    { key: 'domain', label: () => t('Adresse'), width: 0, sort: r => r.domain, render: r => r.domain },
    { key: 'action', label: () => t('Aktion'), width: 120, sort: r => r.action, render: r => <Badge appearance="tint" color={r.action === 'allow' ? 'success' : 'danger'}>{actionText(r.action)}</Badge> },
    { key: 'created', label: () => t('Angelegt'), width: 130, sort: r => r.created, render: r => fmtUnix(r.created) },
  ]
  return (
    <>
      <GkRibbon onReload={load}>
        <RibbonButton icon={<Add20Regular />} label={t('Neue Regel')} onClick={() => setAdding(true)} />
        <RibbonButton icon={<Prohibited20Regular />} label={t('gk|Sperren')} disabled={!picked.some(r => r.action !== 'block')} onClick={() => setAction(picked, 'block')} />
        <RibbonButton icon={<CheckmarkCircle20Regular />} label={t('Erlauben')} disabled={!picked.some(r => r.action !== 'allow')} onClick={() => setAction(picked, 'allow')} />
        <RibbonButton icon={<Delete20Regular />} label={t('Entfernen')} disabled={!picked.length} onClick={() => remove(picked)} />
      </GkRibbon>
      <Hint>{t('Eine eigene Regel gilt für die Adresse und alle Adressen darunter und geht den Filterlisten vor. Reihenfolge: eigene Erlaubnis, eigene Sperre, Erlaubnis einer Filterliste, Sperre einer Filterliste.')}</Hint>
      <Section title={t('Adresse prüfen')} hint={t('Zeigt, ob GateKeeper eine Adresse wie werbung.beispiel.de sperrt und durch welche Liste oder Regel.')}>
        <div className={s.bar}>
          <Input value={name} placeholder={t('gk|werbung.beispiel.de')} style={{ flex: '1 1 240px', maxWidth: 360 }} aria-label={t('Adresse prüfen')}
            onChange={(_, d) => { setName(d.value); setCheck(null) }} onKeyDown={e => { if (e.key === 'Enter') runCheck() }} />
          <Button disabled={!name.trim()} onClick={runCheck}>{t('Prüfen')}</Button>
        </div>
        {check && <Text>
          <Badge appearance="tint" color={check.blocked ? 'danger' : 'success'} style={{ marginRight: 8 }}>{check.blocked ? t('gk|gesperrt') : t('gk|erlaubt')}</Badge>
          <span className={s.mono}>{check.domain}</span> – {checkText(check)}
        </Text>}
      </Section>
      <Section title={t('Eigene Regeln')} last>
        {rules.length === 0
          ? <Text className={s.muted}>{t('Noch keine eigenen Regeln. „Neue Regel“ im Menüband legt eine an; in der Statistik wird aus einer gesperrten Adresse mit „Erlauben“ eine Regel.')}</Text>
          : <>
            <SearchBox placeholder={t('Regeln suchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ maxWidth: 360 }} />
            <ExplorerTable id="gatekeeper.rules" cols={cols} rows={shown} rowKey={r => r.domain} initialSort={{ col: 'domain', desc: false }} />
            {shown.length === 0 && <Text className={s.muted}>{t('Keine passenden Regeln.')}</Text>}
          </>}
      </Section>
      {adding && <RuleDialog initial={{ domain: '', action: 'block' }} onClose={saved => { setAdding(false); if (saved) load() }} />}
    </>
  )
}

// ------------------------------------------------------------ Statistik

const dayText = (day: string) => new Date(day + 'T00:00:00').toLocaleDateString(locale, { weekday: 'short', day: '2-digit', month: '2-digit', year: 'numeric' })

export function GateKeeperStats() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [days, setDays] = useState(30)
  const [stats, setStats] = useState<GkStats | null>(null)
  const [rules, setRules] = useState<GkRule[]>([])
  const [query, setQuery] = useState('')
  const [selected, setSelected] = useState<string[]>([])
  const load = () => Promise.all([
    api.get<GkStats>(`/api/gatekeeper/stats?order=last&limit=2000&days=${days}`),
    api.get<GkRule[]>('/api/gatekeeper/rules'),
  ]).then(([st, r]) => { setStats(st); setRules(r); setSelected(x => x.filter(d => st.hits.some(h => h.domain === d))) }).catch(fail)
  useEffect(() => { load() }, [days]) // eslint-disable-line react-hooks/exhaustive-deps
  if (!stats) return null
  const q = query.trim().toLowerCase()
  const shown = stats.hits.filter(h => !q || h.domain.includes(q))
  const picked = stats.hits.filter(h => selected.includes(h.domain))
  const ruleOf = (d: string) => rules.find(r => r.domain === d)

  const allow = async (list: GkHit[]) => {
    try { for (const h of list) await api.post('/api/gatekeeper/rules', { domain: h.domain, action: 'allow' }) } catch (e) { fail(e); load(); return }
    notify(list.length === 1 ? t('{d} ist jetzt erlaubt', { d: list[0].domain }) : t('{n} Adressen sind jetzt erlaubt', { n: list.length }))
    setSelected([]); load()
  }
  const reset = async () => {
    if (!await askConfirm(t('Zähler zurücksetzen?'), t('Alle Zähler und die Liste der gesperrten Adressen beginnen wieder bei null. Eigene Regeln bleiben.'), t('Zurücksetzen'))) return
    try { await api.del('/api/gatekeeper/stats'); setSelected([]); load() } catch (e) { fail(e) }
  }

  const cols: ExplorerCol<GkHit>[] = [
    selectCol<GkHit, string>({ rows: shown, id: h => h.domain, selected, onChange: setSelected }),
    { key: 'domain', label: () => t('Adresse'), width: 0, sort: h => h.domain, render: h => h.domain },
    { key: 'hits', label: () => t('Treffer'), width: 90, sort: h => h.hits, render: h => <span style={{ fontVariantNumeric: 'tabular-nums' }}>{fmtNum(h.hits)}</span> },
    { key: 'last', label: () => t('gk|Zuletzt'), width: 130, sort: h => h.last, render: h => fmtUnix(h.last) },
    { key: 'rule', label: () => t('Eigene Regel'), width: 120, sort: h => ruleOf(h.domain)?.action ?? '', render: h => {
      const r = ruleOf(h.domain)
      return r ? <Badge appearance="tint" color={r.action === 'allow' ? 'success' : 'danger'}>{actionText(r.action)}</Badge> : null
    } },
  ]
  const dayCols: ExplorerCol<GkStats['days'][number]>[] = [
    { key: 'day', label: () => t('Tag'), width: 160, sort: d => d.day, render: d => dayText(d.day) },
    { key: 'connections', label: () => t('Verbindungen'), width: 120, sort: d => d.connections, render: d => fmtNum(d.connections) },
    { key: 'dns', label: () => t('DNS-Anfragen'), width: 120, sort: d => d.dns, render: d => fmtNum(d.dns) },
    { key: 'blocked', label: () => t('gk|Gesperrt'), width: 100, sort: d => d.blocked, render: d => fmtNum(d.blocked) },
    { key: 'traffic', label: () => t('Datenverkehr'), width: 120, sort: d => (d.up ?? 0) + (d.down ?? 0), render: d => fmtSize((d.up ?? 0) + (d.down ?? 0)) },
  ]
  return (
    <>
      <GkRibbon onReload={load}>
        <RibbonButton icon={<CheckmarkCircle20Regular />} label={t('Erlauben')} disabled={!picked.length} onClick={() => allow(picked)} />
        <RibbonButton icon={<ArrowReset20Regular />} label={t('Zähler zurücksetzen')} onClick={reset} />
      </GkRibbon>
      <Hint>{t('Datenschutz: GateKeeper speichert auf dem Server nur Zähler und die gesperrten Adressen, keine Inhalte. Welche Apps über GateKeeper laufen, speichert nur das Handy.')}</Hint>
      <Section title={t('Gesperrte Adressen')} hint={t('„Erlauben“ im Menüband legt für die gewählten Adressen eine eigene Regel an.')}>
        {stats.hits.length === 0
          ? <Text className={s.muted}>{t('Noch keine gesperrten Adressen.')}</Text>
          : <>
            <SearchBox placeholder={t('Adressen suchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ maxWidth: 360 }} />
            <ExplorerTable id="gatekeeper.hits" cols={cols} rows={shown} rowKey={h => h.domain} initialSort={{ col: 'last', desc: true }} />
            {shown.length === 0 && <Text className={s.muted}>{t('Keine passenden Adressen.')}</Text>}
          </>}
      </Section>
      <Section title={t('Verlauf')} last>
        <div className={s.bar}>
          <Field label={t('Zeitraum')} orientation="horizontal">
            <Select value={String(days)} onChange={(_, d) => setDays(Number(d.value))}>
              <option value="7">{t('7 Tage')}</option>
              <option value="30">{t('30 Tage')}</option>
              <option value="90">{t('90 Tage')}</option>
            </Select>
          </Field>
          <Text className={s.muted}>{t('Zusammen: {c} Verbindungen, {d} DNS-Anfragen, {b} gesperrt', {
            c: fmtNum(stats.totals.connections), d: fmtNum(stats.totals.dns), b: fmtNum(stats.totals.blocked) })}
            {' · '}{t('Datenverkehr {a}, eingespart etwa {b}', { a: fmtSize(stats.totals.traffic ?? 0), b: fmtSize(stats.totals.saved ?? 0) })}</Text>
        </div>
        {stats.days.length > 0 && <ExplorerTable id="gatekeeper.days" cols={dayCols} rows={stats.days} rowKey={d => d.day} initialSort={{ col: 'day', desc: true }} />}
      </Section>
    </>
  )
}
