import { Button, Dialog, DialogSurface, Text, makeStyles } from '@fluentui/react-components'
import { ChevronLeft24Regular, ChevronRight24Regular, Dismiss20Regular } from '@fluentui/react-icons'
import { useEffect, useRef } from 'react'
import { t } from '../i18n'
import { fileUrl, isVideo, Media, thumbUrl } from './api'
import { useHistoryClose } from '../shell/mobile'

const useStyles = makeStyles({
  surface: { maxWidth: '96vw', width: '96vw', height: '92vh', padding: '8px', display: 'flex', flexDirection: 'column', gap: '6px' },
  stage: { position: 'relative', flex: 1, minHeight: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', touchAction: 'pan-y' },
  media: { maxWidth: '100%', maxHeight: '100%', objectFit: 'contain', display: 'block' },
  nav: { position: 'absolute', top: '50%', transform: 'translateY(-50%)', minWidth: '44px', height: '64px', backgroundColor: 'rgba(0,0,0,0.35)', color: 'white' },
  foot: { display: 'flex', alignItems: 'center', gap: '12px' },
})

/** Fotos und Videos eines Eintrags groß: blättern mit Pfeilen, Pfeiltasten oder Wischen. */
export function MediaLightbox({ journal, journals, items, index, onIndex, onClose, onEntry }: {
  journal: number; items: Media[]; index: number; onIndex: (i: number) => void; onClose: () => void
  /** Tagebuch je Bild, wenn sie aus mehreren stammen (Ansicht Medien) */
  journals?: number[]
  /** „Zum Eintrag“ (Ansicht Medien) */
  onEntry?: (i: number) => void
}) {
  const s = useStyles()
  const m = items[index]
  const jr = journals?.[index] ?? journal
  // Zurück im Browser schließt nur die Großansicht
  useHistoryClose(true, onClose)
  const go = (d: number) => { const n = index + d; if (n >= 0 && n < items.length) onIndex(n) }
  useEffect(() => {
    const key = (e: KeyboardEvent) => { if (e.key === 'ArrowLeft') go(-1); else if (e.key === 'ArrowRight') go(1) }
    window.addEventListener('keydown', key)
    return () => window.removeEventListener('keydown', key)
  })
  // Wischen am Handy
  const touch = useRef<number | null>(null)
  if (!m) return null
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface className={s.surface}>
        <div className={s.stage}
          onTouchStart={e => { touch.current = e.touches[0].clientX }}
          onTouchEnd={e => { if (touch.current == null) return; const dx = e.changedTouches[0].clientX - touch.current; touch.current = null; if (Math.abs(dx) > 50) go(dx < 0 ? 1 : -1) }}>
          {isVideo(m) ? <video key={m.path} src={fileUrl(jr, m)} controls autoPlay className={s.media} />
            : <img key={m.path} src={thumbUrl(jr, m, 2048)} alt="" className={s.media} />}
          {index > 0 && <Button className={s.nav} style={{ left: 4 }} appearance="subtle" icon={<ChevronLeft24Regular />} aria-label={t('Vorheriges')} onClick={() => go(-1)} />}
          {index < items.length - 1 && <Button className={s.nav} style={{ right: 4 }} appearance="subtle" icon={<ChevronRight24Regular />} aria-label={t('Nächstes')} onClick={() => go(1)} />}
        </div>
        <div className={s.foot}>
          <Text size={200}>{m.taken ? new Date(m.taken).toLocaleString() : m.path.split('/').pop()}</Text>
          {items.length > 1 && <Text size={200}>{index + 1} / {items.length}</Text>}
          <div style={{ flex: 1 }} />
          {onEntry && <Button size="small" onClick={() => onEntry(index)}>{t('Zum Eintrag')}</Button>}
          <Button size="small" icon={<Dismiss20Regular />} onClick={onClose}>{t('Schließen')}</Button>
        </div>
      </DialogSurface>
    </Dialog>
  )
}
