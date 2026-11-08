import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, makeStyles,
  MessageBar, MessageBarBody, mergeClasses, ProgressBar, Spinner, Switch, Text, tokens, Tooltip,
} from '@fluentui/react-components'
import {
  ArrowUp20Regular, Chat20Regular, CheckboxChecked20Regular, Delete20Regular, Dismiss16Regular, Folder20Regular, Image20Regular, ImageMultiple20Regular, Lightbulb20Regular,
  Location20Regular, LockClosed20Regular, Mic20Regular, Sparkle20Regular, Note20Regular, Search20Regular, Stop20Regular, TextBold20Regular, TextBulletListLtr20Regular, TextHeader220Regular,
  TextItalic20Regular, TextNumberListLtr20Regular, TextStrikethrough20Regular, TextUnderline20Regular, WeatherPartlyCloudyDay20Regular,
} from '@fluentui/react-icons'
import { PickPhotoDialog } from '../files/PickPhotoDialog'
import { useEffect, useRef, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { sendToChat } from '../chat/SendToChat'
import { Item, KindIcon, Thumb, hasThumb, ownSource } from '../files/common'
import { t } from '../i18n'
import { DayEvent, dayOf, Entry, EntryInput, fileUrl, Found, inTresor, isAudio, isImage, isVideo, journalApi, Media, MOODS, Place, Ref, thumbUrl, uploadMedia } from './api'
import { SuggestDialog, SuggestPick } from './SuggestDialog'
import { MediaLightbox } from './MediaLightbox'
import { AiState, AiWriteDialog } from './AiWriteDialog'
import { askConfirm } from '../shell/context'
import { useTresor } from '../tresor/tresor'

const useStyles = makeStyles({
  surface: { width: 'min(860px, 96vw)', maxWidth: '96vw' },
  grid: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gap: '10px 14px', '@media (max-width: 640px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  full: { gridColumn: '1 / -1' },
  toolbar: { display: 'flex', flexWrap: 'wrap', gap: '2px', padding: '4px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  editorBox: { border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1 },
  editor: {
    minHeight: '220px', maxHeight: '48vh', overflowY: 'auto', padding: '10px 12px', outline: 'none', lineHeight: 1.5,
    '& ul[data-check]': { listStyleType: 'none', paddingLeft: '4px' },
    '& ul[data-check] > li': { position: 'relative', paddingLeft: '26px', cursor: 'text' },
    '& ul[data-check] > li::before': {
      content: '""', position: 'absolute', left: 0, top: '4px', width: '15px', height: '15px', borderRadius: '3px',
      border: `1.5px solid ${tokens.colorNeutralStroke1Pressed}`, cursor: 'pointer',
    },
    '& ul[data-check] > li[data-done="1"]': { color: tokens.colorNeutralForeground3, textDecoration: 'line-through' },
    '& ul[data-check] > li[data-done="1"]::before': { backgroundColor: tokens.colorBrandBackground, border: `1.5px solid ${tokens.colorBrandBackground}` },
  },
  moods: { display: 'flex', gap: '4px' },
  mood: { fontSize: '22px', minWidth: '40px', height: '40px', padding: 0, opacity: 0.45 },
  moodOn: { opacity: 1, backgroundColor: tokens.colorBrandBackground2 },
  media: { display: 'flex', flexWrap: 'wrap', gap: '8px' },
  tile: { position: 'relative', width: '96px', height: '96px', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', backgroundColor: tokens.colorNeutralBackground3, display: 'flex', alignItems: 'center', justifyContent: 'center' },
  tileImg: { width: '100%', height: '100%', objectFit: 'cover', cursor: 'zoom-in' },
  tileX: { position: 'absolute', top: '2px', right: '2px', minWidth: '24px', height: '24px', padding: 0, backgroundColor: tokens.colorNeutralBackground1 },
  tileName: { fontSize: '11px', padding: '4px', textAlign: 'center', wordBreak: 'break-all' },
  refs: { display: 'flex', flexWrap: 'wrap', gap: '6px' },
  chip: { display: 'inline-flex', alignItems: 'center', gap: '4px', padding: '2px 8px', borderRadius: '12px', backgroundColor: tokens.colorNeutralBackground3 },
  place: { display: 'flex', gap: '6px', alignItems: 'center' },
  coords: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  events: { display: 'flex', flexDirection: 'column', gap: '2px', padding: '8px 10px', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3 },
  pickList: { maxHeight: '50vh', overflowY: 'auto', display: 'flex', flexDirection: 'column', marginTop: '8px' },
  // Fotos und Videos als Vorschaubilder zum Anhaken, darunter andere Dateien als Zeilen
  pickGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(104px, 1fr))', gap: '4px', margin: '4px 0' },
  pickCell: {
    position: 'relative', aspectRatio: '1', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground3, outline: '3px solid transparent', outlineOffset: '-3px',
  },
  pickOn: { outline: `3px solid ${tokens.colorBrandStroke1}` },
  pickImg: { width: '100%', height: '100%', display: 'block' },
  pickCheck: { position: 'absolute', top: '2px', left: '2px', backgroundColor: 'rgba(0,0,0,.35)', borderRadius: tokens.borderRadiusMedium },
  pickRow: { display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 4px', cursor: 'pointer', borderRadius: tokens.borderRadiusMedium, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
})

/** Datum (ISO, UTC) als Wert für &lt;input type="datetime-local"&gt; in Ortszeit. */
const toLocalInput = (iso: string) => {
  const d = new Date(iso)
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`
}

export const fmtCoords = (p: Place) => p.lat != null && p.lon != null ? `${p.lat.toFixed(4)}, ${p.lon.toFixed(4)}` : ''
export const mapLink = (p: Place) => `https://www.openstreetmap.org/?mlat=${p.lat}&mlon=${p.lon}#map=15/${p.lat}/${p.lon}`

/**
 * Tagebucheintrag schreiben bzw. ansehen: Datum, Titel, Text mit Formatierung und Checklisten, Stimmung, Schlagwörter, Ort (von Hand,
 * aus dem Foto oder vom Gerät), Wetter (auf Wunsch automatisch), Fotos/Videos/Sprache, Notizen und Dateien als Verweis, privat,
 * Termine des Tages, Vorschlag für den Anfang, im Chat zeigen. In nur lesbaren geteilten Tagebüchern: nur ansehen.
 */
export function EntryEditor({ journal, entry, forDay, lookup, own, onClose, cancelRef }: {
  journal: number; entry: Entry | null
  /** Schließen wie „Abbrechen“ (mit Rückfrage); false: bleibt offen */
  cancelRef?: React.MutableRefObject<(() => boolean | Promise<boolean>) | null>
  /** Neuer Eintrag für diesen Tag (aus dem Kalender) - mit der jetzigen Uhrzeit */
  forDay?: Date | null
  lookup: boolean; own: boolean; onClose: (changed: boolean) => void
}) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const target = entry?.journal ?? journal
  const readOnly = entry ? !entry.canWrite : false
  // Neuer Eintrag in ein Tagebuch im geschlossenen Tresor: wird verschlüsselt abgelegt, Fotos lassen sich hier nicht zeigen
  const tresor = useTresor()
  const sealed = inTresor(target) && !entry && tresor != null && !tresor.open
  const [date, setDate] = useState(() => {
    if (entry) return toLocalInput(entry.date)
    const d = new Date()
    if (forDay) d.setFullYear(forDay.getFullYear(), forDay.getMonth(), forDay.getDate())
    return toLocalInput(d.toISOString())
  })
  const dateTouched = useRef(!!entry || !!forDay)
  const [title, setTitle] = useState(entry?.title ?? '')
  const [mood, setMood] = useState<number | null>(entry?.mood ?? null)
  const [tags, setTags] = useState((entry?.tags ?? []).join(', '))
  const [place, setPlace] = useState<Place>(entry?.place ?? { name: null, lat: null, lon: null })
  const [weather, setWeather] = useState(entry?.weather ?? '')
  const [priv, setPriv] = useState(entry?.private ?? false)
  const [showEvents, setShowEvents] = useState(entry?.showEvents ?? false)
  const [media, setMedia] = useState<Media[]>(entry?.media ?? [])
  const [refs, setRefs] = useState<Ref[]>(entry?.refs ?? [])
  const [events, setEvents] = useState<DayEvent[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [looking, setLooking] = useState(false)
  const [upload, setUpload] = useState<{ done: number; total: number } | null>(null)
  const [picker, setPicker] = useState<'files' | 'notes' | 'gallery' | null>(null)
  const [suggesting, setSuggesting] = useState(false)
  // KI zum Ausformulieren - nur, wenn eingerichtet und für den Benutzer freigegeben
  const [ai, setAi] = useState<AiState | null>(null)
  const [aiOpen, setAiOpen] = useState(false)
  useEffect(() => { if (!readOnly) api.get<AiState>('/api/journal/ai').then(setAi).catch(() => {}) }, [readOnly])
  const [big, setBig] = useState<number | null>(null)
  const pics = media.filter(m => isImage(m) || isVideo(m))
  const [recording, setRecording] = useState<MediaRecorder | null>(null)
  const editor = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  useEffect(() => { if (editor.current) editor.current.innerHTML = entry?.html || (readOnly ? '' : '<p><br></p>') }, [entry, readOnly])
  // Termine des Tages, wenn gewünscht
  const day = date.slice(0, 10)
  useEffect(() => {
    if (!showEvents || !day) { setEvents(null); return }
    journalApi.events(day).then(setEvents).catch(() => setEvents([]))
  }, [showEvents, day])

  const cmd = (name: string, value?: string) => { editor.current?.focus(); document.execCommand(name, false, value) }
  /** Checkliste: aktuelle Liste umschalten bzw. neue Liste mit Haken. */
  const checklist = () => {
    editor.current?.focus()
    const sel = window.getSelection()
    let node = sel?.anchorNode as HTMLElement | null
    while (node && node !== editor.current && node.nodeName !== 'UL' && node.nodeName !== 'OL') node = node.parentElement
    if (!node || node === editor.current || node.nodeName === 'OL') { document.execCommand('insertUnorderedList'); node = (sel?.anchorNode as HTMLElement | null)?.parentElement?.closest('ul') ?? null }
    if (node?.nodeName === 'UL') { if (node.hasAttribute('data-check')) node.removeAttribute('data-check'); else node.setAttribute('data-check', '') }
  }
  /** Haken setzen: Klick auf das Kästchen vor einem Checklistenpunkt. */
  const onEditorClick = (e: React.MouseEvent) => {
    if (readOnly) return
    const li = (e.target as HTMLElement).closest('li')
    if (!li || !li.parentElement?.hasAttribute('data-check')) return
    if (e.clientX - li.getBoundingClientRect().left > 22) return
    if (li.getAttribute('data-done') === '1') li.removeAttribute('data-done'); else li.setAttribute('data-done', '1')
  }

  /** Wetter und Ortsname zu Ort und Tag holen (nur eingeschaltet) - leere Felder werden gefüllt. */
  const fetchLookup = async (p: Place, when: string, quiet = false) => {
    if (!lookup || p.lat == null || p.lon == null) return
    setLooking(true)
    try {
      const r = await journalApi.lookup(p.lat, p.lon, new Date(when).toISOString())
      if (r.weather) setWeather(w => w || r.weather!)
      if (r.place) setPlace(x => (x.name ? x : { ...x, name: r.place }))
      if (!quiet && !r.weather && !r.place) notify(t('Dazu gibt es gerade keine Angaben.'))
    } catch (e) { if (!quiet) fail(e) } finally { setLooking(false) }
  }

  /** Neues Foto: Datum und Ort aus dem Bild übernehmen, solange der Eintrag noch keine hat. */
  const adopt = (found: Found[]) => {
    setMedia(m => [...m, ...found.map(f => f.media).filter(x => !m.some(y => y.path === x.path))])
    const geo = found.find(f => f.lat != null && f.lon != null)
    const taken = found.map(f => f.media.taken).filter(Boolean).sort()[0]
    let when = date
    if (taken && !dateTouched.current) { when = toLocalInput(taken); setDate(when); dateTouched.current = true }
    if (geo && place.lat == null) {
      const p = { ...place, lat: geo.lat, lon: geo.lon }
      setPlace(p)
      fetchLookup(p, when, true)
    }
  }

  const uploadFiles = async (files: File[]) => {
    if (!files.length) return
    setUpload({ done: 0, total: files.length })
    try {
      const found: Found[] = []
      for (const f of files) { found.push(await uploadMedia(f, target)); setUpload(u => u && { ...u, done: u.done + 1 }) }
      adopt(found)
    } catch (e) { fail(e) } finally { setUpload(null) }
  }

  /** Sprachaufnahme im Browser - danach wie eine hochgeladene Datei. */
  const record = async () => {
    if (recording) { recording.stop(); return }
    try {
      const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
      const rec = new MediaRecorder(stream)
      const chunks: Blob[] = []
      rec.ondataavailable = e => chunks.push(e.data)
      rec.onstop = () => {
        stream.getTracks().forEach(x => x.stop())
        setRecording(null)
        const type = rec.mimeType || 'audio/webm'
        const ext = type.includes('mp4') ? 'm4a' : type.includes('ogg') ? 'ogg' : 'webm'
        uploadFiles([new File([new Blob(chunks, { type })], `Sprachaufnahme ${new Date().toLocaleString().replace(/[/:]/g, '-')}.${ext}`, { type })])
      }
      rec.start()
      setRecording(rec)
    } catch { notify(t('Das Mikrofon lässt sich nicht verwenden.')) }
  }

  const here = () => {
    if (!navigator.geolocation) return
    navigator.geolocation.getCurrentPosition(pos => {
      const p = { ...place, lat: Math.round(pos.coords.latitude * 1e6) / 1e6, lon: Math.round(pos.coords.longitude * 1e6) / 1e6 }
      setPlace(p)
      fetchLookup(p, date, true)
    }, () => notify(t('Den Standort gibt der Browser nicht heraus.')))
  }

  // Vorschlag: zum Auswählen - Termine, Erledigtes und die Fotos des Tages aus der Galerie
  const suggest = () => setSuggesting(true)
  const takeSuggestion = async (pick: SuggestPick | null) => {
    setSuggesting(false)
    if (!pick) return
    if (pick.html) { editor.current?.focus(); document.execCommand('insertHTML', false, pick.html) }
    if (!pick.files.length && !pick.external.length) return
    setUpload({ done: 0, total: pick.files.length + pick.external.length })
    try {
      const found = [
        ...(pick.files.length ? await journalApi.fromFiles(pick.files, target) : []),
        ...(pick.external.length ? await journalApi.fromExternal(pick.external, target) : []),
      ]
      adopt(found)
    } catch (e) { fail(e) } finally { setUpload(null) }
  }

  // Stand beim Öffnen - zum Vergleichen, ob etwas geändert wurde
  const snapshot = () => JSON.stringify([date, title, mood, tags, place, weather, priv, showEvents, media.map(m => m.path), refs.map(r => r.type + r.id), editor.current?.innerHTML ?? ''])
  const initial = useRef<string | null>(null)
  useEffect(() => { const timer = setTimeout(() => { initial.current = snapshot() }, 0); return () => clearTimeout(timer) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  /** Schließen ohne Speichern: bei Änderungen erst fragen - dann kommt die Antwort später (Promise). */
  const cancel = (): boolean | Promise<boolean> => {
    if (readOnly || initial.current == null || snapshot() === initial.current) { onClose(false); return true }
    return askConfirm('', t('Änderungen verwerfen?')).then(ok => { if (ok) onClose(false); return ok })
  }
  // Zurück im Browser (im Tagebuch verwaltet) fragt genauso
  if (cancelRef) cancelRef.current = cancel

  const save = async () => {
    const empty = !title.trim() && !(editor.current?.innerText ?? '').trim() && media.length === 0 && refs.length === 0
    if (empty) { notify(t('Der Eintrag ist noch leer.')); return }
    setBusy(true)
    try {
      const body: EntryInput = {
        journal: target, uri: entry?.uri, etag: entry?.etag, date: new Date(date).toISOString(),
        title: title.trim() || null, html: editor.current?.innerHTML ?? '', mood,
        tags: tags.split(/[,;]/).map(x => x.trim()).filter(Boolean), place: place.name || place.lat != null ? place : null,
        private: priv, showEvents, media, refs, weather: weather.trim() || null,
      }
      await journalApi.save(body)
      onClose(true)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  const remove = async () => {
    if (!entry || !await askConfirm('', inTresor(entry.journal) ? t('Diesen Eintrag samt Fotos und Dateien löschen?')
      : t('Diesen Eintrag löschen? Fotos und Dateien bleiben in der Dateiablage.'))) return
    try { await journalApi.remove(entry.journal, entry.uri); onClose(true) } catch (e) { fail(e) }
  }

  const toChat = async () => {
    if (!entry) return
    try { const r = await journalApi.forChat(entry.journal, entry.uri); sendToChat(r.refs, r.text) } catch (e) { fail(e) }
  }

  const tb = (icon: JSX.Element, label: string, run: () => void) => (
    <Tooltip content={label} relationship="label"><Button size="small" appearance="subtle" icon={icon} onMouseDown={e => { e.preventDefault(); run() }} /></Tooltip>
  )

  return (
    <>
      <Dialog open onOpenChange={(_, d) => { if (!d.open) cancel() }}>
        <DialogSurface className={s.surface}>
          <DialogBody>
            <DialogTitle>{readOnly ? (entry?.title || t('Eintrag')) : entry ? t('Eintrag bearbeiten') : t('Neuer Eintrag')}</DialogTitle>
            <DialogContent>
              {sealed && <MessageBar intent="info" style={{ marginBottom: 8 }}><MessageBarBody>
                {t('Der Tresor ist geschlossen. Der Eintrag wird verschlüsselt abgelegt und beim nächsten Öffnen übernommen.')}</MessageBarBody></MessageBar>}
              {entry?.owner && <Text block style={{ marginBottom: 8, color: tokens.colorNeutralForeground3 }}>{t('Aus dem Tagebuch von {name}', { name: entry.owner })}</Text>}
              <div className={s.grid}>
                <Field label={t('Datum und Uhrzeit')}>
                  <Input type="datetime-local" value={date} disabled={readOnly} onChange={(_, d) => { dateTouched.current = true; setDate(d.value) }} />
                </Field>
                <Field label={t('Wie geht es dir?')}>
                  <div className={s.moods} role="radiogroup">
                    {MOODS.map(m => (
                      <Button key={m.value} appearance="subtle" role="radio" aria-checked={mood === m.value} disabled={readOnly && mood !== m.value} className={mergeClasses(s.mood, mood === m.value && s.moodOn)}
                        onClick={() => !readOnly && setMood(mood === m.value ? null : m.value)}>{m.emoji}</Button>
                    ))}
                  </div>
                </Field>
                {(!readOnly || title) && <Field label={t('Titel')} className={s.full}><Input value={title} readOnly={readOnly} onChange={(_, d) => setTitle(d.value)} placeholder={t('Optional')} /></Field>}
                <div className={mergeClasses(s.full, s.editorBox)}>
                  {!readOnly && (
                    <div className={s.toolbar} role="toolbar">
                      {tb(<TextBold20Regular />, t('Fett'), () => cmd('bold'))}
                      {tb(<TextItalic20Regular />, t('Kursiv'), () => cmd('italic'))}
                      {tb(<TextUnderline20Regular />, t('Unterstrichen'), () => cmd('underline'))}
                      {tb(<TextStrikethrough20Regular />, t('Durchgestrichen'), () => cmd('strikeThrough'))}
                      {tb(<TextHeader220Regular />, t('Überschrift'), () => cmd('formatBlock', 'h2'))}
                      {tb(<TextBulletListLtr20Regular />, t('Aufzählung'), () => cmd('insertUnorderedList'))}
                      {tb(<TextNumberListLtr20Regular />, t('Nummerierung'), () => cmd('insertOrderedList'))}
                      {tb(<CheckboxChecked20Regular />, t('Checkliste'), checklist)}
                      <div style={{ flex: 1 }} />
                      {/* Aus dem Tresor geht nichts an eine KI */}
                      {ai?.allowed && !inTresor(target) && <Tooltip content={t('Den Eintrag von der KI ausformulieren lassen')} relationship="label">
                        <Button size="small" appearance="subtle" icon={<Sparkle20Regular />} onClick={() => setAiOpen(true)}>{t('Mit KI schreiben')}</Button>
                      </Tooltip>}
                      <Tooltip content={t('Termine und erledigte Aufgaben des Tages einfügen')} relationship="label">
                        <Button size="small" appearance="subtle" icon={<Lightbulb20Regular />} onClick={suggest}>{t('Vorschlag')}</Button>
                      </Tooltip>
                    </div>
                  )}
                  <div ref={editor} className={s.editor} contentEditable={!readOnly} suppressContentEditableWarning role="textbox" aria-multiline aria-label={t('Text')} onClick={onEditorClick} />
                </div>
                {events && events.length > 0 && (
                  <div className={mergeClasses(s.full, s.events)}>
                    <Text weight="semibold" size={200}>{t('Termine an diesem Tag')}</Text>
                    {events.map((e, i) => <Text key={i} size={200}>{e.allDay ? t('ganztägig') : e.time} · {e.title}{e.location ? ` · ${e.location}` : ''}</Text>)}
                  </div>
                )}
                <Field label={t('Fotos, Videos und Sprachaufnahmen')} className={s.full}>
                  <div className={s.media}>
                    {media.map(m => (
                      <div key={m.path} className={s.tile} title={m.path.split('/').pop()}>
                        {sealed ? <span className={s.tileName}><LockClosed20Regular /> {m.path.split('/').pop()}</span>
                          : isImage(m) || isVideo(m) ? <img src={thumbUrl(target, m, 256)} alt="" className={s.tileImg} loading="lazy" onClick={() => setBig(pics.indexOf(m))} />
                          : isAudio(m) ? <audio src={fileUrl(target, m)} controls style={{ width: 92 }} />
                            : <a className={s.tileName} href={fileUrl(target, m, true)}>{m.path.split('/').pop()}</a>}
                        {!readOnly && <Button size="small" className={s.tileX} icon={<Dismiss16Regular />} aria-label={t('Entfernen')} onClick={() => setMedia(list => list.filter(x => x.path !== m.path))} />}
                      </div>
                    ))}
                    {upload && <div className={s.tile}><ProgressBar value={upload.done / upload.total} style={{ width: 70 }} /></div>}
                    {readOnly && media.length === 0 && <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('Keine')}</Text>}
                  </div>
                  {!readOnly && (
                    <div style={{ display: 'flex', gap: 8, marginTop: 8, flexWrap: 'wrap' }}>
                      <Button icon={<Image20Regular />} disabled={!!upload} onClick={() => fileInput.current?.click()}>{t('Hochladen …')}</Button>
                      <Button icon={recording ? <Stop20Regular /> : <Mic20Regular />} appearance={recording ? 'primary' : 'secondary'} disabled={!!upload && !recording}
                        onClick={record}>{recording ? t('Aufnahme beenden') : t('Sprachaufnahme')}</Button>
                      <Button icon={<ImageMultiple20Regular />} onClick={() => setPicker('gallery')}>{t('Aus der dmOffice-Galerie')}</Button>
                      <Button icon={<Folder20Regular />} onClick={() => setPicker('files')}>{t('Aus den dmOffice-Dateien')}</Button>
                      {own && <Button icon={<Note20Regular />} onClick={() => setPicker('notes')}>{t('Notiz verknüpfen …')}</Button>}
                      <input ref={fileInput} type="file" multiple accept="image/*,video/*,audio/*" hidden
                        onChange={e => { const f = Array.from(e.target.files ?? []); e.target.value = ''; uploadFiles(f) }} />
                    </div>
                  )}
                  {refs.length > 0 && (
                    <div className={s.refs} style={{ marginTop: 8 }}>
                      {refs.map(r => (
                        <span key={r.type + r.id} className={s.chip}>
                          {r.type === 'note' ? <Note20Regular /> : <Folder20Regular />}
                          {r.type === 'note' && own ? <a href={'#/notes/' + r.id}>{r.name}</a> : r.name}
                          {!readOnly && <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} aria-label={t('Entfernen')} onClick={() => setRefs(l => l.filter(x => x !== r))} />}
                        </span>
                      ))}
                    </div>
                  )}
                </Field>
                <Field label={t('Ort')}>
                  <div className={s.place}>
                    <Input style={{ flex: 1 }} value={place.name ?? ''} readOnly={readOnly} placeholder={t('Name des Orts')} onChange={(_, d) => setPlace(p => ({ ...p, name: d.value }))} />
                    {!readOnly && <Tooltip content={t('Aktuellen Standort übernehmen')} relationship="label"><Button icon={<Location20Regular />} onClick={here} /></Tooltip>}
                  </div>
                  {place.lat != null && place.lon != null && (
                    <Text className={s.coords}>
                      <a href={mapLink(place)} target="_blank" rel="noreferrer">{fmtCoords(place)}</a>{' '}
                      {!readOnly && <Button size="small" appearance="transparent" onClick={() => setPlace(p => ({ ...p, lat: null, lon: null }))}>{t('Koordinaten entfernen')}</Button>}
                    </Text>
                  )}
                </Field>
                <Field label={t('Wetter')}>
                  <div className={s.place}>
                    <Input style={{ flex: 1 }} value={weather} readOnly={readOnly} placeholder={t('z. B. sonnig, 22 °C')} onChange={(_, d) => setWeather(d.value)} />
                    {!readOnly && lookup && place.lat != null && (
                      <Tooltip content={t('Wetter und Ortsnamen holen')} relationship="label">
                        <Button icon={looking ? <Spinner size="tiny" /> : <WeatherPartlyCloudyDay20Regular />} disabled={looking} onClick={() => { setWeather(''); fetchLookup(place, date) }} />
                      </Tooltip>
                    )}
                  </div>
                </Field>
                <Field label={t('Schlagwörter')} hint={readOnly ? undefined : t('Mit Komma getrennt')} className={s.full}><Input value={tags} readOnly={readOnly} onChange={(_, d) => setTags(d.value)} placeholder={t('Urlaub, Familie')} /></Field>
                {!readOnly && (
                  <div className={s.full} style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
                    {own && <Switch label={t('Privat – auch in einem geteilten Tagebuch nur für mich')} checked={priv} onChange={(_, d) => setPriv(d.checked)} />}
                    <Switch label={t('Termine dieses Tages zeigen')} checked={showEvents} onChange={(_, d) => setShowEvents(d.checked)} />
                  </div>
                )}
              </div>
            </DialogContent>
            {/* Über die ganze Breite und einzeilig - sonst werden die Knöpfe hoch und der Inhalt verschwindet im Rollbereich */}
            <DialogActions fluid style={{ flexWrap: 'wrap', whiteSpace: 'nowrap' }}>
              {entry && !readOnly && <Button appearance="subtle" icon={<Delete20Regular />} onClick={remove} style={{ marginRight: 'auto' }}>{t('Löschen')}</Button>}
              {entry && own && !inTresor(entry.journal) && <Button appearance="subtle" icon={<Chat20Regular />} onClick={toChat}>{t('Im Chat zeigen …')}</Button>}
              <Button onClick={cancel}>{readOnly ? t('Schließen') : t('Abbrechen')}</Button>
              {!readOnly && <Button appearance="primary" disabled={busy || !!upload || !!recording} onClick={save}>{busy ? <Spinner size="tiny" /> : t('Speichern')}</Button>}
            </DialogActions>
          </DialogBody>
        </DialogSurface>
      </Dialog>
      {aiOpen && ai && <AiWriteDialog state={ai} journal={target} date={day} mood={mood} title={title} notes={editor.current?.innerText ?? ''}
        place={place.name || null} weather={weather.trim() || null}
        onClose={html => {
          setAiOpen(false)
          // Einstellungen sind jetzt die neue Vorgabe - für ein zweites Öffnen neu holen
          api.get<AiState>('/api/journal/ai').then(setAi).catch(() => {})
          if (!html || !editor.current) return
          if ((editor.current.innerText ?? '').trim()) editor.current.innerHTML += html
          else editor.current.innerHTML = html
        }} />}
      {suggesting && <SuggestDialog day={day} media={media} onClose={takeSuggestion} />}
      {picker === 'files' && <FilesPicker onClose={() => setPicker(null)} onPick={async items => {
        setPicker(null)
        try { adopt(await journalApi.fromFiles(items.map(i => i.path), target)) } catch (e) { fail(e) }
      }} />}
      {/* Ganze Galerie (die Fotos des Tages bietet „Vorschlag“): eigene über die Dateiablage, Galerie-Ordner ohne „ext:“ */}
      {picker === 'gallery' && <PickPhotoDialog title={t('Aus der dmOffice-Galerie')} onClose={() => setPicker(null)} onPickMany={async items => {
        setPicker(null)
        const ext = items.filter(i => i.path.startsWith('ext:')), own = items.filter(i => !i.path.startsWith('ext:'))
        try {
          const found = [...(own.length ? await journalApi.fromFiles(own.map(i => i.path), target) : []),
            ...(ext.length ? await journalApi.fromExternal(ext.map(i => i.path.slice(4)), target) : [])]
          adopt(found)
        } catch (e) { fail(e) }
      }} />}
      {picker === 'notes' && <NotesPicker onClose={() => setPicker(null)} onPick={n => {
        setPicker(null)
        setRefs(l => l.some(r => r.type === 'note' && r.id === String(n.id)) ? l : [...l, { type: 'note', id: String(n.id), name: n.title }])
      }} />}
      {big != null && <MediaLightbox journal={target} items={pics} index={big} onIndex={setBig} onClose={() => setBig(null)} />}
    </>
  )
}

const parentOf = (p: string) => (p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '')

/** Dateien aus der Dateiablage wählen (Bilder kommen als kleine Kopie ins Tagebuch, alles andere als Verweis). */
function FilesPicker({ onClose, onPick }: { onClose: () => void; onPick: (items: Item[]) => void }) {
  const s = useStyles()
  const [path, setPath] = useState('')
  const [items, setItems] = useState<Item[] | null>(null)
  const [chosen, setChosen] = useState<Item[]>([])
  const toggle = (i: Item) => setChosen(c => c.some(x => x.path === i.path) ? c.filter(x => x.path !== i.path) : [...c, i])
  useEffect(() => {
    setItems(null)
    api.get<{ items: Item[] }>('/api/files/?' + q({ path })).then(d => setItems(d.items.filter(i => !i.shared && !i.owner))).catch(() => setItems([]))
  }, [path])
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 760, width: '100%' }}>
        <DialogBody>
          <DialogTitle>{t('Aus der Dateiablage')}</DialogTitle>
          <DialogContent>
            <div style={{ display: 'flex', alignItems: 'center', gap: 6 }}>
              <Button size="small" appearance="subtle" icon={<ArrowUp20Regular />} disabled={!path} onClick={() => setPath(parentOf(path))} aria-label={t('Nach oben')} />
              <Folder20Regular /> <span>{path || t('Dateien')}</span>
            </div>
            <div className={s.pickList}>
              {items == null && <Spinner size="small" />}
              {items?.length === 0 && <Text>{t('Dieser Ordner ist leer.')}</Text>}
              {items?.filter(i => i.isDir).map(i => (
                <div key={i.path} className={s.pickRow} onClick={() => setPath(i.path)}><Folder20Regular />{i.name}</div>
              ))}
              {/* Fotos und Videos: Vorschaubilder, neueste zuerst */}
              {!!items?.some(i => !i.isDir && hasThumb(i)) && <div className={s.pickGrid}>
                {items.filter(i => !i.isDir && hasThumb(i)).sort((a, b) => Date.parse(b.modified) - Date.parse(a.modified)).map(i => {
                  const on = chosen.some(x => x.path === i.path)
                  return (
                    <div key={i.path} className={mergeClasses(s.pickCell, on && s.pickOn)} title={i.name} role="checkbox" aria-checked={on} aria-label={i.name} tabIndex={0}
                      onClick={() => toggle(i)} onKeyDown={e => { if (e.key === ' ' || e.key === 'Enter') { e.preventDefault(); toggle(i) } }}>
                      <Thumb item={i} source={ownSource} size={256} className={s.pickImg} />
                      <span className={s.pickCheck}><Checkbox checked={on} tabIndex={-1} /></span>
                    </div>
                  )
                })}
              </div>}
              {items?.filter(i => !i.isDir && !hasThumb(i)).map(i => (
                <label key={i.path} className={s.pickRow}>
                  <Checkbox checked={chosen.some(x => x.path === i.path)} onChange={() => toggle(i)} />
                  <KindIcon item={i} />{i.name}
                </label>
              ))}
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!chosen.length} onClick={() => onPick(chosen)}>{chosen.length > 1 ? t('{n} anhängen', { n: chosen.length }) : t('Anhängen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

interface NoteHit { id: number; title: string; snippet?: string; owner?: string | null }

function NotesPicker({ onClose, onPick }: { onClose: () => void; onPick: (n: NoteHit) => void }) {
  const s = useStyles()
  const [query, setQuery] = useState('')
  const [notes, setNotes] = useState<NoteHit[] | null>(null)
  useEffect(() => {
    const timer = setTimeout(() => {
      api.get<{ items: NoteHit[] }>('/api/notes?' + q(query.trim() ? { q: query.trim() } : { view: 'all' }))
        .then(d => setNotes(d.items.filter(n => n.id > 0 && !n.owner))).catch(() => setNotes([]))
    }, 250)
    return () => clearTimeout(timer)
  }, [query])
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('Notiz verknüpfen')}</DialogTitle>
          <DialogContent>
            <Input contentBefore={<Search20Regular />} placeholder={t('Notizen durchsuchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ width: '100%' }} />
            <div className={s.pickList}>
              {notes == null && <Spinner size="small" />}
              {notes?.length === 0 && <Text>{t('Keine Notizen gefunden.')}</Text>}
              {notes?.map(n => <div key={n.id} className={s.pickRow} onClick={() => onPick(n)}><Note20Regular />{n.title}</div>)}
            </div>
          </DialogContent>
          <DialogActions><Button onClick={onClose}>{t('Abbrechen')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

export { dayOf }
