// Eigene Felder im DMS (Kennung dmsfields): Felder mit Name und Art, Werte am Dokument. Server: DmOffice.Server/Dms/DmsFields.cs
import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Spinner, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { Add20Regular, ArrowDown20Regular, ArrowUp20Regular, Delete20Regular, Open16Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, featureOn } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { askConfirm } from '../shell/context'
import { moneyText, parseMoney } from './money'
import type { Entry } from './model'
import { parseDay } from './DmsPick'

export type FieldType = 'text' | 'number' | 'date' | 'money' | 'url'
export interface DmsField { id: string; name: string; type: FieldType; doctypes: string[]; sort: number }

export const fieldTypeLabel = (x: FieldType) => x === 'text' ? t('Text') : x === 'number' ? t('Zahl') : x === 'date' ? t('Datum') : x === 'money' ? t('Betrag') : t('Link')

// Die Felder ändern sich selten: einmal laden, nach dem Bearbeiten neu (Ereignis für alle Ansichten)
const EVENT = 'dmoffice:dmsfields'
let cache: DmsField[] | null = null
let loading: Promise<DmsField[]> | null = null
const load = () => loading ??= api.get<DmsField[]>('/api/dms/fields').then(x => { cache = x; return x }).catch(() => []).finally(() => { loading = null })

/** Eigene Felder des DMS ([] ohne Schalter dmsfields) */
export function useDmsFields(): DmsField[] {
  const on = featureOn('dmsfields')
  const [fields, setFields] = useState<DmsField[]>(cache ?? [])
  useEffect(() => {
    if (!on) return
    let live = true
    const get = () => load().then(x => { if (live) setFields(x) })
    if (!cache) get()
    const reload = () => { cache = null; get() }
    window.addEventListener(EVENT, reload)
    return () => { live = false; window.removeEventListener(EVENT, reload) }
  }, [on])
  return on ? fields : []
}

/** Wert zum Anzeigen (Liste, Detail): Datum und Betrag in der Schreibweise der Sprache */
export function fieldText(f: DmsField, value: string | null | undefined): string {
  if (value == null || value === '') return ''
  const loc = lang === 'en' ? 'en-GB' : 'de-DE'
  if (f.type === 'date') return new Date(value + 'T00:00:00').toLocaleDateString(loc)
  if (f.type === 'money') return (Number(value) / 100).toLocaleString(loc, { style: 'currency', currency: 'EUR' })
  if (f.type === 'number') return Number(value).toLocaleString(loc, { maximumFractionDigits: 10 })
  return value
}

/** Text aus dem Dokument (Markierwerkzeug) in den Wert eines Felds; null: passt nicht (etwa kein Datum erkannt) */
export function fieldValueFromText(f: DmsField, text: string): string | null {
  if (f.type === 'date') return parseDay(text)
  if (f.type === 'money') { const c = parseMoney(text.replace(/[^\d,.-]/g, '')); return c == null ? null : String(c) }
  if (f.type === 'number') { const m = text.replace(/\s/g, '').match(/-?\d+(?:[.,]\d+)?/); return m ? m[0].replace(',', '.') : null }
  return text
}

/** Eingabe eines Felds im Detailbereich: Wert erst beim Verlassen übernehmen (wie Titel und Betrag) */
export function FieldInput({ field, value, onSave }: { field: DmsField; value: string | null; onSave: (v: string) => void }) {
  const shown = (v: string | null) => v == null ? '' : field.type === 'money' ? moneyText(Number(v)) : v
  const [text, setText] = useState(shown(value))
  useEffect(() => { setText(shown(value)) }, [value]) // eslint-disable-line react-hooks/exhaustive-deps
  const commit = (raw: string) => {
    const v = field.type === 'money' ? (raw.trim() ? String(parseMoney(raw) ?? '') : '') : raw.trim()
    if (field.type === 'money' && raw.trim() && !v) { setText(shown(value)); return }
    if (v !== (value ?? '')) onSave(v)
  }
  if (field.type === 'date') return <Input type="date" value={text} onChange={(_, d) => { setText(d.value); if (d.value !== (value ?? '')) onSave(d.value) }} />
  return <Input value={text} inputMode={field.type === 'number' || field.type === 'money' ? 'decimal' : undefined} contentAfter={field.type === 'money' ? '€'
    : field.type === 'url' && value ? <a href={value} target="_blank" rel="noopener noreferrer" title={t('Link öffnen')} style={{ display: 'inline-flex' }}><Open16Regular /></a> : undefined}
    onChange={(_, d) => setText(d.value)} onBlur={() => commit(text)} onKeyDown={e => { if (e.key === 'Enter') (e.target as HTMLInputElement).blur() }} />
}

