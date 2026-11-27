import { Button, MenuDivider, MenuItem, Text, makeStyles, tokens } from '@fluentui/react-components'
import { Add20Regular, Cloud20Regular, Delete20Regular, Edit20Regular, Globe20Regular, Server20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api, featureOn } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { ContextMenu, askConfirm, useContextMenu } from '../shell/context'
import { AddExternalDialog, EditExternalDialog, ExternalMount } from '../files/ExternalView'
import { Hint, Section } from './parts'
import { dblOpen } from '../shell/rowOpen' // [feature:rowdblopen]

const useStyles = makeStyles({
  page: { display: 'flex', flexDirection: 'column', gap: '20px' },
  list: { display: 'flex', flexDirection: 'column', gap: '2px', maxWidth: '720px' },
  row: { display: 'flex', alignItems: 'center', gap: '12px', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground2 },
  name: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  sub: { color: tokens.colorNeutralForeground3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
})

export const kindLabel = (k: ExternalMount['kind']) => k === 'onedrive' ? 'OneDrive' : k === 'pcloud' ? 'pCloud' : k === 'folder' ? t('Serverordner') : 'WebDAV'

export interface MountsResponse { mounts: ExternalMount[]; targets: ExternalMount[]; canAdd: boolean; webDav?: boolean; sftp?: boolean; oneDrive?: boolean; pCloud?: boolean }

/**
 * Externe Ziele (Version 2): Zugänge zu WebDAV-Diensten, OneDrive oder pCloud - beliebig viele je Art, soweit die Verwaltung
 * die Art erlaubt. Zugangsdaten und Anmeldung liegen nur hier; in Dateien bindet man ein Ziel samt Ordner darin ein, in der
 * Datensicherung wählt man ein Ziel und den Ordner, in den gesichert wird.
 */
export function ExternalTargetsSettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [data, setData] = useState<MountsResponse | null>(null)
  const [adding, setAdding] = useState(false)
  const [editing, setEditing] = useState<ExternalMount | null>(null)
  // Rechtsklick auf ein externes Ziel (Kennung ctxall): dieselben Aktionen wie die Knöpfe
  const menu = useContextMenu<ExternalMount>()
  const ctxall = featureOn('ctxall')
  const load = useCallback(() => api.get<MountsResponse>('/api/files/external/mounts').then(setData).catch(fail), [fail])
  useEffect(() => { load() }, [load])
  const targets = data?.targets ?? []
  const kinds = [data?.webDav && 'WebDAV', data?.sftp && 'SFTP', data?.oneDrive && 'OneDrive', data?.pCloud && 'pCloud'].filter(Boolean).join(', ')
  const used = (m: ExternalMount) => (data?.mounts ?? []).filter(x => x.access === m.id)
  const remove = async (m: ExternalMount) => {
    const n = used(m).length
    if (!await askConfirm(t('Externes Ziel entfernen'), t('„{name}“ entfernen? Die Dateien dort bleiben, wie sie sind.', { name: m.name })
      + (n ? ' ' + t('Seine Einbindungen in Dateien ({n}) entfallen; was sie verwendet (Galerie, Audiothek, Importprofile, DMS, Datenexport), wird abgeschaltet.', { n }) : ''), t('Entfernen'))) return
    try { await api.del('/api/files/external/mounts/' + encodeURIComponent(m.id)); notify(t('Entfernt')); load(); window.dispatchEvent(new Event('dmoffice:external-changed')) } catch (e) { fail(e) }
  }
  return (
    <div className={s.page}>
      <Text>{t('Zugänge zu eigenen Speichern bei WebDAV-Diensten, bei OneDrive oder pCloud. Zugangsdaten und Anmeldung liegen nur hier. In Dateien lässt sich ein externes Ziel samt Ordner darin einbinden; im Datenexport wählt man ein externes Ziel und den Ordner, in den exportiert wird.')}</Text>
      <Section title={t('Angelegt')} hint={data && !data.canAdd ? t('Auf diesem Server sind keine externen Ziele für Benutzer freigegeben (WebDAV, OneDrive, pCloud).') : kinds ? t('Freigegeben: {kinds}', { kinds }) : undefined}>
        <div className={s.list}>
          {data && targets.length === 0 && <Hint>{t('Noch kein externes Ziel angelegt.')}</Hint>}
          {targets.map(m => (
            <div key={m.id} className={s.row} onContextMenu={ctxall ? e => menu.open(e, m) : undefined}
              {...dblOpen(() => setEditing(m))} // [feature:rowdblopen]
            >
              {m.sftp ? <Server20Regular /> : m.kind === 'webdav' ? <Globe20Regular /> : <Cloud20Regular />}
              <div className={s.name}>
                <Text weight="semibold">{m.name}</Text>
                <Text size={200} className={s.sub}>
                  {m.sftp ? 'SFTP' : kindLabel(m.kind)}{m.account ? ' · ' + m.account : ''}
                  {used(m).length ? ' · ' + t('in Dateien: {names}', { names: used(m).map(x => x.name).join(', ') }) : ''}
                </Text>
              </div>
              <Button appearance="subtle" icon={<Edit20Regular />} aria-label={t('Bearbeiten')} title={t('Bearbeiten')} onClick={() => setEditing(m)} />
              <Button appearance="subtle" icon={<Delete20Regular />} aria-label={t('Entfernen')} title={t('Entfernen')} onClick={() => remove(m)} />
            </div>
          ))}
        </div>
        {menu.ctx && <ContextMenu at={menu.ctx.at} onClose={menu.close}>
          <MenuItem icon={<Edit20Regular />} onClick={() => setEditing(menu.ctx!.data)}>{t('Bearbeiten')}</MenuItem>
          <MenuDivider />
          <MenuItem icon={<Delete20Regular />} onClick={() => remove(menu.ctx!.data)}>{t('Entfernen')}</MenuItem>
        </ContextMenu>}
        {data?.canAdd && <div style={{ marginTop: 10 }}><Button icon={<Add20Regular />} onClick={() => setAdding(true)}>{t('Externes Ziel anlegen')}</Button></div>}
      </Section>
      {adding && data && <AddExternalDialog webDav={!!data.webDav} sftp={!!data.sftp} oneDrive={!!data.oneDrive} pCloud={!!data.pCloud} onClose={() => setAdding(false)}
        onAdded={() => { setAdding(false); load(); window.dispatchEvent(new Event('dmoffice:external-changed')) }} />}
      {editing && <EditExternalDialog target={editing} onClose={() => setEditing(null)}
        onSaved={() => { setEditing(null); notify(t('Gespeichert')); load(); window.dispatchEvent(new Event('dmoffice:external-changed')) }} />}
    </div>
  )
}
