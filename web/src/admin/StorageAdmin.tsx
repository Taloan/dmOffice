import { Button, Checkbox, Input, Link, MessageBar, MessageBarBody, MessageBarTitle, ProgressBar, Switch, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, featureOn, fmtSize } from '../api'
import { FormerTargets } from './FormerTargets'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { AdminHeading } from './AdminHeading'
import { BOX_COLORS } from './StorageDiagrams'
import { Target, TargetFields, TargetValues, valuesOf } from './TargetFields'
import { askConfirm } from '../shell/context'

interface Restore { running: boolean; finished: string | null; missing: number; restored: number; notThere: number; failed: number; error: string | null }
interface Cache { usage?: Usage; cap?: number; usedRemote?: number; toRemove?: number; full?: boolean; bytes: number; fetched: number; streamed: number; both: number; bothBytes: number; remote: number; remoteBytes: number; fixedBytes: number; pendingCount: number; pendingBytes: number; waiting?: number; thumbBytes?: number; busy?: BusyPart[] | null }
interface BusyPart { reason: string; ext: string | null; count: number; bytes: number }
interface Status { lowSpace?: boolean; otherServer?: string | null; cache?: Cache | null; restore?: Restore | null; running: boolean; lastRun: string | null; lastError: string | null; local: number; mirrored: number; uploaded: number; pending: number }
interface StorageState {
  target: Target; webDavUrl: string | null; webDavUser: string | null; hasWebDavPassword: boolean; folder: string | null
  limited: boolean; cacheTargetGb: number; minFreeGb: number; atTargetBytes: number; usage?: Usage; sizeGb?: number | null
  volume?: { total: number; free: number } | null; targetSpace?: { total: number; free: number; ours: number } | null; rescued?: { finished: string; rescued: number } | null
  mirror: boolean; endpoint: string | null; region: string | null; bucket: string | null; prefix: string | null; accessKey: string | null
  hasSecret: boolean; configured: boolean; status: Status; notHome?: { count: number; bytes: number } | null
}

const GB = 1024 * 1024 * 1024
/** Belegung des Storage Extender, von links nach rechts */
// strict heißt in der Oberfläche „Minimal“, min heißt „Sparsam“ (gespeicherte Werte bleiben, damit sich bestehende Einstellungen nicht ändern)
type Usage = 'strict' | 'min' | 'opt' | 'max'
const USAGES: Usage[] = ['strict', 'min', 'opt', 'max']
const usageName = (u: Usage) => u === 'strict' ? t('Minimal') : u === 'min' ? t('Sparsam') : u === 'opt' ? t('Optimal') : t('Maximal')
/** Unterzeile und Erklärung je Stufe */
const usageTitle = (u: Usage) => u === 'strict' ? t('Strikt: sofort freigeben') : u === 'min' ? t('Nur bei Bedarf') : u === 'opt' ? t('Kopien möglichst behalten') : t('Freien Platz ausnutzen')
const usageText = (u: Usage) => u === 'strict' ? t('Dateien werden nur dann in den Storage Extender verschoben, wenn lokal der Platz fehlt. Liegen sie wieder lokal, wird die externe Kopie sofort entfernt.')
  : u === 'min' ? t('Dateien werden nur dann in den Storage Extender verschoben, wenn lokal der Platz fehlt. Liegen sie später wieder lokal, wird die externe Kopie nach 7 Tagen entfernt.')
  : u === 'opt' ? t('Dateien werden nur dann in den Storage Extender verschoben, wenn lokal der Platz fehlt. Externe Kopien bleiben erhalten, auch wenn die Dateien wieder lokal liegen. Erst wenn extern Platz benötigt wird, werden diese Kopien entfernt.')
  : t('Freier Speicherplatz im Storage Extender wird genutzt, um möglichst viele Dateien zusätzlich extern vorzuhalten. So entstehen weitere Kopien, solange ausreichend Platz vorhanden ist.')
/** Der Server meldet „unbegrenzt“ als größte Zahl */
const unlimited = (n: number | undefined) => n == null || n >= 9e18

