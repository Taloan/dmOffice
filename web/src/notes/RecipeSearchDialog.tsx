import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Link, Option, SearchBox, Spinner, Text,
  makeStyles, tokens,
} from '@fluentui/react-components'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'

interface Hit {
  url: string; name: string; source: string; image: string | null; yield: string; time: string; ingredients: string[]
  servings: number; kcal: number | null; protein: number | null; fat: number | null; carbs: number | null; unmatched: number
}
export interface FolderTarget { id: string; label: string; depth: number }

const useStyles = makeStyles({
  list: { display: 'flex', flexDirection: 'column', gap: '6px', maxHeight: '58vh', overflowY: 'auto', marginTop: '8px' },
  row: {
    display: 'grid', gridTemplateColumns: '96px minmax(0, 1.2fr) minmax(0, 1.4fr) 120px 32px', gap: '12px', alignItems: 'start', padding: '8px',
    borderRadius: tokens.borderRadiusMedium, border: `1px solid ${tokens.colorNeutralStroke2}`,
    '@media (max-width: 720px)': { gridTemplateColumns: '72px minmax(0, 1fr) 32px', '& > .ing, & > .nut': { display: 'none' } },
  },
  img: { width: '96px', height: '72px', objectFit: 'cover', borderRadius: tokens.borderRadiusMedium, backgroundColor: tokens.colorNeutralBackground3, '@media (max-width: 720px)': { width: '72px', height: '56px' } },
  muted: { color: tokens.colorNeutralForeground3, fontSize: tokens.fontSizeBase200 },
  ing: { fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground2, display: '-webkit-box', WebkitLineClamp: 4, WebkitBoxOrient: 'vertical', overflow: 'hidden' },
})

/**
 * Rezepte im Internet suchen und als Notiz (Rezeptvorlage, Tag „recipe“) übernehmen: Treffer mit Bild, Zutaten und grob
 * geschätzten Nährwerten je Portion; ankreuzen, Zielordner wählen, importieren.
 */
