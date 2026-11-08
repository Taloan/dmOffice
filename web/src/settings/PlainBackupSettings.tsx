// Einstellungen → Datensicherung (docs/datensicherung-konzept.md): Ziel per WebDAV, Bereiche und Ordner, Uhrzeit, Frist für
// .trash, ältere Versionen, ausdrückliche Bestätigung, Zustand und „Jetzt sichern“. Jede Person für sich.
import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody,
  ProgressBar, Select, Spinner, Switch, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { ChevronDown16Regular, ChevronRight16Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { Hint, PageTitle, Section } from './parts'
import { BrowseDialog } from '../files/ImportProfiles'
import { askPassword } from '../shell/context'

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

type Area = 'files' | 'gallery' | 'calendar' | 'tasks' | 'contacts' | 'notes' | 'dms' | 'journal' | 'coach' | 'finance'

interface Settings {
  enabled: boolean; mount: string | null; folder: string; time: string; trashDays: number; versions: boolean
  areas: Area[]; skip: string[]
}
interface State {
  lastRun: string | null; lastOk: string | null; lastError: string | null; seconds: number; files: number; bytes: number
  written: number; moved: number; trashed: number; purged: number; failed: number; fails: number
}
interface Progress { started: string; done: number; total: number; bytes: number; current: string | null }
interface View {
  allowed: boolean; available: Area[]; settings: Settings; mounts: { id: string; name: string; kind: string; account?: string | null }[]; oneDrive: boolean; webDav: boolean; confirmed: boolean; ready: boolean; state: State
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
  { key: 'journal', label: () => t('Tagebuch'), hint: () => t('eine .md-Datei je Eintrag'), pick: true },
  { key: 'coach', label: () => 'Coach', hint: () => t('Mahlzeiten, Gewicht und Aktivität als Tabellen') },
  { key: 'finance', label: () => t('Finanzen'), hint: () => t('Ausgestellte Belege als PDF, dazu Einträge, Kunden und Protokoll als Tabellen') },
]
const TRASH = [7, 30, 90, 0]
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

  if (!view || !f) return <><PageTitle>{t('Datenexport')}</PageTitle><Spinner size="small" /></>
  const change = (patch: Partial<Settings>) => { setF({ ...f, ...patch }); setDirty(true) }
  // Neues Ziel: erneut bestätigen
  const sameTarget = f.mount === view.settings.mount && f.folder.replace(/^\/+|\/+$/g, '') === view.settings.folder
  const needsConfirm = !(view.confirmed && sameTarget)

  const save = async () => {
    // Neues Ziel bestätigen (dorthin gehen ab jetzt alle Daten lesbar): nur mit dem Anmelde-Passwort
    let accountPassword: string | undefined, code: string | undefined
    if (confirm && needsConfirm) {
      const c = await askPassword(t('Ziel des Datenexports bestätigen'), !!me?.twoFactor)
      if (!c) return
      accountPassword = c.password
      code = c.code
    }
    setBusy(true)
    try {
      const v = await api.put<View>('/api/plainbackup', {
        enabled: f.enabled, target: 'external', mount: f.mount, folder: f.folder, time: f.time, trashDays: f.trashDays, versions: f.versions,
        areas: f.areas, skip: f.skip, confirm, accountPassword, code,
      })
      apply(v)
      notify(t('Gespeichert'))
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
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
      <PageTitle>{t('Datenexport')}</PageTitle>
      <MessageBar intent="info"><MessageBarBody>{t('Der Datenexport ist auf diesem Server nicht freigeschaltet. Freischalten lässt er sich über die Verwaltung.')}</MessageBarBody></MessageBar>
    </div>
  )

  const st = view.state
  const run = view.running
  return (
    <div className={s.page}>
      <PageTitle>{t('Datenexport')}</PageTitle>
      <Text>{t('dmOffice legt deine Daten regelmäßig entschlüsselt und in offenen Formaten an einem separaten Speicherort ab, beispielsweise per WebDAV in pCloud. Die Dateien lassen sich ohne dmOffice öffnen. Zurückspielen lässt sich ein Datenexport nicht.')}</Text>
      <Text>{t('dmOffice schreibt ausschließlich an diesen Speicherort und liest keine Daten von dort zurück. Gelöschte Dateien bleiben zunächst im versteckten Ordner .trash erhalten und werden erst nach Ablauf der eingestellten Aufbewahrungsfrist endgültig entfernt.')}</Text>

      <Section title={t('Zustand')}>
        {!view.ready && <Text className={s.muted}>{t('Noch nicht eingeschaltet.')}</Text>}
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
        <div className={s.row}>
          {run || view.queued
            ? <Button onClick={stop}>{t('Anhalten')}</Button>
            : <Button appearance="primary" disabled={!view.ready || dirty} onClick={runNow}>{t('Jetzt exportieren')}</Button>}
          {dirty && view.ready && <Text size={200} className={s.muted}>{t('Erst speichern.')}</Text>}
        </div>
      </Section>

      <Section title={t('Ziel')} hint={t('Speicherort des Datenexports auf einem externen Server. Ein Download über den Browser ist nicht vorgesehen; für eine Kopie auf dem eigenen Rechner lässt sich das Ziel dorthin synchronisieren.')}>
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
            <Field label={t('Ordner')} hint={t('Leer: direkt im externen Ziel. Ein fehlender Ordner wird angelegt.')}>
              <div className={s.row}>
                <Input value={f.folder} onChange={(_, d) => change({ folder: d.value })} placeholder="Sicherung/dmOffice" style={{ flex: 1, maxWidth: 360 }} />
                <Button disabled={!f.mount} onClick={() => setBrowse(true)}>{t('Wählen …')}</Button>
              </div>
            </Field>
            {browse && f.mount && <BrowseDialog mount={f.mount} start={f.folder.replace(/^\/+|\/+$/g, '')} title={t('Ordner wählen')}
              onClose={() => setBrowse(false)} onPick={p => { change({ folder: p }); setBrowse(false) }} />}
          </>}
          <div className={s.row}>
            <Button disabled={busy || !f.mount} onClick={runTest}>{t('Verbindung testen')}</Button>
            {test && <Text className={test.ok ? s.ok : s.bad}>{test.text}</Text>}
          </div>
        </div>
      </Section>

      <Section title={t('Bereiche')} hint={t('E-Mails sind nicht enthalten, sie liegen beim Mailanbieter.')}>
        {AREAS.filter(a => view.available.includes(a.key)).map(a => {
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
          <Field label={t('Täglich um')} hint={t('Bei mehreren Personen läuft der Datenexport nacheinander; zur gewählten Uhrzeit beginnt der Lauf, sobald der vorige fertig ist.')}>
            <Input type="time" value={f.time} onChange={(_, d) => change({ time: d.value })} style={{ maxWidth: 140 }} />
          </Field>
          <Field label={t('Gelöschtes und ältere Versionen in .trash aufbewahren')}>
            <Select value={String(f.trashDays)} onChange={(_, d) => change({ trashDays: Number(d.value) })} style={{ maxWidth: 220 }}>
              {TRASH.map(d => <option key={d} value={d}>{d === 0 ? t('immer') : t('{n} Tage', { n: d })}</option>)}
            </Select>
          </Field>
          <Switch checked={f.versions} onChange={(_, d) => change({ versions: d.checked })} label={t('Ältere Versionen geänderter Dateien aufbewahren')} />
          <Hint>{t('Statt zu überschreiben, kommt die vorige Version nach .trash: höchstens eine je Tag, bei Kalendern, Kontakten und Listen eine je Woche. Schützt etwa vor versehentlichem Überschreiben, braucht aber mehr Platz im Ziel.')}</Hint>
        </div>
      </Section>

      <Section title={t('Einschalten')} last>
        <div className={s.form}>
          {needsConfirm && (
            <Checkbox checked={confirm} onChange={(_, d) => { setConfirm(!!d.checked); setDirty(true) }}
              label={t('Die gewählten Daten liegen im Ziel unverschlüsselt. Wer Zugang zum Ziel hat, kann sie lesen.')} />
          )}
          <Switch checked={f.enabled} disabled={needsConfirm && !confirm && !f.enabled} onChange={(_, d) => change({ enabled: d.checked })} label={t('Datenexport einschalten')} />
          <div className={s.row}>
            <Button appearance="primary" disabled={busy || !dirty} onClick={save}>{t('Speichern')}</Button>
            {dirty && <Button appearance="subtle" onClick={() => apply(view)}>{t('Verwerfen')}</Button>}
          </div>
        </div>
      </Section>

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
            <Button appearance="primary" onClick={() => onDone([...sel])}>{t('Übernehmen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
