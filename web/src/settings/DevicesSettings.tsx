import {
  Button, Card, Dropdown, Field, Input, Option, Switch, Table, TableBody, TableCell, TableHeader, TableHeaderCell, TableRow, Text,
  makeStyles, tokens, MessageBar, MessageBarBody, Dialog, DialogSurface, DialogBody, DialogTitle, DialogContent, DialogActions, Spinner,
} from '@fluentui/react-components'
import { Copy16Regular, Delete20Regular, Key20Regular, QrCode20Regular } from '@fluentui/react-icons'
import QRCode from 'qrcode'
import { useEffect, useState } from 'react'
import { api } from '../api'
import { useApp } from '../App'
import { copyText } from '../shell/browser'
import { t, locale, tError } from '../i18n'

const useStyles = makeStyles({
  urls: { display: 'grid', gridTemplateColumns: '160px 1fr auto', gap: '6px 12px', alignItems: 'center', padding: '14px 16px' },
  mono: { fontFamily: 'Consolas, monospace', fontSize: '13px', wordBreak: 'break-all' },
  muted: { color: tokens.colorNeutralForeground3 },
  token: { fontFamily: 'Consolas, monospace', fontSize: '22px', letterSpacing: '1px', padding: '14px', textAlign: 'center', backgroundColor: tokens.colorNeutralBackground3, borderRadius: tokens.borderRadiusMedium },
  form: { display: 'flex', flexDirection: 'column', gap: '12px', maxWidth: '480px' },
  steps: { margin: 0, paddingLeft: '18px', lineHeight: '1.7' },
  qr: { display: 'block', margin: '0 auto', width: '240px', height: '240px', borderRadius: tokens.borderRadiusMedium, backgroundColor: '#fff', padding: '8px', boxSizing: 'border-box' },
  pairBox: { display: 'flex', flexDirection: 'column', gap: '10px', padding: '14px 16px', maxWidth: '720px' },
  manual: { display: 'grid', gridTemplateColumns: 'auto 1fr', gap: '2px 12px', fontSize: '12px', color: tokens.colorNeutralForeground3 },
})

// Inhalt des QR-Codes fuer die dmOffice-Handy-App
function pairUri(origin: string, login: string, token: string, name: string, serverKey?: string) {
  const q = new URLSearchParams({ v: '1', url: origin, user: login, token, name })
  // Oeffentlicher Schluessel des Servers: die App nagelt ihn fest (verschluesselter Umschlag, E2e.cs)
  if (serverKey) q.set('spk', serverKey)
  return 'dmoffice://pair?' + q.toString()
}

