// Oeffentlicher Link (/s/{token}): ansehen, herunterladen (einzeln oder als ZIP), in Upload-Ordner hochladen.
// Ohne Anmeldung und ohne den verschluesselten Umschlag - nur /api/public/{token}.
import {
  Breadcrumb, BreadcrumbButton, BreadcrumbDivider, BreadcrumbItem, Button, Field, Input, MessageBar, MessageBarBody, ProgressBar, Spinner,
  Table, TableBody, TableCell, TableCellLayout, TableHeader, TableHeaderCell, TableRow, Text, Title3, makeStyles, mergeClasses, tokens,
} from '@fluentui/react-components'
import { AppsList20Regular, ArrowDownload20Regular, ArrowUpload20Regular, Grid20Regular, Home20Regular, LockClosed20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { fmtSize, q } from '../api'
import { locale, t, tError } from '../i18n'
import { useIsMobile } from '../shell/mobile'
import { Item, KindIcon, Thumb, extOf, fmtWhen, hasThumb, publicSource, viewKind } from './common'
import { Viewer } from './Viewer'
import { OfficeEditor, OfficeSession } from './Office'

const useStyles = makeStyles({
  page: { height: '100%', display: 'flex', flexDirection: 'column', backgroundColor: tokens.colorNeutralBackground2, color: tokens.colorNeutralForeground1 },
  top: {
    display: 'flex', alignItems: 'center', gap: '12px', padding: '10px 20px', backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`, flexWrap: 'wrap', '@media (max-width: 767px)': { padding: '8px 12px' },
  },
  brand: { display: 'flex', alignItems: 'center', gap: '8px', fontWeight: 600 },
  title: { flex: 1, minWidth: '160px', display: 'flex', flexDirection: 'column' },
  sub: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
  body: { flex: 1, overflowY: 'auto', padding: '16px 20px', '@media (max-width: 767px)': { padding: '8px' } },
  card: {
    maxWidth: '960px', margin: '0 auto', backgroundColor: tokens.colorNeutralBackground1, borderRadius: tokens.borderRadiusXLarge,
    boxShadow: tokens.shadow4, padding: '16px', display: 'flex', flexDirection: 'column', gap: '12px',
  },
  center: { maxWidth: '380px', margin: '10vh auto 0', alignItems: 'stretch' },
  bar: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  tiles: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))', gap: '8px' },
  tile: {
    display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '6px', padding: '8px', borderRadius: tokens.borderRadiusMedium, cursor: 'pointer',
    ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  thumb: { width: '100%', aspectRatio: '4 / 3', display: 'flex', alignItems: 'center', justifyContent: 'center', overflow: 'hidden', borderRadius: tokens.borderRadiusSmall },
  thumbImg: { width: '100%', height: '100%' },
  name: { fontSize: '12px', textAlign: 'center', wordBreak: 'break-word', maxHeight: '32px', overflow: 'hidden', lineHeight: '16px' },
  drop: {
    border: `2px dashed ${tokens.colorNeutralStroke1}`, borderRadius: tokens.borderRadiusLarge, padding: '24px', textAlign: 'center',
    display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '8px',
  },
  dropOver: { border: `2px dashed ${tokens.colorBrandStroke1}`, backgroundColor: tokens.colorBrandBackground2 },
  single: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '12px', padding: '16px' },
  singleThumb: { maxWidth: '100%', maxHeight: '50vh' },
  row: { cursor: 'pointer' },
})

interface Info {
  name: string; owner: string; mode: 'read' | 'upload' | 'drop'; expires: string | null; isDir: boolean; path: string; item: Item; items: Item[]
  /** Endungen fuer den Office-Betrachter (nur bei "nur ansehen" und eingestelltem Office-Server) */
  office?: string[]
}

export default function PublicShare({ token }: { token: string }) {
  const s = useStyles()
  const mobile = useIsMobile()
  const source = useMemo(() => publicSource(token), [token])
  const base = `/api/public/${encodeURIComponent(token)}`
  const [path, setPath] = useState('')
  const [info, setInfo] = useState<Info | null>(null)
  const [state, setState] = useState<'load' | 'ok' | 'password' | 'error'>('load')
  const [error, setError] = useState('')
  const [pw, setPw] = useState('')
  const [pwError, setPwError] = useState('')
  const [tiles, setTiles] = useState(true)
  const [viewer, setViewer] = useState<number | null>(null)
  const [officeItem, setOfficeItem] = useState<Item | null>(null)
  const [over, setOver] = useState(false)
  const [progress, setProgress] = useState<{ done: number; total: number; pct: number } | null>(null)
  const [uploaded, setUploaded] = useState<string[]>([])
  const input = useRef<HTMLInputElement>(null)

  const load = useCallback(async (p: string) => {
    const r = await fetch(`${base}?` + q({ path: p }), { credentials: 'same-origin' })
    const d = await r.json().catch(() => ({}))
    if (r.status === 401 && d.needsPassword) { setState('password'); setInfo(i => i ?? ({ name: d.name } as Info)); return }
    if (!r.ok) { setError(d.error || `${r.status}`); setState('error'); return }
    setInfo(d); setState('ok')
  }, [base])
  useEffect(() => { load(path) }, [path, load])
  useEffect(() => { document.title = (info?.name ? info.name + ' – ' : '') + 'dmOffice' }, [info?.name])

  const unlock = async (e: React.FormEvent) => {
    e.preventDefault()
    setPwError('')
    const r = await fetch(`${base}/auth`, { method: 'POST', credentials: 'same-origin', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ password: pw }) })
    if (r.ok) { setPw(''); load(path) } else setPwError((await r.json().catch(() => ({}))).error || t('Falsches Passwort.'))
  }

  const download = (i: Item) => {
    const a = document.createElement('a')
    a.href = i.isDir ? `${base}/zip?` + q({ path: i.path }) : source.download(i)
    a.click()
  }
  const files = info?.items?.filter(i => !i.isDir && viewKind(i)) ?? []
  const inOffice = (i: Item) => !i.isDir && !viewKind(i) && !!info?.office?.includes(extOf(i.name).slice(1))
  const openOffice = async (i: Item): Promise<OfficeSession> => {
    const r = await fetch(`${base}/office?` + q({ path: i.path }), { method: 'POST', credentials: 'same-origin' })
    const d = await r.json().catch(() => ({}))
    if (!r.ok) throw new Error(d.error || `${r.status}`)
    return d
  }
  const open = (i: Item) => {
    if (i.isDir) { setPath(i.path); return }
    if (inOffice(i)) { setOfficeItem(i); return }
    const n = files.findIndex(f => f.path === i.path)
    if (n >= 0) setViewer(n); else download(i)
  }

  const send = async (list: File[]) => {
    if (!list.length) return
    let done = 0
    setProgress({ done: 0, total: list.length, pct: 0 })
    const names: string[] = []
    for (const f of list) {
      const form = new FormData()
      form.append('files', f, (f as any).webkitRelativePath || f.name)
      const ok = await new Promise<boolean>(res => {
        const x = new XMLHttpRequest()
        x.open('POST', `${base}/upload?` + q({ path: info?.mode === 'drop' ? '' : path }))
        x.withCredentials = true
        x.upload.onprogress = e => setProgress(p => p && { ...p, pct: e.total ? e.loaded / e.total : 0 })
        x.onload = () => {
          if (x.status >= 200 && x.status < 300) { try { names.push(...JSON.parse(x.responseText).files) } catch { /* egal */ } res(true) }
          else { try { setError(JSON.parse(x.responseText).error) } catch { setError(`${x.status}`) } res(false) }
        }
        x.onerror = () => { setError(t('Verbindung unterbrochen')); res(false) }
        x.send(form)
      })
      if (!ok) break
      done++
      setProgress({ done, total: list.length, pct: 0 })
    }
    setProgress(null)
    setUploaded(u => [...names, ...u])
    if (info?.mode !== 'drop') load(path)
  }

  const header = (
    <div className={s.top}>
      <div className={s.brand}><img src="/favicon.svg" width={24} height={24} alt="" />dmOffice</div>
      {info?.name && <div className={s.title}>
        <Text weight="semibold" truncate wrap={false}>{info.name}</Text>
        {info.owner && <span className={s.sub}>{t('Geteilt von {name}', { name: info.owner })}{info.expires ? ' · ' + t('verfügbar bis {date}', { date: new Date(info.expires).toLocaleDateString(locale) }) : ''}</span>}
      </div>}
      {state === 'ok' && info && info.mode !== 'drop' && (
        <Button appearance="primary" icon={<ArrowDownload20Regular />} onClick={() => download(info.isDir ? { ...info.item, path: '' } : info.item)}>
          {info.isDir ? (mobile ? t('ZIP') : t('Alles herunterladen (ZIP)')) : t('Herunterladen')}
        </Button>
      )}
    </div>
  )

  if (state === 'load') return <div className={s.page}>{header}<div className={s.body}><Spinner /></div></div>
  if (state === 'error') return (
    <div className={s.page}>{header}
      <div className={s.body}><div className={mergeClasses(s.card, s.center)}>
        <Title3>{t('Link nicht verfügbar')}</Title3><Text>{tError(error)}</Text>
      </div></div>
    </div>
  )
  if (state === 'password') return (
    <div className={s.page}>{header}
      <div className={s.body}>
        <form className={mergeClasses(s.card, s.center)} onSubmit={unlock}>
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}><LockClosed20Regular /><Title3>{t('Passwort erforderlich')}</Title3></div>
          <Text>{t('„{name}“ ist mit einem Passwort geschützt.', { name: info?.name ?? '' })}</Text>
          <Field label={t('Passwort')} validationMessage={pwError || undefined}>
            <Input type="password" value={pw} onChange={(_, d) => setPw(d.value)} autoFocus autoComplete="current-password" />
          </Field>
          <Button appearance="primary" type="submit" disabled={!pw}>{t('Öffnen')}</Button>
        </form>
      </div>
    </div>
  )
  const i = info!
  const parts = path ? path.split('/') : []
  const canUpload = i.isDir && (i.mode === 'upload' || i.mode === 'drop')

  const uploadBox = canUpload && (
    <div className={mergeClasses(s.drop, over && s.dropOver)}
      onDragOver={e => { if (e.dataTransfer.types.includes('Files')) { e.preventDefault(); setOver(true) } }}
      onDragLeave={() => setOver(false)}
      onDrop={e => { e.preventDefault(); setOver(false); send(Array.from(e.dataTransfer.files)) }}>
      <ArrowUpload20Regular style={{ width: 32, height: 32 }} />
      <Text>{i.mode === 'drop' ? t('Dateien für {name} hier ablegen – du siehst nur, was du selbst hochlädst.', { name: i.owner }) : t('Dateien hierher ziehen oder auswählen')}</Text>
      <input ref={input} type="file" multiple hidden onChange={e => { if (e.target.files) send(Array.from(e.target.files)); e.target.value = '' }} />
      <Button appearance="primary" icon={<ArrowUpload20Regular />} onClick={() => input.current?.click()} disabled={!!progress}>{t('Dateien auswählen')}</Button>
      {progress && <div style={{ width: '100%' }}>
        <Text size={200}>{t('Lade hoch: {done} von {total}', { done: progress.done, total: progress.total })}</Text>
        <ProgressBar value={(progress.done + progress.pct) / progress.total} />
      </div>}
      {uploaded.length > 0 && <MessageBar intent="success"><MessageBarBody>{t('Hochgeladen: {names}', { names: uploaded.join(', ') })}</MessageBarBody></MessageBar>}
      {error && state === 'ok' && <MessageBar intent="error"><MessageBarBody>{tError(error)}</MessageBarBody></MessageBar>}
    </div>
  )

  return (
    <div className={s.page}>
      {header}
      <div className={s.body}>
        <div className={s.card}>
          {!i.isDir ? (
            <div className={s.single}>
              {hasThumb(i.item) ? <Thumb item={i.item} source={source} size={1600} fit="contain" className={s.singleThumb} fallbackLarge /> : <KindIcon item={i.item} large />}
              <Text size={500} weight="semibold">{i.item.name}</Text>
              <Text className={s.sub}>{fmtSize(i.item.size)} · {fmtWhen(i.item.modified, locale)}</Text>
              <div className={s.bar}>
                {viewKind(i.item) && <Button onClick={() => setViewer(0)}>{t('Ansehen')}</Button>}
                {inOffice(i.item) && <Button onClick={() => setOfficeItem(i.item)}>{t('Ansehen')}</Button>}
                <Button appearance="primary" icon={<ArrowDownload20Regular />} onClick={() => download(i.item)}>{t('Herunterladen')}</Button>
              </div>
            </div>
          ) : <>
            {i.mode !== 'drop' && (
              <div className={s.bar}>
                <div style={{ flex: 1, minWidth: 0, overflowX: 'auto' }}>
                  <Breadcrumb>
                    <BreadcrumbItem><BreadcrumbButton icon={<Home20Regular />} onClick={() => setPath('')} current={!parts.length}>{i.name}</BreadcrumbButton></BreadcrumbItem>
                    {parts.map((p, n) => [
                      <BreadcrumbDivider key={'d' + n} />,
                      <BreadcrumbItem key={n}><BreadcrumbButton onClick={() => setPath(parts.slice(0, n + 1).join('/'))} current={n === parts.length - 1}>{p}</BreadcrumbButton></BreadcrumbItem>,
                    ])}
                  </Breadcrumb>
                </div>
                <Button appearance={tiles ? 'subtle' : 'primary'} icon={<AppsList20Regular />} onClick={() => setTiles(false)} aria-label={t('Liste')} />
                <Button appearance={tiles ? 'primary' : 'subtle'} icon={<Grid20Regular />} onClick={() => setTiles(true)} aria-label={t('Kacheln')} />
              </div>
            )}
            {uploadBox}
            {i.mode !== 'drop' && (i.items.length === 0 ? <Text className={s.sub}>{t('Dieser Ordner ist leer.')}</Text> : tiles ? (
              <div className={s.tiles}>
                {i.items.map(x => (
                  <div key={x.path} className={s.tile} onClick={() => open(x)} title={x.name}>
                    <div className={s.thumb}>{hasThumb(x) ? <Thumb item={x} source={source} className={s.thumbImg} fallbackLarge /> : <KindIcon item={x} large />}</div>
                    <span className={s.name}>{x.name}</span>
                  </div>
                ))}
              </div>
            ) : (
              <Table size="small">
                <TableHeader><TableRow>
                  <TableHeaderCell>{t('Name')}</TableHeaderCell>
                  {!mobile && <TableHeaderCell style={{ width: 160 }}>{t('Geändert')}</TableHeaderCell>}
                  {!mobile && <TableHeaderCell style={{ width: 100 }}>{t('Größe')}</TableHeaderCell>}
                  <TableHeaderCell style={{ width: 44 }} />
                </TableRow></TableHeader>
                <TableBody>
                  {i.items.map(x => (
                    <TableRow key={x.path} className={s.row} onClick={() => open(x)}>
                      <TableCell><TableCellLayout media={<KindIcon item={x} />}
                        description={mobile ? [fmtWhen(x.modified, locale), x.isDir ? '' : fmtSize(x.size)].filter(Boolean).join(' · ') : undefined}>{x.name}</TableCellLayout></TableCell>
                      {!mobile && <TableCell>{fmtWhen(x.modified, locale)}</TableCell>}
                      {!mobile && <TableCell>{x.isDir ? '' : fmtSize(x.size)}</TableCell>}
                      <TableCell><Button size="small" appearance="subtle" icon={<ArrowDownload20Regular />} aria-label={t('Herunterladen')}
                        onClick={e => { e.stopPropagation(); download(x) }} /></TableCell>
                    </TableRow>
                  ))}
                </TableBody>
              </Table>
            ))}
          </>}
        </div>
      </div>
      {officeItem && <OfficeEditor key={officeItem.path} name={officeItem.name} open={() => openOffice(officeItem)} onClose={() => { setOfficeItem(null); load(path) }} />}
      {viewer !== null && (
        <Viewer items={i.isDir ? files : [i.item]} index={viewer} source={source} onIndex={setViewer} onClose={() => setViewer(null)} onDownload={download} />
      )}
    </div>
  )
}
