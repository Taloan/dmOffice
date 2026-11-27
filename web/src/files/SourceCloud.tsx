import { t } from '../i18n'

const CLOUD = 'M7 19h13a5.5 5.5 0 0 0 .6-10.97A7.5 7.5 0 0 0 6.2 6.6 6.2 6.2 0 0 0 7 19z'

/** Liegt das Foto in einem Galerie-Ordner (externer Speicher)? Sonst in der Ablage von dmOffice (eigene oder geteilte). */
export const isExternalPhoto = (path: string) => path.startsWith('ext:')

/**
 * Herkunft eines Fotos auf der Kachel: lila dmOffice-Wolke mit Haken (wie im Logo) = Ablage in dmOffice,
 * leere Wolke = Galerie-Ordner. Dasselbe Foto kann an beiden Stellen liegen.
 */
export function SourceCloud({ path, style, saving, onSave }: { path: string; style?: React.CSSProperties; saving?: boolean; onSave?: () => void }) {
  const ext = isExternalPhoto(path)
  // Leere Wolke anklicken: in die Ablage sichern (mit allen markierten, wenn das Foto dazugehört)
  const click = ext && !saving && onSave ? onSave : undefined
  const title = saving ? t('Wird in die Ablage gesichert') : ext ? (click ? t('In einem externen Galerie-Ordner gespeichert. Klicken: In Ablage sichern') : t('In einem externen Galerie-Ordner gespeichert')) : t('In dmOffice gespeichert')
  // Immer oben rechts (unten liegen je nach Kachelgröße die Bildinfos); Größe nach der Kachel: 12 % ihrer Breite, 11 bis 18 Pixel
  return <svg viewBox="0 0 26 20" role="img" aria-label={title} style={{ position: 'absolute', right: 4, top: 5, width: 'clamp(11px, 12%, 18px)', height: 'auto', zIndex: 2, pointerEvents: 'auto', cursor: click ? 'pointer' : undefined, ...style }}
    onClick={click && (e => { e.stopPropagation(); click() })} onDoubleClick={click && (e => e.stopPropagation())}
    onMouseDown={click && (e => e.stopPropagation())} onPointerDown={click && (e => e.stopPropagation())}>
    <title>{title}</title>
    {saving
      // Wird gesichert: halb gefüllte Wolke mit Pfeil nach oben
      ? <><path d={CLOUD} fill="rgba(123,29,211,0.55)" stroke="#fff" strokeWidth={1.4} />
        <path d="M13.5 15.5v-6M10.8 12l2.7-2.7 2.7 2.7" fill="none" stroke="#fff" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round" /></>
      : ext
      ? <path d={CLOUD} fill="rgba(0,0,0,0.45)" stroke="#fff" strokeWidth={1.6} />
      : <><path d={CLOUD} fill="#7B1DD3" stroke="#fff" strokeWidth={1.2} />
        <path d="M9.5 12.2l2.6 2.6 5-5" fill="none" stroke="#fff" strokeWidth={2.2} strokeLinecap="round" strokeLinejoin="round" /></>}
  </svg>
}
