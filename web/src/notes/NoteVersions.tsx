import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Spinner, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { ArrowUndo20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { askConfirm } from '../shell/context'
import { useIsMobile } from '../shell/mobile'

/** Eine frühere Version (Server: NoteVersions.Entry) */
interface Entry { id: number; at: string; source: string | null; device: string | null; author: string | null; title: string; length: number }
interface Full extends Omit<Entry, 'length'> { blocks: string[] }

const useStyles = makeStyles({
  body: { display: 'grid', gridTemplateColumns: '240px 1fr', gap: '12px', height: '60vh', minHeight: 0 },
  bodyMobile: { gridTemplateColumns: '1fr', gridTemplateRows: 'minmax(120px, 35%) 1fr' },
  list: { display: 'flex', flexDirection: 'column', gap: '4px', overflowY: 'auto', minHeight: 0 },
  item: {
    display: 'flex', flexDirection: 'column', gap: '2px', padding: '6px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    border: `1px solid ${tokens.colorNeutralStroke2}`, background: 'none', textAlign: 'left', color: 'inherit', font: 'inherit',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  active: { backgroundColor: tokens.colorNeutralBackground1Selected, border: `1px solid ${tokens.colorBrandStroke1}` },
  muted: { color: tokens.colorNeutralForeground3 },
  preview: {
    overflowY: 'auto', minHeight: 0, padding: '10px 14px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`,
    '& img': { maxWidth: '100%' },
  },
})

/** Woher ein Stand stammt: Person, Browser bzw. Gerät (notizi) oder dmOffice selbst */
function sourceText(v: Entry | Full) {
  const parts = [v.author, v.source === 'notizi' ? (v.device ? 'notizi · ' + v.device : 'notizi') : v.source === 'dmoffice' ? 'dmOffice' : v.device]
  return parts.filter(Boolean).join(' · ')
}

/**
 * Frühere Versionen einer Notiz (Kennung notesversions), wie bei Dateien und im DMS: Liste mit Zeitpunkt und Quelle, Vorschau,
 * „Wiederherstellen“ (der jetzige Inhalt bleibt dabei als Version erhalten). base: /api/notes oder der Weg der Tresor-Datenbank.
 */
export function NoteVersionsDialog({ base, id, onClose, onRestored }: { base: string; id: number; onClose: () => void; onRestored: () => void }) {
  const s = useStyles()
  const mobile = useIsMobile()
  const { fail, notify } = useApp()
  const [list, setList] = useState<Entry[] | null>(null)
  const [pick, setPick] = useState<number | null>(null)
  const [full, setFull] = useState<Full | null>(null)
  const [busy, setBusy] = useState(false)
  const load = () => api.get<Entry[]>(`${base}/${id}/versions`)
    .then(l => { setList(l); setPick(p => (p != null && l.some(v => v.id === p) ? p : l[0]?.id ?? null)) })
    .catch(e => { setList([]); fail(e) })
  useEffect(() => { load() }, [base, id]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    setFull(null)
    if (pick == null) return
    let live = true
    api.get<Full>(`${base}/${id}/versions/${pick}`).then(v => { if (live) setFull(v) }).catch(fail)
    return () => { live = false }
  }, [base, id, pick, fail])
  const when = (at: string) => new Date(at).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
  const restore = async () => {
    if (pick == null || !await askConfirm('', t('Diese Version wiederherstellen? Der jetzige Inhalt bleibt als Version erhalten.'), t('Wiederherstellen'))) return
    setBusy(true)
    try {
      await api.post(`${base}/${id}/versions/${pick}/restore`)
      notify(t('Version wiederhergestellt'))
      onRestored()
      onClose()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  // Bilder der Notiz über den Server zeigen (im Inhalt steht blob:<Name>)
  const html = full ? full.blocks.join('').replace(/(<img[^>]*?\ssrc=["'])blob:([^"']+)/gi, (_, a, n) => `${a}${base}/blob/${encodeURIComponent(n)}`) : ''
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 900, width: '100%' }}>
        <DialogBody>
          <DialogTitle>{t('Frühere Versionen')}</DialogTitle>
          <DialogContent>
            {list === null ? <Spinner size="small" />
              : list.length === 0 ? <Text>{t('Noch keine. Beim Bearbeiten bleibt der vorige Inhalt als Version erhalten – höchstens alle 10 Minuten, bei größeren Änderungen sofort.')}</Text>
              : <div className={mergeClasses(s.body, mobile && s.bodyMobile)}>
                <div className={s.list} role="listbox" aria-label={t('Frühere Versionen')} onKeyDown={e => {
                  // Pfeiltasten wählen die vorige/nächste Version
                  if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return
                  e.preventDefault()
                  const i = Math.max(0, Math.min(list.length - 1, list.findIndex(v => v.id === pick) + (e.key === 'ArrowDown' ? 1 : -1)))
                  setPick(list[i].id)
                  ;(e.currentTarget.children[i] as HTMLElement | undefined)?.focus()
                }}>
                  {list.map(v => (
                    <button key={v.id} type="button" role="option" aria-selected={pick === v.id} className={mergeClasses(s.item, pick === v.id && s.active)}
                      onClick={() => setPick(v.id)}>
                      <Text weight="semibold">{when(v.at)}</Text>
                      {sourceText(v) && <Text size={200} className={s.muted}>{sourceText(v)}</Text>}
                      <Text size={200} className={s.muted} truncate wrap={false}>{v.title || t('(ohne Titel)')}</Text>
                    </button>
                  ))}
                </div>
                <div className={s.preview}>
                  {full ? <div dangerouslySetInnerHTML={{ __html: html }} /> : <Spinner size="small" />}
                </div>
              </div>}
          </DialogContent>
          <DialogActions>
            {list && list.length > 0 && <Button appearance="primary" icon={<ArrowUndo20Regular />} disabled={pick == null || busy} onClick={restore}>{t('Wiederherstellen')}</Button>}
            <Button onClick={onClose}>{t('Schließen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
