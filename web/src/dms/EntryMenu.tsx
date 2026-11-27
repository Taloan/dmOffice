// Untermenü im Rechtsklick-Menü der Dokumente (Kennung ctxall): Korrespondent, Dokumenttyp oder Tag wählen - wie die Auswahllisten der
// Leiste bei Mehrfachauswahl, mit Suchfeld über der Liste (Eintippen filtert, auch nach Oberbegriffen).
import { Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Text, tokens } from '@fluentui/react-components'
import { Search16Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { t } from '../i18n'
import { Entry, Kind, fullName, ordered } from './model'

export function EntryMenu({ entries, kind, label, icon, none, only, skip, onPick }: {
  entries: Entry[]; kind: Kind; label: string; icon: JSX.Element
  /** Eintrag „keiner“ oben (Korrespondent, Dokumenttyp) */
  none?: string
  /** Nur diese Einträge anbieten (Tag entfernen: die an den Dokumenten) */
  only?: string[]
  /** Diese nicht anbieten (Tag hinzufügen: die schon alle Dokumente haben) */
  skip?: string[]
  onPick: (id: string) => void
}) {
  const [query, setQuery] = useState('')
  const q = query.trim().toLocaleLowerCase()
  const all = ordered(entries, kind).filter(o => (!only || only.includes(o.entry.id)) && !skip?.includes(o.entry.id))
  const shown = q ? all.filter(o => fullName(entries, o.entry.id).toLocaleLowerCase().includes(q)) : all
  return (
    <Menu onOpenChange={(_, d) => { if (!d.open) setQuery('') }}>
      <MenuTrigger disableButtonEnhancement><MenuItem icon={icon} disabled={!all.length && none === undefined}>{label}</MenuItem></MenuTrigger>
      <MenuPopover style={{ padding: 0 }}>
        {all.length > 8 && <div style={{ padding: 6, borderBottom: `1px solid ${tokens.colorNeutralStroke2}` }}>
          {/* Tasten im Feld nicht an das Menü weitergeben (sonst springt die Tastensuche des Menüs an) */}
          <Input size="small" autoFocus value={query} onChange={(_, d) => setQuery(d.value)} contentBefore={<Search16Regular />} placeholder={t('Suchen')}
            onKeyDown={e => { if (e.key !== 'Escape' && e.key !== 'ArrowDown') e.stopPropagation() }} style={{ width: '100%' }} />
        </div>}
        <MenuList style={{ maxHeight: 360, overflowY: 'auto', padding: 4 }}>
          {none !== undefined && !q && <MenuItem onClick={() => onPick('')}>{none}</MenuItem>}
          {shown.map(o => (
            <MenuItem key={o.entry.id} onClick={() => onPick(o.entry.id)}>
              {q ? fullName(entries, o.entry.id) : <>{' '.repeat(4 * o.depth)}{o.entry.name}</>}
            </MenuItem>
          ))}
          {!shown.length && q && <Text size={200} style={{ padding: '6px 8px', color: tokens.colorNeutralForeground3 }}>{t('Nichts gefunden.')}</Text>}
        </MenuList>
      </MenuPopover>
    </Menu>
  )
}
