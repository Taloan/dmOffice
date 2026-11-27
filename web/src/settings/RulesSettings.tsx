import {
  Button, Card, Checkbox, Dropdown, Field, Input, Option, Slider, Switch, Text, makeStyles, tokens, Badge, MessageBar, MessageBarBody,
  MenuItem, MenuDivider, Tab, TabList,
} from '@fluentui/react-components'
import { clearAll } from '../mail/messageCache'
import { Add20Regular, Delete20Regular, ArrowUp20Regular, ArrowDown20Regular, Dismiss16Regular, Add16Regular, Play16Regular, ChevronDown20Regular, ChevronRight20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { Account, api, ApiError, featureOn, Folder } from '../api'
import { useApp } from '../App'
import { CategoryDot, useCategories } from '../mail/Categories'
import { RuleSource, sourceValue, takeRuleSource } from '../mail/ruleFromMail'
import { newId } from '../shell/browser'
import { FilterSelect } from '../shell/FilterSelect'
import { t } from '../i18n'
import { useAutoSave } from './autosave' // [feature:savemode]
import { ArrowReset20Regular, BrainCircuit20Regular, Save20Regular } from '@fluentui/react-icons'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { Section, usePageTab } from './parts'
import { ContextMenu, askConfirm, askPassword, askText, isTyping, useContextMenu } from '../shell/context'

const useStyles = makeStyles({
  rule: { padding: '14px 16px', display: 'flex', flexDirection: 'column', gap: '10px' },
  line: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  head: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  // Kennung ruletabs: zugeklappte Regel zeigt nur Kopfzeile und eine Zeile Zusammenfassung
  summary: { color: tokens.colorNeutralForeground3, cursor: 'pointer', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', paddingLeft: '4px' },
  tabs: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
})

interface Condition { field: string; op: string; value: string }
interface Action { type: string; value: string; read?: boolean }
interface Rule { id: string; name: string; enabled: boolean; accountId: string | null; match: 'all' | 'any'; conditions: Condition[]; actions: Action[] }

const fields = [['from', t('Von')], ['to', t('An/Cc')], ['subject', t('Betreff')], ['body', t('mail|Text')], ['any', t('irgendwo')], ['header:List-Id', t('Kopfzeile List-Id')]]
const ops = [['contains', t('enthält')], ['notContains', t('enthält nicht')], ['equals', t('ist genau')], ['startsWith', t('beginnt mit')], ['endsWith', t('endet mit')], ['regex', t('passt auf (Regex)')]]
const actions = [['move', t('Verschieben nach')], ['copy', t('Kopieren nach')], ['forward', t('Weiterleiten an')], ['redirect', t('Umleiten an')], ['markRead', t('Als gelesen markieren')],
  ['flag', t('Kennzeichnen')], ['category', t('Kategorie zuweisen')], ['delete', t('Löschen (Papierkorb)')],
  ['trashAfter', t('In den Papierkorb nach … Tagen')], ['destroyAfter', t('Vernichten nach … Tagen')],
  ['stop', t('Keine weiteren Regeln')]]
const label = (list: string[][], k: string) => list.find(x => x[0] === k)?.[1] ?? k
/** Kennung ruleforward: Ziele von Weiterleiten/Umleiten mit Stand der Bestätigung (GET /api/settings/forward-targets). */
interface TargetState { address: string; confirmed: boolean; sentAt: string | null; expired: boolean; error: string | null; locked: boolean }
interface Targets { own: string[]; targets: TargetState[]; no8bit?: string[] }
const addresses = (v: string) => v.split(/[,;]/).map(x => x.trim().toLowerCase()).filter(x => x.includes('@'))
/** Mailadresse wie auf dem Server (ForwardTargets.IsAddress): Name@Domain mit Punkt, ohne Leerzeichen und Sonderzeichen wie <>. */
const MAIL_RE = /^[\p{L}\p{N}.!#$%&'*+/=?^_`{|}~-]+@[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?(?:\.[\p{L}\p{N}](?:[\p{L}\p{N}-]*[\p{L}\p{N}])?)+$/u
/** Hinweis am Feld, wenn die Angabe einer Aktion so nicht wirken kann; sonst null. */
const actionProblem = (a: Action): string | null => {
  if (a.type === 'forward' || a.type === 'redirect') {
    const parts = a.value.split(/[,;]/).map(x => x.trim()).filter(Boolean)
    if (!parts.length) return t('Bitte eine Mailadresse eingeben.')
    const bad = parts.filter(x => x.length > 254 || !MAIL_RE.test(x))
    return bad.length ? t('Bitte gültige Mailadressen eingeben, etwa name@beispiel.de: {list}', { list: bad.join(', ') }) : null
  }
  if (a.type === 'trashAfter' || a.type === 'destroyAfter') {
    const d = Number(a.value.trim())
    return /^\d+$/.test(a.value.trim()) && d >= 1 && d <= 3650 ? null : t('Bitte eine Zahl von 1 bis 3650 eingeben.')
  }
  return null
}

export function RulesSettings() {
  const s = useStyles()
  const { fail, notify, me } = useApp()
  const categories = useCategories()
  const [rules, setRules] = useState<Rule[] | null>(null)
  const [accounts, setAccounts] = useState<Account[]>([])
  const [folders, setFolders] = useState<Record<string, Folder[]>>({})
  const [dirty, setDirty] = useState(false)
  // Aus einer Mail angelegt: ihre Angaben je Regel - beim Wechsel des Feldes einer Bedingung passt der Wert dazu
  const [sources, setSources] = useState<Record<string, RuleSource>>({})
  const [running, setRunning] = useState<string | null>(null)
  const fresh = useRef<HTMLDivElement | null>(null)
  // Stand auf dem Server: der Ein/Aus-Schalter speichert sofort nur sich selbst, andere Änderungen bleiben beim „Speichern“
  const saved = useRef<Rule[]>([])
  // Rechtsklick auf eine Regel (Kennung ctxall), nicht in ihren Feldern: dieselben Aktionen wie die Knöpfe
  const menu = useContextMenu<number>()
  const ctxall = featureOn('ctxall')
  // Kennung ruletabs: Reiter je Postfach („Alle Konten“ für Regeln ohne Konto), Regeln zugeklappt bis auf die gerade angelegten
  const tabbed = featureOn('ruletabs')
  const [tab, setTab] = useState<string | null>(null)
  const [open, setOpen] = useState<Set<string>>(new Set())
  const setOpenOne = (id: string, on: boolean) => setOpen(o => { const n = new Set(o); if (on) n.add(id); else n.delete(id); return n })
  // Kennung ruleforward: Umleiten, Ordner anderer eigener Konten, Bestätigung fremder Ziele
  const fwd = featureOn('ruleforward')
  const [targets, setTargets] = useState<Targets | null>(null)
  const loadTargets = () => { if (fwd) api.get<Targets>('/api/settings/forward-targets').then(setTargets).catch(() => {}) }
  useEffect(loadTargets, []) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => {
    api.get<Rule[]>('/api/settings/rules').then(list => {
      saved.current = list
      const src = takeRuleSource()
      if (!src) { setRules(list); return }
      // "Regel erstellen" in der Mail: Newsletter nach List-Id, sonst nach Absender
      const field = src.listId ? 'header:List-Id' : 'from'
      const rule: Rule = { id: newId(), name: src.listId ? src.fromName || src.listId : src.fromName || t('Neue Regel'), enabled: true, accountId: src.account, match: 'all',
        conditions: [{ field, op: 'contains', value: sourceValue(src, field) }], actions: [{ type: 'move', value: '' }] }
      setRules([...list, rule])
      setTab(rule.accountId ?? '')
      setOpen(new Set([rule.id]))
      setSources({ [rule.id]: src })
      setDirty(true)
      setTimeout(() => fresh.current?.scrollIntoView({ behavior: 'smooth', block: 'center' }), 100)
    }).catch(fail)
    // [feature:mailaccountoff]
    // Regeln deaktivierter Konten bleiben bearbeitbar
    api.get<Account[]>('/api/mail/accounts?all=true').then(async list => {
    // [feature:mailaccountoff:else]
    // api.get<Account[]>('/api/mail/accounts').then(async list => {
    // [/feature:mailaccountoff]
      setAccounts(list)
      const entries = await Promise.all(list.map(async a => [a.id, await api.get<Folder[]>(`/api/mail/${a.id}/folders`).catch(() => [])] as const))
      setFolders(Object.fromEntries(entries))
    }).catch(() => {})
  }, [fail])

  // [feature:savemode]
  // Kennung savemode: Regeln speichern von selbst (Felder beim Verlassen, alles andere sofort)
  const later = useRef<() => Promise<boolean>>(async () => true)
  // Vergleich mit dem Stand auf dem Server: Änderungen während des Speicherns gehen nicht unter
  // Angaben, die so nicht wirken können (Adresse, Tage): erst speichern, wenn sie stimmen - der Hinweis steht am Feld
  const invalid = !!rules?.some(r => r.actions.some(a => actionProblem(a) != null))
  const auto = useAutoSave(rules, !!rules && !invalid && JSON.stringify(rules) !== JSON.stringify(saved.current), () => later.current())
  // [feature:savemode:else]
  // const auto = false
  // [/feature:savemode]
  if (!rules) return null
  // Reiter einer Regel: ihr Konto, wenn es das noch gibt, sonst „Alle Konten“
  const tabOf = (r: Rule) => r.accountId && accounts.some(a => a.id === r.accountId) ? r.accountId : ''
  const counts = (key: string) => rules.filter(r => tabOf(r) === key).length
  // Ohne Wahl: „Alle Konten“, wenn dort Regeln stehen, sonst das erste Konto mit Regeln
  const current = tab ?? (counts('') > 0 || !accounts.length ? '' : accounts.find(a => counts(a.id) > 0)?.id ?? '')
  const shown = rules.map((r, i) => ({ r, i })).filter(x => !tabbed || tabOf(x.r) === current)
  const isOpen = (r: Rule) => !tabbed || open.has(r.id)
  const update = (i: number, p: Partial<Rule>) => { setRules(rules.map((r, j) => (j === i ? { ...r, ...p } : r))); setDirty(true) }
  // Nach dem Speichern: neue Ziele warten, die Bestätigungsmail geht im Hintergrund raus - Stand gleich und kurz danach holen
  const afterSave = () => { loadTargets(); if (fwd) setTimeout(loadTargets, 3000) }
  const save = async () => {
    if (invalid) return false
    try { await api.put('/api/settings/rules', rules); saved.current = rules; setDirty(false); afterSave(); if (!auto) notify(t('Regeln gespeichert')); return true }
    catch (e) {
      // Neue Weiterleitung: nur mit dem Anmelde-Passwort, bei Zwei-Faktor auch dem Code
      if (e instanceof ApiError && e.status === 403 && e.data?.confirm) {
        const c = await askPassword(t('Neues Ziel bestätigen'), !!me?.twoFactor)
        if (!c) return false
        try { await api.put('/api/settings/rules', { rules, password: c.password, code: c.code }); saved.current = rules; setDirty(false); afterSave(); if (!auto) notify(t('Regeln gespeichert')); return true }
        catch (e2) { fail(e2); return false }
      }
      fail(e); return false
    }
  }
  later.current = save // [feature:savemode]
  /** Ordner eines anderen eigenen Kontos („Konto|Ordner“, Kennung ruleforward) als „Konto › Ordner“. */
  const folderLabel = (v: string) => {
    const bar = v.indexOf('|')
    const acc = bar > 0 ? accounts.find(a => a.id === v.slice(0, bar)) : undefined
    return acc ? `${acc.name} › ${v.slice(bar + 1)}` : v
  }
  /** Ordner für Verschieben/Kopieren: die des Kontos der Regel, mit ruleforward dazu die der anderen eigenen Konten. */
  const folderItems = (r: Rule, own: Folder[]) => [
    ...own.filter(f => f.selectable).map(f => ({ id: f.path, label: f.path })),
    ...(fwd ? accounts.filter(a => a.id !== r.accountId).flatMap(a => (folders[a.id] ?? []).filter(f => f.selectable)
      .map(f => ({ id: `${a.id}|${f.path}`, label: `${a.name} › ${f.path}` }))) : []),
  ]
  const resend = async (address: string) => {
    try { await api.post('/api/settings/forward-targets/resend', { address }); notify(t('Bestätigungsmail gesendet')) } catch (e) { fail(e) }
    loadTargets()
  }
  const enterCode = async (address: string) => {
    const code = await askText(t('Code aus der Bestätigungsmail'), '', address, t('Bestätigen'))
    if (!code) return
    try { await api.post('/api/settings/forward-targets/confirm', { address, code }); notify(t('Ziel bestätigt')) } catch (e) { fail(e) }
    loadTargets()
  }
  /** Je Ziel der Stand: eigenes Konto, bestätigt, wartet (Erneut senden, Code eingeben) oder neu (geht nach dem Speichern raus). */
  /** Kennung ruleforward: Konten der Regel, deren SMTP-Server kein 8BITMIME kann (Namen). */
  const no8bit = (r: Rule) => (targets?.no8bit ?? []).filter(id => !r.accountId || r.accountId === id).map(id => accounts.find(a => a.id === id)?.name ?? id)
  const targetStates = (value: string) => {
    if (!fwd || !targets) return null
    const list = addresses(value)
    if (!list.length) return null
    return (
      <div style={{ flexBasis: '100%', display: 'flex', flexDirection: 'column', gap: 4, paddingLeft: 4 }}>
        {list.map(addr => {
          const st = targets.targets.find(x => x.address === addr)
          const own = targets.own.includes(addr)
          return (
            <div key={addr} className={s.line} style={{ gap: 6 }}>
              <Text size={200}>{addr}</Text>
              {own ? <Badge appearance="tint" color="success">{t('eigenes Konto')}</Badge>
                : st?.confirmed ? <Badge appearance="tint" color="success">{t('bestätigt')}</Badge>
                : st ? <>
                  <Badge appearance="tint" color="warning">{st.expired ? t('abgelaufen') : t('wartet auf Bestätigung')}</Badge>
                  {st.error && <Text size={200} className={s.muted}>{t('Nicht gesendet: {error}', { error: st.error })}</Text>}
                  <Button size="small" appearance="subtle" onClick={() => resend(addr)}>{t('Erneut senden')}</Button>
                  {!st.expired && !st.locked && st.sentAt && <Button size="small" appearance="subtle" onClick={() => enterCode(addr)}>{t('Code eingeben')}</Button>}
                </>
                : <Text size={200} className={s.muted}>{t('neu – nach dem Speichern geht eine Bestätigungsmail an diese Adresse')}</Text>}
            </div>
          )
        })}
      </div>
    )
  }
  /** Eine Zeile für die zugeklappte Regel: Bedingungen, dann Aktionen. */
  const summary = (r: Rule) => {
    const cond = r.conditions.map(c => `${label(fields, c.field)} ${label(ops, c.op)} „${c.value}“`).join(r.match === 'all' ? t(' und ') : t(' oder '))
    const act = r.actions.map(a => {
      if (a.type === 'trashAfter' || a.type === 'destroyAfter') return label(actions, a.type).replace('…', a.value)
      const v = a.type === 'category' ? categories.find(c => c.key === a.value)?.name ?? '' : a.type === 'move' || a.type === 'copy' ? folderLabel(a.value) : a.value
      return v ? `${label(actions, a.type)} ${v}` : label(actions, a.type)
    }).join(' · ')
    return `${cond || t('ohne Bedingung')} → ${act || t('ohne Aktion')}`
  }
  /** Jetzt ausfuehren: erst speichern, dann auf die Mails im Posteingang anwenden (ohne Weiterleiten). */
  const runNow = async (r: Rule) => {
    if (dirty && !(await save())) return
    setRunning(r.id)
    try {
      const res = await api.post<{ checked: number; matched: number }>(`/api/settings/rules/${r.id}/run`)
      notify(res.matched === 1 ? t('1 Mail getroffen') : t('{n} Mails getroffen', { n: res.matched }), t('{n} Mails im Posteingang geprüft', { n: res.checked }))
    } catch (e) { fail(e) } finally { setRunning(null) }
  }
  /** Regel ein- oder ausschalten: gilt sofort (wie der Junk-Filter). Eine noch nicht gespeicherte Regel wird erst mit „Speichern“ angelegt. */
  const toggle = async (i: number, enabled: boolean) => {
    const id = rules[i].id
    const was = saved.current.find(r => r.id === id)
    // Nur der Schalter weicht ab: danach ist nichts mehr ungespeichert
    const onlySwitch = JSON.stringify(rules.map(r => r.id === id ? { ...r, enabled } : r)) === JSON.stringify(saved.current.map(r => r.id === id ? { ...r, enabled } : r))
    setRules(rules.map((r, j) => (j === i ? { ...r, enabled } : r)))
    if (!was) { setDirty(true); return }
    const next = saved.current.map(r => r.id === id ? { ...r, enabled } : r)
    try {
      await api.put('/api/settings/rules', next)
      saved.current = next
      if (onlySwitch) setDirty(false)
      notify(enabled ? t('Regel eingeschaltet') : t('Regel ausgeschaltet'))
    } catch (e) { fail(e); setRules(rs => rs ? rs.map(r => r.id === id ? { ...r, enabled: was.enabled } : r) : rs) }
  }
  const add = () => {
    const id = newId()
    setRules([...rules, { id, name: t('Neue Regel'), enabled: true, accountId: tabbed ? current || null : null, match: 'all',
      conditions: [{ field: 'from', op: 'contains', value: '' }], actions: [{ type: 'move', value: '' }] }])
    if (tabbed) setOpenOne(id, true)
    setDirty(true)
  }
  /** Verschieben: mit Reitern zur Nachbarregel im selben Reiter (die Reihenfolge auf dem Server bleibt eine Liste). */
  const neighbour = (i: number, d: number) => {
    if (!tabbed) return i + d
    const k = shown.findIndex(x => x.i === i) + d
    return k >= 0 && k < shown.length ? shown[k].i : -1
  }
  const move = (i: number, d: number) => {
    const j = neighbour(i, d)
    if (j < 0 || j >= rules.length) return
    const next = [...rules];[next[i], next[j]] = [next[j], next[i]]
    setRules(next); setDirty(true)
  }

  return (
    <>
      <Text className={s.muted}>
        {t('Regeln laufen auf dem Server für jede neue Mail im Posteingang – auch wenn kein Browser offen ist. Von oben nach unten; „Keine weiteren Regeln“ beendet. Für Mails, die schon da sind: „Jetzt ausführen“. Eine Regel aus einer Mail legst du in der Mail über Mehr → Regel erstellen an.')}
      </Text>
      {tabbed && (
        <div className={s.tabs}>
          <TabList selectedValue={current} onTabSelect={(_, d) => setTab(d.value as string)}>
            <Tab value="">{t('Alle Konten')} ({counts('')})</Tab>
            {accounts.map(a => <Tab key={a.id} value={a.id}>{a.name} ({counts(a.id)})</Tab>)}
          </TabList>
          {shown.length > 0 && (shown.every(x => open.has(x.r.id))
            ? <Button size="small" appearance="subtle" icon={<ChevronRight20Regular />} onClick={() => setOpen(new Set())}>{t('Alle zuklappen')}</Button>
            : <Button size="small" appearance="subtle" icon={<ChevronDown20Regular />} onClick={() => setOpen(new Set([...open, ...shown.map(x => x.r.id)]))}>{t('Alle aufklappen')}</Button>)}
        </div>
      )}
      {tabbed && shown.length === 0 && <Text className={s.muted}>{t('In diesem Reiter gibt es noch keine Regel.')}</Text>}
      {shown.map(({ r, i }) => {
        const folderList = (r.accountId ? folders[r.accountId] : folders[accounts[0]?.id]) ?? []
        return (
          <Card key={r.id} className={s.rule} ref={sources[r.id] ? fresh : undefined} onContextMenu={ctxall ? e => { if (!isTyping(e)) menu.open(e, i) } : undefined}>
            <div className={s.head}>
              {tabbed && <Button appearance="subtle" icon={open.has(r.id) ? <ChevronDown20Regular /> : <ChevronRight20Regular />} onClick={() => setOpenOne(r.id, !open.has(r.id))}
                aria-label={open.has(r.id) ? t('Zuklappen') : t('Aufklappen')} title={open.has(r.id) ? t('Zuklappen') : t('Aufklappen')} aria-expanded={open.has(r.id)} />}
              <Switch checked={r.enabled} onChange={(_, d) => toggle(i, d.checked)} title={t('Ein- und Ausschalten gilt sofort')} />
              <Input value={r.name} onChange={(_, d) => update(i, { name: d.value })} style={{ flex: '1 1 160px', minWidth: 0 }} appearance="underline" />
              <Dropdown style={{ minWidth: 150, flex: '0 1 180px' }} value={r.accountId ? accounts.find(a => a.id === r.accountId)?.name ?? '' : t('Alle Konten')}
                selectedOptions={[r.accountId ?? '']} onOptionSelect={(_, d) => { update(i, { accountId: d.optionValue || null }); if (tabbed) setTab(d.optionValue ?? '') }}>
                <Option value="">{t('Alle Konten')}</Option>
                {accounts.map(a => <Option key={a.id} value={a.id}>{a.name}</Option>)}
              </Dropdown>
              <Button appearance="subtle" icon={<ArrowUp20Regular />} disabled={neighbour(i, -1) < 0} onClick={() => move(i, -1)} aria-label={t('Nach oben')} title={t('Nach oben')} />
              <Button appearance="subtle" icon={<ArrowDown20Regular />} disabled={neighbour(i, 1) < 0 || neighbour(i, 1) >= rules.length} onClick={() => move(i, 1)} aria-label={t('Nach unten')} title={t('Nach unten')} />
              <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => { setRules(rules.filter((_, j) => j !== i)); setDirty(true) }} aria-label={t('Regel löschen')} title={t('Regel löschen')} />
            </div>
            {!isOpen(r) && <Text className={s.summary} onClick={() => setOpenOne(r.id, true)} title={summary(r)}>{summary(r)}</Text>}
            {isOpen(r) && <>
            <div className={s.line}>
              <Text>{t('Wenn')}</Text>
              <Dropdown style={{ minWidth: 90 }} value={r.match === 'all' ? t('alle') : t('eine')} selectedOptions={[r.match]} onOptionSelect={(_, d) => update(i, { match: d.optionValue as any })}>
                <Option value="all">{t('alle')}</Option><Option value="any">{t('eine')}</Option>
              </Dropdown>
              <Text>{t('der Bedingungen zutreffen:')}</Text>
            </div>
            {r.conditions.map((c, ci) => {
              const setC = (p: Partial<Condition>) => update(i, { conditions: r.conditions.map((x, k) => (k === ci ? { ...x, ...p } : x)) })
              // Aus einer Mail angelegt: anderes Feld - Wert aus der Mail, solange er nicht von Hand geaendert wurde
              const src = sources[r.id]
              const setField = (field: string) => setC(src && (!c.value || c.value === sourceValue(src, c.field)) ? { field, value: sourceValue(src, field) } : { field })
              return (
                <div key={ci} className={s.line}>
                  <Dropdown style={{ minWidth: 150 }} value={label(fields, c.field)} selectedOptions={[c.field]} onOptionSelect={(_, d) => setField(d.optionValue!)}>
                    {fields.map(([k, l]) => <Option key={k} value={k}>{l}</Option>)}
                  </Dropdown>
                  <Dropdown style={{ minWidth: 160 }} value={label(ops, c.op)} selectedOptions={[c.op]} onOptionSelect={(_, d) => setC({ op: d.optionValue! })}>
                    {ops.map(([k, l]) => <Option key={k} value={k}>{l}</Option>)}
                  </Dropdown>
                  <Input style={{ flex: 1, minWidth: 200 }} value={c.value} onChange={(_, d) => setC({ value: d.value })} />
                  <Button appearance="subtle" icon={<Dismiss16Regular />} onClick={() => update(i, { conditions: r.conditions.filter((_, k) => k !== ci) })} aria-label={t('Entfernen')} />
                </div>
              )
            })}
            <div><Button size="small" appearance="subtle" icon={<Add16Regular />} onClick={() => update(i, { conditions: [...r.conditions, { field: 'subject', op: 'contains', value: sources[r.id] ? sourceValue(sources[r.id], 'subject') : '' }] })}>{t('Bedingung')}</Button></div>
            <Text>{t('dann:')}</Text>
            {r.actions.map((a, ai) => {
              const setA = (p: Partial<Action>) => update(i, { actions: r.actions.map((x, k) => (k === ai ? { ...x, ...p } : x)) })
              const problem = actionProblem(a)
              return (
                <div key={ai} className={s.line}>
                  <Dropdown style={{ minWidth: 200 }} value={label(actions, a.type)} selectedOptions={[a.type]} onOptionSelect={(_, d) => setA({ type: d.optionValue!, value: d.optionValue === 'trashAfter' ? '7' : d.optionValue === 'destroyAfter' ? '30' : '' })}>
                    {actions.filter(([k]) => (featureOn('mailexpiry') || (k !== 'trashAfter' && k !== 'destroyAfter')) && (fwd || k !== 'redirect' || a.type === 'redirect')
                    ).map(([k, l]) => <Option key={k} value={k}>{l}</Option>)}
                  </Dropdown>
                  {(a.type === 'move' || a.type === 'copy') && featureOn('filterpick') && (
                    <FilterSelect style={{ minWidth: 240 }} value={a.value} unknown={folderLabel(a.value)} placeholder={t('Ordner wählen')} onChange={v => setA({ value: v })}
                      items={folderItems(r, folderList)} />
                  )}
                  {(a.type === 'move' || a.type === 'copy') && !featureOn('filterpick') && (
                    <Dropdown style={{ minWidth: 240 }} value={folderLabel(a.value)} selectedOptions={[a.value]} onOptionSelect={(_, d) => setA({ value: d.optionValue! })} placeholder={t('Ordner wählen')}>
                      {folderItems(r, folderList).map(f => <Option key={f.id} value={f.id}>{f.label}</Option>)}
                    </Dropdown>
                  )}
                  {/* [feature:rulecopyread] */}
                  {a.type === 'copy' && featureOn('rulecopyread') && (
                    <Checkbox label={t('Kopie als gelesen markieren')} checked={!!a.read} onChange={(_, d) => setA({ read: !!d.checked })} />
                  )}
                  {/* [/feature:rulecopyread] */}
                  {a.type === 'category' && (
                    <Dropdown style={{ minWidth: 240 }} value={categories.find(c => c.key === a.value)?.name ?? ''} selectedOptions={[a.value]}
                      onOptionSelect={(_, d) => setA({ value: d.optionValue! })} placeholder={t('Kategorie wählen')}>
                      {categories.map(c => <Option key={c.key} value={c.key} text={c.name}><CategoryDot color={c.color} /> {c.name}</Option>)}
                    </Dropdown>
                  )}
                  {(a.type === 'trashAfter' || a.type === 'destroyAfter') && <>
                    <Input type="number" min={1} max={3650} style={{ width: 90 }} value={a.value} aria-invalid={!!problem} onChange={(_, d) => setA({ value: d.value })} />
                    <Text size={200} className={s.muted}>{a.type === 'destroyAfter'
                      ? t('Tage ab Eingang – danach endgültig gelöscht, auch auf dem Mailserver, ohne Papierkorb.')
                      : t('Tage ab Eingang – bis dahin lässt es sich an der Mail zurücknehmen.')}</Text>
                  </>}
                  {(a.type === 'forward' || a.type === 'redirect') && <Input style={{ flex: 1, minWidth: 240 }} placeholder={t('adresse@beispiel.de, …')} value={a.value} aria-invalid={!!problem} onChange={(_, d) => setA({ value: d.value })} />}
                  <Button appearance="subtle" icon={<Dismiss16Regular />} onClick={() => update(i, { actions: r.actions.filter((_, k) => k !== ai) })} aria-label={t('Entfernen')} />
                  {problem && <Text size={200} role="alert" style={{ flexBasis: '100%', color: tokens.colorPaletteRedForeground1 }}>{problem}</Text>}
                  {a.type === 'redirect' && fwd && <Text size={200} className={s.muted} style={{ flexBasis: '100%' }}>
                    {t('Die Mail geht unverändert weiter, Absender bleibt der ursprüngliche. Nicht jeder Mailanbieter nimmt Mails mit fremdem Absender zum Versand an.')}</Text>}
                  {a.type === 'redirect' && fwd && no8bit(r).length > 0 && <Text size={200} style={{ flexBasis: '100%', color: tokens.colorPaletteMarigoldForeground2 }}>
                    {t('Der Mailserver von {konten} kann kein 8BITMIME: Mails mit 8-Bit-Inhalt werden beim Umleiten umkodiert, die Signatur des ursprünglichen Absenders passt dann nicht mehr. Empfänger können sie ablehnen oder als Spam einstufen.', { konten: no8bit(r).join(', ') })}</Text>}
                  {(a.type === 'forward' || a.type === 'redirect') && !problem && targetStates(a.value)}
                </div>
              )
            })}
            <div><Button size="small" appearance="subtle" icon={<Add16Regular />} onClick={() => update(i, { actions: [...r.actions, { type: 'markRead', value: '' }] })}>{t('Aktion')}</Button></div>
            {!r.accountId && accounts.length > 1 && r.actions.some(a => (a.type === 'move' || a.type === 'copy') && !(fwd && a.value.includes('|'))) && (
              <Text size={200} className={s.muted}>{t('Ordner gelten je Konto – bei „Alle Konten“ muss der Ordner in jedem Konto so heißen.')}</Text>
            )}
            <div className={s.line}>
              <Button size="small" icon={<Play16Regular />} disabled={running != null || r.conditions.length === 0} onClick={() => runNow(r)}>
                {running === r.id ? t('Läuft …') : t('Jetzt ausführen')}
              </Button>
              <Text size={200} className={s.muted}>{fwd ? t('Auf die Mails, die schon im Posteingang liegen (auch wenn die Regel aus ist); weitergeleitet und umgeleitet wird dabei nicht.')
                : t('Auf die Mails, die schon im Posteingang liegen (auch wenn die Regel aus ist); weitergeleitet wird dabei nicht.')}</Text>
            </div>
            </>}
          </Card>
        )
      })}
      {menu.ctx && rules[menu.ctx.data] && (() => {
        const i = menu.ctx.data, r = rules[i]
        return (
          <ContextMenu at={menu.ctx.at} onClose={menu.close}>
            <MenuItem onClick={() => toggle(i, !r.enabled)}>{r.enabled ? t('Ausschalten') : t('Einschalten')}</MenuItem>
            {tabbed && <MenuItem onClick={() => setOpenOne(r.id, !open.has(r.id))}>{open.has(r.id) ? t('Zuklappen') : t('Aufklappen')}</MenuItem>}
            <MenuItem icon={<Play16Regular />} disabled={running != null || r.conditions.length === 0} onClick={() => runNow(r)}>{t('Jetzt ausführen')}</MenuItem>
            <MenuItem icon={<ArrowUp20Regular />} disabled={neighbour(i, -1) < 0} onClick={() => move(i, -1)}>{t('Nach oben')}</MenuItem>
            <MenuItem icon={<ArrowDown20Regular />} disabled={neighbour(i, 1) < 0 || neighbour(i, 1) >= rules.length} onClick={() => move(i, 1)}>{t('Nach unten')}</MenuItem>
            <MenuDivider />
            <MenuItem icon={<Delete20Regular />} onClick={() => { setRules(rules.filter((_, j) => j !== i)); setDirty(true) }}>{t('Regel löschen')}</MenuItem>
          </ContextMenu>
        )
      })()}
      <RibbonActions>
        <RibbonButton icon={<Add20Regular />} label={t('Regel hinzufügen')} onClick={add} />
        {!auto && <RibbonButton icon={<Save20Regular />} label={t('Speichern')} primary disabled={!dirty || invalid} onClick={save} />}
      </RibbonActions>
    </>
  )
}

export function SpamSettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [data, setData] = useState<any>(null)
  const load = () => api.get('/api/settings/spam').then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const [tab, tabBar] = usePageTab('junk', [['filter', t('Filter')], ['lists', t('Spamlisten')], ['images', t('Sichere Absender für Bilder')]])
  if (!data) return null
  const st = data.settings
  const save = async (p: any) => {
    const next = { ...st, ...p }
    setData({ ...data, settings: next })
    try { await api.put('/api/settings/spam', next) } catch (e) { fail(e) }
  }
  const train = async () => {
    try { const r = await api.post('/api/settings/spam/train-folders'); notify(t('Gelernt'), t('{spam} Junk, {ham} gute Mails', { spam: r.spam, ham: r.ham })); load() } catch (e) { fail(e) }
  }
  const reset = async () => {
    if (!await askConfirm('', t('Alles Gelernte vergessen?'), t('Vergessen'))) return
    try { await api.post('/api/settings/spam/reset'); load() } catch (e) { fail(e) }
  }
  const ready = data.trainedSpam >= st.minTrained && data.trainedHam >= st.minTrained
  return (
    <>
      {tabBar}
      {tab === 'filter' && <>
      <RibbonActions>
        <RibbonButton icon={<BrainCircuit20Regular />} label={t('Aus Ordnern lernen')} primary onClick={train} />
        <RibbonButton icon={<ArrowReset20Regular />} label={t('Gelerntes vergessen')} onClick={reset} />
      </RibbonActions>
      <Section title={t('Filter')}>
      <Text className={s.muted}>
        {t('Ein lernender Filter nach dem Verfahren von SpamBayes. Er lernt aus „Junk“ und „Kein Junk“ in der Mailansicht und bewertet jede neue Mail mit einem Wert zwischen 0 (gut) und 100 (Junk). Er arbeitet zusätzlich zum Spamfilter des Mailanbieters.')}
      </Text>
      <Switch label={t('Junk-Filter einschalten')} checked={st.enabled} onChange={(_, d) => save({ enabled: d.checked })} />
      <div style={{ display: 'flex', gap: 12 }}>
        <Badge size="large" color="danger" appearance="tint">{t('{n} als Junk gelernt', { n: data.trainedSpam })}</Badge>
        <Badge size="large" color="success" appearance="tint">{t('{n} als gut gelernt', { n: data.trainedHam })}</Badge>
      </div>
      {!ready && st.enabled && (
        <MessageBar intent="info">
          <MessageBarBody>{t('Bewertet und verschoben wird erst ab je {n} gelernten Mails. Bis dahin bleibt alles im Posteingang – oder einmal aus den vorhandenen Ordnern lernen.', { n: st.minTrained })}</MessageBarBody>
        </MessageBar>
      )}
      <Switch label={t('Kontakte und Empfänger gesendeter Mails sind sichere Absender')} checked={st.trustKnown ?? true} disabled={!st.enabled}
        onChange={(_, d) => save({ trustKnown: d.checked })} />
      <Switch label={t('Aus den Mails sicherer Absender lernen')} checked={st.learnKnown ?? true} disabled={!st.enabled}
        onChange={(_, d) => save({ learnKnown: d.checked })} />
      <Text size={200} className={s.muted}>
        {t('Sicher ist, wer im Adressbuch steht oder eine Mail von dir bekommen hat (Gesendet-Ordner aller Konten). Ihre Mails kommen nie in Junk-E-Mail und werden auf Wunsch als gut gelernt. Deine eigenen Adressen zählen nicht.')}
      </Text>
      <SafeSenders list={data.safe ?? []} onChange={load} />
      </Section>
      <Section title={t('Lernen')}>
      <Field label={t('Aus dem Junk-Ordner des Mailanbieters lernen')}
        hint={t('Was der Mailanbieter in Junk gelegt hat und dort nach drei Tagen noch liegt, wird als Junk gelernt. Ausgenommen sind sichere Absender und nachweislich echte Mails großer Anbieter wie Google, Microsoft und PayPal.')}>
        <Dropdown disabled={!st.enabled} style={{ maxWidth: 260 }}
          value={junkHours(st.junkFolderHours ?? 24)} selectedOptions={[String(st.junkFolderHours ?? 24)]}
          onOptionSelect={(_, d) => save({ junkFolderHours: Number(d.optionValue) })}>
          {[0, 6, 24].map(h => <Option key={h} value={String(h)}>{junkHours(h)}</Option>)}
        </Dropdown>
      </Field>
      <Switch label={t('Archivieren lernt die Mail als „Kein Junk“')} checked={st.learnArchive ?? true} disabled={!st.enabled}
        onChange={(_, d) => save({ learnArchive: d.checked })} />
      {featureOn('junkread') && <>
        <Switch label={t('Aussortierte Junk-Mails als gelesen markieren')} checked={st.junkMarkRead ?? false} disabled={!st.enabled}
          onChange={(_, d) => save({ junkMarkRead: d.checked })} />
        <Switch label={t('Mails, die Junk verlassen, als ungelesen markieren')} checked={st.junkRestoreUnread ?? false} disabled={!st.enabled}
          onChange={(_, d) => save({ junkRestoreUnread: d.checked })} />
      </>}
      {data.sharedJunk && <>
        <Switch label={t('Beim gemeinsamen Junk-Wissen mitmachen')} checked={st.shareJunk ?? true} disabled={!st.enabled}
          onChange={(_, d) => save({ shareJunk: d.checked })} />
        <Text size={200} className={s.muted}>
          {t('Klickt jemand auf diesem Server „Junk“, merkt sich dmOffice nur unlesbare Prüfsummen der Mail. Kommt dieselbe Mail bei anderen an, landet sie dort schneller in Junk. „Kein Junk“ nimmt die eigene Meldung zurück.')}
        </Text>
      </>}
      <Text size={200} className={s.muted}>{t('„Aus Ordnern lernen“: Junk-E-Mail als Junk, Posteingang und Gesendet als gut – nur Mails, die schon vollständig hier liegen.')}</Text>
      </Section>
      <Section title={t('Grenzwerte')} last>
      <Field label={t('Ab {n} in Junk-E-Mail verschieben', { n: Math.round(st.spamCutoff * 100) })}>
        <Slider min={50} max={99} value={Math.round(st.spamCutoff * 100)} onChange={(_, d) => save({ spamCutoff: d.value / 100 })} />
      </Field>
      <Field label={t('Ab {n} als „unsicher“ markieren', { n: Math.round(st.hamCutoff * 100) })}>
        <Slider min={5} max={Math.round(st.spamCutoff * 100) - 1} value={Math.round(st.hamCutoff * 100)} onChange={(_, d) => save({ hamCutoff: d.value / 100 })} />
      </Field>
      </Section>
      </>}
      {tab === 'lists' && <Section title={t('Spamlisten')} last>
      <Text className={s.muted}>{t('Öffentliche Sperrlisten bekannter Spam-Server und -Domains. Ein Treffer fließt in die Bewertung ein; gefragt wird nur nach Adresse und Domain.')}</Text>
      {(data.lists as { id: string; name: string; kind: string; spamhaus: boolean }[]).map(l => {
        const on = (st.lists ?? []).includes(l.id)
        const junk = (st.listsJunk ?? []).includes(l.id)
        return (
          <div key={l.id} style={{ display: 'flex', alignItems: 'center', gap: 12, flexWrap: 'wrap' }}>
            <Checkbox disabled={!st.enabled} checked={on} style={{ minWidth: 220 }}
              label={<>{l.name} <span className={s.muted}>· {l.kind === 'ip' ? t('Absender-Server') : t('Domains und Links')}</span></>}
              onChange={(_, d) => save({ lists: d.checked ? [...(st.lists ?? []), l.id] : (st.lists ?? []).filter((x: string) => x !== l.id), listsJunk: d.checked ? st.listsJunk ?? [] : (st.listsJunk ?? []).filter((x: string) => x !== l.id) })} />
            <Checkbox disabled={!st.enabled || !on} checked={on && junk} label={t('Treffer direkt in Junk')}
              onChange={(_, d) => save({ listsJunk: d.checked ? [...(st.listsJunk ?? []), l.id] : (st.listsJunk ?? []).filter((x: string) => x !== l.id) })} />
            {l.spamhaus && on && !data.spamhausKey && <Text size={200} className={s.muted}>{t('Ohne Zugangsschlüssel in der Verwaltung oft ohne Antwort.')}</Text>}
          </div>
        )
      })}
      </Section>}
      {tab === 'images' && <TrustedImages />}
    </>
  )
}

