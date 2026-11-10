import {
  MessageBar, MessageBarBody,
  Button, Combobox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Option, Spinner, Text, Tooltip, tokens,
} from '@fluentui/react-components'
import { Dismiss20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, Collection } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

// Die drei Stufen: show = nur ansehen, read = ansehen und Kopie behalten, write = gemeinsam bearbeiten
export type Right = 'none' | 'show' | 'read' | 'write'

export const rightLabel = (): Record<Right, string> => ({ none: t('Kein Zugriff'), show: t('Nur ansehen'), read: t('Ansehen und Kopie behalten'), write: t('Gemeinsam bearbeiten') })

const rightHint = (): Record<Right, string> => ({
  none: '', show: t('Nur in dmOffice, ohne Herunterladen und nicht auf ihren Geräten'), read: t('Auch herunterladen und auf ihren Geräten nutzen'), write: t('Alle arbeiten am selben Inhalt'),
})

/** Recht einer Person aus der Antwort des Servers (level; aeltere Antworten nur readOnly). */
export const rightOf = (m: { readOnly?: boolean; level?: string }): Right =>
  m.level === 'show' ? 'show' : m.level === 'send' || (m.level == null && m.readOnly) ? 'read' : 'write'

/** Benutzer dieses Servers mit einem Recht je Person - der Baustein "Mit Personen teilen" (Kalender, Listen, Notizen, Dateien). */
export function usePeople(initial: Record<string, Right>) {
  const { fail } = useApp()
  const [people, setPeople] = useState<{ id: string; login: string; displayName: string }[] | null>(null)
  const [rights, setRights] = useState<Record<string, Right>>(initial)
  useEffect(() => { api.get<any[]>('/api/pim/people').then(setPeople).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const members = Object.keys(rights).filter(id => rights[id] !== 'none')
  return {
    people, rights, setRights,
    /** Antwortkoerper fuer PUT .../shares: wer Zugriff hat und wer davon nur lesen darf */
    body: { members, readOnly: members.filter(id => rights[id] === 'read' || rights[id] === 'show'), showOnly: members.filter(id => rights[id] === 'show') },
    nobody: people !== null && people.length === 0,
  }
}

/**
 * Wer Zugriff hat (Stufe änderbar, entfernen) und darüber „Person hinzufügen“ mit Vorschlägen beim Tippen -
 * auch bei vielen Benutzern auf dem Server übersichtlich. Gleich in allen Freigabe-Dialogen.
 */
export function PeopleList({ state, intro, addAs = 'read' }: { state: ReturnType<typeof usePeople>; intro: string; addAs?: Right }) {
  const { people, rights, setRights } = state
  const [query, setQuery] = useState('')
  const label = rightLabel()
  const hints = rightHint()
  const has = (id: string) => (rights[id] ?? 'none') !== 'none'
  const withAccess = (people ?? []).filter(p => has(p.id))
  const q = query.trim().toLowerCase()
  const matches = (people ?? []).filter(p => !has(p.id) && (!q || p.displayName.toLowerCase().includes(q) || p.login.toLowerCase().includes(q)))
  // Neu hinzugefügt: die Vorgabe des Dialogs - die Stufe lässt sich gleich daneben ändern
  const add = (id: string) => { setRights(r => ({ ...r, [id]: addAs })); setQuery('') }
  return (
    <>
      {people && people.length === 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('Du kannst noch mit niemandem teilen: Auf diesem Server gibt es keinen weiteren Benutzer. Der Administrator legt Benutzer unter Verwaltung an.')}</MessageBarBody>
        </MessageBar>
      )}
      {people && people.length > 0 && <Text>{intro}</Text>}
      {!people ? <Spinner size="tiny" /> : people.length > 0 && <>
        <Field label={t('Person hinzufügen')}>
          <Combobox freeform placeholder={t('Name eingeben …')} value={query} selectedOptions={[]}
            onChange={e => setQuery((e.target as HTMLInputElement).value)}
            onOptionSelect={(_, d) => { if (d.optionValue) add(d.optionValue) }}>
            {matches.slice(0, 8).map(p => <Option key={p.id} value={p.id} text={p.displayName}>{p.displayName} ({p.login})</Option>)}
            {matches.length === 0 && <Option disabled value="" text="">{q ? t('Niemand gefunden') : t('Alle haben schon Zugriff')}</Option>}
            {matches.length > 8 && <Option disabled value="" text="">{t('… und {n} weitere – weitertippen', { n: matches.length - 8 })}</Option>}
          </Combobox>
        </Field>
        <Text weight="semibold">{t('Zugriff haben')}</Text>
        {withAccess.length === 0
          ? <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('Noch niemand – füge oben eine Person hinzu.')}</Text>
          : withAccess.map(p => (
            <div key={p.id} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <Text style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }} title={p.login}>{p.displayName} ({p.login})</Text>
              <Dropdown style={{ minWidth: 210 }} listbox={{ style: { minWidth: 280 } }} value={label[rights[p.id]]} selectedOptions={[rights[p.id]]}
                aria-label={t('Zugriff für {name}', { name: p.displayName })}
                onOptionSelect={(_, d) => setRights(r => ({ ...r, [p.id]: (d.optionValue ?? 'read') as Right }))}>
                {(['show', 'read', 'write'] as Right[]).map(r => (
                  <Option key={r} value={r} text={label[r]}>
                    <div>
                      <div>{label[r]}</div>
                      <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{hints[r]}</Text>
                    </div>
                  </Option>
                ))}
              </Dropdown>
              <Tooltip content={t('Zugriff entfernen')} relationship="label">
                <Button appearance="subtle" icon={<Dismiss20Regular />} onClick={() => setRights(r => ({ ...r, [p.id]: 'none' }))} />
              </Tooltip>
            </div>
          ))}
      </>}
    </>
  )
}

