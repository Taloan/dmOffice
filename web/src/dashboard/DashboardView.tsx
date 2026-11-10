// Übersicht (Dashboard): Widgets in einem Raster über einem Hintergrundbild, zusammenstellbar wie die Widgets auf dem iPad.
import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Radio, RadioGroup, Select, Slider, Spinner, Text, Tooltip,
  makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowClockwise20Regular, ArrowLeft16Regular, ArrowRight16Regular, ArrowReset20Regular, Checkmark20Regular, Dismiss16Regular, Dismiss20Regular, Edit20Regular, Image20Regular, Settings16Regular,
} from '@fluentui/react-icons'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, featureOn, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { useIsMobile } from '../shell/mobile'
import { RibbonButton, RibbonDivider, RibbonHeader } from '../shell/Ribbon'
import { PickPhotoDialog } from '../files/PickPhotoDialog'
import { albumsApi, AlbumInfo, loadAlbum, albumThumb } from '../files/Albums'
import { Background, Device, Layout, Widget, WidgetType, dashboardApi, newId, dashCache } from './api'
import { WIDGET_INFO, WidgetBody } from './widgets'
import { Box, dims, firstFree, place, settle, withBoxes } from './placement'
import { askConfirm } from '../shell/context'

const useStyles = makeStyles({
  // flex: 1 - <main> ist eine Flex-Zeile, ohne wäre das Dashboard nur so breit wie sein Inhalt
  root: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0, height: '100%', minHeight: 0, position: 'relative' },
  stage: { flex: 1, minHeight: 0, position: 'relative', overflow: 'hidden' },
  bg: { position: 'absolute', inset: 0, backgroundSize: 'cover', backgroundPosition: 'center', transition: 'opacity .6s' },
  dim: { position: 'absolute', inset: 0 },
  scroll: { position: 'relative', height: '100%', overflowY: 'auto', padding: '18px 22px 32px', boxSizing: 'border-box' },
  // Breit: feste Stellen im Raster (Spaltenzahl aus der Breite, siehe placement.ts); Handy: der Reihe nach
  grid: { display: 'grid', gridAutoRows: '196px', gap: '16px' },
  ghost: { borderRadius: '20px', border: `2px dashed ${tokens.colorBrandStroke1}`, backgroundColor: 'color-mix(in srgb, ' + tokens.colorBrandBackground + ' 18%, transparent)', pointerEvents: 'none' },
  gridMobile: { gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gridAutoRows: '170px', gap: '10px', gridAutoFlow: 'dense' },
  card: {
    position: 'relative', borderRadius: '20px', padding: '14px', boxSizing: 'border-box', overflow: 'hidden', display: 'flex', flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, boxShadow: tokens.shadow4, minWidth: 0,
  },
  // Über einem Hintergrundbild: halbdurchsichtig, der Hintergrund schimmert durch
  glass: { backgroundColor: 'color-mix(in srgb, ' + tokens.colorNeutralBackground1 + ' 78%, transparent)', backdropFilter: 'blur(18px)', WebkitBackdropFilter: 'blur(18px)' },
  editing: { outline: `2px dashed ${tokens.colorBrandStroke2}`, outlineOffset: '2px', cursor: 'grab' },
  // Einfügemarke: links davor, rechts dahinter
  overBefore: { boxShadow: `-6px 0 0 -1px ${tokens.colorBrandStroke1}` },
  overInto: { outline: `3px solid ${tokens.colorBrandStroke1}`, outlineOffset: '2px' },
  overAfter: { boxShadow: `6px 0 0 -1px ${tokens.colorBrandStroke1}` },
  del: { position: 'absolute', top: '-9px', left: '-9px', zIndex: 2, minWidth: 0, width: '26px', height: '26px', borderRadius: '50%', padding: 0, backgroundColor: tokens.colorPaletteRedBackground3, color: '#fff', border: `2px solid ${tokens.colorNeutralBackground1}`, ':hover': { backgroundColor: tokens.colorPaletteRedForeground1 } },
  sizes: { position: 'absolute', top: '8px', right: '8px', zIndex: 2, display: 'flex', gap: '2px', backgroundColor: tokens.colorNeutralBackground3, borderRadius: '8px', padding: '2px' },
  sizeBtn: { minWidth: 0, padding: '2px 7px', fontSize: '11px', height: 'auto', borderRadius: '6px' },
  sizeOn: { backgroundColor: tokens.colorBrandBackground, color: '#fff', ':hover': { backgroundColor: tokens.colorBrandBackgroundHover, color: '#fff' } },
  gear: { position: 'absolute', bottom: '8px', right: '8px', zIndex: 2 },
  stackNav: { position: 'absolute', bottom: '6px', left: 0, right: 0, display: 'flex', justifyContent: 'center', gap: '5px', pointerEvents: 'none' },
  dot: { width: '6px', height: '6px', borderRadius: '3px', backgroundColor: tokens.colorNeutralForeground3, opacity: 0.5 },
  dotOn: { backgroundColor: tokens.colorBrandForeground1, opacity: 1 },
  tile: { textAlign: 'left', display: 'flex', flexDirection: 'column', alignItems: 'flex-start', gap: '2px', height: 'auto', padding: '10px', borderRadius: '12px', border: `1px solid ${tokens.colorNeutralStroke2}` },
  mut: { color: tokens.colorNeutralForeground3 },
  hint: { display: 'flex', justifyContent: 'flex-end', gap: '8px', alignItems: 'center', padding: '0 4px 8px' },
})

