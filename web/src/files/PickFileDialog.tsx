import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Spinner, makeStyles, tokens } from '@fluentui/react-components'
import { ArrowUp20Regular, Folder20Regular, Search20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { t } from '../i18n'
import { fmtDay } from '../shell/hooks'
import { Item, KindIcon, Thumb, hasThumb, ownSource, parentOf } from './common'

const useStyles = makeStyles({
  surface: { maxWidth: '560px', width: '100%' },
  list: { display: 'flex', flexDirection: 'column', maxHeight: 'calc(100vh - 340px)', minHeight: '200px', overflowY: 'auto', marginTop: '8px', gap: '2px' },
  row: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  // Fotos und Videos mit kleinem Vorschaubild, damit man sieht, was man wählt
  thumb: { width: '44px', height: '44px', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', flexShrink: 0, backgroundColor: tokens.colorNeutralBackground3 },
  thumbImg: { width: '100%', height: '100%', display: 'block' },
  name: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, whiteSpace: 'nowrap' },
  path: { display: 'flex', alignItems: 'center', gap: '4px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, marginTop: '8px' },
  empty: { padding: '16px', textAlign: 'center', color: tokens.colorNeutralForeground3 },
})

/**
 * Eine Datei aus der eigenen Dateiablage auswählen: Ordner durchblättern oder nach dem Namen suchen. Nur Dateien mit
 * einer der Endungen in [accept] lassen sich wählen; neueste zuerst.
 */
export function PickFileDialog({ title, accept, start = '', onPick, onClose }: {
  title: string; accept: string[]; start?: string; onPick: (path: string) => void; onClose: () => void
}) {
  const s = useStyles()
  const [path, setPath] = useState(start)
  const [query, setQuery] = useState('')
  const [items, setItems] = useState<Item[] | null>(null)
  const fits = (i: Item) => accept.some(e => i.name.toLowerCase().endsWith(e.toLowerCase()))
  useEffect(() => {
    setItems(null)
    const term = query.trim()
    const timer = setTimeout(() => {
      const load = term
        ? api.get<{ items: Item[] }>('/api/files/search?' + q({ q: term })).then(d => d.items.filter(i => !i.isDir))
        : api.get<{ items: Item[] }>('/api/files/?' + q({ path })).then(d => d.items)
      load.then(list => setItems(list.filter(i => !i.shared && !i.owner && (i.isDir || fits(i)))
        .sort((a, b) => Number(b.isDir) - Number(a.isDir) || (a.isDir ? a.name.localeCompare(b.name) : Date.parse(b.modified) - Date.parse(a.modified)))))
        .catch(() => setItems([]))
    }, term ? 250 : 0)
    return () => clearTimeout(timer)
  }, [path, query]) // eslint-disable-line react-hooks/exhaustive-deps
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>
            <Input contentBefore={<Search20Regular />} placeholder={t('In der Dateiablage suchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ width: '100%' }} />
            {!query.trim() && (
              <div className={s.path}>
                <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} disabled={!path} onClick={() => setPath(parentOf(path))} aria-label={t('Nach oben')} />
                <Folder20Regular /> <span>{path || t('Dateien')}</span>
              </div>
            )}
            <div className={s.list}>
              {items == null && <Spinner size="small" />}
              {items?.length === 0 && <div className={s.empty}>{query.trim() ? t('Keine passende Datei gefunden.') : t('Hier liegt keine passende Datei.')}</div>}
              {items?.map(i => (
                <div key={i.path} className={s.row} onClick={() => (i.isDir ? (setQuery(''), setPath(i.path)) : onPick(i.path))}>
                  {!i.isDir && hasThumb(i) ? <span className={s.thumb}><Thumb item={i} source={ownSource} size={128} className={s.thumbImg} /></span> : <KindIcon item={i} />}
                  <span className={s.name} title={i.path}>{i.name}{query.trim() && <span className={s.sub}> · {parentOf(i.path) || t('Dateien')}</span>}</span>
                  {!i.isDir && <span className={s.sub}>{fmtSize(i.size)} · {fmtDay(new Date(i.modified))}</span>}
                </div>
              ))}
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
