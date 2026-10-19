// Postausgang: Mails, die gleich (Zurueckrufen) oder zu einer geplanten Zeit rausgehen.
import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input,
  Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, Spinner, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { ChevronDown20Regular, Clock20Regular, Delete20Regular, DocumentEdit20Regular, Send20Regular } from '@fluentui/react-icons'
import { ReactElement, useEffect, useState } from 'react'
import { Account, api } from '../api'
import { useApp } from '../App'
import { fmtLong } from '../shell/hooks'
import { t } from '../i18n'
import { snoozeOptions } from './SnoozeMenu'

const useStyles = makeStyles({
  root: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, overflowY: 'auto', padding: '12px 16px', gap: '8px' },
  row: {
    display: 'flex', alignItems: 'center', gap: '12px', padding: '10px 12px', borderRadius: '6px',
    backgroundColor: tokens.colorNeutralBackground2, border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  main: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  sub: { color: tokens.colorNeutralForeground3 },
  err: { color: tokens.colorPaletteRedForeground1 },
  item: { display: 'flex', justifyContent: 'space-between', gap: '24px', minWidth: '240px' },
})

interface QueueRow { id: number; account: string; subject: string; recipients: string; sendAt: string; state: string; error?: string | null }

/** Zeitpunkt waehlen wie bei der Wiedervorlage - Vorschlaege oder frei. */
export function SendLaterMenu({ trigger, onPick }: { trigger: ReactElement; onPick: (when: Date) => void }) {
  const s = useStyles()
  const [custom, setCustom] = useState(false)
  const pad = (n: number) => String(n).padStart(2, '0')
  const d = new Date(Date.now() + 86400000)
  const [value, setValue] = useState(`${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T08:00`)
  return (
    <>
      <Menu>
        <MenuTrigger disableButtonEnhancement>{trigger}</MenuTrigger>
        <MenuPopover>
          <MenuList>
            {snoozeOptions().map(o => (
              <MenuItem key={o.label} onClick={() => onPick(o.when)}>
                <span className={s.item}><span>{o.label}</span><span className={s.sub}>{o.hint}</span></span>
              </MenuItem>
            ))}
            <MenuDivider />
            <MenuItem onClick={() => setCustom(true)}>{t('Datum und Uhrzeit wählen …')}</MenuItem>
          </MenuList>
        </MenuPopover>
      </Menu>
      {custom && (
        <Dialog open onOpenChange={(_, x) => !x.open && setCustom(false)}>
          <DialogSurface style={{ maxWidth: 360 }}>
            <DialogBody>
              <DialogTitle>{t('Später senden')}</DialogTitle>
              <DialogContent>
                <Field label={t('Wann soll die Mail rausgehen?')}>
                  <Input type="datetime-local" value={value} onChange={(_, x) => setValue(x.value)} />
                </Field>
              </DialogContent>
              <DialogActions>
                <Button onClick={() => setCustom(false)}>{t('Abbrechen')}</Button>
                <Button appearance="primary" disabled={!value || new Date(value) <= new Date()}
                  onClick={() => { setCustom(false); onPick(new Date(value)) }}>{t('Planen')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
    </>
  )
}

/** Inhalt des Ordners "Postausgang". */
export function OutboxView({ accounts, version, onChanged }: { accounts: Account[]; version?: number; onChanged: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [rows, setRows] = useState<QueueRow[] | null>(null)
  const load = () => api.get<QueueRow[]>('/api/mail/queue').then(setRows).catch(fail)
  useEffect(() => { load() }, [version]) // eslint-disable-line react-hooks/exhaustive-deps
  const act = async (fn: () => Promise<unknown>, done?: string) => {
    try { await fn(); if (done) notify(done); load(); onChanged() } catch (e) { fail(e); load() }
  }
  const email = (id: string) => accounts.find(a => a.id === id)?.email ?? id
  if (!rows) return <div className={s.root}><Spinner /></div>
  return (
    <div className={s.root}>
      <Text weight="semibold" size={400}>{t('Postausgang')}</Text>
      {rows.length === 0 && <Text className={s.sub}>{t('Nichts zu senden.')}</Text>}
      {rows.map(r => (
        <div key={r.id} className={s.row}>
          <Clock20Regular />
          <div className={s.main}>
            <Text weight="semibold" truncate>{r.subject || t('(ohne Betreff)')}</Text>
            <Text size={200} className={s.sub} truncate>{t('An')}: {r.recipients} · {email(r.account)}</Text>
            <Text size={200} className={r.state === 'failed' ? s.err : s.sub}>
              {r.state === 'failed' ? t('Nicht gesendet: {error}', { error: r.error ?? '' })
                : r.state === 'sending' ? t('Wird gerade gesendet …')
                : t('Geht raus: {when}', { when: fmtLong(r.sendAt) })}
            </Text>
          </div>
          {r.state !== 'sending' && <>
            <Button size="small" icon={<Send20Regular />} onClick={() => act(() => api.post(`/api/mail/queue/${r.id}/time`, {}))}>{t('Jetzt senden')}</Button>
            <SendLaterMenu trigger={<Button size="small" icon={<ChevronDown20Regular />} iconPosition="after">{t('Andere Zeit')}</Button>}
              onPick={when => act(() => api.post(`/api/mail/queue/${r.id}/time`, { sendAt: when.toISOString() }))} />
            <Button size="small" icon={<DocumentEdit20Regular />} title={t('Nicht senden, zurück in die Entwürfe')}
              onClick={() => act(() => api.post(`/api/mail/queue/${r.id}/to-drafts`), t('In die Entwürfe gelegt'))}>{t('In Entwürfe')}</Button>
            <Button size="small" appearance="subtle" icon={<Delete20Regular />} aria-label={t('Löschen')}
              onClick={() => confirm(t('Diese Mail nicht senden und löschen?')) && act(() => api.post(`/api/mail/queue/${r.id}/cancel`), t('Gelöscht'))} />
          </>}
        </div>
      ))}
    </div>
  )
}
