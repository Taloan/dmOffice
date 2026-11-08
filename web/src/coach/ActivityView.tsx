import { Button, Dialog, DialogBody, DialogContent, DialogSurface, Field, Input, MessageBar, MessageBarBody, Spinner, Text, tokens } from '@fluentui/react-components'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { gallerySource } from '../files/GalleryFolders'
import { t } from '../i18n'
import { fmt } from './api'

interface Photo { kind: string; path: string; name: string; mime: string | null; etag: string | null; taken: number | null; lat: number | null; lon: number | null }
interface Detail {
  id: number; name: string; type: string; start: string; durationS: number | null; distanceM: number | null; kcal: number | null
  track: [number, number][]; trackError: string | null; photos: Photo[]; unlocated: number
}
/** Vorschau des Verortens: je Foto ohne Ort die Stelle auf der Strecke (state ok) oder warum nicht */
interface GeoItem {
  path: string; name: string; mime: string | null; etag: string | null; taken: number | null
  state: 'ok' | 'outside' | 'readonly' | 'unsupported'; lat: number | null; lon: number | null; rough: boolean; target: 'file' | 'sidecar' | null
}
interface GeoPreview { timed: boolean; trackError: string | null; photos: number; located: number; items: GeoItem[] }
interface GeoState { running: boolean; total: number; done: number; failed: { path: string; name: string; error: string }[] }

const ORANGE = '#F2B53C'
const item = (p: Photo) => ({ path: p.path, name: p.name, isDir: false, size: 0, mime: p.mime, modified: '', etag: p.etag ?? undefined })

/**
 * Eine Aktivität von der Uhr: Strecke auf der Karte (Leaflet, OpenStreetMap), Dauer, Strecke, kcal und die Fotos aus der Galerie,
 * die währenddessen aufgenommen wurden - mit Ort als Punkte an der Strecke. Karte und gewähltes Foto stehen nebeneinander
 * (schmal untereinander), darunter alle Fotos zum Wählen.
 */
