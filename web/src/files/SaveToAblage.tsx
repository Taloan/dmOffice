import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Switch, Text, tokens,
} from '@fluentui/react-components'
import { Suspense, lazy, useEffect, useState } from 'react'
import { Folder20Regular } from '@fluentui/react-icons'
import { api, featureOn } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import type { Item } from './common'

// ---- Was gerade gesichert wird: Zwischensymbol auf den Kacheln, bis die Kopie in der Ablage ist ----
let saving = new Set<string>()
const savingListeners = new Set<() => void>()
const doneListeners = new Set<() => void>()
let savingTimer: number | undefined
const emitSaving = () => savingListeners.forEach(f => f())
async function pollSaving() {
  window.clearTimeout(savingTimer)
  try {
    const r = await api.get<{ ids: string[] }>('/api/gallery/save/pending')
    const had = saving.size > 0
    saving = new Set(r.ids)
    emitSaving()
    if (saving.size > 0) savingTimer = window.setTimeout(pollSaving, 3000)
    else if (had) doneListeners.forEach(f => f())
  } catch {
    // Keine Verbindung: Zwischensymbol bleibt, später noch einmal fragen
    if (saving.size > 0) savingTimer = window.setTimeout(pollSaving, 10000)
  }
}
function markSaving(ids: string[], on: boolean) {
  saving = new Set(saving)
  ids.forEach(i => on ? saving.add(i) : saving.delete(i))
  emitSaving()
  if (on) { window.clearTimeout(savingTimer); savingTimer = window.setTimeout(pollSaving, 1500) }
}
/** Fotos, die gerade in die Ablage gesichert werden; onDone: wenn alles fertig ist (dann neu laden). */
export function useSaving(onDone?: () => void): Set<string> {
  const [, force] = useState(0)
  useEffect(() => {
    const f = () => force(n => n + 1)
    savingListeners.add(f)
    // Beim Öffnen einmal fragen: Vielleicht läuft schon etwas (von einem anderen Gerät gestartet)
    if (featureOn('ablagesave') && saving.size === 0) pollSaving()
    return () => { savingListeners.delete(f) }
  }, [])
  useEffect(() => {
    if (!onDone) return
    doneListeners.add(onDone)
    return () => { doneListeners.delete(onDone) }
  }, [onDone])
  return saving
}

// Ordnerauswahl wie beim Verschieben in Dateien - erst beim Öffnen geladen (Dateien ist ein eigenes Paket)
const FolderPicker = lazy(() => import('./FilesView').then(m => ({ default: m.FolderPicker })))

/** Vorgaben für „In Ablage sichern“ (Server: /api/gallery/save/settings). */
export interface SaveSettings { folder: string; order: 'source-month' | 'month' | 'day' | 'structure'; skipKnown: boolean; hintSeen: boolean }

export const ORDERS: SaveSettings['order'][] = ['source-month', 'month', 'day', 'structure']
export const orderLabel = (o: SaveSettings['order']) => o === 'month' ? t('Jahr/Monat') : o === 'day' ? t('Jahr/Monat/Tag')
  : o === 'structure' ? t('Herkunft und Ordner der Quelle übernehmen') : t('Herkunft/Jahr/Monat')

/** Beispielpfad zu den Vorgaben. */
export const examplePath = (s: Pick<SaveSettings, 'folder' | 'order'>) => {
  const root = (s.folder || 'Uploads').replace(/^\/+|\/+$/g, '')
  return s.order === 'month' ? `${root}/2026/11/IMG_1234.jpg` : s.order === 'day' ? `${root}/2026/11/09/IMG_1234.jpg`
    : s.order === 'structure' ? `${root}/Kamera/Urlaub/IMG_1234.jpg` : `${root}/Kamera/2026/11/IMG_1234.jpg`
}

/** Liegt nur in einem Galerie-Ordner, noch nicht in der Ablage (leere Wolke). */
export const onlyInFolder = (i: Item) => !i.isDir && i.path.startsWith('ext:')

/**
 * „In Ablage sichern“ für markierte Fotos und Videos aus Galerie-Ordnern: Beim ersten Mal ein Hinweis, wohin sie kommen; danach
 * ohne Nachfragen. Kopiert wird auf dem Server im Hintergrund - der Fortschritt steht unter Aktivität.
 */
