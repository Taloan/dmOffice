// Textbausteine beim Schreiben von Mails (Kennung mailsnippets, 2.2): Menü „Textbaustein“ im Menüband, „::“ und Suchwort
// im Text, Markiertes als Textbaustein speichern, Verwaltung (Dialog und Einstellungsseite). Ohne Platzhalter - die gibt es
// nur bei den Textbausteinen des Helpdesks; die erscheinen hier mit, wenn der Helpdesk an ist (gekennzeichnet, Platzhalter gefüllt,
// soweit es ohne Ticket geht; verwalten nur im Helpdesk).
import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Divider, Field, Input, MenuItem, Popover,
  PopoverSurface, PopoverTrigger, Portal, Spinner, Text, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { Add20Regular, Delete20Regular, DocumentText20Regular, Edit20Regular, Save20Regular, Search20Regular, Settings20Regular } from '@fluentui/react-icons'
import { useContext, useEffect, useRef, useState } from 'react'
import { useApp } from '../App'
import { t } from '../i18n'
import { askConfirm } from '../shell/context'
import { RibbonActions, RibbonButton, RibbonSlot } from '../shell/Ribbon'
import { RichEditor, RichEditorHandle } from '../shell/RichEditor'
import { AdminCol, AdminTable, pickCol } from '../admin/AdminTable'
import { MailSnippet, SnippetContext, deleteSnippet, matchSnippets, saveSnippet, snippetText, useSnippets } from './snippets'

