import { Button, Checkbox, Dropdown, Input, Option, Spinner, Text, Textarea, makeStyles, tokens } from '@fluentui/react-components'
import { useState } from 'react'
import { useApp } from '../App'
import { t } from '../i18n'
import { coachApi, fmt, Hit, Meal, Proposal } from './api'

const useStyles = makeStyles({
  row: { display: 'grid', gridTemplateColumns: 'auto minmax(0, 1fr) 110px 80px', gap: '8px', alignItems: 'center', padding: '6px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  num: { fontVariantNumeric: 'tabular-nums', textAlign: 'right' },
})

type Row = Proposal & { on: boolean; chosen: Hit | null; amount: string; changed: boolean }
const rowsOf = (list: Proposal[]): Row[] => list.map(p => ({ ...p, on: !!p.food && !!p.grams,
  chosen: p.food ? { id: p.food.id, name: p.food.name, brand: p.food.brand, source: p.food.source, kcalPer100: p.food.per100.kcal, amount: null, portion: null, mine: false } : null,
  amount: p.grams ? String(p.grams) : '', changed: false }))

/**
 * Beschreiben statt suchen: „2 Brötchen mit Käse und ein Kaffee“ → je Posten ein Vorschlag (Lebensmittel, Menge), der sich
 * ändern lässt; eine andere Wahl merkt sich Coach für das nächste Mal. Danach alles auf einmal eintragen.
 */
export function Describe({ date, meal, onAdded, initialText = '', initial }: {
  date: string; meal: Meal; onAdded: () => void
  /** Schon erkannt (Foto vom Teller) */
  initialText?: string; initial?: Proposal[]
}) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [text, setText] = useState(initialText)
  const [rows, setRows] = useState<Row[] | null>(() => initial ? rowsOf(initial) : null)
  const [busy, setBusy] = useState(false)
  const parse = async () => {
    setBusy(true)
    try {
      const r = await coachApi.parse(text, meal)
      setRows(rowsOf(r))
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const save = async () => {
    if (!rows) return
    setBusy(true)
    try {
      let n = 0
      for (const r of rows.filter(r => r.on && r.chosen && Number(r.amount.replace(',', '.')) > 0)) {
        await coachApi.add({ date, meal, food: r.chosen!.id, amount: Number(r.amount.replace(',', '.')), portion: r.portion, remember: r.changed ? r.name : undefined })
        n++
      }
      notify(t('{n} Posten eingetragen.', { n })); onAdded()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 8 }}>
      <Textarea value={text} onChange={(_, d) => setText(d.value)} placeholder={t('z. B. 2 Brötchen mit Butter und Käse, 1 Kaffee mit Milch')} resize="vertical" />
      <div><Button appearance="primary" disabled={!text.trim() || busy} onClick={parse}>{t('Erkennen')}</Button></div>
      {busy && !rows && <Spinner size="tiny" />}
      {rows && rows.length === 0 && <Text className={s.muted}>{t('Darin habe ich nichts erkannt.')}</Text>}
      {rows && rows.length > 0 && <>
        {rows.map((r, i) => {
          const options = [...(r.chosen ? [r.chosen] : []), ...r.alternatives.filter(a => a.id !== r.chosen?.id)]
          const amount = Number(r.amount.replace(',', '.'))
          const up = (patch: Partial<typeof r>) => setRows(rows.map((x, j) => j === i ? { ...x, ...patch } : x))
          return (
            <div key={i} className={s.row}>
              <Checkbox checked={r.on} onChange={(_, d) => up({ on: !!d.checked })} aria-label={r.name} />
              <div style={{ minWidth: 0 }}>
                <Text size={200} className={s.muted} block>{r.name}{r.portion ? ' · ' + r.portion : ''}</Text>
                {options.length > 0
                  ? <Dropdown value={r.chosen?.name ?? ''} selectedOptions={r.chosen ? [r.chosen.id] : []} style={{ minWidth: 0, width: '100%' }}
                      onOptionSelect={(_, d) => up({ chosen: options.find(o => o.id === d.optionValue) ?? null, changed: true, on: true })}>
                      {options.map(o => <Option key={o.id} value={o.id} text={o.name}>{o.name}{o.brand ? ' · ' + o.brand : ''}</Option>)}
                    </Dropdown>
                  : <Text size={200}>{t('Nichts Passendes gefunden – über die Suche eintragen.')}</Text>}
              </div>
              <Input value={r.amount} onChange={(_, d) => up({ amount: d.value })} contentAfter="g" aria-label={t('Gramm')} />
              <Text className={s.num}>{r.chosen && amount > 0 ? fmt(r.chosen.kcalPer100 * amount / 100) + ' kcal' : ''}</Text>
            </div>
          )
        })}
        <div><Button appearance="primary" disabled={busy || !rows.some(r => r.on && r.chosen)} onClick={save}>{t('Alle eintragen')}</Button></div>
      </>}
    </div>
  )
}
