// DMS: Mailordner als Quelle (Server: Dms/DmsMailSources.cs) - wie die E-Mail-Regeln von paperless, mit den Konten von dmOffice.
import {
  Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, Option, Radio, RadioGroup,
  SpinButton, Switch, Text, Tooltip, makeStyles, tokens,
} from '@fluentui/react-components'
import { Add20Regular, ArrowSync20Regular, Delete20Regular, Edit20Regular } from '@fluentui/react-icons'
import { useEffect, useState } from 'react'
import { api, q } from '../api'
import { useApp } from '../App'
import { lang, t } from '../i18n'
import { askConfirm } from '../shell/context'
import { Hint, PageTitle } from '../settings/parts'
import { Entry, ordered, Profile } from './model'

interface Account { id: string; name: string; folders: { path: string; name: string }[] }
interface MailSource {
  id: string; name: string; account: string; folder: string; maxAgeDays: number; active: boolean; order: number; stop: boolean
  filterFrom: string | null; filterTo: string | null; filterSubject: string | null; filterBody: string | null
  scope: 'attachments' | 'mail' | 'both'; allParts: boolean; include: string | null; exclude: string | null
  action: 'none' | 'seen' | 'flag' | 'move' | 'delete'; actionFolder: string | null; titleFrom: 'filename' | 'subject'
  correspondentMode: 'none' | 'preset' | 'sender'; correspondent: string | null; doctype: string | null; tags: string[]; profile: string | null
  every: number; lastRun: string | null; error: string | null; lastCount: number; total: number
}

