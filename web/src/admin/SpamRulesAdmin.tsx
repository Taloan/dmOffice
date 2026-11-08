import {
  Badge, Button, Checkbox, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Dropdown, Field, Input, MessageBar,
  MessageBarBody, Option, Switch, Text, Textarea, makeStyles, tokens,
} from '@fluentui/react-components'
import { Add20Regular, ArrowReset20Regular, ClipboardPaste20Regular, Delete20Regular, Edit20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { AdminHeading } from './AdminHeading'
import { askConfirm } from '../shell/context'

/** Eine Erkennungsregel wie auf dem Server (Mail/SpamRules.cs). */
interface SpamRule {
  id: string; name: string; enabled: boolean; part: string; header?: string | null; pattern: string; ignoreCase: boolean; negate: boolean; action: string
}
interface State { on: boolean; defaults: boolean; rules: SpamRule[]; stats: Record<string, { hits: number; timeouts: number }> }

const partLabel = (p: string) => ({
  subject: t('Betreff'), from: t('Absender (Name und Adresse)'), header: t('Eine Kopfzeile'), headers: t('Alle Kopfzeilen'),
  body: t('Text der Mail'), links: t('Links (einer je Zeile)'),
} as Record<string, string>)[p] ?? p
const actionLabel = (a: string) => ({
  feature: t('Nur Merkmal'), uncertain: t('Mindestens unsicher'), junk: t('Junk'),
} as Record<string, string>)[a] ?? a
const PARTS = ['subject', 'from', 'header', 'headers', 'body', 'links']
const ACTIONS = ['feature', 'uncertain', 'junk']

const useStyles = makeStyles({
  muted: { color: tokens.colorNeutralForeground3 },
  list: { display: 'flex', flexDirection: 'column', gap: '2px' },
  row: {
    display: 'grid', gridTemplateColumns: 'auto minmax(0, 1fr) auto', gap: '4px 12px', alignItems: 'center', padding: '6px 8px',
    borderRadius: tokens.borderRadiusMedium, ':hover': { backgroundColor: tokens.colorNeutralBackground1Hover },
  },
  name: { display: 'flex', flexDirection: 'column', minWidth: 0 },
  code: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, color: tokens.colorNeutralForeground3, overflowWrap: 'anywhere' },
  mono: { fontFamily: tokens.fontFamilyMonospace },
})

/**
 * Zusätzliche Erkennungsregeln (für Bastler): reguläre Ausdrücke auf Betreff, Absender, Kopfzeilen, Text oder Links. Ein Treffer
 * ist ein Merkmal für den lernenden Filter oder hat eine feste Wirkung. Jede Regel prüft der Server beim Speichern - ein kaputter
 * oder zu langsamer Ausdruck lässt sich nicht speichern. Regeln im SpamAssassin-Format lassen sich einfügen.
 */
