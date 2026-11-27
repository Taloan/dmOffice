// Gemeinsames der Dateiablage: Eintraege, Pfade, Arten, Adressen, Symbole, Vorschaubilder.
// Wird auch von der oeffentlichen Seite (PublicShare) benutzt - dort mit eigenen Adressen.
import { useEffect, useRef, useState } from 'react'
import { tokens } from '@fluentui/react-components'
import {
  Folder20Filled, Document20Regular, Image20Regular, DocumentPdf20Regular, Video20Regular, MusicNote220Regular, FolderZip20Regular,
  DocumentText20Regular, Table20Regular, SlideText20Regular, DocumentCopy20Regular,
  Folder48Filled, Document48Regular, Image48Regular, DocumentPdf32Regular, Video48Regular, MusicNote224Regular, FolderZip24Regular,
  DocumentText48Regular, Table48Regular, SlideText48Regular, ShieldLock20Filled, ShieldLock48Filled,
} from '@fluentui/react-icons'
import { q } from '../api'
import { t } from '../i18n'

export interface Item {
  path: string; name: string; isDir: boolean; size: number; mime: string | null; modified: string
  created?: string; fileId?: number; etag?: string; favorite?: boolean; shared?: boolean
  /** Mit Personen geteilt (eigener Ordner) */
  sharedWith?: boolean
  /** Ordner für WebDAV und Nextcloud ausgeblendet */
  davHidden?: boolean | null
  /** Nur bei einem mit mir geteilten Ordner in "Mit mir geteilt": wem er gehoert, und ob ich darin schreiben darf */
  owner?: string | null; canWrite?: boolean | null; showOnly?: boolean | null
  /** Suche: im Inhalt gefunden (Volltextsuche), Ausschnitt mit U+E000/U+E001 um die Treffer */
  snippet?: string | null
  /** Galerie: weitere Dateien desselben Stapels (RAW zum JPG); das Item selbst ist das JPG */
  stack?: Item[]
  /** Im Stapel: dasselbe Foto an anderer Stelle (etwa in einem Galerie-Ordner) - Aktionen auf den Stapel nehmen es nicht mit */
  dupe?: boolean
  /** Aufgeklappter Stapel: Stelle im Stapel („2/3“) und das Foto oben im Stapel */
  stackPos?: { n: number; of: number; lead: string }
  /** Die Datei ist ein Dokument im DMS */
  dms?: boolean | null
  /** Ordner ist ein eigener Cryptomator-Tresor (vault.cryptomator darin) */
  vault?: boolean
  /** Eigener Cryptomator-Tresor, gerade offen */
  vaultOpen?: boolean
}

const RAW_EXT = new Set(['.arw', '.cr2', '.cr3', '.nef', '.nrw', '.orf', '.rw2', '.raf', '.pef', '.dng', '.srw', '.3fr', '.erf'])
const LEAD_EXT = ['.jpg', '.jpeg', '.heic', '.heif']
/**
 * JPG und RAW gleichen Namens im selben Ordner zu einem Stapel: das JPG steht vorn (Bild, Teilen, Chat), die RAW-Dateien
 * hängen daran (Verschieben, Kopieren, Löschen, Herunterladen nehmen sie mit). Reihenfolge bleibt die der Liste.
 */
export function stackPhotos<T extends Item>(list: T[]): T[] {
  const key = (i: Item) => i.path.slice(0, i.path.length - extOf(i.name).length).toLowerCase()
  const byKey = new Map<string, T[]>()
  // Nur, wo der Pfad auf den Namen endet - Album-Einträge („alb:…/Besitzer|Verweis“) nicht, sonst fielen fremde Fotos zusammen
  for (const i of list) if (!i.isDir && i.path.endsWith(i.name)) { const k = key(i); byKey.set(k, [...(byKey.get(k) ?? []), i]) }
  const lead = new Map<string, T>()
  for (const [k, g] of byKey) {
    if (g.length < 2 || !g.some(i => RAW_EXT.has(extOf(i.name)))) continue
    const head = LEAD_EXT.map(e => g.find(i => extOf(i.name) === e)).find(Boolean)
    if (head) lead.set(k, head)
  }
  if (!lead.size) return list
  const out: T[] = []
  for (const i of list) {
    const h = i.isDir ? undefined : lead.get(key(i))
    if (!h) out.push(i)
    else if (h === i) out.push({ ...i, stack: byKey.get(key(i))!.filter(x => x !== i) })
  }
  return out
}
/** Kennzeichen eines Stapels: die RAW-Endungen, bei Doppelten zusätzlich die Anzahl der Kopien („RAF · 2×“). */
export const stackLabel = (i: Item) => {
  const own = [...new Set((i.stack ?? []).filter(x => !x.dupe).map(x => extOf(x.name).slice(1).toUpperCase()))].join('+')
  const copies = 1 + (i.stack ?? []).filter(x => x.dupe && LEAD_EXT.includes(extOf(x.name))).length
  return [own, copies > 1 ? `${copies}×` : ''].filter(Boolean).join(' · ')
}
/** Aufgeklappte Stapel als einzelne Kacheln: das Foto oben im Stapel zuerst, dann die übrigen, jedes mit seiner Stelle („1/3“). */
export function openStacks<T extends Item>(list: T[], open: Set<string>): T[] {
  if (!open.size) return list
  return list.flatMap(i => {
    if (!i.stack?.length || !open.has(i.path)) return [i]
    const all: Item[] = [{ ...i, stack: undefined }, ...i.stack]
    return all.map((x, k) => ({ ...x, stackPos: { n: k + 1, of: all.length, lead: i.path } }) as T)
  })
}
/** Stapel auflösen: das Item und seine RAW-Dateien - ohne Doppelte an anderer Stelle (die bleiben, wo sie sind). */
export const unstack = <T extends Item>(list: T[]): Item[] => list.flatMap(i => [i, ...(i.stack ?? []).filter(x => !x.dupe)])


