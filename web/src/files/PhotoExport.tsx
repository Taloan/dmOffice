import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Radio, RadioGroup,
  SpinButton, Switch, Tab, TabList, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { Delete20Regular, Folder20Regular } from '@fluentui/react-icons'
import { Suspense, lazy, useEffect, useRef, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { PatternField } from './ImportProfiles'

const FolderPicker = lazy(() => import('./FilesView').then(m => ({ default: m.FolderPicker })))

/** Einstellungen eines Exports (wie auf dem Server, PhotoExport.Options). */
export interface ExportOptions {
  format: 'jpg' | 'png'
  quality: number
  size: 'original' | 'long' | 'print'
  longEdge: number
  widthCm: number
  heightCm: number
  dpi: number
  autoOrient: boolean
  edited: boolean
  removeGps: boolean
  folder: string
  /** Dateinamen-Muster ohne Endung. */
  pattern: string
  /** Ordnerstruktur-Muster (Unterordner im Zielordner); leer = direkt in den Zielordner. Fehlt in alten Vorgaben. */
  folders?: string
  title?: string
  description?: string
}
interface Preset { name: string; options: ExportOptions }
/** Platzhalter wie bei den Importprofilen. */
interface Settings { presets: Preset[]; last: ExportOptions; defaults: ExportOptions; folderTokens: string[]; nameTokens: string[]; textTokens: string[] }
interface Check {
  count: number; missing: string[]; preview: string | null; unknown: string[]; small: number; need: number; free: number | null; enough: boolean
  raw: number; engine: boolean; folder: string; folderError: string | null
  /** Muster ungültig (Meldung wie bei den Importprofilen). */
  patternError: string | null
  /** Vorschau je Muster mit dem ersten Foto. */
  folders: string | null; name: string | null; title: string | null; description: string | null
}

/** Gängige Druckformate (cm, quer) zum Übernehmen. */
const PAPER: { name: string; w: number; h: number }[] = [
  { name: '10 × 15', w: 15, h: 10 }, { name: '13 × 18', w: 18, h: 13 }, { name: '20 × 30', w: 30, h: 20 }, { name: '30 × 45', w: 45, h: 30 },
  { name: 'A5', w: 21, h: 14.8 }, { name: 'A4', w: 29.7, h: 21 }, { name: 'A3', w: 42, h: 29.7 }, { name: 'A2', w: 59.4, h: 42 },
]

const px = (cm: number, dpi: number) => Math.max(1, Math.round(cm / 2.54 * dpi))
const num = (v: string) => { const x = parseFloat(v.replace(',', '.')); return Number.isFinite(x) ? x : 0 }

const useStyles = makeStyles({
  content: { display: 'flex', flexDirection: 'column', gap: '12px', minHeight: '440px' },
  // Alle Reiter gleich hoch (der höchste: Titel und Beschreibung) - der Dialog springt beim Wechseln nicht
  tab: { display: 'flex', flexDirection: 'column', gap: '12px', minHeight: '432px' },
  line: { display: 'flex', alignItems: 'flex-end', gap: '8px', flexWrap: 'wrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  // Fester Platz für Hinweise: nichts verschiebt sich, wenn einer dazukommt
  status: { minHeight: '64px', display: 'flex', flexDirection: 'column', gap: '2px', borderTop: `1px solid ${tokens.colorNeutralStroke2}`, paddingTop: '8px' },
  warn: { color: tokens.colorPaletteDarkOrangeForeground1 },
  err: { color: tokens.colorPaletteRedForeground1 },
  preset: { display: 'flex', alignItems: 'center', gap: '8px', padding: '4px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  presetName: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  list: { maxHeight: '220px', overflowY: 'auto' },
})

/**
 * Markierte Fotos exportieren (photoexport): Format, Größe (auch Druckgröße in cm mit dpi), Bearbeitung, Ortsangaben, Zielordner in
 * Meine Dateien. Ablage wie bei den Importprofilen (PatternField, dieselben Platzhalter): Ordnerstruktur-Muster, Dateinamen-Muster,
 * Titel und Beschreibung. Vorgaben je Person. Der Export läuft im Hintergrund (Aktivität „Fotos exportieren“).
 * ids: in der Reihenfolge der Auswahl ({sequence}).
 */
export function PhotoExportDialog({ ids, onClose }: { ids: string[]; onClose: () => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [settings, setSettings] = useState<Settings | null>(null)
  const [o, setO] = useState<ExportOptions | null>(null)
  const [tab, setTab] = useState<'format' | 'size' | 'target' | 'text' | 'presets'>('format')
  const [check, setCheck] = useState<Check | null>(null)
  const [picking, setPicking] = useState(false)
  const [presetName, setPresetName] = useState('')
  const [filter, setFilter] = useState('')
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    api.get<Settings>('/api/gallery/export/settings').then(x => { setSettings(x); setO({ ...x.defaults, ...x.last }) }).catch(fail)
  }, [fail])

  // Vorab prüfen (kurz verzögert): erster Dateiname, zu kleine Fotos, Platz
  const seq = useRef(0)
  useEffect(() => {
    if (!o) return
    const n = ++seq.current
    const timer = setTimeout(() => {
      api.post<Check>('/api/gallery/export/check', { ids, options: o, lang })
        .then(c => { if (n === seq.current) setCheck(c) })
        .catch(() => { /* Hinweise bleiben stehen */ })
    }, 300)
    return () => clearTimeout(timer)
  }, [o, ids])

  if (!o || !settings) return null
  const set = (patch: Partial<ExportOptions>) => setO(x => x && { ...x, ...patch })

  const start = async () => {
    setBusy(true)
    try {
      const r = await api.post<{ count: number; folder: string }>('/api/gallery/export', { ids, options: o, lang })
      notify(t('{n} Fotos werden nach „{folder}“ exportiert – Fortschritt in der Aktivitätsanzeige.', { n: r.count, folder: r.folder }))
      window.dispatchEvent(new Event('dmoffice:activity'))
      onClose()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  const savePreset = async () => {
    try {
      const x = await api.put<Settings>('/api/gallery/export/presets', { name: presetName.trim(), options: o })
      setSettings(x)
      notify(t('Vorgabe „{name}“ gespeichert.', { name: presetName.trim() }))
      setPresetName('')
    } catch (e) { fail(e) }
  }
  const removePreset = async (name: string) => {
    try { setSettings(await api.del<Settings>('/api/gallery/export/presets' + q({ name }))) } catch (e) { fail(e) }
  }

  const printPx = o.size === 'print' ? `${px(o.widthCm, o.dpi)} × ${px(o.heightCm, o.dpi)} px` : ''
  const presets = settings.presets.filter(p => !filter || p.name.toLowerCase().includes(filter.toLowerCase()))
  const blocked = !check || check.count === 0 || check.unknown.length > 0 || !!check.patternError || !!check.folderError || !check.enough
  // Vorschau je Muster; bei einem Fehler im Muster steht er unten im Hinweisbereich
  const shown = (v: string | null | undefined) => check?.patternError ? '' : v ?? ''

  return (<>
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>{t('Fotos exportieren')}</DialogTitle>
          <DialogContent className={s.content}>
            <TabList selectedValue={tab} onTabSelect={(_, d) => setTab(d.value as typeof tab)}>
              <Tab value="format">{t('photoexport|Format')}</Tab>
              <Tab value="size">{t('Größe')}</Tab>
              <Tab value="target">{t('Ziel')}</Tab>
              <Tab value="text">{t('Titel und Beschreibung')}</Tab>
              <Tab value="presets">{t('Vorgaben')}</Tab>
            </TabList>

            {tab === 'format' && <div className={s.tab}>
              <Field label={t('Dateiformat')}>
                <RadioGroup layout="horizontal" value={o.format} onChange={(_, d) => set({ format: d.value as ExportOptions['format'] })}>
                  <Radio value="jpg" label="JPG" />
                  <Radio value="png" label="PNG" />
                </RadioGroup>
              </Field>
              <Field label={t('JPG-Qualität (10–100)')}>
                <SpinButton style={{ maxWidth: 120 }} min={10} max={100} value={o.quality} disabled={o.format !== 'jpg'}
                  onChange={(_, d) => set({ quality: Math.max(10, Math.min(100, d.value ?? (parseInt(d.displayValue ?? '') || o.quality))) })} />
              </Field>
              <Field label={t('Bearbeitung')}>
                <RadioGroup value={o.edited ? 'edited' : 'original'} onChange={(_, d) => set({ edited: d.value === 'edited' })}>
                  <Radio value="edited" label={t('Bearbeitet (Drehen, Ausschnitt, Regler)')} />
                  <Radio value="original" label={t('Original ohne Bearbeitung')} />
                </RadioGroup>
              </Field>
              <Switch checked={o.removeGps} label={t('Ortsangaben (GPS) entfernen')} onChange={(_, d) => set({ removeGps: d.checked })} />
              <Text size={200} className={s.muted}>{t('Aufnahmedatum, Kamera und Objektiv bleiben erhalten.')}</Text>
            </div>}

            {tab === 'size' && <div className={s.tab}>
              <RadioGroup value={o.size} onChange={(_, d) => set({ size: d.value as ExportOptions['size'] })}>
                <Radio value="original" label={t('Originalgröße')} />
                <Radio value="long" label={t('Lange Kante in Pixeln')} />
                <Radio value="print" label={t('Druckgröße (cm und dpi)')} />
              </RadioGroup>
              {o.size === 'long' && <Field label={t('Lange Kante (px)')}>
                <SpinButton style={{ maxWidth: 140 }} min={16} max={30000} step={100} value={o.longEdge}
                  onChange={(_, d) => set({ longEdge: Math.max(16, d.value ?? (parseInt(d.displayValue ?? '') || o.longEdge)) })} />
              </Field>}
              {o.size === 'print' && <>
                <div className={s.line}>
                  <Field label={t('Papierformat')}>
                    <Dropdown style={{ minWidth: 180 }} placeholder={t('Papierformat wählen')} value=""
                      onOptionSelect={(_, d) => { const p = PAPER.find(x => x.name === d.optionValue); if (p) set({ widthCm: p.w, heightCm: p.h }) }}>
                      {PAPER.map(p => <Option key={p.name} value={p.name}>{p.name}</Option>)}
                    </Dropdown>
                  </Field>
                  <Field label={t('Breite (cm)')}>
                    <Input style={{ width: 90 }} inputMode="decimal" defaultValue={String(o.widthCm).replace('.', lang === 'de' ? ',' : '.')} key={'w' + o.widthCm}
                      onBlur={e => { const v = num(e.target.value); if (v > 0) set({ widthCm: v }) }} />
                  </Field>
                  <Field label={t('Höhe (cm)')}>
                    <Input style={{ width: 90 }} inputMode="decimal" defaultValue={String(o.heightCm).replace('.', lang === 'de' ? ',' : '.')} key={'h' + o.heightCm}
                      onBlur={e => { const v = num(e.target.value); if (v > 0) set({ heightCm: v }) }} />
                  </Field>
                </div>
                <Switch checked={o.autoOrient} label={t('Hoch- und Querformat dem Foto anpassen')} onChange={(_, d) => set({ autoOrient: d.checked })} />
                <Text size={200} className={s.muted}>{t('Das Foto wird eingepasst: nichts wird abgeschnitten, nichts vergrößert.')}</Text>
              </>}
              <Field label={t('Auflösung (dpi)')} hint={o.size === 'print' ? t('Ergibt höchstens {px}', { px: printPx }) : t('Wird in die Datei geschrieben (für Druck und Layout).')}>
                <SpinButton style={{ maxWidth: 120 }} min={30} max={2400} step={50} value={o.dpi}
                  onChange={(_, d) => set({ dpi: Math.max(30, Math.min(2400, d.value ?? (parseInt(d.displayValue ?? '') || o.dpi))) })} />
              </Field>
            </div>}

            {tab === 'target' && <div className={s.tab}>
              <Field label={t('Zielordner in Meine Dateien')}>
                <div style={{ display: 'flex', gap: 6, maxWidth: 420 }}>
                  <Input value={o.folder} onChange={(_, d) => set({ folder: d.value })} style={{ flex: 1 }} />
                  <Button icon={<Folder20Regular />} onClick={() => setPicking(true)} aria-label={t('Ordner wählen')} title={t('Ordner wählen')} />
                </div>
              </Field>
              <PatternField label={t('Ordnerstruktur-Muster')} value={o.folders ?? ''} onChange={v => set({ folders: v })} tokens={settings.folderTokens}
                preview={(o.folders ?? '').trim() ? shown(check?.folders) : t('Leer: alle Dateien direkt in den Zielordner.')} />
              <PatternField label={t('Dateinamen-Muster (ohne Dateiendung)')} value={o.pattern} onChange={v => set({ pattern: v })} tokens={settings.nameTokens}
                preview={/[\\/]/.test(o.pattern) ? t('Unterordner gehören ins Ordnerstruktur-Muster.') : shown(check?.name)} />
              <Text size={200} className={s.muted}>{t('{sequence} zählt in der Reihenfolge der Auswahl. Ist ein Name schon belegt, kommt „ (2)“ dazu – nichts wird überschrieben.')}</Text>
            </div>}

            {tab === 'text' && <div className={s.tab}>
              <PatternField label={t('Titel (Platzhalter wie beim Dateinamen)')} value={o.title ?? ''} onChange={v => set({ title: v })} tokens={settings.textTokens}
                preview={shown(check?.title)} />
              <PatternField label={t('Beschreibung (Platzhalter wie beim Dateinamen)')} value={o.description ?? ''} onChange={v => set({ description: v })}
                tokens={settings.textTokens} preview={shown(check?.description)} multiline />
              <Text size={200} className={s.muted}>{t('Titel und Beschreibung stehen in den exportierten Dateien (XMP, bei JPG und PNG). Leer: es wird nichts geschrieben.')}</Text>
            </div>}

            {tab === 'presets' && <div className={s.tab}>
              <div className={s.line}>
                <Field label={t('Aktuelle Einstellungen als Vorgabe speichern')} style={{ flex: 1 }}>
                  <Input value={presetName} placeholder={t('z. B. Fotokalender A3')} onChange={(_, d) => setPresetName(d.value)} />
                </Field>
                <Button disabled={!presetName.trim()} onClick={savePreset}>{t('Speichern')}</Button>
              </div>
              {settings.presets.length > 15 && <Input placeholder={t('Vorgaben filtern')} value={filter} onChange={(_, d) => setFilter(d.value)} />}
              <div className={s.list}>
                {presets.map(p => <div key={p.name} className={s.preset}>
                  <span className={s.presetName}>{p.name}</span>
                  <Button size="small" onClick={() => { setO({ ...settings.defaults, ...p.options }); setTab('format'); notify(t('Vorgabe „{name}“ übernommen.', { name: p.name })) }}>{t('Übernehmen')}</Button>
                  <Button size="small" appearance="subtle" icon={<Delete20Regular />} aria-label={t('Löschen')} title={t('Löschen')} onClick={() => removePreset(p.name)} />
                </div>)}
                {settings.presets.length === 0 && <Text className={s.muted}>{t('Noch keine Vorgaben.')}</Text>}
              </div>
            </div>}

            <div className={s.status}>
              {check ? <>
                <Text size={200}>{t('{n} Fotos · Ziel: {folder} · etwa {size}', { n: check.count, folder: check.folder, size: fmtSize(check.need) })}
                  {check.preview ? ' · ' + t('erste Datei: {name}', { name: check.preview }) : ''}</Text>
                {check.small > 0 && <Text size={200} className={s.warn}>{t('{n} Fotos haben weniger Pixel als gewünscht – sie werden nicht vergrößert.', { n: check.small })}</Text>}
                {check.raw > 0 && !check.engine && <Text size={200} className={s.warn}>{t('{n} RAW-Fotos lassen sich ohne Photo-Engine nicht entwickeln.', { n: check.raw })}</Text>}
                {check.missing.length > 0 && <Text size={200} className={s.warn}>{t('{n} Fotos gibt es nicht mehr oder lassen sich nicht exportieren.', { n: check.missing.length })}</Text>}
                {!check.enough && <Text size={200} className={s.err}>{t('Dafür reicht der freie Speicherplatz voraussichtlich nicht ({free} frei).', { free: fmtSize(check.free ?? 0) })}</Text>}
                {check.folderError && <Text size={200} className={s.err}>{t(check.folderError)}</Text>}
                {check.unknown.length > 0
                  ? <Text size={200} className={s.err}>{t('Unbekannter Platzhalter: {list}', { list: check.unknown.map(x => `{${x}}`).join(', ') })}</Text>
                  : check.patternError && <Text size={200} className={s.err}>{t(check.patternError)}</Text>}
              </> : <Text size={200} className={s.muted}>{t('Wird geprüft …')}</Text>}
            </div>
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={busy || blocked} onClick={start}>{t('Starten')}</Button>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
      {picking && <Suspense fallback={null}>
        <FolderPicker title={t('Zielordner')} exclude={[]} start={o.folder.replace(/^\/+|\/+$/g, '')} onClose={() => setPicking(false)}
          onPick={path => {
            setPicking(false)
            if (path.startsWith('tresor:')) { fail(new Error(t('Der Tresor kann nicht Zielordner sein.'))); return }
            set({ folder: path.replace(/^\/+|\/+$/g, '') })
          }} />
      </Suspense>}
  </>)
}
