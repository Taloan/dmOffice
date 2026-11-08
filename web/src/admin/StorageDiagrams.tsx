import { Text, tokens } from '@fluentui/react-components'
import { t } from '../i18n'

// Systembild für Verwaltung → Storage Extender: links das ganze lokale Speichervolumen, rechts der Storage Extender, beide im selben
// Maßstab. Der Wert eines Kästchens hängt nur von der Größe des Volumes ab: Beide Regler sind Begrenzer und sperren nur Kästchen.
// Feste Farben statt Theme: Sie tragen eine Bedeutung und müssen sich auch bei rotem Theme unterscheiden.
export const BOX_COLORS = { used: '#D85A30', clean: '#A32D2D', pending: '#EF9F27', foreign: '#888780' }
export const LEFT_BOXES = 48
const MAX_RIGHT = 144

const size = { width: 13, height: 15, borderRadius: 2, boxSizing: 'border-box' } as const
const Used = () => <span style={{ ...size, background: BOX_COLORS.used }} />
const Clean = () => <span style={{ ...size, background: BOX_COLORS.clean }} />
const Pending = () => <span style={{ ...size, background: BOX_COLORS.pending }} />
const Foreign = () => <span style={{ ...size, background: BOX_COLORS.foreign }} />
const Free = () => <span style={{ ...size, border: `1px dashed ${tokens.colorNeutralStroke1}` }} />
const Blocked = () => <span style={{ ...size, border: `1px solid ${tokens.colorNeutralStroke1}`, background: `repeating-linear-gradient(135deg, ${BOX_COLORS.foreign} 0 2px, transparent 2px 5px)` }} />

const repeat = (Cell: () => JSX.Element, n: number) => Array.from({ length: Math.max(0, n) }, (_, i) => <Cell key={i} />)
/** Kästchen für eine Menge, aufgerundet: Ein angefangenes Kästchen zählt ganz. */
export const boxes = (bytes: number, unit: number) => bytes <= 0 || unit <= 0 ? 0 : Math.ceil(bytes / unit - 1e-9)

function Area({ cols, title, note, children }: { cols: number; title: string; note: string; children: React.ReactNode }) {
  return (
    <div style={{ minWidth: 0 }}>
      <div style={{ border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: 10, padding: 10, background: tokens.colorNeutralBackground3, minHeight: 96, display: 'grid', gridTemplateColumns: `repeat(${cols}, 13px)`, gap: 4, alignContent: 'center', justifyContent: 'center' }}>{children}</div>
      <div style={{ textAlign: 'center', marginTop: 5 }}><Text size={200} block>{title}</Text><Text size={200} block style={{ color: tokens.colorNeutralForeground3 }}>{note}</Text></div>
    </div>
  )
}

export interface ExtenderBoxes {
  /** Links, in dieser Reihenfolge: belegt, wird bereinigt, nutzbar (mit den beiden davor), gesperrt (der Rest), nicht von dmOffice belegt. */
  used: number; clean: number; usable: number; other: number
  /** Rechts: vorhanden, noch zu übertragen, frei (null: Größe unbekannt), nicht von dmOffice belegt. */
  there: number; pending: number; free: number | null; foreign: number
  leftNote: string; rightNote: string; unitLabel: string; fetching: boolean
}

/** Links immer 48 Kästchen für das ganze Volume, rechts im selben Maßstab: was im Storage Extender liegt. */
export function ExtenderPicture(b: ExtenderBoxes) {
  // Aufgerundet kann die Summe über 48 liegen: Das Graue am Ende gibt nach
  const used = Math.min(LEFT_BOXES, b.used)
  const clean = Math.min(LEFT_BOXES - used, b.clean)
  const other = Math.min(LEFT_BOXES - used - clean, b.other)
  const open = Math.max(0, Math.min(LEFT_BOXES - used - clean - other, b.usable - used - clean))
  const blocked = LEFT_BOXES - used - clean - other - open
  const right = b.there + b.pending + (b.free ?? 0) + b.foreign
  let budget = MAX_RIGHT
  const take = (n: number) => { const k = Math.min(n, budget); budget -= k; return k }
  const legend = (Cell: () => JSX.Element, label: string) => <span style={{ display: 'flex', alignItems: 'center', gap: 5 }}><Cell /><Text size={200}>{label}</Text></span>
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
      <div style={{ display: 'grid', gridTemplateColumns: 'minmax(224px, 1fr) 90px minmax(224px, 1fr)', overflowX: 'auto', gap: 8, alignItems: 'center' }}>
        <Area cols={12} title={t('Lokales Speichervolumen')} note={b.leftNote}>
          {repeat(Used, used)}{repeat(Clean, clean)}{repeat(Free, open)}{repeat(Blocked, blocked)}{repeat(Foreign, other)}
        </Area>
        <Text size={100} align="center" style={{ color: tokens.colorNeutralForeground3 }}>{t('alle Dateien')} →{b.fetching && <><br />← {t('lädt bei Bedarf')}</>}</Text>
        <Area cols={12} title={t('Storage Extender')} note={b.rightNote + (right > MAX_RIGHT ? ' · ' + t('{n} Kästchen, {max} gezeigt', { n: right, max: MAX_RIGHT }) : '')}>
          {repeat(Used, take(b.there))}{repeat(Pending, take(b.pending))}{repeat(Free, take(b.free ?? 0))}{repeat(Foreign, take(b.foreign))}
        </Area>
      </div>
      <Text size={200} align="center" style={{ color: tokens.colorNeutralForeground3 }}>{b.unitLabel}</Text>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: '6px 14px', justifyContent: 'center' }}>
        {legend(Used, t('Belegt'))}{legend(Clean, t('Wird bereinigt'))}{legend(Pending, t('Wird noch übertragen'))}{legend(Free, t('Frei'))}{legend(Blocked, t('Durch die Regler gesperrt'))}{legend(Foreign, t('Sonstige Daten'))}
      </div>
    </div>
  )
}
