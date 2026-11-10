import {
  Dropdown, Field, Input, MessageBar, MessageBarBody, Option, Radio, RadioGroup, Spinner, Switch, Table, TableBody, TableCell,
  TableHeader, TableHeaderCell, TableRow, Text,
} from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Hint, PageTitle, Section } from '../settings/parts'
import { ChatMode, ChatRights, ChatSettings, chatSettings } from './store'
import { ProfilePhoto } from './ProfilePhoto'
import { PukkiFigureChoice } from './PukkiFigure'
import { PukkiLearnSection } from './PukkiLearn'
import { PukkiBriefingSection } from './PukkiBriefing'
import { askConfirm } from '../shell/context'
import { WebPushSetting } from '../settings/WebPushSetting'

const durations: { key: ChatSettings['showFor']; label: () => string }[] = [
  { key: 'forever', label: () => t('Dauerhaft') },
  { key: '1y', label: () => t('1 Jahr') },
  { key: '6m', label: () => t('6 Monate') },
  { key: '1m', label: () => t('1 Monat') },
  { key: '1w', label: () => t('1 Woche') },
  { key: '24h', label: () => t('24 Stunden') },
]

function ModeChoice({ value, onChange }: { value: ChatMode; onChange: (m: ChatMode) => void }) {
  return (
    <RadioGroup value={value} onChange={(_, d) => onChange(d.value as ChatMode)}>
      <Radio value="show" label={t('Zeigen – der Empfänger kann es nur im Chat ansehen')} />
      <Radio value="send" label={t('Senden – der Empfänger darf eine Kopie bei sich speichern')} />
    </RadioGroup>
  )
}

/** Pukki darf meine Mails lesen (eigener Schalter auf dem Server, damit ältere Apps ihn beim Speichern nicht zurücksetzen). */
function PukkiMailSwitch() {
  const { fail } = useApp()
  const [on, setOn] = useState<boolean | null>(null)
  useEffect(() => { api.get<{ allowed: boolean }>('/api/chat/pukki/mail').then(r => setOn(r.allowed)).catch(() => setOn(null)) }, [])
  if (on == null) return null
  return (
    <>
      <Switch label={t('Pukki darf meine Mails lesen')} checked={on} onChange={async (_, d) => {
        setOn(d.checked)
        try { await api.put('/api/chat/pukki/mail', { allowed: d.checked }) } catch (e) { setOn(!d.checked); fail(e) }
      }} />
      <Hint>{t('Dann beantwortet er Fragen wie „Welche Mails sind auf Wiedervorlage?“, „Was ist ungelesen?“ oder „Mails von Anna“. Er liest nur, was dmOffice ohnehin zwischenspeichert, und schreibt keine Mails.')}</Hint>
    </>
  )
}

export type ChatSettingsPart = 'profile' | 'share' | 'privacy' | 'pukki'

/**
 * Einstellungen -> Chat, aufgeteilt auf die Unterseiten des Bereichs: Profil (Bild), Teilen (Vorgabe-Arten, Zeigen, Speichern),
 * Privatsphäre (Lesebestätigungen, Ruhezeiten, Standort) und Pukki (Überblick, Erinnerungen, Mails, Figur, Lernen).
 */
export function ChatSettingsPage({ part }: { part: ChatSettingsPart }) {
  if (part === 'profile') return <><PageTitle>{t('Profil')}</PageTitle><ProfilePhoto /></>
  return <ChatSettingsForm part={part} />
}

