import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Option, Spinner, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Doc, Mount, Profile } from './model'

/** Ablageprofile, die zum Ort der Dokumente passen: im Tresor die mit Ziel Tresor, sonst Dateiablage bzw. derselbe externe Ordner. */
export const profilesFor = (all: Profile[], docs: Doc[], tresor: boolean) => {
  if (tresor) return all.filter(p => p.target === 'tresor')
  const mounts = new Set(docs.map(d => d.mount ?? null))
  if (mounts.size !== 1) return []
  const mount = [...mounts][0]
  return all.filter(p => mount ? p.target === 'external' && p.mount === mount : p.target === 'files')
}

/**
 * Nach Vorgabe ablegen mit Wahl des Ablageprofils (mehrere Profile für diesen Ort): Ordner und Dateiname kommen danach aus dem
 * gewählten Profil, das ab dann für die Dokumente gilt. Vorbelegt: das Profil, das alle schon haben, sonst der Standard.
 */
export function RefileDialog({ docs, tresor, profiles, onClose, onDone }: { docs: Doc[]; tresor: boolean; profiles: Profile[]; onClose: () => void; onDone: () => void }) {
  const { fail } = useApp()
  const [mounts, setMounts] = useState<Mount[]>([])
  const common = docs.every(d => d.profile === docs[0]?.profile) ? docs[0]?.profile : null
  const [pick, setPick] = useState<string | null>((profiles.find(p => p.id === common) ?? profiles.find(p => p.default) ?? profiles[0])?.id ?? null)
  const [busy, setBusy] = useState(false)
  useEffect(() => { api.get<Mount[]>('/api/dms/mounts').then(setMounts).catch(() => {}) }, [])
  const where = (p: Profile) => [
    p.target === 'tresor' ? t('Tresor') : p.target === 'external' ? mounts.find(m => m.id === p.mount)?.name ?? t('Externer Ordner') : t('Dateiablage'),
    ...(p.folder ? p.folder.split('/') : []), p.folders || '…',
  ].join(' › ')
  const chosen = profiles.find(p => p.id === pick) ?? null
  const run = async () => {
    if (!chosen) return
    setBusy(true)
    try { await api.post('/api/dms/docs/refile?' + q({ area: tresor ? 'tresor' : undefined }), { ids: docs.map(d => d.id), profile: chosen.id }); onDone() }
    catch (e) { fail(e); setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open && !busy) onClose() }}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{t('Nach Vorgabe ablegen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>{docs.length === 1 ? t('Ordner und Dateiname kommen aus dem gewählten Ablageprofil. Es gilt danach für dieses Dokument.')
              : t('Ordner und Dateinamen kommen aus dem gewählten Ablageprofil. Es gilt danach für diese {n} Dokumente.', { n: docs.length })}</Text>
            <Field label={t('Ablageprofil')}>
              <Dropdown value={chosen?.name ?? ''} selectedOptions={pick ? [pick] : []} onOptionSelect={(_, d) => setPick(d.optionValue ?? null)}>
                {profiles.map(p => <Option key={p.id} value={p.id} text={p.name}>
                  <span style={{ display: 'flex', flexDirection: 'column' }}><span>{p.name}</span><span style={{ fontSize: 12, color: tokens.colorNeutralForeground3 }}>{where(p)}</span></span>
                </Option>)}
              </Dropdown>
            </Field>
            {chosen && <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              <Text weight="semibold">{t('Ziel')}</Text>
              <Text>{where(chosen)} › {chosen.names}</Text>
            </div>}
          </DialogContent>
          <DialogActions>
            <Button disabled={busy} onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || !chosen} icon={busy ? <Spinner size="tiny" /> : undefined} onClick={run}>{t('Ablegen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
