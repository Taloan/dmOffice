// Dialoge der Dateiablage: Konflikt (ersetzen / beide behalten / ueberspringen, fuer alle), Name eingeben,
// Ordner waehlen, Freigabe-Links verwalten.
import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Divider, Field, Input, Radio, RadioGroup,
  Spinner, Switch, Text, makeStyles, tokens,
} from '@fluentui/react-components'
import { Copy20Regular, Delete20Regular, Link20Regular, LockClosed16Regular } from '@fluentui/react-icons'
import { PeopleList, Right, usePeople } from '../calendar/ShareDialog'
import { useCallback, useEffect, useRef, useState } from 'react'
import { api, q } from '../api'
import { locale, t } from '../i18n'
import { Item } from './common'
import { loadFileSettings } from '../settings/FilesSettings'

const useStyles = makeStyles({
  links: { display: 'flex', flexDirection: 'column', gap: '8px', marginTop: '12px' },
  link: {
    display: 'flex', flexDirection: 'column', gap: '6px', padding: '10px', borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  row: { display: 'flex', gap: '6px', alignItems: 'center', flexWrap: 'wrap' },
  // Neuer Link: feste Raster-Zeilen, damit das Passwortfeld nichts verschiebt
  form: { display: 'grid', gridTemplateColumns: 'minmax(0, 1fr) minmax(0, 1fr)', gap: '10px 16px', marginTop: '12px',
    '@media (max-width: 480px)': { gridTemplateColumns: 'minmax(0, 1fr)' } },
  full: { gridColumn: '1 / -1' },
  url: { flex: 1, minWidth: '160px' },
  muted: { color: tokens.colorNeutralForeground3, fontSize: '12px' },
})

// ---------------- Konflikt ----------------

export type Choice = 'replace' | 'rename' | 'skip'

/** Fragt bei belegtem Namen nach; "fuer alle" merkt sich die Antwort fuer den Rest des Vorgangs. */
export function useConflict() {
  const [ask, setAsk] = useState<{ name: string; folder: boolean; more: boolean; resolve: (c: Choice | null, all: boolean) => void } | null>(null)
  const sticky = useRef<Choice | null>(null)
  const start = useCallback(() => { sticky.current = null }, [])
  const resolve = useCallback((name: string, folder: boolean, more: boolean) => new Promise<Choice | null>(res => {
    if (sticky.current) { res(sticky.current); return }
    setAsk({ name, folder, more, resolve: (c, all) => { if (all && c) sticky.current = c; setAsk(null); res(c) } })
  }), [])
  const dialog = ask && <ConflictDialog {...ask} />
  return { start, resolve, dialog }
}

function ConflictDialog({ name, folder, more, resolve }: { name: string; folder: boolean; more: boolean; resolve: (c: Choice | null, all: boolean) => void }) {
  const [all, setAll] = useState(false)
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && resolve(null, false)}>
      <DialogSurface style={{ maxWidth: 460 }}>
        <DialogBody>
          <DialogTitle>{folder ? t('Ordner gibt es schon') : t('Datei gibt es schon')}</DialogTitle>
          <DialogContent>
            <Text block>{t('Am Ziel gibt es bereits „{name}“. Was soll passieren?', { name })}</Text>
            {more && <Checkbox style={{ marginTop: 12 }} checked={all} onChange={(_, d) => setAll(!!d.checked)} label={t('Für alle weiteren Konflikte übernehmen')} />}
          </DialogContent>
          <DialogActions fluid>
            <Button appearance="primary" onClick={() => resolve('replace', all)}>{t('Ersetzen')}</Button>
            <Button onClick={() => resolve('rename', all)}>{t('Beide behalten')}</Button>
            <Button onClick={() => resolve('skip', all)}>{t('Überspringen')}</Button>
            <Button appearance="subtle" onClick={() => resolve(null, false)}>{t('Abbrechen')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

// ---------------- Name eingeben ----------------

/** Statt prompt(): Feld mit markiertem Namen ohne Endung (wie im Explorer). */
export function NameDialog({ title, initial, label, onDone, onClose }: {
  title: string; initial: string; label?: string; onDone: (name: string) => void; onClose: () => void
}) {
  const [name, setName] = useState(initial)
  const ref = useRef<HTMLInputElement>(null)
  useEffect(() => {
    const el = ref.current
    if (!el) return
    el.focus()
    const dot = initial.lastIndexOf('.')
    el.setSelectionRange(0, dot > 0 ? dot : initial.length)
  }, [initial])
  const bad = /[\\/]/.test(name) || name.trim() === '.' || name.trim() === '..'
  const submit = (e?: React.FormEvent) => { e?.preventDefault(); const n = name.trim(); if (n && !bad) onDone(n) }
  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 420 }}>
        <form onSubmit={submit}>
          <DialogBody>
            <DialogTitle>{title}</DialogTitle>
            <DialogContent>
              <Field label={label ?? t('Name')} validationMessage={bad ? t('Ohne / und \\') : undefined}>
                <Input ref={ref} value={name} onChange={(_, d) => setName(d.value)} />
              </Field>
            </DialogContent>
            <DialogActions>
              <Button onClick={onClose}>{t('Abbrechen')}</Button>
              <Button appearance="primary" type="submit" disabled={!name.trim() || bad}>{t('OK')}</Button>
            </DialogActions>
          </DialogBody>
        </form>
      </DialogSurface>
    </Dialog>
  )
}