const useStyles = makeStyles({
  suggest: {
    position: 'fixed', zIndex: 1000000, minWidth: '260px', maxWidth: '420px', display: 'flex', flexDirection: 'column',
    backgroundColor: tokens.colorNeutralBackground1, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow16, padding: '2px 0',
  },
  suggestItem: { padding: '4px 10px', cursor: 'pointer', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  suggestOn: { backgroundColor: tokens.colorBrandBackground2 },
  muted: { color: tokens.colorNeutralForeground3 },
  tag: { marginLeft: '8px', fontSize: tokens.fontSizeBase100, color: tokens.colorNeutralForeground3, border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusSmall, padding: '0 4px' },
  menu: { width: '340px', display: 'flex', flexDirection: 'column', gap: '6px', padding: '4px' },
  list: { maxHeight: '300px', overflowY: 'auto', display: 'flex', flexDirection: 'column' },
  manager: { display: 'flex', flexDirection: 'column', gap: '12px', minHeight: 0 },
  preview: {
    border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium, padding: '8px 12px', maxHeight: '240px', overflowY: 'auto',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  buttons: { display: 'flex', gap: '8px', flexWrap: 'wrap' },
  editor: { height: '220px', display: 'flex', flexDirection: 'column', border: `1px solid ${tokens.colorNeutralStroke1}`, borderRadius: tokens.borderRadiusMedium, overflow: 'hidden' },
})

/** Eigene Bausteine, dazu die des Helpdesks (falls an und erlaubt); null, solange die eigenen laden. */
function useSnippetList(enabled: boolean, lang?: string): MailSnippet[] | null {
  const own = useSnippets(enabled)
  return own
}

/** Einzufügender Text: eigener Baustein wie gespeichert, Helpdesk-Baustein mit gefüllten Platzhaltern. */
function htmlOf(x: MailSnippet, c: SnippetContext): string {
  return x.html
}

/** Kürzel mit dem schon eingetippten Anfang fett, dann der Titel; Helpdesk-Bausteine gekennzeichnet. */
function Label({ x, query }: { x: MailSnippet; query: string }) {
  const s = useStyles()
  const q = query.trim().toLowerCase()
  const n = x.code && q && x.code.startsWith(q) ? q.length : 0
  return <>{x.code && <><span><b>{x.code.slice(0, n)}</b>{x.code.slice(n)}</span> – </>}{x.title}{x.hd && <span className={s.tag}>{t('Helpdesk')}</span>}</>
}

/** HTML an der Markierung einsetzen – über Range statt execCommand, sonst packt der Browser den Text in <span style="font-size: …">. */
function put(area: HTMLElement, html: string) {
  const sel = window.getSelection()
  if (!sel || !sel.rangeCount || !area.contains(sel.anchorNode)) return
  const r = sel.getRangeAt(0)
  r.deleteContents()
  const tpl = document.createElement('template')
  tpl.innerHTML = html
  const last = tpl.content.lastChild
  r.insertNode(tpl.content)
  if (last) { r.setStartAfter(last); r.collapse(true); sel.removeAllRanges(); sel.addRange(r) }
  // Leere Textknoten vom Teilen entfernen (sonst greift eine Markierung des ersten Knotens ins Leere)
  const walk = document.createTreeWalker(area, NodeFilter.SHOW_TEXT)
  const empty: Node[] = []
  for (let n = walk.nextNode(); n; n = walk.nextNode()) if (!n.textContent && n !== sel.anchorNode) empty.push(n)
  empty.forEach(n => n.parentNode?.removeChild(n))
  area.dispatchEvent(new Event('input', { bubbles: true }))
}

/** Baustein an der Stelle einfügen (range: gesicherte Markierung, sonst die aktuelle Einfügemarke im Text). */
function insertAt(editor: RichEditorHandle | null, html: string, range: Range | null) {
  const area = editor?.area()
  if (!editor || !area) return
  area.focus()
  const sel = window.getSelection()
  if (range && area.contains(range.startContainer)) { sel?.removeAllRanges(); sel?.addRange(range) }
  else if (!sel?.anchorNode || !area.contains(sel.anchorNode)) editor.focusStart()
  put(area, html)
}

/** Markierung im Text, falls eine besteht. */
function selectionIn(area: HTMLElement | null | undefined): Range | null {
  const sel = window.getSelection()
  if (!area || !sel || !sel.rangeCount) return null
  const r = sel.getRangeAt(0)
  return area.contains(r.commonAncestorContainer) ? r.cloneRange() : null
}

/** Inhalt einer Markierung als HTML. */
function rangeHtml(r: Range): string {
  const d = document.createElement('div')
  d.appendChild(r.cloneContents())
  return d.innerHTML
}

/** Vorschlag für den Titel: die ersten Wörter, höchstens 60 Zeichen. */
function titleOf(html: string): string {
  const text = snippetText(html)
  if (text.length <= 60) return text
  const cut = text.slice(0, 60)
  const sp = cut.lastIndexOf(' ')
  return (sp > 20 ? cut.slice(0, sp) : cut) + ' …'
}

// ---------------- „::“ im Text ----------------

interface Shortcut { node: Text; start: number; end: number; query: string; x: number; y: number }

/**
 * „::“ und Suchwort im Text: Liste der passenden Bausteine an der Schreibmarke, Pfeiltasten wählen, Enter oder Tab
 * übernimmt, Esc schließt. Gibt die Liste zurück (per Portal, fest an der Schreibmarke).
 */
export function useSnippetShortcut(editor: React.RefObject<RichEditorHandle>, onInserted?: () => void, enabled = true, context?: () => SnippetContext): JSX.Element | null {
  const s = useStyles()
  const snippets = useSnippetList(enabled, context?.().lang)
  const [hit, setHit] = useState<Shortcut | null>(null)
  const [pick, setPick] = useState(0)
  const dismissed = useRef<{ node: Text; start: number } | null>(null)
  const state = useRef<{ hit: Shortcut | null; matches: MailSnippet[]; pick: number }>({ hit: null, matches: [], pick: 0 })
  const matches = hit && snippets ? matchSnippets(snippets, hit.query, 8) : []
  // Liste schließen, wenn nichts passt und schon ein Leerzeichen getippt ist (dann ist es wohl normaler Text)
  const open = !!hit && !(snippets && matches.length === 0 && /\s/.test(hit.query))
  state.current = { hit: open ? hit : null, matches, pick }

  const apply = (x: MailSnippet) => {
    const h = state.current.hit
    if (!h) return
    setHit(null)
    const area = editor.current?.area()
    if (!area || !area.contains(h.node)) return
    const r = document.createRange()
    r.setStart(h.node, Math.min(h.start, h.node.length)); r.setEnd(h.node, Math.min(h.end, h.node.length))
    area.focus()
    const sel = window.getSelection(); sel?.removeAllRanges(); sel?.addRange(r)
    put(area, htmlOf(x, context?.() ?? {}))
    onInserted?.()
  }
  const applyRef = useRef(apply); applyRef.current = apply

  useEffect(() => {
    const area = editor.current?.area()
    if (!area || !enabled) return
    const read = () => {
      const sel = window.getSelection()
      const node = sel?.anchorNode
      if (!sel || !sel.isCollapsed || !node || node.nodeType !== Node.TEXT_NODE || !area.contains(node)) { setHit(null); return }
      const text = node as Text
      const before = text.data.slice(0, sel.anchorOffset)
      const m = /(^|\s)::([\p{L}\p{N} ._-]{0,40})$/u.exec(before)
      if (!m) { setHit(null); dismissed.current = null; return }
      const start = sel.anchorOffset - m[2].length - 2
      if (dismissed.current?.node === text && dismissed.current.start === start) { setHit(null); return }
      const caret = sel.getRangeAt(0).cloneRange()
      let rect = caret.getBoundingClientRect()
      if (!rect.height) rect = (text.parentElement ?? area).getBoundingClientRect()
      setHit(h => (h && h.node === text && h.start === start ? h : (setPick(0), null), { node: text, start, end: sel.anchorOffset, query: m[2].toLowerCase(), x: rect.left, y: rect.bottom }))
    }
    const key = (e: KeyboardEvent) => {
      const { hit: h, matches: list, pick: p } = state.current
      if (!h) return
      if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); dismissed.current = { node: h.node, start: h.start }; setHit(null); return }
      if (!list.length) return
      if (e.key === 'ArrowDown') { e.preventDefault(); e.stopPropagation(); setPick((p + 1) % list.length) }
      else if (e.key === 'ArrowUp') { e.preventDefault(); e.stopPropagation(); setPick((p - 1 + list.length) % list.length) }
      else if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); e.stopPropagation(); applyRef.current(list[Math.min(p, list.length - 1)]) }
    }
    const blur = () => window.setTimeout(() => { if (document.activeElement !== area) setHit(null) }, 150)
    const up = (e: KeyboardEvent) => { if (!['ArrowDown', 'ArrowUp', 'Enter', 'Tab', 'Escape'].includes(e.key)) read() }
    area.addEventListener('input', read)
    area.addEventListener('keyup', up)
    area.addEventListener('click', read)
    area.addEventListener('keydown', key, true)
    area.addEventListener('blur', blur)
    return () => {
      area.removeEventListener('input', read)
      area.removeEventListener('keyup', up)
      area.removeEventListener('click', read)
      area.removeEventListener('keydown', key, true)
      area.removeEventListener('blur', blur)
    }
  }, [editor, enabled])

  if (!open || !hit) return null
  const left = Math.max(8, Math.min(hit.x, window.innerWidth - 430))
  const below = hit.y + 4 + 260 < window.innerHeight
  const style: React.CSSProperties = below ? { left, top: hit.y + 4 } : { left, bottom: window.innerHeight - hit.y + 24 }
  return (
    <Portal>
      <div className={s.suggest} style={style} role="listbox" aria-label={t('Textbausteine')}>
        {snippets === null ? <Spinner size="tiny" />
          : matches.length === 0
            ? <Text size={200} className={s.muted} style={{ padding: '4px 10px' }}>{snippets.length === 0
              ? t('Noch keine Textbausteine. Markierten Text über „Textbaustein“ im Menüband speichern.')
              : t('Kein Textbaustein zu „{q}“.', { q: hit.query })}</Text>
            : matches.map((x, i) => (
              <div key={x.id} role="option" aria-selected={i === pick} className={mergeClasses(s.suggestItem, i === pick && s.suggestOn)}
                title={snippetText(x.html).slice(0, 200)} onMouseDown={e => { e.preventDefault(); apply(x) }}>
                <Label x={x} query={hit.query} />
              </div>))}
      </div>
    </Portal>
  )
}