const DRAG = 'application/x-dmoffice-widget'
// Raster der breiten Ansicht: Mindestbreite einer Spalte, Höhe einer Reihe, Abstand
const COL_MIN = 230, COL_LEAST = 170, ROW = 196, GAP = 16
// Klein 1×1, mittel 2×1, groß 2×2, hoch 1×2 (Spalten × Zeilen)
const spanOf = (size: number, _mobile: boolean) => size === 1 ? { gridColumn: 'span 1', gridRow: 'span 1' } : size === 2 ? { gridColumn: 'span 2', gridRow: 'span 1' }
  : size === 4 ? { gridColumn: 'span 1', gridRow: 'span 2' } : { gridColumn: 'span 2', gridRow: 'span 2' }

/** Stapel: je nach Tageszeit das passende Widget vorn - morgens Wetter und Termine, mittags Aufgaben, abends Fotos. */
function smartOrder(list: Widget[]): Widget[] {
  const h = new Date().getHours()
  const prio: WidgetType[] = h < 11 ? ['weather', 'today', 'events', 'news', 'tasks'] : h < 17 ? ['tasks', 'events', 'mail', 'shopping', 'chat'] : ['photos', 'meals', 'events', 'chat']
  return [...list].sort((a, b) => (prio.indexOf(a.type) < 0 ? 99 : prio.indexOf(a.type)) - (prio.indexOf(b.type) < 0 ? 99 : prio.indexOf(b.type)))
}

function StackBody({ w, editing }: { w: Widget; editing: boolean }) {
  const s = useStyles()
  const list = useMemo(() => (w.smart ? smartOrder(w.stack ?? []) : w.stack ?? []), [w.stack, w.smart])
  const [n, setN] = useState(0)
  const cur = list[n % Math.max(1, list.length)]
  const x0 = useRef<number | null>(null)
  if (!list.length) return <Text size={200} className={s.mut}>{t('Noch leer – Widgets hineinlegen')}</Text>
  return (
    <div style={{ height: '100%', display: 'flex', flexDirection: 'column', minHeight: 0 }}
      onPointerDown={e => { x0.current = e.clientX }} onPointerUp={e => { if (x0.current != null && Math.abs(e.clientX - x0.current) > 40 && !editing) setN(v => (v + (e.clientX < x0.current! ? 1 : list.length - 1)) % list.length); x0.current = null }}>
      <div style={{ flex: 1, minHeight: 0 }}><WidgetBody key={cur.id} w={{ ...cur, size: w.size }} /></div>
      {list.length > 1 && <>
        <div className={s.stackNav}>{list.map((x, i) => <span key={x.id} className={mergeClasses(s.dot, i === n % list.length && s.dotOn)} />)}</div>
        <Button appearance="transparent" size="small" icon={<ArrowLeft16Regular />} aria-label="‹" style={{ position: 'absolute', left: 2, bottom: -4, opacity: .5 }} onClick={e => { e.stopPropagation(); setN(v => (v + list.length - 1) % list.length) }} />
        <Button appearance="transparent" size="small" icon={<ArrowRight16Regular />} aria-label="›" style={{ position: 'absolute', right: 2, bottom: -4, opacity: .5 }} onClick={e => { e.stopPropagation(); setN(v => (v + 1) % list.length) }} />
      </>}
    </div>
  )
}