const useStyles = makeStyles({
  body: { display: 'flex', gap: '16px', height: '420px', minHeight: 0 },
  list: { width: '240px', flexShrink: 0, display: 'flex', flexDirection: 'column', gap: '6px', minHeight: 0 },
  items: { flex: 1, overflowY: 'auto', display: 'flex', flexDirection: 'column', gap: '2px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, padding: '4px' },
  item: { textAlign: 'left', padding: '6px 8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', border: 'none', background: 'transparent', color: 'inherit', font: 'inherit' },
  itemOn: { backgroundColor: tokens.colorNeutralBackground1Selected, fontWeight: 600 },
  sub: { display: 'block', fontSize: '11px', color: tokens.colorNeutralForeground3, fontWeight: 400 },
  edit: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: '10px' },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
})

type Draft = { id: string | null; name: string; type: FieldType; doctypes: string[] }

/** Eigene Felder verwalten: links die Felder, rechts das gewählte (Name, Art, Dokumenttypen) - der Dialog scrollt nicht */
export function DmsFieldsDialog({ entries, onClose }: { entries: Entry[]; onClose: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [fields, setFields] = useState<DmsField[] | null>(null)
  const [draft, setDraft] = useState<Draft | null>(null)
  const [busy, setBusy] = useState(false)
  const reload = async (select?: string) => {
    const x = await api.get<DmsField[]>('/api/dms/fields')
    setFields(x); cache = x; window.dispatchEvent(new Event(EVENT))
    const f = x.find(y => y.id === select) ?? (select === undefined ? x[0] : undefined)
    setDraft(f ? { id: f.id, name: f.name, type: f.type, doctypes: f.doctypes } : x.length ? null : { id: null, name: '', type: 'text', doctypes: [] })
  }
  useEffect(() => { reload().catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!fields) return null
  const doctypes = entries.filter(e => e.kind === 'doctype').sort((a, b) => a.name.localeCompare(b.name))
  const save = async () => {
    if (!draft) return
    setBusy(true)
    try {
      const body = { name: draft.name, type: draft.type, doctypes: draft.doctypes }
      const f = draft.id ? await api.put<DmsField>(`/api/dms/fields/${draft.id}`, body) : await api.post<DmsField>('/api/dms/fields', body)
      notify(t('Feld gespeichert')); await reload(f.id)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const remove = async () => {
    if (!draft?.id || !await askConfirm(t('Feld löschen'), t('„{name}“ löschen? Die Werte an den Dokumenten werden nicht mehr angezeigt.', { name: draft.name }), t('Löschen'))) return
    try { await api.del(`/api/dms/fields/${draft.id}`); await reload() } catch (e) { fail(e) }
  }
  const move = async (d: number) => {
    const i = fields.findIndex(f => f.id === draft?.id)
    if (i < 0 || i + d < 0 || i + d >= fields.length) return
    const ids = fields.map(f => f.id); [ids[i], ids[i + d]] = [ids[i + d], ids[i]]
    try { await api.put('/api/dms/fields-order', ids); await reload(draft!.id!) } catch (e) { fail(e) }
  }
  const picked = (id: string) => draft?.doctypes.includes(id) ?? false
  return <Dialog open onOpenChange={(_, d) => { if (!d.open && !busy) onClose() }}>
    <DialogSurface style={{ maxWidth: 860, width: '95vw' }}>
      <DialogBody>
        <DialogTitle>{t('Eigene Felder')}</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <Text size={200} className={s.muted}>{t('Für Angaben, die es nicht als Korrespondent, Dokumenttyp oder Tag gibt - etwa Steuernummer, Vertragsnummer, Kündbar bis. Die Werte stehen am Dokument, lassen sich durchsuchen und als Spalte zeigen.')}</Text>
          <div className={s.body}>
            <div className={s.list}>
              <div className={s.items}>
                {fields.length === 0 && <Text className={s.muted} style={{ padding: 6 }}>{t('Noch keine eigenen Felder.')}</Text>}
                {fields.map(f => <button key={f.id} type="button" className={mergeClasses(s.item, draft?.id === f.id && s.itemOn)}
                  onClick={() => setDraft({ id: f.id, name: f.name, type: f.type, doctypes: f.doctypes })}>
                  {f.name}<span className={s.sub}>{fieldTypeLabel(f.type)}{f.doctypes.length ? ' · ' + f.doctypes.map(id => entries.find(e => e.id === id)?.name).filter(Boolean).join(', ') : ''}</span>
                </button>)}
              </div>
              <div className={s.row}>
                <Button size="small" icon={<Add20Regular />} onClick={() => setDraft({ id: null, name: '', type: 'text', doctypes: [] })}>{t('Neues Feld')}</Button>
                {draft?.id && <>
                  <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} aria-label={t('Nach oben')} onClick={() => move(-1)} />
                  <Button size="small" appearance="subtle" icon={<ArrowDown20Regular />} aria-label={t('Nach unten')} onClick={() => move(1)} />
                </>}
              </div>
            </div>
            {draft && <div className={s.edit}>
              <Field label={t('Name')} required><Input value={draft.name} onChange={(_, d) => setDraft({ ...draft, name: d.value })} placeholder={t('etwa Steuernummer')} /></Field>
              <Field label={t('Art')} hint={draft.id ? t('Die Art lässt sich nach dem Anlegen nicht mehr ändern.') : undefined}>
                <Dropdown disabled={!!draft.id} value={fieldTypeLabel(draft.type)} selectedOptions={[draft.type]} onOptionSelect={(_, d) => setDraft({ ...draft, type: d.optionValue as FieldType })}>
                  {(['text', 'number', 'date', 'money', 'url'] as const).map(x => <Option key={x} value={x}>{fieldTypeLabel(x)}</Option>)}
                </Dropdown>
              </Field>
              <Field label={t('Gleich anbieten bei Dokumenttyp')} hint={t('Bei diesen Dokumenttypen steht das Feld gleich im Detailbereich, sonst über „Feld hinzufügen“.')}>
                <Dropdown multiselect value={draft.doctypes.map(id => entries.find(e => e.id === id)?.name).filter(Boolean).join(', ')} placeholder={t('(keiner)')}
                  selectedOptions={draft.doctypes} onOptionSelect={(_, d) => setDraft({ ...draft, doctypes: d.selectedOptions })}>
                  {doctypes.map(e => <Option key={e.id} value={e.id} text={e.name}>{e.name}{picked(e.id) ? '' : ''}</Option>)}
                </Dropdown>
              </Field>
              <div className={s.row}>
                <Button appearance="primary" disabled={busy || !draft.name.trim()} icon={busy ? <Spinner size="tiny" /> : undefined} onClick={save}>{draft.id ? t('Speichern') : t('Anlegen')}</Button>
                {draft.id && <Button icon={<Delete20Regular />} onClick={remove}>{t('Löschen')}</Button>}
              </div>
            </div>}
          </div>
        </DialogContent>
        <DialogActions><Button onClick={onClose}>{t('Schließen')}</Button></DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}
