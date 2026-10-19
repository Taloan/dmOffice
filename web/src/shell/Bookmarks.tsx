import { Button, Menu, MenuItem, MenuList, MenuPopover, Tooltip, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { Add20Regular, Delete20Regular, Rename20Regular, ArrowUp20Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { t } from '../i18n'
import { usePrefs } from '../prefs'

export const MAX_BOOKMARKS = 5

const useStyles = makeStyles({
  line: { width: '28px', height: '1px', backgroundColor: tokens.colorNeutralStroke2, margin: '6px 0' },
  zone: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '4px', borderRadius: tokens.borderRadiusLarge, padding: '2px 0' },
  over: { outline: `2px dashed ${tokens.colorBrandStroke1}`, outlineOffset: '1px' },
  mark: {
    width: '36px', height: '36px', minWidth: '36px', borderRadius: tokens.borderRadiusLarge, padding: 0, fontWeight: 600, fontSize: '15px',
    color: tokens.colorBrandForeground1, backgroundColor: tokens.colorNeutralBackground1,
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover, color: tokens.colorBrandForeground1 },
  },
  add: { width: '36px', height: '28px', minWidth: '36px', color: tokens.colorNeutralForeground3 },
})

/** Nur Web-Adressen - kein javascript: oder data: aus einem gezogenen Link. */
function cleanUrl(raw: string): string | null {
  const text = raw.trim().split(/\r?\n/).find(l => l && !l.startsWith('#')) ?? ''
  try {
    const u = new URL(/^[a-z]+:\/\//i.test(text) ? text : 'https://' + text)
    return u.protocol === 'https:' || u.protocol === 'http:' ? u.href : null
  } catch { return null }
}

const nameOf = (url: string) => { try { return new URL(url).hostname.replace(/^www\./, '') } catch { return url } }

/**
 * Eigene Lesezeichen unten in der Leiste (hoechstens fünf): einen Link hierher ziehen oder "+".
 * Rechtsklick: umbenennen, nach oben, entfernen. Gespeichert in den Einstellungen - auf jedem Geraet gleich.
 */
export function Bookmarks() {
  const s = useStyles()
  const { prefs, update } = usePrefs()
  const list = prefs.bookmarks ?? []
  const [over, setOver] = useState(false)
  const [menu, setMenu] = useState<{ i: number; x: number; y: number } | null>(null)
  const save = (next: { name: string; url: string }[]) => update({ bookmarks: next.slice(0, MAX_BOOKMARKS) })

  const add = (url: string, name?: string) => {
    if (list.length >= MAX_BOOKMARKS) { alert(t('Höchstens {n} Lesezeichen – erst eines entfernen.', { n: MAX_BOOKMARKS })); return }
    if (list.some(b => b.url === url)) return
    save([...list, { url, name: (name?.trim() || nameOf(url)).slice(0, 60) }])
  }
  const ask = () => {
    const raw = prompt(t('Adresse des Lesezeichens:'), 'https://')
    const url = raw ? cleanUrl(raw) : null
    if (!url) return
    add(url, prompt(t('Name:'), nameOf(url)) ?? undefined)
  }

  return (
    <div className={mergeClasses(s.zone, over && s.over)}
      onDragOver={e => { if (e.dataTransfer.types.includes('text/uri-list') || e.dataTransfer.types.includes('text/plain')) { e.preventDefault(); setOver(true) } }}
      onDragLeave={() => setOver(false)}
      onDrop={e => {
        e.preventDefault()
        setOver(false)
        const url = cleanUrl(e.dataTransfer.getData('text/uri-list') || e.dataTransfer.getData('text/plain'))
        if (url) {
          // Beim Ziehen aus dem Browser steht der Linktext oft in text/html
          const html = e.dataTransfer.getData('text/html')
          const text = html ? new DOMParser().parseFromString(html, 'text/html').body.textContent?.trim() : ''
          add(url, text && text !== url ? text : undefined)
        }
      }}>
      <div className={s.line} />
      {list.map((b, i) => (
        <Tooltip key={b.url} content={`${b.name} – ${nameOf(b.url)}`} relationship="label" positioning="after">
          <Button appearance="subtle" className={s.mark} aria-label={b.name}
            onClick={() => window.open(b.url, '_blank', 'noopener,noreferrer')}
            onContextMenu={e => { e.preventDefault(); setMenu({ i, x: e.clientX, y: e.clientY }) }}>
            {(b.name.match(/[\p{L}\p{N}]/u)?.[0] ?? '•').toUpperCase()}
          </Button>
        </Tooltip>
      ))}
      {list.length < MAX_BOOKMARKS && (
        <Tooltip content={t('Lesezeichen hinzufügen – oder einen Link hierher ziehen')} relationship="label" positioning="after">
          <Button appearance="transparent" className={s.add} icon={<Add20Regular />} onClick={ask} aria-label={t('Lesezeichen hinzufügen')} />
        </Tooltip>
      )}
      {menu && (
        <Menu open onOpenChange={(_, d) => !d.open && setMenu(null)} positioning={{ target: { getBoundingClientRect: () => new DOMRect(menu.x, menu.y, 0, 0) } }}>
          <MenuPopover>
            <MenuList>
              <MenuItem icon={<Rename20Regular />} onClick={() => {
                const name = prompt(t('Name:'), list[menu.i].name)
                if (name?.trim()) save(list.map((b, j) => j === menu.i ? { ...b, name: name.trim().slice(0, 60) } : b))
              }}>{t('Umbenennen')}</MenuItem>
              {menu.i > 0 && <MenuItem icon={<ArrowUp20Regular />} onClick={() => {
                const next = [...list];
                [next[menu.i - 1], next[menu.i]] = [next[menu.i], next[menu.i - 1]]
                save(next)
              }}>{t('Nach oben')}</MenuItem>}
              <MenuItem icon={<Delete20Regular />} onClick={() => save(list.filter((_, j) => j !== menu.i))}>{t('Entfernen')}</MenuItem>
            </MenuList>
          </MenuPopover>
        </Menu>
      )}
    </div>
  )
}
