import { Badge, Button, Input, Link, Spinner, Switch, Text, makeStyles, tokens } from '@fluentui/react-components'
import { CalendarAdd20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'

/** Coach: Vorsorge und Impfungen nach Alter und Geschlecht (STIKO-Impfkalender 2026, G-BA) - /api/coach/checks. */

interface Item {
  key: string; kind: 'vaccine' | 'screening'; title: string; text: string; source: string; url: string; minAge: number
  state: 'due' | 'ok' | 'done' | 'skip'; done: string | null; next: string | null
}
interface View { complete: boolean; remind: boolean; items: Item[] }

const useStyles = makeStyles({
  row: { display: 'flex', alignItems: 'flex-start', gap: '10px', padding: '10px 0', borderTop: `1px solid ${tokens.colorNeutralStroke3}`, flexWrap: 'wrap' },
  main: { flex: '1 1 260px', minWidth: 0, display: 'flex', flexDirection: 'column', gap: '2px' },
  actions: { display: 'flex', alignItems: 'center', gap: '6px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
})

const day = (iso: string) => new Date(iso + 'T12:00:00').toLocaleDateString(locale, { day: 'numeric', month: 'short', year: 'numeric' })
const today = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}` }

export function Checks({ onProfile }: { onProfile?: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [v, setV] = useState<View | null>(null)
  const [editing, setEditing] = useState<string | null>(null)
  const [date, setDate] = useState(today)
  useEffect(() => { api.get<View>('/api/coach/checks').then(setV).catch(fail) }, [fail])
  if (!v) return <Spinner />
  if (!v.complete) return <Text className={s.muted}>{t('Mit Geburtsdatum und Geschlecht im Profil zeigt der Coach hier, welche Vorsorge und Impfungen für dich empfohlen sind.')} {onProfile && <Link onClick={onProfile}>{t('Profil öffnen')}</Link>}</Text>

  const mark = (key: string, body: object) => api.put<View>(`/api/coach/checks/${key}`, body).then(r => { setV(r); setEditing(null) }).catch(fail)
  const calendar = (i: Item) => api.post<{ day: string }>(`/api/coach/checks/${i.key}/calendar`).then(r => notify(t('Erinnerung eingetragen: {day}, 9 Uhr', { day: day(r.day) }))).catch(fail)
  const order = { due: 0, ok: 1, done: 2, skip: 3 }
  const items = [...v.items].sort((a, b) => order[a.state] - order[b.state] || a.minAge - b.minAge)
  const badge = (i: Item) => i.state === 'due' ? <Badge color="warning">{t('fällig')}</Badge>
    : i.state === 'ok' ? <Badge color="success" appearance="tint">{i.next ? t('nächste ab {day}', { day: day(i.next) }) : t('erledigt')}</Badge>
      : i.state === 'done' ? <Badge color="success" appearance="tint">{t('erledigt')}</Badge>
        : <Badge appearance="outline">{t('nicht für mich')}</Badge>

  return (
    <div style={{ display: 'flex', flexDirection: 'column' }}>
      <Switch checked={v.remind} label={t('Pukki erinnert an Fälliges (höchstens einmal pro Woche)')}
        onChange={(_, d) => api.put<View>('/api/coach/checks/settings', { remind: d.checked }).then(setV).catch(fail)} />
      {items.map(i => (
        <div key={i.key} className={s.row}>
          <div className={s.main}>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <Text weight="semibold">{t(i.title)}</Text>{badge(i)}
            </div>
            <Text size={200}>{t(i.text)}</Text>
            <Text className={s.muted}>{i.kind === 'vaccine' ? t('Impfung') : t('Früherkennung')} · {t('ab {n} Jahren', { n: i.minAge })}{i.done ? ' · ' + t('zuletzt {day}', { day: day(i.done) }) : ''} · <Link href={i.url} target="_blank" rel="noopener noreferrer">{i.source}</Link></Text>
          </div>
          <div className={s.actions}>
            {editing === i.key ? <>
              <Input type="date" size="small" value={date} max={today()} onChange={(_, d) => setDate(d.value)} />
              <Button size="small" appearance="primary" onClick={() => mark(i.key, { done: date, skip: false })}>{t('Speichern')}</Button>
              <Button size="small" onClick={() => setEditing(null)}>{t('Abbrechen')}</Button>
            </> : <>
              {i.state !== 'skip' && <Button size="small" icon={<CalendarAdd20Regular />} onClick={() => calendar(i)}>{t('In den Kalender')}</Button>}
              <Button size="small" onClick={() => { setDate(i.done ?? today()); setEditing(i.key) }}>{i.done ? t('Datum ändern') : t('Erledigt am …')}</Button>
              {i.state === 'skip'
                ? <Button size="small" appearance="subtle" onClick={() => mark(i.key, { skip: false })}>{t('Wieder anzeigen')}</Button>
                : <Button size="small" appearance="subtle" onClick={() => mark(i.key, { skip: true })}>{t('Nicht für mich')}</Button>}
            </>}
          </div>
        </div>
      ))}
      <Text className={s.muted} style={{ marginTop: 8 }}>{t('Nur Information nach den öffentlichen Empfehlungen – ob und wann, entscheidest du mit deiner Ärztin oder deinem Arzt.')}</Text>
    </div>
  )
}
