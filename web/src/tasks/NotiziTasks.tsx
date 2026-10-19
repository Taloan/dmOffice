import { Button, Checkbox, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { ChevronDown20Regular, ChevronRight20Regular, Note16Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

export interface NoteTask { note: number; noteTitle: string; folder: string | null; block: number; n: number; text: string; done: boolean; modified: string }

const useStyles = makeStyles({
  group: { display: 'flex', alignItems: 'center', gap: '6px', fontSize: '12px', fontWeight: 600, color: tokens.colorBrandForeground1, margin: '10px 4px 2px', cursor: 'pointer', alignSelf: 'flex-start' },
  row: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 12px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground2, ':hover': { backgroundColor: tokens.colorNeutralBackground2Hover },
  },
  text: { flex: 1, minWidth: 0 },
  done: { textDecoration: 'line-through', color: tokens.colorNeutralForeground3 },
  doneHead: { display: 'flex', alignItems: 'center', gap: '6px', alignSelf: 'flex-start', fontWeight: 600, marginTop: '8px' },
  hint: { color: tokens.colorNeutralForeground3 },
})

/** Laedt die Kaestchen aus allen Notizen - auch fuer den Zaehler in der Seitenleiste. */
export function useNotiziTasks() {
  const [list, setList] = useState<NoteTask[]>([])
  const load = useCallback(() => api.get<NoteTask[]>('/api/notes/tasks').then(setList).catch(() => {}), [])
  useEffect(() => { load() }, [load])
  return { list, load }
}

const openNote = (id: number) => { location.hash = `#/notes/${id}` }

/**
 * Systemliste "Notizi-Aufgaben": die Kaestchen (☐/☑) aus den Notizen, nach Notiz gruppiert.
 * Abhaken schreibt in die Notiz zurueck (und per Abgleich zu notizi), ein Klick oeffnet die Notiz.
 */
export function NotiziTasks({ list, reload, showDone, setShowDone }: {
  list: NoteTask[]; reload: () => void; showDone: boolean; setShowDone: (f: (v: boolean) => boolean) => void
}) {
  const s = useStyles()
  const { fail } = useApp()
  const [busy, setBusy] = useState<string | null>(null)
  const key = (x: NoteTask) => `${x.note}/${x.block}/${x.n}`

  const toggle = async (x: NoteTask) => {
    setBusy(key(x))
    try { await api.post('/api/notes/tasks/set', { note: x.note, block: x.block, n: x.n, text: x.text, done: !x.done }) }
    catch (e) { fail(e) }
    finally { setBusy(null); reload() }
  }

  if (list.length === 0)
    return <Text className={s.hint}>{t('Keine Aufgaben in Notizen. In einer Notiz ein Kästchen setzen (☐ am Zeilenanfang) – es erscheint dann hier.')}</Text>

  const open = list.filter(x => !x.done)
  const done = list.filter(x => x.done)
  // Nach Notiz gruppieren, zuletzt geaenderte Notiz zuerst
  const groups = (items: NoteTask[]) => [...new Map(items.map(x => [x.note, x])).values()]
    .sort((a, b) => b.modified.localeCompare(a.modified))
    .map(n => ({ note: n.note, title: n.noteTitle, items: items.filter(x => x.note === n.note) }))

  const row = (x: NoteTask) => (
    <div key={key(x)} className={s.row} onClick={() => openNote(x.note)} title={t('Notiz öffnen')}>
      <Checkbox shape="circular" checked={x.done} disabled={busy === key(x)} onClick={e => e.stopPropagation()} onChange={() => toggle(x)}
        aria-label={x.done ? t('Als nicht erledigt markieren') : t('Als erledigt markieren')} />
      <span className={mergeClasses(s.text, x.done && s.done)}>{x.text}</span>
    </div>
  )
  const grouped = (items: NoteTask[]) => groups(items).map(g => (
    <div key={g.note} style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
      <span className={s.group} onClick={() => openNote(g.note)} title={t('Notiz öffnen')}><Note16Regular />{g.title || t('(ohne Titel)')}</span>
      {g.items.map(row)}
    </div>
  ))

  return (
    <>
      {open.length === 0 && <Text className={s.hint}>{t('Alles erledigt.')}</Text>}
      {grouped(open)}
      {done.length > 0 && (
        <>
          <Button appearance="subtle" className={s.doneHead} icon={showDone ? <ChevronDown20Regular /> : <ChevronRight20Regular />}
            onClick={() => setShowDone(v => !v)} aria-expanded={showDone}>{t('Erledigt {n}', { n: done.length })}</Button>
          {showDone && grouped(done)}
        </>
      )}
    </>
  )
}
