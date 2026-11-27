import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { ArrowUndo20Regular, History20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { moneyText } from './money'
import { askConfirm } from '../shell/context'
import { areaQ, Doc, Entry, fullName } from './model'

/** Angaben eines Dokuments zu einem Zeitpunkt (Server: DmsHistory.Meta) */
interface Meta { title: string; docDate: string | null; correspondent: string | null; doctype: string | null; tags: string[]; amount: number | null }
interface Version { id: number; at: string; change: string | null; before: Meta; after: Meta }

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '10px', maxHeight: '60vh', overflowY: 'auto' },
  item: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}` },
  head: { display: 'flex', alignItems: 'center', gap: '8px', justifyContent: 'space-between' },
  muted: { color: tokens.colorNeutralForeground3 },
  old: { color: tokens.colorNeutralForeground3, textDecoration: 'line-through' },
})

/** Was sich zwischen zwei Ständen geändert hat: je Angabe alter und neuer Wert, lesbar */
function diff(a: Meta, b: Meta, entries: Entry[]): { label: string; from: string; to: string }[] {
  const name = (id: string | null) => (id ? fullName(entries, id) || t('(gelöscht)') : '–')
  const day = (d: string | null) => (d ? new Date(d + 'T00:00').toLocaleDateString() : '–')
  const tags = (l: string[]) => (l.length ? l.map(id => name(id)).join(', ') : '–')
  const out: { label: string; from: string; to: string }[] = []
  if (a.title !== b.title) out.push({ label: t('Titel'), from: a.title || '–', to: b.title || '–' })
  if (a.docDate !== b.docDate) out.push({ label: t('Dokumentdatum'), from: day(a.docDate), to: day(b.docDate) })
  if (a.correspondent !== b.correspondent) out.push({ label: t('Korrespondent'), from: name(a.correspondent), to: name(b.correspondent) })
  if (a.doctype !== b.doctype) out.push({ label: t('Dokumenttyp'), from: name(a.doctype), to: name(b.doctype) })
  if (a.tags.join('|') !== b.tags.join('|')) out.push({ label: t('Tags'), from: tags(a.tags), to: tags(b.tags) })
  if (a.amount !== b.amount) out.push({ label: t('Betrag'), from: a.amount == null ? '–' : moneyText(a.amount) + ' €', to: b.amount == null ? '–' : moneyText(b.amount) + ' €' })
  return out
}

/**
 * Frühere Versionen der Angaben (Kennung dmshistory): jede Änderung von Hand mit altem und neuem Wert; „Wiederherstellen“ setzt
 * alle Angaben auf den Stand vor dieser Änderung (als neue Änderung - auch das lässt sich wieder zurücknehmen).
 */
export function DmsVersions({ doc, entries, tresor, onRestored }: { doc: Doc; entries: Entry[]; tresor: boolean; onRestored: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [open, setOpen] = useState(false)
  const [list, setList] = useState<Version[] | null>(null)
  const load = () => api.get<Version[]>(`/api/dms/docs/${doc.id}/history` + areaQ(tresor)).then(setList).catch(e => { setList([]); fail(e) })
  useEffect(() => { if (open) { setList(null); load() } }, [open, doc.id]) // eslint-disable-line react-hooks/exhaustive-deps
  const restore = async (v: Version) => {
    if (!await askConfirm('', t('Alle Angaben auf den Stand vor dieser Änderung setzen? Der jetzige Stand bleibt als Version erhalten.'), t('Wiederherstellen'))) return
    try {
      await api.post(`/api/dms/docs/${doc.id}/history/${v.id}/restore` + areaQ(tresor))
      notify(t('Version wiederhergestellt'))
      onRestored(); load()
    } catch (e) { fail(e) }
  }
  return (
    <>
      <div><Button size="small" appearance="subtle" icon={<History20Regular />} onClick={() => setOpen(true)}>{t('Frühere Versionen')}</Button></div>
      {open && <Dialog open onOpenChange={(_, d) => { if (!d.open) setOpen(false) }}>
        <DialogSurface style={{ maxWidth: 560 }}>
          <DialogBody>
            <DialogTitle>{t('Frühere Versionen')}</DialogTitle>
            <DialogContent>
              {list === null ? <Spinner size="small" />
                : list.length === 0 ? <Text>{t('Die Angaben dieses Dokuments wurden noch nicht von Hand geändert.')}</Text>
                : <div className={s.list}>
                  {list.map(v => (
                    <div key={v.id} className={s.item}>
                      <div className={s.head}>
                        <Text weight="semibold">{new Date(v.at).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}</Text>
                        <Button size="small" icon={<ArrowUndo20Regular />} onClick={() => restore(v)}>{t('Wiederherstellen')}</Button>
                      </div>
                      {diff(v.before, v.after, entries).map(d => (
                        <Text key={d.label} size={200}>{d.label}: <span className={s.old}>{d.from}</span> → {d.to}</Text>
                      ))}
                    </div>
                  ))}
                </div>}
              {list && list.length > 0 && <Text size={200} className={s.muted} style={{ display: 'block', marginTop: 8 }}>
                {t('„Wiederherstellen“ setzt alle Angaben auf den Stand vor der jeweiligen Änderung.')}</Text>}
            </DialogContent>
            <DialogActions><Button onClick={() => setOpen(false)}>{t('Schließen')}</Button></DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>}
    </>
  )
}
