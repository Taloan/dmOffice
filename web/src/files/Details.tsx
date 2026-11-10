// Detailbereich rechts (Handy: Vollbild): Vorschau, Eigenschaften, Freigaben, fruehere Versionen.
import { rightLabel, rightOf } from '../calendar/ShareDialog'
import { ChatShared } from '../chat/ChatShared'
import { Button, Dialog, DialogBody, DialogContent, DialogSurface, DialogTitle, Divider, Field, Input, Spinner, Text, Textarea, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import {
  ArrowDownload20Regular, Dismiss20Regular, FullScreenMaximize16Regular, History20Regular, Link20Regular, Open20Regular, People20Regular, Share20Regular, Star20Filled, Star20Regular,
} from '@fluentui/react-icons'
import { Suspense, lazy, useCallback, useEffect, useState } from 'react'
import { api, featureOn, fmtSize, q } from '../api'
import { DmsFileInfo } from '../dms/DmsFileInfo'
import { locale, t } from '../i18n'
import { Item, KindIcon, Thumb, hasThumb, kindLabel, kindOf, ownSource, parentOf } from './common'
import { Link, copyText, linkStatus } from './Dialogs'
import { extPlace, gallerySource, isExt } from './GalleryFolders'
import { FilterAttr, Meta, dimsOf, fmtAperture, fmtDuration, fmtExposure, labelColor, labelName, MARK_LABELS } from './PhotoFilter'
import { askConfirm } from '../shell/context'

// Minikarte zum Aufnahmeort (dieselbe wie im Chat) - erst geladen, wenn ein Foto Ortsdaten hat
const LocationMap = lazy(() => import('../chat/LocationMap'))
const PlacePickMap = lazy(() => import('./PlacePickMap'))

/** Datum lesbar - ohne gueltiges Datum ein Strich statt "Invalid Date" */
const when = (v?: string) => { const d = v ? new Date(v) : null; return d && !isNaN(d.getTime()) ? d.toLocaleString(locale) : '—' }

const useStyles = makeStyles({
  root: {
    width: '320px', flexShrink: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', minHeight: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 767px)': { position: 'absolute', inset: 0, width: 'auto', zIndex: 16, borderLeft: 'none' },
  },
  head: { display: 'flex', alignItems: 'center', gap: '8px', padding: '10px 8px 6px 16px' },
  // Ganzer Name, notfalls umbrochen (auch ohne Leerzeichen) - gekürzt ließ er sich im schmalen Bereich nicht lesen
  title: { flex: 1, minWidth: 0, fontWeight: 600, overflowWrap: 'anywhere', lineHeight: tokens.lineHeightBase300 },
  body: { flex: 1, overflowY: 'auto', padding: '0 16px 16px', display: 'flex', flexDirection: 'column', gap: '12px' },
  preview: {
    // flexShrink 0: sonst drückt eine lange Liste darunter (z. B. RAW-Datei daneben, Kameradaten) die Vorschau zusammen
    height: '180px', flexShrink: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3, overflow: 'hidden',
  },
  previewImg: { maxWidth: '100%', maxHeight: '100%' },
  actions: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
  props: { display: 'grid', gridTemplateColumns: 'fit-content(45%) minmax(0, 1fr)', columnGap: '12px', rowGap: '6px', fontSize: '13px' },
  key: { color: tokens.colorNeutralForeground3, overflowWrap: 'anywhere' },
  val: { overflowWrap: 'anywhere' },
  section: { fontWeight: 600, fontSize: '13px' },
  version: { display: 'flex', alignItems: 'center', gap: '4px', fontSize: '13px' },
  muted: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
  hash: {
    fontFamily: tokens.fontFamilyMonospace, fontSize: '11px', color: tokens.colorNeutralForeground3, wordBreak: 'break-all', cursor: 'copy',
    background: 'none', border: 'none', padding: 0, textAlign: 'left',
    ':hover': { color: tokens.colorNeutralForeground1 },
  },
})

interface Info {
  item: Item; size: number; files: number; folders: number; sha256?: string | null
  versions: { id: number; size: number; modified: string; saved: string }[]
  links: Link[]
  /** Mit wem ein eigener Ordner geteilt ist */
  people?: { id: string; login: string; displayName: string; readOnly: boolean; level?: string }[]
  /** In einem mit mir geteilten Ordner: wem er gehoert und ob ich schreiben darf */
  owner?: string | null; canWrite?: boolean
  /** Nur gezeigt (Version 2): ansehen ja, herunterladen nein */
  showOnly?: boolean
}

export function Details({ item, selection, onClose, onOpen, onOpenFolder, onShare, onFavorite, onDownload, version, fail, notify, onChanged, meta, metaOf, width, onFilter, inViewer }: {
  item: Item | null; selection: Item[]; onClose: () => void; onOpen: (i: Item) => void; onOpenFolder: (path: string, focus?: string) => void
  onShare: (i: Item) => void; onFavorite: (i: Item, on: boolean) => void; onDownload?: (list: Item[]) => void
  version: number; fail: (e: unknown) => void; notify: (m: string) => void; onChanged: () => void
  /** Aufnahmedaten des Fotos (Galerie), soweit bekannt. */
  meta?: Meta
  /** Galerie: eine Angabe anklicken setzt sie als Filter (Spalte und Wert; ohne Wert der Wert dieses Fotos) */
  onFilter?: (pick: [FilterAttr, string?][]) => void
  /** Aufnahmedaten beliebiger Fotos (Mehrfachauswahl: Sterne, Titel, Beschreibung für alle) */
  metaOf?: (i: Item) => Meta | undefined
  /** Im Betrachter: Vorschau und Knöpfe (Öffnen, Herunterladen, Teilen, Favorit) stehen dort schon - hier weglassen */
  inViewer?: boolean
  /** Verstellte Breite (Desktop) */
  width?: number
}) {
  const s = useStyles()
  const [info, setInfo] = useState<Info | null>(null)
  // Aufnahmeort groß (Dialog mit Zoom)
  const [bigMap, setBigMap] = useState(false)
  // Rechtsklick in die Karte: Aufnahmeort an diese Stelle (wie Verorten, geht ins Foto)
  const relocate = async (p: Item, lat: number, lon: number) => {
    if (!await askConfirm(t('Aufnahmeort setzen'), t('Aufnahmeort von „{name}“ an diese Stelle setzen?', { name: p.name }), t('Setzen'))) return
    try {
      const r = await api.post<{ failed: { name: string; error: string }[] }>('/api/gallery/info', { items: [p.path], lat, lon })
      if (r.failed?.length) fail(r.failed.map(f => `${f.name}: ${f.error}`).join(', '))
      else notify(t('Aufnahmeort gesetzt'))
      onChanged()
    } catch (e) { fail(e) }
  }
  const load = useCallback(() => {
    // Galerie-Ordner (externer Speicher): keine Angaben der Dateiablage
    if (!item || isExt(item)) { setInfo(null); return }
    api.get<Info>('/api/files/info?' + q({ path: item.path })).then(setInfo).catch(() => setInfo(null))
  }, [item?.path, item?.etag]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(load, [load, version])

  const restore = async (id: number) => {
    if (!item || !await askConfirm('', t('Diese Version wiederherstellen? Der jetzige Inhalt bleibt als Version erhalten.'))) return
    try { await api.post('/api/files/versions/restore', { path: item.path, id }); notify(t('Version wiederhergestellt')); onChanged(); load() } catch (e) { fail(e) }
  }

  if (!item) {
    const total = selection.reduce((n, i) => n + (i.isDir ? 0 : i.size), 0)
    return (
      <aside className={s.root} style={width ? { width } : undefined}>
        <div className={s.head}><span className={s.title}>{t('Details')}</span><Button appearance="subtle" icon={<Dismiss20Regular />} onClick={onClose} aria-label={t('Schließen')} /></div>
        <div className={s.body}>
          <Text>{selection.length ? t('{n} Elemente ausgewählt', { n: selection.length }) : t('Wähle eine Datei oder einen Ordner, um Details zu sehen.')}</Text>
          {selection.length > 0 && <Text className={s.muted}>{t('Dateien zusammen: {size}', { size: fmtSize(total) })}</Text>}
          {selection.length > 0 && onDownload && <Button icon={<ArrowDownload20Regular />} onClick={() => onDownload(selection)}>{t('Herunterladen')}</Button>}
          {/* Mehrere Fotos markiert: Sterne, Titel und Beschreibung für alle */}
          {(() => {
            const photos = selection.filter(x => !x.isDir && kindOf(x) === 'image')
            return photos.length > 1 && metaOf && <PhotoInfoEditor key={photos.map(x => x.path).join('|')} items={photos} metas={photos.map(metaOf)} fail={fail} notify={notify} onChanged={onChanged} />
          })()}
          {/* Verorten: Ort suchen, Rechtsklick in die Karte setzt den Aufnahmeort für alle markierten Fotos */}
          {(() => {
            const photos = selection.filter(x => !x.isDir && kindOf(x) === 'image')
            if (photos.length < 2 || !metaOf) return null
            const points = photos.map(metaOf).filter(m => m?.lat != null && m?.lon != null).map(m => [m!.lat!, m!.lon!] as [number, number])
            const place = async (lat: number, lon: number) => {
              if (!await askConfirm(t('Aufnahmeort setzen'), t('Aufnahmeort von {n} Fotos an diese Stelle setzen?', { n: photos.length }), t('Setzen'))) return
              try {
                const failed: { name: string; error: string }[] = []
                for (let k = 0; k < photos.length; k += 50) {
                  const r = await api.post<{ failed: { name: string; error: string }[] }>('/api/gallery/info', { items: photos.slice(k, k + 50).map(x => x.path), lat, lon })
                  failed.push(...(r.failed ?? []))
                }
                if (failed.length) fail(failed.map(f => `${f.name}: ${f.error}`).join(', '))
                else notify(t('Aufnahmeort gesetzt'))
                onChanged()
              } catch (e) { fail(e) }
            }
            return <>
              <Divider style={{ flexGrow: 0 }} />
              <Text weight="semibold">{t('Aufnahmeort')}</Text>
              <Suspense fallback={<div style={{ height: 220 }} />}><PlacePickMap key={photos.map(x => x.path).join('|')} points={points} onPick={place} /></Suspense>
            </>
          })()}
        </div>
      </aside>
    )
  }
  const i = info?.item ?? item
  const kind = kindOf(i)
  // Galerie: Angabe als Verweis - setzt sie in der Filterleiste
  const pick = (what: [FilterAttr, string?][], text: React.ReactNode) => !onFilter ? text
    : <a href="#" title={t('Als Filter setzen')} onClick={e => { e.preventDefault(); onFilter(what) }}>{text}</a>
  return (
    <aside className={s.root} style={width ? { width } : undefined}>
      <div className={s.head}>
        <KindIcon item={i} />
        <span className={s.title} title={i.name}>{i.name || t('Meine Dateien')}</span>
        <Button appearance="subtle" icon={<Dismiss20Regular />} onClick={onClose} aria-label={t('Schließen')} />
      </div>
      <div className={s.body}>
        {!inViewer && <>
        <div className={s.preview}>
          {hasThumb(i) ? <Thumb item={i} source={isExt(i) ? gallerySource : ownSource} size={1600} fit="contain" className={s.previewImg} fallbackLarge /> : <KindIcon item={i} large />}
        </div>
        <div className={s.actions}>
          <Button size="small" icon={<Open20Regular />} onClick={() => onOpen(i)}>{t('Öffnen')}</Button>
          {!i.isDir && onDownload && <Button size="small" icon={<ArrowDownload20Regular />} onClick={() => onDownload([i])}>{t('Herunterladen')}</Button>}
          {i.path && !info?.owner && <Button size="small" icon={<Share20Regular />} onClick={() => onShare(i)}>{t('Teilen')}</Button>}
          {i.path && !info?.owner && !isExt(i) && <Tooltip content={i.favorite ? t('Aus Favoriten entfernen') : t('Zu Favoriten')} relationship="label">
            <Button size="small" icon={i.favorite ? <Star20Filled color="#E8A33D" /> : <Star20Regular />}
              onClick={async () => { await onFavorite(i, !i.favorite); load() }} />
          </Tooltip>}
        </div>
        </>}
        <Divider style={{ flexGrow: 0 }} />
        <div className={s.props}>
          <span className={s.key}>{t('Art')}</span><span className={s.val}>{kindLabel(kind)}{i.mime && !i.isDir ? ` (${i.mime})` : ''}</span>
          <span className={s.key}>{t('Größe')}</span>
          <span className={s.val}>{info ? fmtSize(info.size) : isExt(i) ? fmtSize(i.size) : <Spinner size="extra-tiny" />}
            {info && i.isDir && <span className={s.muted}> · {t('{f} Dateien, {d} Ordner', { f: info.files, d: info.folders })}</span>}</span>
          <span className={s.key}>{t('Geändert')}</span><span className={s.val}>{pick([['year'], ['month']], when(info?.item.modified ?? i.modified))}</span>
          {i.created && <><span className={s.key}>{t('Erstellt')}</span><span className={s.val}>{new Date(i.created).toLocaleString(locale)}</span></>}
          {info?.owner && <><span className={s.key}>{t('Besitzer')}</span><span className={s.val}>{info.owner} · {info.showOnly ? t('nur ansehen') : info.canWrite ? t('du darfst bearbeiten') : t('nur lesen')}</span></>}
          {i.path && !isExt(i) && <><span className={s.key}>{t('Ort')}</span>
            <span className={s.val}><a href="#" onClick={e => { e.preventDefault(); onOpenFolder(parentOf(i.path), i.path) }}>{'/' + parentOf(i.path)}</a></span></>}
          {/* Galerie-Ordner: lesbar, öffnet den externen Ordner unter Dateien */}
          {isExt(i) && extPlace(i.path) && <><span className={s.key}>{t('Ort')}</span>
            <span className={s.val}><a href={extPlace(i.path)!.href} onClick={e => { e.preventDefault(); onOpenFolder(parentOf(i.path), i.path) }}>{extPlace(i.path)!.label}</a></span></>}
          {info && !i.isDir && <><span className={s.key}>SHA-256</span>
            {info.sha256
              ? <Tooltip content={t('Prüfsumme des Inhalts – klicken zum Kopieren')} relationship="description">
                <button type="button" className={s.hash} onClick={async () => { if (await copyText(info.sha256!)) notify(t('Prüfsumme kopiert')) }}>{info.sha256}</button>
              </Tooltip>
              : <span className={s.muted}>{t('wird noch berechnet')}</span>}</>}
        </div>
        {/* Titel und Beschreibung wie in Lightroom (dc:title, dc:description): gleich hier bearbeiten */}
        {!i.isDir && kindOf(i) === 'image' && <PhotoInfoEditor key={i.path + '|' + (meta?.title ?? '') + '|' + (meta?.description ?? '') + '|' + (meta?.rating ?? 0)}
          items={[i]} metas={[meta]} fail={fail} notify={notify} onChanged={onChanged} />}
        {meta && (meta.keywords || meta.rating || meta.taken || meta.camera || meta.label || meta.width) && <>
          <Divider style={{ flexGrow: 0 }} />
          <div className={s.props}>
            {meta.keywords && <><span className={s.key}>{t('Stichwörter')}</span><span className={s.val}>
              {meta.keywords.split('\n').map((k, n) => <span key={k}>{n > 0 && ', '}{pick([['keyword', k]], k)}</span>)}</span></>}
            {meta.label && <><span className={s.key}>{t('Farbmarkierung')}</span><span className={s.val}>
              <span style={{ display: 'inline-block', width: 10, height: 10, borderRadius: 5, marginRight: 6, background: labelColor(meta.label) }} />{pick([['label']], labelName(meta.label))}</span></>}
            {meta.taken && <><span className={s.key}>{t('Aufgenommen')}</span><span className={s.val}>{pick([['year'], ['month']], new Date(meta.taken).toLocaleString(locale))}</span></>}
            {meta.camera && <><span className={s.key}>{t('Kamera')}</span><span className={s.val}>{pick([['camera']], meta.camera)}</span></>}
            {meta.lens && <><span className={s.key}>{t('Objektiv')}</span><span className={s.val}>{pick([['lens']], meta.lens)}</span></>}
            {meta.focal ? <><span className={s.key}>{t('Brennweite')}</span><span className={s.val}>{pick([['focal']], meta.focal + ' mm')}</span></> : null}
            {meta.aperture ? <><span className={s.key}>{t('Blende')}</span><span className={s.val}>{pick([['aperture']], fmtAperture(meta.aperture))}</span></> : null}
            {meta.exposure ? <><span className={s.key}>{t('Belichtungszeit')}</span><span className={s.val}>{pick([['exposure']], fmtExposure(meta.exposure))}</span></> : null}
            {meta.iso ? <><span className={s.key}>{t('ISO')}</span><span className={s.val}>{pick([['iso']], meta.iso)}</span></> : null}
            {dimsOf(meta) && <><span className={s.key}>{t('Abmessungen')}</span><span className={s.val}>{dimsOf(meta)}{meta.width && meta.height ? ` · ${(meta.width * meta.height / 1e6).toFixed(1)} MP` : ''}</span></>}
            {meta.duration ? <><span className={s.key}>{t('Dauer')}</span><span className={s.val}>{fmtDuration(meta.duration)}</span></> : null}
            {meta.lat != null && meta.lon != null && <><span className={s.key}>{t('Aufnahmeort')}</span><span className={s.val}>
              <a href={`https://www.openstreetmap.org/?mlat=${meta.lat}&mlon=${meta.lon}#map=15/${meta.lat}/${meta.lon}`} target="_blank" rel="noopener noreferrer">{meta.lat.toFixed(5)}, {meta.lon.toFixed(5)}</a></span></>}
          </div>
          {meta.lat != null && meta.lon != null && <>
            <div style={{ flexShrink: 0, position: 'relative' }}>
              <Suspense fallback={<div style={{ height: 150 }} />}><LocationMap key={i.path} lat={meta.lat} lon={meta.lon} controls onPick={kind === 'image' ? (la, lo) => relocate(i, la, lo) : undefined} /></Suspense>
              <Tooltip content={t('Karte vergrößern')} relationship="label">
                <Button size="small" icon={<FullScreenMaximize16Regular />} onClick={() => setBigMap(true)}
                  style={{ position: 'absolute', top: 12, right: 6, zIndex: 500, minWidth: 0 }} />
              </Tooltip>
            </div>
            {bigMap && <Dialog open onOpenChange={(_, d) => { if (!d.open) setBigMap(false) }}>
              <DialogSurface style={{ maxWidth: '90vw', width: 1100 }}>
                <DialogBody>
                  <DialogTitle action={<Button appearance="subtle" icon={<Dismiss20Regular />} aria-label={t('Schließen')} onClick={() => setBigMap(false)} />}>{i.name}</DialogTitle>
                  <DialogContent>
                    <Suspense fallback={<Spinner />}><LocationMap lat={meta.lat} lon={meta.lon} controls height="70vh" onPick={kind === 'image' ? (la, lo) => relocate(i, la, lo) : undefined} /></Suspense>
                  </DialogContent>
                </DialogBody>
              </DialogSurface>
            </Dialog>}
          </>}
        </>}
        {!i.isDir && (kindOf(i) === 'image' || /\.(heic|arw|cr[23]|nef|raf|dng|orf|rw2)$/i.test(i.name)) && !i.path.startsWith('alb:') && <AllTags item={i} />}
        {info?.people && info.people.length > 0 && <>
          <Divider style={{ flexGrow: 0 }} />
          <span className={s.section}><People20Regular style={{ verticalAlign: 'middle' }} /> {t('Geteilt mit {n} Person(en)', { n: info.people.length })}</span>
          {info.people.map(p => <span key={p.id} className={s.muted}>{p.displayName} · {rightLabel()[rightOf(p)]}</span>)}
          <Button size="small" onClick={() => onShare(i)}>{t('Freigabe verwalten')}</Button>
        </>}
        {info && info.links.length > 0 && <>
          <Divider style={{ flexGrow: 0 }} />
          <span className={s.section}><Link20Regular style={{ verticalAlign: 'middle' }} /> {t('Freigegeben über {n} Link(s)', { n: info.links.length })}</span>
          {info.links.map(l => (
            <span key={l.id} className={s.muted}>{linkStatus(l.mode)}{l.expires ? ' · ' + t('bis {date}', { date: new Date(l.expires).toLocaleDateString(locale) }) : ''}{l.hasPassword ? ' · ' + t('mit Passwort') : ''}</span>
          ))}
          <Button size="small" onClick={() => onShare(i)}>{t('Links verwalten')}</Button>
        </>}
        {info && !i.isDir && !!i.fileId && !info.owner && <ChatShared fileId={i.fileId} />}
        {info && !i.isDir && !!i.fileId && !info.owner && featureOn('dms') && <DmsFileInfo fileId={i.fileId} />}
        {info && !i.isDir && <>
          <Divider style={{ flexGrow: 0 }} />
          <span className={s.section}><History20Regular style={{ verticalAlign: 'middle' }} /> {t('Frühere Versionen')}</span>
          {info.versions.length === 0 ? <span className={s.muted}>{t('Noch keine. Beim Überschreiben bleibt der alte Inhalt hier {days} Tage erhalten.', { days: 30 })}</span>
            : info.versions.map(v => (
              <div key={v.id} className={s.version}>
                <span style={{ flex: 1 }}>{new Date(v.modified).toLocaleString(locale)}<br /><span className={s.muted}>{fmtSize(v.size)}</span></span>
                {!info.showOnly && <Tooltip content={t('Herunterladen')} relationship="label">
                  <Button size="small" appearance="subtle" icon={<ArrowDownload20Regular />} as="a"
                    href={'/api/files/download?' + q({ path: i.path, version: v.id })} />
                </Tooltip>}
                {info.canWrite !== false && <Button size="small" onClick={() => restore(v.id)}>{t('Wiederherstellen')}</Button>}
              </div>
            ))}
        </>}
      </div>
    </aside>
  )
}

/** Alle Angaben in der Datei (EXIF, XMP, IPTC, Hersteller) - erst auf Knopfdruck, die Datei muss dafür gelesen werden. */
function AllTags({ item }: { item: Item }) {
  const s = useStyles()
  const [tags, setTags] = useState<{ group: string; name: string; value: string }[] | null>(null)
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  useEffect(() => { setTags(null); setOpen(false); setError(null) }, [item.path])
  const load = async () => {
    setOpen(true)
    if (tags) return
    setBusy(true)
    try {
      setTags(await api.get(item.path.startsWith('ext:') ? '/api/gallery/external/exif?' + q({ id: item.path.slice(4) }) : '/api/files/exif?' + q({ path: item.path })))
    } catch (e: any) { setError(e?.message ?? String(e)) } finally { setBusy(false) }
  }
  const groups = [...new Set((tags ?? []).map(x => x.group))]
  return <>
    <Divider style={{ flexGrow: 0 }} />
    {!open ? <Button size="small" style={{ flexShrink: 0 }} onClick={load}>{t('Alle Angaben der Datei')}</Button> : <>
      <span className={s.section}>{t('Alle Angaben der Datei')} <Button size="small" appearance="subtle" onClick={() => setOpen(false)}>{t('Ausblenden')}</Button></span>
      {busy && <Spinner size="tiny" />}
      {error && <span className={s.muted}>{error}</span>}
      {tags?.length === 0 && <span className={s.muted}>{t('Keine Angaben in der Datei.')}</span>}
      {groups.map(g => <div key={g}>
        <Text size={200} weight="semibold" block style={{ margin: '8px 0 2px' }}>{g}</Text>
        <div className={s.props}>
          {tags!.filter(x => x.group === g).map((x, n) => <span key={n} style={{ display: 'contents' }}><span className={s.key}>{x.name}</span><span className={s.val}>{x.value}</span></span>)}
        </div>
      </div>)}
    </>}
  </>
}

/**
 * Titel, Beschreibung und Sterne eines oder mehrerer Fotos (JPG in die Datei, RAW in die XMP-Datei daneben - dieselben Felder wie
 * Lightroom: dc:title, dc:description, xmp:Rating). Bei mehreren: gemeinsame Werte vorbelegt; Unterschiedliches bleibt, solange
 * das Feld nicht geändert wird. Sterne werden gleich gespeichert, Titel und Beschreibung mit „Speichern“.
 */
function PhotoInfoEditor({ items, metas, fail, notify, onChanged }: { items: Item[]; metas: (Meta | undefined)[]; fail: (e: unknown) => void; notify: (m: string) => void; onChanged: () => void }) {
  const common = (f: (m: Meta | undefined) => string) => { const v = metas.map(f); return v.every(x => x === v[0]) ? v[0] : null }
  const title0 = common(m => m?.title ?? ''), desc0 = common(m => m?.description ?? '')
  const ratings = metas.map(m => m?.rating ?? 0)
  const rating0 = ratings.every(r => r === ratings[0]) ? ratings[0] : null
  const [title, setTitle] = useState(title0 ?? '')
  const [desc, setDesc] = useState(desc0 ?? '')
  const [rating, setRating] = useState<number | null>(rating0)
  const picks = metas.map(m => m?.pick ?? 0), labels = metas.map(m => m?.label ?? '')
  const [pick, setPick] = useState<number | null>(picks.every(p => p === picks[0]) ? picks[0] : null)
  const [label, setLabel] = useState<string | null>(labels.every(l => l === labels[0]) ? labels[0] : null)
  const [busy, setBusy] = useState(false)
  // Per Taste im Betrachter oder in der Galerie geändert: hier gleich zeigen (sonst stand bis zum nächsten Bild der alte Stand da)
  const pick0 = picks.every(p => p === picks[0]) ? picks[0] : null
  const label0 = labels.every(l => l === labels[0]) ? labels[0] : null
  useEffect(() => { setRating(rating0) }, [rating0])
  useEffect(() => { setPick(pick0) }, [pick0])
  useEffect(() => { setLabel(label0) }, [label0])
  const titleChanged = title !== (title0 ?? ''), descChanged = desc !== (desc0 ?? '')
  const send = async (body: object, done: string) => {
    setBusy(true)
    try {
      // Der Server nimmt höchstens 50 Fotos je Anfrage - größere Auswahl in Teilen
      const failed: { name: string; error: string }[] = []
      for (let k = 0; k < items.length; k += 50) {
        const r = await api.post<{ failed: { name: string; error: string }[] }>('/api/gallery/info', { items: items.slice(k, k + 50).map(i => i.path), ...body })
        failed.push(...(r.failed ?? []))
      }
      if (failed.length) fail(failed.map(f => `${f.name}: ${f.error}`).join(', '))
      else notify(done)
      onChanged()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const mixed = t('Unterschiedlich – bleibt, wenn nicht geändert')
  return <>
    <Divider style={{ flexGrow: 0 }} />
    <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
      {items.length > 1 && <Text size={200}>{t('Gilt für {n} markierte Fotos', { n: items.length })}</Text>}
      <Field label={t('Bewertung')}>
        <div style={{ display: 'flex', gap: 2 }}>
          {[1, 2, 3, 4, 5].map(n => (
            <Button key={n} appearance="transparent" size="small" disabled={busy} aria-label={t('{n} Sterne', { n })} style={{ minWidth: 0, padding: 2 }}
              icon={rating != null && n <= rating ? <Star20Filled color="#E8A33D" /> : <Star20Regular />}
              // Derselbe Stern noch einmal: Bewertung entfernen
              onClick={() => { const v = rating === n ? 0 : n; setRating(v); send({ rating: v }, v ? t('{n} Sterne vergeben', { n: v }) : t('Bewertung entfernt')) }} />
          ))}
          {rating == null && <Text size={200} style={{ alignSelf: 'center', marginLeft: 6 }}>{t('Unterschiedlich')}</Text>}
        </div>
      </Field>
      <Field label={t('Markierung')}>
        <div style={{ display: 'flex', gap: 2 }}>
          {([[1, 'P', t('Ausgewählt'), '#30A46C'], [-1, 'X', t('Abgelehnt'), '#E5484D'], [0, 'U', t('Ohne Markierung'), undefined]] as const).map(([v, k, name, color]) => (
            <Button key={k} size="small" appearance={pick === v ? 'primary' : 'subtle'} disabled={busy} title={`${name} (${k})`} style={{ minWidth: 30, color: pick === v ? undefined : color }}
              onClick={() => { setPick(v); send({ pick: v }, name) }}>{k}</Button>
          ))}
          {pick == null && <Text size={200} style={{ alignSelf: 'center', marginLeft: 6 }}>{t('Unterschiedlich')}</Text>}
        </div>
      </Field>
      <Field label={t('Farbe')}>
        <div style={{ display: 'flex', gap: 2, alignItems: 'center' }}>
          {MARK_LABELS.map(l => (
            <Button key={l} size="small" appearance="transparent" disabled={busy} title={labelName(l)} aria-pressed={label === l} style={{ minWidth: 0, padding: 4 }}
              // Dieselbe Farbe noch einmal: Farbe entfernen
              onClick={() => { const v = label === l ? '' : l; setLabel(v); send({ label: v }, v ? labelName(v) : t('Ohne Farbe')) }}>
              <span style={{ width: 14, height: 14, borderRadius: '50%', display: 'inline-block', background: labelColor(l), outline: label === l ? `2px solid ${tokens.colorNeutralForeground1}` : 'none', outlineOffset: 2 }} />
            </Button>
          ))}
          {label == null && <Text size={200} style={{ marginLeft: 6 }}>{t('Unterschiedlich')}</Text>}
        </div>
      </Field>
      <Field label={t('Titel')}><Input value={title} placeholder={title0 == null ? mixed : undefined} onChange={(_, d) => setTitle(d.value)} /></Field>
      <Field label={t('Beschreibung')}><Textarea value={desc} placeholder={desc0 == null ? mixed : undefined} onChange={(_, d) => setDesc(d.value)} resize="vertical" rows={3} /></Field>
      {(titleChanged || descChanged) && <div style={{ display: 'flex', gap: 6 }}>
        <Button appearance="primary" size="small" disabled={busy}
          onClick={() => send({ ...(titleChanged ? { title } : {}), ...(descChanged ? { description: desc } : {}) }, t('Gespeichert'))}>{t('Speichern')}</Button>
        <Button size="small" disabled={busy} onClick={() => { setTitle(title0 ?? ''); setDesc(desc0 ?? '') }}>{t('Verwerfen')}</Button>
      </div>}
    </div>
  </>
}
