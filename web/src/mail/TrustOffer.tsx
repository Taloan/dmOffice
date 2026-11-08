import { Button, MessageBar, MessageBarActions, MessageBarBody } from '@fluentui/react-components'
import { Dismiss16Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

/** Prüfung des Servers vor „Kein Junk“: was sich als sicherer Absender eintragen ließe (Mail/SpamTrust.cs, SafeList). */
export interface TrustCheck {
  address: string; domain: string; verified: boolean; freemail: boolean; addressListed: boolean; domainListed: boolean; token: string
}

/** Vor „Kein Junk“ prüfen lassen (danach wandert die Mail weiter und bekommt eine neue Nummer). */
export const checkTrust = (account: string, folder: string, uid: number) =>
  api.get<TrustCheck>(`/api/mail/${encodeURIComponent(account)}/trust?folder=${encodeURIComponent(folder)}&uid=${uid}`)

/**
 * Nach „Kein Junk“: „Diesem Absender immer vertrauen?“ - die Adresse oder die ganze Domain. Nur wenn der Mailserver bestätigt hat,
 * dass die Mail wirklich von dort kommt; Freemail-Domains (gmail.com, gmx.de …) nur als einzelne Adresse.
 */
export function TrustOffer({ check, onClose }: { check: TrustCheck; onClose: () => void }) {
  const { fail, notify } = useApp()
  const [busy, setBusy] = useState(false)
  if (check.addressListed || (check.domainListed && !check.freemail)) return null
  const add = async (scope: 'address' | 'domain') => {
    setBusy(true)
    try {
      const r = await api.post<{ entry: string }>('/api/mail/trust', { token: check.token, scope })
      notify(t('„{entry}“ ist jetzt ein sicherer Absender.', { entry: r.entry }))
      onClose()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const close = <Button appearance="transparent" size="small" icon={<Dismiss16Regular />} aria-label={t('Schließen')} onClick={onClose} />
  if (!check.verified) return (
    <MessageBar intent="info" layout="multiline" style={{ margin: '4px 8px' }}>
      <MessageBarBody>{t('Als „Kein Junk“ gelernt. Mails von dieser Domain lassen sich nicht verifizieren – ein Eintrag hilft hier nicht.')}</MessageBarBody>
      <MessageBarActions containerAction={close} />
    </MessageBar>
  )
  return (
    <MessageBar intent="success" layout="multiline" style={{ margin: '4px 8px' }}>
      <MessageBarBody>{t('Als „Kein Junk“ gelernt. Diesem Absender immer vertrauen?')}</MessageBarBody>
      <MessageBarActions containerAction={close}>
        <Button size="small" disabled={busy} onClick={() => add('address')}>{check.address}</Button>
        {!check.freemail && <Button size="small" disabled={busy} onClick={() => add('domain')}>{t('Alle von @{domain}', { domain: check.domain })}</Button>}
      </MessageBarActions>
    </MessageBar>
  )
}
