import {
  Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Menu, MenuItem, MenuList, MenuPopover, MenuTrigger,
  Spinner, Table, TableBody, TableCell, TableCellLayout, TableHeader, TableHeaderCell, TableRow, Text,
  makeStyles, mergeClasses, tokens, ProgressBar, MenuItemRadio,
} from '@fluentui/react-components'
import {
  ArrowUpload20Regular, FolderAdd20Regular, Folder20Filled, Document20Regular, Image20Regular, Delete20Regular, ArrowDownload20Regular,
  Rename20Regular, MoreHorizontal20Regular, DocumentPdf20Regular, Home20Regular,
  FolderOpen20Regular, Folder20Regular, ArrowSort20Regular, PanelLeft20Regular, Settings20Regular,
} from '@fluentui/react-icons'
import { RibbonButton, RibbonDivider, RibbonHeader, useSidePane } from '../shell/Ribbon'
import { usePrefs } from '../prefs'
import { useCallback, useEffect, useRef, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { useApp } from '../App'
import { locale, t } from '../i18n'

const useStyles = makeStyles({
  outer: { display: 'flex', flexDirection: 'column', flex: 1, minWidth: 0 },
  root: { flex: 1, display: 'flex', minWidth: 0, minHeight: 0 },
  side: { width: '220px', flexShrink: 0, borderRight: `1px solid ${tokens.colorNeutralStroke2}`, padding: '12px 8px', display: 'flex', flexDirection: 'column', gap: '2px', overflowY: 'auto' },
  nav: { display: 'flex', alignItems: 'center', gap: '10px', padding: '7px 10px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  navName: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  active: { backgroundColor: tokens.colorBrandBackground2, fontWeight: 600 },
  main: { flex: 1, display: 'flex', flexDirection: 'column', minWidth: 0 },
  sel: { backgroundColor: tokens.colorBrandBackground2, ':hover': { backgroundColor: tokens.colorBrandBackground2Hover } },
  crumbs: { padding: '8px 16px' },
  table: { flex: 1, overflowY: 'auto', padding: '0 16px 16px' },
  drop: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '-8px' },
  row: { cursor: 'pointer' },
  foot: { padding: '10px 10px 2px', color: tokens.colorNeutralForeground3, fontSize: '12px', display: 'flex', flexDirection: 'column', gap: '6px' },
})

interface Item { path: string; name: string; isDir: boolean; size: number; mime: string | null; modified: string }

function Icon({ item }: { item: Item }) {
  if (item.isDir) return <Folder20Filled color="#E8A33D" />
  if (item.mime?.startsWith('image/')) return <Image20Regular />
  if (item.mime === 'application/pdf') return <DocumentPdf20Regular />
  return <Document20Regular />
}

/** Die Dateien im eigenen WebDAV - dieselben, die Programme unter /dav/files sehen. */
export default function FilesView() {
  const s = useStyles()
  const { fail } = useApp()
  const [path, setPath] = useState(() => decodeURIComponent(location.hash.split('/').slice(2).join('/')))
  const [data, setData] = useState<{ items: Item[]; usage: number; quota: number } | null>(null)
  const [busy, setBusy] = useState(false)
  const [dragging, setDragging] = useState(false)
  const upload = useRef<HTMLInputElement>(null)
  const [selected, setSelected] = useState<Item | null>(null)
  const [roots, setRoots] = useState<Item[]>([])
  const { prefs, update } = usePrefs()
  const sort = prefs.fileSort ?? 'name'
  const [sidePane, toggleSide] = useSidePane('files')
  // Auf Touch-Geraeten oeffnet ein Tippen, sonst waehlt ein Klick aus und Doppelklick oeffnet
  const touch = typeof matchMedia === 'function' && matchMedia('(pointer: coarse)').matches
  const loadRoots = useCallback(() => api.get<{ items: Item[] }>('/api/files/?' + q({ path: '' })).then(d => setRoots(d.items.filter(i => i.isDir))).catch(() => {}), [])
  useEffect(() => { loadRoots() }, [loadRoots])

  // Nur die Antwort der neuesten Anfrage uebernehmen
  const req = useRef(0)
  const load = useCallback(() => {
    const n = ++req.current
    return api.get('/api/files/?' + q({ path }))
      .then(d => { if (n === req.current) setData(d) })
      .catch(e => { if (n !== req.current) return; fail(e); setPath('') })
  }, [path, fail])
  useEffect(() => { setSelected(null); load(); history.replaceState(null, '', '#/files/' + path.split('/').map(encodeURIComponent).join('/')) }, [path]) // eslint-disable-line react-hooks/exhaustive-deps

  const send = async (files: FileList | File[]) => {
    const f = new FormData()
    Array.from(files).forEach(x => f.append('files', x, (x as any).webkitRelativePath || x.name))
    setBusy(true)
    try { await api.form('/api/files/upload?' + q({ path }), f); load() } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const mkdir = async () => {
    const name = prompt(t('Name des Ordners:'))
    if (!name) return
    try { await api.post('/api/files/mkdir', { path: path ? `${path}/${name}` : name }); setSelected(null); load(); loadRoots() } catch (e) { fail(e) }
  }
  const remove = async (i: Item) => {
    if (!confirm(i.isDir ? t('„{name}“ samt Inhalt löschen?', { name: i.name }) : t('„{name}“ löschen?', { name: i.name }))) return
    try { await api.post('/api/files/delete', { path: i.path }); setSelected(null); load(); loadRoots() } catch (e) { fail(e) }
  }
  const rename = async (i: Item) => {
    const name = prompt(t('Neuer Name:'), i.name)
    if (!name || name === i.name) return
    const to = i.path.includes('/') ? i.path.slice(0, i.path.lastIndexOf('/') + 1) + name : name
    try { await api.post('/api/files/move', { from: i.path, to, copy: false }); setSelected(null); load(); loadRoots() } catch (e) { fail(e) }
  }
  const open = (i: Item) => {
    if (i.isDir) setPath(i.path)
    else window.open('/api/files/download?' + q({ path: i.path, inline: true }), '_blank')
  }

  const parts = path ? path.split('/') : []
  const download = (i: Item) => { location.href = '/api/files/download?' + q({ path: i.path }) }
  const items = data ? [...data.items].sort((a, b) => Number(b.isDir) - Number(a.isDir)
    || (sort === 'modified' ? b.modified.localeCompare(a.modified) : sort === 'size' ? b.size - a.size : a.name.localeCompare(b.name, locale, { sensitivity: 'base' }))) : []

  return (
    <div className={s.outer}>
      {/* Menueband ueber die ganze Breite wie in Office - auch ueber dem Navigationsbereich */}
      <RibbonHeader area="files"
        start={<>
          <RibbonButton primary icon={busy ? <Spinner size="tiny" /> : <ArrowUpload20Regular />} label={t('Hochladen')} onClick={() => upload.current?.click()} />
          <RibbonDivider />
          <RibbonButton icon={<FolderOpen20Regular />} label={t('Öffnen')} disabled={!selected} onClick={() => selected && open(selected)} />
          <RibbonButton icon={<ArrowDownload20Regular />} label={t('Herunterladen')} disabled={!selected || selected.isDir} onClick={() => selected && download(selected)} />
          <RibbonButton icon={<Rename20Regular />} label={t('Umbenennen')} disabled={!selected} onClick={() => selected && rename(selected)} />
          <RibbonButton icon={<Delete20Regular />} label={t('Löschen')} disabled={!selected} onClick={() => selected && remove(selected)} />
          <RibbonDivider />
          <RibbonButton icon={<FolderAdd20Regular />} label={t('Neuer Ordner')} onClick={mkdir} />
        </>}
        view={<>
          <Menu>
            <MenuTrigger disableButtonEnhancement><RibbonButton icon={<ArrowSort20Regular />} label={t('Sortieren')} /></MenuTrigger>
            <MenuPopover>
              <MenuList checkedValues={{ sort: [sort] }} onCheckedValueChange={(_, d) => update({ fileSort: d.checkedItems[0] as any })}>
                <MenuItemRadio name="sort" value="name">{t('Name')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="modified">{t('Geändert')}</MenuItemRadio>
                <MenuItemRadio name="sort" value="size">{t('Größe')}</MenuItemRadio>
              </MenuList>
            </MenuPopover>
          </Menu>
          <RibbonDivider />
          <RibbonButton icon={<PanelLeft20Regular />} label={t('Ordnerbereich')} pressed={sidePane} onClick={toggleSide} />
          <RibbonDivider />
          <RibbonButton icon={<Settings20Regular />} label={t('WebDAV-Zugang')} onClick={() => { location.hash = '#/settings/devices' }} />
        </>} />
      <div className={s.root}>
      <aside className={s.side} style={sidePane ? undefined : { display: 'none' }}>
        <div className={mergeClasses(s.nav, !path && s.active)} onClick={() => setPath('')}><Home20Regular />{t('Meine Dateien')}</div>
        {roots.map(f => {
          const inside = path === f.path || path.startsWith(f.path + '/')
          return (
            <div key={f.path} className={mergeClasses(s.nav, inside && s.active)} onClick={() => setPath(f.path)} style={{ paddingLeft: 24 }}>
              {inside ? <FolderOpen20Regular /> : <Folder20Regular />}<span className={s.navName}>{f.name}</span>
            </div>
          )
        })}
        <div style={{ flex: 1 }} />
        {data && (
          <div className={s.foot}>
            {data.quota > 0 ? <>
              <ProgressBar value={Math.min(1, data.usage / data.quota)} color={data.usage / data.quota > 0.9 ? 'error' : 'brand'} />
              <span>{t('{used} von {quota} belegt', { used: fmtSize(data.usage), quota: fmtSize(data.quota) })}</span>
            </> : <span>{t('Belegt: {used}', { used: fmtSize(data.usage) })}</span>}
          </div>
        )}
      </aside>
    <div className={`${s.main} ${dragging ? s.drop : ''}`}
      onDragOver={e => { if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); setDragging(true) } }}
      onDragLeave={() => setDragging(false)}
      onDrop={e => { e.preventDefault(); setDragging(false); if (e.dataTransfer.files.length) send(e.dataTransfer.files) }}>
      <input ref={upload} type="file" multiple hidden onChange={e => { if (e.target.files) send(e.target.files); e.target.value = '' }} />
      <div className={s.crumbs}>
        <Breadcrumb>
          <BreadcrumbItem><BreadcrumbButton icon={<Home20Regular />} onClick={() => setPath('')}>{t('Dateien')}</BreadcrumbButton></BreadcrumbItem>
          {parts.map((p, i) => [
            <BreadcrumbDivider key={'d' + i} />,
            <BreadcrumbItem key={i}><BreadcrumbButton onClick={() => setPath(parts.slice(0, i + 1).join('/'))} current={i === parts.length - 1}>{p}</BreadcrumbButton></BreadcrumbItem>,
          ])}
        </Breadcrumb>
      </div>
      <div className={s.table}>
        {!data ? <Spinner /> : data.items.length === 0 ? (
          <Text style={{ color: tokens.colorNeutralForeground3 }}>{t('Leer. Dateien hierher ziehen oder „Hochladen“.')}</Text>
        ) : (
          <Table size="small">
            <TableHeader>
              <TableRow>
                <TableHeaderCell>{t('Name')}</TableHeaderCell>
                <TableHeaderCell style={{ width: 180 }}>{t('Geändert')}</TableHeaderCell>
                <TableHeaderCell style={{ width: 100 }}>{t('Größe')}</TableHeaderCell>
                <TableHeaderCell style={{ width: 50 }} />
              </TableRow>
            </TableHeader>
            <TableBody>
              {items.map(i => (
                <TableRow key={i.path} className={mergeClasses(s.row, selected?.path === i.path && s.sel)} onDoubleClick={() => open(i)}
                  onClick={() => touch ? open(i) : setSelected(i)} aria-selected={selected?.path === i.path}>
                  <TableCell><TableCellLayout media={<Icon item={i} />}>{i.name}</TableCellLayout></TableCell>
                  <TableCell>{new Date(i.modified).toLocaleString(locale)}</TableCell>
                  <TableCell>{i.isDir ? '' : fmtSize(i.size)}</TableCell>
                  <TableCell>
                    <Menu>
                      <MenuTrigger disableButtonEnhancement><Button appearance="subtle" size="small" icon={<MoreHorizontal20Regular />} aria-label={t('Mehr')} onClick={e => e.stopPropagation()} /></MenuTrigger>
                      <MenuPopover>
                        <MenuList>
                          {!i.isDir && <MenuItem icon={<ArrowDownload20Regular />} onClick={() => download(i)}>{t('Herunterladen')}</MenuItem>}
                          <MenuItem icon={<Rename20Regular />} onClick={() => rename(i)}>{t('Umbenennen')}</MenuItem>
                          <MenuItem icon={<Delete20Regular />} onClick={() => remove(i)}>{t('Löschen')}</MenuItem>
                        </MenuList>
                      </MenuPopover>
                    </Menu>
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        )}
      </div>
    </div>
      </div>
    </div>
  )
}
