import { Button, Card, Checkbox, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import {
  CalendarCheckmark20Regular, CalendarQuestionMark20Regular, CalendarCancel20Regular, CalendarLtr24Regular, Warning16Regular,
} from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, MailItem, q } from '../api'
import { useApp } from '../App'
import { fmtDay, fmtLong, fmtTime } from '../shell/hooks'
import { locale, t } from '../i18n'

/** Einladung aus der Mail (vom Server ausgewertet, iTIP). */
interface Invite {
  method: 'REQUEST' | 'CANCEL' | 'REPLY' | 'PUBLISH' | string
  uid: string; title: string; start: string | null; end: string | null; allDay: boolean
  location?: string | null; organizerName?: string | null; organizerEmail?: string | null; recurring: boolean
  myStatus?: string | null; calendarId?: number | null; calendarName?: string | null; outdated: boolean
  conflicts: string[]; replyFrom?: string | null; replyStatus?: string | null
}

const useStyles = makeStyles({
  card: { margin: '0 20px 10px', padding: '12px 16px', display: 'flex', flexDirection: 'column', gap: '8px', flexShrink: 0 },
  head: { display: 'flex', gap: '12px', alignItems: 'flex-start' },
  icon: { color: tokens.colorBrandForeground1, flexShrink: 0, marginTop: '2px' },
  meta: { display: 'flex', flexDirection: 'column', gap: '2px', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3 },
  warn: { color: tokens.colorPaletteDarkOrangeForeground1, display: 'inline-flex', gap: '4px', alignItems: 'center' },
  actions: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
})

const statusText = (s?: string | null) =>
  s === 'ACCEPTED' ? t('Du hast zugesagt.') : s === 'TENTATIVE' ? t('Du hast mit Vorbehalt zugesagt.') : s === 'DECLINED' ? t('Du hast abgesagt.') : null

// Ganztaegig: Datum ohne Uhrzeit (der Server liefert Mitternacht UTC)
const day = (iso: string) => { const d = new Date(iso); const l = new Date(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()); return `${l.toLocaleDateString(locale, { weekday: 'short' })} ${fmtDay(l)}` }

function when(i: Invite) {
  if (!i.start) return ''
  const s = new Date(i.start), e = i.end ? new Date(i.end) : s
  if (i.allDay) {
    const last = new Date(e.getTime() - 86400000)
    return last > s ? `${day(i.start)} – ${day(last.toISOString())}` : day(i.start)
  }
  return s.toDateString() === e.toDateString()
    ? `${fmtLong(i.start)} – ${fmtTime(e)}`
    : `${fmtLong(i.start)} – ${fmtLong(e.toISOString())}`
}

