import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Switch, makeStyles,
  Radio, RadioGroup,
} from '@fluentui/react-components'
import { useState } from 'react'
import { api, Collection } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

const palette = ['#0F6CBD', '#13A10E', '#CA5010', '#C239B3', '#E3008C', '#986F0B', '#038387', '#8764B8', '#D13438', '#69797E']

const useStyles = makeStyles({
  colors: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  color: { width: '26px', height: '26px', borderRadius: '50%', border: '2px solid transparent', cursor: 'pointer', padding: 0 },
  on: { outline: '2px solid currentColor', outlineOffset: '2px' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
})

export async function importInto(id: number, file: File) {
  const f = new FormData()
  f.append('file', file, file.name)
  return api.form<{ added: number; failed: number }>(`/api/pim/import/${id}`, f)
}

/** Kalender, Aufgabenliste oder Adressbuch anlegen, umbenennen, einfaerben, loeschen. */
export function CollectionDialog({ kind, collection, onClose, tasks }: {
  kind: 'calendars' | 'addressbooks'; collection: Collection | null; onClose: (changed: boolean) => void; tasks?: boolean
}) {
  const s = useStyles()
  const { fail } = useApp()
  const [name, setName] = useState(collection?.name ?? '')
  const [color, setColor] = useState(collection?.color ?? palette[collection ? 0 : Math.floor(Math.random() * palette.length)])
  const [withTasks, setWithTasks] = useState(collection ? collection.components.includes('VTODO') : !!tasks)
  const [withEvents, setWithEvents] = useState(collection ? collection.components.includes('VEVENT') : !tasks)
  // Aufgabenliste, Einkaufsliste oder Checkliste (Lists.cs)
  const [listType, setListType] = useState<string>(collection?.listType ?? 'tasks')

  const components = [withEvents && 'VEVENT', withTasks && 'VTODO'].filter(Boolean).join(',')

  const save = async () => {
    try {
      let id = collection?.id
      if (collection) await api.put(`/api/pim/collections/${collection.id}`, { name, color, components: kind === 'calendars' ? components : undefined })
      else id = (await api.post<{ id: number }>(`/api/pim/collections/${kind}`, { name, color, components: kind === 'calendars' ? components : undefined })).id
      if (id && withTasks && kind === 'calendars' && listType !== (collection?.listType ?? 'tasks'))
        await api.put(`/api/pim/lists/${id}`, { type: listType })
      onClose(true)
    } catch (e) { fail(e) }
  }
  // Ein Kalender mit Terminen wird aus der Aufgabenansicht nie geloescht - dort nur die Aufgaben abschalten
  const mixed = !!collection && kind === 'calendars' && collection.components.includes('VEVENT') && collection.components.includes('VTODO')
  const fromTasksOnly = !!tasks && mixed
  const remove = async () => {
    if (!collection) return
    if (fromTasksOnly) {
      const n = collection.tasks ?? 0
      const ev = collection.events ?? 0
      const msg = [
        t('In „{name}“ keine Aufgaben mehr führen?', { name: collection.name }),
        n === 1 ? t('Die 1 Aufgabe darin wird nicht mehr angezeigt.') : n ? t('Die {n} Aufgaben darin werden nicht mehr angezeigt.', { n }) : '',
        ev === 1 ? t('Der 1 Termin bleibt.') : t('Die {n} Termine bleiben.', { n: ev }),
      ].filter(Boolean).join(' ')
      if (!confirm(msg)) return
      try { await api.put(`/api/pim/collections/${collection.id}`, { components: 'VEVENT' }); onClose(true) } catch (e) { fail(e) }
      return
    }
    const count = (n: number, one: string, many: string) => n === 1 ? t(one) : t(many, { n })
    const what = kind === 'addressbooks' ? count(collection.count, '1 Kontakt', '{n} Kontakte')
      : t('{events} und {tasks}', {
        events: count(collection.events ?? 0, '1 Termin', '{n} Termine'),
        tasks: count(collection.tasks ?? 0, '1 Aufgabe', '{n} Aufgaben'),
      })
    const answer = prompt(t('„{name}“ endgültig löschen – mit {what}, auch auf allen verbundenen Geräten.', { name: collection.name, what })
      + '\n\n' + t('Zum Bestätigen den Namen eingeben:'))
    if (answer?.trim() !== collection.name) return
    try { await api.del(`/api/pim/collections/${collection.id}`); onClose(true) } catch (e) { fail(e) }
  }

  const noun = kind === 'addressbooks' ? 'Adressbuch' : tasks && !mixed ? 'Aufgabenliste' : 'Kalender'
  const newTitle = { Adressbuch: t('Neues Adressbuch'), Aufgabenliste: t('Neue Aufgabenliste'), Kalender: t('Neuer Kalender') }[noun]
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 420 }}>
        <DialogBody>
          <DialogTitle>{collection ? (withEvents && withTasks && kind === 'calendars' ? t('Kalender und Aufgabenliste') : t(noun)) : newTitle}</DialogTitle>
          <DialogContent>
            <form className={s.form} onSubmit={e => { e.preventDefault(); save() }}>
              <Field label={t('Name')}><Input value={name} onChange={(_, d) => setName(d.value)} autoFocus /></Field>
              {kind === 'calendars' && (
                <>
                  <Field label={t('Farbe')}>
                    <div className={s.colors}>
                      {palette.map(c => <button key={c} type="button" className={`${s.color} ${c === color ? s.on : ''}`} style={{ background: c }} onClick={() => setColor(c)} aria-label={c} />)}
                    </div>
                  </Field>
                  <Field label={t('Enthält')} hint={withEvents && withTasks
                    ? t('Erscheint im Kalender und als Aufgabenliste – auch auf dem Handy, dort zweimal mit demselben Namen.')
                    : withTasks ? t('Erscheint als Aufgabenliste.') : t('Erscheint als Kalender.')}>
                    <div style={{ display: 'flex', gap: 16 }}>
                      <Switch label={t('Termine')} checked={withEvents} onChange={(_, d) => setWithEvents(d.checked)} />
                      <Switch label={t('Aufgaben')} checked={withTasks} onChange={(_, d) => setWithTasks(d.checked)} />
                    </div>
                  </Field>
                  {withTasks && (
                    <Field label={t('Listentyp')} hint={listType === 'shop'
                      ? t('Mit Menge, Einheit und Abteilungen; Vorschläge aus früheren Einträgen.')
                      : listType === 'check' ? t('Abhaken und für das nächste Mal zurücksetzen – z. B. Packliste für den Urlaub.') : undefined}>
                      <RadioGroup layout="horizontal" value={listType} onChange={(_, d) => setListType(d.value)}>
                        <Radio value="tasks" label={t('Aufgaben')} />
                        <Radio value="shop" label={t('Einkaufsliste')} />
                        <Radio value="check" label={t('Checkliste')} />
                      </RadioGroup>
                    </Field>
                  )}
                </>
              )}
              <button type="submit" hidden />
            </form>
          </DialogContent>
          <DialogActions>
            {collection && <Button onClick={remove} style={{ marginRight: 'auto' }}>{fromTasksOnly ? t('Aus Aufgaben entfernen') : t('Löschen')}</Button>}
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={save} disabled={!name.trim() || (kind === 'calendars' && !components)}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
