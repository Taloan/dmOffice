import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Radio, RadioGroup, Text, tokens,
} from '@fluentui/react-components'
import { useState } from 'react'
import { api, CalEvent, Collection, featureOn, RemindRule } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { ReminderList } from './ReminderEditor'

/** Ergebnis von POST /api/pim/events/remind */
export interface RemindResult { changed: number; skipped: number }

/**
 * Erinnerung für mehrere markierte Termine setzen (calbulkremind): eigene Erinnerungen oder zurück auf den
 * Kalender-Standard (calremdefault). Serien werden als Ganzes geändert.
 */
export function BulkRemindDialog({ events, calendars, taskLists, onClose }: {
  events: CalEvent[]; calendars: Collection[]; taskLists: Collection[]; onClose: (r: RemindResult | null) => void
}) {
  const { fail } = useApp()
  const [mode, setMode] = useState<'own' | 'default'>('own')
  const allDay: boolean | 'mixed' = events.every(e => e.allDay) ? true : events.every(e => !e.allDay) ? false : 'mixed'
  const [rems, setRems] = useState<RemindRule[]>([{ minutes: allDay === false ? 15 : 360 }])
  const [busy, setBusy] = useState(false)
  // Serien: ein Eintrag je Objekt (Kalender + Uri)
  const items = [...new Map(events.map(e => [`${e.calendar}|${e.uri}`, { calendar: e.calendar, uri: e.uri }])).values()]
  const series = events.some(e => e.recurring)
  const withDefault = featureOn('calremdefault') && events.some(e => calendars.find(c => c.id === e.calendar)?.remind)
  const save = async () => {
    setBusy(true)
    try {
      const r = await api.post<RemindResult>('/api/pim/events/remind',
        { items, reminders: mode === 'own' ? rems : null, default: mode === 'default' })
      onClose(r)
    } catch (e) { fail(e); setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(null) }}>
      <DialogSurface style={{ maxWidth: 620 }}>
        <DialogBody>
          <DialogTitle>{events.length === 1 ? t('Erinnerung für 1 Termin') : t('Erinnerung für {n} Termine', { n: events.length })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {withDefault && <RadioGroup layout="horizontal" value={mode} onChange={(_, d) => setMode(d.value as 'own' | 'default')}>
              <Radio value="own" label={t('Eigene Erinnerungen')} />
              <Radio value="default" label={t('Standard des Kalenders')} />
            </RadioGroup>}
            <ReminderList value={rems} onChange={setRems} allDay={allDay} taskLists={taskLists} disabled={mode === 'default'} />
            <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              {t('Ersetzt die bisherigen Erinnerungen der markierten Termine.')}{series ? ' ' + t('Serien werden als Ganzes geändert.') : ''}
            </Text>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy} onClick={save}>{t('Übernehmen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
