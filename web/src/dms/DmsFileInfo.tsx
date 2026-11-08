// DMS in den Details der Dateiablage: Angaben eines Dokuments (nur lesen), Sprung nach DMS. Fragt nur /api/dms/of.
import { Button, Divider, makeStyles, tokens } from '@fluentui/react-components'
import { DocumentBulletList20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { t } from '../i18n'
import { Doc, Entry, fullName } from './model'

const useStyles = makeStyles({
  section: { fontWeight: 600 },
  muted: { color: tokens.colorNeutralForeground3 },
})

/** Angaben vom DMS zu einer Datei - nichts, wenn die Datei kein Dokument vom DMS ist. */
export function DmsFileInfo({ fileId }: { fileId: number }) {
  const s = useStyles()
  const [doc, setDoc] = useState<Doc | null>(null)
  const [entries, setEntries] = useState<Entry[]>([])
  useEffect(() => {
    setDoc(null)
    api.get<Doc | null>('/api/dms/of?' + q({ fileId })).then(d => {
      setDoc(d)
      if (d) api.get<Entry[]>('/api/dms/lists').then(setEntries).catch(() => {})
    }).catch(() => {})
  }, [fileId])
  if (!doc) return null
  const tags = doc.tags.map(x => entries.find(e => e.id === x.id)?.name).filter(Boolean).join(', ')
  return (
    <>
      <Divider style={{ flexGrow: 0 }} />
      <span className={s.section}><DocumentBulletList20Regular style={{ verticalAlign: 'middle' }} /> DMS</span>
      {doc.correspondent && <span>{t('Korrespondent')}: {fullName(entries, doc.correspondent)}</span>}
      {doc.doctype && <span>{t('Dokumenttyp')}: {fullName(entries, doc.doctype)}</span>}
      {tags && <span>{t('Tags')}: {tags}</span>}
      {doc.docDate && <span className={s.muted}>{t('Dokumentdatum')}: {new Date(doc.docDate + 'T12:00:00').toLocaleDateString()}</span>}
      {doc.inbox && <span className={s.muted}>{t('In der Inbox')}</span>}
      <Button size="small" onClick={() => { location.hash = '#/dms' }}>{t('Im DMS öffnen')}</Button>
    </>
  )
}