export function ActivityView({ id, onClose }: { id: number; onClose: () => void }) {
  const { fail } = useApp()
  const [d, setD] = useState<Detail | null>(null)
  const [big, setBig] = useState<Photo | null>(null)
  const box = useRef<HTMLDivElement>(null)
  // Verorten: Vorschau mit Versatz der Kamera-Uhr (Minuten), dann im Hintergrund schreiben und den Stand abfragen
  const [geo, setGeo] = useState<GeoPreview | null>(null)
  const [offset, setOffset] = useState('0')
  const [geoBusy, setGeoBusy] = useState<GeoState | null>(null)
  // Die Vorschau liest alle Fotos und holt die Strecke mit Zeiten - das dauert, also sichtbar warten
  const [geoLoading, setGeoLoading] = useState(false)
  const [geoMsg, setGeoMsg] = useState<{ intent: 'success' | 'warning'; text: string } | null>(null)
  const load = useCallback(() => api.get<Detail>('/api/coach/activities/' + id), [id])
  useEffect(() => { load().then(x => { setD(x); setBig(x.photos[0] ?? null) }).catch(e => { fail(e); onClose() }) }, [load, fail, onClose])
  const seconds = Math.round((Number(offset.replace(',', '.')) || 0) * 60)
  const preview = useCallback(() => api.get<GeoPreview>(`/api/coach/activities/${id}/geotag?offset=${seconds}`).then(setGeo).catch(fail), [id, seconds, fail])
  useEffect(() => {
    if (!geo) return
    const timer = setTimeout(preview, 400)
    return () => clearTimeout(timer)
  }, [seconds]) // eslint-disable-line react-hooks/exhaustive-deps
  const ready = geo?.items.filter(x => x.state === 'ok') ?? []
  // Fotos wie in der Liste: JPG und RAW gleichen Namens im selben Ordner sind ein Foto, verortet werden beide Dateien
  const readyPhotos = new Set(ready.map(x => x.path.replace(/\.[^./]+$/, '').toLowerCase())).size
  const startGeo = async () => {
    try {
      await api.post(`/api/coach/activities/${id}/geotag`, { offset: seconds, paths: ready.map(x => x.path) })
      setGeo(null)
      let s: GeoState
      do {
        await new Promise(r => setTimeout(r, 1200))
        s = await api.get<GeoState>(`/api/coach/activities/${id}/geotag/state`)
        setGeoBusy(s)
      } while (s.running)
      setGeoBusy(null)
      setGeoMsg(s.failed.length
        ? { intent: 'warning', text: t('{n} von {m} Dateien verortet. Nicht geschrieben: {list}', { n: s.total - s.failed.length, m: s.total, list: s.failed.map(f => `${f.name} (${f.error})`).join(', ') }) }
        : { intent: 'success', text: t('{n} Dateien verortet.', { n: s.total }) })
      setD(await load())
    } catch (e) { setGeoBusy(null); fail(e) }
  }
  const located = d?.photos.filter(p => p.lat != null && p.lon != null) ?? []
  const pending = geo?.items.filter(x => x.state === 'ok' && x.lat != null) ?? []
  const hasMap = !!d && (d.track.length > 1 || located.length > 0)
  // Fotos in der Karte: gewähltes hervorheben (aus der Karte oder aus der Liste darunter)
  const mapRef = useRef<L.Map | null>(null)
  const markers = useRef(new Map<string, { marker: L.Marker; photo: Photo }>())
  const photoIcon = (p: Photo, on: boolean) => L.divIcon({ className: '', iconSize: on ? [52, 52] : [40, 40], iconAnchor: on ? [26, 26] : [20, 20],
    html: `<img src="${gallerySource.thumb(item(p), 128)}" style="width:${on ? 52 : 40}px;height:${on ? 52 : 40}px;object-fit:cover;border-radius:8px;border:${on ? `3px solid ${ORANGE}` : '2px solid #fff'};box-shadow:0 1px 4px rgba(0,0,0,.4)">` })
  useEffect(() => {
    for (const [path, { marker, photo }] of markers.current) {
      const on = big?.path === path
      marker.setIcon(photoIcon(photo, on))
      marker.setZIndexOffset(on ? 1000 : 0)
      // Gewähltes außerhalb des Ausschnitts: dorthin schieben
      if (on && mapRef.current && !mapRef.current.getBounds().contains(marker.getLatLng())) mapRef.current.panTo(marker.getLatLng())
    }
  }, [big]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!box.current || !d || !hasMap) return
    const map = L.map(box.current)
    mapRef.current = map
    markers.current.clear()
    // Kartenbilder von OpenStreetMap - nur mit der Adresse des Servers als Herkunft (wie im Atlas)
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      referrerPolicy: 'strict-origin', maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    }).addTo(map)
    const pts: L.LatLngExpression[] = []
    if (d.track.length > 1) {
      L.polyline(d.track, { color: ORANGE, weight: 5, opacity: 0.9 }).addTo(map)
      L.circleMarker(d.track[0], { radius: 6, color: '#fff', weight: 2, fillColor: '#2E9E5B', fillOpacity: 1 }).addTo(map)
      L.circleMarker(d.track[d.track.length - 1], { radius: 6, color: '#fff', weight: 2, fillColor: '#D13438', fillOpacity: 1 }).addTo(map)
      pts.push(...d.track)
    }
    for (const p of located) {
      const marker = L.marker([p.lat!, p.lon!], { icon: photoIcon(p, big?.path === p.path), zIndexOffset: big?.path === p.path ? 1000 : 0 }).on('click', () => setBig(p)).addTo(map)
      markers.current.set(p.path, { marker, photo: p })
      pts.push([p.lat!, p.lon!])
    }
    // Vorschau des Verortens: gestrichelt umrandet, noch nicht geschrieben
    for (const g of pending) {
      const p: Photo = { kind: g.path.startsWith('ext:') ? 'ext' : 'file', path: g.path, name: g.name, mime: g.mime, etag: g.etag, taken: g.taken, lat: g.lat, lon: g.lon }
      const icon = L.divIcon({ className: '', iconSize: [36, 36], iconAnchor: [18, 18],
        html: `<img src="${gallerySource.thumb(item(p), 128)}" style="width:36px;height:36px;object-fit:cover;border-radius:8px;border:2px dashed ${ORANGE};opacity:.85">` })
      L.marker([g.lat!, g.lon!], { icon, title: g.name }).addTo(map)
    }
    map.fitBounds(L.latLngBounds(pts), { padding: [24, 24], maxZoom: 16 })
    return () => { map.remove(); mapRef.current = null; markers.current.clear() }
  }, [d, hasMap, geo]) // eslint-disable-line react-hooks/exhaustive-deps
  const min = Math.round((d?.durationS ?? 0) / 60)
  const icon = d ? (/cycl|bik/.test(d.type) ? '🚴' : /run/.test(d.type) ? '🏃' : '🚶') : ''
  return (
    <Dialog open onOpenChange={(_, x) => { if (!x.open) onClose() }}>
      <DialogSurface style={{ maxWidth: d?.photos.length ? 1180 : 760 }}>
        <DialogBody>
          <DialogContent>
            {!d ? <Spinner /> : <div style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <Text size={600} weight="semibold" style={{ color: ORANGE, flex: 1 }}>{icon} {d.name || d.type}</Text>
                <Button appearance="subtle" onClick={onClose}>{t('Schließen')}</Button>
              </div>
              <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(110px, 1fr))', gap: 8 }}>
                {[[t('Start'), d.start.slice(11, 16)], [t('Dauer'), min ? `${Math.floor(min / 60)}:${String(min % 60).padStart(2, '0')} h` : '–'],
                  [t('Strecke'), d.distanceM ? fmt(d.distanceM / 1000, 2) + ' km' : '–'], [t('Verbrannt'), d.kcal != null ? fmt(d.kcal) + ' kcal' : '–']].map(([l, v]) =>
                  <div key={l} style={{ backgroundColor: ORANGE + '17', border: `1px solid ${ORANGE}59`, borderRadius: 12, padding: '8px 10px' }}>
                    <Text size={200} style={{ color: ORANGE }} block>{l}</Text><Text weight="semibold" size={400}>{v}</Text>
                  </div>)}
              </div>
              {d.unlocated > 0 && d.track.length > 1 && !geo && !geoBusy &&
                <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                  <Button disabled={geoLoading} onClick={() => { setGeoMsg(null); setGeoLoading(true); preview().finally(() => setGeoLoading(false)) }}>
                    {t('{n} Fotos ohne Ort verorten', { n: d.unlocated })}</Button>
                  {geoLoading && <><Spinner size="tiny" /><Text>{t('Fotos werden mit der Strecke abgeglichen …')}</Text></>}
                </div>}
              {geoBusy && <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <Spinner size="tiny" /><Text>{t('Dateien werden verortet … {n} von {m}', { n: geoBusy.done, m: geoBusy.total })}</Text>
              </div>}
              {geoMsg && <MessageBar intent={geoMsg.intent}><MessageBarBody>{geoMsg.text}</MessageBarBody></MessageBar>}
              {geo && <div style={{ display: 'flex', flexDirection: 'column', gap: 8, padding: 12, borderRadius: 12, border: `1px solid ${ORANGE}59`, backgroundColor: ORANGE + '10' }}>
                {!geo.timed
                  ? <Text>{geo.trackError ?? t('Die Strecke hat keine Zeitangaben – Fotos lassen sich damit nicht verorten.')}</Text>
                  : <>
                    <Text>{!ready.length ? t('Keine Fotos zum Verorten.')
                      : readyPhotos < ready.length
                        ? t('{n} Fotos ohne Ort bekommen eine Stelle auf der Strecke (gestrichelt auf der Karte), zusammen {f} Dateien: JPG in die Datei, RAW in eine XMP-Datei daneben.', { n: readyPhotos, f: ready.length })
                        : t('{n} Fotos ohne Ort bekommen eine Stelle auf der Strecke (gestrichelt auf der Karte). JPG: in die Datei, RAW: in eine XMP-Datei daneben.', { n: ready.length })}</Text>
                    {([['outside', t('{n} außerhalb der Aufzeichnung', { n: geo.items.filter(x => x.state === 'outside').length })],
                      ['readonly', t('{n} in einem Ordner nur zum Lesen', { n: geo.items.filter(x => x.state === 'readonly').length })],
                      ['unsupported', t('{n} in einem Format, das hier nicht beschrieben wird', { n: geo.items.filter(x => x.state === 'unsupported').length })]] as const)
                      .filter(([k]) => geo.items.some(x => x.state === k))
                      .map(([k, label]) => <Text key={k} size={200} style={{ color: tokens.colorNeutralForeground3 }}>{label}</Text>)}
                    {ready.some(x => x.rough) && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
                      {t('{n} davon in einer Lücke der Aufzeichnung – die Stelle ist ungefähr.', { n: ready.filter(x => x.rough).length })}</Text>}
                    <Field label={t('Uhr der Kamera korrigieren (Minuten, +5 = Fotos 5 Minuten später einordnen)')}>
                      <Input value={offset} onChange={(_, x) => setOffset(x.value)} style={{ width: 110 }} />
                    </Field>
                  </>}
                <div style={{ display: 'flex', gap: 8 }}>
                  <Button appearance="primary" disabled={!geo.timed || ready.length === 0} onClick={startGeo}>{readyPhotos < ready.length ? t('{n} Fotos verorten ({f} Dateien)', { n: readyPhotos, f: ready.length }) : t('{n} Fotos verorten', { n: ready.length })}</Button>
                  <Button onClick={() => setGeo(null)}>{t('Abbrechen')}</Button>
                </div>
              </div>}
              {!hasMap && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{d.trackError ?? t('Zu dieser Aktivität gibt es keine Strecke.')}</Text>}
              {(hasMap || big) && <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
                {hasMap && <div ref={box} style={{ flex: '1 1 340px', height: 420, borderRadius: 12, overflow: 'hidden' }} />}
                {big && <div style={{ flex: '1 1 340px', height: 420, borderRadius: 12, overflow: 'hidden', backgroundColor: '#000', display: 'flex', alignItems: 'center', justifyContent: 'center' }}>
                  <img src={gallerySource.thumb(item(big), 1600)} alt={big.name} style={{ maxWidth: '100%', maxHeight: '100%', objectFit: 'contain' }} />
                </div>}
              </div>}
              {d.photos.length > 0 && <>
                <Text weight="semibold">{t('Fotos währenddessen ({n})', { n: d.photos.length })}</Text>
                <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(96px, 1fr))', gap: 6 }}>
                  {d.photos.map(p => <img key={p.path} src={gallerySource.thumb(item(p), 256)} alt={p.name} loading="lazy" onClick={() => setBig(p)}
                    style={{ width: '100%', aspectRatio: '1', objectFit: 'cover', borderRadius: 8, cursor: 'pointer', boxSizing: 'border-box',
                      border: big?.path === p.path ? `3px solid ${ORANGE}` : '3px solid transparent' }} />)}
                </div>
              </>}
            </div>}
          </DialogContent>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
