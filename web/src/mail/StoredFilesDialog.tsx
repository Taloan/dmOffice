import { Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { ArrowLeft20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { Item, ownSource, Thumb } from '../files/common'
import { t } from '../i18n'

const useStyles = makeStyles({
  surface: { width: 'min(720px, calc(100vw - 24px))', maxWidth: 'none', height: 'min(640px, calc(100vh - 48px))', display: 'flex', flexDirection: 'column' },
  body: { display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0 },
  list: { flex: 1, minHeight: 0, overflowY: 'auto', display: 'flex', flexDirection: 'column' },
  row: { display: 'flex', alignItems: 'center', gap: '12px', padding: '6px 4px', cursor: 'pointer', borderRadius: tokens.borderRadiusMedium,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  thumb: { width: '44px', height: '44px', flexShrink: 0, borderRadius: '6px', overflow: 'hidden', display: 'flex', alignItems: 'center', justifyContent: 'center' },
  name: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
})

/** Dateien aus der eigenen Dateiablage zum Anhaengen waehlen: Ordner oeffnen, Dateien ankreuzen, mit Vorschaubildern. */
export function StoredFilesDialog({ onPick, onClose }: { onPick: (items: Item[]) => void; onClose: () => void }) {
  const s = useStyles()
  const [path, setPath] = useState('')
  const [items, setItems] = useState<Item[] | null>(null)
  const [picked, setPicked] = useState<Item[]>([])
  useEffect(() => {
    setItems(null)
    api.get<{ items: Item[] }>('/api/files/?' + q({ path })).then(r => setItems([...r.items].sort((a, b) =>
      Number(b.isDir) - Number(a.isDir) || a.name.localeCompare(b.name)))).catch(() => setItems([]))
  }, [path])
  const toggle = (i: Item) => setPicked(p => p.some(x => x.path === i.path) ? p.filter(x => x.path !== i.path) : [...p, i])
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface className={s.surface}>
        <DialogBody className={s.body}>
          <DialogTitle>
            <span style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              {path && <Button appearance="subtle" icon={<ArrowLeft20Regular />} aria-label={t('Zurück')} onClick={() => setPath(path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')} />}
              {path ? path.split('/').pop() : t('Dateien')}
            </span>
          </DialogTitle>
          <DialogContent className={s.list}>
            {items == null ? <Spinner /> : items.length === 0 ? <Text className={s.sub}>{t('Dieser Ordner ist leer.')}</Text> : items.map(i => {
              const on = picked.some(x => x.path === i.path)
              return (
                <div key={i.path} className={s.row} onClick={() => i.isDir ? setPath(i.path) : toggle(i)}>
                  <div className={s.thumb}><Thumb item={i} source={ownSource} size={96} /></div>
                  <div className={s.name}>
                    <Text truncate>{i.name}</Text>
                    {!i.isDir && <span className={s.sub}>{fmtSize(i.size)}</span>}
                  </div>
                  {!i.isDir && <Checkbox checked={on} onChange={() => toggle(i)} onClick={e => e.stopPropagation()} />}
                </div>
              )
            })}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!picked.length} onClick={() => onPick(picked)}>{t('Anfügen ({n})', { n: picked.length })}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
