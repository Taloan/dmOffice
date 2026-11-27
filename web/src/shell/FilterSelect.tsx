import { Combobox, makeStyles, Option } from '@fluentui/react-components'
import { useState } from 'react'
import { t } from '../i18n'

// Fluent setzt die Höhe der Liste per autoSize auf den ganzen freien Platz (Inline-Stil) - ohne !important wird sie endlos lang
const useListStyles = makeStyles({ list: { maxHeight: '360px !important' } })

export interface FilterItem {
  id: string; label: string
  /** Zusatz hinter dem Namen in der Liste (etwa „ · login“ oder die Kundennummer), wird mit durchsucht */
  extra?: string
  /** Einrückung in der Liste ohne Suchtext (Ordner, Untereinträge) */
  depth?: number
}

/**
 * Auswahl aus einer langen Liste (Mailordner, Personen, Kunden, Einträge) mit Eintippen-und-Filtern - verallgemeinert aus
 * finance/CategorySelect. Ohne Eingabe stehen alle Einträge in der Liste (mit Einrückung); Tippen filtert nach Name und Zusatz.
 * none: Eintrag „keiner“ (Wert leer) oben in der Liste. unknown: Text im Feld, wenn der Wert nicht in der Liste steht.
 */
export function FilterSelect({ items, value, onChange, none, unknown, placeholder, size, disabled, style, listWidth, autoFocus, defaultOpen, onClose, 'aria-label': ariaLabel }: {
  items: FilterItem[]; value: string | null | undefined; onChange: (id: string) => void
  none?: string; unknown?: string; placeholder?: string; size?: 'small' | 'medium'; disabled?: boolean
  style?: React.CSSProperties; listWidth?: number; autoFocus?: boolean; defaultOpen?: boolean; onClose?: () => void; 'aria-label'?: string
}) {
  const [query, setQuery] = useState<string | null>(null)
  const selected = value ? items.find(i => i.id === value) : undefined
  const q = (query ?? '').trim().toLocaleLowerCase()
  const shown = q ? items.filter(i => (i.label + (i.extra ?? '')).toLocaleLowerCase().includes(q)) : items
  const current = value ? selected?.label ?? unknown ?? value : none ?? ''
  const ls = useListStyles()
  return <Combobox size={size} style={style} disabled={disabled} autoFocus={autoFocus} defaultOpen={defaultOpen} aria-label={ariaLabel}
    placeholder={placeholder ?? t('Zum Suchen tippen')}
    value={query ?? current} selectedOptions={[value ?? '']}
    listbox={{ className: ls.list, style: { minWidth: listWidth ?? 280 } }}
    onInput={e => setQuery((e.target as HTMLInputElement).value)}
    // Beim Hineinklicken den Text markieren: Tippen ersetzt ihn und filtert sofort
    onFocus={e => (e.target as HTMLInputElement).select?.()}
    // Fluent hebt beim Tippen die Auswahl auf (optionValue leer), sobald kein Eintrag mit dem Text beginnt; das darf das Filtern nicht beenden
    onOptionSelect={(_, d) => { if (d.optionValue === undefined) return; setQuery(null); onChange(d.optionValue) }}
    onOpenChange={(_, d) => { if (!d.open) { setQuery(null); onClose?.() } }}>
    {none !== undefined && !q && <Option value="" text={none}>{none}</Option>}
    {shown.map(i => <Option key={i.id} value={i.id} text={i.label}>
      <span style={!q && i.depth ? { paddingLeft: i.depth * 12 } : undefined}>{i.label}{i.extra ?? ''}</span>
    </Option>)}
    {shown.length === 0 && <Option disabled value="\u0000" text="">{t('Nichts gefunden')}</Option>}
  </Combobox>
}
