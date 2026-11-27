import { Button, Combobox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Option, OptionGroup, Text } from '@fluentui/react-components'
import { useEffect, useMemo, useState } from 'react'
import { api, ApiError, type Account } from '../api'
import { t } from '../i18n'
import { siteList } from './AliasDialog'
import { useCatchall } from './CatchallFolders'

/** Ein möglicher Absender: Mailkonto selbst (alias undefined) oder ein Alias, gesendet aus account. */
export interface SenderChoice { account: string; alias?: string }

interface Option_ { key: string; account: string; alias?: string; text: string; group: 'accounts' | 'aliases' }

/** Höchstens so viele Einträge in der Liste; Eintippen filtert über alle. */
const MAX = 250

/**
 * „Von“ beim Schreiben (Kennung catchalldomains): Mailkonten und alle Aliase - angelegte und nicht angelegte Catchall-Aliase, ohne
 * gesperrte (/api/mail/aliases/senders), dazu die Aliase verwalteter Postfächer. Eintippen filtert; ein getippter Alias einer
 * eingetragenen Domain gilt auch, wenn er noch keine Post bekommen hat. Ein Alias sendet aus dem Postfach seiner Domain.
 */
export function SenderPicker({ accounts, value, onChange }: { accounts: Account[]; value: SenderChoice; onChange: (v: SenderChoice) => void }) {
  const st = useCatchall()
  const [senders, setSenders] = useState<{ address: string; state: string; account: string | null }[]>([])
  const [q, setQ] = useState<string | null>(null)
  useEffect(() => { api.get<typeof senders>('/api/mail/aliases/senders').then(setSenders).catch(() => {}) }, [])

  const options = useMemo<Option_[]>(() => {
    const nameOf = (id: string) => { const a = accounts.find(x => x.id === id); return a?.displayName || a?.name || '' }
    const list: Option_[] = accounts.map(a => ({ key: a.id, account: a.id, text: `${a.displayName || a.name} <${a.email}>`, group: 'accounts' }))
    // Aliase verwalteter Postfächer (Kennung mailserver), falls vorhanden
    for (const a of accounts) for (const x of (a as { aliases?: string[] | null }).aliases ?? [])
      list.push({ key: `${a.id}|${x}`, account: a.id, alias: x, text: `${a.displayName || a.name} <${x}>`, group: 'aliases' })
    for (const x of senders) if (x.account && accounts.some(a => a.id === x.account))
      list.push({ key: `${x.account}|${x.address}`, account: x.account, alias: x.address, text: `${nameOf(x.account)} <${x.address}>`, group: 'aliases' })
    return list
  }, [accounts, senders])

  const current = value.alias ? `${value.account}|${value.alias}` : value.account
  const currentText = options.find(o => o.key === current)?.text
    ?? (value.alias ? `<${value.alias}>` : '')
  const needle = (q ?? '').trim().toLowerCase()
  const shown = needle ? options.filter(o => o.text.toLowerCase().includes(needle)) : options
  // Getippter Alias einer eingetragenen Domain (noch ohne Post): aus dem Postfach der Domain, gesperrte nicht
  const typed = useMemo<Option_ | null>(() => {
    if (!st || !/^[^@\s<>]+@[^@\s<>]+\.[^@\s<>]+$/.test(needle) || options.some(o => o.alias === needle)) return null
    const site = siteList(st).find(d => needle.endsWith('@' + d.domain))
    if (!site?.account || st.items.some(i => i.address === needle && i.blocked)) return null
    const a = accounts.find(x => x.id === site.account)
    return a ? { key: `${a.id}|${needle}`, account: a.id, alias: needle, text: `${a.displayName || a.name} <${needle}>`, group: 'aliases' } : null
  }, [st, needle, options, accounts])

  const pick = (key?: string) => {
    setQ(null)
    const o = key === typed?.key ? typed : options.find(x => x.key === key)
    if (o) onChange({ account: o.account, alias: o.alias })
  }
  const group = (g: Option_['group']) => shown.filter(o => o.group === g)
  return (
    <Combobox freeform appearance="underline" style={{ minWidth: 'min(360px, 60vw)' }} aria-label={t('Von')} placeholder={t('Absender eintippen …')}
      value={q ?? currentText} selectedOptions={[current]}
      onInput={e => setQ((e.target as HTMLInputElement).value)}
      onOpenChange={(_, d) => { if (!d.open) setQ(null) }}
      onOptionSelect={(_, d) => pick(d.optionValue)}>
      {group('accounts').length > 0 && <OptionGroup label={t('Mailkonten')}>
        {group('accounts').map(o => <Option key={o.key} value={o.key} text={o.text}>{o.text}</Option>)}
      </OptionGroup>}
      {(group('aliases').length > 0 || typed) && <OptionGroup label={t('Aliase')}>
        {typed && <Option key={typed.key} value={typed.key} text={typed.text}>{typed.text}</Option>}
        {group('aliases').slice(0, MAX).map(o => <Option key={o.key} value={o.key} text={o.text}>{o.text}</Option>)}
      </OptionGroup>}
      {shown.length === 0 && !typed && <Option disabled value="" text="">{t('Kein Absender gefunden.')}</Option>}
    </Combobox>
  )
}

/** Antwort des Servers, wenn der Mailserver den Alias als Absender ablehnt (Mail/MailApi.cs, /send, 409 „sender-rejected“). */
export interface SenderRejected { from: string; domain: string; account: string; email: string; error: string }

export function senderRejectedOf(e: unknown): SenderRejected | null {
  if (!(e instanceof ApiError) || e.status !== 409) return null
  const d = e.data as (SenderRejected & { code?: string }) | undefined
  return d?.code === 'sender-rejected' ? d : null
}

/**
 * Der Mailserver hat den Alias als Absender abgelehnt: mit dem Postfach senden (einmal) oder für die ganze Domain so einstellen
 * („Antworten von“). Der Entwurf bleibt in beiden Fällen offen, bis das Senden klappt.
 */
export function SenderRejectedDialog({ info, onAccount, onClose }: {
  info: SenderRejected
  /** Mit dem Postfach senden; always: für diese Domain immer so */
  onAccount: (always: boolean) => void
  onClose: () => void
}) {
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{t('Absender abgelehnt')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <Text>{t('Der Mailserver nimmt {a} nicht als Absender an. Die Mail ist nicht gesendet, der Entwurf bleibt offen.', { a: info.from })}</Text>
            {info.error && <Text size={200} style={{ opacity: 0.8 }}>{info.error}</Text>}
          </DialogContent>
          <DialogActions fluid>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button onClick={() => onAccount(true)}>{t('Für diese Domain immer so')}</Button>
            <Button appearance="primary" onClick={() => onAccount(false)}>{t('Mit {a} senden', { a: info.email })}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