export function RecipeSearchDialog({ targets, folder, dbName, onClose }: {
  targets: FolderTarget[]; folder: string | null; dbName: string | null; onClose: (imported: number) => void
}) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [text, setText] = useState('')
  const [aiAvailable, setAiAvailable] = useState(false)
  const [ai, setAi] = useState(true)
  const [hits, setHits] = useState<Hit[] | null>(null)
  const [busy, setBusy] = useState(false)
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const [target, setTarget] = useState<string>(folder ?? '')
  const [importing, setImporting] = useState(false)
  useEffect(() => { api.get<{ ai: boolean }>('/api/notes/recipes/search/state').then(r => setAiAvailable(r.ai)).catch(() => {}) }, [])
  const search = async () => {
    if (text.trim().length < 2) return
    setBusy(true); setHits(null); setPicked(new Set())
    try { setHits((await api.get<{ hits: Hit[] }>('/api/notes/recipes/search?' + q({ q: text.trim(), ai: aiAvailable && ai }))).hits) }
    catch (e) { fail(e); setHits([]) } finally { setBusy(false) }
  }
  const toggle = (url: string) => setPicked(p => { const n = new Set(p); if (n.has(url)) n.delete(url); else n.add(url); return n })
  const run = async () => {
    setImporting(true)
    let n = 0
    for (const url of picked) {
      try { await api.post('/api/notes/recipes/import', { url, folder: target || null }); n++ } catch (e) { fail(e) }
    }
    setImporting(false)
    if (n) notify(t('{n} Rezepte übernommen', { n }))
    onClose(n)
  }
  const targetLabel = target ? targets.find(x => x.id === target)?.label ?? '' : t('Ohne Ordner')
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(0) }}>
      <DialogSurface style={{ maxWidth: 980, width: '96vw' }}>
        <DialogBody>
          <DialogTitle>{t('Rezepte suchen')}</DialogTitle>
          <DialogContent>
            <div style={{ display: 'flex', gap: 8, alignItems: 'center', flexWrap: 'wrap' }}>
              <SearchBox value={text} placeholder={t('z. B. Rührei, Linsensuppe')} style={{ flex: 1, minWidth: 220 }}
                onChange={(_, d) => setText(d.value)} onKeyDown={e => { if (e.key === 'Enter') search() }} />
              <Button appearance="primary" disabled={busy || text.trim().length < 2} onClick={search}>{t('Suchen')}</Button>
              {aiAvailable && <Checkbox checked={ai} onChange={(_, d) => setAi(!!d.checked)} label={t('Auch mit KI suchen')} />}
            </div>
            <Text block className={s.muted} style={{ marginTop: 4 }}>
              {t('Der Suchbegriff geht an DuckDuckGo{ai}; dmOffice liest die Rezepte von den gefundenen Seiten. Nährwerte sind grob geschätzt.', { ai: aiAvailable && ai ? t(' und an die eingerichtete KI') : '' })}
            </Text>
            {busy && <Spinner style={{ marginTop: 16 }} label={t('Sucht und liest die Rezepte …')} />}
            {hits && !busy && hits.length === 0 && <Text block style={{ marginTop: 16 }}>{t('Keine Rezepte gefunden – anderer Begriff?')}</Text>}
            {hits && hits.length > 0 && (
              <div className={s.list}>
                {hits.map(h => (
                  <div key={h.url} className={s.row} style={picked.has(h.url) ? { borderColor: tokens.colorBrandStroke1 } : undefined}>
                    {h.image ? <img src={'/api/notes/recipes/search/image?' + q({ u: h.image })} alt="" className={s.img} loading="lazy" /> : <div className={s.img} />}
                    <div style={{ minWidth: 0 }}>
                      <Text weight="semibold" block>{h.name}</Text>
                      <Text block className={s.muted}>{[h.source, h.time, h.yield].filter(Boolean).join(' · ')}</Text>
                      <Link href={h.url} target="_blank" rel="noreferrer" style={{ fontSize: tokens.fontSizeBase200 }}>{t('Seite öffnen')}</Link>
                    </div>
                    <div className={`ing ${s.ing}`}>{h.ingredients.join(' · ')}</div>
                    <div className="nut" style={{ fontSize: tokens.fontSizeBase200 }}>
                      {h.kcal != null ? <>
                        <Text weight="semibold" block>{t('~{kcal} kcal', { kcal: Math.round(h.kcal) })}</Text>
                        <span className={s.muted}>{t('je Portion ({n})', { n: h.servings })}</span><br />
                        <span className={s.muted}>E {Math.round(h.protein ?? 0)} · F {Math.round(h.fat ?? 0)} · KH {Math.round(h.carbs ?? 0)} g</span>
                      </> : <span className={s.muted}>{t('Nährwerte unbekannt')}</span>}
                    </div>
                    <Checkbox checked={picked.has(h.url)} onChange={() => toggle(h.url)} aria-label={t('Auswählen')} />
                  </div>
                ))}
              </div>
            )}
          </DialogContent>
          <DialogActions style={{ flexWrap: 'wrap' }}>
            <Text className={s.muted} style={{ marginRight: 'auto' }}>{dbName ? t('Datenbank: {name}', { name: dbName }) : ''}</Text>
            <Dropdown value={targetLabel} selectedOptions={[target]} onOptionSelect={(_, d) => setTarget(d.optionValue ?? '')} style={{ minWidth: 200 }} aria-label={t('Zielordner')}>
              <Option value="">{t('Ohne Ordner')}</Option>
              {targets.map(f => <Option key={f.id} value={f.id} text={f.label}><span style={{ paddingLeft: f.depth * 12 }}>{f.label}</span></Option>)}
            </Dropdown>
            <Button onClick={() => onClose(0)}>{t('Schließen')}</Button>
            <Button appearance="primary" disabled={!picked.size || importing} onClick={run}>
              {importing ? <Spinner size="tiny" /> : t('Importieren ({n})', { n: picked.size })}
            </Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