export function useSaveToAblage() {
  const { fail, notify } = useApp()
  const [ask, setAsk] = useState<{ items: Item[]; settings: SaveSettings } | null>(null)
  const available = featureOn('ablagesave')
  const start = async (items: Item[]) => {
    const ids = items.map(i => i.path)
    // Zwischensymbol sofort, nicht erst nach der Antwort
    markSaving(ids, true)
    try {
      const r = await api.post<{ count: number }>('/api/gallery/save', { ids })
      notify(r.count === 1 ? t('Das Foto wird in die Ablage gesichert. Den Fortschritt zeigt die Aktivität.')
        : t('{n} Fotos werden in die Ablage gesichert. Den Fortschritt zeigt die Aktivität.', { n: r.count }))
    } catch (e) { markSaving(ids, false); fail(e) }
  }
  const save = async (list: Item[]) => {
    const items = list.filter(onlyInFolder)
    if (!items.length) { notify(t('Die ausgewählten Fotos liegen schon in der Ablage.')); return }
    const ids = items.map(i => i.path)
    markSaving(ids, true)
    try {
      const s = await api.get<SaveSettings>('/api/gallery/save/settings')
      // Beim ersten Mal erst der Hinweis, wohin sie kommen - bis dahin ohne Zwischensymbol
      if (!s.hintSeen) { markSaving(ids, false); setAsk({ items, settings: s }); return }
      await start(items)
    } catch (e) { markSaving(ids, false); fail(e) }
  }
  const dialog = ask && <Dialog open onOpenChange={(_, d) => { if (!d.open) setAsk(null) }}>
    <DialogSurface style={{ maxWidth: 520 }}>
      <DialogBody>
        <DialogTitle>{t('In Ablage sichern')}</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <Text>{t('Die Fotos werden in deine Ablage kopiert, etwa nach:')}</Text>
          <Text font="monospace" size={200} style={{ color: tokens.colorNeutralForeground2 }}>{examplePath(ask.settings)}</Text>
          <Text size={200}>{t('Die Originale im externen Galerie-Ordner bleiben unverändert. Was schon in der Ablage liegt, wird übersprungen.')}</Text>
          <Text size={200}>{t('Zielordner und Ordnung lassen sich unter Einstellungen › Galerie › In Ablage sichern ändern.')}</Text>
        </DialogContent>
        <DialogActions>
          <Button onClick={() => { setAsk(null); location.hash = '#/settings/gallery-save' }}>{t('Ändern')}</Button>
          <Button appearance="primary" onClick={async () => {
            const a = ask
            setAsk(null)
            try { await api.put('/api/gallery/save/settings', { ...a.settings, hintSeen: true }) } catch { /* Hinweis kommt dann eben noch einmal */ }
            await start(a.items)
          }}>{t('Sichern')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
  return { available, save, dialog }
}

/** Einstellungen › Galerie › In Ablage sichern. */
export function SaveToAblageSettings() {
  const { fail, notify } = useApp()
  const [s, setS] = useState<SaveSettings | null>(null)
  const [picking, setPicking] = useState(false)
  useEffect(() => { api.get<SaveSettings>('/api/gallery/save/settings').then(setS).catch(fail) }, [fail])
  if (!s) return null
  const store = async (next: SaveSettings) => {
    setS(next)
    try { setS(await api.put<SaveSettings>('/api/gallery/save/settings', next)); notify(t('Gespeichert')) } catch (e) { fail(e) }
  }
  return <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 560 }}>
    <Text>{t('Fotos und Videos aus externen Galerie-Ordnern lassen sich in der Galerie markieren und mit „In Ablage sichern“ in deine Ablage kopieren. Die Originale bleiben unverändert.')}</Text>
    <Field label={t('Zielordner')} hint={t('In deiner Ablage')}>
      <div style={{ display: 'flex', gap: 6, maxWidth: 360 }}>
        <Input value={s.folder} onChange={(_, d) => setS({ ...s, folder: d.value })} onBlur={() => store(s)} style={{ flex: 1 }} />
        <Button icon={<Folder20Regular />} onClick={() => setPicking(true)} aria-label={t('Ordner wählen')} title={t('Ordner wählen')} />
      </div>
    </Field>
    {picking && <Suspense fallback={null}>
      <FolderPicker title={t('Zielordner')} exclude={[]} start={s.folder.replace(/^\/+|\/+$/g, '')} onClose={() => setPicking(false)}
        onPick={path => {
          setPicking(false)
          if (path.startsWith('tresor:')) { fail(new Error(t('Der Tresor kann nicht Zielordner sein.'))); return }
          store({ ...s, folder: path.replace(/^\/+|\/+$/g, '') })
        }} />
    </Suspense>}
    <Field label={t('Ordnung darunter')}>
      <Dropdown value={orderLabel(s.order)} selectedOptions={[s.order]} onOptionSelect={(_, d) => d.optionValue && store({ ...s, order: d.optionValue as SaveSettings['order'] })} style={{ maxWidth: 320 }}>
        {ORDERS.map(o => <Option key={o} value={o}>{orderLabel(o)}</Option>)}
      </Dropdown>
    </Field>
    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Beispiel: {path}', { path: examplePath(s) })}</Text>
    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Herkunft ist der Name des externen Galerie-Ordners. Jahr und Monat kommen aus dem Aufnahmedatum, fehlt es, aus dem Dateidatum.')}</Text>
    <Switch checked={s.skipKnown} label={t('Überspringen, was schon in der Ablage liegt')} onChange={(_, d) => store({ ...s, skipKnown: d.checked })} />
    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Erkannt werden gleiche Dateien und dasselbe Bild mit anderen Bildangaben, etwa aus Lightroom.')}</Text>
  </div>
}
