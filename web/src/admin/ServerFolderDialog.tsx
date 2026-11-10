import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { ArrowUp20Regular, Folder20Regular, HardDrive20Regular, LockClosed16Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { t } from '../i18n'

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', maxHeight: 'calc(100vh - 320px)', minHeight: '180px', overflowY: 'auto', marginTop: '8px', gap: '2px' },
  row: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  off: { cursor: 'default', color: tokens.colorNeutralForeground4, ':hover': { backgroundColor: 'transparent' } },
  name: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  path: { display: 'flex', alignItems: 'center', gap: '4px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
})

interface Listing { path: string | null; parent: string | null; dirs: { name: string; path: string; blocked: boolean }[] }

/**
 * Ordner auf dem Server wählen (Verwaltung): zuerst die im Container eingebundenen Ordner, dann deren Unterordner.
 * Das Datenverzeichnis von dmOffice ist gesperrt.
 */
export function ServerFolderDialog({ title, start, onPick, onClose }: { title: string; start?: string; onPick: (path: string) => void; onClose: () => void }) {
  const s = useStyles()
  const [path, setPath] = useState<string | null>(start?.trim() || null)
  const [data, setData] = useState<Listing | null>(null)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => {
    setData(null); setError(null)
    api.get<Listing>('/api/admin/server-folders?' + q({ path: path ?? undefined }))
      .then(setData)
      .catch(() => { if (path) setPath(null); else setError(t('Die Ordner des Servers ließen sich nicht lesen.')) })
  }, [path])
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560, width: '100%' }}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>
            {path === null
              ? <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Im Container eingebundene Ordner – weitere bindest du in der Container-Vorlage ein (Regel: im Container unter /external/<Name>, etwa die Unraid-Freigabe /mnt/user/musik nach /external/musik).')}</Text>
              : (
                <div className={s.path}>
                  <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} onClick={() => setPath(data?.parent && data.parent !== data.path ? data.parent : null)} aria-label={t('Nach oben')} />
                  <Folder20Regular /><span>{path}</span>
                </div>
              )}
            <div className={s.list}>
              {error && <Text>{error}</Text>}
              {!data && !error && <Spinner size="small" />}
              {data?.dirs.length === 0 && <Text style={{ color: tokens.colorNeutralForeground3, padding: 8 }}>{path === null ? t('Im Container ist kein Ordner eingebunden.') : t('Keine Unterordner.')}</Text>}
              {data?.dirs.map(d => (
                <div key={d.path} className={d.blocked ? `${s.row} ${s.off}` : s.row} onClick={() => !d.blocked && setPath(d.path)}
                  title={d.blocked ? t('Das Datenverzeichnis von dmOffice lässt sich nicht freigeben.') : d.path}>
                  {path === null ? <HardDrive20Regular /> : <Folder20Regular />}
                  <span className={s.name}>{d.name}</span>
                  {d.blocked && <LockClosed16Regular />}
                </div>
              ))}
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!path || !data} onClick={() => path && onPick(path)}>{t('Diesen Ordner wählen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
