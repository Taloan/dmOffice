import { Button, Checkbox, InfoLabel, Link, MessageBar, MessageBarBody, MessageBarTitle, ProgressBar, Slider, Switch, Text, tokens } from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'
import { AdminHeading } from './AdminHeading'
import { AdminCol, AdminTable } from './AdminTable'
import { boxes, BOX_COLORS, ExtenderPicture, LEFT_BOXES } from './StorageDiagrams'
import { Target, TargetFields, TargetValues, valuesOf } from './TargetFields'
import { askConfirm } from '../shell/context'

interface Restore { running: boolean; finished: string | null; missing: number; restored: number; notThere: number; failed: number; error: string | null }
interface Cache { bytes: number; fetched: number; streamed: number; both: number; bothBytes: number; remote: number; remoteBytes: number; fixedBytes: number; pendingCount: number; pendingBytes: number; waiting?: number; thumbBytes?: number; busy?: BusyPart[] | null }
interface BusyPart { reason: string; ext: string | null; count: number; bytes: number }
interface Status { lowSpace?: boolean; otherServer?: string | null; cache?: Cache | null; restore?: Restore | null; running: boolean; lastRun: string | null; lastError: string | null; local: number; mirrored: number; uploaded: number; pending: number }
interface StorageState {
  target: Target; webDavUrl: string | null; webDavUser: string | null; hasWebDavPassword: boolean; folder: string | null
  limited: boolean; cacheTargetGb: number; minFreeGb: number; atTargetBytes: number
  volume?: { total: number; free: number } | null; targetSpace?: { total: number; free: number; ours: number } | null; rescued?: { finished: string; rescued: number } | null
  mirror: boolean; endpoint: string | null; region: string | null; bucket: string | null; prefix: string | null; accessKey: string | null
  hasSecret: boolean; configured: boolean; status: Status; notHome?: { count: number; bytes: number } | null
}

const GB = 1024 * 1024 * 1024