export function DevicesSettings() {
  const s = useStyles()
  const { fail, notify, me } = useApp()
  const [dav, setDav] = useState<any>(null)
  const [list, setList] = useState<any[]>([])
  const [name, setName] = useState('')
  const [created, setCreated] = useState<string | null>(null)
  const [pair, setPair] = useState<{ qr: string; uri: string; token: string } | null>(null)
  const [pairing, setPairing] = useState(false)
  const load = () => api.get('/api/settings/app-passwords').then(setList).catch(fail)
  useEffect(() => { api.get('/api/settings/dav').then(setDav).catch(fail); load() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const create = async () => {
    try { const r = await api.post('/api/settings/app-passwords', { name: name || t('Gerät') }); setCreated(r.password); setName(''); load() } catch (e) { fail(e) }
  }
  // Handy-App koppeln: App-Passwort anlegen und einmalig als QR-Code zeigen
  const pairPhone = async () => {
    if (!dav) return
    setPairing(true)
    try {
      const origin = String(dav.server).replace(/\/dav\/?$/, '')
      const r = await api.post('/api/settings/app-passwords', { name: t('Handy-App') + ' · ' + new Date().toLocaleDateString(locale) })
      const uri = pairUri(origin, dav.login, r.password, me?.displayName || dav.login, dav.serverKey)
      setPair({ uri, token: r.password, qr: await QRCode.toDataURL(uri, { width: 480, margin: 1 }) })
      load()
    } catch (e) { fail(e) } finally { setPairing(false) }
  }
  const revoke = async (p: any) => {
    if (!confirm(t('„{name}“ widerrufen? Das Gerät kann danach nicht mehr synchronisieren.', { name: p.name }))) return
    try { await api.del(`/api/settings/app-passwords/${p.id}`); load() } catch (e) { fail(e) }
  }
  const copy = async (text: string) => {
    if (await copyText(text)) notify(t('Kopiert'))
    else notify(t('Kopieren ging nicht'), t('Bitte markieren und mit Strg+C kopieren.'), 'warning')
  }

  const row = (label: string, url: string) => (
    <>
      <Text>{label}</Text><span className={s.mono}>{url}</span>
      <Button appearance="subtle" size="small" icon={<Copy16Regular />} onClick={() => copy(url)} aria-label={t('Kopieren')} title={t('Kopieren')} />
    </>
  )

  return (
    <>
      <Text size={600} weight="semibold">{t('Geräte & Sync')}</Text>
      <Text>
        {t('dmOffice ist selbst CalDAV-, CardDAV- und WebDAV-Server. Handy, Thunderbird, macOS und notizi verbinden sich direkt – mit dem Anmeldenamen und einem')}
        <b> {t('App-Passwort')}</b> {t('je Gerät, nicht mit dem Login-Passwort.')}
      </Text>
      {dav && (
        <Card className={s.urls}>
          {row(t('Anmeldename'), dav.login)}
          {row(t('Server (alles)'), dav.server)}
          {row('CalDAV', dav.caldav)}
          {row('CardDAV', dav.carddav)}
          {me.files !== false && row(t('WebDAV (Dateien)'), dav.webdav)}
          {row('notizi', dav.notizi)}
        </Card>
      )}
      <Text weight="semibold">{t("So geht's")}</Text>
      <ul className={s.steps}>
        <li><b>Android</b>: {t('DAVx⁵ → Konto hinzufügen → „Mit URL und Benutzername anmelden“ → Server-Adresse, Anmeldename, App-Passwort.')}</li>
        <li><b>iPhone/iPad</b>: {t('Einstellungen → Kalender → Accounts → Andere → CalDAV-Account (Server: nur der Hostname); genauso CardDAV für Kontakte.')}</li>
        <li><b>Thunderbird</b>: {t('Neuer Kalender → Im Netzwerk → CalDAV-Adresse; Adressbuch → CardDAV-Adresse.')}</li>
        <li><b>notizi</b>: {t('Einstellungen → Sync → Verbindung hinzufügen → WebDAV, Adresse „notizi“ von oben, Benutzer und App-Passwort.')}</li>
      </ul>
      {dav && me.files !== false && <NetworkDrive url={dav.webdav} login={dav.login} copy={copy} />}

      <Text weight="semibold" style={{ marginTop: 8 }}>{t('Handy-App koppeln')}</Text>
      <Card className={s.pairBox}>
        <Text>
          {t('Die dmOffice-App für Android zeigt Kalender und Aufgaben mit Widgets auf dem Startbildschirm. Zum Koppeln legt dmOffice ein eigenes App-Passwort an und zeigt es als QR-Code – in der App „Mit QR-Code einrichten“ wählen und den Code abfotografieren.')}
        </Text>
        <Text size={200} className={s.muted}>
          {t('Der Code erscheint nur ein einziges Mal. Das App-Passwort steht danach als „Handy-App“ in der Liste unten und kann dort jederzeit widerrufen werden.')}
        </Text>
        <div><Button appearance="primary" icon={<QrCode20Regular />} onClick={pairPhone} disabled={!dav || pairing}>{pairing ? <Spinner size="tiny" /> : t('QR-Code erzeugen')}</Button></div>
      </Card>

      <Text weight="semibold" style={{ marginTop: 8 }}>{t('App-Passwörter')}</Text>
      <form style={{ display: 'flex', gap: 8, maxWidth: 480 }} onSubmit={e => { e.preventDefault(); create() }}>
        <Input placeholder={t('Name, z. B. Pixel 8 oder Laptop')} value={name} onChange={(_, d) => setName(d.value)} style={{ flex: 1 }} />
        <Button appearance="primary" icon={<Key20Regular />} type="submit">{t('Erstellen')}</Button>
      </form>
      {list.length > 0 && (
        <Table size="small" style={{ maxWidth: 720 }}>
          <TableHeader><TableRow><TableHeaderCell>{t('Gerät')}</TableHeaderCell><TableHeaderCell>{t('Erstellt')}</TableHeaderCell><TableHeaderCell>{t('Zuletzt benutzt')}</TableHeaderCell><TableHeaderCell /></TableRow></TableHeader>
          <TableBody>
            {list.map(p => (
              <TableRow key={p.id}>
                <TableCell>{p.name}</TableCell>
                <TableCell>{new Date(p.created).toLocaleDateString(locale)}</TableCell>
                <TableCell>{p.lastUsed ? new Date(p.lastUsed).toLocaleString(locale) : t('noch nie')}</TableCell>
                <TableCell><Button appearance="subtle" icon={<Delete20Regular />} onClick={() => revoke(p)} title={t('Widerrufen')} /></TableCell>
              </TableRow>
            ))}
          </TableBody>
        </Table>
      )}
      {pair && (
        <Dialog open onOpenChange={(_, d) => !d.open && setPair(null)}>
          <DialogSurface style={{ maxWidth: 440 }}>
            <DialogBody>
              <DialogTitle>{t('Handy-App koppeln')}</DialogTitle>
              <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
                <Text>{t('Diesen Code jetzt mit der dmOffice-App scannen. Er wird nur dieses eine Mal gezeigt – nach dem Schließen ist er weg.')}</Text>
                <img className={s.qr} src={pair.qr} alt={t('QR-Code zum Koppeln')} />
                <Text size={200} className={s.muted}>{t('Ohne Kamera: in der App „Von Hand einrichten“ wählen und eintragen:')}</Text>
                <div className={s.manual}>
                  <span>{t('Adresse')}</span><span className={s.mono}>{String(dav.server).replace(/\/dav\/?$/, '')}</span>
                  <span>{t('Anmeldename')}</span><span className={s.mono}>{dav.login}</span>
                  <span>{t('App-Passwort')}</span><span className={s.mono}>{pair.token}</span>
                </div>
              </DialogContent>
              <DialogActions>
                <Button onClick={() => copy(pair.token)}>{t('App-Passwort kopieren')}</Button>
                <Button appearance="primary" onClick={() => setPair(null)}>{t('Erledigt')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
      {created && (
        <Dialog open onOpenChange={(_, d) => !d.open && setCreated(null)}>
          <DialogSurface style={{ maxWidth: 440 }}>
            <DialogBody>
              <DialogTitle>{t('App-Passwort')}</DialogTitle>
              <DialogContent style={{ display: 'flex', flexDirection: 'column', gap: 12 }}>
                <Text>{t('Jetzt im Gerät eintragen – es wird nur dieses eine Mal gezeigt. Bindestriche dürfen wegfallen.')}</Text>
                <div className={s.token}>{created}</div>
              </DialogContent>
              <DialogActions>
                <Button onClick={() => copy(created)}>{t('Kopieren')}</Button>
                <Button appearance="primary" onClick={() => setCreated(null)}>{t('Erledigt')}</Button>
              </DialogActions>
            </DialogBody>
          </DialogSurface>
        </Dialog>
      )}
    </>
  )
}

export function NotesSettings() {
  const s = useStyles()
  const { fail, notify } = useApp()
  const [data, setData] = useState<any>(null)
  const [enabled, setEnabled] = useState(false)
  const [root, setRoot] = useState('notizisync')
  const [password, setPassword] = useState('')
  const [folder, setFolder] = useState('')
  const [busy, setBusy] = useState(false)

  const load = () => api.get('/api/settings/notizi').then(d => {
    setData(d); setEnabled(d.enabled); setRoot(d.root); setFolder(d.dbFolder)
  }).catch(fail)
  useEffect(() => { load() }, []) // eslint-disable-line react-hooks/exhaustive-deps

  const save = async () => {
    setBusy(true)
    try {
      await api.put('/api/settings/notizi', { enabled, root, dbFolder: folder, e2eePassword: password || undefined })
      setPassword('')
      notify(t('Gespeichert'))
      await load()
    } catch (e) { fail(e) } finally { setBusy(false) }
  }

  if (!data) return null
  const dbs: any[] = data.databases ?? []
  return (
    <>
      <Text size={600} weight="semibold">{t('Notizen (notizi)')}</Text>
      <Text>
        {t('Die Notizen kommen über den Abgleichordner von notizi – Ende-zu-Ende-verschlüsselt im WebDAV von dmOffice. dmOffice ist dabei ein weiteres Gerät wie das Handy: Absätze und Listen lassen sich hier bearbeiten, Tabellen, Bilder und Codeblöcke bleiben unverändert.')}
      </Text>
      <form className={s.form} onSubmit={e => { e.preventDefault(); save() }}>
        <Switch label={t('Notizen einschalten')} checked={enabled} onChange={(_, d) => setEnabled(d.checked)} />
        <Field label={t('Wurzel im WebDAV')} hint={t('der Ordner, den notizi als WebDAV-Wurzel nutzt')}>
          <Input value={root} onChange={(_, d) => setRoot(d.value)} />
        </Field>
        <Field label={t('E2EE-Passwort')} hint={data.hasPassword ? t('leer lassen: bleibt') : t('wie in notizi unter Einstellungen → Sync')}>
          <Input type="password" value={password} onChange={(_, d) => setPassword(d.value)} autoComplete="new-password" />
        </Field>
        <Field label={t('Datenbank')} hint={t('Ordner unter der Wurzel; noch keiner da: Namen eintippen, dmOffice richtet ihn ein')}>
          {dbs.length > 0 ? (
            <Dropdown value={dbs.find(d => d.folder === folder)?.title ?? folder} selectedOptions={[folder]} onOptionSelect={(_, d) => setFolder(d.optionValue!)}>
              {dbs.map(d => <Option key={d.folder} value={d.folder} text={d.title}>{d.title}{!d.passwordFits && ' ' + t('(Passwort passt nicht)')}</Option>)}
            </Dropdown>
          ) : <Input value={folder} onChange={(_, d) => setFolder(d.value)} placeholder={t('z. B. notizi pr')} />}
        </Field>
        <div><Button appearance="primary" type="submit" disabled={busy}>{busy ? <Spinner size="tiny" /> : t('Speichern')}</Button></div>
      </form>
      {data.status?.error && <MessageBar intent="error"><MessageBarBody>{tError(data.status.error)}</MessageBarBody></MessageBar>}
      {data.status?.lastRun && !data.status.error && (
        <MessageBar intent="success"><MessageBarBody>{t('Zuletzt abgeglichen {time}: {report}', { time: new Date(data.status.lastRun).toLocaleString(locale), report: data.status.report })}</MessageBarBody></MessageBar>
      )}
      <Text size={200} className={s.muted}>
        {t('Der Abgleichordner liegt in der verschlüsselten Datenbank von dmOffice, unter Dateien → {root}. notizi verbindet sich mit der Adresse aus „Geräte & Sync“.', { root })}
      </Text>
    </>
  )
}

// Netzlaufwerk ohne Sync-Programm: Explorer, Finder, Dateimanager unter Linux
const webClientFix = 'Set-ItemProperty -Path HKLM:\\SYSTEM\\CurrentControlSet\\Services\\WebClient\\Parameters -Name FileSizeLimitInBytes -Value 4294967295; Restart-Service WebClient'

function NetworkDrive({ url, login, copy }: { url: string; login: string; copy: (text: string) => void }) {
  const s = useStyles()
  const davs = url.replace(/^https:/, 'davs:').replace(/^http:/, 'dav:')
  const code = (text: string) => (
    <span style={{ display: 'inline-flex', alignItems: 'center', gap: 4 }}>
      <span className={s.mono}>{text}</span>
      <Button appearance="subtle" size="small" icon={<Copy16Regular />} onClick={() => copy(text)} aria-label={t('Kopieren')} title={t('Kopieren')} />
    </span>
  )
  return (
    <>
      <Text weight="semibold" style={{ marginTop: 8 }}>{t('Dateien als Netzlaufwerk')}</Text>
      <Text>{t('Ohne Sync-Programm: Die Dateiablage erscheint als Laufwerk im Dateimanager. Dateien werden beim Öffnen geladen und beim Speichern zurückgeschrieben – offline gibt es sie nicht.')}</Text>
      <ul className={s.steps}>
        <li>
          <b>Windows</b>: {t('Explorer → „Dieser PC“ → „Netzlaufwerk verbinden“ → Ordner:')} {code(url)}
          {t('„Verbindung mit anderen Anmeldeinformationen herstellen“ anhaken, dann Anmeldename')} <b>{login}</b> {t('und ein App-Passwort.')}
        </li>
        <li>
          <b>macOS</b>: {t('Finder → Gehe zu → „Mit Server verbinden“ (⌘K) →')} {code(url)}
        </li>
        <li>
          <b>Linux</b>: {t('Im Dateimanager (Nautilus, Dolphin) als Adresse:')} {code(davs)}
        </li>
      </ul>
      <MessageBar intent="info" layout="multiline">
        <MessageBarBody>
          {t('Windows lädt über WebDAV höchstens 50 MB pro Datei. Für größere Dateien einmal in einer PowerShell als Administrator:')}
          <div style={{ marginTop: 6 }}>{code(webClientFix)}</div>
        </MessageBarBody>
      </MessageBar>
    </>
  )
}
