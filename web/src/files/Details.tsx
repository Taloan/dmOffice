// Detailbereich rechts (Handy: Vollbild): Vorschau, Eigenschaften, Freigaben, fruehere Versionen.
import { Button, Divider, Spinner, Text, Tooltip, makeStyles, tokens } from '@fluentui/react-components'
import {
  ArrowDownload20Regular, Dismiss20Regular, History20Regular, Link20Regular, Open20Regular, People20Regular, Share20Regular, Star20Filled, Star20Regular,
} from '@fluentui/react-icons'
import { useCallback, useEffect, useState } from 'react'
import { api, fmtSize, q } from '../api'
import { locale, t } from '../i18n'
import { Item, KindIcon, Thumb, hasThumb, kindLabel, kindOf, ownSource, parentOf } from './common'
import { Link, copyText, linkStatus } from './Dialogs'

/** Datum lesbar - ohne gueltiges Datum ein Strich statt "Invalid Date" */
const when = (v?: string) => { const d = v ? new Date(v) : null; return d && !isNaN(d.getTime()) ? d.toLocaleString(locale) : '—' }

const useStyles = makeStyles({
  root: {
    width: '320px', flexShrink: 0, borderLeft: `1px solid ${tokens.colorNeutralStroke2}`, display: 'flex', flexDirection: 'column', minHeight: 0,
    backgroundColor: tokens.colorNeutralBackground1,
    '@media (max-width: 767px)': { position: 'absolute', inset: 0, width: 'auto', zIndex: 16, borderLeft: 'none' },
  },
  head: { display: 'flex', alignItems: 'center', gap: '8px', padding: '10px 8px 6px 16px' },
  title: { flex: 1, minWidth: 0, fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  body: { flex: 1, overflowY: 'auto', padding: '0 16px 16px', display: 'flex', flexDirection: 'column', gap: '12px' },
  preview: {
    height: '180px', display: 'flex', alignItems: 'center', justifyContent: 'center', borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3, overflow: 'hidden',
  },
  previewImg: { maxWidth: '100%', maxHeight: '100%' },
  actions: { display: 'flex', gap: '4px', flexWrap: 'wrap' },
  props: { display: 'grid', gridTemplateColumns: 'auto 1fr', columnGap: '12px', rowGap: '6px', fontSize: '13px' },
  key: { color: tokens.colorNeutralForeground3 },
  val: { wordBreak: 'break-word' },
  section: { fontWeight: 600, fontSize: '13px' },
  version: { display: 'flex', alignItems: 'center', gap: '4px', fontSize: '13px' },
  muted: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
  hash: {
    fontFamily: tokens.fontFamilyMonospace, fontSize: '11px', color: tokens.colorNeutralForeground3, wordBreak: 'break-all', cursor: 'copy',
    background: 'none', border: 'none', padding: 0, textAlign: 'left',
    ':hover': { color: tokens.colorNeutralForeground1 },
  },
})

interface Info {
  item: Item; size: number; files: number; folders: number; sha256?: string | null
  versions: { id: number; size: number; modified: string; saved: string }[]
  links: Link[]
  /** Mit wem ein eigener Ordner geteilt ist */
  people?: { id: string; login: string; displayName: string; readOnly: boolean }[]
  /** In einem mit mir geteilten Ordner: wem er gehoert und ob ich schreiben darf */
  owner?: string | null; canWrite?: boolean
}

export function Details({ item, selection, onClose, onOpen, onOpenFolder, onShare, onFavorite, onDownload, version, fail, notify, onChanged }: {
  item: Item | null; selection: Item[]; onClose: () => void; onOpen: (i: Item) => void; onOpenFolder: (path: string) => void
  onShare: (i: Item) => void; onFavorite: (i: Item, on: boolean) => void; onDownload: (list: Item[]) => void
  version: number; fail: (e: unknown) => void; notify: (m: string) => void; onChanged: () => void
}) {
  const s = useStyles()
  const [info, setInfo] = useState<Info | null>(null)
  const load = useCallback(() => {
    if (!item) { setInfo(null); return }
    api.get<Info>('/api/files/info?' + q({ path: item.path })).then(setInfo).catch(() => setInfo(null))
  }, [item?.path, item?.etag]) // eslint-disable-line react-hooks/exhaustive-deps
  useEffect(load, [load, version])

  const restore = async (id: number) => {
    if (!item || !confirm(t('Diese Version wiederherstellen? Der jetzige Inhalt bleibt als Version erhalten.'))) return
    try { await api.post('/api/files/versions/restore', { path: item.path, id }); notify(t('Version wiederhergestellt')); onChanged(); load() } catch (e) { fail(e) }
  }

  if (!item) {
    const total = selection.reduce((n, i) => n + (i.isDir ? 0 : i.size), 0)
    return (
      <aside className={s.root}>
        <div className={s.head}><span className={s.title}>{t('Details')}</span><Button appearance="subtle" icon={<Dismiss20Regular />} onClick={onClose} aria-label={t('Schließen')} /></div>
        <div className={s.body}>
          <Text>{selection.length ? t('{n} Elemente ausgewählt', { n: selection.length }) : t('Wähle eine Datei oder einen Ordner, um Details zu sehen.')}</Text>
          {selection.length > 0 && <Text className={s.muted}>{t('Dateien zusammen: {size}', { size: fmtSize(total) })}</Text>}
          {selection.length > 0 && <Button icon={<ArrowDownload20Regular />} onClick={() => onDownload(selection)}>{t('Herunterladen')}</Button>}
        </div>
      </aside>
    )
  }
  const i = info?.item ?? item
  const kind = kindOf(i)
  return (
    <aside className={s.root}>
      <div className={s.head}>
        <KindIcon item={i} />
        <span className={s.title} title={i.name}>{i.name || t('Meine Dateien')}</span>
        <Button appearance="subtle" icon={<Dismiss20Regular />} onClick={onClose} aria-label={t('Schließen')} />
      </div>
      <div className={s.body}>
        <div className={s.preview}>
          {hasThumb(i) ? <Thumb item={i} source={ownSource} size={1600} fit="contain" className={s.previewImg} fallbackLarge /> : <KindIcon item={i} large />}
        </div>
        <div className={s.actions}>
          <Button size="small" icon={<Open20Regular />} onClick={() => onOpen(i)}>{t('Öffnen')}</Button>
          {!i.isDir && <Button size="small" icon={<ArrowDownload20Regular />} onClick={() => onDownload([i])}>{t('Herunterladen')}</Button>}
          {i.path && !info?.owner && <Button size="small" icon={<Share20Regular />} onClick={() => onShare(i)}>{t('Teilen')}</Button>}
          {i.path && !info?.owner && <Tooltip content={i.favorite ? t('Aus Favoriten entfernen') : t('Zu Favoriten')} relationship="label">
            <Button size="small" icon={i.favorite ? <Star20Filled color="#E8A33D" /> : <Star20Regular />}
              onClick={async () => { await onFavorite(i, !i.favorite); load() }} />
          </Tooltip>}
        </div>
        <Divider style={{ flexGrow: 0 }} />
        <div className={s.props}>
          <span className={s.key}>{t('Art')}</span><span className={s.val}>{kindLabel(kind)}{i.mime && !i.isDir ? ` (${i.mime})` : ''}</span>
          <span className={s.key}>{t('Größe')}</span>
          <span className={s.val}>{info ? fmtSize(info.size) : <Spinner size="extra-tiny" />}
            {info && i.isDir && <span className={s.muted}> · {t('{f} Dateien, {d} Ordner', { f: info.files, d: info.folders })}</span>}</span>
          <span className={s.key}>{t('Geändert')}</span><span className={s.val}>{when(info?.item.modified ?? i.modified)}</span>
          {i.created && <><span className={s.key}>{t('Erstellt')}</span><span className={s.val}>{new Date(i.created).toLocaleString(locale)}</span></>}
          {info?.owner && <><span className={s.key}>{t('Besitzer')}</span><span className={s.val}>{info.owner} · {info.canWrite ? t('du darfst bearbeiten') : t('nur lesen')}</span></>}
          {i.path && <><span className={s.key}>{t('Ort')}</span>
            <span className={s.val}><a href="#" onClick={e => { e.preventDefault(); onOpenFolder(parentOf(i.path)) }}>{'/' + parentOf(i.path)}</a></span></>}
          {info && !i.isDir && <><span className={s.key}>SHA-256</span>
            {info.sha256
              ? <Tooltip content={t('Prüfsumme des Inhalts – klicken zum Kopieren')} relationship="description">
                <button type="button" className={s.hash} onClick={async () => { if (await copyText(info.sha256!)) notify(t('Prüfsumme kopiert')) }}>{info.sha256}</button>
              </Tooltip>
              : <span className={s.muted}>{t('wird noch berechnet')}</span>}</>}
        </div>
        {info?.people && info.people.length > 0 && <>
          <Divider style={{ flexGrow: 0 }} />
          <span className={s.section}><People20Regular style={{ verticalAlign: 'middle' }} /> {t('Geteilt mit {n} Person(en)', { n: info.people.length })}</span>
          {info.people.map(p => <span key={p.id} className={s.muted}>{p.displayName} · {p.readOnly ? t('Lesen') : t('Bearbeiten')}</span>)}
          <Button size="small" onClick={() => onShare(i)}>{t('Freigabe verwalten')}</Button>
        </>}
        {info && info.links.length > 0 && <>
          <Divider style={{ flexGrow: 0 }} />
          <span className={s.section}><Link20Regular style={{ verticalAlign: 'middle' }} /> {t('Freigegeben über {n} Link(s)', { n: info.links.length })}</span>
          {info.links.map(l => (
            <span key={l.id} className={s.muted}>{linkStatus(l.mode)}{l.expires ? ' · ' + t('bis {date}', { date: new Date(l.expires).toLocaleDateString(locale) }) : ''}{l.hasPassword ? ' · ' + t('mit Passwort') : ''}</span>
          ))}
          <Button size="small" onClick={() => onShare(i)}>{t('Links verwalten')}</Button>
        </>}
        {info && !i.isDir && <>
          <Divider style={{ flexGrow: 0 }} />
          <span className={s.section}><History20Regular style={{ verticalAlign: 'middle' }} /> {t('Frühere Versionen')}</span>
          {info.versions.length === 0 ? <span className={s.muted}>{t('Noch keine. Beim Überschreiben bleibt der alte Inhalt hier {days} Tage erhalten.', { days: 30 })}</span>
            : info.versions.map(v => (
              <div key={v.id} className={s.version}>
                <span style={{ flex: 1 }}>{new Date(v.modified).toLocaleString(locale)}<br /><span className={s.muted}>{fmtSize(v.size)}</span></span>
                <Tooltip content={t('Herunterladen')} relationship="label">
                  <Button size="small" appearance="subtle" icon={<ArrowDownload20Regular />} as="a"
                    href={'/api/files/download?' + q({ path: i.path, version: v.id })} />
                </Tooltip>
                {info.canWrite !== false && <Button size="small" onClick={() => restore(v.id)}>{t('Wiederherstellen')}</Button>}
              </div>
            ))}
        </>}
      </div>
    </aside>
  )
}
