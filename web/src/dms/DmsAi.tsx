import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Option, ProgressBar, Radio, RadioGroup, Spinner, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { Checkmark20Regular, Delete20Regular, Dismiss20Regular, FolderArrowRight20Regular, Open20Regular, Sparkle20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { EntrySelect } from './EntrySelect'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { fullName, kindLabel, type Doc, type Entry, type Kind } from './model'

/** Lauf der KI-Prüfung (Server: DmsAiCheck.Run) */
interface Run { id: number; kind: 'check' | 'assign'; field: Kind; entry: string | null; mode: 'existing' | 'new'; state: 'running' | 'done' | 'cancelled' | 'failed'; total: number; done: number; created: string; error: string | null }
interface Suggestion { id: number; run: number; doc: number; field: Kind; current: string | null; verdict: 'fits' | 'doubt' | 'wrong' | null; proposalEntry: string | null; proposalNew: string | null; proposalParent: string | null; confidence: number; reason: string | null; state: string }

const useStyles = makeStyles({
  page: { flex: 1, overflow: 'auto', padding: '12px 20px', display: 'flex', flexDirection: 'column', gap: '10px', minWidth: 0 },
  bar: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  run: { display: 'flex', gap: '10px', alignItems: 'center', fontSize: '12px', color: tokens.colorNeutralForeground2 },
  table: { width: '100%', borderCollapse: 'collapse', fontSize: '13px', '& td, & th': { padding: '5px 8px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, textAlign: 'left', verticalAlign: 'middle' },
    '& th': { fontWeight: 600, color: tokens.colorNeutralForeground3, fontSize: '12px' } },
  wrong: { color: tokens.colorPaletteRedForeground1, fontWeight: 600 },
  doubt: { color: tokens.colorPaletteMarigoldForeground1, fontWeight: 600 },
  newVal: { color: tokens.colorBrandForeground1, fontWeight: 600 },
  muted: { color: tokens.colorNeutralForeground3 },
})

const verdictLabel = (v: Suggestion['verdict']) => v === 'wrong' ? t('passt nicht') : v === 'doubt' ? t('zweifelhaft') : v === 'fits' ? t('passt') : ''

/** Startdialog: Zuordnung eines Eintrags prüfen bzw. Dokumente ohne Zuordnung einordnen (mit Modus). */
export function AiStartDialog({ kind, entry, entries, service, onClose, onStarted }: {
  kind: Kind; entry: Entry | null; entries: Entry[]; service: string | null; onClose: () => void; onStarted: () => void
}) {
  const { fail } = useApp()
  const [mode, setMode] = useState<'existing' | 'new'>(kind === 'correspondent' ? 'new' : 'existing')
  const [busy, setBusy] = useState(false)
  const start = async () => {
    setBusy(true)
    try { await api.post('/api/dms/ai/runs', { kind: entry ? 'check' : 'assign', field: kind, entry: entry?.id ?? null, mode }); onStarted() }
    catch (e) { fail(e); setBusy(false) }
  }
  return <Dialog open onOpenChange={(_, d) => { if (!d.open && !busy) onClose() }}>
    <DialogSurface style={{ maxWidth: 520 }}>
      <DialogBody>
        <DialogTitle>{entry ? t('Zuordnung mit KI prüfen') : t('Mit KI einordnen')}</DialogTitle>
        <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
          <Text>{entry
            ? t('Die KI prüft die Dokumente mit „{name}“ und markiert, was nicht passt. Ergebnis: eine Vorschlagsliste - geändert wird erst, was du übernimmst.', { name: fullName(entries, entry.id) })
            : t('Die KI schlägt für Dokumente ohne {kind} einen Eintrag vor. Ergebnis: eine Vorschlagsliste - geändert wird erst, was du übernimmst.', { kind: kindLabel(kind) })}</Text>
          {!entry && <RadioGroup value={mode} onChange={(_, d) => setMode(d.value as 'existing' | 'new')}>
            <Radio value="existing" label={t('Nur vorhandene Einträge - was nicht passt, bleibt ohne Zuordnung')} />
            <Radio value="new" label={t('Auch neue Einträge vorschlagen (mit Oberbegriff) - angelegt erst beim Übernehmen')} />
          </RadioGroup>}
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            {t('An {service} gehen je Dokument Titel, Angaben und ein Auszug des Texts. Höchstens 200 Dokumente je Lauf. Der Tresor geht nie an die KI.', { service: service ?? t('die eingerichtete KI') })}</Text>
        </DialogContent>
        <DialogActions>
          <Button disabled={busy} onClick={onClose}>{t('Abbrechen')}</Button>
          <Button appearance="primary" icon={busy ? <Spinner size="tiny" /> : <Sparkle20Regular />} disabled={busy} onClick={start}>{t('Starten')}</Button>
        </DialogActions>
      </DialogBody>
    </DialogSurface>
  </Dialog>
}

/**
 * KI-Vorschläge: Läufe mit Fortschritt, darunter die offenen Vorschläge (ohne „passt“). Markierte übernehmen (auf Wunsch gleich nach
 * Vorgabe ablegen) oder verwerfen; der Vorschlag lässt sich vorher ändern. Klick auf den Titel öffnet das Dokument.
 */
export function AiSuggestions({ entries, onOpenDoc, onChanged }: { entries: Entry[]; onOpenDoc: (d: Doc) => void; onChanged: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [runs, setRuns] = useState<Run[]>([])
  const [list, setList] = useState<{ suggestion: Suggestion; doc: Doc }[] | null>(null)
  const [picked, setPicked] = useState<Set<number>>(new Set())
  const [filter, setFilter] = useState<'' | 'wrong' | 'doubt' | 'new' | 'assign'>('')
  const [busy, setBusy] = useState(false)
  // Übernehmen läuft in Paketen, damit Fortschritt sichtbar ist (je Dokument lernt und legt der Server neu ab - das dauert)
  const [progress, setProgress] = useState<{ what: 'apply' | 'refile'; done: number; total: number } | null>(null)
  const load = () => {
    api.get<Run[]>('/api/dms/ai/runs').then(setRuns).catch(() => {})
    api.get<{ suggestion: Suggestion; doc: Doc }[]>('/api/dms/ai/suggestions').then(setList).catch(fail)
  }
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Solange ein Lauf arbeitet: alle 2 Sekunden nachladen
  const running = runs.some(r => r.state === 'running')
  useEffect(() => {
    if (!running) return
    const h = window.setInterval(load, 2000)
    return () => window.clearInterval(h)
  }, [running]) // eslint-disable-line react-hooks/exhaustive-deps

  const rows = (list ?? []).filter(({ suggestion: x }) => !filter || (filter === 'new' ? !!x.proposalNew : filter === 'assign' ? !x.verdict : x.verdict === filter))
  const all = rows.length > 0 && rows.every(r => picked.has(r.suggestion.id))
  const ids = rows.filter(r => picked.has(r.suggestion.id)).map(r => r.suggestion.id)
  const act = async (what: 'apply' | 'refile' | 'drop') => {
    if (!ids.length) return
    setBusy(true)
    try {
      if (what === 'drop') { const r = await api.post<{ dropped: number }>('/api/dms/ai/suggestions/drop', { ids }); notify(t('{n} Vorschläge verworfen', { n: r.dropped })) }
      else {
        let changed = 0
        setProgress({ what, done: 0, total: ids.length })
        for (let i = 0; i < ids.length; i += 10) {
          const part = ids.slice(i, i + 10)
          const r = await api.post<{ changed: number }>('/api/dms/ai/suggestions/apply', { ids: part, refile: what === 'refile' })
          changed += r.changed
          setProgress({ what, done: Math.min(i + part.length, ids.length), total: ids.length })
          setPicked(p => { const n = new Set(p); part.forEach(x => n.delete(x)); return n })
          load()
        }
        notify(t('Fertig: {n} Dokumente geändert', { n: changed })); onChanged()
      }
      setPicked(new Set()); load()
    } catch (e) { fail(e); load() } finally { setBusy(false); setProgress(null) }
  }
  const edit = async (x: Suggestion, entry: string) => {
    try { await api.put(`/api/dms/ai/suggestions/${x.id}`, { entry: entry || null, newName: null, parent: null }); load() } catch (e) { fail(e) }
  }
  const proposal = (x: Suggestion) => x.proposalNew
    ? <span className={s.newVal} title={t('Wird beim Übernehmen angelegt')}>{t('Neu: {name}', { name: (x.proposalParent ? fullName(entries, x.proposalParent) + ' › ' : '') + x.proposalNew })}</span>
    : x.proposalEntry ? fullName(entries, x.proposalEntry) : x.verdict ? <span className={s.muted}>{t('entfernen')}</span> : ''
  const noProposal = (x: Suggestion) => !x.verdict && !x.proposalEntry && !x.proposalNew

  return <div className={s.page}>
    {runs.filter(r => r.state === 'running' || r.state === 'failed').slice(0, 3).map(r => <div key={r.id} className={s.run}>
      <Sparkle20Regular />
      <span>{r.kind === 'check' ? t('Prüfe „{name}“', { name: fullName(entries, r.entry) }) : t('Ordne Dokumente ohne {kind} ein', { kind: kindLabel(r.field) })}</span>
      {r.state === 'running' ? <><ProgressBar value={r.total ? r.done / r.total : 0} style={{ width: 160 }} /><span>{r.done} / {r.total}</span>
        <Button size="small" appearance="subtle" icon={<Dismiss20Regular />} onClick={() => api.post(`/api/dms/ai/runs/${r.id}/cancel`).then(load).catch(fail)}>{t('Abbrechen')}</Button></>
        : <span className={s.wrong}>{t('Fehlgeschlagen: {error}', { error: r.error ?? '' })}</span>}
    </div>)}
    <RibbonActions>
      <RibbonButton primary icon={<Checkmark20Regular />} label={t('Übernehmen')} disabled={busy || !ids.length} onClick={() => act('apply')} />
      <RibbonButton icon={<FolderArrowRight20Regular />} label={t('Übernehmen und nach Vorgabe ablegen')} disabled={busy || !ids.length} onClick={() => act('refile')} />
      <RibbonButton icon={<Delete20Regular />} label={t('Verwerfen')} disabled={busy || !ids.length} onClick={() => act('drop')} />
    </RibbonActions>
    <div className={s.bar}>
      <Dropdown aria-label={t('Filter')} style={{ minWidth: 200 }} selectedOptions={[filter]} onOptionSelect={(_, d) => { setFilter((d.optionValue ?? '') as typeof filter); setPicked(new Set()) }}
        value={filter === 'wrong' ? t('Nur „passt nicht“') : filter === 'doubt' ? t('Nur „zweifelhaft“') : filter === 'new' ? t('Nur neue Einträge') : filter === 'assign' ? t('Nur Einordnen') : t('Alle Vorschläge')}>
        <Option value="">{t('Alle Vorschläge')}</Option><Option value="wrong">{t('Nur „passt nicht“')}</Option><Option value="doubt">{t('Nur „zweifelhaft“')}</Option>
        <Option value="assign">{t('Nur Einordnen')}</Option><Option value="new">{t('Nur neue Einträge')}</Option>
      </Dropdown>
      {progress && <span className={s.run}><Spinner size="tiny" />
        <span>{progress.what === 'refile' ? t('Übernehme und lege ab …') : t('Übernehme …')}</span>
        <ProgressBar value={progress.total ? progress.done / progress.total : 0} style={{ width: 160 }} /><span>{progress.done} / {progress.total}</span></span>}
    </div>
    {list === null ? <Spinner /> : rows.length === 0
      ? <Text className={s.muted}>{running ? t('Die KI arbeitet - Vorschläge erscheinen hier nach und nach.') : t('Keine offenen Vorschläge. Starten lässt sich eine Prüfung im Baum per Rechtsklick auf einen Eintrag oder eine Liste.')}</Text>
      : <table className={s.table}>
        <thead><tr>
          <th style={{ width: 32 }}><Checkbox checked={all ? true : ids.length ? 'mixed' : false} onChange={() => setPicked(all ? new Set() : new Set(rows.map(r => r.suggestion.id)))} /></th>
          <th>{t('Dokument')}</th><th>{t('Liste')}</th><th>{t('Jetzt')}</th><th>{t('Ergebnis')}</th><th>{t('Vorschlag')}</th><th>{t('dms|Sicherheit')}</th><th>{t('Grund')}</th>
        </tr></thead>
        <tbody>
          {rows.map(({ suggestion: x, doc: d }) => <tr key={x.id}>
            <td><Checkbox checked={picked.has(x.id)} onChange={(_, c) => setPicked(p => { const n = new Set(p); if (c.checked) n.add(x.id); else n.delete(x.id); return n })} /></td>
            <td><Button appearance="transparent" size="small" icon={<Open20Regular />} iconPosition="after" onClick={() => onOpenDoc(d)} style={{ padding: 0, minWidth: 0, justifyContent: 'flex-start', textAlign: 'left' }}>{d.title || d.originalName}</Button></td>
            <td>{kindLabel(x.field)}</td>
            <td>{x.current ? fullName(entries, x.current) : <span className={s.muted}>{t('(keiner)')}</span>}</td>
            <td>{x.verdict ? <span className={x.verdict === 'wrong' ? s.wrong : s.doubt}>{verdictLabel(x.verdict)}</span> : noProposal(x) ? <span className={s.muted}>{t('kein Vorschlag')}</span> : t('einordnen')}</td>
            <td style={{ minWidth: 200 }}>{x.proposalNew ? proposal(x)
              : <EntrySelect entries={entries} kind={x.field} value={x.proposalEntry} none={x.verdict ? t('(entfernen)') : t('(keiner)')} size="small" onPick={id => edit(x, id)} />}</td>
            <td>{x.confidence} %</td>
            <td className={s.muted} style={{ maxWidth: 320 }}>{x.reason}</td>
          </tr>)}
        </tbody>
      </table>}
  </div>
}