// ---------------- Menü im Menüband ----------------

/** Knopf „Textbaustein“ im Reiter „Nachricht“: einfügen (mit Suche), Markiertes speichern, verwalten. */
export function SnippetMenu({ editor, onInserted, context }: { editor: React.RefObject<RichEditorHandle>; onInserted?: () => void; context?: () => SnippetContext }) {
  const s = useStyles()
  const snippets = useSnippetList(true, context?.().lang)
  const [open, setOpen] = useState(false)
  const [query, setQuery] = useState('')
  const [pick, setPick] = useState(0)
  const range = useRef<Range | null>(null)
  const [hasSel, setHasSel] = useState(false)
  const [saving, setSaving] = useState<string | null>(null)
  const [manage, setManage] = useState(false)
  const list = snippets ? matchSnippets(snippets, query) : []

  const onOpen = (v: boolean) => {
    if (v) {
      // Markierung sichern: das Suchfeld nimmt dem Text den Fokus
      range.current = selectionIn(editor.current?.area())
      setHasSel(!!range.current && !range.current.collapsed)
      setQuery(''); setPick(0)
    }
    setOpen(v)
  }
  const insert = (x: MailSnippet) => { setOpen(false); insertAt(editor.current, htmlOf(x, context?.() ?? {}), range.current); onInserted?.() }

  return <>
    <Popover open={open} onOpenChange={(_, d) => onOpen(d.open)} positioning="below-start" trapFocus>
      <PopoverTrigger disableButtonEnhancement>
        <RibbonButton icon={<DocumentText20Regular />} label={t('Textbaustein')} onMouseDown={(e: React.MouseEvent) => e.preventDefault()} />
      </PopoverTrigger>
      <PopoverSurface>
        <div className={s.menu}>
          <Input autoFocus contentBefore={<Search20Regular />} placeholder={t('Textbaustein suchen')} value={query}
            onChange={(_, d) => { setQuery(d.value); setPick(0) }}
            onKeyDown={e => {
              if (!list.length) return
              if (e.key === 'ArrowDown') { e.preventDefault(); setPick(p => (p + 1) % list.length) }
              else if (e.key === 'ArrowUp') { e.preventDefault(); setPick(p => (p - 1 + list.length) % list.length) }
              else if (e.key === 'Enter') { e.preventDefault(); insert(list[Math.min(pick, list.length - 1)]) }
            }} />
          <div className={s.list} role="listbox" aria-label={t('Textbausteine')}>
            {snippets === null ? <Spinner size="tiny" />
              : list.length === 0 ? <Text size={200} className={s.muted}>{snippets.length === 0 ? t('Noch keine Textbausteine.') : t('Kein passender Textbaustein.')}</Text>
              : list.map((x, i) => (
                <div key={x.id} role="option" aria-selected={i === pick} className={mergeClasses(s.suggestItem, i === pick && s.suggestOn)}
                  title={snippetText(x.html).slice(0, 200)} onMouseEnter={() => setPick(i)} onClick={() => insert(x)}>
                  <Label x={x} query={query} />
                </div>))}
          </div>
          <Text size={200} className={s.muted}>{t('Im Text: „::“ und Kürzel oder Suchwort tippen.')}</Text>
          <Divider />
          <MenuItem icon={<Save20Regular />} disabled={!hasSel} onClick={() => { const r = range.current; setOpen(false); if (r) setSaving(rangeHtml(r)) }}>
            {t('Markierten Text als Textbaustein speichern')}
          </MenuItem>
          <MenuItem icon={<Settings20Regular />} onClick={() => { setOpen(false); setManage(true) }}>{t('Textbausteine verwalten …')}</MenuItem>
        </div>
      </PopoverSurface>
    </Popover>
    {saving != null && <SnippetDialog html={saving} onClose={() => setSaving(null)} onSaved={() => setSaving(null)} />}
    {manage && <SnippetManagerDialog onClose={() => setManage(false)} />}
  </>
}

