// Übertragen zwischen „Meine Dateien“ und externen Ordnern: vorher fragen, wenn Ordner dabei sind oder im Ziel schon etwas gleich
// heißt - mit Quelle, Ziel, Anzahl der Dateien und Ordner und der Größe. Einzelne Dateien ohne Konflikt gehen gleich los.
import { Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Radio, RadioGroup, Spinner, Text, tokens } from '@fluentui/react-components'
import { useCallback, useRef, useState } from 'react'
import { api, fmtSize } from '../api'
import { useApp } from '../App'
import { t, locale } from '../i18n'

type Args = { fromMount: string | null; paths: string[]; toMount: string | null; to: string; move: boolean }
type Preview = { files: number; dirs: number; bytes: number; conflicts: { name: string; folder: boolean }[] }
type Conflict = 'skip' | 'replace' | 'rename'

export function useTransfer() {
  const { fail, notify } = useApp()
  const names = useRef<Record<string, string> | null>(null)
  const [ask, setAsk] = useState<{ args: Args; preview: Preview } | null>(null)
  const [busy, setBusy] = useState(false)

  const run = useCallback(async (args: Args, conflict?: Conflict) => {
    try {
      await api.post('/api/files/external/transfer', { ...args, conflict })
      notify(args.move ? t('Verschieben läuft') : t('Kopieren läuft'), t('Der Fortschritt steht in der Aktivitätsanzeige (oben rechts).'), 'info')
    } catch (e) { fail(e) }
  }, [fail, notify])

  const start = useCallback(async (args: Args) => {
    setBusy(true)
    try {
      if (!names.current) {
        const r = await api.get<{ mounts: { id: string; name: string }[] }>('/api/files/external/mounts').catch(() => ({ mounts: [] }))
        names.current = Object.fromEntries(r.mounts.map(m => [m.id, m.name]))
      }
      const preview = await api.post<Preview>('/api/files/external/transfer/preview', args)
      // Nur Dateien, nichts gleichnamig im Ziel: ohne Rückfrage
      if (preview.dirs === 0 && preview.conflicts.length === 0) { await run(args); return }
      setAsk({ args, preview })
    } catch (e) { fail(e) } finally { setBusy(false) }
  }, [fail, run])

  const where = (mount: string | null, path: string) =>
    [mount ? names.current?.[mount] ?? t('Externer Ordner') : t('Meine Dateien'), ...path.split('/').filter(Boolean)].join(' › ')

  const dialog = (
    <>
      {busy && !ask && <Spinner size="tiny" style={{ position: 'fixed', right: 16, bottom: 16 }} />}
      {ask && <TransferDialog args={ask.args} preview={ask.preview} where={where}
        onClose={() => setAsk(null)} onGo={c => { const a = ask.args; setAsk(null); run(a, c) }} />}
    </>
  )
  return { start, dialog }
}

function TransferDialog({ args, preview, where, onClose, onGo }: {
  args: Args; preview: Preview; where: (mount: string | null, path: string) => string; onClose: () => void; onGo: (c?: Conflict) => void
}) {
  const [conflict, setConflict] = useState<Conflict>('skip')
  const parent = (p: string) => p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : ''
  const what = args.paths.length === 1 ? where(args.fromMount, args.paths[0]) : t('{n} Einträge aus {place}', { n: args.paths.length, place: where(args.fromMount, parent(args.paths[0])) })
  const n = (x: number) => x.toLocaleString(locale)
  const anyFolder = preview.conflicts.some(c => c.folder)
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 520 }}>
        <DialogBody>
          <DialogTitle>{args.move ? t('Verschieben') : t('Kopieren')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            <div style={{ display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '4px 12px' }}>
              <Text weight="semibold">{t('Von')}</Text><Text style={{ wordBreak: 'break-word' }}>{what}</Text>
              <Text weight="semibold">{t('Nach')}</Text><Text style={{ wordBreak: 'break-word' }}>{where(args.toMount, args.to)}</Text>
              <Text weight="semibold">{t('Umfang')}</Text>
              <Text>{t('{files} Dateien in {dirs} Ordnern, {size}', { files: n(preview.files), dirs: n(preview.dirs), size: fmtSize(preview.bytes) })}</Text>
            </div>
            {args.move && <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
              {t('An der Quelle wird endgültig gelöscht, was vollständig angekommen ist – nicht in den Papierkorb.')}
            </Text>}
            {preview.conflicts.length > 0 && <>
              <Text>{t('Im Ziel gibt es schon: {names}', { names: preview.conflicts.map(c => '„' + c.name + '“').join(', ') })}</Text>
              <RadioGroup value={conflict} onChange={(_, d) => setConflict(d.value as Conflict)}>
                <Radio value="skip" label={anyFolder ? t('Zusammenführen, gleichnamige Dateien überspringen') : t('Überspringen')} />
                <Radio value="replace" label={anyFolder ? t('Zusammenführen, gleichnamige Dateien ersetzen') : t('Ersetzen')} />
                <Radio value="rename" label={t('Beide behalten (neuer Name „… (2)“)')} />
              </RadioGroup>
            </>}
          </DialogContent>
          <DialogActions>
            <Button onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" onClick={() => onGo(preview.conflicts.length ? conflict : undefined)}>{args.move ? t('Verschieben') : t('Kopieren')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
