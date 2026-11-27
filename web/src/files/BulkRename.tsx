import { Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Tab, TabList, Text, tokens } from '@fluentui/react-components'
import { useMemo, useState } from 'react'
import { api } from '../api'
import { t } from '../i18n'
import { Item, join } from './common'

/**
 * Mehrere Dateien und Ordner auf einmal umbenennen (Kennung bulkrename): nach Muster mit Platzhaltern oder per Suchen und Ersetzen.
 * Eine Vorschau zeigt alt → neu; doppelte oder unzulässige Namen sind rot und sperren „Umbenennen“. Rückgängig in einem Schritt.
 */
type Mode = 'pattern' | 'replace'
const BAD = /[\\/:*?"<>|]/
const split = (name: string, dir: boolean) => {
  const i = name.lastIndexOf('.')
  return dir || i <= 0 ? { base: name, ext: '' } : { base: name.slice(0, i), ext: name.slice(i + 1) }
}

export function BulkRename({ items, dir, siblings, onClose, onDone }: {
  items: Item[]; dir: string; siblings: string[]
  onClose: () => void
  onDone: (moves: { from: string; to: string }[]) => void
}) {
  const [mode, setMode] = useState<Mode>('pattern')
  const [pattern, setPattern] = useState('{name}')
  const [start, setStart] = useState(1)
  const [find, setFind] = useState('')
  const [repl, setRepl] = useState('')
  const [regex, setRegex] = useState(false)
  const [caseSens, setCaseSens] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const rows = useMemo(() => {
    let rx: RegExp | null = null
    let rxError: string | null = null
    if (mode === 'replace' && find) {
      try { rx = new RegExp(regex ? find : find.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'), caseSens ? 'g' : 'gi') } catch { rxError = t('Ungültiger regulärer Ausdruck') }
    }
    const out = items.map((it, idx) => {
      let name = it.name
      if (mode === 'pattern') {
        const { base, ext } = split(it.name, it.isDir)
        const d = it.modified ? new Date(it.modified) : null
        const date = d && !isNaN(+d) ? `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}` : ''
        name = pattern
          .replace(/\{n(?::(\d+))?\}/g, (_, w) => String(start + idx).padStart(w ? Number(w) : 0, '0'))
          .replace(/\{name\}/g, base).replace(/\{date\}/g, date).replace(/\{ext\}/g, ext)
        // Ohne {ext} im Muster bleibt die Endung erhalten
        if (ext && !/\{ext\}/.test(pattern)) name += '.' + ext
      } else if (rx) name = it.name.replace(rx, repl)
      return { it, name: name.trim() }
    })
    const others = new Set(siblings.filter(s => !items.some(i => i.name === s)).map(s => s.toLowerCase()))
    const count = new Map<string, number>()
    out.forEach(r => count.set(r.name.toLowerCase(), (count.get(r.name.toLowerCase()) ?? 0) + 1))
    return {
      rxError,
      list: out.map(r => ({
        ...r,
        problem: !r.name ? t('Leer') : BAD.test(r.name) ? t('Unzulässiges Zeichen') : r.name === '.' || r.name === '..' ? t('Unzulässiger Name')
          : (count.get(r.name.toLowerCase()) ?? 0) > 1 ? t('Doppelt') : others.has(r.name.toLowerCase()) ? t('Gibt es schon') : null,
      })),
    }
  }, [items, siblings, mode, pattern, start, find, repl, regex, caseSens])

  const changes = rows.list.filter(r => r.name !== r.it.name)
  const blocked = !!rows.rxError || rows.list.some(r => r.problem) || !changes.length

  const run = async () => {
    setBusy(true); setError(null)
    const done: { from: string; to: string }[] = []
    try {
      // Ziel ist der alte Name einer anderen Datei (etwa beim Durchnummerieren): erst auf Zwischennamen, dann auf den neuen Namen
      const olds = new Set(items.map(i => i.name.toLowerCase()))
      const twoStep = changes.some(r => olds.has(r.name.toLowerCase()))
      const steps = changes.map((r, i) => ({ from: r.it.path, mid: join(dir, `~rename-${Date.now()}-${i}`), to: join(dir, r.name) }))
      if (twoStep) {
        for (const s of steps) { await api.post('/api/files/move', { from: s.from, to: s.mid, copy: false }); done.push({ from: s.from, to: s.mid }) }
        for (const s of steps) { await api.post('/api/files/move', { from: s.mid, to: s.to, copy: false }); done.splice(done.findIndex(d => d.to === s.mid), 1, { from: s.from, to: s.to }) }
      } else for (const s of steps) { await api.post('/api/files/move', { from: s.from, to: s.to, copy: false }); done.push({ from: s.from, to: s.to }) }
      onDone(done)
    } catch (e) {
      setError(t('Umbenennen abgebrochen: {n} von {total} umbenannt. {msg}', { n: done.length, total: changes.length, msg: e instanceof Error ? e.message : String(e) }))
      if (done.length) onDone(done)
    } finally { setBusy(false) }
  }

  const muted = { color: tokens.colorNeutralForeground3 }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open && !busy) onClose() }}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <DialogBody>
          <DialogTitle>{t('{n} Einträge umbenennen', { n: items.length })}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <TabList selectedValue={mode} onTabSelect={(_, d) => setMode(d.value as Mode)}>
              <Tab value="pattern">{t('Muster')}</Tab>
              <Tab value="replace">{t('Suchen und ersetzen')}</Tab>
            </TabList>
            {mode === 'pattern' ? <div style={{ display: 'flex', gap: 8, alignItems: 'flex-end', flexWrap: 'wrap' }}>
              <Field label={t('Neuer Name')} style={{ flex: 1, minWidth: 260 }}
                hint={t('{name} alter Name, {n} Nummer, {n:3} Nummer mit drei Stellen, {date} Änderungsdatum, {ext} Endung. Ohne {ext} bleibt die Endung erhalten.')}>
                <Input autoFocus value={pattern} onChange={(_, d) => setPattern(d.value)} />
              </Field>
              <Field label={t('Beginnen bei')}>
                <Input type="number" min={0} value={String(start)} style={{ width: 90 }} onChange={(_, d) => setStart(Math.max(0, Math.round(Number(d.value)) || 0))} />
              </Field>
            </div> : <div style={{ display: 'flex', flexDirection: 'column', gap: 6 }}>
              <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
                <Field label={t('Suchen')} style={{ flex: 1, minWidth: 200 }}><Input autoFocus value={find} onChange={(_, d) => setFind(d.value)} /></Field>
                <Field label={t('Ersetzen durch')} style={{ flex: 1, minWidth: 200 }}><Input value={repl} onChange={(_, d) => setRepl(d.value)} /></Field>
              </div>
              <div style={{ display: 'flex', gap: 12 }}>
                <Checkbox checked={regex} onChange={(_, d) => setRegex(!!d.checked)} label={t('Regulärer Ausdruck')} />
                <Checkbox checked={caseSens} onChange={(_, d) => setCaseSens(!!d.checked)} label={t('Groß-/Kleinschreibung beachten')} />
              </div>
              {rows.rxError && <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{rows.rxError}</Text>}
            </div>}
            <div style={{ maxHeight: 300, overflowY: 'auto', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: 4 }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 13 }}>
                <thead><tr style={{ textAlign: 'left', ...muted }}>
                  <th style={{ padding: '4px 8px', fontWeight: 400 }}>{t('Bisher')}</th><th style={{ padding: '4px 8px', fontWeight: 400 }}>{t('Neu')}</th>
                </tr></thead>
                <tbody>{rows.list.map(r => (
                  <tr key={r.it.path} style={{ borderTop: `1px solid ${tokens.colorNeutralStroke3}` }}>
                    <td style={{ padding: '3px 8px', wordBreak: 'break-all' }}>{r.it.name}</td>
                    <td style={{ padding: '3px 8px', wordBreak: 'break-all', color: r.problem ? tokens.colorPaletteRedForeground1 : r.name === r.it.name ? tokens.colorNeutralForeground3 : undefined }}>
                      {r.name || '–'}{r.problem && <span style={{ marginLeft: 6 }}>({r.problem})</span>}</td>
                  </tr>))}</tbody>
              </table>
            </div>
            <Text size={200} style={muted}>{changes.length === 1 ? t('1 Name ändert sich.') : t('{n} Namen ändern sich.', { n: changes.length })}</Text>
            {error && <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>}
          </DialogContent>
          <DialogActions>
            <Button disabled={busy} onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={blocked || busy} onClick={run}>{busy ? t('Benenne um …') : t('Umbenennen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
