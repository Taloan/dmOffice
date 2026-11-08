import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import { useEffect, useRef } from 'react'

/**
 * Kartenausschnitt für einen Standort im Chat (OpenStreetMap wie im Atlas): Markierung, Kreis für die Genauigkeit; folgt Live-Positionen.
 * controls: Zoom-Knöpfe und Mausrad (Aufnahmeort in den Details, große Karte); height: Höhe des Kastens; onPick: Rechtsklick in die
 * Karte wählt eine Stelle (Aufnahmeort neu setzen).
 */
export default function LocationMap({ lat, lon, accuracy, controls, height = '150px', onPick }: { lat: number; lon: number; accuracy?: number | null; controls?: boolean; height?: string; onPick?: (lat: number, lon: number) => void }) {
  const pickRef = useRef(onPick)
  pickRef.current = onPick
  const box = useRef<HTMLDivElement>(null)
  const map = useRef<{ map: L.Map; dot: L.CircleMarker; ring: L.Circle } | null>(null)

  useEffect(() => {
    if (!box.current) return
    const m = L.map(box.current, { zoomControl: !!controls, attributionControl: true, scrollWheelZoom: controls ? 'center' : false })
    // OpenStreetMap sperrt Kartenbilder ohne Herkunftsangabe: nur die Adresse des Servers, nicht die Seite
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      referrerPolicy: 'strict-origin', maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    }).addTo(m)
    const brand = getComputedStyle(document.documentElement).getPropertyValue('--colorBrandBackground').trim() || '#7B1DD3'
    const ring = L.circle([lat, lon], { radius: accuracy ?? 0, color: brand, weight: 1, fillOpacity: 0.12 }).addTo(m)
    // Der Punkt selbst tut nichts beim Anklicken - kein Handzeiger
    const dot = L.circleMarker([lat, lon], { radius: 7, color: '#fff', weight: 2, fillColor: brand, fillOpacity: 1, interactive: false }).addTo(m)
    m.on('contextmenu', e => { if (pickRef.current) { e.originalEvent.preventDefault(); pickRef.current(e.latlng.lat, e.latlng.lng) } })
    m.setView([lat, lon], 16)
    // In einem Dialog steht die Größe erst nach dem Aufklappen fest
    const fit = window.setTimeout(() => m.invalidateSize(), 250)
    map.current = { map: m, dot, ring }
    return () => { window.clearTimeout(fit); m.remove(); map.current = null }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  // Live: neue Position in derselben Nachricht
  useEffect(() => {
    const c = map.current
    if (!c) return
    c.dot.setLatLng([lat, lon])
    c.ring.setLatLng([lat, lon]).setRadius(accuracy ?? 0)
    c.map.panTo([lat, lon])
  }, [lat, lon, accuracy])

  return <div ref={box} style={{ width: '100%', height, borderRadius: '6px', marginTop: '6px', zIndex: 0 }} />
}
