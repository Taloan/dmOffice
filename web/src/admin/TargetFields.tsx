import { Button, Field, Input, Radio, RadioGroup } from '@fluentui/react-components'
import { useState } from 'react'
import { FolderOpen20Regular } from '@fluentui/react-icons'
import { t } from '../i18n'
import { ServerFolderDialog } from './ServerFolderDialog'
import { featureOn } from '../api'
import { SftpFields, isSftp } from '../files/SftpFields'

// Ein Ziel (S3, WebDAV, Ordner auf dem Server) - für den Storage Extender und für ein eigenes Core-Backup-Ziel
export type Target = 's3' | 'webdav' | 'folder'
export interface TargetValues { endpoint: string; region: string; bucket: string; prefix: string; accessKey: string; secretKey: string; webDavUrl: string; webDavUser: string; webDavPassword: string; folder: string }

/** Aus dem Stand des Servers (Geheimnisse kommen nie zurück - die Felder bleiben leer). */
export function valuesOf(s: { endpoint?: string | null; region?: string | null; bucket?: string | null; prefix?: string | null; accessKey?: string | null; webDavUrl?: string | null; webDavUser?: string | null; folder?: string | null }): TargetValues {
  return { endpoint: s.endpoint ?? '', region: s.region ?? '', bucket: s.bucket ?? '', prefix: s.prefix ?? '', accessKey: s.accessKey ?? '', secretKey: '',
    webDavUrl: s.webDavUrl ?? '', webDavUser: s.webDavUser ?? '', webDavPassword: '', folder: s.folder ?? '' }
}

/** Nur im Heimnetz erreichbar (Ordner, private Adresse)? Ein Server woanders (vServer) kommt dann nach einem Ausfall nicht dran. */
export function homeOnly(target: Target, url: string | null | undefined): boolean {
  if (target === 'folder') return true
  try {
    const h = new URL(url ?? '').hostname
    return /^(localhost|127\.|10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)|\.(local|lan|home|fritz\.box)$|^[^.]+$/.test(h)
  } catch { return false }
}

export function TargetFields({ target, onTarget, v, onChange, hasSecret, hasWebDavPassword, kindLocked }: {
  target: Target; onTarget: (t: Target) => void; v: TargetValues; onChange: (k: keyof TargetValues, value: string) => void
  hasSecret: boolean; hasWebDavPassword: boolean; kindLocked?: boolean
}) {
  const [browse, setBrowse] = useState(false)
  const set = (k: keyof TargetValues) => (_: unknown, d: { value: string }) => onChange(k, d.value)
  // SFTP läuft auf dem Server als WebDAV-Ziel mit sftp://-Adresse - hier eine eigene Art
  const sftp = target === 'webdav' && isSftp(v.webDavUrl)
  const pick = (value: string) => {
    if (value === 'sftp') { onTarget('webdav'); if (!isSftp(v.webDavUrl)) onChange('webDavUrl', 'sftp://') }
    else { if (value === 'webdav' && isSftp(v.webDavUrl)) onChange('webDavUrl', ''); onTarget(value as Target) }
  }
  return <>
    <Field label={t('Art')}>
      <RadioGroup value={sftp ? 'sftp' : target} onChange={(_, d) => pick(d.value)} disabled={kindLocked}>
        <Radio value="folder" label={t('Ordner auf dem Server')} />
        <Radio value="webdav" label={t('WebDAV')} />
        {(featureOn('sftp') || sftp) && <Radio value="sftp" label={t('SFTP')} />}
        <Radio value="s3" label={t('S3')} />
      </RadioGroup>
    </Field>
    {sftp && <SftpFields url={v.webDavUrl} onUrl={x => onChange('webDavUrl', x)} user={v.webDavUser} onUser={x => onChange('webDavUser', x)}
      secret={v.webDavPassword} onSecret={x => onChange('webDavPassword', x)} hasSecret={hasWebDavPassword} admin />}
    {target === 'webdav' && !sftp && <>
      <Field label={t('Adresse')}>
        <Input value={v.webDavUrl} onChange={set('webDavUrl')} />
      </Field>
      <Field label={t('Benutzername')}><Input value={v.webDavUser} onChange={set('webDavUser')} autoComplete="off" /></Field>
      <Field label={t('Passwort')} hint={hasWebDavPassword ? t('Ein Passwort ist gespeichert – leer lassen, um es zu behalten.') : undefined}>
        <Input type="password" autoComplete="new-password" value={v.webDavPassword} onChange={set('webDavPassword')} placeholder={hasWebDavPassword ? '••••••••' : ''} />
      </Field>
    </>}
    {target === 'folder' && (
      <Field label={t('Ordner')} hint={t('Die Platte muss in der Container-Vorlage unter /external eingebunden sein.')}>
        <Input value={v.folder} onChange={set('folder')} placeholder="/external/backup"
          contentAfter={<Button size="small" appearance="transparent" icon={<FolderOpen20Regular />} onClick={() => setBrowse(true)} aria-label={t('Ordner auf dem Server wählen')} title={t('Ordner auf dem Server wählen')} />} />
      </Field>
    )}
    {browse && <ServerFolderDialog title={t('Ordner auf dem Server wählen')} start={v.folder} onClose={() => setBrowse(false)}
      onPick={p => { onChange('folder', p); setBrowse(false) }} />}
    {target === 's3' && <>
      <Field label={t('Adresse')}>
        <Input value={v.endpoint} onChange={set('endpoint')} />
      </Field>
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <Field label={t('Region')} style={{ flex: 1, minWidth: 140 }}><Input value={v.region} onChange={set('region')} /></Field>
        <Field label={t('Bucket')} style={{ flex: 1, minWidth: 140 }}><Input value={v.bucket} onChange={set('bucket')} placeholder="dmoffice" /></Field>
        <Field label={t('Ordner im Bucket')} style={{ flex: 1, minWidth: 140 }}><Input value={v.prefix} onChange={set('prefix')} placeholder="dmoffice" /></Field>
      </div>
      <Field label={t('Zugangsschlüssel (Key ID)')}><Input value={v.accessKey} onChange={set('accessKey')} autoComplete="off" /></Field>
      <Field label={t('Geheimer Schlüssel')} hint={hasSecret ? t('Ein Schlüssel ist gespeichert – leer lassen, um ihn zu behalten.') : undefined}>
        <Input type="password" autoComplete="new-password" value={v.secretKey} onChange={set('secretKey')} placeholder={hasSecret ? '••••••••' : ''} />
      </Field>
    </>}
  </>
}
