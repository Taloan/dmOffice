import { Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Select, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { Add20Regular, Delete20Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api, q } from '../api'
import { t } from '../i18n'
import { ATTRS, FilterAttr, knownValues } from './PhotoFilter'

const useStyles = makeStyles({
  surface: { maxWidth: '640px', width: '100%' },
  rows: { display: 'flex', flexDirection: 'column', gap: '10px', marginTop: '12px' },
  row: { border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusLarge, padding: '8px 10px' },
  head: { display: 'flex', alignItems: 'center', gap: '8px' },
  values: { maxHeight: '170px', overflowY: 'auto', display: 'flex', flexDirection: 'column', marginTop: '4px' },
  value: { display: 'flex', alignItems: 'center', gap: '4px' },
  count: { marginLeft: 'auto', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, paddingRight: '6px' },
  muted: { color: tokens.colorNeutralForeground3 },
  match: { marginTop: '10px', fontWeight: 600 },
})

export interface SmartRule { cols: FilterAttr[]; sel: string[][] }
type Facets = { total: number; values: [string, number][] }[]

/** Reihenfolge im Auswahlfeld: was man für Sammlungen am häufigsten braucht, zuerst. */
const ORDER: FilterAttr[] = ['rating', 'label', 'keyword', 'source', 'year', 'month', 'camera', 'lens', 'type', 'orientation', 'focal', 'aperture', 'exposure', 'iso']

/**
 * Smart-Album wie die smarten Sammlungen in Lightroom: Name und Bedingungen. Ein Foto gehört hinein, wenn es alle Bedingungen
 * erfüllt; innerhalb einer Bedingung genügt einer der gewählten Werte. Gleiche Regel wie die Filterleiste (cols/sel), deshalb
 * zählt der Server wie dort - die Werte einer Bedingung schon eingeschränkt durch die Bedingungen darüber.
 */
export function SmartAlbumDialog({ title, initialName = '', initial, onClose, onSave }: {
  title: string; initialName?: string; initial?: SmartRule | null; onClose: () => void; onSave: (name: string, rule: string) => Promise<void> | void
}) {
  const s = useStyles()
  const [name, setName] = useState(initialName)
  // Jede Art nur einmal (ältere Regeln konnten eine doppelt enthalten); gewählte Werte doppelter Arten zusammengefasst
  const [rule, setRule] = useState<SmartRule>(() => {
    if (!initial?.cols.length) return { cols: ['rating'], sel: [[]] }
    const cols: FilterAttr[] = [], sel: string[][] = []
    initial.cols.forEach((c, i) => {
      const k = cols.indexOf(c)
      if (k < 0) { cols.push(c); sel.push([...(initial.sel[i] ?? [])]) }
      else sel[k] = [...new Set([...sel[k], ...(initial.sel[i] ?? [])])]
    })
    return { cols, sel }
  })
  // Feste Kennung je Zeile: Die Art einer Zeile ändert sich beim Umschalten, die Zeile bleibt dieselbe
  const next = useRef(0)
  const [ids, setIds] = useState<number[]>(() => rule.cols.map(() => next.current++))
  const [facets, setFacets] = useState<Facets | null>(null)
  const [busy, setBusy] = useState(false)
  // Werte und Anzahlen vom Server; die zusätzliche letzte Spalte liefert, wie viele Fotos insgesamt passen
  const filter = JSON.stringify({ cols: rule.cols, sel: rule.sel })
  useEffect(() => {
    let gone = false
    setFacets(null)
    const timer = setTimeout(() => {
      api.get<{ cols: Facets }>('/api/gallery/timeline/facets?' + q({ cols: [...rule.cols, 'type'].join(','), filter }))
        .then(r => { if (!gone) setFacets(r.cols) }).catch(() => { if (!gone) setFacets([]) })
    }, 200)
    return () => { gone = true; clearTimeout(timer) }
  }, [filter]) // eslint-disable-line react-hooks/exhaustive-deps
  const matching = facets?.[rule.cols.length]?.total
  const free = useMemo(() => ORDER.filter(a => !rule.cols.includes(a)), [rule.cols])
  const change = (i: number, col: FilterAttr) => {
    if (rule.cols.some((c, k) => k !== i && c === col)) return
    setRule(r => ({ cols: r.cols.map((c, k) => k === i ? col : c), sel: r.sel.map((x, k) => k === i ? [] : x) }))
  }
  const remove = (i: number) => {
    setRule(r => ({ cols: r.cols.filter((_, k) => k !== i), sel: r.sel.filter((_, k) => k !== i) }))
    setIds(l => l.filter((_, k) => k !== i))
  }
  const add = () => {
    if (!free.length) return
    setRule(r => ({ cols: [...r.cols, free[0]], sel: [...r.sel, []] }))
    setIds(l => [...l, next.current++])
  }
  const toggle = (i: number, v: string) => setRule(r => ({ cols: r.cols, sel: r.sel.map((x, k) => k !== i ? x : x.includes(v) ? x.filter(y => y !== v) : [...x, v]) }))
  const ok = name.trim().length > 0 && rule.sel.some(x => x.length > 0)

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent>
            <Field label={t('Name')}><Input value={name} onChange={(_, d) => setName(d.value)} placeholder={t('z. B. Beste Fotos 2025')} autoFocus /></Field>
            <Text size={200} className={s.muted} block style={{ marginTop: 8 }}>
              {t('Ins Album kommen alle Fotos, die jede Bedingung erfüllen. Innerhalb einer Bedingung genügt einer der angehakten Werte. Neue passende Fotos erscheinen von selbst.')}
            </Text>
            <div className={s.rows}>
              {rule.cols.map((col, i) => {
                const attr = ATTRS[col]
                // Sortiert wie in der Filterleiste: neueste/höchste zuerst, Zahlen der Größe nach, sonst alphabetisch; „Unbekannt“ ans Ende
                const got = facets?.[i]?.values ?? []
                const vals = [...got, ...knownValues(col).filter(v => !got.some(x => x[0] === v)).map(v => [v, 0] as [string, number])].sort((a, b) => (a[0] === '') !== (b[0] === '') ? (a[0] === '' ? 1 : -1)
                  : attr.order === 'desc' ? b[0].localeCompare(a[0], undefined, { numeric: true })
                  : attr.order === 'num' ? Number(a[0]) - Number(b[0]) : a[0].localeCompare(b[0]))
                return (
                  <div key={ids[i]} className={s.row}>
                    <div className={s.head}>
                      <Select value={col} onChange={(_, d) => change(i, d.value as FilterAttr)} aria-label={t('Bedingung')}>
                        {[col, ...free].map(a => <option key={a} value={a}>{ATTRS[a].label()}</option>)}
                      </Select>
                      <Text size={200} className={s.muted} style={{ flex: 1 }}>{rule.sel[i].length ? t('{n} gewählt', { n: rule.sel[i].length }) : t('noch nichts gewählt')}</Text>
                      <Button appearance="subtle" icon={<Delete20Regular />} aria-label={t('Bedingung entfernen')} disabled={rule.cols.length === 1}
                        onClick={() => remove(i)} />
                    </div>
                    <div className={s.values}>
                      {facets == null && <Spinner size="tiny" />}
                      {facets != null && vals.length === 0 && <Text size={200} className={s.muted}>{t('Keine Werte – die Bedingungen darüber lassen nichts übrig.')}</Text>}
                      {vals.map(([v, n]) => (
                        <label key={v} className={s.value}>
                          <Checkbox checked={rule.sel[i].includes(v)} onChange={() => toggle(i, v)} />
                          <span>{v === '' ? t('Unbekannt') : attr.show ? attr.show(v) : v}</span>
                          <span className={s.count}>{n.toLocaleString()}</span>
                        </label>
                      ))}
                      {/* Gewählte Werte, die gerade nicht vorkommen, bleiben sichtbar und abwählbar */}
                      {rule.sel[i].filter(v => !vals.some(x => x[0] === v)).map(v => (
                        <label key={'x' + v} className={s.value}>
                          <Checkbox checked onChange={() => toggle(i, v)} />
                          <span>{v === '' ? t('Unbekannt') : attr.show ? attr.show(v) : v}</span>
                          <span className={s.count}>0</span>
                        </label>
                      ))}
                    </div>
                  </div>
                )
              })}
            </div>
            {free.length > 0 && rule.cols.length < 6 && (
              <Button icon={<Add20Regular />} style={{ marginTop: 10 }} onClick={add}>{t('Bedingung hinzufügen')}</Button>
            )}
            <div className={s.match}>{matching == null ? <Spinner size="tiny" /> : t('{n} Fotos und Videos passen', { n: matching.toLocaleString() })}</div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!ok || busy} onClick={async () => {
              setBusy(true)
              // Leere Bedingungen fallen weg
              const keep = rule.cols.map((c, i) => [c, rule.sel[i]] as const).filter(([, v]) => v.length > 0)
              try { await onSave(name.trim(), JSON.stringify({ cols: keep.map(x => x[0]), sel: keep.map(x => x[1]) })) } finally { setBusy(false) }
            }}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