/** Größe in GB: Zahl und Einheit (GB | TB) in einem Feld - für alle Grenzen auf der Seite gleich */
function SizeField({ gb, onChange, disabled, label }: { gb: number; onChange: (gb: number) => void; disabled?: boolean; label: string }) {
  const [unit, setUnit] = useState<'GB' | 'TB'>(gb >= 1024 && gb % 1024 === 0 ? 'TB' : 'GB')
  const f = unit === 'TB' ? 1024 : 1
  const pick = (u: 'GB' | 'TB') => <button type="button" disabled={disabled} onClick={() => setUnit(u)} aria-pressed={unit === u}
    style={{ border: 'none', borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, padding: '0 9px', height: '100%', cursor: disabled ? 'default' : 'pointer', fontSize: 12,
      background: unit === u ? tokens.colorBrandBackground2 : 'transparent', color: unit === u ? tokens.colorBrandForeground2 : tokens.colorNeutralForeground3, fontWeight: unit === u ? 600 : 400 }}>{u}</button>
  return (
    <Input type="number" min={1} step={unit === 'TB' ? 0.1 : 1} value={String(Math.round(gb / f * 10) / 10)} disabled={disabled} aria-label={label}
      style={{ width: 180, paddingRight: 0, overflow: 'hidden' }} input={{ style: { textAlign: 'right' } }}
      contentAfter={<span style={{ display: 'flex', alignSelf: 'stretch', height: 30 }}>{pick('GB')}{pick('TB')}</span>}
      onChange={(_, d) => { const v = Number(d.value); if (Number.isFinite(v) && v >= 0) onChange(Math.round(v * f * 10) / 10) }} />
  )
}

/** Belegung als drei Knöpfe nebeneinander: Minimal, Optimal, Maximal */
function UsagePick({ value, onChange }: { value: Usage; onChange: (u: Usage) => void }) {
  return (
    <div role="radiogroup" aria-label={t('Belegung')} style={{ display: 'flex', width: 'fit-content', border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: 4, overflow: 'hidden' }}>
      {USAGES.map((u, i) => <button key={u} type="button" role="radio" aria-checked={value === u} onClick={() => onChange(u)}
        style={{ border: 'none', borderLeft: i ? `1px solid ${tokens.colorNeutralStroke2}` : 'none', padding: '6px 12px', cursor: 'pointer', fontSize: 13,
          background: value === u ? tokens.colorBrandBackground2 : 'transparent', color: value === u ? tokens.colorBrandForeground2 : tokens.colorNeutralForeground1, fontWeight: value === u ? 600 : 400 }}>{usageName(u)}</button>)}
    </div>
  )
}

/** Balken „x von y belegt“: Teile in ihren Farben, der Rest frei */
function Bar({ label, whole, parts }: { label: string; whole: number; parts: { bytes: number; color: string }[] }) {
  return (
    <div>
      <Text size={200} block style={{ color: tokens.colorNeutralForeground3 }}>{label}</Text>
      <div style={{ display: 'flex', height: 14, borderRadius: 4, overflow: 'hidden', border: `1px solid ${tokens.colorNeutralStroke1}`, marginTop: 3 }}>
        {parts.filter(p => p.bytes > 0).map((p, i) => <span key={i} style={{ width: `${Math.min(100, p.bytes / Math.max(1, whole) * 100)}%`, background: p.color }} />)}
      </div>
    </div>
  )
}

/** Tabelle einer Spalte - in beiden Spalten dieselben Zeilen mit denselben Namen. null: noch kein Wert. */
interface FactRow { label: string; value: string | null; color?: string; free?: boolean; strong?: boolean; sub?: boolean }
function Facts({ rows }: { rows: FactRow[] }) {
  return (
    <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13, alignSelf: 'start' }}>
      <tbody>{rows.map(r => <tr key={r.label} style={{ borderTop: `1px solid ${tokens.colorNeutralStroke2}` }}>
        <td style={{ padding: '4px 0', paddingLeft: r.sub ? 15 : 0, color: r.sub ? tokens.colorNeutralForeground3 : undefined, fontWeight: r.strong ? 600 : undefined }}>
          {(r.color || r.free) && <span style={{ display: 'inline-block', width: 9, height: 9, borderRadius: 2, marginRight: 6, background: r.color, border: r.free ? `1px dashed ${tokens.colorNeutralStroke1}` : undefined }} />}
          {r.label}
        </td>
        <td style={{ padding: '4px 0', textAlign: 'right', whiteSpace: 'nowrap', fontVariantNumeric: 'tabular-nums', fontWeight: r.strong ? 600 : undefined }}>{r.value ?? '–'}</td>
      </tr>)}</tbody>
    </table>
  )
}

