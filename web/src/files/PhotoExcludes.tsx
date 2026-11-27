import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Radio, RadioGroup, Text, tokens,
} from '@fluentui/react-components'
import { Add20Regular, Dismiss16Regular } from '@fluentui/react-icons'
import { lazy, Suspense, useEffect, useState } from 'react'
import { api, featureOn } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

/** Ordner, deren Bilder die Ansicht Fotos nicht zeigt (Musik-Cover, App-Daten ...) - gilt auch in der App. */
export const loadPhotoExcludes = () => api.get<string[]>('/api/files/photo-excludes')
const savePhotoExcludes = (folders: string[]) => api.put<string[]>('/api/files/photo-excludes', { folders })
const FolderPicker = lazy(() => import('./FilesView').then(m => ({ default: m.FolderPicker })))

/** Ordner dazunehmen (Kennung galexclfolder): Einträge darunter fallen weg, der neue Ordner schließt sie mit ein. */
const withFolder = (list: string[], folder: string) => {
  const low = folder.toLowerCase()
  return featureOn('galexclfolder') ? [...list.filter(x => !x.toLowerCase().startsWith(low + '/')), folder] : [...list, folder]
}

/**
 * Bild gewaehlt -> Ordner ausschliessen: zur Wahl stehen der Ordner des Bildes und alle darueber, damit man etwa
 * bei Android/data/music/interpreten/a/a-ha/album/cover.jpg gleich den ganzen Ordner music ausschliessen kann.
 */
export function ExcludeFolderDialog({ path, onClose }: { path: string; onClose: (changed: boolean) => void }) {
  const { fail, notify } = useApp()
  const parts = path.split('/').slice(0, -1)
  const chain = parts.map((_, i) => parts.slice(0, i + 1).join('/'))
  const [pick, setPick] = useState(chain[chain.length - 1] ?? '')
  const save = async () => {
    try {
      const list = await loadPhotoExcludes()
      await savePhotoExcludes(withFolder(list, pick))
      notify(t('Aus der Galerie ausgeschlossen'), '/' + pick)
      onClose(true)
    } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{t('Ordner aus der Galerie ausschließen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {chain.length === 0 ? <Text>{t('Das Bild liegt direkt in „Meine Dateien“ – den ganzen Bereich auszuschließen geht nicht.')}</Text> : <>
              <Text>{t('Bilder in diesem Ordner und allen Unterordnern erscheinen nicht mehr in der Galerie. Die Dateien selbst bleiben, wo sie sind.')}</Text>
              <RadioGroup value={pick} onChange={(_, d) => setPick(d.value)}>
                {chain.map((c, i) => (
                  <Radio key={c} value={c} label={<span style={{ paddingLeft: i * 12 }}>{c.split('/').pop()}{i === chain.length - 1
                    ? <span style={{ color: tokens.colorNeutralForeground3 }}> · {t('Ordner des Bildes')}</span> : null}</span>} />
                ))}
              </RadioGroup>
              <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>/{pick}</Text>
            </>}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!pick} onClick={save}>{t('Ausschließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Liste der ausgeschlossenen Ordner - einzelne wieder aufnehmen. */
export function PhotoExcludesDialog({ onClose }: { onClose: (changed: boolean) => void }) {
  const { fail } = useApp()
  const [list, setList] = useState<string[] | null>(null)
  const [changed, setChanged] = useState(false)
  useEffect(() => { loadPhotoExcludes().then(setList).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const [picking, setPicking] = useState(false)
  const remove = async (p: string) => {
    try { setList(await savePhotoExcludes((list ?? []).filter(x => x !== p))); setChanged(true) } catch (e) { fail(e) }
  }
  const add = async (folder: string) => {
    setPicking(false)
    const f = folder.replace(/^\/+|\/+$/g, '')
    // Nur Ordner der Ablage: Tresor und externe Ordner zeigt die Galerie nicht über diese Liste
    if (!f || /^(tresor|xmnt):/.test(f)) return
    try { setList(await savePhotoExcludes(withFolder(list ?? [], f))); setChanged(true) } catch (e) { fail(e) }
  }
  const extra = featureOn('galexclfolder')
  return (
    <>
    <Dialog open={!picking} onOpenChange={(_, d) => !d.open && onClose(changed)}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{t('Aus der Galerie ausgeschlossen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
            {list?.length === 0 && <Text>{t(extra ? 'Noch keine. „Ordner hinzufügen …“ oder Rechtsklick auf ein Bild → „Ordner aus der Galerie ausschließen …“.' : 'Noch keine. Rechtsklick auf ein Bild → „Ordner aus der Galerie ausschließen …“.')}</Text>}
            {extra && !!list?.length && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Jeder Ordner gilt mit allen Unterordnern.')}</Text>}
            {list?.map(p => (
              <div key={p} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <Text style={{ flex: 1, wordBreak: 'break-all' }}>/{p}</Text>
                <Button appearance="subtle" icon={<Dismiss16Regular />} onClick={() => remove(p)} aria-label={t('Wieder aufnehmen')} title={t('Wieder aufnehmen')} />
              </div>
            ))}
          </DialogContent>
          <DialogActions>
            {extra && <Button icon={<Add20Regular />} onClick={() => setPicking(true)} style={{ marginRight: 'auto', whiteSpace: 'nowrap', flexShrink: 0 }}>{t('Ordner hinzufügen …')}</Button>}
            <Button appearance="primary" onClick={() => onClose(changed)}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
    {picking && <Suspense fallback={null}><FolderPicker title={t('Ordner aus der Galerie ausschließen')} exclude={[]} onPick={add} onClose={() => setPicking(false)} /></Suspense>}
    </>
  )
}
