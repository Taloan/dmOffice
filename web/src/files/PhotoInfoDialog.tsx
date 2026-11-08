import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Rating, Text, Textarea } from '@fluentui/react-components'
import { Dismiss12Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Item } from './common'
import type { Meta } from './PhotoFilter'
import { RAW } from './PhotoEditor'

const CHUNK = 50

/** Gemeinsamer Wert aller markierten Fotos - undefined, wenn sie sich unterscheiden. */
function common<T>(values: T[]): T | undefined {
  return values.every(v => v === values[0]) ? values[0] : undefined
}

/**
 * Titel, Bildunterschrift und Bewertung eines oder mehrerer Fotos ändern - geschrieben in die Fotos selbst (JPEG) bzw. in die
 * Sidecar-Datei (RAW), wie Lightroom es liest. Bei mehreren bleibt jedes Feld, das man nicht anfasst, wie es ist.
 */
export function PhotoInfoDialog({ items, metaOf, onClose, onDone }: {
  items: Item[]; metaOf: (i: Item) => Meta | undefined; onClose: () => void; onDone: () => void
}) {
  const { fail, notify } = useApp()
  const usable = items.filter(i => !i.isDir && (/\.jpe?g$/i.test(i.name) || RAW.test(i.name)))
  const metas = usable.map(metaOf)
  const start = {
    title: common(metas.map(m => m?.title ?? '')),
    description: common(metas.map(m => m?.description ?? '')),
    rating: common(metas.map(m => m?.rating ?? 0)),
  }
  const [title, setTitle] = useState(start.title ?? '')
  const [description, setDescription] = useState(start.description ?? '')
  const [rating, setRating] = useState(start.rating ?? 0)
  // Stichwörter: wie viele der Fotos jedes schon haben; entfernt gilt für alle, hinzugefügt auch
  const counts = new Map<string, { name: string; n: number }>()
  for (const m of metas) for (const k of (m?.keywords ?? '').split('\n').map(x => x.trim()).filter(Boolean)) {
    const c = counts.get(k.toLowerCase()) ?? { name: k, n: 0 }
    c.n++; counts.set(k.toLowerCase(), c)
  }
  const [removed, setRemoved] = useState<string[]>([])
  const [added, setAdded] = useState<string[]>([])
  const [kw, setKw] = useState('')
  const addKw = (text: string) => {
    const parts = text.split(/[,;\n]/).map(x => x.trim()).filter(Boolean)
    if (!parts.length) return
    setAdded(a => [...a, ...parts.filter(p => !a.some(x => x.toLowerCase() === p.toLowerCase()) && !(counts.get(p.toLowerCase())?.n === usable.length && !removed.some(r => r.toLowerCase() === p.toLowerCase())))])
    setRemoved(r => r.filter(x => !parts.some(p => p.toLowerCase() === x.toLowerCase())))
    setKw('')
  }
  // Nur angefasste Felder werden geschrieben
  const [touched, setTouched] = useState({ title: false, description: false, rating: false })
  const [progress, setProgress] = useState<number | null>(null)
  const [failed, setFailed] = useState<{ id: string; name: string; error: string }[]>([])
  const mixed = t('Unterschiedlich - bleibt, wenn leer')
  const any = touched.title || touched.description || touched.rating || added.length > 0 || removed.length > 0 || kw.trim().length > 0

  const save = async () => {
    const body = {
      title: touched.title ? title : undefined,
      description: touched.description ? description : undefined,
      rating: touched.rating ? rating : undefined,
      // Noch im Eingabefeld Stehendes zählt mit
      addKeywords: [...added, ...kw.split(/[,;\n]/).map(x => x.trim()).filter(Boolean)],
      removeKeywords: removed,
    }
    const bad: typeof failed = []
    setFailed([]); setProgress(0)
    try {
      for (let k = 0; k < usable.length; k += CHUNK) {
        const r = await api.post<{ failed: typeof failed }>('/api/gallery/info', { items: usable.slice(k, k + CHUNK).map(i => i.path), ...body })
        bad.push(...r.failed)
        setProgress(Math.min(usable.length, k + CHUNK))
      }
    } catch (e) { fail(e) }
    setProgress(null)
    onDone()
    if (bad.length) { setFailed(bad); return }
    notify(usable.length === 1 ? t('Gespeichert.') : t('{n} Fotos gespeichert.', { n: usable.length }))
    onClose()
  }

  const busy = progress != null
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open && !busy) onClose() }}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>{usable.length === 1 ? usable[0].name : t('{n} Fotos', { n: usable.length })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {usable.length < items.length && <Text>{t('{n} von {all} markierten Dateien lassen sich ändern (JPEG und RAW).', { n: usable.length, all: items.length })}</Text>}
            <Field label={t('Titel')}>
              <Input value={title} maxLength={500} placeholder={start.title === undefined ? mixed : undefined} disabled={busy}
                onChange={(_, d) => { setTitle(d.value); setTouched(x => ({ ...x, title: true })) }} />
            </Field>
            <Field label={t('Bildunterschrift')}>
              <Textarea value={description} maxLength={4000} rows={3} resize="vertical" placeholder={start.description === undefined ? mixed : undefined} disabled={busy}
                onChange={(_, d) => { setDescription(d.value); setTouched(x => ({ ...x, description: true })) }} />
            </Field>
            <Field label={t('Bewertung')} hint={start.rating === undefined && !touched.rating ? t('Unterschiedlich - bleibt, wenn nicht geändert') : undefined}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <Rating value={rating} max={5} color="marigold" onChange={(_, d) => { if (!busy) { setRating(d.value); setTouched(x => ({ ...x, rating: true })) } }} />
                {rating > 0 && <Button size="small" appearance="subtle" disabled={busy} onClick={() => { setRating(0); setTouched(x => ({ ...x, rating: true })) }}>{t('Entfernen')}</Button>}
              </div>
            </Field>
            <Field label={t('Stichwörter')} hint={usable.length > 1 ? t('Hinzugefügte bekommen alle markierten Fotos, entfernte verlieren alle.') : undefined}>
              <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, marginBottom: 6 }}>
                {[...counts.values()].filter(c => !removed.some(r => r.toLowerCase() === c.name.toLowerCase())).map(c =>
                  <Button key={c.name} size="small" shape="circular" icon={<Dismiss12Regular />} iconPosition="after" disabled={busy}
                    title={t('Entfernen')} onClick={() => setRemoved(r => [...r, c.name])}>
                    {c.n < usable.length ? `${c.name} (${c.n}/${usable.length})` : c.name}
                  </Button>)}
                {added.map(k =>
                  <Button key={'+' + k} size="small" shape="circular" appearance="primary" icon={<Dismiss12Regular />} iconPosition="after" disabled={busy}
                    title={t('Entfernen')} onClick={() => setAdded(a => a.filter(x => x !== k))}>{k}</Button>)}
              </div>
              <Input value={kw} disabled={busy} placeholder={t('Stichwort eingeben, Enter fügt hinzu')}
                onChange={(_, d) => { if (/[,;]/.test(d.value)) addKw(d.value); else setKw(d.value) }}
                onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); addKw(kw) } }} />
            </Field>
            <Text size={200}>{t('JPEG-Fotos bekommen die Angaben in die Datei, RAW-Fotos in die XMP-Datei daneben - so lesen sie auch Lightroom und andere Programme.')}</Text>
            {busy && usable.length > CHUNK && <Text>{t('{n} von {all} gespeichert …', { n: progress, all: usable.length })}</Text>}
            {failed.length > 0 && <>
              <Text weight="semibold">{t('{n} Fotos ließen sich nicht ändern:', { n: failed.length })}</Text>
              <div style={{ maxHeight: 160, overflow: 'auto', fontSize: 12 }}>
                {failed.map(f => <div key={f.id}><b>{f.name}</b>: {f.error}</div>)}
              </div>
            </>}
          </DialogContent>
          <DialogActions>
            <Button appearance="primary" disabled={busy || !any || usable.length === 0} onClick={save}>{t('Speichern')}</Button>
            <Button disabled={busy} onClick={onClose}>{failed.length ? t('Schließen') : t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