export function SpamRulesAdmin() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [state, setState] = useState<State | null>(null)
  const [edit, setEdit] = useState<{ rule: SpamRule; index: number } | null>(null)
  const [importing, setImporting] = useState(false)
  useEffect(() => { api.get<State>('/api/admin/spamrules').then(setState).catch(fail) }, []) // eslint-disable-line react-hooks/exhaustive-deps
  if (!state) return null
  const save = async (on: boolean, rules: SpamRule[]) => {
    try { setState(await api.put<State>('/api/admin/spamrules', { on, rules })); return true } catch (e) { fail(e); return false }
  }
  const remove = async (i: number) => {
    if (!await askConfirm('', t('Regel „{name}“ löschen?', { name: state.rules[i].name }))) return
    save(state.on, state.rules.filter((_, j) => j !== i))
  }
  const reset = async () => {
    if (!await askConfirm('', t('Alle Regeln durch die mitgelieferten Startregeln ersetzen?'))) return
    try { setState(await api.post<State>('/api/admin/spamrules/defaults')); notify(t('Startregeln wiederhergestellt')) } catch (e) { fail(e) }
  }
  const fresh: SpamRule = { id: '', name: '', enabled: true, part: 'subject', header: null, pattern: '', ignoreCase: true, negate: false, action: 'feature' }
  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 12, maxWidth: 820 }}>
      <AdminHeading>{t('Zusätzliche Erkennungsregeln')}</AdminHeading>
      <Text>{t('Für Bastler: eigene reguläre Ausdrücke (.NET) auf Betreff, Absender, Kopfzeilen, Text oder Links. Ein Treffer geht als Merkmal in den lernenden Filter – wie viel er zählt, lernt der Filter aus den Mails jedes Benutzers. Auf Wunsch macht eine Regel die Mail auch direkt „unsicher“ oder zu Junk. Sichere Absender gehen immer vor.')}</Text>
      <Text size={200} className={s.muted}>{t('Jeder Ausdruck wird beim Speichern geprüft; ungültige oder zu langsame lassen sich nicht speichern. Braucht eine Regel bei einer Mail länger als 0,1 Sekunden, wird sie dort übersprungen. Mitgeliefert sind nur einige eigene Startregeln. Regeln aus anderen Quellen (etwa SpamAssassin) lassen sich einfügen – auf deren Lizenz achtest du dann selbst.')}</Text>
      <Switch label={t('Zusätzliche Erkennungsregeln verwenden')} checked={state.on} onChange={(_, d) => save(d.checked, state.rules)} />
      <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap' }}>
        <Button icon={<Add20Regular />} onClick={() => setEdit({ rule: fresh, index: -1 })}>{t('Regel hinzufügen')}</Button>
        <Button icon={<ClipboardPaste20Regular />} onClick={() => setImporting(true)}>{t('Im SpamAssassin-Format einfügen')}</Button>
        {!state.defaults && <Button icon={<ArrowReset20Regular />} onClick={reset}>{t('Startregeln wiederherstellen')}</Button>}
      </div>
      {state.rules.length === 0
        ? <Text className={s.muted}>{t('Keine Regeln.')}</Text>
        : <div className={s.list} style={{ opacity: state.on ? 1 : 0.6 }}>
          {state.rules.map((r, i) => {
            const st = state.stats[r.id]
            return (
              <div key={r.id + i} className={s.row}>
                <Switch checked={r.enabled} aria-label={t('Regel „{name}“ verwenden', { name: r.name })}
                  onChange={(_, d) => save(state.on, state.rules.map((x, j) => j === i ? { ...x, enabled: d.checked } : x))} />
                <div className={s.name}>
                  <Text weight="semibold">{r.name}</Text>
                  <span className={s.code}>
                    {partLabel(r.part)}{r.part === 'header' && r.header ? ` „${r.header}“` : ''} {r.negate ? '!~' : '=~'} {r.pattern}{r.ignoreCase ? ' (i)' : ''}
                  </span>
                  <div style={{ display: 'flex', gap: 6, marginTop: 2, flexWrap: 'wrap' }}>
                    <Badge appearance="tint" color={r.action === 'junk' ? 'danger' : r.action === 'uncertain' ? 'warning' : 'informative'}>{actionLabel(r.action)}</Badge>
                    {st && st.hits > 0 && <Badge appearance="outline">{t('{n} Treffer seit dem Start', { n: st.hits })}</Badge>}
                    {st && st.timeouts > 0 && <Badge appearance="tint" color="warning">{t('{n}× zu langsam', { n: st.timeouts })}</Badge>}
                  </div>
                </div>
                <div style={{ display: 'flex' }}>
                  <Button appearance="subtle" icon={<Edit20Regular />} aria-label={t('Bearbeiten')} onClick={() => setEdit({ rule: r, index: i })} />
                  <Button appearance="subtle" icon={<Delete20Regular />} aria-label={t('Löschen')} onClick={() => remove(i)} />
                </div>
              </div>
            )
          })}
        </div>}
      {edit && <RuleDialog rule={edit.rule} onClose={() => setEdit(null)}
        onSave={async r => {
          const rules = edit.index < 0 ? [...state.rules, r] : state.rules.map((x, j) => j === edit.index ? r : x)
          if (await save(state.on, rules)) setEdit(null)
        }} />}
      {importing && <ImportDialog onClose={() => setImporting(false)}
        onAdd={async rules => { if (await save(state.on, [...state.rules, ...rules])) { setImporting(false); notify(t('{n} Regeln übernommen', { n: rules.length })) } }} />}
    </div>
  )
}

