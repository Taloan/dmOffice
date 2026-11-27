import { Button, Link, MessageBar, MessageBarActions, MessageBarBody, Tooltip } from '@fluentui/react-components'
import { LockClosed20Regular, LockOpen20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, Message } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { copyText } from '../shell/browser'
import { askConfirm } from '../shell/context'

// OpenPGP (Mail/OpenPgp*.cs, docs/openpgp-konzept.md): Signatur in der Lese-Ansicht, Signieren beim Verfassen

export interface PgpSignature {
  state: 'valid' | 'unconfirmed' | 'mismatch' | 'bad' | 'unknown' | 'expired' | 'revoked' | 'error'
  kind: 'mime' | 'inline'
  email?: string | null; name?: string | null; fingerprint?: string | null; keyId?: string | null
  signed?: string | null; source?: string | null; partial?: boolean; keyAdded?: boolean; wkdTried?: boolean
}

export interface PgpKey { fingerprint: string; userIds: string[]; algorithm: string; created: string; expires?: string | null; matches: boolean }
export interface PgpAccount { id: string; name: string; email: string; key: PgpKey | null; sign: boolean; attachKey: boolean; autocrypt: boolean }
export interface PgpSettings { wkd: boolean; accounts: PgpAccount[] }

/** Fingerabdruck in Vierergruppen, wie GnuPG ihn zeigt */
export const fingerprintText = (fp: string) => fp.replace(/(.{4})/g, '$1 ').trim()

let cached: Promise<PgpSettings> | null = null
/** Einstellungen einmal je Seite laden; nach Änderungen in den Einstellungen neu (pgpChanged) */
export const pgpSettings = () => (cached ??= api.get<PgpSettings>('/api/mail/pgp/settings').catch(e => { cached = null; throw e }))
export const pgpChanged = (s?: PgpSettings) => { cached = s ? Promise.resolve(s) : null }

/** Signieren im Editor: Vorgabe des Kontos, bis man selbst umschaltet; ohne Schlüssel nicht verfügbar */
export function usePgpSign(account: string, initial?: boolean) {
  const [settings, setSettings] = useState<PgpSettings | null>(null)
  const [choice, setChoice] = useState<boolean | undefined>(initial)
  useEffect(() => { pgpSettings().then(setSettings).catch(() => { }) }, [])
  const acc = settings?.accounts.find(a => a.id === account)
  const available = !!acc?.key
  const sign = available && (choice ?? acc!.sign)
  return { available, sign, choice, setSign: (v: boolean) => setChoice(v) }
}

export function PgpSignButton({ sign, onChange }: { sign: boolean; onChange: (v: boolean) => void }) {
  return (
    <Tooltip content={sign ? t('Wird mit OpenPGP signiert – klicken, um nicht zu signieren') : t('Mit OpenPGP signieren')} relationship="label">
      <Button appearance={sign ? 'primary' : 'subtle'} icon={sign ? <LockClosed20Regular /> : <LockOpen20Regular />} onClick={() => onChange(!sign)} />
    </Tooltip>
  )
}

function bar(sig: PgpSignature, from?: string): { intent: 'success' | 'info' | 'warning' | 'error'; text: string } {
  const who = sig.name ? `${sig.name} <${sig.email}>` : sig.email ?? ''
  switch (sig.state) {
    // Inline-PGP neben HTML oder weiterem Text: geprüft ist nur der signierte Textblock, nicht das, was angezeigt wird
    case 'valid': return { intent: sig.kind === 'inline' && sig.partial ? 'info' : 'success', text: t('Signiert von {who} – geprüft.', { who }) }
    case 'unconfirmed': return { intent: 'info', text: t('Signiert von {who}. Die Signatur stimmt, der Schlüssel kam aber nur per Mail und ist noch nicht bestätigt.', { who }) }
    case 'mismatch': return { intent: 'warning', text: t('Die Signatur stimmt, der Schlüssel gehört aber zu {who}, nicht zum Absender {from}.', { who, from: from ?? '' }) }
    case 'bad': return { intent: 'error', text: t('Vorsicht: Die Signatur ist ungültig. Der Inhalt wurde nach dem Signieren verändert, oder die Signatur ist gefälscht.') }
    case 'expired': return { intent: 'warning', text: t('Signiert von {who} mit einem abgelaufenen Schlüssel.', { who }) }
    case 'revoked': return { intent: 'error', text: t('Signiert von {who} mit einem zurückgezogenen Schlüssel – nicht mehr vertrauenswürdig.', { who }) }
    case 'unknown': return {
      intent: 'info', text: sig.wkdTried
        ? t('Signiert mit einem unbekannten Schlüssel ({id}). Beim Mailanbieter des Absenders ist keiner hinterlegt.', { id: sig.keyId ?? '?' })
        : t('Signiert mit einem unbekannten Schlüssel ({id}).', { id: sig.keyId ?? '?' }),
    }
    default: return { intent: 'warning', text: t('Die Mail ist signiert, die Signatur lässt sich aber nicht lesen.') }
  }
}

