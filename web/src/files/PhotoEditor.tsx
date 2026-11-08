import { Button, Slider, Spinner, Text, ToggleButton, Tooltip } from '@fluentui/react-components'
import { ArrowReset20Regular, ArrowRotateClockwise20Regular, ArrowRotateCounterclockwise20Regular, ArrowUndo16Regular, ChevronDown16Regular, ChevronRight16Regular, Copy20Regular, Save20Regular, Sparkle20Regular, Wand20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { t } from '../i18n'
import { ColorMixer, bandWeights, hueOf, mixKey, type MixPart } from './ColorMixer'

/** Ausschnitt in Anteilen (0..1) des gezeigten, schon gedrehten Bildes. */
export interface CropRect { x: number; y: number; w: number; h: number }

/** Reglerwerte (0 = unverändert); Belichtung in Blenden. */
export interface Adjust {
  exposure: number; contrast: number; highlights: number; shadows: number; whites: number; blacks: number; temperature: number; tint: number; vibrance: number; saturation: number; dehaze: number; clarity: number
  /** Farbmischer: hsl_h_red, hsl_s_blue … (−100 … +100) */
  [mixer: `hsl_${string}`]: number
}
const NONE: Adjust = { exposure: 0, contrast: 0, highlights: 0, shadows: 0, whites: 0, blacks: 0, temperature: 0, tint: 0, vibrance: 0, saturation: 0, dehaze: 0, clarity: 0 }

/** Gespeicherte Entwicklungseinstellungen eines Fotos (nicht-destruktiv, das Original bleibt). */
export interface PhotoSettings { rotate: number; crop: CropRect | null; adjust: Adjust | null; origin?: string }
type Slide = [keyof Adjust, string, number, number]
// Regler in Gruppen wie in Lightroom: Weißabgleich, Tonwert, Präsenz
const WHITE: Slide[] = [['temperature', 'Temperatur', -50, 50], ['tint', 'Tönung', -50, 50]]
const TONE: Slide[] = [['exposure', 'Belichtung', -2, 2], ['contrast', 'Kontrast', -60, 60], ['highlights', 'Lichter', -60, 60], ['shadows', 'Tiefen', -60, 60],
  ['whites', 'Weiß', -50, 50], ['blacks', 'Schwarz', -50, 50]]
// Klarheit und Dunst entfernen rechnet die Photo-Engine (−100 … +100 wie Lightroom); ohne sie wirkt Dunst grob, Klarheit nicht
const PRESENCE: Slide[] = [['clarity', 'Klarheit', -100, 100], ['dehaze', 'Dunst entfernen', -100, 100], ['vibrance', 'Dynamik', -50, 50], ['saturation', 'Sättigung', -50, 50]]
type Section = 'crop' | 'auto' | 'white' | 'tone' | 'presence' | 'mixer'
// Aufgeklappte Bereiche bleiben gemerkt
const OPEN_KEY = 'dmoffice.photoEditor.open'
const openAtStart = (): Set<Section> => { try { const v = localStorage.getItem(OPEN_KEY); if (v) return new Set(JSON.parse(v)) } catch { /* egal */ } return new Set<Section>(['auto', 'white', 'tone', 'presence']) }
const neutral = (a: Adjust) => Object.values(a).every(v => Math.abs(v) < 0.005)

/** RAW-Dateien (entwickelt die Photo-Engine; gespeichert wird als neues JPG daneben). */
export const RAW = /\.(raf|arw|srf|sr2|cr2|cr3|crw|nef|nrw|dng|orf|rw2|raw|pef|srw|3fr|iiq|x3f)$/i

/** Was der Server zum Bearbeiten kann (einmal gefragt): KI-Vorschlag, Photo-Engine, RAW. */
type Caps = { available: boolean; engine?: boolean; raw?: boolean }
let capsOnce: Promise<Caps> | null = null
export const photoCaps = () => (capsOnce ??= api.get<Caps>('/api/files/photo-adjust/ai').catch((): Caps => ({ available: false })))

const FULL: CropRect = { x: 0, y: 0, w: 1, h: 1 }
// 0: wie das ganze Foto („Original“), -1: Hochkant 3:4
const RATIOS: [string, number | null][] = [['Frei', null], ['Original', 0], ['1:1', 1], ['4:3', 4 / 3], ['3:2', 3 / 2], ['16:9', 16 / 9], ['Hochkant', -1]]

// Vorschlag-Knöpfe auf dunklem Grund: deutlich als bedienbar (die Standardknöpfe wirken dort wie gesperrt)
const ACTION: React.CSSProperties = { justifyContent: 'flex-start', color: '#fff', backgroundColor: 'rgba(255,255,255,.1)', border: '1px solid rgba(255,255,255,.45)' }

/**
 * Foto bearbeiten im Bildbetrachter: links das Bild (große Vorschau, schon aufrecht, gedreht auf einer Leinwand), rechts eine Leiste
 * mit aufklappbaren Bereichen wie in Lightroom - Zuschneiden, Automatisch (KI, Engine), Weißabgleich, Tonwert, Präsenz -, unten
 * fest Zurücksetzen, Abbrechen und Speichern. Ist „Zuschneiden“ aufgeklappt, zeigt das Bild Ausschnitt mit Ecken und Gitter, sonst
 * nur den Ausschnitt. Speichern rechnet der Server aus dem Original (Metadaten bleiben) - als Kopie oder überschreibend.
 */
export function PhotoEditor({ src, path, onApply, onExport, onCancel }: {
  /** src: Vorschau des Originals (orig=1) - der Editor arbeitet immer darauf */
  src: string; path: string
  /** Fertig: Einstellungen merken (leer = zurück zum Original) */
  onApply: (s: PhotoSettings | null) => Promise<void>
  /** Als JPG exportieren: eine neue Datei mit der Bearbeitung („…_bearbeitet.jpg“) */
  onExport: (s: PhotoSettings) => Promise<void>; onCancel: () => void
}) {
  // Regler: die Vorschau rechnet der Server (wie beim Speichern), kurz nach dem letzten Schieben
  const [adjust, setAdjust] = useState<Adjust>(NONE)
  const [shown, setShown] = useState(src)
  const [ai, setAi] = useState(false)
  const [engine, setEngine] = useState(false)
  // Welcher Vorschlag gerade läuft - nur der zeigt den Kringel, gesperrt sind beide
  const [asking, setAsking] = useState<'suggest' | 'engine' | null>(null)
  const [analysis, setAnalysis] = useState<string | null>(null)
  // Letzter Vorschlag und wie stark er gilt (Prozent; alle vorgeschlagenen Regler gleichmäßig)
  // Farbmischer: Reiter und Zielwerkzeug
  const [mixPart, setMixPart] = useState<MixPart>('h')
  const [target, setTarget] = useState(false)
  const [suggested, setSuggested] = useState<Partial<Adjust> | null>(null)
  const [strength, setStrength] = useState(100)
  const scaled = (v: Partial<Adjust>, pct: number) =>
    Object.fromEntries(Object.entries(v).filter(([, x]) => typeof x === 'number').map(([k, x]) => [k, Math.round((x as number) * pct) / 100])) as Partial<Adjust>
  const applyStrength = (pct: number) => { setStrength(pct); if (suggested) setAdjust(a => ({ ...a, ...scaled(suggested, pct) }) as Adjust) }
  // Im Tresor (Pfad „tresor:…“) gibt es keinen Vorschlag der KI - sie hat dort grundsätzlich keinen Zugriff
  useEffect(() => { photoCaps().then(r => { setAi(r.available && !path.startsWith('tresor:')); setEngine(!!r.engine) }) }, [path])
  // Das Original schon holen (Galerie-Ordner über den Tunnel) - Vorschläge und Speichern warten dann nicht darauf
  useEffect(() => { api.post('/api/files/photo-edit/prefetch', { path }).catch(() => { }) }, [path])
  const raw = RAW.test(path)
  // Während der Server rechnet: kleiner Kringel im Bild
  const [pending, setPending] = useState(false)
  useEffect(() => {
    if (neutral(adjust) && !raw) { setShown(src); setPending(false); return }
    let url: string | null = null
    // Überholte Anfragen abbrechen - sonst kann eine spät eintreffende ältere Vorschau die neuere überdecken
    const ctl = new AbortController()
    setPending(true)
    const timer = setTimeout(async () => {
      try {
        const r = await fetch('/api/files/photo-adjust/preview', { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ path, adjust }), signal: ctl.signal })
        if (r.ok) { const b = await r.blob(); if (!ctl.signal.aborted) { url = URL.createObjectURL(b); setShown(url) } }
      } catch { /* Vorschau bleibt */ }
      finally { if (!ctl.signal.aborted) setPending(false) }
    }, 250)
    return () => { clearTimeout(timer); ctl.abort(); if (url) URL.revokeObjectURL(url) }
  }, [adjust, path, src, raw])
  // Vorschlag von der KI (Claude) oder der Photo-Engine (ohne KI) - beide belegen nur die Regler vor
  const suggest = async (from: 'suggest' | 'engine') => {
    setAsking(from)
    try { const r = await api.post<{ values: Adjust; analysis: string | null }>('/api/files/photo-adjust/' + from, { path })
      setSuggested(r.values); setAdjust({ ...NONE, ...scaled(r.values, strength) } as Adjust); setAnalysis(r.analysis) }
    catch (e) { setAnalysis(e instanceof Error ? e.message : String(e)) }
    finally { setAsking(null) }
  }
  const [img, setImg] = useState<HTMLImageElement | null>(null)
  // Vergleich mit dem Original: Alt gedrückt halten, ins Bild drücken oder über „Zurücksetzen“ fahren
  const [orig, setOrig] = useState<HTMLImageElement | null>(null)
  const [peek, setPeek] = useState(false)
  useEffect(() => { const i = new Image(); i.onload = () => setOrig(i); i.src = src }, [src])
  useEffect(() => {
    const down = (e: KeyboardEvent) => { if (e.key === 'Alt') { e.preventDefault(); setPeek(true) } }
    const up = (e: KeyboardEvent) => { if (e.key === 'Alt') { e.preventDefault(); setPeek(false) } }
    const off = () => setPeek(false)
    window.addEventListener('keydown', down); window.addEventListener('keyup', up); window.addEventListener('blur', off)
    return () => { window.removeEventListener('keydown', down); window.removeEventListener('keyup', up); window.removeEventListener('blur', off) }
  }, [])
  const drawn = peek && orig ? orig : img
  const [rotate, setRotate] = useState(0)
  const [crop, setCrop] = useState<CropRect>({ x: 0, y: 0, w: 1, h: 1 })
  const [ratio, setRatio] = useState<number | null>(null)
  // Welcher Speichern-Knopf gerade läuft - nur der zeigt den Kringel, gesperrt sind beide
  const [saving, setSaving] = useState<'apply' | 'export' | 'remove' | null>(null)
  // Schon bearbeitet: die gemerkten Einstellungen laden
  const [hadEdit, setHadEdit] = useState(false)
  useEffect(() => {
    api.get<{ edit: PhotoSettings | null }>('/api/files/photo-edits?path=' + encodeURIComponent(path)).then(r => {
      if (!r.edit) return
      setHadEdit(true); setRotate(r.edit.rotate ?? 0); setCrop(r.edit.crop ?? FULL); setAdjust({ ...NONE, ...(r.edit.adjust ?? {}) })
    }).catch(() => { })
  }, [path])
  const busy = saving != null
  const [open, setOpen] = useState<Set<Section>>(openAtStart)
  const toggle = (k: Section) => setOpen(o => { const n = new Set(o); if (n.has(k)) n.delete(k); else n.add(k); try { localStorage.setItem(OPEN_KEY, JSON.stringify([...n])) } catch { /* egal */ } return n })
  const mode: 'crop' | 'color' = open.has('crop') ? 'crop' : 'color'
  // Im Farbmodus nur der Ausschnitt; beim Zuschneiden das ganze Bild
  const view = mode === 'color' ? crop : FULL
  const canvas = useRef<HTMLCanvasElement>(null)
  const box = useRef<HTMLDivElement>(null)
  const [size, setSize] = useState({ w: 0, h: 0 })
  useEffect(() => { const i = new Image(); i.onload = () => setImg(i); i.src = shown }, [shown])
  // Verfügbare Fläche - ändert sich mit dem Fenster, dann neu einpassen
  const [area, setArea] = useState({ w: 0, h: 0 })
  useEffect(() => {
    const el = box.current
    if (!el) return
    const ro = new ResizeObserver(([e]) => setArea({ w: e.contentRect.width, h: e.contentRect.height }))
    ro.observe(el)
    return () => ro.disconnect()
  }, [])

  // Gedrehtes Bild (bzw. im Farbmodus den Ausschnitt) in die verfügbare Fläche einpassen
  useEffect(() => {
    if (!drawn || !canvas.current || area.w <= 32 || area.h <= 32) return
    const swap = rotate % 180 !== 0
    const iw = swap ? drawn.height : drawn.width, ih = swap ? drawn.width : drawn.height
    // Füllt die Fläche - auch vergrößert (Ausschnitt im Farbmodus, Vorschau der Regler mit 1400 px)
    const f = Math.min((area.w - 32) / (iw * view.w), (area.h - 32) / (ih * view.h))
    const W = iw * f, H = ih * f
    const w = Math.round(W * view.w), h = Math.round(H * view.h)
    // In Gerätepixeln zeichnen - bei skalierter Anzeige sonst unscharf
    const dpr = window.devicePixelRatio || 1
    const c = canvas.current
    c.width = Math.round(w * dpr); c.height = Math.round(h * dpr); c.style.width = w + 'px'; c.style.height = h + 'px'
    const g = c.getContext('2d')!
    g.setTransform(dpr, 0, 0, dpr, 0, 0); g.imageSmoothingQuality = 'high'
    g.translate(-view.x * W, -view.y * H); g.translate(W / 2, H / 2); g.rotate(rotate * Math.PI / 180)
    const dw = swap ? H : W, dh = swap ? W : H
    g.drawImage(drawn, -dw / 2, -dh / 2, dw, dh)
    setSize({ w, h })
  }, [drawn, rotate, view, area])

  // Seitenverhältnis: größter Ausschnitt mittig; a = Breite/Höhe des gedrehten Bildes
  const fitRatio = (r: number | null, a: number): CropRect => {
    if (r == null) return FULL
    const want = r === -1 ? 3 / 4 : r === 0 ? a : r
    return a > want ? { w: want / a, h: 1, x: (1 - want / a) / 2, y: 0 } : { w: 1, h: a / want, x: 0, y: (1 - a / want) / 2 }
  }
  const aspect = (rot: number) => !img ? 1 : rot % 180 ? img.height / img.width : img.width / img.height
  const applyRatio = (r: number | null) => { setRatio(r); if (r != null) setCrop(fitRatio(r, aspect(rotate))) }
  // Drehen: Ausschnitt neu, ein gewähltes Seitenverhältnis bleibt
  const turn = (d: number) => { const n = (rotate + d + 360) % 360; setRotate(n); setCrop(fitRatio(ratio, aspect(n))) }

  // Ziehen: Ecke (nw, ne, sw, se) oder ganze Fläche (move); bei festem Verhältnis bleibt es erhalten
  const drag = (e: React.PointerEvent, mode: string) => {
    e.preventDefault(); e.stopPropagation()
    const start = { x: e.clientX, y: e.clientY, c: crop }
    // Verhältnis in Anteilen (Breite/Höhe); aus den Bildmaßen, nicht den gerundeten Pixeln der Leinwand
    const r = ratio == null ? null : (ratio === -1 ? 3 / 4 : ratio === 0 ? aspect(rotate) : ratio) / aspect(rotate)
    const move = (ev: PointerEvent) => {
      const dx = (ev.clientX - start.x) / size.w, dy = (ev.clientY - start.y) / size.h
      let { x, y, w, h } = start.c
      if (mode === 'move') { x = Math.min(1 - w, Math.max(0, x + dx)); y = Math.min(1 - h, Math.max(0, y + dy)) }
      else {
        // Die gegenüberliegende Ecke bleibt stehen; am Bildrand wird begrenzt statt angehalten
        const left = mode.includes('w'), top = mode.includes('n')
        const ax = left ? x + w : x, ay = top ? y + h : y
        const maxW = left ? ax : 1 - ax, maxH = top ? ay : 1 - ay
        let nw = left ? w - dx : w + dx, nh = top ? h - dy : h + dy
        if (r != null) {
          // Festes Verhältnis: beide Richtungen zählen (Mittel), dann so groß, wie es ins Bild passt
          nw = Math.min(Math.max(0.05, (nw + nh * r) / 2), maxW, maxH * r); nh = nw / r
        } else { nw = Math.min(Math.max(0.05, nw), maxW); nh = Math.min(Math.max(0.05, nh), maxH) }
        w = nw; h = nh; x = left ? ax - nw : ax; y = top ? ay - nh : ay
      }
      setCrop({ x, y, w, h })
    }
    const up = () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up) }
    window.addEventListener('pointermove', move); window.addEventListener('pointerup', up)
  }
  // Zielwerkzeug: Farbe unter dem Zeiger (Mittel über 5 × 5 Pixel), dann hoch = mehr, runter = weniger - anteilig auf die Bänder
  const targetDrag = (e: React.PointerEvent<HTMLCanvasElement>) => {
    e.preventDefault()
    const c = e.currentTarget, rect = c.getBoundingClientRect()
    const g = c.getContext('2d', { willReadFrequently: true })
    if (!g) return
    const dpr = c.width / rect.width
    const px = Math.round((e.clientX - rect.left) * dpr), py = Math.round((e.clientY - rect.top) * dpr)
    const d = g.getImageData(Math.max(0, px - 2), Math.max(0, py - 2), 5, 5).data
    let r = 0, gg = 0, b = 0, n = 0
    for (let i = 0; i < d.length; i += 4) { r += d[i]; gg += d[i + 1]; b += d[i + 2]; n++ }
    const [hue, sat] = hueOf(r / n, gg / n, b / n)
    if (sat < 0.05) return
    const w = bandWeights(hue), part = mixPart, startY = e.clientY
    const base = Object.fromEntries(Object.keys(w).map(k => [k, (adjust[mixKey(part, k) as `hsl_${string}`] ?? 0)]))
    const move = (ev: PointerEvent) => {
      const delta = (startY - ev.clientY) * 0.6
      setAdjust(a => {
        const n = { ...a }
        for (const [band, x] of Object.entries(w)) if (x > 0) n[mixKey(part, band) as `hsl_${string}`] = Math.round(Math.max(-100, Math.min(100, base[band] + delta * x)))
        return n
      })
    }
    const up = () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up) }
    window.addEventListener('pointermove', move); window.addEventListener('pointerup', up)
  }
  const mixValues = Object.fromEntries(Object.entries(adjust).filter(([k]) => k.startsWith('hsl_'))) as Record<string, number>
  const mixChanged = Object.values(mixValues).some(v => Math.abs(v) >= 0.5)
  const full = crop.x < 0.001 && crop.y < 0.001 && crop.w > 0.999 && crop.h > 0.999
  const settings = (): PhotoSettings => ({ rotate, crop: full ? null : crop, adjust: neutral(adjust) ? null : adjust })
  const apply = async () => { setSaving('apply'); try { await onApply(unchanged ? null : settings()) } finally { setSaving(null) } }
  const exportJpg = async () => { setSaving('export'); try { await onExport(settings()) } finally { setSaving(null) } }
  const remove = async () => { setSaving('remove'); try { await onApply(null) } finally { setSaving(null) } }
  const unchanged = full && rotate === 0 && neutral(adjust)
  // Zurücksetzen je Modus und alles zusammen
  const cropChanged = !full || rotate !== 0 || ratio != null
  const colorChanged = !neutral(adjust) || !!analysis
  const resetCrop = () => { setRotate(0); setCrop(FULL); setRatio(null) }
  const resetColor = () => { setPeek(false); setAdjust(NONE); setAnalysis(null) }
  const handle = (pos: string, style: React.CSSProperties) => <div onPointerDown={e => drag(e, pos)}
    style={{ position: 'absolute', width: 18, height: 18, background: '#fff', borderRadius: 4, boxShadow: '0 0 3px rgba(0,0,0,.7)', cursor: `${pos}-resize`, ...style }} />
  return (
    <div style={{ flex: 1, minHeight: 0, display: 'flex', flexDirection: 'column' }}>
      <div style={{ flex: 1, minHeight: 0, display: 'flex' }}>
      <div ref={box} style={{ flex: 1, minWidth: 0, minHeight: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', position: 'relative', overflow: 'hidden' }}>
        {!img && <Spinner />}
        <div style={{ position: 'relative', width: size.w, height: size.h }}>
          <canvas ref={canvas} style={{ display: 'block', cursor: mode === 'color' ? (target ? 'crosshair' : 'pointer') : undefined, touchAction: target ? 'none' : undefined }}
            onPointerDown={e => { if (mode === 'color') { if (target) targetDrag(e); else setPeek(true) } }} onPointerUp={() => setPeek(false)} onPointerLeave={() => setPeek(false)} />
          {pending && !peek && <Spinner size="tiny" style={{ position: 'absolute', right: 8, top: 8 }} />}
          {peek && !neutral(adjust) && <span style={{ position: 'absolute', left: 8, top: 8, padding: '2px 8px', borderRadius: 6, background: 'rgba(0,0,0,.65)', color: '#fff', fontSize: 12 }}>{t('Original')}</span>}
          {mode === 'crop' && size.w > 0 && <div onPointerDown={e => drag(e, 'move')} style={{
            position: 'absolute', left: crop.x * size.w, top: crop.y * size.h, width: crop.w * size.w, height: crop.h * size.h, cursor: 'move',
            boxShadow: '0 0 0 9999px rgba(0,0,0,.55)', outline: '2px solid #fff',
            backgroundImage: 'linear-gradient(rgba(255,255,255,.35) 1px, transparent 1px), linear-gradient(90deg, rgba(255,255,255,.35) 1px, transparent 1px)',
            backgroundSize: '33.333% 33.333%',
          }}>
            {handle('nw', { left: -9, top: -9 })}{handle('ne', { right: -9, top: -9 })}{handle('sw', { left: -9, bottom: -9 })}{handle('se', { right: -9, bottom: -9 })}
          </div>}
        </div>
      </div>
      {/* Leiste rechts: aufklappbare Bereiche, unten fest Zurücksetzen, Abbrechen und Speichern */}
      <div style={{ width: 280, flexShrink: 0, display: 'flex', flexDirection: 'column', borderLeft: '1px solid rgba(255,255,255,.12)', minHeight: 0 }}>
        <div style={{ flex: 1, minHeight: 0, overflowY: 'auto' }}>
          <Part k="crop" label={t('Zuschneiden')} open={open} toggle={toggle} changed={cropChanged} onReset={resetCrop}>
            <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <Text size={200} style={{ opacity: 0.8, flex: 1 }}>{t('Drehen')}</Text>
              <Tooltip content={t('Nach links drehen')} relationship="label"><Button size="small" style={ACTION} icon={<ArrowRotateCounterclockwise20Regular />} onClick={() => turn(-90)} /></Tooltip>
              <Tooltip content={t('Nach rechts drehen')} relationship="label"><Button size="small" style={ACTION} icon={<ArrowRotateClockwise20Regular />} onClick={() => turn(90)} /></Tooltip>
            </div>
            <Text size={200} style={{ opacity: 0.8 }}>{t('Seitenverhältnis')}</Text>
            <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6 }}>
              {/* Gewähltes Verhältnis primär - der neutrale „gewählt“-Ton ist auf dunklem Grund kaum zu sehen */}
              {RATIOS.map(([l, r]) => <ToggleButton key={l} size="small" shape="circular" checked={ratio === r} appearance={ratio === r ? 'primary' : undefined}
                onClick={() => applyRatio(r)}>{l === 'Frei' || l === 'Hochkant' || l === 'Original' ? t(l) : l}</ToggleButton>)}
            </div>
          </Part>
          {(ai || engine) && <Part k="auto" label={t('Automatisch')} open={open} toggle={toggle}>
            <div style={{ display: 'flex', gap: 6 }}>
              {ai && <Button size="small" icon={asking === 'suggest' ? <Spinner size="tiny" /> : <Sparkle20Regular />} disabled={!!asking} onClick={() => suggest('suggest')} style={{ ...ACTION, flex: 1 }}>{t('KI')}</Button>}
              {engine && <Button size="small" icon={asking === 'engine' ? <Spinner size="tiny" /> : <Wand20Regular />} disabled={!!asking} onClick={() => suggest('engine')} style={{ ...ACTION, flex: 1 }}>{t('Engine')}</Button>}
            </div>
            {suggested && <div style={{ display: 'grid', gridTemplateColumns: '96px minmax(0, 1fr) 40px', alignItems: 'center', gap: 4, fontSize: 12 }}
              onDoubleClick={() => applyStrength(100)} title={t('Doppelklick: auf 100 %')}>
              <span style={{ opacity: 0.85 }}>{t('Stärke')}</span>
              <Slider size="small" min={0} max={150} step={5} value={strength} onChange={(_, d) => applyStrength(d.value)} />
              <span style={{ textAlign: 'right', opacity: 0.75 }}>{strength} %</span>
            </div>}
            {analysis && <Text size={200} style={{ opacity: 0.8 }}>{analysis}</Text>}
          </Part>}
          {([['white', t('Weißabgleich'), WHITE], ['tone', t('Tonwert'), TONE], ['presence', t('Präsenz'), PRESENCE]] as [Section, string, Slide[]][]).map(([k, label, list]) =>
            <Part key={k} k={k} label={label} open={open} toggle={toggle} changed={list.some(([x]) => Math.abs(adjust[x]) >= 0.005)}
              onReset={() => setAdjust(a => ({ ...a, ...Object.fromEntries(list.map(([x]) => [x, 0])) }))}>
              {/* Doppelklick auf Regler oder Wert: zurück auf 0 (Mitte) */}
              {list.map(([x, name, min, max]) => <div key={x} onDoubleClick={() => setAdjust(a => ({ ...a, [x]: 0 }))} title={t('Doppelklick: auf 0')}
                style={{ display: 'grid', gridTemplateColumns: '96px minmax(0, 1fr) 40px', alignItems: 'center', gap: 4, fontSize: 12 }}>
                <span style={{ opacity: 0.85 }}>{t(name)}</span>
                <Slider size="small" min={min} max={max} step={x === 'exposure' ? 0.05 : 1} value={adjust[x]} style={{ minWidth: 0 }}
                  onChange={(_, d) => setAdjust(a => ({ ...a, [x]: d.value }))} />
                <span style={{ textAlign: 'right', opacity: 0.8, fontVariantNumeric: 'tabular-nums' }}>
                  {x === 'exposure' ? (adjust[x] > 0 ? '+' : '') + adjust[x].toFixed(2) : (adjust[x] > 0 ? '+' : '') + Math.round(adjust[x])}</span>
              </div>)}
            </Part>)}
          <Part k="mixer" label={t('Farbmischer')} open={open} toggle={toggle} changed={mixChanged}
            onReset={() => { setTarget(false); setAdjust(a => Object.fromEntries(Object.entries(a).filter(([k]) => !k.startsWith('hsl_'))) as unknown as Adjust) }}>
            <ColorMixer values={mixValues} part={mixPart} onPart={p => setMixPart(p)} target={target} onTarget={setTarget}
              onChange={(k, v) => setAdjust(a => ({ ...a, [k]: v }))} />
          </Part>
          <Text size={100} block style={{ opacity: 0.6, padding: '8px 12px' }}>{t('Vergleich mit dem Original: Alt gedrückt halten oder ins Bild drücken.')}</Text>
        </div>
        <div style={{ borderTop: '1px solid rgba(255,255,255,.12)', padding: 10, display: 'flex', flexDirection: 'column', gap: 6 }}>
          <div style={{ display: 'flex', gap: 6 }}>
            <Button size="small" appearance="subtle" style={{ color: 'inherit', flex: 1 }} icon={<ArrowReset20Regular />} disabled={busy || (!cropChanged && !colorChanged)}
              onMouseEnter={() => setPeek(true)} onMouseLeave={() => setPeek(false)} onClick={() => { resetCrop(); resetColor() }}>{t('Zurücksetzen')}</Button>
            <Button size="small" appearance="subtle" style={{ color: 'inherit', flex: 1 }} onClick={onCancel} disabled={busy}>{t('Abbrechen')}</Button>
          </div>
          {/* Fertig merkt nur die Einstellungen - das Original bleibt; eine Datei gibt es mit „Als JPG exportieren“ */}
          <Button appearance="primary" icon={saving === 'apply' ? <Spinner size="tiny" /> : <Save20Regular />} disabled={busy || unchanged && !hadEdit} onClick={apply}>{t('Fertig')}</Button>
          <Button icon={saving === 'export' ? <Spinner size="tiny" /> : <Copy20Regular />} disabled={busy || unchanged} onClick={exportJpg}>{t('Als JPG exportieren')}</Button>
          {hadEdit && <Button size="small" appearance="subtle" style={{ color: 'inherit' }} icon={saving === 'remove' ? <Spinner size="tiny" /> : <ArrowReset20Regular />} disabled={busy}
            onClick={remove}>{t('Bearbeitung entfernen')}</Button>}
        </div>
      </div>
      </div>
    </div>
  )
}

