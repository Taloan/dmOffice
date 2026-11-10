import {
  Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, MessageBar, MessageBarBody, SearchBox, Spinner, Text,
  makeStyles, tokens,
} from '@fluentui/react-components'
import { Document20Regular, DocumentPdf20Regular, Image20Regular, Open16Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { t } from '../i18n'
import { useDebounced } from '../shell/hooks'
import { Snippet } from '../shell/Snippet'

interface Hit { path: string; name: string; size: number; mime: string | null; modified: string; method: string; snippet: string }

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '2px', maxHeight: '55vh', overflowY: 'auto', marginTop: '10px' },
  hit: {
    display: 'flex', gap: '10px', padding: '8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer', alignItems: 'flex-start',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  body: { display: 'flex', flexDirection: 'column', minWidth: 0, flex: 1, gap: '2px' },
  meta: { color: tokens.colorNeutralForeground3, fontSize: '12px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  snippet: { fontSize: '12px', color: tokens.colorNeutralForeground2, overflowWrap: 'anywhere' },
})

const parentOf = (p: string) => (p.includes('/') ? p.slice(0, p.lastIndexOf('/')) : '')

/** Inhaltssuche der Dateiablage (Volltextsuche, Einstellungen -> Suchen): Treffer mit Ordner und Ausschnitt.
 *  Ein Klick oeffnet den Ordner der Datei, das Symbol daneben die Datei selbst. */
export function ContentSearch({ under, onGo, onClose }: { under: string; onGo: (folder: string) => void; onClose: () => void }) {
  const s = useStyles()
  const [text, setText] = useState('')
  const query = useDebounced(text, 300)
  const [hits, setHits] = useState<Hit[] | null>(null)
  const [enabled, setEnabled] = useState(true)
  const [error, setError] = useState('')
  const [onlyHere, setOnlyHere] = useState(false)

  useEffect(() => {
    if (!query.trim()) { setHits(null); return }
    let stale = false
    setHits(null)
    api.get<{ enabled: boolean; items: Hit[] }>('/api/files/search/content?' + q({ q: query, path: onlyHere ? under : '' }))
      .then(r => { if (!stale) { setHits(r.items); setEnabled(r.enabled); setError('') } })
      .catch(e => { if (!stale) { setHits([]); setError(e instanceof Error ? e.message : String(e)) } })
    return () => { stale = true }
  }, [query, onlyHere, under])

  const icon = (h: Hit) => h.mime?.startsWith('image/') ? <Image20Regular /> : h.mime === 'application/pdf' || h.name.toLowerCase().endsWith('.pdf') ? <DocumentPdf20Regular /> : <Document20Regular />

  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 720 }}>
        <DialogBody>
          <DialogTitle>{t('Inhalte durchsuchen')}</DialogTitle>
          <DialogContent>
            <SearchBox autoFocus placeholder={t('Wörter im Text von PDFs, Dokumenten und Scans')} value={text} onChange={(_, d) => setText(d.value)} style={{ width: '100%', maxWidth: 'none' }} />
            {under && (
              <div style={{ marginTop: 6 }}>
                <Button size="small" appearance={onlyHere ? 'primary' : 'secondary'} onClick={() => setOnlyHere(v => !v)}>
                  {onlyHere ? t('Nur in „{folder}“', { folder: under }) : t('Überall suchen')}
                </Button>
              </div>
            )}
            {!enabled && (
              <MessageBar intent="info" style={{ marginTop: 10 }}><MessageBarBody>
                {t('Die Inhaltssuche ist aus.')} <a href="#/settings/search">{t('Unter Einstellungen → Suchen einschalten')}</a>
              </MessageBarBody></MessageBar>
            )}
            {error && <MessageBar intent="error" style={{ marginTop: 10 }}><MessageBarBody>{error}</MessageBarBody></MessageBar>}
            <div className={s.list}>
              {query.trim() && hits === null && <Spinner size="tiny" />}
              {hits?.length === 0 && enabled && !error && <Text style={{ padding: 8 }}>{t('Keine Datei enthält diese Wörter.')}</Text>}
              {hits?.map(h => (
                <div key={h.path} className={s.hit} onClick={() => { onGo(parentOf(h.path)); onClose() }} title={h.path}>
                  {icon(h)}
                  <div className={s.body}>
                    <Text weight="semibold">{h.name}</Text>
                    <span className={s.meta}>{(parentOf(h.path) || t('Dateien')) + ' · ' + fmtSize(h.size) + (h.method === 'ocr' ? ' · ' + t('Texterkennung') : '')}</span>
                    <span className={s.snippet}><Snippet text={h.snippet} /></span>
                  </div>
                  <Button appearance="subtle" size="small" icon={<Open16Regular />} aria-label={t('Öffnen')} title={t('Öffnen')}
                    onClick={e => { e.stopPropagation(); window.open('/api/files/download?' + q({ path: h.path, inline: true }), '_blank') }} />
                </div>
              ))}
            </div>
          </DialogContent>
          <DialogActions><Button onClick={onClose}>{t('Schließen')}</Button></DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
