import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import { Button, Input, Text, tokens } from '@fluentui/react-components'
import { Search20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api, q } from '../api'
import { t } from '../i18n'

interface PlaceHit { name: string; region?: string | null; country?: string | null; lat: number; lon: number }

/**
 * Karte zum Verorten (Details, mehrere Fotos): Ort suchen, Rechtsklick in die Karte wählt die Stelle. Vorhandene Orte der Fotos als
 * Punkte, der Ausschnitt zeigt sie; ohne Orte Mitteleuropa.
 */
export default function PlacePickMap({ points, onPick, height = '220px' }: { points: [number, number][]; onPick: (lat: number, lon: number) => void; height?: string }) {
  const box = useRef<HTMLDivElement>(null)
  const map = useRef<L.Map | null>(null)
  const pickRef = useRef(onPick)
  pickRef.current = onPick
  const [query, setQuery] = useState('')
  const [hits, setHits] = useState<PlaceHit[] | null>(null)

  useEffect(() => {
    if (!box.current) return
    const m = L.map(box.current, { zoomControl: true, attributionControl: true, scrollWheelZoom: 'center' })
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      referrerPolicy: 'strict-origin', maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    }).addTo(m)
    const brand = getComputedStyle(document.documentElement).getPropertyValue('--colorBrandBackground').trim() || '#7B1DD3'
    for (const [la, lo] of points) L.circleMarker([la, lo], { radius: 5, color: '#fff', weight: 1.5, fillColor: brand, fillOpacity: 1, interactive: false }).addTo(m)
    if (points.length) m.fitBounds(L.latLngBounds(points), { padding: [20, 20], maxZoom: 15 })
    else m.setView([50.5, 10], 5)
    m.on('contextmenu', e => { e.originalEvent.preventDefault(); pickRef.current(e.latlng.lat, e.latlng.lng) })
    const fit = window.setTimeout(() => m.invalidateSize(), 250)
    map.current = m
    return () => { window.clearTimeout(fit); m.remove(); map.current = null }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const search = async () => {
    if (query.trim().length < 2) return
    try { setHits(await api.get<PlaceHit[]>('/api/gallery/places?' + q({ q: query.trim() }))) } catch { setHits([]) }
  }
  const go = (h: PlaceHit) => { map.current?.setView([h.lat, h.lon], 14); setHits(null) }

  return <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
    <div style={{ display: 'flex', gap: 6 }}>
      <Input size="small" style={{ flex: 1 }} value={query} placeholder={t('Ort suchen')} onChange={(_, d) => setQuery(d.value)}
        onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); search() } }} />
      <Button size="small" icon={<Search20Regular />} aria-label={t('Ort suchen')} onClick={search} />
    </div>
    {hits && (hits.length === 0 ? <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Kein Ort gefunden.')}</Text>
      : <div style={{ display: 'flex', flexDirection: 'column' }}>
        {hits.map((h, k) => <Button key={k} size="small" appearance="subtle" style={{ justifyContent: 'flex-start', fontWeight: 400 }} onClick={() => go(h)}>
          {[h.name, h.region, h.country].filter(Boolean).join(', ')}</Button>)}
      </div>)}
    <div ref={box} style={{ width: '100%', height, borderRadius: '6px', zIndex: 0 }} />
    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Rechtsklick in die Karte setzt den Aufnahmeort.')}</Text>
  </div>
}
