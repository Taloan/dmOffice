import { Button, Text, makeStyles, tokens } from '@fluentui/react-components'
import { CalendarAdd20Regular, CalendarCheckmark20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, MailItem, q } from '../api'
import { useApp } from '../App'
import { fmtTime } from '../shell/hooks'
import { locale, t } from '../i18n'

interface Smart { kind: string; title: string; start: string; end: string | null; allDay: boolean; location?: string | null; details?: string | null; inCalendar: boolean }

const useStyles = makeStyles({
  bar: {
    margin: '0 20px 10px', padding: '8px 12px', borderRadius: tokens.borderRadiusMedium, display: 'flex', alignItems: 'center', gap: '10px', flexWrap: 'wrap',
    backgroundColor: tokens.colorNeutralBackground3, flexShrink: 0,
  },
  text: { flex: 1, minWidth: '200px', display: 'flex', flexDirection: 'column' },
  muted: { color: tokens.colorNeutralForeground3 },
})

// Ganztaegig: der Server liefert das Datum ohne Zeitzone
function when(x: Smart) {
  const day = (d: Date) => d.toLocaleDateString(locale, { weekday: 'short', day: '2-digit', month: '2-digit' })
  if (x.allDay) {
    const s = new Date(x.start.slice(0, 10) + 'T12:00')
    const e = x.end ? new Date(new Date(x.end.slice(0, 10) + 'T12:00').getTime() - 86400000) : s
    return e > s ? `${day(s)} – ${day(e)}` : day(s)
  }
  const s = new Date(x.start), e = x.end ? new Date(x.end) : null
  return `${day(s)}, ${fmtTime(s)}${e ? (e.toDateString() === s.toDateString() ? ` – ${fmtTime(e)}` : ` – ${day(e)} ${fmtTime(e)}`) : ''}`
}

/** Lieferung oder Buchung in der Mail: Hinweis mit "In Kalender". Wie in Outlook, aber auf dem eigenen Server. */
export function SmartCard({ item }: { item: MailItem }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [list, setList] = useState<Smart[]>([])
  const [busy, setBusy] = useState(false)
  const load = () => api.get<Smart[] | undefined>(`/api/mail/${item.account}/smart?` + q({ folder: item.folder, uid: item.uid }))
    .then(r => setList(r ?? [])).catch(() => setList([]))
  useEffect(() => { setList([]); load() }, [item.account, item.folder, item.uid]) // eslint-disable-line react-hooks/exhaustive-deps
  if (list.length === 0) return null

  const add = async () => {
    setBusy(true)
    try { await api.post(`/api/mail/${item.account}/smart`, { folder: item.folder, uid: item.uid }); notify(t('Im Kalender „E-Mail-Ankündigungen“ eingetragen')); load() }
    catch (e) { fail(e) } finally { setBusy(false) }
  }
  const all = list.every(x => x.inCalendar)
  return (
    <div className={s.bar}>
      <div className={s.text}>
        {list.map((x, i) => (
          <span key={i}><Text weight="semibold">{x.title}</Text> <Text>{when(x)}</Text>{x.location && <Text className={s.muted}> · {x.location}</Text>}</span>
        ))}
      </div>
      {all
        ? <Button appearance="subtle" icon={<CalendarCheckmark20Regular />} onClick={() => { location.hash = '#/calendar' }}>{t('Im Kalender')}</Button>
        : <Button icon={<CalendarAdd20Regular />} disabled={busy} onClick={add}>{t('In Kalender')}</Button>}
    </div>
  )
}
