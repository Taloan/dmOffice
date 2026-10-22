import {
  Avatar, Button, Dropdown, Field, Input, Option, SearchBox, Spinner, Switch, Text, Textarea, Toolbar, ToolbarButton, makeStyles, mergeClasses, tokens,
  Menu, MenuDivider, MenuItem, MenuList, MenuPopover, MenuTrigger, Link,
} from '@fluentui/react-components'
import {
  PersonAdd20Regular, Edit20Regular, Delete20Regular, Mail20Regular, Call20Regular, Location20Regular, Add16Regular, Dismiss16Regular,
  Building20Regular, Gift20Regular, Globe20Regular, Book20Regular, Star20Regular, Star20Filled, Star12Filled,
  BookAdd20Regular, ArrowImport20Regular, ArrowExport20Regular, ArrowSort20Regular, PanelLeft20Regular,
  MoreHorizontal16Regular, Open20Regular, Rename20Regular, People20Regular, CheckboxChecked20Regular,
} from '@fluentui/react-icons'
import { ContextMenu, TargetMenu, askText, isTyping, useContextMenu, useMultiSelect } from '../shell/context'
import { useNavStyles } from '../shell/nav'
import { MenuItemRadio } from '@fluentui/react-components'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { BackBar, SidePane, useBackClose, useIsMobile, useMobileStyles } from '../shell/mobile'
import { usePrefs } from '../prefs'
import { useEffect, useMemo, useRef, useState } from 'react'
import { api, Collection, Contact } from '../api'
import { useApp } from '../App'
import { CollectionDialog, importInto, removeCollection } from '../calendar/CollectionDialog'
import { locale, t } from '../i18n'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { display: 'flex', flex: 1, minWidth: 0, minHeight: 0, position: 'relative' },
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  panes: { flex: 1, display: 'flex', minHeight: 0, minWidth: 0 },
  books: { width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}` },
  list: { width: '320px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', minHeight: 0,
    '@media (max-width: 767px)': { width: 'auto', flex: 1, minWidth: 0, borderRight: 'none' } },
  items: { flex: 1, overflowY: 'auto', outline: 'none' },
  letter: { padding: '8px 14px 2px', fontWeight: 600, color: tokens.colorBrandForeground1, fontSize: '13px' },
  item: { display: 'flex', alignItems: 'center', gap: '10px', padding: '4px 14px', cursor: 'pointer', userSelect: 'none', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
    '@media (max-width: 767px)': { padding: '6px 14px' } },
  sel: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  sub: { fontSize: '12px', color: tokens.colorNeutralForeground3, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  detail: { flex: 1, minHeight: 0, overflowY: 'auto', padding: '24px 32px', '@media (max-width: 767px)': { padding: '16px' } },
  head: { display: 'flex', alignItems: 'center', gap: '20px', marginBottom: '24px', '@media (max-width: 767px)': { gap: '12px', marginBottom: '12px' } },
  row: { display: 'flex', gap: '12px', alignItems: 'flex-start', padding: '8px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` },
  label: { width: '90px', color: tokens.colorNeutralForeground3, fontSize: '13px', flexShrink: 0 },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(2, minmax(0, 1fr))', gap: '10px 16px', maxWidth: '760px', '@media (max-width: 767px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  full: { gridColumn: '1 / -1' },
  multi: { display: 'flex', gap: '6px', alignItems: 'center', marginBottom: '6px', flexWrap: 'wrap' },
  section: { gridColumn: '1 / -1', marginTop: '10px', fontWeight: 600 },
  // Adresse: Typ, Strasse, PLZ, Ort, Entfernen - auf dem Handy zweispaltig
  address: { display: 'grid', gridTemplateColumns: '110px 1fr 100px 1fr auto', gap: '6px', '@media (max-width: 767px)': { gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)' } },
})

// Erster Wert geht in die vCard (TYPE=...), nur der zweite ist Anzeige
const emailTypes = [['work', t('Arbeit')], ['home', t('Privat')], ['other', t('Andere')]]
const phoneTypes = [['cell', t('Mobil')], ['work', t('Arbeit')], ['home', t('Privat')], ['fax', t('Fax')], ['other', t('Andere')]]
const typeLabel = (list: string[][], type: string) => list.find(x => x[0] === type)?.[1] ?? type

const empty = (book: number): Contact => ({
  id: '', book, uri: '', etag: '', displayName: '', prefix: '', given: '', additional: '', family: '', suffix: '', nickname: '',
  org: '', department: '', title: '', emails: [{ type: 'work', value: '' }], phones: [{ type: 'cell', value: '' }], addresses: [],
  urls: [], birthday: null, note: '', categories: [], photo: null,
})

