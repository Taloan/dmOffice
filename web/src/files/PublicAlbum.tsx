// Öffentliches Album (/a/{token}): Fotos ansehen ohne Anmeldung - Raster, Betrachter, wenn erlaubt Herunterladen.
import { Button, Input, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components'
import { ArrowDownload20Regular, ChevronLeft24Regular, ChevronRight24Regular, Dismiss24Regular, LockClosed20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useRef, useState } from 'react'
import { locale, t } from '../i18n'

interface Photo { k: string; name: string; mime: string | null; etag: string; taken: number; width?: number | null; height?: number | null; title?: string | null }
interface Page { name: string; owner: string; expires: string | null; download: boolean; total: number; items: Photo[] }

const useStyles = makeStyles({
  root: { minHeight: '100%', backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground1 },
  head: { padding: '20px 16px 8px', maxWidth: '1400px', margin: '0 auto' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(150px, 1fr))', gap: '4px', padding: '8px 16px 32px', maxWidth: '1400px', margin: '0 auto' },
  cell: { aspectRatio: '1', overflow: 'hidden', borderRadius: '6px', cursor: 'pointer', backgroundColor: tokens.colorNeutralBackground3, border: 'none', padding: 0 },
  img: { width: '100%', height: '100%', objectFit: 'cover', display: 'block' },
  center: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px', padding: '80px 16px', textAlign: 'center' },
  viewer: { position: 'fixed', inset: 0, backgroundColor: 'rgba(0,0,0,.94)', zIndex: 10, display: 'flex', alignItems: 'center', justifyContent: 'center' },
  big: { maxWidth: '100vw', maxHeight: '100vh', objectFit: 'contain' },
  bar: { position: 'absolute', top: 0, left: 0, right: 0, display: 'flex', alignItems: 'center', gap: '8px', padding: '8px 12px', color: '#fff', background: 'linear-gradient(rgba(0,0,0,.6), transparent)' },
  nav: { position: 'absolute', top: '50%', transform: 'translateY(-50%)', color: '#fff' },
})

export default function PublicAlbum({ token }: { token: string }) {
  const s = useStyles()
  const base = `/api/public-album/${token}`
  const [page, setPage] = useState<Page | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [locked, setLocked] = useState<string | null>(null)
  const [password, setPassword] = useState('')
  const [open, setOpen] = useState<number | null>(null)
  const [more, setMore] = useState(false)
  const end = useRef<HTMLDivElement>(null)

  const load = useCallback(async (offset: number) => {
    const r = await fetch(`${base}/?offset=${offset}`, { credentials: 'same-origin' })
    const d = await r.json().catch(() => null)
    if (r.status === 401 && d?.needsPassword) { setLocked(d.name ?? ''); return }
    if (!r.ok) { setError(d?.error ?? t('Diesen Link gibt es nicht (mehr).')); return }
    setLocked(null)
    setPage(p => offset === 0 || !p ? d : { ...d, items: [...p.items, ...d.items] })
  }, [base])
  useEffect(() => { load(0) }, [load])
  useEffect(() => { document.title = page ? page.name : 'dmOffice' }, [page])

  // Weitere Fotos, sobald das Ende des Rasters ins Bild kommt
  useEffect(() => {
    const el = end.current
    if (!el || !page || page.items.length >= page.total) return
    const io = new IntersectionObserver(async e => {
      if (!e[0].isIntersecting || more) return
      setMore(true); try { await load(page.items.length) } finally { setMore(false) }
    }, { rootMargin: '800px' })
    io.observe(el)
    return () => io.disconnect()
  }, [page, more, load])

  // Betrachter: Pfeiltasten, Esc, Wischen
  useEffect(() => {
    if (open == null || !page) return
    const on = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(null)
      else if (e.key === 'ArrowRight') setOpen(i => i == null ? i : Math.min(page.items.length - 1, i + 1))
      else if (e.key === 'ArrowLeft') setOpen(i => i == null ? i : Math.max(0, i - 1))
    }
    window.addEventListener('keydown', on)
    return () => window.removeEventListener('keydown', on)
  }, [open, page])
  const x0 = useRef<number | null>(null)

  const unlock = async () => {
    const r = await fetch(`${base}/auth`, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password }) })
    if (!r.ok) { const d = await r.json().catch(() => null); setError(d?.error ?? t('Falsches Passwort.')); return }
    setError(null); setPassword(''); load(0)
  }

  const thumb = (p: Photo, size: number) => `${base}/thumb?k=${encodeURIComponent(p.k)}&size=${size}&v=${encodeURIComponent(p.etag)}`
  const isVideo = (p: Photo) => (p.mime ?? '').startsWith('video/')

  if (locked != null) return (
    <div className={s.root}><div className={s.center}>
      <LockClosed20Regular />
      <Text size={500} weight="semibold">{locked}</Text>
      <Text>{t('Dieses Album ist mit einem Passwort geschützt.')}</Text>
      <Input type="password" value={password} onChange={(_, d) => setPassword(d.value)} onKeyDown={e => { if (e.key === 'Enter') unlock() }} placeholder={t('Passwort')} autoFocus />
      {error && <Text style={{ color: tokens.colorPaletteRedForeground1 }}>{error}</Text>}
      <Button appearance="primary" onClick={unlock}>{t('Öffnen')}</Button>
    </div></div>
  )
  if (error && !page) return <div className={s.root}><div className={s.center}><Text size={500}>{error}</Text></div></div>
  if (!page) return <div className={s.root}><div className={s.center}><Spinner /></div></div>

  const cur = open != null ? page.items[open] : null
  return (
    <div className={s.root}>
      <div className={s.head}>
        <Text as="h1" size={700} weight="semibold" block>{page.name}</Text>
        <Text size={300} style={{ color: tokens.colorNeutralForeground3 }}>
          {[page.owner && t('von {name}', { name: page.owner }), t('{n} Fotos und Videos', { n: page.total.toLocaleString() }),
            page.expires && t('sichtbar bis {date}', { date: new Date(page.expires).toLocaleDateString(locale) })].filter(Boolean).join(' · ')}
        </Text>
      </div>
      <div className={s.grid}>
        {page.items.map((p, i) => (
          <button key={p.k} className={s.cell} onClick={() => setOpen(i)} title={p.title || p.name} aria-label={p.title || p.name}>
            <img className={s.img} src={thumb(p, 256)} alt="" loading="lazy" />
          </button>
        ))}
      </div>
      <div ref={end} style={{ height: 1 }} />
      {more && <Spinner style={{ margin: '0 0 24px' }} />}

      {cur && (
        <div className={s.viewer}
          onPointerDown={e => { x0.current = e.clientX }}
          onPointerUp={e => { if (x0.current != null && Math.abs(e.clientX - x0.current) > 50) setOpen(i => i == null ? i : Math.min(page.items.length - 1, Math.max(0, i + (e.clientX < x0.current! ? 1 : -1)))); x0.current = null }}>
          {isVideo(cur)
            ? <video key={cur.k} className={s.big} src={`${base}/file?k=${encodeURIComponent(cur.k)}`} controls autoPlay />
            : <img key={cur.k} className={s.big} src={thumb(cur, 2048)} alt={cur.title || cur.name} />}
          <div className={s.bar}>
            <Text style={{ color: '#fff', flex: 1, minWidth: 0 }} truncate wrap={false}>
              {cur.title || cur.name} · {new Date(cur.taken).toLocaleString(locale, { dateStyle: 'medium', timeStyle: 'short' })}
            </Text>
            {page.download && <Button appearance="transparent" style={{ color: '#fff' }} icon={<ArrowDownload20Regular />} as="a"
              href={`${base}/file?k=${encodeURIComponent(cur.k)}&download=true`} aria-label={t('Herunterladen')} />}
            <Button appearance="transparent" style={{ color: '#fff' }} icon={<Dismiss24Regular />} onClick={() => setOpen(null)} aria-label={t('Schließen')} />
          </div>
          {open! > 0 && <Button className={s.nav} style={{ left: 8 }} appearance="transparent" icon={<ChevronLeft24Regular />} onClick={() => setOpen(open! - 1)} aria-label={t('Zurück')} />}
          {open! < page.items.length - 1 && <Button className={s.nav} style={{ right: 8 }} appearance="transparent" icon={<ChevronRight24Regular />} onClick={() => setOpen(open! + 1)} aria-label={t('Weiter')} />}
        </div>
      )}
    </div>
  )
}
