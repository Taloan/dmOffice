import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Select, Spinner, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Search20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { locale, t } from '../i18n'
import { Item, Thumb } from './common'
import { gallerySource } from './GalleryFolders'
import { TLBucket, galleryMonth, galleryMonths, searchGallery } from './GalleryTimeline'

const useStyles = makeStyles({
  surface: { maxWidth: '760px', width: '100%' },
  bar: { display: 'flex', gap: '8px', alignItems: 'center' },
  grid: {
    display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(110px, 1fr))', gap: '4px', marginTop: '10px',
    maxHeight: 'calc(100vh - 320px)', minHeight: '240px', overflowY: 'auto', alignContent: 'start',
  },
  cell: {
    position: 'relative', aspectRatio: '1', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', cursor: 'pointer', padding: 0, border: 'none',
    backgroundColor: tokens.colorNeutralBackground3, outline: '2px solid transparent', outlineOffset: '-2px',
    ':hover': { outline: `2px solid ${tokens.colorBrandStroke1}` },
  },
  img: { width: '100%', height: '100%', display: 'block' },
  picked: { outline: `3px solid ${tokens.colorBrandStroke1}`, ':hover': { outline: `3px solid ${tokens.colorBrandStroke1}` } },
  empty: { gridColumn: '1 / -1', padding: '24px', textAlign: 'center', color: tokens.colorNeutralForeground3 },
})

const monthName = (k: string) => new Date(+k.slice(0, 4), +k.slice(5) - 1, 1).toLocaleDateString(locale, { month: 'long', year: 'numeric' })
const isImage = (i: Item) => (i.mime ?? '').startsWith('image/')

// [feature:noteimg]
/** Größe beim Einfügen eines Bildes: 1280 px Kantenlänge oder das Original. */
export type ImageSize = 'orig' | 1280
/** Gewählte Größe, für alle Wege gleich gemerkt (Galerie, Dateien, Gerät). */
export function useImageSize(): [ImageSize, (v: ImageSize) => void] {
  const [size, setSize] = useState<ImageSize>(() => { try { return localStorage.getItem('dm.pickPhotoSize') === 'orig' ? 'orig' : 1280 } catch { return 1280 } })
  const pick = (v: ImageSize) => { setSize(v); try { localStorage.setItem('dm.pickPhotoSize', String(v)) } catch { /* ohne Speicher */ } }
  return [size, pick]
}
/** Auswahl 1280 px / Original - in den Dialogen links neben den Knöpfen. */
export function ImageSizeSelect({ value, onChange }: { value: ImageSize; onChange: (v: ImageSize) => void }) {
  return (
    <Select value={String(value)} onChange={(_, d) => onChange(d.value === 'orig' ? 'orig' : 1280)} aria-label={t('Größe')} style={{ marginRight: 'auto' }}>
      <option value="1280">{t('1280 px (empfohlen)')}</option>
      <option value="orig">{t('Original')}</option>
    </Select>
  )
}
// [/feature:noteimg]

/**
 * Ein Foto aus der Galerie wählen (eigene Ablage und Galerie-Ordner): Vorschaubilder Monat für Monat, neueste zuerst,
 * oder nach Name, Ort, Tag suchen. Gibt den Pfad zurück („ext:…“ bei Galerie-Ordnern). Mit onPickMany: mehrere
 * ankreuzen (auch über Monate hinweg) und mit „Übernehmen“ zurückgeben.
 */
export function PickPhotoDialog({ title, onPick, onPickMany, onClose, sizeChoice }: {
  title: string; onPick?: (path: string) => void; onPickMany?: (items: Item[], size: ImageSize) => void; onClose: () => void
  /** Größe wählen lassen: 1280 px oder Original (Kennung noteimg) - die Wahl bleibt gemerkt */
  sizeChoice?: boolean
}) {
  const s = useStyles()
  const [size, pickSize] = useImageSize()
  const [picked, setPicked] = useState<Item[]>([])
  const toggle = (i: Item) => setPicked(p => p.some(x => x.path === i.path) ? p.filter(x => x.path !== i.path) : [...p, i])
  const [months, setMonths] = useState<TLBucket[] | null>(null)
  const [month, setMonth] = useState('')
  const [query, setQuery] = useState('')
  const [items, setItems] = useState<Item[] | null>(null)
  useEffect(() => {
    galleryMonths().then(b => { const m = b.filter(x => /^\d{4}-\d{2}$/.test(x.k)); setMonths(m); setMonth(m[0]?.k ?? '') }).catch(() => setMonths([]))
  }, [])
  useEffect(() => {
    setItems(null)
    const term = query.trim()
    if (!term && !month) { if (months) setItems([]); return }
    const timer = setTimeout(() => {
      (term ? searchGallery(term, { sort: 'taken', desc: true, stack: false, ext: '', filter: null }).then(r => r.items) : galleryMonth(month).then(r => r.items))
        .then(list => setItems(list.filter(isImage))).catch(() => setItems([]))
    }, term ? 300 : 0)
    return () => clearTimeout(timer)
  }, [month, query, months])
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>
            <div className={s.bar}>
              <Input contentBefore={<Search20Regular />} placeholder={t('Fotos suchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ flex: 1 }} />
              <Select value={month} disabled={!!query.trim() || !months?.length} onChange={(_, d) => setMonth(d.value)} aria-label={t('Monat')}>
                {(months ?? []).map(b => <option key={b.k} value={b.k}>{monthName(b.k)} · {b.n}</option>)}
              </Select>
            </div>
            <div className={s.grid}>
              {items == null && <div className={s.empty}><Spinner size="small" /></div>}
              {items?.length === 0 && <div className={s.empty}>{query.trim() ? t('Kein passendes Foto gefunden.') : t('Hier gibt es keine Fotos.')}</div>}
              {items?.map(i => (
                <button key={i.path} className={mergeClasses(s.cell, picked.some(x => x.path === i.path) && s.picked)} title={i.name} aria-label={i.name}
                  aria-pressed={onPickMany ? picked.some(x => x.path === i.path) : undefined} onClick={() => onPickMany ? toggle(i) : onPick?.(i.path)}>
                  <Thumb item={i} source={gallerySource} size={256} className={s.img} />
                </button>
              ))}
            </div>
          </DialogContent>
          <DialogActions>
            {sizeChoice && <ImageSizeSelect value={size} onChange={pickSize} />}
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            {onPickMany && <Button appearance="primary" disabled={!picked.length} onClick={() => onPickMany(picked, sizeChoice ? size : 'orig')}>{t('Übernehmen ({n})', { n: picked.length })}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
