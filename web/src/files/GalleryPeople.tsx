import { Button, Checkbox, Input, Spinner, Text, Tooltip, makeStyles, mergeClasses, tokens } from '@fluentui/react-components'
import { ArrowLeft20Regular, Checkmark16Regular, Delete20Regular, Dismiss16Regular, Edit20Regular, EyeOff16Regular, LockClosed20Regular, PeopleSearch20Regular } from '@fluentui/react-icons'
import { useCallback, useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { Item, Source } from './common'
import { decodeRows } from './GalleryTimeline'
import { TRESOR_EVENT, requestTresorOpen } from '../tresor/tresor'

/** Stand vom Server (Gallery/GalleryFaces.cs) */
interface Overview {
  persons: { id: number; name: string; hidden: boolean; count: number; face: number | null }[]
  /** suggest: benannte Person, der die Gruppe am ehesten gleicht */
  groups: { id: string; count: number; face: number | null; suggest: { id: number; name: string } | null }[]
  /** Anzahl ausgeblendeter Gruppen */
  scanned: number; faces: number; ignored?: number
}
/** tresor: Gesichtsdaten liegen im Tresor - ohne ihn keine Personen, geschlossen keine Übersicht */
/** waiting: die Erkennung wartet, bis die Galerie-Ordner fertig ausgewertet sind */
export interface FacesStatus { available: boolean; enabled: boolean; running: boolean; waiting?: boolean; overview: Overview | null; tresor?: { exists: boolean; open: boolean } }
interface Photos { items: Item[]; faces: Record<string, number[]>; suggestions: { face: number; path: string; sim: number }[] }

const useStyles = makeStyles({
  page: { padding: '12px 16px 24px', display: 'flex', flexDirection: 'column', gap: '16px' },
  head: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
  grid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(104px, 1fr))', gap: '12px' },
  person: { display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '4px', cursor: 'pointer', border: 'none', background: 'none', color: 'inherit', padding: 0, font: 'inherit' },
  face: { width: '96px', height: '96px', borderRadius: '50%', objectFit: 'cover', background: tokens.colorNeutralBackground3 },
  small: { width: '72px', height: '72px', borderRadius: '50%', objectFit: 'cover', background: tokens.colorNeutralBackground3 },
  name: { fontSize: '13px', fontWeight: 600, maxWidth: '104px', overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' },
  count: { fontSize: '12px', color: tokens.colorNeutralForeground3 },
  photos: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(140px, 1fr))', gap: '4px' },
  tile: { position: 'relative', aspectRatio: '1', overflow: 'hidden', borderRadius: tokens.borderRadiusSmall, cursor: 'pointer', background: tokens.colorNeutralBackground3 },
  img: { width: '100%', height: '100%', objectFit: 'cover', display: 'block' },
  remove: { position: 'absolute', top: '4px', right: '4px', minWidth: 0, background: 'rgba(0,0,0,.55)', color: '#fff' },
  pick: { position: 'absolute', top: '2px', left: '2px', borderRadius: tokens.borderRadiusSmall, background: 'rgba(0,0,0,.35)' },
  picked: { outline: `3px solid ${tokens.colorBrandStroke1}`, outlineOffset: '-3px' },
  intro: { maxWidth: '560px', display: 'flex', flexDirection: 'column', gap: '12px', alignItems: 'flex-start' },
  quickGrid: { display: 'grid', gridTemplateColumns: 'repeat(auto-fill, minmax(132px, 1fr))', gap: '16px 12px' },
  quick: { position: 'relative', display: 'flex', flexDirection: 'column', alignItems: 'center', gap: '4px' },
  hide: { position: 'absolute', top: 0, right: '6px', minWidth: 0, borderRadius: '50%', background: 'rgba(0,0,0,.55)', color: '#fff' },
  suggest: { display: 'flex', alignItems: 'center', gap: '2px', maxWidth: '132px' },
  suggestName: { fontSize: '13px', fontWeight: 600, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap', color: tokens.colorBrandForeground1 },
  quickInput: { width: '128px' },
})

/**
 * Wurde der Wert aus der Vorschlagsliste (datalist) gewählt statt getippt? Chrome meldet das als insertReplacementText,
 * Firefox ganz ohne inputType. Nur bekannte Namen zählen.
 */
const fromList = (e: { nativeEvent: Event }, value: string, names: string[]) => {
  const kind = (e.nativeEvent as InputEvent).inputType
  return (kind === undefined || kind === 'insertReplacementText') && names.includes(value.trim())
}
const crop = (id: number | null, size = 192) => id == null ? undefined : `/api/gallery/faces/crop/${id}?size=${size}`

/**
 * Personen in der Galerie: erkannte Gesichter, nach Personen und unbenannten Gruppen. Gruppe antippen und benennen, Person öffnen
 * zeigt ihre Fotos (antippen öffnet den Betrachter), falsch Zugeordnetes lässt sich lösen, Vorschläge bestätigen oder ablehnen.
 */
export default function GalleryPeople({ status, reload, source, onOpen }: {
  status: FacesStatus; reload: () => void; source: Source; onOpen: (list: Item[], index: number) => void
}) {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [sel, setSel] = useState<{ kind: 'person'; id: number; name: string } | { kind: 'group'; id: string } | null>(null)
  const [photos, setPhotos] = useState<Photos | null>(null)
  // Mehrere Fotos der Person markieren und auf einmal entfernen („Ist nicht …“)
  const [picked, setPicked] = useState<Set<string>>(new Set())
  const [name, setName] = useState('')
  const [renaming, setRenaming] = useState(false)
  const [busy, setBusy] = useState(false)
  const [confirmOff, setConfirmOff] = useState(false)
  // Schnell zuordnen in der Übersicht: Name je Gruppe, zuletzt ausgeblendete Gruppe (Rückgängig), Eingabe nach dem Neuladen
  const [quick, setQuick] = useState<Record<string, string>>({})
  const [lastHidden, setLastHidden] = useState<string | null>(null)
  const focusAt = useRef<number | null>(null)
  const ov = status.overview
  useEffect(() => {
    if (focusAt.current == null) return
    const el = document.querySelector<HTMLInputElement>(`[data-quick="${focusAt.current}"]`)
    focusAt.current = null
    el?.focus()
  }, [ov])

  const load = useCallback(async () => {
    if (!sel) { setPhotos(null); return }
    try {
      const r = await api.get<{ items: unknown[]; faces: Record<string, number[]>; suggestions: Photos['suggestions'] }>(
        sel.kind === 'person' ? `/api/gallery/faces/persons/${sel.id}` : `/api/gallery/faces/groups/${encodeURIComponent(sel.id)}`)
      setPhotos({ items: decodeRows(r.items), faces: r.faces, suggestions: r.suggestions })
    } catch (e) { setSel(null); fail(e) }
  }, [sel, fail])
  useEffect(() => { setPhotos(null); setName(''); setRenaming(false); setPicked(new Set()); load() }, [load])

  // again: die gezeigten Fotos neu laden (nicht, wenn f eine andere Person wählt - das lädt der Effekt)
  const act = async (f: () => Promise<unknown>, after?: () => void, again = true) => {
    setBusy(true)
    try { await f(); after?.(); reload(); if (again) await load() } catch (e) { fail(e) } finally { setBusy(false) }
  }

  const toggle = (on: boolean) => act(() => api.put('/api/gallery/faces', { enabled: on }), () => { setConfirmOff(false); setSel(null) }, false)
  // Tresor geöffnet oder geschlossen: Stand neu holen
  useEffect(() => {
    window.addEventListener(TRESOR_EVENT, reload)
    return () => window.removeEventListener(TRESOR_EVENT, reload)
  }, [reload])
  const tresor = status.tresor ?? { exists: true, open: true }

  if (!status.enabled) return (
    <div className={s.page}>
      <div className={s.intro}>
        <Text size={500} weight="semibold">{t('Personen')}</Text>
        <Text>{t('Die Photo-Engine sucht Gesichter in deinen Fotos und fasst ähnliche zu Gruppen zusammen. Du gibst ihnen Namen und findest so alle Fotos einer Person.')}</Text>
        <Text>{t('Gesichtsdaten sind besonders schützenswert. Sie liegen deshalb verschlüsselt in deinem Tresor – nur du kommst an sie heran. Ausschalten löscht sie wieder.')}</Text>
        {tresor.exists
          ? <Button appearance="primary" icon={busy ? <Spinner size="tiny" /> : <PeopleSearch20Regular />} disabled={busy} onClick={() => toggle(true)}>{t('Personen einschalten')}</Button>
          : <Button appearance="primary" icon={<LockClosed20Regular />} onClick={() => { location.hash = '#/settings/tresor' }}>{t('Zuerst den Tresor einrichten')}</Button>}
      </div>
    </div>
  )
  // Eingeschaltet, aber der Tresor ist auf diesem Gerät zu: Personen erst nach dem Öffnen
  if (!tresor.open) return (
    <div className={s.page}>
      <div className={s.intro}>
        <Text size={500} weight="semibold">{t('Personen')}</Text>
        <Text>{t('Die Personen liegen im Tresor. Ansehen und Benennen der Gesichter setzt einen geöffneten Tresor voraus. Neue Fotos werden auch bei geschlossenem Tresor eingelesen.')}</Text>
        <Button appearance="primary" icon={<LockClosed20Regular />} onClick={() => requestTresorOpen().then(ok => { if (ok) reload() })}>{t('Tresor öffnen')}</Button>
      </div>
    </div>
  )

  const names = ov?.persons.map(p => p.name) ?? []
  const nameGroup = (id: string) => act(async () => {
    const r = await api.post<{ person: number }>('/api/gallery/faces/name', { group: id, name })
    notify(t('Benannt: {name}', { name }))
    setSel({ kind: 'person', id: r.person, name: name.trim() })
  }, undefined, false)

  // Fotos einer Person oder Gruppe
  if (sel) {
    const person = sel.kind === 'person' ? sel : null
    return (
      <div className={s.page}>
        <div className={s.head}>
          <Button appearance="subtle" icon={<ArrowLeft20Regular />} onClick={() => setSel(null)}>{t('Personen')}</Button>
          {person && !renaming && <>
            <Text size={500} weight="semibold">{person.name}</Text>
            <Tooltip content={t('Umbenennen')} relationship="label">
              <Button appearance="subtle" icon={<Edit20Regular />} onClick={() => { setName(person.name); setRenaming(true) }} />
            </Tooltip>
            <Tooltip content={t('Person auflösen - die Fotos bleiben, die Gesichter sind wieder ohne Namen')} relationship="label">
              <Button appearance="subtle" icon={<Delete20Regular />} disabled={busy}
                onClick={() => act(() => api.del(`/api/gallery/faces/persons/${person.id}`), () => setSel(null), false)} />
            </Tooltip>
          </>}
          {(renaming || !person) && <form style={{ display: 'flex', gap: 8, alignItems: 'center' }} onSubmit={e => {
            e.preventDefault()
            if (!name.trim()) return
            if (person) act(async () => {
              const r = await api.put<{ person: number }>(`/api/gallery/faces/persons/${person.id}`, { name })
              setRenaming(false); setSel({ kind: 'person', id: r.person, name: name.trim() })
            }, undefined, false)
            else nameGroup(sel.id as string)
          }}>
            <Input autoFocus value={name} placeholder={t('Wer ist das?')} list="faces-names" style={{ minWidth: 220 }}
              onChange={(e, d) => {
                setName(d.value)
                // Aus der Liste gewählt: gleich übernehmen, ohne Enter
                const form = e.currentTarget.form
                if (fromList(e, d.value, names)) setTimeout(() => form?.requestSubmit(), 0)
              }} />
            <datalist id="faces-names">{names.map(n => <option key={n} value={n} />)}</datalist>
            <Button appearance="primary" type="submit" disabled={busy || !name.trim()}>{person ? t('Speichern') : t('Benennen')}</Button>
            {renaming && <Button onClick={() => setRenaming(false)}>{t('Abbrechen')}</Button>}
          </form>}
          {photos && <Text className={s.count}>{t('{n} Fotos', { n: photos.items.length })}</Text>}
          {person && photos && picked.size > 0 && <>
            <Button appearance="primary" icon={<Dismiss16Regular />} disabled={busy}
              onClick={() => act(() => api.post(`/api/gallery/faces/persons/${person.id}/reject`, { faces: [...picked].flatMap(p => photos.faces[p] ?? []) }), () => setPicked(new Set()))}>
              {t('{n} Fotos: ist nicht {name}', { n: picked.size, name: person.name })}</Button>
            <Button appearance="subtle" onClick={() => setPicked(new Set())}>{t('Auswahl aufheben')}</Button>
          </>}
        </div>
        {!photos ? <Spinner /> : <>
          <div className={s.photos}>
            {photos.items.map((i, k) => (
              <div key={i.path} className={mergeClasses(s.tile, picked.has(i.path) && s.picked)}
                onClick={() => picked.size > 0 ? togglePick(i.path) : onOpen(photos.items, k)}>
                <img className={s.img} src={source.thumb(i, 256)} alt={i.name} loading="lazy" draggable={false} />
                {person && <span className={s.pick} onClick={e => { e.stopPropagation(); togglePick(i.path) }}>
                  <Checkbox checked={picked.has(i.path)} aria-label={t('Auswählen')} />
                </span>}
                {person && <Tooltip content={t('Ist nicht {name}', { name: person.name })} relationship="label">
                  <Button size="small" className={s.remove} icon={<Dismiss16Regular />} disabled={busy}
                    onClick={e => { e.stopPropagation(); act(() => api.post(`/api/gallery/faces/persons/${person.id}/reject`, { faces: photos.faces[i.path] ?? [] })) }} />
                </Tooltip>}
              </div>
            ))}
          </div>
          {person && photos.suggestions.length > 0 && <>
            <Text weight="semibold">{t('Ist das auch {name}?', { name: person.name })}</Text>
            <div className={s.grid}>
              {photos.suggestions.map(x => (
                <div key={x.face} className={s.person}>
                  <img className={s.small} src={crop(x.face, 144)} alt="" loading="lazy" />
                  <div style={{ display: 'flex', gap: 4 }}>
                    <Tooltip content={t('Ja')} relationship="label">
                      <Button size="small" icon={<Checkmark16Regular />} disabled={busy}
                        onClick={() => act(() => api.post(`/api/gallery/faces/persons/${person.id}/confirm`, { faces: [x.face] }))} />
                    </Tooltip>
                    <Tooltip content={t('Nein')} relationship="label">
                      <Button size="small" icon={<Dismiss16Regular />} disabled={busy}
                        onClick={() => act(() => api.post(`/api/gallery/faces/persons/${person.id}/reject`, { faces: [x.face] }))} />
                    </Tooltip>
                  </div>
                </div>
              ))}
            </div>
          </>}
        </>}
      </div>
    )
  }

  // Übersicht: Gruppe direkt benennen, Vorschlag übernehmen oder ablehnen, ausblenden
  const togglePick = (path: string) => setPicked(old => { const n = new Set(old); if (!n.delete(path)) n.add(path); return n })
  const quickName = (g: string, index: number, n: string) => act(async () => {
    await api.post('/api/gallery/faces/name', { group: g, name: n })
    notify(t('Benannt: {name}', { name: n.trim() }))
    setQuick(q => { const x = { ...q }; delete x[g]; return x })
    focusAt.current = index
  }, undefined, false)
  const hideGroup = (g: string) => act(() => api.post(`/api/gallery/faces/groups/${encodeURIComponent(g)}/ignore`, {}), () => setLastHidden(g), false)
  const shown = ov?.persons.filter(p => !p.hidden) ?? []
  return (
    <div className={s.page}>
      <div className={s.head}>
        <Text size={500} weight="semibold">{t('Personen')}</Text>
        <Text className={s.count}>{t('{n} Fotos eingelesen', { n: ov?.scanned ?? 0 })}</Text>
        {status.running && <><Spinner size="tiny" /><Text className={s.count}>{t('liest neue Fotos ein …')}</Text></>}
        {status.waiting && <Text className={s.count}>{t('wartet, bis die Galerie-Ordner fertig ausgewertet sind')}</Text>}
        <div style={{ flex: 1 }} />
        {!confirmOff
          ? <Button appearance="subtle" onClick={() => setConfirmOff(true)}>{t('Ausschalten')}</Button>
          : <>
            <Text size={200}>{t('Alle Gesichtsdaten und Namen löschen?')}</Text>
            <Button appearance="primary" disabled={busy} onClick={() => toggle(false)}>{t('Ausschalten und löschen')}</Button>
            <Button onClick={() => setConfirmOff(false)}>{t('Abbrechen')}</Button>
          </>}
      </div>
      {shown.length > 0 && <div className={s.grid}>
        {shown.map(p => (
          <button key={p.id} className={s.person} onClick={() => setSel({ kind: 'person', id: p.id, name: p.name })}>
            <img className={s.face} src={crop(p.face)} alt="" loading="lazy" />
            <span className={s.name} title={p.name}>{p.name}</span>
            <span className={s.count}>{t('{n} Fotos', { n: p.count })}</span>
          </button>
        ))}
      </div>}
      <datalist id="faces-names">{names.map(n => <option key={n} value={n} />)}</datalist>
      {((ov?.groups.length ?? 0) > 0 || lastHidden || (ov?.ignored ?? 0) > 0) && <>
        <div className={s.head}>
          <Text weight="semibold">{t('Noch ohne Namen')}</Text>
          {lastHidden && <>
            <Text className={s.count}>{t('Ausgeblendet.')}</Text>
            <Button size="small" appearance="subtle" disabled={busy}
              onClick={() => act(() => api.del(`/api/gallery/faces/ignored?group=${encodeURIComponent(lastHidden)}`), () => setLastHidden(null), false)}>{t('Rückgängig')}</Button>
          </>}
          <div style={{ flex: 1 }} />
          {(ov?.ignored ?? 0) > 0 && <Button size="small" appearance="subtle" disabled={busy}
            onClick={() => act(() => api.del('/api/gallery/faces/ignored'), () => setLastHidden(null), false)}>
            {t('Ausgeblendete wieder zeigen ({n})', { n: ov!.ignored! })}</Button>}
        </div>
        <Text size={200} className={s.count}>{t('Namen aus der Liste wählen oder eintippen und mit Enter bestätigen. Wer keine Rolle spielt, lässt sich ausblenden – auch auf späteren Fotos.')}</Text>
        <div className={s.quickGrid}>
          {/* Ohne Vorschlag erst ab drei Fotos - zwei Treffer sind oft Zufall */}
          {ov!.groups.filter(g => g.suggest || g.count >= 3).map((g, i) => (
            <div key={g.id} className={s.quick}>
              <button className={s.person} onClick={() => setSel({ kind: 'group', id: g.id })} title={t('Fotos zeigen')}>
                <img className={s.face} src={crop(g.face)} alt="" loading="lazy" />
                <span className={s.count}>{t('{n} Fotos', { n: g.count })}</span>
              </button>
              <Tooltip content={t('Ausblenden – spielt keine Rolle')} relationship="label">
                <Button size="small" className={s.hide} icon={<EyeOff16Regular />} disabled={busy} onClick={() => hideGroup(g.id)} />
              </Tooltip>
              <form onSubmit={e => { e.preventDefault(); const n = quick[g.id]?.trim(); if (n) quickName(g.id, i, n) }}>
                <Input size="small" className={s.quickInput} value={quick[g.id] ?? ''} placeholder={t('Wer ist das?')} list="faces-names"
                  data-quick={i} onChange={(e, d) => {
                    setQuick(q => ({ ...q, [g.id]: d.value }))
                    // Aus der Liste gewählt: gleich übernehmen, ohne Enter
                    if (fromList(e, d.value, names)) quickName(g.id, i, d.value.trim())
                  }} />
              </form>
              {g.suggest && <div className={s.suggest}>
                {/* Der Name selbst bestätigt den Vorschlag wie der Haken */}
                <span className={s.suggestName} title={t('Ja, das ist {name}', { name: g.suggest.name })} role="button" tabIndex={0}
                  style={{ cursor: busy ? 'default' : 'pointer' }}
                  onClick={() => { if (!busy) quickName(g.id, i, g.suggest!.name) }}
                  onKeyDown={e => { if ((e.key === 'Enter' || e.key === ' ') && !busy) { e.preventDefault(); quickName(g.id, i, g.suggest!.name) } }}>{g.suggest.name}?</span>
                <Tooltip content={t('Ja, das ist {name}', { name: g.suggest.name })} relationship="label">
                  <Button size="small" appearance="subtle" icon={<Checkmark16Regular />} disabled={busy} onClick={() => quickName(g.id, i, g.suggest!.name)} />
                </Tooltip>
                <Tooltip content={t('Nein, nicht {name}', { name: g.suggest.name })} relationship="label">
                  <Button size="small" appearance="subtle" icon={<Dismiss16Regular />} disabled={busy}
                    onClick={() => act(() => api.post(`/api/gallery/faces/groups/${encodeURIComponent(g.id)}/reject`, { person: g.suggest!.id }), undefined, false)} />
                </Tooltip>
              </div>}
            </div>
          ))}
        </div>
      </>}
      {ov && shown.length === 0 && ov.groups.length === 0 && !status.running && <Text>{t('Noch keine Gesichter gefunden.')}</Text>}
    </div>
  )
}
