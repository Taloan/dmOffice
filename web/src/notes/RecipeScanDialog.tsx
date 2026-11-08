import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, MessageBar, MessageBarBody,
  Spinner, Switch, Text, Textarea, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { ArrowDown16Regular, ArrowUp16Regular, Delete16Regular, DocumentPdf20Regular, Sparkle16Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { PageTitle, Section } from '../settings/parts'

export interface ScanDraft {
  title: string; yield: string; prepTime: string; cookTime: string; totalTime: string; description: string
  ingredients: string; steps: string; tip: string; source: string
  /** Kalorien je Portion (Zahl); ohne Angabe schlägt der Essensplan das Rezept nicht vor */
  calories?: string
}
interface ScanState { ocr: boolean; reason: string | null; aiAllowed: boolean; ai: boolean }
interface Page { file: File; url: string | null }
interface Crop { x: number; y: number; w: number; h: number }

const useStyles = makeStyles({
  surface: { maxWidth: '1100px', width: '96vw' },
  pages: { display: 'flex', flexWrap: 'wrap', gap: '8px', marginTop: '8px' },
  page: {
    position: 'relative', width: '120px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium,
    padding: '4px', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '2px',
  },
  thumb: { width: '110px', height: '140px', objectFit: 'cover', backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusSmall },
  pdf: { width: '110px', height: '140px', display: 'flex', alignItems: 'center', justifyContent: 'center', backgroundColor: tokens.colorNeutralBackground3 },
  grid: {
    display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1.2fr)', gap: '16px', alignItems: 'start',
    '@media (max-width: 760px)': { gridTemplateColumns: 'minmax(0, 1fr)' },
  },
  preview: { position: 'relative', userSelect: 'none', touchAction: 'none', maxHeight: '70vh', overflow: 'auto' },
  previewImg: { width: '100%', display: 'block' },
  cropBox: { position: 'absolute', border: `2px solid ${tokens.colorBrandStroke1}`, backgroundColor: 'rgba(0, 120, 212, 0.15)', pointerEvents: 'none' },
  fields: { display: 'flex', flexDirection: 'column', gap: '8px', maxHeight: '70vh', overflowY: 'auto', paddingRight: '4px' },
  row: { display: 'flex', gap: '8px', flexWrap: 'wrap', '& > *': { flex: '1 1 120px' } },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  raw: { whiteSpace: 'pre-wrap', fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, maxHeight: '160px', overflowY: 'auto' },
  titleImg: { maxWidth: '160px', maxHeight: '110px', borderRadius: tokens.borderRadiusMedium },
  tabs: { display: 'flex', gap: '4px', flexWrap: 'wrap', marginBottom: '6px' },
})

const isPdf = (f: File) => f.type === 'application/pdf' || f.name.toLowerCase().endsWith('.pdf')

/**
 * Rezept aus Foto (docs/rezept-scan-konzept.md): Seiten hochladen, der Server erkennt Titel, Portionen, Zutaten und Schritte
 * (mehrere Rezepte auf einer Seite getrennt), in der Prüfansicht korrigieren, Titelbild wählen und je Rezept eine Notiz anlegen.
 */