/** Einladung im Lesebereich: Zusagen, Mit Vorbehalt, Absagen - wie in Outlook. */
export function InviteCard({ item }: { item: MailItem }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [invite, setInvite] = useState<Invite | null>(null)
  const [busy, setBusy] = useState(false)
  const [send, setSend] = useState(true)

  useEffect(() => {
    let alive = true
    setInvite(null)
    api.get<Invite | undefined>(`/api/mail/${item.account}/invite?` + q({ folder: item.folder, uid: item.uid }))
      .then(r => { if (alive) setInvite(r ?? null) }).catch(() => {})
    return () => { alive = false }
  }, [item.account, item.folder, item.uid])

  if (!invite || invite.method === 'PUBLISH') return null

  const respond = async (response: string) => {
    setBusy(true)
    try {
      const r = await api.post<{ sent: boolean; invite: Invite }>(`/api/mail/${item.account}/invite`, { folder: item.folder, uid: item.uid, response, send })
      setInvite(r.invite)
      notify(response === 'decline' ? t('Abgesagt') : response === 'remove' ? t('Aus dem Kalender entfernt') : response === 'apply' ? t('Antwort übernommen') : t('Im Kalender eingetragen'),
        r.sent ? t('Antwort an {name} gesendet.', { name: invite.organizerName || invite.organizerEmail || '' }) : undefined)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  const organizer = invite.organizerName || invite.organizerEmail
  return (
    <Card className={s.card} appearance="filled-alternative">
      <div className={s.head}>
        <CalendarLtr24Regular className={s.icon} />
        <div className={s.meta}>
          <Text weight="semibold">
            {invite.method === 'CANCEL' ? t('Abgesagt: {title}', { title: invite.title })
              : invite.method === 'REPLY' ? t('Antwort auf deine Einladung') : invite.title}
          </Text>
          {invite.method !== 'REPLY' && <Text>{when(invite)}{invite.recurring ? ' · ' + t('Serie') : ''}</Text>}
          {invite.location && invite.method !== 'REPLY' && <Text className={s.muted}>{invite.location}</Text>}
          {organizer && invite.method === 'REQUEST' && <Text size={200} className={s.muted}>{t('Organisator: {name}', { name: organizer })}</Text>}
          {invite.method === 'REPLY' && (
            <Text>
              {invite.replyStatus === 'ACCEPTED' ? t('{name} hat zugesagt.', { name: invite.replyFrom ?? '' })
                : invite.replyStatus === 'TENTATIVE' ? t('{name} hat mit Vorbehalt zugesagt.', { name: invite.replyFrom ?? '' })
                : invite.replyStatus === 'DECLINED' ? t('{name} hat abgesagt.', { name: invite.replyFrom ?? '' })
                : t('{name} hat geantwortet.', { name: invite.replyFrom ?? '' })}
            </Text>
          )}
          {invite.calendarName && invite.method === 'REQUEST' && (
            <Text size={200} className={s.muted}>{statusText(invite.myStatus) ?? ''} {t('Steht im Kalender „{name}“.', { name: invite.calendarName })}</Text>
          )}
          {invite.outdated && <Text size={200} className={s.warn}><Warning16Regular /> {t('Im Kalender steht schon eine neuere Fassung dieser Einladung.')}</Text>}
          {invite.conflicts.length > 0 && invite.method === 'REQUEST' && (
            <Text size={200} className={s.warn}><Warning16Regular /> {t('Zur selben Zeit: {list}', { list: invite.conflicts.join(', ') })}</Text>
          )}
        </div>
      </div>
      {invite.method === 'REQUEST' && !invite.outdated && (
        <div className={s.actions}>
          <Button appearance={invite.myStatus === 'ACCEPTED' ? 'primary' : 'secondary'} icon={<CalendarCheckmark20Regular />} disabled={busy} onClick={() => respond('accept')}>{t('Zusagen')}</Button>
          <Button appearance={invite.myStatus === 'TENTATIVE' ? 'primary' : 'secondary'} icon={<CalendarQuestionMark20Regular />} disabled={busy} onClick={() => respond('tentative')}>{t('Mit Vorbehalt')}</Button>
          <Button appearance={invite.myStatus === 'DECLINED' ? 'primary' : 'secondary'} icon={<CalendarCancel20Regular />} disabled={busy} onClick={() => respond('decline')}>{t('Absagen')}</Button>
          {organizer && <Checkbox label={t('Antwort an den Organisator senden')} checked={send} onChange={(_, d) => setSend(!!d.checked)} />}
          {busy && <Spinner size="tiny" />}
        </div>
      )}
      {invite.method === 'CANCEL' && invite.calendarId != null && (
        <div className={s.actions}>
          <Button icon={<CalendarCancel20Regular />} disabled={busy} onClick={() => respond('remove')}>{t('Aus dem Kalender entfernen')}</Button>
        </div>
      )}
      {invite.method === 'CANCEL' && invite.calendarId == null && <Text size={200} className={s.muted}>{t('Steht nicht (mehr) im Kalender.')}</Text>}
      {invite.method === 'REPLY' && invite.calendarId != null && (
        <div className={s.actions}>
          <Button disabled={busy} onClick={() => respond('apply')}>{t('Im Termin vermerken')}</Button>
        </div>
      )}
    </Card>
  )
}
