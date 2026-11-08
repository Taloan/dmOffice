import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, InfoLabel, Option, Spinner, Switch, Text, Tooltip, makeStyles, tokens,
} from '@fluentui/react-components'
import { ArrowClockwise20Regular, ArrowUp20Regular, Delete20Regular, Folder20Filled, ImageOff20Regular, Share20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { askConfirm } from '../shell/context'
import { Item, Source, ownSource } from './common'
import { albumFile, albumThumb, isAlb } from './Albums'
import { setSourceNames } from './PhotoFilter'

/** Fotos und Videos aus Galerie-Ordnern (externe Ordner): Pfad „ext:&lt;Quelle&gt;/&lt;Pfad&gt;“ - nur ansehen und herunterladen. */
export const EXT = 'ext:'
/** Nicht aus der eigenen Ablage: Galerie-Ordner („ext:“) und Fotos eines Albums („alb:“) - nur ansehen und herunterladen. */
export const isExt = (i: { path: string }) => i.path.startsWith(EXT) || isAlb(i)
const idOf = (i: Item) => i.path.slice(EXT.length)
/** Galerie-Ordner, deren externer Ordner beschreibbar eingebunden ist - nur dort lässt sich löschen (gefüllt von loadSourceNames). */
const writable = new Set<string>()
export const extWritable = (i: { path: string }) => i.path.startsWith(EXT) && writable.has(idOf(i as Item).split('/')[0])
export const extId = (i: { path: string }) => idOf(i as Item)
/** Galerie-Ordner, deren externer Ordner gerade nicht erreichbar ist (gefüllt von loadSourceNames): nur die gespeicherten
 * Vorschaubilder, kein Öffnen und kein Herunterladen. */
const offline = new Set<string>()
/** Galerie-Ordner: externer Ordner, Pfad darin und lesbarer Name (gefüllt von loadSourceNames). */
const places = new Map<string, { mount: string; mountName: string }>()
/**
 * Ort eines Eintrags aus einem Galerie-Ordner (sein Pfad zählt ab dem externen Ordner, nicht ab dem Galerie-Ordner): lesbar („pCloud › Fotos › 2013“) und die Adresse des Ordners unter Dateien
 * (#/files/:ext/…) - statt der internen Kennung „ext:…“. Null: kein Galerie-Ordner oder Namen noch nicht geladen.
 */
export function extPlace(path: string): { label: string; href: string } | null {
  if (!path.startsWith(EXT)) return null
  const id = path.slice(EXT.length), cut = id.indexOf('/')
  const p = places.get(cut < 0 ? id : id.slice(0, cut))
  if (!p) return null
  const rel = cut < 0 ? '' : id.slice(cut + 1)
  const dir = rel.includes('/') ? rel.slice(0, rel.lastIndexOf('/')) : ''
  return {
    label: p.mountName + (dir ? ' › ' + dir.split('/').join(' › ') : ''),
    href: '#/files/:ext/' + encodeURIComponent(p.mount) + (dir ? '/' + dir.split('/').map(encodeURIComponent).join('/') : ''),
  }
}
export const extOffline = (i: { path: string }) => i.path.startsWith(EXT) && offline.has(idOf(i as Item).split('/')[0])

/** Foto aus einem anderen Bereich (z. B. der Übersicht) im Betrachter öffnen; Schließen führt dorthin zurück. */
let photoFromElsewhere = false
export const openPhotoFromElsewhere = (path: string) => { photoFromElsewhere = true; location.hash = '#/gallery/photo/' + encodeURIComponent(path) }
/** Für den Betrachter: kam das Foto aus einem anderen Bereich? Gilt nur einmal. */
export const takePhotoFromElsewhere = () => { const r = photoFromElsewhere; photoFromElsewhere = false; return r }

/** Adressen der Galerie: Album-Fotos über das Album, externe Einträge über /api/gallery, alles andere wie in der Dateiablage. */
export const gallerySource: Source = {
  download: (i, inline) => isAlb(i) ? albumFile(i, inline) : isExt(i) ? '/api/gallery/external/file?' + q({ id: idOf(i), inline: inline || undefined }) : ownSource.download(i, inline),
  thumb: (i, size = 256) => isAlb(i) ? albumThumb(i, size) : isExt(i) ? '/api/gallery/external/thumb?' + q({ id: idOf(i), size, v: i.etag }) : ownSource.thumb(i, size),
  storeThumb: async (i, blob) => {
    if (isAlb(i)) return
    if (!isExt(i)) return ownSource.storeThumb!(i, blob)
    await fetch('/api/gallery/external/thumb?' + q({ id: idOf(i) }), { method: 'PUT', body: blob, credentials: 'same-origin' })
  },
}

/** Kacheln: große Vorschau nur, wenn schon vorhanden - sonst das kleine Bild (Blättern holt keine Originale über die Freigabe). */
export const galleryTileSource: Source = {
  ...gallerySource,
  thumb: (i, size = 256) => isExt(i) && size > 256 ? '/api/gallery/external/thumb?' + q({ id: idOf(i), size, ready: 1, v: i.etag }) : gallerySource.thumb(i, size),
}

interface Mount { id: string; name: string; kind: 'folder' | 'webdav' | 'onedrive' | 'pcloud'; readOnly: boolean }
interface GallerySource { id: string; mount: string; mountName: string | null; path: string; available?: boolean; watch: boolean; draining?: boolean; sidecars: boolean; proxies?: boolean; proxyCount?: number; proxyBytes?: number; pending?: number; lastScan: string | null; error: string | null; count: number; scanning: boolean }

const useStyles = makeStyles({
  // Zwei Zeilen: oben Ordner, Stand und Knöpfe, darunter die Schalter (brechen um, statt den Namen zusammenzudrücken)
  row: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '6px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  head: { display: 'flex', alignItems: 'center', gap: '8px' },
  switches: { display: 'flex', flexWrap: 'wrap', columnGap: '12px', marginLeft: '-8px' },
  name: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  muted: { color: tokens.colorNeutralForeground3 },
  browse: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, maxHeight: '220px', overflowY: 'auto', padding: '4px' },
  dir: { display: 'flex', alignItems: 'center', gap: '6px', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
})

/** Namen der Galerie-Ordner für die Quelle in Filterleiste und Smart-Alben laden („Storage › RAW › PRI“). */
export async function loadSourceNames() {
  try {
    const d = await api.get<{ sources: GallerySource[]; mounts: Mount[] }>('/api/gallery/sources')
    const mount = (id: string) => d.mounts.find(m => m.id === id)?.name ?? id
    writable.clear()
    for (const x of d.sources) if (d.mounts.some(m => m.id === x.mount && !m.readOnly)) writable.add(x.id)
    offline.clear()
    for (const x of d.sources) if (x.available === false) offline.add(x.id)
    const names = d.sources.map(x => ({ id: x.id, name: (x.mountName ?? mount(x.mount)) + (x.path ? ' › ' + x.path.split('/').join(' › ') : '') }))
    places.clear()
    d.sources.forEach(x => places.set(x.id, { mount: x.mount, mountName: x.mountName ?? mount(x.mount) }))
    setSourceNames(names)
  } catch { /* ohne Namen: die Kennung */ }
}

/**
 * Galerie → Galerie-Ordner: Ordner aus externen Speichern (Serverordner, eigene WebDAV-Ordner) in die Galerie aufnehmen,
 * je Ordner überwacht (regelmäßig neu eingelesen) oder nur auf Knopfdruck. Die eigene Ablage bleibt wie bisher dabei.
 */
/** onShare: Quelle als Album teilen (legt bei Bedarf das Smart-Album „Quelle = dieser Ordner“ an). */
export function GalleryFoldersDialog({ onClose, onShare }: { onClose: (changed: boolean) => void; onShare?: (source: { id: string; name: string }) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [data, setData] = useState<{ sources: GallerySource[]; mounts: Mount[] } | null>(null)
  const [changed, setChanged] = useState(false)
  const [mount, setMount] = useState<string>('')
  const [path, setPath] = useState('')
  const [dirs, setDirs] = useState<string[] | null>(null)
  const load = () => api.get<{ sources: GallerySource[]; mounts: Mount[] }>('/api/gallery/sources').then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Während eingelesen wird, den Stand nachladen
  useEffect(() => {
    if (!data?.sources.some(x => x.scanning)) return
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
  const add = () => act(async () => { await api.post('/api/gallery/sources', { mount, path, watch: true }); setMount(''); setPath('') })
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(changed) }}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>{t('Galerie-Ordner')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>{t('Die Galerie zeigt die Fotos und Videos deiner Ablage. Hier nimmst du zusätzlich Ordner aus externen Speichern auf – die Dateien bleiben, wo sie sind.')}</Text>
            {data.sources.length === 0 && <Text className={s.muted}>{t('Noch keine Galerie-Ordner.')}</Text>}
            {data.sources.map(x => (
              <div key={x.id} className={s.row}>
                <div className={s.head}>
                <div className={s.name}>
                  <Text weight="semibold">{(x.mountName ?? mountName(x.mount)) + (x.path ? ' › ' + x.path.split('/').join(' › ') : '')}</Text>
                  <Text size={200} className={s.muted}>
                    {x.scanning ? t('Wird eingelesen …')
                      : x.error ? t('Fehler: {error}', { error: x.error })
                      : x.lastScan ? t('{n} Fotos und Videos · eingelesen {when}', { n: x.count, when: new Date(x.lastScan).toLocaleString() })
                      : t('Wird gleich eingelesen …')}
                    {x.pending ? ' · ' + t('{n} noch ohne Bildangaben oder Vorschau', { n: x.pending.toLocaleString() })
                      // Nicht überwacht: ruht, bis „Jetzt neu einlesen“ das Abarbeiten anstößt (kein Abruf beim Anbieter im Hintergrund)
                      + (!x.watch && !x.draining && !x.scanning ? ' – ' + t('ruht, mit „Jetzt neu einlesen“ abarbeiten') : '') : ''}
                    {x.proxyCount ? ' · ' + t('{n} große Vorschauen gespeichert ({size})', { n: x.proxyCount.toLocaleString(), size: fmtSize(x.proxyBytes ?? 0) }) : ''}
                  </Text>
                </div>
                {x.scanning && <Spinner size="tiny" />}
                {onShare && <Tooltip content={t('Als Album teilen oder zeigen: andere sehen die Fotos dieses Ordners als Album, nur ansehen')} relationship="label">
                  <Button appearance="subtle" icon={<Share20Regular />} onClick={() => onShare({ id: x.id, name: (x.mountName ?? mountName(x.mount)) + (x.path ? ' › ' + x.path.split('/').join(' › ') : '') })} />
                </Tooltip>}
                <Tooltip content={t('Jetzt neu einlesen')} relationship="label">
                  <Button appearance="subtle" icon={<ArrowClockwise20Regular />} disabled={x.scanning} onClick={() => act(() => api.post(`/api/gallery/sources/${x.id}/scan`))} />
                </Tooltip>
                {!!x.proxyCount && <Tooltip content={t('Große Vorschauen dieses Ordners löschen ({size}) – beim Öffnen eines Fotos entsteht sie neu', { size: fmtSize(x.proxyBytes ?? 0) })} relationship="label">
                  <Button appearance="subtle" icon={<ImageOff20Regular />} onClick={async () => {
                    if (!await askConfirm(t('Große Vorschauen löschen?'), t('{n} große Vorschauen ({size}) dieses Ordners werden gelöscht. „Große Vorschau vorab“ wird dabei ausgeschaltet; beim Öffnen eines Fotos entsteht seine Vorschau neu.', { n: x.proxyCount!.toLocaleString(), size: fmtSize(x.proxyBytes ?? 0) }), t('Löschen'))) return
                    act(() => api.del(`/api/gallery/sources/${x.id}/proxies`))
                  }} />
                </Tooltip>}
                <Tooltip content={t('Aus der Galerie nehmen (die Dateien bleiben)')} relationship="label">
                  <Button appearance="subtle" icon={<Delete20Regular />} onClick={() => act(() => api.del(`/api/gallery/sources/${x.id}`))} />
                </Tooltip>
                </div>
                <div className={s.switches}>
                  <Switch checked={x.watch} label={<InfoLabel info={t('liest den Ordner alle 15 Minuten neu ein und arbeitet Neues und Offenes laufend ab. Aus: Der Ordner ruht, der Server fragt den Speicher nicht an – „Jetzt neu einlesen“ (↻) liest ihn einmal ein und arbeitet alles Offene ab.')}>{t('Überwachen')}</InfoLabel>} onChange={(_, d) => act(() => api.put(`/api/gallery/sources/${x.id}`, { watch: d.checked }))} />
                  <Switch checked={x.sidecars ?? true} label={<InfoLabel info={t('übernimmt Titel, Beschreibung, Stichwörter, Bewertung und Farbmarkierung aus XMP-Dateien neben den Fotos (Lightroom, darktable, Capture One).')}>{t('XMP-Dateien')}</InfoLabel>} onChange={(_, d) => act(() => api.put(`/api/gallery/sources/${x.id}`, { sidecars: d.checked }))} />
                  <Switch checked={!!x.proxies} label={<InfoLabel info={t('legt für jedes Foto gleich beim Einlesen eine große Vorschau (2048 px) auf dem Server ab – in der Galerie öffnet es dann sofort, ohne das Original über die Leitung zu holen. Braucht etwa 200 KB je Foto. Aus: Eine große Vorschau entsteht erst, wenn ein Foto geöffnet wird oder die Personenerkennung es untersucht – sie holt das Original ohnehin und speichert die Vorschau dabei mit. Gespeicherte bleiben (die Zahl hinter „große Vorschauen gespeichert“).')}>{t('Große Vorschau vorab')}</InfoLabel>} onChange={(_, d) => act(() => api.put(`/api/gallery/sources/${x.id}`, { proxies: d.checked }))} />
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
