import { Button, Field, Select, Text, makeStyles, tokens } from '@fluentui/react-components'
import { Fragment, useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { Hint, PageTitle, Section } from './parts'
import { askConfirm } from '../shell/context'

const useStyles = makeStyles({
  page: { display: 'flex', flexDirection: 'column', gap: '20px' },
  row: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(270px, 1fr))', gap: '12px', maxWidth: '760px' },
  stats: { display: 'grid', gridTemplateColumns: 'max-content 1fr', columnGap: '16px', rowGap: '4px', maxWidth: '760px' },
  actions: { display: 'flex', gap: '8px' },
  muted: { color: tokens.colorNeutralForeground3 },
  bars: { display: 'grid', gridTemplateColumns: 'max-content 1fr max-content', gap: '6px 12px', alignItems: 'center', maxWidth: '560px' },
  bar: { height: '8px', borderRadius: '4px', backgroundColor: tokens.colorNeutralBackground4, overflow: 'hidden' },
  fill: { height: '100%' },
  buttons: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
})

export interface FileSettings {
  versionsMode: 'auto' | 'fixed' | 'off'
  versionsMax: number
  versionsDays: number
  versionsMb: number
  trashDays: number
  trashMb: number
  linkMaxDownloads: number
  linkDays: number
}

/** Hoechstwerte der Verwaltung, 0 = keine Vorgabe. */
export interface FileLimits {
  versionsDays: number; versionsMb: number; trashDays: number; trashMb: number; linkMaxDownloads: number; linkDays: number
}

/** Kopie am zweiten Ort (S3, WebDAV, Ordner) - null, solange die Verwaltung sie nicht eingeschaltet hat. */
interface CopyInfo {
  target: 's3' | 'webdav' | 'folder'; lastRun: string | null; ok: boolean
  cache: { local: number; target: number; max: number } | null
  backup: { lastRun: string | null; ok: boolean; keepCount: number } | null
}
interface ExternalSpace { id: string; name: string; kind: 'folder' | 'webdav' | 'onedrive' | 'pcloud'; readOnly: boolean; used: number | null; free: number | null; error: string | null }

interface Data { settings: FileSettings; limits: FileLimits; usage: number; trash: number; versions: number; quota: number; used?: number; copy?: CopyInfo | null }

/** Vorgaben fuer neue Freigabelinks - der Teilen-Dialog liest sie beim Oeffnen. */
export const loadFileSettings = () => api.get<Data>('/api/files/settings').then(d => d.settings)

/** Stufen fuer Speichergrenzen in MB (0 = keine Grenze). */
export const sizeSteps = [10, 100, 500, 1024, 0]
export const mbText = (n: number) => n === 0 ? t('keine Grenze') : n >= 1024 && n % 1024 === 0 ? `${n / 1024} GB` : `${n} MB`
export const daysText = (d: number, none: string) =>
  d === 0 ? none : d === 365 ? t('1 Jahr') : d === 730 ? t('2 Jahre') : d === 1 ? t('1 Tag') : t('{n} Tage', { n: d })

/** Auswahl; mit Hoechstwert nur Stufen darunter, "unbegrenzt" (0) faellt weg und der Hoechstwert selbst steht dabei. */
function Choice({ label, value, steps, text, max, onChange }: {
  label: string; value: number; steps: number[]; text: (v: number) => string; max?: number; onChange: (v: number) => void
}) {
  let list = max && max > 0 ? [...steps.filter(v => v > 0 && v < max), max] : steps
  if (!list.includes(value)) list = [...list, value].sort((a, b) => (a || Infinity) - (b || Infinity))
  return (
    <Field label={label}>
      <Select value={String(value)} onChange={(_, d) => onChange(Number(d.value))}>
        {list.map(v => <option key={v} value={String(v)}>{text(v)}</option>)}
      </Select>
    </Field>
  )
}

/** Laden und Speichern, gemeinsam fuer die vier Seiten. */
function useFileSettings() {
  const { fail, notify } = useApp()
  const [data, setData] = useState<Data | null>(null)
  const load = () => api.get<Data>('/api/files/settings').then(setData).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const save = async (p: Partial<FileSettings>) => {
    if (!data) return
    if (p.versionsMode === 'off' && data.versions > 0
      && !await askConfirm('', t('Keine früheren Versionen mehr? Die vorhandenen ({size}) werden gelöscht.', { size: fmtSize(data.versions) }))) return
    const next = { ...data.settings, ...p }
    setData({ ...data, settings: next })
    try {
      const r = await api.put<{ settings: FileSettings; trash: number; versions: number }>('/api/files/settings', next)
      setData(d => d && { ...d, ...r })
    } catch (e) { fail(e); load() }
  }
  return { data, load, save, fail, notify }
}

