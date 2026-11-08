import {
  Badge, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, makeStyles,
  MessageBar, MessageBarBody, mergeClasses, Option, OptionGroup, ProgressBar, Radio, RadioGroup, Spinner, Switch,
  SearchBox, Text, tokens,
} from '@fluentui/react-components'
import {
  Add20Regular, ArrowExport20Regular, ArrowMove20Regular, CheckboxChecked20Regular, CheckmarkCircle20Filled, Circle20Regular, ArrowImport20Regular, CalendarLtr20Regular, ChevronLeft20Regular, Delete20Regular, Dismiss16Regular, Emoji20Regular,
  EyeOff20Regular, Grid20Regular, History20Regular, Image20Regular, LockClosed16Regular, LockClosed20Regular, LockOpen20Regular, Map20Regular, NotebookAdd20Regular, People16Regular, Print20Regular, Rename20Regular,
  Settings20Regular, Share20Regular, Timeline20Regular,
} from '@fluentui/react-icons'
import { RibbonButton, RibbonDivider, RibbonHeader } from '../shell/Ribbon'
import { lazy, Suspense, useCallback, useEffect, useMemo, useState, useRef } from 'react'
import { q } from '../api'
import { useApp } from '../App'
import { ShareDialog } from '../calendar/ShareDialog'
import { finishToFiles, uploadChunked } from '../files/chunked'
import { t } from '../i18n'
import { EntryEditor, fmtCoords } from './EntryEditor'
import { EntryView } from './EntryView'
import { MediaLightbox } from './MediaLightbox'
import { useHistoryClose } from '../shell/mobile'
import { dayOf, Entry, ImportStatus, inTresor, TRESOR_ANY, isImage, isVideo, Journal, journalApi, Media, moodEmoji, MOODS, OnThisDay, Preview, Settings, thumbUrl } from './api'
import { PickFileDialog } from '../files/PickFileDialog'
import { parentOf } from '../files/common'
import { askConfirm } from '../shell/context'
import { OPEN_COLOR, requestTresorOpen, TRESOR_EVENT, useTresor } from '../tresor/tresor'

const JournalAtlas = lazy(() => import('./JournalAtlas'))

type View = 'timeline' | 'calendar' | 'media' | 'onthisday' | 'mood' | 'atlas'

const useStyles = makeStyles({
  // flex: 1 - <main> ist eine Flex-Zeile, ohne wäre das Tagebuch nur so breit wie sein Inhalt
  root: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0, height: '100%', minHeight: 0 },
  bar: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap', padding: '10px 16px', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  filters: { display: 'flex', alignItems: 'center', gap: '6px', flexWrap: 'wrap', padding: '6px 16px' },
  scroll: { flex: 1, overflowY: 'auto', padding: '8px 16px 32px' },
  // Zeitleiste: eine lesbare Spalte statt über die ganze Breite
  column: { maxWidth: '760px', margin: '0 auto' },
  // Kacheln: mehrere Einträge nebeneinander, Monats-/Rückblick-Überschrift über die ganze Breite
  tiles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(300px, 1fr))', gap: '16px', alignItems: 'start' },
  tileHead: { gridColumn: '1 / -1', marginBottom: '-6px' },
  storyTile: { marginBottom: 0 },
  photosTile: { height: '220px', '@media (max-width: 520px)': { height: '200px' } },
  month: { margin: '18px 0 6px', fontWeight: tokens.fontWeightSemibold, color: tokens.colorNeutralForeground2 },
  monthBig: { margin: '28px 0 10px', fontSize: tokens.fontSizeBase500, fontWeight: tokens.fontWeightSemibold },
  card: {
    display: 'flex', gap: '14px', padding: '12px', marginBottom: '8px', borderRadius: tokens.borderRadiusLarge, cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow2, ':hover': { boxShadow: tokens.shadow8 },
  },
  story: {
    marginBottom: '16px', borderRadius: tokens.borderRadiusXLarge, overflow: 'hidden', cursor: 'pointer',
    backgroundColor: tokens.colorNeutralBackground1, boxShadow: tokens.shadow4, ':hover': { boxShadow: tokens.shadow16 },
    ':focus-visible': { outline: `2px solid ${tokens.colorBrandStroke1}` },
  },
  photos: { display: 'grid', gap: '2px', height: '320px', '@media (max-width: 520px)': { height: '240px' } },
  photos1: { gridTemplateColumns: '1fr' },
  photos2: { gridTemplateColumns: '1fr 1fr' },
  photos3: { gridTemplateColumns: '2fr 1fr', gridTemplateRows: '1fr 1fr' },
  photoBig: { gridRow: '1 / span 2' },
  photo: { position: 'relative', overflow: 'hidden', backgroundColor: tokens.colorNeutralBackground3 },
  photoBlur: { position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'cover', filter: 'blur(18px) brightness(0.7)', transform: 'scale(1.15)' },
  photoWhole: { position: 'relative', width: '100%', height: '100%', objectFit: 'contain', display: 'block' },
  storyBody: { padding: '12px 16px 14px', display: 'flex', flexDirection: 'column', gap: '6px' },
  // Mit Fotos: Angaben unten auf den Fotos, auf einem Verlauf ins Dunkle (wie bei Journey)
  photoWrap: { position: 'relative' },
  overlay: {
    position: 'absolute', left: 0, right: 0, bottom: 0, padding: '56px 16px 12px', display: 'flex', flexDirection: 'column', gap: '4px',
    background: 'linear-gradient(to bottom, rgba(0,0,0,0) 0%, rgba(0,0,0,0.55) 45%, rgba(0,0,0,0.82) 100%)', color: 'rgba(255,255,255,0.95)', pointerEvents: 'none',
    '& .fui-Text': { color: 'inherit' }, textShadow: '0 1px 2px rgba(0,0,0,0.6)',
  },
  overlayHead: { display: 'flex', alignItems: 'baseline', gap: '8px', fontSize: tokens.fontSizeBase200, color: 'rgba(255,255,255,0.85)' },
  overlayText: { color: 'rgba(255,255,255,0.9)', display: '-webkit-box', WebkitLineClamp: 2, WebkitBoxOrient: 'vertical', overflow: 'hidden', whiteSpace: 'pre-line' },
  overlayMeta: { display: 'flex', gap: '10px', flexWrap: 'wrap', alignItems: 'center', fontSize: tokens.fontSizeBase200, color: 'rgba(255,255,255,0.85)' },
  tagOnPhoto: { padding: '0 6px', borderRadius: '6px', backgroundColor: 'rgba(255,255,255,0.18)' },
  storyHead: { display: 'flex', alignItems: 'baseline', gap: '8px', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  storyTitle: { fontSize: tokens.fontSizeBase400, fontWeight: tokens.fontWeightSemibold, lineHeight: tokens.lineHeightBase400 },
  storyText: { color: tokens.colorNeutralForeground2, display: '-webkit-box', WebkitLineClamp: 4, WebkitBoxOrient: 'vertical', overflow: 'hidden', whiteSpace: 'pre-line' },
  moreBig: { position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', backgroundColor: 'rgba(0,0,0,0.45)', color: 'white', fontSize: '22px', fontWeight: 600 },
  day: { width: '48px', flexShrink: 0, textAlign: 'center' },
  dayNum: { fontSize: '26px', fontWeight: tokens.fontWeightSemibold, lineHeight: 1.1, color: tokens.colorBrandForeground1 },
  dayName: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3 },
  body: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column', gap: '4px' },
  title: { fontWeight: tokens.fontWeightSemibold },
  snippet: { color: tokens.colorNeutralForeground2, display: '-webkit-box', WebkitLineClamp: 3, WebkitBoxOrient: 'vertical', overflow: 'hidden', whiteSpace: 'pre-line' },
  meta: { display: 'flex', gap: '10px', flexWrap: 'wrap', color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200, alignItems: 'center' },
  thumb: { position: 'relative', width: '96px', height: '96px', flexShrink: 0, borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', '@media (max-width: 520px)': { width: '72px', height: '72px' } },
  thumbImg: { width: '100%', height: '100%', objectFit: 'cover' },
  more: { position: 'absolute', right: '4px', bottom: '4px', padding: '0 6px', borderRadius: '8px', backgroundColor: 'rgba(0,0,0,0.55)', color: 'white', fontSize: '12px' },
  // So breit, dass sechs Wochen mit quadratischen Tagen ohne Rollen in die Höhe passen (Kopf, Menüband, Leisten ~260 px)
  cal: { display: 'grid', gridTemplateColumns: 'repeat(7, minmax(0, 1fr))', gap: '4px', maxWidth: 'max(420px, calc((100vh - 260px) / 6 * 7))', margin: '0 auto' },
  calBar: { display: 'flex', alignItems: 'center', gap: '8px', margin: '8px auto', maxWidth: 'max(420px, calc((100vh - 260px) / 6 * 7))' },
  calHead: { textAlign: 'center', fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, padding: '4px 0' },
  calCell: {
    aspectRatio: '1', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground1, position: 'relative', overflow: 'hidden',
    display: 'flex', alignItems: 'flex-start', justifyContent: 'flex-start', padding: '4px', fontSize: tokens.fontSizeBase200, border: 'none', cursor: 'default', color: 'inherit',
  },
  calHas: { cursor: 'pointer', outline: `2px solid ${tokens.colorBrandStroke2}` },
  // Tag ohne Eintrag: als Kasten erkennbar, beim Überfahren ein „+“ – ein Klick legt dort einen Eintrag an
  calEmpty: {
    cursor: 'pointer', outline: `1px solid ${tokens.colorNeutralStroke2}`,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover, outline: `1px solid ${tokens.colorBrandStroke2}` },
    ':hover .dm-cal-plus': { opacity: 1 },
  },
  calPlus: { position: 'absolute', inset: 0, display: 'flex', alignItems: 'center', justifyContent: 'center', fontSize: '24px', color: tokens.colorBrandForeground1, opacity: 0, transition: 'opacity .1s', pointerEvents: 'none' },
  calImg: { position: 'absolute', inset: 0, width: '100%', height: '100%', objectFit: 'cover', opacity: 0.85 },
  calNum: { position: 'relative', zIndex: 1, padding: '0 4px', borderRadius: '6px', backgroundColor: tokens.colorNeutralBackground1, whiteSpace: 'nowrap' },
  // Tag nur mit Text: Titel bzw. Textanfang in der Zelle
  calText: {
    position: 'absolute', left: '6px', right: '6px', top: '28px', bottom: '6px', overflow: 'hidden', textAlign: 'left', fontSize: tokens.fontSizeBase100,
    lineHeight: tokens.lineHeightBase200, color: tokens.colorNeutralForeground2, display: '-webkit-box', WebkitLineClamp: 4, WebkitBoxOrient: 'vertical',
  },
  mediaGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(120px, 1fr))', gap: '6px' },
  mediaTile: { aspectRatio: '1', borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', cursor: 'pointer', position: 'relative' },
  empty: { margin: '48px auto', textAlign: 'center', color: tokens.colorNeutralForeground3, maxWidth: '420px' },
  chart: { width: '100%', maxWidth: '900px', height: 'auto', display: 'block' },
})