/** Name des virtuellen Ordners in der Wurzel, solange andere etwas mit mir teilen. */
export const WITH_ME = 'Mit mir geteilt'
/** Liegt der Pfad in "Mit mir geteilt"? Nur wenn es solche Ordner gibt - sonst ist es ein gewoehnlicher eigener Name. */
export const inWithMe = (p: string, any: boolean) => any && (p === WITH_ME || p.startsWith(WITH_ME + '/'))

export const parentOf = (p: string) => (p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '')
export const join = (dir: string, name: string) => (dir ? `${dir}/${name}` : name)
export const nameOf = (p: string) => p.slice(p.lastIndexOf('/') + 1)
/** Ordner in sich selbst oder einen eigenen Unterordner - geht nicht. */
export const inside = (folder: string, target: string) => target === folder || target.startsWith(folder + '/')
export const extOf = (name: string) => (name.includes('.') ? name.slice(name.lastIndexOf('.')).toLowerCase() : '')

export type Kind = 'folder' | 'image' | 'pdf' | 'doc' | 'sheet' | 'slides' | 'audio' | 'video' | 'archive' | 'text' | 'other'

const EXT: Record<string, Kind> = {}
const add = (k: Kind, list: string) => list.split(' ').forEach(e => { EXT['.' + e] = k })
add('image', 'jpg jpeg png gif webp bmp svg heic heif tif tiff ico jfif avif arw cr2 cr3 nef nrw orf rw2 raf pef dng srw 3fr erf')
add('pdf', 'pdf')
add('doc', 'doc docx odt rtf pages')
add('sheet', 'xls xlsx ods csv numbers')
add('slides', 'ppt pptx odp key')
add('audio', 'mp3 m4a wav ogg oga opus flac aac wma')
add('video', 'mp4 m4v mov webm mkv avi wmv ogv')
add('archive', 'zip 7z rar tar gz tgz bz2 xz')
add('text', 'txt md markdown log json xml yaml yml ini conf cfg toml cs js ts tsx jsx py java kt c h cpp go rs sh ps1 bat sql css scss html htm php rb swift ics vcf')

/** Dieselben Arten wie auf dem Server (FileKinds) - fuer Filter, Symbole und den Betrachter. */
export function kindOf(i: { name: string; isDir: boolean; mime?: string | null }): Kind {
  if (i.isDir) return 'folder'
  const k = EXT[extOf(i.name)]
  if (k) return k
  const m = i.mime ?? ''
  return m.startsWith('image/') ? 'image' : m.startsWith('audio/') ? 'audio' : m.startsWith('video/') ? 'video' : m.startsWith('text/') ? 'text' : 'other'
}

export const kindLabel = (k: Kind | string): string => ({
  folder: t('Ordner'), image: t('Bild'), pdf: 'PDF', doc: t('Dokument'), sheet: t('Tabelle'), slides: t('Präsentation'),
  audio: t('Audio'), video: t('Video'), archive: t('Archiv'), text: t('Textdatei'), other: t('Datei'),
} as Record<string, string>)[k] ?? t('Datei')

/**
 * Dateiname zum Anzeigen: Richtungs-Steuerzeichen (etwa U+202E) entfernt, damit „Urlaub ‮gnp.exe“ nicht als „….png“
 * erscheint; Name in <bdi>, damit Schrift von rechts nach links den Text daneben nicht mitdreht. Der echte Name bleibt unverändert.
 */