export function FilesStorageSettings() {
  const s = useStyles()
  const { data, load, fail, notify } = useFileSettings()
  // Externe Ordner fragen fremde Server - eigener Abruf, damit die Seite nicht auf sie wartet
  const [ext, setExt] = useState<ExternalSpace[] | null>(null)
  useEffect(() => { api.get<ExternalSpace[]>('/api/files/external/space').then(setExt).catch(() => setExt([])) }, [])
  if (!data) return null
  const emptyTrash = async () => {
    if (!data.trash || !await askConfirm('', t('Papierkorb leeren? Alles darin wird endgültig gelöscht.'))) return
    try { await api.post('/api/files/trash/empty'); notify(t('Papierkorb geleert'), fmtSize(data.trash)); load() } catch (e) { fail(e) }
  }
  const purge = async () => {
    if (!data.versions || !await askConfirm('', t('Alle früheren Versionen löschen? Die aktuellen Dateien bleiben.'))) return
    try { await api.post('/api/files/versions/purge'); notify(t('Frühere Versionen gelöscht'), fmtSize(data.versions)); load() } catch (e) { fail(e) }
  }
  // Mit Kontingent zaehlt alles, was der Benutzer belegt (auch Mails, Kalender, Kontakte, Notizen)
  const total = data.quota > 0 && data.used != null ? data.used : data.usage + data.trash + data.versions
  const scale = Math.max(data.quota || 0, total, 1)
  const bar = (n: number, color: string) => (
    <div className={s.bar}><div className={s.fill} style={{ width: `${Math.max(n > 0 ? 1 : 0, (n / scale) * 100)}%`, backgroundColor: color }} /></div>
  )
  return (
    <div className={s.page}>
      <PageTitle>{t('Speicherplatz')}</PageTitle>
      <Section title={t('Belegt')} last={!data.copy && !ext?.length}>
        <div className={s.bars}>
          <Text>{t('Dateien')}</Text>{bar(data.usage, tokens.colorBrandBackground)}<Text>{fmtSize(data.usage)}</Text>
          <Text>{t('Frühere Versionen')}</Text>{bar(data.versions, tokens.colorPaletteMarigoldBorderActive)}<Text>{fmtSize(data.versions)}</Text>
          <Text>{t('Papierkorb')}</Text>{bar(data.trash, tokens.colorPaletteRedBorderActive)}<Text>{fmtSize(data.trash)}</Text>
        </div>
        <Hint>{data.quota > 0
          ? t('{used} von {quota} belegt (alles zusammen). Wird es eng, weichen zuerst die ältesten Versionen, dann der Papierkorb.', { used: fmtSize(total), quota: fmtSize(data.quota) })
          : t('{used} belegt, keine Grenze gesetzt.', { used: fmtSize(total) })}</Hint>
        <div className={s.buttons}>
          <Button disabled={!data.trash} onClick={emptyTrash}>{t('Papierkorb leeren')}</Button>
          <Button disabled={!data.versions} onClick={purge}>{t('Alle früheren Versionen löschen')}</Button>
        </div>
      </Section>
      {data.copy && <CopySection copy={data.copy} last={!ext?.length} />}
      {!!ext?.length && <ExternalSection list={ext} />}
    </div>
  )
}

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString() : t('noch nicht'))

function CopySection({ copy, last }: { copy: CopyInfo; last: boolean }) {
  const s = useStyles()
  const where = copy.target === 'webdav' ? t('einem WebDAV-Server') : copy.target === 'folder' ? t('einer zweiten Platte am Server') : t('einem S3-Speicher')
  const c = copy.cache
  const scale = c ? Math.max(c.max, c.local, 1) : 1
  return (
    <Section title={t('Kopie und Sicherung')} last={last}>
      <Text>{t('Deine Dateien liegen zusätzlich verschlüsselt auf {where}. Zuletzt abgeglichen: {when}.', { where, when: when(copy.lastRun) })}</Text>
      {!copy.ok && <Hint>{t('Beim letzten Abgleich gab es ein Problem – Näheres sieht die Verwaltung.')}</Hint>}
      {c && (
        <>
          <div className={s.bars}>
            <Text>{t('Hier auf dem Server')}</Text>
            <div className={s.bar}><div className={s.fill} style={{ width: `${Math.max(c.local > 0 ? 1 : 0, (c.local / scale) * 100)}%`, backgroundColor: tokens.colorBrandBackground }} /></div>
            <Text>{fmtSize(c.local)}</Text>
          </div>
          <Hint>{t('Der lokale Speicherpool hält neue und zuletzt verwendete Dateien bereit. Die übrigen Dateien liegen im Storage Extender und werden beim Öffnen automatisch nachgeladen. Das erste Öffnen dauert dann etwas länger.')}</Hint>
        </>
      )}
      {copy.backup
        ? <Text>{t('Das Core-Backup bewahrt {n} Stände auf. Letzte Sicherung: {when}.', { n: copy.backup.keepCount, when: when(copy.backup.lastRun) })}</Text>
        : <Hint>{t('Die Kopie allein ist keine Sicherung: Was du hier löschst, verschwindet nach einigen Tagen auch dort.')}</Hint>}
      {copy.backup && !copy.backup.ok && <Hint>{t('Die letzte Sicherung hat nicht geklappt – Näheres sieht die Verwaltung.')}</Hint>}
    </Section>
  )
}