export default function DashboardView() {
  const s = useStyles()
  const { fail, notify, me } = useApp()
  const mobile = useIsMobile()
  // Eigene Zusammenstellung für breite Bildschirme und fürs Handy
  const device: Device = mobile ? 'phone' : 'desktop'
  const [layout, setLayoutState] = useState<Layout | null>(dashCache.layout[device] ?? null)
  // Gemerkt für die Rückkehr in die Übersicht (sofort da, dann aufgefrischt)
  const setLayout: typeof setLayoutState = v => setLayoutState(prev => { const next = typeof v === 'function' ? v(prev) : v; if (next) dashCache.layout[device] = next; return next })
  // Anpassen: Stand von vorher - Änderungen bleiben bis „Fertig“ nur hier, „Abbrechen“ stellt ihn wieder her
  const [before, setBefore] = useState<Layout | null>(null)
  const editing = before != null
  const [adding, setAdding] = useState<null | { into?: string }>(null)
  const [settings, setSettings] = useState<Widget | null>(null)
  const [bgDialog, setBgDialog] = useState(false)
  const [drag, setDrag] = useState<string | null>(null)
  const [over, setOver] = useState<{ id: string; after: boolean; into?: boolean } | null>(null)
  const [tick, setTick] = useState(0)
  // Breit: Spaltenzahl aus der Breite des Rasters; beim Ziehen die Zielstelle (gestrichelt) und wo man das Widget gegriffen hat
  const gridEl = useRef<HTMLDivElement | null>(null)
  const [width, setWidth] = useState(0)
  const [target, setTarget] = useState<Box | null>(null)
  const grab = useRef({ x: 0, y: 0 })
  // Breite des Rasters beobachten, sobald es da ist (es erscheint erst mit der Zusammenstellung)
  const observer = useRef<ResizeObserver | null>(null)
  const gridRef = useCallback((el: HTMLDivElement | null) => {
    observer.current?.disconnect(); observer.current = null
    gridEl.current = el
    if (!el) return
    setWidth(el.clientWidth)
    observer.current = new ResizeObserver(() => setWidth(el.clientWidth))
    observer.current.observe(el)
  }, [])
  const [bgUrl, setBgUrlState] = useState<string | null>(dashCache.bg[device] ?? null)
  const setBgUrl = (u: string | null) => { dashCache.bg[device] = u; setBgUrlState(u) }
  // Fenster schmal oder breit gezogen: die andere Zusammenstellung
  useEffect(() => {
    setLayoutState(dashCache.layout[device] ?? null); setBgUrlState(dashCache.bg[device] ?? null)
    dashboardApi.layout(device).then(setLayout).catch(fail)
  }, [device]) // eslint-disable-line react-hooks/exhaustive-deps

  // Hintergrund: hochgeladen (vom Server), aus der Galerie (großes Vorschaubild) oder jeden Tag ein anderes aus einem Album
  useEffect(() => {
    const b = layout?.background
    if (!b || b.kind === 'none') { setBgUrl(null); return }
    if (b.kind === 'upload') { setBgUrl(`/api/dashboard/background?device=${device}&v=${tick}`); return }
    if (b.kind === 'gallery' && b.path) {
      setBgUrl(b.path.startsWith('ext:') ? '/api/gallery/external/thumb?' + q({ id: b.path.slice(4), size: 1600 }) : '/api/files/thumb?' + q({ path: b.path, size: 1600 }))
      return
    }
    if (b.kind === 'album' && b.album != null) {
      loadAlbum(b.album).then(a => {
        // Fotos am Typ erkennen, sonst an der Endung (aus Galerie-Ordnern fehlt der Typ manchmal)
        const imgs = a.items.filter(i => (i.mime ?? '').startsWith('image/') || (!(i.mime ?? '').startsWith('video/') && /\.(jpe?g|png|webp|heic|heif|gif|avif|tiff?|arw|cr[23]|nef|raf|orf|rw2|dng|pef|srw)$/i.test(i.name)))
        if (!imgs.length) { setBgUrl(null); return }
        const dayN = Math.floor(Date.now() / 86_400_000)
        setBgUrl(albumThumb(imgs[dayN % imgs.length], 1600))
      }).catch(() => setBgUrl(null))
    }
  }, [layout?.background.kind, layout?.background.path, layout?.background.album, tick])

  const save = async (next: Layout) => {
    setLayout(next)
    try { setLayout(await dashboardApi.save(next, device)) } catch (e) { fail(e) }
  }
  // Beim Anpassen nur hier ändern (immer auf dem neuesten Stand, auch bei mehreren Änderungen kurz nacheinander)
  const patch = (fn: (l: Layout) => Layout) => { if (editing) setLayout(l => l && fn(l)); else if (layout) save(fn(layout)) }
  // Widget in einen Stapel legen (klein, hinten an)
  const intoStack = (from: string, stack: string) => patch(l => {
    const x = l.widgets.find(w => w.id === from)
    if (!x || x.type === 'stack') return l
    return { ...l, widgets: l.widgets.filter(w => w.id !== from).map(w => w.id === stack ? { ...w, stack: [...(w.stack ?? []), { ...x, size: 1 as const }] } : w) }
  })
  const startEdit = () => { if (layout && !before) setBefore(layout) }
  // Spalten: die gespeicherte Zahl, solange jede Spalte noch mindestens COL_LEAST breit ist - sonst so viele, wie hineinpassen
  const fitting = Math.max(1, Math.floor((width + GAP) / (COL_MIN + GAP)))
  const cols = layout?.cols && (width + GAP) / layout.cols - GAP >= COL_LEAST ? layout.cols : fitting
  // Breit: die angezeigten Stellen mitspeichern (auch die der neuen Widgets) und die Spaltenzahl dazu
  const fix = (l: Layout): Layout => mobile || !width ? l : { ...l, cols, widgets: withBoxes(l.widgets, place(l.widgets.filter(w => w.type !== 'system' || me.isAdmin), cols)) }
  const finish = async () => { const l = layout && fix(layout); setBefore(null); if (l) try { setLayout(await dashboardApi.save(l, device)) } catch (e) { fail(e) } }
  const cancel = () => { if (before) setLayout(l => ({ ...before, background: l?.background ?? before.background })); setBefore(null); setSettings(null); setAdding(null) }
  const update = (id: string, fn: (w: Widget) => Widget) => patch(l => ({ ...l, widgets: l.widgets.map(w => w.id === id ? fn(w) : w.stack ? { ...w, stack: w.stack.map(x => x.id === id ? fn(x) : x) } : w) }))
  const remove = (id: string) => patch(l => ({ ...l, widgets: l.widgets.filter(w => w.id !== id).map(w => w.stack ? { ...w, stack: w.stack.filter(x => x.id !== id) } : w) }))
  const add = (type: WidgetType, into?: string) => {
    const w: Widget = { id: newId(), type, size: WIDGET_INFO[type].size, opts: {}, ...(type === 'stack' ? { stack: [], smart: true } : {}) }
    if (into) update(into, st => ({ ...st, stack: [...(st.stack ?? []), { ...w, size: 1 }] }))
    else patch(l => ({ ...l, widgets: [...l.widgets, w] }))
    setAdding(null)
    if (type !== 'stack' && Object.keys(SETTINGS[type] ?? {}).length) setSettings(w)
  }
  // Vor oder hinter ein anderes Widget; ohne Ziel (freie Fläche, „Widget hinzufügen“) ans Ende
  const move = (from: string, to: string | null, after = false) => patch(l => {
    const list = [...l.widgets]
    const a = list.findIndex(w => w.id === from)
    if (a < 0) return l
    const [x] = list.splice(a, 1)
    const b = to == null ? list.length : list.findIndex(w => w.id === to)
    if (b < 0) return l
    list.splice(to == null ? b : b + (after ? 1 : 0), 0, x)
    return { ...l, widgets: list }
  })
  // Breit: an eine Stelle setzen (verschoben oder neue Größe) - was im Weg ist, rutscht nach unten
  const putAt = (id: string, size: Widget['size'], x: number, y: number) => patch(l => {
    const shown = l.widgets.filter(w => w.type !== 'system' || me.isAdmin)
    const { w, h } = dims(size)
    const ww = Math.min(w, cols)
    const at = { x: Math.max(0, Math.min(x, cols - ww)), y: Math.max(0, y), w: ww, h }
    const boxes = settle(place(shown, cols), id, at)
    return { ...l, cols, widgets: withBoxes(l.widgets.map(x0 => x0.id === id ? { ...x0, size } : x0), boxes) }
  })
  // Zielstelle unter dem Mauszeiger (dort, wo die gegriffene Ecke des Widgets landet)
  const cellAt = (e: React.DragEvent, size: number): Box | null => {
    const el = gridEl.current
    if (!el) return null
    const r = el.getBoundingClientRect()
    const colW = (r.width - GAP * (cols - 1)) / cols
    const { w, h } = dims(size)
    const ww = Math.min(w, cols)
    const x = Math.round((e.clientX - grab.current.x - r.left) / (colW + GAP))
    const y = Math.round((e.clientY - grab.current.y - r.top) / (ROW + GAP))
    return { x: Math.max(0, Math.min(x, cols - ww)), y: Math.max(0, y), w: ww, h }
  }
  // Zurück auf die Vorgabe dieser Geräteart (Handy: wieder die Desktop-Fassung)
  const reset = async () => { if (await askConfirm('', t('Diese Übersicht auf die Vorgabe zurücksetzen?'))) { setBefore(null); dashboardApi.reset(device).then(setLayout).catch(fail) } }

  if (!layout) return <Spinner style={{ margin: 'auto' }} />
  const hasBg = !!bgUrl
  const widgets = layout.widgets.filter(w => w.type !== 'system' || me.isAdmin)
  const boxes = mobile ? null : place(widgets, cols)
  const posOf = (b: Box | undefined) => b ? { gridColumn: `${b.x + 1} / span ${b.w}`, gridRow: `${b.y + 1} / span ${b.h}` } : {}
  const bottom = boxes ? Math.max(0, ...[...boxes.values()].map(b => b.y + b.h)) : 0
  const addAt = boxes ? firstFree([...boxes.values()], 1, 1, cols) : null

  const ribbon = (
    <RibbonHeader area="dashboard"
      start={<>
        <RibbonButton primary={editing} icon={editing ? <Checkmark20Regular /> : <Edit20Regular />} label={editing ? t('Fertig') : t('Anpassen')} onClick={() => editing ? finish() : startEdit()} />
        {editing && <RibbonButton icon={<Dismiss20Regular />} label={t('Abbrechen')} onClick={cancel} />}
        <RibbonButton icon={<Add20Regular />} label={t('Widget hinzufügen')} onClick={() => { startEdit(); setAdding({}) }} />
        <RibbonButton icon={<Image20Regular />} label={t('Hintergrund')} onClick={() => setBgDialog(true)} />
        <RibbonDivider />
        <RibbonButton icon={<ArrowClockwise20Regular />} label={t('Aktualisieren')} onClick={() => setTick(x => x + 1)} />
      </>}
      view={<RibbonButton icon={<ArrowReset20Regular />} label={t('Zurücksetzen')} onClick={reset} />} />
  )

  return (
    <div className={s.root}>
      {ribbon}
      <div className={s.stage}>
        {bgUrl && <div className={s.bg} style={{ backgroundImage: `url("${bgUrl}")` }} />}
        {bgUrl && <div className={s.dim} style={{ background: `rgba(0,0,0,${layout.background.dim / 100})` }} />}
        <div className={s.scroll}>
          {editing && !mobile && <div className={s.hint}><Text size={200} style={{ color: hasBg ? '#fff' : undefined, textShadow: hasBg ? '0 1px 4px rgba(0,0,0,.6)' : undefined }}>{t('Ziehen zum Verschieben, auf die Mitte eines Stapels zum Hineinlegen · Größe oben rechts · ⚙ für Einstellungen')}</Text></div>}
          <div key={tick} ref={gridRef} className={mergeClasses(s.grid, mobile && s.gridMobile)}
            // Breit: beim Anpassen eine Reihe Platz unter dem letzten Widget - zum Hinziehen
            style={mobile ? undefined : { gridTemplateColumns: `repeat(${cols}, minmax(0, 1fr))`, minHeight: editing ? (bottom + 1) * (ROW + GAP) : undefined }}
            onDragOver={e => {
              if (!editing || !e.dataTransfer.types.includes(DRAG)) return
              e.preventDefault(); setOver(null)
              const w = widgets.find(x => x.id === drag)
              if (!mobile && w) { const b = cellAt(e, w.size); setTarget(o => o && b && o.x === b.x && o.y === b.y ? o : b) }
            }}
            onDragLeave={e => { if (e.currentTarget === e.target) setTarget(null) }}
            onDrop={e => {
              const from = e.dataTransfer.getData(DRAG)
              if (!from) return
              e.preventDefault()
              const w = widgets.find(x => x.id === from)
              const b = !mobile && w ? cellAt(e, w.size) : null
              setTarget(null)
              if (b && w) putAt(from, w.size, b.x, b.y); else move(from, null)
            }}>
            {widgets.map(w => {
              const info = WIDGET_INFO[w.type]
              const hasSettings = w.type === 'stack' || Object.keys(SETTINGS[w.type] ?? {}).length > 0
              return (
                <section key={w.id} className={mergeClasses(s.card, hasBg && s.glass, editing && s.editing, over?.id === w.id && (over.into ? s.overInto : over.after ? s.overAfter : s.overBefore))}
                  style={boxes ? posOf(boxes.get(w.id)) : spanOf(w.size, mobile)}
                  aria-label={w.title || info?.label()}
                  draggable={editing} onDragStart={e => {
                    e.dataTransfer.setData(DRAG, w.id); setDrag(w.id)
                    const r = e.currentTarget.getBoundingClientRect(); grab.current = { x: e.clientX - r.left, y: e.clientY - r.top }
                  }} onDragEnd={() => { setDrag(null); setOver(null); setTarget(null) }}
                  onDragOver={e => {
                    if (!editing || !e.dataTransfer.types.includes(DRAG)) return
                    const r = e.currentTarget.getBoundingClientRect()
                    // Auf die Mitte eines Stapels: hinein (ein Stapel selbst nicht)
                    const into = w.type === 'stack' && drag !== null && drag !== w.id && widgets.find(x => x.id === drag)?.type !== 'stack' && Math.abs(e.clientX - (r.left + r.width / 2)) < r.width / 5
                    // Breit: sonst entscheidet das Raster (Zielstelle) - das Ereignis geht weiter nach oben
                    if (!mobile && !into) { setOver(o => o?.id === w.id ? null : o); return }
                    e.preventDefault(); e.stopPropagation()
                    if (into) setTarget(null)
                    // Handy: rechte Hälfte dahinter, linke Hälfte davor
                    const after = e.clientX > r.left + r.width / 2
                    setOver(o => o?.id === w.id && o.after === after && !!o.into === into ? o : { id: w.id, after, into })
                  }} onDragLeave={() => setOver(o => o?.id === w.id ? null : o)}
                  onDrop={e => {
                    const from = e.dataTransfer.getData(DRAG); const after = over?.after ?? false; const into = !!over?.into
                    if (!mobile && !into) return
                    setOver(null); e.stopPropagation(); if (from && from !== w.id) { e.preventDefault(); if (into) intoStack(from, w.id); else move(from, w.id, after) }
                  }}>
                  {editing && <>
                    <Tooltip content={t('Widget entfernen')} relationship="label"><Button className={s.del} aria-label={t('Widget entfernen')} icon={<Dismiss16Regular />} onClick={() => remove(w.id)} /></Tooltip>
                    <div className={s.sizes}>
                      {([[1, 'S', 'Klein'], [2, 'M', 'Mittel'], [4, 'H', 'widget|Hoch'], [3, 'L', 'Groß']] as const).map(([n, k, l]) => <Button key={n} size="small" appearance="transparent" className={mergeClasses(s.sizeBtn, w.size === n && s.sizeOn)} title={t(l)} onClick={() => { const b = boxes?.get(w.id); if (b) putAt(w.id, n, b.x, b.y); else update(w.id, x => ({ ...x, size: n })) }}>{k}</Button>)}
                    </div>
                    {hasSettings && <Button size="small" className={s.gear} icon={<Settings16Regular />} onClick={() => setSettings(w)}>{t('Einstellungen')}</Button>}
                  </>}
                  {w.title && w.type !== 'today' && w.type !== 'photos' && <Text size={200} weight="semibold" style={{ marginBottom: 4, color: tokens.colorNeutralForeground2 }}>{w.title}</Text>}
                  <div style={{ flex: 1, minHeight: 0, pointerEvents: editing ? 'none' : undefined, opacity: editing && drag === w.id ? .5 : 1 }}>
                    {w.type === 'stack' ? <StackBody w={w} editing={editing} /> : <WidgetBody w={w} />}
                  </div>
                </section>
              )
            })}
            {target && <div className={s.ghost} style={posOf(target)} />}
            {editing && (
              <button className={mergeClasses(s.card, hasBg && s.glass)} style={{ ...(addAt ? posOf({ ...addAt, w: 1, h: 1 }) : spanOf(1, mobile)), alignItems: 'center', justifyContent: 'center', cursor: 'pointer', borderStyle: 'dashed', font: 'inherit', color: 'inherit' }} onClick={() => setAdding({})}>
                <Add20Regular /><Text size={200}>{t('Widget hinzufügen')}</Text>
              </button>
            )}
          </div>
        </div>
      </div>
      {adding && <AddDialog admin={me.isAdmin} into={adding.into} onPick={type => add(type, adding.into)} onClose={() => setAdding(null)} />}
      {settings && <SettingsDialog w={settings} layout={layout} onSave={w => { update(w.id, () => w); setSettings(null) }} onClose={() => setSettings(null)}
        onAddToStack={() => { setSettings(null); setAdding({ into: settings.id }) }} onRemoveFromStack={id => update(settings.id, st => ({ ...st, stack: (st.stack ?? []).filter(x => x.id !== id) }))} />}
      {bgDialog && <BackgroundDialog bg={layout.background} onSave={async (b, file) => {
        try {
          if (file) { setLayout(await dashboardApi.uploadBackground(file, device)); setTick(x => x + 1) }
          // Gleich speichern, auch mitten im Anpassen - ohne die noch nicht gespeicherten Änderungen mitzunehmen
          const saved = await dashboardApi.save({ ...(await dashboardApi.layout(device)), background: b }, device)
          setLayout(l => l ? { ...l, background: saved.background } : saved)
          if (b.kind !== 'upload' && layout.background.kind === 'upload') await dashboardApi.removeBackground(device)
          notify(t('Gespeichert'))
        } catch (e) { fail(e) }
        setBgDialog(false)
      }} onClose={() => setBgDialog(false)} />}
    </div>
  )
}

