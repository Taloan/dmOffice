import { Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import {
  ArrowRedo20Regular, ArrowUndo20Regular, Attach20Regular, Cloud20Regular, CloudLink20Regular, Desktop20Regular, DocumentSignature20Regular, Highlight20Regular, Image20Regular,
  ImageMultiple20Regular, Important20Regular, Link20Regular, LockClosed20Regular, LockOpen20Regular, MailCheckmark20Regular, Save20Regular,
  TextAlignCenter20Regular, TextAlignLeft20Regular, TextAlignRight20Regular, TextBold20Regular, TextBulletListLtr20Regular, TextClearFormatting20Regular,
  TextColor20Regular, TextFont20Regular, TextFontSize20Regular, TextIndentDecreaseLtr20Regular, TextIndentIncreaseLtr20Regular, TextItalic20Regular,
  TextNumberListLtr20Regular, TextQuote20Regular, TextStrikethrough20Regular, TextUnderline20Regular,
  DocumentBulletList20Regular,
} from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { t } from '../i18n'
import { RibbonButton, RibbonDivider } from '../shell/Ribbon'
import { RichEditorHandle, Swatches, colors, marks } from '../shell/RichEditor'
import { featureOn } from '../api'
import { SnippetMenu } from './Snippets' // [feature:mailsnippets]
import { SnippetContext } from './snippets' // [feature:mailsnippets]
import { AiReplyMenu, aiHtml, useAiReply } from './AiReply' // [feature:mailaireply]

const useStyles = makeStyles({
  group: { display: 'flex', flexDirection: 'column', alignItems: 'center', flexShrink: 0, padding: '0 4px' },
  body: { display: 'grid', gridAutoFlow: 'column', gridTemplateRows: 'repeat(2, 30px)', columnGap: '2px', rowGap: '2px', alignItems: 'center', height: '62px' },
  label: { fontSize: '11px', lineHeight: '14px', color: tokens.colorNeutralForeground3, whiteSpace: 'nowrap' },
  on: { backgroundColor: tokens.colorNeutralBackground1Selected },
})

const FONTS = ['Aptos', 'Calibri', 'Arial', 'Segoe UI', 'Verdana', 'Georgia', 'Times New Roman', 'Courier New']
const SIZES: [string, string][] = [['8', '1'], ['10', '2'], ['12', '3'], ['14', '4'], ['18', '5'], ['24', '6'], ['36', '7']]
// Befehle, deren Zustand (an/aus) die Knöpfe zeigen
const STATES = ['bold', 'italic', 'underline', 'strikeThrough', 'insertUnorderedList', 'insertOrderedList', 'justifyLeft', 'justifyCenter', 'justifyRight']

function Group({ label, children }: { label: string; children: React.ReactNode }) {
  const s = useStyles()
  return <div className={s.group} role="group" aria-label={label}><div className={s.body}>{children}</div><span className={s.label}>{label}</span></div>
}

export type AttachSource = 'computer' | 'gallery' | 'files' | 'dms'
/** Anhang, Freigabelink oder Bild im Text */
export type AttachAs = 'attach' | 'link' | 'inline'

export interface MessageRibbonProps {
  editor: React.RefObject<RichEditorHandle>
  /** Einfügemarke steht im Text; sonst sind die Formatknöpfe grau (wie in Outlook) */
  active: boolean
  onAttach: (from: AttachSource, as: AttachAs) => void
  onSignature?: () => void
  important: boolean; onImportant: (v: boolean) => void
  receipt: boolean; onReceipt: (v: boolean) => void
  pgp?: { sign: boolean; onChange: (v: boolean) => void }
  /** Weitere Umschalter der Nachricht hinter „Signieren“ (Nachfassen, Mit Passwort) */
  options?: React.ReactNode
  onSave: () => void
  /** Text geändert (Textbaustein eingefügt) */
  onChange?: () => void
  snippetContext?: () => SnippetContext // [feature:mailsnippets]
  /** Beim Antworten: die beantwortete Mail - für „Antwort mit KI“ (Kennung mailaireply) */
  aiSource?: { account: string; folder: string; uid: number } // [feature:mailaireply]
}

/** Reiter „Nachricht“ im Menüband beim Schreiben: Text formatieren, einfügen, kennzeichnen. */
export function MessageRibbon({ editor, active, onAttach, onSignature, important, onImportant, receipt, onReceipt, pgp, onSave, onChange, ...more }: MessageRibbonProps) {
  const s = useStyles()
  const ai = useAiReply() // [feature:mailaireply]
  const [on, setOn] = useState<Record<string, boolean>>({})
  // Zustand der Knöpfe folgt der Einfügemarke
  useEffect(() => {
    const read = () => {
      const area = editor.current?.area()
      const sel = document.getSelection()
      if (!area || !sel?.anchorNode || !area.contains(sel.anchorNode)) return
      setOn(Object.fromEntries(STATES.map(c => [c, editor.current?.active(c) ?? false])))
    }
    document.addEventListener('selectionchange', read)
    return () => document.removeEventListener('selectionchange', read)
  }, [editor])
  const ex = (cmd: string, value?: string) => editor.current?.exec(cmd, value)
  // Kleiner Knopf; mousedown verhindert, dass der Text den Fokus (und die Markierung) verliert
  const btn = (label: string, icon: JSX.Element, cmd: string, value?: string) => (
    <Tooltip content={label} relationship="label">
      <Button appearance="subtle" size="small" icon={icon} disabled={!active} className={on[cmd] ? s.on : undefined} aria-pressed={on[cmd]}
        onMouseDown={e => e.preventDefault()} onClick={() => ex(cmd, value)} />
    </Tooltip>
  )
  const menu = (label: string, icon: JSX.Element, items: [string, () => void][]) => (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <Tooltip content={label} relationship="label">
          <Button appearance="subtle" size="small" icon={icon} disabled={!active} onMouseDown={e => e.preventDefault()} />
        </Tooltip>
      </MenuTrigger>
      <MenuPopover><MenuList>{items.map(([l, f]) => <MenuItem key={l} onMouseDown={e => e.preventDefault()} onClick={f}>{l}</MenuItem>)}</MenuList></MenuPopover>
    </Menu>
  )
  // Je Art die Quelle wählen; als Link nur, was in der eigenen Ablage liegt (Galerie-Ordner gehen als Anhang)
  const attachMenu = (icon: JSX.Element, label: string, as: AttachAs) => (
    <Menu>
      <MenuTrigger disableButtonEnhancement>
        <RibbonButton icon={icon} label={label} disabled={as === 'inline' && !active} onMouseDown={(e: React.MouseEvent) => e.preventDefault()} />
      </MenuTrigger>
      <MenuPopover>
        <MenuList>
          <MenuItem icon={<Desktop20Regular />} onClick={() => onAttach('computer', as)}>{t('Vom Computer')}</MenuItem>
          <MenuItem icon={<ImageMultiple20Regular />} onClick={() => onAttach('gallery', as)}>{t('Aus der dmOffice-Galerie')}</MenuItem>
          <MenuItem icon={<Cloud20Regular />} onClick={() => onAttach('files', as)}>{t('Aus den dmOffice-Dateien')}</MenuItem>
          {as === 'attach' && featureOn('maildms') && featureOn('dms') && <MenuItem icon={<DocumentBulletList20Regular />} onClick={() => onAttach('dms', as)}>{t('Aus dem DMS')}</MenuItem>}
        </MenuList>
      </MenuPopover>
    </Menu>
  )
  return <>
    <Group label={t('Rückgängig')}>
      {btn(t('Rückgängig (Strg+Z)'), <ArrowUndo20Regular />, 'undo')}
      {btn(t('Wiederholen (Strg+Y)'), <ArrowRedo20Regular />, 'redo')}
    </Group>
    <RibbonDivider />
    <Group label={t('Basistext')}>
      {menu(t('Schriftart'), <TextFont20Regular />, FONTS.map(f => [f, () => ex('fontName', f)]))}
      {menu(t('Schriftgröße'), <TextFontSize20Regular />, SIZES.map(([l, v]) => [l + ' pt', () => ex('fontSize', v)]))}
      {btn(t('Fett (Strg+B)'), <TextBold20Regular />, 'bold')}
      {btn(t('Kursiv (Strg+I)'), <TextItalic20Regular />, 'italic')}
      {btn(t('Unterstrichen (Strg+U)'), <TextUnderline20Regular />, 'underline')}
      {btn(t('Durchgestrichen'), <TextStrikethrough20Regular />, 'strikeThrough')}
      <Swatches label={t('Schriftfarbe')} icon={<TextColor20Regular />} values={colors} disabled={!active} onPick={c => ex('foreColor', c)} />
      <Swatches label={t('Hervorheben')} icon={<Highlight20Regular />} values={marks} disabled={!active} onPick={c => ex('hiliteColor', c)} />
      {btn(t('Formatierung entfernen'), <TextClearFormatting20Regular />, 'removeFormat')}
    </Group>
    <RibbonDivider />
    <Group label={t('Absatz')}>
      {btn(t('Aufzählung'), <TextBulletListLtr20Regular />, 'insertUnorderedList')}
      {btn(t('Nummerierung'), <TextNumberListLtr20Regular />, 'insertOrderedList')}
      {btn(t('Einzug verkleinern'), <TextIndentDecreaseLtr20Regular />, 'outdent')}
      {btn(t('Einzug vergrößern'), <TextIndentIncreaseLtr20Regular />, 'indent')}
      {btn(t('Linksbündig'), <TextAlignLeft20Regular />, 'justifyLeft')}
      {btn(t('Zentriert'), <TextAlignCenter20Regular />, 'justifyCenter')}
      {btn(t('Rechtsbündig'), <TextAlignRight20Regular />, 'justifyRight')}
      {btn(t('Zitat'), <TextQuote20Regular />, 'formatBlock', 'blockquote')}
    </Group>
    <RibbonDivider />
    {attachMenu(<Attach20Regular />, t('Anhängen'), 'attach')}
    {attachMenu(<CloudLink20Regular />, t('Als Link'), 'link')}
    {attachMenu(<Image20Regular />, t('Bild im Text'), 'inline')}
    <RibbonButton icon={<Link20Regular />} label={t('Link')} disabled={!active} onMouseDown={(e: React.MouseEvent) => e.preventDefault()} onClick={() => editor.current?.link()} />
    {onSignature && <RibbonButton icon={<DocumentSignature20Regular />} label={t('Signatur')} disabled={!active} onMouseDown={(e: React.MouseEvent) => e.preventDefault()} onClick={onSignature} />}
    {featureOn('mailsnippets') && <SnippetMenu editor={editor} onInserted={onChange} context={more.snippetContext} />} {/* [feature:mailsnippets] */}
    {/* [feature:mailaireply] */}
    {ai && more.aiSource && <AiReplyMenu source={more.aiSource} onDraft={text => {
      // Vor den bisherigen Text (Signatur, Zitat) - nichts wird ersetzt
      editor.current?.focusStart(); editor.current?.insertHtml(aiHtml(text)); onChange?.()
    }} />}
    {/* [/feature:mailaireply] */}
    <RibbonDivider />
    <RibbonButton icon={<Important20Regular />} label={t('Wichtig')} pressed={important} onClick={() => onImportant(!important)} />
    <RibbonButton icon={<MailCheckmark20Regular />} label={t('Lese­bestätigung')} pressed={receipt} onClick={() => onReceipt(!receipt)} />
    {pgp && <RibbonButton icon={pgp.sign ? <LockClosed20Regular /> : <LockOpen20Regular />} label={t('Signieren')} pressed={pgp.sign} onClick={() => pgp.onChange(!pgp.sign)} />}
    {more.options}
    <RibbonDivider />
    <RibbonButton icon={<Save20Regular />} label={t('Entwurf speichern')} onClick={onSave} />
  </>
}
