import { Checkbox, Dropdown, Field, Input, Option, Switch } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, Collection } from '../api'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import { t } from '../i18n'
import { Hint, PageTitle, Section } from './parts'
import { RecentAddresses } from './RecentAddresses'

const days = [t('Sonntag'), t('Montag'), t('Dienstag'), t('Mittwoch'), t('Donnerstag'), t('Freitag'), t('Samstag')]
const reminders: [string, string][] = [['', t('Keine')], ['0', t('Zum Beginn')], ['5', t('{n} Minuten vorher', { n: 5 })], ['15', t('{n} Minuten vorher', { n: 15 })],
  ['30', t('{n} Minuten vorher', { n: 30 })], ['60', t('1 Stunde vorher')], ['1440', t('1 Tag vorher')]]

export function CalendarViewSettings() {
  const { prefs, update } = usePrefs()
  const work = prefs.workDays ?? [1, 2, 3, 4, 5]
  return (
    <>
      <PageTitle>{t('Ansicht')}</PageTitle>
      <Section title={t('Kalenderdarstellung')}>
        <Field label={t('Erster Wochentag')} style={{ maxWidth: 260 }}>
          <Dropdown value={days[prefs.firstDay ?? 1]} selectedOptions={[String(prefs.firstDay ?? 1)]} onOptionSelect={(_, d) => update({ firstDay: Number(d.optionValue) })}>
            {[1, 0, 6].map(d => <Option key={d} value={String(d)}>{days[d]}</Option>)}
          </Dropdown>
        </Field>
        <Field label={t('Zeitraster')} style={{ maxWidth: 260 }}>
          <Dropdown value={t('{n} Minuten', { n: prefs.slotMinutes ?? 30 })} selectedOptions={[String(prefs.slotMinutes ?? 30)]} onOptionSelect={(_, d) => update({ slotMinutes: Number(d.optionValue) })}>
            {[10, 15, 30, 60].map(m => <Option key={m} value={String(m)}>{t('{n} Minuten', { n: m })}</Option>)}
          </Dropdown>
        </Field>
        <Switch label={t('Wochennummern anzeigen')} checked={prefs.weekNumbers ?? true} onChange={(_, d) => update({ weekNumbers: d.checked })} />
      </Section>
      <Section title={t('Arbeitszeit')} hint={t('Wird im Kalender hervorgehoben; die Arbeitswoche zeigt nur diese Tage.')} last>
        <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
          {[1, 2, 3, 4, 5, 6, 0].map(d => (
            <Checkbox key={d} label={days[d].slice(0, 2)} checked={work.includes(d)}
              onChange={(_, x) => update({ workDays: x.checked ? [...work, d].sort() : work.filter(w => w !== d) })} />
          ))}
        </div>
        <div style={{ display: 'flex', gap: 12 }}>
          <Field label={t('Beginn')}><Input type="time" value={prefs.workStart ?? '08:00'} onChange={(_, d) => update({ workStart: d.value })} /></Field>
          <Field label={t('Ende')}><Input type="time" value={prefs.workEnd ?? '18:00'} onChange={(_, d) => update({ workEnd: d.value })} /></Field>
        </div>
      </Section>
    </>
  )
}

export function EventSettings() {
  const { prefs, update } = usePrefs()
  const value = prefs.defaultReminder === undefined ? '15' : prefs.defaultReminder === null ? '' : String(prefs.defaultReminder)
  return (
    <>
      <PageTitle>{t('Ereignisse')}</PageTitle>
      <Section title={t('Standarderinnerung')} hint={t('Für neue Termine. Die Erinnerung wird im Termin gespeichert, das Handy erinnert also ebenso.')}>
        <Field style={{ maxWidth: 260 }}>
          <Dropdown value={reminders.find(r => r[0] === value)?.[1] ?? value} selectedOptions={[value]}
            onOptionSelect={(_, d) => update({ defaultReminder: d.optionValue ? Number(d.optionValue) : null })}>
            {reminders.map(([k, l]) => <Option key={k} value={k}>{l}</Option>)}
          </Dropdown>
        </Field>
      </Section>
      <InviteCalendarSetting />
      <Section title={t('Importieren und exportieren')} last>
        <Hint>
          {t('Im Kalender links beim jeweiligen Kalender auf ⋮ → „Importieren (.ics)“ oder „Exportieren (.ics)“.')}{' '}
          {t('Aus Nextcloud: in der Kalender-App beim Kalender ⋯ → „Herunterladen“.')}
        </Hint>
      </Section>
    </>
  )
}

/** Standardkalender fuer angenommene Einladungen. */
function InviteCalendarSetting() {
  const { fail } = useApp()
  const [cals, setCals] = useState<Collection[]>([])
  const [value, setValue] = useState<number | null>(null)
  const [smart, setSmart] = useState(true)
  useEffect(() => {
    api.get<Collection[]>('/api/pim/collections/calendars').then(l => setCals(l.filter(c => c.components.includes('VEVENT')))).catch(fail)
    api.get('/api/settings').then(s => { setValue(s.inviteCalendar ?? null); setSmart(s.smartCalendar ?? true) }).catch(fail)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const current = cals.find(c => c.id === value) ?? cals[0]
  return (
    <Section title={t('Einladungen')} hint={t('Zugesagte Einladungen aus E-Mails kommen in diesen Kalender. Steht ein Termin schon in einem anderen Kalender, bleibt er dort.')}>
      <Field style={{ maxWidth: 320 }} label={t('Standardkalender für Einladungen')}>
        <Dropdown value={current?.name ?? ''} selectedOptions={current ? [String(current.id)] : []}
          onOptionSelect={async (_, d) => {
            const id = Number(d.optionValue)
            setValue(id)
            try { await api.put('/api/settings/general', { inviteCalendar: id }) } catch (e) { fail(e) }
          }}>
          {cals.map(c => <Option key={c.id} value={String(c.id)} text={c.name}>
            <span style={{ display: 'inline-block', width: 10, height: 10, borderRadius: 3, background: c.color || '#0F6CBD', marginRight: 6 }} />{c.name}
          </Option>)}
        </Dropdown>
      </Field>
      <Switch checked={smart} label={t('Termine aus E-Mails in den Kalender „E-Mail-Ankündigungen“ eintragen')}
        onChange={async (_, d) => { setSmart(d.checked); try { await api.put('/api/settings/general', { smartCalendar: d.checked }) } catch (e) { fail(e) } }} />
      <Hint>{t('Paketankündigungen (DHL, Amazon, Hermes, DPD, GLS, UPS) sowie Flüge, Bahn, Hotels und Reservierungen mit maschinenlesbaren Angaben kommen in den Kalender „E-Mail-Ankündigungen“. Ausgewertet wird nur auf deinem Server.')}</Hint>
    </Section>
  )
}

export function PeopleSettings() {
  return (
    <>
      <PageTitle>{t('Kontakte')}</PageTitle>
      <Section title={t('Importieren und exportieren')}>
        <Hint>
          {t('In Kontakte links beim Adressbuch auf ⋮ → „Importieren (.vcf)“ oder „Exportieren (.vcf)“.')}{' '}
          {t('Aus Nextcloud: Kontakte-App → Einstellungen (unten links) → beim Adressbuch ⋯ → „Herunterladen“.')}
        </Hint>
      </Section>
      <Section title={t('Adressvorschläge')} hint={t('Beim Schreiben schlägt dmOffice Kontakte und die zuletzt angeschriebenen Adressen vor. Hier lassen sie sich aufräumen oder als Kontakt übernehmen.')} last>
        <RecentAddresses />
      </Section>
    </>
  )
}
