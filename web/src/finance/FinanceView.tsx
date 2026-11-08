import {
  Button, MessageBar, MessageBarActions, MessageBarBody, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Radio, RadioGroup, Spinner, Tab, TabList, Text, Textarea, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowDownload20Regular, ArrowSync20Regular, Checkmark20Regular, Copy20Regular, Delete20Regular, DocumentPdf20Regular, Edit20Regular, LockClosed20Regular, Money20Regular,
  ArrowUndo20Regular, DismissCircle20Regular, Search20Regular, PersonAdd20Regular, ArrowUpload20Regular, DocumentEdit20Regular, ClipboardTask20Regular,
  Receipt20Regular, People20Regular, Toolbox20Regular, DocumentBulletList20Regular, DataBarVertical20Regular, Settings20Regular,
  ArrowRepeatAll20Regular, Stop20Regular, SelectObject20Regular, Warning16Regular, Mail20Regular, MailArrowUp20Regular, Dismiss16Regular,
} from '@fluentui/react-icons'
import { createContext, useContext, useEffect, useMemo, useRef, useState } from 'react'
import { createPortal } from 'react-dom'
import { api } from '../api'
import { useApp } from '../App'
import { lang, locale, t } from '../i18n'
import { moneyText, parseMoney } from './money'
import { Template, TemplateDesigner, TplLayout } from './TemplateDesigner'
import { ColDef, useGridColumns } from '../shell/GridColumns'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { pressable, useNavStyles } from '../shell/nav'
import { askChoice, askConfirm } from '../shell/context'
import { handOverCompose } from '../mail/handoff'
import { featureOn } from '../api'
import { requestTresorOpen } from '../tresor/tresor'
import { parseDay, useRegionPick } from '../dms/DmsPick'
import { SidePane, useIsMobile } from '../shell/mobile'

interface Category { id: string; kind: 'income' | 'expense'; name: string; count: number }
/** Platzhalter für Einträge, die im DMS als Einnahme oder Ausgabe gekennzeichnet wurden: Kategorie muss noch zugeordnet werden. */
const isUnassigned = (id: string) => id === 'unassigned-income' || id === 'unassigned-expense'
interface Entry { id: number; kind: 'income' | 'expense'; date: string; amountCents: number; category: string; text: string; party: string; dmsDoc: number | null; dmsTresor: boolean; invoice: number | null; corrects: number | null; recurring?: number | null }
type Interval = 'week' | 'month' | 'quarter' | 'halfyear' | 'year'
/** Zeile der Einträge: gebucht oder geplanter Termin einer Wiederholung (planned, negative Kennung). */
type Row = Entry & { planned?: boolean }
interface Recurring { id: number; kind: 'income' | 'expense'; amountCents: number; category: string; text: string; party: string; interval: Interval; start: string; end: string | null; count: number | null; done: number; next: string | null; stopped: boolean; dmsDoc: number | null; dmsTresor: boolean }
interface Line { description: string; qtyMilli: number; unit: string; priceCents: number; totalCents: number }
interface Doc {
  invoiceType: '' | 'partial' | 'final'; refKind: string | null; billedCents: number
  id: number; kind: 'offer' | 'order' | 'invoice' | 'cancel'; state: 'draft' | 'issued'; number: string | null; docDate: string | null; serviceDate: string; dueDate: string | null; subject: string; intro: string; outro: string
  recipient: string; contactUid: string | null; totalCents: number; refId: number | null; refNumber: string | null; paidCents: number; cancelled: boolean; dmsDoc: number | null; customer: number | null; customerNumber: string; template: number | null; lines: Line[]
}
interface Customer { id: number; number: string; name: string; address: string; note: string; contactUid: string | null; docs: number; invoicedCents: number; email: string }
interface YearState { year: number; closed: boolean }
interface Summary {
  year: number; closed: boolean; income: number; expense: number; surplus: number
  categories: { id: string; name: string; kind: string; cents: number }[]; months: { month: number; income: number; expense: number }[]
}
interface MailTpl { subject: string; body: string; roles: string[]; docs: number[] }
interface Settings {
  name: string; address: string; taxNumber: string; vatId: string; contact: string; bank: string; iban: string; bic: string; paymentDays: number; offerDays: number; invoicePattern: string; offerPattern: string; orderPattern: string
  mailAccount: string; mailReplyTo: string; mailTemplates: Record<string, MailTpl>
}
/** Entwurf zu einem Beleg (GET /api/finance/docs/{id}/mail). */
interface MailDocRef { id: number; tresor: boolean; title: string; name: string; mime: string | null }
interface MailDraft {
  account: string | null; replyTo: string | null; to: string[]; subject: string; html: string; pdfName: string
  roles: { role: string; roleName: string; original: MailDocRef | null; current: MailDocRef | null }[]; docs: MailDocRef[]
}

/**
 * Beleg per E-Mail: Entwurf vom Server holen, bei Rollen mit inzwischen aktualisiertem Dokument fragen, festhalten, was mitgeht, PDF und
 * Anhänge laden und im Mailbereich als „Neue E-Mail“ öffnen. Gesendet wird erst dort. false: abgebrochen.
 */
async function mailDoc(docId: number): Promise<boolean> {
  const draft = await api.get<MailDraft>(`/api/finance/docs/${docId}/mail`)
  if (!draft.account) throw new Error(t('Es ist noch kein E-Mail-Konto eingerichtet.'))
  const chosen: { role: string; doc: MailDocRef }[] = []
  for (const r of draft.roles) {
    let pick = r.original ?? r.current
    if (r.original && r.current && r.current.id !== r.original.id) {
      const answer = await askChoice(t('Aktualisierte Version'), t('Für die Rolle „{role}“ liegt inzwischen eine aktualisierte Version vor: „{title}“.', { role: r.roleName, title: r.current.title || r.current.name }),
        { value: 'original', label: t('Ursprüngliche behalten') }, { value: 'current', label: t('Aktualisierte anhängen') })
      if (!answer) return false
      pick = answer === 'current' ? r.current : r.original
    }
    if (pick) chosen.push({ role: r.role, doc: pick })
  }
  await api.put(`/api/finance/docs/${docId}/mail`, { roles: chosen.map(c => ({ role: c.role, doc: c.doc.id, tresor: c.doc.tresor })) })
  const file = async (d: MailDocRef) => {
    const blob = await api.blob(`/api/dms/docs/${d.id}/content${d.tresor ? '?area=tresor' : ''}`)
    return new File([blob], d.name || (d.title || String(d.id)), { type: d.mime || blob.type })
  }
  const pdf = await api.blob(`/api/finance/docs/${docId}/pdf`)
  const extra = [...chosen.map(c => c.doc), ...draft.docs.filter(d => !chosen.some(c => c.doc.id === d.id && !c.doc.tresor))]
  const files = [new File([pdf], draft.pdfName, { type: 'application/pdf' }), ...await Promise.all(extra.map(file))]
  handOverCompose({ account: draft.account, to: draft.to, cc: [], bcc: [], subject: draft.subject, html: draft.html, files, replyTo: draft.replyTo ?? undefined, dirty: true })
  return true
}
interface ContactAddress { street: string; zip: string; city: string; country: string }
interface Contact { uid: string | null; displayName: string; org: string; addresses: ContactAddress[] }
interface DmsDocHit { id: number; title: string; originalName: string; docDate: string | null; correspondent?: string | null; amount?: number | null; trashed: boolean; inTresor?: boolean }
interface FinService { id: number; description: string; unit: string; priceCents: number }

type Page = 'income' | 'expense' | 'invoice' | 'order' | 'offer' | 'customers' | 'services' | 'templates' | 'summary' | 'settings'
const PAGES: Page[] = ['income', 'expense', 'invoice', 'order', 'offer', 'customers', 'services', 'templates', 'summary', 'settings']
type DocKind = 'offer' | 'order' | 'invoice'

/** Entwurf, der nach dem Wechsel auf eine andere Seite gleich im Editor aufgehen soll (Auftrag aus Angebot, Rechnung aus Auftrag usw.). */
let pendingEdit: number | null = null

/** Überschrift wie auf dem Beleg. */
const docHeading = (d: Pick<Doc, 'kind' | 'invoiceType'>) => d.kind === 'offer' ? t('Angebot') : d.kind === 'order' ? t('Auftrag') : d.kind === 'cancel' ? t('Stornorechnung')
  : d.invoiceType === 'partial' ? t('Abschlagsrechnung') : d.invoiceType === 'final' ? t('Schlussrechnung') : t('Rechnung')

