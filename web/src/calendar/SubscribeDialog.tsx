import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Spinner, Tab, TabList, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { useState } from 'react'
import { api, CalendarRemindDefault, Collection, featureOn } from '../api'
import { RemindDefaultEditor } from './ReminderEditor'
import { useApp } from '../App'
import { t } from '../i18n'

const useStyles = makeStyles({
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  colors: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  color: { width: '22px', height: '22px', borderRadius: '50%', border: '2px solid transparent', cursor: 'pointer', padding: 0 },
  on: { border: `2px solid ${tokens.colorNeutralForeground1}` },
  hint: { color: tokens.colorNeutralForeground3 },
})

const palette = ['#69797E', '#0F6CBD', '#13A10E', '#CA5010', '#D13438', '#8764B8', '#00B7C3', '#C19C00']
const intervals: [number, string][] = [[60, 'Stündlich'], [360, 'Alle 6 Stunden'], [1440, 'Täglich'], [10080, 'Wöchentlich'], [0, 'Nur von Hand']]

/** Kalender abonnieren (neu, auch mit Adresse aus einem webcal-Link) oder ein Abo bearbeiten. */
export function SubscribeDialog({ collection, url: initialUrl, onClose, taskLists = [] }: {
  collection: Collection | null; url?: string; onClose: (changed: boolean) => void
  /** Aufgabenlisten für „Aufgabe anlegen“ im Kalender-Standard (calremtask) */
  taskLists?: Collection[]
}) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const sub = collection?.subscription
  const [url, setUrl] = useState(sub?.url ?? initialUrl ?? '')
  const [name, setName] = useState(collection?.name ?? '')
  const [color, setColor] = useState(collection?.color ?? palette[0])
  const [interval, setInterval] = useState(sub?.interval ?? 360)
  const [busy, setBusy] = useState(false)
  // Kalender-Standard (calremdefault) im eigenen Reiter; wirkt zur Laufzeit, das Abo bleibt unverändert
  const remindOn = featureOn('calremdefault') && (!collection || collection.id > 0)
  const [tab, setTab] = useState<'abo' | 'remind'>('abo')
  const startRemind = JSON.stringify(collection?.remind ?? { reminders: [], rules: [] })
  const [remind, setRemind] = useState<CalendarRemindDefault>(() => JSON.parse(startRemind))
  const saveRemind = (id: number) => remindOn && JSON.stringify(remind) !== startRemind ? api.put(`/api/pim/collections/${id}/remind`, remind) : Promise.resolve()

  const save = async () => {
    setBusy(true)
    try {
      if (collection) {
        await api.put(`/api/pim/subscriptions/${collection.id}`, { url, interval })
        await api.put(`/api/pim/collections/${collection.id}`, { name, color })
        await saveRemind(collection.id)
        notify(t('Abo gespeichert'))
      } else {
        const r = await api.post<{ id: number; count: number }>('/api/pim/subscriptions', { url, name: name || null, color, interval })
        await saveRemind(r.id)
        notify(t('Kalender abonniert'), r.count === 1 ? t('1 Termin') : t('{n} Termine', { n: r.count }))
      }
      onClose(true)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: remindOn ? 600 : 520 }}>
        <DialogBody>
          <DialogTitle>{collection ? t('Abo bearbeiten') : t('Kalender abonnieren')}</DialogTitle>
          <DialogContent>
            {remindOn && <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as 'abo' | 'remind')} style={{ marginBottom: 8 }}>
              <Tab value="abo">{t('Abo')}</Tab>
              <Tab value="remind">{t('Erinnerung')}</Tab>
            </TabList>}
            {tab === 'remind' && <RemindDefaultEditor value={remind} onChange={setRemind} taskLists={taskLists} />}
            <form className={s.form} style={tab === 'remind' ? { display: 'none' } : undefined} onSubmit={e => { e.preventDefault(); save() }}>
              <Text className={s.hint}>
                {t('Ein abonnierter Kalender wird neu geholt und bleibt so aktuell, wenn der Anbieter etwas ändert – z. B. Müllabfuhr, Feiertage, Schulferien oder ein freigegebener Google- oder Outlook-Kalender. Er ist nur lesbar und erscheint auch auf verbundenen Geräten.')}
              </Text>
              <Field label={t('Adresse')} required hint={t('webcal://…, https://… oder http://… (meist endet sie auf .ics)')}>
                <Input value={url} onChange={(_, d) => setUrl(d.value)} placeholder="webcal://…" autoFocus />
              </Field>
              <Field label={t('Name')} hint={collection ? undefined : t('Leer lassen: der Name des Anbieters')}>
                <Input value={name} onChange={(_, d) => setName(d.value)} />
              </Field>
              <Field label={t('Farbe')}>
                <div className={s.colors}>
                  {palette.map(p => <button key={p} type="button" className={mergeClasses(s.color, p === color && s.on)} style={{ background: p }} onClick={() => setColor(p)} aria-label={p} />)}
                </div>
              </Field>
              <Field label={t('Aktualisieren')} hint={interval === 0 ? t('Wird einmal geholt, danach nur über „Jetzt aktualisieren“ im Menü des Kalenders.') : undefined}>
                <Dropdown value={t(intervals.find(i => i[0] === interval)?.[1] ?? 'Alle 6 Stunden')} selectedOptions={[String(interval)]}
                  onOptionSelect={(_, d) => setInterval(Number(d.optionValue))}>
                  {intervals.map(([v, l]) => <Option key={v} value={String(v)}>{t(l)}</Option>)}
                </Dropdown>
              </Field>
              {sub?.lastError && <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{t('Letzter Abruf: {error}', { error: sub.lastError })}</Text>}
              <button type="submit" hidden />
            </form>
          </DialogContent>
          <DialogActions>
            {busy && <Spinner size="tiny" />}
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={save} disabled={busy || !url.trim()}>{collection ? t('Speichern') : t('Abonnieren')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