export default function ContactsView() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [books, setBooks] = useState<Collection[]>([])
  const [book, setBook] = useState<number | 'all'>('all')
  const [contacts, setContacts] = useState<Contact[] | null>(null)
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<Contact | null>(null)
  const [detail, setDetail] = useState<Contact | null>(null)
  const [edit, setEdit] = useState<Contact | null>(null)
  const [bookDialog, setBookDialog] = useState<Collection | 'new' | null>(null)
  const importInput = useRef<HTMLInputElement>(null)
  const { prefs, update } = usePrefs()
  const sort = prefs.contactSort ?? 'name'
  const [sidePane, toggleSide, closeSide] = useSidePane('people')
  // Handy: Liste in voller Breite, Kontakt bzw. Formular im Vollbild mit Zurueck
  const mobile = useIsMobile()
  const ms = useMobileStyles()
  const open = !!(edit || selected)
  const closeDetail = () => { setEdit(null); setSelected(null) }
  useBackClose(open, closeDetail)
  const sortKey = (c: Contact) => (sort === 'family' ? c.family || c.displayName : sort === 'org' ? c.org || '~' + c.displayName : c.displayName).trim()
  const importTo = (id: number | undefined) => {
    if (!id) return
    importInput.current!.dataset.book = String(id)
    importInput.current?.click()
  }

  const loadBooks = () => api.get<Collection[]>('/api/pim/collections/addressbooks').then(setBooks).catch(fail)
  const load = () => api.get<Contact[]>('/api/pim/contacts' + (book === 'all' ? '' : `?books=${book}`)).then(setContacts).catch(fail)
  useEffect(() => { loadBooks() }, []) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(() => { load() }, [book]) // eslint-disable-line react-hooks/exhaustive-deps
  const loadDetail = (c: Contact) =>
    api.get<Contact>(`/api/pim/contacts/${c.book}/${encodeURIComponent(c.uri)}`).then(setDetail).catch(fail)
  useEffect(() => {
    if (!selected) { setDetail(null); return }
    loadDetail(selected)
  }, [selected?.id]) // eslint-disable-line react-hooks/exhaustive-deps

  const filtered = useMemo(() => {
    const term = search.trim().toLowerCase()
    if (!contacts) return []
    const list = !term ? [...contacts]
      : contacts.filter(c => [c.displayName, c.org, ...c.emails.map(e => e.value), ...c.phones.map(p => p.value)].some(x => x.toLowerCase().includes(term)))
    return list.sort((a, b) => sortKey(a).localeCompare(sortKey(b), locale, { sensitivity: 'base' }))
  }, [contacts, search, sort]) // eslint-disable-line react-hooks/exhaustive-deps

  const save = async (c: Contact) => {
    try {
      const r = await api.post('/api/pim/contacts', {
        ...c, emails: c.emails.filter(e => e.value.trim()), phones: c.phones.filter(p => p.value.trim()),
        moveTo: c.moveTo,
      })
      setEdit(null)
      await load()
      const [b, ...u] = (r.id as string).split('/')
      const saved = { ...c, book: Number(b), uri: u.join('/'), id: r.id }
      setSelected(saved)
      // Detail neu laden (neues etag) - bei gleicher id loest der Effekt nicht aus
      await loadDetail(saved)
      notify(t('Gespeichert'))
    } catch (e) { fail(e) }
  }

  const remove = async (c: Contact) => {
    if (!confirm(t('„{name}“ löschen?', { name: c.displayName }))) return
    try { await api.del(`/api/pim/contacts/${c.book}/${encodeURIComponent(c.uri)}`); setSelected(null); load() } catch (e) { fail(e) }
  }

  const writeMail = (addr: string | string[]) => {
    sessionStorage.setItem('dmoffice.compose.to', ([] as string[]).concat(addr).join('\n'))
    location.hash = '#/mail'
  }

  // ---------------- Auswahl, Kontextmenue, Tastatur, Ziehen ----------------
  const nv = useNavStyles()
  const mx = useMultiSelect<string>(useMemo(() => filtered.map(c => c.id), [filtered]))
  const picked = filtered.filter(c => mx.selected.includes(c.id))
  type Ctx = { kind: 'contact'; ids: string[] } | { kind: 'book'; b: Collection } | { kind: 'side' } | { kind: 'list' }
  const cm = useContextMenu<Ctx>()
  const listRef = useRef<HTMLDivElement>(null)
  useEffect(() => { mx.clear() }, [book]) // eslint-disable-line react-hooks/exhaustive-deps
  // Pfeiltasten: der Kontakt rechts folgt der Auswahl
  useEffect(() => {
    if (mx.selected.length !== 1 || mx.selected[0] === selected?.id) return
    const c = filtered.find(x => x.id === mx.selected[0])
    if (c) { setSelected(c); setEdit(null) }
  }, [mx.selected]) // eslint-disable-line react-hooks/exhaustive-deps
  const clickRow = (e: React.MouseEvent, c: Contact) => {
    mx.click(e, c.id)
    if (!(e.ctrlKey || e.metaKey || e.shiftKey)) { setSelected(c); setEdit(null) }
    else setSelected(null)
    listRef.current?.focus({ preventScroll: true })
  }
  const rowMenu = (e: React.MouseEvent, c: Contact) => {
    const ids = mx.selected.includes(c.id) ? mx.selected : [c.id]
    mx.pick(c.id)
    cm.open(e, { kind: 'contact', ids })
  }
  const DRAG = 'application/x-dmoffice-contacts'
  const [dropOn, setDropOn] = useState<number | null>(null)
  const full = (c: Contact) => api.get<Contact>(`/api/pim/contacts/${c.book}/${encodeURIComponent(c.uri)}`)
  /** In ein anderes Adressbuch verschieben bzw. kopieren - Foto und alles andere bleiben. */
  const transfer = async (list: Contact[], target: number, copy: boolean) => {
    let n = 0
    try {
      for (const c of list) {
        if (c.book === target && !copy) continue
        const x = await full(c)
        if (copy) await api.post('/api/pim/contacts', { ...x, id: '', uri: '', etag: '', uid: undefined, book: target, moveTo: undefined })
        else await api.post('/api/pim/contacts', { ...x, photo: undefined, moveTo: target })
        n++
      }
    } catch (err) { fail(err) }
    if (n) notify(copy ? t('{n} kopiert', { n }) : t('{n} verschoben', { n }))
    setSelected(null); mx.clear(); load(); loadBooks()
  }
  const removeMany = async (list: Contact[]) => {
    if (!list.length) return
    if (list.length === 1) { remove(list[0]); return }
    if (!confirm(t('{n} Kontakte löschen?', { n: list.length }))) return
    try { for (const c of list) await api.del(`/api/pim/contacts/${c.book}/${encodeURIComponent(c.uri)}`) } catch (err) { fail(err) }
    setSelected(null); mx.clear(); load(); loadBooks()
  }
  const setVip = async (list: Contact[], vip: boolean) => {
    try { for (const c of list) await api.post('/api/pim/contacts', { ...(await full(c)), photo: undefined, vip, moveTo: c.book }) } catch (err) { fail(err) }
    load(); if (detail && list.some(c => c.id === detail.id)) loadDetail(detail)
  }
  const bookDrop = (id: number) => ({
    onDragOver: (e: React.DragEvent) => {
      if (!e.dataTransfer.types.includes(DRAG)) return
      e.preventDefault(); e.dataTransfer.dropEffect = e.ctrlKey ? 'copy' : 'move'; setDropOn(id)
    },
    onDragLeave: () => setDropOn(d => (d === id ? null : d)),
    onDrop: (e: React.DragEvent) => {
      setDropOn(null)
      const ids: string[] = JSON.parse(e.dataTransfer.getData(DRAG) || '[]')
      const list = (contacts ?? []).filter(c => ids.includes(c.id))
      if (!list.length) return
      e.preventDefault()
      transfer(list, id, e.ctrlKey)
    },
  })
  const renameBook = async (b: Collection) => {
    const name = await askText(t('Umbenennen'), b.name)
    if (!name || name === b.name) return
    try { await api.put(`/api/pim/collections/${b.id}`, { name }); loadBooks() } catch (err) { fail(err) }
  }
  const deleteBook = async (b: Collection) => {
    try { if (await removeCollection(b, 'addressbooks')) { if (book === b.id) setBook('all'); loadBooks(); load() } } catch (err) { fail(err) }
  }
  const newContact = (b?: number) => { setSelected(null); setEdit(empty(b ?? (book === 'all' ? books[0]?.id : book))) }
  const onListKey = (e: React.KeyboardEvent) => {
    if (isTyping(e)) return
    const k = e.key
    if (k === 'Delete') { e.preventDefault(); removeMany(picked) }
    else if (k === 'Escape') { mx.clear(); closeDetail() }
    else if (e.ctrlKey && k.toLowerCase() === 'a') { e.preventDefault(); mx.all() }
    else if (e.ctrlKey && k.toLowerCase() === 'n') { e.preventDefault(); newContact() }
    else if (k === 'Enter' && picked.length === 1) { e.preventDefault(); setSelected(picked[0]); setEdit(null) }
    else if (k === 'F2' && picked.length === 1) { e.preventDefault(); full(picked[0]).then(setEdit).catch(fail) }
    else if (k === 'ArrowDown' || k === 'ArrowUp') { e.preventDefault(); mx.step(k === 'ArrowDown' ? 1 : -1, e.shiftKey) }
  }

  const ctxMenu = () => {
    const x = cm.ctx?.data
    if (!x) return null
    if (x.kind === 'side') return <MenuItem icon={<BookAdd20Regular />} onClick={() => setBookDialog('new')}>{t('Neues Adressbuch')}</MenuItem>
    if (x.kind === 'list') return <>
      <MenuItem icon={<PersonAdd20Regular />} onClick={() => newContact()}>{t('Neuer Kontakt')}</MenuItem>
      {filtered.length > 0 && <MenuItem icon={<CheckboxChecked20Regular />} onClick={mx.all}>{t('Alle auswählen')}</MenuItem>}
    </>
    if (x.kind === 'book') {
      const b = x.b
      return <>
        <MenuItem icon={<Open20Regular />} onClick={() => setBook(b.id)}>{t('Öffnen')}</MenuItem>
        <MenuItem icon={<PersonAdd20Regular />} onClick={() => newContact(b.id)}>{t('Neuer Kontakt')}</MenuItem>
        <MenuDivider />
        <MenuItem icon={<Edit20Regular />} onClick={() => setBookDialog(b)}>{t('Bearbeiten …')}</MenuItem>
        <MenuItem icon={<Rename20Regular />} onClick={() => renameBook(b)}>{t('Umbenennen')}</MenuItem>
        <MenuDivider />
        <MenuItem icon={<ArrowImport20Regular />} onClick={() => importTo(b.id)}>{t('Importieren (.vcf) …')}</MenuItem>
        <MenuItem icon={<ArrowExport20Regular />} onClick={() => window.open(`/api/pim/export/${b.id}`)}>{t('Exportieren (.vcf)')}</MenuItem>
        <MenuDivider />
        <MenuItem icon={<Delete20Regular />} onClick={() => deleteBook(b)}>{t('Löschen')}</MenuItem>
      </>
    }
    const sel = (contacts ?? []).filter(c => x.ids.includes(c.id))
    if (!sel.length) return null
    const one = sel.length === 1 ? sel[0] : null
    const mails = sel.map(c => c.emails[0]?.value).filter(Boolean) as string[]
    const allVip = sel.every(c => c.vip)
    const inBooks = new Set(sel.map(c => c.book))
    return <>
      {!one && <MenuItem disabled>{t('{n} Kontakte ausgewählt', { n: sel.length })}</MenuItem>}
      {one && <MenuItem icon={<Open20Regular />} onClick={() => { setSelected(one); setEdit(null) }}>{t('Öffnen')}</MenuItem>}
      {one && <MenuItem icon={<Edit20Regular />} onClick={() => full(one).then(setEdit).catch(fail)}>{t('Bearbeiten')}</MenuItem>}
      <MenuItem icon={mails.length > 1 ? <People20Regular /> : <Mail20Regular />} disabled={!mails.length} onClick={() => writeMail(mails)}>{t('E-Mail senden')}</MenuItem>
      <MenuItem icon={allVip ? <Star20Filled /> : <Star20Regular />} onClick={() => setVip(sel, !allVip)}>{allVip ? t('Nicht mehr VIP') : t('Als VIP markieren')}</MenuItem>
      <MenuDivider />
      <TargetMenu kind="move" targets={books.filter(b => !(inBooks.size === 1 && inBooks.has(b.id))).map(b => ({ id: b.id, label: b.name }))} onPick={id => transfer(sel, Number(id), false)} />
      <TargetMenu kind="copy" targets={books.filter(b => !(inBooks.size === 1 && inBooks.has(b.id))).map(b => ({ id: b.id, label: b.name }))} onPick={id => transfer(sel, Number(id), true)} />
      {one && <MenuItem icon={<ArrowExport20Regular />} onClick={() => window.open(`/api/pim/contacts/${one.book}/${encodeURIComponent(one.uri)}/vcf`)}>{t('Exportieren (.vcf)')}</MenuItem>}
      <MenuDivider />
      <MenuItem icon={<Delete20Regular />} onClick={() => removeMany(sel)}>{t('Löschen')}</MenuItem>
    </>
  }

  let last = ''
  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      <RibbonHeader area="people" onMenu={toggleSide}
        start={<>
          <RibbonButton primary icon={<PersonAdd20Regular />} label={t('Neuer Kontakt')} onClick={() => newContact()} />
          <RibbonDivider />
          <RibbonButton icon={<Mail20Regular />} label={t('E-Mail')} disabled={!detail?.emails[0]} onClick={() => detail?.emails[0] && writeMail(detail.emails[0].value)} />
          <RibbonButton icon={<Edit20Regular />} label={t('Bearbeiten')} disabled={!detail} onClick={() => detail && setEdit(detail)} />
          <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!detail && !picked.length} onClick={() => picked.length > 1 ? removeMany(picked) : detail && remove(detail)} />
          <RibbonDivider />
          <RibbonButton icon={<BookAdd20Regular />} label={t('Neues Adressbuch')} onClick={() => setBookDialog('new')} />
          <RibbonButton icon={<ArrowImport20Regular />} label={t('Importieren')} disabled={!books.length} onClick={() => importTo(book === 'all' ? books[0]?.id : book)} />
          <RibbonButton icon={<ArrowExport20Regular />} label={t('Exportieren')} disabled={book === 'all'} onClick={() => book !== 'all' && window.open(`/api/pim/export/${book}`)} />
        </>}
        view={<>
          <Menu>
            <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ArrowSort20Regular />} label={t('Sortieren')} /></MenuTrigger>
            <MenuPopover>
              <MenuList checkedValues={{ sort: [sort] }} onCheckedValueChange={(_, d) => update({ contactSort: d.checkedItems[0] as any })}>
                <MenuItemRadio name="sort" value="name">{t('Anzeigename')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="family">{t('Nachname')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="org">{t('Firma')}</MenuItemRadio>
              </MenuList>
            </MenuPopover>
          </Menu>
          <RibbonDivider />
          <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />
        </>} />
      <div className={s.root}>
      <SidePane className={mergeClasses(s.books, nv.pane)} open={sidePane} onClose={closeSide}>
        <div style={{ display: 'flex', flexDirection: 'column', flex: 1 }} onContextMenu={e => cm.open(e, { kind: 'side' })}>
        <div className={mergeClasses(nv.row, book === 'all' && nv.active)} onClick={() => setBook('all')}><People20Regular /><span className={nv.name}>{t('Alle Kontakte')}</span></div>
        <div className={nv.section}><span style={{ flex: 1 }}>{t('Adressbücher')}</span>
          <Button appearance="subtle" size="small" icon={<BookAdd20Regular />} aria-label={t('Neues Adressbuch')} title={t('Neues Adressbuch')} onClick={() => setBookDialog('new')} />
        </div>
        {books.map(b => (
          <div key={b.id} className={mergeClasses(nv.row, book === b.id && nv.active, dropOn === b.id && nv.drop)} onClick={() => setBook(b.id)} tabIndex={0}
            onContextMenu={e => cm.open(e, { kind: 'book', b })} {...bookDrop(b.id)}
            onKeyDown={e => {
              if (e.key === 'Enter') setBook(b.id)
              else if (e.key === 'F2') { e.preventDefault(); renameBook(b) }
              else if (e.key === 'Delete') { e.preventDefault(); deleteBook(b) }
            }}>
            <Book20Regular /><span className={nv.name}>{b.name}</span><span className={nv.count}>{b.count}</span>
            <Button className="dm-more" appearance="transparent" size="small" icon={<MoreHorizontal16Regular />} aria-label={t('Mehr')}
              onClick={e => { e.stopPropagation(); cm.open(e, { kind: 'book', b }) }} />
          </div>
        ))}
        <Link className={nv.link} onClick={() => setBookDialog('new')}>{t('+ Adressbuch')}</Link>
        <div style={{ flex: 1, minHeight: 24 }} />
        </div>
        <input ref={importInput} type="file" accept=".vcf,text/vcard" hidden onChange={async e => {
          const f = e.target.files?.[0]
          if (f) {
            try {
              const r = await importInto(Number(importInput.current!.dataset.book), f)
              notify(r.added === 1 ? t('1 Kontakt übernommen') : t('{n} Kontakte übernommen', { n: r.added }),
                r.failed ? (r.failed === 1 ? t('1 Eintrag ließ sich nicht lesen') : t('{n} ließen sich nicht lesen', { n: r.failed })) : undefined)
              load(); loadBooks()
            } catch (err) { fail(err) }
          }
          e.target.value = ''
        }} />
      </SidePane>
      <div className={s.main}>
      <div className={s.panes}>
      <div className={s.list}>
        <div style={{ padding: 10 }}><SearchBox placeholder={t('Kontakte durchsuchen')} value={search} onChange={(_, d) => setSearch(d.value)} style={{ width: '100%' }} /></div>
        <div className={s.items} ref={listRef} tabIndex={-1} onKeyDown={onListKey}
          onContextMenu={e => cm.open(e, { kind: 'list' })}>
          {!contacts ? <Spinner /> : filtered.length === 0 ? <Text style={{ padding: 16, display: 'block' }}>{t('Keine Kontakte.')}</Text> : filtered.map(c => {
            const letter = (sortKey(c).replace(/^~/, '')[0] || '#').toUpperCase()
            const head = letter !== last ? <div key={'l' + letter} className={s.letter}>{letter}</div> : null
            last = letter
            return [head,
              <div key={c.id} className={mergeClasses(s.item, (selected?.id === c.id || mx.selected.includes(c.id)) && s.sel)} onClick={e => clickRow(e, c)}
                onContextMenu={e => rowMenu(e, c)} draggable={!mobile}
                onDragStart={e => { e.dataTransfer.setData(DRAG, JSON.stringify(mx.selected.includes(c.id) ? mx.selected : [c.id])); e.dataTransfer.effectAllowed = 'copyMove' }}>
                <Avatar name={c.displayName} size={32} color="colorful" />
                <div style={{ minWidth: 0 }}>
                  <div>{c.displayName}{c.vip && <Star12Filled style={{ marginLeft: 4, color: tokens.colorPaletteYellowForeground1 }} />}</div>
                  <div className={s.sub}>{c.emails[0]?.value || c.org || c.phones[0]?.value}</div>
                </div>
              </div>]
          })}
        </div>
      </div>
      <div className={mergeClasses(mobile && open && ms.full)} style={mobile && !open ? { display: 'none' } : { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0, minHeight: 0 }}>
      {mobile && <BackBar onBack={closeDetail} title={edit ? (edit.id ? t('Bearbeiten') : t('Neuer Kontakt')) : detail?.displayName} />}
      <div className={s.detail}>
        {edit ? <ContactForm initial={edit} books={books} onSave={save} onCancel={() => setEdit(null)} />
          : detail ? (
            <>
              <div className={s.head}>
                <Avatar name={detail.displayName} image={detail.photo ? { src: detail.photo } : undefined} size={96} color="colorful" />
                <div>
                  <Text size={700} weight="semibold" block>{detail.displayName}</Text>
                  {(detail.title || detail.org) && <Text block>{[detail.title, detail.org, detail.department].filter(Boolean).join(' · ')}</Text>}
                </div>
              </div>
              <Toolbar style={{ marginBottom: 12, padding: 0 }}>
                {detail.emails[0] && <ToolbarButton icon={<Mail20Regular />} onClick={() => writeMail(detail.emails[0].value)}>{t('E-Mail')}</ToolbarButton>}
                <ToolbarButton icon={<Edit20Regular />} onClick={() => setEdit(detail)}>{t('Bearbeiten')}</ToolbarButton>
                {/* VIP: in der App gibt es Benachrichtigungen "nur von VIPs", der Prio-Posteingang zeigt sie immer */}
                <ToolbarButton icon={detail.vip ? <Star20Filled color={tokens.colorPaletteYellowForeground1} /> : <Star20Regular />}
                  onClick={() => save({ ...detail, vip: !detail.vip, moveTo: detail.book })}>{t('VIP')}</ToolbarButton>
                <ToolbarButton icon={<Delete20Regular />} onClick={() => remove(detail)}>{t('Löschen')}</ToolbarButton>
              </Toolbar>
              {detail.emails.map((e, i) => (
                <div key={'e' + i} className={s.row}><Mail20Regular /><span className={s.label}>{typeLabel(emailTypes, e.type)}</span><Link onClick={() => writeMail(e.value)}>{e.value}</Link></div>
              ))}
              {detail.phones.map((p, i) => (
                <div key={'p' + i} className={s.row}><Call20Regular /><span className={s.label}>{typeLabel(phoneTypes, p.type)}</span><Link href={`tel:${p.value}`}>{p.value}</Link></div>
              ))}
              {detail.addresses.map((a, i) => (
                <div key={'a' + i} className={s.row}><Location20Regular /><span className={s.label}>{typeLabel(emailTypes, a.type)}</span>
                  <span>{a.street}<br />{[a.zip, a.city].filter(Boolean).join(' ')}{a.region ? `, ${a.region}` : ''}<br />{a.country}</span></div>
              ))}
              {detail.org && <div className={s.row}><Building20Regular /><span className={s.label}>{t('Firma')}</span>{detail.org}</div>}
              {detail.birthday && <div className={s.row}><Gift20Regular /><span className={s.label}>{t('Geburtstag')}</span>{new Date(detail.birthday).toLocaleDateString(locale)}</div>}
              {/* Nur http(s) verlinken - eine vCard aus fremder Quelle koennte javascript: als Webadresse haben */}
              {detail.urls.map((u, i) => <div key={'u' + i} className={s.row}><Globe20Regular /><span className={s.label}>{t('Web')}</span>{/^https?:\/\//i.test(u.trim()) ? <Link href={u} target="_blank" rel="noopener noreferrer">{u}</Link> : <span>{u}</span>}</div>)}
              {detail.note && <div className={s.row}><span className={s.label}>{t('Notiz')}</span><span style={{ whiteSpace: 'pre-wrap' }}>{detail.note}</span></div>}
            </>
          ) : <Text style={{ color: tokens.colorNeutralForeground3 }}>{picked.length > 1 ? t('{n} Kontakte ausgewählt', { n: picked.length }) : t('Wähle einen Kontakt aus.')}</Text>}
      </div>
      </div>
      </div>
      </div>
      <ContextMenu at={cm.ctx?.at} onClose={cm.close}>{ctxMenu()}</ContextMenu>
      {bookDialog && <CollectionDialog kind="addressbooks" collection={bookDialog === 'new' ? null : bookDialog} onClose={c => { setBookDialog(null); if (c) loadBooks() }} />}
      </div>
    </div>
  )
}

function ContactForm({ initial, books, onSave, onCancel }: { initial: Contact; books: Collection[]; onSave: (c: Contact) => void; onCancel: () => void }) {
  const s = useStyles()
  const [c, setC] = useState<Contact>({ ...initial, moveTo: initial.book })
  const set = (p: Partial<Contact>) => setC(x => ({ ...x, ...p }))
  const photoInput = useRef<HTMLInputElement>(null)

  const listEditor = (key: 'emails' | 'phones', types: string[][], label: string, addLabel: string, type: string) => (
    <div className={s.full}>
      <Text weight="semibold" block style={{ marginBottom: 6 }}>{label}</Text>
      {c[key].map((v, i) => (
        <div key={i} className={s.multi}>
          <Dropdown style={{ minWidth: 110 }} aria-label={t('Typ')} value={typeLabel(types, v.type)} selectedOptions={[v.type]}
            onOptionSelect={(_, d) => set({ [key]: c[key].map((x, j) => j === i ? { ...x, type: d.optionValue! } : x) } as any)}>
            {types.map(([k, l]) => <Option key={k} value={k}>{l}</Option>)}
          </Dropdown>
          <Input style={{ flex: 1 }} type={key === 'emails' ? 'email' : 'tel'} value={v.value}
            onChange={(_, d) => set({ [key]: c[key].map((x, j) => j === i ? { ...x, value: d.value } : x) } as any)} />
          <Button appearance="subtle" icon={<Dismiss16Regular />} aria-label={t('Entfernen')} onClick={() => set({ [key]: c[key].filter((_, j) => j !== i) } as any)} />
        </div>
      ))}
      <Button size="small" appearance="subtle" icon={<Add16Regular />} onClick={() => set({ [key]: [...c[key], { type, value: '' }] } as any)}>{addLabel}</Button>
    </div>
  )

  return (
    <form onSubmit={e => { e.preventDefault(); onSave(c) }}>
      <div className={s.head}>
        <Avatar name={c.displayName || `${c.given} ${c.family}`} image={c.photo ? { src: c.photo } : undefined} size={96} color="colorful"
          onClick={() => photoInput.current?.click()} style={{ cursor: 'pointer' }} />
        <div>
          <Text size={600} weight="semibold" block>{initial.uri ? t('Kontakt bearbeiten') : t('Neuer Kontakt')}</Text>
          <Link onClick={() => photoInput.current?.click()}>{t('Foto wählen')}</Link>{c.photo && <> · <Link onClick={() => set({ photo: '' })}>{t('Foto entfernen')}</Link></>}
        </div>
        <input ref={photoInput} type="file" accept="image/*" hidden onChange={e => {
          const f = e.target.files?.[0]
          if (!f) return
          // Auf 256 px verkleinern - vCards mit grossen Fotos bremsen jedes Handy
          const img = new Image()
          img.onload = () => {
            const size = 256, cv = document.createElement('canvas')
            const r = Math.min(1, size / Math.max(img.width, img.height))
            cv.width = img.width * r; cv.height = img.height * r
            cv.getContext('2d')!.drawImage(img, 0, 0, cv.width, cv.height)
            set({ photo: cv.toDataURL('image/jpeg', 0.85) })
          }
          img.src = URL.createObjectURL(f)
        }} />
      </div>
      <div className={s.grid}>
        <Field label={t('Vorname')}><Input value={c.given} onChange={(_, d) => set({ given: d.value })} autoFocus /></Field>
        <Field label={t('Nachname')}><Input value={c.family} onChange={(_, d) => set({ family: d.value })} /></Field>
        <Field label={t('Anzeigename')} hint={t('leer: aus Vor- und Nachname')} className={s.full}><Input value={c.displayName} onChange={(_, d) => set({ displayName: d.value })} /></Field>
        <Field label={t('Firma')}><Input value={c.org} onChange={(_, d) => set({ org: d.value })} /></Field>
        <Field label={t('Position')}><Input value={c.title} onChange={(_, d) => set({ title: d.value })} /></Field>
        {listEditor('emails', emailTypes, t('E-Mail'), t('E-Mail hinzufügen'), 'work')}
        {listEditor('phones', phoneTypes, t('Telefon'), t('Telefon hinzufügen'), 'cell')}
        <Text className={s.section}>{t('Adressen')}</Text>
        {c.addresses.map((a, i) => {
          const upd = (p: any) => set({ addresses: c.addresses.map((x, j) => j === i ? { ...x, ...p } : x) })
          return (
            <div key={i} className={mergeClasses(s.full, s.address)}>
              <Dropdown style={{ minWidth: 100 }} aria-label={t('Typ')} value={typeLabel(emailTypes, a.type)} selectedOptions={[a.type]} onOptionSelect={(_, d) => upd({ type: d.optionValue })}>
                {emailTypes.map(([k, l]) => <Option key={k} value={k}>{l}</Option>)}
              </Dropdown>
              <Input placeholder={t('Straße')} value={a.street} onChange={(_, d) => upd({ street: d.value })} />
              <Input placeholder={t('PLZ')} value={a.zip} onChange={(_, d) => upd({ zip: d.value })} />
              <Input placeholder={t('Ort')} value={a.city} onChange={(_, d) => upd({ city: d.value })} />
              <Button appearance="subtle" icon={<Dismiss16Regular />} aria-label={t('Entfernen')} onClick={() => set({ addresses: c.addresses.filter((_, j) => j !== i) })} />
              <span />
              <Input placeholder={t('Land')} value={a.country} onChange={(_, d) => upd({ country: d.value })} />
            </div>
          )
        })}
        <div className={s.full}>
          <Button size="small" appearance="subtle" icon={<Add16Regular />}
            onClick={() => set({ addresses: [...c.addresses, { type: 'home', street: '', city: '', region: '', zip: '', country: '' }] })}>{t('Adresse hinzufügen')}</Button>
        </div>
        <Field label={t('Geburtstag')}><Input type="date" value={c.birthday ?? ''} onChange={(_, d) => set({ birthday: d.value || null })} /></Field>
        <Field label={t('Webseite')}><Input value={c.urls[0] ?? ''} onChange={(_, d) => set({ urls: d.value ? [d.value, ...c.urls.slice(1)] : c.urls.slice(1) })} /></Field>
        <Field label={t('Notiz')} className={s.full}><Textarea value={c.note} onChange={(_, d) => set({ note: d.value })} rows={4} /></Field>
        <Switch className={s.full} label={t('VIP – Benachrichtigungen und Prio-Posteingang')} checked={!!c.vip} onChange={(_, d) => set({ vip: d.checked })} />
        {books.length > 1 && (
          <Field label={t('Adressbuch')} className={s.full}>
            <Dropdown value={books.find(b => b.id === c.moveTo)?.name ?? ''} selectedOptions={[String(c.moveTo)]} onOptionSelect={(_, d) => set({ moveTo: Number(d.optionValue) })}>
              {books.map(b => <Option key={b.id} value={String(b.id)}>{b.name}</Option>)}
            </Dropdown>
          </Field>
        )}
        <div className={s.full} style={{ display: 'flex', gap: 8, marginTop: 8 }}>
          <Button appearance="primary" type="submit">{t('Speichern')}</Button>
          <Button onClick={onCancel}>{t('Abbrechen')}</Button>
        </div>
      </div>
    </form>
  )
}
