// Betrachter im Vollbild: Bilder als Lightbox (vor/zurueck mit Pfeilen, Tasten und Wischen), PDF eingebettet,
// Text und Code lesbar (und bearbeitbar), Markdown dargestellt, Audio und Video abspielbar.
import { Button, Spinner, Text, Tooltip, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import {
  ArrowDownload20Regular, ChevronLeft24Regular, ChevronRight24Regular, Dismiss24Regular, Edit20Regular, Info20Regular, Open20Regular,
  Save20Regular, ZoomIn20Regular, ZoomOut20Regular,
} from '@fluentui/react-icons'
import { useCallback, useEffect, useRef, useState } from 'react'
import { fmtSize } from '../api'
import { locale, t } from '../i18n'
import { Item, KindIcon, Source, Thumb, extOf, fmtWhen, viewKind } from './common'
import { Markdown } from './Markdown'

const useStyles = makeStyles({
  root: {
    position: 'fixed', inset: 0, zIndex: 1000, display: 'flex', flexDirection: 'column', backgroundColor: '#141414', color: '#fff',
  },
  light: { backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground1 },
  bar: {
    display: 'flex', alignItems: 'center', gap: '4px', padding: '6px 8px', minHeight: '44px', flexShrink: 0,
    paddingTop: 'calc(6px + env(safe-area-inset-top, 0px))',
  },
  title: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', paddingLeft: '8px' },
  name: { fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  sub: { fontSize: '12px', opacity: 0.75, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  btn: { color: 'inherit', minWidth: '36px' },
  stage: { flex: 1, minHeight: 0, position: 'relative', display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden' },
  img: { maxWidth: '100%', maxHeight: '100%', objectFit: 'contain', userSelect: 'none', cursor: 'zoom-in' },
  zoomed: { overflow: 'auto', alignItems: 'flex-start', justifyContent: 'flex-start' },
  imgFull: { maxWidth: 'none', maxHeight: 'none', cursor: 'zoom-out', margin: 'auto' },
  nav: {
    position: 'absolute', top: '50%', transform: 'translateY(-50%)', zIndex: 2, width: '44px', height: '64px', borderRadius: '8px',
    backgroundColor: 'rgba(0,0,0,.35)', color: '#fff', border: 'none', cursor: 'pointer', display: 'flex', alignItems: 'center', justifyContent: 'center',
    ':hover': { backgroundColor: 'rgba(0,0,0,.6)' },
    '@media (max-width: 767px)': { display: 'none' },
  },
  frame: { width: '100%', height: '100%', border: 'none', backgroundColor: '#fff' },
  doc: {
    width: '100%', height: '100%', overflow: 'auto', padding: '16px 24px', boxSizing: 'border-box', backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1, '@media (max-width: 767px)': { padding: '12px' },
  },
  pre: { margin: 0, fontFamily: 'Consolas, "Cascadia Mono", monospace', fontSize: '13px', whiteSpace: 'pre-wrap', wordBreak: 'break-word', tabSize: 4 },
  editor: {
    width: '100%', height: '100%', border: 'none', outline: 'none', resize: 'none', padding: '16px 24px', boxSizing: 'border-box',
    fontFamily: 'Consolas, "Cascadia Mono", monospace', fontSize: '13px', backgroundColor: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1,
  },
  none: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px', textAlign: 'center', padding: '24px' },
  media: { maxWidth: '100%', maxHeight: '100%' },
})

/** Text bis zu dieser Groesse laden (Rest abgeschnitten), bearbeiten nur darunter. */
const TEXT_LIMIT = 2 * 1024 * 1024

export function Viewer({ items, index, source, onIndex, onClose, onDownload, onDetails, onSave, startEdit }: {
  items: Item[]; index: number; source: Source; onIndex: (i: number) => void; onClose: () => void
  /** Neue Textdatei: gleich im Bearbeiten-Modus */
  startEdit?: boolean
  onDownload: (i: Item) => void; onDetails?: (i: Item) => void
  /** Text speichern (nur angemeldet); wirft bei Konflikt ApiError 409 */
  onSave?: (i: Item, text: string, force: boolean) => Promise<Item>
}) {
  const s = useStyles()
  const item = items[index]
  const kind = item ? viewKind(item) : null
  const [zoom, setZoom] = useState(false)
  const [original, setOriginal] = useState(false)
  const [imgFailed, setImgFailed] = useState(0)
  const [text, setText] = useState<{ body: string; cut: boolean } | null>(null)
  const [edit, setEdit] = useState<string | null>(null)
  // Speichern liest das Feld selbst - der Zustand kann beim schnellen Tippen und Strg+S noch hinterherhinken
  const area = useRef<HTMLTextAreaElement>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const touch = useRef<{ x: number; y: number } | null>(null)
  const mobile = typeof matchMedia === 'function' && matchMedia('(max-width: 767px)').matches

  const go = useCallback((d: number) => {
    if (edit !== null) return
    const n = index + d
    if (n >= 0 && n < items.length) onIndex(n)
  }, [index, items.length, onIndex, edit])

  useEffect(() => { setZoom(false); setOriginal(extOf(item?.name ?? '') === '.gif'); setImgFailed(0); setText(null); setEdit(null); setError(null) }, [item?.path]) // eslint-disable-line react-hooks/exhaustive-deps

  // Text laden: nur den Anfang grosser Dateien (Bereichsanfrage)
  useEffect(() => {
    if (!item || (kind !== 'text' && kind !== 'markdown')) return
    let gone = false
    fetch(source.download(item), { credentials: 'same-origin', cache: 'no-store', headers: { Range: `bytes=0-${TEXT_LIMIT - 1}` } })
      .then(async r => {
        if (!r.ok) throw new Error(`${r.status}`)
        const buf = await r.arrayBuffer()
        if (gone) return
        const body = new TextDecoder('utf-8').decode(buf)
        setText({ body, cut: item.size > TEXT_LIMIT })
        if (startEdit && onSave && item.size <= TEXT_LIMIT) setEdit(body)
      })
      .catch(e => { if (!gone) setError(String(e.message ?? e)) })
    return () => { gone = true }
  // Nur beim Wechsel der Datei - nach dem eigenen Speichern steht der Text schon da
  }, [item?.path, kind]) // eslint-disable-line react-hooks/exhaustive-deps

  // Nachbarn vorladen, damit vor/zurueck sofort da ist
  useEffect(() => {
    for (const d of [-1, 1]) {
      const n = items[index + d]
      if (n && viewKind(n) === 'image') { const img = new Image(); img.src = source.thumb(n, 1600) }
    }
  }, [index, items, source])

  const save = async (force = false) => {
    const value = area.current?.value ?? edit
    if (!onSave || value === null) return
    setSaving(true); setError(null)
    try {
      await onSave(item, value, force)
      setText({ body: value, cut: false }); setEdit(null)
    } catch (e: any) {
      setError(e?.status === 409 ? 'conflict' : e?.message ?? String(e))
    } finally { setSaving(false) }
  }

  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (edit !== null) {
        if ((e.ctrlKey || e.metaKey) && e.key === 's') { e.preventDefault(); save() }
        else if (e.key === 'Escape') { e.preventDefault(); setEdit(null) }
        return
      }
      if (e.key === 'Escape') { e.preventDefault(); onClose() }
      else if (e.key === 'ArrowLeft') { e.preventDefault(); go(-1) }
      else if (e.key === 'ArrowRight') { e.preventDefault(); go(1) }
    }
    window.addEventListener('keydown', key, true)
    return () => window.removeEventListener('keydown', key, true)
  }) // eslint-disable-line react-hooks/exhaustive-deps

  if (!item) return null
  const dark = kind === 'image' || kind === 'video' || kind === 'audio' || kind === null
  const canEdit = !!onSave && (kind === 'text' || kind === 'markdown') && text !== null && !text.cut

  const body = () => {
    switch (kind) {
      case 'image': {
        const src = original || imgFailed > 0 ? source.download(item, true) : source.thumb(item, 1600)
        if (imgFailed > 1) return <NoPreview item={item} onDownload={onDownload} />
        return <img key={src} src={src} alt={item.name} className={mergeClasses(s.img, zoom && s.imgFull)} draggable={false}
          onClick={() => setZoom(z => !z)} onError={() => setImgFailed(n => n + 1)} />
      }
      case 'pdf':
        // Handys zeigen PDFs nicht eingebettet: erste Seite und Knopf zum Oeffnen
        if (mobile) return (
          <div className={s.none}>
            <div style={{ width: 220, height: 300, display: 'flex', alignItems: 'center', justifyContent: 'center', background: '#fff', borderRadius: 4 }}>
              <Thumb item={item} source={source} size={1600} fit="contain" fallbackLarge className={s.media} />
            </div>
            <Button appearance="primary" icon={<Open20Regular />} as="a" href={source.download(item, true)} target="_blank">{t('PDF öffnen')}</Button>
          </div>
        )
        return <iframe className={s.frame} src={source.download(item, true)} title={item.name} />
      case 'audio':
        return <div className={s.none}><KindIcon item={item} large /><audio key={item.path} controls autoPlay src={source.download(item, true)} style={{ width: 'min(520px, 90vw)' }} /></div>
      case 'video':
        return <video key={item.path} className={s.media} controls autoPlay playsInline src={source.download(item, true)}
          onError={() => setError(t('Dieses Video kann der Browser nicht abspielen.'))} />
      case 'text':
      case 'markdown':
        if (error && !text) return <NoPreview item={item} onDownload={onDownload} />
        if (!text) return <Spinner />
        if (edit !== null) return <textarea ref={area} className={s.editor} value={edit} onChange={e => setEdit(e.target.value)} autoFocus spellCheck={kind === 'markdown'} />
        return (
          <div className={s.doc}>
            {text.cut && <Text block style={{ marginBottom: 8, color: tokens.colorPaletteMarigoldForeground1 }}>{t('Nur der Anfang wird gezeigt ({size}).', { size: fmtSize(TEXT_LIMIT) })}</Text>}
            {kind === 'markdown' ? <Markdown text={text.body} /> : <pre className={s.pre}>{text.body}</pre>}
          </div>
        )
      default:
        return <NoPreview item={item} onDownload={onDownload} />
    }
  }

  return (
    <div className={mergeClasses(s.root, !dark && s.light)} role="dialog" aria-label={item.name}
      onTouchStart={e => { if (kind === 'image' && !zoom) touch.current = { x: e.touches[0].clientX, y: e.touches[0].clientY } }}
      onTouchEnd={e => {
        const st = touch.current; touch.current = null
        if (!st) return
        const dx = e.changedTouches[0].clientX - st.x, dy = e.changedTouches[0].clientY - st.y
        if (Math.abs(dx) > 60 && Math.abs(dx) > Math.abs(dy) * 1.5) go(dx < 0 ? 1 : -1)
        else if (dy > 120 && Math.abs(dy) > Math.abs(dx) * 2) onClose()
      }}>
      <div className={s.bar}>
        <div className={s.title}>
          <span className={s.name} title={item.name}>{item.name}</span>
          <span className={s.sub}>
            {items.length > 1 && `${index + 1} / ${items.length} · `}{fmtSize(item.size)} · {fmtWhen(item.modified, locale)}
          </span>
        </div>
        {kind === 'image' && !original && <Tooltip content={t('Originalgröße laden')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<ZoomIn20Regular />} onClick={() => { setOriginal(true); setZoom(true) }} />
        </Tooltip>}
        {kind === 'image' && zoom && <Tooltip content={t('Einpassen')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<ZoomOut20Regular />} onClick={() => setZoom(false)} />
        </Tooltip>}
        {canEdit && edit === null && <Button appearance="transparent" className={s.btn} icon={<Edit20Regular />} onClick={() => setEdit(text!.body)}>{mobile ? undefined : t('Bearbeiten')}</Button>}
        {edit !== null && <>
          <Button appearance="primary" icon={saving ? <Spinner size="tiny" /> : <Save20Regular />} disabled={saving} onClick={() => save()}>{t('Speichern')}</Button>
          <Button appearance="transparent" className={s.btn} onClick={() => { setEdit(null); setError(null) }}>{t('Abbrechen')}</Button>
        </>}
        {(kind === 'pdf' || kind === 'image') && !mobile && <Tooltip content={t('In neuem Tab öffnen')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Open20Regular />} as="a" href={source.download(item, true)} target="_blank" />
        </Tooltip>}
        <Tooltip content={t('Herunterladen')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<ArrowDownload20Regular />} onClick={() => onDownload(item)} />
        </Tooltip>
        {onDetails && <Tooltip content={t('Details')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Info20Regular />} onClick={() => onDetails(item)} />
        </Tooltip>}
        <Tooltip content={t('Schließen (Esc)')} relationship="label">
          <Button appearance="transparent" className={s.btn} icon={<Dismiss24Regular />} onClick={onClose} />
        </Tooltip>
      </div>
      {error && (edit !== null || text) && (
        <div style={{ padding: '6px 16px', background: tokens.colorPaletteRedBackground2, color: tokens.colorPaletteRedForeground2, display: 'flex', gap: 12, alignItems: 'center' }}>
          {error === 'conflict' ? <>
            <span style={{ flex: 1 }}>{t('Die Datei wurde inzwischen geändert.')}</span>
            <Button size="small" onClick={() => save(true)}>{t('Trotzdem speichern')}</Button>
          </> : <span>{error}</span>}
        </div>
      )}
      <div className={mergeClasses(s.stage, zoom && s.zoomed)}>
        {index > 0 && edit === null && <button className={s.nav} style={{ left: 8 }} onClick={() => go(-1)} aria-label={t('Zurück')}><ChevronLeft24Regular /></button>}
        {body()}
        {index < items.length - 1 && edit === null && <button className={s.nav} style={{ right: 8 }} onClick={() => go(1)} aria-label={t('Weiter')}><ChevronRight24Regular /></button>}
      </div>
    </div>
  )
}

function NoPreview({ item, onDownload }: { item: Item; onDownload: (i: Item) => void }) {
  const s = useStyles()
  return (
    <div className={s.none}>
      <div style={{ transform: 'scale(1.6)' }}><KindIcon item={item} large /></div>
      <Text size={400} weight="semibold">{item.name}</Text>
      <Text>{t('Für diese Datei gibt es keine Vorschau.')}</Text>
      <Button appearance="primary" icon={<ArrowDownload20Regular />} onClick={() => onDownload(item)}>{t('Herunterladen')} ({fmtSize(item.size)})</Button>
    </div>
  )
}
