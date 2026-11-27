// Eine einzelne Datei (Beleg, Anhang, PDF vom Server) in dmOffice ansehen: derselbe Betrachter wie in den Dateien
// (PDF als Seitenbilder, Bilder, Audio, Video, Text) mit Dateinamen, Herunterladen und „In neuem Tab öffnen“.
// Was der Betrachter nicht zeigen kann, wird mit dem richtigen Dateinamen heruntergeladen - nicht in einem leeren Fenster geöffnet.
import { useCallback, useState } from 'react'
import { Portal } from '@fluentui/react-components'
import { apiUrl, ApiError } from '../api'
import { t } from '../i18n'
import { Item, Source, extOf, viewKind } from './common'
import { Viewer } from './Viewer'

export interface PreviewFile {
  /** Adresse zum Ansehen (inline) */
  url: string
  /** Adresse zum Herunterladen; ohne: dieselbe wie zum Ansehen */
  download?: string
  /** Dateiname mit Endung: Titel im Betrachter, Art der Vorschau und Name beim Herunterladen */
  name: string
  mime?: string | null
  size?: number
  /** Nur gezeigt (Chat „Nur zeigen“): ohne Herunterladen */
  noDownload?: boolean
}

/** Datei mit ihrem Namen herunterladen (Link mit download-Attribut statt eines neuen Fensters). */
export function downloadFile(url: string, name: string) {
  const a = document.createElement('a')
  a.href = apiUrl(url); a.download = name
  document.body.appendChild(a); a.click(); a.remove()
}

const itemOf = (f: PreviewFile): Item => ({ path: 'preview:' + f.url, name: f.name, isDir: false, size: f.size ?? 0, mime: f.mime ?? null, modified: '' })

/** Lässt sich die Datei in dmOffice ansehen (sonst Herunterladen)? */
export const canPreview = (f: { name: string; mime?: string | null }) => viewKind({ path: '', name: f.name, isDir: false, size: 0, mime: f.mime ?? null, modified: '' }) !== null

/** Betrachter für eine Datei; gezeigt wird er, solange file gesetzt ist. */
export function FilePreview({ file, onClose }: { file: PreviewFile; onClose: () => void }) {
  const source: Source = {
    download: (_, inline) => apiUrl(inline ? file.url : file.download ?? file.url),
    thumb: () => apiUrl(file.url),
  }
  // Am Ende der Seite mit den Farben des Designs: liegt über allem, auch wenn der Aufruf aus einem Dialog kommt
  return <Portal><Viewer items={[itemOf(file)]} index={0} source={source} onIndex={() => {}} onClose={onClose}
    onDownload={file.noDownload ? undefined : () => downloadFile(file.download ?? file.url, file.name)} /></Portal>
}

/** Endung zum Typ, wenn der Name keine hat (etwa „Beleg“): damit Vorschau und Download die Art kennen. */
const EXT_OF: Record<string, string> = {
  'application/pdf': '.pdf', 'image/png': '.png', 'image/jpeg': '.jpg', 'image/webp': '.webp', 'image/gif': '.gif',
  'audio/mpeg': '.mp3', 'video/mp4': '.mp4', 'text/plain': '.txt',
}

/** Dateiname aus Content-Disposition (filename* vor filename). */
export function dispositionName(header: string | null): string | null {
  if (!header) return null
  const star = /filename\*\s*=\s*(?:UTF-8|utf-8)''([^;]+)/.exec(header)
  if (star) { try { return decodeURIComponent(star[1].trim()) } catch { /* weiter mit filename */ } }
  const plain = /filename\s*=\s*"?([^";]+)"?/.exec(header)
  return plain ? plain[1].trim() : null
}

/**
 * Vorschau an einer Stelle einbauen: open(file) zeigt die Datei im Betrachter (oder lädt sie mit ihrem Namen herunter, wenn es
 * keine Vorschau gibt), element gehört in die Ausgabe der Komponente. Vorher wird die Datei abgerufen: fehlt sie, kommt die
 * Meldung des Servers über fail statt eines leeren Betrachters; Typ und Name ergänzt die Antwort des Servers.
 */
export function useFilePreview(fail: (e: unknown) => void) {
  const [file, setFile] = useState<PreviewFile | null>(null)
  const open = useCallback(async (f: PreviewFile) => {
    let shown = f
    const ctl = new AbortController()
    try {
      const r = await fetch(apiUrl(f.url), { credentials: 'same-origin', signal: ctl.signal })
      if (!r.ok) {
        const body = await r.json().catch(() => null) as { error?: string } | null
        throw new ApiError(body?.error || t('Die Datei lässt sich nicht öffnen.'), r.status, body)
      }
      const mime = (r.headers.get('Content-Type') ?? '').split(';')[0].trim().toLowerCase()
      let name = f.name || dispositionName(r.headers.get('Content-Disposition')) || t('Datei')
      if (!extOf(name) && EXT_OF[mime]) name += EXT_OF[mime]
      const length = Number(r.headers.get('Content-Length'))
      shown = { ...f, name, mime: f.mime ?? (mime && mime !== 'application/octet-stream' ? mime : null), size: f.size ?? (length > 0 ? length : undefined) }
      // Den Inhalt lädt der Betrachter selbst
      ctl.abort()
    } catch (e) {
      if ((e as Error).name !== 'AbortError') { fail(e); return }
    }
    // Ohne Vorschau herunterladen; nur Gezeigtes zeigt der Betrachter mit dem Hinweis, dass es keine Vorschau gibt
    if (canPreview(shown) || shown.noDownload) setFile(shown)
    else downloadFile(shown.download ?? shown.url, shown.name)
  }, [fail])
  const element = file ? <FilePreview file={file} onClose={() => setFile(null)} /> : null
  return { open, element }
}
