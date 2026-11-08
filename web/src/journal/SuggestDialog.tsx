import { Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Spinner, Text, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { CheckmarkCircle20Filled, Video20Regular } from '@fluentui/react-icons'
import { useEffect, useMemo, useState } from 'react'
import { useApp } from '../App'
import { kindOf } from '../files/common'
import { gallerySource } from '../files/GalleryFolders'
import { t } from '../i18n'
import { DayEvent, DayPhoto, journalApi, Media } from './api'

const useStyles = makeStyles({
  section: { display: 'flex', flexDirection: 'column', gap: '2px' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(104px, 1fr))', gap: '6px', maxHeight: '40vh', overflowY: 'auto', paddingTop: '4px' },
  tile: {
    position: 'relative', aspectRatio: '1', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground3, display: 'flex', alignItems: 'center', justifyContent: 'center', outline: '2px solid transparent',
  },
  on: { outline: `3px solid ${tokens.colorBrandStroke1}` },
  img: { width: '100%', height: '100%', objectFit: 'cover' },
  mark: { position: 'absolute', top: '4px', right: '4px', color: tokens.colorBrandForeground1, backgroundColor: tokens.colorNeutralBackground1, borderRadius: '50%' },
  muted: { color: tokens.colorNeutralForeground3 },
})

/** Grundname ohne Endung und ohne „ (2)“ - angehängte Bilder werden zu .jpg kleingerechnet und bekommen bei Gleichnamigen eine Zahl. */
const base = (p: string) => p.slice(p.lastIndexOf('/') + 1).replace(/\.[^.]+$/, '').replace(/ \(\d+\)$/, '').toLowerCase()

/**
 * Schon am Eintrag? Gleicher Pfad (Verweis), gleiche Aufnahmezeit (die Kopie am Eintrag ist kleingerechnet und umbenannt,
 * die Aufnahmezeit behält sie) oder - ohne Aufnahmezeit - gleicher Name.
 */
const attached = (p: DayPhoto, media: Media[]) => media.some(m => m.path === p.path
  || (m.taken && p.taken != null ? Math.abs(Date.parse(m.taken) - p.taken) < 1000 : base(m.path) === base(p.name)))

export interface SuggestPick { html: string; files: string[]; external: string[] }

/**
 * Vorschlag für einen Tag zum Auswählen: Termine und erledigte Aufgaben als Text, dazu die Fotos und Videos des Tages aus der
 * Galerie (eigene Ablage und Galerie-Ordner) - ohne die, die schon am Eintrag hängen. Übernommen wird, was angehakt ist.
 */
export function SuggestDialog({ day, media, onClose }: { day: string; media: Media[]; onClose: (pick: SuggestPick | null) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [data, setData] = useState<{ events: DayEvent[]; tasks: string[]; photos: DayPhoto[] } | null>(null)
  const [events, setEvents] = useState<Set<number>>(new Set())
  const [tasks, setTasks] = useState<Set<number>>(new Set())
  const [photos, setPhotos] = useState<Set<string>>(new Set())
  useEffect(() => {
    journalApi.suggestDay(day).then(r => {
      setData(r)
      // Text wie bisher vorab angehakt, Fotos wählt man selbst
      setEvents(new Set(r.events.map((_, i) => i)))
      setTasks(new Set(r.tasks.map((_, i) => i)))
    }).catch(e => { fail(e); onClose(null) })
  }, [day]) // eslint-disable-line react-hooks/exhaustive-deps
  const shown = useMemo(() => (data?.photos ?? []).filter(p => !attached(p, media)), [data, media])
  const toggle = <T,>(set: Set<T>, apply: (s: Set<T>) => void, v: T) => { const n = new Set(set); if (n.has(v)) n.delete(v); else n.add(v); apply(n) }
  const take = () => {
    if (!data) return
    const enc = (x: string) => x.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!))
    const ev = data.events.filter((_, i) => events.has(i))
    const done = data.tasks.filter((_, i) => tasks.has(i))
    // Essensplan-Einträge als Mahlzeiten („Abendessen: Kichererbsen-Salat“), die übrigen als Termine
    const appts = ev.filter(e => !e.meal), meals = ev.filter(e => e.meal)
    const html = (appts.length ? `<p><b>${t('Termine:')}</b> ${appts.map(e => enc((e.allDay || !e.time ? '' : e.time + ' ') + e.title)).join(', ')}</p>` : '')
      + (meals.length ? `<p><b>${t('Mahlzeiten:')}</b> ${meals.map(e => enc(t(e.meal!) + ': ' + e.title)).join(', ')}</p>` : '')
      + (done.length ? `<p><b>${t('Erledigt:')}</b> ${done.map(enc).join(', ')}</p>` : '')
    const picked = shown.filter(p => photos.has(p.path))
    onClose({ html, files: picked.filter(p => p.kind === 'file').map(p => p.path), external: picked.filter(p => p.kind === 'ext').map(p => p.path.slice(4)) })
  }
  const nothing = data && !data.events.length && !data.tasks.length && !shown.length
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(null) }}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <DialogBody>
          <DialogTitle>{t('Vorschlag für {day}', { day: new Date(day + 'T12:00').toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }) })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            {!data && <Spinner />}
            {nothing && <Text className={s.muted}>{t('Für diesen Tag gibt es keine Termine, keine erledigten Aufgaben und keine Fotos.')}</Text>}
            {data?.events.some(e => !e.meal) && <div className={s.section}>
              <Text weight="semibold">{t('Termine')}</Text>
              {data.events.map((e, i) => e.meal ? null : <Checkbox key={i} checked={events.has(i)} onChange={() => toggle(events, setEvents, i)}
                label={(e.allDay || !e.time ? '' : e.time + ' ') + e.title + (e.location ? ' · ' + e.location : '')} />)}
            </div>}
            {data?.events.some(e => e.meal) && <div className={s.section}>
              <Text weight="semibold">{t('Mahlzeiten')}</Text>
              {data.events.map((e, i) => !e.meal ? null : <Checkbox key={i} checked={events.has(i)} onChange={() => toggle(events, setEvents, i)}
                label={t(e.meal) + ': ' + e.title} />)}
            </div>}
            {!!data?.tasks.length && <div className={s.section}>
              <Text weight="semibold">{t('Erledigt')}</Text>
              {data.tasks.map((x, i) => <Checkbox key={i} checked={tasks.has(i)} onChange={() => toggle(tasks, setTasks, i)} label={x} />)}
            </div>}
            {!!shown.length && <div className={s.section}>
              <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
                <Text weight="semibold" style={{ flex: 1 }}>{t('Fotos und Videos aus der Galerie ({n})', { n: shown.length })}</Text>
                <Button size="small" appearance="subtle" onClick={() => setPhotos(photos.size === shown.length ? new Set() : new Set(shown.map(p => p.path)))}>
                  {photos.size === shown.length ? t('Keine') : t('Alle Fotos')}</Button>
              </div>
              <div className={s.grid}>
                {shown.map(p => {
                  const item = { path: p.path, name: p.name, isDir: false, size: 0, mime: p.mime, modified: '', etag: p.etag ?? undefined }
                  const on = photos.has(p.path)
                  return (
                    <div key={p.path} className={mergeClasses(s.tile, on && s.on)} title={p.name} onClick={() => toggle(photos, setPhotos, p.path)}>
                      {kindOf(item) === 'video' ? <Video20Regular /> : <img className={s.img} src={gallerySource.thumb(item, 256)} alt={p.name} loading="lazy" />}
                      {on && <CheckmarkCircle20Filled className={s.mark} />}
                    </div>
                  )
                })}
              </div>
              {data && data.photos.length > shown.length && <Text size={200} className={s.muted}>{data.photos.length - shown.length === 1 ? t('Eines hängt schon am Eintrag.') : t('{n} hängen schon am Eintrag.', { n: data.photos.length - shown.length })}</Text>}
            </div>}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!data || (!events.size && !tasks.size && !photos.size)} onClick={take}>{t('Übernehmen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
