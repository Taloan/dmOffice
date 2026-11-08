import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { ArrowMove20Regular, Edit20Regular, Folder20Regular, LockClosed16Regular, Note20Regular, People16Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { t } from '../i18n'
import { fmtCoords, mapLink } from './EntryEditor'
import { DayEvent, Entry, fileUrl, isAudio, isImage, isVideo, journalApi, moodEmoji, thumbUrl } from './api'
import { MediaLightbox } from './MediaLightbox'

const useStyles = makeStyles({
  surface: { width: 'min(860px, 96vw)', maxWidth: '96vw' },
  head: { display: 'flex', alignItems: 'baseline', gap: '10px', flexWrap: 'wrap', color: tokens.colorNeutralForeground3 },
  text: {
    lineHeight: 1.6, fontSize: tokens.fontSizeBase400, overflowWrap: 'anywhere', margin: '8px 0',
    '& ul[data-check]': { listStyleType: 'none', paddingLeft: '4px' },
    '& ul[data-check] > li': { position: 'relative', paddingLeft: '26px' },
    '& ul[data-check] > li::before': { content: '""', position: 'absolute', left: 0, top: '5px', width: '15px', height: '15px', borderRadius: '3px', border: `1.5px solid ${tokens.colorNeutralStroke1Pressed}` },
    '& ul[data-check] > li[data-done="1"]': { color: tokens.colorNeutralForeground3, textDecoration: 'line-through' },
    '& ul[data-check] > li[data-done="1"]::before': { backgroundColor: tokens.colorBrandBackground, border: `1.5px solid ${tokens.colorBrandBackground}` },
  },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: '6px', margin: '8px 0' },
  tile: { position: 'relative', aspectRatio: '1', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', cursor: 'zoom-in', backgroundColor: tokens.colorNeutralBackground3, border: 'none', padding: 0 },
  img: { width: '100%', height: '100%', objectFit: 'cover', display: 'block' },
  meta: { display: 'flex', gap: '12px', flexWrap: 'wrap', alignItems: 'center', color: tokens.colorNeutralForeground2, fontSize: tokens.fontSizeBase200, marginTop: '6px' },
  events: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3, margin: '8px 0' },
  chip: { display: 'inline-flex', alignItems: 'center', gap: '4px', padding: '2px 8px', borderRadius: '12px', backgroundColor: tokens.colorNeutralBackground3 },
})

/** Eintrag ansehen (wie in der App): Text, Fotos zum Durchblättern, Ort, Wetter, Schlagwörter; Bearbeiten nur auf Wunsch. */
export function EntryView({ entry, own, onEdit, onMove, onClose }: {
  entry: Entry; own: boolean; onEdit?: () => void
  /** In ein anderes eigenes Tagebuch verschieben */
  onMove?: () => void; onClose: () => void
}) {
  const s = useStyles()
  const [big, setBig] = useState<number | null>(null)
  const [events, setEvents] = useState<DayEvent[] | null>(null)
  const d = new Date(entry.date)
  useEffect(() => {
    if (entry.showEvents) journalApi.events(entry.date.slice(0, 10)).then(setEvents).catch(() => setEvents([]))
  }, [entry])
  // Esc schließt auch nach dem großen Foto: dann liegt der Fokus nicht mehr im Dialog und Fluent bekommt die Taste nicht mit
  useEffect(() => {
    if (big != null) return
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape' && !e.defaultPrevented && location.hash.startsWith('#/journal')) onClose() }
    document.addEventListener('keydown', key)
    return () => document.removeEventListener('keydown', key)
  }, [big, onClose])
  const pics = entry.media.filter(m => isImage(m) || isVideo(m))
  const other = entry.media.filter(m => !isImage(m) && !isVideo(m))
  return (
    <>
      <Dialog open onOpenChange={(_, x) => !x.open && onClose()}>
        <DialogSurface className={s.surface}>
          <DialogBody>
            <DialogTitle>{entry.title || d.toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })}</DialogTitle>
            <DialogContent>
              <div className={s.head}>
                <span>{d.toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })} · {d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}</span>
                {entry.mood != null && <span style={{ fontSize: 22 }} title={t('Stimmung')}>{moodEmoji(entry.mood)}</span>}
                {entry.private && <LockClosed16Regular aria-label={t('Privat')} />}
                {entry.owner && <span><People16Regular style={{ verticalAlign: 'middle' }} /> {entry.owner}</span>}
              </div>
              {events && events.length > 0 && (
                <div className={s.events}>
                  <Text weight="semibold" size={200}>{t('Termine an diesem Tag')}</Text>
                  {events.map((e, i) => <Text key={i} size={200}>{e.allDay ? t('ganztägig') : e.time} · {e.title}{e.location ? ` · ${e.location}` : ''}</Text>)}
                </div>
              )}
              {entry.html.trim() && <div className={s.text} dangerouslySetInnerHTML={{ __html: entry.html }} />}
              {pics.length > 0 && (
                <div className={s.grid}>
                  {pics.map((m, i) => (
                    <button key={m.path} className={s.tile} onClick={() => setBig(i)} aria-label={t('Groß anzeigen')}>
                      <img src={thumbUrl(entry.journal, m, 512)} alt="" className={s.img} loading="lazy" />
                      {isVideo(m) && <span style={{ position: 'absolute', left: 8, top: 6, color: 'white', textShadow: '0 0 4px black' }}>▶</span>}
                    </button>
                  ))}
                </div>
              )}
              {other.map(m => isAudio(m)
                ? <audio key={m.path} src={fileUrl(entry.journal, m)} controls style={{ display: 'block', margin: '6px 0' }} />
                : <a key={m.path} href={fileUrl(entry.journal, m, true)} style={{ display: 'block' }}>{m.path.split('/').pop()}</a>)}
              {entry.refs.length > 0 && (
                <div className={s.meta}>
                  {entry.refs.map(r => (
                    <span key={r.type + r.id} className={s.chip}>
                      {r.type === 'note' ? <Note20Regular /> : <Folder20Regular />}
                      {r.type === 'note' && own ? <a href={'#/notes/' + r.id} onClick={onClose}>{r.name}</a> : r.name}
                    </span>
                  ))}
                </div>
              )}
              <div className={s.meta}>
                {entry.place && (entry.place.name || entry.place.lat != null) && <span>📍 {entry.place.lat != null
                  ? <a href={mapLink(entry.place)} target="_blank" rel="noreferrer">{entry.place.name || fmtCoords(entry.place)}</a> : entry.place.name}</span>}
                {entry.weather && <span>🌤 {entry.weather}</span>}
                {entry.tags.map(x => <Badge key={x} appearance="tint" shape="rounded">#{x}</Badge>)}
              </div>
            </DialogContent>
            <DialogActions>
              {onMove && <Button icon={<ArrowMove20Regular />} onClick={onMove}>{t('Verschieben …')}</Button>}
              {onEdit && <Button icon={<Edit20Regular />} onClick={onEdit}>{t('Bearbeiten')}</Button>}
              <Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button>
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {big != null && <MediaLightbox journal={entry.journal} items={pics} index={big} onIndex={setBig} onClose={() => setBig(null)} />}
    </>
  )
}