// ---------------- Anlegen und Bearbeiten ----------------

/** Textbaustein anlegen (html: Vorgabe, etwa aus der Markierung) oder bearbeiten (snippet). */
export function SnippetDialog({ snippet, html, onClose, onSaved }: { snippet?: MailSnippet; html?: string; onClose: () => void; onSaved: (x: MailSnippet) => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const start = snippet?.html ?? html ?? ''
  const [title, setTitle] = useState(snippet?.title ?? (html ? titleOf(html) : ''))
  const [code, setCode] = useState(snippet?.code ?? '')
  const [busy, setBusy] = useState(false)
  const [empty, setEmpty] = useState(!snippetText(start) && !/<img/i.test(start))
  const ed = useRef<RichEditorHandle>(null)
  const codeBad = /\s/.test(code.trim())
  const save = async () => {
    setBusy(true)
    try {
      const x = await saveSnippet(snippet?.id ?? null, { title: title.trim(), code: code.trim(), html: ed.current?.html() ?? '' })
      notify(t('Textbaustein gespeichert'))
      onSaved(x)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>{snippet ? t('Textbaustein bearbeiten') : t('Neuer Textbaustein')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Titel')} required><Input value={title} maxLength={200} onChange={(_, d) => setTitle(d.value)} /></Field>
            <Field label={t('Kürzel')} hint={t('Optional. Im Text mit „::“ und Kürzel einfügen, etwa ::gruss.')}
              validationState={codeBad ? 'error' : 'none'} validationMessage={codeBad ? t('Das Kürzel darf keine Leerzeichen enthalten.') : undefined}>
              <Input value={code} maxLength={40} onChange={(_, d) => setCode(d.value)} />
            </Field>
            <Field label={t('Text')}>
              <div className={s.editor}>
                <RichEditor ref={ed} initial={start} minimal onChange={() => { const h = ed.current?.html() ?? ''; setEmpty(!snippetText(h) && !/<img/i.test(h)) }} />
              </div>
            </Field>
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || !title.trim() || codeBad || empty} onClick={save}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

// ---------------- Verwalten ----------------

/** Liste der eigenen Textbausteine mit Vorschau; Neu, Bearbeiten, Löschen im Menüband (falls vorhanden), sonst darüber. */
export function SnippetManager() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const snippets = useSnippets()
  const inRibbon = useContext(RibbonSlot) != null
  const [sel, setSel] = useState<string | null>(null)
  const [q, setQ] = useState('')
  const [edit, setEdit] = useState<MailSnippet | 'new' | null>(null)
  const picked = snippets?.find(x => x.id === sel) ?? null
  const rows = snippets ? (q.trim() ? matchSnippets(snippets, q) : snippets) : []
  const remove = async (x: MailSnippet) => {
    if (!await askConfirm(t('Textbaustein löschen'), t('„{title}“ wird gelöscht.', { title: x.title }), t('Löschen'))) return
    try { await deleteSnippet(x.id); setSel(null); notify(t('Textbaustein gelöscht')) } catch (e) { fail(e) }
  }
  const cols: AdminCol<MailSnippet>[] = [
    pickCol<MailSnippet, string>({ id: x => x.id, selected: sel, onChange: setSel }),
    { key: 'title', label: () => t('Titel'), width: 260, sort: x => x.title.toLowerCase(), render: x => x.title },
    { key: 'code', label: () => t('Kürzel'), width: 110, sort: x => x.code ?? '', render: x => x.code ?? '' },
    { key: 'text', label: () => t('Text'), width: 320, render: x => <span className={s.muted}>{snippetText(x.html).slice(0, 120)}</span> },
  ]
  const actions = <>
    <RibbonButton primary icon={<Add20Regular />} label={t('Neuer Textbaustein')} onClick={() => setEdit('new')} />
    <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={!picked} onClick={() => picked && setEdit(picked)} />
    <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!picked} onClick={() => picked && remove(picked)} />
  </>
  return (
    <div className={s.manager}>
      {inRibbon ? <RibbonActions>{actions}</RibbonActions> : (
        <div className={s.buttons}>
          <Button appearance="primary" icon={<Add20Regular />} onClick={() => setEdit('new')}>{t('Neuer Textbaustein')}</Button>
          <Button icon={<Edit20Regular />} disabled={!picked} onClick={() => picked && setEdit(picked)}>{t('Bearbeiten')}</Button>
          <Button icon={<Delete20Regular />} disabled={!picked} onClick={() => picked && remove(picked)}>{t('Löschen')}</Button>
        </div>
      )}
      <Input contentBefore={<Search20Regular />} placeholder={t('Textbausteine durchsuchen')} value={q} onChange={(_, d) => setQ(d.value)} style={{ maxWidth: 320 }} />
      {snippets === null ? <Spinner size="tiny" /> : snippets.length === 0
        ? <Text className={s.muted}>{t('Noch keine Textbausteine. Beim Schreiben einen Text markieren und über „Textbaustein“ im Menüband speichern, oder hier „Neuer Textbaustein“.')}</Text>
        : <AdminTable id="mail-snippets" cols={cols} rows={rows} rowKey={x => x.id} initialSort={{ col: 'title', desc: false }}
          onRowOpen={x => { setSel(x.id); setEdit(x) }} // [feature:rowdblopen]
          onRowClick={x => setSel(x.id)} active={sel} />}
      {picked && <div className={s.preview} dangerouslySetInnerHTML={{ __html: picked.html }} />}
      {edit && <SnippetDialog snippet={edit === 'new' ? undefined : edit} onClose={() => setEdit(null)} onSaved={x => { setEdit(null); setSel(x.id) }} />}
    </div>
  )
}

/** Verwalten als Dialog (aus dem Menü „Textbaustein“). */
export function SnippetManagerDialog({ onClose }: { onClose: () => void }) {
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 860 }}>
        <DialogBody>
          <DialogTitle>{t('Textbausteine')}</DialogTitle>
          <DialogContent><RibbonSlot.Provider value={null}><SnippetManager /></RibbonSlot.Provider></DialogContent>
          <DialogActions><Button appearance="primary" onClick={onClose}>{t('Schließen')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Einstellungen → Mail → Textbausteine */
export function SnippetSettings() {
  return <>
    <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>{t('Eigene Textbausteine zum Einfügen beim Schreiben: im Menüband über „Textbaustein“ oder im Text mit „::“ und Kürzel oder Suchwort.')}</Text>
    <SnippetManager />
  </>
}