export const plainName = (n: string) => n.replace(/[\u202A-\u202E\u2066-\u2069]/g, '')
export const ShownName = ({ name }: { name: string }) => <bdi>{plainName(name)}</bdi>

/** Filter in Liste und Suche. */
export const FILTERS: Kind[] = ['folder', 'image', 'pdf', 'doc', 'sheet', 'slides', 'text', 'audio', 'video', 'archive']

const FOLDER = '#E8A33D'

export function KindIcon({ item, large }: { item: { name: string; isDir: boolean; mime?: string | null; vault?: boolean; vaultOpen?: boolean }; large?: boolean }) {
  // Eigener Cryptomator-Tresor: Ordner mit Schloss, offen grün (wie der Tresor in der Seitenleiste)
  if (item.vault) { const c = item.vaultOpen ? tokens.colorPaletteGreenForeground1 : FOLDER; return large ? <ShieldLock48Filled color={c} /> : <ShieldLock20Filled color={c} /> }
  const k = kindOf(item)
  const style = large ? { width: 48, height: 48 } : undefined
  if (large) switch (k) {
    case 'folder': return <Folder48Filled color={FOLDER} />
    case 'image': return <Image48Regular style={style} />
    case 'pdf': return <DocumentPdf32Regular color="#C4314B" style={style} />
    case 'video': return <Video48Regular color="#8764B8" />
    case 'audio': return <MusicNote224Regular color="#C239B3" style={style} />
    case 'archive': return <FolderZip24Regular color="#986F0B" style={style} />
    case 'sheet': return <Table48Regular color="#107C41" />
    case 'slides': return <SlideText48Regular color="#C43E1C" />
    case 'doc': return <DocumentText48Regular color="#185ABD" />
    case 'text': return <DocumentText48Regular />
    default: return <Document48Regular />
  }
  switch (k) {
    case 'folder': return <Folder20Filled color={FOLDER} />
    case 'image': return <Image20Regular color="#0F7B8F" />
    case 'pdf': return <DocumentPdf20Regular color="#C4314B" />
    case 'video': return <Video20Regular color="#8764B8" />
    case 'audio': return <MusicNote220Regular color="#C239B3" />
    case 'archive': return <FolderZip20Regular color="#986F0B" />
    case 'sheet': return <Table20Regular color="#107C41" />
    case 'slides': return <SlideText20Regular color="#C43E1C" />
    case 'doc': return <DocumentText20Regular color="#185ABD" />
    case 'text': return <DocumentText20Regular />
    default: return extOf(item.name) ? <Document20Regular /> : <DocumentCopy20Regular />
  }
}

/** Was der Betrachter zeigen kann. */
export type ViewKind = 'image' | 'pdf' | 'text' | 'markdown' | 'audio' | 'video' | null

export function viewKind(i: Item): ViewKind {
  if (i.isDir) return null
  const k = kindOf(i)
  const ext = extOf(i.name)
  if (k === 'image') return 'image'
  if (k === 'pdf') return 'pdf'
  if (k === 'audio') return 'audio'
  if (k === 'video') return 'video'
  if (ext === '.md' || ext === '.markdown') return 'markdown'
  if (k === 'text' || ext === '.csv' || i.mime?.startsWith('text/')) return 'text'
  return null
}

/** Hat der Server ein Vorschaubild (Bild, PDF) oder kann der Browser eins machen (Video)? */
export const hasThumb = (i: Item) => !i.isDir && ['image', 'pdf', 'video'].includes(kindOf(i)) && extOf(i.name) !== '.svg'

/** Adressen fuer Inhalte: angemeldet (/api/files) oder ueber einen oeffentlichen Link (/api/public/{token}). */
export interface Source {
  download: (i: Item, inline?: boolean) => string
  thumb: (i: Item, size?: number) => string
  /** Videos: Standbild im Browser erzeugen und hochladen (nur angemeldet) */
  storeThumb?: (i: Item, blob: Blob) => Promise<void>
}

export const ownSource: Source = {
  download: (i, inline) => '/api/files/download?' + q({ path: i.path, inline: inline || undefined }),
  thumb: (i, size = 256) => '/api/files/thumb?' + q({ path: i.path, size, v: i.etag, quiet: 1 }),
  storeThumb: async (i, blob) => {
    await fetch('/api/files/thumb?' + q({ path: i.path }), { method: 'PUT', body: blob, credentials: 'same-origin' })
  },
}