/** Signatur einer Mail laden (Kopf der Lese-Ansicht und Prüfbereich teilen sich den Stand) */
export function usePgpSignature(message: Message) {
  const [sig, setSig] = useState<PgpSignature | null>(null)
  const [keyAdded, setKeyAdded] = useState(false)
  const url = (lookup?: boolean) => `/api/mail/${encodeURIComponent(message.account)}/signature?folder=${encodeURIComponent(message.folder)}&uid=${message.uid}${lookup ? '&lookup=true' : ''}`
  const load = (lookup?: boolean) => api.get<{ signature: PgpSignature | null; keyAdded: boolean }>(url(lookup))
    .then(r => { setSig(r.signature); setKeyAdded(r.keyAdded) })
  useEffect(() => {
    setSig(null); setKeyAdded(false)
    if (message.pgp) load().catch(() => { })
  }, [message.account, message.folder, message.uid]) // eslint-disable-line react-hooks/exhaustive-deps
  return { sig, keyAdded, load }
}
export type PgpState = ReturnType<typeof usePgpSignature>

/** Farbe der Signatur für das Symbol im Prüfbereich */
export const pgpIntent = (state: PgpState, from?: string) => state.sig ? bar(state.sig, from).intent : state.keyAdded ? 'info' : null

/** Balken über dem Inhalt einer signierten Mail; bei Mails, die nur einen Schlüssel mitbringen, ein kurzer Hinweis */
export function PgpBar({ message, className }: { message: Message; className?: string }) {
  const state = usePgpSignature(message)
  return <PgpBarView message={message} state={state} className={className} />
}

export function PgpBarView({ message, state, className }: { message: Message; state: PgpState; className?: string }) {
  const { fail, notify } = useApp()
  const { sig, keyAdded, load } = state
  if (!sig) return keyAdded ? (
    <MessageBar intent="info" className={className} layout="singleline">
      <MessageBarBody>{t('Der OpenPGP-Schlüssel des Absenders wurde gespeichert (noch nicht bestätigt).')}</MessageBarBody>
    </MessageBar>
  ) : null

  const { intent, text } = bar(sig, message.from?.address)
  const confirm = async () => {
    if (!sig.fingerprint) return
    if (!await askConfirm(t('Schlüssel bestätigen'), t('Vergleiche den Fingerabdruck mit dem Absender: {fp}. Stimmt er überein?', { fp: fingerprintText(sig.fingerprint) }), t('Stimmt überein'))) return
    try { await api.post(`/api/mail/pgp/keys/${sig.fingerprint}/confirm`); await load() } catch (e) { fail(e) }
  }
  const lookup = async () => {
    try { await load(true) } catch (e) { fail(e) }
  }
  return (
    <MessageBar intent={intent} className={className} layout="multiline">
      <MessageBarBody>
        {text}
        {sig.partial && <> {t('Nur ein Teil der Mail ist signiert.')}</>}
        {sig.fingerprint && <> <Tooltip content={t('Fingerabdruck: {fp}', { fp: fingerprintText(sig.fingerprint) })} relationship="description">
          <Link inline onClick={async () => { if (await copyText(sig.fingerprint!)) notify(t('Fingerabdruck kopiert')) }}>{t('Fingerabdruck')}</Link>
        </Tooltip></>}
      </MessageBarBody>
      {(sig.state === 'unconfirmed' || (sig.state === 'unknown' && !sig.wkdTried)) && (
        <MessageBarActions>
          {sig.state === 'unconfirmed' && <Button size="small" onClick={confirm}>{t('Schlüssel bestätigen')}</Button>}
          {sig.state === 'unknown' && <Button size="small" onClick={lookup}>{t('Beim Anbieter suchen')}</Button>}
        </MessageBarActions>
      )}
    </MessageBar>
  )
}