function ChatSettingsForm({ part }: { part: Exclude<ChatSettingsPart, 'profile'> }) {
  const { fail } = useApp()
  const [s, setS] = useState<ChatSettings | null>(null)
  const [rights, setRights] = useState<ChatRights | null>(null)
  useEffect(() => {
    chatSettings(true).then(r => { setS(r.settings); setRights(r.rights) }).catch(fail)
  }, [fail])
  if (!s || !rights) return <Spinner size="small" />
  const save = async (change: Partial<ChatSettings>) => {
    const next = { ...s, ...change }
    setS(next)
    try { setS(await api.put<ChatSettings>('/api/chat/settings', next)); chatSettings(true) }
    catch (e) { fail(e); setS(s) }
  }
  if (part === 'share') return (
    <>
      <PageTitle>{t('Teilen')}</PageTitle>
      <Section title={t('Was der Empfänger mit Anhängen darf')}
        hint={t('Hier legst du für jede Art von Anhang die Vorgabe fest. Beim Verschicken kannst du das für jede Nachricht einzeln ändern.')}>
        <Field label={t('Fotos, Videos und Dateien von diesem Gerät')}><ModeChoice value={s.deviceMode} onChange={m => save({ deviceMode: m })} /></Field>
        <Field label={t('Dateien aus deiner Dateiablage')}><ModeChoice value={s.filesMode} onChange={m => save({ filesMode: m })} /></Field>
        <Field label={t('Notizen, Termine, Aufgaben und Kontakte')}><ModeChoice value={s.itemsMode} onChange={m => save({ itemsMode: m })} /></Field>
      </Section>
      <Section title={t('Wenn du etwas nur zeigst')}>
        <Field label={t('So lange kann der Empfänger es ansehen')}
          hint={rights.expiring ? t('Gerechnet ab dem Tag, an dem du es verschickst. Danach sieht der Empfänger es nicht mehr; dein Original bleibt, wo es ist.')
            : t('In der Verwaltung ist keine Ablaufzeit erlaubt – es bleibt dauerhaft sichtbar.')}>
          <Dropdown value={durations.find(d => d.key === s.showFor)?.label() ?? ''} selectedOptions={[s.showFor]} disabled={!rights.expiring}
            onOptionSelect={(_, d) => save({ showFor: d.optionValue as ChatSettings['showFor'] })} style={{ maxWidth: 240 }}>
            {durations.map(d => <Option key={d.key} value={d.key}>{d.label()}</Option>)}
          </Dropdown>
        </Field>
        <Switch label={t('Empfänger dürfen es an andere weiterleiten')} checked={s.reshow} onChange={(_, d) => save({ reshow: d.checked })} />
        <Hint>{t('Sonst kann nur der Empfänger selbst es ansehen. Was du sendest, darf er immer weiterleiten – er kann es ja ohnehin speichern.')}</Hint>
      </Section>
      <Section title={t('Wenn du etwas aus dem Chat bei dir speicherst')}
        hint={t('Fotos und Dateien landen in deiner Dateiablage im Ordner „Chat“, Notizen in deinen Notizen im Ordner „Chat“; beide legt dmOffice bei Bedarf an. Beim Speichern kannst du jedes Mal ein anderes Ziel wählen.')} last>{null}</Section>
    </>
  )
  if (part === 'privacy') return (
    <>
      <PageTitle>{t('Privatsphäre')}</PageTitle>
      {/* Benachrichtigungen im Browser (Web Push) - dieselbe Einstellung wie unter Allgemein → Benachrichtigungen */}
      <WebPushSetting />
      <Section title={t('Lesebestätigungen')}>
        <Switch label={t('Lesebestätigungen senden')} checked={s.readReceipts ?? true} onChange={(_, d) => save({ readReceipts: d.checked })} />
        <Hint>{t('Wenn das an ist, sehen andere, dass du ihre Nachricht gelesen hast. Ist es aus, sehen sie nur, dass die Nachricht bei dir angekommen ist. Eine Nachricht an dich kann der Absender dann nur so lange ändern oder löschen, bis sie bei dir angekommen ist.')}</Hint>
      </Section>
      <Section title={t('Ruhezeiten')}>
        <Switch label={t('Zu bestimmten Zeiten keine Benachrichtigungen')} checked={s.quiet} onChange={(_, d) => save({ quiet: d.checked })} />
        {s.quiet && (
          <>
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
              <Field label={t('Von')}>
                <Input type="time" value={s.quietFrom} onChange={(_, d) => setS({ ...s, quietFrom: d.value })} onBlur={() => save({ quietFrom: s.quietFrom })} style={{ maxWidth: 140 }} />
              </Field>
              <Field label={t('Bis')}>
                <Input type="time" value={s.quietTo} onChange={(_, d) => setS({ ...s, quietTo: d.value })} onBlur={() => save({ quietTo: s.quietTo })} style={{ maxWidth: 140 }} />
              </Field>
            </div>
            <Switch label={t('Trotzdem benachrichtigen, wenn mich jemand mit @ erwähnt')} checked={s.quietMentions} onChange={(_, d) => save({ quietMentions: d.checked })} />
          </>
        )}
        <Hint>{t('Zum Beispiel nachts. Nachrichten kommen in dieser Zeit trotzdem an, Handy und Browser bleiben nur still. Hast du danach noch ungelesene Nachrichten, bekommst du eine Benachrichtigung.')}</Hint>
      </Section>
      <Section title={t('Standort')} last>
        <Switch label={t('Andere dürfen mich fragen, wo ich gerade bin')} checked={s.locationRequests} onChange={(_, d) => save({ locationRequests: d.checked })} />
        <Hint>{t('Du entscheidest bei jeder Anfrage selbst, ob du deinen Standort schickst. Er geht dann einmal als Nachricht an die Person, die gefragt hat – er wird nicht laufend weitergegeben.')}</Hint>
      </Section>
    </>
  )
  return (
    <>
      <PageTitle>{t('Pukki')}</PageTitle>
      <Section title={t('Assistent')}>
        <Switch label={t('Pukki schickt mir morgens einen Überblick über den Tag')} checked={s.pukkiDaily} onChange={(_, d) => save({ pukkiDaily: d.checked })} />
        {s.pukkiDaily && (
          <Field label={t('Uhrzeit des Überblicks')}>
            <Input type="time" value={s.pukkiTime} onChange={(_, d) => setS({ ...s, pukkiTime: d.value })} onBlur={() => save({ pukkiTime: s.pukkiTime })} style={{ maxWidth: 140 }} />
          </Field>
        )}
        <Switch label={t('Pukki erinnert mich an Termine')} checked={s.pukkiReminders} onChange={(_, d) => save({ pukkiReminders: d.checked })} />
        <PukkiMailSwitch />
        <Hint>{t('Pukki ist dein Assistent im Chat. Den Überblick schickt er nur, wenn an dem Tag etwas ansteht. Erinnerungen bekommst du für Termine, bei denen du eine Erinnerung eingestellt hast. Schreib ihm „hilfe“, dann zeigt er dir, was er kann.')}</Hint>
      </Section>
      {s.pukkiDaily && <PukkiBriefingSection />}
      <PukkiFigureChoice />
      <PukkiLearnSection />
    </>
  )
}