// ---------------- Widget hinzufügen ----------------

function AddDialog({ admin, into, onPick, onClose }: { admin: boolean; into?: string; onPick: (t: WidgetType) => void; onClose: () => void }) {
  const s = useStyles()
  const [query, setQuery] = useState('')
  // Hauptschalter: Aufgaben, Notizen, Fotos nur mit ihrem Bereich
  const released = (x: WidgetType) => x === 'tasks' || x === 'shopping' ? featureOn('tasks') : x === 'notes' ? featureOn('notes') : x === 'photos' ? featureOn('gallery') : true
  const types = (Object.keys(WIDGET_INFO) as WidgetType[]).filter(x => (x !== 'system' || admin) && (x !== 'stack' || !into) && released(x))
    .filter(x => !query || WIDGET_INFO[x].label().toLowerCase().includes(query.toLowerCase()) || WIDGET_INFO[x].hint().toLowerCase().includes(query.toLowerCase()))
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{into ? t('In den Stapel') : t('Widget hinzufügen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <Input value={query} onChange={(_, d) => setQuery(d.value)} placeholder={t('Widgets suchen')} autoFocus />
            <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(160px, 1fr))', gap: 8 }}>
              {types.map(x => <Button key={x} className={s.tile} appearance="secondary" onClick={() => onPick(x)}><Text weight="semibold" size={300}>{WIDGET_INFO[x].label()}</Text><Text size={200} className={s.mut}>{WIDGET_INFO[x].hint()}</Text></Button>)}
            </div>
          </DialogContent>
          <DialogActions><Button onClick={onClose}>{t('Schließen')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

// ---------------- Einstellungen je Widget ----------------

type Setting =
  | { kind: 'multi'; label: string; from: 'calendars' | 'lists' | 'plans' | 'accounts'; source: WidgetType }
  | { kind: 'one'; label: string; from: 'plans' | 'lists'; source: WidgetType; none?: string }
  | { kind: 'choice'; label: string; options: [string, string][]; def?: string; vertical?: boolean }
  | { kind: 'bool'; label: string }
  | { kind: 'int'; label: string; min: number; max: number }
  | { kind: 'text'; label: string; hint?: string }
  | { kind: 'mailMode'; label: string }
  | { kind: 'folder'; label: string }
const days: [string, string][] = [['1', '1 Tag'], ['3', '3 Tage'], ['7', '7 Tage'], ['14', '14 Tage']]
const SETTINGS: Partial<Record<WidgetType, Record<string, Setting>>> = {
  events: { calendars: { kind: 'multi', label: 'Welche Kalender', from: 'calendars', source: 'events' }, days: { kind: 'choice', label: 'Zeitraum', options: days }, invites: { kind: 'bool', label: 'Einladungen zeigen' } },
  meals: { calendar: { kind: 'one', label: 'Welcher Essensplan', from: 'plans', source: 'meals' }, range: { kind: 'choice', label: 'Zeitraum', options: [['today', 'Heute'], ['week', '7 Tage']] } },
  tasks: { lists: { kind: 'multi', label: 'Welche Listen', from: 'lists', source: 'tasks' }, days: { kind: 'choice', label: 'Zeitraum', options: days }, assigned: { kind: 'bool', label: 'Nur mir zugewiesene' }, mail: { kind: 'bool', label: 'Fällige Wiedervorlage-Mails dazu' } },
  shopping: { list: { kind: 'one', label: 'Welche Liste', from: 'lists', source: 'shopping', none: 'Die erste Einkaufsliste' } },
  mail: { accounts: { kind: 'multi', label: 'Konten', from: 'accounts', source: 'mail' }, mode: { kind: 'mailMode', label: 'Was zeigen' }, count: { kind: 'int', label: 'Anzahl', min: 1, max: 20 } },
  chat: { count: { kind: 'int', label: 'Anzahl', min: 1, max: 20 } },
  news: { count: { kind: 'int', label: 'Anzahl', min: 1, max: 15 }, text: { kind: 'choice', label: 'Schriftgröße', def: 'm', options: [['s', 'Klein'], ['m', 'Mittel'], ['l', 'Groß']] } },
  notes: { favorites: { kind: 'bool', label: 'Nur Favoriten' }, count: { kind: 'int', label: 'Anzahl', min: 1, max: 20 } },
  files: { favorites: { kind: 'bool', label: 'Nur Favoriten' }, count: { kind: 'int', label: 'Anzahl', min: 1, max: 20 }, folder: { kind: 'folder', label: 'Zielordner' } },
  birthdays: { days: { kind: 'choice', label: 'Zeitraum', options: [['7', '7 Tage'], ['14', '14 Tage'], ['30', '30 Tage'], ['90', '90 Tage']] } },
  photos: { every: { kind: 'choice', label: 'Bild wechseln', def: '7', vertical: true, options: [['7', 'alle 7 Sekunden'], ['30', 'alle 30 Sekunden'], ['60', 'jede Minute'], ['300', 'alle 5 Minuten'], ['0', 'gar nicht']] } },
  countdown: { title: { kind: 'text', label: 'Wort im Titel des Termins', hint: 'Leer: der nächste mehrtägige Termin' } },
}

function SettingsDialog({ w, layout, onSave, onClose, onAddToStack, onRemoveFromStack }: {
  w: Widget; layout: Layout; onSave: (w: Widget) => void; onClose: () => void; onAddToStack: () => void; onRemoveFromStack: (id: string) => void
}) {
  const { fail } = useApp()
  const [draft, setDraft] = useState<Widget>({ ...w, opts: { ...(w.opts ?? {}) } })
  const [lists, setLists] = useState<Record<string, { id: number | string; name: string }[]>>({})
  const [cats, setCats] = useState<{ key: string; name: string }[]>([])
  const defs = SETTINGS[w.type] ?? {}
  const live = layout.widgets.find(x => x.id === w.id) ?? w
  useEffect(() => {
    const need = new Set(Object.values(defs).flatMap(d => (d.kind === 'multi' || d.kind === 'one') ? [d.source + ':' + d.from] : []))
    for (const key of need) {
      const [source, from] = key.split(':')
      dashboardApi.data<any>(source as WidgetType, {}).then(d => setLists(l => ({ ...l, [from]: (d[from] ?? []) as { id: number | string; name: string }[] }))).catch(fail)
    }
    if (w.type === 'mail') api.get<{ key: string; name: string }[]>('/api/settings/categories').then(setCats).catch(() => {})
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const o = draft.opts ?? {}
  const set = (k: string, v: unknown) => setDraft(d => ({ ...d, opts: { ...(d.opts ?? {}), [k]: v } }))
  const field = (k: string, d: Setting) => {
    switch (d.kind) {
      case 'multi': {
        const sel = (o[k] as (number | string)[] | undefined) ?? []
        const all = lists[d.from] ?? []
        // Kein Field drumherum: das gäbe allen Kästchen dieselbe id, ein Klick träfe dann immer „Alle“
        return <div key={k} role="group" aria-label={t(d.label)} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}><Text weight="semibold" size={300}>{t(d.label)}</Text><div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
          <Checkbox label={t('Alle')} checked={sel.length === 0} onChange={() => set(k, [])} />
          {all.map(x => <Checkbox key={String(x.id)} label={x.name} checked={sel.includes(x.id)} onChange={(_, c) => set(k, c.checked ? [...sel, x.id] : sel.filter(y => y !== x.id))} />)}
        </div></div>
      }
      case 'one': {
        const all = lists[d.from] ?? []
        return <Field key={k} label={t(d.label)}><Select value={String(o[k] ?? '')} onChange={(_, e) => set(k, e.value ? Number(e.value) : undefined)}><option value="">{t(d.none ?? 'Alle')}</option>{all.map(x => <option key={String(x.id)} value={String(x.id)}>{x.name}</option>)}</Select></Field>
      }
      case 'choice': return <Field key={k} label={t(d.label)}><RadioGroup layout={d.vertical ? 'vertical' : 'horizontal'} value={String(o[k] ?? d.def ?? d.options[1]?.[0] ?? d.options[0][0])} onChange={(_, e) => set(k, /^\d+$/.test(e.value) ? Number(e.value) : e.value)}>{d.options.map(([v, l]) => <Radio key={v} value={v} label={t(l)} />)}</RadioGroup></Field>
      case 'bool': return <Checkbox key={k} label={t(d.label)} checked={o[k] === undefined ? k === 'invites' || k === 'mail' : !!o[k]} onChange={(_, c) => set(k, c.checked)} />
      case 'int': return <Field key={k} label={t(d.label)}><Input type="number" min={d.min} max={d.max} value={String(o[k] ?? '')} onChange={(_, e) => set(k, e.value ? Number(e.value) : undefined)} style={{ width: 100 }} /></Field>
      case 'text': return <Field key={k} label={t(d.label)} hint={d.hint ? t(d.hint) : undefined}><Input value={String(o[k] ?? '')} onChange={(_, e) => set(k, e.value)} /></Field>
      case 'folder': return <Field key={k} label={t(d.label)}><Input value={String(o[k] ?? '')} onChange={(_, e) => set(k, e.value)} placeholder={t('Dateien')} /></Field>
      case 'mailMode': {
        const v = String(o[k] ?? 'all')
        return <Field key={k} label={t(d.label)}><Select value={v} onChange={(_, e) => set(k, e.value)}>
          <option value="all">{t('Alle ungelesenen')}</option><option value="focused">{t('Nur Relevante')}</option>
          {cats.map(c => <option key={c.key} value={'category:' + c.key}>{t('Kategorie')}: {c.name}</option>)}
        </Select></Field>
      }
    }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>{t('Einstellungen')} – {WIDGET_INFO[w.type]?.label()}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Titel des Widgets')}><Input value={draft.title ?? ''} onChange={(_, e) => setDraft(d => ({ ...d, title: e.value || null }))} placeholder={WIDGET_INFO[w.type]?.label()} /></Field>
            {Object.entries(defs).map(([k, d]) => field(k, d))}
            {w.type === 'stack' && <>
              <Checkbox label={t('Je nach Tageszeit das passende vorn')} checked={!!draft.smart} onChange={(_, c) => setDraft(d => ({ ...d, smart: !!c.checked }))} />
              <Text weight="semibold" size={200}>{t('Widgets im Stapel')}</Text>
              {(live.stack ?? []).length === 0 && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Noch leer – Widgets hineinlegen')}</Text>}
              {(live.stack ?? []).map(x => <div key={x.id} style={{ display: 'flex', alignItems: 'center', gap: 8 }}><Text style={{ flex: 1 }}>{x.title || WIDGET_INFO[x.type]?.label()}</Text><Button size="small" appearance="subtle" onClick={() => onRemoveFromStack(x.id)}>{t('Aus dem Stapel')}</Button></div>)}
              <div><Button icon={<Add20Regular />} onClick={onAddToStack}>{t('In den Stapel')}</Button></div>
            </>}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={() => onSave({ ...draft, stack: live.stack })}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

// ---------------- Hintergrund ----------------

function BackgroundDialog({ bg, onSave, onClose }: { bg: Background; onSave: (b: Background, file?: File) => void; onClose: () => void }) {
  const s = useStyles()
  const [draft, setDraft] = useState<Background>({ ...bg })
  const [file, setFile] = useState<File | null>(null)
  const [albums, setAlbums] = useState<AlbumInfo[]>([])
  const [pick, setPick] = useState(false)
  useEffect(() => { albumsApi.list().then(setAlbums).catch(() => {}) }, [])
  const input = useRef<HTMLInputElement>(null)
  return (<>
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>{t('Hintergrund')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <RadioGroup value={draft.kind} onChange={(_, d) => setDraft(x => ({ ...x, kind: d.value as Background['kind'] }))}>
              <Radio value="none" label={t('Kein Hintergrund')} />
              <Radio value="gallery" label={t('Foto aus der Galerie')} />
              <Radio value="upload" label={t('Bild hochladen')} />
              <Radio value="album" label={t('Jeden Tag ein Foto aus einem Album')} />
            </RadioGroup>
            {draft.kind === 'gallery' && <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}><Button onClick={() => setPick(true)}>{t('Aus der Galerie wählen')}</Button><Text size={200} className={s.mut} style={{ overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{draft.path}</Text></div>}
            {draft.kind === 'upload' && <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
              <Button onClick={() => input.current?.click()}>{t('Bild hochladen')}</Button><Text size={200} className={s.mut}>{file?.name}</Text>
              <input ref={input} type="file" accept="image/*" hidden onChange={e => setFile(e.target.files?.[0] ?? null)} />
            </div>}
            {draft.kind === 'album' && <Field label={t('Album')}><Select value={String(draft.album ?? '')} onChange={(_, d) => setDraft(x => ({ ...x, album: d.value ? Number(d.value) : null }))}><option value="">–</option>{albums.map(a => <option key={a.id} value={a.id}>{a.name}</option>)}</Select></Field>}
            {draft.kind !== 'none' && <Field label={t('Abdunkeln') + ` (${draft.dim} %)`}><Slider min={0} max={80} step={5} value={draft.dim} onChange={(_, d) => setDraft(x => ({ ...x, dim: d.value }))} /></Field>}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={() => onSave(draft, draft.kind === 'upload' && file ? file : undefined)}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
        {pick && <PickPhotoDialog title={t('Hintergrundbild wählen')} onPick={p => { setDraft(x => ({ ...x, path: p })); setPick(false) }} onClose={() => setPick(false)} />}
      </DialogSurface>
    </Dialog>
  </>)
}
