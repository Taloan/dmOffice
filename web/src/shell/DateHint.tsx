import { Button, makeStyles, tokens } from '@fluentui/react-components'
import { CalendarLtr16Regular, Dismiss12Regular } from '@fluentui/react-icons'
import { locale, t } from '../i18n'
import { fmtTime } from './hooks'
import type { NaturalDate } from './naturalDate'

const useStyles = makeStyles({
  chip: {
    display: 'inline-flex', alignItems: 'center', gap: '6px', padding: '2px 4px 2px 8px', borderRadius: '12px', fontSize: '12px',
    backgroundColor: tokens.colorBrandBackground2, color: tokens.colorBrandForeground2, alignSelf: 'flex-start',
  },
  word: { fontStyle: 'italic', opacity: 0.8 },
})

/** Erkanntes Datum unter einem Eingabefeld - mit x zum Ignorieren. */
export function DateHint({ nd, onIgnore }: { nd: NaturalDate; onIgnore: () => void }) {
  const s = useStyles()
  const when = nd.date.toLocaleDateString(locale, { weekday: 'short', day: '2-digit', month: '2-digit' }) + (nd.hasTime ? ' ' + fmtTime(nd.date) : '')
  return (
    <span className={s.chip} title={t('Aus dem Text erkannt')}>
      <CalendarLtr16Regular /> {when} <span className={s.word}>„{nd.matched}“</span>
      <Button appearance="transparent" size="small" icon={<Dismiss12Regular />} onClick={onIgnore} aria-label={t('Datum nicht übernehmen')} style={{ minWidth: 20, height: 20, padding: 0 }} />
    </span>
  )
}

/** Datum fuer eine Aufgabe: ganztaegig als Tag, sonst Zeitpunkt. */
export function taskDue(nd: NaturalDate): { due: string; dueAllDay: boolean } {
  const d = nd.date
  if (!nd.hasTime) return { due: `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`, dueAllDay: true }
  return { due: d.toISOString(), dueAllDay: false }
}
