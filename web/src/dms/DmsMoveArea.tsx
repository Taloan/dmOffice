import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Option, Spinner, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { Doc, Mount, Profile } from './model'

/**
 * In den Tresor verschieben bzw. aus dem Tresor holen: Ziel-Ablageprofil wählen (vorbelegt mit dem Standard dieses Ziels) und sehen,
 * wo die Dokumente danach liegen. Aus dem Tresor kommen Dateiablage und externe Ordner in Frage, in den Tresor nur Profile mit Ziel Tresor.
 */
export function MoveAreaDialog({ docs, toTresor, onClose, onMoved }: { docs: Doc[]; toTresor: boolean; onClose: () => void; onMoved: (text: string) => void }) {
  const { fail } = useApp()
  const [profiles, setProfiles] = useState<Profile[] | null>(null)
  const [mounts, setMounts] = useState<Mount[]>([])
  const [pick, setPick] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    api.get<Profile[]>('/api/dms/profiles?' + q({ lang })).then(all => {
      const usable = all.filter(p => toTresor ? p.target === 'tresor' : p.target !== 'tresor')
      setProfiles(usable)
      setPick((usable.find(p => p.default && (p.target === 'tresor') === toTresor) ?? usable.find(p => p.target === (toTresor ? 'tresor' : 'files')) ?? usable[0])?.id ?? null)
    }).catch(fail)
    api.get<Mount[]>('/api/dms/mounts').then(setMounts).catch(() => {})
  }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const where = (p: Profile) => [
    p.target === 'tresor' ? t('Tresor') : p.target === 'external' ? mounts.find(m => m.id === p.mount)?.name ?? t('Externer Ordner') : t('Dateiablage'),
    ...(p.folder ? p.folder.split('/') : []), p.folders || '…',
  ].join(' › ')
  const chosen = profiles?.find(p => p.id === pick) ?? null
  const unconfirmed = docs.filter(d => d.inbox).length
  const n = docs.length
  const move = async () => {
    if (!chosen) return
    setBusy(true)
    try {
      const r = await api.post<{ moved: number; notPlaced: number; already: number; merged: number; failed: { id: number; title: string; error: string }[] }>('/api/dms/docs/move-area', { ids: docs.map(d => d.id), to: toTresor ? 'tresor' : 'files', profile: chosen.id })
      onMoved([r.moved === 1 ? t('1 Dokument verschoben') : t('{n} Dokumente verschoben', { n: r.moved }),
        r.already ? (toTresor ? t('{n} gibt es im Tresor schon (gleiche Datei)', { n: r.already }) : t('{n} gibt es in der Ablage schon (gleiche Datei)', { n: r.already })) : '',
        r.notPlaced ? t('{n} noch nicht abgelegt', { n: r.notPlaced }) : '',
        r.merged ? (toTresor ? t('{n} lagen schon im Tresor - Kopie in der Ablage gelöscht', { n: r.merged }) : t('{n} lagen schon in der Ablage - Kopie im Tresor gelöscht', { n: r.merged })) : '',
        ...r.failed.map(f => t('Nicht verschoben: „{title}“ - {error}', { title: f.title, error: f.error })),
      ].filter(Boolean).join(' · '))
    } catch (e) { fail(e); setBusy(false) }
  }

  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open && !busy) onClose() }}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{toTresor ? t('In den Tresor verschieben') : t('Aus dem Tresor holen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>{toTresor
              ? (n === 1 ? t('Das Dokument kommt samt Angaben, Text und Original in den Tresor und wird im offenen Bereich endgültig gelöscht (nicht in den Papierkorb).')
                : t('{n} Dokumente kommen samt Angaben, Text und Original in den Tresor und werden im offenen Bereich endgültig gelöscht (nicht in den Papierkorb).', { n }))
              : (n === 1 ? t('Das Dokument kommt samt Angaben, Text und Original aus dem Tresor in den offenen Bereich; dort liegt es unverschlüsselt.')
                : t('{n} Dokumente kommen samt Angaben, Text und Original aus dem Tresor in den offenen Bereich; dort liegen sie unverschlüsselt.', { n }))}</Text>
            {profiles === null ? <Spinner size="tiny" /> : profiles.length === 0
              ? <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{toTresor ? t('Es gibt kein Ablageprofil mit Ziel Tresor.') : t('Es gibt kein Ablageprofil außerhalb des Tresors.')}</Text>
              : <Field label={t('Ablageprofil')}>
                <Dropdown value={chosen?.name ?? ''} selectedOptions={pick ? [pick] : []} onOptionSelect={(_, d) => setPick(d.optionValue ?? null)}>
                  {profiles.map(p => <Option key={p.id} value={p.id} text={p.name}>
                    <span style={{ display: 'flex', flexDirection: 'column' }}><span>{p.name}</span><span style={{ fontSize: 12, color: tokens.colorNeutralForeground3 }}>{where(p)}</span></span>
                  </Option>)}
                </Dropdown>
              </Field>}
            {chosen && <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
              <Text weight="semibold">{t('Ziel')}</Text>
              <Text>{where(chosen)}</Text>
              {unconfirmed > 0 && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Unbestätigte liegen dort bis zum Bestätigen im Ordner „{inbox}“.', { inbox: chosen.inboxFolder || '.INBOX' })}</Text>}
            </div>}
          </DialogContent>
          <DialogActions>
            <Button disabled={busy} onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || !chosen} icon={busy ? <Spinner size="tiny" /> : undefined} onClick={move}>
              {toTresor ? t('In den Tresor') : t('Aus dem Tresor holen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