// ---------------- Freigabe ----------------

export interface Link {
  id: string; token: string; mode: 'read' | 'upload' | 'drop'; expires: string | null; hasPassword: boolean; created: string; downloads: number
  /** Hoechstens so viele Abrufe, dann laeuft der Link ab; null = unbegrenzt */
  maxDownloads?: number | null
  expired?: boolean; item?: Item | null
}

export const linkUrl = (l: Link) => `${location.origin}/s/${l.token}`

// Wie bei den Freigaben an Personen: Senden = der andere bekommt die Datei (ansehen, herunterladen),
// Teilen = er arbeitet mit (legt Dateien dazu); "Nur hochladen" ist ein Briefkasten und keins von beiden
/** In Listen der Freigaben: Stand des Links ("Gesendet", "Geteilt") */
export const linkStatus = (m: string) => m === 'upload' ? t('Geteilt') : m === 'drop' ? t('Nur hochladen') : t('Gesendet')
export const modeLabel = (m: string) => m === 'upload' ? t('Teilen – ansehen und hochladen') : m === 'drop' ? t('Nur hochladen') : t('Senden – ansehen und herunterladen')

export async function copyText(text: string): Promise<boolean> {
  try { await navigator.clipboard.writeText(text); return true } catch {
    // Ohne HTTPS gibt es die Zwischenablage-Schnittstelle nicht
    const ta = document.createElement('textarea'); ta.value = text; document.body.appendChild(ta); ta.select()
    const ok = document.execCommand('copy'); ta.remove(); return ok
  }
}

const dateOnly = (iso: string | null) => (iso ? new Date(iso).toISOString().slice(0, 10) : '')

/**
 * Teilen einer Datei oder eines Ordners. Ordner: oben "Mit Personen teilen" (andere Benutzer dieses Servers, Lesen oder Bearbeiten,
 * der Inhalt bleibt bei dir), darunter "Link" (ohne Anmeldung, mit Ablauf, Abrufgrenze, Passwort). Dateien: nur der Link.
 */