/** Regel bearbeiten - mit Prüfung durch den Server während der Eingabe und einem Probetext. */
function RuleDialog({ rule, onSave, onClose }: { rule: SpamRule; onSave: (r: SpamRule) => void; onClose: () => void }) {
  const s = useStyles()
  const [r, setR] = useState<SpamRule>(rule)
  const [sample, setSample] = useState('')
  const [check, setCheck] = useState<{ ok: boolean; error: string | null; match: boolean; found: string | null } | null>(null)
  const seq = useRef(0)
  const set = (p: Partial<SpamRule>) => setR(x => ({ ...x, ...p }))
  useEffect(() => {
    const n = ++seq.current
    const timer = setTimeout(() => {
      api.post('/api/admin/spamrules/check', { rule: { ...r, name: r.name || '?' }, sample })
        .then(c => { if (n === seq.current) setCheck(c) }).catch(() => { })
    }, 300)
    return () => clearTimeout(timer)
  }, [r, sample])
  // Der Server prüft beim Speichern noch einmal - die Anzeige kann der Eingabe kurz hinterherhängen
  const valid = !!check?.ok && r.name.trim().length > 0
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 640 }}>
        <DialogBody>
          <DialogTitle>{rule.pattern ? t('Regel bearbeiten') : t('Regel hinzufügen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            <Field label={t('Name')} required><Input value={r.name} onChange={(_, d) => set({ name: d.value })} /></Field>
            <div style={{ display: 'flex', gap: 12, flexWrap: 'wrap' }}>
              <Field label={t('Bereich der Mail')} style={{ flex: 1, minWidth: 200 }}>
                <Dropdown value={partLabel(r.part)} selectedOptions={[r.part]} onOptionSelect={(_, d) => set({ part: d.optionValue ?? 'subject' })}>
                  {PARTS.map(p => <Option key={p} value={p}>{partLabel(p)}</Option>)}
                </Dropdown>
              </Field>
              {r.part === 'header' && <Field label={t('Name der Kopfzeile')} style={{ flex: 1, minWidth: 160 }}>
                <Input value={r.header ?? ''} placeholder="Reply-To" onChange={(_, d) => set({ header: d.value })} />
              </Field>}
            </div>
            <Field label={t('Regulärer Ausdruck')} required
              validationState={check && !check.ok ? 'error' : 'none'} validationMessage={check && !check.ok ? check.error : undefined}
              hint={r.part === 'header' ? t('Fehlt die Kopfzeile, wird ein leerer Text geprüft – „^$“ trifft also, wenn sie fehlt.') : undefined}>
              <Input className={s.mono} value={r.pattern} onChange={(_, d) => set({ pattern: d.value })} />
            </Field>
            <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
              <Checkbox label={t('Groß- und Kleinschreibung egal')} checked={r.ignoreCase} onChange={(_, d) => set({ ignoreCase: !!d.checked })} />
              <Checkbox label={t('Trifft, wenn der Ausdruck nicht passt')} checked={r.negate} onChange={(_, d) => set({ negate: !!d.checked })} />
            </div>
            <Field label={t('Wirkung bei einem Treffer')}>
              <Dropdown value={actionLabel(r.action)} selectedOptions={[r.action]} onOptionSelect={(_, d) => set({ action: d.optionValue ?? 'feature' })}>
                {ACTIONS.map(a => <Option key={a} value={a}>{actionLabel(a)}</Option>)}
              </Dropdown>
            </Field>
            <Field label={t('Probetext')} hint={t('Zum Ausprobieren, wird nicht gespeichert.')}>
              <Textarea className={s.mono} value={sample} rows={3} resize="vertical" onChange={(_, d) => setSample(d.value)} />
            </Field>
            {check?.ok && sample && (
              <MessageBar intent={check.match ? 'success' : 'info'}>
                <MessageBarBody>{check.match
                  ? (check.found ? t('Trifft: „{text}“', { text: check.found }) : t('Trifft.'))
                  : t('Trifft nicht.')}</MessageBarBody>
              </MessageBar>
            )}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={!valid} onClick={() => onSave({ ...r, name: r.name.trim() })}>{t('Speichern')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
/** Regelzeilen im SpamAssassin-Format übersetzen lassen und auswählen, welche übernommen werden. */
function ImportDialog({ onAdd, onClose }: { onAdd: (rules: SpamRule[]) => void; onClose: () => void }) {
  const s = useStyles()
  const { fail } = useApp()
  const [text, setText] = useState('')
  const [result, setResult] = useState<{ rules: SpamRule[]; skipped: string[] } | null>(null)
  const [chosen, setChosen] = useState<Set<number>>(new Set())
  const parse = async () => {
    try {
      const r = await api.post<{ rules: SpamRule[]; skipped: string[] }>('/api/admin/spamrules/import', { text })
      setResult(r); setChosen(new Set(r.rules.map((_, i) => i)))
    } catch (e) { fail(e) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface style={{ maxWidth: 760 }}>
        <DialogBody>
          <DialogTitle>{t('Im SpamAssassin-Format einfügen')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
            {!result ? <>
              <Text>{t('Regelzeilen einfügen: header, body, rawbody, uri mit /…/ oder m{…}, dazu describe (wird der Name) und score (ab 5 Junk, ab 2 mindestens unsicher, sonst nur Merkmal). meta- und eval-Regeln gibt es hier nicht.')}</Text>
              <Textarea className={s.mono} value={text} rows={12} resize="vertical" onChange={(_, d) => setText(d.value)}
                placeholder={'header   SUBJ_FREE  Subject =~ /\\bfree\\b/i\ndescribe SUBJ_FREE  Subject says free\nscore    SUBJ_FREE  2.5'} />
            </> : <>
              {result.rules.length === 0
                ? <Text>{t('Keine Regel ließ sich übernehmen.')}</Text>
                : <div className={s.list} style={{ maxHeight: 320, overflow: 'auto' }}>
                  {result.rules.map((r, i) => (
                    <Checkbox key={i} checked={chosen.has(i)}
                      onChange={(_, d) => setChosen(c => { const x = new Set(c); if (d.checked) x.add(i); else x.delete(i); return x })}
                      label={<span className={s.name}>
                        <span>{r.name} · {actionLabel(r.action)}</span>
                        <span className={s.code}>{partLabel(r.part)}{r.header ? ` „${r.header}“` : ''} {r.negate ? '!~' : '=~'} {r.pattern}</span>
                      </span>} />
                  ))}
                </div>}
              {result.skipped.length > 0 && (
                <MessageBar intent="warning" layout="multiline">
                  <MessageBarBody>
                    <div>{t('{n} Zeilen nicht übernommen:', { n: result.skipped.length })}</div>
                    <ul style={{ margin: '4px 0 0', paddingLeft: 18, maxHeight: 160, overflow: 'auto' }}>
                      {result.skipped.map((x, i) => <li key={i} className={s.code}>{x}</li>)}
                    </ul>
                  </MessageBarBody>
                </MessageBar>
              )}
            </>}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={result ? () => setResult(null) : onClose}>{result ? t('Zurück') : t('Abbrechen')}</Button>
            {!result
              ? <Button appearance="primary" disabled={!text.trim()} onClick={parse}>{t('Prüfen')}</Button>
              : <Button appearance="primary" disabled={chosen.size === 0}
                onClick={() => onAdd(result.rules.filter((_, i) => chosen.has(i)))}>{t('{n} Regeln übernehmen', { n: chosen.size })}</Button>}
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}
