import {
  Badge, Button, Dialog, DialogActions, DialogBody, DialogContent, DialogSurface, DialogTitle, Field, Input, Link, Switch, Text, Textarea, makeStyles, tokens,
} from '@fluentui/react-components'
import { ArrowDownload20Regular, ArrowUpload20Regular, Checkmark20Regular, Delete20Regular, Key20Regular } from '@fluentui/react-icons'
import { useEffect, useRef, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { t } from '../i18n'
import { fingerprintText, PgpAccount, pgpChanged, PgpSettings } from '../mail/Pgp'
import { fmtDay } from '../shell/hooks'
import { askConfirm } from '../shell/context'
import { Hint, PageTitle, Section } from './parts'

interface KnownKey {
  fingerprint: string; emails: string[]; userIds: string[]; source: string; confirmed: boolean
  created: string; expires?: string | null; revoked: boolean; algorithm: string; added: string
}

const useStyles = makeStyles({
  account: { display: 'flex', flexDirection: 'column', gap: '6px', padding: '10px 12px', border: `1px solid ${tokens.colorNeutralStroke2}`, borderRadius: tokens.borderRadiusMedium },
  row: { display: 'flex', gap: '8px', flexWrap: 'wrap', alignItems: 'center' },
  mono: { fontFamily: tokens.fontFamilyMonospace, fontSize: tokens.fontSizeBase200, wordBreak: 'break-all' },
  key: { display: 'flex', justifyContent: 'space-between', gap: '8px', alignItems: 'center', padding: '6px 0', borderBottom: `1px solid ${tokens.colorNeutralStroke3}` },
})

const sourceText = (s: string) => ({
  own: t('eigener'), import: t('importiert'), wkd: t('vom Mailanbieter (WKD)'), attached: t('aus einem Anhang'), autocrypt: t('per Autocrypt'),
} as Record<string, string>)[s] ?? s

/** Einstellungen → E-Mail → OpenPGP: eigene Schlüssel je Konto, Signieren, bekannte Schlüssel anderer, WKD. */
export function OpenPgpSettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [st, setSt] = useState<PgpSettings | null>(null)
  const [keys, setKeys] = useState<KnownKey[]>([])
  const [importFor, setImportFor] = useState<PgpAccount | 'public' | null>(null)
  const apply = (n: PgpSettings) => { setSt(n); pgpChanged(n) }
  const loadKeys = () => api.get<KnownKey[]>('/api/mail/pgp/keys').then(setKeys).catch(fail)
  useEffect(() => { api.get<PgpSettings>('/api/mail/pgp/settings').then(apply).catch(fail); loadKeys() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const save = async (change: { wkd?: boolean; accounts?: { id: string; sign?: boolean; attachKey?: boolean; autocrypt?: boolean }[] }) => {
    try { apply(await api.put<PgpSettings>('/api/mail/pgp/settings', change)) } catch (e) { fail(e) }
  }
  const generate = async (a: PgpAccount) => {
    try { apply(await api.post<PgpSettings>(`/api/mail/pgp/${encodeURIComponent(a.id)}/generate`)); loadKeys(); notify(t('Schlüssel erzeugt')) } catch (e) { fail(e) }
  }
  const remove = async (a: PgpAccount) => {
    if (!await askConfirm(t('Schlüssel entfernen?'), t('Mit diesem Konto lässt sich danach nicht mehr signieren. Wer deinen öffentlichen Schlüssel schon hat, kann alte Mails weiter prüfen. Ohne Sicherung lässt sich der private Schlüssel nicht wiederherstellen.'), t('Entfernen'))) return
    try { apply(await api.del<PgpSettings>(`/api/mail/pgp/${encodeURIComponent(a.id)}/key`)); loadKeys() } catch (e) { fail(e) }
  }

  if (!st) return <PageTitle>OpenPGP</PageTitle>
  return (
    <>
      <PageTitle>OpenPGP</PageTitle>
      <Section title={t('Eigene Schlüssel')} hint={t('Mit einem eigenen Schlüssel signierst du deine Mails. Empfänger sehen dann, dass sie wirklich von dir sind und unterwegs nicht verändert wurden. Der private Schlüssel liegt verschlüsselt auf deinem dmOffice-Server.')}>
        {st.accounts.map(a => (
          <div key={a.id} className={s.account}>
            <Text weight="semibold">{a.email || a.name}</Text>
            {a.key ? <>
              <span className={s.mono}>{fingerprintText(a.key.fingerprint)}</span>
              <Hint>{t('{algo}, erstellt am {date}', { algo: a.key.algorithm, date: fmtDay(new Date(a.key.created)) })}{a.key.expires ? ' · ' + t('läuft ab am {date}', { date: fmtDay(new Date(a.key.expires)) }) : ''} · {a.key.userIds.join(', ')}</Hint>
              {!a.key.matches && <Text style={{ color: tokens.colorPaletteMarigoldForeground2 }}>{t('Die Adresse des Kontos steht nicht in diesem Schlüssel – Empfänger sehen die Signatur dann nicht als passend an.')}</Text>}
              <Switch checked={a.sign} label={t('Neue Mails standardmäßig signieren (im Editor umschaltbar)')} onChange={(_, d) => save({ accounts: [{ id: a.id, sign: d.checked }] })} />
              <Switch checked={a.attachKey} label={t('Öffentlichen Schlüssel an signierte Mails anhängen')} onChange={(_, d) => save({ accounts: [{ id: a.id, attachKey: d.checked }] })} />
              <Switch checked={a.autocrypt} label={t('Kopfzeile „Autocrypt“ mit dem öffentlichen Schlüssel setzen')} onChange={(_, d) => save({ accounts: [{ id: a.id, autocrypt: d.checked }] })} />
              <div className={s.row}>
                <Button icon={<ArrowDownload20Regular />} as="a" href={`/api/mail/pgp/${encodeURIComponent(a.id)}/public`}>{t('Öffentlichen Schlüssel exportieren')}</Button>
                <Button icon={<Delete20Regular />} appearance="subtle" onClick={() => remove(a)}>{t('Entfernen')}</Button>
              </div>
            </> : (
              <div className={s.row}>
                <Button icon={<Key20Regular />} appearance="primary" onClick={() => generate(a)}>{t('Schlüssel erzeugen')}</Button>
                <Button icon={<ArrowUpload20Regular />} onClick={() => setImportFor(a)}>{t('Schlüssel importieren')}</Button>
                <Hint>{t('Erzeugt wird ein Ed25519-Schlüssel (mit Curve25519 zum Verschlüsseln).')}</Hint>
              </div>
            )}
          </div>
        ))}
        {!st.accounts.length && <Hint>{t('Noch kein E-Mail-Konto eingerichtet.')}</Hint>}
        <Hint>
          {t('Damit andere deine Signatur prüfen können, brauchen sie deinen öffentlichen Schlüssel – als Anhang, per Autocrypt oder aus einem Verzeichnis. Lade ihn dazu bei keys.openpgp.org hoch und bestätige dort deine Adresse, oder lege ihn im Web Key Directory (WKD) deiner Domain ab.')}{' '}
          <Link href="https://keys.openpgp.org/upload" target="_blank" rel="noreferrer">keys.openpgp.org</Link>
        </Hint>
      </Section>
      <Section title={t('Schlüssel anderer')} hint={t('Öffentliche Schlüssel von Absendern übernimmt dmOffice aus angehängten Schlüsseln und Autocrypt-Kopfzeilen. Aus einer Mail gelten sie erst als geprüft, wenn du den Fingerabdruck bestätigst.')} last>
        <Switch checked={st.wkd} label={t('Unbekannte Schlüssel beim Mailanbieter des Absenders nachschlagen (WKD)')} onChange={(_, d) => save({ wkd: d.checked })} />
        <Hint>{t('Nur bei signierten Mails mit unbekanntem Schlüssel, nie bei Junk, höchstens einmal am Tag je Absender. Der Anbieter des Absenders erfährt dabei, dass die Mail geöffnet wurde.')}</Hint>
        <div className={s.row}>
          <Button icon={<ArrowUpload20Regular />} onClick={() => setImportFor('public')}>{t('Öffentlichen Schlüssel importieren')}</Button>
        </div>
        {keys.filter(k => k.source !== 'own').map(k => (
          <div key={k.fingerprint} className={s.key}>
            <div style={{ display: 'flex', flexDirection: 'column', gap: 2, minWidth: 0 }}>
              <Text weight="semibold">{k.userIds.join(', ') || k.emails.join(', ') || t('(ohne Adresse)')}</Text>
              <span className={s.mono}>{fingerprintText(k.fingerprint)}</span>
              <div className={s.row}>
                <Badge appearance="tint" color={k.confirmed ? 'success' : 'warning'}>{k.confirmed ? t('bestätigt') : t('unbestätigt')}</Badge>
                {k.revoked && <Badge appearance="tint" color="danger">{t('pgp|zurückgezogen')}</Badge>}
                <Hint>{k.algorithm} · {sourceText(k.source)} · {fmtDay(new Date(k.added))}</Hint>
              </div>
            </div>
            <div className={s.row} style={{ flexWrap: 'nowrap' }}>
              {!k.confirmed && <Button size="small" icon={<Checkmark20Regular />} onClick={async () => {
                if (!await askConfirm(t('Schlüssel bestätigen'), t('Vergleiche den Fingerabdruck mit dem Absender, z. B. am Telefon: {fp}. Stimmt er überein?', { fp: fingerprintText(k.fingerprint) }), t('Stimmt überein'))) return
                try { await api.post(`/api/mail/pgp/keys/${k.fingerprint}/confirm`); loadKeys() } catch (e) { fail(e) }
              }}>{t('Bestätigen')}</Button>}
              <Button size="small" appearance="subtle" icon={<ArrowDownload20Regular />} as="a" href={`/api/mail/pgp/keys/${k.fingerprint}/export`} aria-label={t('Exportieren')} />
              <Button size="small" appearance="subtle" icon={<Delete20Regular />} aria-label={t('Löschen')} onClick={async () => {
                if (!await askConfirm('', t('Diesen Schlüssel löschen?'))) return
                try { await api.del(`/api/mail/pgp/keys/${k.fingerprint}`); loadKeys() } catch (e) { fail(e) }
              }} />
            </div>
          </div>
        ))}
      </Section>
      {importFor && <ImportDialog target={importFor} onClose={() => setImportFor(null)} onDone={n => {
        if (n) apply(n)
        loadKeys()
        setImportFor(null)
      }} />}
    </>
  )
}

/** Schlüssel als Text einfügen oder aus einer Datei (.asc, .gpg, .pgp, .key); beim eigenen mit Passwort */
function ImportDialog({ target, onClose, onDone }: { target: PgpAccount | 'public'; onClose: () => void; onDone: (s?: PgpSettings) => void }) {
  const { fail, notify } = useApp()
  const [key, setKey] = useState('')
  const [pass, setPass] = useState('')
  const [busy, setBusy] = useState(false)
  const file = useRef<HTMLInputElement>(null)
  const own = target !== 'public'
  const pick = async (f?: File) => {
    if (!f) return
    const buf = new Uint8Array(await f.arrayBuffer())
    // Binärdateien (.gpg) in eine ASCII-Hülle, damit sie ins Textfeld passen
    const text = new TextDecoder().decode(buf)
    setKey(/-----BEGIN PGP/.test(text) ? text : binaryArmor(buf, own))
  }
  const run = async () => {
    setBusy(true)
    try {
      if (own) {
        const r = await api.post<{ state: PgpSettings; warning?: string | null }>(`/api/mail/pgp/${encodeURIComponent(target.id)}/import`, { key, passphrase: pass || null })
        if (r.warning) notify(t('Importiert – mit Hinweis'), r.warning, 'warning')
        else notify(t('Schlüssel importiert'))
        onDone(r.state)
      } else {
        const r = await api.post<{ imported: string[] }>('/api/mail/pgp/keys', { key })
        notify(t('{n} Schlüssel importiert', { n: r.imported.length }))
        onDone()
      }
    } catch (e) { fail(e) } finally { setBusy(false) }
  }
  return (
    <Dialog open onOpenChange={(_, d) => { if (!d.open) onClose() }}>
      <DialogSurface>
        <DialogBody>
          <DialogTitle>{own ? t('Eigenen Schlüssel importieren') : t('Öffentlichen Schlüssel importieren')}</DialogTitle>
          <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 10 }}>
            {own && <Text>{t('Den privaten Schlüssel einfügen, z. B. aus „gpg --export-secret-keys --armor“. dmOffice speichert ihn ohne das Passwort, geschützt durch die Verschlüsselung deiner Daten auf dem Server.')}</Text>}
            <Field label={t('Schlüssel')}>
              <Textarea value={key} onChange={(_, d) => setKey(d.value)} rows={8} resize="vertical" placeholder="-----BEGIN PGP …" style={{ fontFamily: tokens.fontFamilyMonospace }} />
            </Field>
            <div><Button size="small" onClick={() => file.current?.click()}>{t('Aus Datei …')}</Button></div>
            <input ref={file} type="file" accept=".asc,.gpg,.pgp,.key,.txt" hidden onChange={e => { void pick(e.target.files?.[0]); e.target.value = '' }} />
            {own && <Field label={t('Passwort des Schlüssels (falls gesetzt)')}><Input type="password" value={pass} onChange={(_, d) => setPass(d.value)} /></Field>}
          </DialogContent>
          <DialogActions>
            <Button appearance="secondary" onClick={onClose}>{t('Abbrechen')}</Button>
            <Button appearance="primary" disabled={busy || !key.trim()} onClick={run}>{t('Importieren')}</Button>
          </DialogActions>
        </DialogBody>
      </DialogSurface>
    </Dialog>
  )
}

/** Binärer Schlüssel in eine ASCII-Hülle (ohne Prüfsumme - die ist optional) */
function binaryArmor(buf: Uint8Array, secret: boolean) {
  let bin = ''
  buf.forEach(b => { bin += String.fromCharCode(b) })
  const b64 = btoa(bin).replace(/(.{64})/g, '$1\n')
  const kind = secret ? 'PRIVATE' : 'PUBLIC'
  return `-----BEGIN PGP ${kind} KEY BLOCK-----\n\n${b64}\n-----END PGP ${kind} KEY BLOCK-----\n`
}
