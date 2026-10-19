import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Spinner, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { useState } from 'react'
import { api, Collection } from '../api'
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
const intervals: [number, string][] = [[60, 'Stündlich'], [360, 'Alle 6 Stunden'], [1440, 'Täglich'], [10080, 'Wöchentlich']]

/** Kalender abonnieren (neu) oder ein Abo bearbeiten. */
export function SubscribeDialog({ collection, onClose }: { collection: Collection | null; onClose: (changed: boolean) => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const sub = collection?.subscription
  const [url, setUrl] = useState(sub?.url ?? '')
  const [name, setName] = useState(collection?.name ?? '')
  const [color, setColor] = useState(collection?.color ?? palette[0])
  const [interval, setInterval] = useState(sub?.interval ?? 360)
  const [busy, setBusy] = useState(false)

  const save = async () => {
    setBusy(true)
    try {
      if (collection) {
        await api.put(`/api/pim/subscriptions/${collection.id}`, { url, interval })
        await api.put(`/api/pim/collections/${collection.id}`, { name, color })
        notify(t('Abo gespeichert'))
      } else {
        const r = await api.post<{ id: number; count: number }>('/api/pim/subscriptions', { url, name: name || null, color, interval })
        notify(t('Kalender abonniert'), r.count === 1 ? t('1 Termin') : t('{n} Termine', { n: r.count }))
      }
      onClose(true)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{collection ? t('Abo bearbeiten') : t('Kalender abonnieren')}</DialogTitle>
          <DialogContent>
            <form className={s.form} onSubmit={e => { e.preventDefault(); save() }}>
              <Text className={s.hint}>
                {t('Ein abonnierter Kalender wird regelmäßig neu geholt und bleibt aktuell, wenn der Anbieter etwas ändert – z. B. Müllabfuhr, Feiertage, Schulferien oder ein freigegebener Google- oder Outlook-Kalender. Er ist nur lesbar und erscheint auch auf verbundenen Geräten.')}
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
              <Field label={t('Aktualisieren')}>
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