interface RightsRow { id: string; login: string; displayName: string; expiring: boolean; locationRequests: boolean }

/** Verwaltung -> Chat: was jeder Benutzer im Chat darf. */
export function ChatRightsAdmin() {
  const { fail } = useApp()
  const [rows, setRows] = useState<RightsRow[] | null>(null)
  const [retention, setRetention] = useState<string | null>(null)
  useEffect(() => { api.get<RightsRow[]>('/api/chat/admin/rights').then(setRows).catch(fail) }, [fail])
  useEffect(() => { api.get<{ value: string }>('/api/chat/admin/retention').then(r => setRetention(r.value)).catch(fail) }, [fail])
  if (!rows || !retention) return <Spinner size="small" />
  // Aufbewahrung serverweit (Chat-Alltag)
  const keeps = [
    { key: 'forever', label: () => t('Immer') }, { key: '5y', label: () => t('5 Jahre') }, { key: '2y', label: () => t('2 Jahre') },
    { key: '1y', label: () => t('1 Jahr') }, { key: '6m', label: () => t('6 Monate') }, { key: '3m', label: () => t('3 Monate') },
  ]
  const keep = async (value: string) => {
    const label = keeps.find(k => k.key === value)?.label() ?? value
    if (value !== 'forever' && !await askConfirm('', t('Chat-Nachrichten, die älter sind als {span}, werden für alle endgültig gelöscht. Weiter?', { span: label }))) return
    try { await api.put('/api/chat/admin/retention', { value }); setRetention(value) } catch (e) { fail(e) }
  }
  const set = async (r: RightsRow, change: Partial<RightsRow>) => {
    const next = { ...r, ...change }
    setRows(rows.map(x => (x.id === r.id ? next : x)))
    try { await api.put(`/api/chat/admin/rights/${r.id}`, { expiring: next.expiring, locationRequests: next.locationRequests }) }
    catch (e) { fail(e); setRows(rows) }
  }
  return (
    <>
      <MessageBar intent="info" style={{ maxWidth: 760 }}>
        <MessageBarBody>{t('Hier legst du fest, was jemand im Chat darf – etwa, dass Kinder nichts verschicken, das später spurlos verschwindet.')}</MessageBarBody>
      </MessageBar>
      <Field label={t('Chat-Nachrichten aufbewahren')} style={{ maxWidth: 760, marginTop: 12 }}
        hint={t('Gilt für alle Unterhaltungen auf dem Server. Ältere Nachrichten werden endgültig gelöscht; Dateien und Notizen bleiben bei ihren Besitzern.')}>
        <Dropdown value={keeps.find(k => k.key === retention)?.label() ?? ''} selectedOptions={[retention]} onOptionSelect={(_, d) => d.optionValue && keep(d.optionValue)} style={{ maxWidth: 240 }}>
          {keeps.map(k => <Option key={k.key} value={k.key}>{k.label()}</Option>)}
        </Dropdown>
      </Field>
      <Table style={{ maxWidth: 760 }}>
        <TableHeader>
          <TableRow>
            <TableHeaderCell>{t('Name')}</TableHeaderCell>
            <TableHeaderCell>{t('Ablaufende Inhalte beim Zeigen')}</TableHeaderCell>
            <TableHeaderCell>{t('Standort-Anfragen senden')}</TableHeaderCell>
          </TableRow>
        </TableHeader>
        <TableBody>
          {rows.map(r => (
            <TableRow key={r.id}>
              <TableCell><Text weight="semibold">{r.displayName}</Text> <Text size={200}>{r.login}</Text></TableCell>
              <TableCell><Switch checked={r.expiring} onChange={(_, d) => set(r, { expiring: d.checked })} label={r.expiring ? t('erlaubt') : t('nicht erlaubt')} /></TableCell>
              <TableCell><Switch checked={r.locationRequests} onChange={(_, d) => set(r, { locationRequests: d.checked })} label={r.locationRequests ? t('erlaubt') : t('nicht erlaubt')} /></TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </>
  )
}
