import { Button, Divider, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Toolbar, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import {
  TextBold20Regular, TextItalic20Regular, TextUnderline20Regular, TextStrikethrough20Regular, TextBulletListLtr20Regular,
  TextNumberListLtr20Regular, TextIndentIncreaseLtr20Regular, TextIndentDecreaseLtr20Regular, Link20Regular,
  TextClearFormatting20Regular, TextColor20Regular, Highlight20Regular, Image20Regular, TextFontSize20Regular, TextQuote20Regular, TextHeader120Regular,
} from '@fluentui/react-icons'
import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react'
import { t } from '../i18n'
import { askText } from './context'

const useStyles = makeStyles({
  wrap: { display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 },
  bar: { flexWrap: 'wrap', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, padding: '2px 4px' },
  area: { flex: 1, overflowY: 'auto', padding: '12px 16px', color: tokens.colorNeutralForeground1, fontSize: '14px' },
  swatches: { display: 'grid', gridTemplateColumns: 'repeat(6, 22px)', gap: '4px', padding: '6px' },
  swatch: { width: '22px', height: '22px', borderRadius: '4px', border: `1px solid ${tokens.colorNeutralStroke1}`, cursor: 'pointer', padding: 0 },
})

export const colors = ['#000000', '#444444', '#888888', '#C00000', '#E8590C', '#B8860B', '#2E7D32', '#0F6CBD', '#1F3A93', '#7B1FA2', '#AD1457', '#FFFFFF']
export const marks = ['#FFF59D', '#C5E1A5', '#B3E5FC', '#F8BBD0', '#FFE0B2', 'transparent']

export interface RichEditorHandle {
  html(): string
  text(): string
  focus(): void
  /** Einfügemarke an den Anfang des Textes setzen (Antworten: vor Signatur und Zitat). */
  focusStart(): void
  insertHtml(html: string): void
  /** Formatbefehl (execCommand) auf den Text anwenden, etwa aus dem Menüband. */
  exec(cmd: string, value?: string): void
  /** Ist der Befehl an der Einfügemarke gerade aktiv (fett, Liste …)? */
  active(cmd: string): boolean
  link(): void
  pickImage(): void
  /** Der Textbereich (für Fokus-Abfragen von außen). */
  area(): HTMLDivElement | null
}

interface Props {
  initial: string
  onChange?: () => void
  /** Bild einfuegen: Datei → Adresse fuer src (data: oder eigene). */
  onImage?: (file: File) => Promise<string>
  minimal?: boolean
  /** Schrift des Textbereichs (Standardschrift beim Verfassen). */
  areaStyle?: React.CSSProperties
  /** Sprache fuer die Rechtschreibpruefung (Sprache des Kontos). */
  lang?: string
  /** Ueberschriften anbieten (Notfalltext) */
  headings?: boolean
  /** Eigene Formatleiste zeigen (Standard); aus, wenn die Befehle im Menüband stehen */
  toolbar?: boolean
}

/** Bild für eingebetteten Text: höchstens 1600 px, als JPEG (data:-Adresse). */
export async function smallImage(file: File): Promise<string> {
  const url = URL.createObjectURL(file)
  try {
    const img = await new Promise<HTMLImageElement>((res, rej) => { const i = new Image(); i.onload = () => res(i); i.onerror = rej; i.src = url })
    const f = Math.min(1, 1600 / Math.max(img.width, img.height))
    const c = document.createElement('canvas')
    c.width = Math.round(img.width * f); c.height = Math.round(img.height * f)
    c.getContext('2d')!.drawImage(img, 0, 0, c.width, c.height)
    return c.toDataURL('image/jpeg', 0.85)
  } finally { URL.revokeObjectURL(url) }
}

/** Farbe in [r, g, b] (Schreibweisen wie der Browser sie versteht); null, wenn unbekannt oder durchsichtig. */
function rgbOf(value: string): [number, number, number] | null {
  const ctx = document.createElement('canvas').getContext('2d')
  if (!ctx) return null
  ctx.fillStyle = '#010203'
  ctx.fillStyle = value.trim()
  const v = String(ctx.fillStyle)
  if (v === '#010203' && !/^#?010203$/i.test(value.trim())) return null
  const hex = /^#([0-9a-f]{2})([0-9a-f]{2})([0-9a-f]{2})$/i.exec(v)
  if (hex) return [parseInt(hex[1], 16), parseInt(hex[2], 16), parseInt(hex[3], 16)]
  const rgba = /^rgba\((\d+),\s*(\d+),\s*(\d+),\s*([\d.]+)\)$/.exec(v)
  if (rgba && Number(rgba[4]) > 0) return [Number(rgba[1]), Number(rgba[2]), Number(rgba[3])]
  return null
}

/** Grau, Weiß oder Schwarz: Farbe des Designs der Quelle, keine gewählte Auszeichnung. */
const neutral = (value: string) => {
  const c = rgbOf(value)
  return !c || Math.max(...c) - Math.min(...c) < 40
}

/**
 * Eingefügtes HTML: Schrift- und Hintergrundfarben in Grau, Weiß oder Schwarz entfernen. Sie stammen aus dem Design
 * der Quelle (eine dunkle Seite liefert hellgraue Schrift) und wären beim Empfänger auf Weiß kaum lesbar.
 * Bunte Farben (rot, Markierungen) bleiben.
 */
export function cleanPasted(html: string): string {
  const doc = new DOMParser().parseFromString(html, 'text/html')
  doc.querySelectorAll('script,style,meta,link,title').forEach(n => n.remove())
  doc.body.querySelectorAll<HTMLElement>('*').forEach(el => {
    const st = el.style
    if (st?.color && neutral(st.color)) st.removeProperty('color')
    if (st?.backgroundColor && neutral(st.backgroundColor)) st.removeProperty('background-color')
    if (st?.background && !st.backgroundImage && neutral(st.background)) st.removeProperty('background')
    if (el.getAttribute('color') && neutral(el.getAttribute('color')!)) el.removeAttribute('color')
    if (el.getAttribute('bgcolor') && neutral(el.getAttribute('bgcolor')!)) el.removeAttribute('bgcolor')
    if (el.getAttribute('style') === '') el.removeAttribute('style')
  })
  return doc.body.innerHTML
}

/** Formatierter Text wie in Outlook: contentEditable mit den gaengigen Befehlen. */
export const RichEditor = forwardRef<RichEditorHandle, Props>(function RichEditor({ initial, onChange, onImage, minimal, areaStyle, lang, headings, toolbar = true }, ref) {
  const s = useStyles()
  const area = useRef<HTMLDivElement>(null)
  const fileInput = useRef<HTMLInputElement>(null)

  useEffect(() => {
    if (area.current) area.current.innerHTML = initial
    document.execCommand('styleWithCSS', false, 'true')
    document.execCommand('defaultParagraphSeparator', false, 'div')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useImperativeHandle(ref, () => ({
    html: () => area.current?.innerHTML ?? '',
    text: () => area.current?.innerText ?? '',
    focus: () => area.current?.focus(),
    focusStart: () => {
      const a = area.current
      if (!a) return
      a.focus()
      const r = document.createRange()
      r.setStart(a.firstChild ?? a, 0)
      r.collapse(true)
      const sel = window.getSelection()
      sel?.removeAllRanges()
      sel?.addRange(r)
      a.scrollTop = 0
    },
    insertHtml: (html: string) => { area.current?.focus(); document.execCommand('insertHTML', false, html); onChange?.() },
    exec: (cmd: string, value?: string) => exec(cmd, value),
    active: (cmd: string) => { try { return document.queryCommandState(cmd) } catch { return false } },
    link: () => link(),
    pickImage: () => fileInput.current?.click(),
    area: () => area.current,
  }))

  const exec = (cmd: string, value?: string) => {
    area.current?.focus()
    document.execCommand(cmd, false, value)
    onChange?.()
  }

  const link = async () => {
    // Markierung merken: Der Dialog nimmt dem Editor den Fokus
    const sel = window.getSelection()
    const range = sel && sel.rangeCount && area.current?.contains(sel.anchorNode) ? sel.getRangeAt(0).cloneRange() : null
    const url = await askText(t('Link'), 'https://', t('Adresse'))
    if (!url) return
    area.current?.focus()
    if (range) { const s = window.getSelection(); s?.removeAllRanges(); s?.addRange(range) }
    exec('createLink', url)
  }

  const image = async (file: File) => {
    const src = onImage ? await onImage(file) : await new Promise<string>(res => {
      const r = new FileReader()
      r.onload = () => res(r.result as string)
      r.readAsDataURL(file)
    })
    exec('insertHTML', `<img src="${src}" style="max-width:100%">`)
  }

  const onPaste = (e: React.ClipboardEvent) => {
    const file = Array.from(e.clipboardData.files).find(f => f.type.startsWith('image/'))
    if (file) { e.preventDefault(); image(file); return }
    const html = e.clipboardData.getData('text/html')
    if (!html) return
    e.preventDefault()
    exec('insertHTML', cleanPasted(html))
  }

  const btn = (label: string, icon: JSX.Element, cmd: string, value?: string) => (
    <Tooltip content={label} relationship="label">
      <Button appearance="subtle" size="small" icon={icon} onMouseDown={e => e.preventDefault()} onClick={() => exec(cmd, value)} />
    </Tooltip>
  )

  return (
    <div className={s.wrap}>
      {toolbar && <Toolbar size="small" className={s.bar}>
        {headings && (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Tooltip content={t('Überschrift')} relationship="label">
                <Button appearance="subtle" size="small" icon={<TextHeader120Regular />} onMouseDown={e => e.preventDefault()} />
              </Tooltip>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {[[t('Überschrift'), 'h2'], [t('Unterüberschrift'), 'h3'], [t('Text'), 'div']].map(([l, v]) => (
                  <MenuItem key={v} onMouseDown={e => e.preventDefault()} onClick={() => exec('formatBlock', v)}>{l}</MenuItem>
                ))}
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        {btn(t('Fett (Strg+B)'), <TextBold20Regular />, 'bold')}
        {btn(t('Kursiv (Strg+I)'), <TextItalic20Regular />, 'italic')}
        {btn(t('Unterstrichen (Strg+U)'), <TextUnderline20Regular />, 'underline')}
        {!minimal && btn(t('Durchgestrichen'), <TextStrikethrough20Regular />, 'strikeThrough')}
        {!minimal && (
          <Menu>
            <MenuTrigger disableButtonEnhancement>
              <Tooltip content={t('Schriftgröße')} relationship="label">
                <Button appearance="subtle" size="small" icon={<TextFontSize20Regular />} onMouseDown={e => e.preventDefault()} />
              </Tooltip>
            </MenuTrigger>
            <MenuPopover>
              <MenuList>
                {[[t('Klein'), '2'], [t('Normal'), '3'], [t('Groß'), '5'], [t('Sehr groß'), '6']].map(([l, v]) => (
                  <MenuItem key={v} onMouseDown={e => e.preventDefault()} onClick={() => exec('fontSize', v)}>{l}</MenuItem>
                ))}
              </MenuList>
            </MenuPopover>
          </Menu>
        )}
        <Swatches label={t('Schriftfarbe')} icon={<TextColor20Regular />} values={colors} onPick={c => exec('foreColor', c)} />
        <Swatches label={t('Hervorheben')} icon={<Highlight20Regular />} values={marks} onPick={c => exec('hiliteColor', c)} />
        <Divider vertical style={{ height: 20 }} />
        {btn(t('Aufzählung'), <TextBulletListLtr20Regular />, 'insertUnorderedList')}
        {btn(t('Nummerierung'), <TextNumberListLtr20Regular />, 'insertOrderedList')}
        {!minimal && btn(t('Einzug verkleinern'), <TextIndentDecreaseLtr20Regular />, 'outdent')}
        {!minimal && btn(t('Einzug vergrößern'), <TextIndentIncreaseLtr20Regular />, 'indent')}
        {!minimal && btn(t('Zitat'), <TextQuote20Regular />, 'formatBlock', 'blockquote')}
        <Tooltip content={t('Link')} relationship="label">
          <Button appearance="subtle" size="small" icon={<Link20Regular />} onMouseDown={e => e.preventDefault()} onClick={link} />
        </Tooltip>
        {!minimal && (
          <Tooltip content={t('Bild einfügen')} relationship="label">
            <Button appearance="subtle" size="small" icon={<Image20Regular />} onClick={() => fileInput.current?.click()} />
          </Tooltip>
        )}
        {btn(t('Formatierung entfernen'), <TextClearFormatting20Regular />, 'removeFormat')}
      </Toolbar>}
      <input ref={fileInput} type="file" accept="image/*" hidden onChange={e => { const f = e.target.files?.[0]; if (f) image(f); e.target.value = '' }} />
      {/* minHeight 0 inline: .dm-editor (global.css) setzt min-height: 100 % - im festen Rahmen ragte die Fläche sonst um die Formatleiste heraus */}
      <div ref={area} className={`${s.area} dm-editor`} style={{ minHeight: 0, ...areaStyle }} contentEditable suppressContentEditableWarning
        onInput={() => onChange?.()} onPaste={onPaste} spellCheck lang={lang} />
    </div>
  )
})

export function Swatches({ label, icon, values, onPick, disabled }: { label: string; icon: JSX.Element; values: string[]; onPick: (c: string) => void; disabled?: boolean }) {
  const s = useStyles()
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content={label} relationship="label">
          <Button appearance="subtle" size="small" icon={icon} disabled={disabled} onMouseDown={e => e.preventDefault()} />
        </Tooltip>
      </MenuTrigger>
      <MenuPopover>
        <div className={s.swatches}>
          {values.map(c => (
            <button key={c} className={s.swatch} style={{ background: c === 'transparent' ? 'repeating-linear-gradient(45deg,#ccc 0 3px,#fff 3px 6px)' : c }}
              title={c === 'transparent' ? t('Keine') : c} onMouseDown={e => e.preventDefault()} onClick={() => onPick(c)} />
          ))}
        </div>
      </MenuPopover>
    </Menu>
  )
}
