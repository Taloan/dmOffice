import {
  Button, Card, Dropdown, Field, Input, Option, Slider, Switch, Text, makeStyles, tokens, Badge, MessageBar, MessageBarBody,
} from '@fluentui/react-components'
import { Add20Regular, Delete20Regular, ArrowUp20Regular, ArrowDown20Regular, Dismiss16Regular, Add16Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { Account, api, Folder } from '../api'
import { useApp } from '../App'
import { CategoryDot, useCategories } from '../mail/Categories'
import { newId } from '../shell/browser'
import { t } from '../i18n'

const useStyles = makeStyles({
  rule: { padding: '14px 16px', display: 'flex', flexDirection: 'column', gap: '10px' },
  line: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  head: { display: 'flex', gap: '8px', alignItems: 'center' },
  muted: { color: tokens.colorNeutralForeground3 },
})

interface Condition { field: string; op: string; value: string }
interface Action { type: string; value: string }
interface Rule { id: string; name: string; enabled: boolean; accountId: string | null; match: 'all' | 'any'; conditions: Condition[]; actions: Action[] }

const fields = [['from', t('Von')], ['to', t('An/Cc')], ['subject', t('Betreff')], ['body', t('Text')], ['any', t('irgendwo')], ['header:List-Id', t('Kopfzeile List-Id')]]
const ops = [['contains', t('enthält')], ['notContains', t('enthält nicht')], ['equals', t('ist genau')], ['startsWith', t('beginnt mit')], ['endsWith', t('endet mit')], ['regex', t('passt auf (Regex)')]]
const actions = [['move', t('Verschieben nach')], ['copy', t('Kopieren nach')], ['forward', t('Weiterleiten an')], ['markRead', t('Als gelesen markieren')],
  ['flag', t('Kennzeichnen')], ['category', t('Kategorie zuweisen')], ['delete', t('Löschen (Papierkorb)')], ['stop', t('Keine weiteren Regeln')]]
const label = (list: string[][], k: string) => list.find(x => x[0] === k)?.[1] ?? k

export function RulesSettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const categories = useCategories()
  const [rules, setRules] = useState<Rule[] | null>(null)
  const [accounts, setAccounts] = useState<Account[]>([])
  const [folders, setFolders] = useState<Record<string, Folder[]>>({})
  const [dirty, setDirty] = useState(false)

  useEffect(() => {
    api.get<Rule[]>('/api/settings/rules').then(setRules).catch(fail)
    api.get<Account[]>('/api/mail/accounts').then(async list => {
      setAccounts(list)
      const entries = await Promise.all(list.map(async a => [a.id, await api.get<Folder[]>(`/api/mail/${a.id}/folders`).catch(() => [])] as const))
      setFolders(Object.fromEntries(entries))
    }).catch(() => {})
  }, [fail])

  if (!rules) return null
  const update = (i: number, p: Partial<Rule>) => { setRules(rules.map((r, j) => (j === i ? { ...r, ...p } : r))); setDirty(true) }
  const save = async () => {
    try { await api.put('/api/settings/rules', rules); setDirty(false); notify(t('Regeln gespeichert')) } catch (e) { fail(e) }
  }
  const add = () => {
    setRules([...rules, { id: newId(), name: t('Neue Regel'), enabled: true, accountId: null, match: 'all',
      conditions: [{ field: 'from', op: 'contains', value: '' }], actions: [{ type: 'move', value: '' }] }])
    setDirty(true)
  }
  const move = (i: number, d: number) => {
    const j = i + d
    if (j < 0 || j >= rules.length) return
    const next = [...rules];[next[i], next[j]] = [next[j], next[i]]
    setRules(next); setDirty(true)
  }

  return (
    <>
      <Text size={600} weight="semibold">{t('Regeln')}</Text>
      <Text className={s.muted}>
        {t('Regeln laufen auf dem Server für jede neue Mail im Posteingang – auch wenn kein Browser offen ist. Von oben nach unten; „Keine weiteren Regeln“ beendet. Alte Mails bleiben unberührt.')}
      </Text>
      {rules.map((r, i) => {
        const folderList = (r.accountId ? folders[r.accountId] : folders[accounts[0]?.id]) ?? []
        return (
          <Card key={r.id} className={s.rule}>
            <div className={s.head}>
              <Switch checked={r.enabled} onChange={(_, d) => update(i, { enabled: d.checked })} />
              <Input value={r.name} onChange={(_, d) => update(i, { name: d.value })} style={{ flex: 1 }} appearance="underline" />
              <Dropdown style={{ minWidth: 180 }} value={r.accountId ? accounts.find(a => a.id === r.accountId)?.name ?? '' : t('Alle Konten')}
                selectedOptions={[r.accountId ?? '']} onOptionSelect={(_, d) => update(i, { accountId: d.optionValue || null })}>
                <Option value="">{t('Alle Konten')}</Option>
                {accounts.map(a => <Option key={a.id} value={a.id}>{a.name}</Option>)}
              </Dropdown>
              <Button appearance="subtle" icon={<ArrowUp20Regular />} onClick={() => move(i, -1)} aria-label={t('Nach oben')} title={t('Nach oben')} />
              <Button appearance="subtle" icon={<ArrowDown20Regular />} onClick={() => move(i, 1)} aria-label={t('Nach unten')} title={t('Nach unten')} />
              <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => { setRules(rules.filter((_, j) => j !== i)); setDirty(true) }} aria-label={t('Regel löschen')} title={t('Regel löschen')} />
            </div>
            <div className={s.line}>
              <Text>{t('Wenn')}</Text>
              <Dropdown style={{ minWidth: 90 }} value={r.match === 'all' ? t('alle') : t('eine')} selectedOptions={[r.match]} onOptionSelect={(_, d) => update(i, { match: d.optionValue as any })}>
                <Option value="all">{t('alle')}</Option><Option value="any">{t('eine')}</Option>
              </Dropdown>
              <Text>{t('der Bedingungen zutreffen:')}</Text>
            </div>
            {r.conditions.map((c, ci) => {
              const setC = (p: Partial<Condition>) => update(i, { conditions: r.conditions.map((x, k) => (k === ci ? { ...x, ...p } : x)) })
              return (
                <div key={ci} className={s.line}>
                  <Dropdown style={{ minWidth: 150 }} value={label(fields, c.field)} selectedOptions={[c.field]} onOptionSelect={(_, d) => setC({ field: d.optionValue! })}>
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
            <div><Button size="small" appearance="subtle" icon={<Add16Regular />} onClick={() => update(i, { conditions: [...r.conditions, { field: 'subject', op: 'contains', value: '' }] })}>{t('Bedingung')}</Button></div>
            <Text>{t('dann:')}</Text>
            {r.actions.map((a, ai) => {
              const setA = (p: Partial<Action>) => update(i, { actions: r.actions.map((x, k) => (k === ai ? { ...x, ...p } : x)) })
              return (
                <div key={ai} className={s.line}>
                  <Dropdown style={{ minWidth: 200 }} value={label(actions, a.type)} selectedOptions={[a.type]} onOptionSelect={(_, d) => setA({ type: d.optionValue!, value: '' })}>
                    {actions.map(([k, l]) => <Option key={k} value={k}>{l}</Option>)}
                  </Dropdown>
                  {(a.type === 'move' || a.type === 'copy') && (
                    <Dropdown style={{ minWidth: 240 }} value={a.value} selectedOptions={[a.value]} onOptionSelect={(_, d) => setA({ value: d.optionValue! })} placeholder={t('Ordner wählen')}>
                      {folderList.filter(f => f.selectable).map(f => <Option key={f.path} value={f.path}>{f.path}</Option>)}
                    </Dropdown>
                  )}
                  {a.type === 'category' && (
                    <Dropdown style={{ minWidth: 240 }} value={categories.find(c => c.key === a.value)?.name ?? ''} selectedOptions={[a.value]}
                      onOptionSelect={(_, d) => setA({ value: d.optionValue! })} placeholder={t('Kategorie wählen')}>
                      {categories.map(c => <Option key={c.key} value={c.key} text={c.name}><CategoryDot color={c.color} /> {c.name}</Option>)}
                    </Dropdown>
                  )}
                  {a.type === 'forward' && <Input style={{ flex: 1, minWidth: 240 }} placeholder={t('adresse@beispiel.de, …')} value={a.value} onChange={(_, d) => setA({ value: d.value })} />}
                  <Button appearance="subtle" icon={<Dismiss16Regular />} onClick={() => update(i, { actions: r.actions.filter((_, k) => k !== ai) })} aria-label={t('Entfernen')} />
                </div>
              )
            })}
            <div><Button size="small" appearance="subtle" icon={<Add16Regular />} onClick={() => update(i, { actions: [...r.actions, { type: 'markRead', value: '' }] })}>{t('Aktion')}</Button></div>
            {!r.accountId && accounts.length > 1 && r.actions.some(a => a.type === 'move' || a.type === 'copy') && (
              <Text size={200} className={s.muted}>{t('Ordner gelten je Konto – bei „Alle Konten“ muss der Ordner in jedem Konto so heißen.')}</Text>
            )}
          </Card>
        )
      })}
      <div style={{ display: 'flex', gap: 8 }}>
        <Button icon={<Add20Regular />} onClick={add}>{t('Regel hinzufügen')}</Button>
        <Button appearance="primary" disabled={!dirty} onClick={save}>{t('Speichern')}</Button>
      </div>
    </>
  )
}

export function SpamSettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [data, setData] = useState<any>(null)
  const load = () => api.get('/api/settings/spam').then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
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
    if (!confirm(t('Alles Gelernte vergessen?'))) return
    try { await api.post('/api/settings/spam/reset'); load() } catch (e) { fail(e) }
  }
  const ready = data.trainedSpam >= st.minTrained && data.trainedHam >= st.minTrained
  return (
    <>
      <Text size={600} weight="semibold">{t('Junk-E-Mail')}</Text>
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
        {t('Sicher ist, wer im Adressbuch steht oder eine Mail von dir bekommen hat (Gesendet-Ordner aller Konten). Ihre Mails kommen nie in Junk-E-Mail und werden auf Wunsch als gut gelernt. Deine eigenen Adressen zählen nicht – Junk mit gefälschtem eigenem Absender ist häufig.')}
      </Text>
      <Field label={t('Ab {n} in Junk-E-Mail verschieben', { n: Math.round(st.spamCutoff * 100) })}>
        <Slider min={50} max={99} value={Math.round(st.spamCutoff * 100)} onChange={(_, d) => save({ spamCutoff: d.value / 100 })} />
      </Field>
      <Field label={t('Ab {n} als „unsicher“ markieren', { n: Math.round(st.hamCutoff * 100) })}>
        <Slider min={5} max={Math.round(st.spamCutoff * 100) - 1} value={Math.round(st.hamCutoff * 100)} onChange={(_, d) => save({ hamCutoff: d.value / 100 })} />
      </Field>
      <div style={{ display: 'flex', gap: 8 }}>
        <Button appearance="primary" onClick={train}>{t('Aus Ordnern lernen')}</Button>
        <Button onClick={reset}>{t('Gelerntes vergessen')}</Button>
      </div>
      <Text size={200} className={s.muted}>{t('„Aus Ordnern lernen“: Junk-E-Mail als Junk, Posteingang und Gesendet als gut – nur Mails, die schon vollständig hier liegen.')}</Text>
      <TrustedImages />
    </>
  )
}

/** Absender und Domains, deren Bilder aus dem Netz immer geladen werden. */
function TrustedImages() {
  const s = useStyles()
  const { fail } = useApp()
  const [list, setList] = useState<string[]>([])
  const [entry, setEntry] = useState('')
  const load = () => api.get<string[]>('/api/settings/trusted-images').then(setList).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const add = async () => {
    try { await api.post('/api/settings/trusted-images', { entry }); setEntry(''); load() } catch (e) { fail(e) }
  }
  const remove = async (e: string) => {
    try { await api.post('/api/settings/trusted-images/remove', { entry: e }); load() } catch (err) { fail(err) }
  }
  return (
    <>
      <Text weight="semibold" size={400} style={{ marginTop: 16 }}>{t('Sichere Absender für Bilder')}</Text>
      <Text className={s.muted}>{t('Bei diesen Absendern lädt dmOffice Bilder aus dem Internet ohne Nachfrage. Hinzufügen geht auch direkt in einer Mail über „Bilder anzeigen ▾“.')}</Text>
      <form style={{ display: 'flex', gap: 8, maxWidth: 480 }} onSubmit={e => { e.preventDefault(); add() }}>
        <Input style={{ flex: 1 }} placeholder={t('adresse@beispiel.de oder @beispiel.de')} value={entry} onChange={(_, d) => setEntry(d.value)} />
        <Button type="submit" disabled={!entry.includes('@')}>{t('Hinzufügen')}</Button>
      </form>
      {list.length === 0 ? <Text size={200} className={s.muted}>{t('Noch keine.')}</Text> : list.map(e => (
        <div key={e} className={s.line} style={{ maxWidth: 480 }}>
          <Text style={{ flex: 1 }}>{e}</Text>
          <Button appearance="subtle" icon={<Dismiss16Regular />} onClick={() => remove(e)} aria-label={t('Entfernen')} />
        </div>
      ))}
    </>
  )
}
