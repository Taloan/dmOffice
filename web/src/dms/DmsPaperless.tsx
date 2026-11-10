// DMS: Übernahme aus paperless-ngx (Server: Dms/DmsPaperless.cs). Schritte: Export einlesen, Listen übernehmen, Probelauf,
// Übernahme; danach in der Inbox die Abweichungen prüfen.
import {
  Button, Checkbox, Dropdown, Field, Option, ProgressBar, Slider, Spinner, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { Folder20Filled } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { BrowseDialog } from '../files/ImportProfiles'
import { lang, t } from '../i18n'
import { askConfirm } from '../shell/context'
import { Hint, PageTitle } from '../settings/parts'
import { Kind, kindLabel, Match, matchLabel, Profile } from './model'

interface PlEntry { key: string; kind: Kind; pk: number; name: string; match: Match; matchText: string | null; inboxTag: boolean; note: string | null; existing: string | null; mapped: string | null }
interface ReadResult { documents: number; withFile: number; withArchive: number; missing: number; entries: PlEntry[] }
interface ListStat { kind: Kind; same: number; different: number; empty: number; total: number }
interface EntryStat { kind: Kind; id: string; name: string; same: number; different: number; empty: number; total: number }
interface Difference { pk: number; title: string; kind: Kind; paperless: string | null; dms: string | null }
interface Trial { learned: number; tested: number; lists: ListStat[]; entries: EntryStat[]; differences: Difference[] }
interface Status { progress: { total: number; done: number; duplicates: number; errors: number; running: boolean; error: string | null; last: string | null } | null; differing: number; same: number; compared: number }

const useStyles = makeStyles({
  box: { display: 'flex', flexDirection: 'column', gap: '8px', padding: '12px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, maxWidth: '860px' },
  head: { fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightSemibold, textTransform: 'uppercase', letterSpacing: '0.05em', color: tokens.colorNeutralForeground2 },
  line: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  table: { display: 'grid', gridTemplateColumns: 'minmax(140px, 2fr) repeat(4, minmax(60px, 1fr))', gap: '2px 12px', alignItems: 'center' },
  th: { fontSize: '12px', color: tokens.colorNeutralForeground3 },
  entries: { maxHeight: '320px', overflowY: 'auto', display: 'flex', flexDirection: 'column' },
  diffs: { maxHeight: '320px', overflowY: 'auto', display: 'grid', gridTemplateColumns: 'minmax(140px, 2fr) 110px 1fr 1fr', gap: '2px 12px', fontSize: '13px' },
})

const pct = (n: number, total: number) => total ? `${Math.round(n * 100 / total)} %` : '–'

/** Einstellungen → DMS → Übernahme aus paperless. */
export function DmsPaperlessSettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [mounts, setMounts] = useState<{ id: string; name: string }[]>([])
  const [profiles, setProfiles] = useState<Profile[]>([])
  const [mount, setMount] = useState('')
  const [folder, setFolder] = useState('')
  const [pick, setPick] = useState(false)
  const [read, setRead] = useState<ReadResult | null>(null)
  const [skip, setSkip] = useState<Set<string>>(new Set())
  const [listsDone, setListsDone] = useState(false)
  const [share, setShare] = useState(70)
  const [trial, setTrial] = useState<Trial | null>(null)
  const [profile, setProfile] = useState<string | null>(null)
  const [status, setStatus] = useState<Status | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const body = () => ({ mount, folder, skip: [...skip], share: share / 100, profile })
  const loadStatus = () => api.get<Status>('/api/dms/paperless/status').then(setStatus).catch(() => {})
  useEffect(() => {
    api.get<{ id: string; name: string }[]>('/api/dms/mounts').then(setMounts).catch(() => {})
    api.get<Profile[]>('/api/dms/profiles?' + q({ lang })).then(p => { setProfiles(p); setProfile(p.find(x => x.default)?.id ?? p[0]?.id ?? null) }).catch(fail)
    loadStatus()
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!status?.progress?.running) return
    const h = setTimeout(loadStatus, 2000)
    return () => clearTimeout(h)
  }, [status]) // eslint-disable-line react-hooks/exhaustive-deps

  const step = async (name: string, f: () => Promise<void>) => { setBusy(name); try { await f() } catch (e) { fail(e) } finally { setBusy(null) } }
  const doRead = () => step('read', async () => {
    const r = await api.post<ReadResult>('/api/dms/paperless/read', body())
    setRead(r); setTrial(null); setListsDone(r.entries.every(e => e.mapped || e.inboxTag))
    setSkip(new Set(r.entries.filter(e => e.inboxTag).map(e => e.key)))
  })
  const doLists = () => step('lists', async () => {
    const r = await api.post<{ mapped: number }>('/api/dms/paperless/lists', body())
    notify(t('{n} Einträge zugeordnet', { n: r.mapped })); setListsDone(true)
  })
  const doTrial = () => step('trial', async () => setTrial(await api.post<Trial>('/api/dms/paperless/trial', body())))
  const doImport = async () => {
    if (!read) return
    if (!await askConfirm(t('Übernahme starten'), t('{n} Dokumente werden übernommen und nach dem gewählten Ablageprofil in der Dateiablage abgelegt. Sie stehen danach in der Inbox. Bereits vorhandene werden übersprungen; der Export bleibt unverändert.', { n: read.withFile }), t('Übernehmen'))) return
    step('import', async () => { await api.post('/api/dms/paperless/import', body()); await loadStatus() })
  }
  const confirmSame = () => step('same', async () => {
    const r = await api.post<{ count: number }>('/api/dms/paperless/confirm-same'); notify(r.count === 1 ? t('1 Dokument bestätigt') : t('{n} Dokumente bestätigt', { n: r.count })); await loadStatus()
  })
  const discard = async () => {
    if (!await askConfirm(t('Angaben aus paperless verwerfen'), t('Die zum Vergleich gemerkten Angaben aus paperless werden gelöscht. Die Dokumente und ihre Einordnung im DMS bleiben.'), t('Verwerfen'))) return
    step('discard', async () => { await api.post('/api/dms/paperless/discard'); await loadStatus() })
  }
  const where = (mount ? mounts.find(m => m.id === mount)?.name ?? mount : t('Eigene Ablage')) + (folder ? ' › ' + folder.split('/').join(' › ') : '')
  const p = status?.progress

  return (
    <>
      <PageTitle>{t('Übernahme aus paperless')}</PageTitle>
      <Hint>{t('Übernimmt Dokumente aus einem Export von paperless-ngx (document_exporter: Originale, Archivfassungen, manifest.json). Korrespondenten, Dokumenttypen und Tags kommen mit Namen, Farbe und Zuweisung. Wer, was und welches Thema bestimmt das DMS selbst; die Angaben aus paperless bleiben zum Vergleich, damit sich falsche Zuordnungen in paperless und Schwächen der Erkennung zeigen. Gelernt wird erst aus dem, was in der Inbox bestätigt wird.')}</Hint>

      <div className={s.box}>
        <Text className={s.head}>{t('1. Export einlesen')}</Text>
        <div className={s.line}>
          <Dropdown value={mount ? mounts.find(m => m.id === mount)?.name ?? mount : t('Eigene Ablage')} selectedOptions={[mount]} onOptionSelect={(_, d) => { setMount(d.optionValue ?? ''); setFolder(''); setRead(null) }}>
            <Option value="">{t('Eigene Ablage')}</Option>
            {mounts.map(m => <Option key={m.id} value={m.id}>{m.name}</Option>)}
          </Dropdown>
          <Button icon={<Folder20Filled color="#E8A33D" />} onClick={() => setPick(true)}>{t('Ordner wählen')}</Button>
          <Text>{where}</Text>
        </div>
        <div><Button appearance="primary" disabled={!folder || !!busy} onClick={doRead}>{busy === 'read' ? <Spinner size="tiny" /> : t('Einlesen')}</Button></div>
        {read && <Text>{t('{n} Dokumente, davon {f} mit Datei und {a} mit Archivfassung; {m} Dateien fehlen.', { n: read.documents, f: read.withFile, a: read.withArchive, m: read.missing })}</Text>}
      </div>

      {read && <div className={s.box}>
        <Text className={s.head}>{t('2. Listen übernehmen')}</Text>
        <Text size={200} className={s.muted}>{t('Gleichnamige Einträge im DMS werden weiterverwendet. Abgewählte Einträge kommen nicht mit.')}</Text>
        <div className={s.entries}>
          {read.entries.map(e => (
            <Checkbox key={e.key} disabled={e.inboxTag || !!e.mapped} checked={!skip.has(e.key) && !e.inboxTag}
              onChange={(_, d) => setSkip(x => { const n = new Set(x); if (d.checked) n.delete(e.key); else n.add(e.key); return n })}
              label={<span>{kindLabel(e.kind)}: <b>{e.name}</b> · {matchLabel(e.match)}{e.matchText ? ` (${e.matchText})` : ''}
                {e.mapped ? ' · ' + t('schon zugeordnet') : e.existing ? ' · ' + t('gibt es schon') : ''}{e.note ? <span className={s.muted}> · {t(e.note)}</span> : null}</span>} />
          ))}
        </div>
        <div><Button appearance="primary" disabled={!!busy} onClick={doLists}>{busy === 'lists' ? <Spinner size="tiny" /> : t('Listen übernehmen')}</Button>
          {listsDone && <Text className={s.muted} style={{ marginLeft: 8 }}>{t('Übernommen.')}</Text>}</div>
      </div>}

      {read && listsDone && <div className={s.box}>
        <Text className={s.head}>{t('3. Probelauf')}</Text>
        <Text size={200} className={s.muted}>{t('Das DMS lernt aus einem zufälligen Teil der Dokumente mit den Angaben aus paperless und ordnet den Rest ein. Es wird nichts abgelegt und nichts gelernt; der Probelauf lässt sich wiederholen, etwa nach geänderten Zuweisungen.')}</Text>
        <Field label={t('Gelernt wird aus {n} % der Dokumente', { n: share })}>
          <Slider min={30} max={90} step={5} value={share} onChange={(_, d) => setShare(d.value)} />
        </Field>
        <div><Button appearance="primary" disabled={!!busy} onClick={doTrial}>{busy === 'trial' ? <Spinner size="tiny" /> : t('Probelauf starten')}</Button></div>
        {trial && <>
          <Text>{t('Gelernt aus {l} Dokumenten, eingeordnet: {t}.', { l: trial.learned, t: trial.tested })}</Text>
          <div className={s.table}>
            <span className={s.th} /><span className={s.th}>{t('gleich')}</span><span className={s.th}>{t('anders')}</span><span className={s.th}>{t('leer')}</span><span className={s.th}>{t('Fälle')}</span>
            {trial.lists.map(l => [
              <b key={l.kind + 'n'}>{kindLabel(l.kind, true)}</b>, <span key={l.kind + 's'}>{pct(l.same, l.total)}</span>, <span key={l.kind + 'd'}>{pct(l.different, l.total)}</span>,
              <span key={l.kind + 'e'}>{pct(l.empty, l.total)}</span>, <span key={l.kind + 't'}>{l.total}</span>,
            ])}
          </div>
          <details>
            <summary>{t('Je Eintrag')}</summary>
            <div className={s.table}>
              {trial.entries.map(e => [
                <span key={e.id + 'n'}>{kindLabel(e.kind)}: {e.name}</span>, <span key={e.id + 's'}>{pct(e.same, e.total)}</span>, <span key={e.id + 'd'}>{pct(e.different, e.total)}</span>,
                <span key={e.id + 'e'}>{pct(e.empty, e.total)}</span>, <span key={e.id + 't'}>{e.total}</span>,
              ])}
            </div>
          </details>
          {trial.differences.length > 0 && <details>
            <summary>{t('Abweichende Dokumente ({n})', { n: trial.differences.length })}</summary>
            <div className={s.diffs}>
              <span className={s.th}>{t('Titel')}</span><span className={s.th}>{t('Liste')}</span><span className={s.th}>paperless</span><span className={s.th}>DMS</span>
              {trial.differences.map((d, i) => [
                <span key={i + 't'}>{d.title}</span>, <span key={i + 'k'}>{kindLabel(d.kind)}</span>, <span key={i + 'p'}>{d.paperless ?? '–'}</span>, <span key={i + 'm'}>{d.dms ?? '–'}</span>,
              ])}
            </div>
          </details>}
        </>}
      </div>}

      {read && listsDone && <div className={s.box}>
        <Text className={s.head}>{t('4. Übernahme')}</Text>
        <Field label={t('Ablageprofil')}>
          <Dropdown value={profiles.find(x => x.id === profile)?.name ?? ''} selectedOptions={[profile ?? '']} onOptionSelect={(_, d) => setProfile(d.optionValue ?? null)}>
            {profiles.map(x => <Option key={x.id} value={x.id}>{x.name}</Option>)}
          </Dropdown>
        </Field>
        <div><Button appearance="primary" disabled={!!busy || !!p?.running} onClick={doImport}>{t('Übernahme starten')}</Button></div>
      </div>}

      {(p || (status?.compared ?? 0) > 0) && <div className={s.box}>
        <Text className={s.head}>{t('Stand')}</Text>
        {p && <>
          {p.running && <ProgressBar value={p.total ? (p.done + p.duplicates + p.errors) / p.total : undefined} />}
          <Text>{t('{d} von {n} übernommen, {s} schon vorhanden, {e} Fehler', { d: p.done, n: p.total, s: p.duplicates, e: p.errors })}{p.running && p.last ? ' · ' + p.last : ''}</Text>
          {p.error && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{t(p.error)}</Text>}
        </>}
        {(status?.compared ?? 0) > 0 && <>
          <Text>{t('{n} Dokumente mit Angaben aus paperless; in der Inbox {s} gleich eingeordnet, {d} abweichend.', { n: status!.compared, s: status!.same, d: status!.differing })}</Text>
          <Text size={200} className={s.muted}>{t('Die Abweichungen stehen im DMS unter „Abweichung von paperless“, mit beiden Angaben nebeneinander.')}</Text>
          <div className={s.line}>
            <Button disabled={!status!.same || !!busy} onClick={confirmSame}>{t('Gleich eingeordnete bestätigen ({n})', { n: status!.same })}</Button>
            <Button disabled={!!busy} onClick={discard}>{t('Angaben aus paperless verwerfen')}</Button>
          </div>
        </>}
      </div>}

      {pick && <BrowseDialog mount={mount} start={folder} title={t('Ordner des Exports wählen')} needPath={!mount}
        onPick={path => { setFolder(path); setRead(null); setPick(false) }} onClose={() => setPick(false)} />}
    </>
  )
}
