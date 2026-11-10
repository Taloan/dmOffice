import { Button, Spinner, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Calendar20Regular, DocumentText20Regular, Person20Regular, SelectObject20Regular, Tag20Regular, TextT20Regular } from '@fluentui/react-icons'
import { useRef, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { askConfirm } from '../shell/context'
import { Doc, Entry, Kind } from './model'

/** Feld, das aus einem Rahmen im Dokument übernommen wird. */
export type PickField = 'title' | 'docDate' | 'correspondent' | 'doctype' | 'tag'

const useStyles = makeStyles({
  bar: { display: 'flex', flexDirection: 'column', gap: '4px', width: '240px', flexShrink: 0, position: 'sticky', top: 0, alignSelf: 'flex-start' },
  field: { display: 'flex', alignItems: 'flex-start', gap: '8px', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', textAlign: 'left',
    border: `1px solid transparent`, backgroundColor: 'transparent', color: 'inherit', font: 'inherit',
    ':hover': { backgroundColor: tokens.colorSubtleBackgroundHover } },
  active: { border: `1px solid ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  value: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  muted: { color: tokens.colorNeutralForeground3, overflowWrap: 'anywhere' },
  page: { position: 'relative', lineHeight: 0, '& img': { maxWidth: '100%', boxShadow: tokens.shadow8, backgroundColor: '#fff', userSelect: 'none' } },
  picking: { cursor: 'crosshair' },
  rect: { position: 'absolute', border: `2px solid ${tokens.colorBrandStroke1}`, backgroundColor: 'rgba(15, 108, 189, 0.12)', pointerEvents: 'none' },
})

const MONTHS: Record<string, number> = {
  jan: 1, januar: 1, january: 1, jän: 1, jänner: 1, feb: 2, februar: 2, february: 2, mär: 3, märz: 3, mrz: 3, mar: 3, march: 3, apr: 4, april: 4,
  mai: 5, may: 5, jun: 6, juni: 6, june: 6, jul: 7, juli: 7, july: 7, aug: 8, august: 8, sep: 9, sept: 9, september: 9,
  okt: 10, oktober: 10, oct: 10, october: 10, nov: 11, november: 11, dez: 12, dezember: 12, dec: 12, december: 12,
}

/** Datum aus markiertem Text: 27.01.2026, 27.1.26, 2026-01-27, 27. Januar 2026; null, wenn keines dabei ist. */
export function parseDay(text: string): string | null {
  const iso = (y: number, m: number, d: number) => {
    if (y < 100) y += y < 70 ? 2000 : 1900
    const dt = new Date(Date.UTC(y, m - 1, d))
    if (dt.getUTCFullYear() !== y || dt.getUTCMonth() !== m - 1 || dt.getUTCDate() !== d) return null
    return `${y}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`
  }
  let m = text.match(/(\d{4})-(\d{1,2})-(\d{1,2})/)
  if (m) return iso(+m[1], +m[2], +m[3])
  m = text.match(/(\d{1,2})\s*\.\s*(\d{1,2})\s*\.\s*(\d{4}|\d{2})(?!\d)/)
  if (m) return iso(+m[3], +m[2], +m[1])
  m = text.match(/(\d{1,2})\.?\s+([A-Za-zÄÖÜäöü]+)\.?\s+(\d{4})/)
  if (m && MONTHS[m[2].toLowerCase()]) return iso(+m[3], MONTHS[m[2].toLowerCase()], +m[1])
  return null
}

const nameKey = (n: string) => n.replace(/\s+/g, '').toLowerCase()

/** Rahmen ziehen und Feld übernehmen: gemeinsam für die große Seitenansicht und die Bearbeitungsansicht. */
/**
 * Rahmen im Dokument ziehen und den Text darin lesen (Textschicht der PDF, sonst Texterkennung des Ausschnitts auf dem Server).
 * Für das DMS und für Belege in Finanzen. take bekommt Feld und Text und meldet, ob er übernommen wurde; sonst bleibt das Feld gewählt.
 */
export function useRegionPick<F extends string>(docId: number, tresor: boolean, take: (field: F, text: string) => Promise<boolean> | boolean) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [field, setField] = useState<F | null>(null)
  const [drag, setDrag] = useState<{ page: number; x0: number; y0: number; x1: number; y1: number } | null>(null)
  const [busy, setBusy] = useState(false)
  const start = useRef<{ page: number; box: DOMRect } | null>(null)

  const frac = (e: React.PointerEvent, box: DOMRect) => ({ x: Math.min(1, Math.max(0, (e.clientX - box.left) / box.width)), y: Math.min(1, Math.max(0, (e.clientY - box.top) / box.height)) })
  const down = (page: number) => (e: React.PointerEvent<HTMLDivElement>) => {
    if (!field || busy || e.button !== 0) return
    e.preventDefault()
    const box = e.currentTarget.getBoundingClientRect()
    start.current = { page, box }
    const p = frac(e, box)
    setDrag({ page, x0: p.x, y0: p.y, x1: p.x, y1: p.y })
    e.currentTarget.setPointerCapture(e.pointerId)
  }
  const move = (e: React.PointerEvent) => {
    if (!start.current || !drag) return
    const p = frac(e, start.current.box)
    setDrag({ ...drag, x1: p.x, y1: p.y })
  }
  const up = async () => {
    const d = drag, f = field
    start.current = null
    if (!d || !f) return
    // Zu klein (versehentlicher Klick): nichts tun
    if (Math.abs(d.x1 - d.x0) < 0.01 || Math.abs(d.y1 - d.y0) < 0.005) { setDrag(null); return }
    setBusy(true)
    try {
      const r = await api.post<{ text: string; ocr: boolean }>(`/api/dms/docs/${docId}/region` + (tresor ? '?area=tresor' : ''),
        { page: d.page, x0: d.x0, y0: d.y0, x1: d.x1, y1: d.y1 })
      const text = r.text.replace(/\s+/g, ' ').trim()
      if (!text) notify(t('Im Rahmen wurde kein Text gefunden.'))
      else if (await take(f, text)) setField(null)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const choose = (f: F | null) => { setField(field === f ? null : f); setDrag(null) }

  /** Die Seiten als Bilder; mit gewähltem Feld lässt sich ein Rahmen ziehen. */
  const pagesView = (pages: number[], src: (n: number, width: number) => string, width = 1600) => (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, alignItems: 'center' }}>
      {pages.map(n => (
        <div key={n} className={mergeClasses(s.page, !!field && s.picking)} onPointerDown={down(n)} onPointerMove={move} onPointerUp={up}>
          <img src={src(n, width)} alt={t('Seite {n}', { n: n + 1 })} loading="lazy" draggable={false} />
          {drag?.page === n && <div className={s.rect} style={{
            left: `${Math.min(drag.x0, drag.x1) * 100}%`, top: `${Math.min(drag.y0, drag.y1) * 100}%`,
            width: `${Math.abs(drag.x1 - drag.x0) * 100}%`, height: `${Math.abs(drag.y1 - drag.y0) * 100}%`,
          }} />}
        </div>
      ))}
    </div>
  )
  return { field, choose, busy, pagesView }
}

export function usePick({ doc, tresor, entries, onChange, onEntriesChanged, onText }: {
  doc: Doc; tresor: boolean; entries: Entry[]; onChange: (body: Record<string, unknown>) => Promise<unknown>; onEntriesChanged: () => void
  /** Text ohne passenden Eintrag: ins Feld zum Prüfen und Auswählen geben, statt gleich einen Eintrag anzulegen */
  onText?: (kind: Kind, text: string) => void
}) {
  const { notify } = useApp()
  const [last, setLast] = useState<{ field: PickField; text: string } | null>(null)

  // Vorhandener Eintrag (Groß/klein und Leerzeichen egal); sonst nichts anlegen, ohne dass es jemand prüft - die Erkennung irrt
  // sich (aus „fotoboerse.de“ wird „zroeboerse.de“): ins Feld geben oder erst nachfragen. null: nichts gesetzt
  const entryFor = async (kind: Kind, text: string) => {
    const found = entries.find(e => e.kind === kind && nameKey(e.name) === nameKey(text))
    if (found) return found.id
    if (onText) { onText(kind, text); return null }
    if (!await askConfirm(t('Neuer Eintrag'), t('„{name}“ gibt es noch nicht. Neu anlegen und zuordnen?', { name: text }), t('Anlegen'))) return null
    const created = await api.post<Entry>('/api/dms/lists', { kind, name: text })
    onEntriesChanged()
    return created.id
  }

  const take = async (f: PickField, text: string) => {
    if (f === 'title') await onChange({ title: text })
    else if (f === 'docDate') {
      const day = parseDay(text)
      if (!day) { notify(t('Kein Datum erkannt: „{text}“', { text })); return false }
      await onChange({ docDate: day })
    }
    else {
      const kind: Kind = f === 'tag' ? 'tag' : f
      const id = await entryFor(kind, text)
      if (id) await onChange(kind === 'tag' ? { addTags: [id] } : { [kind]: id })
    }
    setLast({ field: f, text })
    return true
  }
  const region = useRegionPick<PickField>(doc.id, tresor, take)
  return { ...region, last }
}

export type Pick = ReturnType<typeof usePick>

/** Knopf an einem Feld der Bearbeitungsansicht: „Aus dem Dokument übernehmen“ (gewählt: Rahmen ziehen). */
export function PickButton({ pick, field }: { pick: Pick; field: PickField }) {
  const on = pick.field === field
  return (
    <Button size="small" appearance={on ? 'primary' : 'subtle'} icon={<SelectObject20Regular />} disabled={pick.busy}
      title={on ? t('Im Dokument einen Rahmen um den Text ziehen') : t('Aus dem Dokument übernehmen')} aria-pressed={on} onClick={() => pick.choose(field)} />
  )
}

/**
 * Große Seitenansicht mit Leiste der Felder: Feld wählen, im Dokument einen Rahmen um den Text ziehen - der Text wird in das Feld
 * übernommen (Textschicht der PDF, sonst Texterkennung des Ausschnitts auf dem Server). Korrespondent, Dokumenttyp und Tag:
 * vorhandener Eintrag (Groß/klein und Leerzeichen egal), sonst neu angelegt.
 */
export function PickPages({ doc, tresor, pages, src, entries, onChange, onEntriesChanged }: {
  doc: Doc; tresor: boolean; pages: number[]; src: (n: number, width: number) => string; entries: Entry[]
  onChange: (body: Record<string, unknown>) => Promise<unknown>; onEntriesChanged: () => void
}) {
  const s = useStyles()
  const pick = usePick({ doc, tresor, entries, onChange, onEntriesChanged })
  const name = (id: string | null) => id ? entries.find(e => e.id === id)?.name ?? '' : ''
  const fields: { key: PickField; label: string; icon: React.ReactNode; value: string }[] = [
    { key: 'title', label: t('Titel'), icon: <TextT20Regular />, value: doc.title },
    { key: 'docDate', label: t('Dokumentdatum'), icon: <Calendar20Regular />, value: doc.docDate ? new Date(doc.docDate + 'T00:00:00').toLocaleDateString() : '' },
    { key: 'correspondent', label: t('Korrespondent'), icon: <Person20Regular />, value: name(doc.correspondent) },
    { key: 'doctype', label: t('Dokumenttyp'), icon: <DocumentText20Regular />, value: name(doc.doctype) },
    { key: 'tag', label: t('Tag hinzufügen'), icon: <Tag20Regular />, value: doc.tags.map(x => name(x.id)).filter(Boolean).join(', ') },
  ]
  return (
    <div style={{ display: 'flex', gap: 16, alignItems: 'flex-start' }}>
      <div style={{ flex: 1, minWidth: 0 }}>{pick.pagesView(pages, src)}</div>
      <div className={s.bar}>
        <Text weight="semibold"><SelectObject20Regular style={{ verticalAlign: 'middle', marginRight: 6 }} />{t('Aus dem Dokument übernehmen')}</Text>
        <Text size={200} className={s.muted}>{t('Feld wählen, dann im Dokument einen Rahmen um den Text ziehen.')}</Text>
        {fields.map(f => (
          <button key={f.key} type="button" className={mergeClasses(s.field, pick.field === f.key && s.active)} aria-pressed={pick.field === f.key}
            onClick={() => pick.choose(f.key)}>
            {f.icon}
            <span className={s.value}>
              <Text weight="semibold">{f.label}</Text>
              <Text size={200} className={s.muted}>{f.value || '–'}</Text>
            </span>
          </button>
        ))}
        {pick.busy && <Spinner size="tiny" label={t('Text wird gelesen …')} />}
        {pick.last && !pick.busy && <Text size={200} className={s.muted}>{t('Übernommen: „{text}“', { text: pick.last.text })}</Text>}
        {pick.field && <Button size="small" appearance="subtle" onClick={() => pick.choose(null)}>{t('Abbrechen')}</Button>}
      </div>
    </div>
  )
}
