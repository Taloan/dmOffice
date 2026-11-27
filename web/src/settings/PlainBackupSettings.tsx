// Einstellungen → Datensicherung (docs/datensicherung-konzept.md): Ziel per WebDAV, Bereiche und Ordner, Uhrzeit, Frist für
// .trash, ältere Versionen, ausdrückliche Bestätigung, Zustand und „Jetzt sichern“. Jede Person für sich.
import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody,
  ProgressBar, Select, Spinner, Switch, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { ArrowUndo20Regular, ChevronDown16Regular, ChevronRight16Regular, Play20Regular, PlugConnected20Regular, Save20Regular, Stop20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api, fmtSize, featureOn } from '../api'
import { useApp } from '../App'
import { lang, locale, t } from '../i18n'
import { plugins } from '../plugins/registry'
import { SchedulePicker } from './SchedulePicker'
import { Hint, Section, SectionTabs } from './parts'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import { BrowseDialog } from '../files/ImportProfiles'
import { askPassword } from '../shell/context'
import { useAutoSave } from './autosave' // [feature:savemode]
import { useRef } from 'react' // [feature:savemode]

const useStyles = makeStyles({
  page: { display: 'flex', flexDirection: 'column', gap: '20px' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '560px' },
  row: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' },
  area: { display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap', minHeight: '32px' },
  muted: { color: tokens.colorNeutralForeground3 },
  ok: { color: tokens.colorPaletteGreenForeground1 },
  bad: { color: tokens.colorPaletteRedForeground1 },
  stats: { display: 'grid', gridTemplateColumns: 'max-content 1fr', gap: '4px 16px', maxWidth: '560px' },
  tree: { display: 'flex', flexDirection: 'column', gap: '2px', maxHeight: '50vh', overflowY: 'auto', minWidth: '320px' },
  node: { display: 'flex', alignItems: 'center', gap: '2px' },
})

// Bereiche von Plugins: „p:<Kennung>“ (W5)
type Area = 'files' | 'gallery' | 'calendar' | 'tasks' | 'contacts' | 'notes' | 'dms' | `p:${string}`
  | 'journal' // [feature:journal]

interface Settings {
  enabled: boolean; mount: string | null; folder: string; time: string; trashDays: number; versions: boolean
  scheduleKind?: 'every' | 'cron' | null; everyCount?: number; everyUnit?: 'h' | 'd' | 'w'; weekday?: number; cron?: string | null
  areas: Area[]; skip: string[]
}
interface State {
  lastRun: string | null; lastOk: string | null; lastError: string | null; seconds: number; files: number; bytes: number
  written: number; moved: number; trashed: number; purged: number; failed: number; fails: number
}
interface Progress { started: string; done: number; total: number; bytes: number; current: string | null }
interface View {
  allowed: boolean; available: Area[]; pluginAreas?: { key: Area; label: { de: string; en: string } }[]; settings: Settings; mounts: { id: string; name: string; kind: string; account?: string | null }[]; oneDrive: boolean; webDav: boolean; confirmed: boolean; ready: boolean; state: State
  next: string | null; running: Progress | null; queued: boolean
}
interface Choice { id: string; name: string; folder: boolean }

const AREAS: { key: Area; label: () => string; hint: () => string; pick?: boolean }[] = [
  { key: 'files', label: () => t('Dateien'), hint: () => t('Originale in der Ordnerstruktur der Ablage'), pick: true },
  { key: 'gallery', label: () => t('Galerie'), hint: () => t('Alben als Liste; die Fotos kommen mit „Dateien“') },
  { key: 'calendar', label: () => t('Kalender'), hint: () => t('eine .ics-Datei je Kalender'), pick: true },
  { key: 'tasks', label: () => t('Aufgaben'), hint: () => t('eine .ics-Datei je Liste'), pick: true },
  { key: 'contacts', label: () => t('Kontakte'), hint: () => t('eine .vcf-Datei je Adressbuch'), pick: true },
  { key: 'notes', label: () => t('Notizen'), hint: () => t('eine .md-Datei je Notiz, Bilder und Anhänge daneben'), pick: true },
  { key: 'dms', label: () => 'DMS', hint: () => t('Liste der Dokumente mit Titel, Datum, Korrespondent, Typ und Tags') },
  // [feature:journal]
  { key: 'journal', label: () => t('Tagebuch'), hint: () => t('eine .md-Datei je Eintrag'), pick: true },
  // [/feature:journal]
]
const TRASH = [7, 30, 90, 0]
/** Erklärung zu einem Bereich aus einem Plugin (exportHints seines Web-Teils), sonst leer. */
const pluginExportHint = (key: string) => plugins().map(p => p.module.exportHints?.[key.slice(2)]).find(Boolean) ?? ''
const when = (iso: string) => new Date(iso).toLocaleString(locale, { dateStyle: 'short', timeStyle: 'short' })

export function PlainBackupSettings() {
  const s = useStyles()
  const { fail, notify, me } = useApp()
  const [view, setView] = useState<View | null>(null)
  // Formular (Passwörter nur, wenn neu eingegeben)
  const [f, setF] = useState<Settings | null>(null)
  const [confirm, setConfirm] = useState(false)
  const [dirty, setDirty] = useState(false)
  const [busy, setBusy] = useState(false)
  const [test, setTest] = useState<{ ok: boolean; text: string } | null>(null)
  const [pick, setPick] = useState<Area | null>(null)
  const [browse, setBrowse] = useState(false)

  const apply = useCallback((v: View, form = true) => {
    setView(v)
    if (form) { setF(v.settings); setConfirm(false); setDirty(false) }
  }, [])
  const load = useCallback((form = true) => api.get<View>('/api/plainbackup').then(v => apply(v, form)).catch(fail), [apply, fail])
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Während eines Laufs den Stand nachladen, ohne das Formular anzufassen
  const active = !!view && (!!view.running || view.queued)
  useEffect(() => {
    if (!active) return
    const timer = setInterval(() => load(false), 3000)
    return () => clearInterval(timer)
  }, [active, load])
  // [feature:savemode]
  // Kennung savemode: speichert von selbst (Felder beim Verlassen); neues Ziel weiter erst nach Bestätigung mit Passwort
  const later = useRef<() => Promise<boolean>>(async () => true)
  // Eingeschaltet mit neuem, noch nicht bestätigtem Ziel: erst nach dem Häkchen (sonst lehnt der Server ab)
  const waiting = !!view && !!f && dirty && f.enabled && !confirm
    && !(view.confirmed && f.mount === view.settings.mount && f.folder.replace(/^\/+|\/+$/g, '') === view.settings.folder)
  const auto = useAutoSave({ f, confirm }, dirty && !waiting, () => later.current())
  // Stand des Formulars jetzt (für „inzwischen weitergetippt?“ nach dem Speichern)
  const fNow = useRef(f)
  fNow.current = f
  // [feature:savemode:else]
  // const auto = false
  // [/feature:savemode]

  if (!view || !f) return <Spinner size="small" />
  const change = (patch: Partial<Settings>) => { setF({ ...f, ...patch }); setDirty(true) }
  // Neues Ziel: erneut bestätigen
  const sameTarget = f.mount === view.settings.mount && f.folder.replace(/^\/+|\/+$/g, '') === view.settings.folder
  const needsConfirm = !(view.confirmed && sameTarget)

  const save = async () => {
    // Neues Ziel bestätigen (dorthin gehen ab jetzt alle Daten lesbar): nur mit dem Anmelde-Passwort
    let accountPassword: string | undefined, code: string | undefined
    if (confirm && needsConfirm) {
      const c = await askPassword(t('Ziel des Datenexports bestätigen'), !!me?.twoFactor)
      // [feature:savemode]
      if (!c) return false
      // [feature:savemode:else]
      // if (!c) return
      // [/feature:savemode]
      accountPassword = c.password
      code = c.code
    }
    setBusy(true)
    try {
      const v = await api.put<View>('/api/plainbackup', {
        enabled: f.enabled, target: 'external', mount: f.mount, folder: f.folder, time: f.time, trashDays: f.trashDays, versions: f.versions,
        areas: f.areas, skip: f.skip, confirm, accountPassword, code,
        ...(featureOn('exportcron') ? { scheduleKind: f.scheduleKind ?? 'every', everyCount: f.everyCount ?? 1, everyUnit: f.everyUnit ?? 'd', weekday: f.weekday ?? 1, cron: f.cron ?? '' } : {}),
      })
      // [feature:savemode]
      // Während des Speicherns weitergetippt: Eingabe bleibt stehen und wird danach gespeichert
      if (auto && fNow.current !== f) setView(v)
      else
      // [/feature:savemode]
      apply(v)
      // [feature:savemode]
      if (!auto) notify(t('Gespeichert'))
      // [feature:savemode:else]
      // notify(t('Gespeichert'))
      // [/feature:savemode]
      return true // [feature:savemode]
    } catch (e) { fail(e) } finally { setBusy(false) }
    return false // [feature:savemode]
  }
  later.current = save // [feature:savemode]
  const runTest = async () => {
    setTest(null); setBusy(true)
    try {
      const r = await api.post<{ ok: boolean; free: number | null }>('/api/plainbackup/test', { target: 'external', mount: f.mount, folder: f.folder })
      setTest({ ok: true, text: r.free != null ? t('Verbindung in Ordnung. Frei: {size}', { size: fmtSize(r.free) }) : t('Verbindung in Ordnung.') })
    } catch (e) { setTest({ ok: false, text: e instanceof Error ? e.message : String(e) }) } finally { setBusy(false) }
  }
  const stop = async () => {
    try { apply(await api.post<View>('/api/plainbackup/stop'), false); notify(t('Export angehalten')) } catch (e) { fail(e) }
  }
  const runNow = async () => {
    try { apply(await api.post<View>('/api/plainbackup/run'), false); notify(t('Export gestartet')) } catch (e) { fail(e) }
  }

  if (!view.allowed) return (
    <div className={s.page}>
      <MessageBar intent="info"><MessageBarBody>{t('Der Datenexport ist auf diesem Server nicht freigeschaltet. Freischalten lässt er sich über die Verwaltung.')}</MessageBarBody></MessageBar>
    </div>
  )

  const st = view.state
  const run = view.running
  return (
    <div className={s.page}>
      <Text>{t('dmOffice legt deine Daten regelmäßig entschlüsselt und in offenen Formaten an einem separaten Speicherort ab. Die Dateien lassen sich ohne dmOffice öffnen. Zurückspielen lässt sich ein Datenexport nicht.')}</Text>
      <Text>{t('dmOffice schreibt ausschließlich an diesen Speicherort und liest keine Daten von dort zurück. Gelöschte Dateien bleiben zunächst im versteckten Ordner .trash erhalten und werden erst nach Ablauf der eingestellten Aufbewahrungsfrist endgültig entfernt.')}</Text>

      <RibbonActions>
        {run || view.queued
          ? <RibbonButton icon={<Stop20Regular />} label={t('Anhalten')} onClick={stop} />
          : <RibbonButton icon={<Play20Regular />} label={t('Jetzt exportieren')} primary disabled={!view.ready || dirty} onClick={runNow} />}
        <RibbonButton icon={<PlugConnected20Regular />} label={t('Verbindung testen')} disabled={busy || !f.mount} onClick={runTest} />
        {!auto && <>
          <RibbonButton icon={<Save20Regular />} label={t('Speichern')} disabled={busy || !dirty} onClick={save} />
          <RibbonButton icon={<ArrowUndo20Regular />} label={t('Verwerfen')} disabled={!dirty} onClick={() => apply(view)} />
        </>}
      </RibbonActions>
      <SectionTabs page="plainbackup">
      <Section title={t('Zustand')}>
        {!view.ready && <Text className={s.muted}>{t('Noch nicht aktiviert.')}</Text>}
        {run && <>
          <ProgressBar value={run.total ? run.done / run.total : undefined} />
          <Text size={200} className={s.muted}>{t('Läuft: {done} von {total}', { done: run.done.toLocaleString(locale), total: run.total.toLocaleString(locale) })}{run.current ? ' · ' + run.current : ''}</Text>
        </>}
        {!run && view.queued && <Text className={s.muted}>{t('Wartet auf den Start …')}</Text>}
        {st.lastRun && (
          <div className={s.stats}>
            <Text>{t('Zuletzt exportiert')}</Text>
            <Text className={st.lastError && st.lastError !== 'Angehalten.' ? s.bad : s.ok}>{when(st.lastRun)}{st.lastError === 'Angehalten.' ? ' – ' + t('angehalten') : st.lastError ? ' – ' + t('mit Fehler') : ''}</Text>
            <Text>{t('Im Ziel')}</Text>
            <Text>{t('{n} Dateien, {size}', { n: st.files.toLocaleString(locale), size: fmtSize(st.bytes) })}</Text>
            <Text>{t('Letzter Lauf')}</Text>
            <Text>{t('{written} übertragen, {moved} verschoben, {trashed} nach .trash, {purged} endgültig entfernt', { written: st.written, moved: st.moved, trashed: st.trashed, purged: st.purged })}</Text>
            {st.lastOk && st.lastOk !== st.lastRun && <><Text>{t('Zuletzt ohne Fehler')}</Text><Text>{when(st.lastOk)}</Text></>}
            {view.next && <><Text>{t('Nächster Lauf')}</Text><Text>{when(view.next)}</Text></>}
          </div>
        )}
        {/* Fehler des letzten Laufs nur, solange kein neuer läuft */}
        {st.lastError && !run && !view.queued && <MessageBar intent={st.lastError === 'Angehalten.' ? 'info' : 'error'}><MessageBarBody>{t(st.lastError)}</MessageBarBody></MessageBar>}
        {dirty && view.ready && !auto && <Text size={200} className={s.muted}>{t('Erst speichern.')}</Text>}
      </Section>

      <Section title={t('Ziel')} hint={t('Speicherort des Datenexports auf einem externen Server.')}>
        <div className={s.form}>
          {/* Ziel ist ein externes Ziel (Einstellungen → Externe Ziele) und ein Ordner darin */}
          {view.mounts.length === 0 ? <>
            <Text>{t('Noch kein externes Ziel angelegt. Externe Ziele (WebDAV, OneDrive, pCloud) mit ihren Zugangsdaten werden unter Einstellungen → Dateien → Externe Ziele angelegt.')}</Text>
            <div className={s.row}><Button onClick={() => { location.hash = '#/settings/external-targets' }}>{t('Externe Ziele öffnen')}</Button></div>
          </> : <>
            <Field label={t('Externes Ziel')}>
              <div className={s.row}>
              <Select value={f.mount ?? ''} onChange={(_, d) => change({ mount: d.value || null, folder: '' })} style={{ maxWidth: 360 }}>
                <option value="">{t('Bitte wählen')}</option>
                {view.mounts.map(m => <option key={m.id} value={m.id}>{m.name}{m.account ? ' · ' + m.account : ''}</option>)}
              </Select>
              <Button onClick={() => { location.hash = '#/settings/external-targets' }}>{t('Neues Ziel anlegen')}</Button>
              </div>
            </Field>
            <Field label={t('Ordner')} hint={t('Leer: direkt im externen Ziel.')}>
              <div className={s.row}>
                <Input value={f.folder} onChange={(_, d) => change({ folder: d.value })} placeholder="Sicherung/dmOffice" style={{ flex: 1, maxWidth: 360 }} />
                <Button disabled={!f.mount} onClick={() => setBrowse(true)}>{t('Wählen …')}</Button>
              </div>
            </Field>
            {browse && f.mount && <BrowseDialog mount={f.mount} start={f.folder.replace(/^\/+|\/+$/g, '')} title={t('Ordner wählen')}
              onClose={() => setBrowse(false)} onPick={p => { change({ folder: p }); setBrowse(false) }} />}
          </>}
          {test && <Text className={test.ok ? s.ok : s.bad}>{test.text}</Text>}
        </div>
      </Section>

      <Section title={t('Bereiche')} hint={t('E-Mails sind nicht enthalten.')}>
        {[...AREAS, ...(view.pluginAreas ?? []).map(p => ({ key: p.key, label: () => p.label[lang] ?? p.label.de, hint: () => pluginExportHint(p.key), pick: false }))]
          .filter(a => view.available.includes(a.key)).map(a => {
          const on = f.areas.includes(a.key)
          const skipped = f.skip.filter(x => x.startsWith(a.key + ':')).length
          return (
            <div key={a.key} className={s.area}>
              <Checkbox checked={on} label={a.label()} onChange={(_, d) => change({ areas: d.checked ? [...f.areas, a.key] : f.areas.filter(x => x !== a.key) })} />
              <Text size={200} className={s.muted}>{a.hint()}</Text>
              {a.pick && on && <Button size="small" appearance="subtle" onClick={() => setPick(a.key)}>
                {skipped ? t('Ordner auswählen … ({n} abgewählt)', { n: skipped }) : t('Ordner auswählen …')}</Button>}
            </div>
          )
        })}
      </Section>

      <Section title={t('Zeitplan und Aufbewahrung')}>
        <div className={s.form}>
          {featureOn('exportcron')
            ? <Field label={t('Zeitplan')} hint={t('Bei mehreren Benutzern läuft der Datenexport nacheinander. Zur geplanten Zeit beginnt der Lauf, sobald der vorige fertig ist.')}>
              <SchedulePicker preview="/api/plainbackup/preview" at={f.time} onAt={x => change({ time: x })}
                value={{ kind: f.scheduleKind ?? 'every', count: f.everyCount ?? 1, unit: f.everyUnit ?? 'd', weekday: f.weekday ?? 1, cron: f.cron ?? '0 3 * * *' }}
                onChange={x => change({ scheduleKind: x.kind, everyCount: x.count, everyUnit: x.unit, weekday: x.weekday, cron: x.cron })} />
            </Field>
            : <Field label={t('Täglich um')} hint={t('Bei mehreren Benutzern läuft der Datenexport nacheinander. Zur gewählten Uhrzeit beginnt der Lauf, sobald der vorige fertig ist.')}>
              <Input type="time" value={f.time} onChange={(_, d) => change({ time: d.value })} style={{ maxWidth: 140 }} />
            </Field>}
          <Field label={t('Gelöschtes und ältere Versionen in .trash aufbewahren')}>
            <Select value={String(f.trashDays)} onChange={(_, d) => change({ trashDays: Number(d.value) })} style={{ maxWidth: 220 }}>
              {TRASH.map(d => <option key={d} value={d}>{d === 0 ? t('immer') : t('{n} Tage', { n: d })}</option>)}
            </Select>
          </Field>
          <Switch checked={f.versions} onChange={(_, d) => change({ versions: d.checked })} label={t('Ältere Versionen geänderter Dateien aufbewahren')} />
          <Hint>{t('Statt zu überschreiben, kommt die vorige Version nach .trash: höchstens eine je Tag, bei Kalendern, Kontakten und Listen eine je Woche. Das braucht mehr Platz im Ziel.')}</Hint>
        </div>
      </Section>

      <Section title={t('Einschalten')} last>
        <div className={s.form}>
          {needsConfirm && (
            <Checkbox checked={confirm} onChange={(_, d) => { setConfirm(!!d.checked); setDirty(true) }}
              label={t('Die gewählten Daten liegen im Ziel unverschlüsselt. Wer Zugang zum Ziel hat, kann sie lesen.')} />
          )}
          {auto && waiting && <Text size={200} className={s.muted}>{t('Das neue Ziel wird gespeichert, sobald es bestätigt ist.')}</Text>} {/* [feature:savemode] */}
          <Switch checked={f.enabled} disabled={needsConfirm && !confirm && !f.enabled} onChange={(_, d) => change({ enabled: d.checked })} label={t('Datenexport einschalten')} />
        </div>
      </Section>
      </SectionTabs>

      {pick && <SkipDialog area={pick} skip={f.skip} onDone={skip => { if (skip) change({ skip }); setPick(null) }} />}
    </div>
  )
}

/** Ordner bzw. Sammlungen eines Bereichs abwählen. Dateien und Notizen als Baum; ein abgewählter Ordner nimmt alles darunter mit. */
function SkipDialog({ area, skip, onDone }: { area: Area; skip: string[]; onDone: (skip: string[] | null) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [sel, setSel] = useState(() => new Set(skip))
  const [kids, setKids] = useState<Record<string, Choice[]>>({})
  const [open, setOpen] = useState<Set<string>>(new Set())
  const loadKids = useCallback((path: string) =>
    api.get<Choice[]>(`/api/plainbackup/choices?area=${area}&path=${encodeURIComponent(path)}`).then(l => setKids(k => ({ ...k, [path]: l }))).catch(fail), [area, fail])
  useEffect(() => { loadKids('') }, [loadKids])
  const key = (id: string) => `${area}:${id}`
  const toggle = (id: string) => setSel(x => { const n = new Set(x); if (n.has(key(id))) n.delete(key(id)); else n.add(key(id)); return n })
  const expand = (id: string) => {
    setOpen(o => { const n = new Set(o); if (n.has(id)) n.delete(id); else n.add(id); return n })
    if (!kids[id]) loadKids(id)
  }
  const tree = area === 'files' || area === 'notes'
  const render = (list: Choice[] | undefined, depth: number, off: boolean): JSX.Element[] => (list ?? []).flatMap(c => {
    const skipped = sel.has(key(c.id))
    return [
      <div key={c.id} className={s.node} style={{ paddingLeft: depth * 20 }}>
        {tree ? <Button size="small" appearance="transparent" icon={open.has(c.id) ? <ChevronDown16Regular /> : <ChevronRight16Regular />} onClick={() => expand(c.id)}
          aria-label={open.has(c.id) ? t('Zuklappen') : t('Aufklappen')} /> : null}
        <Checkbox checked={!off && !skipped} disabled={off} label={c.name} onChange={() => toggle(c.id)} />
      </div>,
      ...(tree && open.has(c.id) ? render(kids[c.id], depth + 1, off || skipped) : []),
    ]
  })
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onDone(null) }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('Was exportiert wird')}</DialogTitle>
          <DialogContent>
            <div className={s.tree}>
              {area === 'notes' && <Checkbox checked={!sel.has(key(''))} label={t('Notizen ohne Ordner')} onChange={() => toggle('')} />}
              {kids[''] === undefined ? <Spinner size="tiny" /> : kids[''].length === 0 && area !== 'notes'
                ? <Text className={s.muted}>{t('Keine Ordner.')}</Text> : render(kids[''], 0, false)}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={() => onDone(null)}>{t('Abbrechen')}</Button>
            {/* [feature:savemode] */}
            <Button appearance="primary" onClick={() => onDone([...sel])}>{featureOn('savemode') ? t('Speichern') : t('Übernehmen')}</Button>
            {/* [feature:savemode:else] */}
            {/* <Button appearance="primary" onClick={() => onDone([...sel])}>{t('Übernehmen')}</Button> */}
            {/* [/feature:savemode] */}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