const useStyles = makeStyles({
  root: { display: 'flex', flexDirection: 'column', height: '100%', minHeight: 0, width: '100%', minWidth: 0 },
  saldo: { display: 'flex', flexDirection: 'column', alignItems: 'flex-end', justifyContent: 'center', padding: '0 16px', flexShrink: 0, fontSize: '26px', fontWeight: 700, lineHeight: 1.1,
    fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap', '@media (max-width: 767px)': { fontSize: '16px', padding: '0 8px' } },
  saldoLabel: { fontSize: '12px', fontWeight: 400, color: tokens.colorNeutralForeground3 },
  split: { display: 'flex', flex: 1, minHeight: 0, minWidth: 0, position: 'relative' },
  side: { width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  tabs: { padding: '4px 16px 0', flexShrink: 0 },
  body: { flex: 1, minHeight: 0, overflowY: 'auto', padding: '12px 20px 24px', display: 'flex', flexDirection: 'column', gap: '12px', '@media (max-width: 767px)': { padding: '8px 12px 16px' } },
  bar: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  table: { display: 'flex', flexDirection: 'column', overflowX: 'auto', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, flexShrink: 0 },
  row: { display: 'grid', alignItems: 'center', gap: '8px', padding: '6px 10px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}`, minHeight: '32px', cursor: 'default', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  sel: { backgroundColor: tokens.colorNeutralBackground1Selected, ':hover': { backgroundColor: tokens.colorNeutralBackground1Selected } },
  head: { fontWeight: 600, color: tokens.colorNeutralForeground2, backgroundColor: tokens.colorNeutralBackground2, height: '32px', padding: '0 10px', ':hover': { backgroundColor: tokens.colorNeutralBackground2 } },
  cell: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', minWidth: 0 },
  num: { textAlign: 'right', fontVariantNumeric: 'tabular-nums' },
  cards: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  card: { padding: '10px 16px', minWidth: '150px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, display: 'flex', flexDirection: 'column', gap: '2px' },
  form: { display: 'flex', flexDirection: 'column', gap: '10px', maxWidth: '640px' },
  two: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '10px', '@media (max-width: 767px)': { gridTemplateColumns: '1fr' } },
  lineRow: { display: 'grid', gridTemplateColumns: 'minmax(160px, 1fr) 70px 70px 110px 100px 32px', gap: '6px', alignItems: 'start', '& > *': { minWidth: 0 }, '& input': { minWidth: 0, width: '100%' }, '& .fui-Input': { minWidth: 0, width: '100%' }, '@media (max-width: 767px)': { gridTemplateColumns: '1fr 1fr', paddingBottom: '10px', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` } },
  wide: { maxWidth: '860px', width: '95vw' },
  // Eintrag mit Beleg: links das Dokument, rechts das Formular; auf dem Handy untereinander
  withDoc: { maxWidth: 'min(1280px, 96vw)', width: '96vw' },
  docSplit: { display: 'grid', gridTemplateColumns: 'minmax(0, 1.2fr) minmax(320px, 1fr)', gap: '16px', alignItems: 'start',
    '@media (max-width: 900px)': { gridTemplateColumns: '1fr' } },
  docPages: { maxHeight: '72vh', overflowY: 'auto', padding: '4px', backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusMedium,
    '@media (max-width: 900px)': { maxHeight: '40vh' } },
  withPick: { display: 'flex', gap: '4px', alignItems: 'center', '& > :first-child': { flex: 1, minWidth: 0 } },
  bad: { color: tokens.colorPaletteRedForeground1 },
})

/** Platz ganz vorn im Menüband, an dem die jeweilige Seite ihre Knöpfe zeigt - der Neu-Knopf zuerst, wie „Neue E-Mail“ bei den Mails. */
const ActionSlot = createContext<HTMLElement | null>(null)
function PageActions({ children }: { children: React.ReactNode }) {
  const el = useContext(ActionSlot)
  return el ? createPortal(<>{children}<RibbonDivider /></>, el) : null
}

const fmtMoney = (cents: number) => (cents / 100).toLocaleString(locale, { style: 'currency', currency: 'EUR' })
const fmtDay = (d: string | null) => d ? new Date(d + 'T12:00:00').toLocaleDateString(locale) : ''
const today = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}` }
const plusDays = (iso: string, n: number) => { const d = new Date(iso + 'T12:00:00'); d.setDate(d.getDate() + n); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}` }
const parseQty = (text: string): number | null => {
  const v = text.trim().replace(',', '.')
  return /^\d+(\.\d{1,3})?$/.test(v) ? parseFloat(v) : null
}

/** Tabelle wie im Explorer: Sortieren, Breite, Reihenfolge, Spaltenauswahl. */
function Grid<T extends { id: number | string }>({ id, cols, defaults, rows, value, cell, selected, onPick, onOpen, empty, initialSort, phone }: {
  id: string; cols: Record<string, ColDef>; defaults: string[]; rows: T[]; value: (row: T, key: string) => string | number; cell: (row: T, key: string) => React.ReactNode
  selected?: number | string | null; onPick?: (row: T) => void; onOpen?: (row: T) => void; empty: string; initialSort: string
  /** Handy: statt breiter Tabelle je Eintrag Titel, Zeile darunter und Wert rechts */
  phone?: (row: T) => { title: React.ReactNode; sub: React.ReactNode; right?: React.ReactNode }
}) {
  const s = useStyles()
  const mobile = useIsMobile()
  const grid = useGridColumns(id, cols, defaults, '')
  const key = grid.sort?.key ?? initialSort
  const desc = grid.sort ? grid.sort.desc : true
  const sorted = useMemo(() => [...rows].sort((a, b) => {
    const x = value(a, key), y = value(b, key)
    const c = typeof x === 'number' && typeof y === 'number' ? x - y : String(x).localeCompare(String(y), locale, { sensitivity: 'base', numeric: true })
    return desc ? -c : c
  }), [rows, key, desc]) // eslint-disable-line react-hooks/exhaustive-deps
  if (mobile && phone) return (
    <div className={s.table}>
      {sorted.map(r => {
        const x = phone(r)
        return <div key={r.id} className={`${s.row} ${selected === r.id ? s.sel : ''}`} style={{ gridTemplateColumns: 'minmax(0, 1fr) auto' }} onClick={() => onPick?.(r)} onDoubleClick={() => onOpen?.(r)}>
          <span style={{ minWidth: 0 }}><div className={s.cell}>{x.title}</div><Text size={200} className={s.muted} block truncate wrap={false}>{x.sub}</Text></span>
          <span className={s.num}>{x.right}</span>
        </div>
      })}
      {rows.length === 0 && <Text className={s.muted} style={{ padding: 10 }}>{empty}</Text>}
    </div>
  )
  return (
    <div className={s.table}>
      <div className={`${s.row} ${s.head}`} style={{ gridTemplateColumns: grid.template, minWidth: 'max-content' }} onContextMenu={grid.openMenu}>{grid.cells}</div>
      {grid.columnMenu}
      {sorted.map(r => (
        <div key={r.id} className={`${s.row} ${selected === r.id ? s.sel : ''}`} style={{ gridTemplateColumns: grid.template, minWidth: 'max-content' }} onClick={() => onPick?.(r)} onDoubleClick={() => onOpen?.(r)}>
          {grid.cols.map(c => <span key={c.key} className={s.cell}>{cell(r, c.key)}</span>)}
        </div>
      ))}
      {rows.length === 0 && <Text className={s.muted} style={{ padding: 10 }}>{empty}</Text>}
    </div>
  )
}

/** Auswahl aus einer Liste: Suchfeld und Tabelle wie im Explorer. Doppelklick oder „Übernehmen“ wählt den Eintrag. */
function PickDialog<T extends { id: number | string }>({ title, load, grid, onPick, onClose }: {
  title: string
  /** Einträge zum Suchtext (leer: alle oder die neuesten) */
  load: (q: string) => Promise<T[]>
  grid: Omit<Parameters<typeof Grid<T>>[0], 'rows' | 'selected' | 'onPick' | 'onOpen'>
  onPick: (row: T) => void
  onClose: () => void
}) {
  const s = useStyles()
  const { fail } = useApp()
  const [q, setQ] = useState('')
  const [rows, setRows] = useState<T[] | null>(null)
  const [sel, setSel] = useState<T | null>(null)
  const seq = useRef(0)
  useEffect(() => {
    // Erst wenn das Tippen ruht; eine ältere, später eintreffende Antwort überschreibt die neuere nicht
    const n = ++seq.current
    const h = setTimeout(() => load(q).then(r => { if (n === seq.current) { setRows(r); setSel(null) } }).catch(e => { if (n === seq.current) fail(e) }), q ? 250 : 0)
    return () => clearTimeout(h)
  }, [q]) // eslint-disable-line react-hooks/exhaustive-deps
  return <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
    <DialogSurface className={s.wide} style={{ maxWidth: 900 }}>
      <DialogBody>
        <DialogTitle>{title}</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <Input autoFocus contentBefore={<Search20Regular />} placeholder={t('Suchen')} value={q} onChange={(_, d) => setQ(d.value)}
            onKeyDown={e => { if (e.key === 'Enter' && rows?.length === 1) { e.preventDefault(); onPick(rows[0]) } }} />
          <div style={{ maxHeight: '55vh', overflowY: 'auto' }}>
            {rows == null ? <Spinner size="small" /> : <Grid {...grid} rows={rows} selected={sel?.id} onPick={setSel} onOpen={onPick} />}
          </div>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" disabled={!sel} onClick={() => sel && onPick(sel)}>{t('Übernehmen')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}

const CONTACT_COLS: Record<string, ColDef> = {
  name: { label: () => t('Name'), width: 220, sort: 'name' },
  org: { label: () => t('Firma'), width: 220, sort: 'org' },
  city: { label: () => t('Ort'), width: 160, sort: 'city' },
}

/** Kontakt aus den Kontakten wählen; gesucht wird in Name, Firma und Ort. */
function ContactPicker({ onPick, onClose }: { onPick: (c: Contact) => void; onClose: () => void }) {
  const all = useRef<Promise<Contact[]> | null>(null)
  const city = (c: Contact) => [c.addresses[0]?.zip, c.addresses[0]?.city].filter(Boolean).join(' ')
  const load = async (q: string) => {
    all.current ??= api.get<Contact[]>('/api/pim/contacts')
    const list = (await all.current).map((c, i) => ({ ...c, id: c.uid ?? `${i}` }))
    const words = q.toLowerCase().split(/\s+/).filter(Boolean)
    return list.filter(c => { const hay = `${c.displayName} ${c.org} ${city(c)}`.toLowerCase(); return words.every(w => hay.includes(w)) })
  }
  return <PickDialog title={t('Kontakt wählen')} load={load} onClose={onClose} onPick={c => onPick(c)} grid={{
    id: 'finance-pick-contact', cols: CONTACT_COLS, defaults: ['name', 'org', 'city'], initialSort: 'name', empty: t('Keine passenden Kontakte.'),
    phone: c => ({ title: c.displayName, sub: [c.org !== c.displayName ? c.org : '', city(c)].filter(Boolean).join(' · ') }),
    value: (c, k) => k === 'org' ? c.org : k === 'city' ? city(c) : c.displayName,
    cell: (c, k) => k === 'org' ? c.org : k === 'city' ? city(c) : c.displayName,
  }} />
}

const SERVICE_COLS: Record<string, ColDef> = {
  description: { label: () => t('Leistung'), width: 360, sort: 'description' },
  unit: { label: () => t('Einheit'), width: 100, sort: 'unit' },
  price: { label: () => t('Einzelpreis'), width: 120, sort: 'price' },
}
const serviceGrid = (num: string) => ({
  cols: SERVICE_COLS, defaults: ['description', 'unit', 'price'], initialSort: 'description',
  phone: (x: FinService) => ({ title: x.description.split('\n')[0], sub: x.unit, right: fmtMoney(x.priceCents) }),
  value: (x: FinService, k: string) => k === 'unit' ? x.unit : k === 'price' ? x.priceCents : x.description,
  cell: (x: FinService, k: string) => k === 'unit' ? x.unit : k === 'price' ? <div className={num}>{fmtMoney(x.priceCents)}</div> : x.description.split('\n')[0],
})

/** Vorab erfasste Leistung wählen; gesucht wird im Text und in der Einheit. */
function ServicePicker({ onPick, onClose }: { onPick: (x: FinService) => void; onClose: () => void }) {
  const s = useStyles()
  const all = useRef<Promise<FinService[]> | null>(null)
  const load = async (q: string) => {
    all.current ??= api.get<FinService[]>('/api/finance/services')
    const words = q.toLowerCase().split(/\s+/).filter(Boolean)
    return (await all.current).filter(x => { const hay = `${x.description} ${x.unit}`.toLowerCase(); return words.every(w => hay.includes(w)) })
  }
  return <PickDialog title={t('Leistung hinzufügen')} load={load} onClose={onClose} onPick={onPick}
    grid={{ id: 'finance-pick-service', empty: t('Keine passenden Leistungen. Leistungen lassen sich im Reiter Leistungen anlegen.'), ...serviceGrid(s.num) }} />
}

function YearSelect({ years, year, onChange }: { years: YearState[]; year: number; onChange: (y: number) => void }) {
  return <Dropdown style={{ minWidth: 110 }} aria-label={t('Jahr')} value={String(year)} selectedOptions={[String(year)]} onOptionSelect={(_, d) => d.optionValue && onChange(Number(d.optionValue))}>
    {years.map(y => <Option key={y.year} value={String(y.year)}>{String(y.year)}</Option>)}
  </Dropdown>
}

// ---------------- Einnahmen und Ausgaben ----------------

const ENTRY_COLS: Record<string, ColDef> = {
  date: { label: () => t('Datum'), width: 100, sort: 'date' },
  kind: { label: () => t('Art'), width: 90, sort: 'kind' },
  category: { label: () => t('Kategorie'), width: 190, sort: 'category' },
  text: { label: () => t('Text'), width: 300, sort: 'text' },
  party: { label: () => t('Geschäftspartner'), width: 180, sort: 'party' },
  amount: { label: () => t('Betrag'), width: 110, sort: 'amount' },
  doc: { label: () => t('Beleg'), width: 70, sort: 'doc' },
}

function EntriesPage({ kind, year, years, setYear, reloadYears, cats }: { kind: 'income' | 'expense'; year: number; years: YearState[]; setYear: (y: number) => void; reloadYears: () => void; cats: Category[] }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [rows, setRows] = useState<Entry[]>([])
  const [picked, setPicked] = useState<Row | null>(null)
  const [edit, setEdit] = useState<Partial<Entry> | null>(null)
  // Wiederholungen: Eigenschaft der Einträge. Der nächste Termin jeder laufenden steht als „geplant“ in der Liste (zählt nicht mit)
  const [series, setSeries] = useState<Recurring[]>([])
  const [editSeries, setEditSeries] = useState<Recurring | null>(null)
  const closed = years.find(y => y.year === year)?.closed ?? false
  const load = () => {
    api.get<Entry[]>(`/api/finance/entries?year=${year}`).then(setRows).catch(fail)
    api.get<Recurring[]>('/api/finance/recurring').then(setSeries).catch(() => setSeries([]))
  }
  useEffect(() => { load(); setPicked(null) }, [year]) // eslint-disable-line react-hooks/exhaustive-deps
  const name = (id: string) => cats.find(c => c.id === id)?.name ?? ''
  const mine = rows.filter(r => r.kind === kind)
  const total = mine.reduce((a, r) => a + r.amountCents, 0)
  const planned: Row[] = series.filter(x => x.kind === kind && x.next && Number(x.next.slice(0, 4)) === year).map(x => ({
    id: -x.id, kind, date: x.next!, amountCents: x.amountCents, category: x.category, text: x.text, party: x.party,
    dmsDoc: null, dmsTresor: false, invoice: null, corrects: null, recurring: x.id, planned: true,
  }))
  const shown: Row[] = [...mine, ...planned]
  const seriesOf = (r: Row | null) => r?.recurring ? series.find(x => x.id === r.recurring) : undefined
  const pickedSeries = seriesOf(picked)
  const repeatText = (x: Recurring) => [intervalName(x.interval),
    x.next ? t('nächster Termin {date}', { date: fmtDay(x.next) }) : t('beendet'),
    x.end ? t('bis {date}', { date: fmtDay(x.end) }) : x.count ? t('{done} von {n} gebucht', { done: x.done, n: x.count }) : t('ohne Ende')].join(' · ')
  const stopSeries = async (x: Recurring) => {
    if (!await askConfirm(t('Wiederholung beenden'), t('Es wird nichts mehr gebucht. Gebuchte Einträge bleiben.'), t('Beenden'))) return
    try { await api.post(`/api/finance/recurring/${x.id}/stop`, {}); setPicked(null); load() } catch (e) { fail(e) }
  }
  const remove = async (e: Entry) => {
    if (!await askConfirm(t('Eintrag löschen'), t('Der Eintrag wird gelöscht. Das Protokoll hält das fest. Ein verknüpfter Beleg bleibt im DMS fixiert.'), t('Löschen'))) return
    try { await api.del(`/api/finance/entries/${e.id}`); setPicked(null); load(); reloadYears() } catch (x) { fail(x) }
  }
  const openReceipt = (e: { dmsDoc: number | null; dmsTresor: boolean }) => openDmsDoc(e.dmsDoc, e.dmsTresor)
  const openDmsDoc = async (doc: number | null, inTresor: boolean) => {
    if (!doc) return
    // Fenster gleich öffnen: Nach dem Warten auf den Tresor ließe der Browser es sonst nicht mehr zu
    const win = window.open('about:blank', '_blank')
    try {
      if (inTresor && !await requestTresorOpen()) { win?.close(); return }
      const area = inTresor ? '?area=tresor' : ''
      await api.get(`/api/dms/docs/${doc}${area}`)
      if (win) win.location.href = `/api/dms/docs/${doc}/content${area}`
    } catch (x) { win?.close(); fail(x) }
  }
  const correct = async (e: Entry) => {
    if (!await askConfirm(t('Eintrag korrigieren'), t('Es entsteht ein Gegeneintrag mit dem umgekehrten Betrag und dem heutigen Datum. Der ursprüngliche Eintrag bleibt stehen.'), t('Korrigieren'))) return
    try { await api.post(`/api/finance/entries/${e.id}/correct`, { date: today() }); notify(t('Gegeneintrag angelegt')); setYear(new Date().getFullYear()); load(); reloadYears() } catch (x) { fail(x) }
  }
  return <>
    <PageActions>
      <RibbonButton primary icon={<Add20Regular />} label={kind === 'income' ? t('Neue Einnahme') : t('Neue Ausgabe')} disabled={closed} onClick={() => setEdit({ kind })} />
      <RibbonDivider />
      <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={!picked || !!picked.invoice || closed}
        onClick={() => picked && (picked.planned ? pickedSeries && setEditSeries(pickedSeries) : setEdit(picked))} />
      <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!picked || picked.planned || !!picked.invoice || closed} onClick={() => picked && remove(picked)} />
      <RibbonButton icon={<ArrowUndo20Regular />} label={t('Korrigieren')} disabled={!picked || picked.planned || !!picked.corrects} onClick={() => picked && correct(picked)} />
      <RibbonButton icon={<DocumentPdf20Regular />} label={t('Beleg öffnen')} disabled={!picked?.dmsDoc} onClick={() => picked && openReceipt(picked)} />
      <RibbonDivider />
      <RibbonButton icon={<ArrowRepeatAll20Regular />} label={t('Wiederholung ändern')} disabled={!pickedSeries || pickedSeries.stopped || !pickedSeries.next}
        onClick={() => pickedSeries && setEditSeries(pickedSeries)} />
      <RibbonButton icon={<DocumentPdf20Regular />} label={t('Grundlage öffnen')} disabled={!pickedSeries?.dmsDoc}
        onClick={() => pickedSeries && openReceipt(pickedSeries)} />
      <RibbonButton icon={<Stop20Regular />} label={t('Wiederholung beenden')} disabled={!pickedSeries?.next} onClick={() => pickedSeries && stopSeries(pickedSeries)} />
    </PageActions>
    <div className={s.bar}>
      <YearSelect years={years} year={year} onChange={setYear} />
      {closed && <Text className={s.muted}><LockClosed20Regular style={{ verticalAlign: 'middle', marginRight: 4 }} />{t('Dieses Jahr ist abgeschlossen.')}</Text>}
    </div>
    <div className={s.cards}>
      <div className={s.card}><Text size={200} className={s.muted}>{kind === 'income' ? t('Einnahmen') : t('Ausgaben')}</Text><Text weight="semibold">{fmtMoney(total)}</Text></div>
    </div>
    <Grid id={`finance-${kind}`} cols={ENTRY_COLS} defaults={['date', 'category', 'text', 'party', 'amount', 'doc']} rows={shown} selected={picked?.id} onPick={setPicked} initialSort="date"
      onOpen={r => { if (r.planned) { const x = seriesOf(r); if (x) setEditSeries(x) } else if (!r.invoice && !closed) setEdit(r); else if (r.dmsDoc) openReceipt(r) }}
      empty={kind === 'income' ? t('Für dieses Jahr gibt es noch keine Einnahmen.') : t('Für dieses Jahr gibt es noch keine Ausgaben.')}
      phone={r => ({ title: r.text || name(r.category), sub: [fmtDay(r.date), r.kind === 'income' ? t('Einnahme') : t('Ausgabe'), r.party].filter(Boolean).join(' · '), right: fmtMoney(r.amountCents) })}
      value={(r, k) => k === 'amount' ? r.amountCents : k === 'date' ? r.date : k === 'kind' ? r.kind : k === 'category' ? name(r.category) : k === 'party' ? r.party : k === 'doc' ? (r.dmsDoc ? 1 : 0) : r.text}
      cell={(r, k) => {
        // Geplanter Termin einer Wiederholung: blass, noch nicht gebucht
        if (r.planned) switch (k) {
          case 'date': return <span className={s.muted}>{fmtDay(r.date)}</span>
          case 'amount': return <div className={mergeClasses(s.num, s.muted)}>{fmtMoney(r.amountCents)}</div>
          case 'category': return <span className={s.muted}>{name(r.category)}</span>
          case 'party': return <span className={s.muted}>{r.party}</span>
          case 'doc': return seriesOf(r)?.dmsDoc ? <span className={s.muted}>{t('Grundlage')}</span> : ''
          case 'kind': return <span className={s.muted}>{r.kind === 'income' ? t('Einnahme') : t('Ausgabe')}</span>
          default: return <span className={s.muted} title={t('Geplant, noch nicht gebucht')}><ArrowRepeatAll20Regular style={{ verticalAlign: 'middle', marginRight: 4 }} />{r.text} · {t('geplant')}</span>
        }
        switch (k) {
          case 'date': return fmtDay(r.date)
          case 'kind': return r.kind === 'income' ? t('Einnahme') : t('Ausgabe')
          case 'category': return isUnassigned(r.category)
            ? <span title={t('Aus dem DMS gebucht. Bitte eine Kategorie zuordnen.')} style={{ color: tokens.colorPaletteMarigoldForeground1 }}>
              <Warning16Regular style={{ verticalAlign: 'middle', marginRight: 4 }} />{name(r.category)}</span>
            : name(r.category)
          case 'party': return r.party
          case 'amount': return <div className={s.num}>{fmtMoney(r.amountCents)}</div>
          case 'doc': return r.dmsDoc ? (r.dmsTresor ? t('im Tresor') : t('ja')) : seriesOf(r)?.dmsDoc ? <span className={s.muted} title={t('Kein eigener Beleg. Die Wiederholung hat ein Dokument als Grundlage.')}>{t('Grundlage')}</span> : ''
          default: return r.recurring ? <span title={t('Wiederkehrend')}><ArrowRepeatAll20Regular style={{ verticalAlign: 'middle', marginRight: 4 }} />{r.text}</span> : r.text
        }
      }} />
    {edit && <EntryDialog entry={edit} cats={cats} onClose={() => setEdit(null)}
      partOf={(x => x ? { text: repeatText(x), onEdit: x.next ? () => { setEdit(null); setEditSeries(x) } : undefined } : undefined)(seriesOf(edit as Row))}
      onSaved={date => {
        setEdit(null); setPicked(null); reloadYears()
        const y = Number(date.slice(0, 4)); if (date && y !== year && y <= new Date().getFullYear()) setYear(y); else load()
      }} />}
    {editSeries && <EntryDialog entry={{ kind }} series={editSeries} cats={cats} onClose={() => setEditSeries(null)}
      onSaved={() => { setEditSeries(null); setPicked(null); load(); reloadYears() }} />}
  </>
}

const INTERVALS: [Interval, () => string][] = [
  ['week', () => t('Wöchentlich')], ['month', () => t('Monatlich')], ['quarter', () => t('Vierteljährlich')], ['halfyear', () => t('Halbjährlich')], ['year', () => t('Jährlich')],
]
/** Betrag aus markiertem Text: der letzte Betrag mit zwei Nachkommastellen („Summe 1.234,56 €“), sonst die ganze Angabe. */
function amountIn(text: string): number | null {
  const found = text.match(/-?\d{1,3}(?:[.\s']\d{3})*[.,]\d{2}(?!\d)|-?\d+[.,]\d{2}(?!\d)/g)
  return parseMoney(found ? found[found.length - 1].replace(/[\s']/g, '') : text)
}

type EntryPick = 'date' | 'amount' | 'text' | 'party'

const intervalName = (i: Interval) => INTERVALS.find(x => x[0] === i)?.[1]() ?? ''

/**
 * Einnahme oder Ausgabe erfassen. Ein neuer Eintrag lässt sich wiederkehrend anlegen (series bearbeitet einen solchen): Gebucht wird je Termin ein
 * Eintrag, sobald der Tag erreicht ist. Nach der ersten Buchung stehen erster Termin und Rhythmus fest.
 */
function EntryDialog({ entry, series, partOf, cats, onClose, onSaved }: { entry: Partial<Entry>; series?: Recurring; cats: Category[]; onClose: () => void; onSaved: (date: string) => void
  /** Gebuchter Eintrag einer Wiederholung: Angaben dazu und Weg zum Ändern */
  partOf?: { text: string; onEdit?: () => void }
}) {
  const s = useStyles()
  const { fail } = useApp()
  const kind = series?.kind ?? entry.kind ?? 'income'
  const mine = cats.filter(c => c.kind === kind && (!isUnassigned(c.id) || c.id === (series?.category ?? entry.category)))
  if (series) entry = { kind, date: series.start, amountCents: series.amountCents, category: series.category, text: series.text, party: series.party, dmsDoc: series.dmsDoc, dmsTresor: series.dmsTresor }
  const [repeat, setRepeat] = useState<Interval | ''>(series?.interval ?? '')
  const [endMode, setEndMode] = useState<'none' | 'date' | 'count'>(series?.end ? 'date' : series?.count ? 'count' : 'none')
  const [endDate, setEndDate] = useState(series?.end ?? '')
  const [count, setCount] = useState(series?.count ? String(series.count) : '12')
  const fixed = !!series && series.done > 0
  const [date, setDate] = useState(entry.date ?? today())
  const [amount, setAmount] = useState(entry.amountCents != null ? moneyText(entry.amountCents) : '')
  const [category, setCategory] = useState(entry.category ?? mine.find(c => !isUnassigned(c.id))?.id ?? '')
  const [text, setText] = useState(entry.text ?? '')
  const [party, setParty] = useState(entry.party ?? '')
  const [doc, setDoc] = useState<number | null>(entry.dmsDoc ?? null)
  const [docTresor, setDocTresor] = useState(entry.dmsTresor ?? false)
  const [error, setError] = useState<string | null>(null)
  const [taken, setTaken] = useState<string[]>([])
  // Werte aus dem gewählten DMS-Dokument; nur in Felder, die noch leer sind. Das Datum nur, solange es nicht geändert wurde.
  const takeFrom = (h: Omit<DmsDocHit, 'id'>) => {
    const got: string[] = []
    if (h.amount != null && !amount.trim()) { setAmount(moneyText(h.amount)); setError(null); got.push(t('Betrag')) }
    // Bei einer Wiederholung ist das Datum des Dokuments (Vertrag, Abo) nicht der erste Termin
    if (h.docDate && !repeat && date === (entry.date ?? today())) { setDate(h.docDate); got.push(t('Datum')) }
    const title = h.title || h.originalName
    if (title && !text.trim()) { setText(title); got.push(t('Text')) }
    if (h.correspondent && !party.trim()) { setParty(h.correspondent); got.push(t('Geschäftspartner')) }
    return got
  }
  const [endError, setEndError] = useState<string | null>(null)
  // Beleg daneben anzeigen; Feld wählen und im Dokument einen Rahmen um den Wert ziehen (wie im DMS)
  const { notify } = useApp()
  const [pages, setPages] = useState(0)
  useEffect(() => {
    setPages(0)
    if (!doc) return
    api.get<{ count: number }>(`/api/dms/docs/${doc}/pages${docTresor ? '?area=tresor' : ''}`).then(r => setPages(Math.min(r.count, 20))).catch(() => setPages(0))
  }, [doc, docTresor])
  const pick = useRegionPick<EntryPick>(doc ?? 0, docTresor, (f, v) => {
    if (f === 'date') {
      const d = parseDay(v)
      if (!d) { notify(t('Kein Datum erkannt: „{text}“', { text: v })); return false }
      setDate(d)
    } else if (f === 'amount') {
      const c = amountIn(v)
      if (c == null || c === 0) { notify(t('Kein Betrag erkannt: „{text}“', { text: v })); return false }
      setAmount(moneyText(Math.abs(c))); setError(null)
    } else if (f === 'text') setText(v)
    else setParty(v)
    return true
  })
  const showDoc = !!doc && pages > 0
  const src = (n: number, width: number) => `/api/dms/docs/${doc}/page/${n}?width=${width}${docTresor ? '&area=tresor' : ''}`
  const pickButton = (f: EntryPick) => showDoc && <Button appearance={pick.field === f ? 'primary' : 'subtle'} icon={<SelectObject20Regular />} disabled={pick.busy}
    aria-pressed={pick.field === f} title={pick.field === f ? t('Im Dokument einen Rahmen um den Text ziehen') : t('Aus dem Dokument übernehmen')}
    aria-label={t('Aus dem Dokument übernehmen')} onClick={() => pick.choose(f)} />
  const save = async () => {
    const amountCents = parseMoney(amount)
    if (amountCents == null || amountCents === 0) { setError(t('Bitte einen Betrag angeben.')); return }
    try {
      if (repeat) {
        const n = Number(count)
        if (endMode === 'count' && (!Number.isInteger(n) || n < 1 || n > 1000)) { setEndError(t('Bitte eine Anzahl von 1 bis 1000 angeben.')); return }
        if (endMode === 'date' && !endDate) { setEndError(t('Bitte ein Enddatum angeben.')); return }
        const body = { kind, amountCents, category, text, party, interval: repeat, start: date, end: endMode === 'date' ? endDate : null, count: endMode === 'count' ? n : null, dmsDoc: doc, dmsTresor: docTresor }
        if (series) await api.put(`/api/finance/recurring/${series.id}`, body); else await api.post('/api/finance/recurring', body)
        onSaved(date)
        return
      }
      const body = { kind, date, amountCents, category, text, party, dmsDoc: doc, dmsTresor: docTresor }
      onSaved((entry.id ? await api.put<Entry>(`/api/finance/entries/${entry.id}`, body) : await api.post<Entry>('/api/finance/entries', body)).date)
    } catch (e) { fail(e) }
  }
  return <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
    <DialogSurface className={showDoc ? s.withDoc : undefined}>
      <DialogBody>
        <DialogTitle>{repeat ? (kind === 'income' ? t('Wiederkehrende Einnahme') : t('Wiederkehrende Ausgabe')) : kind === 'income' ? t('Einnahme') : t('Ausgabe')}</DialogTitle>
        <DialogContent className={showDoc ? s.docSplit : undefined}>
          {showDoc && <div className={s.docPages}>{pick.pagesView(Array.from({ length: pages }, (_, i) => i), src, 1400)}</div>}
          <div className={s.form}>
          {showDoc && <Text size={200} className={s.muted}>{pick.busy ? t('Text wird gelesen …') : pick.field ? t('Im Dokument einen Rahmen um den Text ziehen') : t('Mit dem Knopf neben einem Feld lässt sich der Wert im Dokument markieren und übernehmen.')}</Text>}
          <div className={s.two}>
            <Field label={repeat ? t('Erster Termin') : t('Datum')} hint={repeat ? (fixed ? t('Steht nach der ersten Buchung fest.') : t('Termine bis heute werden beim Speichern gebucht.')) : t('Der Tag, an dem das Geld geflossen ist.')} required>
              <div className={s.withPick}><Input type="date" value={date} disabled={fixed} onChange={(_, d) => setDate(d.value)} />{pickButton('date')}</div>
            </Field>
            <Field label={t('Betrag')} required validationMessage={error ?? undefined}>
              <div className={s.withPick}><Input value={amount} inputMode="decimal" contentAfter="€" onChange={(_, d) => { setAmount(d.value); setError(null) }} />{pickButton('amount')}</div>
            </Field>
          </div>
          <Field label={t('Kategorie')} required>
            <Dropdown value={mine.find(c => c.id === category)?.name ?? ''} selectedOptions={[category]} onOptionSelect={(_, d) => d.optionValue && setCategory(d.optionValue)}>
              {mine.map(c => <Option key={c.id} value={c.id}>{c.name}</Option>)}
            </Dropdown>
          </Field>
          <Field label={t('Text')}><div className={s.withPick}><Input value={text} onChange={(_, d) => setText(d.value)} />{pickButton('text')}</div></Field>
          <Field label={t('Geschäftspartner')}><div className={s.withPick}><Input value={party} onChange={(_, d) => setParty(d.value)} />{pickButton('party')}</div></Field>
          {partOf && <div className={s.bar}>
            <ArrowRepeatAll20Regular />
            <Text size={200} style={{ flex: 1 }}>{t('Teil einer Wiederholung: {text}', { text: partOf.text })}</Text>
            {partOf.onEdit && <Button size="small" onClick={partOf.onEdit}>{t('Wiederholung ändern')}</Button>}
          </div>}
          {/* Wiederholen nur beim Anlegen oder für einen wiederkehrenden Eintrag; ein gebuchter Eintrag bleibt ein einzelner */}
          {!entry.id && <div className={s.two}>
            <Field label={t('Wiederholen')}>
              <Dropdown disabled={fixed} value={repeat ? intervalName(repeat) : t('Keine Wiederholung')} selectedOptions={[repeat]} onOptionSelect={(_, d) => setRepeat((d.optionValue ?? '') as Interval | '')}>
                {!series && <Option value="">{t('Keine Wiederholung')}</Option>}
                {INTERVALS.map(([v, label]) => <Option key={v} value={v}>{label()}</Option>)}
              </Dropdown>
            </Field>
            {repeat && <Field label={t('Ende')} validationMessage={endError ?? undefined}>
              <RadioGroup value={endMode} onChange={(_, d) => { setEndMode(d.value as 'none' | 'date' | 'count'); setEndError(null) }}>
                <Radio value="none" label={t('Ohne Ende')} />
                <div className={s.bar}><Radio value="date" label={t('Am')} />
                  <Input type="date" value={endDate} disabled={endMode !== 'date'} onChange={(_, d) => { setEndDate(d.value); setEndError(null) }} /></div>
                <div className={s.bar}><Radio value="count" label={t('Nach')} />
                  <Input style={{ width: 80 }} value={count} inputMode="numeric" disabled={endMode !== 'count'} onChange={(_, d) => { setCount(d.value); setEndError(null) }} />
                  <Text>{t('Buchungen')}</Text></div>
              </RadioGroup>
            </Field>}
          </div>}
          <DocPicker basis={!!repeat} value={doc} tresor={docTresor} onChange={(id, inTresor, hit) => { setDoc(id); setDocTresor(inTresor); setTaken(hit ? takeFrom(hit) : []) }} />
          {taken.length > 0 && <Text size={200} className={s.muted}>{t('Aus dem Beleg übernommen: {fields}. Bitte prüfen.', { fields: taken.join(', ') })}</Text>}
          {repeat && series && series.done > 0 && <Text size={200} className={s.muted}>{t('Änderungen gelten für die künftigen Buchungen. Gebuchte Einträge bleiben unverändert.')}</Text>}
          </div>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" onClick={save}>{t('Speichern')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}

/** Beleg aus dem DMS wählen, aus der Ablage oder dem offenen Tresor. Das gewählte Dokument wird beim Speichern fixiert. */
function DocPicker({ basis, value, tresor, onChange }: { basis?: boolean; value: number | null; tresor: boolean; onChange: (id: number | null, tresor: boolean, hit?: Omit<DmsDocHit, 'id'>) => void }) {
  const s = useStyles()
  const [query, setQuery] = useState('')
  const [known, setKnown] = useState(false)
  const [picking, setPicking] = useState(false)
  const label = (d: Omit<DmsDocHit, 'id'>) => [d.title || d.originalName, fmtDay(d.docDate), d.inTresor ? t('Tresor') : ''].filter(Boolean).join(' · ')
  useEffect(() => {
    if (!value) { setKnown(false); return }
    setKnown(true)
    // Beleg im geschlossenen Tresor: Der Titel ist dann nicht lesbar, die Verknüpfung bleibt. Erst fragen, sonst bäte die Anfrage um das Tresor-Passwort
    const load = async () => {
      if (tresor && !(await api.get<{ tresorOpen: boolean }>('/api/dms/state')).tresorOpen) { setQuery(t('Beleg im Tresor')); return }
      setQuery(label({ ...await api.get<DmsDocHit>(`/api/dms/docs/${value}${tresor ? '?area=tresor' : ''}`), inTresor: tresor }))
    }
    load().catch(() => setQuery(tresor ? t('Beleg im Tresor') : t('Beleg nicht gefunden')))
  }, [value, tresor]) // eslint-disable-line react-hooks/exhaustive-deps
  // Ablage und offener Tresor zusammen; die Kennung im Tresor kann dieselbe Zahl sein wie in der Ablage
  const load = async (text: string) => {
    const params = `limit=100&sort=imported&desc=true${text.trim() ? '&q=' + encodeURIComponent(text.trim()) : ''}`
    const open = await api.get<{ items: DmsDocHit[] }>(`/api/dms/docs?${params}`)
    const state = await api.get<{ tresorOpen: boolean }>('/api/dms/state')
    const inside = state.tresorOpen ? await api.get<{ items: DmsDocHit[] }>(`/api/dms/docs?area=tresor&${params}`).then(r => r.items.map(d => ({ ...d, inTresor: true }))).catch(() => []) : []
    // Korrespondent kommt als Kennung; die Namen stehen in den Listen des DMS
    const names = new Map<string, string>()
    await api.get<{ id: string; name: string }[]>('/api/dms/lists').then(l => l.forEach(e => names.set(e.id, e.name))).catch(() => {})
    return [...open.items, ...inside].filter(d => !d.trashed)
      .map(d => ({ ...d, correspondent: d.correspondent ? names.get(d.correspondent) ?? '' : '', docId: d.id, id: (d.inTresor ? 't' : 'a') + d.id }))
  }
  const fixHint = tresor ? t('Das Dokument wird fixiert. Im Tresor gilt das für alle Wege in dmOffice, solange der Tresor offen ist. Zum Öffnen des Belegs muss der Tresor offen sein.')
    : t('Das Dokument wird fixiert: Es lässt sich nicht mehr ändern oder löschen.')
  // Wiederholung: ein Dokument für alle Buchungen (Vertrag, Abo); der Beleg je Buchung kommt an den gebuchten Eintrag
  return <Field label={basis ? t('Grundlage aus dem DMS') : t('Beleg aus dem DMS')}
    hint={basis ? t('Zum Beispiel Vertrag oder Abo. Gilt für alle Buchungen. Jede Buchung kann zusätzlich einen eigenen Beleg haben.') + ' ' + fixHint : fixHint}>
    <div style={{ display: 'flex', gap: 6 }}>
      <Input style={{ flex: 1, minWidth: 0 }} readOnly placeholder={basis ? t('Kein Dokument gewählt') : t('Kein Beleg gewählt')} value={query} onClick={() => setPicking(true)} />
      <Button icon={<Search20Regular />} onClick={() => setPicking(true)}>{t('Auswählen')}</Button>
      {(value || known) && <Button onClick={() => { onChange(null, false); setQuery('') }}>{t('Entfernen')}</Button>}
    </div>
    {picking && <PickDialog title={t('Beleg aus dem DMS')} load={load} onClose={() => setPicking(false)}
      onPick={h => { onChange(h.docId, !!h.inTresor, h); setQuery(label(h)); setPicking(false) }} grid={{
        id: 'finance-pick-dms', cols: DMS_PICK_COLS, defaults: ['title', 'correspondent', 'date', 'amount', 'area'], initialSort: 'date', empty: t('Keine passenden Dokumente.'),
        phone: h => ({ title: h.title || h.originalName, sub: [fmtDay(h.docDate), h.correspondent, h.inTresor ? t('Tresor') : ''].filter(Boolean).join(' · '), right: h.amount != null ? fmtMoney(h.amount) : '' }),
        value: (h, k) => k === 'correspondent' ? h.correspondent ?? '' : k === 'date' ? h.docDate ?? '' : k === 'amount' ? h.amount ?? -1 : k === 'area' ? (h.inTresor ? 1 : 0) : h.title || h.originalName,
        cell: (h, k) => k === 'correspondent' ? h.correspondent ?? '' : k === 'date' ? fmtDay(h.docDate) : k === 'amount' ? <div className={s.num}>{h.amount != null ? fmtMoney(h.amount) : ''}</div>
          : k === 'area' ? (h.inTresor ? t('Tresor') : t('Ablage')) : h.title || h.originalName,
      }} />}
  </Field>
}

const DMS_PICK_COLS: Record<string, ColDef> = {
  title: { label: () => t('Titel'), width: 320, sort: 'title' },
  correspondent: { label: () => t('Korrespondent'), width: 180, sort: 'correspondent' },
  date: { label: () => t('Dokumentdatum'), width: 120, sort: 'date' },
  amount: { label: () => t('Betrag'), width: 110, sort: 'amount' },
  area: { label: () => t('Ort'), width: 90, sort: 'area' },
}

// ---------------- Angebote und Rechnungen ----------------

const DOC_COLS: Record<string, ColDef> = {
  number: { label: () => t('Nummer'), width: 130, sort: 'number' },
  date: { label: () => t('Datum'), width: 100, sort: 'date' },
  recipient: { label: () => t('Empfänger'), width: 240, sort: 'recipient' },
  subject: { label: () => t('Betreff'), width: 240, sort: 'subject' },
  total: { label: () => t('Betrag'), width: 110, sort: 'total' },
  due: { label: () => t('Fällig'), width: 100, sort: 'due' },
  status: { label: () => t('Stand'), width: 150, sort: 'status' },
}

function statusOf(d: Doc): string {
  if (d.state === 'draft') return t('Entwurf')
  if (d.kind === 'cancel') return t('Stornorechnung')
  if (d.kind === 'offer') return t('Ausgestellt')
  if (d.kind === 'order') return d.billedCents >= d.totalCents && d.totalCents > 0 ? t('Berechnet') : d.billedCents > 0 ? t('Teilweise berechnet') : t('Bestätigt')
  if (d.cancelled) return t('Storniert')
  if (d.paidCents >= d.totalCents) return t('Bezahlt')
  if (d.dueDate && d.dueDate < today()) return t('Überfällig')
  if (d.paidCents > 0) return t('Teilweise bezahlt')
  return t('Offen')
}

function DocsPage({ kind, onChanged }: { kind: DocKind; onChanged: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  // null: lädt noch (sonst stünde kurz „Noch keine …“ da)
  const [rows, setRows] = useState<Doc[] | null>(null)
  const [picked, setPicked] = useState<Doc | null>(null)
  const [edit, setEdit] = useState<Doc | 'new' | null>(null)
  const [paying, setPaying] = useState<Doc | null>(null)
  const load = () => api.get<Doc[]>(`/api/finance/docs?kind=${kind}`).then(list => { setRows(list); setPicked(p => p ? list.find(x => x.id === p.id) ?? null : null) }).catch(fail)
  useEffect(() => {
    load(); setPicked(null)
    // Auf einer anderen Seite angelegter Entwurf: gleich bearbeiten
    if (pendingEdit != null) { const id = pendingEdit; pendingEdit = null; api.get<Doc>(`/api/finance/docs/${id}`).then(setEdit).catch(fail) }
  }, [kind]) // eslint-disable-line react-hooks/exhaustive-deps
  const issued =(r: { doc: Doc; dmsCopy: boolean; dmsPending: boolean }, text: string) => {
    notify(r.dmsCopy ? text : r.dmsPending ? t('{text} Die Kopie erscheint beim nächsten Öffnen des Tresors im DMS, dort noch nicht fixiert. Das PDF steht hier bereit.', { text })
      : t('{text} Im DMS wurde keine Kopie angelegt. Das PDF steht hier weiterhin bereit.', { text }))
    setPicked(r.doc); load()
  }
  const issue = async (d: Doc, thenMail = false) => {
    if (!await askConfirm(t('Beleg ausstellen'), t('Der Beleg erhält seine Nummer und lässt sich danach nicht mehr ändern oder löschen. Eine Kopie wird nach dem Standardprofil im DMS abgelegt und fixiert.'), t('Ausstellen'))) return
    try { issued(await api.post(`/api/finance/docs/${d.id}/issue`), t('Ausgestellt.')) } catch (e) { fail(e); return }
    if (thenMail) await sendMail(d)
  }
  const [mailing, setMailing] = useState(false)
  const sendMail = async (d: Doc) => {
    setMailing(true)
    try { await mailDoc(d.id) } catch (e) { fail(e) } finally { setMailing(false) }
  }
  const cancel = async (d: Doc) => {
    if (!await askConfirm(t('Rechnung stornieren'), t('Es entsteht eine Stornorechnung mit eigener Nummer und dem heutigen Datum. Die Rechnung selbst bleibt unverändert erhalten.'), t('Stornieren'))) return
    try { issued(await api.post(`/api/finance/docs/${d.id}/cancel`, { date: today() }), t('Storniert.')) } catch (e) { fail(e) }
  }
  const removeDraft = async (d: Doc) => {
    if (!await askConfirm(t('Entwurf löschen'), t('Der Entwurf wird endgültig gelöscht.'), t('Löschen'))) return
    try { await api.del(`/api/finance/docs/${d.id}`); setPicked(null); load() } catch (e) { fail(e) }
  }
  // Neuer Entwurf öffnet sich im Editor, auf der Seite seiner Belegart
  const opened = (draft: Doc) => {
    notify(t('Entwurf angelegt'))
    const page = draft.kind === 'cancel' ? 'invoice' : draft.kind
    if (page === kind) { load(); setEdit(draft) } else { pendingEdit = draft.id; location.hash = `#/finance/${page}` }
  }
  const copy = async (d: Doc, to: DocKind) => {
    try { opened(await api.post<Doc>(`/api/finance/docs/${d.id}/copy`, { kind: to })) } catch (e) { fail(e) }
  }
  const final = async (d: Doc) => {
    try { opened(await api.post<Doc>(`/api/finance/docs/${d.id}/final`)) } catch (e) { fail(e) }
  }
  const [partialFor, setPartialFor] = useState<Doc | null>(null)
  const [fromOffer, setFromOffer] = useState(false)
  // Grundlage: für Aufträge die Angebote, für Rechnungen Angebote und Aufträge, auch Entwürfe
  const loadOffers = async (q: string) => {
    const words = q.toLowerCase().split(/\s+/).filter(Boolean)
    const [offers, orders] = await Promise.all([api.get<Doc[]>('/api/finance/docs?kind=offer'), kind === 'invoice' ? api.get<Doc[]>('/api/finance/docs?kind=order') : Promise.resolve([])])
    return [...orders, ...offers]
      .filter(d => { const hay = `${docHeading(d)} ${d.number ?? ''} ${d.recipient} ${d.subject}`.toLowerCase(); return words.every(w => hay.includes(w)) })
  }
  // Ausstellen nur anbieten, wenn alles da ist; sonst sagen, was fehlt
  const [settings, setSettings] = useState<Settings | null>(null)
  useEffect(() => { api.get<{ settings: Settings }>('/api/finance/settings').then(r => setSettings(r.settings)).catch(() => {}) }, [])
  const [full, setFull] = useState<Doc | null>(null)
  useEffect(() => {
    setFull(null)
    if (picked?.state === 'draft') api.get<Doc>(`/api/finance/docs/${picked.id}`).then(setFull).catch(() => {})
  }, [picked]) // eslint-disable-line react-hooks/exhaustive-deps
  const missingSettings = !settings ? [] : [
    ...(!settings.name.trim() ? [t('Name des Unternehmens')] : []), ...(!settings.address.trim() ? [t('Anschrift des Unternehmens')] : []),
    ...(picked && picked.kind === 'invoice' && !settings.taxNumber.trim() && !settings.vatId.trim() ? [t('Steuernummer oder USt-IdNr.')] : []),
  ]
  const missingDoc = !full ? [] : [
    ...(!full.recipient.trim() ? [t('Kunde mit Anschrift')] : []), ...(!full.docDate ? [t('Datum')] : []),
    ...(full.kind === 'invoice' && !full.serviceDate.trim() ? [t('Leistungsdatum')] : []),
    ...(full.lines.length === 0 ? [t('mindestens eine Position')] : full.lines.some(l => !l.description.trim()) ? [t('Text bei jeder Position')] : []),
  ]
  const missing = [...missingSettings, ...missingDoc]
  const open = (d: Doc) => d.state === 'draft' ? openEdit(d) : window.open(`/api/finance/docs/${d.id}/pdf`, '_blank')
  const openEdit = (d: Doc) => api.get<Doc>(`/api/finance/docs/${d.id}`).then(setEdit).catch(fail)
  return <>
    {/* Feste Knöpfe je Belegart; was für den gewählten Beleg nicht geht, ist ausgegraut */}
    <PageActions>
      <RibbonButton primary icon={<Add20Regular />} label={kind === 'offer' ? t('Neues Angebot') : kind === 'order' ? t('Neuer Auftrag') : t('Neue Rechnung')} onClick={() => setEdit('new')} />
      {kind === 'invoice' && <RibbonButton icon={<Copy20Regular />} label={t('Aus Angebot oder Auftrag')} onClick={() => setFromOffer(true)} />}
      {kind === 'order' && <RibbonButton icon={<Copy20Regular />} label={t('Aus Angebot')} onClick={() => setFromOffer(true)} />}
      <RibbonDivider />
      <RibbonButton icon={<DocumentPdf20Regular />} label={picked?.state === 'draft' ? t('Vorschau') : 'PDF'} disabled={!picked} onClick={() => picked && window.open(`/api/finance/docs/${picked.id}/pdf`, '_blank')} />
      <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={picked?.state !== 'draft'} onClick={() => picked && openEdit(picked)} />
      <RibbonButton icon={<Checkmark20Regular />} label={t('Ausstellen')} disabled={picked?.state !== 'draft' || missing.length > 0}
        title={picked?.state === 'draft' && missing.length ? t('Zum Ausstellen fehlt: {list}', { list: missing.join(', ') }) : t('Ausstellen')} onClick={() => picked && issue(picked)} />
      {/* Ausgestellter Beleg als Mail-Entwurf mit PDF und Anhängen der Vorlage; gesendet wird im Mailbereich */}
      {picked?.state === 'issued'
        ? <RibbonButton icon={<Mail20Regular />} label={t('Per E-Mail senden')} disabled={mailing || picked.kind === 'cancel'} onClick={() => sendMail(picked)} />
        : <RibbonButton icon={<MailArrowUp20Regular />} label={t('Ausstellen und senden')} disabled={picked?.state !== 'draft' || missing.length > 0 || mailing}
          title={picked?.state === 'draft' && missing.length ? t('Zum Ausstellen fehlt: {list}', { list: missing.join(', ') }) : t('Ausstellen und als E-Mail-Entwurf öffnen')}
          onClick={() => picked && issue(picked, true)} />}
      <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={picked?.state !== 'draft'} onClick={() => picked && removeDraft(picked)} />
      <RibbonDivider />
      <RibbonButton icon={<Copy20Regular />} label={t('Duplizieren')} disabled={!picked || picked.kind === 'cancel' || !!picked.invoiceType} onClick={() => picked && copy(picked, picked.kind as DocKind)} />
      {kind === 'offer' && <RibbonButton icon={<Add20Regular />} label={t('Auftrag daraus')} disabled={!picked} onClick={() => picked && copy(picked, 'order')} />}
      {kind !== 'invoice' && <RibbonButton icon={<Add20Regular />} label={t('Rechnung daraus')} disabled={!picked} onClick={() => picked && copy(picked, 'invoice')} />}
      {kind === 'order' && <>
        <RibbonButton icon={<Add20Regular />} label={t('Abschlagsrechnung')} disabled={picked?.state !== 'issued'} onClick={() => picked && setPartialFor(picked)} />
        <RibbonButton icon={<Add20Regular />} label={t('Schlussrechnung')} disabled={picked?.state !== 'issued'} onClick={() => picked && final(picked)} />
      </>}
      {kind === 'invoice' && <>
        <RibbonButton icon={<Money20Regular />} label={t('Zahlung erfassen')} disabled={!picked || picked.state !== 'issued' || picked.kind !== 'invoice' || picked.cancelled || picked.paidCents >= picked.totalCents} onClick={() => picked && setPaying(picked)} />
        <RibbonButton icon={<DismissCircle20Regular />} label={t('Stornieren')} disabled={!picked || picked.state !== 'issued' || picked.kind !== 'invoice' || picked.cancelled} onClick={() => picked && cancel(picked)} />
      </>}
    </PageActions>
    <Grid id={`finance-${kind}`} cols={DOC_COLS} defaults={['number', 'date', 'recipient', 'subject', 'total', 'due', 'status']} rows={rows ?? []} selected={picked?.id} onPick={setPicked} onOpen={open} initialSort="date"
      empty={rows == null ? '' : kind === 'offer' ? t('Noch keine Angebote.') : kind === 'order' ? t('Noch keine Aufträge.') : t('Noch keine Rechnungen.')}
      phone={r => ({ title: r.number ?? t('Entwurf'), sub: [fmtDay(r.docDate), r.recipient.split('\n')[0], statusOf(r)].filter(Boolean).join(' · '), right: fmtMoney(r.totalCents) })}
      value={(r, k) => k === 'total' ? r.totalCents : k === 'number' ? r.number ?? '' : k === 'date' ? r.docDate ?? '' : k === 'recipient' ? r.recipient : k === 'subject' ? r.subject : k === 'due' ? r.dueDate ?? '' : statusOf(r)}
      cell={(r, k) => {
        switch (k) {
          case 'number': return r.number ?? <span className={s.muted}>{t('Entwurf')}</span>
          case 'date': return fmtDay(r.docDate)
          case 'recipient': return r.recipient.split('\n')[0]
          case 'subject': return r.kind === 'cancel' ? t('Storniert {number}', { number: r.refNumber ?? '' }) : r.invoiceType ? `${docHeading(r)} · ${r.subject}` : r.subject
          case 'total': return <div className={s.num}>{fmtMoney(r.totalCents)}</div>
          case 'due': return fmtDay(r.dueDate)
          default: return statusOf(r)
        }
      }} />
    {/* Unter der Liste: erscheint erst mit der Auswahl eines Entwurfs und darf die Zeilen nicht verschieben */}
    {picked?.state === 'draft' && missing.length > 0 && <MessageBar intent="info" layout="multiline">
      <MessageBarBody>{t('Zum Ausstellen fehlt: {list}', { list: missing.join(', ') })}</MessageBarBody>
      {missingSettings.length > 0 && <MessageBarActions><Button onClick={() => { location.hash = '#/finance/settings' }}>{t('Einstellungen öffnen')}</Button></MessageBarActions>}
    </MessageBar>}
    {edit && <DocDialog kind={edit === 'new' ? kind : edit.kind === 'cancel' ? 'invoice' : edit.kind} doc={edit === 'new' ? null : edit} onClose={() => setEdit(null)} onSaved={d => { setEdit(null); setPicked(d); load(); onChanged() }} />}
    {fromOffer && <PickDialog title={kind === 'order' ? t('Auftrag aus Angebot') : t('Rechnung aus Angebot oder Auftrag')} load={loadOffers} onClose={() => setFromOffer(false)}
      onPick={o => { setFromOffer(false); copy(o, kind) }} grid={{
        id: 'finance-pick-source', cols: PICK_SOURCE_COLS, defaults: ['art', 'number', 'date', 'recipient', 'subject', 'total', 'status'], initialSort: 'date',
        empty: kind === 'order' ? t('Keine passenden Angebote.') : t('Keine passenden Angebote oder Aufträge.'),
        phone: r => ({ title: `${docHeading(r)} ${r.number ?? ''}`, sub: [fmtDay(r.docDate), r.recipient.split('\n')[0], r.subject].filter(Boolean).join(' · '), right: fmtMoney(r.totalCents) }),
        value: (r, k) => k === 'art' ? docHeading(r) : k === 'total' ? r.totalCents : k === 'number' ? r.number ?? '' : k === 'date' ? r.docDate ?? '' : k === 'recipient' ? r.recipient : k === 'subject' ? r.subject : statusOf(r),
        cell: (r, k) => k === 'art' ? docHeading(r) : k === 'number' ? r.number ?? t('Entwurf') : k === 'date' ? fmtDay(r.docDate) : k === 'recipient' ? r.recipient.split('\n')[0] : k === 'subject' ? r.subject
          : k === 'total' ? <div className={s.num}>{fmtMoney(r.totalCents)}</div> : statusOf(r),
      }} />}
    {partialFor && <PartialDialog source={partialFor} onClose={() => setPartialFor(null)} onCreated={d => { setPartialFor(null); opened(d) }} />}
    {paying && <PayDialog doc={paying} onClose={() => setPaying(null)} onDone={() => { setPaying(null); notify(t('Zahlung erfasst')); load(); onChanged() }} />}
  </>
}

const PICK_SOURCE_COLS: Record<string, ColDef> = { art: { label: () => t('Art'), width: 110, sort: 'art' }, ...DOC_COLS }

/** Abschlagsrechnung zu einem Auftrag: Prozent der Auftragssumme oder fester Betrag. */
function PartialDialog({ source, onClose, onCreated }: { source: Doc; onClose: () => void; onCreated: (d: Doc) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [mode, setMode] = useState<'percent' | 'amount'>('percent')
  const [value, setValue] = useState('')
  const [error, setError] = useState<string | null>(null)
  const percent = parseQty(value)
  const amountCents = mode === 'percent' ? (percent != null ? Math.round(source.totalCents * percent / 100) : null) : parseMoney(value)
  const create = async () => {
    if (amountCents == null || amountCents <= 0) { setError(mode === 'percent' ? t('Bitte einen Prozentsatz angeben.') : t('Bitte einen Betrag angeben.')); return }
    try { onCreated(await api.post<Doc>(`/api/finance/docs/${source.id}/partial`, mode === 'percent' ? { percent } : { amountCents })) } catch (e) { fail(e) }
  }
  return <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
    <DialogSurface>
      <DialogBody>
        <DialogTitle>{t('Abschlagsrechnung zu {number}', { number: source.number ?? '' })}</DialogTitle>
        <DialogContent className={s.form}>
          <Text>{t('Auftragssumme {total}, davon berechnet {billed}.', { total: fmtMoney(source.totalCents), billed: fmtMoney(source.billedCents) })}</Text>
          <Field label={t('Abschlag')}>
            <Dropdown value={mode === 'percent' ? t('Prozent der Auftragssumme') : t('Fester Betrag')} selectedOptions={[mode]} onOptionSelect={(_, d) => { setMode(d.optionValue as 'percent' | 'amount'); setValue(''); setError(null) }}>
              <Option value="percent">{t('Prozent der Auftragssumme')}</Option>
              <Option value="amount">{t('Fester Betrag')}</Option>
            </Dropdown>
          </Field>
          <Field label={mode === 'percent' ? t('Prozent') : t('Betrag')} validationMessage={error ?? undefined}
            hint={mode === 'percent' && amountCents != null ? t('Ergibt {amount}.', { amount: fmtMoney(amountCents) }) : undefined}>
            <Input autoFocus inputMode="decimal" value={value} contentAfter={mode === 'percent' ? '%' : '€'} onChange={(_, d) => { setValue(d.value); setError(null) }} />
          </Field>
          <Text size={200} className={s.muted}>{t('Es entsteht ein Entwurf. Die Schlussrechnung zieht alle ausgestellten Abschlagsrechnungen ab.')}</Text>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" onClick={create}>{t('Entwurf anlegen')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}

interface LineDraft { description: string; qty: string; unit: string; price: string }

function DocDialog({ kind, doc, onClose, onSaved }: { kind: DocKind; doc: Doc | null; onClose: () => void; onSaved: (d: Doc) => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [date, setDate] = useState(doc?.docDate ?? today())
  const [service, setService] = useState(doc?.serviceDate ?? '')
  const [due, setDue] = useState(doc?.dueDate ?? '')
  const [subject, setSubject] = useState(doc?.subject ?? '')
  const [intro, setIntro] = useState(doc?.intro ?? '')
  const [outro, setOutro] = useState(doc?.outro ?? '')
  const [recipient, setRecipient] = useState(doc?.recipient ?? '')
  const [uid, setUid] = useState<string | null>(doc?.contactUid ?? null)
  const [customer, setCustomer] = useState<number | null>(doc?.customer ?? null)
  const [customers, setCustomers] = useState<Customer[]>([])
  const [templates, setTemplates] = useState<Template[]>([])
  const [template, setTemplate] = useState<number | null>(doc?.template ?? null)
  useEffect(() => { api.get<Template[]>('/api/finance/templates').then(setTemplates).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { api.get<Customer[]>('/api/finance/customers').then(setCustomers).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const pickCustomer = (c: Customer) => { setCustomer(c.id); setUid(c.contactUid); setRecipient([c.name, c.address].filter(Boolean).join('\n')) }
  // Frei eingegebene Anschrift als Kunden anlegen: erste Zeile Name, der Rest Anschrift; Kundennummer vergibt dmOffice
  const saveAsCustomer = async () => {
    const [name, ...rest] = recipient.trim().split('\n')
    try {
      const c = await api.post<Customer>('/api/finance/customers', { name: name.trim(), address: rest.join('\n').trim(), contactUid: uid })
      setCustomers(list => [...list, c]); setCustomer(c.id)
      notify(t('Kunde {name} angelegt ({number})', { name: c.name, number: c.number }))
    } catch (e) { fail(e) }
  }
  const [lines, setLines] = useState<LineDraft[]>(doc?.lines.length ? doc.lines.map(l => ({ description: l.description, qty: String(l.qtyMilli / 1000).replace('.', lang === 'en' ? '.' : ','), unit: l.unit, price: moneyText(l.priceCents) }))
    : [{ description: '', qty: '1', unit: '', price: '' }])
  const [contactPick, setContactPick] = useState(false)
  const [servicePick, setServicePick] = useState(false)
  const [error, setError] = useState<string | null>(null)
  // Frist aus den Einstellungen, solange nichts eingetragen ist
  useEffect(() => {
    if (doc || kind === 'order') return
    api.get<{ settings: Settings }>('/api/finance/settings').then(r => setDue(plusDays(today(), kind === 'offer' ? r.settings.offerDays : r.settings.paymentDays))).catch(() => {})
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const setLine = (i: number, patch: Partial<LineDraft>) => setLines(l => l.map((x, n) => n === i ? { ...x, ...patch } : x))
  const lineTotal = (l: LineDraft) => {
    const q = parseQty(l.qty), p = parseMoney(l.price)
    return q != null && p != null ? Math.round(q * p) : null
  }
  const total = lines.reduce((a, l) => a + (lineTotal(l) ?? 0), 0)
  const pickContact = (c: Contact) => {
    const a = c.addresses[0]
    const place = a ? [[a.zip, a.city].filter(Boolean).join(' '), a.country].filter(Boolean) : []
    setRecipient([c.org, c.displayName !== c.org ? c.displayName : '', a?.street, ...place].filter(Boolean).join('\n'))
    setUid(c.uid)
  }
  // Leistung als Position übernehmen; eine noch leere letzte Position wird ersetzt
  const addService = (x: FinService) => setLines(ls => {
    const line = { description: x.description, qty: '1', unit: x.unit, price: moneyText(x.priceCents) }
    const last = ls[ls.length - 1]
    return last && !last.description.trim() && !last.price.trim() ? [...ls.slice(0, -1), line] : [...ls, line]
  })
  const save = async () => {
    const parsed = []
    for (const l of lines) {
      if (!l.description.trim() && !l.price.trim()) continue
      const qty = parseQty(l.qty), priceCents = parseMoney(l.price)
      if (qty == null) { setError(t('Bitte bei jeder Position eine gültige Menge eintragen.')); return }
      if (priceCents == null) { setError(t('Bitte bei jeder Position einen gültigen Preis eintragen.')); return }
      parsed.push({ description: l.description, qty, unit: l.unit, priceCents })
    }
    setError(null)
    const body = { kind, docDate: date, serviceDate: service, dueDate: due, subject, intro, outro, recipient, contactUid: uid, lines: parsed, customer, template }
    try { onSaved(doc ? await api.put<Doc>(`/api/finance/docs/${doc.id}`, body) : await api.post<Doc>('/api/finance/docs', body)) } catch (e) { fail(e) }
  }
  return <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
    <DialogSurface className={s.wide} style={{ maxWidth: 860 }}>
      <DialogBody>
        <DialogTitle>{docHeading({ kind, invoiceType: doc?.invoiceType ?? '' })} ({t('Entwurf')}){doc?.refNumber && doc.refKind !== 'invoice' ? ` · ${t('zu {art} {number}', { art: doc.refKind === 'order' ? t('Auftrag') : t('Angebot'), number: doc.refNumber })}` : ''}</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <div className={s.two}>
            {/* Kunde: aus der Kundenliste, aus den Kontakten oder frei eingegeben; die Anschrift steht so als Empfänger auf dem Beleg */}
            <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <Field label={t('Kunde')} required>
                <div style={{ display: 'flex', gap: 6 }}>
                  <Dropdown style={{ flex: 1, minWidth: 0 }} placeholder={t('Kunde wählen')} value={customers.find(c => c.id === customer)?.name ?? ''} selectedOptions={customer ? [String(customer)] : []}
                    onOptionSelect={(_, d) => { const c = customers.find(x => String(x.id) === d.optionValue); if (c) pickCustomer(c); else setCustomer(null) }}>
                    <Option value="" text={t('(keiner)')}>{t('(keiner)')}</Option>
                    {customers.map(c => <Option key={c.id} value={String(c.id)} text={c.name}>{`${c.name} (${c.number})`}</Option>)}
                  </Dropdown>
                  <Button icon={<Search20Regular />} onClick={() => setContactPick(true)}>{t('Aus den Kontakten')}</Button>
                </div>
              </Field>
              <Textarea rows={4} aria-label={t('Anschrift')} placeholder={t('Name und Anschrift, wie sie auf dem Beleg stehen')} value={recipient}
                onChange={(_, d) => { setRecipient(d.value); setUid(null) }} />
              {customer == null && recipient.trim() !== '' &&
                <Button size="small" icon={<PersonAdd20Regular />} style={{ alignSelf: 'flex-start' }} onClick={saveAsCustomer}>{t('Als Kunde speichern')}</Button>}
              {contactPick && <ContactPicker onClose={() => setContactPick(false)} onPick={c => { pickContact(c); setContactPick(false) }} />}
            </div>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
              <Field label={t('Datum')} required><Input type="date" value={date} onChange={(_, d) => setDate(d.value)} /></Field>
              {kind === 'order'
                ? <Field label={t('Leistungsdatum')} hint={t('Geplanter Tag oder Zeitraum. Abschlags- und Schlussrechnung übernehmen ihn.')}><Input value={service} onChange={(_, d) => setService(d.value)} /></Field>
                : <Field label={kind === 'offer' ? t('Gültig bis') : t('Zahlbar bis')}><Input type="date" value={due} onChange={(_, d) => setDue(d.value)} /></Field>}
              {kind === 'invoice' && <Field label={t('Leistungsdatum')} hint={t('Tag oder Zeitraum der Leistung, zum Beispiel „März 2026“.')} required><Input value={service} onChange={(_, d) => setService(d.value)} /></Field>}
            </div>
          </div>
          {doc?.invoiceType === 'final' && <Text size={200} className={s.muted}>{t('Die Positionen „abzüglich Abschlagsrechnung …“ müssen den ausgestellten Abschlagsrechnungen entsprechen, sonst lässt sich die Schlussrechnung nicht ausstellen.')}</Text>}
          <div className={s.two}>
            <Field label={t('Betreff')}><Input value={subject} onChange={(_, d) => setSubject(d.value)} /></Field>
            <Field label={t('Vorlage')}>
              <Dropdown value={(templates.find(x => x.id === template) ?? templates.find(x => x.isDefault))?.name ?? ''} selectedOptions={[String(template ?? templates.find(x => x.isDefault)?.id ?? '')]}
                onOptionSelect={(_, d) => setTemplate(Number(d.optionValue) || null)}>
                {templates.map(x => <Option key={x.id} value={String(x.id)} text={x.name}>{x.isDefault ? `${x.name} (${t('Standard')})` : x.name}</Option>)}
              </Dropdown>
            </Field>
          </div>
          <Field label={t('Text vor den Positionen')}><Textarea rows={2} value={intro} onChange={(_, d) => setIntro(d.value)} /></Field>
          <Text weight="semibold">{t('Positionen')}</Text>
          {lines.map((l, i) => <div key={i} className={s.lineRow}>
            <Field label={i === 0 ? t('Leistung') : undefined} style={{ minWidth: 0 }}><Textarea rows={1} resize="vertical" aria-label={t('Leistung')} value={l.description} onChange={(_, d) => setLine(i, { description: d.value })} /></Field>
            <Field label={i === 0 ? t('Menge') : undefined}><Input aria-label={t('Menge')} inputMode="decimal" value={l.qty} onChange={(_, d) => setLine(i, { qty: d.value })} /></Field>
            <Field label={i === 0 ? t('Einheit') : undefined}><Input aria-label={t('Einheit')} value={l.unit} onChange={(_, d) => setLine(i, { unit: d.value })} /></Field>
            <Field label={i === 0 ? t('Einzelpreis') : undefined}><Input aria-label={t('Einzelpreis')} inputMode="decimal" contentAfter="€" value={l.price} onChange={(_, d) => setLine(i, { price: d.value })} /></Field>
            <Field label={i === 0 ? t('Betrag') : undefined}><Text className={s.num} style={{ display: 'block', lineHeight: '32px' }}>{lineTotal(l) != null ? fmtMoney(lineTotal(l)!) : ''}</Text></Field>
            <Field label={i === 0 ? ' ' : undefined}><Button appearance="subtle" icon={<Delete20Regular />} aria-label={t('Position entfernen')} onClick={() => setLines(x => x.length > 1 ? x.filter((_, n) => n !== i) : [{ description: '', qty: '1', unit: '', price: '' }])} /></Field>
          </div>)}
          <div className={s.bar} style={{ justifyContent: 'space-between' }}>
            <div className={s.bar}>
              <Button icon={<Add20Regular />} onClick={() => setServicePick(true)}>{t('Leistung hinzufügen')}</Button>
              <Button icon={<Add20Regular />} onClick={() => setLines(x => [...x, { description: '', qty: '1', unit: '', price: '' }])}>{t('Freie Position')}</Button>
            </div>
            <Text weight="semibold">{t('Gesamtbetrag')}: {fmtMoney(total)}</Text>
          </div>
          {servicePick && <ServicePicker onClose={() => setServicePick(false)} onPick={x => { addService(x); setServicePick(false) }} />}
          <Text size={200} className={s.muted}>{t('Auf dem Beleg steht: Gemäß § 19 UStG wird keine Umsatzsteuer berechnet.')}</Text>
          <Field label={t('Text nach den Positionen')}><Textarea rows={2} value={outro} onChange={(_, d) => setOutro(d.value)} /></Field>
          {error && <Text className={s.bad}>{error}</Text>}
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" onClick={save}>{t('Speichern')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}

function PayDialog({ doc, onClose, onDone }: { doc: Doc; onClose: () => void; onDone: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const open = doc.totalCents - doc.paidCents
  const [date, setDate] = useState(today())
  const [amount, setAmount] = useState(moneyText(open))
  const [error, setError] = useState<string | null>(null)
  const save = async () => {
    const amountCents = parseMoney(amount)
    if (!amountCents) { setError(t('Bitte einen Betrag angeben.')); return }
    try { await api.post(`/api/finance/docs/${doc.id}/pay`, { date, amountCents }); onDone() } catch (e) { fail(e) }
  }
  return <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
    <DialogSurface>
      <DialogBody>
        <DialogTitle>{t('Zahlung zu {number} erfassen', { number: doc.number ?? '' })}</DialogTitle>
        <DialogContent className={s.form}>
          <Text>{t('Es entsteht ein Eintrag bei den Einnahmen. Er zählt zu dem Jahr, in dem das Geld eingegangen ist.')}</Text>
          <Field label={t('Tag des Zahlungseingangs')} required><Input type="date" value={date} onChange={(_, d) => setDate(d.value)} /></Field>
          <Field label={t('Betrag')} required validationMessage={error ?? undefined}><Input value={amount} inputMode="decimal" contentAfter="€" onChange={(_, d) => { setAmount(d.value); setError(null) }} /></Field>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" onClick={save}>{t('Erfassen')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}

// ---------------- Vorlagen ----------------

const TEMPLATE_COLS: Record<string, ColDef> = {
  name: { label: () => t('Name'), width: 300, sort: 'name' },
  standard: { label: () => t('Standard'), width: 90, sort: 'standard' },
  modified: { label: () => t('Geändert'), width: 150, sort: 'modified' },
}

function TemplatesPage() {
  const { fail, notify } = useApp()
  const [rows, setRows] = useState<Template[]>([])
  const [picked, setPicked] = useState<Template | null>(null)
  const [edit, setEdit] = useState<(Partial<Template> & { layout: TplLayout }) | null>(null)
  const load = () => api.get<Template[]>('/api/finance/templates').then(list => { setRows(list); setPicked(p => p ? list.find(x => x.id === p.id) ?? null : null) }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const create = async () => { try { setEdit({ layout: await api.get<TplLayout>('/api/finance/templates/default-layout') }) } catch (e) { fail(e) } }
  const makeDefault = async (x: Template) => { try { await api.post(`/api/finance/templates/${x.id}/default`); notify(t('Standardvorlage gesetzt')); load() } catch (e) { fail(e) } }
  const remove = async (x: Template) => {
    if (!await askConfirm(t('Vorlage löschen'), t('„{name}“ wird gelöscht. Ausgestellte Belege behalten ihr PDF, Entwürfe nehmen danach die Standardvorlage.', { name: x.name }), t('Löschen'))) return
    try { await api.del(`/api/finance/templates/${x.id}`); setPicked(null); load() } catch (e) { fail(e) }
  }
  return <>
    <PageActions>
      <RibbonButton primary icon={<Add20Regular />} label={t('Neue Vorlage')} onClick={create} />
      <RibbonDivider />
      <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={!picked} onClick={() => picked && setEdit(picked)} />
      <RibbonButton icon={<Copy20Regular />} label={t('Duplizieren')} disabled={!picked} onClick={() => picked && setEdit({ name: t('Kopie von {name}', { name: picked.name }), layout: picked.layout })} />
      <RibbonButton icon={<Checkmark20Regular />} label={t('Als Standard')} disabled={!picked || picked.isDefault} onClick={() => picked && makeDefault(picked)} />
      <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!picked || picked.isDefault} onClick={() => picked && remove(picked)} />
    </PageActions>
    <Grid id="finance-templates" cols={TEMPLATE_COLS} defaults={['name', 'standard', 'modified']} rows={rows} selected={picked?.id} onPick={setPicked} onOpen={setEdit} initialSort="name" empty={t('Keine Einträge')}
      phone={r => ({ title: r.name, sub: r.isDefault ? t('Standard') : '' })}
      value={(r, k) => k === 'standard' ? (r.isDefault ? 1 : 0) : k === 'modified' ? r.modified : r.name}
      cell={(r, k) => k === 'standard' ? (r.isDefault ? t('ja') : '') : k === 'modified' ? new Date(r.modified).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' }) : r.name} />
    {edit && <TemplateDesigner template={edit} onClose={() => setEdit(null)} onSaved={x => { setEdit(null); setPicked(x); load() }} />}
  </>
}

// ---------------- Leistungen ----------------

function ServicesPage() {
  const s = useStyles()
  const { fail } = useApp()
  const [rows, setRows] = useState<FinService[]>([])
  const [picked, setPicked] = useState<FinService | null>(null)
  const [edit, setEdit] = useState<Partial<FinService> | null>(null)
  const load = () => api.get<FinService[]>('/api/finance/services').then(list => { setRows(list); setPicked(p => p ? list.find(x => x.id === p.id) ?? null : null) }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const remove = async (x: FinService) => {
    if (!await askConfirm(t('Leistung löschen'), t('Die Leistung wird gelöscht. Belege, in denen sie steht, bleiben unverändert.'), t('Löschen'))) return
    try { await api.del(`/api/finance/services/${x.id}`); setPicked(null); load() } catch (e) { fail(e) }
  }
  return <>
    <PageActions>
      <RibbonButton primary icon={<Add20Regular />} label={t('Neue Leistung')} onClick={() => setEdit({})} />
      <RibbonDivider />
      <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={!picked} onClick={() => picked && setEdit(picked)} />
      <RibbonButton icon={<Copy20Regular />} label={t('Duplizieren')} disabled={!picked} onClick={() => picked && setEdit({ description: picked.description, unit: picked.unit, priceCents: picked.priceCents })} />
      <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!picked} onClick={() => picked && remove(picked)} />
    </PageActions>
    <Grid id="finance-services" rows={rows} selected={picked?.id} onPick={setPicked} onOpen={setEdit} empty={t('Noch keine Leistungen.')} {...serviceGrid(s.num)} />
    {edit && <ServiceDialog service={edit} onClose={() => setEdit(null)} onSaved={x => { setEdit(null); setPicked(x); load() }} />}
  </>
}

function ServiceDialog({ service, onClose, onSaved }: { service: Partial<FinService>; onClose: () => void; onSaved: (x: FinService) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [description, setDescription] = useState(service.description ?? '')
  const [unit, setUnit] = useState(service.unit ?? '')
  const [price, setPrice] = useState(service.priceCents != null ? moneyText(service.priceCents) : '')
  const [error, setError] = useState<string | null>(null)
  const save = async () => {
    const priceCents = price.trim() ? parseMoney(price) : 0
    if (priceCents == null) { setError(t('Bitte einen gültigen Preis eintragen.')); return }
    const body = { description, unit, priceCents }
    try { onSaved(service.id ? await api.put<FinService>(`/api/finance/services/${service.id}`, body) : await api.post<FinService>('/api/finance/services', body)) } catch (e) { fail(e) }
  }
  return <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
    <DialogSurface>
      <DialogBody>
        <DialogTitle>{service.id ? t('Leistung') : t('Neue Leistung')}</DialogTitle>
        <DialogContent className={s.form}>
          <Field label={t('Leistung')} hint={t('Dieser Text steht in der Position auf dem Beleg.')} required>
            <Textarea rows={3} resize="vertical" value={description} onChange={(_, d) => setDescription(d.value)} />
          </Field>
          <div className={s.two}>
            <Field label={t('Einheit')}><Input value={unit} placeholder={t('zum Beispiel Stunde')} onChange={(_, d) => setUnit(d.value)} /></Field>
            <Field label={t('Einzelpreis')} validationMessage={error ?? undefined}><Input value={price} inputMode="decimal" contentAfter="€" onChange={(_, d) => { setPrice(d.value); setError(null) }} /></Field>
          </div>
          <Text size={200} className={s.muted}>{t('In einem Angebot oder einer Rechnung lassen sich Text, Menge und Preis danach noch anpassen.')}</Text>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" disabled={!description.trim()} onClick={save}>{t('Speichern')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}

// ---------------- Kunden ----------------

const CUSTOMER_COLS: Record<string, ColDef> = {
  number: { label: () => t('Kundennummer'), width: 120, sort: 'number' },
  name: { label: () => t('Name'), width: 240, sort: 'name' },
  address: { label: () => t('Anschrift'), width: 320, sort: 'address' },
  email: { label: () => t('E-Mail'), width: 220, sort: 'email' },
  docs: { label: () => t('Belege'), width: 80, sort: 'docs' },
  invoiced: { label: () => t('Berechnet'), width: 120, sort: 'invoiced' },
}

function CustomersPage() {
  const s = useStyles()
  const { fail } = useApp()
  const [rows, setRows] = useState<Customer[]>([])
  const [picked, setPicked] = useState<Customer | null>(null)
  const [edit, setEdit] = useState<Partial<Customer> | null>(null)
  const load = () => api.get<Customer[]>('/api/finance/customers').then(list => { setRows(list); setPicked(p => p ? list.find(x => x.id === p.id) ?? null : null) }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const remove = async (c: Customer) => {
    if (!await askConfirm(t('Kunde löschen'), t('{name} wird gelöscht.', { name: c.name }), t('Löschen'))) return
    try { await api.del(`/api/finance/customers/${c.id}`); setPicked(null); load() } catch (e) { fail(e) }
  }
  return <>
    <PageActions>
      <RibbonButton primary icon={<Add20Regular />} label={t('Neuer Kunde')} onClick={() => setEdit({})} />
      <RibbonDivider />
      <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={!picked} onClick={() => picked && setEdit(picked)} />
      <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!picked || picked.docs > 0}
        title={picked && picked.docs > 0 ? t('Zu diesem Kunden gibt es Belege. Er lässt sich nicht löschen.') : t('Löschen')} onClick={() => picked && remove(picked)} />
    </PageActions>
    <Grid id="finance-customers" cols={CUSTOMER_COLS} defaults={['number', 'name', 'address', 'docs', 'invoiced']} rows={rows} selected={picked?.id} onPick={setPicked} onOpen={setEdit} initialSort="name" empty={t('Noch keine Kunden.')}
      phone={r => ({ title: r.name, sub: [r.number, r.address.replace(/\n/g, ', ')].filter(Boolean).join(' · '), right: fmtMoney(r.invoicedCents) })}
      value={(r, k) => k === 'number' ? r.number : k === 'address' ? r.address : k === 'email' ? r.email : k === 'docs' ? r.docs : k === 'invoiced' ? r.invoicedCents : r.name}
      cell={(r, k) => {
        switch (k) {
          case 'number': return r.number
          case 'address': return r.address.replace(/\n/g, ', ')
          case 'email': return r.email
          case 'docs': return <div className={s.num}>{r.docs}</div>
          case 'invoiced': return <div className={s.num}>{fmtMoney(r.invoicedCents)}</div>
          default: return r.name
        }
      }} />
    {edit && <CustomerDialog customer={edit} onClose={() => setEdit(null)} onSaved={c => { setEdit(null); setPicked(c); load() }} />}
  </>
}

function CustomerDialog({ customer, onClose, onSaved }: { customer: Partial<Customer>; onClose: () => void; onSaved: (c: Customer) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [number, setNumber] = useState(customer.number ?? '')
  const [name, setName] = useState(customer.name ?? '')
  const [address, setAddress] = useState(customer.address ?? '')
  const [note, setNote] = useState(customer.note ?? '')
  const [email, setEmail] = useState(customer.email ?? '')
  const [uid, setUid] = useState<string | null>(customer.contactUid ?? null)
  const [contactPick, setContactPick] = useState(false)
  const pick = (c: Contact) => {
    const a = c.addresses[0]
    setName(c.org || c.displayName)
    setAddress([c.org && c.displayName !== c.org ? c.displayName : '', a?.street, [a?.zip, a?.city].filter(Boolean).join(' '), a?.country].filter(Boolean).join('\n'))
    setUid(c.uid)
  }
  const save = async () => {
    const body = { number, name, address, note, contactUid: uid, email }
    try { onSaved(customer.id ? await api.put<Customer>(`/api/finance/customers/${customer.id}`, body) : await api.post<Customer>('/api/finance/customers', body)) } catch (e) { fail(e) }
  }
  return <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
    <DialogSurface>
      <DialogBody>
        <DialogTitle>{customer.id ? t('Kunde') : t('Neuer Kunde')}</DialogTitle>
        <DialogContent className={s.form}>
          <Field label={t('Aus den Kontakten')}>
            <Button icon={<Search20Regular />} style={{ alignSelf: 'flex-start' }} onClick={() => setContactPick(true)}>{t('Kontakt wählen')}</Button>
          </Field>
          {contactPick && <ContactPicker onClose={() => setContactPick(false)} onPick={c => { pick(c); setContactPick(false) }} />}
          <Field label={t('Name')} required><Input value={name} onChange={(_, d) => setName(d.value)} /></Field>
          <Field label={t('Anschrift')}><Textarea rows={3} value={address} onChange={(_, d) => setAddress(d.value)} /></Field>
          <Field label={t('E-Mail')} hint={t('Empfänger, wenn ein Beleg per E-Mail gesendet wird.')}><Input type="email" value={email} onChange={(_, d) => setEmail(d.value)} /></Field>
          <Field label={t('Kundennummer')} hint={customer.id ? undefined : t('Leer: dmOffice vergibt die nächste freie Nummer.')}><Input value={number} onChange={(_, d) => setNumber(d.value)} /></Field>
          <Field label={t('Notiz')}><Textarea rows={2} value={note} onChange={(_, d) => setNote(d.value)} /></Field>
          <Text size={200} className={s.muted}>{t('Änderungen gelten für neue Belege. Ausgestellte Belege bleiben unverändert.')}</Text>
        </DialogContent>
        <DialogActions>
          <Button onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" disabled={!name.trim()} onClick={save}>{t('Speichern')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}

// ---------------- Übersicht ----------------

function SummaryPage({ year, years, setYear, reloadYears }: { year: number; years: YearState[]; setYear: (y: number) => void; reloadYears: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [sum, setSum] = useState<Summary | null>(null)
  const [log, setLog] = useState<{ intact: boolean; entries: { id: number; at: string; action: string; ref: string; detail: string }[] } | null>(null)
  const load = () => api.get<Summary>(`/api/finance/summary?year=${year}`).then(setSum).catch(fail)
  useEffect(() => { load() }, [year]) // eslint-disable-line react-hooks/exhaustive-deps
  const close = async () => {
    if (!await askConfirm(t('Jahr {year} abschließen', { year }), t('Die Einträge dieses Jahres lassen sich danach nicht mehr ändern, löschen oder ergänzen. Das lässt sich nicht rückgängig machen. Korrekturen sind nur noch als Gegeneintrag in einem offenen Jahr möglich.'), t('Abschließen'))) return
    try { await api.post(`/api/finance/years/${year}/close`); notify(t('Jahr abgeschlossen')); reloadYears(); load() } catch (e) { fail(e) }
  }
  if (!sum) return null
  const part = (kind: string) => sum.categories.filter(c => c.kind === kind)
  const block = (title: string, kind: string, total: number) => <div className={s.card} style={{ flex: '1 1 300px' }}>
    <Text weight="semibold">{title}</Text>
    {part(kind).map(c => <div key={c.id} className={s.bar} style={{ justifyContent: 'space-between' }}><Text>{isUnassigned(c.id) ? t('Noch nicht zugeordnet') : c.name}</Text><Text className={s.num}>{fmtMoney(c.cents)}</Text></div>)}
    {!part(kind).length && <Text className={s.muted}>{t('Keine Einträge')}</Text>}
    <div className={s.bar} style={{ justifyContent: 'space-between', borderTop: `1px solid ${tokens.colorNeutralStroke2}`, paddingTop: 4, marginTop: 4 }}><Text weight="semibold">{t('Summe')}</Text><Text weight="semibold" className={s.num}>{fmtMoney(total)}</Text></div>
  </div>
  return <>
    <PageActions>
      <RibbonButton icon={<DocumentPdf20Regular />} label={t('Jahresübersicht als PDF')} onClick={() => window.open(`/api/finance/summary.pdf?year=${year}`, '_blank')} />
      <RibbonButton icon={<ArrowDownload20Regular />} label={t('Einträge als CSV')} onClick={() => window.open(`/api/finance/entries.csv?year=${year}`, '_blank')} />
      <RibbonButton icon={<LockClosed20Regular />} label={t('Jahr abschließen')} disabled={sum.closed || year >= new Date().getFullYear()} onClick={close} />
    </PageActions>
    <div className={s.bar}>
      <YearSelect years={years} year={year} onChange={setYear} />
      {sum.closed && <Text className={s.muted}><LockClosed20Regular style={{ verticalAlign: 'middle', marginRight: 4 }} />{t('Dieses Jahr ist abgeschlossen.')}</Text>}
    </div>
    <div className={s.cards}><div className={s.card}><Text size={200} className={s.muted}>{t('Überschuss')}</Text><Text size={500} weight="semibold">{fmtMoney(sum.surplus)}</Text></div></div>
    <div className={s.cards}>{block(t('Einnahmen'), 'income', sum.income)}{block(t('Ausgaben'), 'expense', sum.expense)}</div>
    <Text size={200} className={s.muted}>{t('Diese Übersicht ist keine amtliche Anlage EÜR. Die Werte lassen sich in die Steuererklärung übertragen.')}</Text>
    <div className={s.bar}>
      <Button onClick={() => api.get('/api/finance/log').then(setLog).catch(fail)}>{t('Protokoll anzeigen')}</Button>
    </div>
    {log && <>
      <Text className={log.intact ? s.muted : s.bad}>{log.intact ? t('Die Prüfsummen des Protokolls stimmen.') : t('Die Prüfsummen des Protokolls stimmen nicht. Einträge wurden außerhalb von dmOffice verändert.')}</Text>
      <Grid id="finance-log" cols={LOG_COLS} defaults={['at', 'action', 'detail']} rows={log.entries} empty={t('Keine Einträge')} initialSort="at"
        value={(r, k) => k === 'at' ? r.at : k === 'action' ? actionLabel(r.action) : r.detail}
        cell={(r, k) => k === 'at' ? new Date(r.at).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' }) : k === 'action' ? actionLabel(r.action) : r.detail} />
    </>}
  </>
}

const actionLabel = (a: string) => ({
  'template.save': t('Vorlage geändert'), 'template.default': t('Standardvorlage gesetzt'), 'template.delete': t('Vorlage gelöscht'),
  'number.confirm': t('Nummern bestätigt'), 'customer.add': t('Kunde angelegt'), 'customer.change': t('Kunde geändert'), 'customer.delete': t('Kunde gelöscht'),
  'service.add': t('Leistung angelegt'), 'service.change': t('Leistung geändert'), 'service.delete': t('Leistung gelöscht'),
  settings: t('Einstellungen geändert'), number: t('Nächste Nummer gesetzt'), 'entry.add': t('Eintrag angelegt'), 'entry.change': t('Eintrag geändert'), 'entry.delete': t('Eintrag gelöscht'),
  'year.close': t('Jahr abgeschlossen'), 'doc.issue': t('Beleg ausgestellt'),
} as Record<string, string>)[a] ?? a

const LOG_COLS: Record<string, ColDef> = {
  at: { label: () => t('Zeit'), width: 140, sort: 'at' },
  action: { label: () => t('Vorgang'), width: 170, sort: 'action' },
  detail: { label: () => t('Angaben'), width: 460, sort: 'detail' },
}

// ---------------- Einstellungen ----------------

function SettingsPage({ cats, reloadCats }: { cats: Category[]; reloadCats: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [v, setV] = useState<Settings | null>(null)
  const [nextInvoice, setNextInvoice] = useState('')
  const [nextOffer, setNextOffer] = useState('')
  const [nextOrder, setNextOrder] = useState('')
  const [year, setYear] = useState(0)
  const [newCat, setNewCat] = useState<{ kind: 'income' | 'expense'; name: string }>({ kind: 'expense', name: '' })
  const [toConfirm, setToConfirm] = useState(false)
  const [tab, setTab] = useState<'company' | 'numbers' | 'mail' | 'categories'>('company')
  const load = () => api.get<{ settings: Settings; nextInvoice: number; nextOffer: number; nextOrder: number; year: number; numbersToConfirm: boolean }>('/api/finance/settings').then(r => {
    setV(r.settings); setNextInvoice(String(r.nextInvoice)); setNextOffer(String(r.nextOffer)); setNextOrder(String(r.nextOrder)); setYear(r.year); setToConfirm(r.numbersToConfirm)
  }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!v) return null
  const set = (patch: Partial<Settings>) => setV({ ...v, ...patch })
  const text = (key: keyof Settings, label: string, hint?: string, required?: boolean) => <Field label={label} hint={hint} required={required}><Input value={String(v[key] ?? '')} onChange={(_, d) => set({ [key]: d.value })} /></Field>
  const save = async () => {
    try { await api.put('/api/finance/settings', { settings: v, nextInvoice: Number(nextInvoice) || null, nextOffer: Number(nextOffer) || null, nextOrder: Number(nextOrder) || null }); notify(t('Gespeichert')); load() } catch (e) { fail(e); load() }
  }
  const addCat = async () => {
    try { await api.post('/api/finance/categories', newCat); setNewCat({ ...newCat, name: '' }); reloadCats() } catch (e) { fail(e) }
  }
  const removeCat = async (c: Category) => { try { await api.del(`/api/finance/categories/${c.id}`); reloadCats() } catch (e) { fail(e) } }
  // Reiter statt einer langen Seite; Unternehmen und Nummern werden gemeinsam gespeichert, ein Reiterwechsel verliert nichts
  return <div className={s.form}>
    {toConfirm && <MessageBar intent="warning" layout="multiline">
      <MessageBarBody>{t('Der Server wurde aus einem Core-Backup wiederhergestellt. Dieser Stand kennt später vergebene Nummern nicht. Bitte die nächsten Nummern mit deinen letzten Belegen vergleichen, speichern und dann bestätigen. Bis dahin lässt sich nichts ausstellen.')}</MessageBarBody>
      <MessageBarActions><Button onClick={async () => { try { await api.post('/api/finance/numbers/confirm'); notify(t('Nummern bestätigt')); load() } catch (e) { fail(e) } }}>{t('Nummern bestätigen')}</Button></MessageBarActions>
    </MessageBar>}
    <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as typeof tab)}>
      <Tab value="company">{t('Unternehmen')}</Tab>
      <Tab value="numbers">{t('Nummern und Fristen')}</Tab>
      <Tab value="mail">{t('E-Mail')}</Tab>
      <Tab value="categories">{t('Kategorien')}</Tab>
    </TabList>
    {tab === 'company' && <>
    <Text size={200} className={s.muted}>{t('Diese Angaben stehen auf Angeboten und Rechnungen. Ein ausgestellter Beleg behält den Stand vom Tag des Ausstellens.')}</Text>
    {text('name', t('Name'), undefined, true)}
    <Field label={t('Anschrift')} required><Textarea rows={3} value={v.address} onChange={(_, d) => set({ address: d.value })} /></Field>
    <div className={s.two}>{text('taxNumber', t('Steuernummer'), t('Für Rechnungen ist die Steuernummer oder die USt-IdNr. nötig.'))}{text('vatId', t('USt-IdNr.'))}</div>
    {text('contact', t('Kontaktzeile'), t('Erscheint in der Fußzeile, zum Beispiel Telefon und E-Mail.'))}
    <div className={s.two}>{text('bank', t('Bank'))}{text('iban', 'IBAN')}</div>
    {text('bic', 'BIC')}
    </>}
    {tab === 'numbers' && <>
    <Text size={200} className={s.muted}>{t('Die Nummer wird erst beim Ausstellen vergeben. Entwürfe verbrauchen keine Nummer. Muster mit den Platzhaltern {jahr} und {nr}. Das Jahr kommt aus dem Belegdatum, jedes Jahr beginnt bei 1.')}</Text>
    <div className={s.two}>
      {text('invoicePattern', t('Muster für Rechnungen'))}
      <Field label={t('Nächste Rechnungsnummer {year}', { year })} hint={t('Beim Umstieg aus einem anderen Programm. Kleiner werden darf sie nicht.')}><Input type="number" min={1} value={nextInvoice} onChange={(_, d) => setNextInvoice(d.value)} /></Field>
      {text('offerPattern', t('Muster für Angebote'))}
      <Field label={t('Nächste Angebotsnummer {year}', { year })}><Input type="number" min={1} value={nextOffer} onChange={(_, d) => setNextOffer(d.value)} /></Field>
      {text('orderPattern', t('Muster für Aufträge'))}
      <Field label={t('Nächste Auftragsnummer {year}', { year })}><Input type="number" min={1} value={nextOrder} onChange={(_, d) => setNextOrder(d.value)} /></Field>
      <Field label={t('Zahlungsziel in Tagen')}><Input type="number" min={0} max={365} value={String(v.paymentDays)} onChange={(_, d) => set({ paymentDays: Number(d.value) })} /></Field>
      <Field label={t('Angebot gültig in Tagen')}><Input type="number" min={0} max={365} value={String(v.offerDays)} onChange={(_, d) => set({ offerDays: Number(d.value) })} /></Field>
    </div>
    </>}
    {tab === 'mail' && <MailSettings v={v} set={set} />}
    {tab !== 'categories' && <div><Button appearance="primary" onClick={save}>{t('Speichern')}</Button></div>}
    {tab === 'categories' && <>
    {(['income', 'expense'] as const).map(kind => <div key={kind} style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
      <Text size={200} className={s.muted}>{kind === 'income' ? t('Einnahmen') : t('Ausgaben')}</Text>
      {cats.filter(c => c.kind === kind && !isUnassigned(c.id)).map(c => <div key={c.id} className={s.bar} style={{ justifyContent: 'space-between' }}>
        <Text>{c.name}</Text>
        <Button appearance="subtle" size="small" icon={<Delete20Regular />} disabled={c.count > 0} title={c.count > 0 ? t('Diese Kategorie wird noch verwendet.') : undefined} aria-label={t('Löschen')} onClick={() => removeCat(c)} />
      </div>)}
    </div>)}
    <div className={s.bar}>
      <Dropdown style={{ minWidth: 120 }} aria-label={t('Art')} value={newCat.kind === 'income' ? t('Einnahmen') : t('Ausgaben')} selectedOptions={[newCat.kind]} onOptionSelect={(_, d) => setNewCat({ ...newCat, kind: d.optionValue as 'income' | 'expense' })}>
        <Option value="income">{t('Einnahmen')}</Option><Option value="expense">{t('Ausgaben')}</Option>
      </Dropdown>
      <Input style={{ flex: 1, minWidth: 160 }} placeholder={t('Neue Kategorie')} value={newCat.name} onChange={(_, d) => setNewCat({ ...newCat, name: d.value })} />
      <Button icon={<Add20Regular />} disabled={!newCat.name.trim()} onClick={addCat}>{t('Hinzufügen')}</Button>
    </div>
    </>}
  </div>
}

// ---------------- Einstellungen: E-Mail ----------------

const MAIL_KINDS: [DocKind, () => string][] = [['offer', () => t('Angebot')], ['order', () => t('Auftragsbestätigung')], ['invoice', () => t('Rechnung')]]

/** Postfach, Antwortadresse und je Belegart Betreff, Text und Anhänge aus dem DMS (Rollen und feste Dokumente). */
function MailSettings({ v, set }: { v: Settings; set: (patch: Partial<Settings>) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [accounts, setAccounts] = useState<{ id: string; name: string; email: string }[]>([])
  const [defaults, setDefaults] = useState<{ placeholders: string[]; templates: Record<string, MailTpl> } | null>(null)
  const [roles, setRoles] = useState<{ id: string; name: string }[]>([])
  const [kind, setKind] = useState<DocKind>('invoice')
  const [titles, setTitles] = useState<Record<number, string>>({})
  const [adding, setAdding] = useState(false)
  const dms = featureOn('dms')
  useEffect(() => {
    api.get<{ id: string; name: string; email: string }[]>('/api/mail/accounts').then(setAccounts).catch(() => setAccounts([]))
    api.get<{ placeholders: string[]; templates: Record<string, MailTpl> }>('/api/finance/mail/defaults').then(setDefaults).catch(fail)
    if (dms) api.get<{ id: string; kind: string; name: string }[]>('/api/dms/lists').then(l => setRoles(l.filter(e => e.kind === 'role'))).catch(() => {})
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const base: Partial<MailTpl> = v.mailTemplates?.[kind] ?? defaults?.templates[kind] ?? {}
  const tpl: MailTpl = { subject: base.subject ?? '', body: base.body ?? '', roles: base.roles ?? [], docs: base.docs ?? [] }
  const setTpl = (patch: Partial<MailTpl>) => set({ mailTemplates: { ...(v.mailTemplates ?? {}), [kind]: { ...tpl, ...patch } } })
  useEffect(() => {
    for (const id of tpl.docs.filter(id => titles[id] === undefined))
      api.get<{ title: string; originalName: string }>(`/api/dms/docs/${id}`).then(d => setTitles(x => ({ ...x, [id]: d.title || d.originalName })))
        .catch(() => setTitles(x => ({ ...x, [id]: t('Dokument nicht gefunden') })))
  }, [kind, tpl.docs.join(',')]) // eslint-disable-line react-hooks/exhaustive-deps
  const account = accounts.find(a => a.id === v.mailAccount) ?? accounts[0]
  const loadDocs = async (text: string) => {
    const r = await api.get<{ items: DmsDocHit[] }>(`/api/dms/docs?limit=100&sort=imported&desc=true${text.trim() ? '&q=' + encodeURIComponent(text.trim()) : ''}`)
    return r.items.filter(d => !d.trashed)
  }
  return <>
    <Text size={200} className={s.muted}>{t('Ein ausgestellter Beleg lässt sich als E-Mail-Entwurf öffnen: mit dem PDF, dem Text der Vorlage und den Anhängen. Gesendet wird erst im Mailbereich.')}</Text>
    <div className={s.two}>
      <Field label={t('Postfach')} hint={accounts.length ? t('Absender ist die Adresse dieses Kontos.') : t('Es ist noch kein E-Mail-Konto eingerichtet.')}>
        <Dropdown disabled={!accounts.length} value={account ? `${account.name || account.email} <${account.email}>` : ''} selectedOptions={account ? [account.id] : []}
          onOptionSelect={(_, d) => d.optionValue && set({ mailAccount: d.optionValue })}>
          {accounts.map(a => <Option key={a.id} value={a.id} text={a.email}>{(a.name || a.email) + ' <' + a.email + '>'}</Option>)}
        </Dropdown>
      </Field>
      <Field label={t('Antwortadresse')} hint={t('Optional. Antworten der Kunden gehen an diese Adresse.')}>
        <Input type="email" value={v.mailReplyTo ?? ''} onChange={(_, d) => set({ mailReplyTo: d.value })} />
      </Field>
    </div>
    <TabList size="small" selectedValue={kind} onTabSelect={(_, d) => setKind(d.value as DocKind)}>
      {MAIL_KINDS.map(([k, label]) => <Tab key={k} value={k}>{label()}</Tab>)}
    </TabList>
    <Field label={t('Betreff')}><Input value={tpl.subject} onChange={(_, d) => setTpl({ subject: d.value })} /></Field>
    <Field label={t('Text')} hint={defaults ? t('Platzhalter: {list}', { list: defaults.placeholders.join(' ') }) : undefined}>
      <Textarea rows={8} resize="vertical" value={tpl.body} onChange={(_, d) => setTpl({ body: d.value })} />
    </Field>
    {dms && <>
      <Field label={t('Rollen als Anhang')} hint={t('Angehängt wird das Dokument, das die Rolle im DMS innehat. Folgebelege übernehmen das ihres Vorgängers.')}>
        <Dropdown multiselect placeholder={roles.length ? t('Keine') : t('Im DMS gibt es noch keine Rollen.')} disabled={!roles.length}
          value={tpl.roles.map(r => roles.find(x => x.id === r)?.name).filter(Boolean).join(', ')} selectedOptions={tpl.roles}
          onOptionSelect={(_, d) => setTpl({ roles: d.selectedOptions })}>
          {roles.map(r => <Option key={r.id} value={r.id}>{r.name}</Option>)}
        </Dropdown>
      </Field>
      <Field label={t('Feste Dokumente als Anhang')}>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 4 }}>
          {tpl.docs.map(id => <div key={id} className={s.bar}>
            <Text style={{ flex: 1, minWidth: 0 }}>{titles[id] ?? '…'}</Text>
            <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} aria-label={t('Entfernen')} onClick={() => setTpl({ docs: tpl.docs.filter(x => x !== id) })} />
          </div>)}
          <Button size="small" icon={<Add20Regular />} style={{ alignSelf: 'flex-start' }} onClick={() => setAdding(true)}>{t('Dokument aus dem DMS hinzufügen')}</Button>
        </div>
      </Field>
      {adding && <PickDialog title={t('Dokument aus dem DMS')} load={loadDocs} onClose={() => setAdding(false)}
        onPick={h => { if (!tpl.docs.includes(h.id)) setTpl({ docs: [...tpl.docs, h.id] }); setAdding(false) }} grid={{
          id: 'finance-pick-mail-doc', cols: DMS_PICK_COLS, defaults: ['title', 'correspondent', 'date'], initialSort: 'date', empty: t('Keine passenden Dokumente.'),
          phone: h => ({ title: h.title || h.originalName, sub: fmtDay(h.docDate), right: '' }),
          value: (h, k) => k === 'date' ? h.docDate ?? '' : k === 'correspondent' ? h.correspondent ?? '' : h.title || h.originalName,
          cell: (h, k) => k === 'date' ? fmtDay(h.docDate) : k === 'correspondent' ? h.correspondent ?? '' : h.title || h.originalName,
        }} />}
    </>}
    {defaults && <div><Button size="small" onClick={() => setTpl({ subject: defaults.templates[kind].subject, body: defaults.templates[kind].body })}>{t('Text der Vorgabe einsetzen')}</Button></div>}
  </>
}

// ---------------- Bereich ----------------

/** Baum links wie in Mail und DMS: Bereiche gruppiert, Belege mit Anzahl. */
const TREE: ((() => string) | [Page, JSX.Element, () => string])[] = [
  ['summary', <DataBarVertical20Regular />, () => t('Übersicht')],
  ['income', <ArrowDownload20Regular />, () => t('Einnahmen')],
  ['expense', <ArrowUpload20Regular />, () => t('Ausgaben')],
  () => t('Belege'),
  ['offer', <DocumentEdit20Regular />, () => t('Angebote')],
  ['order', <ClipboardTask20Regular />, () => t('Aufträge')],
  ['invoice', <Receipt20Regular />, () => t('Rechnungen')],
  () => t('Stammdaten'),
  ['customers', <People20Regular />, () => t('Kunden')],
  ['services', <Toolbox20Regular />, () => t('Leistungen')],
  ['templates', <DocumentBulletList20Regular />, () => t('Vorlagen')],
  () => '',
  ['settings', <Settings20Regular />, () => t('Einstellungen')],
]

const pageOfHash = (): Page => { const p = location.hash.replace(/^#\/?/, '').split('/')[1] as Page; return PAGES.includes(p) ? p : 'income' }

export default function FinanceView() {
  const s = useStyles()
  const { fail } = useApp()
  const [page, setPage] = useState<Page>(pageOfHash)
  const [year, setYear] = useState(new Date().getFullYear())
  const [years, setYears] = useState<YearState[]>([])
  const [cats, setCats] = useState<Category[]>([])
  const [stamp, setStamp] = useState(0)
  const loadYears = () => api.get<YearState[]>('/api/finance/years').then(setYears).catch(fail)
  const loadCats = () => api.get<Category[]>('/api/finance/categories').then(l => setCats(l.map(c => isUnassigned(c.id) ? { ...c, name: t('Noch nicht zugeordnet') } : c))).catch(fail)
  useEffect(() => { loadYears(); loadCats() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    const on = () => { if (location.hash.replace(/^#\/?/, '').split('/')[0] === 'finance') setPage(pageOfHash()) }
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [])
  const go = (p: Page) => { location.hash = `#/finance/${p}` }
  const [slot, setSlot] = useState<HTMLDivElement | null>(null)
  const nv = useNavStyles()
  const mobile = useIsMobile()
  const [drawer, toggleSide, closeSide] = useSidePane('finance')
  const [counts, setCounts] = useState<Partial<Record<Page, number>>>({})
  const loadCounts = () => Promise.all((['offer', 'order', 'invoice'] as DocKind[]).map(k => api.get<Doc[]>(`/api/finance/docs?kind=${k}`).then(l => [k, l.length] as const)))
    .then(r => setCounts(Object.fromEntries(r))).catch(() => {})
  useEffect(() => { loadCounts() }, [stamp]) // eslint-disable-line react-hooks/exhaustive-deps
  const docsChanged = () => { loadYears(); loadCounts() }
  // Saldo des gewählten Jahres, immer rechts im Menüband; neu geladen, sobald sich Einträge oder Belege ändern (dann kommen auch die Jahre neu)
  const [saldo, setSaldo] = useState<number | null>(null)
  useEffect(() => { api.get<Summary>(`/api/finance/summary?year=${year}`).then(x => setSaldo(x.surplus)).catch(() => setSaldo(null)) }, [year, years, stamp])
  return <div className={s.root}>
    <RibbonHeader area="finance" onMenu={toggleSide}
      start={<>
        {/* Vorn die Knöpfe der Seite, der Neu-Knopf ganz links; dahinter Aktualisieren. Die Bereiche stehen im Baum links. */}
        <div ref={setSlot} style={{ display: 'contents' }} />
        <RibbonButton icon={<ArrowSync20Regular />} label={t('Aktualisieren')} onClick={() => { loadYears(); loadCats(); setStamp(x => x + 1) }} />
        {saldo !== null && <><div style={{ flex: 1 }} />
          <div className={s.saldo} title={t('Einnahmen minus Ausgaben im Jahr {year}', { year })}>
            <span className={s.saldoLabel}>{t('Saldo {year}', { year })}</span>
            <span style={{ color: saldo > 0 ? tokens.colorPaletteGreenForeground1 : saldo < 0 ? tokens.colorPaletteRedForeground1 : undefined }}>{fmtMoney(saldo)}</span>
          </div></>}
      </>}
      view={<></>} />
    <div className={s.split}>
      <SidePane as="aside" className={mergeClasses(s.side, nv.pane)} open={mobile ? drawer : true} onClose={closeSide} width={{ key: 'finance.side', def: 220 }}>
        {TREE.map((x, n) => typeof x === 'function'
          ? <div key={'h' + n} className={nv.section}>{x()}</div>
          : <div key={x[0]} className={mergeClasses(nv.row, page === x[0] && nv.active)} {...pressable(() => go(x[0]))} onClick={() => { go(x[0]); if (mobile) closeSide() }}>
            {x[1]}<span className={nv.name}>{x[2]()}</span>{counts[x[0]] !== undefined && <span className={nv.count}>{counts[x[0]]}</span>}
          </div>)}
      </SidePane>
      <ActionSlot.Provider value={slot}><div className={s.body} key={stamp}>
        {(page === 'income' || page === 'expense') && <EntriesPage key={page} kind={page} year={year} years={years} setYear={setYear} reloadYears={loadYears} cats={cats} />}
        {page === 'invoice' && <DocsPage kind="invoice" onChanged={docsChanged} />}
        {page === 'offer' && <DocsPage kind="offer" onChanged={docsChanged} />}
        {page === 'order' && <DocsPage kind="order" onChanged={docsChanged} />}
        {page === 'customers' && <CustomersPage />}
        {page === 'services' && <ServicesPage />}
        {page === 'templates' && <TemplatesPage />}
        {page === 'summary' && <SummaryPage year={year} years={years} setYear={setYear} reloadYears={loadYears} />}
        {page === 'settings' && <SettingsPage cats={cats} reloadCats={loadCats} />}
      </div></ActionSlot.Provider>
    </div>
  </div>
}