export function publicSource(token: string): Source {
  const base = `/api/public/${encodeURIComponent(token)}`
  return {
    download: (i, inline) => `${base}/download?` + q({ path: i.path, inline: inline || undefined }),
    thumb: (i, size = 256) => `${base}/thumb?` + q({ path: i.path, size, v: i.etag, quiet: 1 }),
  }
}

// Standbilder fuer Videos: hoechstens zwei gleichzeitig, jedes Video nur einmal je Sitzung versuchen
const tried = new Set<string>()
let running = 0
const waiting: (() => void)[] = []
const slot = () => new Promise<void>(r => { if (running < 2) { running++; r() } else waiting.push(() => { running++; r() }) })
const free = () => { running--; waiting.shift()?.() }

function videoFrame(url: string): Promise<Blob | null> {
  return new Promise(resolve => {
    const v = document.createElement('video')
    v.muted = true; v.preload = 'metadata'; v.crossOrigin = 'use-credentials'; v.playsInline = true
    let done = false
    const finish = (b: Blob | null) => { if (done) return; done = true; v.removeAttribute('src'); v.load(); resolve(b) }
    const timer = setTimeout(() => finish(null), 15000)
    v.onloadedmetadata = () => { v.currentTime = Math.min(1, (v.duration || 2) / 3) }
    v.onseeked = () => {
      clearTimeout(timer)
      const w = Math.min(640, v.videoWidth || 640), h = Math.round(w * (v.videoHeight || 360) / (v.videoWidth || 640))
      const c = document.createElement('canvas'); c.width = w; c.height = h
      try { c.getContext('2d')!.drawImage(v, 0, 0, w, h); c.toBlob(b => finish(b), 'image/jpeg', 0.8) } catch { finish(null) }
    }
    v.onerror = () => { clearTimeout(timer); finish(null) }
    v.src = url
  })
}

/** Vorschauen, die in dieser Sitzung schon geladen wurden (liegen im Browser-Cache): sofort zeigen, ohne zu warten. */
const thumbSeen = new Set<string>()
/** Vorschauen vorab laden (etwa das Ziel eines Sprungs), damit die Kacheln gleich mit Bild erscheinen; fertig: alle da oder nach ms. */
export function preloadThumbs(urls: string[], ms: number): Promise<void> {
  const todo = urls.filter(u => !thumbSeen.has(u))
  if (!todo.length) return Promise.resolve()
  return new Promise(done => {
    let left = todo.length
    const one = () => { if (--left === 0) done() }
    for (const u of todo) { const img = new Image(); img.onload = () => { thumbSeen.add(u); one() }; img.onerror = one; img.src = u }
    setTimeout(done, ms)
  })
}

/** Zuletzt gescrollt (irgendwo auf der Seite) - während schnellem Scrollen bleiben neue Vorschauen kurz Platzhalter. */
let lastScroll = 0
// Von der Ansicht selbst gesetzte Scrollposition (Sprung, Ausgleich beim Nachladen): zählt nicht als Scrollen
let quiet: { el: HTMLElement; top: number } | null = null
if (typeof window !== 'undefined') window.addEventListener('scroll', e => {
  const q = quiet
  quiet = null
  if (q && e.target === q.el && Math.abs(q.el.scrollTop - q.top) < 1) return
  lastScroll = Date.now()
}, { capture: true, passive: true })
/** Scrollposition setzen, ohne dass die Vorschaubilder wie beim Scrollen von Hand erst auf eine Pause warten. */
export function quietScroll(el: HTMLElement, top: number) {
  el.scrollTop = top
  quiet = { el, top: el.scrollTop }
}

/**
 * Noch ladende Vorschaubilder. Die Module bleiben beim Wechsel eingehängt (nur ausgeblendet) - deren Abrufe liefen weiter
 * und das neue Modul wartete mit seinen Daten dahinter. Beim Wechsel werden ausgeblendete Bilder deshalb angehalten (src weg,
 * das bricht den Abruf ab) und beim Zurückkehren fortgesetzt.
 */
const loadingImgs = new Set<HTMLImageElement>()
const parkedImgs = new Map<HTMLImageElement, string>()
function sweepThumbs() {
  for (const [img, src] of parkedImgs) {
    if (!img.isConnected || img.getAttribute('src') !== null) parkedImgs.delete(img)
    else if (img.getClientRects().length) { parkedImgs.delete(img); img.setAttribute('src', src) }
  }
  for (const img of loadingImgs) {
    const src = img.getAttribute('src')
    if (src && img.isConnected && !img.complete && !img.getClientRects().length) { parkedImgs.set(img, src); img.removeAttribute('src') }
  }
}
if (typeof window !== 'undefined') window.addEventListener('hashchange', () => { setTimeout(sweepThumbs, 0); setTimeout(sweepThumbs, 120) })