const useStyles = makeStyles({
  row: { display: 'flex', alignItems: 'center', gap: '8px', padding: '8px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke2}` },
  name: { flex: 1, minWidth: 0, display: 'flex', flexDirection: 'column' },
  muted: { color: tokens.colorNeutralForeground3 },
  list: { display: 'flex', flexDirection: 'column', maxWidth: '760px' },
  section: { display: 'flex', flexDirection: 'column', gap: '8px', padding: '12px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
  head: { fontSize: tokens.fontSizeBase200, fontWeight: tokens.fontWeightSemibold, textTransform: 'uppercase', letterSpacing: '0.05em', color: tokens.colorNeutralForeground2 },
  grid: { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '8px 12px', '@media (max-width: 767px)': { gridTemplateColumns: '1fr' } },
  line: { display: 'flex', alignItems: 'center', gap: '8px', flexWrap: 'wrap' },
})

const scopeLabel = (s: MailSource['scope']) => ({ attachments: t('Nur Anhänge'), mail: t('Ganze Mail'), both: t('Mail und Anhänge') })[s]
const actionLabel = (a: MailSource['action']) => ({ none: t('Nichts'), seen: t('Als gelesen markieren'), flag: t('Kennzeichnen'), move: t('In Ordner verschieben'), delete: t('Löschen (Papierkorb)') })[a]

/** Einstellungen → DMS → Mailordner. */
/** Mailordner als Quelle; embedded: als Abschnitt der Seite „Quellen“. */
export function DmsMailSourcesSettings({ embedded }: { embedded?: boolean }) {
  const s = useStyles()
  const { fail } = useApp()
  const [list, setList] = useState<MailSource[] | null>(null)
  const [accounts, setAccounts] = useState<Account[]>([])
  const [profiles, setProfiles] = useState<Profile[]>([])
  const [entries, setEntries] = useState<Entry[]>([])
  const [edit, setEdit] = useState<MailSource | 'new' | null>(null)
  const load = () => api.get<MailSource[]>('/api/dms/mail-sources').then(setList).catch(fail)
  useEffect(() => {
    load()
    api.get<Account[]>('/api/dms/mail-accounts').then(setAccounts).catch(() => {})
    api.get<Profile[]>('/api/dms/profiles?' + q({ lang })).then(setProfiles).catch(() => {})
    api.get<Entry[]>('/api/dms/lists').then(setEntries).catch(() => {})
  }, []) // eslint-disable-line react-hooks/exhaustive-deps
  const act = async (f: () => Promise<unknown>) => { try { await f(); load() } catch (e) { fail(e) } }
  const remove = async (x: MailSource) => {
    if (await askConfirm(t('Quelle löschen'), t('Die Quelle „{name}“ wird gelöscht. Übernommene Dokumente bleiben.', { name: x.name }), t('Löschen')))
      act(() => api.del(`/api/dms/mail-sources/${x.id}`))
  }
  const status = (x: MailSource) => x.error ? t('Fehler: {error}', { error: t(x.error) })
    : x.lastRun ? t('Zuletzt geprüft {when}: {n} Dokumente, insgesamt {total}', { when: new Date(x.lastRun).toLocaleString(), n: x.lastCount, total: x.total })
    : t('Noch nicht geprüft.')
  return (
    <>
      {embedded ? <Text size={400} weight="semibold">{t('Mailordner')}</Text> : <PageTitle>{t('Mailordner')}</PageTitle>}
      <Hint>{t('DMS überwacht Ordner der in dmOffice eingerichteten Mailkonten und legt Anhänge oder ganze Mails ab, die zu den Filtern passen. Verarbeitete Mails merkt sich die Quelle; beim Ändern der Quelle wird das vergessen, schon Abgelegtes erkennt das DMS am Inhalt.')}</Hint>
      {accounts.length === 0 && <Text className={s.muted}>{t('Kein Mailkonto eingerichtet.')}</Text>}
      <div className={s.list}>
        {list?.sort((a, b) => a.order - b.order).map(x => (
          <div key={x.id} className={s.row}>
            <div className={s.name}>
              <Text weight="semibold">{x.name}</Text>
              <Text size={200} className={s.muted}>{accounts.find(a => a.id === x.account)?.name ?? x.account} › {t('Ordner „{folder}“', { folder: x.folder })} · {scopeLabel(x.scope)} · {x.action === 'move' && x.actionFolder
                ? t('danach nach „{folder}“ verschieben', { folder: x.actionFolder }) : actionLabel(x.action)}</Text>
              <Text size={200} className={s.muted}>{status(x)}</Text>
            </div>
            <Tooltip content={t('Im Hintergrund regelmäßig prüfen')} relationship="label">
              <Switch checked={x.active} onChange={(_, d) => act(() => api.put(`/api/dms/mail-sources/${x.id}`, { ...x, active: d.checked }))} />
            </Tooltip>
            <Tooltip content={t('Jetzt prüfen')} relationship="label"><Button appearance="subtle" icon={<ArrowSync20Regular />} onClick={() => act(() => api.post(`/api/dms/mail-sources/${x.id}/run`))} /></Tooltip>
            <Tooltip content={t('Bearbeiten')} relationship="label"><Button appearance="subtle" icon={<Edit20Regular />} onClick={() => setEdit(x)} /></Tooltip>
            <Tooltip content={t('Löschen')} relationship="label"><Button appearance="subtle" icon={<Delete20Regular />} onClick={() => remove(x)} /></Tooltip>
          </div>
        ))}
      </div>
      {list?.length === 0 && accounts.length > 0 && <Text className={s.muted}>{t('Noch keine Mailordner.')}</Text>}
      <div><Button icon={<Add20Regular />} disabled={accounts.length === 0} onClick={() => setEdit('new')}>{t('Neuer Mailordner')}</Button></div>
      {edit && <EditMailSource source={edit === 'new' ? null : edit} accounts={accounts} profiles={profiles} entries={entries}
        onClose={saved => { setEdit(null); if (saved) load() }} />}
    </>
  )
}

function EditMailSource({ source, accounts, profiles, entries, onClose }: {
  source: MailSource | null; accounts: Account[]; profiles: Profile[]; entries: Entry[]; onClose: (saved: boolean) => void
}) {
  const s = useStyles()
  const { fail } = useApp()
  const [f, setF] = useState<MailSource>(() => source ? { ...source } : {
    id: '', name: '', account: accounts[0]?.id ?? '', folder: 'INBOX', maxAgeDays: 30, active: true, order: 100, stop: false,
    filterFrom: '', filterTo: '', filterSubject: '', filterBody: '', scope: 'attachments', allParts: false, include: '*.pdf', exclude: '',
    action: 'none', actionFolder: null, titleFrom: 'filename', correspondentMode: 'none', correspondent: null, doctype: null, tags: [],
    profile: profiles.find(p => p.default)?.id ?? profiles[0]?.id ?? null, every: 10, lastRun: null, error: null, lastCount: 0, total: 0,
  })
  const set = (patch: Partial<MailSource>) => setF(x => ({ ...x, ...patch }))
  const [busy, setBusy] = useState(false)
  const folders = accounts.find(a => a.id === f.account)?.folders ?? []
  const save = async () => {
    setBusy(true)
    try {
      if (source) await api.put(`/api/dms/mail-sources/${source.id}`, f)
      else await api.post('/api/dms/mail-sources', f)
      onClose(true)
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  const folderPick = (value: string | null, onSel: (v: string) => void) => (
    <Dropdown value={folders.find(x => x.path === value)?.name ?? value ?? ''} selectedOptions={[value ?? '']} onOptionSelect={(_, d) => onSel(d.optionValue ?? '')}>
      {folders.map(x => <Option key={x.path} value={x.path} text={x.name}>{x.path}</Option>)}
    </Dropdown>
  )
  const pick = (kind: 'correspondent' | 'doctype', value: string | null, onSel: (v: string | null) => void) => {
    const opts = ordered(entries, kind)
    return (
      <Dropdown value={value ? opts.find(o => o.entry.id === value)?.entry.name ?? '' : t('Keine Vorgabe')} selectedOptions={[value ?? '']} onOptionSelect={(_, d) => onSel(d.optionValue || null)}>
        <Option value="">{t('Keine Vorgabe')}</Option>
        {opts.map(o => <Option key={o.entry.id} value={o.entry.id} text={o.entry.name}><span style={{ paddingLeft: o.depth * 12 }}>{o.entry.name}</span></Option>)}
      </Dropdown>
    )
  }
  const tagOpts = ordered(entries, 'tag')
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose(false) }}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>{source ? t('Quelle bearbeiten') : t('Neue Quelle')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <div className={s.grid}>
              <Field label={t('Name')} required><Input value={f.name} onChange={(_, d) => set({ name: d.value })} /></Field>
              <Field label={t('Reihenfolge')}><SpinButton min={0} max={10000} value={f.order} onChange={(_, d) => set({ order: d.value ?? (Number(d.displayValue) || f.order) })} /></Field>
            </div>
            <div className={s.section}>
              <Text className={s.head}>{t('Mailordner')}</Text>
              <div className={s.grid}>
                <Field label={t('Konto')}>
                  <Dropdown value={accounts.find(a => a.id === f.account)?.name ?? ''} selectedOptions={[f.account]} onOptionSelect={(_, d) => set({ account: d.optionValue ?? '', folder: 'INBOX', actionFolder: null })}>
                    {accounts.map(a => <Option key={a.id} value={a.id}>{a.name}</Option>)}
                  </Dropdown>
                </Field>
                <Field label={t('Ordner')}>{folderPick(f.folder, v => set({ folder: v }))}</Field>
                <Field label={t('Höchstes Alter (Tage)')}><SpinButton min={1} max={3650} value={f.maxAgeDays} onChange={(_, d) => set({ maxAgeDays: d.value ?? (Number(d.displayValue) || f.maxAgeDays) })} /></Field>
                <Field label={t('Prüfen alle … Minuten')}><SpinButton min={1} max={1440} value={f.every} onChange={(_, d) => set({ every: d.value ?? (Number(d.displayValue) || f.every) })} /></Field>
              </div>
              <Checkbox checked={f.active} label={t('Überwachung aktiv')} onChange={(_, d) => set({ active: !!d.checked })} />
              <Checkbox checked={f.stop} label={t('Weitere Quellen nicht mehr anwenden, wenn diese eine Mail verarbeitet hat')} onChange={(_, d) => set({ stop: !!d.checked })} />
            </div>
            <div className={s.section}>
              <Text className={s.head}>{t('Filter (alle angegebenen müssen passen)')}</Text>
              <div className={s.grid}>
                <Field label={t('Absender enthält')}><Input value={f.filterFrom ?? ''} onChange={(_, d) => set({ filterFrom: d.value })} /></Field>
                <Field label={t('Empfänger enthält')}><Input value={f.filterTo ?? ''} onChange={(_, d) => set({ filterTo: d.value })} /></Field>
                <Field label={t('Betreff enthält')}><Input value={f.filterSubject ?? ''} onChange={(_, d) => set({ filterSubject: d.value })} /></Field>
                <Field label={t('Text enthält')}><Input value={f.filterBody ?? ''} onChange={(_, d) => set({ filterBody: d.value })} /></Field>
              </div>
            </div>
            <div className={s.section}>
              <Text className={s.head}>{t('Was abgelegt wird')}</Text>
              <RadioGroup layout="horizontal" value={f.scope} onChange={(_, d) => set({ scope: d.value as MailSource['scope'] })}>
                <Radio value="attachments" label={scopeLabel('attachments')} /><Radio value="mail" label={scopeLabel('mail')} /><Radio value="both" label={scopeLabel('both')} />
              </RadioGroup>
              {f.scope !== 'mail' && <>
                <Checkbox checked={f.allParts} label={t('Auch eingebettete Teile (etwa Bilder im Text) als Anhang nehmen')} onChange={(_, d) => set({ allParts: !!d.checked })} />
                <div className={s.grid}>
                  <Field label={t('Nur Dateien wie')} hint={t('Mit Komma getrennt, * als Platzhalter, z. B. *.pdf, *rechnung*')}><Input value={f.include ?? ''} onChange={(_, d) => set({ include: d.value })} /></Field>
                  <Field label={t('Nicht Dateien wie')}><Input value={f.exclude ?? ''} onChange={(_, d) => set({ exclude: d.value })} /></Field>
                </div>
                <Field label={t('Titel der Anhänge')}>
                  <RadioGroup layout="horizontal" value={f.titleFrom} onChange={(_, d) => set({ titleFrom: d.value as MailSource['titleFrom'] })}>
                    <Radio value="filename" label={t('Dateiname')} /><Radio value="subject" label={t('Betreff')} />
                  </RadioGroup>
                </Field>
              </>}
              <Text size={200} className={s.muted}>{t('Die ganze Mail wird als PDF abgelegt (Kopf, Text, Liste der Anhänge, PDF-Anhänge angefügt); das .eml bleibt als Original im DMS.')}</Text>
            </div>
            <div className={s.section}>
              <Text className={s.head}>{t('Danach mit der Mail')}</Text>
              <div className={s.line}>
                <Dropdown value={actionLabel(f.action)} selectedOptions={[f.action]} onOptionSelect={(_, d) => set({ action: d.optionValue as MailSource['action'] })}>
                  {(['none', 'seen', 'flag', 'move', 'delete'] as const).map(a => <Option key={a} value={a}>{actionLabel(a)}</Option>)}
                </Dropdown>
                {f.action === 'move' && folderPick(f.actionFolder, v => set({ actionFolder: v }))}
              </div>
            </div>
            <div className={s.section}>
              <Text className={s.head}>{t('Ablage und Vorgaben')}</Text>
              <div className={s.grid}>
                <Field label={t('Ablageprofil')}>
                  <Dropdown value={profiles.find(p => p.id === f.profile)?.name ?? ''} selectedOptions={[f.profile ?? '']} onOptionSelect={(_, d) => set({ profile: d.optionValue ?? null })}>
                    {profiles.map(p => <Option key={p.id} value={p.id}>{p.name}</Option>)}
                  </Dropdown>
                </Field>
                <Field label={t('Korrespondent')}>
                  <Dropdown value={{ none: t('Einordnen wie sonst'), preset: t('Fest vorgeben'), sender: t('Aus dem Absender') }[f.correspondentMode]} selectedOptions={[f.correspondentMode]}
                    onOptionSelect={(_, d) => set({ correspondentMode: d.optionValue as MailSource['correspondentMode'] })}>
                    <Option value="none">{t('Einordnen wie sonst')}</Option><Option value="preset">{t('Fest vorgeben')}</Option><Option value="sender">{t('Aus dem Absender')}</Option>
                  </Dropdown>
                </Field>
                {f.correspondentMode === 'preset' && <Field label={t('Vorgegebener Korrespondent')}>{pick('correspondent', f.correspondent, v => set({ correspondent: v }))}</Field>}
                <Field label={t('Dokumenttyp')}>{pick('doctype', f.doctype, v => set({ doctype: v }))}</Field>
                <Field label={t('Tags')}>
                  <Dropdown multiselect value={f.tags.map(id => entries.find(e => e.id === id)?.name ?? '').join(', ')} selectedOptions={f.tags} onOptionSelect={(_, d) => set({ tags: d.selectedOptions })}>
                    {tagOpts.map(o => <Option key={o.entry.id} value={o.entry.id} text={o.entry.name}><span style={{ paddingLeft: o.depth * 12 }}>{o.entry.name}</span></Option>)}
                  </Dropdown>
                </Field>
              </div>
              {f.correspondentMode === 'sender' && <Text size={200} className={s.muted}>{t('Gibt es zum Absender noch keinen Korrespondenten, legt das DMS einen mit der Zuweisung „Absender“ an.')}</Text>}
            </div>
          </DialogContent>
          <DialogActions>
            <Button onClick={() => onClose(false)}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || !f.name.trim() || !f.account || !f.folder || (f.action === 'move' && !f.actionFolder)} onClick={save}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