function ExternalSection({ list }: { list: ExternalSpace[] }) {
  const s = useStyles()
  return (
    <Section title={t('Externe Ordner')} last>
      <div className={s.bars}>
        {list.map(m => {
          const total = m.free != null && m.used != null ? m.used + m.free : 0
          const info = m.error ? t('nicht erreichbar') : m.free == null ? t('Platz unbekannt') : t('{free} frei', { free: fmtSize(m.free) })
          return (
            <Fragment key={m.id}>
              <Text title={m.kind === 'webdav' ? t('WebDAV-Ordner') : m.kind === 'onedrive' ? t('OneDrive-Ordner') : m.kind === 'pcloud' ? t('pCloud-Ordner') : t('Ordner auf dem Server')}>{m.name}{m.readOnly ? ` (${t('nur lesen')})` : ''}</Text>
              <div className={s.bar} title={m.error ?? undefined}>{total > 0 && <div className={s.fill} style={{ width: `${Math.max(1, (m.used! / total) * 100)}%`, backgroundColor: tokens.colorPaletteTealBorderActive }} />}</div>
              <Text>{info}</Text>
            </Fragment>
          )
        })}
      </div>
      <Hint>{t('Externe Ordner zählen nicht zu deinem Speicherplatz hier – die Dateien liegen dort, wo der Ordner hinzeigt.')}</Hint>
    </Section>
  )
}

export function FilesVersionSettings() {
  const s = useStyles()
  const { data, save } = useFileSettings()
  if (!data) return null
  const st = data.settings, l = data.limits
  return (
    <div className={s.page}>
      <PageTitle>{t('Frühere Versionen')}</PageTitle>
      <Text>{t('Beim Überschreiben einer Datei bleibt der alte Stand als Version erhalten und lässt sich wiederherstellen. Das kostet Speicherplatz.')}</Text>
      <div className={s.row}>
        <Field label={t('Art')}>
          <Select value={st.versionsMode} onChange={(_, d) => save({ versionsMode: d.value as FileSettings['versionsMode'] })}>
            <option value="auto">{t('Automatisch ausdünnen (empfohlen)')}</option>
            <option value="fixed">{t('Feste Anzahl je Datei')}</option>
            <option value="off">{t('Keine Versionen')}</option>
          </Select>
        </Field>
        {st.versionsMode === 'fixed' && (
          <Choice label={t('Höchstens je Datei')} value={st.versionsMax} steps={[3, 5, 10, 20, 50, 100]}
            text={n => t('{n} Versionen', { n })} onChange={v => save({ versionsMax: v })} />
        )}
        {st.versionsMode !== 'off' && (
          <Choice label={t('Höchstens aufbewahren')} value={st.versionsDays} steps={[7, 30, 90, 180, 365, 730, 0]} max={l.versionsDays}
            text={d => daysText(d, t('unbegrenzt'))} onChange={v => save({ versionsDays: v })} />
        )}
        {st.versionsMode !== 'off' && (
          <Choice label={t('Speichergrenze für alle Versionen')} value={st.versionsMb} steps={sizeSteps} max={l.versionsMb}
            text={mbText} onChange={v => save({ versionsMb: v })} />
        )}
      </div>
      {st.versionsMode === 'auto' && (
        <Hint>{t('Wie Nextcloud und Time Machine: aus den letzten 24 Stunden bleibt eine Version je Stunde, aus den letzten 30 Tagen eine je Tag, danach eine je Woche.')}</Hint>
      )}
      {st.versionsMode !== 'off' && <Hint>{t('Ist die Speichergrenze erreicht, fallen die ältesten Versionen zuerst weg. Änderungen gelten sofort.')}</Hint>}
    </div>
  )
}

