import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input,
  Menu, MenuItem, MenuList, MenuPopover, Option, Text, Tooltip, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { Add16Regular, ChevronDown16Regular, ChevronRight16Regular, FolderSearch20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { Account, api, Folder } from '../api'
import { useApp } from '../App'
import { usePrefs } from '../prefs'
import { locale, t } from '../i18n'
import { CategoryDot, useCategories } from './Categories'
import { folderLabel } from './FolderPane'
import type { Selection } from './MailView'

export interface MailCriteria {
  text?: string | null; accounts?: string[] | null; folders?: string[] | null; categories?: string[] | null
  days?: number | null; from?: string | null; to?: string | null
  unread?: boolean; flagged?: boolean; attachments?: boolean; includeDeleted?: boolean
}
export interface SearchFolder { id: string; name: string; color: string; criteria: MailCriteria }
type Counts = Record<string, { total: number; unread: number }>

const useStyles = makeStyles({
  head: {
    display: 'flex', alignItems: 'center', gap: '4px', padding: '6px 6px', cursor: 'pointer', borderRadius: tokens.borderRadiusMedium,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  item: {
    display: 'flex', alignItems: 'center', gap: '8px', padding: '6px 8px 6px 22px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  active: { backgroundColor: tokens.colorNeutralBackground1Selected, fontWeight: 600 },
  name: { flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  unread: { color: tokens.colorBrandForeground1, fontWeight: 600, fontSize: '12px' },
  form: { display: 'flex', flexDirection: 'column', gap: '12px' },
  row: { display: 'flex', gap: '12px', flexWrap: 'wrap' },
  colors: { display: 'flex', gap: '6px', flexWrap: 'wrap' },
  color: { width: '22px', height: '22px', borderRadius: '50%', border: '2px solid transparent', cursor: 'pointer', padding: 0 },
  colorOn: { border: `2px solid ${tokens.colorNeutralForeground1}` },
  preview: { color: tokens.colorNeutralForeground3 },
})

const palette = ['#0F6CBD', '#D13438', '#CA5010', '#C19C00', '#13A10E', '#00B7C3', '#8764B8', '#E3008C', '#69797E']

/** Suchordner und Kategorien als virtuelle Ordner (wie bei Proton) - unter den Konten im Ordnerbereich. */
export function VirtualFolders({ accounts, folders, selection, onSelect, refresh }: {
  accounts: Account[]; folders: Record<string, Folder[]>; selection: Selection | null; onSelect: (s: Selection) => void; refresh: unknown
}) {
  const s = useStyles()
  const { fail } = useApp()
  const { prefs, update } = usePrefs()
  const categories = useCategories()
  const [list, setList] = useState<SearchFolder[]>([])
  const [counts, setCounts] = useState<Counts>({})
  const [edit, setEdit] = useState<SearchFolder | 'new' | null>(null)
  const [menu, setMenu] = useState<{ f: SearchFolder; x: number; y: number } | null>(null)
  const closed = prefs.virtualClosed ?? {}

  const load = useCallback(() => {
    api.get<SearchFolder[]>('/api/settings/search-folders').then(setList).catch(() => {})
    api.get<Counts>('/api/mail/virtual-counts').then(setCounts).catch(() => {})
  }, [])
  useEffect(() => { load() }, [load, refresh])

  const save = async (next: SearchFolder[]) => {
    try {
      const saved = await api.put<SearchFolder[]>('/api/settings/search-folders', next)
      setList(saved)
      load()
      return saved
    } catch (e) { fail(e); return null }
  }
  // Virtuelle Ordner haengen am ersten Konto - die Mails selbst kommen aus allen
  const anchor = accounts[0]?.id
  const open = (folder: string) => anchor && onSelect({ account: anchor, folder })
  const toggle = (k: 'search' | 'categories') => update({ virtualClosed: { ...closed, [k]: !closed[k] } })

  const item = (folder: string, icon: JSX.Element, name: string, onMenu?: (e: React.MouseEvent) => void) => {
    const c = counts[folder]
    return (
      <div key={folder} className={mergeClasses(s.item, selection?.folder === folder && s.active)} onClick={() => open(folder)}
        onContextMenu={onMenu ? e => { e.preventDefault(); onMenu(e) } : undefined}>
        {icon}
        <span className={s.name}>{name}</span>
        {c && c.unread > 0 && <span className={s.unread}>{c.unread.toLocaleString(locale)}</span>}
      </div>
    )
  }

  if (!anchor) return null
  return (
    <>
      <div style={{ marginBottom: 10 }}>
        <div className={s.head} onClick={() => toggle('search')}>
          {closed.search ? <ChevronRight16Regular /> : <ChevronDown16Regular />}
          <Text weight="semibold" style={{ flex: 1 }}>{t('Suchordner')}</Text>
          <Tooltip content={t('Neuer Suchordner')} relationship="label">
            <Button appearance="transparent" size="small" icon={<Add16Regular />} onClick={e => { e.stopPropagation(); setEdit('new') }} />
          </Tooltip>
        </div>
        {!closed.search && list.map(f => item('sf:' + f.id, <FolderSearch20Regular style={{ color: f.color }} />, f.name,
          e => setMenu({ f, x: e.clientX, y: e.clientY })))}
        {!closed.search && list.length === 0 && (
          <Text size={200} className={s.preview} style={{ paddingLeft: 24, display: 'block' }}>{t('Gespeicherte Suchen erscheinen hier wie Ordner.')}</Text>
        )}
      </div>
      {categories.length > 0 && (prefs.categoryFolders ?? true) && (
        <div style={{ marginBottom: 10 }}>
          <div className={s.head} onClick={() => toggle('categories')}>
            {closed.categories ? <ChevronRight16Regular /> : <ChevronDown16Regular />}
            <Text weight="semibold" style={{ flex: 1 }}>{t('Kategorien')}</Text>
          </div>
          {!closed.categories && categories.map(c => item('cat:' + c.key, <span style={{ width: 20, display: 'inline-flex', justifyContent: 'center' }}><CategoryDot color={c.color} /></span>, c.name))}
        </div>
      )}
      {menu && (
        <Menu open onOpenChange={(_, d) => !d.open && setMenu(null)}
          positioning={{ target: { getBoundingClientRect: () => ({ x: menu.x, y: menu.y, top: menu.y, left: menu.x, bottom: menu.y, right: menu.x, width: 0, height: 0 }) } as any }}>
          <MenuPopover>
            <MenuList>
              <MenuItem onClick={() => { setEdit(menu.f); setMenu(null) }}>{t('Bearbeiten …')}</MenuItem>
              <MenuItem onClick={() => {
                const f = menu.f
                setMenu(null)
                if (confirm(t('Suchordner „{name}“ löschen? Die Mails bleiben, wo sie sind.', { name: f.name }))) save(list.filter(x => x.id !== f.id))
              }}>{t('Löschen')}</MenuItem>
            </MenuList>
          </MenuPopover>
        </Menu>
      )}
      {edit && (
        <SearchFolderDialog folder={edit === 'new' ? null : edit} accounts={accounts} folders={folders}
          onClose={result => {
            setEdit(null)
            if (!result) return
            const next = edit === 'new' ? [...list, result] : list.map(x => x.id === result.id ? result : x)
            // Die id vergibt der Server - den neuen Ordner in der gespeicherten Liste suchen
            save(next).then(saved => {
              const created = edit === 'new' && saved?.find(x => !list.some(o => o.id === x.id))
              if (created) open('sf:' + created.id)
            })
          }} />
      )}
    </>
  )
}

const periods: [string, string][] = [['', 'Jederzeit'], ['7', 'Letzte 7 Tage'], ['30', 'Letzte 30 Tage'], ['90', 'Letzte 3 Monate'], ['365', 'Letztes Jahr'], ['custom', 'Zeitraum wählen …']]

function SearchFolderDialog({ folder, accounts, folders, onClose }: {
  folder: SearchFolder | null; accounts: Account[]; folders: Record<string, Folder[]>; onClose: (f: SearchFolder | null) => void
}) {
  const s = useStyles()
  const categories = useCategories()
  const [name, setName] = useState(folder?.name ?? '')
  const [color, setColor] = useState(folder?.color ?? palette[0])
  const [c, setC] = useState<MailCriteria>(folder?.criteria ?? {})
  const [preview, setPreview] = useState<{ total: number; unread: number } | null>(null)
  const set = (p: Partial<MailCriteria>) => setC(x => ({ ...x, ...p }))
  const period = c.from || c.to ? 'custom' : c.days ? String(c.days) : ''
  const [customPeriod, setCustomPeriod] = useState(period === 'custom')

  // Vorschau: wie viele Mails passen
  useEffect(() => {
    const timer = setTimeout(() => api.post('/api/mail/query', c).then(r => setPreview({ total: r.total, unread: r.unread })).catch(() => setPreview(null)), 300)
    return () => clearTimeout(timer)
  }, [c])

  const folderOptions = accounts.flatMap(a => (folders[a.id] ?? []).filter(f => f.selectable && f.special !== 'flagged')
    .map(f => ({ key: a.id + '|' + f.path, label: (accounts.length > 1 ? (a.name || a.email) + ' › ' : '') + folderLabel(f) })))
  const chosenFolders = c.folders ?? []
  const chosenCats = c.categories ?? []

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose(null)}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{folder ? t('Suchordner bearbeiten') : t('Neuer Suchordner')}</DialogTitle>
          <DialogContent>
            <form className={s.form} onSubmit={e => { e.preventDefault() }}>
              <Field label={t('Name')} required><Input value={name} onChange={(_, d) => setName(d.value)} autoFocus /></Field>
              <Field label={t('Farbe')}>
                <div className={s.colors}>
                  {palette.map(p => <button key={p} type="button" className={mergeClasses(s.color, p === color && s.colorOn)} style={{ background: p }} onClick={() => setColor(p)} aria-label={p} />)}
                </div>
              </Field>
              <Field label={t('Suchbegriffe')} hint={t('Wie in der Suche, auch von:, an:, betreff:. Leer: alle Mails, die zu den übrigen Angaben passen.')}>
                <Input value={c.text ?? ''} onChange={(_, d) => set({ text: d.value })} placeholder={t('z. B. rechnung von:telekom')} />
              </Field>
              <div className={s.row}>
                <Field label={t('Ordner')} style={{ flex: 1, minWidth: 220 }}>
                  <Dropdown multiselect placeholder={t('Alle Ordner')} selectedOptions={chosenFolders}
                    value={chosenFolders.length ? folderOptions.filter(o => chosenFolders.includes(o.key)).map(o => o.label).join(', ') : ''}
                    onOptionSelect={(_, d) => set({ folders: d.selectedOptions })}>
                    {folderOptions.map(o => <Option key={o.key} value={o.key}>{o.label}</Option>)}
                  </Dropdown>
                </Field>
                <Field label={t('Kategorien')} style={{ flex: 1, minWidth: 180 }}>
                  <Dropdown multiselect placeholder={t('Beliebig')} selectedOptions={chosenCats}
                    value={categories.filter(x => chosenCats.includes(x.key)).map(x => x.name).join(', ')}
                    onOptionSelect={(_, d) => set({ categories: d.selectedOptions })}>
                    {categories.map(x => <Option key={x.key} value={x.key} text={x.name}><CategoryDot color={x.color} /> {x.name}</Option>)}
                  </Dropdown>
                </Field>
              </div>
              <div className={s.row}>
                <Field label={t('Zeitraum')} style={{ minWidth: 200 }}>
                  <Dropdown value={t(periods.find(p => p[0] === (customPeriod ? 'custom' : period))?.[1] ?? 'Jederzeit')}
                    selectedOptions={[customPeriod ? 'custom' : period]}
                    onOptionSelect={(_, d) => {
                      const v = d.optionValue ?? ''
                      setCustomPeriod(v === 'custom')
                      set(v === 'custom' ? { days: null } : { days: v ? Number(v) : null, from: null, to: null })
                    }}>
                    {periods.map(([k, l]) => <Option key={k} value={k}>{t(l)}</Option>)}
                  </Dropdown>
                </Field>
                {customPeriod && <>
                  <Field label={t('Von')}><Input type="date" value={c.from ?? ''} onChange={(_, d) => set({ from: d.value || null })} /></Field>
                  <Field label={t('Bis')}><Input type="date" value={c.to ?? ''} onChange={(_, d) => set({ to: d.value || null })} /></Field>
                </>}
              </div>
              <div className={s.row}>
                <Checkbox label={t('Nur ungelesene')} checked={!!c.unread} onChange={(_, d) => set({ unread: !!d.checked })} />
                <Checkbox label={t('Nur markierte')} checked={!!c.flagged} onChange={(_, d) => set({ flagged: !!d.checked })} />
                <Checkbox label={t('Mit Anhang')} checked={!!c.attachments} onChange={(_, d) => set({ attachments: !!d.checked })} />
                <Checkbox label={t('Auch Papierkorb und Junk')} checked={!!c.includeDeleted} onChange={(_, d) => set({ includeDeleted: !!d.checked })} />
              </div>
              {preview && (
                <Text size={200} className={s.preview}>
                  {preview.total === 1 ? t('1 Mail passt') : t('{n} Mails passen', { n: preview.total.toLocaleString(locale) })}
                  {preview.unread > 0 ? ' · ' + t('{n} ungelesen', { n: preview.unread.toLocaleString(locale) }) : ''}
                </Text>
              )}
            </form>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(null)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!name.trim()}
              onClick={() => onClose({ id: folder?.id ?? '', name: name.trim(), color, criteria: c })}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Name eines virtuellen Ordners fuer die Titelzeile der Liste. */
export function useVirtualName(folder: string | undefined): string | null {
  const categories = useCategories()
  const [list, setList] = useState<SearchFolder[]>([])
  const isSearch = !!folder?.startsWith('sf:')
  useEffect(() => { if (isSearch) api.get<SearchFolder[]>('/api/settings/search-folders').then(setList).catch(() => {}) }, [isSearch, folder])
  if (!folder) return null
  if (folder.startsWith('sf:')) return list.find(f => 'sf:' + f.id === folder)?.name ?? t('Suchordner')
  if (folder.startsWith('cat:')) return categories.find(c => 'cat:' + c.key === folder)?.name ?? t('Kategorie')
  return null
}
