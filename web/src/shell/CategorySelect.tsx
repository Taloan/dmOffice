import { Combobox, makeStyles, Option } from '@fluentui/react-components'
import { useState } from 'react'
import { t } from '../i18n'

// Fluent setzt die Höhe der Liste per autoSize auf den ganzen freien Platz (Inline-Stil) - ohne !important wird sie endlos lang
const useListStyles = makeStyles({ list: { maxHeight: '360px !important' } })

export interface SelectItem { id: string; label: string; extra?: string }

/**
 * Auswahl aus einer langen Liste (Kategorien, Konten) mit Suche: Tippen filtert nach Nummer oder Name, wie die Auswahl im DMS.
 * Ohne Eingabe stehen alle Einträge in der Liste; nach der Wahl zeigt das Feld wieder den gewählten Eintrag.
 */
export function CategorySelect({ items, value, onChange, placeholder, size, autoFocus, defaultOpen, onClose, style, listWidth }: {
  items: SelectItem[]; value: string | null; onChange: (id: string) => void; placeholder?: string; size?: 'small' | 'medium'
  autoFocus?: boolean; defaultOpen?: boolean; onClose?: () => void; style?: React.CSSProperties; listWidth?: number
}) {
  const [query, setQuery] = useState<string | null>(null)
  const selected = items.find(i => i.id === value)
  const q = (query ?? '').trim().toLocaleLowerCase()
  const shown = q ? items.filter(i => i.label.toLocaleLowerCase().includes(q)) : items
  const ls = useListStyles()
  return <Combobox size={size} style={style} autoFocus={autoFocus} defaultOpen={defaultOpen} placeholder={placeholder ?? t('Zum Suchen tippen')}
    value={query ?? selected?.label ?? ''} selectedOptions={value ? [value] : []}
    listbox={{ className: ls.list, style: { minWidth: listWidth ?? 320 } }}
    onInput={e => setQuery((e.target as HTMLInputElement).value)}
    onOptionSelect={(_, d) => { setQuery(null); if (d.optionValue) onChange(d.optionValue) }}
    onOpenChange={(_, d) => { if (!d.open) { setQuery(null); onClose?.() } }}>
    {shown.map(i => <Option key={i.id} value={i.id} text={i.label}>{i.label}{i.extra ?? ''}</Option>)}
    {shown.length === 0 && <Option disabled value="" text="">{t('Nichts gefunden')}</Option>}
  </Combobox>
}
