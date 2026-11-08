// Betrachter im Vollbild: Bilder als Lightbox (vor/zurueck mit Pfeilen, Tasten und Wischen), PDF eingebettet,
// Text und Code lesbar (und bearbeitbar), Markdown dargestellt, Audio und Video abspielbar.
import { Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Spinner, Text, Tooltip, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import {
  ArrowDownload20Regular, ChevronLeft24Regular, ChevronRight24Regular, Dismiss24Regular, Edit20Regular, Info20Regular, Open20Regular,
  Save20Regular, ZoomIn20Regular, ZoomOut20Regular,
  Chat20Regular,
  ImageEdit20Regular, Delete20Regular, Share20Regular, Star20Regular, Star20Filled, Filmstrip20Regular,
} from '@fluentui/react-icons'
import { MARK_LABELS, labelColor, labelName } from './PhotoFilter'
import { Adjust, CropRect, PhotoEditor, PhotoSettings, RAW, photoCaps } from './PhotoEditor'
import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react'
import { fmtSize } from '../api'
import { locale, t } from '../i18n'
import { Item, KindIcon, Source, extOf, fmtWhen, viewKind } from './common'
import { Markdown } from './Markdown'

const useStyles = makeStyles({
  root: {
    position: 'fixed', inset: 0, zIndex: 1000, display: 'flex', flexDirection: 'column', backgroundColor: '#141414', color: '#fff',
  },
  light: { backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground1 },
  bar: {
    display: 'flex', alignItems: 'center', gap: '4px', padding: '6px 8px', minHeight: '44px', flexShrink: 0,
    paddingTop: 'calc(6px + env(safe-area-inset-top, 0px))',
  },
  title: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', paddingLeft: '8px' },
  name: { fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sub: { fontSize: '12px', opacity: 0.75, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  btn: { color: 'inherit', minWidth: '36px' },
  // Leiste thematisch gruppiert: Markieren | Ansicht | Bearbeiten | Teilen | Datei | Details, Schließen
  sep: { width: '1px', alignSelf: 'stretch', margin: '8px 4px', backgroundColor: 'rgba(255,255,255,0.18)' },
  strip: { display: 'flex', gap: '4px', overflowX: 'auto', padding: '6px 8px', flexShrink: 0, backgroundColor: 'rgba(0,0,0,0.35)' },
  stripImg: { height: '64px', width: '64px', objectFit: 'cover', borderRadius: '4px', cursor: 'pointer', flexShrink: 0, outline: '2px solid transparent', outlineOffset: '-2px' },
  stripOn: { outline: '3px solid #F5A623' },
  side: {
    width: '340px', maxWidth: '45vw', flexShrink: 0, overflowY: 'auto', backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1,
    borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, '@media (max-width: 700px)': { position: 'absolute', right: 0, top: '44px', bottom: 0, maxWidth: '90vw', zIndex: 2 },
  },
  stage: { flex: 1, minWidth: 0, minHeight: 0, position: 'relative', display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden' },
  img: { maxWidth: '100%', maxHeight: '100%', objectFit: 'contain', userSelect: 'none', cursor: 'zoom-in' },
  zoomed: { overflow: 'auto', alignItems: 'flex-start', justifyContent: 'flex-start' },
  imgFull: { maxWidth: 'none', maxHeight: 'none', cursor: 'zoom-out', margin: 'auto' },
  nav: {
    position: 'absolute', top: '50%', transform: 'translateY(-50%)', zIndex: 2, width: '44px', height: '64px', borderRadius: '8px',
    backgroundColor: 'rgba(0,0,0,.35)', color: '#fff', border: 'none', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
    ':hover': { backgroundColor: 'rgba(0,0,0,.6)' },
    '@media (max-width: 767px)': { display: 'none' },
  },
  pages: { width: '100%', height: '100%', overflowY: 'auto', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px', padding: '12px', boxSizing: 'border-box' },
  page: { width: 'min(100%, 1000px)', height: 'auto', flexShrink: 0, minHeight: '200px', backgroundColor: '#fff', boxShadow: tokens.shadow8 },
  doc: {
    width: '100%', height: '100%', overflow: 'auto', padding: '16px 24px', boxSizing: 'border-box', backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1, '@media (max-width: 767px)': { padding: '12px' },
  },
  pre: { margin: 0, fontFamily: 'Consolas, "Cascadia Mono", monospace', fontSize: '13px', whiteSpace: 'pre-wrap', wordBreak: 'break-word', tabSize: 4 },
  editor: {
    width: '100%', height: '100%', border: 'none', outline: 'none', resize: 'none', padding: '16px 24px', boxSizing: 'border-box',
    fontFamily: 'Consolas, "Cascadia Mono", monospace', fontSize: '13px', backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1,
  },
  none: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px', textAlign: 'center', padding: '24px' },
  media: { maxWidth: '100%', maxHeight: '100%' },
})

/** Text bis zu dieser Groesse laden (Rest abgeschnitten), bearbeiten nur darunter. */
const TEXT_LIMIT = 2 * 1024 * 1024

export function Viewer({ items, index, source, onIndex, onClose, onSeen, onDownload, onDetails, onChat, onSave, startEdit, side, onPhotoEdit, onPhotoSettings, markOf, onMark, onShare, onFavorite, onDelete, groupOf }: {
  items: Item[]; index: number; source: Source; onIndex: (i: number) => void; onClose: () => void
  /** Eine Weile angezeigt - für „Zuletzt verwendet“ */
  onSeen?: (i: Item) => void
  /** Neue Textdatei: gleich im Bearbeiten-Modus */
  startEdit?: boolean
  /** Fehlt bei nur gezeigten Dateien (Version 2): kein Herunterladen */
  onDownload?: (i: Item) => void; onDetails?: (i: Item) => void
  /** Details als Leiste rechts neben dem Bild (statt zurück in die Liste); close blendet sie wieder aus */
  side?: (i: Item, close: () => void) => React.ReactNode
  /** Als JPG exportieren: neue Datei mit der Bearbeitung (der Server rechnet aus dem Original) */
  onPhotoEdit?: (i: Item, rotate: number, crop: CropRect | null, overwrite: boolean, adjust: Adjust | null) => Promise<void>
  /** Fertig: Entwicklungseinstellungen merken (null: Bearbeitung entfernen) - das Original bleibt */
  onPhotoSettings?: (i: Item, s: PhotoSettings | null) => Promise<void>
  /** Im Chat zeigen oder senden (nur Dateien aus der eigenen Ablage) */
  onChat?: (i: Item) => void
  /** Galerie: Markierung, Sterne, Farbe des Fotos und setzen (Tasten P/X/U, 0–5, 6–9 wie in der Galerie) */
  markOf?: (i: Item) => { pick?: number | null; rating?: number | null; label?: string | null } | undefined
  onMark?: (i: Item, body: { pick?: number; rating?: number; label?: string }) => void
  /** Teilen (Link, Personen), Favorit, Löschen */
  onShare?: (i: Item) => void
  onFavorite?: (i: Item) => void
  onDelete?: (i: Item) => void
  /** Gruppe in der Liste (etwa der Tag): der Filmstreifen zeigt nur die Gruppe des gezeigten Bilds */
  groupOf?: (i: Item) => string
  /** Text speichern (nur angemeldet); wirft bei Konflikt ApiError 409 */
  onSave?: (i: Item, text: string, force: boolean) => Promise<Item>
}) {
  const s = useStyles()
  const item = items[index]
  const kind = item ? viewKind(item) : null
  const [zoom, setZoom] = useState(false)
  // Details-Leiste rechts: offen oder zu bleibt gemerkt (in diesem Browser)
  const [showSide, setShowSideRaw] = useState(() => { try { return localStorage.getItem('dm.viewer.side') === '1' } catch { return false } })
  const setShowSide = (v: boolean | ((x: boolean) => boolean)) => setShowSideRaw(x => {
    const n = typeof v === 'function' ? v(x) : v
    try { localStorage.setItem('dm.viewer.side', n ? '1' : '0') } catch { /* ohne Speicher */ }
    return n
  })
  // Filmstreifen unten: gemerkt in diesem Browser
  const [strip, setStrip] = useState(() => { try { return localStorage.getItem('dm.viewer.strip') === '1' } catch { return false } })
  const toggleStrip = () => setStrip(v => { try { localStorage.setItem('dm.viewer.strip', v ? '0' : '1') } catch { /* ohne Speicher */ } return !v })
  const stripRef = useRef<HTMLDivElement>(null)
  useEffect(() => { stripRef.current?.querySelector<HTMLElement>('[data-on="1"]')?.scrollIntoView({ block: 'nearest', inline: 'center' }) }, [index, strip])
  // „Zuletzt verwendet“: was eine Weile im Betrachter steht (nicht beim schnellen Durchblättern, nicht das Vorladen der Nachbarn)
  useEffect(() => {
    const it = items[index]
    if (!it || !onSeen) return
    const h = setTimeout(() => onSeen(it), 2000)
    return () => clearTimeout(h)
  }, [items[index]?.path]) // eslint-disable-line react-hooks/exhaustive-deps
  const [cropping, setCropping] = useState(false)
  const [original, setOriginal] = useState(false)
  // Gedrückt halten: das unbearbeitete Original zeigen
  const [peek, setPeek] = useState(false)
  // RAW bearbeiten geht nur mit der Photo-Engine
  const [rawEdit, setRawEdit] = useState(false)
  useEffect(() => { if (onPhotoEdit) photoCaps().then(r => setRawEdit(!!r.raw)) }, [onPhotoEdit])
  const [imgFailed, setImgFailed] = useState(0)
  // Großes Bild geladen? Bis dahin das kleine Vorschaubild (meist schon aus dem Raster da) bildschirmfüllend mit Kringel
  const [loadedSrc, setLoadedSrc] = useState<string | null>(null)
  const [loadedPath, setLoadedPath] = useState<string | null>(null)
  const [text, setText] = useState<{ body: string; cut: boolean } | null>(null)
  const [edit, setEdit] = useState<string | null>(null)
  // Speichern liest das Feld selbst - der Zustand kann beim schnellen Tippen und Strg+S noch hinterherhinken
  const area = useRef<HTMLTextAreaElement>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const touch = useRef<{ x: number; y: number } | null>(null)
  const mobile = typeof matchMedia === 'function' && matchMedia('(max-width: 767px)').matches

  const go = useCallback((d: number) => {
    if (edit !== null) return
    const n = index + d
    if (n >= 0 && n < items.length) onIndex(n)
  }, [index, items.length, onIndex, edit])

  useEffect(() => { setZoom(false); setLoadingFull(false); setScale(1); setCropping(false); setOriginal(extOf(item?.name ?? '') === '.gif'); setImgFailed(0); setText(null); setEdit(null); setError(null) }, [item?.path]) // eslint-disable-line react-hooks/exhaustive-deps

  // Mausrad: voriges/nächstes Bild; mit Strg zoomen (zur Mausposition hin, ab etwa 1,5fach das Original). Nicht passiv, damit der
  // Browser bei Strg+Rad nicht die ganze Seite vergrößert.
  const stageRef = useRef<HTMLDivElement>(null)
  const [scale, setScale] = useState(1)
  const [origin] = useState('50% 50%')
  // Vergrößert: mit gedrückter linker Maustaste verschieben (Original: Bühne scrollen, Strg+Rad: Bild versetzen). Ein Klick ohne
  // Bewegung schaltet wie bisher zurück.
  const [pan, setPan] = useState({ x: 0, y: 0 })
  const [dragging, setDragging] = useState(false)
  const drag = useRef<{ x: number; y: number; sl: number; st: number; px: number; py: number; moved: boolean } | null>(null)
  const dragged = useRef(false)
  useEffect(() => { if (scale <= 1.01) setPan({ x: 0, y: 0 }) }, [scale])
  useEffect(() => setPan({ x: 0, y: 0 }), [item?.path])
  const enlarged = zoom || scale > 1.01
  // Vergrößern per Klick oder Strg+Rad: der Punkt im Bild (Anteil fx/fy) bleibt an seiner Stelle in der Bühne (cx/cy, dort stand
  // der Mauszeiger). Beim Verschieben mitgeführt, damit das Nachladen des Originals (größeres Bild) den Ausschnitt hält
  const zoomAt = useRef<{ fx: number; fy: number; cx: number; cy: number } | null>(null)
  // Angezeigte Größe im Zoom per Strg+Rad (null: natürliche Größe des geladenen Bilds). Breite und Höhe fest, damit das Bild
  // schon richtig liegt, solange das Original noch lädt - sonst hat es kurz keine Höhe und der Ausschnitt verrutscht
  const [zoomW, setZoomWState] = useState<{ w: number; h: number } | null>(null)
  // Dieselbe Größe sofort lesbar: mehrere Rad-Schritte vor dem nächsten Zeichnen bauen aufeinander auf
  const zoomRef = useRef<{ w: number; h: number } | null>(null)
  const setZoomW = (z: { w: number; h: number } | null) => { zoomRef.current = z; setZoomWState(z) }
  // Original für 100 % lädt (Klick bzw. Lupe)
  const [loadingFull, setLoadingFull] = useState(false)
  const pathRef = useRef<string | undefined>(undefined)
  pathRef.current = item?.path
  const imgRef = useRef<HTMLImageElement | null>(null)
  const centerOn = () => {
    const st = stageRef.current, img = imgRef.current
    if (!st || !img || !img.offsetWidth || !img.offsetHeight) return
    const at = zoomAt.current ?? { fx: 0.5, fy: 0.5, cx: st.clientWidth / 2, cy: st.clientHeight / 2 }
    st.scrollLeft = img.offsetLeft + at.fx * img.offsetWidth - at.cx
    st.scrollTop = img.offsetTop + at.fy * img.offsetHeight - at.cy
  }
  useLayoutEffect(() => { if (zoom) centerOn(); else { zoomAt.current = null; setZoomW(null) } }, [zoom, loadedSrc, zoomW]) // eslint-disable-line react-hooks/exhaustive-deps
  const followScroll = () => {
    const st = stageRef.current, img = imgRef.current
    if (!zoom || !st || !img || !img.offsetWidth || !img.offsetHeight) return
    const cx = zoomAt.current?.cx ?? st.clientWidth / 2, cy = zoomAt.current?.cy ?? st.clientHeight / 2
    zoomAt.current = { fx: (st.scrollLeft + cx - img.offsetLeft) / img.offsetWidth, fy: (st.scrollTop + cy - img.offsetTop) / img.offsetHeight, cx, cy }
  }
  const dragStart = (e: React.PointerEvent<HTMLImageElement>) => {
    if (!enlarged || e.button !== 0) return
    const st = stageRef.current
    drag.current = { x: e.clientX, y: e.clientY, sl: st?.scrollLeft ?? 0, st: st?.scrollTop ?? 0, px: pan.x, py: pan.y, moved: false }
    e.currentTarget.setPointerCapture(e.pointerId)
  }
  const dragMove = (e: React.PointerEvent<HTMLImageElement>) => {
    const d = drag.current
    if (!d) return
    const dx = e.clientX - d.x, dy = e.clientY - d.y
    if (!d.moved && Math.abs(dx) + Math.abs(dy) < 4) return
    if (!d.moved) { d.moved = true; setDragging(true) }
    if (zoom && stageRef.current) { stageRef.current.scrollLeft = d.sl - dx; stageRef.current.scrollTop = d.st - dy }
    else setPan({ x: d.px + dx, y: d.py + dy })
  }
  const dragEnd = () => { dragged.current = !!drag.current?.moved; drag.current = null; setDragging(false) }
  const wheelAt = useRef(0)
  useEffect(() => {
    const el = stageRef.current
    if (!el || kind !== 'image') return
    const wheel = (e: WheelEvent) => {
      if (e.ctrlKey) {
        e.preventDefault()
        // Zoomen unter dem Mauszeiger, auch nach dem ersten Klick weiter; unter die eingepasste Größe: zurück zum Einpassen
        const img = imgRef.current
        if (!img || !img.naturalWidth) return
        const b = img.getBoundingClientRect(), sb = el.getBoundingClientRect()
        const fit = Math.min(el.clientWidth / img.naturalWidth, el.clientHeight / img.naturalHeight, 1) * img.naturalWidth
        const cur = zoom ? zoomRef.current?.w ?? img.offsetWidth : b.width
        const ratio = zoomRef.current ? zoomRef.current.h / zoomRef.current.w : img.naturalHeight / img.naturalWidth
        const next = Math.min(cur * Math.pow(1.0015, -e.deltaY), Math.max(img.naturalWidth, fit) * 8)
        if (next <= fit * 1.01) { setZoom(false); return }
        zoomAt.current = { fx: (e.clientX - b.left) / b.width, fy: (e.clientY - b.top) / b.height, cx: e.clientX - sb.left, cy: e.clientY - sb.top }
        if (next > img.naturalWidth) setOriginal(true)
        setZoomW({ w: next, h: next * ratio }); setZoom(true)
        return
      }
      if (zoom || scale > 1.01) return
      e.preventDefault()
      // Ein Rad-Ruck blättert einmal (Touchpads schicken viele kleine Schritte)
      const now = Date.now()
      if (now - wheelAt.current < 280 || Math.abs(e.deltaY) < 4) return
      wheelAt.current = now
      go(e.deltaY > 0 ? 1 : -1)
    }
    el.addEventListener('wheel', wheel, { passive: false })
    return () => el.removeEventListener('wheel', wheel)
  // cropping: nach dem Bearbeiten ist die Bühne ein neues Element
  }, [kind, zoom, scale, go, cropping])

  // Vorige und nächste Bilder schon laden - Blättern zeigt sie dann sofort. Erst wenn das gezeigte da ist: sonst stehen sie beim
  // Erzeugen der Vorschau auf dem Server vor ihm an
  const shownLoaded = loadedPath === items[index]?.path
  useEffect(() => {
    if (!shownLoaded) return
    for (const d of [1, -1, 2]) {
      const n = items[index + d]
      if (n && viewKind(n) === 'image') { const im = new Image(); im.src = source.thumb(n, 1600) }
    }
  }, [index, items, source, shownLoaded])

  // Text laden: nur den Anfang grosser Dateien (Bereichsanfrage)
  useEffect(() => {
    if (!item || (kind !== 'text' && kind !== 'markdown')) return
    let gone = false
    fetch(source.download(item, true), { credentials: 'same-origin', cache: 'no-store', headers: { Range: `bytes=0-${TEXT_LIMIT - 1}` } })
      .then(async r => {
        if (!r.ok) throw new Error(`${r.status}`)
        const buf = await r.arrayBuffer()
        if (gone) return
        const body = new TextDecoder('utf-8').decode(buf)
        setText({ body, cut: item.size > TEXT_LIMIT })
        if (startEdit && onSave && item.size <= TEXT_LIMIT) setEdit(body)
      })
      .catch(e => { if (!gone) setError(String(e.message ?? e)) })
    return () => { gone = true }
  // Nur beim Wechsel der Datei - nach dem eigenen Speichern steht der Text schon da
  }, [item?.path, kind]) // eslint-disable-line react-hooks/exhaustive-deps

  // Nachbarn vorladen, damit vor/zurueck sofort da ist
  useEffect(() => {
    for (const d of [-1, 1]) {
      const n = items[index + d]
      if (n && viewKind(n) === 'image') { const img = new Image(); img.src = source.thumb(n, 1600) }
    }
  }, [index, items, source])

  const save = async (force = false) => {
    const value = area.current?.value ?? edit
    if (!onSave || value === null) return
    setSaving(true); setError(null)
    try {
      await onSave(item, value, force)
      setText({ body: value, cut: false }); setEdit(null)
    } catch (e: any) {
      setError(e?.status === 409 ? 'conflict' : e?.message ?? String(e))
    } finally { setSaving(false) }
  }

  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (edit !== null) {
        if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); save() }
        else if (e.key === 'Escape') { e.preventDefault(); setEdit(null) }
        return
      }
      // Im Foto-Editor: Esc schließt nur den Editor, Pfeiltasten blättern nicht (die Änderungen gingen sonst verloren)
      if (cropping) { if (e.key === 'Escape') { e.preventDefault(); setCropping(false) } return }
      // Markieren wie in der Galerie
      const k = e.key.toLowerCase()
      // Farbe wie im Camera Importer: Strg+1 bis Strg+5, Strg+0 ohne; dieselbe noch einmal: weg
      if (onMark && item && (e.ctrlKey || e.metaKey) && !e.altKey && !e.shiftKey && /^Digit[0-5]$/.test(e.code) && !(e.target as HTMLElement).closest?.('input, textarea')) {
        e.preventDefault()
        const n = +e.code.slice(5)
        const l = n === 0 ? '' : MARK_LABELS[n - 1]
        onMark(item, { label: l && markOf?.(item)?.label === l ? '' : l })
        return
      }
      if (onMark && item && !e.ctrlKey && !e.metaKey && !e.altKey && /^[pxu0-9]$/.test(k) && !(e.target as HTMLElement).closest?.('input, textarea')) {
        e.preventDefault()
        if (k === 'p') onMark(item, { pick: 1 })
        else if (k === 'x') onMark(item, { pick: -1 })
        else if (k === 'u') onMark(item, { pick: 0 })
        else if (+k <= 5) onMark(item, { rating: +k })
        else { const l = ['red', 'yellow', 'green', 'blue'][+k - 6]; onMark(item, { label: markOf?.(item)?.label === l ? '' : l }) }
        return
      }
      if (e.key === 'Delete' && onDelete && item) { e.preventDefault(); onDelete(item); return }
      if (e.key === 'Escape') { e.preventDefault(); onClose() }
      else if (e.key === 'ArrowLeft') { e.preventDefault(); go(-1) }
      else if (e.key === 'ArrowRight') { e.preventDefault(); go(1) }
    }
    window.addEventListener('keydown', key, true)
    return () => window.removeEventListener('keydown', key, true)
  }) // eslint-disable-line react-hooks/exhaustive-deps

  if (!item) return null
  const dark = kind === 'image' || kind === 'video' || kind === 'audio' || kind === null
  const canEdit = !!onSave && (kind === 'text' || kind === 'markdown') && text !== null && !text.cut
  // Bearbeitet (Einstellungen gemerkt): der Etag trägt den Fingerabdruck
  const edited = kind === 'image' && !!item.etag?.includes('~e')

  /** Klick bzw. Lupe: auf 100 % des Originals, der Punkt at bleibt an seiner Stelle (null: Mitte). Erst das Original laden, dann in
   *  einem Schritt vergrößern - nicht erst auf die Größe der Vorschau und danach noch einmal. */
  const zoom100 = (at: { fx: number; fy: number; cx: number; cy: number } | null) => {
    const path = item.path
    const show = (w: number, h: number) => {
      if (pathRef.current !== path) return
      setLoadingFull(false)
      zoomAt.current = at
      setZoomW({ w, h }); setOriginal(true); setZoom(true)
    }
    const im = new Image()
    im.onload = () => show(im.naturalWidth, im.naturalHeight)
    im.onerror = () => { const img = imgRef.current; if (img?.naturalWidth) show(img.naturalWidth, img.naturalHeight); else setLoadingFull(false) }
    setLoadingFull(true)
    im.src = edited ? source.thumb(item, 2048) : source.download(item, true)
    if (im.complete && im.naturalWidth) show(im.naturalWidth, im.naturalHeight)
  }

  const body = () => {
    switch (kind) {
      case 'image': {
        // Bearbeitet: „Originalgröße“ ist die große bearbeitete Fassung (die Datei selbst ist unbearbeitet)
        const src = peek && edited ? source.thumb(item, 1600) + '&orig=1'
          : original ? (edited ? source.thumb(item, 2048) : source.download(item, true))
          : imgFailed > 0 ? source.download(item, true) : source.thumb(item, 1600)
        if (imgFailed > 1) return <NoPreview item={item} onDownload={onDownload} />
        // Original lädt nach: eingepasst die unscharfe Vorschau darüber; vergrößert bleibt das bisherige Bild stehen (dasselbe
        // Element, der Browser zeigt es, bis das neue da ist) - sonst sprang die Ansicht kurz auf „eingepasst“
        const waiting = loadedSrc !== src && !zoom
        return <>
          {loadingFull && <Spinner style={{ position: 'absolute', top: '50%', left: '50%', transform: 'translate(-50%, -50%)', zIndex: 1 }} />}
          {waiting && <div style={{ position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', pointerEvents: 'none' }}>
            <img src={source.thumb(item, 256)} alt="" draggable={false} style={{ width: '100%', height: '100%', objectFit: 'contain', filter: 'blur(1px)' }} />
            <Spinner style={{ position: 'absolute' }} />
          </div>}
          <img key={item.path} src={src} alt={item.name} className={mergeClasses(s.img, zoom && s.imgFull)} draggable={false}
            style={{ ...(scale > 1.01 && !zoom ? { transform: `translate(${pan.x}px, ${pan.y}px) scale(${scale})`, transformOrigin: origin } : {}),
              ...(zoom && zoomW ? { width: zoomW.w, height: zoomW.h } : {}),
              ...(enlarged ? { cursor: dragging ? 'grabbing' : 'grab' } : {}), ...(waiting ? { opacity: 0 } : {}) }}
            onLoad={() => { setLoadedSrc(src); setLoadedPath(item.path) }} onPointerDown={dragStart} onPointerMove={dragMove} onPointerUp={dragEnd} onPointerCancel={dragEnd}
            ref={imgRef}
            onClick={e => {
              if (dragged.current) { dragged.current = false; return }
              if (scale > 1.01) { setScale(1); return }
              // Vergrößert: zurück zum Einpassen; sonst 100 % mit der angeklickten Stelle unter dem Mauszeiger
              if (zoom) { setZoom(false); return }
              if (loadingFull) return
              const b = e.currentTarget.getBoundingClientRect(), sb = stageRef.current?.getBoundingClientRect() ?? b
              zoom100({ fx: (e.clientX - b.left) / b.width, fy: (e.clientY - b.top) / b.height, cx: e.clientX - sb.left, cy: e.clientY - sb.top })
            }} onError={() => setImgFailed(n => n + 1)} />
        </>
      }
      case 'pdf':
        // Seiten als Bilder vom Server: der PDF-Betrachter des Browsers lässt sich nicht überall einbetten (Handys, Vivaldi)
        return <PdfPagesView key={item.path} url={source.download(item, true)} title={item.name} />
      case 'audio':
        return <div className={s.none}><KindIcon item={item} large /><audio key={item.path} controls autoPlay src={source.download(item, true)} style={{ width: 'min(520px, 90vw)' }} /></div>
      case 'video':
        return <video key={item.path} className={s.media} controls autoPlay playsInline src={source.download(item, true)}
          onError={() => setError(t('Dieses Video kann der Browser nicht abspielen.'))} />
      case 'text':
      case 'markdown':
        if (error && !text) return <NoPreview item={item} onDownload={onDownload} />
        if (!text) return <Spinner />
        if (edit !== null) return <textarea ref={area} className={s.editor} value={edit} onChange={e => setEdit(e.target.value)} autoFocus spellCheck={kind === 'markdown'} />
        return (
          <div className={s.doc}>
            {text.cut && <Text block style={{ marginBottom: 8, color: tokens.colorPaletteMarigoldForeground1 }}>{t('Nur der Anfang wird gezeigt ({size}).', { size: fmtSize(TEXT_LIMIT) })}</Text>}
            {kind === 'markdown' ? <Markdown text={text.body} /> : <pre className={s.pre}>{text.body}</pre>}
          </div>
        )
      default:
        return <NoPreview item={item} onDownload={onDownload} />
    }
  }

  return (
    <div className={mergeClasses(s.root, !dark && s.light)} role="dialog" aria-label={item.name}
      onTouchStart={e => { if (kind === 'image' && !zoom && !cropping) touch.current = { x: e.touches[0].clientX, y: e.touches[0].clientY } }}
      onTouchEnd={e => {
        const st = touch.current; touch.current = null
        if (!st) return
        const dx = e.changedTouches[0].clientX - st.x, dy = e.changedTouches[0].clientY - st.y
        if (Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy) * 1.5) go(dx < 0 ? 1 : -1)
        else if (dy > 120 && Math.abs(dy) > Math.abs(dx) * 2) onClose()
      }}>
      <div className={s.bar}>
        <div className={s.title}>
          <span className={s.name} title={item.name}>{item.name}</span>
          <span className={s.sub}>
            {items.length > 1 && `${index + 1} / ${items.length} · `}{fmtSize(item.size)} · {fmtWhen(item.modified, locale)}{edited && ` · ${t('bearbeitet')}`}
          </span>
        </div>
        {/* Markieren (Galerie) */}
        {onMark && kind === 'image' && !cropping && (() => {
          const m = markOf?.(item)
          return <>
            {([[1, 'P', t('Ausgewählt'), '#4CC38A'], [-1, 'X', t('Abgelehnt'), '#FF6369'], [0, 'U', t('Ohne Markierung'), undefined]] as const).map(([v, key, name, color]) => (
              <Tooltip key={key} content={`${name} (${key})`} relationship="label">
                <Button appearance="transparent" className={s.btn} aria-pressed={(m?.pick ?? 0) === v}
                  style={{ minWidth: 30, fontWeight: (m?.pick ?? 0) === v ? 700 : 400, color: (m?.pick ?? 0) === v ? color ?? 'inherit' : 'inherit', opacity: (m?.pick ?? 0) === v ? 1 : 0.7 }}
                  onClick={() => onMark(item, { pick: v })}>{key}</Button>
              </Tooltip>))}
            <Menu>
              <MenuTrigger disableButtonEnhancement><Tooltip content={t('Bewertung (0–5)')} relationship="label">
                <Button appearance="transparent" className={s.btn} style={{ fontSize: 12, letterSpacing: -1, padding: '0 6px' }}>
                  {/* Fünf kleine Sterne (nicht der Stern des Favoriten) */}
                  <span style={{ color: '#E8A33D' }}>{'★'.repeat(m?.rating ?? 0)}</span><span style={{ opacity: 0.6 }}>{'☆'.repeat(5 - (m?.rating ?? 0))}</span>
                </Button>
              </Tooltip></MenuTrigger>
              <MenuPopover><MenuList>
                {[5, 4, 3, 2, 1].map(n => <MenuItem key={n} onClick={() => onMark(item, { rating: n })}>{'★'.repeat(n)} ({n})</MenuItem>)}
                <MenuItem onClick={() => onMark(item, { rating: 0 })}>{t('Ohne Bewertung')} (0)</MenuItem>
              </MenuList></MenuPopover>
            </Menu>
            <Menu>
              <MenuTrigger disableButtonEnhancement><Tooltip content={t('Farbe')} relationship="label">
                <Button appearance="transparent" className={s.btn}><span style={{ width: 14, height: 14, borderRadius: '50%', display: 'inline-block', background: labelColor(m?.label) ?? 'transparent', border: m?.label ? 'none' : '1px solid currentColor' }} /></Button>
              </Tooltip></MenuTrigger>
              <MenuPopover><MenuList>
                {MARK_LABELS.map((l, k) => <MenuItem key={l} icon={<span style={{ width: 12, height: 12, borderRadius: '50%', display: 'inline-block', background: labelColor(l) }} />}
                  onClick={() => onMark(item, { label: l })}>{labelName(l)} ({t('Strg')}+{k + 1}{k < 4 ? `, ${k + 6}` : ''})</MenuItem>)}
                <MenuItem onClick={() => onMark(item, { label: '' })}>{t('Ohne Farbe')} ({t('Strg')}+0)</MenuItem>
              </MenuList></MenuPopover>
            </Menu>
            {onFavorite && <Tooltip content={t('Favorit')} relationship="label">
              <Button appearance="transparent" className={s.btn} icon={item.favorite ? <Star20Filled color="#E8A33D" /> : <Star20Regular />} aria-pressed={!!item.favorite} onClick={() => onFavorite(item)} />
            </Tooltip>}
            <span className={s.sep} />
          </>
        })()}
        {edited && !cropping && <Tooltip content={t('Gedrückt halten: Original zeigen')} relationship="label">
          <Button appearance="transparent" className={s.btn} aria-pressed={peek}
            onPointerDown={() => setPeek(true)} onPointerUp={() => setPeek(false)} onPointerLeave={() => setPeek(false)} onPointerCancel={() => setPeek(false)}
            onKeyDown={e => { if (e.key === ' ' || e.key === 'Enter') setPeek(true) }} onKeyUp={() => setPeek(false)}>{t('Original')}</Button>
        </Tooltip>}
        {kind === 'image' && !original && !cropping && <Tooltip content={t('Originalgröße laden')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<ZoomIn20Regular />} onClick={() => zoom100(null)} />
        </Tooltip>}
        {kind === 'image' && zoom && <Tooltip content={t('Einpassen')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<ZoomOut20Regular />} onClick={() => setZoom(false)} />
        </Tooltip>}
        {items.length > 1 && !cropping && <Tooltip content={t('Filmstreifen')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Filmstrip20Regular />} aria-pressed={strip} onClick={toggleStrip} />
        </Tooltip>}
        <span className={s.sep} />
        {kind === 'image' && onPhotoEdit && (/\.jpe?g$/i.test(item.name) || rawEdit && RAW.test(item.name)) && !cropping && <Tooltip content={t('Foto bearbeiten')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<ImageEdit20Regular />} onClick={() => { setZoom(false); setScale(1); setCropping(true) }} />
        </Tooltip>}
        {canEdit && edit === null && <Button appearance="transparent" className={s.btn} icon={<Edit20Regular />} onClick={() => setEdit(text!.body)}>{mobile ? undefined : t('Bearbeiten')}</Button>}
        {edit !== null && <>
          <Button appearance="primary" icon={saving ? <Spinner size="tiny" /> : <Save20Regular />} disabled={saving} onClick={() => save()}>{t('Speichern')}</Button>
          <Button appearance="transparent" className={s.btn} onClick={() => { setEdit(null); setError(null) }}>{t('Abbrechen')}</Button>
        </>}
        {kind === 'image' && onPhotoEdit && !cropping && <span className={s.sep} />}
        {onChat && item.fileId && <Tooltip content={t('Im Chat zeigen oder senden …')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Chat20Regular />} onClick={() => onChat(item)} />
        </Tooltip>}
        {onShare && !cropping && <Tooltip content={t('Teilen …')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Share20Regular />} onClick={() => onShare(item)} />
        </Tooltip>}
        {(onShare || onChat && item.fileId) && !cropping && <span className={s.sep} />}
        {(kind === 'pdf' || kind === 'image') && !mobile && <Tooltip content={t('In neuem Tab öffnen')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Open20Regular />} as="a" href={source.download(item, true)} target="_blank" />
        </Tooltip>}
        <Tooltip content={t('Herunterladen')} relationship="label">
          {onDownload && <Button appearance="transparent" className={s.btn} icon={<ArrowDownload20Regular />} onClick={() => onDownload(item)} />}
        </Tooltip>
        {onDelete && !cropping && <Tooltip content={t('Löschen (Entf)')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Delete20Regular />} onClick={() => onDelete(item)} />
        </Tooltip>}
        <span className={s.sep} />
        {(side || onDetails) && !cropping && <Tooltip content={t('Details')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Info20Regular />} aria-pressed={showSide}
            onClick={() => side ? setShowSide(v => !v) : onDetails!(item)} />
        </Tooltip>}
        <Tooltip content={t('Schließen (Esc)')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Dismiss24Regular />} onClick={onClose} />
        </Tooltip>
      </div>
      {error && (edit !== null || text) && (
        <div style={{ padding: '6px 16px', background: tokens.colorPaletteRedBackground2, color: tokens.colorPaletteRedForeground2, display: 'flex', gap: 12, alignItems: 'center' }}>
          {error === 'conflict' ? <>
            <span style={{ flex: 1 }}>{t('Die Datei wurde inzwischen geändert.')}</span>
            <Button size="small" onClick={() => save(true)}>{t('Trotzdem speichern')}</Button>
          </> : <span>{error}</span>}
        </div>
      )}
      {/* Nach dem Speichern die Vorschau mit neuem Stand zeigen (die Originaldatei-Adresse trägt keinen Stand) */}
      {/* Der Editor arbeitet auf dem Original (orig=1) und lädt gemerkte Einstellungen selbst */}
      {cropping && onPhotoEdit ? <PhotoEditor src={source.thumb(item, 1600) + '&orig=1'} path={item.path} onCancel={() => setCropping(false)}
        onApply={async st => { if (onPhotoSettings) await onPhotoSettings(item, st); setCropping(false); setOriginal(false) }}
        onExport={async st => { await onPhotoEdit(item, st.rotate, st.crop, false, st.adjust); setCropping(false); setOriginal(false) }} /> :
      <div style={{ flex: 1, minHeight: 0, display: 'flex' }}>
        {/* Pfeile außerhalb der scrollenden Bühne: bleiben beim Vergrößern an ihrem Platz */}
        <div style={{ flex: 1, minWidth: 0, position: 'relative', display: 'flex' }}>
          <div ref={stageRef} className={mergeClasses(s.stage, zoom && s.zoomed)} onScroll={followScroll}>
            {body()}
          </div>
          {index > 0 && edit === null && <button className={s.nav} style={{ left: 8 }} onClick={() => go(-1)} aria-label={t('Zurück')}><ChevronLeft24Regular /></button>}
          {index < items.length - 1 && edit === null && <button className={s.nav} style={{ right: 8 }} onClick={() => go(1)} aria-label={t('Weiter')}><ChevronRight24Regular /></button>}
        </div>
        {/* Details rechts, beim Blättern für das jeweils gezeigte Bild */}
        {side && showSide && <div className={s.side}>{side(item, () => setShowSide(false))}</div>}
      </div>}
      {strip && items.length > 1 && !cropping && <div ref={stripRef} className={s.strip}
        // Mausrad (senkrecht) scrollt den Filmstreifen waagerecht
        onWheel={e => { if (!e.ctrlKey && Math.abs(e.deltaY) > Math.abs(e.deltaX)) e.currentTarget.scrollLeft += e.deltaY }}>
        {items.map((x, k) => groupOf && groupOf(x) !== groupOf(item) ? null : <img key={x.path} data-on={k === index ? '1' : '0'} className={mergeClasses(s.stripImg, k === index && s.stripOn)} src={source.thumb(x, 128)} alt="" title={x.name}
          // Noch ohne Vorschaubild: leere Kachel statt des Symbols für ein kaputtes Bild
          onError={e => { const img = e.currentTarget; img.onerror = null; img.src = 'data:image/gif;base64,R0lGODlhAQABAAAAACH5BAEKAAEALAAAAAABAAEAAAICTAEAOw=='; img.style.background = 'rgba(255,255,255,0.12)' }}
          loading="lazy" onClick={() => onIndex(k)} />)}
      </div>}
    </div>
  )
}

/**
 * PDF als Seitenbilder untereinander (Server: Download-Adresse mit pdfpages / pdfpage, siehe PdfPages.cs). Eingebettet zeigt
 * nicht jeder Browser PDFs: Handys gar nicht, Vivaldi nicht unter der CSP der Seite (object-src 'none').
 */
export function PdfPagesView({ url, title }: { url: string; title: string }) {
  const s = useStyles()
  const box = useRef<HTMLDivElement>(null)
  const [count, setCount] = useState<number | null>(null)
  const [width, setWidth] = useState(1200)
  const page = (extra: string) => url + (url.includes('?') ? '&' : '?') + extra
  useEffect(() => {
    let alive = true
    setCount(null)
    fetch(page('pdfpages=1'), { credentials: 'same-origin' })
      .then(r => r.ok ? r.json() : { count: 0 }).then((r: { count: number }) => { if (alive) setCount(r.count) })
      .catch(() => { if (alive) setCount(0) })
    return () => { alive = false }
  }, [url]) // eslint-disable-line react-hooks/exhaustive-deps
  // Bildbreite nach Platz und Pixeldichte, in Stufen (sonst rechnet jede Fenstergröße neu)
  useEffect(() => {
    const w = box.current?.clientWidth ?? 1000
    setWidth(Math.min(2000, Math.ceil(Math.min(w - 24, 1000) * (window.devicePixelRatio || 1) / 400) * 400))
  }, [count])
  if (count === null) return <div className={s.none}><Spinner /></div>
  if (count === 0) return <div className={s.none}><Text>{t('Die PDF lässt sich nicht anzeigen.')}</Text></div>
  const shown = Math.min(count, 100)
  return (
    <div ref={box} className={s.pages}>
      {Array.from({ length: shown }, (_, n) => (
        <img key={n} className={s.page} src={page(`pdfpage=${n}&width=${width}`)} alt={t('Seite {n}', { n: n + 1 })} title={`${title} – ${n + 1}/${count}`} loading="lazy" />
      ))}
      {count > shown && <Text>{t('Weitere {n} Seiten: Herunterladen', { n: count - shown })}</Text>}
    </div>
  )
}

function NoPreview({ item, onDownload }: { item: Item; onDownload?: (i: Item) => void }) {
  const s = useStyles()
  return (
    <div className={s.none}>
      <div style={{ transform: 'scale(1.6)' }}><KindIcon item={item} large /></div>
      <Text size={400} weight="semibold">{item.name}</Text>
      <Text>{t('Für diese Datei gibt es keine Vorschau.')}</Text>
      {onDownload
        ? <Button appearance="primary" icon={<ArrowDownload20Regular />} onClick={() => onDownload(item)}>{t('Herunterladen')} ({fmtSize(item.size)})</Button>
        : <Text>{t('Nur gezeigt – herunterladen ist nicht erlaubt.')}</Text>}
    </div>
  )
}
