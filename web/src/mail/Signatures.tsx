import { Button, Dropdown, Field, Input, Option, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Add20Regular, Delete20Regular, Signature20Regular } from '@fluentui/react-icons'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { useEffect, useRef, useState } from 'react'
import { api, type Account } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { askConfirm, askText } from '../shell/context'
import { RichEditor, type RichEditorHandle } from '../shell/RichEditor'
import { ExplorerTable } from '../sdk'
import { Hint, Section } from '../settings/parts'
import { siteList, type AliasState } from './AliasDialog'

/** Signatur mit Namen und Zuordnung (Mail/MailSignatures.cs). */
export interface Signature { id: string; name: string; html: string; accounts: string[]; domains: string[]; aliases: string[] }

export const SIGNATURES_CHANGED = 'dmoffice:signatures'

/** Signaturen laden (neu nach SIGNATURES_CHANGED); null solange unterwegs. */
export function useSignatures(): Signature[] | null {
  const [list, setList] = useState<Signature[] | null>(null)
  useEffect(() => {
    const load = () => { api.get<Signature[]>('/api/mail/signatures').then(setList).catch(() => setList([])) }
    load()
    window.addEventListener(SIGNATURES_CHANGED, load)
    return () => window.removeEventListener(SIGNATURES_CHANGED, load)
  }, [])
  return list
}

/** Welche Signatur gilt? Alias vor Domain vor Konto (wie der Server); null: keine zugeordnet - dann die des Mailkontos. */
export function resolveSignature(list: Signature[], account: string, from?: string | null): Signature | null {
  const a = from?.trim().toLowerCase()
  if (a) {
    const byAlias = list.find(s => s.aliases.includes(a))
    if (byAlias) return byAlias
    const byDomain = list.find(s => s.domains.includes(a.slice(a.lastIndexOf('@') + 1)))
    if (byDomain) return byDomain
  }
  return list.find(s => s.accounts.includes(account)) ?? null
}

/** Auswahl im Verfassen: „auto“ (wechselt mit dem Absender), eine Signatur oder „keine“. */
export type SignatureChoice = { kind: 'auto' } | { kind: 'none' } | { kind: 'sig'; id: string }

/** Feld „Signatur“ im Verfassen-Fenster: zeigt die gerade geltende, von Hand umstellbar. */
export function SignaturePicker({ list, current, onPick }: {
  list: Signature[]
  /** Gerade eingesetzte Signatur: Name oder null für „Signatur des Kontos“, '' für keine */
  current: string | null
  onPick: (c: SignatureChoice) => void
}) {
  const text = current === '' ? t('Keine') : current ?? t('Signatur des Kontos')
  return (
    <Dropdown appearance="underline" value={text} selectedOptions={[]} style={{ minWidth: 200 }} aria-label={t('Signatur')}
      onOptionSelect={(_, d) => onPick(d.optionValue === '__auto' ? { kind: 'auto' } : d.optionValue === '__none' ? { kind: 'none' } : { kind: 'sig', id: d.optionValue! })}>
      <Option value="__auto" text={t('Passend zum Absender')}>{t('Passend zum Absender')}</Option>
      {list.map(s => <Option key={s.id} value={s.id} text={s.name}>{s.name}</Option>)}
      <Option value="__none" text={t('Keine')}>{t('Keine')}</Option>
    </Dropdown>
  )
}

/** Signatur im Text des Editors ersetzen (Block .dm-signature); fehlt er, kommt er hinter die erste Zeile, vor das Zitat. */
export function putSignature(area: HTMLElement | null | undefined, html: string) {
  if (!area) return
  let box = area.querySelector<HTMLElement>('div.dm-signature')
  if (!html) {
    // Leerzeile vor der Signatur gehört zu ihr
    if (box && box.previousSibling?.nodeName === 'BR') box.previousSibling.remove()
    box?.remove()
    return
  }
  if (!box) {
    box = document.createElement('div')
    box.className = 'dm-signature'
    const first = area.firstElementChild
    if (first) { first.after(box); box.before(document.createElement('br')) } else area.append(box)
  }
  box.innerHTML = html
}