/**
 * Erst laden, wenn gerade nicht gescrollt wird (wie Immich: beim schnellen Scrollen Platzhalter). Welche Bilder überhaupt
 * geladen werden, entscheidet der Browser selbst (loading="lazy": nur was in der Nähe des Sichtbaren liegt).
 * Nicht mit IntersectionObserver: der meldete in manchen Browsern nichts - dann blieben alle Kacheln leer.
 */
function useScrollIdle(skip: boolean, ms = 150) {
  const [go, setGo] = useState(skip)
  useEffect(() => {
    if (go) return
    let timer: ReturnType<typeof setTimeout> | undefined
    const tick = () => { if (Date.now() - lastScroll >= ms) setGo(true); else timer = setTimeout(tick, ms) }
    tick()
    return () => clearTimeout(timer)
  }, [go]) // eslint-disable-line react-hooks/exhaustive-deps
  return go
}

/** Vorschaubild mit Symbol als Ersatz; laedt erst, wenn es sichtbar wird und kurz stehen bleibt. */
export function Thumb({ item, source, size = 256, className, fallbackLarge, fit = 'cover' }: {
  item: Item; source: Source; size?: number; className?: string; fallbackLarge?: boolean; fit?: 'cover' | 'contain'
}) {
  const [n, setN] = useState(0)
  const url = source.thumb(item, size) + (n ? `&n=${n}` : '')
  // Schon einmal geladen (liegt im Browser-Cache): gleich zeigen statt erst das Symbol - sonst blinkt die Kachel bei jedem Neuzeichnen
  const [state, setState] = useState<'load' | 'ok' | 'none'>(hasThumb(item) ? thumbSeen.has(url) ? 'ok' : 'load' : 'none')
  const ref = useRef<HTMLImageElement>(null)
  const go = useScrollIdle(thumbSeen.has(url))
  useEffect(() => {
    const img = ref.current
    if (!go || state === 'ok' || !img) return
    loadingImgs.add(img)
    return () => { loadingImgs.delete(img); parkedImgs.delete(img) }
  }, [go, state])
  // Nur bei einer anderen Datei zuruecksetzen - nicht beim ersten Zeichnen: Aus dem Browser-Cache kommt das Bild oft
  // schon vor diesem Effekt an (onLoad gelaufen), ein Zuruecksetzen danach liesse fuer immer nur das Symbol stehen.
  const shown = useRef(item.path + '|' + item.etag)
  useEffect(() => {
    const key = item.path + '|' + item.etag
    if (shown.current === key) return
    shown.current = key
    const img = ref.current
    setState(!hasThumb(item) ? 'none' : img?.complete && img.naturalWidth > 0 ? 'ok' : 'load')
  }, [item.path, item.etag]) // eslint-disable-line react-hooks/exhaustive-deps
  const failed = async () => {
    // Video ohne Standbild: im Browser eins machen und dem Server geben
    if (kindOf(item) === 'video' && source.storeThumb && !tried.has(item.path + item.etag)) {
      tried.add(item.path + item.etag)
      await slot()
      try {
        const blob = await videoFrame(source.download(item, true))
        if (blob) { await source.storeThumb(item, blob); setN(x => x + 1); setState('load'); return }
      } finally { free() }
    }
    setState('none')
  }
  if (state === 'none') return <KindIcon item={item} large={fallbackLarge} />
  // Waehrend das Vorschaubild entsteht, steht das Symbol da - keine leere Flaeche
  return (
    <>
      {state === 'load' && <KindIcon item={item} large={fallbackLarge} />}
      {go && <img ref={ref} className={className} loading="lazy" alt="" draggable={false}
        style={state === 'ok' ? { objectFit: fit } : { objectFit: fit, position: 'absolute', width: 1, height: 1, opacity: 0.01 }}
        src={url} onLoad={() => { thumbSeen.add(url); setState('ok') }} onError={e => { if (!parkedImgs.has(e.currentTarget)) failed() }} />}
    </>
  )
}

/** Datum kurz und lesbar. */
export function fmtWhen(iso: string, locale: string): string {
  const d = new Date(iso)
  if (isNaN(d.getTime())) return ''
  const now = new Date()
  if (d.toDateString() === now.toDateString()) return d.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })
  return d.toLocaleDateString(locale, { day: '2-digit', month: '2-digit', year: d.getFullYear() === now.getFullYear() ? undefined : 'numeric' })
    + ' ' + d.toLocaleTimeString(locale, { hour: '2-digit', minute: '2-digit' })
}