const junkHours = (h: number) => h <= 0 ? t('Nie') : h === 6 ? t('Alle 6 Stunden') : t('Einmal am Tag')

/**
 * Liste von Absendern (Adresse oder @Domain) als Chips mit × - gleiche Bedienung für alle solchen Listen. Mit onAdd: Eingabefeld
 * und „Hinzufügen“ darüber; was man eintippt, filtert zugleich die Chips (lange Listen).
 */
function SenderChips({ list, onRemove, onAdd, empty }: { list: string[]; onRemove: (entry: string) => void; onAdd?: (entry: string) => Promise<boolean>; empty?: string }) {
  const s = useStyles()
  const [entry, setEntry] = useState('')
  const q = entry.trim().toLowerCase()
  const shown = q ? list.filter(e => e.toLowerCase().includes(q)) : list
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      {onAdd && <form style={{ display: 'flex', gap: 8, maxWidth: 480 }} onSubmit={async e => { e.preventDefault(); if (await onAdd(entry.trim())) setEntry('') }}>
        <Input style={{ flex: 1 }} placeholder={t('adresse@beispiel.de oder @beispiel.de')} value={entry} onChange={(_, d) => setEntry(d.value)} />
        <Button type="submit" disabled={!entry.includes('@')}>{t('Hinzufügen')}</Button>
      </form>}
      {list.length === 0 ? <Text size={200} className={s.muted}>{empty ?? t('Noch keine.')}</Text>
        : <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          {shown.map(e => (
            <Badge key={e} size="large" appearance="outline" shape="rounded">
              {e}
              <Button appearance="transparent" size="small" className={s.muted} aria-label={t('„{entry}“ entfernen', { entry: e })} onClick={() => onRemove(e)}>×</Button>
            </Badge>
          ))}
        </div>}
    </div>
  )
}

