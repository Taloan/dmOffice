// Abgleich beim Ansehen (GalleryFolders.cs): das gezeigte Foto und die sichtbaren Kacheln aus Galerie-Ordnern. Je Foto höchstens alle
// paar Minuten, Fehler bleiben still - das Anzeigen wartet nie darauf.
import { api } from '../api'
import { Meta } from './PhotoFilter'

const EXT = 'ext:'
/** So lange gilt ein Foto oder eine Kachel in dieser Sitzung als abgeglichen. */
const AGAIN_MS = 5 * 60_000
/** So viele Kacheln je Aufruf (Obergrenze des Servers). */
const TILES_MAX = 40

export interface Fresh {
  key: string; changed: boolean; fileChanged: boolean; gone: boolean; etag: string | null
  size: number | null; modified: string | null; meta: Meta | null
}

const shown = new Map<string, number>()
const tiles = new Map<string, number>()
const due = (seen: Map<string, number>, path: string) => Date.now() - (seen.get(path) ?? -AGAIN_MS) >= AGAIN_MS

/** Das gezeigte Foto; null, wenn es gerade dran war oder der Abruf nicht klappte. */
export async function freshenPhoto(path: string): Promise<Fresh | null> {
  if (!due(shown, path)) return null
  shown.set(path, Date.now()); tiles.set(path, Date.now())
  try { return await api.post<Fresh>('/api/gallery/external/refresh', { id: path.slice(EXT.length) }) }
  catch { return null }
}

/** Sichtbare Kacheln; null, wenn keine dran ist oder der Abruf nicht klappte. open: kamen nicht dran, gleich noch einmal. */
export async function freshenTiles(paths: string[]): Promise<{ items: Fresh[]; open: boolean } | null> {
  const list = paths.filter(p => due(tiles, p)).slice(0, TILES_MAX)
  if (!list.length) return null
  for (const p of list) tiles.set(p, Date.now())
  try {
    const r = await api.post<{ items: Fresh[]; open: string[] }>('/api/gallery/external/visible', { ids: list.map(p => p.slice(EXT.length)) })
    for (const id of r.open) tiles.delete(EXT + id)
    return { items: r.items, open: r.open.length > 0 }
  } catch { return null }
}
