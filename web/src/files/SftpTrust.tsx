import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, FluentProvider, MessageBar, MessageBarActions, MessageBarBody, Text, tokens } from '@fluentui/react-components'
import { Fingerprint20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { api, ApiError, featureOn } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { activeTheme, storedTheme } from '../theme'

/** Schlüssel eines SFTP-Servers, wie er sich gerade meldet (Server: /api/sftp/host-key). */
export interface SftpHost {
  host?: string; port?: number; fingerprint: string; keyType?: string
  /** Bisher bestätigter Fingerabdruck */
  known: string | null
  state: 'trusted' | 'new' | 'changed'
}

/** Welche Verbindung: Adresse aus einem Formular oder ein externer Ordner per Kennung */
export type SftpRef = { url: string } | { mount: string }

/** Wiederherstellen ohne Konto: eigene Adressen, dazu der Einrichtungscode */
export interface SftpTrustApi { hostKey: string; trust: string; extra?: Record<string, unknown> }
const DEFAULT_API: SftpTrustApi = { hostKey: '/api/sftp/host-key', trust: '/api/sftp/trust-host' }

/** Fehler „Fingerabdruck nicht bestätigt“ vom Server (Verbinden abgelehnt); sonst null. */
export function sftpHostOf(e: unknown): SftpHost | null {
  return featureOn('sftptrust') && e instanceof ApiError && e.data?.sftpHost ? e.data.sftpHost as SftpHost : null
}

/** Kurztext zum Stand, für Meldungen an der Verbindung. */
export function sftpHostText(h: SftpHost): string {
  return h.state === 'changed'
    ? t('Der Server meldet einen neuen Fingerabdruck. Verbunden wird wieder, sobald er bestätigt ist.')
    : t('Der Fingerabdruck des Servers wartet auf Bestätigung.')
}

const mono = { fontFamily: tokens.fontFamilyMonospace, wordBreak: 'break-all' as const }

function Line({ label, value }: { label: string; value: string }) {
  return <div style={{ display: 'grid', gridTemplateColumns: '130px 1fr', gap: 8, alignItems: 'baseline' }}>
    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{label}</Text>
    <Text style={mono}>{value}</Text>
  </div>
}

/** Fingerabdruck zeigen; true: bestätigt (Knopf), false: abgebrochen oder nur angesehen. */
function askTrust(h: SftpHost, mayChange: boolean): Promise<boolean> {
  return new Promise(resolve => {
    const host = document.createElement('div')
    document.body.appendChild(host)
    const root = createRoot(host)
    const done = (r: boolean) => { root.unmount(); host.remove(); resolve(r) }
    const dark = storedTheme() === 'dark' || (storedTheme() === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
    const canConfirm = h.state === 'new' || (h.state === 'changed' && mayChange)
    root.render(
      <FluentProvider theme={activeTheme(dark)}>
        <Dialog open onOpenChange={(_, x) => !x.open && done(false)}>
          <DialogSurface style={{ maxWidth: 560 }}>
            <DialogBody>
              <DialogTitle>{h.state === 'new' ? t('Fingerabdruck des Servers bestätigen') : h.state === 'changed' ? t('Neuer Fingerabdruck des Servers') : t('Fingerabdruck des Servers')}</DialogTitle>
              <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }} data-sftp-trust={h.state}>
                <Text>{h.state === 'new' ? t('Stimmt der Fingerabdruck mit dem überein, den der Server selbst anzeigt, bestätige ihn. Danach verbindet dmOffice.')
                  : h.state === 'changed' ? (mayChange ? t('Der Server meldet einen anderen Fingerabdruck als bisher. Bestätige den neuen, wenn der Server neu eingerichtet wurde.')
                    : t('Der Server meldet einen anderen Fingerabdruck als bisher. Den neuen bestätigt ein Admin.'))
                  : t('Dieser Fingerabdruck ist bestätigt.')}</Text>
                {h.host && <Line label={t('Server')} value={h.host + (h.port && h.port !== 22 ? ':' + h.port : '')} />}
                {h.keyType && <Line label={t('Schlüsseltyp')} value={h.keyType} />}
                {h.state === 'changed'
                  ? <><Line label={t('Bisher bestätigt')} value={h.known ?? ''} /><Line label={t('Neu')} value={h.fingerprint} /></>
                  : <Line label={t('Fingerabdruck')} value={h.fingerprint} />}
              </DialogContent>
              <DialogActions>
                <Button onClick={() => done(false)}>{canConfirm ? t('Abbrechen') : t('Schließen')}</Button>
                {canConfirm && <Button appearance="primary" onClick={() => done(true)}>{h.state === 'changed' ? t('Neuen bestätigen') : t('Bestätigen')}</Button>}
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      </FluentProvider>)
  })
}