interface TrashState { usage: number; items: { isDir: boolean; size: number; deleted: string }[] }

export function FilesTrashSettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const { data, save } = useFileSettings()
  const [trash, setTrash] = useState<TrashState | null>(null)
  const loadTrash = () => api.get<TrashState>('/api/files/trash').then(setTrash).catch(fail)
  useEffect(() => { loadTrash() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!data) return null
  const st = data.settings, l = data.limits
  const items = trash?.items ?? []
  const dirs = items.filter(i => i.isDir).length
  const oldest = items.length ? items.reduce((a, b) => (a.deleted < b.deleted ? a : b)).deleted : null
  const date = (iso: string) => new Date(iso).toLocaleDateString(locale, { day: 'numeric', month: 'long', year: 'numeric' })
  const empty = async () => {
    if (!await askConfirm(t('Papierkorb leeren'), t('Alles im Papierkorb ({size}) endgültig löschen? Das lässt sich nicht rückgängig machen.', { size: fmtSize(trash?.usage ?? 0) }), t('Endgültig löschen'))) return
    try { await api.post('/api/files/trash/empty'); notify(t('Papierkorb geleert')); loadTrash() } catch (e) { fail(e) }
  }
  return (
    <div className={s.page}>
      <PageTitle>{t('Papierkorb')}</PageTitle>
      <Text>{t('Gelöschte Dateien und Ordner lassen sich aus dem Papierkorb zurückholen, bis er geleert wird.')}</Text>
      {trash && (items.length === 0
        ? <Text className={s.muted}>{t('Der Papierkorb ist leer.')}</Text>
        : <div className={s.stats}>
          <span className={s.muted}>{t('Inhalt')}</span>
          <span>{[items.length - dirs === 1 ? t('1 Datei') : items.length > dirs ? t('{n} Dateien', { n: items.length - dirs }) : '', dirs ? t('{n} Ordner', { n: dirs }) : ''].filter(Boolean).join(', ')}</span>
          <span className={s.muted}>{t('Belegt')}</span>
          <span>{fmtSize(trash.usage)}{st.trashMb ? ' ' + t('von {limit}', { limit: mbText(st.trashMb) }) : ''}</span>
          {oldest && <><span className={s.muted}>{t('Am längsten darin')}</span>
            <span>{t('gelöscht am {date}', { date: date(oldest) })}{st.trashDays ? ' · ' + t('wird am {date} endgültig entfernt', { date: date(new Date(Date.parse(oldest) + st.trashDays * 86_400_000).toISOString()) }) : ''}</span></>}
        </div>)}
      {items.length > 0 && <div className={s.actions}>
        <Button onClick={() => { location.hash = '#/files/:trash' }}>{t('Papierkorb öffnen')}</Button>
        <Button appearance="primary" onClick={empty}>{t('Jetzt leeren')}</Button>
      </div>}
      <div className={s.row}>
        <Choice label={t('Automatisch leeren nach')} value={st.trashDays} steps={[7, 14, 30, 60, 90, 180, 365, 0]} max={l.trashDays}
          text={d => daysText(d, t('nie'))} onChange={v => save({ trashDays: v })} />
        <Choice label={t('Speichergrenze')} value={st.trashMb} steps={sizeSteps} max={l.trashMb} text={mbText} onChange={v => save({ trashMb: v })} />
      </div>
      <Hint>{t('Ist die Grenze erreicht, wird das am längsten Gelöschte zuerst endgültig entfernt.')}</Hint>
    </div>
  )
}

export function FilesLinkSettings() {
  const s = useStyles()
  const { data, save } = useFileSettings()
  if (!data) return null
  const st = data.settings, l = data.limits
  return (
    <div className={s.page}>
      <PageTitle>{t('Freigabelinks')}</PageTitle>
      <Text>{t('Vorgaben für neue Links im Teilen-Dialog – dort lassen sie sich je Link ändern.')}</Text>
      <div className={s.row}>
        <Choice label={t('Läuft ab nach Abrufen')} value={st.linkMaxDownloads} steps={[1, 3, 5, 10, 25, 50, 100, 0]} max={l.linkMaxDownloads}
          text={n => n === 0 ? t('unbegrenzt') : String(n)} onChange={v => save({ linkMaxDownloads: v })} />
        <Choice label={t('Läuft ab nach')} value={st.linkDays} steps={[1, 7, 14, 30, 90, 365, 0]} max={l.linkDays}
          text={d => daysText(d, t('kein Ablaufdatum'))} onChange={v => save({ linkDays: v })} />
      </div>
    </div>
  )
}