/** Zeile der Tabellen „Belegung“ und „Dateien“. sub: gehört zur Zeile darüber, eingerückt. */
interface Fact { key: string; label: string; bytes: number; count?: number; color?: string; strong?: boolean; sub?: boolean }
const factLabel = (r: Fact) => (
  <span style={{ paddingLeft: r.sub ? 16 : 0, fontWeight: r.strong ? 600 : undefined, color: r.sub ? tokens.colorNeutralForeground3 : undefined }}>
    {r.color && <span style={{ display: 'inline-block', width: 9, height: 9, borderRadius: 2, background: r.color, marginRight: 6 }} />}{r.label}
  </span>
)
const factNumber = (text: string, r: Fact) => <div style={{ textAlign: 'right', whiteSpace: 'nowrap', fontWeight: r.strong ? 600 : undefined, fontVariantNumeric: 'tabular-nums' }}>{text}</div>
const usageCols: AdminCol<Fact>[] = [
  { key: 'label', label: () => t('Bereich'), width: 0, sort: r => r.label, render: factLabel, fixed: true },
  { key: 'size', label: () => t('Größe'), width: 110, sort: r => r.bytes, render: r => factNumber(fmtSize(r.bytes), r), fixed: true },
]
const fileCols: AdminCol<Fact>[] = [
  { key: 'label', label: () => t('Ort'), width: 0, sort: r => r.label, render: factLabel, fixed: true },
  { key: 'count', label: () => t('Dateien'), width: 90, sort: r => r.count ?? 0, render: r => factNumber((r.count ?? 0).toLocaleString(locale), r) },
  { key: 'size', label: () => t('Größe'), width: 110, sort: r => r.bytes, render: r => factNumber(fmtSize(r.bytes), r), fixed: true },
]

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
  const take = (s: StorageState) => {
    setSt(s)
    setFreeGb(s.minFreeGb)
    setLimited(s.limited)
    setLimitGb(s.cacheTargetGb)
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
    try { take(await api.put<StorageState>('/api/admin/storage/mode', { limited, targetGb: limitGb, minFreeGb: freeGb })); notify(t('Gespeichert')) } catch (e) { fail(e) }
  }
  const reset = () => { setFreeGb(st.minFreeGb); setLimited(st.limited); setLimitGb(st.cacheTargetGb) }
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
    if (!await askConfirm('', t('Nur bestätigen, wenn kein anderer dmOffice-Server diesen Ort benutzt – etwa weil dieser Server umgezogen ist. Danach räumt dmOffice dort wieder auf. Vermerk entfernen?'))) return
    try { await api.post('/api/admin/storage/forget-other', {}); notify(t('Vermerk entfernt')); take(await api.get<StorageState>('/api/admin/storage')) } catch (e) { fail(e) }
  }

  // Lokales Speichervolumen: 48 Kästchen für das ganze Volume, der Wert eines Kästchens hängt nur von dessen Größe ab. Beide Regler
  // sind Begrenzer und sperren nur Kästchen. Rot ist, was sich bereinigen lässt - bei geänderten Reglern als Vorschau.
  const dirty = freeGb !== st.minFreeGb || limited !== st.limited || (limited && limitGb !== st.cacheTargetGb)
  const vol = st.volume
  const cache = s.cache
  const used = cache?.bytes ?? 0
  const fixedBytes = Math.min(used, cache?.fixedBytes ?? 0)
  const pendingBytes = cache?.pendingBytes ?? 0
  const other = vol ? Math.max(0, vol.total - vol.free - used) : 0
  const reserve = freeGb * GB
  const room = vol ? Math.max(0, vol.total - reserve - other) : 0
  const limit = limited ? Math.min(room, limitGb * GB) : room
  const unit = vol ? vol.total / LEFT_BOXES : 0
  const stay = Math.max(Math.min(used, limit), fixedBytes)
  const clean = Math.max(0, used - stay)
  const allBytes = cache ? cache.bothBytes + cache.remoteBytes + pendingBytes : used
  const allCount = cache ? cache.both + cache.remote + cache.pendingCount : 0
  const onlyThere = cache?.remoteBytes ?? 0
  // Liegen Dateien nur im Storage Extender und passen alle: Sie kommen zurück
  const comesBack = onlyThere > 0 && used + onlyThere <= limit
  const extender = st.targetSpace
  // Alles, was am Ziel liegt, auch Vorschaubilder und gelöschte Dateien in der Aufbewahrungsfrist
  const ours = st.atTargetBytes
  const foreign = extender ? Math.max(0, extender.total - extender.free - extender.ours) : 0
  const extenderFree = extender ? Math.max(0, extender.free - pendingBytes) : 0
  // Regler in GB: nicht gleichmäßig, damit 1 GB genauso fein einstellbar ist wie mehrere TB
  const maxGb = Math.max(2, Math.round((vol?.total ?? 100 * GB) / GB))
  const gbOf = (v: number) => Math.max(1, Math.round(Math.pow(maxGb, v / 100)))
  const sliderOf = (g: number) => Math.min(100, Math.round(Math.log(Math.max(1, g)) / Math.log(maxGb) * 100))
  const busyLabel = (p: BusyPart) => p.reason === 'hash' ? t('Prüfsumme fehlt')
    : p.reason === 'hashOld' ? t('Prüfsumme fehlt (Versionen, Papierkorb)')
    : p.reason === 'text' ? t('Wartet auf die Volltextsuche')
    : p.reason === 'edited' ? t('Bearbeitetes Foto ohne Vorschaubild ({ext})', { ext: p.ext ?? '' })
    : t('Vorschaubild fehlt ({ext})', { ext: p.ext ?? '' })

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 10, maxWidth: 640 }}>
      <MessageBar intent="info" layout="multiline" icon={null}><MessageBarBody>
        <MessageBarTitle>{t('Storage Extender – Mehr Platz für Dateien')}</MessageBarTitle>
        <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 6 }}>
          <div><b>{t('Nur nötig, wenn der Platz auf dem Server nicht reicht.')}</b></div>
          <div>{t('Mit dem Storage Extender erweitert dmOffice den verfügbaren Speicherplatz durch einen externen Speicherort, beispielsweise S3, WebDAV oder eine Storage Box. Alle Dateien der internen Dateiablage werden dort verschlüsselt gespeichert.')}</div>
          <Text weight="semibold">{t('Lokaler Speicherpool')}</Text>
          <div>{t('Wie viel Speicherplatz dmOffice lokal für Dateien verwenden darf, bestimmen zwei Grenzen: der freizuhaltende Speicherplatz und die maximale Größe des lokalen Speicherpools. Es gilt immer die kleinere der beiden Grenzen.')}</div>
          <ul style={{ margin: 0, paddingInlineStart: 20 }}>
            <li><b>{t('Genügend Platz:')}</b> {t('Ist der lokale Speicherpool groß genug, bleiben alle Dateien auch auf dem Server gespeichert. Damit liegen sie vollständig an beiden Speicherorten vor.')}</li>
            <li><b>{t('Begrenzter Platz:')}</b> {t('Reicht der lokale Speicherpool nicht für alle Dateien, werden bei Bedarf automatisch lange nicht mehr verwendete Dateien lokal bereinigt und angeforderte von extern nachgeladen.')}</li>
          </ul>
          <div>{t('So nutzt dmOffice den lokalen Speicherplatz optimal, unabhängig davon, wie groß die gesamte Dateisammlung ist.')}</div>
        </div>
      </MessageBarBody></MessageBar>
      <AdminHeading>{t('Ziel')}</AdminHeading>
      <TargetFields target={target} onTarget={x => { setTested(false); setTarget(x) }} v={f} onChange={set} hasSecret={st.hasSecret} hasWebDavPassword={st.hasWebDavPassword} kindLocked={away > 0} />
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', alignItems: 'center' }}>
        <Button appearance="primary" disabled={busy} onClick={test}>{busy ? t('Prüfe …') : t('Speichern und testen')}</Button>
        {target === 's3' && st.hasSecret && <Button appearance="subtle" disabled={away > 0} onClick={() => save(false, '')}>{t('Schlüssel löschen')}</Button>}
      </div>
      {tested && <MessageBar intent="success"><MessageBarBody>{t('Klappt – dmOffice kann dort schreiben, lesen und löschen.')}</MessageBarBody></MessageBar>}
      {away > 0 && <Text size={200} style={muted}>{t('Art und Schlüssel lassen sich nicht ändern, solange Dateien nur im Storage Extender vollständig liegen. Beim Speichern neuer Verbindungsdaten prüft dmOffice, ob dort alle diese Dateien vorhanden sind. Fehlen Dateien, werden die neuen Verbindungsdaten nicht übernommen.')}</Text>}

      <AdminHeading><InfoLabel size="large" info={t('Der frei zu lassende Speicherplatz bezieht sich auf das gesamte lokale Speichervolumen, einschließlich anderer dort gespeicherter Daten.')}>{t('Lokaler Speicherpool')}</InfoLabel></AdminHeading>
      {vol && <ExtenderPicture used={boxes(stay, unit)} clean={boxes(clean, unit)} usable={boxes(limit, unit)} other={boxes(other, unit)}
        there={boxes(ours, unit)} pending={boxes(pendingBytes, unit)} free={extender ? boxes(extenderFree, unit) : null} foreign={boxes(foreign, unit)}
        leftNote={t('{used} belegt, lokaler Speicherpool {limit}', { used: fmtSize(used), limit: fmtSize(limit) })}
        rightNote={extender ? t('{used} von {total} belegt', { used: fmtSize(ours), total: fmtSize(extender.total) }) : t('{used} belegt', { used: fmtSize(ours) })}
        unitLabel={unit > 0 ? t('1 Kästchen = {size}', { size: fmtSize(unit) }) : ''} fetching={onlyThere > 0 || clean > 0} />}
      <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', gap: 10, flexWrap: 'wrap' }}>
          <Text>{t('Auf dem lokalen Speichervolumen immer frei lassen')}</Text>
          <Text weight="semibold">{fmtSize(reserve)}</Text>
        </div>
        <Slider min={0} max={100} step={1} value={sliderOf(freeGb)} onChange={(_, d) => setFreeGb(Math.min(gbOf(d.value), Math.max(1, Math.floor(maxGb / 2))))} aria-label={t('Auf dem lokalen Speichervolumen immer frei lassen')} />
      </div>
      <div style={{ display: 'flex', flexDirection: 'column', gap: 2 }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', gap: 10, flexWrap: 'wrap', alignItems: 'center' }}>
          <Checkbox checked={limited} onChange={(_, d) => setLimited(!!d.checked)} label={t('Für den lokalen Speicherpool höchstens nutzen')} />
          <Text weight="semibold">{limited ? fmtSize(limitGb * GB) : t('keine Begrenzung')}</Text>
        </div>
        <Slider min={0} max={100} step={1} disabled={!limited} value={sliderOf(limitGb)} onChange={(_, d) => setLimitGb(gbOf(d.value))} aria-label={t('Für den lokalen Speicherpool höchstens nutzen')} />
      </div>
      {vol && st.mirror && (s.running && (clean > 0 || pendingBytes > 0)
        ? <MessageBar intent="info" layout="multiline"><MessageBarBody>{t('dmOffice überträgt und bereinigt gerade.')}</MessageBarBody></MessageBar>
        : clean > 0 ? <MessageBar intent="warning" layout="multiline"><MessageBarBody>{t('Mit diesen Werten bereinigt dmOffice {size} im lokalen Speicherpool. Diese Dateien bleiben im Storage Extender und werden bei Bedarf automatisch nachgeladen.', { size: fmtSize(clean) })}</MessageBarBody></MessageBar>
        : used > limit ? <MessageBar intent="info" layout="multiline"><MessageBarBody>{t('Noch nicht verarbeitete Dateien überschreiten die Begrenzung um {size}. dmOffice bereinigt sie, sobald sie verarbeitet sind.', { size: fmtSize(used - limit) })}</MessageBarBody></MessageBar>
        : comesBack ? <MessageBar intent="success" layout="multiline"><MessageBarBody>{t('Mit diesen Werten passen alle Dateien in den lokalen Speicherpool. dmOffice lädt die fehlenden Dateien automatisch nach.')}</MessageBarBody></MessageBar>
        : dirty ? <MessageBar intent="success" layout="multiline"><MessageBarBody>{t('Es sind keine Dateien zu bereinigen.')}</MessageBarBody></MessageBar> : null)}
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
        <Button appearance="primary" disabled={!dirty} onClick={apply}>{t('Übernehmen')}</Button>
        <Button disabled={!dirty} onClick={reset}>{t('Zurücksetzen')}</Button>
        {dirty && <Text size={200} style={muted}>{t('Noch nicht übernommen')}</Text>}
      </div>

      {vol && <>
        <AdminHeading>{t('Belegung')}</AdminHeading>
        {st.mirror && <Text size={200} style={muted}>{t('Die Zahlen unter „Belegung“ und „Dateien“ stammen vom letzten Abgleich, sie können einige Minuten alt sein.')}</Text>}
        <AdminTable id="storage.usage" cols={usageCols} rowKey={r => r.key} rows={[
          { key: 'total', label: t('Gesamt'), bytes: vol.total, strong: true },
          { key: 'reserve', label: t('Immer frei lassen'), bytes: reserve },
          { key: 'other', label: t('Belegt durch sonstige Daten'), bytes: Math.max(0, other - (cache?.thumbBytes ?? 0)), color: BOX_COLORS.foreign },
          ...(cache ? [{ key: 'thumbs', label: t('Belegt durch Vorschaubilder'), bytes: cache.thumbBytes ?? 0, color: BOX_COLORS.foreign }] : []),
          { key: 'files', label: t('Belegt durch Dateien'), bytes: used, color: BOX_COLORS.used, strong: true },
          ...(cache && (cache.waiting ?? 0) > 0 ? [{ key: 'waiting', label: t('davon noch nicht verarbeitet (wird erst danach bereinigt)'), bytes: cache.waiting ?? 0, sub: true }] : []),
          ...(cache?.busy ?? []).map(p => ({ key: p.reason + (p.ext ?? ''), label: `${busyLabel(p)}, ${p.count === 1 ? t('1 Datei') : t('{n} Dateien', { n: p.count.toLocaleString(locale) })}`, bytes: p.bytes, sub: true })),
          { key: 'free', label: t('Noch frei im lokalen Speicherpool'), bytes: Math.max(0, limit - used) },
        ]} />
      </>}
      {s.lowSpace && <MessageBar intent="warning"><MessageBarBody>{t('Auf dem lokalen Speichervolumen ist nur noch der frei zu lassende Speicherplatz verfügbar, es sind {free} frei. Neue Dateien werden abgelehnt, solange sich der lokale Speicherpool nicht weiter bereinigen lässt.', { free: fmtSize(vol?.free ?? 0) })}</MessageBarBody></MessageBar>}

      {st.mirror && <>
        <AdminHeading>{t('Dateien')}</AdminHeading>
        {restoring ? <>
          <Text size={200}>{restoring.missing === 0 ? t('Prüft, was hier fehlt …')
            : t('Holt Dateien zurück: {restored} von {missing} …', { restored: restoreDone.toLocaleString(locale), missing: restoring.missing.toLocaleString(locale) })}</Text>
          {restoring.missing > 0 && <ProgressBar value={Math.min(1, restoreDone / restoring.missing)} thickness="medium" />}
        </> : s.running ? <>
          <Text size={200}>{s.local > 0
            ? t('Gleicht gerade ab: {mirrored} von {local} Dateien im Storage Extender', { mirrored: Math.min(s.mirrored, s.local).toLocaleString(locale), local: s.local.toLocaleString(locale) })
            : t('Gleicht gerade ab …')}{s.uploaded > 0 ? ' · ' + t('{n} hochgeladen', { n: s.uploaded.toLocaleString(locale) }) : ''}</Text>
          <ProgressBar value={s.local > 0 ? Math.min(1, s.mirrored / s.local) : undefined} thickness="medium" />
        </> : !s.lastRun && <Text size={200}>{t('Erster Abgleich startet gleich …')}</Text>}
        {s.cache && <AdminTable id="storage.files" cols={fileCols} rowKey={r => r.key} rows={[
          { key: 'all', label: t('Insgesamt'), count: allCount, bytes: allBytes, strong: true },
          { key: 'both', label: t('Im lokalen Speicherpool und im Storage Extender'), count: cache?.both ?? 0, bytes: cache?.bothBytes ?? 0, sub: true },
          { key: 'away', label: t('Nur im Storage Extender'), count: cache?.remote ?? 0, bytes: onlyThere, sub: true },
          { key: 'pending', label: t('Nur im lokalen Speicherpool, wird noch übertragen'), count: cache?.pendingCount ?? 0, bytes: pendingBytes, sub: true },
        ]} />}
        {s.pending > 0 && <Text size={200}>{t('{n} noch offen', { n: s.pending })}</Text>}
        {s.lastRun && <Text size={200} style={muted}>{t('Zuletzt abgeglichen: {when} · seit dem Start {n} nachgeladen', { when: new Date(s.lastRun).toLocaleString(locale), n: (s.cache?.fetched ?? 0) + (s.cache?.streamed ?? 0) })}</Text>}
        {s.restore && !s.restore.running && s.restore.finished && s.restore.missing > 0 && (
          <Text size={200}>{t('Zurückgeholt: {restored} von {missing}', { restored: s.restore.restored, missing: s.restore.missing })}
            {s.restore.notThere + s.restore.failed > 0 ? ' · ' + t('{n} waren auch dort nicht zu finden oder kaputt', { n: s.restore.notThere + s.restore.failed }) : ''}</Text>
        )}
        {s.lastError && <MessageBar intent="warning"><MessageBarBody>{s.lastError}</MessageBarBody></MessageBar>}
        {s.restore?.error && <MessageBar intent="warning"><MessageBarBody>{s.restore.error}</MessageBarBody></MessageBar>}
      </>}

      <AdminHeading>{t('Storage Extender')}</AdminHeading>
      <MessageBar intent="warning" layout="multiline"><MessageBarBody>
        {t('Für ein eventuelles Backup des Storage Extenders ist der Administrator verantwortlich.')}
        {' '}{withLink(t('Die Dateien der internen Dateiablage können über das interne {link} gesichert werden.'), t('Files-Backup'), () => onOpen('backup'))}
      </MessageBarBody></MessageBar>
      {!ready && <Text size={200} style={muted}>{t('Erst das Ziel speichern und testen.')}</Text>}
      {/* Gesperrt, solange Dateien nur im Storage Extender liegen: bleibt sichtbar eingeschaltet, ein Klick nennt den Grund */}
      <Switch checked={st.mirror} disabled={!st.configured || !ready} style={st.mirror && away > 0 ? { cursor: 'not-allowed' } : undefined}
        onChange={(_, d) => { if (st.mirror && away > 0) notify(t('Storage Extender bleibt eingeschaltet'), t('Einige Dateien liegen nur im Storage Extender. Er lässt sich erst ausschalten, wenn alle Dateien auch im lokalen Speicherpool liegen. Bei genügend Speicherplatz lädt dmOffice sie automatisch nach.'), 'warning'); else save(d.checked) }}
        label={t('Storage Extender verwenden')} />
      {st.mirror && away > 0 && vol && <Text size={200} style={muted}>{t('Zum Ausschalten werden {need} im lokalen Speicherpool gebraucht, verfügbar sind {usable}.', { need: fmtSize(used + onlyThere), usable: fmtSize(limit) })}</Text>}
      {st.mirror && <Text size={200} style={muted}>{away > 0
        ? t('Einige Dateien liegen nur im Storage Extender. Er lässt sich erst ausschalten, wenn alle Dateien auch im lokalen Speicherpool liegen. Bei genügend Speicherplatz lädt dmOffice sie automatisch nach.')
        : t('Alle Dateien liegen auch im lokalen Speicherpool.')}</Text>}
      {s.otherServer && <MessageBar intent="warning" layout="multiline"><MessageBarBody>{t('Am selben Ort speichert noch ein anderer dmOffice-Server (zuletzt {when}), etwa ein Testserver. Damit keiner dem anderen Dateien wegnimmt, löscht dmOffice dort gerade nichts. Gib jedem Server einen eigenen Ordner.', { when: new Date(s.otherServer).toLocaleString() })}<div style={{ marginTop: 6 }}><Button size="small" onClick={forgetOther}>{t('Das war dieser Server vor einem Umzug')}</Button></div></MessageBarBody></MessageBar>}
      {st.rescued && st.rescued.rescued > 0 && <MessageBar intent="info" layout="multiline"><MessageBarBody>{t('Nach der Wiederherstellung wurden {n} Dateien ohne Namen gerettet, die nach dem Core-Backup hochgeladen worden waren. Sie liegen im Ordner „Nach Wiederherstellung gefunden“.', { n: st.rescued.rescued })}</MessageBarBody></MessageBar>}
    </div>
  )
}