/** Selbst eingetragene sichere Absender (nach „Kein Junk“ angeboten) - gelten nur für Mails, die der Mailserver als echt bestätigt hat. */
function SafeSenders({ list, onChange }: { list: string[]; onChange: () => void }) {
  const { fail } = useApp()
  if (list.length === 0) return null
  const remove = async (entry: string) => {
    try { await api.del(`/api/settings/spam/safe?entry=${encodeURIComponent(entry)}`); onChange() } catch (e) { fail(e) }
  }
  return (
    <Field label={t('Selbst eingetragene sichere Absender')}
      hint={t('Gelten nur, wenn der Mailserver bestätigt hat, dass die Mail wirklich vom Absender kommt – eine gefälschte Absenderadresse bekommt das Vertrauen nicht.')}>
      <SenderChips list={list} onRemove={remove} />
    </Field>
  )
}

/** Absender und Domains, deren Bilder aus dem Netz immer geladen werden. */
function TrustedImages() {
  const { fail } = useApp()
  const [list, setList] = useState<string[]>([])
  const load = () => api.get<string[]>('/api/settings/trusted-images').then(setList).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const add = async (entry: string) => {
    try { await api.post('/api/settings/trusted-images', { entry }); clearAll(); load(); return true } catch (e) { fail(e); return false }
  }
  const remove = async (e: string) => {
    try { await api.post('/api/settings/trusted-images/remove', { entry: e }); clearAll(); load() } catch (err) { fail(err) }
  }
  return (
    <Section title={t('Sichere Absender für Bilder')} hint={t('Bei diesen Absendern lädt dmOffice Bilder aus dem Internet ohne Nachfrage. Hinzufügen geht auch direkt in einer Mail über „Bilder anzeigen ▾“.')} last>
      <SenderChips list={list} onRemove={remove} onAdd={add} />
    </Section>
  )
}