/** Ein aufklappbarer Bereich der Leiste: Kopfzeile (antippen: auf/zu), bei Änderungen ein Zurücksetzen nur für diesen Bereich. */
function Part({ k, label, open, toggle, changed, onReset, children }: {
  k: Section; label: string; open: Set<Section>; toggle: (k: Section) => void; changed?: boolean; onReset?: () => void; children: React.ReactNode
}) {
  const on = open.has(k)
  return (
    <div style={{ borderBottom: '1px solid rgba(255,255,255,.08)' }}>
      <div role="button" tabIndex={0} aria-expanded={on} onClick={() => toggle(k)} onKeyDown={e => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); toggle(k) } }}
        style={{ display: 'flex', alignItems: 'center', gap: 6, padding: '7px 10px', cursor: 'pointer', fontWeight: 600, fontSize: 13, background: 'rgba(255,255,255,.05)', userSelect: 'none' }}>
        {on ? <ChevronDown16Regular /> : <ChevronRight16Regular />}
        <span style={{ flex: 1 }}>{label}</span>
        {changed && onReset && <Tooltip content={t('Zurücksetzen')} relationship="label">
          <Button size="small" appearance="transparent" style={{ color: 'inherit', minWidth: 0, padding: 2 }} icon={<ArrowUndo16Regular />} onClick={e => { e.stopPropagation(); onReset() }} />
        </Tooltip>}
      </div>
      {on && <div style={{ padding: '8px 12px 10px', display: 'flex', flexDirection: 'column', gap: 8 }}>{children}</div>}
    </div>
  )
}
