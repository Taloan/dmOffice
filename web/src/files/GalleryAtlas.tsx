import { Button, Text, tokens } from '@fluentui/react-components'
import { Location20Regular } from '@fluentui/react-icons'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import { useEffect, useRef, useState } from 'react'
import { t } from '../i18n'
import { Item, Source } from './common'
import { Meta } from './PhotoFilter'

/**
 * Atlas der Galerie: die gezeigten Fotos mit Aufnahmeort auf einer Karte (Leaflet, Kartenbilder von OpenStreetMap). Nahe beieinander
 * liegende fasst er zu einem Kreis mit Anzahl zusammen (je Zoomstufe neu); antippen zoomt hinein, bei wenigen zeigt er Vorschauen,
 * ein Foto antippen öffnet es. Filter und Album gelten wie in der Kachelansicht. Mit place (markierte Fotos): „Ort setzen“ und
 * auf die Karte tippen setzt ihn für alle markierten.
 */
export default function GalleryAtlas({ items, metaOf, source, onOpen, place }: {
  items: Item[]; metaOf: (i: Item) => Meta | undefined; source: Source; onOpen: (i: Item, list: Item[]) => void
  place?: { count: number; onPlace: (lat: number, lon: number) => void }
}) {
  const box = useRef<HTMLDivElement>(null)
  const placed = items.flatMap(i => { const m = metaOf(i); return m?.lat != null && m.lon != null ? [{ i, at: L.latLng(m.lat, m.lon) }] : [] })
  const [placing, setPlacing] = useState(false)
  const placingRef = useRef(false)
  placingRef.current = placing
  const onPlaceRef = useRef(place?.onPlace)
  onPlaceRef.current = place?.onPlace

  useEffect(() => {
    if (!box.current || (!placed.length && !place)) return
    const map = L.map(box.current, { worldCopyJump: true })
    // OpenStreetMap sperrt Kartenbilder ohne Herkunftsangabe: nur die Adresse des Servers, nicht die Seite
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      referrerPolicy: 'strict-origin', maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    }).addTo(map)
    const brand = getComputedStyle(document.documentElement).getPropertyValue('--colorBrandBackground').trim() || '#7B1DD3'
    const layer = L.layerGroup().addTo(map)
    const thumbs = (list: Item[]) => {
      const div = document.createElement('div')
      div.style.cssText = 'display:grid;grid-template-columns:repeat(3,72px);gap:4px'
      for (const i of list.slice(0, 9)) {
        const img = document.createElement('img')
        img.src = source.thumb(i, 256)
        img.style.cssText = 'width:72px;height:72px;object-fit:cover;border-radius:4px;cursor:pointer'
        img.onclick = () => onOpen(i, list)
        div.appendChild(img)
      }
      if (list.length > 9) {
        const more = document.createElement('div')
        more.textContent = t('und {n} weitere', { n: list.length - 9 })
        more.style.cssText = 'grid-column:1/-1;font-size:12px'
        div.appendChild(more)
      }
      return div
    }
    // Zusammenfassen: Raster von 56 Pixeln auf der aktuellen Zoomstufe
    const draw = () => {
      layer.clearLayers()
      const cells = new Map<string, { at: L.LatLng[]; items: Item[] }>()
      for (const p of placed) {
        const pt = map.project(p.at, map.getZoom())
        const k = Math.floor(pt.x / 56) + ':' + Math.floor(pt.y / 56)
        const c = cells.get(k) ?? { at: [], items: [] }
        c.at.push(p.at); c.items.push(p.i); cells.set(k, c)
      }
      for (const c of cells.values()) {
        const center = L.latLng(c.at.reduce((s, a) => s + a.lat, 0) / c.at.length, c.at.reduce((s, a) => s + a.lng, 0) / c.at.length)
        const n = c.items.length
        const size = n === 1 ? 44 : Math.min(64, 36 + Math.round(Math.log10(n) * 12))
        // Ein Foto: seine Vorschau als Punkt; mehrere: Vorschau des ersten mit Anzahl
        const html = `<div style="width:${size}px;height:${size}px;border-radius:50%;border:2px solid white;box-shadow:0 1px 4px rgba(0,0,0,.4);`
          + `background:${brand} center/cover no-repeat url('${source.thumb(c.items[0], 256)}');position:relative">`
          + (n > 1 ? `<span style="position:absolute;right:-6px;top:-6px;background:${brand};color:white;border-radius:10px;padding:0 6px;font:600 12px/18px sans-serif;border:1px solid white">${n}</span>` : '')
          + '</div>'
        const marker = L.marker(center, { icon: L.divIcon({ html, className: '', iconSize: [size, size], iconAnchor: [size / 2, size / 2] }) }).addTo(layer)
        if (n === 1) marker.on('click', () => onOpen(c.items[0], [c.items[0]]))
        // Viele: hineinzoomen, bis sie sich trennen; ganz nah oder wenige: Vorschauen zum Antippen
        else if (n > 9 && map.getZoom() < map.getMaxZoom() - 2) marker.on('click', () => map.fitBounds(L.latLngBounds(c.at), { padding: [60, 60], maxZoom: map.getZoom() + 4 }))
        else marker.bindPopup(thumbs(c.items), { maxWidth: 260 })
      }
    }
    map.on('zoomend', draw)
    // Ort setzen: Tippen auf die Karte
    map.on('click', e => { if (placingRef.current) { setPlacing(false); onPlaceRef.current?.(e.latlng.lat, e.latlng.lng) } })
    if (placed.length) map.fitBounds(L.latLngBounds(placed.map(p => p.at)), { padding: [40, 40], maxZoom: 13 })
    else map.setView([50, 10], 5)
    draw()
    return () => { map.remove() }
  }, [placed.length, items, !!place]) // eslint-disable-line react-hooks/exhaustive-deps

  useEffect(() => { if (box.current) box.current.style.cursor = placing ? 'crosshair' : '' }, [placing])

  if (!placed.length && !place) return <Text block style={{ margin: '48px auto', textAlign: 'center', color: tokens.colorNeutralForeground3 }}>{t('Keine Fotos mit Aufnahmeort.')}</Text>
  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100%', minHeight: 320, gap: 4 }}>
      {place && <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
        <Button icon={<Location20Regular />} appearance={placing ? 'primary' : 'secondary'} onClick={() => setPlacing(p => !p)}>
          {placing ? t('Auf die Karte tippen …') : t('Ort für {n} markierte Fotos setzen', { n: place.count })}
        </Button>
        {placing && <Button appearance="subtle" onClick={() => setPlacing(false)}>{t('Abbrechen')}</Button>}
      </div>}
      <div ref={box} className={placing ? 'atlas-placing' : undefined} style={{ flex: 1, minHeight: 320, borderRadius: 8, overflow: 'hidden' }} />
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
        {t('{n} von {total} Fotos mit Aufnahmeort. Die Kartenbilder kommen von OpenStreetMap.', { n: placed.length, total: items.length })}
      </Text>
    </div>
  )
}
