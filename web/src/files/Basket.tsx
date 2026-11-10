import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Input, Popover, PopoverSurface, PopoverTrigger, Text, tokens,
} from '@fluentui/react-components'
import { ArrowDownload20Regular, Cart20Regular, Dismiss16Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { t } from '../i18n'
import { isCmv, isTresor } from '../tresor/tresor'
import { Item, KindIcon } from './common'
import { RibbonButton } from '../shell/Ribbon'

/**
 * Downloadkorb (docs/tresor-cryptomator-konzept.md, 9.2): Dateien und Ordner aus verschiedenen Ordnern sammeln - in „Meine Dateien“,
 * im Tresor und in eigenen Cryptomator-Tresoren - und als ZIP herunterladen. Bleibt je Sitzung, bis heruntergeladen oder geleert.
 */
const KEY = 'dm.basket'
const load = (): Item[] => { try { return JSON.parse(sessionStorage.getItem(KEY) ?? '[]') } catch { return [] } }
const listeners = new Set<(b: Item[]) => void>()
let current = load()
const set = (b: Item[]) => {
  current = b
  try { sessionStorage.setItem(KEY, JSON.stringify(b)) } catch { /* ohne Speicher nur bis zum Neuladen */ }
  listeners.forEach(l => l(b))
}

export const basket = {
  add: (list: Item[]) => set([...current, ...list.filter(i => !current.some(x => x.path === i.path))]),
  remove: (path: string) => set(current.filter(i => i.path !== path)),
  clear: () => set([]),
}

export function useBasket() {
  const [b, setB] = useState(current)
  useEffect(() => { listeners.add(setB); return () => { listeners.delete(setB) } }, [])
  return b
}

/** ZIP über ein Formular (der Browser speichert die Datei selbst). */
function submitZip(paths: string[], name: string) {
  const form = document.createElement('form')
  form.method = 'POST'; form.action = '/api/files/zip'; form.style.display = 'none'
  const add = (k: string, v: string) => { const inp = document.createElement('input'); inp.type = 'hidden'; inp.name = k; inp.value = v; form.appendChild(inp) }
  paths.forEach(p => add('paths', p))
  add('name', name)
  document.body.appendChild(form); form.submit(); form.remove()
}

/** Symbol mit Anzahl; öffnet den Korb: Inhalt, Größe, einzelne entfernen, „Als ZIP herunterladen“, „Leeren“. */
export function BasketButton() {
  const items = useBasket()
  const [limit, setLimit] = useState(0)
  // Ordner: Größe samt Inhalt vom Server
  const [dirSize, setDirSize] = useState<Record<string, number>>({})
  useEffect(() => { if (items.length) api.get<{ limit: number }>('/api/files/zip-limit').then(r => setLimit(r.limit)).catch(() => {}) }, [items.length])
  useEffect(() => {
    items.filter(i => i.isDir && dirSize[i.path] == null).forEach(i =>
      api.get<{ size: number }>(`/api/files/info?path=${encodeURIComponent(i.path)}`).then(r => setDirSize(d => ({ ...d, [i.path]: r.size }))).catch(() => {}))
  }, [items]) // eslint-disable-line react-hooks/exhaustive-deps
  if (!items.length) return null
  const sizeOf = (i: Item) => i.isDir ? dirSize[i.path] ?? 0 : i.size
  const size = items.reduce((n, i) => n + sizeOf(i), 0)
  const tooBig = limit > 0 && size > limit
  const download = () => {
    // Je Bereich ein ZIP: Tresor, eigene Cryptomator-Tresore, „Meine Dateien“
    const groups = [items.filter(i => isTresor(i.path)), items.filter(i => isCmv(i.path)), items.filter(i => !isTresor(i.path) && !isCmv(i.path))].filter(g => g.length)
    groups.forEach((g, n) => setTimeout(() => submitZip(g.map(i => i.path), groups.length > 1 ? `${t('Downloadkorb')} ${n + 1}` : t('Downloadkorb')), n * 800))
    basket.clear()
  }
  return (
    <Popover positioning="below-end" withArrow>
      <PopoverTrigger disableButtonEnhancement>
        <RibbonButton icon={<Cart20Regular />} label={`${t('Downloadkorb')} (${items.length})`} />
      </PopoverTrigger>
      <PopoverSurface style={{ width: 340, display: 'flex', flexDirection: 'column', gap: 8 }}>
        <Text weight="semibold">{t('Downloadkorb')}</Text>
        <div style={{ maxHeight: 280, overflow: 'auto', display: 'flex', flexDirection: 'column', gap: 2 }}>
          {items.map(i => (
            <div key={i.path} style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <KindIcon item={i} />
              <Text truncate wrap={false} style={{ flex: 1, minWidth: 0 }} title={i.path}>{i.name}</Text>
              <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{i.isDir && dirSize[i.path] == null ? t('Ordner') : fmtSize(sizeOf(i))}</Text>
              <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} title={t('Entfernen')} onClick={() => basket.remove(i.path)} />
            </div>
          ))}
        </div>
        <Text size={200}>{t('Zusammen {size}', { size: fmtSize(size) })}</Text>
        {tooBig && <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{t('Größer als die Grenze für ZIP-Downloads ({limit}).', { limit: fmtSize(limit) })}</Text>}
        <div style={{ display: 'flex', gap: 8 }}>
          <Button appearance="primary" icon={<ArrowDownload20Regular />} disabled={tooBig} onClick={download}>{t('Als ZIP herunterladen')}</Button>
          <Button onClick={basket.clear}>{t('Leeren')}</Button>
        </div>
      </PopoverSurface>
    </Popover>
  )
}

