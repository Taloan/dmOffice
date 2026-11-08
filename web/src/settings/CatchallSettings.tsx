import {
  Badge, Button, Dropdown, Field, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, MessageBar, MessageBarBody, Option, Radio, RadioGroup,
  Spinner, Switch, Text, Tooltip, makeStyles, tokens,
} from '@fluentui/react-components'
import { Add20Regular, Copy20Regular, Delete20Regular, Edit20Regular, HourglassHalf20Regular, LockClosed20Regular, LockOpen20Regular, MoreHorizontal20Regular, PersonAdd20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { copyText } from '../shell/browser'
import { fmtDate } from '../shell/hooks'
import { AliasConfig, AliasItem, AliasState, NewAliasDialog } from '../mail/AliasDialog'
import { AdoptAliasDialog, CATCHALL_CHANGED, catchallChanged, deleteAlias } from '../mail/CatchallFolders'
import { Hint, PageTitle, Section } from './parts'

const useStyles = makeStyles({
  row: { display: 'flex', alignItems: 'center', gap: '8px', padding: '8px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, flexWrap: 'wrap' },
  main: { flex: 1, minWidth: '220px', display: 'flex', flexDirection: 'column', gap: '2px', cursor: 'pointer' },
  muted: { color: tokens.colorNeutralForeground3 },
  details: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '0 0 8px 0', flexBasis: '100%' },
})

interface Sender { address: string; name: string | null; count: number }

/** E-Mail → Catchall-Aliase: Domain und Postfach, Verhalten, Aliase mit Übersicht, Sperren mit einem Klick, unbekannte übernehmen
 * (Mail/CatchallAliases.cs, Ordner: Mail/CatchallFolders.cs). */
export function CatchallSettings() {
  const { notify, fail } = useApp()
  const s = useStyles()
  const [st, setSt] = useState<AliasState | null>(null)
  const [domains, setDomains] = useState('')
  const [adding, setAdding] = useState(false)
  const [open, setOpen] = useState<string | null>(null)
  const [senders, setSenders] = useState<Sender[] | null>(null)
  const [editing, setEditing] = useState<{ address: string; note: string } | null>(null)
  // Entfernen nach Zeit je Alias (Mail/MailExpiry.cs, beim Eingang eingeplant)
  const [timing, setTiming] = useState<{ address: string; kind: '' | 'trash' | 'destroy'; days: string } | null>(null)
  const load = () => api.get<AliasState>('/api/mail/aliases').then(x => { setSt(x); setDomains(x.config.domains.join(', ')) }).catch(fail)
  useEffect(() => {
    load()
    const reload = () => { load() }
    window.addEventListener(CATCHALL_CHANGED, reload)
    return () => window.removeEventListener(CATCHALL_CHANGED, reload)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const save = async (change: Partial<AliasConfig>) => {
    if (!st) return
    const config = { ...st.config, ...change }
    setSt({ ...st, config })
    try { const x = await api.put<AliasState>('/api/mail/aliases/config', config); setSt(x); setDomains(x.config.domains.join(', ')); catchallChanged() }
    catch (e) { fail(e); load() }
  }
  const update = async (body: { address: string; note?: string; block?: boolean; blockAs?: string; expireKind?: string; expireDays?: number | null }) => {
    try {
      const a = await api.put<AliasItem>('/api/mail/aliases/item', body)
      setSt(x => x && { ...x, items: x.items.map(i => i.address === a.address ? a : i) })
      catchallChanged()
    } catch (e) { fail(e) }
  }
  const remove = async (a: AliasItem) => {
    try { await deleteAlias(a.address) } catch (e) { fail(e) }
  }
  const [adopt, setAdopt] = useState<string | null>(null)
  const toggle = async (a: AliasItem) => {
    if (open === a.address) { setOpen(null); return }
    setOpen(a.address); setSenders(null)
    try { setSenders((await api.get<{ senders: Sender[] }>('/api/mail/aliases/item?address=' + encodeURIComponent(a.address))).senders) } catch (e) { fail(e) }
  }
  const copy = async (a: string) => { if (await copyText(a)) notify(t('{a} kopiert', { a })) }

  if (!st) return <PageTitle>{t('Catchall-Aliase')}</PageTitle>
  const c = st.config
  const off = c.domains.length === 0 || !c.account
  const account = st.accounts.find(a => a.id === c.account)
  return (
    <>
      <PageTitle>{t('Catchall-Aliase')}</PageTitle>
      <Section title={t('Domain und Postfach')} hint={t('Beim Hoster der Domain alle Adressen (Catch-all) an ein Postfach weiterleiten, das dmOffice abholt. Dann bekommt jeder Shop, Verein oder jedes Forum einen eigenen Alias dieser Domain. Im Postfach entsteht je Domain ein Ordner, darunter einer je Alias – auch andere Mailprogramme sehen sie.')}>
        <Field label={t('Domains')} hint={t('Mehrere mit Komma trennen, z. B. example.com')}>
          <Input value={domains} onChange={(_, d) => setDomains(d.value)} placeholder="example.com"
            onBlur={() => { if (domains !== c.domains.join(', ')) save({ domains: domains.split(/[,\s]+/).filter(Boolean) }) }} />
        </Field>
        <Field label={t('Postfach, in dem die Mails ankommen')}>
          <Dropdown value={account ? (account.email || account.name) : ''} placeholder={t('Postfach wählen')} selectedOptions={c.account ? [c.account] : []}
            onOptionSelect={(_, d) => save({ account: d.optionValue ?? null })}>
            {st.accounts.map(a => <Option key={a.id} value={a.id}>{a.email || a.name}</Option>)}
          </Dropdown>
        </Field>
        {!st.accounts.length && <Hint>{t('Noch kein E-Mail-Konto eingerichtet.')}</Hint>}
      </Section>
      <Section title={t('Unbekannte Adressen')} hint={t('Mails an eine Adresse der Domain, die kein Alias ist. Sie bekommen keinen eigenen Ordner – sonst legte Spam an erfundene Adressen Ordner an.')}>
        <RadioGroup value={c.unknown} disabled={off} onChange={(_, d) => save({ unknown: d.value as AliasConfig['unknown'] })}>
          <Radio value="accept" label={t('Annehmen, in den Ordner der Domain')} />
          <Radio value="junk" label={t('Gleich in Junk')} />
        </RadioGroup>
        {st.unknown.length > 0 && <div>
          {st.unknown.map(u => (
            <div key={u.address} className={s.row}>
              <div className={s.main} style={{ cursor: 'default' }}>
                <Text>{u.address}</Text>
                <Text size={200} className={s.muted}>{u.count === 1 ? t('1 Mail') : t('{n} Mails', { n: u.count })} · {t('letzte {d}', { d: fmtDate(u.last) })}</Text>
              </div>
              <Button appearance="subtle" icon={<PersonAdd20Regular />} onClick={() => setAdopt(u.address)}>{t('Als Alias anlegen')}</Button>
            </div>
          ))}
        </div>}
      </Section>
      <Section title={t('Sperren')} hint={t('Neue Mails an gesperrte Aliase gehen beim Eingang gleich weg; der Ordner mit den alten Mails bleibt. „Warum?“ an der Mail nennt den Grund.')}>
        <RadioGroup value={c.blockAs} disabled={off} onChange={(_, d) => save({ blockAs: d.value as AliasConfig['blockAs'] })}>
          <Radio value="junk" label={t('In Junk verschieben')} />
          <Radio value="delete" label={t('Löschen (in den Papierkorb)')} />
        </RadioGroup>
      </Section>
      <Section title={t('Antworten')}>
        <Switch disabled={off} checked={c.replyAs} label={t('Antworten mit dem Alias als Absender')} onChange={(_, d) => save({ replyAs: d.checked })} />
        <MessageBar intent="warning" layout="multiline">
          <MessageBarBody>{t('Nur einschalten, wenn der SMTP-Server des Postfachs diesen Absender erlaubt und SPF und DKIM der Domain für diesen Server eingerichtet sind. Sonst lehnt der Server die Mail ab oder sie landet beim Empfänger im Spam.')}</MessageBarBody>
        </MessageBar>
      </Section>
      <Section title={t('Aliase')} last>
        <div><Button icon={<Add20Regular />} disabled={off} onClick={() => setAdding(true)}>{t('Neuer Alias')}</Button></div>
        {!st.items.length && <Hint>{off ? t('Erst Domain und Postfach eintragen.') : t('Noch keine Aliase.')}</Hint>}
        <div>
          {st.items.map(a => (
            <div key={a.address} className={s.row}>
              <div className={s.main} onClick={() => toggle(a)}>
                <span style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                  <Text weight="semibold">{a.address}</Text>
                  {a.blocked && <Badge color="danger" appearance="tint">{a.blocked === 'delete' ? t('gesperrt – löschen') : t('gesperrt – Junk')}</Badge>}
                </span>
                {a.note && <Text>{a.note}</Text>}
                <Text size={200} className={s.muted}>
                  {t('angelegt {d}', { d: fmtDate(a.created, false) })} · {a.count === 1 ? t('1 Mail') : t('{n} Mails', { n: a.count })}
                  {a.last && <> · {t('letzte {d}', { d: fmtDate(a.last) })}</>}
                  {a.expireKind && <> · {a.expireKind === 'destroy'
                    ? t('neue Mails nach {n} Tagen vernichten', { n: a.expireDays ?? 0 })
                    : t('neue Mails nach {n} Tagen in den Papierkorb', { n: a.expireDays ?? 0 })}</>}
                </Text>
              </div>
              <Tooltip content={t('Kopieren')} relationship="label"><Button appearance="subtle" icon={<Copy20Regular />} onClick={() => copy(a.address)} /></Tooltip>
              <Button appearance="subtle" icon={a.blocked ? <LockOpen20Regular /> : <LockClosed20Regular />} onClick={() => update({ address: a.address, block: !a.blocked })}>
                {a.blocked ? t('Entsperren') : t('Sperren')}
              </Button>
              <Menu>
                <MenuTrigger disableButtonEnhancement><Button appearance="subtle" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} /></MenuTrigger>
                <MenuPopover>
                  <MenuList>
                    <MenuItem icon={<Edit20Regular />} onClick={() => setEditing({ address: a.address, note: a.note })}>{t('Notiz ändern …')}</MenuItem>
                    <MenuItem icon={<HourglassHalf20Regular />} onClick={() => setTiming({ address: a.address, kind: a.expireKind ?? 'trash', days: String(a.expireDays ?? 30) })}>{t('Entfernen nach Zeit …')}</MenuItem>
                    <MenuItem icon={<LockClosed20Regular />} onClick={() => update({ address: a.address, block: true, blockAs: 'junk' })}>{t('Sperren – in Junk')}</MenuItem>
                    <MenuItem icon={<LockClosed20Regular />} onClick={() => update({ address: a.address, block: true, blockAs: 'delete' })}>{t('Sperren – löschen')}</MenuItem>
                    <MenuItem icon={<Delete20Regular />} onClick={() => remove(a)}>{t('Löschen …')}</MenuItem>
                  </MenuList>
                </MenuPopover>
              </Menu>
              {editing?.address === a.address && (
                <div className={s.details}>
                  <div style={{ display: 'flex', gap: 6 }}>
                    <Input value={editing.note} style={{ flex: 1 }} autoFocus onChange={(_, d) => setEditing({ ...editing, note: d.value })}
                      onKeyDown={e => { if (e.key === 'Enter') { update({ address: a.address, note: editing.note }); setEditing(null) } else if (e.key === 'Escape') setEditing(null) }} />
                    <Button appearance="primary" onClick={() => { update({ address: a.address, note: editing.note }); setEditing(null) }}>{t('Speichern')}</Button>
                  </div>
                </div>
              )}
              {timing?.address === a.address && (
                <div className={s.details}>
                  <div style={{ display: 'flex', gap: 6, alignItems: 'center', flexWrap: 'wrap' }}>
                    <Dropdown value={timing.kind === 'trash' ? t('In den Papierkorb nach …') : timing.kind === 'destroy' ? t('Vernichten nach …') : t('Aus')}
                      selectedOptions={[timing.kind]} onOptionSelect={(_, d) => setTiming({ ...timing, kind: (d.optionValue ?? '') as any })} style={{ minWidth: 200 }}>
                      <Option value="">{t('Aus')}</Option>
                      <Option value="trash">{t('In den Papierkorb nach …')}</Option>
                      <Option value="destroy">{t('Vernichten nach …')}</Option>
                    </Dropdown>
                    {timing.kind && <><Input type="number" value={timing.days} style={{ width: 90 }} onChange={(_, d) => setTiming({ ...timing, days: d.value })} />
                      <Text>{t('Tagen')}</Text></>}
                    <Button appearance="primary" onClick={() => { update({ address: a.address, expireKind: timing.kind, expireDays: timing.kind ? Number(timing.days) : null }); setTiming(null) }}>{t('Speichern')}</Button>
                    <Button onClick={() => setTiming(null)}>{t('Abbrechen')}</Button>
                  </div>
                  <Hint>{timing.kind === 'destroy'
                    ? t('Gilt für neue Mails an diesen Alias, ab Eingang. Vernichten löscht sie auch auf dem Mailserver, ohne Papierkorb; Kopien anderswo erreicht dmOffice nicht. Bis dahin lässt es sich an der Mail zurücknehmen.')
                    : t('Gilt für neue Mails an diesen Alias, ab Eingang. Bis dahin lässt es sich an der Mail zurücknehmen.')}</Hint>
                </div>
              )}
              {open === a.address && (
                <div className={s.details}>
                  <Text size={200} weight="semibold">{t('Häufigste Absender')}</Text>
                  {senders == null ? <Spinner size="tiny" /> : senders.length === 0 ? <Hint>{t('Noch keine Mails.')}</Hint>
                    : senders.map(x => <Text key={x.address} size={200}>{x.name ? `${x.name} <${x.address}>` : x.address} · {x.count}</Text>)}
                </div>
              )}
            </div>
          ))}
        </div>
      </Section>
      {adding && <NewAliasDialog onClose={() => setAdding(false)} />}
      {adopt && <AdoptAliasDialog address={adopt} onClose={() => setAdopt(null)} />}
    </>
  )
}
