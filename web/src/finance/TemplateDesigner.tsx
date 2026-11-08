import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Option, Spinner, Tab, TabList, Text, Textarea, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { Add20Regular, ArrowDown20Regular, ArrowUp20Regular, ChevronLeft20Regular, ChevronRight20Regular, Delete20Regular, ReOrderDotsVertical20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

export interface TplColumn { field: string; label: string; width: number }
export interface TplFact { label: string; value: string }
export interface TplBlock {
  type: string; text: string; bold: boolean; grey: boolean; size: number; align: string; gap: number; image?: string | null; imageWidth: number
  facts?: TplFact[] | null; columns?: TplColumn[] | null; cells?: TplBlock[][] | null
}
export interface TplLayout { margin: number; fontSize: number; header: TplBlock[]; body: TplBlock[]; footer: TplBlock[] }
export interface Template { id: number; name: string; isDefault: boolean; modified: string; layout: TplLayout }

type Region = 'header' | 'body' | 'footer'
/** Auswahl: Baustein im Bereich, optional in einer Spalte (cell) eines Spalten-Bausteins. */
interface Sel { index: number; cell?: number; sub?: number }

const useStyles = makeStyles({
  surface: { maxWidth: '98vw', width: '98vw', height: '94vh', maxHeight: '94vh', display: 'flex', flexDirection: 'column', boxSizing: 'border-box' },
  body: { display: 'flex', flexDirection: 'column', flex: 1, minHeight: 0, height: '100%' },
  content: { display: 'grid', gridTemplateColumns: '280px minmax(280px, 1fr) minmax(320px, 1.2fr)', gap: '14px', flex: 1, minHeight: 0, overflow: 'hidden',
    '@media (max-width: 1000px)': { gridTemplateColumns: '1fr', overflowY: 'auto' } },
  pane: { display: 'flex', flexDirection: 'column', gap: '8px', minHeight: 0, overflowY: 'auto', paddingRight: '4px' },
  block: { display: 'flex', alignItems: 'center', gap: '4px', padding: '4px 6px', borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`, cursor: 'pointer', minHeight: '32px',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  sel: { backgroundColor: tokens.colorNeutralBackground1Selected, borderTopColor: tokens.colorBrandStroke1, borderRightColor: tokens.colorBrandStroke1, borderBottomColor: tokens.colorBrandStroke1, borderLeftColor: tokens.colorBrandStroke1 },
  over: { boxShadow: `0 -2px 0 ${tokens.colorBrandStroke1}` },
  label: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  sub: { marginLeft: '14px', display: 'flex', flexDirection: 'column', gap: '4px' },
  row: { display: 'flex', gap: '6px', alignItems: 'end', flexWrap: 'wrap' },
  grow: { flex: '1 1 120px', minWidth: 0, '& input': { minWidth: 0, width: '100%' }, '& .fui-Input': { minWidth: 0, width: '100%' }, '& .fui-Dropdown': { minWidth: 0, width: '100%' } },
  page: { width: '100%', border: `1px solid ${tokens.colorNeutralStroke2}`, display: 'block', backgroundColor: '#fff' },
  bad: { color: tokens.colorPaletteRedForeground1 },
  keys: { display: 'flex', flexWrap: 'wrap', gap: '4px' },
})

const blockNames: Record<string, () => string> = {
  text: () => t('Text'), image: () => t('Bild'), recipient: () => t('Empfänger'), facts: () => t('Angabenliste'), table: () => t('Positionen'), total: () => t('Gesamtbetrag'),
  note: () => t('Hinweis nach § 19 UStG'), rule: () => t('Linie'), spacer: () => t('Abstand'), pagebreak: () => t('Seitenumbruch'), columns: () => t('Spalten'),
}
const fieldNames: Record<string, () => string> = { pos: () => t('Nummer der Position'), description: () => t('Leistung'), qty: () => t('Menge'), price: () => t('Einzelpreis'), total: () => t('Betrag') }

const newBlock = (type: string): TplBlock => ({
  type, text: type === 'total' ? 'Gesamtbetrag' : '', bold: false, grey: false, size: 0, align: 'left', gap: 10, image: null, imageWidth: 120,
  facts: type === 'facts' ? [{ label: '', value: '' }] : null,
  columns: type === 'table' ? [{ field: 'description', label: 'Leistung', width: 50 }, { field: 'qty', label: 'Menge', width: 15 }, { field: 'price', label: 'Einzelpreis', width: 18 }, { field: 'total', label: 'Betrag', width: 17 }] : null,
  cells: type === 'columns' ? [[], []] : null,
})
const MAIN = ['text', 'image', 'recipient', 'facts', 'table', 'total', 'note', 'rule', 'spacer', 'pagebreak', 'columns']
const CELL = ['text', 'image', 'recipient', 'facts', 'rule', 'spacer']
const OUTER = ['text', 'image', 'facts', 'rule', 'spacer', 'columns']

const summary = (b: TplBlock) => {
  if (b.type === 'text') return b.text.split('\n')[0] || t('Text')
  if (b.type === 'spacer') return `${t('Abstand')} ${b.gap}`
  return blockNames[b.type]?.() ?? b.type
}

export function TemplateDesigner({ template, onClose, onSaved }: { template: Partial<Template> & { layout: TplLayout }; onClose: () => void; onSaved: (x: Template) => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [name, setName] = useState(template.name ?? t('Neue Vorlage'))
  const [layout, setLayout] = useState<TplLayout>(template.layout)
  const [region, setRegion] = useState<Region>('body')
  const [sel, setSel] = useState<Sel | null>(null)
  const [keys, setKeys] = useState<string[]>([])
  const [kind, setKind] = useState('invoice')
  const [lines, setLines] = useState(3)
  const [page, setPage] = useState(1)
  const [preview, setPreview] = useState<{ pages: number; image: string } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [dragFrom, setDragFrom] = useState<number | null>(null)
  const [dragOver, setDragOver] = useState<number | null>(null)
  const textRef = useRef<HTMLTextAreaElement>(null)
  const seq = useRef(0)

  useEffect(() => { api.get<string[]>('/api/finance/templates/keys').then(setKeys).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Vorschau aus demselben Satz wie das PDF, kurz nach der letzten Änderung
  useEffect(() => {
    const mine = ++seq.current
    const timer = setTimeout(() => {
      setBusy(true)
      api.post<{ pages: number; image: string }>('/api/finance/templates/preview', { layout, kind, lines, page })
        .then(r => { if (mine === seq.current) { setPreview(r); setError(null); if (page > r.pages) setPage(r.pages) } })
        .catch(e => { if (mine === seq.current) setError(e instanceof Error ? e.message : String(e)) })
        .finally(() => { if (mine === seq.current) setBusy(false) })
    }, 450)
    return () => clearTimeout(timer)
  }, [layout, kind, lines, page])

  const list = layout[region]
  const setList = (next: TplBlock[]) => setLayout(l => ({ ...l, [region]: next }))
  const current: TplBlock | null = !sel ? null : sel.cell != null && sel.sub != null ? list[sel.index]?.cells?.[sel.cell]?.[sel.sub] ?? null : list[sel.index] ?? null
  const update = (patch: Partial<TplBlock>) => {
    if (!sel) return
    setList(list.map((b, i) => {
      if (i !== sel.index) return b
      if (sel.cell == null || sel.sub == null) return { ...b, ...patch }
      return { ...b, cells: (b.cells ?? []).map((c, n) => n !== sel.cell ? c : c.map((x, m) => m === sel.sub ? { ...x, ...patch } : x)) }
    }))
  }
  const add = (type: string) => {
    if (sel?.cell != null) {
      const cell = sel.cell
      setList(list.map((b, i) => i === sel.index ? { ...b, cells: (b.cells ?? []).map((c, n) => n === cell ? [...c, newBlock(type)] : c) } : b))
      setSel({ index: sel.index, cell, sub: list[sel.index]?.cells?.[cell]?.length ?? 0 })
      return
    }
    const at = sel ? sel.index + 1 : list.length
    setList([...list.slice(0, at), newBlock(type), ...list.slice(at)])
    setSel({ index: at })
  }
  const move = (from: number, to: number) => {
    if (to < 0 || to >= list.length || from === to) return
    const next = [...list]
    const [b] = next.splice(from, 1)
    next.splice(to, 0, b)
    setList(next)
    setSel({ index: to })
  }
  const remove = () => {
    if (!sel) return
    if (sel.cell != null && sel.sub != null) {
      setList(list.map((b, i) => i === sel.index ? { ...b, cells: (b.cells ?? []).map((c, n) => n === sel.cell ? c.filter((_, m) => m !== sel.sub) : c) } : b))
      setSel({ index: sel.index, cell: sel.cell })
    } else { setList(list.filter((_, i) => i !== sel.index)); setSel(null) }
  }
  const insertKey = (key: string) => {
    if (!current || current.type !== 'text') return
    const el = textRef.current
    const at = el?.selectionStart ?? current.text.length
    update({ text: current.text.slice(0, at) + `{${key}}` + current.text.slice(el?.selectionEnd ?? at) })
  }
  const pickImage = (file: File | undefined) => {
    if (!file) return
    if (file.size > 1024 * 1024) { setError(t('Das Bild ist zu groß (höchstens 1 MB).')); return }
    const r = new FileReader()
    r.onload = () => update({ image: String(r.result) })
    r.readAsDataURL(file)
  }
  const save = async () => {
    const body = { name, layout }
    try {
      const saved = template.id ? await api.put<Template>(`/api/finance/templates/${template.id}`, body) : await api.post<Template>('/api/finance/templates', body)
      notify(t('Vorlage gespeichert'))
      onSaved(saved)
    } catch (e) { fail(e) }
  }
  const number = (value: number, label: string, on: (n: number) => void, step = 1) =>
    <Field label={label} className={s.grow}><Input type="number" step={step} value={String(value)} onChange={(_, d) => on(Number(d.value))} /></Field>
  const addMenu = (types: string[]) => <Menu>
    <MenuTrigger disableButtonEnhancement><Button icon={<Add20Regular />}>{t('Baustein hinzufügen')}</Button></MenuTrigger>
    <MenuPopover><MenuList>{types.map(x => <MenuItem key={x} onClick={() => add(x)}>{blockNames[x]()}</MenuItem>)}</MenuList></MenuPopover>
  </Menu>

  const props = () => {
    if (!current) return <Text className={s.muted}>{t('Baustein wählen, um ihn zu bearbeiten.')}</Text>
    const fontRow = <div className={s.row}>
      {number(current.size, t('Schriftgröße'), n => update({ size: n }))}
      <Field label={t('Ausrichtung')} className={s.grow}>
        <Dropdown value={current.align === 'center' ? t('Mitte') : current.align === 'right' ? t('Rechts') : t('Links')} selectedOptions={[current.align]} onOptionSelect={(_, d) => update({ align: d.optionValue ?? 'left' })}>
          <Option value="left">{t('Links')}</Option><Option value="center">{t('Mitte')}</Option><Option value="right">{t('Rechts')}</Option>
        </Dropdown>
      </Field>
    </div>
    const styleRow = <div className={s.row}>
      <Checkbox label={t('Fett')} checked={current.bold} onChange={(_, d) => update({ bold: !!d.checked })} />
      <Checkbox label={t('Grau')} checked={current.grey} onChange={(_, d) => update({ grey: !!d.checked })} />
    </div>
    const keysView = <>
      <Text size={200} className={s.muted}>{t('Platzhalter')}: {t('Text in eckigen Klammern entfällt, wenn ein Platzhalter darin leer ist.')}</Text>
      <div className={s.keys}>{keys.map(k => <Button key={k} size="small" appearance="outline" onClick={() => insertKey(k)} disabled={current.type !== 'text'}>{k}</Button>)}</div>
    </>
    switch (current.type) {
      case 'text': return <>
        <Field label={t('Text')}><Textarea ref={textRef} rows={5} value={current.text} onChange={(_, d) => update({ text: d.value })} /></Field>
        {fontRow}{styleRow}{keysView}
      </>
      case 'image': return <>
        <Field label={t('Bild')} hint={t('PNG oder JPEG, höchstens 1 MB.')}><input type="file" accept="image/png,image/jpeg" onChange={e => pickImage(e.target.files?.[0])} /></Field>
        {current.image && <img src={current.image} alt="" style={{ maxWidth: 180, maxHeight: 90, objectFit: 'contain' }} />}
        <div className={s.row}>
          {number(current.imageWidth, t('Bildbreite'), n => update({ imageWidth: n }))}
          <Field label={t('Ausrichtung')} className={s.grow}>
            <Dropdown value={current.align === 'center' ? t('Mitte') : current.align === 'right' ? t('Rechts') : t('Links')} selectedOptions={[current.align]} onOptionSelect={(_, d) => update({ align: d.optionValue ?? 'left' })}>
              <Option value="left">{t('Links')}</Option><Option value="center">{t('Mitte')}</Option><Option value="right">{t('Rechts')}</Option>
            </Dropdown>
          </Field>
        </div>
      </>
      case 'recipient': case 'note': return <>{fontRow}{styleRow}<Text size={200} className={s.muted}>{t('Diesen Baustein gibt es nur einmal. Er ist Pflicht und lässt sich nicht weiter einstellen.')}</Text></>
      case 'facts': return <>
        {(current.facts ?? []).map((f, i) => <div key={i} className={s.row}>
          <Field label={i === 0 ? t('Bezeichnung') : undefined} className={s.grow}><Input aria-label={t('Bezeichnung')} value={f.label} onChange={(_, d) => update({ facts: (current.facts ?? []).map((x, n) => n === i ? { ...x, label: d.value } : x) })} /></Field>
          <Field label={i === 0 ? t('Wert') : undefined} className={s.grow}><Input aria-label={t('Wert')} value={f.value} onChange={(_, d) => update({ facts: (current.facts ?? []).map((x, n) => n === i ? { ...x, value: d.value } : x) })} /></Field>
          <Button appearance="subtle" icon={<Delete20Regular />} aria-label={t('Entfernen')} onClick={() => update({ facts: (current.facts ?? []).filter((_, n) => n !== i) })} />
        </div>)}
        <div><Button icon={<Add20Regular />} onClick={() => update({ facts: [...(current.facts ?? []), { label: '', value: '' }] })}>{t('Zeile hinzufügen')}</Button></div>
        {fontRow}{styleRow}
        <Text size={200} className={s.muted}>{t('Platzhalter')}: {keys.map(k => `{${k}}`).join(' ')}</Text>
      </>
      case 'table': return <>
        {(current.columns ?? []).map((c, i) => <div key={i} className={s.row}>
          <Field label={i === 0 ? t('Feld') : undefined} className={s.grow}>
            <Dropdown aria-label={t('Feld')} value={fieldNames[c.field]?.() ?? c.field} selectedOptions={[c.field]} onOptionSelect={(_, d) => update({ columns: (current.columns ?? []).map((x, n) => n === i ? { ...x, field: d.optionValue ?? x.field } : x) })}>
              {Object.keys(fieldNames).map(f => <Option key={f} value={f}>{fieldNames[f]()}</Option>)}
            </Dropdown>
          </Field>
          <Field label={i === 0 ? t('Bezeichnung') : undefined} className={s.grow}><Input aria-label={t('Bezeichnung')} value={c.label} onChange={(_, d) => update({ columns: (current.columns ?? []).map((x, n) => n === i ? { ...x, label: d.value } : x) })} /></Field>
          <Field label={i === 0 ? t('Breite') : undefined} style={{ width: 70 }}><Input aria-label={t('Breite')} type="number" value={String(c.width)} onChange={(_, d) => update({ columns: (current.columns ?? []).map((x, n) => n === i ? { ...x, width: Number(d.value) } : x) })} /></Field>
          <Button appearance="subtle" icon={<ArrowUp20Regular />} aria-label={t('Nach oben')} disabled={i === 0} onClick={() => { const a = [...(current.columns ?? [])]; [a[i - 1], a[i]] = [a[i], a[i - 1]]; update({ columns: a }) }} />
          <Button appearance="subtle" icon={<Delete20Regular />} aria-label={t('Entfernen')} onClick={() => update({ columns: (current.columns ?? []).filter((_, n) => n !== i) })} />
        </div>)}
        <div><Button icon={<Add20Regular />} onClick={() => update({ columns: [...(current.columns ?? []), { field: 'pos', label: 'Pos.', width: 8 }] })}>{t('Spalte hinzufügen')}</Button></div>
        {number(current.size, t('Schriftgröße'), n => update({ size: n }))}
      </>
      case 'total': return <>
        <Field label={t('Bezeichnung')}><Input value={current.text} onChange={(_, d) => update({ text: d.value })} /></Field>
        {number(current.size, t('Schriftgröße'), n => update({ size: n }))}
      </>
      case 'spacer': return number(current.gap, t('Höhe'), n => update({ gap: n }))
      case 'columns': return <div className={s.row}>
        {number(current.cells?.length ?? 2, t('Spalten'), n => {
          const count = Math.min(4, Math.max(1, Math.round(n || 1)))
          const c = [...(current.cells ?? [])]
          while (c.length < count) c.push([])
          update({ cells: c.slice(0, count) })
        })}
      </div>
      default: return null
    }
  }

  const blockRow = (b: TplBlock, i: number) => <div key={i}>
    <div className={mergeClasses(s.block, sel?.index === i && sel.sub == null && sel.cell == null && s.sel, dragOver === i && s.over)} onClick={() => setSel({ index: i })} draggable
      onDragStart={() => setDragFrom(i)} onDragEnd={() => { setDragFrom(null); setDragOver(null) }} onDragOver={e => { e.preventDefault(); setDragOver(i) }}
      onDrop={e => { e.preventDefault(); if (dragFrom != null) move(dragFrom, i); setDragFrom(null); setDragOver(null) }}>
      <ReOrderDotsVertical20Regular style={{ flexShrink: 0, cursor: 'grab' }} />
      <Text className={s.label}>{summary(b)}</Text>
      <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} aria-label={t('Nach oben')} disabled={i === 0} onClick={e => { e.stopPropagation(); move(i, i - 1) }} />
      <Button size="small" appearance="subtle" icon={<ArrowDown20Regular />} aria-label={t('Nach unten')} disabled={i === list.length - 1} onClick={e => { e.stopPropagation(); move(i, i + 1) }} />
    </div>
    {b.type === 'columns' && <div className={s.sub} style={{ marginTop: 4 }}>
      {(b.cells ?? []).map((c, n) => <div key={n} className={s.sub} style={{ marginLeft: 0 }}>
        <div className={mergeClasses(s.block, sel?.index === i && sel.cell === n && sel.sub == null && s.sel)} onClick={() => setSel({ index: i, cell: n })}><Text size={200} className={s.label}>{t('Spalte {n}', { n: n + 1 })}</Text></div>
        {c.map((x, m) => <div key={m} style={{ marginLeft: 12 }} className={mergeClasses(s.block, sel?.index === i && sel.cell === n && sel.sub === m && s.sel)} onClick={() => setSel({ index: i, cell: n, sub: m })}><Text className={s.label}>{summary(x)}</Text></div>)}
      </div>)}
    </div>}
  </div>

  const inCell = sel?.cell != null
  return <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
    <DialogSurface className={s.surface}>
      <DialogBody className={s.body}>
        <DialogTitle>{t('Vorlage')}</DialogTitle>
        <DialogContent className={s.content}>
          <div className={s.pane}>
            <Field label={t('Name der Vorlage')} required><Input value={name} onChange={(_, d) => setName(d.value)} /></Field>
            <div className={s.row}>
              {number(layout.margin, t('Seitenrand'), n => setLayout(l => ({ ...l, margin: n })))}
              {number(layout.fontSize, t('Grundschrift'), n => setLayout(l => ({ ...l, fontSize: n })), 0.5)}
            </div>
            <TabList size="small" selectedValue={region} onTabSelect={(_, d) => { setRegion(d.value as Region); setSel(null) }}>
              <Tab value="body">{t('Inhalt')}</Tab><Tab value="header">{t('Kopfzeile')}</Tab><Tab value="footer">{t('Fußzeile')}</Tab>
            </TabList>
            <div>{addMenu(inCell ? CELL : region === 'body' ? MAIN : OUTER)}</div>
            {list.map(blockRow)}
            <Text size={200} className={s.muted}>{t('Empfänger, Positionen, Gesamtbetrag und Hinweis nach § 19 UStG sind Pflicht.')} {t('Größen in Punkt, Schriftgröße 0 ist die Grundschrift.')}</Text>
          </div>
          <div className={s.pane}>
            {current && <div className={s.row} style={{ justifyContent: 'space-between' }}>
              <Text weight="semibold">{blockNames[current.type]?.()}</Text>
              <Button icon={<Delete20Regular />} onClick={remove}>{t('Entfernen')}</Button>
            </div>}
            {props()}
            <Text size={200} className={s.muted}>{t('Ändern betrifft nur neue Belege. Ausgestellte Belege behalten ihr PDF.')}</Text>
          </div>
          <div className={s.pane}>
            <div className={s.row}>
              <Dropdown aria-label={t('Art')} style={{ minWidth: 130 }} value={kind === 'offer' ? t('Angebot') : kind === 'cancel' ? t('Stornorechnung') : t('Rechnung')} selectedOptions={[kind]} onOptionSelect={(_, d) => setKind(d.optionValue ?? 'invoice')}>
                <Option value="invoice">{t('Rechnung')}</Option><Option value="offer">{t('Angebot')}</Option><Option value="cancel">{t('Stornorechnung')}</Option>
              </Dropdown>
              <Dropdown aria-label={t('Positionen')} style={{ minWidth: 170 }} value={t('Vorschau mit {n} Positionen', { n: lines })} selectedOptions={[String(lines)]} onOptionSelect={(_, d) => { setLines(Number(d.optionValue)); setPage(1) }}>
                {[3, 15, 40, 90].map(n => <Option key={n} value={String(n)} text={t('Vorschau mit {n} Positionen', { n })}>{t('Vorschau mit {n} Positionen', { n })}</Option>)}
              </Dropdown>
              <Button appearance="subtle" icon={<ChevronLeft20Regular />} aria-label={t('Vorige Seite')} disabled={page <= 1} onClick={() => setPage(p => p - 1)} />
              <Text>{t('Seite {page} von {pages}', { page, pages: preview?.pages ?? 1 })}</Text>
              <Button appearance="subtle" icon={<ChevronRight20Regular />} aria-label={t('Nächste Seite')} disabled={page >= (preview?.pages ?? 1)} onClick={() => setPage(p => p + 1)} />
              {busy && <Spinner size="tiny" />}
            </div>
            {error && <Text className={s.bad}>{error}</Text>}
            {preview && <img className={s.page} src={preview.image} alt={t('Vorschau der Vorlage')} />}
          </div>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" disabled={!name.trim()} onClick={save}>{t('Speichern')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}
