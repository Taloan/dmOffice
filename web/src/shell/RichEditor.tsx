import { Button, Divider, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Toolbar, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import {
  TextBold20Regular, TextItalic20Regular, TextUnderline20Regular, TextStrikethrough20Regular, TextBulletListLtr20Regular,
  TextNumberListLtr20Regular, TextIndentIncreaseLtr20Regular, TextIndentDecreaseLtr20Regular, Link20Regular,
  TextClearFormatting20Regular, TextColor20Regular, Highlight20Regular, Image20Regular, TextFontSize20Regular, TextQuote20Regular,
} from '@fluentui/react-icons'
import { forwardRef, useEffect, useImperativeHandle, useRef } from 'react'
import { t } from '../i18n'

const useStyles = makeStyles({
  wrap: { display: 'flex', flexDirection: 'column', minHeight: 0, flex: 1 },
  bar: { flexWrap: 'wrap', borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, padding: '2px 4px' },
  area: { flex: 1, overflowY: 'auto', padding: '12px 16px', color: tokens.colorNeutralForeground1, fontSize: '14px' },
  swatches: { display: 'grid', gridTemplateColumns: 'repeat(6, 22px)', gap: '4px', padding: '6px' },
  swatch: { width: '22px', height: '22px', borderRadius: '4px', border: `1px solid ${tokens.colorNeutralStroke1}`, cursor: 'pointer', padding: 0 },
})

const colors = ['#000000', '#444444', '#888888', '#C00000', '#E8590C', '#B8860B', '#2E7D32', '#0F6CBD', '#1F3A93', '#7B1FA2', '#AD1457', '#FFFFFF']
const marks = ['#FFF59D', '#C5E1A5', '#B3E5FC', '#F8BBD0', '#FFE0B2', 'transparent']

export interface RichEditorHandle {
  html(): string
  text(): string
  focus(): void
  insertHtml(html: string): void
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
}

/** Formatierter Text wie in Outlook: contentEditable mit den gaengigen Befehlen. */
export const RichEditor = forwardRef<RichEditorHandle, Props>(function RichEditor({ initial, onChange, onImage, minimal, areaStyle, lang }, ref) {
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
    insertHtml: (html: string) => { area.current?.focus(); document.execCommand('insertHTML', false, html) },
  }))

  const exec = (cmd: string, value?: string) => {
    area.current?.focus()
    document.execCommand(cmd, false, value)
    onChange?.()
  }

  const link = () => {
    const url = prompt(t('Adresse des Links:'), 'https://')
    if (url) exec('createLink', url)
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
    if (file) { e.preventDefault(); image(file) }
  }

  const btn = (label: string, icon: JSX.Element, cmd: string, value?: string) => (
    <Tooltip content={label} relationship="label">
      <Button appearance="subtle" size="small" icon={icon} onMouseDown={e => e.preventDefault()} onClick={() => exec(cmd, value)} />
    </Tooltip>
  )

  return (
    <div className={s.wrap}>
      <Toolbar size="small" className={s.bar}>
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
      </Toolbar>
      <input ref={fileInput} type="file" accept="image/*" hidden onChange={e => { const f = e.target.files?.[0]; if (f) image(f); e.target.value = '' }} />
      <div ref={area} className={`${s.area} dm-editor`} style={areaStyle} contentEditable suppressContentEditableWarning
        onInput={() => onChange?.()} onPaste={onPaste} spellCheck lang={lang} />
    </div>
  )
})

function Swatches({ label, icon, values, onPick }: { label: string; icon: JSX.Element; values: string[]; onPick: (c: string) => void }) {
  const s = useStyles()
  return (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content={label} relationship="label">
          <Button appearance="subtle" size="small" icon={icon} onMouseDown={e => e.preventDefault()} />
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