/**
 * „Tresor herunterladen“ (9.1): der System-Tresor verschlüsselt im Cryptomator-Format, im Web als ZIP ohne Kompression.
 * Vorher Größe zeigen und mit dem Tresor-Passwort bestätigen; über der Grenze statt dessen der Hinweis auf die Datensicherung.
 */
export function TresorDownloadButton() {
  const [open, setOpen] = useState(false)
  const [info, setInfo] = useState<{ available: boolean; size: number; limit: number } | null>(null)
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => { if (open) api.get<{ available: boolean; size: number; limit: number }>('/api/tresor/download').then(setInfo).catch(() => setInfo(null)) }, [open])
  const tooBig = !!info && info.limit > 0 && info.size > info.limit
  const go = async () => {
    setBusy(true); setError(null)
    try {
      const { token } = await api.post<{ token: string }>('/api/tresor/download/check', { password })
      // Einmal-Token: der Browser lädt die Datei selbst
      const link = document.createElement('a')
      link.href = `/api/tresor/download/zip?token=${encodeURIComponent(token)}`; link.download = 'vault.zip'
      document.body.appendChild(link); link.click(); link.remove()
      setOpen(false)
    } catch (e) { setError(e instanceof Error ? e.message : String(e)) }
    finally { setBusy(false); setPassword('') }
  }
  return <>
    <RibbonButton icon={<ArrowDownload20Regular />} label={t('Tresor herunterladen')} onClick={() => setOpen(true)} />
    <Dialog open={open} onOpenChange={(_, d) => setOpen(d.open)}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <DialogBody>
          <DialogTitle>{t('Tresor herunterladen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <Text>{t('Als Cryptomator-Tresor (verschlüsselt). Im Web als ZIP-Datei.')}</Text>
            {info && <Text size={200}>{t('Größe: {size}', { size: fmtSize(info.size) })}</Text>}
            {info && !info.available && <Text>{t('Der Tresor liegt noch nicht im Cryptomator-Format vor – einmal mit dem Tresor-Passwort öffnen.')}</Text>}
            {tooBig && <Text>{t('Größer als die Grenze für ZIP-Downloads ({limit}). Den Tresor bitte über den Datenexport mit WebDAV-Ziel exportieren.', { limit: fmtSize(info!.limit) })}</Text>}
            {info?.available && !tooBig && <Input type="password" autoFocus value={password} placeholder={t('Tresor-Passwort')} onChange={(_, d) => setPassword(d.value)}
              onKeyDown={e => { if (e.key === 'Enter' && password) go() }} />}
            {error && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => setOpen(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!info?.available || tooBig || !password || busy} onClick={go}>{t('Herunterladen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  </>
}
