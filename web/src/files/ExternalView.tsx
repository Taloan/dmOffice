import {
  Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle,
  Field, Input, makeStyles, Radio, RadioGroup, Select, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger, mergeClasses, MessageBar, MessageBarBody, Spinner, Table, TableBody, TableCell,
  TableCellLayout, TableHeader, TableHeaderCell, TableRow, Text, tokens, Toolbar, ToolbarButton,
} from '@fluentui/react-components'
import {
  ArrowClockwise20Regular, ArrowDownload20Regular, Copy20Regular, Dismiss20Regular, Folder20Regular, FolderArrowRight20Regular, Search20Regular, ArrowUpload20Regular, Delete20Regular, FolderAdd20Regular, MoreHorizontal20Regular, Rename20Regular,
} from '@fluentui/react-icons'
import { type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { fmtWhen, KindIcon, kindOf } from './common'
import { usePrefs } from '../prefs'
import { TopFolderChoice } from './TopFolderChoice'
import { BrowseDialog } from './ImportProfiles'
import { ExternalHits } from './ExternalSearch'
import { useDebounced } from '../shell/hooks'
import { locale } from '../i18n'
import { askConfirm, ContextMenu, useContextMenu } from '../shell/context'
import { useFileClip } from './clip'
import { useTransfer } from './TransferDialog'
import { ClipboardPaste20Regular, Cut20Regular, FolderOpen20Regular } from '@fluentui/react-icons'
import { MicrosoftSignIn } from '../settings/AccountsSettings'

/** available: gerade erreichbar (der Server prüft es beim Laden der Liste) */
export interface ExternalMount { id: string; name: string; kind: 'folder' | 'webdav' | 'onedrive' | 'pcloud'; readOnly: boolean; search?: boolean; available?: boolean; account?: string | null; access?: string | null; path?: string | null }
interface Entry { name: string; isDir: boolean; size: number; modified: string | null }

const useStyles = makeStyles({
  root: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0, minHeight: 0 },
  head: { display: 'flex', alignItems: 'center', gap: '8px', padding: '8px 16px', flexWrap: 'wrap' },
  list: { flex: 1, overflow: 'auto', padding: '0 16px 16px' },
  drop: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-6px' },
  row: { cursor: 'default' },
  tiles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(var(--tile, 132px), 1fr))', gap: '8px', paddingTop: '8px' },
  tile: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', padding: '8px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    userSelect: 'none', position: 'relative', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  thumb: { width: '100%', aspectRatio: '4 / 3', display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden', borderRadius: tokens.borderRadiusSmall },
  tileName: { fontSize: '12px', textAlign: 'center', wordBreak: 'break-word', lineHeight: '16px', maxHeight: '32px', overflow: 'hidden', width: '100%' },
  tileMenu: { position: 'absolute', top: '2px', right: '2px' },
  tileCheck: { position: 'absolute', top: '2px', left: '2px', zIndex: 1 },
  tileSel: { backgroundColor: tokens.colorNeutralBackground1Selected, ':hover': { backgroundColor: tokens.colorNeutralBackground1Selected } },
  count: { color: tokens.colorNeutralForeground2, padding: '0 6px', whiteSpace: 'nowrap' },
  muted: { color: tokens.colorNeutralForeground3 },
  pickList: { display: 'flex', flexDirection: 'column', maxHeight: '260px', minHeight: '120px', overflow: 'auto', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
  pickRow: { display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 10px', cursor: 'pointer', border: 'none', background: 'none', textAlign: 'left', color: 'inherit', font: 'inherit',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
})

const join = (a: string, b: string) => (a ? a + '/' + b : b)

/**
 * Ein externer Ordner (Ordner auf dem Server oder WebDAV): blaettern, herunterladen, hochladen, Ordner anlegen,
 * umbenennen, loeschen. Die Dateien gehoeren dem fremden Speicher - keine Versionen, kein Papierkorb, keine Freigaben.
 */
export function ExternalView({ mount, path, onPath, vaultPanel, onVaultOpen }: {
  mount: ExternalMount; path: string; onPath: (p: string) => void
  /** Cryptomator-Tresor in diesem Ordner, gesperrt: Passwortfeld (raw: verschlüsselte Dateien zeigen) */
  vaultPanel?: (root: string, raw: () => void) => React.ReactNode
  /** … schon offen: dorthin */
  onVaultOpen?: (root: string) => void
}) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [items, setItems] = useState<Entry[] | null>(null)
  const [vault, setVault] = useState<{ root: string; unlocked: boolean } | null>(null)
  const [raw, setRaw] = useState(false)
  const [readOnly, setReadOnly] = useState(mount.readOnly)
  const [busy, setBusy] = useState(false)
  const [dragging, setDragging] = useState(false)
  const [ask, setAsk] = useState<{ kind: 'mkdir' } | { kind: 'rename'; item: Entry } | null>(null)
  const [name, setName] = useState('')
  const file = useRef<HTMLInputElement>(null)
  // Mehrfachauswahl wie in „Meine Dateien“: Namen im aktuellen Ordner; Strg-Klick schaltet um, Umschalt-Klick nimmt den Bereich
  const [selected, setSelected] = useState<string[]>([])
  const anchor = useRef<string | null>(null)
  const [picker, setPicker] = useState<'move' | 'copy' | null>(null)
  // Zwischenablage gemeinsam mit „Meine Dateien“ und anderen externen Ordnern
  const [clip, setClip] = useFileClip()
  const cm = useContextMenu<Entry | null>()
  const xfer = useTransfer()
  useEffect(() => { setSelected([]); anchor.current = null }, [mount.id, path])
  // Suche (wenn der Admin den Ordner durchsuchbar gemacht hat): Namen und Inhalte in diesem Ordner und darunter
  const [find, setFind] = useState('')
  const term = useDebounced(find.trim(), 300)
  useEffect(() => setFind(''), [mount.id])

  // Wie die Dateiablage: Details oder Kacheln, Sortierung, Ordner oben, versteckte Dateien, Kachelgröße (Menüband „Ansicht“)
  const { prefs } = usePrefs()
  const tiles = prefs.fileView === 'tiles'
  const sort = prefs.fileSort ?? 'name'
  const desc = prefs.fileSortDesc ?? false
  const tilePx = prefs.fileTile === 'small' ? '96px' : prefs.fileTile === 'large' ? '220px' : '132px'
  // Liste kommt aus dem Zwischenspeicher des Servers; „Aktualisieren“ holt sie am Speicher frisch
  const load = useCallback((fresh = false) => {
    if (fresh) setItems(null)
    api.get<{ readOnly: boolean; items: Entry[]; vault?: { root: string; unlocked: boolean } | null }>('/api/files/external/list?' + q({ mount: mount.id, path, fresh: fresh || undefined }))
      .then(r => { setItems(r.items); setVault(r.vault ?? null); setReadOnly(r.readOnly); setSelected(sel => sel.filter(n => r.items.some(i => i.name === n))) })
      .catch(e => { fail(e); setItems([]) })
  }, [mount.id, path, fail])
  useEffect(() => { setItems(null); setVault(null); setRaw(false) }, [mount.id, path])
  useEffect(() => { if (vault?.unlocked && !raw) onVaultOpen?.(vault.root) }, [vault, raw]) // eslint-disable-line react-hooks/exhaustive-deps
  const shown = useMemo(() => {
    const dir = desc ? -1 : 1
    return (items ?? []).filter(i => prefs.fileHidden || !i.name.startsWith('.'))
      .sort((a, b) => (prefs.fileFolderTop !== false ? Number(b.isDir) - Number(a.isDir) : 0) || dir * (
        sort === 'modified' ? (a.modified ?? '').localeCompare(b.modified ?? '') : sort === 'size' ? a.size - b.size
          : a.name.localeCompare(b.name, locale, { sensitivity: 'base', numeric: true })))
  }, [items, prefs.fileHidden, prefs.fileFolderTop, sort, desc])
  const sel = shown.filter(i => selected.includes(i.name))
  const toggle = (i: Entry) => { anchor.current = i.name; setSelected(x => x.includes(i.name) ? x.filter(n => n !== i.name) : [...x, i.name]) }
  const click = (e: React.MouseEvent, i: Entry) => {
    const ctrl = e.ctrlKey || e.metaKey
    if (e.shiftKey && anchor.current) {
      const names = shown.map(x => x.name)
      const a = names.indexOf(anchor.current), b = names.indexOf(i.name)
      if (a >= 0 && b >= 0) {
        const range = names.slice(Math.min(a, b), Math.max(a, b) + 1)
        setSelected(x => ctrl ? Array.from(new Set([...x, ...range])) : range)
        return
      }
    }
    if (ctrl) toggle(i)
    else { anchor.current = i.name; setSelected([i.name]) }
  }
  const plain = (e: React.MouseEvent) => !e.ctrlKey && !e.metaKey && !e.shiftKey
  const thumbOf = (i: Entry) => '/api/files/external/thumb?' + q({ mount: mount.id, path: join(path, i.name), v: i.modified ?? '' })
  const menu = (i: Entry) => (
    <Menu>
      <MenuTrigger disableButtonEnhancement><Button appearance="subtle" size="small" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} onClick={e => e.stopPropagation()} /></MenuTrigger>
      <MenuPopover>
        <MenuList>
          {!i.isDir && <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download(i)}>{t('Herunterladen')}</MenuItem>}
          {!readOnly && <MenuItem icon={<Rename20Regular />} onClick={() => { setName(i.name); setAsk({ kind: 'rename', item: i }) }}>{t('Umbenennen')}</MenuItem>}
          {!readOnly && <MenuItem icon={<Delete20Regular />} onClick={() => remove([i])}>{t('Löschen')}</MenuItem>}
        </MenuList>
      </MenuPopover>
    </Menu>
  )
  useEffect(() => { load() }, [load])

  const upload = async (files: FileList | File[]) => {
    if (!files.length) return
    setBusy(true)
    try {
      const form = new FormData()
      for (const f of Array.from(files)) form.append('file', f, f.name)
      await api.form('/api/files/external/upload?' + q({ mount: mount.id, path }), form)
      notify(t('Hochgeladen'))
      load()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const download = (i: Entry) => {
    const a = document.createElement('a')
    a.href = '/api/files/external/download?' + q({ mount: mount.id, path: join(path, i.name) })
    a.download = i.name
    a.click()
  }
  const remove = async (list: Entry[]) => {
    if (!list.length) return
    const i = list[0]
    const text = list.length > 1 ? t('{n} Einträge endgültig löschen? Externe Ordner haben keinen Papierkorb.', { n: list.length })
      : i.isDir ? t('Ordner „{name}“ mit allem darin endgültig löschen? Externe Ordner haben keinen Papierkorb.', { name: i.name }) : t('„{name}“ endgültig löschen? Externe Ordner haben keinen Papierkorb.', { name: i.name })
    if (!await askConfirm('', text)) return
    try { await api.post('/api/files/external/delete', { mount: mount.id, paths: list.map(x => join(path, x.name)) }); setSelected([]); load() } catch (e) { fail(e) }
  }
  // Mehrere Dateien: nacheinander, sonst lässt der Browser nur die erste durch; Ordner nicht
  const downloadAll = (list: Entry[]) => list.filter(i => !i.isDir).forEach((i, n) => setTimeout(() => download(i), n * 400))
  // Verschieben bzw. Kopieren in einen Ordner desselben externen Ordners
  const transfer = async (target: string, mode: 'move' | 'copy') => {
    const list = sel
    setPicker(null)
    if (!list.length || target === path) return
    if (list.some(i => target === join(path, i.name) || target.startsWith(join(path, i.name) + '/'))) { fail(new Error(t('Ein Ordner lässt sich nicht in sich selbst verschieben oder kopieren.'))); return }
    setBusy(true)
    try {
      if (mode === 'copy') await api.post('/api/files/external/copy', { mount: mount.id, paths: list.map(i => join(path, i.name)), to: target })
      else for (const i of list) await api.post('/api/files/external/move', { mount: mount.id, from: join(path, i.name), to: join(target, i.name) })
      notify(mode === 'copy' ? t('Kopiert') : t('Verschoben'))
      setSelected([])
    } catch (e) { fail(e) } finally { setBusy(false); load() }
  }
  const toClip = (list: Entry[], cut: boolean) => {
    if (!list.length) return
    setClip({ mount: mount.id, paths: list.map(i => join(path, i.name)), cut })
    notify(cut ? t('{n} ausgeschnitten – mit Einfügen (Strg+V) ablegen', { n: list.length }) : t('{n} kopiert – mit Einfügen (Strg+V) ablegen', { n: list.length }), undefined, 'info')
  }
  // Einfügen: aus demselben externen Ordner wie bisher, sonst (Ablage, anderer externer Ordner) überträgt der Server im Hintergrund
  const paste = async (target: string) => {
    const c = clip
    if (!c || readOnly) return
    if (c.cut) setClip(null)
    if (c.mount === mount.id) {
      if (c.paths.some(p => target === p || target.startsWith(p + '/'))) { fail(new Error(t('Ein Ordner lässt sich nicht in sich selbst verschieben oder kopieren.'))); return }
      setBusy(true)
      try {
        if (!c.cut) await api.post('/api/files/external/copy', { mount: mount.id, paths: c.paths, to: target })
        else for (const p of c.paths) await api.post('/api/files/external/move', { mount: mount.id, from: p, to: join(target, p.split('/').pop()!) })
        notify(c.cut ? t('Verschoben') : t('Kopiert'))
      } catch (e) { fail(e) } finally { setBusy(false); load() }
      return
    }
    xfer.start({ fromMount: c.mount, paths: c.paths, toMount: mount.id, to: target, move: c.cut })
  }
  const keys = (e: React.KeyboardEvent) => {
    if ((e.target as HTMLElement).closest('input, textarea')) return
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'c' && sel.length) { e.preventDefault(); toClip(sel, false); return }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'x' && sel.length && !readOnly) { e.preventDefault(); toClip(sel, true); return }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'v' && clip && !readOnly) { e.preventDefault(); paste(path); return }
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'a') { e.preventDefault(); setSelected(shown.map(i => i.name)) }
    else if (e.key === 'Escape' && selected.length) setSelected([])
    else if (e.key === 'Delete' && sel.length && !readOnly) remove(sel)
  }
  const submit = async () => {
    const n = name.trim()
    if (!n || !ask) return
    try {
      if (ask.kind === 'mkdir') await api.post('/api/files/external/mkdir', { mount: mount.id, path: join(path, n) })
      else await api.post('/api/files/external/move', { mount: mount.id, from: join(path, ask.item.name), to: join(path, n) })
      setAsk(null); load()
    } catch (e) { fail(e) }
  }
  const parts = path ? path.split('/') : []

  // Cryptomator-Tresor in diesem Ordner: wie in „Meine Dateien“ erst das Passwort
  if (vault && !vault.unlocked && !raw && vaultPanel) return <>{vaultPanel(vault.root, () => setRaw(true))}</>

  return (
    <div className={mergeClasses(s.root, dragging && s.drop)} tabIndex={-1} style={{ outline: 'none' }} onKeyDown={keys}
      onDragOver={e => { if (!readOnly && e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragging(true) } }}
      onDragLeave={e => { if (e.currentTarget === e.target) setDragging(false) }}
      onDrop={e => { e.preventDefault(); setDragging(false); if (!readOnly) upload(e.dataTransfer.files) }}>
      <div className={s.head}>
        <Breadcrumb size="small" aria-label={t('Pfad')}>
          <BreadcrumbItem><BreadcrumbButton onClick={() => onPath('')} current={!parts.length}>{mount.name}</BreadcrumbButton></BreadcrumbItem>
          {parts.map((p, n) => (
            <span key={n} style={{ display: 'contents' }}>
              <BreadcrumbDivider />
              <BreadcrumbItem><BreadcrumbButton onClick={() => onPath(parts.slice(0, n + 1).join('/'))} current={n === parts.length - 1}>{p}</BreadcrumbButton></BreadcrumbItem>
            </span>
          ))}
        </Breadcrumb>
        <div style={{ flex: 1 }} />
        {mount.search && <Input size="small" value={find} onChange={(_, d) => setFind(d.value)} contentBefore={<Search20Regular />} style={{ width: 220 }}
          placeholder={path ? t('In „{name}“ suchen', { name: parts[parts.length - 1] }) : t('In „{name}“ suchen', { name: mount.name })} aria-label={t('Im Ordner suchen')}
          onKeyDown={e => { if (e.key === 'Escape') setFind('') }} />}
        <Toolbar size="small">
          {sel.length > 0 && <>
            <Text size={200} className={s.count}>{t('{n} ausgewählt', { n: sel.length })}</Text>
            <ToolbarButton icon={<ArrowDownload20Regular />} disabled={!sel.some(i => !i.isDir)} onClick={() => downloadAll(sel)} aria-label={t('Herunterladen')} title={t('Herunterladen')} />
            {!readOnly && <>
              <ToolbarButton icon={<FolderArrowRight20Regular />} disabled={busy} onClick={() => setPicker('move')} aria-label={t('Verschieben nach …')} title={t('Verschieben nach …')} />
              <ToolbarButton icon={<Copy20Regular />} disabled={busy} onClick={() => setPicker('copy')} aria-label={t('Kopieren nach …')} title={t('Kopieren nach …')} />
              <ToolbarButton icon={<Delete20Regular />} disabled={busy} onClick={() => remove(sel)} aria-label={t('Löschen')} title={t('Löschen')} />
            </>}
            <ToolbarButton icon={<Dismiss20Regular />} onClick={() => setSelected([])} aria-label={t('Auswahl aufheben')} title={t('Auswahl aufheben')} />
          </>}
          {!readOnly && <ToolbarButton icon={<ArrowUpload20Regular />} disabled={busy} onClick={() => file.current?.click()}>{t('Hochladen')}</ToolbarButton>}
          {!readOnly && <ToolbarButton icon={<FolderAdd20Regular />} onClick={() => { setName(''); setAsk({ kind: 'mkdir' }) }}>{t('Neuer Ordner')}</ToolbarButton>}
          <ToolbarButton icon={<ArrowClockwise20Regular />} onClick={() => load(true)} aria-label={t('Aktualisieren')} title={t('Aktualisieren')} />
        </Toolbar>
        <input ref={file} type="file" multiple hidden onChange={e => { if (e.target.files) upload(e.target.files); e.target.value = '' }} />
      </div>
      <div style={{ padding: '0 16px' }}>
        <MessageBar intent="info"><MessageBarBody>
          {mount.kind === 'webdav' ? t('WebDAV-Ordner – die Dateien liegen auf dem anderen Server.') : mount.kind === 'onedrive' ? t('OneDrive-Ordner – die Dateien liegen bei Microsoft.') : mount.kind === 'pcloud' ? t('pCloud-Ordner – die Dateien liegen bei pCloud.') : t('Ordner auf dem Server – die Dateien liegen dort, nicht in deiner Ablage.')}
          {' '}{readOnly ? t('Nur zum Lesen.') : t('Gelöschtes ist endgültig weg (kein Papierkorb, keine Versionen).')}
        </MessageBarBody></MessageBar>
      </div>
      <div className={s.list} onContextMenu={e => { if (!(e.target as HTMLElement).closest('[data-entry]')) cm.open(e, null) }}>
        {term ? <ExternalHits term={term} mount={mount.id} path={path} onOpen={(_, dir) => { setFind(''); onPath(dir) }} />
          : items == null ? <Spinner style={{ marginTop: 24 }} /> : shown.length === 0 ? <Text className={s.muted} style={{ display: 'block', padding: 16 }}>{readOnly ? t('Leer.') : t('Leer. Dateien hierher ziehen oder „Hochladen“.')}</Text> : tiles ? (
          <div className={s.tiles} style={{ ['--tile' as any]: tilePx }}>
            {shown.map(i => (
              <div key={i.name} data-entry className={mergeClasses(s.tile, selected.includes(i.name) && s.tileSel)} title={i.name} aria-selected={selected.includes(i.name)}
                onContextMenu={e => { if (!selected.includes(i.name)) setSelected([i.name]); cm.open(e, i) }}
                onDoubleClick={() => (i.isDir ? onPath(join(path, i.name)) : download(i))}
                onClick={e => { if (i.isDir && plain(e) && !selected.length) onPath(join(path, i.name)); else click(e, i) }}>
                <span className={s.tileCheck} onClick={e => { e.stopPropagation(); toggle(i) }} onDoubleClick={e => e.stopPropagation()}>
                  <Checkbox checked={selected.includes(i.name)} aria-label={t('Auswählen')} />
                </span>
                <span className={s.tileMenu} onClick={e => e.stopPropagation()}>{menu(i)}</span>
                <div className={s.thumb}>
                  {!i.isDir && kindOf({ name: i.name, isDir: false } as any) === 'image'
                    ? <img src={thumbOf(i)} alt="" loading="lazy" style={{ width: '100%', height: '100%', objectFit: 'cover' }}
                        onError={e => { (e.currentTarget as HTMLImageElement).style.display = 'none' }} />
                    : <KindIcon item={{ name: i.name, isDir: i.isDir }} large />}
                </div>
                <span className={s.tileName}>{i.name}</span>
              </div>
            ))}
          </div>
        ) : (
          <Table size="small" aria-label={mount.name}>
            <TableHeader>
              <TableRow>
                <TableHeaderCell style={{ width: 36 }}>
                  <Checkbox checked={sel.length === 0 ? false : sel.length === shown.length ? true : 'mixed'} aria-label={t('Alle auswählen')}
                    onChange={() => setSelected(sel.length === shown.length ? [] : shown.map(i => i.name))} />
                </TableHeaderCell>
                <TableHeaderCell>{t('Name')}</TableHeaderCell>
                <TableHeaderCell style={{ width: 160 }}>{t('Geändert')}</TableHeaderCell>
                <TableHeaderCell style={{ width: 100 }}>{t('Größe')}</TableHeaderCell>
                <TableHeaderCell style={{ width: 44 }} />
              </TableRow>
            </TableHeader>
            <TableBody>
              {shown.map(i => (
                <TableRow key={i.name} data-entry className={s.row} appearance={selected.includes(i.name) ? 'brand' : 'none'} aria-selected={selected.includes(i.name)}
                  onContextMenu={e => { if (!selected.includes(i.name)) setSelected([i.name]); cm.open(e, i) }}
                  onClick={e => click(e, i)} onDoubleClick={() => (i.isDir ? onPath(join(path, i.name)) : download(i))}>
                  <TableCell onClick={e => { e.stopPropagation(); toggle(i) }} onDoubleClick={e => e.stopPropagation()}>
                    <Checkbox checked={selected.includes(i.name)} aria-label={t('Auswählen')} />
                  </TableCell>
                  <TableCell>
                    <TableCellLayout media={<KindIcon item={{ name: i.name, isDir: i.isDir }} />}>
                      {i.isDir ? <a href="#" onClick={e => { e.preventDefault(); if (!plain(e)) return; e.stopPropagation(); onPath(join(path, i.name)) }} style={{ color: 'inherit', textDecoration: 'none' }}>{i.name}</a> : i.name}
                    </TableCellLayout>
                  </TableCell>
                  <TableCell>{i.modified ? fmtWhen(i.modified, locale) : ''}</TableCell>
                  <TableCell>{i.isDir ? '' : fmtSize(i.size)}</TableCell>
                  <TableCell onClick={e => e.stopPropagation()}>{menu(i)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </div>
      {xfer.dialog}
      <ContextMenu at={cm.ctx?.at} onClose={cm.close}>
        {(() => {
          const i = cm.ctx?.data ?? null
          const list = i ? (selected.includes(i.name) ? sel : [i]) : []
          const target = i?.isDir ? join(path, i.name) : path
          return <>
            {i?.isDir && <MenuItem icon={<FolderOpen20Regular />} onClick={() => onPath(join(path, i.name))}>{t('Öffnen')}</MenuItem>}
            {list.some(x => !x.isDir) && <MenuItem icon={<ArrowDownload20Regular />} onClick={() => downloadAll(list)}>{t('Herunterladen')}</MenuItem>}
            {list.length > 0 && !readOnly && <MenuItem icon={<Cut20Regular />} secondaryContent={t('Strg+X')} onClick={() => toClip(list, true)}>{t('Ausschneiden')}</MenuItem>}
            {list.length > 0 && <MenuItem icon={<Copy20Regular />} secondaryContent={t('Strg+C')} onClick={() => toClip(list, false)}>{t('Kopieren')}</MenuItem>}
            {clip && !readOnly && (!i || i.isDir) && <MenuItem icon={<ClipboardPaste20Regular />} secondaryContent={i ? undefined : t('Strg+V')} onClick={() => paste(target)}>{i ? t('Hier einfügen') : t('Einfügen')}</MenuItem>}
            {list.length > 0 && !readOnly && <MenuItem icon={<FolderArrowRight20Regular />} onClick={() => setPicker('move')}>{t('Verschieben nach …')}</MenuItem>}
            {list.length > 0 && !readOnly && <MenuItem icon={<Copy20Regular />} onClick={() => setPicker('copy')}>{t('Kopieren nach …')}</MenuItem>}
            {list.length === 1 && !readOnly && <MenuItem icon={<Rename20Regular />} onClick={() => { setName(list[0].name); setAsk({ kind: 'rename', item: list[0] }) }}>{t('Umbenennen')}</MenuItem>}
            {list.length > 0 && !readOnly && <MenuItem icon={<Delete20Regular />} onClick={() => remove(list)}>{t('Löschen')}</MenuItem>}
            {!i && !readOnly && <MenuItem icon={<FolderAdd20Regular />} onClick={() => { setName(''); setAsk({ kind: 'mkdir' }) }}>{t('Neuer Ordner')}</MenuItem>}
            {!i && <MenuItem icon={<ArrowClockwise20Regular />} onClick={() => load(true)}>{t('Aktualisieren')}</MenuItem>}
          </>
        })()}
      </ContextMenu>
      {picker && <BrowseDialog mount={mount.id} start={path}
        title={picker === 'move' ? t('{n} verschieben nach …', { n: sel.length }) : t('{n} kopieren nach …', { n: sel.length })}
        onClose={() => setPicker(null)} onPick={target => transfer(target, picker)} />}
      <Dialog open={!!ask} onOpenChange={(_, d) => { if (!d.open) setAsk(null) }}>
        <DialogSurface>
          <form onSubmit={e => { e.preventDefault(); submit() }}>
            <DialogBody>
              <DialogTitle>{ask?.kind === 'mkdir' ? t('Neuer Ordner') : t('Umbenennen')}</DialogTitle>
              <DialogContent><Field label={t('Name')}><Input value={name} onChange={(_, d) => setName(d.value)} autoFocus /></Field></DialogContent>
              <DialogActions>
                <Button onClick={() => setAsk(null)}>{t('Abbrechen')}</Button>
                <Button appearance="primary" type="submit" disabled={!name.trim()}>{t('OK')}</Button>
              </DialogActions>
            </DialogBody>
          </form>
        </DialogSurface>
      </Dialog>
    </div>
  )
}

type OwnKind = 'webdav' | 'onedrive' | 'pcloud'

/** Eigenen WebDAV-, OneDrive- oder pCloud-Ordner einbinden (je nachdem, was der Admin erlaubt). */
export function AddExternalDialog({ webDav = true, oneDrive = false, pCloud = false, onClose, onAdded }: { webDav?: boolean; oneDrive?: boolean; pCloud?: boolean; onClose: () => void; onAdded: (m: ExternalMount) => void }) {
  const { fail } = useApp()
  const kinds = ([['webdav', webDav, 'WebDAV'], ['onedrive', oneDrive, 'OneDrive'], ['pcloud', pCloud, 'pCloud']] as const).filter(k => k[1])
  const [kind, setKind] = useState<OwnKind>(kinds[0]?.[0] ?? 'webdav')
  const [f, setF] = useState({ name: '', url: '', user: '', password: '' })
  const [busy, setBusy] = useState(false)
  const set = (k: keyof typeof f) => (_: unknown, d: { value: string }) => setF(x => ({ ...x, [k]: d.value }))
  const add = async () => {
    setBusy(true)
    try { onAdded(await api.post<ExternalMount>('/api/files/external/mounts', f)) } catch (e) { fail(e) } finally { setBusy(false) }
  }
  // Art nur zur Wahl, wenn mehrere erlaubt sind
  const choice = kinds.length > 1 ? (
    <Field label={t('Art')}>
      <RadioGroup layout="horizontal" value={kind} onChange={(_, d) => setKind(d.value as OwnKind)}>
        {kinds.map(k => <Radio key={k[0]} value={k[0]} label={k[2]} />)}
      </RadioGroup>
    </Field>
  ) : null
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 560 }}>
        {kind === 'onedrive' ? <AddOneDrive choice={choice} onClose={onClose} onAdded={onAdded} />
          : kind === 'pcloud' ? <AddPCloud choice={choice} onClose={onClose} onAdded={onAdded} /> : (
          <form onSubmit={e => { e.preventDefault(); add() }}>
            <DialogBody>
              <DialogTitle>{t('WebDAV-Ordner einbinden')}</DialogTitle>
              <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {choice}
                <Field label={t('Name')}><Input value={f.name} onChange={set('name')} placeholder={t('z. B. Nextcloud im Büro')} autoFocus /></Field>
                <Field label={t('Adresse')} hint={t('Nextcloud: https://cloud.example.org/remote.php/dav/files/<name>/ · Storage Box: https://u123456.your-storagebox.de/')}>
                  <Input value={f.url} onChange={set('url')} placeholder="https://" />
                </Field>
                <Field label={t('Benutzername')}><Input value={f.user} onChange={set('user')} autoComplete="off" /></Field>
                <Field label={t('Passwort')} hint={t('Am besten ein App-Passwort nur für dmOffice.')}>
                  <Input type="password" autoComplete="new-password" value={f.password} onChange={set('password')} />
                </Field>
              </DialogContent>
              <DialogActions>
                <Button onClick={onClose}>{t('Abbrechen')}</Button>
                <Button appearance="primary" type="submit" disabled={busy || !f.name.trim() || !f.url.trim()}>{busy ? t('Verbinde …') : t('Einbinden')}</Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
      </DialogSurface>
    </Dialog>
  )
}

/**
 * Externes Ziel bearbeiten: Name; bei WebDAV Adresse, Benutzer und Passwort (leer = bleibt), bei OneDrive und pCloud neu
 * anmelden samt Ordnerwahl. Die Id bleibt - Einbindungen in Dateien, DMS und die Datensicherung zeigen weiter darauf.
 */
export function EditExternalDialog({ target, onClose, onSaved }: { target: ExternalMount; onClose: () => void; onSaved: (m: ExternalMount) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [f, setF] = useState<{ name: string; url: string; user: string; password: string; passwordSet: boolean } | null>(null)
  const [signIn, setSignIn] = useState(false)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    api.get<{ name: string; url: string | null; user: string | null; passwordSet: boolean }>('/api/files/external/mounts/' + encodeURIComponent(target.id))
      .then(r => setF({ name: r.name, url: r.url ?? '', user: r.user ?? '', password: '', passwordSet: r.passwordSet })).catch(fail)
  }, [target.id, fail])
  const set = (k: 'name' | 'url' | 'user' | 'password') => (_: unknown, d: { value: string }) => setF(x => x && ({ ...x, [k]: d.value }))
  const webDav = target.kind === 'webdav'
  const save = async () => {
    if (!f) return
    setBusy(true)
    try {
      onSaved(await api.put<ExternalMount>('/api/files/external/mounts/' + encodeURIComponent(target.id),
        webDav ? { name: f.name, url: f.url, user: f.user, password: f.password } : { name: f.name }))
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const named = { ...target, name: f?.name.trim() || target.name }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 560 }}>
        {signIn && target.kind === 'onedrive' ? <AddOneDrive choice={null} replace={named} onClose={onClose} onAdded={onSaved} />
          : signIn && target.kind === 'pcloud' ? <AddPCloud choice={null} replace={named} onClose={onClose} onAdded={onSaved} /> : (
          <form onSubmit={e => { e.preventDefault(); save() }}>
            <DialogBody>
              <DialogTitle>{t('Externes Ziel bearbeiten')}</DialogTitle>
              <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
                {!f ? <Spinner size="tiny" /> : <>
                  <Field label={t('Name')}><Input value={f.name} onChange={set('name')} autoFocus /></Field>
                  {webDav ? <>
                    <Field label={t('Adresse')} hint={t('Einbindungen in Dateien beziehen ihren Ordner auf diese Adresse.')}>
                      <Input value={f.url} onChange={set('url')} placeholder="https://" />
                    </Field>
                    <Field label={t('Benutzername')}><Input value={f.user} onChange={set('user')} autoComplete="off" /></Field>
                    <Field label={t('Passwort')} hint={f.passwordSet ? t('Gespeichert. Leer lassen, um es zu behalten.') : undefined}>
                      <Input type="password" autoComplete="new-password" value={f.password} placeholder={f.passwordSet ? '••••••••' : ''} onChange={set('password')} />
                    </Field>
                  </> : <>
                    <Text size={200} className={s.muted}>{(target.kind === 'onedrive' ? 'OneDrive' : 'pCloud') + (target.account ? ' · ' + target.account : '')}</Text>
                    <Text size={200} className={s.muted}>{t('Neu anmelden, etwa nach abgelaufener Anmeldung oder für ein anderes Konto; dabei wird auch der Ordner neu gewählt. Einbindungen in Dateien beziehen ihren Ordner auf diesen.')}</Text>
                    <div><Button onClick={() => setSignIn(true)}>{target.kind === 'onedrive' ? t('Mit Microsoft neu anmelden') : t('Mit pCloud neu anmelden')}</Button></div>
                  </>}
                </>}
              </DialogContent>
              <DialogActions>
                <Button onClick={onClose}>{t('Abbrechen')}</Button>
                <Button appearance="primary" type="submit" disabled={busy || !f?.name.trim() || (webDav && !f?.url.trim())}>{busy ? t('Verbinde …') : t('Speichern')}</Button>
              </DialogActions>
            </DialogBody>
          </form>
        )}
      </DialogSurface>
    </Dialog>
  )
}

interface DriveFolders { drive: { id: string; type: string; owner: string | null }; folders: { id: string; name: string }[] }

/**
 * OneDrive einbinden: bei Microsoft anmelden (Gerätecode, Zugriff auf die Dateien), dann einen Ordner wählen.
 * Das Token bleibt auf dem Server; hier geht nur die Kennung der Anmeldung hin und her.
 */
function AddOneDrive({ choice, replace, onClose, onAdded }: { choice: ReactNode; replace?: ExternalMount; onClose: () => void; onAdded: (m: ExternalMount) => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [step, setStep] = useState<'start' | 'signin' | 'pick'>('start')
  const [flow, setFlow] = useState<string | null>(null)
  // Weg vom Laufwerk bis zum gewählten Ordner
  const [trail, setTrail] = useState<{ id: string; name: string }[]>([])
  const [data, setData] = useState<DriveFolders | null>(null)
  const [name, setName] = useState(replace?.name ?? 'OneDrive')
  const [named, setNamed] = useState(!!replace)
  const [busy, setBusy] = useState(false)
  const here = trail.length ? trail[trail.length - 1].id : ''
  useEffect(() => {
    if (!flow) return
    let stop = false
    setData(null)
    api.get<DriveFolders>('/api/files/external/onedrive/folders?' + q({ flow, item: here }))
      .then(r => { if (!stop) setData(r) }).catch(e => { if (!stop) fail(e) })
    return () => { stop = true }
  }, [flow, here, fail])
  const go = (next: { id: string; name: string }[]) => {
    setTrail(next)
    if (!named) setName(next.length ? next[next.length - 1].name : 'OneDrive')
  }
  const add = async () => {
    setBusy(true)
    try {
      onAdded(await api.post<ExternalMount>('/api/files/external/onedrive', { flow, name: name.trim(), item: here, path: trail.map(x => x.name).join('/'), replace: replace?.id }))
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const type = data?.drive.type === 'business' ? t('geschäftlich') : data?.drive.type === 'personal' ? t('privat') : data?.drive.type
  return (
    <DialogBody>
      <DialogTitle>{replace ? t('OneDrive neu anmelden') : t('OneDrive-Ordner einbinden')}</DialogTitle>
      <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        {step === 'start' && <>
          {choice}
          <Text>{t('Ein Ordner im OneDrive, privat oder geschäftlich. dmOffice fragt bei Microsoft den Zugriff auf die Dateien an.')}</Text>
          <Text size={200} className={s.muted}>{t('Bei geschäftlichen Konten kann die Organisation verlangen, dass ihr Administrator zustimmt.')}</Text>
          <div><Button appearance="primary" onClick={() => setStep('signin')}>{t('Mit Microsoft anmelden')}</Button></div>
        </>}
        {step === 'signin' && <MicrosoftSignIn startUrl="/api/files/external/onedrive/start"
          onDone={f => { setFlow(f); setStep('pick') }} onCancel={() => setStep('start')} />}
        {step === 'pick' && <>
          <Text size={200} className={s.muted}>
            {data ? [data.drive.owner, type].filter(Boolean).join(' · ') : t('Wird geladen …')}
          </Text>
          <Breadcrumb size="small" aria-label={t('Ordner')}>
            <BreadcrumbItem><BreadcrumbButton onClick={() => go([])} current={trail.length === 0}>OneDrive</BreadcrumbButton></BreadcrumbItem>
            {trail.map((x, i) => (
              <span key={x.id} style={{ display: 'contents' }}>
                <BreadcrumbDivider />
                <BreadcrumbItem><BreadcrumbButton onClick={() => go(trail.slice(0, i + 1))} current={i === trail.length - 1}>{x.name}</BreadcrumbButton></BreadcrumbItem>
              </span>
            ))}
          </Breadcrumb>
          <div className={s.pickList} role="list" aria-label={t('Ordner')}>
            {!data ? <div style={{ padding: 12 }}><Spinner size="tiny" /></div>
              : data.folders.length === 0 ? <Text size={200} className={s.muted} style={{ padding: 10 }}>{t('Keine Unterordner.')}</Text>
                : data.folders.map(x => (
                  <button key={x.id} type="button" role="listitem" className={s.pickRow} onClick={() => go([...trail, x])}>
                    <Folder20Regular />{x.name}
                  </button>
                ))}
          </div>
          <Text size={200} className={s.muted}>{replace ? t('Das externe Ziel ist dann der Ordner, der oben zuletzt steht.') : t('Eingebunden wird der Ordner, der oben zuletzt steht.')}</Text>
          <Field label={t('Name in dmOffice')}><Input value={name} onChange={(_, d) => { setName(d.value); setNamed(true) }} /></Field>
        </>}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('Abbrechen')}</Button>
        {step === 'pick' && <Button appearance="primary" disabled={busy || !data || !name.trim()} onClick={add}>{busy ? t('Verbinde …') : replace ? t('Speichern') : t('Einbinden')}</Button>}
      </DialogActions>
    </DialogBody>
  )
}

/**
 * pCloud einbinden: in einem eigenen Fenster bei pCloud anmelden (OAuth), hier warten, bis der Server den Rücksprung hat,
 * dann einen Ordner wählen. Das Token bleibt auf dem Server; hier geht nur die Kennung der Anmeldung hin und her.
 */
function AddPCloud({ choice, replace, onClose, onAdded }: { choice: ReactNode; replace?: ExternalMount; onClose: () => void; onAdded: (m: ExternalMount) => void }) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [step, setStep] = useState<'start' | 'signin' | 'pick'>('start')
  const [flow, setFlow] = useState<string | null>(null)
  const [account, setAccount] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Weg von der obersten Ebene bis zum gewählten Ordner
  const [trail, setTrail] = useState<string[]>([])
  const [folders, setFolders] = useState<string[] | null>(null)
  const [name, setName] = useState(replace?.name ?? 'pCloud')
  const [named, setNamed] = useState(!!replace)
  const [busy, setBusy] = useState(false)
  const popup = useRef<Window | null>(null)
  const path = trail.join('/')
  // Eigene App bei pCloud: Client-ID und Secret (das gespeicherte Secret kommt nicht zurück, nur ob es eins gibt)
  const [app, setApp] = useState<{ clientId: string; secret: string; secretSet: boolean; redirect: string } | null>(null)
  useEffect(() => {
    api.get<{ clientId: string; secretSet: boolean; redirect: string }>('/api/files/external/pcloud/app')
      .then(r => setApp({ ...r, secret: '' })).catch(fail)
  }, [fail])
  const appReady = !!app && !!app.clientId.trim() && (app.secretSet || !!app.secret.trim())

  const signIn = async () => {
    setError(null)
    // Das Fenster gleich im Klick öffnen (sonst blockt der Browser es), die Adresse folgt
    const w = window.open('', 'dmoffice-pcloud', 'width=560,height=720')
    popup.current = w
    try {
      const r = await api.post<{ flow: string; url: string }>('/api/files/external/pcloud/start', { clientId: app?.clientId ?? '', clientSecret: app?.secret ?? '' })
      setApp(a => a && ({ ...a, secret: '', secretSet: true }))
      if (w) { w.opener = null; w.location.href = r.url } else window.open(r.url, '_blank', 'noopener')
      setFlow(r.flow); setStep('signin')
    } catch (e) { w?.close(); fail(e) }
  }
  // Warten auf den Rücksprung von pCloud
  useEffect(() => {
    if (step !== 'signin' || !flow) return
    const id = setInterval(() => {
      api.get<{ state: 'pending' | 'ok' | 'error'; error: string | null; account: string | null }>('/api/files/external/pcloud/status?' + q({ flow }))
        .then(r => {
          if (r.state === 'pending') return
          clearInterval(id)
          try { popup.current?.close() } catch { /* schon zu */ }
          if (r.state === 'ok') { setAccount(r.account); setStep('pick') } else { setError(r.error); setStep('start') }
        }).catch(e => { clearInterval(id); fail(e); setStep('start') })
    }, 2000)
    return () => clearInterval(id)
  }, [step, flow, fail])
  useEffect(() => {
    if (step !== 'pick' || !flow) return
    let stop = false
    setFolders(null)
    api.get<{ folders: string[] }>('/api/files/external/pcloud/folders?' + q({ flow, path }))
      .then(r => { if (!stop) setFolders(r.folders) }).catch(e => { if (!stop) fail(e) })
    return () => { stop = true }
  }, [step, flow, path, fail])
  const go = (next: string[]) => {
    setTrail(next)
    if (!named) setName(next.length ? next[next.length - 1] : 'pCloud')
  }
  const add = async () => {
    setBusy(true)
    try { onAdded(await api.post<ExternalMount>('/api/files/external/pcloud', { flow, name: name.trim(), path, replace: replace?.id })) } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <DialogBody>
      <DialogTitle>{replace ? t('pCloud neu anmelden') : t('pCloud-Ordner einbinden')}</DialogTitle>
      <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
        {step === 'start' && <>
          {choice}
          <Text>{t('Ein Ordner in pCloud. Die Anmeldung läuft in einem eigenen Fenster bei pCloud, auch mit Zwei-Faktor-Anmeldung. Danach hat dmOffice Zugriff auf die Dateien des Kontos.')}</Text>
          <Text size={200} className={s.muted}>{t('Dafür ist eine eigene App bei pCloud nötig: auf my.pcloud.com unter Einstellungen → My Apps → Create new app anlegen, dort die Rücksprungadresse eintragen und hier Client-ID und Client-Secret der App angeben.')}</Text>
          {app && <>
            <Field label={t('Rücksprungadresse (Redirect URI) für die pCloud-App')}>
              <div style={{ display: 'flex', gap: 4 }}>
                <Input readOnly value={app.redirect} style={{ flex: 1 }} onFocus={e => e.target.select()} />
                <Button icon={<Copy20Regular />} title={t('Adresse kopieren')} aria-label={t('Adresse kopieren')}
                  onClick={() => { navigator.clipboard?.writeText(app.redirect).then(() => notify(t('Kopiert'))).catch(() => {}) }} />
              </div>
            </Field>
            <Field label={t('Client-ID')}>
              <Input value={app.clientId} autoComplete="off" onChange={(_, d) => setApp(a => a && ({ ...a, clientId: d.value }))} />
            </Field>
            <Field label={t('Client-Secret')} hint={app.secretSet ? t('Gespeichert. Leer lassen, um es zu behalten.') : undefined}>
              <Input type="password" autoComplete="new-password" value={app.secret} placeholder={app.secretSet ? '••••••••' : ''}
                onChange={(_, d) => setApp(a => a && ({ ...a, secret: d.value }))} />
            </Field>
          </>}
          {error && <MessageBar intent="error"><MessageBarBody>{t(error)}</MessageBarBody></MessageBar>}
          <div><Button appearance="primary" disabled={!appReady} onClick={signIn}>{t('Mit pCloud anmelden')}</Button></div>
        </>}
        {step === 'signin' && <>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}><Spinner size="tiny" /><Text>{t('Warte auf die Anmeldung bei pCloud …')}</Text></div>
          <Text size={200} className={s.muted}>{t('Im Fenster von pCloud anmelden und den Zugriff erlauben. Danach geht es hier weiter.')}</Text>
        </>}
        {step === 'pick' && <>
          {account && <Text size={200} className={s.muted}>{account}</Text>}
          <Breadcrumb size="small" aria-label={t('Ordner')}>
            <BreadcrumbItem><BreadcrumbButton onClick={() => go([])} current={trail.length === 0}>pCloud</BreadcrumbButton></BreadcrumbItem>
            {trail.map((x, i) => (
              <span key={i} style={{ display: 'contents' }}>
                <BreadcrumbDivider />
                <BreadcrumbItem><BreadcrumbButton onClick={() => go(trail.slice(0, i + 1))} current={i === trail.length - 1}>{x}</BreadcrumbButton></BreadcrumbItem>
              </span>
            ))}
          </Breadcrumb>
          <div className={s.pickList} role="list" aria-label={t('Ordner')}>
            {!folders ? <div style={{ padding: 12 }}><Spinner size="tiny" /></div>
              : folders.length === 0 ? <Text size={200} className={s.muted} style={{ padding: 10 }}>{t('Keine Unterordner.')}</Text>
                : folders.map(x => (
                  <button key={x} type="button" role="listitem" className={s.pickRow} onClick={() => go([...trail, x])}>
                    <Folder20Regular />{x}
                  </button>
                ))}
          </div>
          <Text size={200} className={s.muted}>{replace ? t('Das externe Ziel ist dann der Ordner, der oben zuletzt steht.') : t('Eingebunden wird der Ordner, der oben zuletzt steht.')}</Text>
          <Field label={t('Name in dmOffice')}><Input value={name} onChange={(_, d) => { setName(d.value); setNamed(true) }} /></Field>
        </>}
      </DialogContent>
      <DialogActions>
        <Button onClick={onClose}>{t('Abbrechen')}</Button>
        {step === 'pick' && <Button appearance="primary" disabled={busy || !folders || !name.trim()} onClick={add}>{busy ? t('Verbinde …') : replace ? t('Speichern') : t('Einbinden')}</Button>}
      </DialogActions>
    </DialogBody>
  )
}

/**
 * Eigener WebDAV-Ordner: welche Ordner der obersten Ebene in dmOffice erscheinen; auf Wunsch nicht mehr einbinden.
 * Kommt gleich nach dem Einbinden ([added]) und per Rechtsklick auf den Ordner links.
 */
export function ExternalMountDialog({ mount, added, onClose, onRemoved }: { mount: ExternalMount; added?: boolean; onClose: () => void; onRemoved: () => void }) {
  const { fail, notify } = useApp()
  const [dirs, setDirs] = useState<string[] | null>(null)
  const [include, setInclude] = useState<string[]>([])
  const [noFolder, setNoFolder] = useState(false)
  const [name, setName] = useState(mount.name)
  useEffect(() => {
    api.get<{ dirs: string[]; include: string[] }>(`/api/files/external/mounts/${encodeURIComponent(mount.id)}/top`)
      .then(r => { setDirs(r.dirs); setInclude(r.include) }).catch(e => { setDirs([]); fail(e) })
  }, [mount.id, fail])
  const save = async () => {
    try {
      await api.put(`/api/files/external/mounts/${encodeURIComponent(mount.id)}/include`, { include, name: name.trim() })
      notify(t('Gespeichert'))
      if (name.trim() !== mount.name) window.dispatchEvent(new Event('dmoffice:external-changed'))
      onClose()
    } catch (e) { fail(e) }
  }
  const remove = async () => {
    if (!await askConfirm('', t('„{name}“ nicht mehr einbinden? Die Dateien dort bleiben, wie sie sind.', { name: mount.name }))) return
    try { await api.del('/api/files/external/mounts/' + encodeURIComponent(mount.id)); onRemoved() } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{mount.name}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 8 }}>
            {added && <Text>{t('Die Verbindung klappt.')}</Text>}
            <Field label={t('Name in Dateien')}><Input value={name} onChange={(_, d) => setName(d.value)} /></Field>
            <Text weight="semibold">{t('Welche Ordner sollen in dmOffice erscheinen?')}</Text>
            <TopFolderChoice dirs={dirs} include={include} onChange={setInclude} onNone={setNoFolder} />
          </DialogContent>
          <DialogActions>
            {!added && <Button appearance="subtle" onClick={remove} style={{ marginRight: 'auto' }}>{t('Nicht mehr einbinden …')}</Button>}
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={dirs === null || !name.trim() || noFolder} onClick={save}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/**
 * In Dateien einbinden: ein externes Ziel (angelegt unter Einstellungen → Externe Ziele) und ein Ordner darin, unter einem
 * eigenen Namen. Ein Ziel lässt sich mehrfach mit verschiedenen Ordnern einbinden; Zugangsdaten bleiben beim Ziel.
 */
export function BindExternalDialog({ targets, onClose, onBound }: { targets: ExternalMount[]; onClose: () => void; onBound: (m: ExternalMount) => void }) {
  const { fail } = useApp()
  const [target, setTarget] = useState(targets[0]?.id ?? '')
  const [path, setPath] = useState('')
  const [name, setName] = useState(targets[0]?.name ?? '')
  const [named, setNamed] = useState(false)
  const [browse, setBrowse] = useState(false)
  const [busy, setBusy] = useState(false)
  const chosen = targets.find(x => x.id === target)
  // Vorschlag für den Namen: letzter Ordner, sonst das Ziel - bis man selbst etwas einträgt
  useEffect(() => { if (!named) setName(path ? path.split('/').pop()! : chosen?.name ?? '') }, [path, chosen, named])
  const bind = async () => {
    setBusy(true)
    try { onBound(await api.post<ExternalMount>('/api/files/external/mounts/bind', { access: target, path, name: name.trim() })) } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const settings = () => { onClose(); location.hash = '#/settings/external-targets' }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{t('Externen Ordner einbinden')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {targets.length === 0 ? <>
              <Text>{t('Noch kein externes Ziel angelegt. Externe Ziele (WebDAV, OneDrive, pCloud) mit ihren Zugangsdaten werden unter Einstellungen → Dateien → Externe Ziele angelegt.')}</Text>
            </> : <>
              <Field label={t('Externes Ziel')}>
                <Select value={target} onChange={(_, d) => { setTarget(d.value); setPath('') }}>
                  {targets.map(x => <option key={x.id} value={x.id}>{x.name}{x.account ? ' · ' + x.account : ''}</option>)}
                </Select>
              </Field>
              <Field label={t('Ordner')}>
                <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                  <Input readOnly value={'/' + path} style={{ flex: 1 }} />
                  <Button onClick={() => setBrowse(true)} disabled={!target}>{t('Wählen …')}</Button>
                </div>
              </Field>
              <Field label={t('Name in Dateien')}><Input value={name} onChange={(_, d) => { setName(d.value); setNamed(true) }} /></Field>
            </>}
          </DialogContent>
          <DialogActions>
            {targets.length === 0
              ? <Button appearance="primary" onClick={settings}>{t('Externe Ziele öffnen')}</Button>
              : <Button appearance="primary" disabled={busy || !target || !name.trim()} onClick={bind}>{t('Einbinden')}</Button>}
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
        {browse && <BrowseDialog mount={target} start={path} title={t('Ordner wählen')} onClose={() => setBrowse(false)} onPick={p => { setPath(p); setBrowse(false) }} />}
      </DialogSurface>
    </Dialog>
  )
}
