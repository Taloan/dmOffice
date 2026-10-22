import {
  MessageBar, MessageBarBody,
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Option, Spinner, Text, tokens,
} from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, Collection } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

export type Right = 'none' | 'read' | 'write'

export const rightLabel = (): Record<Right, string> => ({ none: t('Kein Zugriff'), read: t('Lesen'), write: t('Bearbeiten') })

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
    body: { members, readOnly: members.filter(id => rights[id] === 'read') },
    nobody: people !== null && people.length === 0,
  }
}

export function PeopleList({ state, intro, hint }: { state: ReturnType<typeof usePeople>; intro: string; hint?: string }) {
  const { people, rights, setRights } = state
  const label = rightLabel()
  return (
    <>
      {people && people.length === 0 && (
        <MessageBar intent="warning">
          <MessageBarBody>{t('Du kannst noch mit niemandem teilen: Auf diesem Server gibt es keinen weiteren Benutzer. Der Administrator legt Benutzer unter Verwaltung an.')}</MessageBarBody>
        </MessageBar>
      )}
      {people && people.length > 0 && <Text>{intro}</Text>}
      {!people ? <Spinner size="tiny" /> : people.length === 0
        ? null
        : people.map(p => (
          <div key={p.id} style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
            <Text style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis' }}>{p.displayName} ({p.login})</Text>
            <Dropdown style={{ minWidth: 150 }} value={label[rights[p.id] ?? 'none']} selectedOptions={[rights[p.id] ?? 'none']}
              onOptionSelect={(_, d) => setRights(r => ({ ...r, [p.id]: (d.optionValue ?? 'none') as Right }))}>
              {(['none', 'read', 'write'] as Right[]).map(r => <Option key={r} value={r}>{label[r]}</Option>)}
            </Dropdown>
          </div>
        ))}
      {people && people.length > 0 && (
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
          {hint ?? t('Den Zugriff kannst du jederzeit ändern oder entziehen. Name und Farbe bestimmst du.')}
        </Text>
      )}
    </>
  )
}

/**
 * Einen eigenen Kalender oder eine Aufgabenliste mit anderen Benutzern dieses Servers teilen.
 * Jeder bekommt Lesen oder Bearbeiten; alle arbeiten in derselben Quelle, es gibt keine Kopien.
 */
export function ShareDialog({ list, notesFolder, onClose }: {
  list: Pick<Collection, 'id' | 'name' | 'components' | 'sharedWith'>
  /** Ein Notizordner (sync_id) statt Kalender/Liste */
  notesFolder?: string
  onClose: (changed: boolean) => void
}) {
  const { fail } = useApp()
  const kind = notesFolder ? 'notes' : list.components.includes('VEVENT') ? 'calendar' : 'list'
  const state = usePeople(Object.fromEntries((list.sharedWith ?? []).map(x => [x.id, x.readOnly ? 'read' : 'write'])))
  const save = async () => {
    try {
      await api.put(notesFolder ? `/api/notes/shares/${notesFolder}` : `/api/pim/shares/${list.id}`, state.body)
      onClose(true)
    } catch (e) { fail(e) }
  }
  const intro = kind === 'notes'
    ? t('Wer hier Zugriff hat, sieht den Ordner samt Unterordnern bei sich unter „Geteilt mit mir“. Mit „Bearbeiten“ lassen sich Notizen anlegen, ändern und löschen; Änderungen sind sofort für alle da. Verschieben aus dem Ordner heraus und endgültiges Löschen bleiben bei dir.')
    : kind === 'calendar'
    ? t('Wer hier Zugriff hat, sieht den Kalender bei sich als „{name} (von dir)“ – auch in DAVx⁵, iOS und der Android-App. Mit „Bearbeiten“ lassen sich Termine eintragen, ändern und löschen; Änderungen sind sofort für alle da.', { name: list.name })
    : t('Wer hier Zugriff hat, sieht die Liste bei sich unter „Geteilt mit mir“. Mit „Bearbeiten“ lassen sich Einträge hinzufügen, abhaken und ändern.')
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(false)}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ teilen', { name: list.name })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            <PeopleList state={state} intro={intro} />
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