/**
 * Vor dem Speichern oder Prüfen einer SFTP-Verbindung: Fingerabdruck abfragen und, wenn er neu oder geändert ist, zum Bestätigen
 * zeigen. true: bestätigt (oder nicht zu klären - dann meldet das Verbinden selbst, was fehlt), false: nicht bestätigt.
 * show: auch einen schon bestätigten zeigen (Knopf „Fingerabdruck prüfen“). Fehler beim Bestätigen gehen als Ausnahme weiter.
 */
export async function ensureSftpTrust(ref: SftpRef, opts: { admin: boolean; endpoints?: SftpTrustApi }): Promise<boolean> {
  return await trustFlow(ref, opts.admin, false, opts.endpoints) !== 'refused'
}

/** Abfragen, zeigen, bestätigen: „confirmed“ eben bestätigt, „trusted“ war es schon (oder nicht zu klären), „refused“ nicht bestätigt. */
async function trustFlow(ref: SftpRef, admin: boolean, show: boolean, ep = DEFAULT_API): Promise<'confirmed' | 'trusted' | 'refused'> {
  if (!featureOn('sftptrust')) return 'trusted'
  let h: SftpHost
  try { h = await api.post<SftpHost>(ep.hostKey, { ...ref, ...ep.extra }) } catch (e) { if (show) throw e; return 'trusted' }
  if (h.state === 'trusted' && !show) return 'trusted'
  const ok = await askTrust(h, admin)
  if (h.state === 'trusted') return 'trusted'
  if (!ok) return 'refused'
  await api.post(ep.trust, { ...ref, ...ep.extra, fingerprint: h.fingerprint })
  // Ordnerliste neu: Ordner, die am Fingerabdruck scheiterten, sind wieder erreichbar
  window.dispatchEvent(new Event('dmoffice:external-changed'))
  return 'confirmed'
}

/** Knopf „Fingerabdruck prüfen“ für Meldungen an der Verbindung (Ordner lässt sich nicht lesen). */
export function SftpTrustButton({ refTo, onTrusted }: { refTo: SftpRef; onTrusted: () => void }) {
  const { me, fail, notify } = useApp()
  const check = async () => {
    try {
      const r = await trustFlow(refTo, me.isAdmin, true)
      if (r === 'confirmed') notify(t('Fingerabdruck bestätigt'))
      if (r !== 'refused') onTrusted()
    } catch (e) { fail(e) }
  }
  return <Button icon={<Fingerprint20Regular />} onClick={check}>{t('Fingerabdruck prüfen')}</Button>
}

/**
 * In den SFTP-Feldern: Fingerabdruck ansehen und bestätigen. Für schon gespeicherte Verbindungen (saved) wird beim Öffnen
 * nachgesehen; meldet der Server einen anderen als den bestätigten, steht das darunter.
 */
export function SftpTrustRow({ url, saved }: { url: string; saved: boolean }) {
  const { me, fail, notify } = useApp()
  const [changed, setChanged] = useState(false)
  const ready = /^sftp:\/\/[^/?#:]+/i.test(url.trim())
  useEffect(() => {
    if (!saved || !ready || !featureOn('sftptrust')) return
    let gone = false
    api.post<SftpHost>(DEFAULT_API.hostKey, { url }).then(h => { if (!gone) setChanged(h.state === 'changed') }).catch(() => {})
    return () => { gone = true }
    // Nur beim Öffnen: nicht bei jedem Tastendruck im Adressfeld beim Server nachfragen
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [saved])
  if (!featureOn('sftptrust') || !ready) return null
  const check = async () => {
    try {
      const r = await trustFlow({ url }, me.isAdmin, true)
      if (r === 'confirmed') { setChanged(false); notify(t('Fingerabdruck bestätigt')) }
    } catch (e) { fail(e) }
  }
  return <>
    <div><Button size="small" appearance="subtle" icon={<Fingerprint20Regular />} onClick={check}>{t('Fingerabdruck prüfen')}</Button></div>
    {changed && <MessageBar intent="warning">
      <MessageBarBody>{t('Der Server meldet einen neuen Fingerabdruck. Verbunden wird wieder, sobald er bestätigt ist.')}</MessageBarBody>
      <MessageBarActions><Button size="small" onClick={check}>{t('Fingerabdruck prüfen')}</Button></MessageBarActions>
    </MessageBar>}
  </>
}
