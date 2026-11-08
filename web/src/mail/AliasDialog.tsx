import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Text, Tooltip, tokens } from '@fluentui/react-components'
import { ArrowShuffle20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t, tError } from '../i18n'
import { copyText } from '../shell/browser'
import { catchallChanged } from './CatchallFolders'

/** Einstellungen der Catchall-Aliase (Mail/CatchallAliases.cs, Config). */
export interface AliasConfig { domains: string[]; account: string | null; unknown: 'accept' | 'junk'; blockAs: 'junk' | 'delete'; replyAs: boolean }
/** Ein Catchall-Alias; blocked: null offen, sonst wohin neue Mails gehen. */
export interface AliasItem { address: string; note: string; created: string; blocked: 'junk' | 'delete' | null; auto: boolean; count: number; last: string | null; folder?: string | null
  /** Entfernen nach Zeit für neue Mails: trash oder destroy nach expireDays Tagen */
  expireKind?: 'trash' | 'destroy' | null; expireDays?: number | null }
export interface AliasState {
  config: AliasConfig; accounts: { id: string; name: string; email: string }[]; items: AliasItem[]
  /** Je Domain ihr IMAP-Ordner (Mails an unbekannte Adressen) */
  domains: { domain: string; folder: string | null }[]
  /** Adressen der Domain mit Post, die nicht in der Liste stehen - zum Übernehmen */
  unknown: { address: string; count: number; last: string }[]
}

/** Neuer Catchall-Alias: Namen selbst wählen oder zufällig, Notiz „wofür“, danach in die Zwischenablage. */
export function NewAliasDialog({ onClose, onCreated }: { onClose: () => void; onCreated?: (a: AliasItem) => void }) {
  const { notify, fail } = useApp()
  const [config, setConfig] = useState<AliasConfig | null>(null)
  const [domain, setDomain] = useState('')
  const [name, setName] = useState('')
  const [note, setNote] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const shuffle = () => api.get<{ name: string }>('/api/mail/aliases/suggest').then(r => setName(r.name)).catch(fail)
  useEffect(() => {
    api.get<AliasState>('/api/mail/aliases').then(st => {
      setConfig(st.config)
      setDomain(st.config.domains[0] ?? '')
      if (st.config.domains.length) shuffle()
    }).catch(fail)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const create = async () => {
    setBusy(true); setError(null)
    try {
      const a = await api.post<AliasItem>('/api/mail/aliases', { name: name.trim() || null, domain, note })
      const copied = await copyText(a.address)
      notify(copied ? t('{a} kopiert', { a: a.address }) : t('Angelegt: {a}', { a: a.address }))
      onCreated?.(a)
      catchallChanged()
      onClose()
    } catch (e) { setError(tError(e instanceof Error ? e.message : String(e))) } finally { setBusy(false) }
  }

  const none = config != null && (config.domains.length === 0 || !config.account)
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>{t('Neuer Catchall-Alias')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {none ? <Text>{t('Erst unter Einstellungen → E-Mail → Catchall-Aliase eine Domain eintragen und das Postfach wählen.')}</Text> : config && <>
              <Field label={t('Alias')} validationMessage={error ?? undefined}>
                <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
                  <Input value={name} onChange={(_, d) => setName(d.value)} style={{ flex: 1, minWidth: 0 }} placeholder={t('Name')}
                    onKeyDown={e => { if (e.key === 'Enter') create() }} autoFocus />
                  {config.domains.length > 1
                    ? <Dropdown value={'@' + domain} selectedOptions={[domain]} onOptionSelect={(_, d) => d.optionValue && setDomain(d.optionValue)} style={{ minWidth: 140 }}>
                        {config.domains.map(d => <Option key={d} value={d}>{'@' + d}</Option>)}
                      </Dropdown>
                    : <Text>@{domain}</Text>}
                  <Tooltip content={t('Zufälligen Namen erzeugen')} relationship="label">
                    <Button appearance="subtle" icon={<ArrowShuffle20Regular />} onClick={shuffle} />
                  </Tooltip>
                </div>
              </Field>
              <Field label={t('Wofür?')} hint={t('Zum Beispiel „Gewinnspiel Baumarkt“ – so weißt du später, wer den Alias weitergegeben hat.')}>
                <Input value={note} onChange={(_, d) => setNote(d.value)} onKeyDown={e => { if (e.key === 'Enter') create() }} />
              </Field>
              <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Der Alias wird nach dem Anlegen in die Zwischenablage kopiert; sein Ordner entsteht im Postfach.')}</Text>
            </>}
          </DialogContent>
          <DialogActions>
            {none
              ? <Button appearance="primary" onClick={() => { onClose(); location.hash = '#/settings/aliases' }}>{t('Zu den Einstellungen')}</Button>
              : <Button appearance="primary" disabled={!config || busy} onClick={create}>{t('Anlegen und kopieren')}</Button>}
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
