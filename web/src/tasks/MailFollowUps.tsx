import { Button, Checkbox, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { CalendarLtr16Regular, Mail16Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { addrText, api, MailItem } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { FollowUpDueButton } from '../mail/FollowUpMenu'

export interface FollowUp { mail: MailItem; due: string | null }

const useStyles = makeStyles({
  row: {
    display: 'flex', alignItems: 'center', gap: '10px', padding: '10px 12px', borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow2, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  body: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: '2px' },
  title: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  meta: { fontSize: '12px', color: tokens.colorNeutralForeground3, display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  late: { color: tokens.colorPaletteRedForeground1 },
  hint: { color: tokens.colorNeutralForeground3 },
})

const todayIso = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}` }

/** Laedt die gekennzeichneten Mails - auch fuer die Zaehler in der Seitenleiste. */
export function useFollowUps() {
  const [list, setList] = useState<FollowUp[]>([])
  const load = useCallback(() => api.get<FollowUp[]>('/api/mail/followups').then(setList).catch(() => {}), [])
  useEffect(() => { load() }, [load])
  return { list, load }
}

/** Systemliste "E-Mail-Nachverfolgung": gekennzeichnete Mails als Aufgaben. Nicht umbenennbar, nicht loeschbar. */
export function MailFollowUps({ list, reload }: { list: FollowUp[]; reload: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const today = todayIso()
  // Faellige zuerst, dann nach Datum, ohne Datum zuletzt
  const sorted = [...list].sort((a, b) => (a.due ?? '9999') .localeCompare(b.due ?? '9999') || b.mail.date.localeCompare(a.mail.date))

  const set = async (f: FollowUp, due: string | null, done = false) => {
    try {
      await api.post(`/api/mail/${f.mail.account}/followup`, { folder: f.mail.folder, uids: [f.mail.uid], due, done })
      if (done) notify(t('Nachverfolgung erledigt'))
      reload()
    } catch (e) { fail(e) }
  }
  const open = (f: FollowUp) => {
    if (f.mail.messageId) location.hash = `#/mail/flagged/${encodeURIComponent(f.mail.account)}/${encodeURIComponent(f.mail.messageId)}`
  }

  if (sorted.length === 0)
    return <Text className={s.hint}>{t('Keine Mails zur Nachverfolgung. In der Mail über „Nachverfolgen“ markieren – sie erscheinen dann hier, mit Fälligkeitsdatum.')}</Text>

  return (
    <>
      {sorted.map(f => {
        const late = !!f.due && f.due < today
        return (
          <div key={f.mail.id} className={s.row} onClick={() => open(f)}>
            <Checkbox shape="circular" checked={false} onClick={e => e.stopPropagation()} onChange={() => set(f, null, true)} aria-label={t('Als erledigt markieren')} />
            <div className={s.body}>
              <span className={s.title}>{f.mail.subject || t('(ohne Betreff)')}</span>
              <span className={s.meta}>
                <span><Mail16Regular style={{ verticalAlign: -3 }} /> {addrText(f.mail.from)}</span>
                {f.due && <span className={mergeClasses(late && s.late)}><CalendarLtr16Regular style={{ verticalAlign: -3 }} /> {new Date(f.due + 'T12:00').toLocaleDateString(locale, { weekday: 'short', day: '2-digit', month: '2-digit' })}</span>}
              </span>
            </div>
            <span onClick={e => e.stopPropagation()}>
              <FollowUpDueButton due={f.due} onPick={due => set(f, due)} onDone={() => set(f, null, true)} />
            </span>
            <Button appearance="subtle" size="small" onClick={e => { e.stopPropagation(); open(f) }}>{t('Öffnen')}</Button>
          </div>
        )
      })}
    </>
  )
}
