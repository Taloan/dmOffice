import { Button, Field, Input, Radio, RadioGroup, SpinButton, Text, Textarea, tokens } from '@fluentui/react-components'
import { Copy20Regular, Key20Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { copyText } from '../shell/browser'

export const isSftp = (url: string | null | undefined) => /^\s*sftp:\/\//i.test(url ?? '')

interface Parts { host: string; port: number; folder: string; connections: number }

/** sftp://server:port/ordner?connections=n zerlegen; „/~/ordner“ = relativ zum Startordner */
export function sftpParts(url: string): Parts {
  try {
    const u = new URL(url.trim())
    const path = decodeURIComponent(u.pathname)
    return { host: u.hostname, port: u.port ? Number(u.port) : 22, folder: path.startsWith('/~') ? path.slice(2).replace(/^\/+/, '') : path,
      connections: Math.min(16, Math.max(1, Number(u.searchParams.get('connections')) || 4)) }
  } catch { return { host: '', port: 22, folder: '', connections: 4 } }
}

/** Adresse zusammensetzen; ein Ordner ohne führenden „/“ gilt relativ zum Startordner des Kontos */
export function sftpUrl(p: Parts): string {
  if (!p.host.trim()) return 'sftp://'
  const folder = p.folder.trim()
  const path = !folder ? '' : folder.startsWith('/') ? folder : '/~/' + folder
  return `sftp://${p.host.trim()}${p.port && p.port !== 22 ? ':' + p.port : ''}${path.split('/').map(encodeURIComponent).join('/')}?connections=${p.connections}`
}

/**
 * Felder für ein SFTP-Ziel. Die Adresse (sftp://…) und die Anmeldung landen in denselben Feldern wie bei WebDAV (Adresse, Benutzer,
 * Passwort): Als Passwort geht auch ein privater SSH-Schlüssel; „Schlüssel erzeugen“ setzt ihn ein und zeigt den öffentlichen Teil.
 */
export function SftpFields({ url, onUrl, user, onUser, secret, onSecret, hasSecret, admin }: {
  url: string; onUrl: (v: string) => void; user: string; onUser: (v: string) => void; secret: string; onSecret: (v: string) => void
  /** Ein Passwort oder Schlüssel ist schon gespeichert */
  hasSecret?: boolean
  /** Verwaltung: gemerkten Server-Schlüssel zurücksetzen */
  admin?: boolean
}) {
  const { fail, notify } = useApp()
  const p = sftpParts(url)
  const set = (patch: Partial<Parts>) => onUrl(sftpUrl({ ...p, ...patch }))
  const [mode, setMode] = useState<'password' | 'key'>(secret.trim().startsWith('-----BEGIN') ? 'key' : 'password')
  const [pub, setPub] = useState<string | null>(null)
  const muted = { color: tokens.colorNeutralForeground3 }
  const newKey = async () => {
    try {
      const r = await api.post<{ privateKey: string; publicKey: string }>('/api/sftp/keypair')
      onSecret(r.privateKey); setPub(r.publicKey)
    } catch (e) { fail(e) }
  }
  const forget = async () => {
    try { await api.post('/api/sftp/forget-host', { url }); notify(t('Gemerkter Server-Schlüssel zurückgesetzt')) } catch (e) { fail(e) }
  }
  return <>
    <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
      <Field label={t('Server')} style={{ flex: 1, minWidth: 200 }}><Input value={p.host} placeholder="nas.example.org" onChange={(_, d) => set({ host: d.value })} /></Field>
      <Field label={t('Port')} style={{ width: 110 }}>
        <SpinButton value={p.port} min={1} max={65535} onChange={(_, d) => set({ port: d.value ?? (Number(d.displayValue) || 22) })} />
      </Field>
    </div>
    <Field label={t('Ordner')}>
      <Input value={p.folder} onChange={(_, d) => set({ folder: d.value })} />
    </Field>
    <Field label={t('Benutzername')}><Input value={user} onChange={(_, d) => onUser(d.value)} autoComplete="off" /></Field>
    <Field label={t('Anmeldung')}>
      <RadioGroup layout="horizontal" value={mode} onChange={(_, d) => { setMode(d.value as 'password' | 'key'); onSecret(''); setPub(null) }}>
        <Radio value="password" label={t('Passwort')} /><Radio value="key" label={t('SSH-Schlüssel')} />
      </RadioGroup>
    </Field>
    {mode === 'password'
      ? <Field label={t('Passwort')} hint={hasSecret ? t('Gespeichert. Leer lassen, um es zu behalten.') : undefined}>
          <Input type="password" autoComplete="new-password" value={secret} placeholder={hasSecret ? '••••••••' : ''} onChange={(_, d) => onSecret(d.value)} />
        </Field>
      : <>
          <Field label={t('Privater Schlüssel')} hint={hasSecret && !secret ? t('Gespeichert. Leer lassen, um ihn zu behalten.') : t('PEM oder OpenSSH, ohne Passphrase.')}>
            <Textarea rows={3} value={secret.startsWith('-----BEGIN') && pub ? t('(neu erzeugt)') : secret} readOnly={!!pub} onChange={(_, d) => onSecret(d.value)}
              style={{ fontFamily: tokens.fontFamilyMonospace }} />
          </Field>
          <div><Button icon={<Key20Regular />} onClick={newKey}>{t('Schlüssel erzeugen')}</Button></div>
          {pub && <Field label={t('Öffentlicher Schlüssel')}>
            <div style={{ display: 'flex', gap: 6, alignItems: 'flex-start' }}>
              <Textarea readOnly rows={3} value={pub} style={{ flex: 1, fontFamily: tokens.fontFamilyMonospace }} />
              <Button icon={<Copy20Regular />} onClick={() => { copyText(pub); notify(t('Kopiert')) }} aria-label={t('Kopieren')} />
            </div>
          </Field>}
        </>}
    <Field label={t('Gleichzeitige Verbindungen')}>
      <SpinButton value={p.connections} min={1} max={16} style={{ width: 110 }} onChange={(_, d) => set({ connections: d.value ?? (Number(d.displayValue) || 4) })} />
    </Field>
    <Text size={200} style={muted}>{t('Beim ersten Verbinden merkt sich dmOffice den Schlüssel des Servers. Meldet sich später ein anderer, bricht die Verbindung ab.')}</Text>
    {admin && p.host && <div><Button size="small" appearance="subtle" onClick={forget}>{t('Gemerkten Server-Schlüssel zurücksetzen')}</Button></div>}
  </>
}
