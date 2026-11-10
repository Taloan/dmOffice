import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Option, Spinner, Switch, Text, Tooltip, makeStyles, tokens,
} from '@fluentui/react-components'
import { ArrowClockwise20Regular, ArrowUp20Regular, Delete20Regular, Folder20Filled, Prohibited16Filled } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { AudioSource, loadSources, Mount, sourceName } from './library'

const useStyles = makeStyles({
  row: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '6px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  head: { display: 'flex', alignItems: 'center', gap: '8px' },
  name: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  muted: { color: tokens.colorNeutralForeground3 },
  off: { color: '#E5484D', display: 'inline-flex', verticalAlign: 'middle', marginRight: '4px' },
  browse: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, maxHeight: '220px', overflowY: 'auto', padding: '4px' },
  dir: { display: 'flex', alignItems: 'center', gap: '6px', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
})

/**
 * Audiothek-Ordner: Ordner aus externen Ordnern (Serverordner, eigene WebDAV-Ordner) in die Audiothek aufnehmen, je Ordner
 * überwacht (alle 15 Minuten neu eingelesen) oder nur auf Knopfdruck. Die Dateien bleiben, wo sie sind.
 */
export function AudioFoldersDialog({ onClose }: { onClose: (changed: boolean) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [data, setData] = useState<{ sources: AudioSource[]; mounts: Mount[] } | null>(null)
  const [changed, setChanged] = useState(false)
  const [mount, setMount] = useState('')
  const [path, setPath] = useState('')
  const [dirs, setDirs] = useState<string[] | null>(null)
  const load = () => loadSources().then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Während eingelesen wird, den Stand nachladen
  useEffect(() => {
    if (!data?.sources.some(x => x.scanning || x.pending > 0)) return
    const h = setTimeout(() => { load(); setChanged(true) }, 3000)
    return () => clearTimeout(h)
  }, [data]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!mount) { setDirs(null); return }
    setDirs(null)
    api.get<{ items: { name: string; isDir: boolean }[] }>('/api/files/external/list?' + q({ mount, path }))
      .then(r => setDirs(r.items.filter(i => i.isDir && !i.name.startsWith('.') && !i.name.startsWith('@')).map(i => i.name).sort((a, b) => a.localeCompare(b))))
      .catch(e => { setDirs([]); fail(e) })
  }, [mount, path]) // eslint-disable-line react-hooks/exhaustive-deps
  if (!data) return null
  const mountName = (id: string) => data.mounts.find(m => m.id === id)?.name ?? id
  const act = async (f: () => Promise<unknown>) => { try { await f(); setChanged(true); load() } catch (e) { fail(e) } }
  const add = () => act(async () => { await api.post('/api/audio/sources', { mount, path, watch: true }); setMount(''); setPath('') })
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(changed) }}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>{t('Audiothek-Ordner')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>{t('Die Audiothek zeigt die Musik aus diesen Ordnern (MP3, M4A, AAC, FLAC, OGG, Opus). Die Dateien bleiben, wo sie sind; dmOffice liest nur.')}</Text>
            {data.sources.length === 0 && <Text className={s.muted}>{t('Noch keine Audiothek-Ordner.')}</Text>}
            {data.sources.map(x => (
              <div key={x.id} className={s.row}>
                <div className={s.head}>
                  <div className={s.name}>
                    <Text weight="semibold">{!x.available && <span className={s.off}><Prohibited16Filled /></span>}{sourceName(x, data.mounts)}</Text>
                    <Text size={200} className={s.muted}>
                      {!x.available ? t('Gerade nicht erreichbar')
                        : x.scanning && !x.lastScan ? t('Wird eingelesen …')
                        : x.error ? t('Fehler: {error}', { error: x.error })
                        : x.lastScan ? t('{n} Titel · eingelesen {when}', { n: x.count.toLocaleString(), when: new Date(x.lastScan).toLocaleString() })
                        : t('Wird gleich eingelesen …')}
                      {x.pending ? ' · ' + t('{n} noch ohne Tags', { n: x.pending.toLocaleString() }) : ''}
                    </Text>
                  </div>
                  {(x.scanning || x.pending > 0) && <Spinner size="tiny" />}
                  <Tooltip content={t('Jetzt neu einlesen')} relationship="label">
                    <Button appearance="subtle" icon={<ArrowClockwise20Regular />} disabled={x.scanning} onClick={() => act(() => api.post(`/api/audio/sources/${x.id}/scan`))} />
                  </Tooltip>
                  <Tooltip content={t('Aus der Audiothek nehmen (die Dateien bleiben)')} relationship="label">
                    <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => act(() => api.del(`/api/audio/sources/${x.id}`))} />
                  </Tooltip>
                </div>
                <div style={{ marginLeft: -8 }}>
                  <Tooltip content={t('Überwachen: alle 15 Minuten neu einlesen – neue Titel erscheinen von selbst, gelöschte verschwinden.')} relationship="description">
                    <Switch checked={x.watch} label={t('Überwachen')} onChange={(_, d) => act(() => api.put(`/api/audio/sources/${x.id}`, { watch: d.checked }))} />
                  </Tooltip>
                </div>
              </div>
            ))}
            <Text weight="semibold">{t('Ordner hinzufügen')}</Text>
            {data.mounts.length === 0
              ? <Text className={s.muted}>{t('Es gibt noch keine externen Ordner. Ordner auf dem Server lassen sich über die Verwaltung freigeben; eigene WebDAV-Ordner bindest du in der Dateiablage unter „Externe Ordner“ ein.')}</Text>
              : <>
                <Dropdown placeholder={t('Externen Ordner wählen')} value={mount ? mountName(mount) : ''} selectedOptions={mount ? [mount] : []}
                  onOptionSelect={(_, d) => { setMount(d.optionValue ?? ''); setPath('') }}>
                  {data.mounts.map(m => <Option key={m.id} value={m.id}>{m.name}</Option>)}
                </Dropdown>
                {mount && (
                  <div className={s.browse}>
                    <Text size={200} className={s.muted} block style={{ padding: '2px 6px' }}>{mountName(mount) + (path ? ' › ' + path.split('/').join(' › ') : '')}</Text>
                    {path && <div className={s.dir} onClick={() => setPath(path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')}><ArrowUp20Regular />{t('Übergeordneter Ordner')}</div>}
                    {dirs === null ? <Spinner size="tiny" /> : dirs.map(d => (
                      <div key={d} className={s.dir} onClick={() => setPath(path ? `${path}/${d}` : d)}><Folder20Filled color="#E8A33D" />{d}</div>
                    ))}
                  </div>
                )}
              </>}
          </DialogContent>
          <DialogActions>
            {mount && <Button appearance="primary" onClick={add}>{path ? t('„{name}“ hinzufügen', { name: path.slice(path.lastIndexOf('/') + 1) }) : t('Ganzen Ordner hinzufügen')}</Button>}
            <Button onClick={() => onClose(changed)}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