export function RecipeScanDialog({ folder, onClose }: { folder: string | null; onClose: (id: number | null) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [state, setState] = useState<ScanState | null>(null)
  const [pages, setPages] = useState<Page[]>([])
  const [busy, setBusy] = useState(false)
  const [drafts, setDrafts] = useState<ScanDraft[] | null>(null)
  const [images, setImages] = useState<(Blob | null)[]>([])
  const [cur, setCur] = useState(0)
  const [raw, setRaw] = useState('')
  const [hints, setHints] = useState<string[]>([])
  const [usedAi, setUsedAi] = useState(false)
  const [imageUrl, setImageUrl] = useState<string | null>(null)
  const draft = drafts?.[cur] ?? null
  const image = images[cur] ?? null
  const setImage = (b: Blob | null) => setImages(im => { const n = [...im]; n[cur] = b; return n })
  const found = (list: ScanDraft[]) => { setDrafts(list); setImages(list.map(() => null)); setCur(0) }
  const [shown, setShown] = useState(0)
  const [cropping, setCropping] = useState(false)
  const fileInput = useRef<HTMLInputElement>(null)
  const imageInput = useRef<HTMLInputElement>(null)

  useEffect(() => { api.get<ScanState>('/api/notes/recipes/scan/state').then(setState).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => () => pages.forEach(p => p.url && URL.revokeObjectURL(p.url)), []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => {
    if (!image) { setImageUrl(null); return }
    const u = URL.createObjectURL(image)
    setImageUrl(u)
    return () => URL.revokeObjectURL(u)
  }, [image])

  const add = (files: FileList | null) => {
    if (!files) return
    const list = Array.from(files).filter(f => f.type.startsWith('image/') || isPdf(f))
    setPages(p => [...p, ...list.map(file => ({ file, url: isPdf(file) ? null : URL.createObjectURL(file) }))].slice(0, 10))
  }
  const move = (i: number, d: number) => setPages(p => {
    const n = [...p]; const j = i + d
    if (j < 0 || j >= n.length) return p
    ;[n[i], n[j]] = [n[j], n[i]]
    return n
  })
  const remove = (i: number) => setPages(p => { if (p[i].url) URL.revokeObjectURL(p[i].url!); return p.filter((_, k) => k !== i) })

  const recognize = async () => {
    setBusy(true)
    try {
      const form = new FormData()
      pages.forEach(p => form.append('page', p.file, p.file.name))
      const r = await api.form<{ drafts: ScanDraft[]; text: string; hints: string[]; ai: boolean }>('/api/notes/recipes/scan', form)
      found(r.drafts); setRaw(r.text); setHints(r.hints); setUsedAi(r.ai)
      setShown(Math.max(0, pages.findIndex(p => p.url)))
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const withAi = async () => {
    setBusy(true)
    try {
      // Seitenbilder und der erkannte Text gehen an die KI (PDF-Seiten nur als Text)
      const form = new FormData()
      form.append('text', raw)
      pages.filter(p => p.url).forEach(p => form.append('page', p.file, p.file.name))
      const r = await api.form<{ drafts: ScanDraft[] }>('/api/notes/recipes/scan/ai', form)
      const source = draft?.source ?? ''
      found(r.drafts.map(d => ({ ...d, source }))); setUsedAi(true); setHints(h => h.filter(x => x.startsWith('Seite')))
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  // Je Rezept eine Notiz; schlägt eins fehl, bleiben die übrigen im Dialog
  const save = async () => {
    if (!drafts) return
    setBusy(true)
    let last: number | null = null
    let i = 0
    try {
      for (; i < drafts.length; i++) {
        const form = new FormData()
        form.append('draft', JSON.stringify(drafts[i]))
        if (folder) form.append('folder', folder)
        if (images[i]) form.append('image', images[i]!, 'titelbild.jpg')
        last = (await api.form<{ id: number }>('/api/notes/recipes/scan/save', form)).id
      }
      onClose(last)
    } catch (e) {
      fail(e)
      setDrafts(drafts.slice(i)); setImages(images.slice(i)); setCur(0)
    } finally { setBusy(false) }
  }
  const drop = () => {
    if (!drafts || drafts.length < 2) return
    setDrafts(drafts.filter((_, k) => k !== cur)); setImages(images.filter((_, k) => k !== cur)); setCur(0)
  }
  const set = (k: keyof ScanDraft) => (_: unknown, d: { value: string }) => setDrafts(ds => ds && ds.map((x, i) => i === cur ? { ...x, [k]: d.value } : x))

  const imagePages = pages.map((p, i) => ({ ...p, i })).filter(p => p.url)

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && !busy && onClose(null)}>
      <DialogSurface className={s.surface}>
        <DialogBody>
          <DialogTitle>{t('Rezept aus Foto')}</DialogTitle>
          <DialogContent>
            {state && !state.ocr && <MessageBar intent="warning"><MessageBarBody>{state.reason ?? t('Texterkennung ist auf diesem Server nicht verfügbar.')}</MessageBarBody></MessageBar>}
            {!draft && <>
              <Text>{t('Fotos oder Scans der Kochbuchseiten hinzufügen, bis zu 10 Seiten in der Reihenfolge des Rezepts.')}</Text>
              <input ref={fileInput} type="file" accept="image/*,application/pdf" multiple hidden onChange={e => { add(e.target.files); e.target.value = '' }} />
              <div className={s.pages}>
                {pages.map((p, i) => (
                  <div key={i} className={s.page}>
                    {p.url ? <img className={s.thumb} src={p.url} alt="" /> : <div className={s.pdf}><DocumentPdf20Regular /></div>}
                    <Text size={200}>{t('Seite {n}', { n: i + 1 })}</Text>
                    <div>
                      <Button size="small" appearance="subtle" icon={<ArrowUp16Regular />} aria-label={t('Nach vorn')} disabled={i === 0} onClick={() => move(i, -1)} />
                      <Button size="small" appearance="subtle" icon={<ArrowDown16Regular />} aria-label={t('Nach hinten')} disabled={i === pages.length - 1} onClick={() => move(i, 1)} />
                      <Button size="small" appearance="subtle" icon={<Delete16Regular />} aria-label={t('Entfernen')} onClick={() => remove(i)} />
                    </div>
                  </div>
                ))}
              </div>
              <div style={{ marginTop: 8 }}>
                <Button disabled={pages.length >= 10 || busy} onClick={() => fileInput.current?.click()}>{t('Seiten hinzufügen')}</Button>
              </div>
            </>}
            {draft && <div className={s.grid}>
              <div>
                {imagePages.length > 1 && <div className={s.tabs}>
                  {imagePages.map(p => <Button key={p.i} size="small" appearance={p.i === shown ? 'primary' : 'secondary'} onClick={() => setShown(p.i)}>{t('Seite {n}', { n: p.i + 1 })}</Button>)}
                </div>}
                {pages[shown]?.url
                  ? <CropImage src={pages[shown].url!} active={cropping} onCrop={b => { setImage(b); setCropping(false) }} />
                  : <Text className={s.muted}>{t('Keine Vorschau für PDF-Seiten.')}</Text>}
                <Field label={t('Erkannter Text')} style={{ marginTop: 8 }}><div className={s.raw}>{raw}</div></Field>
              </div>
              <div className={s.fields}>
                {hints.map(h => <MessageBar key={h} intent="warning"><MessageBarBody>{h}</MessageBarBody></MessageBar>)}
                {drafts!.length > 1 && <div className={s.tabs}>
                  {drafts!.map((d, i) => <Button key={i} size="small" appearance={i === cur ? 'primary' : 'secondary'} onClick={() => setCur(i)}>
                    {d.title || t('Rezept {n}', { n: i + 1 })}</Button>)}
                  <Button size="small" appearance="subtle" icon={<Delete16Regular />} onClick={drop}>{t('Dieses Rezept entfernen')}</Button>
                </div>}
                {usedAi && <Text className={s.muted}>{t('Von der KI gelesen.')}</Text>}
                {state?.aiAllowed && !usedAi && raw && <div>
                  <Button size="small" icon={<Sparkle16Regular />} disabled={busy} onClick={withAi}>{t('Mit KI erkennen')}</Button>
                </div>}
                <Field label={t('Titel')}><Input value={draft.title} onChange={set('title')} /></Field>
                <div className={s.row}>
                  <Field label={t('Portionen')}><Input value={draft.yield} onChange={set('yield')} placeholder={t('z. B. 4 Portionen')} /></Field>
                  <Field label={t('Kalorien je Portion')} hint={t('Der Essensplan schlägt nur Rezepte mit dieser Angabe vor.')}>
                    <Input value={draft.calories ?? ''} onChange={(_, d) => set('calories')(_, { value: d.value.replace(/\D/g, '').slice(0, 4) })} inputMode="numeric" contentAfter="kcal" />
                  </Field>
                  <Field label={t('Arbeitszeit')}><Input value={draft.prepTime} onChange={set('prepTime')} /></Field>
                  <Field label={t('Koch-/Backzeit')}><Input value={draft.cookTime} onChange={set('cookTime')} /></Field>
                  <Field label={t('Gesamtzeit')}><Input value={draft.totalTime} onChange={set('totalTime')} /></Field>
                </div>
                <Field label={t('Beschreibung')}><Textarea value={draft.description} onChange={set('description')} resize="vertical" /></Field>
                <Field label={t('Zutaten')} hint={t('Eine Zeile je Zutat. Eine Zeile mit Doppelpunkt am Ende beginnt einen Unterabschnitt, z. B. „Für den Teig:“.')}>
                  <Textarea value={draft.ingredients} onChange={set('ingredients')} rows={8} resize="vertical" />
                </Field>
                <Field label={t('Zubereitung')} hint={t('Eine Zeile je Schritt.')}>
                  <Textarea value={draft.steps} onChange={set('steps')} rows={8} resize="vertical" />
                </Field>
                <Field label={t('Tipp')}><Textarea value={draft.tip} onChange={set('tip')} resize="vertical" /></Field>
                <Field label={t('Quelle')}><Input value={draft.source} onChange={set('source')} placeholder={t('z. B. Buchtitel, S. 45')} /></Field>
                <Field label={t('Titelbild')}>
                  <input ref={imageInput} type="file" accept="image/*" hidden onChange={e => { if (e.target.files?.[0]) setImage(e.target.files[0]); e.target.value = '' }} />
                  {imageUrl && <img className={s.titleImg} src={imageUrl} alt="" />}
                  <div className={s.tabs}>
                    <Button size="small" onClick={() => imageInput.current?.click()}>{t('Bild wählen')}</Button>
                    {pages[shown]?.url && <Button size="small" appearance={cropping ? 'primary' : 'secondary'} onClick={() => setCropping(c => !c)}>
                      {cropping ? t('Rechteck auf der Seite aufziehen') : t('Ausschnitt einer Seite')}</Button>}
                    {image && <Button size="small" appearance="subtle" onClick={() => setImage(null)}>{t('Ohne Titelbild')}</Button>}
                  </div>
                </Field>
              </div>
            </div>}
          </DialogContent>
          <DialogActions>
            {busy && <Spinner size="tiny" />}
            {draft && <Button disabled={busy} onClick={() => setDrafts(null)}>{t('Zurück')}</Button>}
            <Button disabled={busy} onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            {!draft
              ? <Button appearance="primary" disabled={busy || pages.length === 0 || !state?.ocr} onClick={recognize}>{busy ? t('Erkennt …') : t('Erkennen')}</Button>
              : <Button appearance="primary" disabled={busy} onClick={save}>{drafts!.length > 1 ? t('{n} Rezepte anlegen', { n: drafts!.length }) : t('Rezept anlegen')}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Seitenbild; im Ausschnitt-Modus ein Rechteck aufziehen - der Ausschnitt kommt als JPEG zurück. */
function CropImage({ src, active, onCrop }: { src: string; active: boolean; onCrop: (b: Blob) => void }) {
  const s = useStyles()
  const img = useRef<HTMLImageElement>(null)
  const [start, setStart] = useState<{ x: number; y: number } | null>(null)
  const [box, setBox] = useState<Crop | null>(null)
  useEffect(() => { if (!active) { setBox(null); setStart(null) } }, [active])
  const pos = (e: React.PointerEvent) => {
    const r = img.current!.getBoundingClientRect()
    return { x: Math.min(Math.max(e.clientX - r.left, 0), r.width), y: Math.min(Math.max(e.clientY - r.top, 0), r.height) }
  }
  const down = (e: React.PointerEvent) => {
    if (!active) return
    try { (e.target as Element).setPointerCapture(e.pointerId) } catch { /* Zeiger schon weg */ }
    const p = pos(e); setStart(p); setBox({ x: p.x, y: p.y, w: 0, h: 0 })
  }
  const moveTo = (e: React.PointerEvent) => {
    if (!active || !start) return
    const p = pos(e)
    setBox({ x: Math.min(p.x, start.x), y: Math.min(p.y, start.y), w: Math.abs(p.x - start.x), h: Math.abs(p.y - start.y) })
  }
  const up = () => {
    if (!active || !start || !box) return
    setStart(null)
    const el = img.current!
    if (box.w < 20 || box.h < 20) { setBox(null); return }
    const k = el.naturalWidth / el.clientWidth
    const canvas = document.createElement('canvas')
    canvas.width = Math.round(box.w * k); canvas.height = Math.round(box.h * k)
    canvas.getContext('2d')!.drawImage(el, box.x * k, box.y * k, box.w * k, box.h * k, 0, 0, canvas.width, canvas.height)
    canvas.toBlob(b => { if (b) onCrop(b) }, 'image/jpeg', 0.9)
  }
  return (
    <div className={mergeClasses(s.preview)} style={{ cursor: active ? 'crosshair' : 'default' }} onPointerDown={down} onPointerMove={moveTo} onPointerUp={up}>
      <img ref={img} className={s.previewImg} src={src} alt="" draggable={false} />
      {box && <div className={s.cropBox} style={{ left: box.x, top: box.y, width: box.w, height: box.h }} />}
    </div>
  )
}

/** Einstellungen → Notizen → Rezept-Scan: KI für schwierige Seiten (nur mit freigegebener KI). */
export function RecipeScanSettings() {
  const { fail } = useApp()
  const [state, setState] = useState<ScanState | null>(null)
  useEffect(() => { api.get<ScanState>('/api/notes/recipes/scan/state').then(setState).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const toggle = async (ai: boolean) => {
    try { await api.put('/api/notes/recipes/scan/settings', { ai }); setState(x => x && { ...x, ai }) } catch (e) { fail(e) }
  }
  return (
    <>
      <PageTitle>{t('Rezept-Scan')}</PageTitle>
      <Section title={t('Texterkennung')} hint={t('„Rezept aus Foto“ in den Notizen liest Kochbuchseiten auf diesem Server und macht daraus eine Rezept-Notiz.')}>
        {state == null ? <Spinner size="tiny" /> : <Text>{state.ocr ? t('Texterkennung ist verfügbar.') : state.reason ?? t('Texterkennung ist auf diesem Server nicht verfügbar.')}</Text>}
      </Section>
      <Section last title={t('KI')} hint={t('Findet die Erkennung keinen Titel, keine Zutaten oder keine Zubereitung, liest die eingerichtete KI die Seiten: die Fotos und den erkannten Text.')}>
        {state && (state.aiAllowed
          ? <Switch label={t('Schwierige Seiten mit KI erkennen')} checked={state.ai} onChange={(_, d) => toggle(d.checked)} />
          : <Text>{t('KI ist für dieses Konto nicht freigegeben.')}</Text>)}
      </Section>
    </>
  )
}
