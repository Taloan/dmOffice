import { Button, Input, Text, makeStyles, tokens } from '@fluentui/react-components'
import { useEffect, useRef, useState } from 'react'
import { featureOn } from '../api'
import { usePrefs } from '../prefs'
import { t } from '../i18n'
import { ArrowReset20Regular } from '@fluentui/react-icons'
import { RibbonActions, RibbonButton } from '../shell/Ribbon'
import type { Item } from '../files/common'
import { gallerySource } from '../files/GalleryFolders'
import { galleryMonth, galleryMonths } from '../files/GalleryTimeline'
import type { Meta } from '../files/PhotoFilter'
import { TileHead } from '../files/TileBar'
import { DEFAULT_TILE_HEAD, TILE_FIELDS, insertAt, tileHeadOf, unknownTileFields, type TileField } from '../files/tileTemplate'
import { Hint, Section } from './parts'

const useStyles = makeStyles({
  grid: { display: 'grid', gridTemplateColumns: 'auto minmax(0, 1fr) minmax(0, 1fr)', gap: '6px 8px', alignItems: 'center', maxWidth: '720px' },
  field: { minWidth: 0, width: '100%' },
  cloud: { display: 'flex', flexWrap: 'wrap', gap: '4px', maxWidth: '720px' },
  tile: { width: '230px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, overflow: 'hidden', backgroundColor: tokens.colorNeutralBackground1 },
  img: { display: 'block', width: '100%', height: '150px', objectFit: 'cover', backgroundColor: tokens.colorNeutralBackground3 },
  muted: { color: tokens.colorNeutralForeground3 },
  err: { color: tokens.colorPaletteRedForeground1 },
})

/** Namen der Felder in der „Wolke“ (der Platzhalter selbst bleibt deutsch und steht im Tooltip). */
const LABELS: Record<TileField, () => string> = {
  titel: () => t('Titel'), beschreibung: () => t('Beschreibung'), tags: () => t('Tags'),
  'aufn-datum': () => t('Aufnahmedatum'), 'aufn-zeit': () => t('Aufnahmezeit'), 'datum-zeit': () => t('Datum und Zeit'),
  'aufn-yy': () => t('Jahr (zweistellig)'), 'aufn-mm': () => t('Monat'), 'aufn-dd': () => t('Tag des Monats'),
  kamera: () => t('Kamera'), objektiv: () => t('Objektiv'), blende: () => t('Blende'), 'bel-zeit': () => t('Belichtungszeit'),
  iso: () => 'ISO', brennweite: () => t('Brennweite'), filename: () => t('Dateiname'), ext: () => t('Dateiart'), groesse: () => t('Dateigröße'),
  abmessung: () => t('Abmessung'), dauer: () => t('Dauer'), sterne: () => t('Sterne'), farbe: () => t('Farbmarkierung'), ordner: () => t('Galerie-Ordner'),
}

/** Ein Foto für die Vorschau: aus dem neuesten Monat das mit den meisten Bildangaben. */
async function previewPhoto(): Promise<{ item: Item; meta?: Meta } | null> {
  const months = await galleryMonths()
  for (const m of months.slice(0, 3)) {
    const { items, meta } = await galleryMonth(m.k)
    const score = (x: Meta | undefined) => !x ? 0 : [x.title, x.description, x.camera, x.lens, x.aperture, x.exposure, x.focal].filter(Boolean).length
    const best = [...items].sort((a, b) => score(meta[b.path]) - score(meta[a.path]))[0]
    if (best) return { item: best, meta: meta[best.path] }
  }
  return null
}

/** Einstellungen → Galerie → Kachel (Kennung tilehead): Kopfzeile der Kachel „Voll“ frei belegen. */
export function TileHeadSettings() {
  const s = useStyles()
  const { prefs, update } = usePrefs()
  const [rows, setRows] = useState<string[]>(() => tileHeadOf(prefs.photoTileHead))
  const [photo, setPhoto] = useState<{ item: Item; meta?: Meta } | null | undefined>(undefined)
  const inputs = useRef<(HTMLInputElement | null)[]>([])
  const active = useRef(0)
  const saveTimer = useRef<number>()

  useEffect(() => { previewPhoto().then(setPhoto).catch(() => setPhoto(null)) }, [])

  const change = (next: string[]) => {
    setRows(next)
    window.clearTimeout(saveTimer.current)
    saveTimer.current = window.setTimeout(() => update({ photoTileHead: next }), 400)
  }
  const insert = (field: TileField) => {
    const i = active.current, el = inputs.current[i]
    const r = insertAt(rows[i], el?.selectionStart ?? rows[i].length, el?.selectionEnd ?? rows[i].length, `{${field}}`)
    change(rows.map((x, k) => k === i ? r.text : x))
    requestAnimationFrame(() => { el?.focus(); el?.setSelectionRange(r.cursor, r.cursor) })
  }
  const unknown = unknownTileFields(rows)
  const reset = () => { window.clearTimeout(saveTimer.current); setRows([...DEFAULT_TILE_HEAD]); update({ photoTileHead: undefined }) }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 20 }}>
      <Section title={t('Kopfzeile der Kachel')} hint={featureOn('mosaicweb') ? t('Gilt für die Kachel „Voll“ in der Galerie. Vier Zeilen, je links und rechts. Im Mosaik gelten Infozeilen nicht; dort erscheinen Titel, Datum und Kamera beim Überfahren.') : t('Gilt für die Kachel „Voll“ in der Galerie. Vier Zeilen, je links und rechts.')}>
        <div className={s.grid}>
          <span />
          <Text size={200} className={s.muted}>{t('links')}</Text>
          <Text size={200} className={s.muted}>{t('rechts')}</Text>
          {[0, 1, 2, 3].map(line => [
            <Text key={'l' + line} size={200}>{t('Zeile {n}', { n: line + 1 })}</Text>,
            ...[0, 1].map(side => {
              const i = line * 2 + side
              return <Input key={i} className={s.field} value={rows[i]} input={{ ref: (el: HTMLInputElement | null) => { inputs.current[i] = el } }}
                onFocus={() => { active.current = i }} onChange={(_, d) => change(rows.map((x, k) => k === i ? d.value : x))}
                aria-label={t('Zeile {n}', { n: line + 1 }) + ' ' + (side ? t('rechts') : t('links'))} />
            }),
          ])}
        </div>
        {unknown.length > 0 && <Text size={200} className={s.err}>{t('Unbekannter Platzhalter: {list}', { list: unknown.map(x => `{${x}}`).join(', ') })}</Text>}
        <Hint>{t('Feld anklicken: es kommt an die Stelle des Cursors im zuletzt gewählten Eingabefeld. Text und Felder lassen sich mischen.')}</Hint>
        <div className={s.cloud}>
          {TILE_FIELDS.map(f => (
            <Button key={f} size="small" title={`{${f}}`} onMouseDown={e => e.preventDefault()} onClick={() => insert(f)}>{LABELS[f]()}</Button>
          ))}
        </div>
        <Hint>{t('Text vor und nach einem Feld erscheint nur, wenn das Feld einen Wert hat: {"f"blende} ergibt „f2,8“, ohne Blende nichts.')}</Hint>
        <RibbonActions><RibbonButton icon={<ArrowReset20Regular />} label={t('Vorgabe wiederherstellen')} onClick={reset} /></RibbonActions>
      </Section>
      <Section title={t('Vorschau')} last>
        {photo === undefined ? <Text size={200} className={s.muted}>{t('Lädt …')}</Text>
          : photo === null ? <Text size={200} className={s.muted}>{t('Noch keine Fotos in der Galerie.')}</Text>
          : <div className={s.tile}>
              <TileHead item={photo.item} meta={photo.meta} templates={rows} />
              <img className={s.img} src={gallerySource.thumb(photo.item, 256)} alt="" />
            </div>}
      </Section>
    </div>
  )
}