const useStyles = makeStyles({
  md: { display: 'flex', gap: '16px', alignItems: 'flex-start', flexWrap: 'wrap' },
  left: { flex: '0 1 220px', minWidth: '180px', display: 'flex', flexDirection: 'column', gap: '8px' },
  right: { flex: '1 1 420px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: '10px' },
  list: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, overflow: 'hidden' },
  li: {
    display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: '8px', padding: '8px 10px', cursor: 'pointer',
    borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, ':last-child': { borderBottom: 'none' }, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  sel: { backgroundColor: tokens.colorNeutralBackground1Selected, boxShadow: `inset 3px 0 0 ${tokens.colorBrandStroke1}` },
  muted: { color: tokens.colorNeutralForeground3 },
  editor: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, minHeight: '140px' },
})

/** Eine Zeile der Zuordnung: Absender (Konto, Domain oder Alias) → Signatur. */
interface Assign { key: string; kind: 'account' | 'domain' | 'alias'; label: string; sig: string | null }

/**
 * Einstellungen → E-Mail → Signaturen (Kennung mailsignatures): Signaturen mit Namen anlegen und Absendern zuordnen. Beim Schreiben
 * gilt die Signatur des Alias, sonst die der Domain, sonst die des Kontos; ohne Zuordnung die Signatur aus dem Mailkonto.
 */