/** Eine Spalte: Überschrift, Balken, Einstellungen, Tabelle, Fußzeile - die Zeilen teilen sich beide Spalten (subgrid) */
const column: React.CSSProperties = { display: 'grid', gridRow: 'span 5', gridTemplateRows: 'subgrid', rowGap: 10, padding: '14px 16px', borderRadius: 10,
  border: `1px solid ${tokens.colorNeutralStroke2}`, background: tokens.colorNeutralBackground1 }
const fieldLabel: React.CSSProperties = { display: 'flex', alignItems: 'center', minHeight: 32, marginBottom: 2 }
const COLORS = { files: BOX_COLORS.used, clear: BOX_COLORS.clean, thumbs: BOX_COLORS.foreign }

/** Satz mit Verweis auf eine Verwaltungsseite: {link} im Text steht für den Verweis. */
function withLink(text: string, label: string, open: () => void) {
  const [before, after] = text.split('{link}')
  return <>{before}<Link inline onClick={open}>{label}</Link>{after}</>
}

/**
 * Verwaltung → Storage Extender: Am Ziel (S3, WebDAV, Ordner) liegen alle Dateien, auf dem Server die zuletzt genutzten.
 * Passen alle in die Grenzen, liegen alle auch auf dem Server. Der geheime Schlüssel kommt nie zurück in die Oberfläche.
 */
