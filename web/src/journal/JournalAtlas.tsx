import { Spinner, Text, tokens } from '@fluentui/react-components'
import L from 'leaflet'
import 'leaflet/dist/leaflet.css'
import { useEffect, useRef, useState } from 'react'
import { useApp } from '../App'
import { t } from '../i18n'
import { journalApi, PlaceHit, thumbUrl } from './api'

/**
 * Atlas: alle Einträge mit Ort auf einer Karte (Leaflet, Kartenbilder von OpenStreetMap). Antippen zeigt Datum, Titel und Foto;
 * „Öffnen“ zeigt den Eintrag. Wird erst geladen, wenn man die Ansicht öffnet.
 */
export default function JournalAtlas({ journal, onOpen }: { journal: number | null; onOpen: (p: { journal: number; uri: string }) => void }) {
  const { fail } = useApp()
  const box = useRef<HTMLDivElement>(null)
  const [list, setList] = useState<PlaceHit[] | null>(null)
  useEffect(() => { journalApi.places(journal ?? undefined).then(setList).catch(fail) }, [journal, fail])

  useEffect(() => {
    if (!box.current || !list?.length) return
    const map = L.map(box.current, { worldCopyJump: true })
    // OpenStreetMap sperrt Kartenbilder ohne Herkunftsangabe (dmOffice schickt sonst keine nach draußen):
    // nur die Adresse des Servers, nicht die Seite
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', {
      referrerPolicy: 'strict-origin', maxZoom: 19, attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
    }).addTo(map)
    const brand = getComputedStyle(document.documentElement).getPropertyValue('--colorBrandBackground').trim() || '#7B1DD3'
    const points: L.LatLngExpression[] = []
    // Einträge am selben Ort (auf etwa 10 m genau) als ein Punkt mit Zahl; das Fenster listet alle, neueste zuerst
    const groups = new Map<string, PlaceHit[]>()
    for (const p of list) {
      const key = `${p.place.lat!.toFixed(4)},${p.place.lon!.toFixed(4)}`
      groups.set(key, [...(groups.get(key) ?? []), p])
    }
    const entryDiv = (p: PlaceHit, compact: boolean) => {
      const div = document.createElement('div')
      div.style.cssText = compact ? 'display:flex;gap:8px;align-items:center;padding:4px 0;border-top:1px solid rgba(128,128,128,.25)' : 'min-width:160px'
      const day = new Date(p.date).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })
      if (p.photo) {
        const img = document.createElement('img')
        img.src = thumbUrl(p.journal, p.photo, 256)
        img.style.cssText = compact ? 'width:48px;height:48px;object-fit:cover;border-radius:4px;flex-shrink:0' : 'width:160px;height:110px;object-fit:cover;border-radius:6px;display:block;margin-bottom:4px'
        div.appendChild(img)
      }
      const text = document.createElement('div')
      const head = document.createElement('div')
      head.style.fontWeight = '600'
      head.textContent = p.title || day
      text.appendChild(head)
      const sub = document.createElement('div')
      sub.textContent = [p.title ? day : null, compact ? null : p.place.name].filter(Boolean).join(' · ')
      text.appendChild(sub)
      const open = document.createElement('a')
      open.href = '#'
      open.textContent = t('Öffnen')
      open.onclick = e => { e.preventDefault(); onOpen({ journal: p.journal, uri: p.uri }) }
      text.appendChild(open)
      div.appendChild(text)
      return div
    }
    for (const group of groups.values()) {
      const p = group[0]
      const at: L.LatLngExpression = [p.place.lat!, p.place.lon!]
      points.push(at)
      if (group.length === 1) {
        L.circleMarker(at, { radius: 8, color: 'white', weight: 2, fillColor: brand, fillOpacity: 0.95 }).bindPopup(entryDiv(p, false)).addTo(map)
        continue
      }
      const sorted = [...group].sort((a, b) => b.date.localeCompare(a.date))
      const box = document.createElement('div')
      box.style.cssText = 'min-width:220px;max-height:280px;overflow-y:auto'
      const title = document.createElement('div')
      title.style.cssText = 'font-weight:600;margin-bottom:4px'
      title.textContent = (p.place.name ? p.place.name + ' · ' : '') + t('{n} Einträge', { n: group.length })
      box.appendChild(title)
      for (const e of sorted) box.appendChild(entryDiv(e, true))
      const icon = L.divIcon({
        className: '', iconSize: [28, 28], iconAnchor: [14, 14],
        html: `<div style="width:28px;height:28px;border-radius:50%;background:${brand};border:2px solid white;color:white;font:600 12px sans-serif;display:flex;align-items:center;justify-content:center">${group.length}</div>`,
      })
      L.marker(at, { icon }).bindPopup(box, { maxWidth: 320 }).addTo(map)
    }
    map.fitBounds(L.latLngBounds(points), { padding: [40, 40], maxZoom: 13 })
    return () => { map.remove() }
  }, [list, onOpen])

  if (!list) return <Spinner style={{ marginTop: 32 }} />
  if (!list.length) return <Text block style={{ margin: '48px auto', textAlign: 'center', color: tokens.colorNeutralForeground3 }}>{t('Noch keine Einträge mit Ort.')}</Text>
  return (
    <>
      <div ref={box} style={{ height: 'calc(100vh - 220px)', minHeight: 320, borderRadius: 8, overflow: 'hidden' }} />
      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Die Kartenbilder kommen von OpenStreetMap.')}</Text>
    </>
  )
}