export function SignatureSettings() {
  const { fail } = useApp()
  const s = useStyles()
  const list = useSignatures()
  const [accounts, setAccounts] = useState<Account[]>([])
  const [domains, setDomains] = useState<string[]>([])
  const [sel, setSel] = useState<string | null>(null)
  const [name, setName] = useState('')
  const editor = useRef<RichEditorHandle>(null)
  const timer = useRef<number>()
  useEffect(() => {
    api.get<Account[]>('/api/mail/accounts').then(setAccounts).catch(() => {})
    api.get<AliasState>('/api/mail/aliases').then(st => setDomains(siteList(st).map(d => d.domain))).catch(() => {})
  }, [])
  const cur = list?.find(x => x.id === sel) ?? list?.[0] ?? null
  useEffect(() => { setName(cur?.name ?? '') }, [cur?.id]) // eslint-disable-line react-hooks/exhaustive-deps
  if (!list) return null

  const changed = () => window.dispatchEvent(new Event(SIGNATURES_CHANGED))
  const put = async (x: Signature, change: Partial<Signature>) => {
    const body = { ...x, ...change }
    try { await api.put(`/api/mail/signatures/${x.id}`, { name: body.name, html: body.html, accounts: body.accounts, domains: body.domains, aliases: body.aliases }); changed() }
    catch (e) { fail(e) }
  }
  const create = async () => {
    const n = await askText(t('Neue Signatur'), '', t('Name'))
    if (!n?.trim()) return
    try { const x = await api.post<Signature>('/api/mail/signatures', { name: n.trim(), html: '', accounts: [], domains: [], aliases: [] }); setSel(x.id); changed() }
    catch (e) { fail(e) }
  }
  const remove = async () => {
    if (!cur || !await askConfirm(t('{n} löschen?', { n: cur.name }), t('Absender mit dieser Signatur bekommen wieder die Signatur ihres Mailkontos.'), t('Löschen'))) return
    try { await api.del(`/api/mail/signatures/${cur.id}`); setSel(null); changed() } catch (e) { fail(e) }
  }
  // Text: kurz nach der letzten Änderung speichern
  const onText = () => {
    window.clearTimeout(timer.current)
    const x = cur
    timer.current = window.setTimeout(() => { if (x) put(x, { html: editor.current?.html() ?? '' }) }, 800)
  }

  // Zuordnung: alle Mailkonten und Domains, dazu einzeln zugeordnete Aliase
  const owner = (pick: (x: Signature) => string[], key: string) => list.find(x => pick(x).includes(key))?.id ?? null
  const extraAliases = [...new Set(list.flatMap(x => x.aliases))]
  const rows: Assign[] = [
    ...accounts.map(a => ({ key: 'account:' + a.id, kind: 'account' as const, label: a.email || a.name, sig: owner(x => x.accounts, a.id) })),
    ...domains.map(d => ({ key: 'domain:' + d, kind: 'domain' as const, label: '@' + d, sig: owner(x => x.domains, d) })),
    ...extraAliases.map(a => ({ key: 'alias:' + a, kind: 'alias' as const, label: a, sig: owner(x => x.aliases, a) })),
  ]
  const field = (k: Assign['kind']) => (k === 'account' ? 'accounts' : k === 'domain' ? 'domains' : 'aliases') as 'accounts' | 'domains' | 'aliases'
  const assign = async (r: Assign, to: string | null) => {
    const value = r.key.slice(r.key.indexOf(':') + 1), f = field(r.kind)
    const old = list.find(x => x.id === r.sig)
    // Neue Signatur nimmt die Zuordnung (der Server nimmt sie der alten weg); „keine“: bei der alten entfernen
    if (to) { const x = list.find(y => y.id === to); if (x) await put(x, { [f]: [...x[f], value] }) }
    else if (old) await put(old, { [f]: old[f].filter(v => v !== value) })
  }
  const addAlias = async () => {
    const a = (await askText(t('Zuordnung für einen Alias'), '', t('Alias')))?.trim().toLowerCase()
    if (!a || !a.includes('@') || !cur) return
    await put(cur, { aliases: [...cur.aliases, a] })
  }
  const kindText = (k: Assign['kind']) => k === 'account' ? t('Konto') : k === 'domain' ? t('Domain') : t('Alias')

  return (
    <>
      <Hint>{t('Signaturen mit Namen anlegen und Absendern zuordnen. Beim Schreiben gilt die Signatur des Alias, sonst die der Domain, sonst die des Kontos. Ohne Zuordnung gilt die Signatur aus den Einstellungen des Mailkontos.')}</Hint>
      <div className={s.md}>
        <div className={s.left}>
          <RibbonActions><RibbonButton primary icon={<Add20Regular />} label={t('Neue Signatur')} onClick={create} /></RibbonActions>
          {list.length > 0 && <div className={s.list} role="listbox" aria-label={t('Signaturen')}>
            {list.map(x => {
              const n = x.accounts.length + x.domains.length + x.aliases.length
              return <div key={x.id} role="option" aria-selected={x.id === cur?.id} className={mergeClasses(s.li, x.id === cur?.id && s.sel)} onClick={() => setSel(x.id)}>
                <Text weight={x.id === cur?.id ? 'semibold' : 'regular'} truncate>{x.name}</Text>
                <Text size={200} className={s.muted}>{n === 0 ? '–' : n === 1 ? t('1 Zuordnung') : t('{n} Zuordnungen', { n })}</Text>
              </div>
            })}
          </div>}
        </div>
        <div className={s.right}>
          {!cur ? <Hint>{t('Noch keine Signatur.')}</Hint> : <>
            <div style={{ display: 'flex', gap: 8, alignItems: 'flex-end', flexWrap: 'wrap' }}>
              <Field label={t('Name')} style={{ flex: 1, minWidth: 200 }}>
                <Input value={name} onChange={(_, d) => setName(d.value)} onBlur={() => { if (name.trim() && name !== cur.name) put(cur, { name: name.trim() }) }} />
              </Field>
              <Button appearance="subtle" icon={<Delete20Regular />} onClick={remove}>{t('Löschen …')}</Button>
            </div>
            <div className={s.editor}><RichEditor key={cur.id} ref={editor} initial={cur.html} onChange={onText} /></div>
          </>}
        </div>
      </div>
      <Section title={t('Zuordnung')} last hint={t('Jeder Absender hat höchstens eine Signatur. Einzelne Aliase gehen vor ihrer Domain, Domains vor dem Konto.')}>
        {rows.length === 0 ? <Hint>{t('Noch kein E-Mail-Konto eingerichtet.')}</Hint>
          : <ExplorerTable<Assign> id="mail.signatures.assign" rows={rows} rowKey={r => r.key}
            cols={[
              { key: 'who', label: () => t('Absender'), width: 260, sort: r => r.label, render: r => <span style={{ display: 'inline-flex', gap: 6, alignItems: 'center' }}><Signature20Regular />{r.label}</span> },
              { key: 'kind', label: () => t('Art'), width: 100, sort: r => kindText(r.kind), render: r => kindText(r.kind) },
              { key: 'sig', label: () => t('Signatur'), width: 0, sort: r => list.find(x => x.id === r.sig)?.name ?? '', render: r =>
                <Dropdown size="small" value={list.find(x => x.id === r.sig)?.name ?? t('keine')} selectedOptions={[r.sig ?? '']}
                  onOptionSelect={(_, d) => assign(r, d.optionValue || null)} style={{ minWidth: 160 }}>
                  <Option value="" text={t('keine')}>{t('keine')}</Option>
                  {list.map(x => <Option key={x.id} value={x.id} text={x.name}>{x.name}</Option>)}
                </Dropdown> },
            ]} />}
        {cur && <div><Button icon={<Add20Regular />} onClick={addAlias}>{t('Zuordnung für einen Alias …')}</Button></div>}
      </Section>
    </>
  )
}
