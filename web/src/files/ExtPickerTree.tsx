// Externe Ordner als Ziel im Dialog „… verschieben/kopieren nach“: je Ordner ein Baum, der seine Unterordner erst beim
// Aufklappen holt. Das gewählte Ziel heißt "xmnt:<Kennung>:<Pfad>" (Pfad im externen Ordner, "" = seine Wurzel).
import { makeStyles, mergeClasses, Spinner, tokens } from '@fluentui/react-components'
import { ChevronDown16Regular, ChevronRight16Regular, Cloud20Regular, Folder20Regular, HardDrive20Regular } from '@fluentui/react-icons'
import { useState } from 'react'
import { api, q } from '../api'
import type { ExternalMount } from './ExternalView'

export const EXT_TARGET = 'xmnt:'
export const extTarget = (mount: string, path: string) => `${EXT_TARGET}${mount}:${path}`
export function parseExtTarget(t: string): { mount: string; path: string } | null {
  if (!t.startsWith(EXT_TARGET)) return null
  const rest = t.slice(EXT_TARGET.length), at = rest.indexOf(':')
  return at < 0 ? null : { mount: rest.slice(0, at), path: rest.slice(at + 1) }
}

const useStyles = makeStyles({
  row: { display: 'flex', alignItems: 'center', gap: '6px', padding: '3px 6px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover } },
  active: { backgroundColor: tokens.colorNeutralBackground1Selected },
  twisty: { width: '16px', display: 'inline-flex', flexShrink: 0 },
  name: { overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
})

function Node({ mount, path, name, depth, current, onOpen, icon }: {
  mount: string; path: string; name: string; depth: number; current: string; onOpen: (t: string) => void; icon?: JSX.Element
}) {
  const s = useStyles()
  const [open, setOpen] = useState(false)
  const [children, setChildren] = useState<string[] | null>(null)
  const toggle = async (e: React.MouseEvent) => {
    e.stopPropagation()
    if (!open && children == null) {
      try {
        const r = await api.get<{ items: { name: string; isDir: boolean }[] }>('/api/files/external/list?' + q({ mount, path }))
        setChildren(r.items.filter(i => i.isDir).map(i => i.name).sort((a, b) => a.localeCompare(b)))
      } catch { setChildren([]) }
    }
    setOpen(o => !o)
  }
  const me = extTarget(mount, path)
  return (
    <>
      <div className={mergeClasses(s.row, current === me && s.active)} style={{ paddingLeft: 6 + depth * 16 }} onClick={() => onOpen(me)} onDoubleClick={toggle}>
        <span className={s.twisty} onClick={toggle}>{open ? <ChevronDown16Regular /> : <ChevronRight16Regular />}</span>
        {icon ?? <Folder20Regular />}<span className={s.name}>{name}</span>
      </div>
      {open && (children == null ? <Spinner size="extra-tiny" /> : children.map(c => (
        <Node key={c} mount={mount} path={path ? path + '/' + c : c} name={c} depth={depth + 1} current={current} onOpen={onOpen} />
      )))}
    </>
  )
}

/** Alle beschreibbaren externen Ordner als Ziele. */
export function ExtPickerTree({ mounts, current, onOpen }: { mounts: ExternalMount[]; current: string; onOpen: (t: string) => void }) {
  return (
    <>
      {mounts.filter(m => !m.readOnly && m.available !== false).map(m => (
        <Node key={m.id} mount={m.id} path="" name={m.name} depth={0} current={current} onOpen={onOpen}
          icon={m.kind === 'folder' ? <HardDrive20Regular /> : <Cloud20Regular />} />
      ))}
    </>
  )
}
