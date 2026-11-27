// Gemeinsame Anzeige für das Hochladen außerhalb der Dateiablage (DMS, Notiz-Anhänge, Archiv, Server-Ordner, Übersicht, Chat):
// Fortschritt in Prozent, Name der Datei, Abbrechen - unter dem Arbeitsbereich, damit nichts verrutscht. Solange etwas hochlädt, fragt der Browser vor dem Verlassen der Seite nach.
import { useEffect, useRef, useState } from 'react'
import { Button, ProgressBar, Text, tokens } from '@fluentui/react-components'
import { t } from '../i18n'
import { useApp } from '../App'
import type { SendProgress } from './chunked'

export function UploadBar({ p, onCancel, style }: { p: SendProgress; onCancel: () => void; style?: React.CSSProperties }) {
  const share = p.total ? p.bytes / p.total : p.count ? p.done / p.count : 0
  return (
    <div style={{ padding: '8px 16px', display: 'flex', flexDirection: 'column', gap: 6, borderTop: `1px solid ${tokens.colorNeutralStroke2}`, flexShrink: 0, ...style }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
        <Text size={200} style={{ flex: 1, minWidth: 0, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {t('Lade hoch: {done} von {total} · {pct} %', { done: p.done, total: Math.max(p.count, 1), pct: Math.round(share * 100) })}
          {p.name && <> · {p.name}</>}
        </Text>
        <Button size="small" onClick={onCancel}>{t('Abbrechen')}</Button>
      </div>
      <ProgressBar value={share} thickness="large" />
    </div>
  )
}

/** Warnung beim Verlassen der Seite, solange active - ein laufender Upload ginge sonst still verloren. */
export function useLeaveWarning(active: boolean) {
  useEffect(() => {
    if (!active) return
    const warn = (e: BeforeUnloadEvent) => { e.preventDefault(); e.returnValue = '' }
    window.addEventListener('beforeunload', warn)
    return () => window.removeEventListener('beforeunload', warn)
  }, [active])
}

/**
 * Ein Upload-Vorgang mit Anzeige: run(fn) startet ihn (fn bekommt Fortschritt und Abbruch-Signal), bar ist die Leiste zum
 * Einblenden. Abgebrochen: Meldung „Hochladen abgebrochen“, run gibt undefined zurück; andere Fehler gehen an den Aufrufer.
 */
export function useUploadTask() {
  const { notify } = useApp()
  const [p, setP] = useState<SendProgress | null>(null)
  const ctl = useRef<AbortController | null>(null)
  useLeaveWarning(!!p)
  const run = async <T,>(fn: (onProgress: (p: SendProgress) => void, signal: AbortSignal) => Promise<T>): Promise<T | undefined> => {
    const c = new AbortController()
    ctl.current = c
    setP({ bytes: 0, total: 0, done: 0, count: 0, name: '' })
    try { return await fn(x => { if (!c.signal.aborted) setP(x) }, c.signal) }
    catch (e) {
      if (c.signal.aborted) { notify(t('Hochladen abgebrochen')); return undefined }
      throw e
    } finally { if (ctl.current === c) { ctl.current = null; setP(null) } }
  }
  const cancel = () => ctl.current?.abort()
  return { run, busy: !!p, progress: p, cancel, bar: p ? <UploadBar p={p} onCancel={cancel} /> : null }
}
