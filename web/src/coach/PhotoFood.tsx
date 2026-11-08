import { Button, ProgressBar, Text, makeStyles, tokens } from '@fluentui/react-components'
import { Desktop24Regular, Image24Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { useApp } from '../App'
import { Item, Thumb } from '../files/common'
import { gallerySource } from '../files/GalleryFolders'
import { PickFileDialog } from '../files/PickFileDialog'
import { PickPhotoDialog } from '../files/PickPhotoDialog'
import { t } from '../i18n'
import { journalApi } from '../journal/api'
import { coachApi, Meal, Proposal } from './api'
import { Describe } from './Describe'

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', gap: '8px' },
  ways: { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: '8px' },
  way: { flexDirection: 'column', gap: '2px', height: '64px', borderRadius: '14px' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(4, minmax(0, 1fr))', gap: '6px' },
  cell: {
    position: 'relative', aspectRatio: '1', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', cursor: 'pointer', padding: 0, border: 'none',
    display: 'flex', alignItems: 'center', justifyContent: 'center', backgroundColor: tokens.colorNeutralBackground3,
    outline: '2px solid transparent', outlineOffset: '-2px', ':hover': { outline: `2px solid ${tokens.colorBrandStroke1}` },
  },
  img: { width: '100%', height: '100%', display: 'block' },
  links: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
})

const IMAGES = ['.jpg', '.jpeg', '.png', '.webp', '.heic', '.heif', '.avif']
const itemOf = (path: string, name = path.split('/').pop() ?? path, mime: string | null = null, etag?: string): Item =>
  ({ path, name, isDir: false, size: 0, mime, modified: '', etag })

/** Verkleinert hochladen (1600 px reichen zum Erkennen). Was der Browser nicht lesen kann (z. B. HEIC), geht unverändert zum Server. */
async function shrink(file: Blob): Promise<Blob> {
  try {
    const bmp = await createImageBitmap(file)
    const scale = Math.min(1, 1600 / Math.max(bmp.width, bmp.height))
    const c = document.createElement('canvas')
    c.width = Math.round(bmp.width * scale); c.height = Math.round(bmp.height * scale)
    c.getContext('2d')!.drawImage(bmp, 0, 0, c.width, c.height)
    bmp.close()
    return await new Promise<Blob | null>(r => c.toBlob(r, 'image/jpeg', 0.85)) ?? file
  } catch { return file }
}

/**
 * Foto vom Teller: Foto aus der Galerie oder vom Gerät wählen, die KI des Servers erkennt Lebensmittel und Mengen, danach wie bei „Text“
 * (prüfen, alle eintragen). Fotos aufnehmen geht nur in der App. Gespeichert wird das Foto nicht.
 */
export function PhotoFood({ date, meal, onAdded }: { date: string; meal: Meal; onAdded: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [busy, setBusy] = useState(false)
  const [result, setResult] = useState<{ text: string; items: Proposal[] } | null>(null)
  const [round, setRound] = useState(0)
  const [showOwn, setShowOwn] = useState(false)
  const [own, setOwn] = useState<Item[] | null>(null)
  const [pickAll, setPickAll] = useState(false)
  const [pickFiles, setPickFiles] = useState(false)
  const device = useRef<HTMLInputElement>(null)
  // Die Fotos dieses Tages: eigene Ablage und Galerie-Ordner
  useEffect(() => { setOwn(null) }, [date])
  useEffect(() => {
    if (!showOwn || own) return
    journalApi.suggestDay(date)
      .then(r => setOwn(r.photos.filter(p => p.mime?.startsWith('image/') !== false).reverse().slice(0, 60).map(p => itemOf(p.path, p.name, p.mime, p.etag ?? undefined))))
      .catch(() => setOwn([]))
  }, [showOwn, own, date])
  const run = async (load: () => Promise<Blob>) => {
    setBusy(true); setResult(null); setShowOwn(false)
    try { setResult(await coachApi.photo(await load())); setRound(n => n + 1) }
    catch (e) { fail(e) }
    finally { setBusy(false) }
  }
  // Aus der Galerie: die große Vorschau reicht zum Erkennen und deckt auch RAW und HEIC ab
  const sendItem = (i: Item) => run(async () => {
    const r = await fetch(gallerySource.thumb(i, 1600), { credentials: 'same-origin' })
    if (!r.ok) throw new Error(t('Das Foto ließ sich nicht lesen.'))
    return r.blob()
  })
  return (
    <div className={s.root}>
      {busy && <><Text>{t('Foto wird ausgewertet …')}</Text><ProgressBar /></>}
      {result && result.items.length === 0 && <Text>{t('Auf dem Foto wurde kein Essen erkannt.')}</Text>}
      {result && result.items.length > 0 && <Describe key={round} date={date} meal={meal} onAdded={onAdded} initialText={result.text} initial={result.items} />}
      {!busy && <div className={s.ways}>
        <Button className={s.way} onClick={() => setShowOwn(v => !v)}><Image24Regular />{t('Aus Galerie')}</Button>
        <Button className={s.way} onClick={() => device.current?.click()}><Desktop24Regular />{t('Vom Gerät')}</Button>
      </div>}
      <input ref={device} type="file" accept="image/*" hidden onChange={e => { const f = e.target.files?.[0]; e.target.value = ''; if (f) run(() => shrink(f)) }} />
      {showOwn && !busy && <>
        {own === null && <ProgressBar />}
        {own?.length === 0 && <Text size={200} className={s.muted}>{t('In der Galerie gibt es von diesem Tag keine Fotos.')}</Text>}
        {!!own?.length && <div className={s.grid}>
          {own.map(i => <button key={i.path} className={s.cell} title={i.name} aria-label={i.name} onClick={() => sendItem(i)}>
            <Thumb item={i} source={gallerySource} size={256} className={s.img} />
          </button>)}
        </div>}
        <div className={s.links}>
          <Button appearance="subtle" onClick={() => setPickAll(true)}>{t('Aus der dmOffice-Galerie')}</Button>
          <Button appearance="subtle" onClick={() => setPickFiles(true)}>{t('Aus den dmOffice-Dateien')}</Button>
        </div>
      </>}
      {pickAll && <PickPhotoDialog title={t('Foto wählen')} onClose={() => setPickAll(false)} onPick={path => { setPickAll(false); sendItem(itemOf(path)) }} />}
      {pickFiles && <PickFileDialog title={t('Foto wählen')} accept={IMAGES} onClose={() => setPickFiles(false)} onPick={path => { setPickFiles(false); sendItem(itemOf(path)) }} />}
    </div>
  )
}