const fmtMonth = (d: Date) => d.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })
const sameDay = (a: Date, b: Date) => a.getFullYear() === b.getFullYear() && a.getMonth() === b.getMonth() && a.getDate() === b.getDate()

/** Tagebuch (Version 2): Zeitleiste, Kalender, Medien, Heute vor …, Stimmung, Atlas; schreiben, teilen, drucken, Diarium importieren. */
export default function JournalView() {
  const s = useStyles()
  const { fail } = useApp()
  const [journals, setJournals] = useState<Journal[] | null>(null)
  const [journal, setJournal] = useState<number | null>(null)
  const [settings, setSettings] = useState<Settings | null>(null)
  const [view, setView] = useState<View>('timeline')
  // Zeitleiste und „Heute vor …“ als Kacheln (nur in diesem Browser gemerkt)
  const [tiles, setTiles] = useState(() => { try { return localStorage.getItem('dmoffice.journal.tiles') === '1' } catch { return false } })
  const toggleTiles = () => setTiles(v => { try { localStorage.setItem('dmoffice.journal.tiles', v ? '0' : '1') } catch { /* egal */ } return !v })
  const [query, setQuery] = useState('')
  const [tag, setTag] = useState<string | null>(null)
  const [mood, setMood] = useState<number | null>(null)
  const [day, setDay] = useState<Date | null>(null)
  const [list, setList] = useState<Preview[] | null>(null)
  const [complete, setComplete] = useState(false)
  const [tags, setTags] = useState<{ tag: string; count: number }[]>([])
  const [editing, setEditing] = useState<Entry | 'new' | null>(null)
  // Erst ansehen, Bearbeiten auf Wunsch
  const [viewing, setViewing] = useState<Entry | null>(null)
  // Ein Verlaufsschritt für den offenen Eintrag (Ansicht und Bearbeiten): Zurück im Browser schließt ihn
  const editorCancel = useRef<(() => boolean | Promise<boolean>) | null>(null)
  // Tag aus dem Kalender gewählt: Filter weg führt zurück in den Kalender
  const [fromView, setFromView] = useState<View | null>(null)
  const fromCalendar = fromView != null
  const setFromCalendar = (on: boolean) => setFromView(on ? 'calendar' : null)
  const backToCalendar = () => { setDay(null); const v = fromView ?? 'calendar'; setFromView(null); setView(v) }
  useHistoryClose(fromCalendar && view === 'timeline', backToCalendar)
  // Neuer Eintrag aus dem Kalender: für diesen Tag
  const [newDay, setNewDay] = useState<Date | null>(null)
  // Auswählen in der Zeitleiste (zum Verschieben); moving: diese Einträge ins Ziel wählen
  const [selecting, setSelecting] = useState(false)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [moving, setMoving] = useState<{ ids: string[]; from: number[] } | null>(null)
  const stopSelecting = () => { setSelecting(false); setSelected(new Set()) }
  // Neuer Eintrag in ein anderes Tagebuch als das gewählte (bei geschlossenem Tresor: „irgendeins im Tresor“)
  const [newIn, setNewIn] = useState<number | null>(null)
  // Aus der Übersicht: #/journal/2026-10-25 zeigt den Tag (mit /<Eintrag> hervorgehoben), #/journal/new öffnet einen neuen Eintrag für heute,
  // #/journal/onthisday/<Eintrag> „Heute vor …“ mit allen Rückblicken (der angeklickte hervorgehoben)
  const [highlight, setHighlight] = useState<string | null>(null)
  useEffect(() => {
    const open = () => {
      // Aus dem Chat (Pukki): #/journal/entry/<tagebuch>/<uri> öffnet den Eintrag
      const e = location.hash.match(/^#\/journal\/entry\/(-?\d+)\/(.+)$/)
      if (e) {
        history.replaceState(null, '', '#/journal')
        journalApi.entry(Number(e[1]), decodeURIComponent(e[2])).then(setViewing).catch(fail)
        return
      }
      const m = location.hash.match(/^#\/journal\/(\d{4}-\d{2}-\d{2}|new|onthisday)(?:\/(.+))?$/)
      if (!m) return
      history.replaceState(null, '', '#/journal')
      if (m[1] === 'new') { setNewDay(null); setEditing('new'); return }
      if (m[1] === 'onthisday') { setView('onthisday'); setHighlight(m[2] ? decodeURIComponent(m[2]) : null); return }
      setView('timeline'); setDay(new Date(m[1] + 'T12:00:00')); setHighlight(m[2] ? decodeURIComponent(m[2]) : null)
    }
    open()
    window.addEventListener('hashchange', open)
    return () => window.removeEventListener('hashchange', open)
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  // Kalender erst beim ersten Öffnen laden; version lädt ihn nach Änderungen neu
  const [calendarSeen, setCalendarSeen] = useState(false)
  useEffect(() => { if (view === 'calendar') setCalendarSeen(true) }, [view])
  const [version, setVersion] = useState(0)
  const [dialog, setDialog] = useState<'import' | 'export' | 'journal' | 'rename' | 'settings' | 'share' | null>(null)

  const loadJournals = useCallback(() => journalApi.journals().then(setJournals).catch(fail), [fail])
  useEffect(() => { loadJournals(); journalApi.settings().then(setSettings).catch(() => {}) }, [loadJournals])
  // Tresor auf- oder zugegangen: Tagebücher im Tresor erscheinen bzw. verschwinden
  const tresor = useTresor()
  useEffect(() => {
    window.addEventListener(TRESOR_EVENT, loadJournals)
    return () => window.removeEventListener(TRESOR_EVENT, loadJournals)
  }, [loadJournals])
  useEffect(() => { if (journal != null && journals && !journals.some(j => j.id === journal)) setJournal(null) }, [journals, journal])

  const params = useMemo(() => {
    const p: Parameters<typeof journalApi.entries>[0] = { journal: journal ?? undefined, q: query.trim() || undefined, tag: tag ?? undefined, mood: mood ?? undefined, limit: 100 }
    if (day) { const from = new Date(day); from.setHours(0, 0, 0, 0); const to = new Date(from); to.setDate(to.getDate() + 1); p.from = from.toISOString(); p.to = to.toISOString() }
    return p
  }, [journal, query, tag, mood, day])

  const load = useCallback(() => {
    journalApi.entries(params).then(r => { setList(r); setComplete(r.length < 100) }).catch(fail)
    // Schlagwörter: im Tresor nur die des gewählten Tagebuchs, sonst die aller Tagebücher im offenen Bereich
    journalApi.tags(inTresor(params.journal) ? params.journal : undefined).then(setTags).catch(() => {})
  }, [params, fail])
  useEffect(() => { const timer = setTimeout(load, query ? 250 : 0); return () => clearTimeout(timer) }, [load, query])

  // Import im Hintergrund: Hinweis mit Fortschritt; am Ende die Einträge neu laden
  const [importing, setImporting] = useState<ImportStatus | null>(null)
  useEffect(() => {
    let alive = true
    let timer: number | undefined
    const poll = () => journalApi.importStatus().then(st => {
      if (!alive) return
      setImporting(st.running ? st : null)
      if (st.running) timer = window.setTimeout(poll, 2000)
      else if (st.finished && timer !== undefined) { load(); loadJournals() }
    }).catch(() => {})
    poll()
    return () => { alive = false; clearTimeout(timer) }
  }, [dialog]) // eslint-disable-line react-hooks/exhaustive-deps

  const resetFilters = () => { setQuery(''); setTag(null); setMood(null); setDay(null); setFromCalendar(false) }
  // Rollposition je Ansicht merken; neue Filter beginnen oben
  const scroller = useRef<HTMLDivElement>(null)
  const scrollPos = useRef<Record<string, number>>({})
  const shownView = useRef(view)
  useEffect(() => {
    const el = scroller.current
    if (!el || shownView.current === view) return
    scrollPos.current[shownView.current] = el.scrollTop
    shownView.current = view
    requestAnimationFrame(() => { el.scrollTop = scrollPos.current[view] ?? 0 })
  }, [view])
  useEffect(() => { if (scroller.current && view === 'timeline') scroller.current.scrollTop = 0 }, [query, tag, mood, day]) // eslint-disable-line react-hooks/exhaustive-deps
  // Ansicht Medien: Foto groß, blättern durch alle gezeigten
  const [mediaBig, setMediaBig] = useState<{ list: { entry: string; journal: number; media: Media }[]; index: number } | null>(null)

  const more = () => {
    const last = list?.[list.length - 1]
    if (!last) return
    journalApi.entries({ ...params, before: last.date }).then(r => { setList(l => [...(l ?? []), ...r]); setComplete(r.length < 100) }).catch(fail)
  }

  const open = useCallback(async (p: { journal: number; uri: string }) => {
    try { setViewing(await journalApi.entry(p.journal, p.uri)) } catch (e) { fail(e) }
  }, [fail])
  const current = journals?.find(j => j.id === journal)
  // Eigene im offenen Bereich (das letzte davon lässt sich nicht löschen)
  const ownJournals = (journals ?? []).filter(j => !j.owner && !j.tresor)
  // Eigenes Tagebuch zum Teilen, Umbenennen, Löschen, Verschieben: das gewählte - oder das einzige, wenn es nur eins gibt
  const mine = current && !current.owner ? current : !current && ownJournals.length === 1 ? ownJournals[0] : null
  const moveArea = async () => {
    if (!mine) return
    const shared = !mine.tresor && (mine.sharedWith?.length ?? 0) > 0
    const text = mine.tresor
      ? t('„{name}“ kommt mit allen Einträgen, Fotos und Dateien aus dem Tresor in den offenen Bereich; dort liegt es unverschlüsselt.', { name: mine.name })
      : (shared ? t('Tagebücher im Tresor können nicht geteilt werden. Alle Freigaben dieses Tagebuchs werden entfernt.') + ' ' : '')
        + t('„{name}“ kommt mit allen Einträgen, Fotos und Dateien in den Tresor und wird im offenen Bereich endgültig gelöscht. Aus der Dateiablage angehängte Dateien kommen als Kopie mit, das Original bleibt.', { name: mine.name })
    if (!await askConfirm(mine.tresor ? t('Aus dem Tresor holen') : t('In den Tresor verschieben'), text, mine.tresor ? t('Aus dem Tresor holen') : t('In den Tresor'))) return
    try {
      const r = await journalApi.moveJournal(mine.id, mine.tresor ? 'open' : 'tresor', shared)
      await loadJournals(); setJournal(r.id); load()
    } catch (e) { fail(e) }
  }
  const viewName = ({ timeline: t('Zeitleiste'), calendar: t('Kalender'), media: t('Medien'), onthisday: t('Heute vor …'), mood: t('Stimmung'), atlas: t('Atlas') } as Record<View, string>)[view]
  // Neuer Eintrag: ins gewählte Tagebuch, wenn man darin schreiben darf, sonst ins erste eigene
  const target = current?.canWrite ? current.id : journals?.find(j => !j.owner && !j.tresor)?.id ?? 0

  return (
    <div className={s.root}>
      {/* Menüband wie in den anderen Bereichen: Start (Einträge, Tagebücher) und Ansicht */}
      <RibbonHeader area="journal"
        start={<>
          <RibbonButton primary icon={<Add20Regular />} label={t('Neuer Eintrag')} disabled={!journals} onClick={() => { setNewDay(null); setEditing('new') }} />
          <RibbonDivider />
          <RibbonButton icon={<CheckboxChecked20Regular />} label={t('Auswählen')} pressed={selecting} disabled={view !== 'timeline'}
            title={t('Einträge auswählen, um sie in ein anderes Tagebuch zu verschieben')} onClick={() => selecting ? stopSelecting() : setSelecting(true)} />
          <RibbonDivider />
          <RibbonButton icon={<NotebookAdd20Regular />} label={t('Neues Tagebuch')} onClick={() => setDialog('journal')} />
          <RibbonButton icon={<Share20Regular />} label={t('Teilen')} disabled={!mine || mine.tresor}
            title={mine?.tresor ? t('Tagebücher im Tresor können nicht geteilt werden.') : mine ? t('„{name}“ teilen', { name: mine.name }) : t('Erst oben ein eigenes Tagebuch wählen')} onClick={() => setDialog('share')} />
          <RibbonButton icon={<Rename20Regular />} label={t('Umbenennen')} disabled={!mine} onClick={() => setDialog('rename')} />
          {tresor?.exists && <RibbonButton icon={mine?.tresor ? <LockOpen20Regular /> : <LockClosed20Regular />}
            label={mine?.tresor ? t('Aus dem Tresor holen') : t('In den Tresor verschieben')} disabled={!mine}
            title={mine ? undefined : t('Erst oben ein eigenes Tagebuch wählen')} onClick={moveArea} />}
          {current?.owner
            ? <RibbonButton icon={<EyeOff20Regular />} label={t('Nicht mehr anzeigen')} onClick={async () => {
                if (!await askConfirm('', t('„{name}“ nicht mehr anzeigen? {owner} kann es wieder mit dir teilen.', { name: current.name, owner: current.owner! }))) return
                try { await journalApi.leaveJournal(current.id); setJournal(null); loadJournals(); load() } catch (e) { fail(e) }
              }} />
            : <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!mine || (!mine.tresor && ownJournals.length < 2)}
                title={mine && !mine.tresor && ownJournals.length < 2 ? t('Das einzige eigene Tagebuch lässt sich nicht löschen.') : undefined} onClick={async () => {
                if (!mine || !await askConfirm('', mine.tresor ? t('„{name}“ mit allen Einträgen, Fotos und Dateien endgültig löschen?', { name: mine.name })
                  : t('„{name}“ mit allen Einträgen löschen? Fotos und Dateien bleiben in der Dateiablage.', { name: mine.name }))) return
                try { await journalApi.deleteJournal(mine.id); setJournal(null); loadJournals(); load() } catch (e) { fail(e) }
              }} />}
          <RibbonDivider />
          <RibbonButton icon={<ArrowImport20Regular />} label={t('Importieren')} title={t('Aus einem Export von dmOffice, Diarium oder Journey importieren …')} onClick={() => setDialog('import')} />
          <RibbonButton icon={<ArrowExport20Regular />} label={t('Exportieren')} title={t('Als ZIP mit Markdown, Fotos und Videos exportieren …')} onClick={() => setDialog('export')} />
          <RibbonButton icon={<Print20Regular />} label={t('Drucken')} title={t('Drucken oder als PDF …')} onClick={() => printEntries(params, fail)} />
          <RibbonDivider />
          <RibbonButton icon={<Settings20Regular />} label={t('Einstellungen')} onClick={() => setDialog('settings')} />
        </>}
        view={<>
          {([
            ['timeline', <Timeline20Regular />, t('Zeitleiste')], ['calendar', <CalendarLtr20Regular />, t('Kalender')], ['media', <Image20Regular />, t('Medien')],
            ['onthisday', <History20Regular />, t('Heute vor …')], ['mood', <Emoji20Regular />, t('Stimmung')], ['atlas', <Map20Regular />, t('Atlas')],
          ] as [View, JSX.Element, string][]).map(([v, icon, label]) => (
            <RibbonButton key={v} icon={icon} label={label} pressed={view === v} onClick={() => { setView(v); setFromCalendar(false); if (v !== 'timeline' && v !== 'media') setDay(null) }} />
          ))}
          <RibbonDivider />
          <RibbonButton icon={<Grid20Regular />} label={t('Kacheln')} pressed={tiles} disabled={view !== 'timeline' && view !== 'onthisday'} onClick={toggleTiles} />
        </>} />
      <div className={s.bar}>
        {current?.tresor && <LockOpen20Regular color={OPEN_COLOR} aria-label={t('Tresor')} />}
        {journals && (journals.length > 1 || tresor?.exists) && (
          <Dropdown value={current ? current.name + (current.owner ? ` (${current.owner})` : '') : t('Alle Tagebücher')} selectedOptions={[String(journal ?? '')]}
            onOptionSelect={(_, d) => {
              if (d.optionValue === 'open-tresor') { requestTresorOpen(); return }
              if (d.optionValue === 'new-tresor') { setNewIn(TRESOR_ANY); setNewDay(null); setEditing('new'); return }
              setJournal(d.optionValue ? Number(d.optionValue) : null)
            }} style={{ minWidth: 160 }} aria-label={t('Tagebuch')}>
            <Option value="">{t('Alle Tagebücher')}</Option>
            {journals.filter(j => !j.tresor).map(j => <Option key={j.id} value={String(j.id)} text={j.name}>{j.name}{j.owner ? ` (${j.owner})` : ''}</Option>)}
            {tresor?.exists && (
              <OptionGroup label={t('Tresor')}>
                {tresor.open
                  ? journals.filter(j => j.tresor).map(j => <Option key={j.id} value={String(j.id)} text={j.name}>{j.name}</Option>)
                  : [<Option key="open" value="open-tresor" text={t('Tresor öffnen …')}>{t('Tresor öffnen …')}</Option>,
                    <Option key="new" value="new-tresor" text={t('Neuer Eintrag im Tresor …')}>{t('Neuer Eintrag im Tresor …')}</Option>]}
              </OptionGroup>
            )}
          </Dropdown>
        )}
        <Text weight="semibold">{viewName}</Text>
        <div style={{ flex: 1 }} />
        {view === 'timeline' && <SearchBox placeholder={t('Tagebuch durchsuchen')} value={query} onChange={(_, d) => setQuery(d.value)} style={{ minWidth: 180 }} />}
      </div>

      {selecting && (
        <MessageBar intent="info">
          <MessageBarBody style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
            <span>{selected.size === 0 ? t('Einträge anklicken, um sie auszuwählen.') : t('{n} ausgewählt', { n: selected.size })}</span>
            <Button size="small" appearance="primary" icon={<ArrowMove20Regular />} disabled={selected.size === 0}
              onClick={() => setMoving({ ids: [...selected], from: [...new Set((list ?? []).filter(e => selected.has(e.id)).map(e => e.journal))] })}>{t('Verschieben …')}</Button>
            <Button size="small" onClick={stopSelecting}>{t('Fertig')}</Button>
          </MessageBarBody>
        </MessageBar>
      )}
      {importing && (
        <MessageBar intent="info">
          <MessageBarBody>
            {t('Import läuft im Hintergrund: {done} von {total} Einträgen.', { done: importing.done, total: importing.total || '…' })}
            <ProgressBar value={importing.total ? importing.done / importing.total : undefined} style={{ marginTop: 4 }} />
          </MessageBarBody>
        </MessageBar>
      )}

      {(view === 'timeline' || view === 'media') && (tags.length > 0 || mood || day) && (
        <div className={s.filters}>
          {day && fromCalendar && <Button size="small" icon={<ChevronLeft20Regular />} onClick={backToCalendar}>{fromView === 'mood' ? t('Zurück zur Stimmung') : t('Zurück zum Kalender')}</Button>}
          {day && <Button size="small" appearance="primary" icon={<Dismiss16Regular />} iconPosition="after"
            onClick={() => { if (fromCalendar) backToCalendar(); else setDay(null) }}>{day.toLocaleDateString()}</Button>}
          {(query || tag || mood || day) && !fromCalendar && <Button size="small" appearance="subtle" onClick={resetFilters}>{t('Filter zurücksetzen')}</Button>}
          {MOODS.map(m => (
            <Button key={m.value} size="small" appearance={mood === m.value ? 'primary' : 'subtle'} onClick={() => setMood(mood === m.value ? null : m.value)} aria-pressed={mood === m.value} style={{ minWidth: 32, padding: '0 6px', fontSize: 16 }}>{m.emoji}</Button>
          ))}
          {tags.slice(0, 20).map(x => (
            <Button key={x.tag} size="small" shape="circular" appearance={tag === x.tag ? 'primary' : 'outline'} onClick={() => setTag(tag === x.tag ? null : x.tag)}>#{x.tag}</Button>
          ))}
        </div>
      )}

      <div className={s.scroll} ref={scroller}>
        {view === 'timeline' && <Timeline list={list} complete={complete} onMore={more}
          onOpen={selecting ? p => { if (!p.owner) setSelected(x => { const n = new Set(x); if (n.has(p.id)) n.delete(p.id); else n.add(p.id); return n }) } : open}
          selected={selecting ? selected : undefined} onNew={() => { setNewDay(null); setEditing('new') }} filtered={!!(query || tag || mood || day)} onReset={resetFilters} highlight={highlight} tiles={tiles} />}
        {/* Bleibt geladen (nur versteckt): zurück aus einem Tag steht der Kalender wieder an derselben Stelle */}
        {calendarSeen && <div style={view === 'calendar' ? undefined : { display: 'none' }}>
          <MonthView journal={journal} version={version}
            onDay={(d, here) => { if (here.length === 1) open(here[0]); else { setQuery(''); setTag(null); setMood(null); setDay(d); setFromCalendar(true); setView('timeline') } }}
            onNew={d => { setNewDay(d); setEditing('new') }} />
        </div>}
        {view === 'media' && <MediaView journal={journal} tag={tag} mood={mood} day={day} onShow={(list, index) => setMediaBig({ list, index })} />}
        {view === 'onthisday' && <OnThisDayView onOpen={open} highlight={highlight} tiles={tiles} />}
        {view === 'mood' && <MoodView journal={journal} onDay={d => { setQuery(''); setTag(null); setMood(null); setDay(d); setFromView('mood'); setView('timeline') }} />}
        {view === 'atlas' && <Suspense fallback={<Spinner style={{ marginTop: 32 }} />}><JournalAtlas journal={journal} onOpen={open} /></Suspense>}
      </div>

      <EntryBack open={!!viewing || !!editing} onBack={() => {
        if (editing) return editorCancel.current ? editorCancel.current() : (setEditing(null), true)
        setViewing(null)
      }} />
      {mediaBig && (
        <MediaLightbox journal={mediaBig.list[0]?.journal ?? 0} journals={mediaBig.list.map(x => x.journal)} items={mediaBig.list.map(x => x.media)}
          index={mediaBig.index} onIndex={i => setMediaBig(b => b && { ...b, index: i })} onClose={() => setMediaBig(null)}
          onEntry={i => { const x = mediaBig.list[i]; setMediaBig(null); open({ journal: x.journal, uri: x.entry.slice(x.entry.indexOf('/') + 1) }) }} />
      )}
      {viewing && (
        <EntryView entry={viewing} own={!viewing.owner}
          onMove={!viewing.owner ? () => setMoving({ ids: [viewing.id], from: [viewing.journal] }) : undefined}
          onEdit={viewing.canWrite ? () => { setEditing(viewing); setViewing(null) } : undefined}
          onClose={() => setViewing(null)} />
      )}
      {editing && (
        <EntryEditor journal={editing === 'new' ? newIn ?? target : target} entry={editing === 'new' ? null : editing} forDay={editing === 'new' ? newDay : null} lookup={!!settings?.lookup}
          own={editing === 'new' ? !journals?.find(j => j.id === target)?.owner : !editing.owner} cancelRef={editorCancel}
          onClose={changed => {
            const was = editing
            setEditing(null); setNewIn(null)
            if (changed) { load(); loadJournals(); setVersion(v => v + 1) }
            // Bestehender Eintrag: zurück in die Ansicht (gleich, damit der Eintrag offen bleibt; gelöscht: zu)
            if (was !== 'new') {
              setViewing(was)
              if (changed) journalApi.entry(was.journal, was.uri).then(setViewing).catch(() => setViewing(null))
            }
          }} />
      )}
      {(dialog === 'journal' || dialog === 'rename') && (
        <NameDialog title={dialog === 'journal' ? t('Neues Tagebuch') : t('Tagebuch umbenennen')} initial={dialog === 'rename' ? mine?.name ?? '' : ''} onClose={() => setDialog(null)}
          canTresor={dialog === 'journal' && !!tresor?.exists}
          onSave={async (name, inVault) => {
            try {
              if (dialog === 'journal') { const r = await journalApi.createJournal(name, null, inVault); await loadJournals(); setJournal(r.id) } else if (mine) await journalApi.renameJournal(mine.id, name, mine.color)
              setDialog(null); loadJournals()
            } catch (e) { fail(e) }
          }} />
      )}
      {dialog === 'share' && mine && !mine.tresor && (
        <ShareDialog list={{ id: mine.id, name: mine.name, components: mine.components, sharedWith: mine.sharedWith ?? undefined }}
          onClose={changed => { setDialog(null); if (changed) loadJournals() }} />
      )}
      {moving && journals && <MoveDialog count={moving.ids.length} journals={journals} from={moving.from} tresorExists={!!tresor?.exists} tresorOpen={!!tresor?.open}
        onClose={() => setMoving(null)}
        onMove={async to => {
          try {
            const r = await journalApi.moveEntries(moving.ids, to)
            setMoving(null); stopSelecting()
            if (viewing) setViewing(r[0] ?? null)
            load(); loadJournals(); setVersion(v => v + 1)
          } catch (e) { fail(e) }
        }} />}
      {dialog === 'settings' && <SettingsDialog onClose={s2 => { setDialog(null); if (s2) setSettings(s2) }} />}
      {dialog === 'export' && <ExportDialog journal={current && !current.showOnly ? current.id : (journals ?? []).find(j => !j.owner)?.id ?? 0}
        journals={(journals ?? []).filter(j => !j.showOnly)} onClose={() => setDialog(null)} />}
      {dialog === 'import' && <ImportDialog journal={ownJournals[0]?.id ?? 0} journals={ownJournals} onClose={changed => { setDialog(null); if (changed) { load(); loadJournals() } }} />}
    </div>
  )
}

function Timeline({ list, complete, onMore, onOpen, onNew, filtered, onReset, highlight, tiles, selected }: {
  list: Preview[] | null; complete: boolean; onMore: () => void; onOpen: (p: Preview) => void; onNew: () => void; filtered: boolean; onReset: () => void
  /** Auswählen: die gewählten Einträge (Klick wählt statt zu öffnen; Einträge anderer lassen sich nicht wählen) */
  selected?: Set<string>
  /** Eintrag, der aus der Übersicht kam: hervorgehoben und ins Bild gerollt */
  highlight?: string | null
  tiles?: boolean
}) {
  const s = useStyles()
  const marked = useRef<HTMLDivElement>(null)
  useEffect(() => { marked.current?.scrollIntoView({ block: 'center', behavior: 'smooth' }) }, [highlight, list])
  if (!list) return <Spinner style={{ marginTop: 32 }} />
  if (!list.length) return (
    <div className={s.empty}>
      {filtered ? <><Text block>{t('Dazu gibt es keinen Eintrag.')}</Text><Button style={{ marginTop: 12 }} onClick={onReset}>{t('Filter zurücksetzen')}</Button></> : <>
        <Text size={500} block>{t('Dein Tagebuch ist noch leer.')}</Text>
        <Text block style={{ margin: '8px 0 16px' }}>{t('Schreib auf, was dich heute bewegt hat – mit Fotos, Ort und Stimmung.')}</Text>
        <Button appearance="primary" icon={<Add20Regular />} onClick={onNew}>{t('Ersten Eintrag schreiben')}</Button>
      </>}
    </div>
  )
  let lastMonth = ''
  return (
    <div className={tiles ? s.tiles : s.column}>
      {list.flatMap(e => {
        const d = new Date(e.date)
        const month = fmtMonth(d)
        const head = month !== lastMonth ? (lastMonth = month, <div key={'m' + e.id} className={mergeClasses(s.monthBig, tiles && s.tileHead)}>{month}</div>) : null
        const on = highlight != null && e.id === highlight
        const picked = selected?.has(e.id)
        const card = (
          <div key={e.id} ref={on ? marked : undefined} style={{
            position: 'relative', borderRadius: 12, opacity: selected && e.owner ? 0.5 : undefined,
            ...(on || picked ? { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: 4 } : {}),
          }}>
            <StoryCard e={e} onOpen={() => onOpen(e)} tile={tiles} />
            {selected && !e.owner && <span style={{ position: 'absolute', right: 10, top: 10, pointerEvents: 'none', color: picked ? tokens.colorBrandForeground1 : tokens.colorNeutralForeground3, background: tokens.colorNeutralBackground1, borderRadius: '50%', display: 'flex' }}>
              {picked ? <CheckmarkCircle20Filled /> : <Circle20Regular />}</span>}
          </div>
        )
        return head ? [head, card] : [card]
      })}
      {!complete && <Button appearance="subtle" onClick={onMore} style={{ display: 'block', margin: '12px auto', gridColumn: '1 / -1' }}>{t('Ältere Einträge laden')}</Button>}
    </div>
  )
}

/** Eintrag in der Zeitleiste: Fotos oben groß (bis zu drei), darunter Datum, Titel, Anfang des Texts, Ort, Wetter, Schlagwörter. */
/** tile: als Kachel - ein Foto, niedriger, für mehrere Einträge nebeneinander. */
function StoryCard({ e, onOpen, tile }: { e: Preview; onOpen: () => void; tile?: boolean }) {
  const s = useStyles()
  const d = new Date(e.date)
  const pics = e.media.filter(m => isImage(m) || isVideo(m))
  // Hauptbild: das erste Querformat - Hochkant-Fotos kommen in die kleinen Kacheln (sonst bleibt vom Motiv nur die Mitte)
  const wide = pics.findIndex(m => isImage(m) && !!m.width && !!m.height && m.width >= m.height)
  const shown = (wide > 0 ? [pics[wide], ...pics.filter((_, i) => i !== wide)] : pics).slice(0, tile ? 1 : 3)
  const when = <>
    <span style={{ fontWeight: 600, color: shown.length ? '#fff' : tokens.colorBrandForeground1 }}>{d.toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long' })}</span>
    <span>{d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}</span>
    <span style={{ flex: 1 }} />
    {e.private && <LockClosed16Regular aria-label={t('Privat')} />}
    {e.mood != null && <span style={{ fontSize: 20 }} title={t('Stimmung')}>{moodEmoji(e.mood)}</span>}
  </>
  const metaItems = <>
    {e.place && (e.place.name || e.place.lat != null) && <span>📍 {e.place.name || fmtCoords(e.place)}</span>}
    {e.weather && <span>🌤 {e.weather}</span>}
    {e.owner && <span><People16Regular style={{ verticalAlign: 'middle' }} /> {e.owner}</span>}
  </>
  const hasMeta = !!(e.place || e.weather || e.tags.length > 0 || e.owner)
  return (
    <div className={mergeClasses(s.story, tile && s.storyTile)} onClick={onOpen} role="button" tabIndex={0} onKeyDown={k => k.key === 'Enter' && onOpen()}>
      {shown.length > 0 ? (
        <div className={s.photoWrap}>
          <div className={mergeClasses(s.photos, shown.length === 1 ? s.photos1 : shown.length === 2 ? s.photos2 : s.photos3, tile && s.photosTile)}>
            {shown.map((m, i) => (
              <div key={m.path} className={mergeClasses(s.photo, shown.length === 3 && i === 0 && s.photoBig)}>
                {i === 0 && isImage(m) ? <>
                  {/* Großes Foto ganz zeigen (nichts abgeschnitten), den Rand füllt es verschwommen selbst - wie in Journey */}
                  <img src={thumbUrl(e.journal, m, 1024)} alt="" aria-hidden className={s.photoBlur} loading="lazy" />
                  <img src={thumbUrl(e.journal, m, 1024)} alt="" className={s.photoWhole} loading="lazy" />
                </> : <img src={thumbUrl(e.journal, m, i === 0 ? 1024 : 512)} alt="" className={s.thumbImg} style={{ objectPosition: '50% 25%' }} loading="lazy" />}
                {isVideo(m) && <span style={{ position: 'absolute', left: 8, top: 6, color: 'white', textShadow: '0 0 4px black' }}>▶</span>}
                {i === shown.length - 1 && pics.length > shown.length && <span className={s.moreBig}>+{pics.length - shown.length}</span>}
              </div>
            ))}
          </div>
          <div className={s.overlay}>
            <div className={s.overlayHead}>{when}</div>
            {e.title && <Text className={s.storyTitle}>{e.title}</Text>}
            {e.snippet && e.snippet !== e.title && <Text className={s.overlayText}>{e.snippet}</Text>}
            {hasMeta && <div className={s.overlayMeta}>{metaItems}{e.tags.map(x => <span key={x} className={s.tagOnPhoto}>#{x}</span>)}</div>}
          </div>
        </div>
      ) : (
        <div className={s.storyBody}>
          <div className={s.storyHead}>{when}</div>
          {e.title && <Text className={s.storyTitle}>{e.title}</Text>}
          {e.snippet && e.snippet !== e.title && <Text className={s.storyText}>{e.snippet}</Text>}
          {hasMeta && <div className={s.meta}>{metaItems}{e.tags.map(x => <Badge key={x} appearance="tint" shape="rounded">#{x}</Badge>)}</div>}
        </div>
      )}
    </div>
  )
}

export function EntryCard({ e, onOpen }: { e: Preview; onOpen: () => void }) {
  const s = useStyles()
  const d = new Date(e.date)
  const pics = e.media.filter(m => isImage(m) || isVideo(m))
  return (
    <div className={s.card} onClick={onOpen} role="button" tabIndex={0} onKeyDown={k => k.key === 'Enter' && onOpen()}>
      <div className={s.day}>
        <div className={s.dayNum}>{d.getDate()}</div>
        <div className={s.dayName}>{d.toLocaleDateString(undefined, { weekday: 'short' })}</div>
        <div className={s.dayName}>{d.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit' })}</div>
      </div>
      <div className={s.body}>
        {e.title && <Text className={s.title}>{e.title}</Text>}
        {e.snippet && <Text className={s.snippet}>{e.snippet}</Text>}
        <div className={s.meta}>
          {e.mood != null && <span style={{ fontSize: 16 }}>{moodEmoji(e.mood)}</span>}
          {e.place && (e.place.name || e.place.lat != null) && <span>📍 {e.place.name || fmtCoords(e.place)}</span>}
          {e.weather && <span>🌤 {e.weather}</span>}
          {e.tags.map(x => <Badge key={x} appearance="tint" shape="rounded">#{x}</Badge>)}
          {e.owner && <span><People16Regular style={{ verticalAlign: 'middle' }} /> {e.owner}</span>}
          {e.private && <LockClosed16Regular aria-label={t('Privat')} />}
        </div>
      </div>
      {pics.length > 0 && (
        <div className={s.thumb}>
          <img src={thumbUrl(e.journal, pics[0], 256)} alt="" className={s.thumbImg} loading="lazy" />
          {pics.length > 1 && <span className={s.more}>+{pics.length - 1}</span>}
        </div>
      )}
    </div>
  )
}

/**
 * Kalender: Monate untereinander zum Durchrollen (neueste oben, ältere laden beim Rollen nach), Sprung zu Monat und Jahr.
 * Tage mit Eintrag markiert - mit dem ersten Foto des Tages, sonst mit Titel bzw. Textanfang.
 */
function MonthView({ journal, version, onDay, onNew }: {
  journal: number | null; version: number; onDay: (d: Date, here: Preview[]) => void; onNew: (d: Date) => void
}) {
  const s = useStyles()
  const now = new Date()
  const thisMonth = new Date(now.getFullYear(), now.getMonth(), 1)
  // Wie viele Monate zurück gezeigt werden (wächst beim Rollen und beim Springen)
  const [count, setCount] = useState(12)
  const [jump, setJump] = useState(() => `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}`)
  const [target, setTarget] = useState<string | null>(null)
  const months = Array.from({ length: count }, (_, i) => new Date(thisMonth.getFullYear(), thisMonth.getMonth() - i, 1))
  const end = useRef<HTMLDivElement>(null)
  useEffect(() => {
    const el = end.current
    if (!el) return
    const io = new IntersectionObserver(x => { if (x[0].isIntersecting) setCount(c => c + 12) }, { rootMargin: '600px' })
    io.observe(el)
    return () => io.disconnect()
  }, [])
  const key = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}`
  const go = (k: string) => {
    const [y, m] = k.split('-').map(Number)
    if (!y || !m) return
    const back = (thisMonth.getFullYear() - y) * 12 + (thisMonth.getMonth() - (m - 1))
    if (back < 0) return
    if (back >= count) setCount(back + 6)
    setTarget(k)
  }
  useEffect(() => {
    if (!target) return
    document.getElementById('jm-' + target)?.scrollIntoView({ block: 'start', behavior: 'smooth' })
    setTarget(null)
  }, [target, count])
  return (
    <>
      <div className={s.calBar} style={{ position: 'sticky', top: -8, zIndex: 2, backgroundColor: tokens.colorNeutralBackground2, padding: '6px 0' }}>
        <Button size="small" onClick={() => go(key(thisMonth))}>{t('Heute')}</Button>
        <div style={{ flex: 1 }} />
        <Input type="month" size="small" value={jump} aria-label={t('Monat anspringen')} max={key(thisMonth)}
          onChange={(_, d) => { setJump(d.value); if (d.value) go(d.value) }} />
      </div>
      {months.map((m, i) => <CalendarMonth key={key(m)} id={'jm-' + key(m)} eager={i < 2} month={m} journal={journal} version={version} onDay={onDay} onNew={onNew} />)}
      <div ref={end} style={{ height: 1 }} />
    </>
  )
}

/** Ein Monat im Kalender; lädt seine Einträge erst, wenn er ins Bild kommt. */
function CalendarMonth({ id, eager, month, journal, version, onDay, onNew }: {
  id: string; eager?: boolean; month: Date; journal: number | null; version: number; onDay: (d: Date, here: Preview[]) => void; onNew: (d: Date) => void
}) {
  const s = useStyles()
  const { fail } = useApp()
  const box = useRef<HTMLDivElement>(null)
  // Die obersten Monate gleich laden, die anderen, wenn sie ins Bild kommen
  const [seen, setSeen] = useState(!!eager)
  const [list, setList] = useState<Preview[]>([])
  useEffect(() => {
    const el = box.current
    if (!el || seen) return
    const io = new IntersectionObserver(x => { if (x[0].isIntersecting) setSeen(true) }, { rootMargin: '400px' })
    io.observe(el)
    return () => io.disconnect()
  }, [seen])
  useEffect(() => {
    if (!seen) return
    const to = new Date(month.getFullYear(), month.getMonth() + 1, 1)
    journalApi.entries({ journal: journal ?? undefined, from: month.toISOString(), to: to.toISOString(), limit: 1000 }).then(setList).catch(fail)
  }, [seen, month, journal, version, fail])
  const first = (month.getDay() + 6) % 7
  const days = new Date(month.getFullYear(), month.getMonth() + 1, 0).getDate()
  const names = Array.from({ length: 7 }, (_, i) => new Date(2024, 0, 1 + i).toLocaleDateString(undefined, { weekday: 'short' }))
  const today = new Date()
  return (
    <div id={id} ref={box} style={{ scrollMarginTop: 48 }}>
      <Text weight="semibold" size={400} block className={s.calBar} style={{ marginTop: 18 }}>{fmtMonth(month)}</Text>
      <div className={s.cal}>
        {names.map(n => <div key={n} className={s.calHead}>{n}</div>)}
        {Array.from({ length: first }, (_, i) => <div key={'x' + i} />)}
        {Array.from({ length: days }, (_, i) => {
          const d = new Date(month.getFullYear(), month.getMonth(), i + 1)
          const here = list.filter(e => sameDay(new Date(e.date), d))
          const withPic = here.find(e => e.media.some(m => isImage(m) || isVideo(m)))
          const pic = withPic?.media.find(m => isImage(m) || isVideo(m))
          const label = !pic ? here.map(e => e.title || e.snippet).find(Boolean) : null
          return (
            <button key={i} className={mergeClasses(s.calCell, here.length > 0 ? s.calHas : s.calEmpty)} onClick={() => (here.length ? onDay(d, here) : onNew(d))}
              style={sameDay(d, today) ? { fontWeight: 700, boxShadow: `inset 0 0 0 2px ${tokens.colorBrandForeground1}` } : undefined}
              title={here.length ? here.map(e => e.title || e.snippet).filter(Boolean).join('\n') : t('Neuer Eintrag am {day}', { day: d.toLocaleDateString() })}
              aria-label={here.length ? d.toLocaleDateString() + ' · ' + (here.length === 1 ? t('1 Eintrag') : t('{n} Einträge', { n: here.length })) : t('Neuer Eintrag am {day}', { day: d.toLocaleDateString() })}>
              {pic && withPic && <img src={thumbUrl(withPic.journal, pic, 256)} alt="" className={s.calImg} loading="lazy" />}
              <span className={s.calNum}>{i + 1}{here.length > 1 ? ` · ${here.length}` : ''}{here[0]?.mood ? ' ' + moodEmoji(here[0].mood) : ''}</span>
              {label && <span className={s.calText}>{label}</span>}
              {here.length === 0 && <span className={mergeClasses(s.calPlus, 'dm-cal-plus')} aria-hidden>+</span>}
            </button>
          )
        })}
      </div>
    </div>
  )
}

/** Verlaufsschritt für den offenen Eintrag (eigene Komponente, damit der Hook nicht an Bedingungen hängt). */
function EntryBack({ open, onBack }: { open: boolean; onBack: () => boolean | void | Promise<boolean> }) {
  useHistoryClose(open, onBack)
  return null
}

type MediaGroup = 'day' | 'month' | 'year' | 'none'
// Sortierung und Gruppen der Ansicht „Medien“: nur in diesem Browser gemerkt
const MEDIA_PREFS = 'dmoffice.journal.media'
function mediaPrefs(): { newest: boolean; group: MediaGroup } {
  try { return { newest: true, group: 'month', ...JSON.parse(localStorage.getItem(MEDIA_PREFS) ?? '{}') } } catch { return { newest: true, group: 'month' } }
}

type MediaItem = { entry: string; journal: number; date: string; media: Media }
function MediaView({ journal, tag, mood, day, onShow }: {
  journal: number | null; tag: string | null; mood: number | null; day: Date | null; onShow: (list: MediaItem[], index: number) => void
}) {
  const s = useStyles()
  const { fail } = useApp()
  const [list, setList] = useState<{ entry: string; journal: number; date: string; media: Media }[] | null>(null)
  const [prefs, setPrefs] = useState(mediaPrefs)
  const change = (p: Partial<typeof prefs>) => setPrefs(old => {
    const next = { ...old, ...p }
    try { localStorage.setItem(MEDIA_PREFS, JSON.stringify(next)) } catch { /* nur nicht gemerkt */ }
    return next
  })
  useEffect(() => { setList(null); journalApi.media(journal ?? undefined, tag, mood).then(setList).catch(fail) }, [journal, tag, mood, fail])
  const groups = useMemo(() => {
    // Aufnahmezeit des Fotos, sonst die Zeit des Eintrags
    const when = (x: { date: string; media: Media }) => new Date(x.media.taken ?? x.date)
    const shown = (list ?? []).filter(x => !day || sameDay(new Date(x.date), day))
      .sort((a, b) => (when(b).getTime() - when(a).getTime()) * (prefs.newest ? 1 : -1))
    const label = (d: Date) => prefs.group === 'day' ? d.toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' })
      : prefs.group === 'month' ? d.toLocaleDateString(undefined, { month: 'long', year: 'numeric' })
        : prefs.group === 'year' ? String(d.getFullYear()) : ''
    const out: { label: string; items: typeof shown }[] = []
    for (const x of shown) {
      const l = label(when(x))
      if (!out.length || out[out.length - 1].label !== l) out.push({ label: l, items: [] })
      out[out.length - 1].items.push(x)
    }
    return out
  }, [list, day, prefs])
  if (!list) return <Spinner style={{ marginTop: 32 }} />
  if (!list.length) return <div className={s.empty}><Text>{tag || mood ? t('Dazu gibt es keine Fotos oder Videos.') : t('Noch keine Fotos oder Videos im Tagebuch.')}</Text></div>
  const groupLabels: Record<MediaGroup, string> = { day: t('Nach Datum'), month: t('Nach Monat'), year: t('Nach Jahr'), none: t('Nicht gruppieren') }
  return (
    <>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap', marginBottom: 4 }}>
        <Dropdown size="small" value={prefs.newest ? t('Neueste zuerst') : t('Älteste zuerst')} selectedOptions={[prefs.newest ? 'new' : 'old']}
          onOptionSelect={(_, d) => change({ newest: d.optionValue === 'new' })} aria-label={t('Sortieren')} style={{ minWidth: 150 }}>
          <Option value="new">{t('Neueste zuerst')}</Option>
          <Option value="old">{t('Älteste zuerst')}</Option>
        </Dropdown>
        <Dropdown size="small" value={groupLabels[prefs.group]} selectedOptions={[prefs.group]}
          onOptionSelect={(_, d) => change({ group: (d.optionValue ?? 'month') as MediaGroup })} aria-label={t('Gruppieren')} style={{ minWidth: 150 }}>
          {(Object.keys(groupLabels) as MediaGroup[]).map(g => <Option key={g} value={g}>{groupLabels[g]}</Option>)}
        </Dropdown>
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('{n} Fotos und Videos', { n: groups.reduce((n, g) => n + g.items.length, 0) })}</Text>
      </div>
      {groups.map(g => (
        <div key={g.label || 'all'}>
          {g.label && <div className={s.month}>{g.label} · {g.items.length}</div>}
          <MediaTiles list={g.items} onOpen={x => { const all = groups.flatMap(gr => gr.items); onShow(all, all.indexOf(x)) }} />
        </div>
      ))}
    </>
  )
}

function MediaTiles({ list, onOpen }: { list: MediaItem[]; onOpen: (x: MediaItem) => void }) {
  const s = useStyles()
  return (
    <div className={s.mediaGrid}>
      {list.map(x => (
        <div key={x.entry + x.media.path} className={s.mediaTile} onClick={() => onOpen(x)} title={new Date(x.date).toLocaleDateString()}>
          <img src={thumbUrl(x.journal, x.media, 256)} alt="" style={{ width: '100%', height: '100%', objectFit: 'cover' }} loading="lazy" />
          {isVideo(x.media) && <span style={{ position: 'absolute', left: 6, bottom: 4, color: 'white', textShadow: '0 0 4px black' }}>▶</span>}
        </div>
      ))}
    </div>
  )
}

/** „Heute vor …“: je Rückblick (vor einem Monat, vor einem Jahr …) die Einträge wie in der Zeitleiste; highlight wie dort. */
function OnThisDayView({ onOpen, highlight, tiles }: { onOpen: (p: Preview) => void; highlight?: string | null; tiles?: boolean }) {
  const s = useStyles()
  const { fail } = useApp()
  const [list, setList] = useState<OnThisDay[] | null>(null)
  const marked = useRef<HTMLDivElement>(null)
  useEffect(() => { journalApi.onThisDay().then(setList).catch(fail) }, [fail])
  useEffect(() => { marked.current?.scrollIntoView({ block: 'center', behavior: 'smooth' }) }, [highlight, list])
  if (!list) return <Spinner style={{ marginTop: 32 }} />
  if (!list.length) return <div className={s.empty}><Text>{t('An diesem Tag gibt es noch nichts aus früheren Wochen, Monaten oder Jahren.')}</Text></div>
  const title = (g: OnThisDay) => `${t(g.label)} · ${new Date(g.day).toLocaleDateString(undefined, { day: 'numeric', month: 'long', year: 'numeric' })}`
  const card = (e: OnThisDay['entries'][number]) => {
    const on = highlight != null && e.id === highlight
    return <div ref={on ? marked : undefined} style={on ? { outline: `2px solid ${tokens.colorBrandStroke1}`, outlineOffset: 4, borderRadius: 12 } : undefined}><StoryCard e={e} onOpen={() => onOpen(e)} tile={tiles} /></div>
  }
  // Kacheln: alle Rückblicke in einem Raster, die Überschrift steht über jeder Kachel - eine Zeile je Rückblick ergäbe bei
  // meist einem Eintrag je Rückblick wieder eine Liste
  if (tiles) return (
    <div className={s.tiles}>
      {list.flatMap(g => g.entries.map(e => (
        <div key={e.id} style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
          <Text weight="semibold">{title(g)}</Text>
          {card(e)}
        </div>
      )))}
    </div>
  )
  return (
    <div className={s.column}>
      {list.flatMap(g => [
        <div key={'h' + g.label} className={s.monthBig}>{title(g)}</div>,
        ...g.entries.map(e => <div key={e.id}>{card(e)}</div>),
      ])}
    </div>
  )
}

/** Stimmungsverlauf: je Tag der Durchschnitt, über 30 Tage, 3 Monate oder ein Jahr. */
function MoodView({ journal, onDay }: { journal: number | null; onDay: (d: Date) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [range, setRange] = useState(90)
  const [list, setList] = useState<{ day: string; mood: number; count: number }[] | null>(null)
  useEffect(() => {
    const from = new Date(); from.setDate(from.getDate() - range)
    journalApi.moods(from.toISOString(), journal ?? undefined).then(setList).catch(fail)
  }, [range, journal, fail])
  const W = 900, H = 280, pad = 36, bottom = 56
  const start = new Date(); start.setDate(start.getDate() - range); start.setHours(0, 0, 0, 0)
  const x = (day: string) => pad + ((new Date(day + 'T12:00:00').getTime() - start.getTime()) / (range * 86400000)) * (W - 2 * pad)
  const y = (m: number) => H - bottom - ((m - 1) / 4) * (H - pad - bottom)
  // Achse: je Woche (30 Tage) bzw. je Monat ein Strich mit Datum
  const ticks: Date[] = []
  if (range <= 31) { const d = new Date(start); d.setDate(d.getDate() + ((8 - d.getDay()) % 7)); for (; d.getTime() <= Date.now(); d.setDate(d.getDate() + 7)) ticks.push(new Date(d)) }
  else { const d = new Date(start.getFullYear(), start.getMonth() + 1, 1); for (; d.getTime() <= Date.now(); d.setMonth(d.getMonth() + 1)) ticks.push(new Date(d)) }
  const xd = (d: Date) => pad + ((d.getTime() - start.getTime()) / (range * 86400000)) * (W - 2 * pad)
  // Linie nur zwischen Tagen, die höchstens drei Tage auseinander liegen - längere Lücken bleiben offen
  const segments: { day: string; mood: number }[][] = []
  for (const p of list ?? []) {
    const last = segments[segments.length - 1]?.slice(-1)[0]
    if (last && (new Date(p.day).getTime() - new Date(last.day).getTime()) / 86400000 <= 3) segments[segments.length - 1].push(p)
    else segments.push([p])
  }
  const avg = list?.length ? list.reduce((a, b) => a + b.mood * b.count, 0) / list.reduce((a, b) => a + b.count, 0) : null
  const brand = tokens.colorBrandForeground1
  return (
    <>
      <div style={{ display: 'flex', gap: 8, alignItems: 'center', margin: '8px 0 12px', flexWrap: 'wrap' }}>
        {[30, 90, 365].map(r => <Button key={r} size="small" appearance={range === r ? 'primary' : 'secondary'} onClick={() => setRange(r)}>
          {r === 30 ? t('30 Tage') : r === 90 ? t('3 Monate') : t('1 Jahr')}</Button>)}
        {avg != null && <Text>{t('Im Schnitt: {emoji} ({value})', { emoji: moodEmoji(avg), value: avg.toFixed(1) })}</Text>}
      </div>
      {!list ? <Spinner /> : !list.length ? <div className={s.empty}><Text>{t('In diesem Zeitraum gibt es keine Einträge mit Stimmung.')}</Text></div> : (
        <svg viewBox={`0 0 ${W} ${H}`} className={s.chart} role="img" aria-label={t('Stimmungsverlauf')}>
          {MOODS.map(m => (
            <g key={m.value}>
              <line x1={pad} x2={W - pad} y1={y(m.value)} y2={y(m.value)} stroke={tokens.colorNeutralStroke2} strokeDasharray="3 4" />
              <text x={8} y={y(m.value) + 6} fontSize="16">{m.emoji}</text>
            </g>
          ))}
          {ticks.map(d => <g key={d.toISOString()}>
            <line x1={xd(d)} x2={xd(d)} y1={pad - 10} y2={H - bottom + 6} stroke={tokens.colorNeutralStroke3} />
            <text x={xd(d)} y={H - bottom + 22} fontSize="12" textAnchor="middle" fill={tokens.colorNeutralForeground3}>
              {range <= 31 ? d.toLocaleDateString(undefined, { day: 'numeric', month: 'numeric' }) : d.toLocaleDateString(undefined, { month: 'short', year: range > 120 ? '2-digit' : undefined })}</text>
          </g>)}
          {segments.map((seg, i) => seg.length > 1 && <polyline key={i} fill="none" stroke={brand} strokeWidth={2.5} strokeLinejoin="round" points={seg.map(p => `${x(p.day)},${y(p.mood)}`).join(' ')} />)}
          {list.map(p => (
            <circle key={p.day} cx={x(p.day)} cy={y(p.mood)} r={6} fill={brand} stroke={tokens.colorNeutralBackground1} strokeWidth={2} style={{ cursor: 'pointer' }}
              onClick={() => onDay(new Date(p.day + 'T12:00:00'))}>
              <title>{new Date(p.day + 'T12:00:00').toLocaleDateString()} · {moodEmoji(p.mood)} {p.mood} · {t('Klicken zeigt den Tag')}</title>
            </circle>
          ))}
        </svg>
      )}
    </>
  )
}

/** Drucken bzw. als PDF speichern (über den Druckdialog des Browsers): die gefilterten Einträge, älteste zuerst, mit Fotos. */
async function printEntries(params: Parameters<typeof journalApi.entries>[0], fail: (e: unknown) => void) {
  const win = window.open('', '_blank')
  if (!win) return
  win.document.write(`<p style="font-family:sans-serif">${t('Wird vorbereitet …')}</p>`)
  try {
    const { api, q } = await import('../api')
    const list = await api.get<Entry[]>('/api/journal/entries?' + q({ ...params, limit: 2000, full: true }))
    const esc = (v: string) => v.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]!))
    const items = list.map(e => {
      const d = new Date(e.date)
      const meta = [e.mood ? moodEmoji(e.mood) : '', e.place?.name ?? (e.place?.lat != null ? fmtCoords(e.place) : ''), e.weather ?? '', e.tags.map(x => '#' + x).join(' ')].filter(Boolean).join(' · ')
      const pics = e.media.filter(isImage).map(m => `<img src="${location.origin + thumbUrl(e.journal, m, 1600)}" alt="">`).join('')
      return `<article><h2>${esc(d.toLocaleDateString(undefined, { weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }))}${e.title ? ' – ' + esc(e.title) : ''}</h2>`
        + (meta ? `<p class="meta">${esc(meta)}</p>` : '') + `<div class="text">${e.html}</div>` + (pics ? `<div class="pics">${pics}</div>` : '') + '</article>'
    }).join('')
    win.document.open()
    win.document.write(`<!doctype html><html><head><meta charset="utf-8"><title>${esc(t('Tagebuch'))}</title><style>
      body{font-family:Georgia,serif;max-width:760px;margin:24px auto;padding:0 16px;color:#222;line-height:1.55}
      h1{font-family:system-ui,sans-serif}
      article{break-inside:avoid-page;margin:0 0 28px;padding-bottom:16px;border-bottom:1px solid #ddd}
      h2{font-family:system-ui,sans-serif;font-size:18px;margin:0 0 4px}
      .meta{color:#666;font-family:system-ui,sans-serif;font-size:13px;margin:0 0 8px}
      .pics{display:grid;grid-template-columns:repeat(2,1fr);gap:6px;margin-top:8px}
      .pics img{width:100%;border-radius:4px}
      ul[data-check]{list-style:none;padding-left:4px} ul[data-check]>li::before{content:'☐ '} ul[data-check]>li[data-done="1"]::before{content:'☑ '}
    </style></head><body><h1>${esc(t('Tagebuch'))}</h1>${items || `<p>${esc(t('Dazu gibt es keinen Eintrag.'))}</p>`}</body></html>`)
    win.document.close()
    // Erst drucken, wenn die Fotos geladen sind
    const imgs = Array.from(win.document.images)
    await Promise.all(imgs.map(i => i.complete ? null : new Promise(ok => { i.onload = i.onerror = ok })))
    win.focus()
    win.print()
  } catch (e) { win.close(); fail(e) }
}

/** Einträge verschieben: Ziel unter den eigenen Tagebüchern wählen (Tresor nur offen; zu: erst öffnen). */
function MoveDialog({ count, journals, from, tresorExists, tresorOpen, onClose, onMove }: {
  count: number; journals: Journal[]; from: number[]; tresorExists: boolean; tresorOpen: boolean; onClose: () => void; onMove: (to: number) => Promise<void>
}) {
  const targets = journals.filter(j => !j.owner && !(from.length === 1 && from[0] === j.id))
  const [to, setTo] = useState<number | null>(targets[0]?.id ?? null)
  const [busy, setBusy] = useState(false)
  const chosen = targets.find(j => j.id === to)
  const intoTresor = !!chosen?.tresor && from.some(f => !inTresor(f))
  const outOfTresor = !!chosen && !chosen.tresor && from.some(f => inTresor(f))
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 480 }}>
        <DialogBody>
          <DialogTitle>{count === 1 ? t('Eintrag verschieben') : t('{n} Einträge verschieben', { n: count })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Ins Tagebuch')}>
              <Dropdown value={chosen?.name ?? ''} selectedOptions={to != null ? [String(to)] : []}
                onOptionSelect={(_, d) => { if (d.optionValue === 'open-tresor') requestTresorOpen(); else setTo(Number(d.optionValue)) }}>
                {targets.filter(j => !j.tresor).map(j => <Option key={j.id} value={String(j.id)}>{j.name}</Option>)}
                {tresorExists && <OptionGroup label={t('Tresor')}>
                  {tresorOpen
                    ? targets.filter(j => j.tresor).map(j => <Option key={j.id} value={String(j.id)}>{j.name}</Option>)
                    : <Option value="open-tresor" text={t('Tresor öffnen …')}>{t('Tresor öffnen …')}</Option>}
                </OptionGroup>}
              </Dropdown>
            </Field>
            {intoTresor && <Text>{t('Fotos und Dateien kommen mit in den Tresor; im offenen Bereich werden sie endgültig gelöscht. Aus der Dateiablage angehängte Dateien kommen als Kopie mit, das Original bleibt.')}</Text>}
            {outOfTresor && <Text>{t('Fotos und Dateien kommen mit aus dem Tresor; im offenen Bereich liegen sie unverschlüsselt.')}</Text>}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={to == null || busy} onClick={async () => { setBusy(true); try { await onMove(to!) } finally { setBusy(false) } }}>{t('Verschieben')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Name eines Tagebuchs; beim Anlegen mit eingerichtetem Tresor die Wahl „Im Tresor anlegen“. */
function NameDialog({ title, initial, canTresor, onClose, onSave }: {
  title: string; initial: string; canTresor?: boolean; onClose: () => void; onSave: (name: string, tresor: boolean) => void
}) {
  const [name, setName] = useState(initial)
  const [inVault, setInVault] = useState(false)
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{title}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Name')}><Input autoFocus value={name} onChange={(_, d) => setName(d.value)} onKeyDown={e => e.key === 'Enter' && name.trim() && onSave(name.trim(), inVault)} /></Field>
            {canTresor && <Field hint={inVault ? t('Nur sichtbar, solange der Tresor offen ist. Teilen ist nicht möglich.') : undefined}>
              <Checkbox checked={inVault} onChange={(_, d) => setInVault(!!d.checked)} label={t('Im Tresor anlegen')} />
            </Field>}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!name.trim()} onClick={() => onSave(name.trim(), inVault)}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Einstellungen des Tagebuchs: Größe der Fotos, Wetter und Ortsnamen aus dem Internet, Pukki fragt abends nach. */
function SettingsDialog({ onClose }: { onClose: (s: Settings | null) => void }) {
  const { fail } = useApp()
  const [st, setSt] = useState<Settings | null>(null)
  const [ask, setAsk] = useState('')
  useEffect(() => { journalApi.settings().then(r => { setSt(r); setAsk(r.ask ?? '') }).catch(fail) }, [fail])
  const save = async () => {
    try { onClose(await journalApi.saveSettings({ imageSize: st!.imageSize, lookup: st!.lookup, ask })) } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(null)}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{t('Einstellungen des Tagebuchs')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 14 }}>
            {!st ? <Spinner size="small" /> : <>
              <Field label={t('Größe der Fotos')} hint={t('Fotos kommen verkleinert ins Tagebuch – das Original bleibt, wo es war. Aufnahmedatum und Ort werden vorher aus dem Foto gelesen.')}>
                <RadioGroup value={String(st.imageSize)} onChange={(_, d) => setSt({ ...st, imageSize: Number(d.value) })}>
                  <Radio value="1024" label={t('Klein – 1024 Pixel an der langen Kante')} />
                  <Radio value="2048" label={t('Mittel – 2048 Pixel an der langen Kante (empfohlen)')} />
                  <Radio value="0" label={t('Originalgröße')} />
                </RadioGroup>
              </Field>
              <Field hint={t('Dabei gehen Ort und Datum des Eintrags an Open-Meteo (Wetter) und OpenStreetMap (Ortsname). Ohne diesen Schalter fragt dmOffice dort nichts.')}>
                <Switch label={t('Wetter und Ortsnamen aus dem Internet holen')} checked={st.lookup} onChange={(_, d) => setSt({ ...st, lookup: d.checked })} />
              </Field>
              <Field hint={t('Pukki fragt dann im Chat „Wie war dein Tag?“ – deine Antwort wird ein Eintrag. Nicht, wenn es heute schon einen gibt.')}>
                <Switch label={t('Pukki fragt abends nach dem Tag')} checked={!!ask} onChange={(_, d) => setAsk(d.checked ? '21:00' : '')} />
                {ask && <Input type="time" value={ask} onChange={(_, d) => setAsk(d.value)} style={{ maxWidth: 140, marginTop: 6 }} aria-label={t('Uhrzeit')} />}
              </Field>
            </>}
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!st} onClick={save}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/**
 * Export als ZIP: je Eintrag eine Markdown-Datei (Jahr/Monat), Fotos und Videos im Original daneben, dazu tagebuch.json zum
 * Wiedereinlesen (auch in ein anderes dmOffice). Ganzes Tagebuch, ein Jahr oder ein Zeitraum.
 */
function ExportDialog({ journal, journals, onClose }: { journal: number; journals: Journal[]; onClose: () => void }) {
  const [target, setTarget] = useState(journal)
  const year = new Date().getFullYear()
  const [range, setRange] = useState<'all' | 'year' | 'last' | 'custom'>('all')
  const [from, setFrom] = useState('')
  const [to, setTo] = useState('')
  const [media, setMedia] = useState(true)
  const span = range === 'year' ? { from: year + '-01-01', to: year + '-12-31' } : range === 'last' ? { from: (year - 1) + '-01-01', to: (year - 1) + '-12-31' }
    : range === 'custom' ? { from: from || undefined, to: to || undefined } : {}
  const url = '/api/journal/export?' + q({ journal: target, ...span, media: media ? undefined : false })
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('Tagebuch exportieren')}</DialogTitle>
          <DialogContent>
            <Text block>{t('Du bekommst eine ZIP-Datei: je Eintrag eine Markdown-Datei (lesbar mit jedem Texteditor, Obsidian und Co.), die Fotos und Videos im Original daneben und eine Datei tagebuch.json mit allem – damit lässt sich der Export wieder einlesen, auch in ein anderes dmOffice.')}</Text>
            {journals.length > 1 && (
              <Field label={t('Tagebuch')} style={{ marginTop: 12 }}>
                <Dropdown value={journals.find(j => j.id === target)?.name ?? ''} selectedOptions={[String(target)]} onOptionSelect={(_, d) => setTarget(Number(d.optionValue))}>
                  {journals.map(j => <Option key={j.id} value={String(j.id)} text={j.name}>{j.name}{j.owner ? ' · ' + j.owner : ''}</Option>)}
                </Dropdown>
              </Field>
            )}
            <Field label={t('Zeitraum')} style={{ marginTop: 12 }}>
              <RadioGroup value={range} onChange={(_, d) => setRange(d.value as typeof range)}>
                <Radio value="all" label={t('Alles')} />
                <Radio value="year" label={String(year)} />
                <Radio value="last" label={String(year - 1)} />
                <Radio value="custom" label={t('Von – bis')} />
              </RadioGroup>
            </Field>
            {range === 'custom' && <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
              <Field label={t('Von')}><Input type="date" value={from} onChange={(_, d) => setFrom(d.value)} /></Field>
              <Field label={t('Bis')}><Input type="date" value={to} onChange={(_, d) => setTo(d.value)} /></Field>
            </div>}
            <Switch style={{ marginTop: 8 }} checked={media} onChange={(_, d) => setMedia(d.checked)} label={t('Fotos und Videos mitnehmen')} />
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" as="a" href={url} download onClick={() => setTimeout(onClose, 300)}>{t('Exportieren')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Import aus Diarium: ZIP hochladen (in Stücken, auch groß) oder eine schon hochgeladene aus der Dateiablage nehmen. */
function ImportDialog({ journal, journals, onClose }: { journal: number; journals: Journal[]; onClose: (changed: boolean) => void }) {
  const { fail } = useApp()
  const [target, setTarget] = useState(journal)
  const [path, setPath] = useState('')
  const [picking, setPicking] = useState(false)
  const [upload, setUpload] = useState<{ loaded: number; total: number } | null>(null)
  const [status, setStatus] = useState<ImportStatus | null>(null)
  useEffect(() => { journalApi.importStatus().then(st => st.running && setStatus(st)).catch(() => {}) }, [])
  useEffect(() => {
    if (!status?.running) return
    const timer = setInterval(() => journalApi.importStatus().then(setStatus).catch(() => {}), 1000)
    return () => clearInterval(timer)
  }, [status?.running])

  const uploadZip = async (file: File) => {
    setUpload({ loaded: 0, total: file.size })
    try {
      const id = await uploadChunked(file, file.name, n => setUpload({ loaded: n, total: file.size }))
      const r = await finishToFiles(id, 'Import', 'rename')
      setPath(r.files?.[0]?.path ?? 'Import/' + file.name)
    } catch (e) { fail(e) } finally { setUpload(null) }
  }
  const start = async () => {
    try { setStatus(await journalApi.importDiarium(path.trim(), target)) } catch (e) { fail(e) }
  }
  const done = status && !status.running
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(!!done)}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{t('Tagebuch importieren')}</DialogTitle>
          <DialogContent>
            {!status && <>
              <Text block>{t('Nimm einen Export aus dmOffice (ZIP), aus Diarium (JSON mit Medien als ZIP) oder aus Journey (ZIP). Schon vorhandene Einträge werden nicht doppelt angelegt. Liegt die ZIP-Datei schon in deiner Dateiablage (zum Beispiel vom Handy geteilt), wähle sie dort aus – sonst lade sie hier hoch; auch große Exporte gehen, sie werden in Stücken übertragen.')}</Text>
              <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap', margin: '12px 0' }}>
                <Button appearance={path ? 'secondary' : 'primary'} disabled={!!upload} onClick={() => setPicking(true)}>{t('Aus der Dateiablage wählen …')}</Button>
                <Button disabled={!!upload} onClick={() => { const i = document.createElement('input'); i.type = 'file'; i.accept = '.zip,application/zip'; i.onchange = () => i.files?.[0] && uploadZip(i.files[0]); i.click() }}>{t('ZIP-Datei hochladen …')}</Button>
                {upload && <ProgressBar value={upload.loaded / upload.total} style={{ flex: 1, minWidth: 120 }} />}
              </div>
              {path && <Text block>{t('Gewählt: {path}', { path })}</Text>}
              {picking && <PickFileDialog title={t('Export auswählen')} accept={['.zip']} start={parentOf(path)}
                onPick={p => { setPath(p); setPicking(false) }} onClose={() => setPicking(false)} />}
              {journals.length > 1 && (
                <Field label={t('In dieses Tagebuch')} style={{ marginTop: 8 }}>
                  <Dropdown value={journals.find(j => j.id === target)?.name ?? ''} selectedOptions={[String(target)]} onOptionSelect={(_, d) => setTarget(Number(d.optionValue))}>
                    {journals.map(j => <Option key={j.id} value={String(j.id)}>{j.name}</Option>)}
                  </Dropdown>
                </Field>
              )}
            </>}
            {status && <>
              {status.running && <ProgressBar value={status.total ? status.done / status.total : undefined} />}
              <Text block style={{ marginTop: 8 }}>
                {status.running ? t('Importiere … {done} von {total} Einträgen', { done: status.done, total: status.total })
                  : status.error ? status.error
                    : t('Fertig: {created} Einträge mit {media} Fotos übernommen, {skipped} schon vorhanden.', { created: status.created, media: status.media, skipped: status.skipped })}
              </Text>
            </>}
          </DialogContent>
          <DialogActions>
            {!status && <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>}
            {!status && <Button appearance="primary" disabled={!path.trim() || !!upload} onClick={start}>{t('Importieren')}</Button>}
            {status && <Button appearance="primary" onClick={() => onClose(true)}>{status.running ? t('Im Hintergrund weiter') : t('Schließen')}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

export { dayOf }