export function StorageAdmin({ onOpen }: { onOpen: (page: string) => void }) {
  const { fail, notify } = useApp()
  const [tested, setTested] = useState(false)
  const [st, setSt] = useState<StorageState | null>(null)
  const [f, setF] = useState<TargetValues>(valuesOf({}))
  const [target, setTarget] = useState<Target>('s3')
  const [busy, setBusy] = useState(false)
  // Regler: zeigen die Auswirkung sofort, wirksam erst mit „Übernehmen“ - sonst bereinigte dmOffice schon beim Ausprobieren
  const [freeGb, setFreeGb] = useState(50)
  const [limited, setLimited] = useState(false)
  const [limitGb, setLimitGb] = useState(100)
  const ext = featureOn('extenderusage')
  const [usage, setUsage] = useState<Usage>('opt')
  const [sizeLimited, setSizeLimited] = useState(false)
  const [sizeGb, setSizeGb] = useState(1024)
  const take = (s: StorageState) => {
    setSt(s)
    setFreeGb(s.minFreeGb)
    setLimited(s.limited)
    setLimitGb(s.cacheTargetGb)
    setUsage(s.usage ?? 'max')
    setSizeLimited(s.sizeGb != null)
    setSizeGb(s.sizeGb ?? 1024)
    setTarget(s.target)
    setF(valuesOf(s))
  }
  useEffect(() => { api.get<StorageState>('/api/admin/storage').then(take).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Stand nachladen, solange der Extender läuft - Eingaben dabei nicht überschreiben
  useEffect(() => {
    if (!st?.mirror && !st?.status.restore?.running) return
    const h = setInterval(() => api.get<StorageState>('/api/admin/storage').then(s => setSt(o => (o ? { ...o, status: s.status, volume: s.volume, targetSpace: s.targetSpace, atTargetBytes: s.atTargetBytes, notHome: s.notHome } : s))).catch(() => {}), 5000)
    return () => clearInterval(h)
  }, [st?.mirror, st?.status.restore?.running])
  if (!st) return null
  const save = async (mirror: boolean, secretKey?: string | null) => {
    try {
      take(await api.put<StorageState>('/api/admin/storage', { mirror, ...f, target, secretKey: secretKey !== undefined ? secretKey : f.secretKey || null, webDavPassword: f.webDavPassword || null }))
      notify(t('Gespeichert'))
    } catch (e) { fail(e) }
  }
  const test = async () => {
    setBusy(true)
    // Erst die Eingaben speichern, dann genau die prüfen
    try { take(await api.put<StorageState>('/api/admin/storage', { mirror: st.mirror, ...f, target, secretKey: f.secretKey || null, webDavPassword: f.webDavPassword || null })); await api.post('/api/admin/storage/test'); setTested(true) } catch (e) { setTested(false); fail(e) } finally { setBusy(false) }
  }
  const apply = async () => {
    try { take(await api.put<StorageState>('/api/admin/storage/mode', { limited, targetGb: limitGb, minFreeGb: freeGb, ...(ext ? { usage, sizeLimited, sizeGb } : {}) })); notify(t('Gespeichert')) } catch (e) { fail(e) }
  }
  const reset = () => { setFreeGb(st.minFreeGb); setLimited(st.limited); setLimitGb(st.cacheTargetGb); setUsage(st.usage ?? 'max'); setSizeLimited(st.sizeGb != null); setSizeGb(st.sizeGb ?? 1024) }
  const set = (k: keyof TargetValues, value: string) => { setTested(false); setF(x => ({ ...x, [k]: value })) }
  const ready = tested || st.mirror
  const muted = { color: tokens.colorNeutralForeground3 }
  const s = st.status
  // Dateien, die nur im Storage Extender liegen: solange darf nichts den Weg dorthin abschneiden (der Server prüft es auch)
  const away = st.notHome?.count ?? 0
  const restoring = s.restore?.running ? s.restore : null
  const restoreDone = restoring ? restoring.restored + restoring.notThere + restoring.failed : 0
  // Der „andere Server“ war dieser vor einem Umzug (neue Kennung): Vermerk am Ziel entfernen
  const forgetOther = async () => {
    if (!await askConfirm('', t('Nur bestätigen, wenn kein anderer dmOffice-Server diesen Ort benutzt. Danach räumt dmOffice dort wieder auf. Vermerk entfernen?'))) return
    try { await api.post('/api/admin/storage/forget-other', {}); notify(t('Vermerk entfernt')); take(await api.get<StorageState>('/api/admin/storage')) } catch (e) { fail(e) }
  }

  // Lokales Speichervolumen: 48 Kästchen für das ganze Volume, der Wert eines Kästchens hängt nur von dessen Größe ab. Beide Regler
  // sind Begrenzer und sperren nur Kästchen. Rot ist, was sich bereinigen lässt - bei geänderten Reglern als Vorschau.
  const dirty = freeGb !== st.minFreeGb || limited !== st.limited || (limited && limitGb !== st.cacheTargetGb)
    || (ext && (usage !== (st.usage ?? 'max') || sizeLimited !== (st.sizeGb != null) || (sizeLimited && sizeGb !== st.sizeGb)))
  const vol = st.volume
  const cache = s.cache
  const used = cache?.bytes ?? 0
  const fixedBytes = Math.min(used, cache?.fixedBytes ?? 0)
  const pendingBytes = cache?.pendingBytes ?? 0
  const other = vol ? Math.max(0, vol.total - vol.free - used) : 0
  // Wie der Server: höchstens die Hälfte des Volumes frei lassen - sonst passt dort keine Datei mehr
  const maxFreeGb = vol ? Math.max(1, Math.floor(vol.total / 2 / GB)) : Infinity
  const reserve = Math.min(freeGb, maxFreeGb) * GB
  const room = vol ? Math.max(0, vol.total - reserve - other) : 0
  const limit = limited ? Math.min(room, limitGb * GB) : room
  const stay = Math.max(Math.min(used, limit), fixedBytes)
  const clean = Math.max(0, used - stay)
  const allBytes = cache ? cache.bothBytes + cache.remoteBytes + pendingBytes : used
  const allCount = cache ? cache.both + cache.remote + cache.pendingCount : 0
  const onlyThere = cache?.remoteBytes ?? 0
  // Liegen Dateien nur im Storage Extender und passen alle: Sie kommen zurück
  const comesBack = onlyThere > 0 && used + onlyThere <= limit
  // Alles, was am Ziel liegt, auch Vorschaubilder und gelöschte Dateien in der Aufbewahrungsfrist
  const ours = st.atTargetBytes
  // Speicherplatz insgesamt: lokaler Speicherpool + Speicherbegrenzung des Storage Extender
  // Grenze des Storage Extender aus den Feldern - wie beim lokalen Speicherpool schon vor „Übernehmen“ als Vorschau
  const cap = ext ? (sizeLimited ? sizeGb * GB : undefined) : cache?.cap
  const usedRemote = cache?.usedRemote ?? ours
  // Vorschau wie beim lokalen Speicherpool: auf „Minimal“ umgestellt, aber noch nicht übernommen - dann werden die Kopien der Dateien,
  // die lokal liegen, geräumt (außer dem Vorrat, wenn der lokale Speicherpool zu über 90 % belegt ist; Server: S3Mirror.ReserveShare)
  const minPreview = !!cache && (usage === 'min' || usage === 'strict') && cache.usage !== 'min' && cache.usage !== 'strict'
  const reserveKept = usage === 'min' && limit > 0 && used > limit * 0.9 ? Math.min(cache?.bothBytes ?? 0, limit * 0.1) : 0
  const toRemove = minPreview ? Math.max(cache?.toRemove ?? 0, (cache?.bothBytes ?? 0) - reserveKept + (cache?.toRemove ?? 0)) : cache?.toRemove ?? 0
  const capKnown = !unlimited(cap)
  // Im Storage Extender: Dateien (an beiden Orten und nur dort), der Rest sind Vorschaubilder
  const extFiles = cache ? cache.bothBytes + cache.remoteBytes : 0
  const extThumbs = Math.max(0, usedRemote - extFiles - (cache?.toRemove ?? 0))

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 640 }}>
      <FormerTargets purposes={['storage']} />
      <MessageBar intent="info" layout="multiline" icon={null}><MessageBarBody>
        <MessageBarTitle>{t('Storage Extender – Mehr Platz für Dateien')}</MessageBarTitle>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 6 }}>
          {ext
            ? <div>{t('Mit dem Storage Extender erweitert dmOffice die interne Dateiablage um einen externen Speicherort, beispielsweise ein NAS, eine Storage Box oder einen Object Storage. Dateien werden dort verschlüsselt gespeichert. Insgesamt stehen der lokale Speicherpool und die Grenze des Storage Extender zur Verfügung.')}</div>
            : <>
              <div><b>{t('Nur nötig, wenn der Platz auf dem Server nicht reicht.')}</b></div>
              <div>{t('Mit dem Storage Extender speichert dmOffice alle Dateien der internen Dateiablage verschlüsselt an einem externen Speicherort, beispielsweise S3, WebDAV oder eine Storage Box. Die zuletzt verwendeten Dateien liegen zusätzlich im lokalen Speicherpool und sind dadurch schnell verfügbar.')}</div>
              <div>{t('Ist der Storage Extender aktiviert, bestimmt sein Speicherplatz, wie viele Dateien insgesamt gespeichert werden können. Der lokale Speicherpool wird nicht hinzugerechnet. Der Storage Extender eignet sich daher vor allem, wenn er deutlich mehr Speicherplatz bietet als das lokale Speichervolumen.')}</div>
            </>}
        </div>
      </MessageBarBody></MessageBar>
      <AdminHeading>{t('Ziel')}</AdminHeading>
      <TargetFields target={target} onTarget={x => { setTested(false); setTarget(x) }} v={f} onChange={set} hasSecret={st.hasSecret} hasWebDavPassword={st.hasWebDavPassword} kindLocked={away > 0} />
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
        <Button appearance="primary" disabled={busy} onClick={test}>{busy ? t('Prüfe …') : t('Speichern und testen')}</Button>
        {target === 's3' && st.hasSecret && <Button appearance="subtle" disabled={away > 0} onClick={() => save(false, '')}>{t('Schlüssel löschen')}</Button>}
      </div>
      {tested && <MessageBar intent="success"><MessageBarBody>{t('Verbindung geprüft: dmOffice kann dort schreiben, lesen und löschen.')}</MessageBarBody></MessageBar>}
      {away > 0 && <Text size={200} style={muted}>{t('Art und Schlüssel lassen sich nicht ändern, solange Dateien nur im Storage Extender vollständig liegen. Beim Speichern neuer Verbindungsdaten prüft dmOffice, ob dort alle diese Dateien vorhanden sind. Fehlen Dateien, werden die neuen Verbindungsdaten nicht übernommen.')}</Text>}

      <AdminHeading>{t('Speicherplatz')}</AdminHeading>
      <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(280px, 1fr))', columnGap: 12, rowGap: 0 }}>
        {/* Lokaler Speicherpool */}
        <div style={column}>
          <Text weight="semibold" size={400}>{t('Lokaler Speicherpool')}</Text>
          {cache ? <Bar label={t('{used} von {limit} belegt', { used: fmtSize(used), limit: fmtSize(limit) })} whole={limit}
            parts={[{ bytes: used - clean, color: COLORS.files }, { bytes: clean, color: COLORS.clear }]} /> : <span />}
          <div>
            <div style={fieldLabel}><Checkbox checked={limited} onChange={(_, d) => setLimited(!!d.checked)} label={t('Höchstens nutzen')} /></div>
            <SizeField gb={limitGb} onChange={setLimitGb} disabled={!limited} label={t('Höchstens nutzen')} />
            <div style={{ ...fieldLabel, marginTop: 8 }}><Text>{t('Immer frei lassen')}</Text></div>
            <SizeField gb={freeGb} onChange={setFreeGb} label={t('Immer frei lassen')} />
            {vol && <Text size={200} block style={{ ...(freeGb > maxFreeGb ? { color: tokens.colorPaletteDarkOrangeForeground1 } : muted), marginTop: 3 }}>
              {freeGb > maxFreeGb ? t('Höchstens die Hälfte des Volumes: {max}', { max: fmtSize(maxFreeGb * GB) }) : t('vom lokalen Speichervolumen ({total}), noch {free} frei', { total: fmtSize(vol.total), free: fmtSize(vol.free) })}</Text>}
          </div>
          <Facts rows={[
            { label: t('Grenze'), value: vol ? fmtSize(limit) : null, strong: true },
            { label: t('Belegt durch Dateien'), value: cache ? fmtSize(used) : null, color: COLORS.files },
            { label: t('davon nur hier'), value: cache ? fmtSize(pendingBytes) : null, sub: true },
            { label: t('davon an beiden Orten'), value: cache ? fmtSize(cache.bothBytes) : null, sub: true },
            { label: t('Vorschaubilder'), value: cache ? fmtSize(cache.thumbBytes ?? 0) : null, color: COLORS.thumbs },
            { label: t('Wird geräumt'), value: cache ? fmtSize(clean) : null, color: COLORS.clear },
            { label: t('Verfügbar'), value: cache ? fmtSize(Math.max(0, limit - used)) : null, free: true },
          ]} />
          <Text size={200} style={muted}>{vol ? t('Sonstige Daten auf dem Volume: {size}', { size: fmtSize(Math.max(0, other - (cache?.thumbBytes ?? 0))) }) : ''}
            {cache && (cache.waiting ?? 0) > 0 ? ' · ' + t('Noch nicht verarbeitet: {size} (wird erst danach geräumt)', { size: fmtSize(cache.waiting ?? 0) }) : ''}</Text>
        </div>
        {/* Storage Extender */}
        <div style={column}>
          <Text weight="semibold" size={400}>{t('Storage Extender')}</Text>
          {cache ? <Bar label={capKnown ? t('{used} von {limit} belegt', { used: fmtSize(usedRemote), limit: fmtSize(cap ?? 0) }) : t('{used} belegt', { used: fmtSize(usedRemote) })}
            whole={capKnown ? cap ?? 0 : usedRemote} parts={[{ bytes: extFiles, color: COLORS.files }, { bytes: extThumbs, color: COLORS.thumbs }, { bytes: toRemove, color: COLORS.clear }]} />
            : <Text size={200} style={muted}>{st.mirror ? t('Erster Abgleich startet gleich …') : t('Nicht eingeschaltet')}</Text>}
          <div>
            {ext && <>
              <div style={fieldLabel}><Checkbox checked={sizeLimited} onChange={(_, d) => setSizeLimited(!!d.checked)} label={t('Höchstens nutzen')} /></div>
              <SizeField gb={sizeGb} onChange={setSizeGb} disabled={!sizeLimited} label={t('Höchstens nutzen')} />
              <div style={{ ...fieldLabel, marginTop: 8 }}><Text>{t('Belegung')}</Text></div>
              <UsagePick value={usage} onChange={setUsage} />
              <Text size={200} weight="semibold" block style={{ marginTop: 4 }}>{usageTitle(usage)}</Text>
              <Text size={200} block style={muted}>{usageText(usage)}</Text>
            </>}
          </div>
          <Facts rows={[
            { label: t('Grenze'), value: capKnown ? fmtSize(cap ?? 0) : t('unbegrenzt'), strong: true },
            { label: t('Belegt durch Dateien'), value: cache ? fmtSize(extFiles) : null, color: COLORS.files },
            { label: t('davon nur hier'), value: cache ? fmtSize(cache.remoteBytes) : null, sub: true },
            { label: t('davon an beiden Orten'), value: cache ? fmtSize(cache.bothBytes) : null, sub: true },
            { label: t('Vorschaubilder'), value: cache ? fmtSize(extThumbs) : null, color: COLORS.thumbs },
            { label: t('Wird geräumt'), value: cache ? fmtSize(toRemove) : null, color: COLORS.clear },
            { label: t('Verfügbar'), value: cache ? (capKnown ? fmtSize(Math.max(0, (cap ?? 0) - usedRemote)) : t('unbegrenzt')) : null, free: true },
          ]} />
          <Text size={200} style={cache?.full ? { color: tokens.colorPaletteDarkOrangeForeground1 } : muted}>{cache?.full ? t('Die Grenze des Storage Extender ist erreicht.') : ''}</Text>
        </div>
      </div>

      {/* Insgesamt: gleich aufgebaut, mit Übernehmen darunter */}
      <div style={{ ...column, display: 'flex', flexDirection: 'column', gap: 10, marginTop: 2 }}>
        <Text weight="semibold" size={400}>{t('Insgesamt')}</Text>
        {cache && capKnown && <Bar label={t('{used} von {limit} belegt', { used: fmtSize(allBytes), limit: fmtSize(limit + (cap ?? 0)) })} whole={limit + (cap ?? 0)} parts={[{ bytes: allBytes, color: COLORS.files }]} />}
        <div style={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 8 }}>
          <div><Text size={200} block style={muted}>{t('Grenze')}</Text><Text size={500} weight="semibold" block>{capKnown ? fmtSize(limit + (cap ?? 0)) : t('unbegrenzt')}</Text>
            {capKnown && <Text size={200} block style={muted}>{fmtSize(limit)} + {fmtSize(cap ?? 0)}</Text>}</div>
          <div><Text size={200} block style={muted}>{t('Belegt durch Dateien')}</Text><Text size={500} weight="semibold" block>{cache ? fmtSize(allBytes) : '–'}</Text>
            {cache && <Text size={200} block style={muted}>{allCount === 1 ? t('1 Datei') : t('{n} Dateien', { n: allCount.toLocaleString(locale) })}</Text>}</div>
          <div><Text size={200} block style={muted}>{t('Verfügbar')}</Text><Text size={500} weight="semibold" block>{!cache ? '–' : capKnown ? fmtSize(Math.max(0, limit + (cap ?? 0) - allBytes)) : t('unbegrenzt')}</Text></div>
        </div>
        <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
          <Button appearance="primary" disabled={!dirty} onClick={apply}>{t('Übernehmen')}</Button>
          <Button disabled={!dirty} onClick={reset}>{t('Zurücksetzen')}</Button>
          {dirty && <Text size={200} style={muted}>{t('Noch nicht übernommen')}</Text>}
        </div>
      </div>
      {vol && st.mirror && (s.running && (clean > 0 || pendingBytes > 0)
        ? <MessageBar intent="info" layout="multiline"><MessageBarBody>{t('dmOffice überträgt und räumt gerade.')}</MessageBarBody></MessageBar>
        : clean > 0 ? <MessageBar intent="warning" layout="multiline"><MessageBarBody>{t('Mit diesen Werten räumt dmOffice {size} im lokalen Speicherpool. Diese Dateien bleiben im Storage Extender und werden bei Bedarf automatisch nachgeladen.', { size: fmtSize(clean) })}</MessageBarBody></MessageBar>
        : used > limit ? <MessageBar intent="info" layout="multiline"><MessageBarBody>{t('Noch nicht verarbeitete Dateien überschreiten die Grenze um {size}. dmOffice räumt sie, sobald sie verarbeitet sind.', { size: fmtSize(used - limit) })}</MessageBarBody></MessageBar>
        : comesBack ? <MessageBar intent="success" layout="multiline"><MessageBarBody>{t('Mit diesen Werten passen alle Dateien in den lokalen Speicherpool. dmOffice lädt die fehlenden Dateien automatisch nach.')}</MessageBarBody></MessageBar>
        : null)}
      {s.lowSpace && <MessageBar intent="warning"><MessageBarBody>{t('Auf dem lokalen Speichervolumen ist nur noch der frei zu lassende Speicherplatz verfügbar, es sind {free} frei. Neue Dateien werden abgelehnt, solange sich der lokale Speicherpool nicht weiter bereinigen lässt.', { free: fmtSize(vol?.free ?? 0) })}</MessageBarBody></MessageBar>}

      {/* Stand des Abgleichs */}
      {st.mirror && <>
        {restoring ? <>
          <Text size={200}>{restoring.missing === 0 ? t('Prüft, was hier fehlt …')
            : t('Holt Dateien zurück: {restored} von {missing} …', { restored: restoreDone.toLocaleString(locale), missing: restoring.missing.toLocaleString(locale) })}</Text>
          {restoring.missing > 0 && <ProgressBar value={Math.min(1, restoreDone / restoring.missing)} thickness="medium" />}
        </> : s.running ? <>
          <Text size={200}>{t('Gleicht gerade ab …')}{s.uploaded > 0 ? ' · ' + t('{n} hochgeladen', { n: s.uploaded.toLocaleString(locale) }) : ''}</Text>
          <ProgressBar thickness="medium" />
        </> : null}
        {s.pending > 0 && <Text size={200}>{t('{n} noch offen', { n: s.pending })}</Text>}
        {s.lastRun && <Text size={200} style={muted}>{t('Zuletzt abgeglichen: {when} · Die Zahlen stammen vom letzten Abgleich.', { when: new Date(s.lastRun).toLocaleString(locale) })}</Text>}
        {s.restore && !s.restore.running && s.restore.finished && s.restore.missing > 0 && (
          <Text size={200}>{t('Zurückgeholt: {restored} von {missing}', { restored: s.restore.restored, missing: s.restore.missing })}
            {s.restore.notThere + s.restore.failed > 0 ? ' · ' + t('{n} waren auch dort nicht zu finden oder kaputt', { n: s.restore.notThere + s.restore.failed }) : ''}</Text>
        )}
        {s.lastError && <MessageBar intent="warning"><MessageBarBody>{s.lastError}</MessageBarBody></MessageBar>}
        {s.restore?.error && <MessageBar intent="warning"><MessageBarBody>{s.restore.error}</MessageBarBody></MessageBar>}
      </>}

      {s.otherServer && <MessageBar intent="warning" layout="multiline"><MessageBarBody>{t('Am selben Ort speichert noch ein anderer dmOffice-Server (zuletzt {when}). Solange löscht dmOffice dort nichts. Jeder Server braucht einen eigenen Ordner.', { when: new Date(s.otherServer).toLocaleString() })}<div style={{ marginTop: 6 }}><Button size="small" onClick={forgetOther}>{t('Das war dieser Server vor einem Umzug')}</Button></div></MessageBarBody></MessageBar>}
      {st.rescued && st.rescued.rescued > 0 && <MessageBar intent="info" layout="multiline"><MessageBarBody>{t('Nach der Wiederherstellung wurden {n} Dateien ohne Namen gerettet, die nach dem Core-Backup hochgeladen worden waren. Sie liegen im Ordner „Nach Wiederherstellung gefunden“.', { n: st.rescued.rescued })}</MessageBarBody></MessageBar>}

      {/* Ganz am Ende: Hinweis zum Backup und der Schalter in einem Kasten - eingeschaltet grün */}
      <MessageBar intent={st.mirror ? 'success' : 'warning'} layout="multiline" style={{ marginTop: 16 }}><MessageBarBody>
        <div>
          {t('Für ein eventuelles Backup des Storage Extenders ist der Administrator verantwortlich.')}
          {' '}{withLink(t('Die Dateien der internen Dateiablage können über das interne {link} gesichert werden.'), t('Files-Backup'), () => onOpen('backup'))}
        </div>
        {!ready && <Text size={200} block style={{ ...muted, marginTop: 6 }}>{t('Erst das Ziel speichern und testen.')}</Text>}
        <div style={{ marginTop: 8 }}>
          {/* Gesperrt, solange Dateien nur im Storage Extender liegen: bleibt sichtbar eingeschaltet, ein Klick nennt den Grund */}
          <Switch checked={st.mirror} disabled={!st.configured || !ready} style={st.mirror && away > 0 ? { cursor: 'not-allowed' } : undefined}
            onChange={(_, d) => { if (st.mirror && away > 0) notify(t('Storage Extender bleibt aktiviert'), t('Einige Dateien liegen nur im Storage Extender. Er lässt sich erst ausschalten, wenn alle Dateien auch im lokalen Speicherpool liegen. Bei genügend Speicherplatz lädt dmOffice sie automatisch nach.'), 'warning'); else save(d.checked) }}
            label={t('Storage Extender verwenden')} />
        </div>
      </MessageBarBody></MessageBar>
      {st.mirror && away > 0 && vol && <Text size={200} style={muted}>{t('Zum Ausschalten werden {need} im lokalen Speicherpool gebraucht, verfügbar sind {usable}.', { need: fmtSize(used + onlyThere), usable: fmtSize(limit) })}</Text>}
      {st.mirror && <Text size={200} style={muted}>{away > 0
        ? t('Einige Dateien liegen nur im Storage Extender. Er lässt sich erst ausschalten, wenn alle Dateien auch im lokalen Speicherpool liegen. Bei genügend Speicherplatz lädt dmOffice sie automatisch nach.')
        : t('Alle Dateien liegen auch im lokalen Speicherpool.')}</Text>}
    </div>
  )
}
