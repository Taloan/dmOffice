import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Option, Spinner, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { ArrowUp20Regular, Folder20Filled } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

/** Stand des Abgleichs nach dem Neuzuordnen (aus /sources). */
export interface RelinkInfo { at: string; pending: boolean; total: number; found: number; removedCount: number; error: string | null }

/** Zeile zum Abgleich am Ordner; leer, wenn es nichts zu sagen gibt. */
export function relinkLine(r: RelinkInfo | null | undefined): string {
  if (!r) return ''
  if (r.pending) return r.error ? t('Abgleich mit dem neuen Ort: {error}', { error: r.error }) : t('Abgleich mit dem neuen Ort steht aus')
  return r.removedCount > 0 ? t('Beim Abgleich mit dem neuen Ort entfernt: {n} (Liste unter Aktivität)', { n: r.removedCount.toLocaleString() }) : ''
}

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  browse: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, maxHeight: '220px', overflowY: 'auto', padding: '4px' },
  dir: { display: 'flex', alignItems: 'center', gap: '6px', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
})

/**
 * Ordner neu zuordnen (Galerie- oder Audiothek-Ordner): neuen Ort wählen wie beim Hinzufügen, Stichprobe prüfen, erst dann umstellen.
 * Der Ordner behält seine Kennung - Index, Markierungen und Vorschauen bleiben.
 */
export function RelinkDialog({ kind, source, name, mounts, onClose }: {
  kind: 'gallery' | 'audio'
  source: { id: string; mount: string; path: string }
  name: string
  mounts: { id: string; name: string }[]
  onClose: (changed: boolean) => void
}) {
  const s = useStyles()
  const { fail } = useApp()
  const [mount, setMount] = useState(source.mount)
  const [path, setPath] = useState(source.path)
  const [dirs, setDirs] = useState<string[] | null>(null)
  const [probe, setProbe] = useState<{ checked: number; found: number; total: number } | null>(null)
  const [busy, setBusy] = useState(false)
  const base = `/api/${kind}/sources/${source.id}/relink`
  useEffect(() => {
    setProbe(null)
    if (!mount) { setDirs(null); return }
    setDirs(null)
    // Nur die Antwort zum gerade gewählten Ordner zählt (eine langsamere frühere käme sonst danach an)
    let current = true
    api.get<{ items: { name: string; isDir: boolean }[] }>('/api/files/external/list?' + q({ mount, path }))
      .then(r => { if (current) setDirs(r.items.filter(i => i.isDir && !i.name.startsWith('.') && !i.name.startsWith('@')).map(i => i.name).sort((a, b) => a.localeCompare(b))) })
      .catch(e => { if (current) { setDirs([]); fail(e) } })
    return () => { current = false }
  }, [mount, path]) // eslint-disable-line react-hooks/exhaustive-deps
  const mountName = (id: string) => mounts.find(m => m.id === id)?.name ?? id
  const same = mount === source.mount && path === source.path
  // Keine einzige Datei am gewählten Ort: Umstellen nähme alles aus dem Index
  const none = !!probe && probe.checked > 0 && probe.found === 0
  const check = async () => {
    setBusy(true)
    try { setProbe(await api.post<{ checked: number; found: number; total: number }>(base + '/probe', { mount, path })) } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const apply = async () => {
    setBusy(true)
    try { await api.post(base, { mount, path }); onClose(true) } catch (e) { fail(e); setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(false) }}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{t('Ordner neu zuordnen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Text>{t('Bisher: {name}', { name })}</Text>
            <Text>{kind === 'gallery'
              ? t('Wähle den Ordner, in dem dieselben Fotos jetzt liegen. Bewertungen, Markierungen, Alben und Vorschauen bleiben erhalten.')
              : t('Wähle den Ordner, in dem dieselben Titel jetzt liegen. Tags, Hörverlauf und Wiedergabelisten bleiben erhalten.')}</Text>
            <Dropdown placeholder={t('Externen Ordner wählen')} value={mount ? mountName(mount) : ''} selectedOptions={mount ? [mount] : []}
              onOptionSelect={(_, d) => { setMount(d.optionValue ?? ''); setPath('') }}>
              {mounts.map(m => <Option key={m.id} value={m.id}>{m.name}</Option>)}
            </Dropdown>
            {mount && (
              <div className={s.browse}>
                <Text size={200} className={s.muted} block style={{ padding: '2px 6px' }}>{mountName(mount) + (path ? ' › ' + path.split('/').join(' › ') : '')}</Text>
                {path && <div className={s.dir} onClick={() => setPath(path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')}><ArrowUp20Regular />{t('Übergeordneter Ordner')}</div>}
                {dirs === null ? <Spinner size="tiny" /> : dirs.map(d => (
                  <div key={d} className={s.dir} onClick={() => setPath(path ? `${path}/${d}` : d)}><Folder20Filled color="#E8A33D" />{d}</div>
                ))}
              </div>
            )}
            {probe && <>
              <Text weight="semibold">{t('{found} von {n} Dateien gefunden', { found: probe.found.toLocaleString(), n: probe.checked.toLocaleString() })}</Text>
              {none ? <Text size={200} className={s.muted}>{t('Am gewählten Ort liegt keine dieser Dateien. Bitte einen anderen Ordner wählen.')}</Text>
              : <Text size={200} className={s.muted}>{kind === 'gallery'
                ? t('Nach dem Umstellen sucht dmOffice jede Datei am neuen Ort. Was dort fehlt, wird aus der Galerie entfernt.')
                : t('Nach dem Umstellen sucht dmOffice jeden Titel am neuen Ort. Was dort fehlt, wird aus der Audiothek entfernt.')}</Text>}
            </>}
          </DialogContent>
          <DialogActions>
            {busy && <Spinner size="tiny" />}
            {probe && !none
              ? <Button appearance="primary" disabled={busy} onClick={apply}>{t('Umstellen')}</Button>
              : <Button appearance="primary" disabled={busy || !mount || same || none} onClick={check}>{t('Prüfen')}</Button>}
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