export function ShareDialog({ item, onClose, notify, fail }: {
  item: Item; onClose: () => void; notify: (m: string) => void; fail: (e: unknown) => void
}) {
  const s = useStyles()
  const withPeople = item.isDir && item.path.length > 0
  const people = usePeople({})
  const [peopleReady, setPeopleReady] = useState(!withPeople)
  useEffect(() => {
    if (!withPeople) return
    api.get<{ items: { members: { id: string; readOnly: boolean }[] }[] }>('/api/files/shares?' + q({ path: item.path })).then(d => {
      people.setRights(Object.fromEntries((d.items[0]?.members ?? []).map(m => [m.id, (m.readOnly ? 'read' : 'write') as Right])))
      setPeopleReady(true)
    }).catch(fail)
  }, [item.path]) // eslint-disable-line react-hooks/exhaustive-deps
  const savePeople = async () => {
    try {
      await api.put('/api/files/shares', { path: item.path, ...people.body })
      notify(people.body.members.length ? t('Freigabe gespeichert') : t('Freigabe aufgehoben'))
    } catch (e) { fail(e) }
  }
  const [links, setLinks] = useState<Link[] | null>(null)
  const [mode, setMode] = useState<'read' | 'upload' | 'drop'>('read')
  const [expires, setExpires] = useState('')
  const [usePw, setUsePw] = useState(false)
  // Vorgabe: nach 10 Abrufen laeuft der Link ab (0 = unbegrenzt)
  const [maxDl, setMaxDl] = useState('10')
  const [pw, setPw] = useState('')
  const [busy, setBusy] = useState(false)
  const load = useCallback(() => api.get<{ items: Link[] }>('/api/files/links?' + q({ path: item.path })).then(d => setLinks(d.items)).catch(fail), [item.path, fail])
  useEffect(() => { load() }, [load])
  // Vorgaben aus Einstellungen -> Dateien
  useEffect(() => {
    loadFileSettings().then(st => {
      setMaxDl(String(st.linkMaxDownloads))
      if (st.linkDays > 0) setExpires(new Date(Date.now() + st.linkDays * 86400000).toISOString().slice(0, 10))
    }).catch(() => { /* Vorgabe bleibt */ })
  }, [])

  const create = async () => {
    setBusy(true)
    try {
      const l = await api.post<Link>('/api/files/links', {
        path: item.path, mode, expires: expires ? new Date(expires + 'T23:59:59').toISOString() : null, password: usePw ? pw : null,
        maxDownloads: Number(maxDl) || 0,
      })
      if (await copyText(linkUrl(l))) notify(t('Link erstellt und kopiert'))
      setPw(''); setUsePw(false)
      await load()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const remove = async (l: Link) => {
    if (!confirm(t('Link widerrufen? Wer ihn hat, kommt dann nicht mehr an die Inhalte.'))) return
    try { await api.del('/api/files/links/' + l.id); await load() } catch (e) { fail(e) }
  }
  const change = async (l: Link, patch: Partial<{ mode: string; expires: string | null; password: string | null; maxDownloads: number }>) => {
    try {
      await api.put('/api/files/links/' + l.id, {
        mode: patch.mode ?? l.mode, expires: 'expires' in patch ? patch.expires : l.expires, password: patch.password ?? null,
        maxDownloads: 'maxDownloads' in patch ? patch.maxDownloads : l.maxDownloads ?? 0,
      })
      await load()
    } catch (e) { fail(e) }
  }

  return (
    <Dialog open onOpenChange={(_, d) => !d.open && onClose()}>
      <DialogSurface style={{ maxWidth: 560 }}>
        <DialogBody>
          <DialogTitle>{t('„{name}“ teilen', { name: item.name || t('Meine Dateien') })}</DialogTitle>
          <DialogContent>
            {withPeople && <>
              <Text block weight="semibold">{t('Mit Personen teilen')}</Text>
              <div style={{ display: 'flex', flexDirection: 'column', gap: 8, marginTop: 6 }}>
                <PeopleList state={people} intro={t('Wer hier Zugriff hat, sieht den Ordner samt Unterordnern bei sich unter „Mit mir geteilt“ – auch in WebDAV, Nextcloud-Clients und der Weboberfläche. Mit „Bearbeiten“ lassen sich Dateien hochladen, ändern, umbenennen und löschen; Gelöschtes landet in deinem Papierkorb und der Speicherplatz zählt bei dir.')}
                  hint={t('Den Zugriff kannst du jederzeit ändern oder entziehen. Einzelne Dateien teilst du per Link.')} />
                {!people.nobody && (
                  <div><Button appearance="primary" disabled={!people.people || !peopleReady} onClick={savePeople}>{t('Personen speichern')}</Button></div>
                )}
              </div>
              <Divider style={{ margin: '16px 0 12px' }} />
              <Text block weight="semibold">{t('Link')}</Text>
            </>}
            <Text block className={s.muted}>{t('Jeder mit dem Link kommt ohne Anmeldung an die Inhalte – schütze ihn bei Bedarf mit Passwort und Ablaufdatum. Jedes Öffnen oder Herunterladen zählt als Abruf; ist die Grenze erreicht, läuft der Link ab.')}</Text>
            {item.isDir && (
              <RadioGroup value={mode} onChange={(_, d) => setMode(d.value as any)} style={{ marginTop: 8 }}>
                <Radio value="read" label={modeLabel('read')} />
                <Radio value="upload" label={modeLabel('upload')} />
                <Radio value="drop" label={t('Nur hochladen (Inhalt bleibt verborgen)')} />
              </RadioGroup>
            )}
            <div className={s.form}>
              <Field label={t('Läuft ab am')} hint={t('leer = läuft nicht ab')}>
                <Input type="date" value={expires} min={new Date().toISOString().slice(0, 10)} onChange={(_, d) => setExpires(d.value)} />
              </Field>
              <Field label={t('Höchstens Abrufe')} hint={t('0 = unbegrenzt')}>
                <Input type="number" min={0} value={maxDl} onChange={(_, d) => setMaxDl(d.value)} />
              </Field>
              <Switch className={s.full} checked={usePw} onChange={(_, d) => setUsePw(d.checked)} label={t('Mit Passwort schützen')} />
              {usePw && (
                <Field className={s.full} label={t('Passwort für den Link')} hint={t('mindestens 4 Zeichen')}>
                  <Input type="password" value={pw} onChange={(_, d) => setPw(d.value)} autoComplete="new-password" autoFocus />
                </Field>
              )}
              <div className={s.full}>
                <Button appearance="primary" icon={busy ? <Spinner size="tiny" /> : <Link20Regular />} disabled={busy || (usePw && pw.length < 4)}
                  onClick={create}>{t('Link erstellen')}</Button>
              </div>
            </div>

            <div className={s.links}>
              {links === null ? <Spinner size="tiny" /> : links.length === 0 ? null : <Text weight="semibold">{t('Bestehende Links')}</Text>}
              {links?.map(l => (
                <div key={l.id} className={s.link}>
                  <div className={s.row}>
                    <Input className={s.url} readOnly value={linkUrl(l)} onFocus={e => e.target.select()} />
                    <Button icon={<Copy20Regular />} onClick={async () => { if (await copyText(linkUrl(l))) notify(t('Link kopiert')) }}>{t('Kopieren')}</Button>
                    <Button icon={<Delete20Regular />} appearance="subtle" onClick={() => remove(l)} aria-label={t('Widerrufen')} title={t('Widerrufen')} />
                  </div>
                  <div className={s.row}>
                    {item.isDir ? (
                      <select value={l.mode} onChange={e => change(l, { mode: e.target.value })} aria-label={t('Art')}
                        style={{ padding: '4px', borderRadius: 4, border: `1px solid ${tokens.colorNeutralStroke1}`, background: tokens.colorNeutralBackground1, color: tokens.colorNeutralForeground1 }}>
                        <option value="read">{modeLabel('read')}</option>
                        <option value="upload">{modeLabel('upload')}</option>
                        <option value="drop">{modeLabel('drop')}</option>
                      </select>
                    ) : <Text size={200}>{modeLabel(l.mode)}</Text>}
                    <Input type="date" size="small" value={dateOnly(l.expires)} aria-label={t('Läuft ab am')}
                      onChange={(_, d) => change(l, { expires: d.value ? new Date(d.value + 'T23:59:59').toISOString() : null })} />
                    <Input type="number" size="small" min={0} style={{ width: 80 }} aria-label={t('Höchstens Abrufe')} title={t('Höchstens Abrufe (0 = unbegrenzt)')}
                      defaultValue={String(l.maxDownloads ?? 0)} onBlur={e => { const v = Number(e.target.value) || 0; if (v !== (l.maxDownloads ?? 0)) change(l, { maxDownloads: v }) }} />
                    {l.hasPassword && <span className={s.muted} style={{ display: 'flex', alignItems: 'center', gap: 2 }}><LockClosed16Regular />{t('Passwort')}</span>}
                    <Button size="small" appearance="subtle" onClick={() => {
                      const p = prompt(l.hasPassword ? t('Neues Passwort (leer = Passwort entfernen):') : t('Passwort für den Link:'))
                      if (p !== null) change(l, { password: p })
                    }}>{l.hasPassword ? t('Passwort ändern') : t('Passwort setzen')}</Button>
                  </div>
                  <span className={s.muted}>
                    {l.expired ? t('Abgelaufen') + ' · ' : ''}{t('Erstellt {date}', { date: new Date(l.created).toLocaleDateString(locale) })} · {l.maxDownloads ? t('{n} von {max} Abrufen', { n: l.downloads, max: l.maxDownloads }) : t('{n}× heruntergeladen', { n: l.downloads })}
                  </span>
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
