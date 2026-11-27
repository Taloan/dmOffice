import { makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { locale, t } from '../i18n'
import type { Item } from './common'
import { dimsOf, fmtDuration, fmtExposure, labelColor, labelName, sourceLabel, type Meta } from './PhotoFilter'
import { fmtSize } from '../api'
import { DEFAULT_TILE_HEAD, renderTile, type TileValues } from './tileTemplate'

/**
 * Galerie-Kachel im Modus „Voll“ (Kennungen tilemodes, tilebar) nach dem Vorbild des Kamera-Importers: Kopfzeile mit den
 * Aufnahmedaten über dem Bild, darunter eine Leiste mit Markierung, Farbe, Sternen, Schnellsammlung und Drehen.
 * Feste Reihenfolge, nicht einstellbar. Die Symbole behalten ihre Größe; ist die Kachel zu schmal, wird hinten abgeschnitten
 * (ein geschrumpftes Symbol wäre weder zu erkennen noch zu treffen).
 */

/** Höhe der Kopfzeile (vier Zeilen) und der Leiste - für die Lage von Häkchen und Abzeichen auf dem Bild. */
export const HEAD_H = 56
export const BAR_H = 20
/** Breite, die die Leiste braucht: 6 Knöpfe (72 px) und 5 Sterne (45 px), 5 Lücken (20 px), Rand (8 px), Kachelrahmen (2 px), etwas Luft. */
export const BAR_MIN_W = 150

/** Farben mit fester Bedeutung (wie im Kamera-Importer), unabhängig vom Farbschema. */
export const PICK_COLOR = '#30c050', REJECT_COLOR = '#e03030'
/** Tönung der Kachel bei Markierung, solange sie nicht gewählt ist. */
export const PICK_TINT = 'rgba(48, 192, 80, 0.16)', REJECT_TINT = 'rgba(224, 48, 48, 0.16)'

const useStyles = makeStyles({
  head: {
    width: '100%', boxSizing: 'border-box', height: `${HEAD_H}px`, padding: '3px 4px 2px', fontSize: '11px', lineHeight: '12.5px', fontWeight: 600,
    color: tokens.colorNeutralForeground3, borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, overflow: 'hidden',
  },
  line: { display: 'flex', gap: '6px', minWidth: 0, height: '12.5px' },
  first: { color: tokens.colorNeutralForeground1 },
  l: { flex: 1, minWidth: 0, whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' },
  r: { flexShrink: 1, minWidth: 0, maxWidth: '60%', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', textAlign: 'right' },
  bar: {
    width: '100%', boxSizing: 'border-box', height: `${BAR_H}px`, padding: '0 4px', display: 'flex', alignItems: 'center',
    overflow: 'hidden', borderTop: `1px solid ${tokens.colorNeutralStroke2}`, color: tokens.colorNeutralForeground3, flexShrink: 0,
  },
  // Mittig, solange es passt; sonst links bündig und hinten abgeschnitten (automatische Ränder fallen dann auf 0)
  row: { display: 'flex', alignItems: 'center', gap: '4px', margin: '0 auto', flexShrink: 0 },
  btn: {
    flexShrink: 0, height: '16px', minWidth: '12px', padding: 0, border: 'none', background: 'none', cursor: 'pointer', color: 'inherit',
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center', borderRadius: '2px',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    ':focus-visible': { outline: `1px solid ${tokens.colorStrokeFocus2}` },
  },
  stars: { display: 'inline-flex', flexShrink: 0 },
  // Sterne kleiner und dicht an dicht
  star: { width: '9px', minWidth: '9px' },
  on: { color: tokens.colorNeutralForeground1 },
  // Gesetzter Stern: hell erst beim Überfahren (die Punkte davor bleiben einheitlich dezent)
  starOn: { '& [data-starfull]': { color: tokens.colorNeutralForeground1 } },
})

/** Regeln für die Kachel: Punkte ohne Mauszeiger, Sterne beim Überfahren, mit Tastaturfokus und auf Touch-Geräten. */
export const STAR_HOVER = {
  '& [data-starfull]': { display: 'none' },
  ':hover [data-starfull]': { display: 'block' },
  ':focus-within [data-starfull]': { display: 'block' },
  ':hover [data-stardot]': { display: 'none' },
  ':focus-within [data-stardot]': { display: 'none' },
  '@media (hover: none)': { '& [data-starfull]': { display: 'block' }, '& [data-stardot]': { display: 'none' } },
} as const

const pad = (n: number) => String(n).padStart(2, '0')
const num = (x: number, d = 1) => x.toLocaleString(locale, { maximumFractionDigits: d })

/** Kopfzeile: Titel | Datum, Beschreibung | Uhrzeit, Blende Zeit Brennweite | Kamera, Objektiv | Dateityp. */
/** Werte eines Fotos für die Platzhalter der Kopfzeile (tileTemplate.ts). Datum und Zeit wie im festen Kopf. */
export function tileValues(item: Item, meta?: Meta): TileValues {
  const d = new Date(meta?.taken ?? Date.parse(item.modified))
  const date = `${pad(d.getDate())}.${pad(d.getMonth() + 1)}.${d.getFullYear()}`
  const time = `${pad(d.getHours())}:${pad(d.getMinutes())}`
  const dot = item.name.lastIndexOf('.')
  return {
    titel: meta?.title, beschreibung: meta?.description, tags: meta?.keywords?.split('\n').filter(Boolean).join(', '),
    'aufn-datum': date, 'aufn-zeit': time, 'datum-zeit': `${date} ${time}`,
    'aufn-yy': String(d.getFullYear()).slice(-2), 'aufn-mm': pad(d.getMonth() + 1), 'aufn-dd': pad(d.getDate()),
    kamera: meta?.camera, objektiv: meta?.lens,
    blende: meta?.aperture ? num(meta.aperture) : null,
    'bel-zeit': meta?.exposure ? fmtExposure(meta.exposure).replace(' s', '') : null,
    iso: meta?.iso ? String(meta.iso) : null,
    brennweite: meta?.focal ? num(meta.focal, 1) : null,
    filename: dot > 0 ? item.name.slice(0, dot) : item.name,
    ext: dot >= 0 ? item.name.slice(dot + 1).toUpperCase() : '',
    groesse: item.size ? fmtSize(item.size) : null,
    abmessung: dimsOf(meta) || null,
    dauer: meta?.duration ? fmtDuration(meta.duration) : null,
    sterne: meta?.rating ? '★'.repeat(meta.rating) + '☆'.repeat(Math.max(0, 5 - meta.rating)) : null,
    farbe: meta?.label ? labelName(meta.label) : null,
    ordner: sourceLabel(item.path),
  }
}

/**
 * Kopfzeile der Kachel „Voll“: vier Zeilen, je links und rechts. Mit Vorlagen (Kennung tilehead) frei belegbar, sonst die Vorgabe –
 * beides ergibt ohne eigene Belegung denselben Kopf. Ist eine Seite leer, bekommt die andere die ganze Breite.
 */
export function TileHead({ item, meta, templates }: { item: Item; meta?: Meta; templates?: readonly string[] }) {
  const s = useStyles()
  const values = tileValues(item, meta)
  const tpl = templates ?? DEFAULT_TILE_HEAD
  const lines: [string, string][] = [0, 1, 2, 3].map(k => [renderTile(tpl[k * 2] ?? '', values), renderTile(tpl[k * 2 + 1] ?? '', values)])
  return (
    <div className={s.head}>
      {lines.map(([l, r], k) => (
        <div key={k} className={mergeClasses(s.line, k === 0 && s.first)}>
          <span className={s.l} title={l || undefined}>{l || ' '}</span><span className={s.r} style={!l ? { maxWidth: 'none' } : undefined} title={r || undefined}>{r}</span>
        </div>
      ))}
    </div>
  )
}

// Symbole (12 px), gemalt statt Schriftzeichen
const Flag = ({ color }: { color?: string }) => (
  <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden><path d="M3 1.5v9.5" stroke={color ?? 'currentColor'} strokeWidth="1.3" fill="none" />
    <path d="M3.4 1.8h6.9l-1.6 2.3 1.6 2.3H3.4z" fill={color ?? 'none'} stroke={color ?? 'currentColor'} strokeWidth="1" strokeLinejoin="round" /></svg>
)
// Ohne Mauszeiger über der Kachel fünf dezente graue Punkte: voll je gesetztem Stern, leer (Ring) für die übrigen; beim Überfahren
// ★ gesetzt, ☆ frei (wie im Kamera-Importer). Umgeschaltet über die Attribute data-stardot / data-starfull (Regeln in der Kachel, FilesView).
const STAR = 'M6 .9l1.5 3.2 3.5.42-2.6 2.4.7 3.47L6 8.66 2.9 10.4l.7-3.47L1 4.52l3.5-.42z'
const Star = ({ on }: { on: boolean }) => <>
  <svg data-stardot width="9" height="9" viewBox="0 0 12 12" aria-hidden>
    {on ? <circle cx="6" cy="6" r="2.2" fill="currentColor" /> : <circle cx="6" cy="6" r="1.8" fill="none" stroke="currentColor" strokeWidth="0.9" />}</svg>
  <svg data-starfull width="9" height="9" viewBox="0 0 12 12" aria-hidden>
    <path d={STAR} fill={on ? 'currentColor' : 'none'} stroke="currentColor" strokeWidth={on ? 0 : 1.1} strokeLinejoin="round" /></svg>
</>
const Ring = ({ on }: { on: boolean }) => (
  <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden><circle cx="6" cy="6" r="4.6" stroke="currentColor" strokeWidth="1.2" fill="none" />{on && <circle cx="6" cy="6" r="2" fill="currentColor" />}</svg>
)
const Turn = ({ right }: { right?: boolean }) => (
  <svg width="12" height="12" viewBox="0 0 12 12" aria-hidden style={right ? { transform: 'scaleX(-1)' } : undefined}>
    <path d="M9.6 7.2A3.9 3.9 0 1 1 7.6 2.5" stroke="currentColor" strokeWidth="1.3" fill="none" strokeLinecap="round" />
    <path d="M5.6.6l3 1.9-2.6 2.3z" fill="currentColor" /></svg>
)

export interface TileBarProps {
  meta?: Meta
  /** In der Schnellsammlung; undefined: Schnellsammlung aus (Kennung quickcoll), dann kein Kreis */
  quick?: boolean
  /** Drehen möglich (JPEG/RAW in eigener Ablage oder Galerie-Ordner) */
  turnable: boolean
  onPick: (pick: number) => void
  /** Farbmenü an dieser Stelle öffnen */
  onLabelMenu: (at: { x: number; y: number }) => void
  onRating: (rating: number) => void
  onQuick: () => void
  onTurn: (deg: number) => void
}

/** Leiste unter dem Bild: Flagge, Farbe, fünf Sterne, Schnellsammlung, Drehen links und rechts. */
export function TileBar({ meta, quick, turnable, onPick, onLabelMenu, onRating, onQuick, onTurn }: TileBarProps) {
  const s = useStyles()
  const pick = meta?.pick ?? 0
  const rating = meta?.rating ?? 0
  const label = labelColor(meta?.label)
  // Klicks gehören der Leiste: nicht zur Kachel (Öffnen, Ziehen) durchreichen
  const own = (fn: (e: React.MouseEvent) => void) => (e: React.MouseEvent) => { e.stopPropagation(); fn(e) }
  const stop = (e: React.SyntheticEvent) => e.stopPropagation()
  return (
    <div className={s.bar} onDoubleClick={stop} onMouseDown={stop} onDragStart={e => { e.preventDefault(); e.stopPropagation() }}><div className={s.row}>
      <button type="button" className={s.btn} draggable={false}
        title={pick === 1 ? t('Ausgewählt (P)') : pick === -1 ? t('Abgelehnt (X)') : t('Ohne Markierung (U)')}
        onClick={own(() => onPick(pick === 0 ? 1 : pick === 1 ? -1 : 0))}>
        <Flag color={pick === 1 ? PICK_COLOR : pick === -1 ? REJECT_COLOR : undefined} />
      </button>
      <button type="button" className={s.btn} title={meta?.label ? labelName(meta.label) : t('Farbmarkierung')}
        onClick={own(e => { const r = (e.currentTarget as HTMLElement).getBoundingClientRect(); onLabelMenu({ x: r.left, y: r.bottom }) })}>
        <span style={{ width: 10, height: 10, boxSizing: 'border-box', borderRadius: 1, ...(label ? { background: label } : { border: '1px solid currentColor' }) }} />
      </button>
      <span className={s.stars}>
        {[1, 2, 3, 4, 5].map(n => (
          <button key={n} type="button" className={mergeClasses(s.btn, s.star, n <= rating && s.starOn)}
            title={n === rating ? t('{n} Sterne - noch einmal: zurücksetzen', { n }) : n === 1 ? t('1 Stern') : t('{n} Sterne', { n })}
            onClick={own(() => onRating(n === rating ? 0 : n))}><Star on={n <= rating} /></button>
        ))}
      </span>
      {quick !== undefined && <button type="button" className={mergeClasses(s.btn, quick && s.on)}
        title={quick ? t('In der Schnellsammlung (B)') : t('In die Schnellsammlung (B)')} onClick={own(onQuick)}><Ring on={quick} /></button>}
      {turnable && <button type="button" className={s.btn} title={t('Nach links drehen')} onClick={own(() => onTurn(-90))}><Turn /></button>}
      {turnable && <button type="button" className={s.btn} title={t('Nach rechts drehen')} onClick={own(() => onTurn(90))}><Turn right /></button>}
    </div></div>
  )
}