/**
 * Einen eigenen Kalender oder eine Aufgabenliste mit anderen Benutzern dieses Servers teilen.
 * Jeder bekommt eine der drei Stufen; alle arbeiten in derselben Quelle, es gibt keine Kopien.
 */
export function ShareDialog({ list, notesFolder, noteId, onClose }: {
  list: Pick<Collection, 'id' | 'name' | 'components' | 'sharedWith'>
  /** Ein Notizordner (sync_id) statt Kalender/Liste */
  notesFolder?: string
  /** Eine einzelne eigene Notiz (Version 2, nur in dmOffice) */
  noteId?: number
  onClose: (changed: boolean) => void
}) {
  const { fail } = useApp()
  const kind = noteId ? 'note' : notesFolder ? 'notes' : list.components.includes('VEVENT') ? 'calendar' : 'list'
  const state = usePeople(Object.fromEntries((list.sharedWith ?? []).map(x => [x.id, rightOf(x)])))
  // Einzelne Notiz: wer sie hat, fragt der Dialog selbst
  useEffect(() => {
    if (noteId) api.get<{ id: string; readOnly: boolean; level?: string }[]>(`/api/notes/${noteId}/shares`)
      .then(m => state.setRights(Object.fromEntries(m.map(x => [x.id, rightOf(x)])))).catch(fail)
  }, [noteId]) // eslint-disable-line react-hooks/exhaustive-deps
  const save = async () => {
    try {
      await api.put(noteId ? `/api/notes/${noteId}/shares` : notesFolder ? `/api/notes/shares/${notesFolder}` : `/api/pim/shares/${list.id}`, state.body)
      onClose(true)
    } catch (e) { fail(e) }
  }
  const intro = kind === 'note'
    ? t('Wer hier Zugriff hat, sieht die Notiz bei sich unter „Mit mir geteilt“ – nur in dmOffice, nicht in notizi. Mit „Gemeinsam bearbeiten“ lässt sie sich bearbeiten; verschieben, archivieren und löschen kannst nur du.')
    : kind === 'notes'
    ? t('Wer hier Zugriff hat, sieht den Ordner samt Unterordnern bei sich unter „Mit mir geteilt“. Mit „Gemeinsam bearbeiten“ lassen sich Notizen anlegen, ändern und löschen; Änderungen erscheinen bei den anderen nach wenigen Sekunden. Verschieben aus dem Ordner heraus und endgültiges Löschen bleiben bei dir.')
    : kind === 'calendar'
    ? t('Wer hier Zugriff hat, sieht den Kalender bei sich als „{name} (von dir)“ – mit „Ansehen und Kopie behalten“ und „Gemeinsam bearbeiten“ auch auf ihren Geräten, mit „Nur ansehen“ nur in dmOffice. Mit „Gemeinsam bearbeiten“ lassen sich Termine eintragen, ändern und löschen; Änderungen sind sofort für alle da.', { name: list.name })
    : t('Wer hier Zugriff hat, sieht die Liste bei sich unter „Mit mir geteilt“. Mit „Gemeinsam bearbeiten“ lassen sich Einträge hinzufügen, abhaken und ändern.')
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ teilen', { name: list.name })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <PeopleList state={state} intro={intro} addAs={kind === 'calendar' ? 'read' : 'write'} />
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{state.nobody ? t('Schließen') : t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={save} disabled={!state.people || state.nobody}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
